using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Net;

namespace AndroidDesk;

internal static class Log
{
    public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AndroidDesk");
    private static readonly object Gate = new();
    public static void Write(string value)
    {
        lock (Gate)
        {
            try { Directory.CreateDirectory(Folder); File.AppendAllText(Path.Combine(Folder, "app.log"), $"{DateTime.Now:O} {value}\n"); }
            catch { /* Logging must not terminate a session. */ }
        }
    }
}

internal sealed class Preferences
{
    public int MaxSize { get; set; } = 1920;
    public int Fps { get; set; } = 60;
    public int Bitrate { get; set; } = 8;
    public bool External { get; set; }
    public bool AutoConnect { get; set; } = true;
    public string RecordingFormat { get; set; } = "mp4";
    public bool RecordAudio { get; set; } = true;
    public bool PhysicalKeyboard { get; set; } = true;
    public bool SharedClipboard { get; set; } = true;
    public string Language { get; set; } = "system";
    public Preferences Copy() => (Preferences)MemberwiseClone();
    public static Preferences Load()
    {
        try { return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Path.Combine(Log.Folder, "settings.json"))) ?? new(); }
        catch { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Log.Folder);
        File.WriteAllText(Path.Combine(Log.Folder, "settings.json"), JsonSerializer.Serialize(this));
    }
}

internal sealed record Device(string Serial, string State, string Model)
{
    public string Label => $"{Model} · {(Serial.Contains(':') || Serial.Contains("._adb-tls-") ? "Wi-Fi" : "USB")}";
    public string PickerLabel => $"{Label}\n{Serial}";
}

internal sealed class AdbService
{
    public string Tools { get; }
    public string Adb => Path.Combine(Tools, "adb.exe");
    public string Scrcpy => Path.Combine(Tools, "scrcpy.exe");
    public AdbService()
    {
        Tools = Path.Combine(AppContext.BaseDirectory, "scrcpy");
        if (!File.Exists(Scrcpy))
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                var candidate = Path.Combine(current.FullName, "scrcpy");
                if (File.Exists(Path.Combine(candidate, "scrcpy.exe"))) { Tools = candidate; break; }
                current = current.Parent;
            }
        }
    }
    public async Task<byte[]> RunAsync(IEnumerable<string> args, int timeoutSeconds = 10)
    {
        var start = new ProcessStartInfo(Adb) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Tools };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException(L.T("Не удалось запустить ADB."));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var output = new MemoryStream();
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), copy);
            var error = await errors;
            if (process.ExitCode != 0) throw new IOException(error.Trim());
            return output.ToArray();
        }
        catch
        {
            if (!process.HasExited) process.Kill(true);
            try { await Task.WhenAll(copy, errors); } catch { }
            throw;
        }
    }
    public static IReadOnlyList<Device> ParseDevices(string output)
    {
        var result = new List<Device>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0] == "List" || parts[0].StartsWith('*')) continue;
            var model = parts.FirstOrDefault(x => x.StartsWith("model:"))?[6..].Replace('_', ' ') ?? parts[0];
            result.Add(new(parts[0], parts[1], model));
        }
        return result;
    }
    public async Task<IReadOnlyList<Device>> DevicesAsync() => ParseDevices(Encoding.UTF8.GetString(await RunAsync(["devices", "-l"])));
    public static string WifiEndpoint(string value)
    {
        value = value.Trim();
        var separator = value.LastIndexOf(':');
        if (separator < 1 || !IPAddress.TryParse(value[..separator].Trim('[', ']'), out var address) ||
            !int.TryParse(value[(separator + 1)..], out var port) || port is < 1 or > 65535)
            throw new ArgumentException(L.T("Укажите IP и порт с экрана телефона, например 192.168.0.10:37001."));
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]:{port}" : $"{address}:{port}";
    }
    public async Task<IReadOnlyList<string>> DiscoverWifiAsync()
    {
        var output = Encoding.UTF8.GetString(await RunAsync(["mdns", "services"], 8));
        var addresses = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || parts[1] != "_adb-tls-connect._tcp") continue;
            try { addresses.Add(WifiEndpoint(parts[2])); } catch (ArgumentException) { }
        }
        return addresses.Distinct().ToArray();
    }
    public async Task PairWifiAsync(string endpoint, string code)
    {
        endpoint = WifiEndpoint(endpoint);
        if (code.Length != 6 || code.Any(c => c is < '0' or > '9'))
            throw new ArgumentException(L.T("Введите шестизначный код с экрана сопряжения телефона."));
        var output = Encoding.UTF8.GetString(await RunAsync(["pair", endpoint, code], 20));
        if (!output.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase))
            throw new IOException(L.T("Не удалось выполнить сопряжение. Проверьте код и порт сопряжения на телефоне."));
    }
    public async Task<string> ConnectWifiAsync(string endpoint)
    {
        endpoint = WifiEndpoint(endpoint);
        var output = Encoding.UTF8.GetString(await RunAsync(["connect", endpoint], 15));
        if (!output.Contains("connected to", StringComparison.OrdinalIgnoreCase))
            throw new IOException(L.T("Не удалось подключиться. Проверьте Wi-Fi, сопряжение и порт подключения на телефоне."));
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if ((await DevicesAsync()).Any(x => x.Serial == endpoint && x.State == "device")) return endpoint;
            await Task.Delay(250);
        }
        throw new IOException(L.T("Телефон найден, но ещё не готов. Проверьте разрешение отладки на телефоне и попробуйте снова."));
    }
    public Task<byte[]> KeyAsync(string serial, int key) => RunAsync(["-s", serial, "shell", "input", "keyevent", key.ToString()]);
    public async Task<byte[]> CaptureFrameAsync(string serial)
    {
        var data = await RunAsync(["-s", serial, "exec-out", "screencap", "-p"], 8);
        if (data.Length < 8 || !data.Take(8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})) throw new IOException(L.T("ADB не вернул PNG-изображение."));
        return data;
    }
    public async Task<string> ScreenshotAsync(string serial)
    {
        var data = await CaptureFrameAsync(serial);
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "AndroidDesk");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"Screenshot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.png");
        await File.WriteAllBytesAsync(path, data);
        return path;
    }
}

internal sealed class ScrcpySession
{
    private Process? _process;
    public nint Window { get; private set; }
    public bool Running => _process is { HasExited: false };
    public string? RecordingPath { get; private set; }
    private int _videoWidth, _videoHeight;
    public double? VideoAspect => Volatile.Read(ref _videoHeight) is var height && height > 0 ? (double)Volatile.Read(ref _videoWidth) / height : null;
    public event Action? Exited;
    public async Task StartAsync(AdbService tools, string serial, Preferences settings, string? recording, Action<nint>? prepareWindow = null)
    {
        if (_process != null) throw new InvalidOperationException(L.T("Сессия уже запущена."));
        var info = new ProcessStartInfo(tools.Scrcpy) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = tools.Tools, RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment["ADB"] = tools.Adb;
        foreach (var arg in new[] {"--serial", serial, "--window-title=AndroidDesk • scrcpy", "--video-codec=h264", $"--max-size={settings.MaxSize}", $"--max-fps={settings.Fps}", $"--video-bit-rate={settings.Bitrate}M", "--video-buffer=0", "--background-color=101317", "--no-window-aspect-ratio-lock", "--window-width=420", "--window-height=780"}) info.ArgumentList.Add(arg);
        info.ArgumentList.Add(settings.PhysicalKeyboard ? "--keyboard=uhid" : "--keyboard=sdk");
        if (recording != null && settings.RecordAudio)
        {
            info.ArgumentList.Add("--audio-codec=aac");
            info.ArgumentList.Add("--require-audio");
            info.ArgumentList.Add("--no-audio-playback");
        }
        else info.ArgumentList.Add("--no-audio");
        if (!settings.External) info.ArgumentList.Add("--window-borderless");
        if (recording != null) info.ArgumentList.Add($"--record={recording}");
        var process = Process.Start(info) ?? throw new IOException(L.T("Не удалось запустить scrcpy."));
        _process = process;
        RecordingPath = recording;
        var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void ReadLine(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            Log.Write("scrcpy: " + e.Data);
            if (e.Data.Contains("Texture", StringComparison.Ordinal))
            {
                var size = System.Text.RegularExpressions.Regex.Match(e.Data, @"(\d+)x(\d+)");
                if (size.Success)
                {
                    Volatile.Write(ref _videoWidth, int.Parse(size.Groups[1].Value));
                    Volatile.Write(ref _videoHeight, int.Parse(size.Groups[2].Value));
                    firstFrame.TrySetResult();
                }
            }
        }
        process.OutputDataReceived += ReadLine;
        process.ErrorDataReceived += ReadLine;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => Exited?.Invoke();
        try
        {
            for (var attempt = 0; attempt < 200; attempt++)
            {
                if (process.HasExited) throw new IOException(L.F("scrcpy завершился с кодом {0}. Подробности: {1}", process.ExitCode, Path.Combine(Log.Folder, "app.log")));
                if (Window == 0)
                {
                    Window = Native.FindWindow(process.Id);
                    if (Window != 0) prepareWindow?.Invoke(Window);
                }
                if (Window != 0 && firstFrame.Task.IsCompleted)
                {
                    // Give SDL one render pass before exposing its replacement window.
                    await Task.Delay(100);
                    if (process.HasExited) throw new IOException(L.T("scrcpy завершился во время запуска видео."));
                    return;
                }
                await Task.Delay(100);
            }
            throw new TimeoutException(L.T("scrcpy не подготовил видео за 20 секунд. Проверьте телефон и разрешение отладки."));
        }
        catch { await StopAsync(); throw; }
    }
    public async Task StopAsync()
    {
        var process = _process;
        if (process == null) return;
        _process = null;
        try
        {
            if (!process.HasExited)
            {
                if (Window != 0) Native.PostMessage(Window, 0x0010, 0, 0); // SDL handles WM_CLOSE and finalizes the container.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    process.Kill(true);
                    await process.WaitForExitAsync();
                    throw new IOException(L.T("scrcpy не ответил на закрытие. Процесс остановлен; последняя видеозапись может быть повреждена."));
                }
            }
            if (process.ExitCode != 0) throw new IOException(L.F("scrcpy завершился с кодом {0}. Проверьте журнал перед использованием записи.", process.ExitCode));
        }
        finally { process.Dispose(); Window = 0; RecordingPath = null; }
    }
}
