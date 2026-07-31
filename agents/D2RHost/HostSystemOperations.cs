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
    private static readonly TimeSpan VmRestoreRetryDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ShutdownWatchdogDelay = TimeSpan.FromSeconds(30);

    private readonly ILogger<HostSystemOperations> _logger;
    private readonly VmPowerLifecycleCoordinator? _vmLifecycle;

    public HostSystemOperations(
        ILogger<HostSystemOperations> logger,
        VmPowerLifecycleCoordinator? vmLifecycle = null)
    {
        _logger = logger;
        _vmLifecycle = vmLifecycle;
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
    public bool TryPrepareSleep(out string? error) => TryPrepareSleep(out error, out _);

    /// <summary>
    /// As above, and reports which mechanism will be used so the command can say what it is really
    /// about to do rather than promising "sleep" and hibernating.
    /// </summary>
    public bool TryPrepareSleep(out string? error, out string? plan)
    {
        plan = null;
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

        var mechanism = ResolveSleepMechanism(
            capabilities.SystemS1 != 0,
            capabilities.SystemS2 != 0,
            capabilities.SystemS3 != 0,
            capabilities.SystemS4 != 0,
            capabilities.HiberFilePresent != 0);

        if (mechanism == SleepMechanism.None)
        {
            error = "this machine has no sleep state D2RHost can reach. Its firmware supports no "
                + "legacy standby (S1/S2/S3) and hibernation is turned off, and Modern Standby "
                + "cannot be entered by a background service.\n"
                + "Enable hibernation once, from an elevated prompt, and sleep will work: "
                + "`powercfg /hibernate on`\n"
                + DescribeSleepStates();
            return false;
        }

        // Hibernation goes through shutdown.exe, which enables the privilege for itself.
        if (mechanism == SleepMechanism.Hibernate)
        {
            plan = " This machine has no legacy standby state, so it will hibernate.";
            error = null;
            return true;
        }

        return WindowsShutdownPrivilege.TryEnable(out error);
    }

    internal enum SleepMechanism
    {
        None,
        LegacySuspend,
        Hibernate
    }

    /// <summary>
    /// Picks how this machine can actually be put to sleep.
    /// </summary>
    /// <remarks>
    /// Modern Standby is deliberately not treated as reachable. A Latitude 7430 reports
    /// "Standby (S0 Low Power Idle) Network Connected" as its only available state, with S1/S2/S3
    /// all unsupported by firmware - and <c>SetSuspendState</c> drives the legacy suspend path, so
    /// there it fails with ERROR_NOT_SUPPORTED (50) from a SYSTEM service and from an elevated
    /// interactive session alike. Treating AoAc as usable is what let such a machine pass preflight
    /// and then silently fail. Entering S0 idle means turning the display off in the interactive
    /// session, which a session-0 service cannot do, so hibernation is the reachable answer.
    /// </remarks>
    internal static SleepMechanism ResolveSleepMechanism(
        bool systemS1,
        bool systemS2,
        bool systemS3,
        bool systemS4,
        bool hiberFilePresent)
    {
        if (systemS1 || systemS2 || systemS3)
        {
            return SleepMechanism.LegacySuspend;
        }

        // S4 without a hiberfile means hibernation is supported but switched off, which is exactly
        // what "Hibernation has not been enabled" reports - and shutdown /h would fail.
        return systemS4 && hiberFilePresent ? SleepMechanism.Hibernate : SleepMechanism.None;
    }

    public void Queue(HostSystemPowerAction action)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            try
            {
                Execute(action);
                if (action == HostSystemPowerAction.Sleep && _vmLifecycle is not null)
                {
                    // SetSuspendState blocks until resume. A hibernating host process may instead
                    // be restarted by Windows, in which case the startup restore service consumes
                    // the same persisted list.
                    await RestorePendingVmsUntilCompleteAsync("host resume");
                }
                else if (_vmLifecycle is not null)
                {
                    // shutdown.exe /t 0 should terminate this process promptly. If Windows
                    // accepted the command but never transitions, do not strand the pre-stopped
                    // VMs forever merely because Process.Start itself succeeded.
                    await Task.Delay(ShutdownWatchdogDelay);
                    var pendingVmCount = _vmLifecycle.GetPendingVmNames().Count;
                    _logger.LogError(
                        "Host system action {Action} did not terminate D2RHost within {Delay}; releasing the transition arm and restoring {PendingVmCount} pending VM(s).",
                        action,
                        ShutdownWatchdogDelay,
                        pendingVmCount);

                    // Call even with an empty journal so the coordinator releases the in-memory
                    // transition arm used to reject overlapping host power commands.
                    await RestorePendingVmsUntilCompleteAsync($"failed {action} watchdog");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Host system action {Action} failed.", action);
                if (action == HostSystemPowerAction.Sleep)
                {
                    ReportSleepFailure($"Host sleep failed: {ex.Message}");
                }

                if (_vmLifecycle is not null)
                {
                    await RestorePendingVmsUntilCompleteAsync($"failed {action}");
                }
            }
        });
    }

    /// <summary>
    /// Confirms configured running VMs are off and durably recorded before the physical-host
    /// action is allowed onto the background queue.
    /// </summary>
    public async Task<VmPowerPreparationResult> PrepareVmsAndQueueAsync(
        HostSystemPowerAction action,
        CancellationToken cancellationToken = default)
    {
        VmPowerPreparationResult preparation;
        try
        {
            preparation = _vmLifecycle is null
                ? new VmPowerPreparationResult(
                    true,
                    [],
                    "VM lifecycle coordination is unavailable; no configured VM stop was attempted.")
                : await _vmLifecycle.PrepareForHostPowerActionAsync(cancellationToken);
        }
        catch
        {
            QueuePendingVmRollbackIfNeeded("aborted host power preparation");
            throw;
        }

        if (!preparation.Ok)
        {
            if (preparation.RetryRestorePending)
            {
                QueuePendingVmRollbackIfNeeded("failed host power preparation");
            }

            return preparation;
        }

        Queue(action);
        return preparation;
    }

    private void QueuePendingVmRollbackIfNeeded(string reason)
    {
        if (_vmLifecycle?.GetPendingVmNames().Count > 0)
        {
            _ = Task.Run(() => RestorePendingVmsUntilCompleteAsync(reason));
        }
    }

    private async Task RestorePendingVmsUntilCompleteAsync(string reason)
    {
        if (_vmLifecycle is null)
        {
            return;
        }

        // Always make one coordinator call: a host with no Running VMs still has an in-memory
        // transition arm that must be released after resume or a failed power action.
        do
        {
            try
            {
                var restore = await _vmLifecycle.RestorePendingAsync();
                if (restore.Complete)
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "VM restore after {Reason} failed; the persisted journal will be retried.",
                    reason);
            }

            await Task.Delay(VmRestoreRetryDelay);
        }
        while (_vmLifecycle.GetPendingVmNames().Count > 0);
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
        if (!TryPrepareSleep(out var prepareError))
        {
            throw new InvalidOperationException($"Cannot sleep this host: {prepareError}");
        }

        var startedAt = Stopwatch.GetTimestamp();
        var mechanism = GetPwrCapabilities(out var capabilities)
            ? ResolveSleepMechanism(
                capabilities.SystemS1 != 0,
                capabilities.SystemS2 != 0,
                capabilities.SystemS3 != 0,
                capabilities.SystemS4 != 0,
                capabilities.HiberFilePresent != 0)
            : SleepMechanism.LegacySuspend;

        if (mechanism == SleepMechanism.Hibernate)
        {
            // A machine whose only standby state is Modern Standby cannot be suspended by a
            // service, so sleep means hibernate there. The operator-visible result is the same:
            // the machine powers down and resumes where it left off.
            _logger.LogInformation("Host has no legacy standby state; hibernating instead.");
            using var hibernate = Process.Start(
                HostSystemPowerActions.CreateShutdownStartInfo(
                    HostSystemPowerAction.Sleep, hibernateForSleep: true))
                ?? throw new InvalidOperationException("Could not start shutdown.exe to hibernate.");
            hibernate.WaitForExit();
            if (hibernate.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"shutdown /h failed with exit code {hibernate.ExitCode}.");
            }
        }
        else if (!SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false))
        {
            var lastError = Marshal.GetLastWin32Error();
            // 50 is ERROR_NOT_SUPPORTED, which is what a Modern-Standby-only machine returns.
            // Say so plainly rather than leaving a bare Win32 code in the log.
            var detail = lastError == 50
                ? "SetSuspendState failed: this machine has no legacy standby state to enter."
                : "SetSuspendState failed.";
            throw new Win32Exception(lastError, $"{detail}\n{DescribeSleepStates()}");
        }

        // Both paths freeze this process until the machine resumes, so a short elapsed time means
        // the transition never happened.
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
