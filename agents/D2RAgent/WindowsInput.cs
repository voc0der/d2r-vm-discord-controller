using System.Diagnostics;
using System.Runtime.InteropServices;
using AgentCommon;

namespace D2RAgent;

internal enum MouseButton
{
    Left,
    Right
}

internal sealed class WindowsInput
{
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const byte VkControl = 0x11;
    private const byte VkA = 0x41;
    private const byte VkEscape = 0x1B;
    private const byte VkG = 0x47;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint InputKeyboard = 1;
    private const uint KeyEventUnicode = 0x0004;
    private const int SwRestore = 9;

    public void FocusProcess(string processName)
    {
        EnsureWindows();

        var normalized = Path.GetFileNameWithoutExtension(processName);
        var process = Process.GetProcessesByName(normalized)
            .OrderByDescending(candidate => candidate.MainWindowHandle != IntPtr.Zero)
            .FirstOrDefault();

        if (process is null)
        {
            throw new InvalidOperationException($"Process is not running: {normalized}");
        }

        if (process.MainWindowHandle == IntPtr.Zero)
        {
            return;
        }

        ShowWindow(process.MainWindowHandle, SwRestore);
        SetForegroundWindow(process.MainWindowHandle);
    }

    public void LeftClick(UiPoint point)
    {
        Click(point, MouseButton.Left);
    }

    public void RightClick(UiPoint point)
    {
        Click(point, MouseButton.Right);
    }

    public void Click(UiPoint point, MouseButton button)
    {
        EnsureWindows();

        var (x, y) = ToScreen(point);
        SetCursorPos(x, y);
        if (button == MouseButton.Left)
        {
            mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
            return;
        }

        mouse_event(MouseEventRightDown, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MouseEventRightUp, 0, 0, 0, UIntPtr.Zero);
    }

    public void PressEscape()
    {
        Key(VkEscape);
    }

    public void PressLegacyGraphicsToggle()
    {
        Key(VkG);
    }

    public void SelectAll()
    {
        KeyDown(VkControl);
        Key(VkA);
        KeyUp(VkControl);
    }

    public void TypeText(string text)
    {
        EnsureWindows();

        foreach (var character in text)
        {
            SendUnicodeChar(character);
        }
    }

    private static (int X, int Y) ToScreen(UiPoint point)
    {
        var width = GetSystemMetrics(SmCxScreen);
        var height = GetSystemMetrics(SmCyScreen);
        var x = Math.Clamp((int)Math.Round(point.X * width), 0, width - 1);
        var y = Math.Clamp((int)Math.Round(point.Y * height), 0, height - 1);
        return (x, y);
    }

    private static void Key(byte virtualKey)
    {
        KeyDown(virtualKey);
        KeyUp(virtualKey);
    }

    private static void KeyDown(byte virtualKey)
    {
        EnsureWindows();
        keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
    }

    private static void KeyUp(byte virtualKey)
    {
        EnsureWindows();
        keybd_event(virtualKey, 0, KeyEventKeyUp, UIntPtr.Zero);
    }

    private static void SendUnicodeChar(char character)
    {
        var inputs = new[]
        {
            Input.ForUnicode(character, keyUp: false),
            Input.ForUnicode(character, keyUp: true)
        };

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            throw new InvalidOperationException($"SendInput failed while typing text. LastError={Marshal.GetLastWin32Error()}");
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("D2R UI automation only runs on Windows.");
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;

        public static Input ForUnicode(char character, bool keyUp)
        {
            return new Input
            {
                Type = InputKeyboard,
                Union = new InputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        Scan = character,
                        Flags = KeyEventUnicode | (keyUp ? KeyEventKeyUp : 0)
                    }
                }
            };
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public char Scan;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }
}
