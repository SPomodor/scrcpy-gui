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
    private readonly ComboBox _resolution = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _fps = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _format = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumberBox _bitrate = new() { Minimum = 1, Maximum = 64, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly ToggleSwitch _audio = new() { Header = "Звук в записи", OnContent = "AAC · звук телефона", OffContent = "Без звука" };
    private readonly ToggleSwitch _external = new() { Header = "Отдельное окно scrcpy", OnContent = "Отдельное окно", OffContent = "Экран внутри приложения" };
    private readonly ToggleSwitch _auto = new() { Header = "Автоподключение", OnContent = "Включено", OffContent = "Выключено" };
    public Func<Preferences, Task<bool>>? ApplyRequested { get; set; }
    public SettingsWindow(Window owner, Preferences preferences, bool recording)
    {
        Title = "Настройки";
        SystemBackdrop = new MicaBackdrop();
        WindowStyle.Own(this, owner, 410, 810);
        var panel = new StackPanel { Spacing = 16, Padding = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Настройки", FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        if (recording) panel.Children.Add(new TextBlock { Text = "Для изменения настроек сначала остановите запись.", TextWrapping = TextWrapping.Wrap });
        AddChoices(_resolution, [("720 px", 720), ("960 px", 960), ("1280 px", 1280), ("1600 px", 1600), ("1920 px", 1920), ("2560 px", 2560), ("Оригинальное", 0)], preferences.MaxSize);
        AddChoices(_fps, [("30 FPS", 30), ("60 FPS", 60), ("90 FPS", 90), ("120 FPS", 120)], preferences.Fps);
        _format.Items.Add(new ComboBoxItem { Content = "MP4 · H.264 / AAC", Tag = "mp4" });
        _format.Items.Add(new ComboBoxItem { Content = "MKV · H.264 / AAC", Tag = "mkv" });
        _format.SelectedIndex = preferences.RecordingFormat == "mkv" ? 1 : 0;
        _bitrate.Value = preferences.Bitrate;
        _audio.IsOn = preferences.RecordAudio;
        _external.IsOn = preferences.External;
        _auto.IsOn = preferences.AutoConnect;
        foreach (var element in new Control[] { _resolution, _fps, _format, _bitrate, _audio, _external, _auto }) element.IsEnabled = !recording;
        AddField(panel, "Разрешение · длинная сторона", _resolution);
        AddField(panel, "Частота кадров", _fps);
        AddField(panel, "Битрейт, Мбит/с", _bitrate);
        AddField(panel, "Формат записи", _format);
        panel.Children.Add(_audio); panel.Children.Add(_external); panel.Children.Add(_auto);
        panel.Children.Add(new TextBlock { Text = "Применение настроек и переключение записи ненадолго перезапускают трансляцию. Захват звука требует Android 11+; на Android 11 разблокируйте телефон.", FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        var apply = new Button { Content = "Применить", HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = !recording };
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
                if (ApplyRequested != null && await ApplyRequested(preferences)) Close();
            }
            finally { apply.IsEnabled = !recording; }
        };
        panel.Children.Add(apply);
        var videos = new Button { Content = "Открыть видеозаписи", HorizontalAlignment = HorizontalAlignment.Stretch };
        videos.Click += (_, _) => { try { WindowStyle.OpenFolder(MainWindow.Videos); } catch (Exception ex) { Log.Write(ex.ToString()); } };
        panel.Children.Add(videos);
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
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single, DisplayMemberPath = "PickerLabel", MinHeight = 100 };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.65 };
    private readonly Button _connect = new() { Content = "Подключить", HorizontalAlignment = HorizontalAlignment.Stretch };
    private string _signature = "";
    public Func<string, Task>? DeviceRequested { get; set; }
    public DeviceWindow(Window owner)
    {
        Title = "Выбор телефона";
        SystemBackdrop = new MicaBackdrop();
        WindowStyle.Own(this, owner, 440, 400);
        var panel = new StackPanel { Spacing = 18, Padding = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Телефоны", FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(_hint); panel.Children.Add(_list); panel.Children.Add(_connect);
        _list.SelectionChanged += (_, _) => _connect.IsEnabled = _list.SelectedItem != null;
        _connect.Click += async (_, _) =>
        {
            if (_list.SelectedItem is not Device device || DeviceRequested == null) return;
            _connect.IsEnabled = false;
            await DeviceRequested(device.Serial);
            Close();
        };
        var root = new ScrollViewer { Content = panel, Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] };
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
        _hint.Text = devices.Count == 0 ? "Устройства не найдены. Подключите телефон и разрешите отладку. Список обновляется автоматически." : "Выберите устройство. Текущая трансляция и запись будут остановлены при переключении.";
        _connect.IsEnabled = _list.SelectedItem != null;
    }
}

internal sealed class NotificationWindow : Window
{
    private readonly DispatcherTimer _timer = new();
    public NotificationWindow(Window owner, string title, string message, string? path, bool error)
    {
        Title = "Уведомление";
        WindowStyle.Own(this, owner, 350, 132);
        if (AppWindow.Presenter is OverlappedPresenter presenter) { presenter.SetBorderAndTitleBar(false, false); presenter.IsResizable = false; }
        var position = owner.AppWindow.Position;
        var size = owner.AppWindow.Size;
        var area = DisplayArea.GetFromWindowId(owner.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(Math.Clamp(position.X + (size.Width - 350) / 2, area.X, Math.Max(area.X, area.X + area.Width - 350)), Math.Clamp(position.Y + size.Height - 160, area.Y, Math.Max(area.Y, area.Y + area.Height - 132))));
        var panel = new StackPanel { Spacing = 6, Padding = new Thickness(16, 10, 16, 10) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(error ? Microsoft.UI.Colors.Salmon : Microsoft.UI.Colors.LightGreen) });
        panel.Children.Add(new TextBlock { Text = message, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) });
        if (path != null)
        {
            var open = new Button { Content = "Показать файл", Padding = new Thickness(10, 3, 10, 3), FontSize = 12 };
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
            panel.Children.Add(open);
        }
        Content = new Border { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 38, 42, 48)), BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 65, 73, 85)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = panel };
        _timer.Interval = TimeSpan.FromSeconds(error ? 8 : 4);
        _timer.Tick += (_, _) => Close();
        Closed += (_, _) => _timer.Stop();
    }
    public void ShowWithoutFocus() { AppWindow.Show(false); _timer.Start(); }
}
