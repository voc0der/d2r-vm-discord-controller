using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentCommon;

public sealed record D2RSettingsSnapshot(
    string Path,
    string Content,
    string Sha256,
    long Length,
    DateTimeOffset? LastWriteUtc);

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
    // A real Settings.json is ~1-3 KB. These bound what this agent will accept as a donor payload
    // so a truncated or wrong-file transfer can never overwrite a working client's settings.
    private const int MinPlausibleContentLength = 64;
    private const int MaxPlausibleContentLength = 512 * 1024;
    private const int MinPlausiblePropertyCount = 3;

    // FOLDERID_SavedGames. Saved Games is relocatable, so the known-folder path is the reliable
    // answer and %USERPROFILE%\Saved Games is only the fallback.
    private static readonly Guid SavedGamesFolderId = new("4C5C32FF-BB9D-43b0-B5B4-2D72E54EAAA4");

    public static string? ResolveSettingsPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPath.Trim()));
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
    /// looked like, which is the open question behind this whole repair path.
    /// </summary>
    public static bool TryReplace(
        string? path,
        string content,
        out string backupPath,
        out string error)
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

        try
        {
            var file = new FileInfo(path);
            if (file.Directory is { Exists: false } directory)
            {
                directory.Create();
            }

            if (file.Exists)
            {
                backupPath = $"{file.FullName}.{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.bak";
                File.Copy(file.FullName, backupPath, overwrite: true);
            }

            // Write-then-rename, not a direct overwrite. A guest that loses power mid-write is the
            // leading suspect for how the file gets corrupted in the first place, so the repair
            // itself must not have that same failure mode: the rename either happens or it does
            // not, and the old file survives until it does.
            var temporaryPath = $"{file.FullName}.d2rops-tmp";
            File.WriteAllText(temporaryPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, file.FullName, overwrite: true);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not write {path}: {ex.Message}";
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
            reason = $"content is only {content.Length} characters, below the {MinPlausibleContentLength} minimum for a real settings file";
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
