using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace AgentCommon;

public static class SelfUpdater
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    static SelfUpdater()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("D2ROps-SelfUpdater/1.0");
    }

    public static async Task<bool> CheckAndOfferUpdateAsync(
        SelfUpdateOptions options,
        CancellationToken cancellationToken = default)
    {
        var result = await CheckAndStartUpdateAsync(options, requirePrompt: true, cancellationToken);
        if (!result.Ok)
        {
            Console.WriteLine($"Update check failed, continuing startup: {result.Message}");
        }

        return result.UpdateStarted;
    }

    public static async Task<SelfUpdateResult> CheckAndStartUpdateAsync(
        SelfUpdateOptions options,
        bool requirePrompt,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return SelfUpdateResult.Skipped("Self-update only runs on Windows.");
        }

        if (requirePrompt && !ConsolePrompt.CanPrompt)
        {
            return SelfUpdateResult.Skipped("Interactive prompt is not available.");
        }

        if (IsDisabled())
        {
            return SelfUpdateResult.Skipped("Self-update is disabled by D2ROPS_DISABLE_UPDATE_CHECK.");
        }

        try
        {
            if (!TryGetCurrentExecutable(out var currentExe, out var executableMessage))
            {
                return SelfUpdateResult.Skipped(executableMessage);
            }

            var currentVersion = GetCurrentVersion();
            if (currentVersion is null)
            {
                return SelfUpdateResult.Skipped("Current app version is not SemVer.");
            }

            var release = await GetLatestReleaseAsync(options, cancellationToken);
            if (release.Version <= currentVersion)
            {
                return new SelfUpdateResult(
                    Ok: true,
                    CheckedLatest: true,
                    UpdateAvailable: false,
                    UpdateStarted: false,
                    Message: $"{options.AppName} is current at {currentVersion}.",
                    CurrentVersion: currentVersion.ToString(),
                    LatestVersion: release.Version.ToString(),
                    LogPath: null);
            }

            Console.WriteLine($"{options.AppName} {release.TagName} is available. Current version: {currentVersion}");
            Console.WriteLine(release.HtmlUrl);
            if (requirePrompt && !ConsolePrompt.ReadBool("Update in place now", true))
            {
                return new SelfUpdateResult(
                    Ok: true,
                    CheckedLatest: true,
                    UpdateAvailable: true,
                    UpdateStarted: false,
                    Message: $"Update to {release.TagName} was skipped by operator.",
                    CurrentVersion: currentVersion.ToString(),
                    LatestVersion: release.Version.ToString(),
                    LogPath: null);
            }

            var scriptPath = WriteUpdaterScript(options, release, currentExe);
            var logPath = Path.ChangeExtension(scriptPath, ".log");
            StartUpdaterScript(scriptPath);

            Console.WriteLine();
            Console.WriteLine("Updater started. This process will exit so the exe can be replaced.");
            Console.WriteLine($"Updater log: {logPath}");
            return new SelfUpdateResult(
                Ok: true,
                CheckedLatest: true,
                UpdateAvailable: true,
                UpdateStarted: true,
                Message: $"Updater started for {options.AppName} {release.TagName}.",
                CurrentVersion: currentVersion.ToString(),
                LatestVersion: release.Version.ToString(),
                LogPath: logPath);
        }
        catch (Exception ex)
        {
            return SelfUpdateResult.Failed(ex.Message);
        }
    }

    private static bool IsDisabled()
    {
        return bool.TryParse(Environment.GetEnvironmentVariable("D2ROPS_DISABLE_UPDATE_CHECK"), out var disabled)
            && disabled;
    }

    private static bool TryGetCurrentExecutable(out string currentExe, out string message)
    {
        currentExe = Environment.ProcessPath ?? "";
        return IsPublishedWindowsExePath(currentExe, File.Exists, out message);
    }

    internal static bool IsPublishedWindowsExePath(
        string? currentExe,
        Func<string, bool> fileExists,
        out string message)
    {
        if (string.IsNullOrWhiteSpace(currentExe)
            || !currentExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || !fileExists(currentExe))
        {
            message = "Current process is not a published Windows exe.";
            return false;
        }

        message = "";
        return true;
    }

    private static ReleaseVersion? GetCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(SelfUpdater).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        return ReleaseVersion.TryParse(informationalVersion, out var parsed)
            ? parsed
            : null;
    }

    private static async Task<LatestRelease> GetLatestReleaseAsync(
        SelfUpdateOptions options,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{options.Owner}/{options.Repository}/releases/latest";
        using var response = await Http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var tagName = root.GetProperty("tag_name").GetString()
            ?? throw new InvalidOperationException("Latest release did not include tag_name.");
        if (!ReleaseVersion.TryParse(tagName, out var version))
        {
            throw new InvalidOperationException($"Latest release tag is not SemVer: {tagName}");
        }

        var htmlUrl = root.TryGetProperty("html_url", out var htmlUrlProperty)
            ? htmlUrlProperty.GetString() ?? ""
            : "";

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (!string.Equals(name, options.AssetName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var downloadUrl = asset.GetProperty("browser_download_url").GetString()
                ?? throw new InvalidOperationException($"Release asset {options.AssetName} did not include a download URL.");
            return new LatestRelease(tagName, version, htmlUrl, downloadUrl);
        }

        throw new InvalidOperationException($"Latest release did not include asset {options.AssetName}.");
    }

    private static string WriteUpdaterScript(
        SelfUpdateOptions options,
        LatestRelease release,
        string currentExe)
    {
        var installDirectory = Path.GetDirectoryName(currentExe)
            ?? throw new InvalidOperationException("Could not find current exe directory.");
        var scriptPath = Path.Combine(
            Path.GetTempPath(),
            $"d2rops-update-{options.AppName}-{Guid.NewGuid():N}.ps1");
        var logPath = Path.ChangeExtension(scriptPath, ".log");
        var restartArgumentLine = string.Join(" ", options.RestartArgs.Select(WindowsArgumentQuote));
        var restartScheduledTaskName = options.RestartScheduledTaskName ?? "";
        var targetExeName = Path.GetFileName(currentExe);

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $parentId = {{Environment.ProcessId}}
            $downloadUrl = {{PsQuote(release.DownloadUrl)}}
            $installDirectory = {{PsQuote(installDirectory)}}
            $targetExe = {{PsQuote(currentExe)}}
            $targetExeName = {{PsQuote(targetExeName)}}
            $restartArgumentLine = {{PsQuote(restartArgumentLine)}}
            $restartScheduledTaskName = {{PsQuote(restartScheduledTaskName)}}
            $logPath = {{PsQuote(logPath)}}
            $zipPath = Join-Path ([System.IO.Path]::GetTempPath()) ('d2rops-update-' + [guid]::NewGuid().ToString('N') + '.zip')
            $extractDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('d2rops-update-' + [guid]::NewGuid().ToString('N'))

            function Write-UpdateLog([string]$message) {
                Add-Content -Path $logPath -Value ((Get-Date).ToString('o') + ' ' + $message)
            }

            function Resolve-RestartExe {
                $sameNamePayload = Get-ChildItem -LiteralPath $extractDirectory -Filter $targetExeName -File -Recurse | Select-Object -First 1
                if ($null -ne $sameNamePayload) {
                    return (Join-Path $installDirectory $sameNamePayload.Name)
                }

                $payloadExes = @(Get-ChildItem -LiteralPath $extractDirectory -Filter '*.exe' -File -Recurse)
                if ($payloadExes.Count -eq 1) {
                    return (Join-Path $installDirectory $payloadExes[0].Name)
                }

                if ($payloadExes.Count -gt 1) {
                    $names = ($payloadExes | ForEach-Object { $_.Name } | Sort-Object -Unique) -join ', '
                    throw ('Could not choose a restart exe from update payload: ' + $names)
                }

                return $targetExe
            }

            function New-D2ROpsScheduledTaskAction([string]$restartExe) {
                if ($restartArgumentLine.Length -gt 0) {
                    return New-ScheduledTaskAction -Execute $restartExe -Argument $restartArgumentLine -WorkingDirectory $installDirectory
                }

                return New-ScheduledTaskAction -Execute $restartExe -WorkingDirectory $installDirectory
            }

            function Start-D2ROpsProcess([string]$restartExe) {
                if ($restartArgumentLine.Length -gt 0) {
                    Write-UpdateLog ('Restarting process ' + $restartExe)
                    Start-Process -FilePath $restartExe -ArgumentList $restartArgumentLine -WorkingDirectory $installDirectory
                } else {
                    Write-UpdateLog ('Restarting process ' + $restartExe)
                    Start-Process -FilePath $restartExe -WorkingDirectory $installDirectory
                }
            }

            try {
                Write-UpdateLog 'Waiting for current process to exit.'
                Wait-Process -Id $parentId -ErrorAction SilentlyContinue
                Start-Sleep -Milliseconds 750

                Write-UpdateLog ('Downloading ' + $downloadUrl)
                Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath -UseBasicParsing

                Write-UpdateLog ('Extracting to ' + $extractDirectory)
                New-Item -ItemType Directory -Force -Path $extractDirectory | Out-Null
                Expand-Archive -Path $zipPath -DestinationPath $extractDirectory -Force

                $restartExe = Resolve-RestartExe
                Write-UpdateLog ('Restart exe resolved as ' + $restartExe)

                Write-UpdateLog ('Copying files to ' + $installDirectory)
                Copy-Item -Path (Join-Path $extractDirectory '*') -Destination $installDirectory -Recurse -Force

                if ($restartScheduledTaskName.Length -gt 0) {
                    $task = Get-ScheduledTask -TaskName $restartScheduledTaskName -ErrorAction SilentlyContinue
                    $taskRestarted = $false
                    if ($null -ne $task) {
                        try {
                            Write-UpdateLog ('Updating scheduled task action ' + $restartScheduledTaskName + ' -> ' + $restartExe)
                            Set-ScheduledTask -TaskName $restartScheduledTaskName -Action (New-D2ROpsScheduledTaskAction $restartExe) | Out-Null
                            Write-UpdateLog ('Restarting scheduled task ' + $restartScheduledTaskName)
                            for ($attempt = 0; $attempt -lt 20; $attempt++) {
                                $task = Get-ScheduledTask -TaskName $restartScheduledTaskName -ErrorAction SilentlyContinue
                                if ($null -eq $task -or $task.State -ne 'Running') {
                                    break
                                }

                                Start-Sleep -Milliseconds 500
                            }

                            Start-ScheduledTask -TaskName $restartScheduledTaskName
                            $taskRestarted = $true
                        } catch {
                            Write-UpdateLog ('Could not restart scheduled task ' + $restartScheduledTaskName + ': ' + $_.Exception.Message)
                        }
                    } else {
                        Write-UpdateLog ('Scheduled task not found: ' + $restartScheduledTaskName)
                    }

                    if (-not $taskRestarted) {
                        Start-D2ROpsProcess $restartExe
                    }
                } else {
                    Start-D2ROpsProcess $restartExe
                }
                Write-UpdateLog 'Update completed.'
            } catch {
                Write-UpdateLog ('Update failed: ' + $_.Exception.Message)
                throw
            } finally {
                Remove-Item -Force -ErrorAction SilentlyContinue $zipPath
                Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $extractDirectory
            }
            """;

        File.WriteAllText(scriptPath, script, Encoding.UTF8);
        return scriptPath;
    }

    private static void StartUpdaterScript(string scriptPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = false
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        Process.Start(startInfo);
    }

    private static string PsQuote(string value)
    {
        return "'" + value.Replace("'", "''") + "'";
    }

    private static string WindowsArgumentQuote(string value)
    {
        if (value.Length > 0 && !value.Any(static c => char.IsWhiteSpace(c) || c == '"'))
        {
            return value;
        }

        var builder = new StringBuilder();
        builder.Append('"');
        var backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
            }
            else
            {
                builder.Append('\\', backslashes);
                builder.Append(c);
            }

            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private sealed record LatestRelease(
        string TagName,
        ReleaseVersion Version,
        string HtmlUrl,
        string DownloadUrl);

    private sealed record ReleaseVersion(int Major, int Minor, int Patch) : IComparable<ReleaseVersion>
    {
        public int CompareTo(ReleaseVersion? other)
        {
            if (other is null)
            {
                return 1;
            }

            var major = Major.CompareTo(other.Major);
            if (major != 0)
            {
                return major;
            }

            var minor = Minor.CompareTo(other.Minor);
            return minor != 0 ? minor : Patch.CompareTo(other.Patch);
        }

        public override string ToString()
        {
            return $"{Major}.{Minor}.{Patch}";
        }

        public static bool operator >(ReleaseVersion left, ReleaseVersion right)
        {
            return left.CompareTo(right) > 0;
        }

        public static bool operator <(ReleaseVersion left, ReleaseVersion right)
        {
            return left.CompareTo(right) < 0;
        }

        public static bool operator >=(ReleaseVersion left, ReleaseVersion right)
        {
            return left.CompareTo(right) >= 0;
        }

        public static bool operator <=(ReleaseVersion left, ReleaseVersion right)
        {
            return left.CompareTo(right) <= 0;
        }

        public static bool TryParse(string? value, out ReleaseVersion version)
        {
            version = new ReleaseVersion(0, 0, 0);
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var normalized = value.Trim();
            if (normalized.StartsWith('v') || normalized.StartsWith('V'))
            {
                normalized = normalized[1..];
            }

            normalized = normalized.Split('+', 2)[0].Split('-', 2)[0];
            var parts = normalized.Split('.');
            if (parts.Length != 3
                || !int.TryParse(parts[0], out var major)
                || !int.TryParse(parts[1], out var minor)
                || !int.TryParse(parts[2], out var patch))
            {
                return false;
            }

            version = new ReleaseVersion(major, minor, patch);
            return true;
        }
    }
}

public sealed record SelfUpdateResult(
    bool Ok,
    bool CheckedLatest,
    bool UpdateAvailable,
    bool UpdateStarted,
    string Message,
    string? CurrentVersion,
    string? LatestVersion,
    string? LogPath)
{
    public static SelfUpdateResult Skipped(string message) =>
        new(
            Ok: true,
            CheckedLatest: false,
            UpdateAvailable: false,
            UpdateStarted: false,
            Message: message,
            CurrentVersion: null,
            LatestVersion: null,
            LogPath: null);

    public static SelfUpdateResult Failed(string message) =>
        new(
            Ok: false,
            CheckedLatest: false,
            UpdateAvailable: false,
            UpdateStarted: false,
            Message: message,
            CurrentVersion: null,
            LatestVersion: null,
            LogPath: null);
}
