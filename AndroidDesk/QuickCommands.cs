using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AndroidDesk;

internal sealed record QuickCommand(Guid Id, string Name, string Command, bool Pinned)
{
    [JsonIgnore] public string Display => $"{(Pinned ? "★  " : "")}{Name}\n{Command.ReplaceLineEndings(" ")}";
}

internal sealed class QuickCommandStore
{
    private readonly string _path;
    private List<QuickCommand> _items = [];
    private bool _preserveUnreadable;
    public IReadOnlyList<QuickCommand> Items => _items;
    public event Action? Changed;
    public QuickCommandStore(string? path = null)
    {
        _path = path ?? Path.Combine(Log.Folder, "commands.json");
        if (!File.Exists(_path)) return;
        try { _items = JsonSerializer.Deserialize<List<QuickCommand>>(File.ReadAllText(_path)) ?? []; }
        catch (Exception ex)
        {
            Log.Write("Cannot read saved commands: " + ex.Message);
            // Preserve the unreadable file before allowing the user to save new commands.
            try { File.Copy(_path, _path + ".unreadable-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); }
            catch { _preserveUnreadable = true; }
        }
        _items = _items.Where(x => x != null && x.Id != Guid.Empty && !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.Command)).DistinctBy(x => x.Id).ToList();
    }
    public QuickCommand Save(Guid? id, string name, string command, bool pinned)
    {
        name = name.Trim(); command = command.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException(L.T("Укажите название команды."));
        CommandRunner.Parse(command);
        var item = new QuickCommand(id ?? Guid.NewGuid(), name, command, pinned);
        var next = _items.ToList();
        var index = next.FindIndex(x => x.Id == item.Id);
        if (index < 0) next.Add(item); else next[index] = item;
        Persist(next);
        return item;
    }
    public void Delete(Guid id) => Persist(_items.Where(x => x.Id != id).ToList());
    private void Persist(List<QuickCommand> items)
    {
        if (_preserveUnreadable) throw new IOException(L.T("Не удалось прочитать библиотеку или создать её резервную копию. Проверьте доступ к commands.json; исходный файл сохранён."));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _path, true);
        _items = items;
        Changed?.Invoke();
    }
}

internal sealed record CommandResult(int ExitCode, string Output);

internal static class CommandRunner
{
    // Parse arguments ourselves; never invoke cmd.exe or PowerShell. Android shell
    // expressions are passed intact to adb shell, including their remote quoting.
    public static IReadOnlyList<string> Parse(string text)
    {
        text = text.Trim();
        var tokens = new List<string>();
        var token = new StringBuilder();
        var quote = '\0'; var started = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote == '\0' && char.IsWhiteSpace(c))
            {
                if (!started) continue;
                tokens.Add(token.ToString()); token.Clear(); started = false;
                if (tokens.Count == 1 && IsAdb(tokens[0])) { tokens.Clear(); continue; }
                if (tokens.Count == 1 && tokens[0] == "shell")
                {
                    var remote = text[(i + 1)..].Trim();
                    if (remote.Length == 0) throw new ArgumentException(L.T("Укажите команду после shell. Интерактивный терминал не поддерживается."));
                    return ["shell", "-n", remote];
                }
                continue;
            }
            started = true;
            if (c == '\\' && i + 1 < text.Length && text[i + 1] == '"') { token.Append('"'); i++; }
            else if (c is '\'' or '"')
            {
                if (quote == '\0') quote = c;
                else if (quote == c) quote = '\0';
                else token.Append(c);
            }
            else token.Append(c);
        }
        if (quote != '\0') throw new ArgumentException(L.T("Закройте кавычки в команде."));
        if (started) tokens.Add(token.ToString());
        if (tokens.Count == 1 && IsAdb(tokens[0])) tokens.Clear();
        if (tokens.Count == 0) throw new ArgumentException(L.T("Введите ADB-команду, например adb shell getprop ro.product.model."));
        if (tokens[0] == "shell") throw new ArgumentException(L.T("Укажите команду после shell."));
        if (tokens[0].StartsWith('-')) throw new ArgumentException(L.T("Начните с команды ADB. Телефон выбирается в окне «Устройства», параметры -s/-d/-e не нужны."));
        return tokens;
    }
    private static bool IsAdb(string value) => value.Equals("adb", StringComparison.OrdinalIgnoreCase) || value.Equals("adb.exe", StringComparison.OrdinalIgnoreCase);

    public static async Task<CommandResult> RunAsync(AdbService adb, string serial, string command, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(serial)) throw new InvalidOperationException(L.T("Сначала выберите подключённый телефон."));
        var args = Parse(command);
        var start = new ProcessStartInfo(adb.Adb)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = adb.Tools,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-s"); start.ArgumentList.Add(serial);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var process = Process.Start(start) ?? throw new IOException(L.T("Не удалось запустить ADB."));
        process.StandardInput.Close();
        var output = ReadBoundedAsync(process.StandardOutput);
        var error = ReadBoundedAsync(process.StandardError);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync();
            await Task.WhenAll(output, error);
            if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
            throw new TimeoutException(L.T("Команда превысила 60 секунд. Клиент ADB остановлен; запущенный процесс на Android может продолжить работу."));
        }
        var stdout = await output; var stderr = await error;
        return new(process.ExitCode, (stdout + (stdout.Length > 0 && stderr.Length > 0 ? "\n" : "") + stderr).TrimEnd());
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        const int limit = 65536;
        var text = new StringBuilder(); var buffer = new char[4096]; var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            var take = Math.Min(read, limit - text.Length);
            if (take > 0) text.Append(buffer, 0, take);
            truncated |= take < read;
        }
        if (truncated) text.Append(L.T("\n… Вывод сокращён (64 тысячи символов). "));
        return text.ToString();
    }
}
