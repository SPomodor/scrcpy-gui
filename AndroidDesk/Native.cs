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
