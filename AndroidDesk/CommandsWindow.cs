using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AndroidDesk;

internal sealed class CommandsWindow : Window
{
    private readonly QuickCommandStore _store;
    private readonly AdbService _adb;
    private readonly Func<string?> _device;
    private readonly TextBox _search = new() { PlaceholderText = "Поиск команд" };
    private readonly ListView _list = new() { DisplayMemberPath = "Display", SelectionMode = ListViewSelectionMode.Single };
    private readonly TextBox _name = new() { Header = "Название", PlaceholderText = "Например, модель телефона", MaxLength = 100 };
    private readonly TextBox _command = new() { Header = "ADB-команда", PlaceholderText = "adb shell getprop ro.product.model", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxHeight = 150, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
    private readonly CheckBox _pinned = new() { Content = "Закрепить на панели быстрого доступа" };
    private readonly Button _save = new() { Content = "Сохранить" };
    private readonly Button _delete = new() { Content = "Удалить", IsEnabled = false };
    private readonly Button _run = new() { Content = "Выполнить" };
    private readonly Button _cancel = new() { Content = "Остановить", IsEnabled = false };
    private readonly TextBlock _status = new() { Text = "Новая команда", TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };
    private readonly TextBox _output = new() { Header = "Результат", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
    private readonly TextBlock _deviceName = new() { FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _deviceTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _execution;
    private Guid? _editing;
    private bool _refreshing, _closed;

    public CommandsWindow(Window owner, AdbService adb, QuickCommandStore store, Func<string?> device)
    {
        _adb = adb; _store = store; _device = device;
        Title = "Быстрые команды"; SystemBackdrop = new MicaBackdrop();
        WindowStyle.Own(this, owner, 880, 760);
        var root = new Grid { Padding = new Thickness(24), RowSpacing = 16, Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(new TextBlock { Text = "Быстрые команды", FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
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
        var create = new Button { Content = "+ Новая команда", HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(create, 2); library.Children.Add(create); body.Children.Add(library);
        var editor = new Grid { RowSpacing = 12 };
        foreach (var h in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) editor.RowDefinitions.Add(new RowDefinition { Height = h });
        Grid.SetColumn(editor, 1); body.Children.Add(editor);
        var form = new StackPanel { Spacing = 12 };
        form.Children.Add(_name); form.Children.Add(_command); form.Children.Add(_pinned);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_save); actions.Children.Add(_delete); actions.Children.Add(_run); actions.Children.Add(_cancel);
        form.Children.Add(actions); form.Children.Add(_deviceName);
        form.Children.Add(new TextBlock { Text = "Можно вводить с adb или без него. Команда выполняется для выбранного телефона. Закреплённые команды запускаются одним нажатием с боковой панели.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.65 });
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
                _editing = item.Id; RefreshList(); _delete.IsEnabled = true; _status.Text = "Сохранено";
            }
            catch (Exception ex) { _status.Text = ex.Message; }
        };
        _delete.Click += (_, _) =>
        {
            if (_editing is not Guid id) return;
            try { _store.Delete(id); LoadEditor(null); RefreshList(); _status.Text = "Команда удалена"; }
            catch (Exception ex) { _status.Text = ex.Message; }
        };
        _run.Click += async (_, _) => await RunAsync(_command.Text, _name.Text);
        _cancel.Click += (_, _) => _execution?.Cancel();
        _store.Changed += RefreshList;
        Closed += (_, _) => { _closed = true; _execution?.Cancel(); _deviceTimer.Stop(); _store.Changed -= RefreshList; };
        RefreshList();
    }
    private void RefreshDevice()
    {
        var serial = _device();
        _deviceName.Text = serial == null ? "Телефон не выбран" : "Устройство: " + serial;
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
        if (_execution == null) _status.Text = item == null ? "Новая команда" : "Сохранённая команда";
    }
    public void DisplayResult(string name, string serial, CommandResult result)
    {
        if (_closed || _execution != null) return;
        _output.Text = result.Output.Length == 0 ? "Команда не вернула текст." : result.Output;
        _status.Text = $"{name} · код завершения {result.ExitCode} · {serial}";
    }
    public async Task RunAsync(string command, string name)
    {
        if (_execution != null) return;
        var serial = _device();
        if (serial == null) { _status.Text = "Сначала подключите и выберите телефон."; return; }
        using var execution = new CancellationTokenSource();
        _execution = execution; _run.IsEnabled = false; _cancel.IsEnabled = true;
        _output.Text = ""; _status.Text = $"Выполняется: {name} · {serial}";
        try
        {
            var result = await CommandRunner.RunAsync(_adb, serial, command, execution.Token);
            if (_closed) return;
            _output.Text = result.Output.Length == 0 ? "Команда не вернула текст." : result.Output;
            _status.Text = $"{name} · код завершения {result.ExitCode} · {serial}";
        }
        catch (OperationCanceledException) { if (!_closed) _status.Text = "Клиент ADB остановлен. Процесс на Android может продолжить работу."; }
        catch (Exception ex) { if (!_closed) { _output.Text = ex.Message; _status.Text = "Не удалось выполнить команду"; } }
        finally { _execution = null; if (!_closed) { _cancel.IsEnabled = false; RefreshDevice(); } }
    }
}
