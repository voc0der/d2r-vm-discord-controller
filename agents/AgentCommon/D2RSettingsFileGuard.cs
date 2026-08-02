using System.Runtime.InteropServices;

namespace AgentCommon;

public enum D2RSettingsProtectionOutcome
{
    /// <summary>No path to protect (non-Windows, or the Saved Games folder could not be resolved).</summary>
    Skipped,

    /// <summary>The settings file does not exist yet, so there is nothing to lock.</summary>
    Missing,

    /// <summary>The file was already read-only when the agent started.</summary>
    AlreadyReadOnly,

    /// <summary>This agent set the read-only attribute.</summary>
    MadeReadOnly,

    /// <summary>The read-only attribute could not be set, or did not stick.</summary>
    Failed
}

public sealed record D2RSettingsProtectionResult(
    D2RSettingsProtectionOutcome Outcome,
    string? SettingsPath,
    bool ReadOnly,
    DateTimeOffset? LastWriteUtc,
    long? Length,
    string Message)
{
    public bool Ok => Outcome is D2RSettingsProtectionOutcome.AlreadyReadOnly
        or D2RSettingsProtectionOutcome.MadeReadOnly
        or D2RSettingsProtectionOutcome.Skipped;
}

/// <summary>
/// Keeps D2R from rewriting its own <c>Settings.json</c>. The game rewrites that file whenever it
/// exits (and silently regenerates it at defaults when it decides the file is unusable), which on a
/// fleet VM means a resolution/graphics change that every pixel classifier in this repo is calibrated
/// against can disappear between two sessions with nothing in any log. Marking the file read-only is
/// what actually stops it: D2R reads it fine and simply cannot write it back.
/// </summary>
public static class D2RSettingsFileGuard
{
    public const string SettingsFileName = "Settings.json";
    public const string SettingsFolderName = "Diablo II Resurrected";
    private const string SavedGamesFolderName = "Saved Games";

    // FOLDERID_SavedGames. Saved Games is relocatable, so the known-folder path is the only
    // reliable answer; %USERPROFILE%\Saved Games is the fallback when the shell call fails.
    private static readonly Guid SavedGamesFolderId = new("4C5C32FF-BB9D-43b0-B5B4-2D72E54EAAA4");

    /// <summary>
    /// Resolves the settings path this agent should protect, honouring an explicit config override.
    /// Returns null when there is nothing sensible to protect (non-Windows, or no profile path).
    /// </summary>
    public static string? ResolveSettingsPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPath.Trim()));
        }

        var savedGames = ResolveSavedGamesRoot();
        return string.IsNullOrWhiteSpace(savedGames)
            ? null
            : Path.Combine(savedGames, SettingsFolderName, SettingsFileName);
    }

    /// <summary>
    /// Resolves the path from config and marks it read-only. Idempotent: a file that is already
    /// read-only is reported, not rewritten.
    /// </summary>
    public static D2RSettingsProtectionResult Protect(VmAgentConfig config, Action<string>? log = null)
    {
        if (!config.ProtectD2RSettings)
        {
            return Report(
                new D2RSettingsProtectionResult(
                    D2RSettingsProtectionOutcome.Skipped,
                    SettingsPath: null,
                    ReadOnly: false,
                    LastWriteUtc: null,
                    Length: null,
                    Message: "protectD2RSettings is disabled in config; D2R can rewrite Settings.json."),
                log);
        }

        var path = ResolveSettingsPath(config.D2RSettingsPath);
        if (string.IsNullOrWhiteSpace(path))
        {
            return Report(
                new D2RSettingsProtectionResult(
                    D2RSettingsProtectionOutcome.Skipped,
                    SettingsPath: null,
                    ReadOnly: false,
                    LastWriteUtc: null,
                    Length: null,
                    Message: OperatingSystem.IsWindows()
                        ? $"Could not resolve the Saved Games folder; set d2rSettingsPath to the full path of {SettingsFileName}."
                        : "Not running on Windows; nothing to protect."),
                log);
        }

        return Report(EnsureReadOnly(path), log);
    }

    /// <summary>
    /// Sets the read-only attribute on one file and verifies it stuck. Separated from
    /// <see cref="Protect"/> so it is exercisable against any path, on any OS.
    /// </summary>
    public static D2RSettingsProtectionResult EnsureReadOnly(string settingsPath)
    {
        FileInfo file;
        try
        {
            file = new FileInfo(settingsPath);
        }
        catch (Exception ex)
        {
            return new D2RSettingsProtectionResult(
                D2RSettingsProtectionOutcome.Failed,
                settingsPath,
                ReadOnly: false,
                LastWriteUtc: null,
                Length: null,
                Message: $"Settings path is not usable: {ex.Message}");
        }

        if (!file.Exists)
        {
            // D2R writes the file the first time it runs as this user. Reporting rather than
            // creating one: an empty or hand-built Settings.json is exactly the "reset to
            // defaults" state being defended against, and locking a placeholder would make it
            // permanent.
            return new D2RSettingsProtectionResult(
                D2RSettingsProtectionOutcome.Missing,
                file.FullName,
                ReadOnly: false,
                LastWriteUtc: null,
                Length: null,
                Message: $"{file.FullName} does not exist; run D2R once so it writes the file, then restart the agent.");
        }

        try
        {
            if (file.Attributes.HasFlag(FileAttributes.ReadOnly))
            {
                return new D2RSettingsProtectionResult(
                    D2RSettingsProtectionOutcome.AlreadyReadOnly,
                    file.FullName,
                    ReadOnly: true,
                    file.LastWriteTimeUtc,
                    file.Length,
                    Message: $"{file.FullName} was already read-only.");
            }

            File.SetAttributes(file.FullName, file.Attributes | FileAttributes.ReadOnly);
        }
        catch (Exception ex)
        {
            return new D2RSettingsProtectionResult(
                D2RSettingsProtectionOutcome.Failed,
                file.FullName,
                ReadOnly: false,
                LastWriteUtc: SafeLastWriteUtc(file),
                Length: SafeLength(file),
                Message: $"Could not set the read-only attribute on {file.FullName}: {ex.Message}");
        }

        // Re-read rather than trusting the write. A set that quietly does not take (the file open
        // by D2R, an ACL that denies attribute writes) would otherwise be reported as protection.
        file.Refresh();
        var readOnly = false;
        try
        {
            readOnly = file.Exists && file.Attributes.HasFlag(FileAttributes.ReadOnly);
        }
        catch (Exception ex)
        {
            return new D2RSettingsProtectionResult(
                D2RSettingsProtectionOutcome.Failed,
                file.FullName,
                ReadOnly: false,
                LastWriteUtc: null,
                Length: null,
                Message: $"Could not confirm the read-only attribute on {file.FullName}: {ex.Message}");
        }

        return readOnly
            ? new D2RSettingsProtectionResult(
                D2RSettingsProtectionOutcome.MadeReadOnly,
                file.FullName,
                ReadOnly: true,
                SafeLastWriteUtc(file),
                SafeLength(file),
                Message: $"Marked {file.FullName} read-only; D2R can no longer overwrite it.")
            : new D2RSettingsProtectionResult(
                D2RSettingsProtectionOutcome.Failed,
                file.FullName,
                ReadOnly: false,
                SafeLastWriteUtc(file),
                SafeLength(file),
                Message: $"Set the read-only attribute on {file.FullName} but it did not stick.");
    }

    private static D2RSettingsProtectionResult Report(D2RSettingsProtectionResult result, Action<string>? log)
    {
        log?.Invoke($"D2R settings guard [{result.Outcome}]: {result.Message}");
        return result;
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
            : Path.Combine(profile, SavedGamesFolderName);
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

    private static DateTimeOffset? SafeLastWriteUtc(FileInfo file)
    {
        try
        {
            return file.Exists ? file.LastWriteTimeUtc : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static long? SafeLength(FileInfo file)
    {
        try
        {
            return file.Exists ? file.Length : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);
}
