using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace AndroidDesk;

// Use the Windows shell's CF_HDROP directly, including over the embedded HWND.
// XAML hit testing does not include the foreign SDL child window.
[ComVisible(true), Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellDropTarget
{
    [PreserveSig] int DragEnter(IDataObject data, uint keys, DropPoint point, ref uint effect);
    [PreserveSig] int DragOver(uint keys, DropPoint point, ref uint effect);
    [PreserveSig] int DragLeave();
    [PreserveSig] int Drop(IDataObject data, uint keys, DropPoint point, ref uint effect);
}
[StructLayout(LayoutKind.Sequential)]
public struct DropPoint { public int X, Y; }

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class ShellFileDrop : IShellDropTarget, IDisposable
{
    [DllImport("ole32.dll")] private static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("ole32.dll")] private static extern int RegisterDragDrop(nint window, IShellDropTarget target);
    [DllImport("ole32.dll")] private static extern int RevokeDragDrop(nint window);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFileW(nint drop, uint index, StringBuilder? path, uint size);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    private readonly Func<bool> _canDrop;
    private readonly Action<string[]> _received;
    private readonly List<nint> _windows = [];
    private readonly bool _initialized;
    private bool _files, _disposed;
    private static FORMATETC Format => new() { cfFormat = 15, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };

    public ShellFileDrop(Func<bool> canDrop, Action<string[]> received)
    {
        _canDrop = canDrop; _received = received;
        var result = OleInitialize(0);
        Marshal.ThrowExceptionForHR(result);
        _initialized = true;
    }
    public void Attach(nint window)
    {
        if (_disposed || window == 0 || _windows.Contains(window)) return;
        RevokeDragDrop(window);
        var result = RegisterDragDrop(window, this);
        Log.Write($"Shell file drop: HWND={window}, register=0x{result:X8}");
        Marshal.ThrowExceptionForHR(result);
        _windows.Add(window);
    }
    public void Detach(nint window)
    {
        if (_windows.Remove(window) && IsWindow(window)) RevokeDragDrop(window);
    }
    public int DragEnter(IDataObject data, uint keys, DropPoint point, ref uint effect)
    {
        try { var format = Format; _files = data.QueryGetData(ref format) == 0; }
        catch { _files = false; }
        return DragOver(keys, point, ref effect);
    }
    public int DragOver(uint keys, DropPoint point, ref uint effect)
    {
        effect = !_disposed && _files && _canDrop() ? effect & 1u : 0;
        return 0;
    }
    public int DragLeave() { _files = false; return 0; }
    public int Drop(IDataObject data, uint keys, DropPoint point, ref uint effect)
    {
        effect = 0;
        if (_disposed || !_canDrop()) return 0;
        try
        {
            var format = Format;
            data.GetData(ref format, out var medium);
            string[] paths;
            try
            {
                var count = DragQueryFileW(medium.unionmember, uint.MaxValue, null, 0);
                paths = new string[checked((int)count)];
                for (uint i = 0; i < count; i++)
                {
                    var size = DragQueryFileW(medium.unionmember, i, null, 0) + 1;
                    var path = new StringBuilder(checked((int)size));
                    DragQueryFileW(medium.unionmember, i, path, size);
                    paths[i] = path.ToString();
                }
            }
            finally { ReleaseStgMedium(ref medium); }
            _files = false;
            if (paths.Length > 0) { _received(paths); effect = 1; }
        }
        catch (Exception ex) { Log.Write("Shell file drop: " + ex); }
        return 0;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var window in _windows.ToArray()) Detach(window);
        if (_initialized) OleUninitialize();
    }
}
