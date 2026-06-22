using System.Runtime.InteropServices;
using AgentCommon;

namespace D2RAgent;

internal static class ConsoleWindowVisibility
{
    private const int SwHide = 0;
    private const int SwShow = 5;

    public static void ApplyConnectionState(AgentConnectionState state)
    {
        if (state == AgentConnectionState.Connected)
        {
            Hide();
        }
        else
        {
            Show();
        }
    }

    private static void Hide()
    {
        SetVisibility(SwHide);
    }

    private static void Show()
    {
        SetVisibility(SwShow);
    }

    private static void SetVisibility(int command)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = GetConsoleWindow();
        if (handle != IntPtr.Zero)
        {
            _ = ShowWindow(handle, command);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);
}
