using AndroidDesk;

if (args.Length != 1) { Console.Error.WriteLine("Usage: dotnet run --project AndroidDesk.Smoke -- DEVICE_SERIAL"); return 2; }
var tools = new AdbService();
var serial = args[0];
var devices = await tools.DevicesAsync();
if (!devices.Any(x => x.Serial == serial && x.State == "device")) throw new InvalidOperationException("Selected device is not ready.");
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
