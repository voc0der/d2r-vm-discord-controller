using System.Diagnostics;

namespace D2RAgent;

internal sealed record ProcessDiscoverySnapshot(
    string[] SearchNames,
    ProcessDiscoveryMatch[] Matches);

internal sealed record ProcessDiscoveryMatch(
    int ProcessId,
    string ProcessName,
    bool HasMainWindow,
    string? MainWindowTitle);

internal static class WindowsProcessFinder
{
    public static Process? FindProcess(IEnumerable<string> processNames)
    {
        return FindProcessesByNameOrWindowTitle(processNames)
            .OrderByDescending(HasMainWindow)
            .FirstOrDefault();
    }

    public static bool IsAnyProcessRunning(IEnumerable<string> processNames)
    {
        return FindProcessesByNameOrWindowTitle(processNames).Any();
    }

    public static ProcessDiscoverySnapshot Discover(IEnumerable<string> processNames)
    {
        var names = WindowsProcessIdentity.NormalizeProcessNames(processNames);
        var matches = FindProcessesByNameOrWindowTitle(names)
            .GroupBy(process => process.Id)
            .Select(group => group.First())
            .Select(ToDiscoveryMatch)
            .Where(match => match is not null)
            .Cast<ProcessDiscoveryMatch>()
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

        var titleNeedles = WindowsProcessIdentity.GetWindowTitleNeedles(names);
        if (titleNeedles.Length == 0)
        {
            yield break;
        }

        foreach (var process in GetProcessesSafe())
        {
            if (WindowsProcessIdentity.IsCurrentProcess(process.Id) || !HasMainWindow(process))
            {
                continue;
            }

            var title = SafeGetMainWindowTitle(process);
            if (titleNeedles.Any(needle => title.Contains(needle, StringComparison.OrdinalIgnoreCase)))
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
        try
        {
            return process.MainWindowHandle != IntPtr.Zero;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static ProcessDiscoveryMatch? ToDiscoveryMatch(Process process)
    {
        try
        {
            return new ProcessDiscoveryMatch(
                process.Id,
                process.ProcessName,
                HasMainWindow(process),
                SafeGetMainWindowTitle(process));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
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
}
