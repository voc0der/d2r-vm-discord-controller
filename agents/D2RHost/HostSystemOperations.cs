using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AgentCommon;

namespace D2RHost;

public sealed class HostSystemOperations
{
    private readonly ILogger<HostSystemOperations> _logger;

    public HostSystemOperations(ILogger<HostSystemOperations> logger)
    {
        _logger = logger;
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
            }
        });
    }

    private static void Execute(HostSystemPowerAction action)
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

    private static void SleepHost()
    {
        if (!SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetSuspendState failed.");
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
}
