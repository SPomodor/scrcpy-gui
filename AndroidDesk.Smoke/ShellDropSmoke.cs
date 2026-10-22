using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using AndroidDesk;

internal static class ShellDropSmoke
{
    public static Task RunAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                string[] expected = [@"C:\Files\проверка с пробелами.APK", @"C:\Files\照片.png", @"C:\Files\folder"];
                string[]? received = null;
                var ready = true;
                using var target = new ShellFileDrop(() => ready, paths => received = paths);
                var data = new DropData(expected);
                uint effect = 3;
                target.DragEnter(data, 0, default, ref effect);
                if (effect != 1) throw new Exception("Shell copy effect was not offered.");
                target.Drop(data, 0, default, ref effect);
                if (effect != 1 || received == null || !expected.SequenceEqual(received)) throw new Exception("Unicode shell drop paths changed.");
                received = null; ready = false; effect = 3;
                target.DragEnter(data, 0, default, ref effect);
                target.Drop(data, 0, default, ref effect);
                if (effect != 0 || received != null) throw new Exception("Drop accepted without a selected phone.");
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
    private sealed class DropData(string[] paths) : IDataObject
    {
        [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint size);
        [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
        public int QueryGetData(ref FORMATETC format) => format.cfFormat == 15 ? 0 : unchecked((int)0x80040064);
        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            if (QueryGetData(ref format) != 0) throw new NotSupportedException();
            var names = Encoding.Unicode.GetBytes(string.Join('\0', paths) + "\0\0");
            var memory = GlobalAlloc(0x42, (nuint)(20 + names.Length));
            var pointer = GlobalLock(memory);
            try
            {
                Marshal.WriteInt32(pointer, 0, 20); // DROPFILES.pFiles
                Marshal.WriteInt32(pointer, 16, 1); // Unicode
                Marshal.Copy(names, 0, pointer + 20, names.Length);
            }
            finally { GlobalUnlock(memory); }
            medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = memory };
        }
        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new NotSupportedException();
        public int GetCanonicalFormatEtc(ref FORMATETC input, out FORMATETC output) { output = input; return 1; }
        public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release) => throw new NotSupportedException();
        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => throw new NotSupportedException();
        public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection) { connection = 0; return unchecked((int)0x80040003); }
        public void DUnadvise(int connection) => throw new NotSupportedException();
        public int EnumDAdvise(out IEnumSTATDATA enumerator) { enumerator = null!; return unchecked((int)0x80040003); }
    }
}
