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
                    SleepFailed?.Invoke($"Host sleep failed: {ex.Message}");
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
        SleepFailed?.Invoke(
            $"Host did not suspend: the sleep call returned after {elapsed.TotalMilliseconds:F0}ms "
            + "instead of blocking until resume.\n"
            + diagnostics);
    }

    /// <summary>
    /// Asks Windows which sleep states are actually available. On the machines where this fails,
    /// the answer is almost always in here - a Hyper-V host has S3 disabled by "an internal system
    /// component", and a Modern Standby laptop offers S0 low-power idle and nothing else.
    /// </summary>
    private string DescribeSleepStates()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("powercfg.exe", "/a")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return "Could not run powercfg /a.";
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(TimeSpan.FromSeconds(15)))
            {
                return "powercfg /a did not complete.";
            }

            return string.IsNullOrWhiteSpace(output)
                ? "powercfg /a returned nothing."
                : $"Available sleep states per powercfg /a:\n{output.Trim()}";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not collect powercfg sleep-state diagnostics.");
            return $"Could not run powercfg /a: {ex.Message}";
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

        [FieldOffset(20)]
        public byte AoAc;
    }
}
