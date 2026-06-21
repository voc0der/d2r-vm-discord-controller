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
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventAbsolute = 0x8000;
    private const uint MouseKeyLeft = 0x0001;
    private const uint MouseKeyRight = 0x0002;
    private const byte VkAlt = 0x12;
    private const byte VkControl = 0x11;
    private const byte VkLeftWindows = 0x5B;
    private const byte VkA = 0x41;
    private const byte VkF4 = 0x73;
    private const byte VkM = 0x4D;
    private const byte VkSpace = 0x20;
    private const byte VkReturn = 0x0D;
    private const byte VkV = 0x56;
    private const byte VkEscape = 0x1B;
    private const byte VkG = 0x47;
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint InputKeyboard = 1;
    private const uint InputMouse = 0;
    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventUnicode = 0x0004;
    private const uint KeyEventScanCode = 0x0008;
    private const uint MapVkToVsc = 0;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmChar = 0x0102;
    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonDown = 0x0204;
    private const uint WmRButtonUp = 0x0205;
    private const int InputHoldMilliseconds = 90;
    private const int InputGapMilliseconds = 35;
    private const int SwRestore = 9;
    private readonly string[] _coordinateProcessNames;

    public WindowsInput(params string[] coordinateProcessNames)
    {
        _coordinateProcessNames = WindowsProcessIdentity.NormalizeProcessNames(coordinateProcessNames);
    }

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
        FocusProcess([processName]);
    }

    public void FocusProcess(IEnumerable<string> processNames)
    {
        if (!TryFocusProcess(processNames))
        {
            throw new InvalidOperationException($"Process is running, but no focusable window was found: {FormatProcessNames(processNames)}");
        }
    }

    public bool TryFocusProcess(string processName)
    {
        return TryFocusProcess([processName]);
    }

    public bool TryFocusProcess(IEnumerable<string> processNames)
    {
        EnsureWindows();

        var process = FindProcess(processNames);

        if (process is null)
        {
            throw new InvalidOperationException($"Process is not running: {FormatProcessNames(processNames)}");
        }

        if (process.MainWindowHandle == IntPtr.Zero)
        {
            return false;
        }

        return TrySetForegroundProcess(process);
    }

    public bool TryClickProcessWindowCenter(string processName)
    {
        return TryClickProcessWindowCenter([processName]);
    }

    public bool TryClickProcessWindowCenter(IEnumerable<string> processNames)
    {
        EnsureWindows();

        var process = FindProcess(processNames);
        if (process is null)
        {
            throw new InvalidOperationException($"Process is not running: {FormatProcessNames(processNames)}");
        }

        if (process.MainWindowHandle == IntPtr.Zero)
        {
            return false;
        }

        ShowWindow(process.MainWindowHandle, SwRestore);
        if (!GetWindowRect(process.MainWindowHandle, out var rect))
        {
            return false;
        }

        var x = (rect.Left + rect.Right) / 2;
        var y = (rect.Top + rect.Bottom) / 2;
        if (!SendMouseClick(x, y, MouseButton.Left))
        {
            SendLegacyMouseClick(x, y, MouseButton.Left);
        }

        Thread.Sleep(50);
        return true;
    }

    public void LeftClick(UiPoint point)
    {
        Click(point, MouseButton.Left, coordinateProcessNames: null);
    }

    public void LeftClick(UiPoint point, IEnumerable<string> coordinateProcessNames)
    {
        Click(point, MouseButton.Left, coordinateProcessNames);
    }

    public void RightClick(UiPoint point)
    {
        Click(point, MouseButton.Right, coordinateProcessNames: null);
    }

    public void RightClick(UiPoint point, IEnumerable<string> coordinateProcessNames)
    {
        Click(point, MouseButton.Right, coordinateProcessNames);
    }

    public bool SendWindowClick(UiPoint point, IEnumerable<string> processNames, MouseButton button)
    {
        EnsureWindows();

        var process = FindProcess(processNames);
        if (process is null || process.MainWindowHandle == IntPtr.Zero)
        {
            return false;
        }

        ShowWindow(process.MainWindowHandle, SwRestore);
        var (x, y) = ToScreen(point, processNames);
        SendWindowMouseClick(process.MainWindowHandle, x, y, button);
        return true;
    }

    public void Click(UiPoint point, MouseButton button)
    {
        Click(point, button, coordinateProcessNames: null);
    }

    public void Click(UiPoint point, MouseButton button, IEnumerable<string>? coordinateProcessNames)
    {
        EnsureWindows();

        var (x, y) = ToScreen(point, coordinateProcessNames);
        if (SendMouseClick(x, y, button))
        {
            return;
        }

        if (button == MouseButton.Left)
        {
            SendLegacyMouseClick(x, y, button);
            return;
        }

        SendLegacyMouseClick(x, y, button);
    }

    public bool SendWindowReadyBurst(string processName, UiPoint point, bool includeEscape)
    {
        return SendWindowReadyBurst([processName], point, includeEscape);
    }

    public bool SendWindowReadyBurst(IEnumerable<string> processNames, UiPoint point, bool includeEscape)
    {
        EnsureWindows();

        var process = FindProcess(processNames);
        if (process is null || process.MainWindowHandle == IntPtr.Zero)
        {
            return false;
        }

        var windowHandle = process.MainWindowHandle;
        ShowWindow(windowHandle, SwRestore);
        _ = TrySetForegroundProcess(process);
        var (x, y) = ToScreen(point, processNames);

        SendWindowMouseClick(windowHandle, x, y, MouseButton.Left);
        if (includeEscape)
        {
            SendWindowKey(windowHandle, VkEscape);
        }

        SendWindowKey(windowHandle, VkSpace);
        SendWindowKey(windowHandle, VkReturn);
        SendWindowMouseClick(windowHandle, x, y, MouseButton.Left);
        return true;
    }

    public bool SendWindowReadySkipKey(IEnumerable<string> processNames)
    {
        EnsureWindows();

        var process = FindProcess(processNames);
        if (process is null || process.MainWindowHandle == IntPtr.Zero)
        {
            return false;
        }

        ShowWindow(process.MainWindowHandle, SwRestore);
        SendWindowKey(process.MainWindowHandle, VkG);
        return true;
    }

    public bool SendWindowLegacyGraphicsToggle(IEnumerable<string> processNames)
    {
        return SendWindowReadySkipKey(processNames);
    }

    public InputDiagnostics GetInputDiagnostics(IEnumerable<string> processNames)
    {
        EnsureWindows();

        var names = WindowsProcessIdentity.NormalizeProcessNames(processNames);
        var screenWidth = GetSystemMetrics(SmCxScreen);
        var screenHeight = GetSystemMetrics(SmCyScreen);
        var foregroundWindow = GetForegroundWindow();
        int? foregroundProcessId = null;
        string? foregroundProcessName = null;
        if (foregroundWindow != IntPtr.Zero)
        {
            _ = GetWindowThreadProcessId(foregroundWindow, out var foregroundPid);
            if (foregroundPid != 0)
            {
                foregroundProcessId = (int)foregroundPid;
                try
                {
                    foregroundProcessName = Process.GetProcessById((int)foregroundPid).ProcessName;
                }
                catch (ArgumentException)
                {
                    foregroundProcessName = null;
                }
            }
        }

        var process = FindProcess(names);
        if (process is null)
        {
            return new InputDiagnostics(
                ProcessFound: false,
                ProcessId: null,
                ProcessName: null,
                SessionId: null,
                UserInteractive: Environment.UserInteractive,
                HasMainWindow: false,
                MainWindowTitle: null,
                IsForeground: false,
                ForegroundProcessId: foregroundProcessId,
                ForegroundProcessName: foregroundProcessName,
                ScreenWidth: screenWidth,
                ScreenHeight: screenHeight,
                WindowRect: null,
                ClientRect: null);
        }

        InputRect? windowRect = null;
        InputRect? clientRect = null;
        if (process.MainWindowHandle != IntPtr.Zero)
        {
            if (GetWindowRect(process.MainWindowHandle, out var window))
            {
                windowRect = new InputRect(window.Left, window.Top, window.Right, window.Bottom);
            }

            if (TryGetProcessClientBounds(names, out var client))
            {
                clientRect = new InputRect(client.Left, client.Top, client.Left + client.Width, client.Top + client.Height);
            }
        }

        return new InputDiagnostics(
            ProcessFound: true,
            ProcessId: process.Id,
            ProcessName: process.ProcessName,
            SessionId: SafeGetSessionId(process),
            UserInteractive: Environment.UserInteractive,
            HasMainWindow: process.MainWindowHandle != IntPtr.Zero,
            MainWindowTitle: process.MainWindowTitle,
            IsForeground: IsForegroundProcess(process.Id),
            ForegroundProcessId: foregroundProcessId,
            ForegroundProcessName: foregroundProcessName,
            ScreenWidth: screenWidth,
            ScreenHeight: screenHeight,
            WindowRect: windowRect,
            ClientRect: clientRect);
    }

    public CursorPosition? GetCursorPosition()
    {
        EnsureWindows();
        if (!GetCursorPos(out var point))
        {
            return null;
        }

        return new CursorPosition(point.X, point.Y);
    }

    public (int X, int Y) ResolveScreenPoint(
        UiPoint point,
        IEnumerable<string>? coordinateProcessNames = null)
    {
        EnsureWindows();
        return ToScreen(point, coordinateProcessNames);
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
        Thread.Sleep(50);
        Key(VkReturn);
    }

    public void PressLegacyGraphicsToggle()
    {
        PressReadySkipKey();
    }

    public void PressReadySkipKey()
    {
        Key(VkG);
    }

    public void PressStartupSkipKey()
    {
        ScanKey(VkG);
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
        return SampleRegion(center, widthRatio, heightRatio, sampleGrid, coordinateProcessNames: null);
    }

    public ScreenRegionStats SampleRegion(
        UiPoint center,
        double widthRatio,
        double heightRatio,
        IEnumerable<string> coordinateProcessNames,
        int sampleGrid = 17)
    {
        return SampleRegion(center, widthRatio, heightRatio, sampleGrid, coordinateProcessNames);
    }

    private ScreenRegionStats SampleRegion(
        UiPoint center,
        double widthRatio,
        double heightRatio,
        int sampleGrid,
        IEnumerable<string>? coordinateProcessNames)
    {
        EnsureWindows();
        var screenWidth = GetSystemMetrics(SmCxScreen);
        var screenHeight = GetSystemMetrics(SmCyScreen);
        var bounds = GetCoordinateBounds(coordinateProcessNames);
        var centerX = bounds.Left + (center.X * bounds.Width);
        var centerY = bounds.Top + (center.Y * bounds.Height);
        var regionWidth = Math.Max(bounds.Width * widthRatio, sampleGrid);
        var regionHeight = Math.Max(bounds.Height * heightRatio, sampleGrid);
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
        var orange = 0;
        var redPixels = 0;
        var bluePixels = 0;

        try
        {
            for (var yIndex = 0; yIndex < grid; yIndex++)
            {
                var y = Math.Clamp(
                    (int)Math.Round(centerY - (regionHeight / 2) + ((yIndex + 0.5) * regionHeight / grid)),
                    0,
                    screenHeight - 1);

                for (var xIndex = 0; xIndex < grid; xIndex++)
                {
                    var x = Math.Clamp(
                        (int)Math.Round(centerX - (regionWidth / 2) + ((xIndex + 0.5) * regionWidth / grid)),
                        0,
                        screenWidth - 1);
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

                    if (red > 110
                        && green > 45
                        && blue < 45
                        && red > green * 1.25)
                    {
                        orange++;
                    }

                    if (red > 95
                        && green < 75
                        && blue < 75
                        && red > green * 1.40
                        && red > blue * 1.40)
                    {
                        redPixels++;
                    }

                    if (blue > 80
                        && blue > green * 1.05
                        && blue > red * 1.35)
                    {
                        bluePixels++;
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
            (double)orange / count,
            (double)redPixels / count,
            (double)bluePixels / count,
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

    private (int X, int Y) ToScreen(UiPoint point, IEnumerable<string>? coordinateProcessNames)
    {
        var screenWidth = GetSystemMetrics(SmCxScreen);
        var screenHeight = GetSystemMetrics(SmCyScreen);
        var bounds = GetCoordinateBounds(coordinateProcessNames);
        var x = Math.Clamp((int)Math.Round(bounds.Left + (point.X * bounds.Width)), 0, screenWidth - 1);
        var y = Math.Clamp((int)Math.Round(bounds.Top + (point.Y * bounds.Height)), 0, screenHeight - 1);
        return (x, y);
    }

    private static Process? FindProcess(string processName)
    {
        return FindProcess([processName]);
    }

    private static Process? FindProcess(IEnumerable<string> processNames)
    {
        var names = WindowsProcessIdentity.NormalizeProcessNames(processNames);
        var process = names
            .SelectMany(Process.GetProcessesByName)
            .OrderByDescending(candidate => candidate.MainWindowHandle != IntPtr.Zero)
            .FirstOrDefault();
        if (process is not null)
        {
            return process;
        }

        var titleNeedles = WindowsProcessIdentity.GetWindowTitleNeedles(names);
        return titleNeedles.Length == 0
            ? null
            : Process.GetProcesses()
                .Where(candidate => candidate.MainWindowHandle != IntPtr.Zero)
                .Select(candidate => new { Process = candidate, Title = SafeGetMainWindowTitle(candidate) })
                .Where(candidate => titleNeedles.Any(needle => candidate.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                .Select(candidate => candidate.Process)
                .FirstOrDefault();
    }

    private CoordinateBounds GetCoordinateBounds(IEnumerable<string>? coordinateProcessNames)
    {
        var names = WindowsProcessIdentity.NormalizeProcessNames(coordinateProcessNames);
        if (names.Length == 0)
        {
            names = _coordinateProcessNames;
        }

        return names.Length > 0 && TryGetProcessClientBounds(names, out var bounds)
            ? bounds
            : new CoordinateBounds(0, 0, GetSystemMetrics(SmCxScreen), GetSystemMetrics(SmCyScreen));
    }

    private static bool TryGetProcessClientBounds(IEnumerable<string> processNames, out CoordinateBounds bounds)
    {
        bounds = default;
        var process = FindProcess(processNames);
        if (process is null || process.MainWindowHandle == IntPtr.Zero)
        {
            return false;
        }

        var windowHandle = process.MainWindowHandle;
        ShowWindow(windowHandle, SwRestore);
        if (!GetClientRect(windowHandle, out var clientRect))
        {
            return false;
        }

        var width = clientRect.Right - clientRect.Left;
        var height = clientRect.Bottom - clientRect.Top;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var origin = new WindowPoint { X = clientRect.Left, Y = clientRect.Top };
        if (!ClientToScreen(windowHandle, ref origin))
        {
            return false;
        }

        bounds = new CoordinateBounds(origin.X, origin.Y, width, height);
        return true;
    }

    private static string SafeGetMainWindowTitle(Process process)
    {
        try
        {
            return process.MainWindowTitle ?? "";
        }
        catch (InvalidOperationException)
        {
            return "";
        }
    }

    private static string FormatProcessNames(IEnumerable<string> processNames)
    {
        var names = WindowsProcessIdentity.NormalizeProcessNames(processNames);
        return names.Length == 0 ? "(none)" : string.Join("/", names);
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
    }

    private static bool TrySetForegroundProcess(Process process)
    {
        ShowWindow(process.MainWindowHandle, SwRestore);
        if (IsForegroundProcess(process.Id))
        {
            return true;
        }

        if (SetForegroundWindow(process.MainWindowHandle))
        {
            Thread.Sleep(50);
            if (IsForegroundProcess(process.Id))
            {
                return true;
            }
        }

        KeyDown(VkAlt);
        try
        {
            _ = SetForegroundWindow(process.MainWindowHandle);
        }
        finally
        {
            KeyUp(VkAlt);
        }

        Thread.Sleep(50);
        if (IsForegroundProcess(process.Id))
        {
            return true;
        }

        var foregroundWindow = GetForegroundWindow();
        var foregroundThread = foregroundWindow == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foregroundWindow, out _);
        var targetThread = GetWindowThreadProcessId(process.MainWindowHandle, out _);
        var currentThread = GetCurrentThreadId();
        var attachedForeground = false;
        var attachedTarget = false;

        try
        {
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                attachedForeground = AttachThreadInput(currentThread, foregroundThread, attach: true);
            }

            if (targetThread != 0 && targetThread != currentThread)
            {
                attachedTarget = AttachThreadInput(currentThread, targetThread, attach: true);
            }

            _ = BringWindowToTop(process.MainWindowHandle);
            _ = SetForegroundWindow(process.MainWindowHandle);
            _ = SetActiveWindow(process.MainWindowHandle);
            _ = SetFocus(process.MainWindowHandle);
        }
        finally
        {
            if (attachedTarget)
            {
                _ = AttachThreadInput(currentThread, targetThread, attach: false);
            }

            if (attachedForeground)
            {
                _ = AttachThreadInput(currentThread, foregroundThread, attach: false);
            }
        }

        Thread.Sleep(50);
        return IsForegroundProcess(process.Id);
    }

    private static bool IsForegroundProcess(int processId)
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(foregroundWindow, out var foregroundProcessId);
        return foregroundProcessId == (uint)processId;
    }

    private static void Key(byte virtualKey)
    {
        KeyDown(virtualKey);
        Thread.Sleep(InputHoldMilliseconds);
        KeyUp(virtualKey);
        Thread.Sleep(InputGapMilliseconds);
    }

    private static void ScanKey(byte virtualKey)
    {
        var scanCode = (ushort)MapVirtualKey(virtualKey, MapVkToVsc);
        if (scanCode == 0)
        {
            Key(virtualKey);
            return;
        }

        var extendedKey = IsExtendedVirtualKey(virtualKey);
        var downSent = SendInputs(new[] { Input.ForScanCode(scanCode, keyUp: false, extendedKey) });
        Thread.Sleep(InputHoldMilliseconds);
        var upSent = SendInputs(new[] { Input.ForScanCode(scanCode, keyUp: true, extendedKey) });
        Thread.Sleep(InputGapMilliseconds);

        if (downSent != 1 || upSent != 1)
        {
            Key(virtualKey);
        }
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

        var sent = SendInputs(inputs);
        if (sent != inputs.Length)
        {
            throw new InvalidOperationException($"SendInput failed while typing text. LastError={Marshal.GetLastWin32Error()}");
        }
    }

    private static uint SendInputs(Input[] inputs)
    {
        EnsureWindows();
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    private static bool SendMouseClick(int x, int y, MouseButton button)
    {
        var down = button == MouseButton.Left ? MouseEventLeftDown : MouseEventRightDown;
        var up = button == MouseButton.Left ? MouseEventLeftUp : MouseEventRightUp;
        var cursorMoved = SetCursorPos(x, y);
        Thread.Sleep(InputGapMilliseconds);
        mouse_event(down, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(InputHoldMilliseconds);
        mouse_event(up, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(InputGapMilliseconds);
        return cursorMoved;
    }

    private static void SendLegacyMouseClick(int x, int y, MouseButton button)
    {
        var down = button == MouseButton.Left ? MouseEventLeftDown : MouseEventRightDown;
        var up = button == MouseButton.Left ? MouseEventLeftUp : MouseEventRightUp;
        SetCursorPos(x, y);
        mouse_event(down, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(InputHoldMilliseconds);
        mouse_event(up, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(InputGapMilliseconds);
    }

    private static void SendWindowKey(IntPtr windowHandle, byte virtualKey)
    {
        _ = PostMessage(windowHandle, WmKeyDown, (IntPtr)virtualKey, KeyLParam(virtualKey, keyUp: false));

        var character = CharacterForVirtualKey(virtualKey);
        if (character.HasValue)
        {
            _ = PostMessage(windowHandle, WmChar, (IntPtr)character.Value, KeyLParam(virtualKey, keyUp: false));
        }

        Thread.Sleep(InputHoldMilliseconds);
        _ = PostMessage(windowHandle, WmKeyUp, (IntPtr)virtualKey, KeyLParam(virtualKey, keyUp: true));
        Thread.Sleep(InputGapMilliseconds);
    }

    private static void SendWindowMouseClick(IntPtr windowHandle, int screenX, int screenY, MouseButton button)
    {
        var point = new WindowPoint { X = screenX, Y = screenY };
        if (!ScreenToClient(windowHandle, ref point))
        {
            return;
        }

        var downMessage = button == MouseButton.Left ? WmLButtonDown : WmRButtonDown;
        var upMessage = button == MouseButton.Left ? WmLButtonUp : WmRButtonUp;
        var mouseKey = button == MouseButton.Left ? MouseKeyLeft : MouseKeyRight;
        var lParam = MakeLParam(point.X, point.Y);

        _ = PostMessage(windowHandle, WmMouseMove, IntPtr.Zero, lParam);
        _ = PostMessage(windowHandle, downMessage, (IntPtr)mouseKey, lParam);
        Thread.Sleep(InputHoldMilliseconds);
        _ = PostMessage(windowHandle, upMessage, IntPtr.Zero, lParam);
        Thread.Sleep(InputGapMilliseconds);
    }

    private static IntPtr KeyLParam(byte virtualKey, bool keyUp)
    {
        var scanCode = (int)MapVirtualKey(virtualKey, MapVkToVsc);
        var lParam = 1 | (scanCode << 16);
        if (keyUp)
        {
            lParam |= unchecked((int)0xC0000000);
        }

        return (IntPtr)lParam;
    }

    private static IntPtr MakeLParam(int low, int high)
    {
        return (IntPtr)(((high & 0xFFFF) << 16) | (low & 0xFFFF));
    }

    private static char? CharacterForVirtualKey(byte virtualKey)
    {
        return virtualKey switch
        {
            VkEscape => (char)0x1B,
            VkReturn => '\r',
            VkSpace => ' ',
            VkG => 'g',
            _ => null
        };
    }

    private static bool IsExtendedVirtualKey(byte virtualKey)
    {
        return virtualKey is VkLeftWindows;
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
    private static extern bool GetCursorPos(out WindowPoint point);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr windowHandle, out WindowRect rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr windowHandle, out WindowRect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr windowHandle, ref WindowPoint point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr windowHandle, ref WindowPoint point);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

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

        public static Input ForVirtualKey(byte virtualKey, bool keyUp)
        {
            return new Input
            {
                Type = InputKeyboard,
                Union = new InputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        VirtualKey = virtualKey,
                        Flags = keyUp ? KeyEventKeyUp : 0
                    }
                }
            };
        }

        public static Input ForScanCode(ushort scanCode, bool keyUp, bool extendedKey = false)
        {
            return new Input
            {
                Type = InputKeyboard,
                Union = new InputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        Scan = (char)scanCode,
                        Flags = KeyEventScanCode
                            | (extendedKey ? KeyEventExtendedKey : 0)
                            | (keyUp ? KeyEventKeyUp : 0)
                    }
                }
            };
        }

        public static Input ForMouse(int x, int y, uint flags)
        {
            return new Input
            {
                Type = InputMouse,
                Union = new InputUnion
                {
                    Mouse = new MouseInput
                    {
                        X = x,
                        Y = y,
                        Flags = flags
                    }
                }
            };
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;

        [FieldOffset(0)]
        public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParamL;
        public ushort ParamH;
    }

    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private struct WindowPoint
    {
        public int X;
        public int Y;
    }

    private readonly record struct CoordinateBounds(int Left, int Top, int Width, int Height);
}

internal sealed record ScreenRegionStats(
    double AverageLuminance,
    double LuminanceStdDev,
    double BrightRatio,
    double GreyRatio,
    double DarkRatio,
    double OrangeRatio,
    double RedRatio,
    double BlueRatio,
    int Samples);

internal sealed record InputRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

internal sealed record InputDiagnostics(
    bool ProcessFound,
    int? ProcessId,
    string? ProcessName,
    int? SessionId,
    bool UserInteractive,
    bool HasMainWindow,
    string? MainWindowTitle,
    bool IsForeground,
    int? ForegroundProcessId,
    string? ForegroundProcessName,
    int ScreenWidth,
    int ScreenHeight,
    InputRect? WindowRect,
    InputRect? ClientRect);

internal sealed record CursorPosition(int X, int Y);
