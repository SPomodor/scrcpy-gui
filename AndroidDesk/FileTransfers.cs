using System.Text;

namespace AndroidDesk;

internal enum FileDropStage { Started, Completed, Failed }
internal sealed record FileDropUpdate(string Path, bool Install, FileDropStage Stage, string? Error = null)
{
    public static FileDropUpdate? Parse(string line)
    {
        if (line.StartsWith("INFO: ", StringComparison.Ordinal)) line = line[6..];
        else if (line.StartsWith("ERROR: ", StringComparison.Ordinal)) line = line[7..];
        else return null;
        if (line.StartsWith("Installing ", StringComparison.Ordinal) && line.EndsWith("...", StringComparison.Ordinal))
            return new(line[11..^3], true, FileDropStage.Started);
        if (line.StartsWith("Pushing ", StringComparison.Ordinal) && line.EndsWith("...", StringComparison.Ordinal))
            return new(line[8..^3], false, FileDropStage.Started);
        if (line.EndsWith(" successfully installed", StringComparison.Ordinal))
            return new(line[..^23], true, FileDropStage.Completed);
        const string pushed = " successfully pushed to ";
        var split = line.LastIndexOf(pushed, StringComparison.Ordinal);
        if (split > 0) return new(line[..split], false, FileDropStage.Completed);
        if (line.StartsWith("Failed to install ", StringComparison.Ordinal)) return new(line[18..], true, FileDropStage.Failed);
        if (line.StartsWith("Failed to push ", StringComparison.Ordinal))
        {
            split = line.LastIndexOf(" to ", StringComparison.Ordinal);
            if (split > 15) return new(line[15..split], false, FileDropStage.Failed);
        }
        return null;
    }
}

internal static class FileTransfers
{
    public const string Destination = "/sdcard/Download/";
    public static bool IsApk(string path) => File.Exists(path) && System.IO.Path.GetExtension(path).Equals(".apk", StringComparison.OrdinalIgnoreCase);
    public static async Task SendAsync(AdbService adb, string serial, string path, CancellationToken cancellation)
    {
        path = System.IO.Path.GetFullPath(path);
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException(L.T("Файл или папка больше не доступны."));
        if (IsApk(path))
        {
            var result = Encoding.UTF8.GetString(await adb.RunAsync(["-s", serial, "install", "-r", path], 3600, cancellation));
            if (!result.Split('\n').Any(x => x.Trim() == "Success"))
                throw new IOException(string.IsNullOrWhiteSpace(result) ? L.T("Android не подтвердил установку APK.") : result.Trim());
        }
        else
        {
            await adb.RunAsync(["-s", serial, "shell", "mkdir", "-p", Destination], 10, cancellation);
            await adb.RunAsync(["-s", serial, "push", path, Destination], 3600, cancellation);
            // Media scanning is best effort; an unsupported broadcast must not turn
            // a completed transfer into a failure. Pass only our fixed target URI.
            try { await adb.RunAsync(["-s", serial, "shell", "am", "broadcast", "-a", "android.intent.action.MEDIA_SCANNER_SCAN_FILE", "-d", "file:///sdcard/Download/"], 10, cancellation); }
            catch (Exception) when (!cancellation.IsCancellationRequested) { }
        }
    }
}
