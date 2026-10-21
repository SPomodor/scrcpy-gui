using AndroidDesk;

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
