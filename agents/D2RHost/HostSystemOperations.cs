using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AgentCommon;

namespace D2RHost;

public sealed class HostSystemOperations
{
    /// <summary>
    /// <c>SetSuspendState</c> blocks until the machine resumes, so a call that returns almost
    /// immediately means the transition was refused rather than slept-and-woken. Modern Standby
    /// laptops do exactly that: the call reports success and the machine stays awake.
    /// </summary>
    private static readonly TimeSpan MinimumCredibleSleep = TimeSpan.FromSeconds(5);

    private readonly ILogger<HostSystemOperations> _logger;

    public HostSystemOperations(ILogger<HostSystemOperations> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Raised when a queued sleep did not actually suspend the machine. The master turns this into
    /// a Discord message; a worker has no Discord of its own and relies on the log.
    /// </summary>
    public event Action<string>? SleepFailed;

    /// <summary>
    /// Single place the failure event is raised, so every path that discovers a sleep did not
    /// happen reaches the operator the same way.
    /// </summary>
    internal void ReportSleepFailure(string message)
    {
        SleepFailed?.Invoke(message);
    }

    /// <summary>
    /// Verifies that Windows exposes a usable sleep state and enables the privilege sleeping needs,
    /// so a machine that cannot sleep says so before the command reports the action as queued.
    /// </summary>
    public bool TryPrepareSleep(out string? error)
    {
        if (!OperatingSystem.IsWindows())
        {
            error = "Host system power actions require Windows.";
            return false;
        }

        if (!GetPwrCapabilities(out var capabilities))
        {
            var nativeError = new Win32Exception(Marshal.GetLastWin32Error());
            error = $"Could not query Windows sleep capabilities: {nativeError.Message}\n"
                + DescribeSleepStates();
            return false;
        }

        if (!HasSupportedSleepState(
                capabilities.SystemS1 != 0,
                capabilities.SystemS2 != 0,
                capabilities.SystemS3 != 0,
                capabilities.AoAc != 0))
        {
            error = "Windows reports no supported sleep state "
                + "(S1, S2, S3, or Modern Standby/S0 low-power idle).\n"
                + DescribeSleepStates();
            return false;
        }

        return WindowsShutdownPrivilege.TryEnable(out error);
    }

    internal static bool HasSupportedSleepState(
        bool systemS1,
        bool systemS2,
        bool systemS3,
        bool aoAc)
    {
        return systemS1 || systemS2 || systemS3 || aoAc;
    }

    public void Queue(HostSystemPowerAction action)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            try
            {
                Execute(action);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Host system action {Action} failed.", action);
                if (action == HostSystemPowerAction.Sleep)
                {
                    ReportSleepFailure($"Host sleep failed: {ex.Message}");
                }
            }
        });
    }

    private void Execute(HostSystemPowerAction action)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Host system power actions require Windows.");
        }

        if (action == HostSystemPowerAction.Sleep)
        {
            SleepHost();
            return;
        }

        using var process = Process.Start(HostSystemPowerActions.CreateShutdownStartInfo(action))
            ?? throw new InvalidOperationException("Could not start shutdown.exe.");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void SleepHost()
    {
        // Shutdown and restart shell out to shutdown.exe, which enables this privilege for itself.
        // Sleep is the one action with no console equivalent, so it has to do it here.
        if (!WindowsShutdownPrivilege.TryEnable(out var privilegeError))
        {
            throw new InvalidOperationException($"Cannot sleep this host: {privilegeError}");
        }

        var startedAt = Stopwatch.GetTimestamp();
        if (!SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetSuspendState failed.");
        }

        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        if (elapsed >= MinimumCredibleSleep)
        {
            _logger.LogInformation("Host resumed after sleeping for {Elapsed}.", elapsed);
            return;
        }

        // Reporting this is the whole point: an unverified sleep is indistinguishable from a
        // working one when the machine is headless and the screen is already off.
        var diagnostics = DescribeSleepStates();
        _logger.LogError(
            "SetSuspendState reported success but returned after {Elapsed}, so the host did not suspend. {Diagnostics}",
            elapsed,
            diagnostics);
        ReportSleepFailure(
            $"Host did not suspend: the sleep call returned after {elapsed.TotalMilliseconds:F0}ms "
            + "instead of blocking until resume.\n"
            + diagnostics);
    }

    /// <summary>
    /// Everything known about why this machine will or will not sleep.
    /// </summary>
    /// <remarks>
    /// The in-process parts come first and deliberately do not shell out. Stripped Windows builds
    /// are exactly the machines where sleep breaks *and* where powercfg.exe and the Settings power
    /// pages are missing or broken, so a diagnostic that depends on them reports nothing precisely
    /// when it is needed. Both facts below come from PowrProf directly.
    /// </remarks>
    private string DescribeSleepStates()
    {
        var parts = new List<string>();

        if (GetPwrCapabilities(out var capabilities))
        {
            parts.Add(
                "Sleep capabilities: "
                + $"S1={Supported(capabilities.SystemS1)} "
                + $"S2={Supported(capabilities.SystemS2)} "
                + $"S3={Supported(capabilities.SystemS3)} "
                + $"S4/hibernate={Supported(capabilities.SystemS4)} "
                + $"ModernStandby={Supported(capabilities.AoAc)} "
                + $"HiberFile={Supported(capabilities.HiberFilePresent)}");
        }
        else
        {
            parts.Add(
                "Sleep capabilities: unavailable "
                + $"({new Win32Exception(Marshal.GetLastWin32Error()).Message})");
        }

        // A missing or unreadable active scheme is worth naming on its own. It breaks the Settings
        // power pages the same way it breaks a programmatic sleep, so seeing both symptoms
        // together points at the scheme rather than at anything this app does.
        parts.Add(DescribeActivePowerScheme());

        // powercfg is a bonus, not the source of truth. /requests is listed first because when a
        // machine accepts the call and stays awake, an outstanding power request is the usual
        // reason - and running VMs are a common holder of one.
        parts.Add(RunPowercfg("/requests", "Outstanding power requests"));
        parts.Add(RunPowercfg("/a", "Sleep states per powercfg"));

        return string.Join("\n", parts);
    }

    private static string Supported(byte value)
    {
        return value != 0 ? "yes" : "no";
    }

    private static string DescribeActivePowerScheme()
    {
        var status = PowerGetActiveScheme(IntPtr.Zero, out var schemePointer);
        if (status != 0 || schemePointer == IntPtr.Zero)
        {
            return $"Active power scheme: could not be read (PowerGetActiveScheme returned {status}). "
                + "A missing or corrupt scheme breaks programmatic sleep and the Settings power pages alike.";
        }

        try
        {
            var schemeId = Marshal.PtrToStructure<Guid>(schemePointer);
            var name = ReadPowerSchemeName(schemePointer);
            return string.IsNullOrWhiteSpace(name)
                ? $"Active power scheme: {schemeId} (no friendly name)"
                : $"Active power scheme: {name} ({schemeId})";
        }
        finally
        {
            LocalFree(schemePointer);
        }
    }

    private static string? ReadPowerSchemeName(IntPtr schemePointer)
    {
        uint size = 0;
        PowerReadFriendlyName(IntPtr.Zero, schemePointer, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
        if (size == 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            return PowerReadFriendlyName(IntPtr.Zero, schemePointer, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0
                ? Marshal.PtrToStringUni(buffer)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private string RunPowercfg(string arguments, string label)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("powercfg.exe", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return $"{label}: could not run powercfg {arguments}.";
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(TimeSpan.FromSeconds(15)))
            {
                return $"{label}: powercfg {arguments} did not complete.";
            }

            output = output.Trim();
            if (string.IsNullOrWhiteSpace(output))
            {
                return $"{label}: powercfg {arguments} returned nothing.";
            }

            // Discord truncates a long message, and truncation here would drop the in-process
            // facts above rather than the least useful tail.
            const int maximumLength = 700;
            if (output.Length > maximumLength)
            {
                output = output[..maximumLength] + "\n(truncated)";
            }

            return $"{label}:\n{output}";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not collect powercfg diagnostics for {Arguments}.", arguments);
            return $"{label}: powercfg {arguments} is unavailable ({ex.Message}).";
        }
    }

    [DllImport("PowrProf.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.I1)]
        bool hibernate,
        [MarshalAs(UnmanagedType.I1)]
        bool forceCritical,
        [MarshalAs(UnmanagedType.I1)]
        bool disableWakeEvent);

    [DllImport("PowrProf.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetPwrCapabilities(out SystemPowerCapabilities capabilities);

    // SYSTEM_POWER_CAPABILITIES is a 76-byte, 4-byte-packed Win32 structure. The Windows SDK
    // uses mutually exclusive legacy fields in reserved bytes, so its flattened documentation
    // can make this look larger than its actual ABI. Only these four one-byte BOOLEAN fields are
    // needed here, but the native call writes the full structure.
    [StructLayout(LayoutKind.Explicit, Size = 76)]
    private struct SystemPowerCapabilities
    {
        [FieldOffset(3)]
        public byte SystemS1;

        [FieldOffset(4)]
        public byte SystemS2;

        [FieldOffset(5)]
        public byte SystemS3;

        [FieldOffset(6)]
        public byte SystemS4;

        [FieldOffset(8)]
        public byte HiberFilePresent;

        [FieldOffset(20)]
        public byte AoAc;
    }

    [DllImport("PowrProf.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("PowrProf.dll", CharSet = CharSet.Unicode)]
    private static extern uint PowerReadFriendlyName(
        IntPtr rootPowerKey,
        IntPtr schemeGuid,
        IntPtr subGroupOfPowerSettingsGuid,
        IntPtr powerSettingGuid,
        IntPtr buffer,
        ref uint bufferSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
