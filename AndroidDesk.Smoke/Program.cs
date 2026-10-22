using AndroidDesk;

if (args is ["--apk-test", var apkDevice, var apkPath])
{
    const string package = "dev.spomodor.androiddesk.transfertest";
    var adb = new AdbService();
    var existing = System.Text.Encoding.UTF8.GetString(await adb.RunAsync(["-s", apkDevice, "shell", "pm", "list", "packages", package]));
    if (existing.Contains(package)) throw new Exception("Test package already exists; refusing to replace it.");
    try
    {
        await FileTransfers.SendAsync(adb, apkDevice, apkPath, default);
        await FileTransfers.SendAsync(adb, apkDevice, apkPath, default);
        var installed = System.Text.Encoding.UTF8.GetString(await adb.RunAsync(["-s", apkDevice, "shell", "pm", "list", "packages", package]));
        if (!installed.Contains("package:" + package)) throw new Exception("Test APK not installed.");
        Console.WriteLine("Real-device APK installation and replacement passed.");
    }
    finally { await adb.RunAsync(["-s", apkDevice, "uninstall", package]); }
    return 0;
}
if (args is ["--file-validation"] || args.Length == 2 && args[0] == "--files")
{
    const string sample = @"C:\My files\тест.APK";
    foreach (var (line, install, stage) in new[] {
        ($"INFO: Installing {sample}...", true, FileDropStage.Started),
        ($"INFO: Pushing {sample}...", false, FileDropStage.Started),
        ($"INFO: {sample} successfully installed", true, FileDropStage.Completed),
        ($"INFO: {sample} successfully pushed to /sdcard/Download/", false, FileDropStage.Completed),
        ($"ERROR: Failed to install {sample}", true, FileDropStage.Failed),
        ($"ERROR: Failed to push {sample} to /sdcard/Download/", false, FileDropStage.Failed) })
    {
        var parsed = FileDropUpdate.Parse(line);
        if (parsed?.Path != sample || parsed.Install != install || parsed.Stage != stage) throw new Exception("File event parsing failed: " + line);
    }
    if (FileDropUpdate.Parse("INFO: Renderer: direct3d") != null) throw new Exception("Unrelated log accepted.");
    var root = Path.Combine(Path.GetTempPath(), "AndroidDesk-files-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var invalidApk = Path.Combine(root, "invalid.APK");
        await File.WriteAllTextAsync(invalidApk, "Not an Android package");
        if (!FileTransfers.IsApk(invalidApk) || FileTransfers.IsApk(root)) throw new Exception("APK detection failed.");
        var adb = new AdbService();
        try { await FileTransfers.SendAsync(adb, "unused", Path.Combine(root, "missing"), default); throw new Exception("Missing file accepted."); }
        catch (FileNotFoundException) { }
        if (args.Length == 2)
        {
            var device = args[1];
            var testFolder = Path.Combine(root, "AndroidDesk-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testFolder);
            var name = "проверка с пробелом.txt";
            var bytes = System.Text.Encoding.UTF8.GetBytes("Android Desk · UTF-8 transfer ✅");
            await File.WriteAllBytesAsync(Path.Combine(testFolder, name), bytes);
            var remote = FileTransfers.Destination + Path.GetFileName(testFolder);
            var single = Path.Combine(root, "AndroidDesk-" + Guid.NewGuid().ToString("N") + " файл.txt");
            await File.WriteAllBytesAsync(single, bytes);
            var singleRemote = FileTransfers.Destination + Path.GetFileName(single);
            try
            {
                await FileTransfers.SendAsync(adb, device, testFolder, default);
                var readback = await adb.RunAsync(["-s", device, "exec-out", "cat", remote + "/" + name ]);
                if (!readback.SequenceEqual(bytes)) throw new Exception("Folder Unicode round trip failed: " + Convert.ToHexString(readback) + " / " + Convert.ToHexString(bytes));
                await FileTransfers.SendAsync(adb, device, single, default);
                readback = await adb.RunAsync(["-s", device, "exec-out", "cat", singleRemote]);
                if (!readback.SequenceEqual(bytes)) throw new Exception("File Unicode round trip failed.");
                try { await FileTransfers.SendAsync(adb, device, invalidApk, default); throw new Exception("Invalid APK accepted."); }
                catch (IOException ex) { if (string.IsNullOrWhiteSpace(ex.Message)) throw new Exception("APK failure diagnostics lost."); }
                using var cancel = new CancellationTokenSource(); cancel.Cancel();
                try { await FileTransfers.SendAsync(adb, device, single, cancel.Token); throw new Exception("Cancellation ignored."); }
                catch (OperationCanceledException) { }
                Console.WriteLine("Real-device Unicode file/folder round trips, invalid APK diagnostics and cancellation passed.");
            }
            finally { await adb.RunAsync(["-s", device, "shell", "rm", "-rf", "'" + remote + "'", "'" + singleRemote + "'"]); }
        }
    }
    finally { Directory.Delete(root, true); }
    Console.WriteLine("Native drop events, APK detection and missing-file validation passed.");
    return 0;
}
if (args is ["--language"])
{
    foreach (var (preference, system, expected) in new[] { ("system", "ru-RU", "ru"), ("system", "ru-KZ", "ru"), ("system", "en-US", "en"), ("system", "de-DE", "en"), ("en", "ru-RU", "en"), ("ru", "en-US", "ru"), ("unknown", "ru-RU", "ru") })
        if (L.Resolve(preference, system) != expected) throw new Exception("System language resolution failed.");
    var legacy = System.Text.Json.JsonSerializer.Deserialize<Preferences>("{\"MaxSize\":960}")!;
    if (legacy.Language != "system" || legacy.Copy().Language != "system") throw new Exception("Legacy preference migration failed.");
    L.Set("en");
    if (L.T("Настройки") != "Settings" || L.F("{0} · код завершения {1} · {2}", "User command", 7, "serial") != "User command · exit code 7 · serial") throw new Exception("English translation failed.");
    if (L.Label("Настройки") != "Settings") throw new Exception("Live label translation failed.");
    L.Set("ru");
    if (L.Label("Settings") != "Настройки" || L.T("Настройки") != "Настройки") throw new Exception("Russian reverse translation failed.");
    using var resources = typeof(L).Assembly.GetManifestResourceStream("AndroidDesk.Strings.en.json")!;
    var translations = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(resources)!;
    foreach (var (key, value) in translations)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new Exception("Empty translation: " + key);
        var formatTokens = System.Text.RegularExpressions.Regex.Matches(key, @"\{\d+\}").Select(x => x.Value).Order().ToArray();
        var translatedTokens = System.Text.RegularExpressions.Regex.Matches(value, @"\{\d+\}").Select(x => x.Value).Order().ToArray();
        if (!formatTokens.SequenceEqual(translatedTokens)) throw new Exception("Format tokens changed: " + key);
    }
    Console.WriteLine($"Language detection, manual overrides, live labels, legacy preferences and {translations.Count} translations passed.");
    return 0;
}
if (args.Length == 2 && args[0] == "--commands")
{
    var parsed = CommandRunner.Parse("adb shell printf '%s' 'hello world'");
    if (!parsed.SequenceEqual(new[] { "shell", "-n", "printf '%s' 'hello world'" })) throw new Exception("Remote shell quoting changed.");
    if (!CommandRunner.Parse("adb.exe install \"C:\\folder name\\app.apk\"").SequenceEqual(new[] { "install", "C:\\folder name\\app.apk" })) throw new Exception("Windows file argument changed.");
    foreach (var invalid in new[] { "", "adb", "adb shell", "-s other shell id", "install \"unfinished" })
    {
        try { CommandRunner.Parse(invalid); throw new Exception("Invalid command accepted."); } catch (ArgumentException) { }
    }
    var libraryPath = Path.Combine(Path.GetTempPath(), "AndroidDesk-commands-" + Guid.NewGuid() + ".json");
    try
    {
        var store = new QuickCommandStore(libraryPath);
        var first = store.Save(null, "Test 0", "adb shell getprop ro.product.model", false);
        // Populate a substantial library, including multiline shell expressions.
        for (var i = 1; i < 150; i++) store.Save(null, "Test " + i, "shell echo one\necho two", false);
        store.Save(first!.Id, "Pinned model", first.Command, true);
        var loaded = new QuickCommandStore(libraryPath);
        if (loaded.Items.Count != 150 || !loaded.Items.Single(x => x.Id == first.Id).Pinned) throw new Exception("Command persistence failed.");
        loaded.Delete(first.Id);
        if (new QuickCommandStore(libraryPath).Items.Count != 149) throw new Exception("Command deletion failed.");
    }
    finally { File.Delete(libraryPath); if (File.Exists(libraryPath + ".tmp")) File.Delete(libraryPath + ".tmp"); }
    var adb = new AdbService();
    var model = await CommandRunner.RunAsync(adb, args[1], "adb shell getprop ro.product.model");
    if (model.ExitCode != 0 || string.IsNullOrWhiteSpace(model.Output)) throw new Exception("Device execution failed.");
    var quoted = await CommandRunner.RunAsync(adb, args[1], "shell printf '%s' 'hello world'");
    if (quoted.ExitCode != 0 || quoted.Output != "hello world") throw new Exception("Real shell quoting failed: " + quoted.Output);
    var failed = await CommandRunner.RunAsync(adb, args[1], "shell echo expected-error >&2; exit 7");
    if (failed.ExitCode != 7 || !failed.Output.Contains("expected-error")) throw new Exception("Exit code / stderr lost.");
    using var cancel = new CancellationTokenSource(300);
    try { await CommandRunner.RunAsync(adb, args[1], "shell sleep 5", cancel.Token); throw new Exception("Cancellation failed."); }
    catch (OperationCanceledException) { }
    Console.WriteLine("Command library (150 entries), pin/edit/delete, parsing, real-device output, quoting, stderr/exit code and cancellation passed.");
    return 0;
}
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
