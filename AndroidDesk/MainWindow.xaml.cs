using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;

namespace AndroidDesk;

public sealed partial class MainWindow : Window
{
    private readonly AdbService _adb = new();
    private readonly ScrcpySession _session = new();
    private readonly Preferences _preferences = Preferences.Load();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly nint _hwnd;
    private bool _polling, _busy, _closing, _allowClose, _updatingDevices, _userStopped;
    private bool _initializing = true;
    private string? _serial, _failedSerial;
    private DateTime _recordStarted;
    private string? _lastMedia;
    private string _deviceSignature = "";
    private static string Videos => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "AndroidDesk");

    public MainWindow()
    {
        InitializeComponent();
        Title = "Android Desk";
        SystemBackdrop = new MicaBackdrop();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.Resize(new SizeInt32(1180, 860));
        AppWindow.Closing += async (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            if (_closing) return;
            _closing = true;
            _poll.Stop();
            _clock.Stop();
            await ExecuteAsync(async () => { await StopAsync(); });
            _allowClose = true;
            Close();
        };
        SetChoice(Resolution, _preferences.MaxSize);
        SetChoice(FrameRate, _preferences.Fps);
        Bitrate.Value = Math.Clamp(_preferences.Bitrate, 1, 64);
        ExternalWindow.IsOn = _preferences.External;
        AutoConnect.IsOn = _preferences.AutoConnect;
        _initializing = false;
        Root.ActualThemeChanged += (_, _) => UpdateTitleBar();
        Root.Loaded += async (_, _) =>
        {
            UpdateTitleBar();
            Root.XamlRoot.Changed += (_, _) => PositionScreen();
            _poll.Start();
            await PollAsync();
        };
        _poll.Tick += async (_, _) => await PollAsync();
        _clock.Tick += (_, _) => RecordTime.Text = $"● REC  {DateTime.Now - _recordStarted:mm\\:ss}";
        _session.Exited += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_busy && !_closing) _ = ExecuteAsync(async () =>
            {
                _failedSerial = _serial;
                var recording = _session.RecordingPath;
                await StopAsync();
                Status.Text = recording == null ? "Трансляция завершена. Нажмите «Подключить» для запуска." : "Запись завершена: " + recording;
            });
        });
        UpdateControls();
    }
    private void UpdateTitleBar()
    {
        var dark = Root.ActualTheme == ElementTheme.Dark;
        var background = dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243);
        var foreground = dark ? Windows.UI.Color.FromArgb(255, 245, 245, 245) : Windows.UI.Color.FromArgb(255, 25, 25, 25);
        AppWindow.TitleBar.BackgroundColor = background;
        AppWindow.TitleBar.ForegroundColor = foreground;
        AppWindow.TitleBar.ButtonBackgroundColor = background;
        AppWindow.TitleBar.ButtonForegroundColor = foreground;
        AppWindow.TitleBar.InactiveBackgroundColor = background;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = background;
    }
    private static void SetChoice(ComboBox box, int value)
    {
        foreach (ComboBoxItem item in box.Items) if (int.Parse((string)item.Tag) == value) { box.SelectedItem = item; return; }
    }
    private void ReadPreferences()
    {
        _preferences.MaxSize = int.Parse((string)((ComboBoxItem)Resolution.SelectedItem).Tag);
        _preferences.Fps = int.Parse((string)((ComboBoxItem)FrameRate.SelectedItem).Tag);
        _preferences.Bitrate = double.IsFinite(Bitrate.Value) ? (int)Math.Clamp(Math.Round(Bitrate.Value), 1, 64) : 8;
        Bitrate.Value = _preferences.Bitrate;
        _preferences.External = ExternalWindow.IsOn;
        _preferences.AutoConnect = AutoConnect.IsOn;
        _preferences.Save();
    }
    private async Task ExecuteAsync(Func<Task> action)
    {
        await _operation.WaitAsync();
        _busy = true;
        UpdateControls();
        try { await action(); }
        catch (Exception ex) { Status.Text = "Ошибка: " + ex.Message; Log.Write(ex.ToString()); }
        finally { _busy = false; UpdateControls(); _operation.Release(); }
    }
    private async Task PollAsync()
    {
        if (_polling || _closing || _busy) return;
        _polling = true;
        try
        {
            var devices = await _adb.DevicesAsync();
            if (_closing || _busy) return;
            var ready = devices.Where(x => x.State == "device").ToList();
            var signature = string.Join('|', ready.Select(x => x.Serial + x.Model));
            if (signature != _deviceSignature)
            {
                var selected = (Devices.SelectedItem as Device)?.Serial;
                _deviceSignature = signature;
                _updatingDevices = true;
                Devices.ItemsSource = ready;
                Devices.SelectedItem = ready.FirstOrDefault(x => x.Serial == selected) ?? ready.FirstOrDefault();
                _updatingDevices = false;
            }
            if (_serial != null && !ready.Any(x => x.Serial == _serial))
            {
                await ExecuteAsync(async () => { await StopAsync(); Status.Text = "Телефон отключён. Ожидаем повторное подключение…"; });
                _userStopped = false;
            }
            if (_failedSerial != null && !ready.Any(x => x.Serial == _failedSerial)) _failedSerial = null;
            if (ready.Count == 0)
            {
                Status.Text = devices.Any(x => x.State == "unauthorized") ? "Разрешите USB-отладку на экране телефона." : devices.Any(x => x.State == "offline") ? "Телефон offline. Проверьте кабель или Wi-Fi." : "Телефон не найден. Включите USB-отладку и подключите устройство.";
            }
            else if (!_session.Running && !_userStopped && AutoConnect.IsOn && Devices.SelectedItem is Device device && _failedSerial != device.Serial)
            {
                await ExecuteAsync(async () =>
                {
                    try { ReadPreferences(); await StartAsync(device.Serial); }
                    catch { _failedSerial = device.Serial; throw; }
                });
            }
            UpdateControls();
        }
        catch (Exception ex) { Status.Text = "ADB: " + ex.Message; Log.Write(ex.ToString()); }
        finally { _polling = false; }
    }
    private async Task StartAsync(string serial, string? recording = null)
    {
        Status.Text = "Подключаем экран…";
        _serial = serial;
        try
        {
            await _session.StartAsync(_adb, serial, _preferences, recording);
            if (!_preferences.External) { Native.Embed(_session.Window, _hwnd); PositionScreen(); }
            EmptyTitle.Text = "Экран в отдельном окне";
            EmptyHint.Text = "Управляйте телефоном в окне scrcpy. Кнопки и запись доступны справа.";
            EmptyState.Visibility = _preferences.External ? Visibility.Visible : Visibility.Collapsed;
            if (recording != null) { _recordStarted = DateTime.Now; _clock.Start(); }
            Status.Text = $"{serial} · H.264 · до {_preferences.Fps} FPS · {_preferences.Bitrate} Мбит/с";
        }
        catch
        {
            try { await StopAsync(); } catch (Exception ex) { Log.Write(ex.ToString()); }
            throw;
        }
    }
    private async Task StopAsync()
    {
        try { await _session.StopAsync(); }
        finally
        {
            _serial = null;
            _clock.Stop();
            RecordTime.Text = "MKV · без звука";
            EmptyState.Visibility = Visibility.Visible;
            EmptyTitle.Text = "Телефон на вашем компьютере";
            EmptyHint.Text = "Выберите устройство и нажмите «Подключить». Для ввода мышью и клавиатурой щёлкните по экрану телефона.";
        }
    }
    private void PositionScreen()
    {
        if (_session.Window == 0 || Native.GetParent(_session.Window) != _hwnd || Root.XamlRoot == null) return;
        var point = Viewport.TransformToVisual(Root).TransformPoint(new Point());
        var scale = Root.XamlRoot.RasterizationScale;
        Native.SetWindowPos(_session.Window, 0, (int)Math.Round(point.X * scale), (int)Math.Round(point.Y * scale), Math.Max(1, (int)Math.Round(Viewport.ActualWidth * scale)), Math.Max(1, (int)Math.Round(Viewport.ActualHeight * scale)), 0x0010 | 0x0040 | 0x0020);
    }
    private void UpdateControls()
    {
        BusyRing.IsActive = _busy;
        ConnectButton.IsEnabled = !_busy && (_session.Running || Devices.SelectedItem != null);
        ConnectButton.Content = _session.Running ? "Отключить" : "Подключить";
        SetEnabled(PhoneControls, !_busy && _session.Running);
        RecordButton.IsEnabled = !_busy && _session.Running;
        RecordLabel.Text = _session.RecordingPath == null ? "Начать запись" : "Остановить запись";
        RecordIcon.Glyph = _session.RecordingPath == null ? "\uE7C8" : "\uE71A";
        SetEnabled(QualityControls, !_busy && _session.RecordingPath == null);
        Devices.IsEnabled = !_busy && !_session.Running;
    }
    private static void SetEnabled(DependencyObject parent, bool enabled)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is Control control) control.IsEnabled = enabled;
            else SetEnabled(child, enabled);
        }
    }
    private async void ConnectClick(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (_session.Running) { _userStopped = true; await StopAsync(); Status.Text = "Трансляция остановлена."; }
        else if (Devices.SelectedItem is Device device) { _userStopped = false; _failedSerial = null; ReadPreferences(); await StartAsync(device.Serial); }
    });
    private async void ApplyClick(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        var serial = _serial;
        ReadPreferences();
        if (_session.Running && serial != null) { await StopAsync(); await StartAsync(serial); }
        else Status.Text = "Настройки сохранены.";
    });
    private async void RecordClick(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (_serial == null || !_session.Running) return;
        var serial = _serial;
        var oldRecording = _session.RecordingPath;
        string? next = null;
        if (oldRecording == null)
        {
            Directory.CreateDirectory(Videos);
            next = Path.Combine(Videos, $"Recording_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.mkv");
        }
        await StopAsync();
        if (oldRecording != null) _lastMedia = oldRecording;
        await StartAsync(serial, next);
        if (oldRecording != null) Status.Text = "Запись сохранена: " + oldRecording;
    });
    private async void KeyClick(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (_serial != null) await _adb.KeyAsync(_serial, int.Parse((string)((Button)sender).Tag));
    });
    private async void ScreenshotClick(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (_serial != null) { _lastMedia = await _adb.ScreenshotAsync(_serial); Status.Text = "Скриншот сохранён: " + _lastMedia; }
    });
    private void ViewportChanged(object sender, SizeChangedEventArgs e) => PositionScreen();
    private void DeviceChanged(object sender, SelectionChangedEventArgs e) { if (!_updatingDevices) { _userStopped = false; _failedSerial = null; } }
    private void AutoConnectChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing || AutoConnect == null) return;
        _preferences.AutoConnect = AutoConnect.IsOn;
        if (AutoConnect.IsOn) { _userStopped = false; _failedSerial = null; }
    }
    private async void OpenVideosClick(object sender, RoutedEventArgs e) => await ExecuteAsync(() => { Directory.CreateDirectory(Videos); Process.Start(new ProcessStartInfo(Videos) { UseShellExecute = true }); return Task.CompletedTask; });
    private async void OpenLogClick(object sender, RoutedEventArgs e) => await ExecuteAsync(() => { Directory.CreateDirectory(Log.Folder); Process.Start(new ProcessStartInfo(Log.Folder) { UseShellExecute = true }); return Task.CompletedTask; });
}
