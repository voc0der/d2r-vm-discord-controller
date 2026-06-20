using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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
    private const byte VkAlt = 0x12;
    private const byte VkControl = 0x11;
    private const byte VkLeftWindows = 0x5B;
    private const byte VkA = 0x41;
    private const byte VkF4 = 0x73;
    private const byte VkM = 0x4D;
    private const byte VkSpace = 0x20;
    private const byte VkV = 0x56;
    private const byte VkEscape = 0x1B;
    private const byte VkG = 0x47;
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint InputKeyboard = 1;
    private const uint KeyEventUnicode = 0x0004;
    private const int SwRestore = 9;

    public void ShowDesktop()
    {
        EnsureWindows();
        KeyDown(VkLeftWindows);
        try
        {
            Key(VkM);
        }
        finally
        {
            KeyUp(VkLeftWindows);
        }
    }

    public void FocusProcess(string processName)
    {
        if (!TryFocusProcess(processName))
        {
            var normalized = Path.GetFileNameWithoutExtension(processName);
            throw new InvalidOperationException($"Process is running, but no focusable window was found: {normalized}");
        }
    }

    public bool TryFocusProcess(string processName)
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
            return false;
        }

        ShowWindow(process.MainWindowHandle, SwRestore);
        SetForegroundWindow(process.MainWindowHandle);
        return true;
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

    public void PressAltF4()
    {
        KeyDown(VkAlt);
        try
        {
            Key(VkF4);
        }
        finally
        {
            KeyUp(VkAlt);
        }
    }

    public void PressStartKey()
    {
        Key(VkSpace);
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
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            PasteText(text);
        }
        catch (Exception pasteEx)
        {
            try
            {
                foreach (var character in text)
                {
                    SendUnicodeChar(character);
                }
            }
            catch (InvalidOperationException typeEx)
            {
                throw new InvalidOperationException(
                    $"Clipboard paste failed, and Unicode typing fallback also failed. PasteError={pasteEx.Message}; TypeError={typeEx.Message}",
                    typeEx);
            }
        }
    }

    public ScreenRegionStats SampleRegion(UiPoint center, double widthRatio, double heightRatio, int sampleGrid = 17)
    {
        EnsureWindows();
        var width = GetSystemMetrics(SmCxScreen);
        var height = GetSystemMetrics(SmCyScreen);
        var centerX = center.X * width;
        var centerY = center.Y * height;
        var regionWidth = Math.Max(width * widthRatio, sampleGrid);
        var regionHeight = Math.Max(height * heightRatio, sampleGrid);
        var grid = Math.Clamp(sampleGrid, 3, 51);
        var hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
        {
            throw new InvalidOperationException($"GetDC failed. LastError={Marshal.GetLastWin32Error()}");
        }

        var count = 0;
        var luminanceSum = 0.0;
        var luminanceSquaredSum = 0.0;
        var bright = 0;
        var grey = 0;
        var dark = 0;

        try
        {
            for (var yIndex = 0; yIndex < grid; yIndex++)
            {
                var y = Math.Clamp(
                    (int)Math.Round(centerY - (regionHeight / 2) + ((yIndex + 0.5) * regionHeight / grid)),
                    0,
                    height - 1);

                for (var xIndex = 0; xIndex < grid; xIndex++)
                {
                    var x = Math.Clamp(
                        (int)Math.Round(centerX - (regionWidth / 2) + ((xIndex + 0.5) * regionWidth / grid)),
                        0,
                        width - 1);
                    var color = GetPixel(hdc, x, y);
                    var red = (int)(color & 0x000000FF);
                    var green = (int)((color & 0x0000FF00) >> 8);
                    var blue = (int)((color & 0x00FF0000) >> 16);
                    var luminance = (0.2126 * red) + (0.7152 * green) + (0.0722 * blue);

                    count++;
                    luminanceSum += luminance;
                    luminanceSquaredSum += luminance * luminance;

                    if (luminance > 130)
                    {
                        bright++;
                    }

                    if (luminance < 35)
                    {
                        dark++;
                    }

                    if (Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue)) < 45
                        && luminance is > 35 and < 170)
                    {
                        grey++;
                    }
                }
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }

        var average = luminanceSum / count;
        var variance = Math.Max((luminanceSquaredSum / count) - (average * average), 0);
        return new ScreenRegionStats(
            average,
            Math.Sqrt(variance),
            (double)bright / count,
            (double)grey / count,
            (double)dark / count,
            count);
    }

    private void PasteText(string text)
    {
        EnsureWindows();
        SetClipboardText(text);
        KeyDown(VkControl);
        Key(VkV);
        KeyUp(VkControl);
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr windowHandle, IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr deviceContext, int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);

    private static void SetClipboardText(string text)
    {
        var opened = false;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            opened = OpenClipboard(IntPtr.Zero);
            if (opened)
            {
                break;
            }

            Thread.Sleep(50);
        }

        if (!opened)
        {
            throw new InvalidOperationException($"OpenClipboard failed. LastError={Marshal.GetLastWin32Error()}");
        }

        IntPtr memory = IntPtr.Zero;
        try
        {
            if (!EmptyClipboard())
            {
                throw new InvalidOperationException($"EmptyClipboard failed. LastError={Marshal.GetLastWin32Error()}");
            }

            var bytes = Encoding.Unicode.GetBytes(text + '\0');
            memory = GlobalAlloc(GmemMoveable, (UIntPtr)bytes.Length);
            if (memory == IntPtr.Zero)
            {
                throw new InvalidOperationException($"GlobalAlloc failed. LastError={Marshal.GetLastWin32Error()}");
            }

            var target = GlobalLock(memory);
            if (target == IntPtr.Zero)
            {
                throw new InvalidOperationException($"GlobalLock failed. LastError={Marshal.GetLastWin32Error()}");
            }

            try
            {
                Marshal.Copy(bytes, 0, target, bytes.Length);
            }
            finally
            {
                GlobalUnlock(memory);
            }

            if (SetClipboardData(CfUnicodeText, memory) == IntPtr.Zero)
            {
                throw new InvalidOperationException($"SetClipboardData failed. LastError={Marshal.GetLastWin32Error()}");
            }

            memory = IntPtr.Zero;
        }
        finally
        {
            CloseClipboard();
            if (memory != IntPtr.Zero)
            {
                GlobalFree(memory);
            }
        }
    }

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

internal sealed record ScreenRegionStats(
    double AverageLuminance,
    double LuminanceStdDev,
    double BrightRatio,
    double GreyRatio,
    double DarkRatio,
    int Samples);
