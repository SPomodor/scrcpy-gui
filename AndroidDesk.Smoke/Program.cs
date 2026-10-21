using AndroidDesk;

if (args is ["--wifi-validation"] || args.Length == 2 && args[0] == "--wifi")
{
    foreach (var invalid in new[] { "", "192.168.0.1", "192.168.0.1:0", "192.168.0.1:65536", "--help", "host:5555" })
    {
        try { AdbService.WifiEndpoint(invalid); throw new Exception("Invalid endpoint accepted."); }
        catch (ArgumentException) { }
    }
    if (AdbService.WifiEndpoint(" [::1]:5555 ") != "[::1]:5555") throw new Exception("IPv6 parsing failed.");
    var wifi = new AdbService();
    try { await wifi.PairWifiAsync("127.0.0.1:5555", "123"); throw new Exception("Invalid pairing code accepted."); }
    catch (ArgumentException) { }
    Console.WriteLine("Wi-Fi endpoint and pairing-code validation passed.");
    if (args.Length == 1) return 0;
    var addresses = await wifi.DiscoverWifiAsync();
    Console.WriteLine($"Discovered wireless endpoints: {addresses.Count}");
    var endpoint = await wifi.ConnectWifiAsync(args[1]);
    var frame = await wifi.CaptureFrameAsync(endpoint);
    Console.WriteLine($"Wi-Fi connection and real PNG capture passed ({frame.Length} bytes).");
    return 0;
}
if (args.Length is < 1 or > 2) { Console.Error.WriteLine("Usage: dotnet run --project AndroidDesk.Smoke -- DEVICE_SERIAL [--clipboard]"); return 2; }
var tools = new AdbService();
var serial = args[0];
var devices = await tools.DevicesAsync();
if (!devices.Any(x => x.Serial == serial && x.State == "device")) throw new InvalidOperationException("Selected device is not ready.");
if (args.Length == 2 && args[1] == "--clipboard")
{
    await using var bridge = new ClipboardBridge(tools, serial);
    await bridge.ConnectAsync();
    var original = await bridge.ReadTextAsync();
    try
    {
        const string marker = "AndroidDesk — Проверка буфера 123 ✅";
        await bridge.SetTextAsync(marker);
        if (await bridge.ReadTextAsync() != marker) throw new IOException("Clipboard Unicode round trip failed.");
        Console.WriteLine("Real-device Unicode clipboard round trip passed.");
    }
    finally { await bridge.SetTextAsync(original); }
    return 0;
}
var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "AndroidDesk");
Directory.CreateDirectory(folder);
var path = Path.Combine(folder, $"Smoke_AAC_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
var session = new ScrcpySession();
try
{
    await session.StartAsync(tools, serial, new Preferences { MaxSize = 720, Fps = 30, Bitrate = 2, RecordAudio = true, External = true }, path);
    await Task.Delay(TimeSpan.FromSeconds(4));
    if (!session.Running) throw new IOException("scrcpy exited before the end of the capture.");
}
finally { await session.StopAsync(); }
if (new FileInfo(path).Length < 1024) throw new IOException("Recording is empty.");
Console.WriteLine("MP4 capture completed: " + path);
return 0;
