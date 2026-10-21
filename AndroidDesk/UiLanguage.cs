using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace AndroidDesk;

internal static class UiLanguage
{
    private static readonly ConditionalWeakTable<Window, object> Attached = new();
    public static void Attach(Window window, FrameworkElement root)
    {
        if (Attached.TryGetValue(window, out _)) return;
        Attached.Add(window, new object());
        void Refresh()
        {
            window.Title = L.Label(window.Title);
            root.Language = L.Language == "ru" ? "ru-RU" : "en-US";
            Translate(root);
        }
        Refresh();
        L.Changed += Refresh;
        window.Closed += (_, _) => L.Changed -= Refresh;
    }
    private static void Translate(DependencyObject element)
    {
        var name = AutomationProperties.GetName(element);
        if (name.Length > 0) AutomationProperties.SetName(element, L.Label(name));
        if (element is FrameworkElement framework && ToolTipService.GetToolTip(framework) is string tooltip)
            ToolTipService.SetToolTip(framework, L.Label(tooltip));
        // These contain user data or native device output. Localize only their captions.
        if (element is TextBox input)
        {
            if (input.Header is string header) input.Header = L.Label(header);
            input.PlaceholderText = L.Label(input.PlaceholderText);
            return;
        }
        if (element is PasswordBox password)
        {
            if (password.Header is string header) password.Header = L.Label(header);
            password.PlaceholderText = L.Label(password.PlaceholderText);
            return;
        }
        if (element is ListView) return;
        if (element is ComboBox combo)
        {
            if (combo.Header is string header) combo.Header = L.Label(header);
            combo.PlaceholderText = L.Label(combo.PlaceholderText);
            foreach (var item in combo.Items.OfType<ComboBoxItem>())
                if (item.Content is string text) item.Content = L.Label(text);
            return;
        }
        if (element is TextBlock label) label.Text = L.Label(label.Text);
        if (element is ToggleSwitch toggle)
        {
            if (toggle.Header is string header) toggle.Header = L.Label(header);
            if (toggle.OnContent is string on) toggle.OnContent = L.Label(on);
            if (toggle.OffContent is string off) toggle.OffContent = L.Label(off);
        }
        if (element is Expander expander && expander.Header is string expanderHeader) expander.Header = L.Label(expanderHeader);
        if (element is ContentControl content && content.Content is string value) content.Content = L.Label(value);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) Translate(VisualTreeHelper.GetChild(element, i));
    }
}
