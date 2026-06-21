using System.Diagnostics;

namespace D2RAgent;

internal static class AutoHotkeyInput
{
    public static AutoHotkeyScriptRun? StartReadyPump(
        string autoHotkeyPath,
        IEnumerable<string> processNames,
        ScreenPoint point,
        TimeSpan duration,
        TimeSpan interval)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var processExe = GetAutoHotkeyProcessExe(processNames);
        var script = $$"""
            #NoEnv
            #NoTrayIcon
            #SingleInstance Force
            SetTitleMatchMode, 2
            CoordMode, Mouse, Screen
            SetKeyDelay, 25, 60
            SetMouseDelay, 25

            endTime := A_TickCount + {{Math.Max(1, (int)duration.TotalMilliseconds)}}
            Loop
            {
                if (A_TickCount >= endTime)
                    break

                WinActivate, ahk_exe {{processExe}}
                WinWaitActive, ahk_exe {{processExe}},, 1
                MouseMove, {{point.X}}, {{point.Y}}, 0
                Click, Left
                SendEvent, {g down}
                Sleep, 60
                SendEvent, {g up}
                Sleep, {{Math.Max(50, (int)interval.TotalMilliseconds)}}
            }

            ExitApp, 0
            """;

        return StartScript(autoHotkeyPath, script);
    }

    public static bool TryClick(
        string autoHotkeyPath,
        IEnumerable<string> processNames,
        ScreenPoint point,
        MouseButton button,
        TimeSpan timeout)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var processExe = GetAutoHotkeyProcessExe(processNames);
        var clickButton = button == MouseButton.Right ? "Right" : "Left";
        var script = $$"""
            #NoEnv
            #NoTrayIcon
            #SingleInstance Force
            SetTitleMatchMode, 2
            CoordMode, Mouse, Screen
            SetMouseDelay, 25

            WinActivate, ahk_exe {{processExe}}
            WinWaitActive, ahk_exe {{processExe}},, 1
            MouseMove, {{point.X}}, {{point.Y}}, 0
            Click, {{clickButton}}

            ExitApp, 0
            """;

        using var run = StartScript(autoHotkeyPath, script);
        if (run is null)
        {
            return false;
        }

        return run.WaitForExit(timeout) && run.ExitCode == 0;
    }

    private static AutoHotkeyScriptRun? StartScript(string autoHotkeyPath, string script)
    {
        string? scriptPath = null;
        try
        {
            scriptPath = Path.Combine(
                Path.GetTempPath(),
                $"d2r-agent-{Guid.NewGuid():N}.ahk");
            File.WriteAllText(scriptPath, script);

            var startInfo = new ProcessStartInfo
            {
                FileName = ResolveAutoHotkeyPath(autoHotkeyPath),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(scriptPath);

            var process = Process.Start(startInfo);
            if (process is null)
            {
                TryDelete(scriptPath);
                return null;
            }

            return new AutoHotkeyScriptRun(process, scriptPath);
        }
        catch (Exception)
        {
            if (scriptPath is not null)
            {
                TryDelete(scriptPath);
            }

            return null;
        }
    }

    private static string ResolveAutoHotkeyPath(string autoHotkeyPath)
    {
        if (!string.IsNullOrWhiteSpace(autoHotkeyPath))
        {
            var configuredDirectory = Path.GetDirectoryName(autoHotkeyPath);
            if (!string.Equals(autoHotkeyPath, "AutoHotkey.exe", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(configuredDirectory))
            {
                return autoHotkeyPath;
            }

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            foreach (var candidate in new[]
                     {
                         autoHotkeyPath,
                         Path.Combine(programFiles, "AutoHotkey", "AutoHotkey.exe"),
                         Path.Combine(programFilesX86, "AutoHotkey", "AutoHotkey.exe")
                     })
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return autoHotkeyPath;
        }

        return "AutoHotkey.exe";
    }

    private static string GetAutoHotkeyProcessExe(IEnumerable<string> processNames)
    {
        var processName = processNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => Path.GetFileNameWithoutExtension(name))
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            ?? "D2R";

        processName = processName
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", "", StringComparison.Ordinal)
            .Replace(",", "", StringComparison.Ordinal);

        return processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName
            : $"{processName}.exe";
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Temporary script cleanup is best-effort.
        }
    }
}

internal sealed class AutoHotkeyScriptRun : IDisposable
{
    private readonly Process _process;
    private readonly string _scriptPath;

    public AutoHotkeyScriptRun(Process process, string scriptPath)
    {
        _process = process;
        _scriptPath = scriptPath;
    }

    public bool HasExited => _process.HasExited;

    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    public bool WaitForExit(TimeSpan timeout)
    {
        return _process.WaitForExit(timeout);
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(1000);
            }
        }
        catch
        {
            // The process may have exited between checks.
        }
        finally
        {
            _process.Dispose();
            try
            {
                File.Delete(_scriptPath);
            }
            catch
            {
                // Temporary script cleanup is best-effort.
            }
        }
    }
}
