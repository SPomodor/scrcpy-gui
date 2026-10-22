using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Streams;
using Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI.Windowing;
using Windows.Foundation;
using Windows.Graphics;

namespace AndroidDesk;

public sealed partial class MainWindow : Window
{
    private readonly AdbService _adb = new();
    private ScrcpySession _session = new();
    private Preferences _preferences = Preferences.Load();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _inputFocus = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly SemaphoreSlim _clipboardGate = new(1, 1);
    private ClipboardBridge? _clipboard;
    private string? _clipboardSerial, _lastClipboardText;
    private bool _phoneInputActive, _mouseWasDown;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly SemaphoreSlim _screenshots = new(1, 1);
    private readonly SemaphoreSlim _files = new(1, 1);
    private readonly CancellationTokenSource _filesShutdown = new();
    private readonly Dictionary<ScrcpySession, string> _nativeFileDrops = [];
    private int _pendingFileBatches;
    private string? _activeFile;
    private bool _filePickerOpen;
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
    private readonly QuickCommandStore _quickCommands = new();
    private CommandsWindow? _commandsWindow;
    private readonly CancellationTokenSource _commandShutdown = new();
    private readonly HashSet<Guid> _runningQuickCommands = [];
    private (string Name, string Serial, CommandResult Result)? _lastQuickResult;
    private NotificationWindow? _notification;
    private string? _lastError;
    internal static string Videos => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "AndroidDesk");

    public MainWindow()
    {
        InitializeComponent();
        Title = "Android Desk";
        SystemBackdrop = new MicaBackdrop();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SetInitialSize();
        AppWindow.Closing += async (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            if (_closing) return;
            _closing = true;
            _poll.Stop(); _clock.Stop(); _inputFocus.Stop();
            Clipboard.ContentChanged -= ClipboardChanged;
            _commandShutdown.Cancel();
            _filesShutdown.Cancel();
            L.Changed -= LanguageChanged;
            _quickCommands.Changed -= RebuildPinnedCommands;
            _settings?.Close(); _devicePicker?.Close(); _commandsWindow?.Close(); _notification?.Close();
            if (await _files.WaitAsync(TimeSpan.FromSeconds(3))) _files.Release();
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
            _inputFocus.Start();
            await PollAsync();
        };
        _poll.Tick += async (_, _) => await PollAsync();
        _inputFocus.Tick += (_, _) =>
        {
            var clicked = _frameReady && !_preferences.External && Native.PhoneClicked(_hwnd, _session.Window);
            if (clicked && !_mouseWasDown) { _phoneInputActive = true; Native.FocusEmbedded(_hwnd, _session.Window); }
            _mouseWasDown = clicked;
        };
        Root.PointerPressed += (_, e) => _phoneInputActive = e.GetCurrentPoint(Root).Position.X < Viewport.ActualWidth;
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) => ForwardHostKey(e, false)), true);
        Root.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler((_, e) => ForwardHostKey(e, true)), true);
        Root.CharacterReceived += (_, e) =>
        {
            if (!_preferences.PhysicalKeyboard && _phoneInputActive && _frameReady && !_preferences.External && Native.IsForeground(_hwnd))
            {
                Native.PostMessage(_session.Window, 0x0102, (nint)e.Character, 1);
                e.Handled = true;
            }
        };
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated && _phoneInputActive && _frameReady) Native.FocusEmbedded(_hwnd, _session.Window);
        };
        Clipboard.ContentChanged += ClipboardChanged;
        _clock.Tick += (_, _) => RecordTime.Text = $"{DateTime.Now - _recordStarted:mm\\:ss}";
        ObserveSession(_session);
        L.Changed += LanguageChanged;
        _quickCommands.Changed += RebuildPinnedCommands;
        RebuildPinnedCommands();
        UpdateControls();
    }
    private void ForwardHostKey(KeyRoutedEventArgs e, bool up)
    {
        // Cross-process child HWNDs may leave WinUI owning keyboard focus. Route only
        // host-delivered events, never hook global input or duplicate native SDL events.
        if (!_phoneInputActive || !_frameReady || _preferences.External || _session.Window == 0 || !Native.IsForeground(_hwnd)) return;
        Native.FocusEmbedded(_hwnd, _session.Window);
        Native.ForwardKey(_session.Window, (int)e.Key, e.KeyStatus.ScanCode, e.KeyStatus.IsExtendedKey, e.KeyStatus.WasKeyDown, up);
        e.Handled = true;
    }
    private void ObserveSession(ScrcpySession session)
    {
        session.FileDrop += update => DispatcherQueue.TryEnqueue(() => HandleNativeFileDrop(session, update));
        session.Exited += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (_nativeFileDrops.Remove(session, out var interrupted))
            {
                UpdateFileActivity();
                if (!_closing) Notify(L.T("Передача прервана"), L.F("Повторите передачу файла: {0}", Path.GetFileName(interrupted)), error: true);
            }
            if (ReferenceEquals(session, _session) && !_busy && !_closing) _ = ExecuteAsync(async () =>
            {
                _failedSerial = _serial;
                var recording = _session.RecordingPath;
                await StopAsync();
                if (recording == null) Notify(L.T("Трансляция завершена"), L.T("Нажмите кнопку подключения для повторного запуска."));
            });
        });
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
            if (!_session.Running) { EmptyTitle.Text = L.T("Не удалось подключиться"); EmptyHint.Text = ex.Message; }
            if (!_closing) Notify(L.T("Ошибка"), ex.Message, error: true);
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
                    Notify(L.T("Телефон отключён"), L.T("Ждём повторного подключения."));
                });
            }
            if (_failedSerial != null && !_devices.Any(x => x.Serial == _failedSerial)) _failedSerial = null;
            if (_devices.Count == 0 && !_session.Running)
            {
                EmptyTitle.Text = L.T("Подключите телефон");
                EmptyHint.Text = devices.Any(x => x.State == "unauthorized") ? L.T("Разрешите USB-отладку на экране телефона.") : devices.Any(x => x.State == "offline") ? L.T("Телефон offline. Проверьте USB-кабель или Wi-Fi.") : L.T("Включите USB-отладку и разрешите подключение. Выбор телефона — кнопка справа.");
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
            if (_lastError != ex.Message) { _lastError = ex.Message; Notify(L.T("Ошибка ADB"), ex.Message, error: true); }
        }
        finally { _polling = false; }
    }
    private async Task StartAsync(string serial, string? recording = null)
    {
        _serial = serial;
        _selectedSerial = serial;
        _frameReady = false;
        EmptyTitle.Text = L.T("Подключаем экран…");
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
            EmptyTitle.Text = L.T("Экран в отдельном окне");
            EmptyHint.Text = L.T("Чтобы вернуть экран сюда, выключите отдельное окно в настройках ⋯.");
            EmptyState.Visibility = _preferences.External ? Visibility.Visible : Visibility.Collapsed;
            if (recording != null) { _recordStarted = DateTime.Now; RecordTime.Text = "00:00"; _clock.Start(); }
            _ = EnsureClipboardAsync(serial);
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
    private async Task ReplaceSessionAsync(string serial, string? recording = null)
    {
        var previous = _session;
        var replacement = new ScrcpySession();
        var previousRecording = previous.RecordingPath;
        try
        {
            // The old renderer remains visible and interactive until the replacement has video.
            await replacement.StartAsync(_adb, serial, _preferences, recording, window =>
            {
                if (_preferences.External) return;
                Native.Embed(window, _hwnd);
                var scale = Root.XamlRoot?.RasterizationScale ?? 1;
                Native.SetWindowPos(window, 0, -32000, 0, Math.Max(1, (int)Math.Round(Viewport.ActualWidth * scale)), Math.Max(1, (int)Math.Round(Viewport.ActualHeight * scale)), 0x0010 | 0x0040 | 0x0020);
            });
        }
        catch
        {
            try { await replacement.StopAsync(); } catch (Exception ex) { Log.Write(ex.ToString()); }
            throw; // Keep the previous stream (and recording, if any) intact.
        }
        _session = replacement;
        ObserveSession(replacement);
        _serial = _selectedSerial = serial;
        _frameReady = true;
        // Render in the final rectangle behind the old HWND before changing z-order.
        PositionScreen(Native.GetParent(previous.Window) == _hwnd ? previous.Window : 0);
        await Task.Delay(120);
        PositionScreen();
        FrozenFrame.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = _preferences.External ? Visibility.Visible : Visibility.Collapsed;
        _clock.Stop();
        if (recording != null) { _recordStarted = DateTime.Now; RecordTime.Text = "00:00"; _clock.Start(); }
        Log.Write("Display: live renderer handed over without PNG replacement");
        await previous.StopAsync();
        if (_phoneInputActive && !_preferences.External) Native.FocusEmbedded(_hwnd, replacement.Window);
        _ = EnsureClipboardAsync(serial);
        if (previousRecording != null) NotifySaved(L.T("Запись сохранена"), previousRecording);
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
            if (recording != null && !_closing) NotifySaved(L.T("Запись сохранена"), recording);
        }
        finally
        {
            await StopClipboardAsync();
            _serial = null; _clock.Stop();
            FrozenFrame.Visibility = FrozenFrame.Source == null ? Visibility.Collapsed : Visibility.Visible;
            EmptyState.Visibility = FrozenFrame.Source == null ? Visibility.Visible : Visibility.Collapsed;
            EmptyTitle.Text = L.T("Трансляция остановлена");
            EmptyHint.Text = L.T("Подключиться — кнопка справа. Выбрать телефон — значок телефона.");
        }
    }
    private async void ClipboardChanged(object? sender, object e)
    {
        if (!_closing && _preferences.SharedClipboard && _clipboard != null) await SyncClipboardAsync();
    }
    private async Task EnsureClipboardAsync(string serial)
    {
        await _clipboardGate.WaitAsync();
        try
        {
            if (_closing || !_session.Running || _serial != serial) return;
            if (!_preferences.SharedClipboard)
            {
                if (_clipboard != null) await _clipboard.DisposeAsync();
                _clipboard = null; _clipboardSerial = null;
                return;
            }
            if (_clipboardSerial == serial && _clipboard?.Connected == true) return;
            if (_clipboard != null) await _clipboard.DisposeAsync();
            _clipboard = new ClipboardBridge(_adb, serial);
            _clipboardSerial = serial; _lastClipboardText = null;
            await _clipboard.ConnectAsync();
        }
        catch (Exception ex)
        {
            Log.Write("Clipboard bridge: " + ex.Message);
            if (!_closing) Notify(L.T("Буфер обмена недоступен"), ex.Message, error: true);
        }
        finally { _clipboardGate.Release(); }
        await SyncClipboardAsync();
    }
    private async Task SyncClipboardAsync()
    {
        await _clipboardGate.WaitAsync();
        try
        {
            if (_closing || !_session.Running || _serial != _clipboardSerial || !_preferences.SharedClipboard || _clipboard?.Connected != true) return;
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text)) return;
            var text = await content.GetTextAsync();
            if (text == _lastClipboardText) return;
            await _clipboard.SetTextAsync(text);
            _lastClipboardText = text;
        }
        catch (Exception ex) { Log.Write("Clipboard sync: " + ex.Message); }
        finally { _clipboardGate.Release(); }
    }
    private async Task StopClipboardAsync()
    {
        await _clipboardGate.WaitAsync();
        try
        {
            if (_clipboard != null) await _clipboard.DisposeAsync();
            _clipboard = null; _clipboardSerial = null; _lastClipboardText = null;
        }
        finally { _clipboardGate.Release(); }
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
    private void SetInitialSize()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var height = area.Height;
        var width = Math.Clamp((int)Math.Round((height - 32) * _aspect) + 60, 320, area.Width);
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y, width, height));
    }
    private void FitToPhone()
    {
        var position = AppWindow.Position;
        var height = AppWindow.Size.Height;
        if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Maximized) presenter.Restore();
        var frame = Native.NonClientSize(_hwnd);
        var scale = Root.XamlRoot?.RasterizationScale ?? Native.DpiScale(_hwnd);
        var clientHeight = Math.Max(1, height - frame.Height);
        var phoneAspect = _session.VideoAspect ?? _aspect;
        var width = (int)Math.Round(clientHeight * phoneAspect) + (int)Math.Round(44 * scale) + frame.Width;
        AppWindow.MoveAndResize(new RectInt32(position.X, position.Y, Math.Max(150, width), height));
        Log.Write($"Window fit: width={width}, height preserved={height}, aspect={phoneAspect:F6}");
    }
    private void PositionScreen(nint insertAfter = 0)
    {
        if (!_frameReady || _session.Window == 0 || Native.GetParent(_session.Window) != _hwnd || Root.XamlRoot == null) return;
        var point = Viewport.TransformToVisual(Root).TransformPoint(new Point());
        var scale = Root.XamlRoot.RasterizationScale;
        Native.SetWindowPos(_session.Window, insertAfter, (int)Math.Round(point.X * scale), (int)Math.Round(point.Y * scale), Math.Max(1, (int)Math.Round(Viewport.ActualWidth * scale)), Math.Max(1, (int)Math.Round(Viewport.ActualHeight * scale)), 0x0010 | 0x0040 | 0x0020);
    }
    private void UpdateControls()
    {
        ConnectButton.IsEnabled = !_busy && (_session.Running || _selectedSerial != null);
        ToolTipService.SetToolTip(ConnectButton, _session.Running ? L.T("Отключить") : L.T("Подключить"));
        foreach (var button in PhoneControls.Children.OfType<Button>()) button.IsEnabled = _devices.Any(x => x.Serial == (_serial ?? _selectedSerial));
        RecordButton.IsEnabled = !_busy && _session.Running;
        var recording = _session.RecordingPath != null;
        RecordButton.Content = recording ? "\uE71A" : "\uE7C8";
        AutomationProperties.SetName(RecordButton, recording ? L.T("Остановить запись") : L.T("Начать запись"));
        ToolTipService.SetToolTip(RecordButton, recording ? L.T("Остановить запись") : L.F("Запись {0}", _preferences.RecordingFormat.ToUpperInvariant()) + (_preferences.RecordAudio ? L.T(" со звуком") : L.T(" без звука")));
        RecordTime.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        DeviceButton.IsEnabled = !_closing;
        SettingsButton.IsEnabled = !_closing;
        ToolTipService.SetToolTip(DeviceButton, L.T("Выбор устройства") + (_serial == null ? "" : " · " + _serial));
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
        await ReplaceSessionAsync(serial, next);
    });
    private async void KeyClick(object sender, RoutedEventArgs e)
    {
        var serial = _serial ?? _selectedSerial;
        if (serial == null || _closing) return;
        await _commands.WaitAsync();
        try { if (!_closing) await _adb.KeyAsync(serial, int.Parse((string)((Button)sender).Tag)); }
        catch (Exception ex) { Log.Write(ex.ToString()); Notify(L.T("Команда не выполнена"), ex.Message, error: true); }
        finally { _commands.Release(); }
    }
    private async void ScreenshotClick(object sender, RoutedEventArgs e)
    {
        var serial = _serial ?? _selectedSerial;
        if (serial == null || _closing || !await _screenshots.WaitAsync(0)) return;
        try { NotifySaved(L.T("Скриншот сохранён"), await _adb.ScreenshotAsync(serial)); }
        catch (Exception ex) { Log.Write(ex.ToString()); Notify(L.T("Скриншот не сохранён"), ex.Message, error: true); }
        finally { _screenshots.Release(); }
    }
    private void ViewportChanged(object sender, SizeChangedEventArgs e) => PositionScreen();
    private void FitClick(object sender, RoutedEventArgs e) => FitToPhone();
    private void SettingsClick(object sender, RoutedEventArgs e)
    {
        if (_settings != null) { _settings.Activate(); return; }
        var window = new SettingsWindow(this, _preferences.Copy(), _session.RecordingPath != null);
        _settings = window;
        window.LanguageRequested = language =>
        {
            try
            {
                var previous = _preferences.Language;
                _preferences.Language = language;
                try { _preferences.Save(); }
                catch { _preferences.Language = previous; throw; }
                L.Set(language);
            }
            catch (Exception ex) { Notify(L.T("Ошибка"), ex.Message, error: true); }
        };
        window.Closed += (_, _) => { if (_settings == window) _settings = null; };
        window.ApplyRequested = async preferences =>
        {
            var applied = false;
            await ExecuteAsync(async () =>
            {
                if (_session.RecordingPath != null) { Notify(L.T("Идёт запись"), L.T("Остановите запись перед изменением настроек.")); return; }
                var serial = _serial;
                preferences.Save();
                _preferences = preferences;
                if (preferences.AutoConnect) { _userStopped = false; _failedSerial = null; }
                if (_session.Running && serial != null) await ReplaceSessionAsync(serial);
                applied = true;
            });
            return applied;
        };
        window.Activate();
    }
    private string? CommandDevice() => _devices.Any(x => x.Serial == (_serial ?? _selectedSerial)) ? _serial ?? _selectedSerial : null;
    private void FilesDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.AcceptedOperation = !_closing && CommandDevice() != null && e.DataView.Contains(StandardDataFormats.StorageItems) ? DataPackageOperation.Copy : DataPackageOperation.None;
        e.DragUIOverride.Caption = L.T(CommandDevice() == null ? "Сначала подключите и выберите телефон." : "APK → установить · файлы и папки → Download");
    }
    private async void FilesDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_closing || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var serial = CommandDevice();
        if (serial == null) return;
        var deferral = e.GetDeferral();
        string[] paths = [];
        try { paths = (await e.DataView.GetStorageItemsAsync()).Select(x => x.Path).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray(); }
        catch (Exception ex) { Notify(L.T("Не удалось прочитать файлы"), ex.Message, error: true); }
        finally { deferral.Complete(); }
        if (paths.Length > 0 && !_closing) await SendFilesAsync(paths, serial);
    }
    private async void FilesClick(object sender, RoutedEventArgs e)
    {
        if (_closing || _filePickerOpen) return;
        var serial = CommandDevice();
        if (serial == null) { Notify(L.T("Выберите телефон"), L.T("Сначала подключите и выберите телефон."), error: true); return; }
        _filePickerOpen = true; _phoneInputActive = false;
        string[] paths = [];
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
            paths = (await picker.PickMultipleFilesAsync()).Select(x => x.Path).ToArray();
        }
        catch (Exception ex) { if (!_closing) Notify(L.T("Не удалось прочитать файлы"), ex.Message, error: true); }
        finally { _filePickerOpen = false; }
        if (paths.Length > 0 && !_closing) await SendFilesAsync(paths, serial);
    }
    private async Task SendFilesAsync(IEnumerable<string> paths, string serial)
    {
        var files = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0 || _closing) return;
        _pendingFileBatches++; UpdateFileActivity();
        var entered = false; var succeeded = 0; var failed = 0; string? firstError = null;
        try
        {
            await _files.WaitAsync(_filesShutdown.Token); entered = true;
            foreach (var path in files)
            {
                _filesShutdown.Token.ThrowIfCancellationRequested();
                _activeFile = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)); UpdateFileActivity();
                var install = FileTransfers.IsApk(path);
                Notify(install ? L.T("Установка APK…") : L.T("Передача в Download…"), _activeFile);
                try
                {
                    await FileTransfers.SendAsync(_adb, serial, path, _filesShutdown.Token);
                    succeeded++;
                    if (!_closing && files.Length == 1) Notify(L.F(install ? "Установлено: {0}" : "Передано: {0}", _activeFile), serial);
                }
                catch (OperationCanceledException) when (_filesShutdown.IsCancellationRequested) { throw; }
                catch (Exception ex) { failed++; firstError ??= _activeFile + ": " + (ex is OperationCanceledException ? L.T("Телефон не ответил вовремя.") : ex.Message); }
            }
            if (!_closing && failed > 0) Notify(L.F("Готово: {0} · ошибок: {1}", succeeded, failed), firstError ?? "", error: true);
            else if (!_closing && files.Length > 1) Notify(L.F("Готово файлов: {0}", succeeded), serial);
        }
        catch (OperationCanceledException) when (_filesShutdown.IsCancellationRequested) { }
        catch (Exception ex) { if (!_closing) Notify(L.T("Передача не выполнена"), ex.Message, error: true); }
        finally
        {
            if (entered) { _activeFile = null; _files.Release(); }
            _pendingFileBatches--;
            if (!_closing) UpdateFileActivity();
        }
    }
    private void HandleNativeFileDrop(ScrcpySession session, FileDropUpdate update)
    {
        if (_closing) return;
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(update.Path));
        if (update.Stage == FileDropStage.Started)
        {
            _nativeFileDrops[session] = update.Path;
            Notify(update.Install ? L.T("Установка APK…") : L.T("Передача в Download…"), name);
        }
        else
        {
            _nativeFileDrops.Remove(session);
            if (update.Stage == FileDropStage.Completed) Notify(L.F(update.Install ? "Установлено: {0}" : "Передано: {0}", name), FileTransfers.Destination);
            else Notify(L.F("Не удалось: {0}", name), update.Error ?? L.T("Подробности в журнале app.log."), error: true);
        }
        UpdateFileActivity();
    }
    private void UpdateFileActivity()
    {
        var busy = _pendingFileBatches > 0 || _nativeFileDrops.Count > 0;
        FilesProgress.IsActive = busy;
        FilesProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        FilesIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        var file = _activeFile ?? _nativeFileDrops.Values.LastOrDefault();
        ToolTipService.SetToolTip(FilesButton, busy ? L.F("Передача: {0}", file == null ? "…" : Path.GetFileName(file)) : L.T("Установить APK или передать файлы"));
    }
    private void LanguageChanged()
    {
        UpdateControls();
        RebuildPinnedCommands();
        UpdateFileActivity();
    }
    private void CommandsClick(object sender, RoutedEventArgs e)
    {
        _phoneInputActive = false;
        if (_commandsWindow != null) { _commandsWindow.Activate(); return; }
        var window = new CommandsWindow(this, _adb, _quickCommands, CommandDevice);
        _commandsWindow = window;
        if (_lastQuickResult is { } last) window.DisplayResult(last.Name, last.Serial, last.Result);
        window.Closed += (_, _) => { if (_commandsWindow == window) _commandsWindow = null; };
        window.Activate();
    }
    private void RebuildPinnedCommands()
    {
        PinnedCommands.Children.Clear();
        foreach (var command in _quickCommands.Items.Where(x => x.Pinned))
        {
            var button = new Button
            {
                Style = (Style)Root.Resources["QuickButton"],
                Content = new TextBlock { Text = new string(command.Name.Where(char.IsLetterOrDigit).Take(2).ToArray()).ToUpperInvariant() is { Length: > 0 } abbreviation ? abbreviation : ">_", FontFamily = new FontFamily("Segoe UI"), FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                IsEnabled = !_runningQuickCommands.Contains(command.Id)
            };
            AutomationProperties.SetName(button, L.T("Выполнить: ") + command.Name);
            ToolTipService.SetToolTip(button, command.Name + "\n" + command.Command);
            button.Click += async (_, _) => await RunPinnedAsync(command);
            PinnedCommands.Children.Add(button);
        }
    }
    private async Task RunPinnedAsync(QuickCommand command)
    {
        if (_closing || !_runningQuickCommands.Add(command.Id)) return;
        RebuildPinnedCommands();
        try
        {
            var serial = CommandDevice() ?? throw new InvalidOperationException(L.T("Сначала подключите и выберите телефон."));
            var result = await CommandRunner.RunAsync(_adb, serial, command.Command, _commandShutdown.Token);
            if (_closing) return;
            _lastQuickResult = (command.Name, serial, result);
            _commandsWindow?.DisplayResult(command.Name, serial, result);
            if (result.ExitCode == 0) Notify(command.Name + L.T(" · выполнено"), L.T("Результат — в окне быстрых команд."));
            else Notify(command.Name + L.T(" · ошибка"), L.F("Код {0}. {1}", result.ExitCode, result.Output), error: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closing) Notify(L.T("Команда не выполнена"), ex.Message, error: true); }
        finally { _runningQuickCommands.Remove(command.Id); if (!_closing) RebuildPinnedCommands(); }
    }
    private void DeviceClick(object sender, RoutedEventArgs e)
    {
        if (_devicePicker != null) { _devicePicker.Activate(); return; }
        var window = new DeviceWindow(this, _adb);
        _devicePicker = window;
        window.UpdateDevices(_devices, _serial ?? _selectedSerial);
        window.Closed += (_, _) => { if (_devicePicker == window) _devicePicker = null; };
        window.DeviceRequested = async serial =>
        {
            await ExecuteAsync(async () =>
            {
                if (_serial == serial && _session.Running) return;
                _userStopped = false; _failedSerial = null; _selectedSerial = serial;
                if (_session.Running) await ReplaceSessionAsync(serial);
                else await StartAsync(serial);
            });
        };
        window.Activate();
    }
}
