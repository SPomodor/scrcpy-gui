using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AndroidDesk;

internal static class Native
{
    private delegate bool EnumCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetParent(nint child, nint parent);
    [DllImport("user32.dll")] internal static extern nint GetParent(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetStyle(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetStyle(nint window, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] internal static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    internal static void Own(nint window, nint owner) => SetStyle(window, -8, owner);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref uint value, uint size);
    internal static void MakeTransient(nint window)
    {
        SetStyle(window, -16, (nint)((long)GetStyle(window, -16) & ~0x00C40000L));
        SetStyle(window, -20, (nint)(((long)GetStyle(window, -20) | 0x08000080L) & ~0x00060300L));
        uint policy = 1; // DWMNCRP_DISABLED: no system frame around the notification.
        DwmSetWindowAttribute(window, 2, ref policy, 4);
        uint border = 0x002A2523; // Match the notification surface, including compositor fallback.
        DwmSetWindowAttribute(window, 34, ref border, 4);
        uint corner = 1; // Our region owns the corners; avoid a second DWM outline.
        DwmSetWindowAttribute(window, 33, ref corner, 4);
        GetWindowRect(window, out var bounds);
        var radius = Math.Max(8, (int)Math.Round(12 * DpiScale(window)));
        var region = CreateRoundRectRgn(3, 3, bounds.Right - bounds.Left - 3, bounds.Bottom - bounds.Top - 3, radius, radius);
        if (region != 0 && SetWindowRgn(window, region, true) == 0) DeleteObject(region);
        SetWindowPos(window, 0, 0, 0, 0, 0, 0x0010 | 0x0001 | 0x0002 | 0x0004 | 0x0020);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect bounds);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect bounds);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    internal static double DpiScale(nint window) => GetDpiForWindow(window) / 96d;
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    internal static bool PhoneClicked(nint owner, nint child)
    {
        if (!IsForeground(owner) || child == 0 || (GetAsyncKeyState(1) & 0x8000) == 0) return false;
        GetWindowRect(child, out var bounds);
        return GetCursorPos(out var point) && point.X >= bounds.Left && point.X < bounds.Right && point.Y >= bounds.Top && point.Y < bounds.Bottom;
    }
    internal static bool IsForeground(nint owner) => GetAncestor(GetForegroundWindow(), 2) == owner;
    internal static void FocusEmbedded(nint owner, nint child)
    {
        if (child == 0 || !IsForeground(owner)) return;
        var target = GetWindowThreadProcessId(child, out _);
        var current = GetCurrentThreadId();
        if (target == 0) return;
        var attached = target != current && AttachThreadInput(current, target, true);
        try { SetFocus(child); }
        finally { if (attached) AttachThreadInput(current, target, false); }
        PostMessage(child, 0x0007, 0, 0); // Keep SDL's keyboard-focus state in sync with the embedded HWND.
    }
    internal static void ForwardKey(nint child, int key, uint scanCode, bool extended, bool repeat, bool up)
    {
        long flags = 1 | ((long)scanCode << 16) | (extended ? 1L << 24 : 0) | (repeat || up ? 1L << 30 : 0) | (up ? 1L << 31 : 0);
        PostMessage(child, up ? 0x0101u : 0x0100u, key, (nint)flags);
    }
    internal static (int Width, int Height) NonClientSize(nint window)
    {
        GetWindowRect(window, out var outer);
        GetClientRect(window, out var client);
        return (outer.Right - outer.Left - client.Right + client.Left, outer.Bottom - outer.Top - client.Bottom + client.Top);
    }
    internal static nint FindWindow(int process)
    {
        nint result = 0;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var id);
            if (id != process || !IsWindowVisible(window)) return true;
            result = window;
            return false;
        }, 0);
        return result;
    }
    internal static void Embed(nint child, nint parent)
    {
        var style = (long)GetStyle(child, -16);
        SetStyle(child, -16, (nint)((style & ~0x80CF0000L) | 0x40000000L | 0x04000000L));
        SetParent(child, parent);
        if (GetParent(child) != parent) throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось встроить окно scrcpy. Включите отдельное окно.");
    }
}
