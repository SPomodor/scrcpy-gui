using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace AndroidDesk;

internal static class WindowStyle
{
    public static void Apply(Window window, FrameworkElement root)
    {
        UiLanguage.Attach(window, root);
        var dark = root.ActualTheme == ElementTheme.Dark;
        var background = dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243);
        var foreground = dark ? Windows.UI.Color.FromArgb(255, 245, 245, 245) : Windows.UI.Color.FromArgb(255, 25, 25, 25);
        window.AppWindow.TitleBar.BackgroundColor = background;
        window.AppWindow.TitleBar.ForegroundColor = foreground;
        window.AppWindow.TitleBar.ButtonBackgroundColor = background;
        window.AppWindow.TitleBar.ButtonForegroundColor = foreground;
        window.AppWindow.TitleBar.InactiveBackgroundColor = background;
        window.AppWindow.TitleBar.ButtonInactiveBackgroundColor = background;
    }
    public static void Own(Window window, Window owner, int width, int height)
    {
        Native.Own(WinRT.Interop.WindowNative.GetWindowHandle(window), WinRT.Interop.WindowNative.GetWindowHandle(owner));
        var area = DisplayArea.GetFromWindowId(owner.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        window.AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, Math.Min(height, area.Height)));
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
    }
    public static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }
}

internal sealed class SettingsWindow : Window
{
    private readonly ComboBox _language = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    public Action<string>? LanguageRequested { get; set; }
    private readonly ComboBox _resolution = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _fps = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _format = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumberBox _bitrate = new() { Minimum = 1, Maximum = 64, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly ToggleSwitch _audio = new() { Header = L.T("Звук в записи"), OnContent = L.T("AAC · звук телефона"), OffContent = L.T("Без звука") };
    private readonly ToggleSwitch _external = new() { Header = L.T("Отдельное окно scrcpy"), OnContent = L.T("Отдельное окно"), OffContent = L.T("Экран внутри приложения") };
    private readonly ToggleSwitch _auto = new() { Header = L.T("Автоподключение"), OnContent = L.T("Включено"), OffContent = L.T("Выключено") };
    private readonly ToggleSwitch _keyboard = new() { Header = L.T("Физическая клавиатура (UHID)"), OnContent = L.T("Включено"), OffContent = L.T("Совместимый ввод SDK") };
    private readonly ToggleSwitch _clipboard = new() { Header = L.T("Общий текстовый буфер"), OnContent = L.T("В обе стороны"), OffContent = L.T("Только штатная вставка scrcpy") };
    public Func<Preferences, Task<bool>>? ApplyRequested { get; set; }
    public SettingsWindow(Window owner, Preferences preferences, bool recording)
    {
        Title = L.T("Настройки");
        SystemBackdrop = new MicaBackdrop();
        WindowStyle.Own(this, owner, 410, 810);
        var panel = new StackPanel { Spacing = 16, Padding = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = L.T("Настройки"), FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        foreach (var (label, value) in new[] { (L.T("Системный"), "system"), (L.T("Русский"), "ru"), ("English", "en") })
        {
            var item = new ComboBoxItem { Content = label, Tag = value };
            _language.Items.Add(item);
            if (value == preferences.Language) _language.SelectedItem = item;
        }
        if (_language.SelectedItem == null) _language.SelectedIndex = 0;
        _language.SelectionChanged += (_, _) =>
        {
            if (_language.SelectedItem is not ComboBoxItem { Tag: string language }) return;
            preferences.Language = language;
            LanguageRequested?.Invoke(preferences.Language);
        };
        AddField(panel, L.T("Язык интерфейса"), _language);
        if (recording) panel.Children.Add(new TextBlock { Text = L.T("Для изменения настроек сначала остановите запись."), TextWrapping = TextWrapping.Wrap });
        AddChoices(_resolution, [("720 px", 720), ("960 px", 960), ("1280 px", 1280), ("1600 px", 1600), ("1920 px", 1920), ("2560 px", 2560), (L.T("Оригинальное"), 0)], preferences.MaxSize);
        AddChoices(_fps, [("30 FPS", 30), ("60 FPS", 60), ("90 FPS", 90), ("120 FPS", 120)], preferences.Fps);
        _format.Items.Add(new ComboBoxItem { Content = "MP4 · H.264 / AAC", Tag = "mp4" });
        _format.Items.Add(new ComboBoxItem { Content = "MKV · H.264 / AAC", Tag = "mkv" });
        _format.SelectedIndex = preferences.RecordingFormat == "mkv" ? 1 : 0;
        _bitrate.Value = preferences.Bitrate;
        _audio.IsOn = preferences.RecordAudio;
        _external.IsOn = preferences.External;
        _auto.IsOn = preferences.AutoConnect;
        _keyboard.IsOn = preferences.PhysicalKeyboard; _clipboard.IsOn = preferences.SharedClipboard;
        foreach (var element in new Control[] { _resolution, _fps, _format, _bitrate, _audio, _external, _auto, _keyboard, _clipboard }) element.IsEnabled = !recording;
        AddField(panel, L.T("Разрешение · длинная сторона"), _resolution);
        AddField(panel, L.T("Частота кадров"), _fps);
        AddField(panel, L.T("Битрейт, Мбит/с"), _bitrate);
        AddField(panel, L.T("Формат записи"), _format);
        panel.Children.Add(_audio); panel.Children.Add(_external); panel.Children.Add(_auto);
        panel.Children.Add(_keyboard); panel.Children.Add(_clipboard);
        panel.Children.Add(new TextBlock { Text = L.T("Щёлкните по экрану телефона перед печатью. Для русского ввода выберите русскую раскладку физической клавиатуры в Android (Alt+K). Ctrl+V вставляет текст с ПК."), FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = L.T("Применение настроек и переключение записи ненадолго перезапускают трансляцию. Захват звука требует Android 11+; на Android 11 разблокируйте телефон."), FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        var apply = new Button { Content = L.T("Применить"), HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = !recording };
        apply.Click += async (_, _) =>
        {
            apply.IsEnabled = false;
            try
            {
                preferences.MaxSize = (int)((ComboBoxItem)_resolution.SelectedItem).Tag;
                preferences.Fps = (int)((ComboBoxItem)_fps.SelectedItem).Tag;
                preferences.Bitrate = double.IsFinite(_bitrate.Value) ? (int)Math.Clamp(Math.Round(_bitrate.Value), 1, 64) : 8;
                preferences.RecordingFormat = (string)((ComboBoxItem)_format.SelectedItem).Tag;
                preferences.RecordAudio = _audio.IsOn; preferences.External = _external.IsOn; preferences.AutoConnect = _auto.IsOn;
                preferences.PhysicalKeyboard = _keyboard.IsOn; preferences.SharedClipboard = _clipboard.IsOn;
                if (ApplyRequested != null && await ApplyRequested(preferences)) Close();
            }
            finally { apply.IsEnabled = !recording; }
        };
        panel.Children.Add(apply);
        var videos = new Button { Content = L.T("Открыть видеозаписи"), HorizontalAlignment = HorizontalAlignment.Stretch };
        videos.Click += (_, _) => { try { WindowStyle.OpenFolder(MainWindow.Videos); } catch (Exception ex) { Log.Write(ex.ToString()); } };
        panel.Children.Add(videos);
        var author = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        author.Children.Add(new TextBlock { Text = "by", VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Opacity = 0.65 });
        author.Children.Add(new HyperlinkButton { Content = "SPomodor", NavigateUri = new Uri("https://github.com/SPomodor"), FontSize = 12, Padding = new Thickness(0, 4, 0, 4) });
        panel.Children.Add(author);
        var root = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] };
        Content = root;
        root.Loaded += (_, _) => WindowStyle.Apply(this, root);
    }
    private static void AddField(StackPanel parent, string label, Control control)
    {
        var group = new StackPanel { Spacing = 6 };
        group.Children.Add(new TextBlock { Text = label, FontSize = 12 });
        group.Children.Add(control); parent.Children.Add(group);
    }
    private static void AddChoices(ComboBox combo, (string Label, int Value)[] choices, int selected)
    {
        foreach (var (label, value) in choices)
        {
            var item = new ComboBoxItem { Content = label, Tag = value };
            combo.Items.Add(item);
            if (value == selected) combo.SelectedItem = item;
        }
        if (combo.SelectedItem == null) combo.SelectedIndex = 0;
    }
}

internal sealed class DeviceWindow : Window
{
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single, DisplayMemberPath = "PickerLabel" };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.65 };
    private readonly Button _connect = new() { Content = L.T("Подключить"), HorizontalAlignment = HorizontalAlignment.Stretch };
    private string _signature = "";
    private bool _connecting, _closed;
    private WifiWindow? _wifi;
    public Func<string, Task>? DeviceRequested { get; set; }
    public DeviceWindow(Window owner, AdbService adb)
    {
        Title = L.T("Выбор устройства");
        SystemBackdrop = new MicaBackdrop();
        WindowStyle.Own(this, owner, 420, 580);
        var root = new Grid { Padding = new Thickness(24), RowSpacing = 16, Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = height });
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = L.T("Устройства"), FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var wifi = new Button { Content = "Wi-Fi" };
        Grid.SetColumn(wifi, 1); header.Children.Add(wifi);
        wifi.Click += (_, _) =>
        {
            if (_wifi != null) { _wifi.Activate(); return; }
            var window = new WifiWindow(this, adb);
            _wifi = window;
            window.Closed += (_, _) => { if (_wifi == window) _wifi = null; };
            window.Connected = async serial =>
            {
                var devices = (await adb.DevicesAsync()).Where(x => x.State == "device").ToArray();
                if (_closed) return;
                UpdateDevices(devices, serial);
                _list.SelectedItem = devices.FirstOrDefault(x => x.Serial == serial);
            };
            window.Activate();
        };
        root.Children.Add(header);
        Grid.SetRow(_hint, 1); root.Children.Add(_hint);
        Grid.SetRow(_list, 2); root.Children.Add(_list);
        Grid.SetRow(_connect, 3); root.Children.Add(_connect);
        _list.SelectionChanged += (_, _) => _connect.IsEnabled = !_connecting && _list.SelectedItem != null;
        _connect.Click += async (_, _) =>
        {
            if (_connecting || _list.SelectedItem is not Device device || DeviceRequested == null) return;
            _connecting = true; _connect.IsEnabled = false;
            try { await DeviceRequested(device.Serial); if (!_closed) Close(); }
            finally { _connecting = false; if (!_closed) _connect.IsEnabled = _list.SelectedItem != null; }
        };
        Closed += (_, _) => { _closed = true; _wifi?.Close(); };
        Content = root;
        root.Loaded += (_, _) => WindowStyle.Apply(this, root);
    }
    public void UpdateDevices(IReadOnlyList<Device> devices, string? current)
    {
        var signature = string.Join('|', devices.Select(x => x.Serial + x.Model));
        if (signature != _signature || _list.ItemsSource == null)
        {
            var selected = (_list.SelectedItem as Device)?.Serial ?? current;
            _signature = signature;
            _list.ItemsSource = devices;
            _list.SelectedItem = devices.FirstOrDefault(x => x.Serial == selected) ?? devices.FirstOrDefault();
        }
        _hint.Text = devices.Count == 0 ? L.T("Подключите телефон по USB или добавьте его по Wi-Fi. Список обновляется автоматически.") : L.T("Выберите телефон · USB или Wi-Fi");
        _connect.IsEnabled = !_connecting && _list.SelectedItem != null;
    }
}

internal sealed class WifiWindow : Window
{
    private readonly AdbService _adb;
    private readonly TextBox _address = new() { Header = L.T("Адрес подключения · IP:порт"), PlaceholderText = "192.168.0.10:37001" };
    private readonly TextBox _pairAddress = new() { Header = L.T("Адрес сопряжения · IP:порт"), PlaceholderText = "192.168.0.10:40001" };
    private readonly PasswordBox _code = new() { Header = L.T("Код сопряжения"), MaxLength = 6 };
    private readonly ComboBox _discovered = new() { Header = L.T("Найдено в сети"), PlaceholderText = L.T("Выберите адрес"), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _refresh = new() { Content = L.T("Обновить"), VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Button _connect = new() { Content = L.T("Добавить устройство"), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _pair = new() { Content = L.T("Выполнить сопряжение"), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 };
    private bool _busy, _closed;
    public Func<string, Task>? Connected { get; set; }
    public WifiWindow(Window owner, AdbService adb)
    {
        _adb = adb;
        Title = L.T("Подключение по Wi-Fi");
        SystemBackdrop = new MicaBackdrop();
        WindowStyle.Own(this, owner, 450, 860);
        var root = new Grid { Padding = new Thickness(24), RowSpacing = 16, Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = L.T("Подключение по Wi-Fi"), FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = L.T("Android 11+ · Компьютер и телефон должны быть в одной сети. На телефоне откройте «Для разработчиков → Беспроводная отладка» и включите её."), TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
        var discovery = new Grid { ColumnSpacing = 8 };
        discovery.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        discovery.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        discovery.Children.Add(_discovered); Grid.SetColumn(_refresh, 1); discovery.Children.Add(_refresh);
        panel.Children.Add(discovery); panel.Children.Add(_address);
        panel.Children.Add(new TextBlock { Text = L.T("Используйте IP и порт с главного экрана беспроводной отладки. После первого сопряжения достаточно добавить устройство."), TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.65 });
        var pairing = new StackPanel { Spacing = 12, Margin = new Thickness(0, 12, 0, 0) };
        pairing.Children.Add(new TextBlock { Text = L.T("На телефоне выберите «Сопряжение с помощью кода». Введите адрес и код из открывшегося окна. Порт сопряжения отличается от порта подключения."), TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        pairing.Children.Add(_pairAddress); pairing.Children.Add(_code); pairing.Children.Add(_pair);
        var expander = new Expander { Header = L.T("Первое подключение · сопряжение"), Content = pairing, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(expander); panel.Children.Add(_status);
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Grid.SetRow(_connect, 1); root.Children.Add(_connect);
        _discovered.SelectionChanged += (_, _) => { if (_discovered.SelectedItem is string address) _address.Text = address; };
        _refresh.Click += async (_, _) => await PerformAsync(RefreshAsync);
        _pair.Click += async (_, _) => await PerformAsync(async () =>
        {
            await _adb.PairWifiAsync(_pairAddress.Text, _code.Password);
            _code.Password = "";
            try { await RefreshAsync(); } catch { /* A successful pairing does not depend on mDNS. */ }
            _status.Text = L.T("Сопряжение выполнено. Проверьте адрес подключения и нажмите «Добавить устройство».");
        });
        _connect.Click += async (_, _) => await PerformAsync(async () =>
        {
            var serial = await _adb.ConnectWifiAsync(_address.Text);
            if (_closed) return;
            if (Connected != null) await Connected(serial);
            if (!_closed) Close();
        });
        Content = root;
        root.Loaded += async (_, _) => { WindowStyle.Apply(this, root); await PerformAsync(RefreshAsync); };
        Closed += (_, _) => { _closed = true; _code.Password = ""; };
    }
    private async Task RefreshAsync()
    {
        var addresses = await _adb.DiscoverWifiAsync();
        if (_closed) return;
        var selected = _address.Text;
        _discovered.ItemsSource = addresses;
        if (addresses.Contains(selected)) _discovered.SelectedItem = selected;
        else if (addresses.Count == 1 && string.IsNullOrWhiteSpace(selected)) _discovered.SelectedItem = addresses[0];
        _status.Text = addresses.Count == 0 ? L.T("Автопоиск не нашёл устройств. Можно ввести адрес с телефона вручную.") : L.T("Найден адрес беспроводной отладки. При первом подключении выполните сопряжение.");
    }
    private async Task PerformAsync(Func<Task> operation)
    {
        if (_busy || _closed) return;
        _busy = true; SetEnabled(false); _status.Text = L.T("Подождите…");
        try { await operation(); }
        catch (OperationCanceledException) { if (!_closed) _status.Text = L.T("Телефон не ответил. Проверьте сеть и адрес и попробуйте снова."); }
        catch (Exception ex) { if (!_closed) _status.Text = string.IsNullOrWhiteSpace(ex.Message) ? L.T("Не удалось подключиться. Проверьте адрес, код и сеть.") : ex.Message; }
        finally { _busy = false; if (!_closed) SetEnabled(true); }
    }
    private void SetEnabled(bool enabled)
    {
        foreach (var control in new Control[] { _refresh, _connect, _pair, _address, _pairAddress, _code, _discovered }) control.IsEnabled = enabled;
    }
}

internal sealed class NotificationWindow : Window
{
    private readonly DispatcherTimer _timer = new();
    public NotificationWindow(Window owner, string title, string message, string? path, bool error)
    {
        Title = L.T("Уведомление");
        var width = error ? 330 : 276;
        var height = error ? 94 : 54;
        WindowStyle.Own(this, owner, width, height);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
        }
        Native.MakeTransient(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var position = owner.AppWindow.Position;
        var size = owner.AppWindow.Size;
        var area = DisplayArea.GetFromWindowId(owner.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(Math.Clamp(position.X + (size.Width - 44 - width) / 2, area.X, Math.Max(area.X, area.X + area.Width - width)), Math.Clamp(position.Y + 44, area.Y, Math.Max(area.Y, area.Y + area.Height - height))));
        var layout = new Grid { ColumnSpacing = 10, Padding = new Thickness(14, 8, 12, 8) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new FontIcon { Glyph = error ? "\uE783" : "\uE73E", FontSize = 14, Foreground = new SolidColorBrush(error ? Windows.UI.Color.FromArgb(255, 247, 155, 150) : Windows.UI.Color.FromArgb(255, 141, 216, 184)) };
        layout.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 3 };
        text.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), TextTrimming = TextTrimming.CharacterEllipsis });
        if (error) text.Children.Add(new TextBlock { Text = message, FontSize = 11, MaxLines = 3, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 190, 193, 200)) });
        Grid.SetColumn(text, 1); layout.Children.Add(text);
        if (path != null)
        {
            var open = new Button { Content = new FontIcon { Glyph = "\uE8A7", FontSize = 13 }, Padding = new Thickness(4), MinWidth = 24, MinHeight = 24, BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), VerticalAlignment = VerticalAlignment.Center };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(open, L.T("Показать файл"));
            ToolTipService.SetToolTip(open, L.T("Показать файл · ") + message);
            open.Click += (_, _) =>
            {
                try
                {
                    var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                    start.ArgumentList.Add("/select,"); start.ArgumentList.Add(path);
                    Process.Start(start);
                }
                catch (Exception ex) { Log.Write(ex.ToString()); }
                Close();
            };
            Grid.SetColumn(open, 2); layout.Children.Add(open);
        }
        Content = new Border { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 35, 37, 42)), CornerRadius = new CornerRadius(6), Child = layout };
        _timer.Interval = TimeSpan.FromSeconds(error ? 7 : 3);
        _timer.Tick += (_, _) => Close();
        Closed += (_, _) => _timer.Stop();
    }
    public void ShowWithoutFocus()
    {
        AppWindow.Show(false);
        Native.MakeTransient(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _timer.Start();
    }
}
