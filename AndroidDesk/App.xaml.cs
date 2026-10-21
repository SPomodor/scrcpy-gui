using Microsoft.UI.Xaml;

namespace AndroidDesk;

public partial class App : Application
{
    private Window? _window;
    public App()
    {
        var languages = Windows.System.UserProfile.GlobalizationPreferences.Languages;
        if (languages.Count > 0) L.SystemLanguage = languages[0];
        L.Set(Preferences.Load().Language);
        InitializeComponent();
        UnhandledException += (_, e) => Log.Write(e.Exception.ToString());
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
