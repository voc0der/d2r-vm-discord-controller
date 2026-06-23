using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace D2RAgent;

internal sealed record ProcessDiscoverySnapshot(
    string[] SearchNames,
    ProcessDiscoveryMatch[] Matches,
    ProcessDiscoveryMatch[] FallbackMatches);

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

// EnumWindows + per-window GetWindowTitle (a SendMessage under the hood, up to 200ms each) is
// the expensive part of detection. Every top-level caller in a single status collection
// (Battle.net check, D2R check, process discovery, input diagnostics) used to pay that cost
// independently - up to 7 full desktop window passes for one /d2r status call - which is what
// pushed status collection past its heartbeat timeout and made detection look 100% broken.
// Passing one of these through a single detection pass memoizes both the window enumeration
// and any per-window title lookups, so the cost is paid at most once no matter how many
// different name searches consult it.
internal sealed class DesktopWindowScanCache
{
    private List<IntPtr>? _visibleWindows;
    private readonly Dictionary<IntPtr, (int Pid, string ProcessName, int? SessionId)> _windowInfo = new();
    private readonly Dictionary<IntPtr, string> _titles = new();

    public List<IntPtr> GetVisibleWindows(Func<List<IntPtr>> enumerate)
    {
        return _visibleWindows ??= enumerate();
    }

    public (int Pid, string ProcessName, int? SessionId) GetWindowInfo(IntPtr handle, Func<(int, string, int?)> resolve)
    {
        if (_windowInfo.TryGetValue(handle, out var cached))
        {
            return cached;
        }

        var resolved = resolve();
        _windowInfo[handle] = resolved;
        return resolved;
    }

    public string GetTitle(IntPtr handle, Func<string> resolve)
    {
        if (_titles.TryGetValue(handle, out var cached))
        {
            return cached;
        }

        var title = resolve();
        _titles[handle] = title;
        return title;
    }
}

internal static class WindowsProcessFinder
{
    public static Process? FindProcess(IEnumerable<string> processNames)
    {
        return FindProcessesByNameOrWindowTitle(processNames)
            .OrderByDescending(process => SafeGetMainWindowHandle(process) != IntPtr.Zero)
            .FirstOrDefault();
    }

    public static ProcessWindowTarget? FindWindowTarget(IEnumerable<string> processNames, DesktopWindowScanCache? cache = null)
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

        var windowTarget = FindTopLevelWindowTargets(names, cache).FirstOrDefault();
        return windowTarget ?? processFallback;
    }

    public static bool IsAnyProcessRunning(IEnumerable<string> processNames, DesktopWindowScanCache? cache = null)
    {
        var names = WindowsProcessIdentity.NormalizeProcessNames(processNames);

        // Same primitive as a bare Process.GetProcessesByName(name).Length > 0 - the simple,
        // long-reliable check - tried first. It is exact-match only, so it can't be the whole
        // story (a renamed/wrapped build won't match), but when it does hit there is no reason
        // to ever touch EnumWindows for this check.
        if (IsAnyNamedProcessRunning(names))
        {
            return true;
        }

        return FindWindowTarget(names, cache) is not null
            || FindProcessesByNameOrWindowTitle(names, cache).Any();
    }

    public static bool IsAnyNamedProcessRunning(IEnumerable<string> processNames)
    {
        var names = WindowsProcessIdentity.NormalizeProcessNames(processNames);
        return names.SelectMany(GetProcessesByNameSafe)
            .Any(process => !WindowsProcessIdentity.IsCurrentProcess(process.Id));
    }

    public static ProcessDiscoverySnapshot Discover(IEnumerable<string> processNames, DesktopWindowScanCache? cache = null)
    {
        var names = WindowsProcessIdentity.NormalizeProcessNames(processNames);
        var processMatches = names.SelectMany(GetProcessesByNameSafe)
            .Where(process => !WindowsProcessIdentity.IsCurrentProcess(process.Id))
            .Select(ToWindowTarget)
            .Where(match => match is not null)
            .Cast<ProcessWindowTarget>();
        var windowMatches = FindTopLevelWindowTargets(names, cache);
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

        // The configured search names need an exact match (Process.GetProcessesByName has no
        // fuzzy mode), so a real, running D2R process under any other name - a build variant,
        // a renamed/wrapped executable, anything not literally "D2R" - reads as a permanent
        // zero, indistinguishable from D2R simply not running. Only spend the full
        // GetProcesses() scan once the strict search has already failed, and only match
        // substrings relevant to what was actually being searched for (D2R search names never
        // fall back to a Battle.net-shaped name, and vice versa) so the fallback can't misreport
        // one product as the other.
        var fallbackNeedles = WindowsProcessIdentity.GetFallbackProcessNameNeedles(names);
        var fallbackMatches = matches.Length == 0 && fallbackNeedles.Length > 0
            ? FindLikelyProcesses(fallbackNeedles)
            : [];

        return new ProcessDiscoverySnapshot(names, matches, fallbackMatches);
    }

    private static ProcessDiscoveryMatch[] FindLikelyProcesses(string[] nameNeedles)
    {
        return GetProcessesSafe()
            .Where(process => !WindowsProcessIdentity.IsCurrentProcess(process.Id))
            .Select(process => (process, name: SafeGetProcessName(process)))
            .Where(entry => nameNeedles.Any(needle => entry.name.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            .Select(entry =>
            {
                // Same hang hazard as ToWindowTarget above: SafeGetMainWindowTitle reaches
                // Process.MainWindowTitle, which is not timeout-protected like GetWindowTitle.
                // Resolve the handle once and only risk the title fetch when a real window
                // exists to fetch it from.
                var handle = SafeGetMainWindowHandle(entry.process);
                return new ProcessDiscoveryMatch(
                    entry.process.Id,
                    entry.name,
                    handle != IntPtr.Zero,
                    handle != IntPtr.Zero ? GetWindowTitle(handle) : "");
            })
            .ToArray();
    }

    private static string SafeGetProcessName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch (InvalidOperationException)
        {
            return "?";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "?";
        }
    }

    public static IEnumerable<Process> FindProcessesByNameOrWindowTitle(
        IEnumerable<string> processNames, DesktopWindowScanCache? cache = null)
    {
        var names = WindowsProcessIdentity.NormalizeProcessNames(processNames);
        foreach (var process in names.SelectMany(GetProcessesByNameSafe))
        {
            if (!WindowsProcessIdentity.IsCurrentProcess(process.Id))
            {
                yield return process;
            }
        }

        foreach (var target in FindTopLevelWindowTargets(names, cache))
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
                // SafeGetMainWindowTitle goes through Process.MainWindowTitle, which is not
                // timeout-protected the way GetWindowTitle is - it's the exact blocking
                // SendMessage-to-an-unresponsive-window hazard described above, just reached
                // through the BCL instead of a raw Win32 call. A zero handle means there's no
                // window to title in the first place, so there's nothing worth risking a hang
                // to fetch.
                handle == IntPtr.Zero ? "" : GetWindowTitle(handle));
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

    private static IEnumerable<ProcessWindowTarget> FindTopLevelWindowTargets(
        string[] processNames, DesktopWindowScanCache? cache = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        var normalized = WindowsProcessIdentity.NormalizeProcessNames(processNames);
        var titleNeedles = WindowsProcessIdentity.GetWindowTitleNeedles(normalized);
        var windows = cache is null
            ? EnumerateTopLevelWindows().Where(IsWindowVisible).ToList()
            : cache.GetVisibleWindows(() => EnumerateTopLevelWindows().Where(IsWindowVisible).ToList());

        foreach (var window in windows)
        {
            // PID lookup is a direct kernel call and never blocks. GetWindowTitle, for a
            // window owned by another process, is backed by SendMessage(WM_GETTEXT) under
            // the hood and can stall for seconds if that window's message queue isn't being
            // serviced (e.g. mid cinematic/loading transition) - exactly when detection
            // matters most. Resolve the process name first so the common case (name already
            // matches) never has to touch the window's message queue at all, and only ask
            // for a title when title-based matching is actually configured and needed. When a
            // cache is supplied, both the PID/process-name resolution and the title are
            // memoized per window handle so a second search (e.g. Battle.net right after D2R)
            // within the same detection pass never repeats either cost.
            var (pid, processName, sessionId) = cache is null
                ? ResolveWindowInfo(window)
                : cache.GetWindowInfo(window, () => ResolveWindowInfo(window));
            if (pid == 0 || WindowsProcessIdentity.IsCurrentProcess(pid))
            {
                continue;
            }

            var processMatches = normalized.Any(name =>
                !string.IsNullOrWhiteSpace(processName)
                && name.Equals(processName, StringComparison.OrdinalIgnoreCase));
            if (!processMatches && titleNeedles.Length == 0)
            {
                continue;
            }

            var title = cache is null
                ? GetWindowTitle(window)
                : cache.GetTitle(window, () => GetWindowTitle(window));
            var titleMatches = !processMatches
                && WindowsProcessIdentity.IsWindowTitleMatch(normalized, processName, title);
            if (!processMatches && !titleMatches)
            {
                continue;
            }

            yield return new ProcessWindowTarget(
                pid,
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

    private static (int Pid, string ProcessName, int? SessionId) ResolveWindowInfo(IntPtr window)
    {
        _ = GetWindowThreadProcessId(window, out var pid);
        if (pid == 0)
        {
            return (0, "", null);
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

        return ((int)pid, processName, sessionId);
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

    private const uint WmGetText = 0x000D;
    private const uint WmGetTextLength = 0x000E;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint WindowTitleTimeoutMs = 200;

    private static string GetWindowTitle(IntPtr windowHandle)
    {
        // GetWindowText/GetWindowTextLength are implemented as a blocking SendMessage to
        // the target window for any window not owned by this process. If that window's
        // message queue isn't being serviced - mid loading screen, mid cinematic, just
        // generally busy - the call can stall for seconds, and this runs once per visible
        // desktop window on every detection pass. SendMessageTimeout with a short,
        // hung-aborting timeout caps the damage from any one unresponsive window.
        var lengthSent = SendMessageTimeout(
            windowHandle, WmGetTextLength, IntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung, WindowTitleTimeoutMs, out var lengthResult);
        var length = (int)lengthResult;
        if (lengthSent == IntPtr.Zero || length <= 0)
        {
            return "";
        }

        var builder = new StringBuilder(length + 1);
        var textSent = SendMessageTimeout(
            windowHandle, WmGetText, (IntPtr)builder.Capacity, builder, SmtoAbortIfHung, WindowTitleTimeoutMs, out _);
        return textSent != IntPtr.Zero ? builder.ToString() : "";
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr windowHandle, uint message, IntPtr wParam, StringBuilder lParam, uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);
}
