using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace D2RHost;

/// <summary>
/// Enables <c>SE_SHUTDOWN_NAME</c> in the current process token.
/// </summary>
/// <remarks>
/// <c>SetSuspendState</c> requires this privilege, and Windows does not enable it for you. Running
/// elevated is not sufficient: an administrator's token *contains* SeShutdownPrivilege but holds it
/// in the disabled state, and the API then fails with ERROR_PRIVILEGE_NOT_HELD (1314).
///
/// This is why shutdown and restart worked while sleep did nothing at all. Those two shell out to
/// shutdown.exe, which enables the privilege for itself; sleep is the one action with no console
/// equivalent, so it went through a direct P/Invoke that never enabled anything. The failure then
/// landed in a background task whose only outlet was a log file, so Discord still reported the
/// action as queued and the machine simply stayed awake.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class WindowsShutdownPrivilege
{
    private const int SePrivilegeEnabled = 0x0002;
    private const int TokenAdjustPrivileges = 0x0020;
    private const int TokenQuery = 0x0008;
    private const int ErrorNotAllAssigned = 1300;

    /// <summary>
    /// Attempts to enable the privilege, returning a human-readable reason on failure.
    /// </summary>
    /// <remarks>
    /// Deliberately returns a message rather than throwing: the caller reports this synchronously
    /// in the command result, so the operator sees "sleep is not permitted for this account"
    /// instead of a success message followed by a machine that never sleeps.
    /// </remarks>
    public static bool TryEnable(out string? error)
    {
        var processHandle = GetCurrentProcess();
        if (!OpenProcessToken(processHandle, TokenAdjustPrivileges | TokenQuery, out var tokenHandle))
        {
            error = Describe("OpenProcessToken", Marshal.GetLastWin32Error());
            return false;
        }

        try
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid))
            {
                error = Describe("LookupPrivilegeValue", Marshal.GetLastWin32Error());
                return false;
            }

            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled
            };

            // AdjustTokenPrivileges reports success even when it changed nothing, so the last
            // error has to be checked separately - that is the case where the account genuinely
            // lacks the privilege, and the one worth naming in the response.
            if (!AdjustTokenPrivileges(tokenHandle, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
            {
                error = Describe("AdjustTokenPrivileges", Marshal.GetLastWin32Error());
                return false;
            }

            var lastError = Marshal.GetLastWin32Error();
            if (lastError == ErrorNotAllAssigned)
            {
                error = "this account does not hold SeShutdownPrivilege, so the host cannot sleep itself. "
                    + "Run D2RHost as an account with the \"Shut down the system\" user right.";
                return false;
            }

            error = null;
            return true;
        }
        finally
        {
            CloseHandle(tokenHandle);
        }
    }

    private static string Describe(string api, int errorCode)
    {
        return $"{api} failed: {new Win32Exception(errorCode).Message} ({errorCode})";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public int PrivilegeCount;
        public Luid Luid;
        public int Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, int desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        int bufferLength,
        IntPtr previousState,
        IntPtr returnLength);
}
