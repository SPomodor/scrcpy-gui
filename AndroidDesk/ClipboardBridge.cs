using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace AndroidDesk;

// A control-only connection to the bundled, unmodified scrcpy-server 5.0.1.
// Video/audio and interactive input continue to be handled by the scrcpy client.
internal sealed class ClipboardBridge : IAsyncDisposable
{
    private readonly AdbService _adb;
    private readonly string _serial;
    private readonly SemaphoreSlim _write = new(1, 1);
    private TcpClient? _client;
    private Process? _server;
    private string? _port;
    private string? _remote;
    private bool _disposed;
    public bool Connected => !_disposed && _client is { Connected: true };
    public ClipboardBridge(AdbService adb, string serial) { _adb = adb; _serial = serial; }
    public async Task ConnectAsync()
    {
        var id = RandomNumberGenerator.GetInt32(1, int.MaxValue).ToString("x8");
        _remote = "/data/local/tmp/androiddesk-clipboard-" + id + ".jar";
        await _adb.RunAsync(["-s", _serial, "push", Path.Combine(_adb.Tools, "scrcpy-server"), _remote], 15);
        _port = Encoding.UTF8.GetString(await _adb.RunAsync(["-s", _serial, "forward", "tcp:0", "localabstract:scrcpy_" + id])).Trim();
        if (!int.TryParse(_port, out var port)) throw new IOException("ADB не выделил порт для буфера обмена.");
        var start = new ProcessStartInfo(_adb.Adb) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-s", _serial, "shell", "CLASSPATH=" + _remote, "app_process", "/", "com.genymobile.scrcpy.Server", "5.0.1", "scid=" + id, "video=false", "audio=false", "control=true", "tunnel_forward=true", "send_device_meta=false", "clipboard_autosync=false", "cleanup=false", "power_on=false", "log_level=warn" }) start.ArgumentList.Add(arg);
        _server = Process.Start(start) ?? throw new IOException("Не удалось запустить канал буфера обмена.");
        _server.OutputDataReceived += (_, _) => { }; // Never log clipboard payloads.
        _server.ErrorDataReceived += (_, e) => { if (e.Data != null) Log.Write("Clipboard server: " + e.Data); };
        _server.BeginOutputReadLine(); _server.BeginErrorReadLine();
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (_server.HasExited) throw new IOException("Сервер буфера обмена завершился.");
            var client = new TcpClient { NoDelay = true };
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                await client.ConnectAsync("127.0.0.1", port, timeout.Token);
                var hello = new byte[1];
                await client.GetStream().ReadExactlyAsync(hello, timeout.Token);
                if (hello[0] != 0) throw new IOException("Некорректный ответ сервера буфера обмена.");
                _client = client;
                Log.Write("Clipboard bridge connected");
                return;
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { client.Dispose(); }
            await Task.Delay(100);
        }
        throw new IOException("Канал буфера обмена не подключился.");
    }
    public async Task SetTextAsync(string text)
    {
        if (_disposed) return;
        var utf8 = Encoding.UTF8.GetBytes(text);
        if (utf8.Length > 262130) throw new IOException("Текст слишком большой для буфера scrcpy (максимум 256 КБ).");
        var packet = new byte[14 + utf8.Length];
        packet[0] = 9; // SET_CLIPBOARD; sequence=0 (no acknowledgement); paste=false.
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(10, 4), (uint)utf8.Length);
        utf8.CopyTo(packet, 14);
        await _write.WaitAsync();
        try
        {
            if (_disposed || _client == null) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _client.GetStream().WriteAsync(packet, timeout.Token);
        }
        finally { _write.Release(); }
    }
    // Used by the real-device smoke test to verify the Unicode round trip without
    // logging or persisting the user's clipboard contents.
    internal async Task<string> ReadTextAsync()
    {
        await _write.WaitAsync();
        try
        {
            if (_disposed || _client == null) throw new IOException("Канал буфера обмена закрыт.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var stream = _client.GetStream();
            await stream.WriteAsync(new byte[] { 8, 0 }, timeout.Token); // GET_CLIPBOARD, no copy key.
            var header = new byte[5];
            await stream.ReadExactlyAsync(header, timeout.Token);
            if (header[0] != 0) throw new IOException("Неожиданный ответ буфера обмена.");
            var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1));
            if (length > 262144) throw new IOException("Некорректная длина буфера обмена.");
            var data = new byte[length];
            await stream.ReadExactlyAsync(data, timeout.Token);
            return Encoding.UTF8.GetString(data);
        }
        finally { _write.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _client?.Dispose();
        if (_server != null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await _server.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { if (!_server.HasExited) _server.Kill(true); }
            _server.Dispose();
        }
        if (_port != null)
        {
            try { await _adb.RunAsync(["-s", _serial, "forward", "--remove", "tcp:" + _port], 3); }
            catch (Exception ex) { Log.Write("Clipboard forward cleanup: " + ex.Message); }
        }
        if (_remote != null)
        {
            try { await _adb.RunAsync(["-s", _serial, "shell", "rm", _remote], 3); }
            catch (Exception ex) { Log.Write("Clipboard server cleanup: " + ex.Message); }
        }
    }
}
