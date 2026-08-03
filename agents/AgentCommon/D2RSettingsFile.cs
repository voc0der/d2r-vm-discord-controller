using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentCommon;

public sealed record D2RSettingsSnapshot(
    string Path,
    string Content,
    string Sha256,
    long Length,
    DateTimeOffset? LastWriteUtc);

/// <summary>
/// Crash-safe state for one settings-repair incident. It deliberately lives beside Settings.json
/// rather than in host memory: the VM agent is commonly restarted by auto-update immediately after
/// a master restart, and a successful copy must still be followed by a client relaunch after both
/// processes have gone away.
/// </summary>
public enum D2RSettingsRepairPhase
{
    /// <summary>An attempt was charged durably, but Settings.json is not known to have been replaced.</summary>
    Prepared,

    /// <summary>Settings.json was replaced and the client must render a healthy frame.</summary>
    Ready
}

public enum D2RSettingsRepairStateReadResult
{
    Missing,
    Loaded,
    Invalid
}

public sealed record D2RSettingsRepairState(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] D2RSettingsRepairPhase Phase,
    [property: JsonRequired] int AttemptCount,
    DateTimeOffset? FirstAttemptUtc,
    DateTimeOffset? LastAttemptUtc)
{
    public const int CurrentSchemaVersion = 2;
    public const int MaxAttemptsPerIncident = 2;
    public static readonly TimeSpan IncidentWindow = TimeSpan.FromMinutes(30);
}

/// <summary>
/// Reads and replaces D2R's <c>Settings.json</c>. The file lives at
/// <c>%USERPROFILE%\Saved Games\Diablo II Resurrected\Settings.json</c> and D2R owns it: the game
/// rewrites it on exit and regenerates it from defaults when it decides the file is unusable, which
/// is what puts a client on the first-run Gamma Calibration screen instead of character select.
/// Nothing here takes write access away from the game - that was tried in v0.2.229 and D2R would
/// not boot. Repair is a replace-with-a-known-good-copy, done while the client is closed.
/// </summary>
public static class D2RSettingsFile
{
    public const string SettingsFileName = "Settings.json";
    public const string SettingsFolderName = "Diablo II Resurrected";
    private const string SavedGamesFolderName = "Saved Games";
    // A known-good Settings.json on this fleet measures 4 KB, and anything under 2 KB is wrong -
    // operator-confirmed, not inferred. That makes size the sharpest check available here: a
    // truncated write (the suspected corruption) fails it, and so does any file that parses fine
    // but is not this file. The ceiling is loose by comparison; it only exists so a wrong-file
    // transfer cannot push megabytes through the command tunnel.
    private const int MinPlausibleContentLength = 2 * 1024;
    private const int MaxPlausibleContentLength = 512 * 1024;
    private const int MinPlausiblePropertyCount = 3;
    private const string RepairStateSuffix = ".d2rops-repair-state.json";

    private static readonly JsonSerializerOptions RepairStateJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // FOLDERID_SavedGames. Saved Games is relocatable, so the known-folder path is the reliable
    // answer and %USERPROFILE%\Saved Games is only the fallback.
    private static readonly Guid SavedGamesFolderId = new("4C5C32FF-BB9D-43b0-B5B4-2D72E54EAAA4");

    public static string? ResolveSettingsPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            try
            {
                var trimmed = configuredPath.Trim();
                if (trimmed.IndexOf('\0') >= 0
                    || trimmed.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0)
                {
                    return null;
                }

                var expanded = Environment.ExpandEnvironmentVariables(trimmed);
                if (expanded.IndexOf('\0') >= 0
                    || expanded.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0)
                {
                    return null;
                }

                return System.IO.Path.GetFullPath(expanded);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }

        var savedGames = ResolveSavedGamesRoot();
        return string.IsNullOrWhiteSpace(savedGames)
            ? null
            : System.IO.Path.Combine(savedGames, SettingsFolderName, SettingsFileName);
    }

    public static bool TryRead(string? path, out D2RSettingsSnapshot snapshot, out string error)
    {
        snapshot = default!;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = $"Could not resolve the Saved Games folder; set d2rSettingsPath to the full path of {SettingsFileName}.";
            return false;
        }

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                error = $"{file.FullName} does not exist.";
                return false;
            }

            var content = File.ReadAllText(file.FullName);
            if (!IsPlausibleSettingsJson(content, out var reason))
            {
                error = $"{file.FullName} is not usable as a settings donor: {reason}";
                return false;
            }

            snapshot = new D2RSettingsSnapshot(
                file.FullName,
                content,
                Sha256(content),
                content.Length,
                file.LastWriteTimeUtc);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not read {path}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Replaces the settings file with <paramref name="content"/>, keeping a timestamped backup of
    /// whatever was there. The backup is the only surviving evidence of what a corrupted file
    /// looked like, which is the open question behind this whole repair path. When supplied,
    /// <paramref name="preCommitCheck"/> runs after the backup and donor temp file are flushed but
    /// immediately before the atomic rename; refusing or throwing leaves the destination unchanged.
    /// </summary>
    public static bool TryReplace(
        string? path,
        string content,
        out string backupPath,
        out string error,
        Func<(bool Allowed, string Error)>? preCommitCheck = null)
    {
        backupPath = "";
        if (!IsPlausibleSettingsJson(content, out var reason))
        {
            error = $"Refusing to write the donor payload: {reason}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            error = $"Could not resolve the Saved Games folder; set d2rSettingsPath to the full path of {SettingsFileName}.";
            return false;
        }

        string? temporaryPath = null;
        try
        {
            var file = new FileInfo(path);
            temporaryPath = $"{file.FullName}.d2rops-tmp";
            if (file.Directory is { Exists: false } directory)
            {
                directory.Create();
            }

            if (file.Exists)
            {
                // Repairs can be retried in the same second (and a manual repair can race the
                // host). A seconds-only name with overwrite=true silently destroyed the first
                // forensic copy. The high-resolution timestamp is useful to humans; the random
                // suffix plus CreateNew makes preserving every prior file an atomic guarantee.
                backupPath = $"{file.FullName}.{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss.fffffff}-{Guid.NewGuid():N}.bak";
                using var source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var backup = new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                source.CopyTo(backup);
                backup.Flush(flushToDisk: true);
            }

            // Write-then-rename, not a direct overwrite. A guest that loses power mid-write is the
            // leading suspect for how the file gets corrupted in the first place, so the repair
            // itself must not have that same failure mode: the rename either happens or it does
            // not, and the old file survives until it does.
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None))
            {
                using (var writer = new StreamWriter(
                           stream,
                           new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                           bufferSize: 4096,
                           leaveOpen: true))
                {
                    writer.Write(content);
                    writer.Flush();
                }

                stream.Flush(flushToDisk: true);
            }

            if (preCommitCheck is not null)
            {
                var (allowed, preCommitError) = preCommitCheck();
                if (!allowed)
                {
                    error = string.IsNullOrWhiteSpace(preCommitError)
                        ? "The pre-commit safety check refused to replace the settings file."
                        : preCommitError;
                    return false;
                }
            }

            File.Move(temporaryPath, file.FullName, overwrite: true);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not write {path}: {ex.Message}";
            return false;
        }
        finally
        {
            try
            {
                if (temporaryPath is not null)
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>The journal path associated with a resolved Settings.json path.</summary>
    public static string? ResolveRepairStatePath(string? settingsPath)
    {
        if (string.IsNullOrWhiteSpace(settingsPath))
        {
            return null;
        }

        try
        {
            if (settingsPath.IndexOf('\0') >= 0
                || settingsPath.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0)
            {
                return null;
            }

            return $"{System.IO.Path.GetFullPath(settingsPath)}{RepairStateSuffix}";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>Loads a previously persisted unresolved repair incident.</summary>
    public static bool TryReadRepairState(
        string? settingsPath,
        out D2RSettingsRepairState state,
        out string error)
    {
        return ReadRepairState(settingsPath, out state, out error)
            == D2RSettingsRepairStateReadResult.Loaded;
    }

    /// <summary>
    /// Distinguishes a normal missing journal from an unreadable or invalid one. Callers must fail
    /// closed on <see cref="D2RSettingsRepairStateReadResult.Invalid"/> because guessing whether a
    /// prior destructive replacement completed can either duplicate a copy or strand the client.
    /// </summary>
    public static D2RSettingsRepairStateReadResult ReadRepairState(
        string? settingsPath,
        out D2RSettingsRepairState state,
        out string error)
    {
        state = default!;
        var statePath = ResolveRepairStatePath(settingsPath);
        if (statePath is null)
        {
            error = "Could not resolve the settings-repair state path.";
            return D2RSettingsRepairStateReadResult.Invalid;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<D2RSettingsRepairState>(
                File.ReadAllText(statePath),
                RepairStateJsonOptions);
            if (!IsValidRepairState(parsed, out error))
            {
                error = $"Could not use {statePath}: {error}";
                return D2RSettingsRepairStateReadResult.Invalid;
            }

            state = parsed!;
            error = "";
            return D2RSettingsRepairStateReadResult.Loaded;
        }
        catch (FileNotFoundException)
        {
            error = $"{statePath} does not exist.";
            return D2RSettingsRepairStateReadResult.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            error = $"{statePath} does not exist.";
            return D2RSettingsRepairStateReadResult.Missing;
        }
        catch (Exception ex)
        {
            error = $"Could not read {statePath}: {ex.Message}";
            return D2RSettingsRepairStateReadResult.Invalid;
        }
    }

    /// <summary>
    /// Atomically persists an unresolved repair incident. The temporary file is flushed to disk
    /// before rename so a guest restart cannot leave a valid-looking journal with partial JSON.
    /// </summary>
    public static bool TryWriteRepairState(
        string? settingsPath,
        D2RSettingsRepairState state,
        out string error)
    {
        if (!IsValidRepairState(state, out error))
        {
            return false;
        }

        var statePath = ResolveRepairStatePath(settingsPath);
        if (statePath is null)
        {
            error = "Could not resolve the settings-repair state path.";
            return false;
        }

        var temporaryPath = $"{statePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var directory = System.IO.Path.GetDirectoryName(statePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(state, RepairStateJsonOptions);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                using (var writer = new StreamWriter(
                           stream,
                           new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                           bufferSize: 1024,
                           leaveOpen: true))
                {
                    writer.Write(json);
                    writer.Flush();
                }

                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, statePath, overwrite: true);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not persist {statePath}: {ex.Message}";
            return false;
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>Deletes the unresolved-incident journal after a healthy rendered frame.</summary>
    public static bool TryClearRepairState(string? settingsPath, out string error)
    {
        var statePath = ResolveRepairStatePath(settingsPath);
        if (statePath is null)
        {
            error = "Could not resolve the settings-repair state path.";
            return false;
        }

        try
        {
            File.Delete(statePath);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not clear {statePath}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Gate for anything about to be written over a client's settings: real JSON, an object, not
    /// empty, and carrying at least one key D2R actually writes. A file that fails this is either
    /// the corruption being repaired or the wrong file entirely.
    /// </summary>
    public static bool IsPlausibleSettingsJson(string? content, out string reason)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            reason = "content is empty";
            return false;
        }

        if (content.Length < MinPlausibleContentLength)
        {
            reason = $"content is only {content.Length} characters; a real settings file is ~4 KB and anything "
                + $"under {MinPlausibleContentLength} is corrupt or truncated";
            return false;
        }

        if (content.Length > MaxPlausibleContentLength)
        {
            reason = $"content is {content.Length} characters, above the {MaxPlausibleContentLength} maximum";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                reason = $"root JSON value is {document.RootElement.ValueKind}, not an object";
                return false;
            }

            // Deliberately schema-free. D2R's key names are the game's business and change across
            // patches; pinning them here would mean a game update silently makes every real file
            // "implausible" and disables repair fleet-wide. A property-count floor plus the size
            // bounds above still rejects the things that actually threaten a working client -
            // "{}", a fragment, some other JSON file - without pretending to know D2R's schema.
            var properties = document.RootElement.EnumerateObject().Count();
            if (properties < MinPlausiblePropertyCount)
            {
                reason = $"root object has only {properties} propert{(properties == 1 ? "y" : "ies")}, below the {MinPlausiblePropertyCount} minimum for a real settings file";
                return false;
            }

            reason = "";
            return true;
        }
        catch (JsonException ex)
        {
            reason = $"content is not valid JSON ({ex.Message})";
            return false;
        }
    }

    public static string Sha256(string content)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }

    private static bool IsValidRepairState(D2RSettingsRepairState? state, out string error)
    {
        if (state is null)
        {
            error = "state JSON was empty.";
            return false;
        }

        if (state.SchemaVersion != D2RSettingsRepairState.CurrentSchemaVersion)
        {
            error = $"unsupported schema version {state.SchemaVersion}.";
            return false;
        }

        if (!Enum.IsDefined(state.Phase))
        {
            error = $"unknown repair phase {state.Phase}.";
            return false;
        }

        if (state.AttemptCount < 0)
        {
            error = "attemptCount cannot be negative.";
            return false;
        }

        if (state.AttemptCount == 0)
        {
            if (state.FirstAttemptUtc is not null || state.LastAttemptUtc is not null)
            {
                error = "an expired attempt budget cannot retain first/last attempt timestamps.";
                return false;
            }

            error = "";
            return true;
        }

        if (state.FirstAttemptUtc is not { } first
            || state.LastAttemptUtc is not { } last)
        {
            error = "firstAttemptUtc and lastAttemptUtc are required.";
            return false;
        }

        if (first > last)
        {
            error = "firstAttemptUtc cannot be later than lastAttemptUtc.";
            return false;
        }

        error = "";
        return true;
    }

    private static string? ResolveSavedGamesRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        if (TryGetKnownFolderPath(SavedGamesFolderId, out var knownFolder))
        {
            return knownFolder;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile)
            ? null
            : System.IO.Path.Combine(profile, SavedGamesFolderName);
    }

    private static bool TryGetKnownFolderPath(Guid folderId, out string path)
    {
        path = "";
        var buffer = IntPtr.Zero;
        try
        {
            if (SHGetKnownFolderPath(folderId, 0, IntPtr.Zero, out buffer) != 0)
            {
                return false;
            }

            path = Marshal.PtrToStringUni(buffer) ?? "";
            return !string.IsNullOrWhiteSpace(path);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(buffer);
            }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);
}
