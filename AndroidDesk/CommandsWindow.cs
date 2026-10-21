using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AndroidDesk;

internal sealed class CommandsWindow : Window
{
    private readonly QuickCommandStore _store;
    private readonly AdbService _adb;
    private readonly Func<string?> _device;
    private readonly TextBox _search = new() { PlaceholderText = L.T("Поиск команд") };
    private readonly ListView _list = new() { DisplayMemberPath = "Display", SelectionMode = ListViewSelectionMode.Single };
    private readonly TextBox _name = new() { Header = L.T("Название"), PlaceholderText = L.T("Например, модель телефона"), MaxLength = 100 };
    private readonly TextBox _command = new() { Header = L.T("ADB-команда"), PlaceholderText = "adb shell getprop ro.product.model", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxHeight = 150, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
    private readonly CheckBox _pinned = new() { Content = L.T("Закрепить на панели быстрого доступа") };
    private readonly Button _save = new() { Content = L.T("Сохранить") };
    private readonly Button _delete = new() { Content = L.T("Удалить"), IsEnabled = false };
    private readonly Button _run = new() { Content = L.T("Выполнить") };
    private readonly Button _cancel = new() { Content = L.T("Остановить"), IsEnabled = false };
    private readonly TextBlock _status = new() { Text = L.T("Новая команда"), TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };
    private readonly TextBox _output = new() { Header = L.T("Результат"), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
    private readonly TextBlock _deviceName = new() { FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _deviceTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _execution;
    private Guid? _editing;
    private bool _refreshing, _closed;
    private (string Name, string Serial, CommandResult Result)? _lastResult;
    private string? _resultStatus;
    private (string Name, string Serial)? _runningCommand;

    public CommandsWindow(Window owner, AdbService adb, QuickCommandStore store, Func<string?> device)
    {
        _adb = adb; _store = store; _device = device;
        Title = L.T("Быстрые команды"); SystemBackdrop = new MicaBackdrop();
        WindowStyle.Own(this, owner, 880, 760);
        var root = new Grid { Padding = new Thickness(24), RowSpacing = 16, Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(new TextBlock { Text = L.T("Быстрые команды"), FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var body = new Grid { ColumnSpacing = 24 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1); root.Children.Add(body);
        var library = new Grid { RowSpacing = 12 };
        library.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        library.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        library.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        library.Children.Add(_search);
        Grid.SetRow(_list, 1); library.Children.Add(_list);
        var create = new Button { Content = L.T("+ Новая команда"), HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(create, 2); library.Children.Add(create); body.Children.Add(library);
        var editor = new Grid { RowSpacing = 12 };
        foreach (var h in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) editor.RowDefinitions.Add(new RowDefinition { Height = h });
        Grid.SetColumn(editor, 1); body.Children.Add(editor);
        var form = new StackPanel { Spacing = 12 };
        form.Children.Add(_name); form.Children.Add(_command); form.Children.Add(_pinned);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_save); actions.Children.Add(_delete); actions.Children.Add(_run); actions.Children.Add(_cancel);
        form.Children.Add(actions); form.Children.Add(_deviceName);
        form.Children.Add(new TextBlock { Text = L.T("Можно вводить с adb или без него. Команда выполняется для выбранного телефона. Закреплённые команды запускаются одним нажатием с боковой панели."), TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.65 });
        editor.Children.Add(new ScrollViewer { Content = form, MaxHeight = 400, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        ScrollViewer.SetVerticalScrollBarVisibility(_output, ScrollBarVisibility.Auto);
        Grid.SetRow(_output, 1); editor.Children.Add(_output);
        Grid.SetRow(_status, 2); editor.Children.Add(_status);
        Content = root;
        root.Loaded += (_, _) => { WindowStyle.Apply(this, root); RefreshDevice(); _deviceTimer.Start(); };
        _deviceTimer.Tick += (_, _) => RefreshDevice();
        create.Click += (_, _) => { _list.SelectedItem = null; LoadEditor(null); };
        _search.TextChanged += (_, _) => RefreshList();
        _list.SelectionChanged += (_, _) => { if (!_refreshing && _list.SelectedItem is QuickCommand item) LoadEditor(item); };
        _save.Click += (_, _) =>
        {
            try
            {
                var item = _store.Save(_editing, _name.Text, _command.Text, _pinned.IsChecked == true);
                _editing = item.Id; RefreshList(); _delete.IsEnabled = true; _status.Text = L.T("Сохранено");
            }
            catch (Exception ex) { _status.Text = ex.Message; }
        };
        _delete.Click += (_, _) =>
        {
            if (_editing is not Guid id) return;
            try { _store.Delete(id); LoadEditor(null); RefreshList(); _status.Text = L.T("Команда удалена"); }
            catch (Exception ex) { _status.Text = ex.Message; }
        };
        _run.Click += async (_, _) => await RunAsync(_command.Text, _name.Text);
        _cancel.Click += (_, _) => _execution?.Cancel();
        _store.Changed += RefreshList;
        L.Changed += RefreshLanguage;
        Closed += (_, _) => { _closed = true; _execution?.Cancel(); _deviceTimer.Stop(); _store.Changed -= RefreshList; L.Changed -= RefreshLanguage; };
        RefreshList();
    }
    private void RefreshDevice()
    {
        var serial = _device();
        _deviceName.Text = serial == null ? L.T("Телефон не выбран") : L.T("Устройство: ") + serial;
        _run.IsEnabled = _execution == null && serial != null;
    }
    private void RefreshList()
    {
        _refreshing = true;
        try
        {
            var query = _search.Text.Trim();
            var items = _store.Items.Where(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Command.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Pinned).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            _list.ItemsSource = items;
            _list.SelectedItem = items.FirstOrDefault(x => x.Id == _editing);
        }
        finally { _refreshing = false; }
    }
    private void LoadEditor(QuickCommand? item)
    {
        _editing = item?.Id; _name.Text = item?.Name ?? ""; _command.Text = item?.Command ?? "";
        _pinned.IsChecked = item?.Pinned ?? false; _delete.IsEnabled = item != null;
        if (_execution == null) _status.Text = item == null ? L.T("Новая команда") : L.T("Сохранённая команда");
    }
    public void DisplayResult(string name, string serial, CommandResult result)
    {
        if (_closed || _execution != null) return;
        SetResult(name, serial, result);
    }
    private void SetResult(string name, string serial, CommandResult result)
    {
        _lastResult = (name, serial, result);
        _output.Text = result.Output.Length == 0 ? L.T("Команда не вернула текст.") : result.Output;
        _status.Text = L.F("{0} · код завершения {1} · {2}", name, result.ExitCode, serial);
        _resultStatus = _status.Text;
    }
    private void RefreshLanguage()
    {
        RefreshDevice();
        if (_runningCommand is { } running) _status.Text = L.F("Выполняется: {0} · {1}", running.Name, running.Serial);
        else if (_lastResult is { } last && _status.Text == _resultStatus) SetResult(last.Name, last.Serial, last.Result);
    }
    public async Task RunAsync(string command, string name)
    {
        if (_execution != null) return;
        var serial = _device();
        if (serial == null) { _status.Text = L.T("Сначала подключите и выберите телефон."); return; }
        using var execution = new CancellationTokenSource();
        _execution = execution; _run.IsEnabled = false; _cancel.IsEnabled = true;
        _runningCommand = (name, serial);
        _output.Text = ""; _status.Text = L.F("Выполняется: {0} · {1}", name, serial);
        try
        {
            var result = await CommandRunner.RunAsync(_adb, serial, command, execution.Token);
            if (_closed) return;
            SetResult(name, serial, result);
        }
        catch (OperationCanceledException) { if (!_closed) _status.Text = L.T("Клиент ADB остановлен. Процесс на Android может продолжить работу."); }
        catch (Exception ex) { if (!_closed) { _output.Text = ex.Message; _status.Text = L.T("Не удалось выполнить команду"); } }
        finally { _execution = null; _runningCommand = null; if (!_closed) { _cancel.IsEnabled = false; RefreshDevice(); } }
    }
}
