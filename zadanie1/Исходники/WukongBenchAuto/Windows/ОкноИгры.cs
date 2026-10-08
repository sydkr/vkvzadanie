using System.Runtime.InteropServices;
using static WukongBenchAuto.Native.NativeMethods;

namespace WukongBenchAuto.Native;

// Поиск окна игры и "нажатия" вместо пользователя.
internal static class GameWindow
{
    // берём самое большое видимое окно этих процессов - это и есть игра
    public static IntPtr Find(IReadOnlySet<int> processIds)
    {
        var best = IntPtr.Zero;
        var bestArea = 0L;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            if (!processIds.Contains((int)pid) || !IsWindowVisible(hWnd)) return true;
            if (!GetClientRect(hWnd, out var rect)) return true;

            var area = (long)rect.Width * rect.Height;
            if (area > bestArea)
            {
                best = hWnd;
                bestArea = area;
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    // Вытаскиваем игру на передний план. Не вышло - ничего не жмём, чтобы не накликать в чужое окно.
    public static bool Activate(IntPtr hWnd)
    {
        if (GetForegroundWindow() == hWnd) return true;

        // Винда не даёт фоновому процессу забрать фокус. Известный трюк: нажать Alt,
        // тогда следующий SetForegroundWindow срабатывает.
        SendKey(VkMenu, keyUp: false, useScanCode: false);
        SendKey(VkMenu, keyUp: true, useScanCode: false);
        if (IsIconic(hWnd)) ShowWindow(hWnd, SwRestore);
        SetForegroundWindow(hWnd);
        Thread.Sleep(300);
        return GetForegroundWindow() == hWnd;
    }

    public static bool IsForeground(IntPtr hWnd) => GetForegroundWindow() == hWnd;

    // Именно скан-кодом: UE читает клаву через Raw Input, обычные виртуальные коды игра не видит.
    public static void PressKey(ushort virtualKey)
    {
        SendKey(virtualKey, keyUp: false, useScanCode: true);
        Thread.Sleep(60);
        SendKey(virtualKey, keyUp: true, useScanCode: true);
    }

    // клик в точку (fx, fy), координаты в долях окна
    public static void ClickRelative(IntPtr hWnd, double fx, double fy)
    {
        if (!GetClientRect(hWnd, out var rect) || rect.Width == 0) return;

        var point = new Point { X = (int)(rect.Width * fx), Y = (int)(rect.Height * fy) };
        ClientToScreen(hWnd, ref point);
        SetCursorPos(point.X, point.Y);
        Thread.Sleep(80);
        SendMouse(MouseEventLeftDown);
        Thread.Sleep(60);
        SendMouse(MouseEventLeftUp);
    }

    private static void SendKey(ushort virtualKey, bool keyUp, bool useScanCode)
    {
        var flags = (keyUp ? KeyEventKeyUp : 0) | (useScanCode ? KeyEventScanCode : 0);
        var input = new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = useScanCode ? (ushort)0 : virtualKey,
                    ScanCode = (ushort)MapVirtualKey(virtualKey, 0),
                    Flags = flags,
                },
            },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
    }

    private static void SendMouse(uint flags)
    {
        var input = new Input
        {
            Type = InputMouse,
            Data = new InputUnion { Mouse = new MouseInput { Flags = flags } },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
    }
}
