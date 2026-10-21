using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Microsoft.UI.Windowing;
using Windows.Foundation;
using Windows.Graphics;

namespace AndroidDesk;

public sealed partial class MainWindow : Window
{
    private readonly AdbService _adb = new();
    private readonly ScrcpySession _session = new();
    private Preferences _preferences = Preferences.Load();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly SemaphoreSlim _screenshots = new(1, 1);
    private readonly nint _hwnd;
    private bool _polling, _busy, _closing, _allowClose, _userStopped;
    private bool _frameReady;
    private string? _serial, _selectedSerial, _failedSerial;
    private string? _fittedSerial;
    private IReadOnlyList<Device> _devices = [];
    private DateTime _recordStarted;
    private double _aspect = 0.45;
    private SettingsWindow? _settings;
    private DeviceWindow? _devicePicker;
    private NotificationWindow? _notification;
    private string? _lastError;
    internal static string Videos => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "AndroidDesk");

    public MainWindow()
    {
        InitializeComponent();
        Title = "Android Desk";
        SystemBackdrop = new MicaBackdrop();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        FitToPhone();
        AppWindow.Closing += async (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            if (_closing) return;
            _closing = true;
            _poll.Stop(); _clock.Stop();
            _settings?.Close(); _devicePicker?.Close(); _notification?.Close();
            await ExecuteAsync(async () => await StopAsync());
            _allowClose = true;
            Close();
        };
        Root.ActualThemeChanged += (_, _) => WindowStyle.Apply(this, Root);
        Root.Loaded += async (_, _) =>
        {
            WindowStyle.Apply(this, Root);
            Root.XamlRoot.Changed += (_, _) => PositionScreen();
            _poll.Start();
            await PollAsync();
        };
        _poll.Tick += async (_, _) => await PollAsync();
        _clock.Tick += (_, _) => RecordTime.Text = $"{DateTime.Now - _recordStarted:mm\\:ss}";
        _session.Exited += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_busy && !_closing) _ = ExecuteAsync(async () =>
            {
                _failedSerial = _serial;
                var recording = _session.RecordingPath;
                await StopAsync();
                if (recording == null) Notify("Трансляция завершена", "Нажмите кнопку подключения для повторного запуска.");
            });
        });
        UpdateControls();
    }
    private async Task ExecuteAsync(Func<Task> action)
    {
        await _operation.WaitAsync();
        _busy = true;
        UpdateControls();
        try { await action(); }
        catch (Exception ex)
        {
            Log.Write(ex.ToString());
            if (!_session.Running) { EmptyTitle.Text = "Не удалось подключиться"; EmptyHint.Text = ex.Message; }
            if (!_closing) Notify("Ошибка", ex.Message, error: true);
        }
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
            _devices = devices.Where(x => x.State == "device").ToList();
            _devicePicker?.UpdateDevices(_devices, _serial ?? _selectedSerial);
            if (_selectedSerial == null || !_devices.Any(x => x.Serial == _selectedSerial)) _selectedSerial = _devices.FirstOrDefault()?.Serial;
            if (_serial != null && !_devices.Any(x => x.Serial == _serial))
            {
                await ExecuteAsync(async () =>
                {
                    try { await StopAsync(); }
                    finally { _userStopped = false; }
                    Notify("Телефон отключён", "Ждём повторного подключения.");
                });
            }
            if (_failedSerial != null && !_devices.Any(x => x.Serial == _failedSerial)) _failedSerial = null;
            if (_devices.Count == 0 && !_session.Running)
            {
                EmptyTitle.Text = "Подключите телефон";
                EmptyHint.Text = devices.Any(x => x.State == "unauthorized") ? "Разрешите USB-отладку на экране телефона." : devices.Any(x => x.State == "offline") ? "Телефон offline. Проверьте USB-кабель или Wi-Fi." : "Включите USB-отладку и разрешите подключение. Выбор телефона — кнопка справа.";
            }
            else if (!_session.Running && !_userStopped && _preferences.AutoConnect && _selectedSerial is string serial && _failedSerial != serial)
            {
                await ExecuteAsync(async () =>
                {
                    try { await StartAsync(serial); }
                    catch { _failedSerial = serial; throw; }
                });
            }
            _lastError = null;
            UpdateControls();
        }
        catch (Exception ex)
        {
            Log.Write(ex.ToString());
            EmptyHint.Text = "ADB: " + ex.Message;
            if (_lastError != ex.Message) { _lastError = ex.Message; Notify("Ошибка ADB", ex.Message, error: true); }
        }
        finally { _polling = false; }
    }
    private async Task StartAsync(string serial, string? recording = null)
    {
        _serial = serial;
        _selectedSerial = serial;
        _frameReady = false;
        EmptyTitle.Text = "Подключаем экран…";
        try
        {
            await _session.StartAsync(_adb, serial, _preferences, recording, window =>
            {
                if (_preferences.External) return;
                Native.Embed(window, _hwnd);
                var scale = Root.XamlRoot?.RasterizationScale ?? 1;
                Native.SetWindowPos(window, 0, -32000, 0, Math.Max(1, (int)(Viewport.ActualWidth * scale)), Math.Max(1, (int)(Viewport.ActualHeight * scale)), 0x0010 | 0x0040 | 0x0020);
            });
            _frameReady = true;
            if (!_preferences.External)
            {
                PositionScreen();
            }
            FrozenFrame.Visibility = Visibility.Collapsed;
            Log.Write("Display: replacement frame ready");
            EmptyTitle.Text = "Экран в отдельном окне";
            EmptyHint.Text = "Чтобы вернуть экран сюда, выключите отдельное окно в настройках ⋯.";
            EmptyState.Visibility = _preferences.External ? Visibility.Visible : Visibility.Collapsed;
            if (recording != null) { _recordStarted = DateTime.Now; RecordTime.Text = "00:00"; _clock.Start(); }
            try
            {
                var size = Encoding.UTF8.GetString(await _adb.RunAsync(["-s", serial, "shell", "wm", "size"]));
                var matches = Regex.Matches(size, @"(\d+)x(\d+)");
                if (matches.Count > 0 && double.TryParse(matches[^1].Groups[1].Value, out var width) && double.TryParse(matches[^1].Groups[2].Value, out var height) && height > 0)
                {
                    _aspect = width / height;
                    if (!_preferences.External && _fittedSerial != serial)
                    {
                        _fittedSerial = serial;
                        FitToPhone();
                    }
                }
            }
            catch (Exception ex) { Log.Write("Screen dimensions: " + ex.Message); }
        }
        catch
        {
            try { await StopAsync(); } catch (Exception ex) { Log.Write(ex.ToString()); }
            throw;
        }
    }
    private async Task StopAsync()
    {
        var recording = _session.RecordingPath;
        if (!_closing && _session.Running && _serial != null && Native.GetParent(_session.Window) == _hwnd)
        {
            try { await FreezeFrameAsync(_serial); }
            catch (Exception ex) { Log.Write("Frame hold: " + ex.Message); }
        }
        _frameReady = false;
        try
        {
            await _session.StopAsync();
            if (recording != null && !_closing) NotifySaved("Запись сохранена", recording);
        }
        finally
        {
            _serial = null; _clock.Stop();
            FrozenFrame.Visibility = FrozenFrame.Source == null ? Visibility.Collapsed : Visibility.Visible;
            EmptyState.Visibility = FrozenFrame.Source == null ? Visibility.Visible : Visibility.Collapsed;
            EmptyTitle.Text = "Трансляция остановлена";
            EmptyHint.Text = "Подключиться — кнопка справа. Выбрать телефон — значок телефона.";
        }
    }
    private async Task FreezeFrameAsync(string serial)
    {
        var bytes = await _adb.CaptureFrameAsync(serial);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
        }
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        FrozenFrame.Source = bitmap;
        FrozenFrame.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        Log.Write("Display: holding frame");
    }
    private void FitToPhone()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var height = area.Height;
        var width = Math.Clamp((int)Math.Round((height - 32) * _aspect) + 60, 320, area.Width);
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y, width, height));
    }
    private void PositionScreen()
    {
        if (!_frameReady || _session.Window == 0 || Native.GetParent(_session.Window) != _hwnd || Root.XamlRoot == null) return;
        var point = Viewport.TransformToVisual(Root).TransformPoint(new Point());
        var scale = Root.XamlRoot.RasterizationScale;
        Native.SetWindowPos(_session.Window, 0, (int)Math.Round(point.X * scale), (int)Math.Round(point.Y * scale), Math.Max(1, (int)Math.Round(Viewport.ActualWidth * scale)), Math.Max(1, (int)Math.Round(Viewport.ActualHeight * scale)), 0x0010 | 0x0040 | 0x0020);
    }
    private void UpdateControls()
    {
        ConnectButton.IsEnabled = !_busy && (_session.Running || _selectedSerial != null);
        ToolTipService.SetToolTip(ConnectButton, _session.Running ? "Отключить" : "Подключить");
        foreach (var button in PhoneControls.Children.OfType<Button>()) button.IsEnabled = _devices.Any(x => x.Serial == (_serial ?? _selectedSerial));
        RecordButton.IsEnabled = !_busy && _session.Running;
        var recording = _session.RecordingPath != null;
        RecordButton.Content = recording ? "\uE71A" : "\uE7C8";
        AutomationProperties.SetName(RecordButton, recording ? "Остановить запись" : "Начать запись");
        ToolTipService.SetToolTip(RecordButton, recording ? "Остановить запись" : $"Запись {_preferences.RecordingFormat.ToUpperInvariant()}" + (_preferences.RecordAudio ? " со звуком" : " без звука"));
        RecordTime.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        DeviceButton.IsEnabled = !_closing;
        SettingsButton.IsEnabled = !_closing;
        ToolTipService.SetToolTip(DeviceButton, "Выбор устройства" + (_serial == null ? "" : " · " + _serial));
    }
    private void NotifySaved(string title, string path) => Notify(title, Path.GetFileName(path), path);
    private void Notify(string title, string message, string? path = null, bool error = false)
    {
        if (_closing) return;
        _notification?.Close();
        var window = new NotificationWindow(this, title, message, path, error);
        _notification = window;
        window.Closed += (_, _) => { if (_notification == window) _notification = null; };
        window.ShowWithoutFocus();
    }
    private async void ConnectClick(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (_session.Running) { _userStopped = true; await StopAsync(); }
        else if (_selectedSerial is string serial) { _userStopped = false; _failedSerial = null; await StartAsync(serial); }
    });
    private async void RecordClick(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (_serial == null || !_session.Running) return;
        var serial = _serial;
        var wasRecording = _session.RecordingPath != null;
        string? next = null;
        if (!wasRecording)
        {
            Directory.CreateDirectory(Videos);
            var extension = _preferences.RecordingFormat == "mkv" ? "mkv" : "mp4";
            next = Path.Combine(Videos, $"Recording_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.{extension}");
        }
        await StopAsync();
        try { await StartAsync(serial, next); }
        catch when (next != null)
        {
            // A failed audio encoder must not leave the phone disconnected or silently record without audio.
            await StartAsync(serial);
            throw new IOException("Не удалось начать запись. Трансляция восстановлена. Если Android не поддерживает захват звука, выключите звук записи в настройках. Подробности — в локальном журнале.");
        }
    });
    private async void KeyClick(object sender, RoutedEventArgs e)
    {
        var serial = _serial ?? _selectedSerial;
        if (serial == null || _closing) return;
        await _commands.WaitAsync();
        try { if (!_closing) await _adb.KeyAsync(serial, int.Parse((string)((Button)sender).Tag)); }
        catch (Exception ex) { Log.Write(ex.ToString()); Notify("Команда не выполнена", ex.Message, error: true); }
        finally { _commands.Release(); }
    }
    private async void ScreenshotClick(object sender, RoutedEventArgs e)
    {
        var serial = _serial ?? _selectedSerial;
        if (serial == null || _closing || !await _screenshots.WaitAsync(0)) return;
        try { NotifySaved("Скриншот сохранён", await _adb.ScreenshotAsync(serial)); }
        catch (Exception ex) { Log.Write(ex.ToString()); Notify("Скриншот не сохранён", ex.Message, error: true); }
        finally { _screenshots.Release(); }
    }
    private void ViewportChanged(object sender, SizeChangedEventArgs e) => PositionScreen();
    private void FitClick(object sender, RoutedEventArgs e) => FitToPhone();
    private void SettingsClick(object sender, RoutedEventArgs e)
    {
        if (_settings != null) { _settings.Activate(); return; }
        var window = new SettingsWindow(this, _preferences.Copy(), _session.RecordingPath != null);
        _settings = window;
        window.Closed += (_, _) => { if (_settings == window) _settings = null; };
        window.ApplyRequested = async preferences =>
        {
            var applied = false;
            await ExecuteAsync(async () =>
            {
                if (_session.RecordingPath != null) { Notify("Идёт запись", "Остановите запись перед изменением настроек."); return; }
                var serial = _serial;
                preferences.Save();
                _preferences = preferences;
                if (preferences.AutoConnect) { _userStopped = false; _failedSerial = null; }
                if (_session.Running && serial != null) { await StopAsync(); await StartAsync(serial); }
                applied = true;
            });
            return applied;
        };
        window.Activate();
    }
    private void DeviceClick(object sender, RoutedEventArgs e)
    {
        if (_devicePicker != null) { _devicePicker.Activate(); return; }
        var window = new DeviceWindow(this);
        _devicePicker = window;
        window.UpdateDevices(_devices, _serial ?? _selectedSerial);
        window.Closed += (_, _) => { if (_devicePicker == window) _devicePicker = null; };
        window.DeviceRequested = async serial =>
        {
            await ExecuteAsync(async () =>
            {
                if (_serial == serial && _session.Running) return;
                _userStopped = false; _failedSerial = null; _selectedSerial = serial;
                await StopAsync();
                await StartAsync(serial);
            });
        };
        window.Activate();
    }
}
