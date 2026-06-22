using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace D2RAgent;

internal sealed record ProcessDiscoverySnapshot(
    string[] SearchNames,
    ProcessDiscoveryMatch[] Matches);

internal sealed record ProcessDiscoveryMatch(
    int ProcessId,
    string ProcessName,
    bool HasMainWindow,
    string? MainWindowTitle);

internal sealed record ProcessWindowTarget(
    int ProcessId,
    string ProcessName,
    int? SessionId,
    IntPtr WindowHandle,
    string? MainWindowTitle);

internal static class WindowsProcessFinder
{
    public static Process? FindProcess(IEnumerable<string> processNames)
    {
        return FindProcessesByNameOrWindowTitle(processNames)
            .OrderByDescending(process => SafeGetMainWindowHandle(process) != IntPtr.Zero)
            .FirstOrDefault();
    }

    public static ProcessWindowTarget? FindWindowTarget(IEnumerable<string> processNames)
    {
        var names = WindowsProcessIdentity.NormalizeProcessNames(processNames);
        ProcessWindowTarget? processFallback = null;

        foreach (var process in names.SelectMany(GetProcessesByNameSafe))
        {
            if (WindowsProcessIdentity.IsCurrentProcess(process.Id))
            {
                continue;
            }

            var target = ToWindowTarget(process);
            if (target is null)
            {
                continue;
            }

            if (target.WindowHandle != IntPtr.Zero)
            {
                return target;
            }

            processFallback ??= target;
        }

        var windowTarget = FindTopLevelWindowTargets(names).FirstOrDefault();
        return windowTarget ?? processFallback;
    }

    public static bool IsAnyProcessRunning(IEnumerable<string> processNames)
    {
        return FindWindowTarget(processNames) is not null
            || FindProcessesByNameOrWindowTitle(processNames).Any();
    }

    public static ProcessDiscoverySnapshot Discover(IEnumerable<string> processNames)
    {
        var names = WindowsProcessIdentity.NormalizeProcessNames(processNames);
        var processMatches = FindProcessesByNameOrWindowTitle(names)
            .Select(ToWindowTarget)
            .Where(match => match is not null)
            .Cast<ProcessWindowTarget>();
        var windowMatches = FindTopLevelWindowTargets(names);
        var matches = processMatches
            .Concat(windowMatches)
            .GroupBy(match => match.ProcessId)
            .Select(group => group.OrderByDescending(match => match.WindowHandle != IntPtr.Zero).First())
            .Select(match => new ProcessDiscoveryMatch(
                match.ProcessId,
                match.ProcessName,
                match.WindowHandle != IntPtr.Zero,
                match.MainWindowTitle))
            .OrderBy(match => match.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(match => match.ProcessId)
            .ToArray();

        return new ProcessDiscoverySnapshot(names, matches);
    }

    public static IEnumerable<Process> FindProcessesByNameOrWindowTitle(IEnumerable<string> processNames)
    {
        var names = WindowsProcessIdentity.NormalizeProcessNames(processNames);
        foreach (var process in names.SelectMany(GetProcessesByNameSafe))
        {
            if (!WindowsProcessIdentity.IsCurrentProcess(process.Id))
            {
                yield return process;
            }
        }

        foreach (var target in FindTopLevelWindowTargets(names))
        {
            Process? process = null;
            try
            {
                process = Process.GetProcessById(target.ProcessId);
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }

            if (process is not null && !WindowsProcessIdentity.IsCurrentProcess(process.Id))
            {
                yield return process;
            }
        }
    }

    public static string SafeGetMainWindowTitle(Process process)
    {
        try
        {
            return process.MainWindowTitle ?? "";
        }
        catch (InvalidOperationException)
        {
            return "";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "";
        }
    }

    public static bool HasMainWindow(Process process)
    {
        return SafeGetMainWindowHandle(process) != IntPtr.Zero;
    }

    public static IntPtr SafeGetMainWindowHandle(Process process)
    {
        try
        {
            return process.MainWindowHandle;
        }
        catch (InvalidOperationException)
        {
            return IntPtr.Zero;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return IntPtr.Zero;
        }
    }

    private static ProcessWindowTarget? ToWindowTarget(Process process)
    {
        try
        {
            var handle = SafeGetMainWindowHandle(process);
            if (handle == IntPtr.Zero)
            {
                handle = FindTopLevelWindowHandleForProcess(process.Id);
            }

            return new ProcessWindowTarget(
                process.Id,
                process.ProcessName,
                SafeGetSessionId(process),
                handle,
                handle == IntPtr.Zero ? SafeGetMainWindowTitle(process) : GetWindowTitle(handle));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static IEnumerable<ProcessWindowTarget> FindTopLevelWindowTargets(string[] processNames)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        var normalized = WindowsProcessIdentity.NormalizeProcessNames(processNames);
        var titleNeedles = WindowsProcessIdentity.GetWindowTitleNeedles(normalized);
        foreach (var window in EnumerateTopLevelWindows())
        {
            if (!IsWindowVisible(window))
            {
                continue;
            }

            var title = GetWindowTitle(window);
            _ = GetWindowThreadProcessId(window, out var pid);
            if (pid == 0 || WindowsProcessIdentity.IsCurrentProcess((int)pid))
            {
                continue;
            }

            var processName = "";
            int? sessionId = null;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                processName = process.ProcessName;
                sessionId = SafeGetSessionId(process);
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }

            var processMatches = normalized.Any(name =>
                !string.IsNullOrWhiteSpace(processName)
                && name.Equals(processName, StringComparison.OrdinalIgnoreCase));
            var titleMatches = titleNeedles.Any(needle =>
                !string.IsNullOrWhiteSpace(title)
                && title.Contains(needle, StringComparison.OrdinalIgnoreCase));
            if (!processMatches && !titleMatches)
            {
                continue;
            }

            yield return new ProcessWindowTarget(
                (int)pid,
                string.IsNullOrWhiteSpace(processName) ? "?" : processName,
                sessionId,
                window,
                title);
        }
    }

    private static IntPtr FindTopLevelWindowHandleForProcess(int processId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return IntPtr.Zero;
        }

        foreach (var window in EnumerateTopLevelWindows())
        {
            if (!IsWindowVisible(window))
            {
                continue;
            }

            _ = GetWindowThreadProcessId(window, out var pid);
            if (pid == processId)
            {
                return window;
            }
        }

        return IntPtr.Zero;
    }

    private static int? SafeGetSessionId(Process process)
    {
        try
        {
            return process.SessionId;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static IEnumerable<IntPtr> EnumerateTopLevelWindows()
    {
        var windows = new List<IntPtr>();
        _ = EnumWindows((windowHandle, _) =>
        {
            windows.Add(windowHandle);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static string GetWindowTitle(IntPtr windowHandle)
    {
        var length = GetWindowTextLength(windowHandle);
        if (length <= 0)
        {
            return "";
        }

        var builder = new StringBuilder(length + 1);
        return GetWindowText(windowHandle, builder, builder.Capacity) > 0
            ? builder.ToString()
            : "";
    }

    private static IEnumerable<Process> GetProcessesByNameSafe(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName);
        }
        catch (InvalidOperationException)
        {
            return [];
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    private static IEnumerable<Process> GetProcessesSafe()
    {
        try
        {
            return Process.GetProcesses();
        }
        catch (InvalidOperationException)
        {
            return [];
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    private delegate bool EnumWindowsProc(IntPtr windowHandle, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr windowHandle, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);
}
