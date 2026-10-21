using System.Globalization;
using System.Text.Json;

namespace AndroidDesk;

internal static class L
{
    private static readonly Dictionary<string, string> English = LoadEnglish();
    private static readonly Dictionary<string, string> Russian = English.GroupBy(x => x.Value, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.First().Key, StringComparer.Ordinal);
    public static string Language { get; private set; } = "ru";
    public static string SystemLanguage { get; set; } = CultureInfo.CurrentUICulture.Name;
    public static event Action? Changed;
    public static string Resolve(string preference, string systemLanguage) => preference is "ru" or "en" ? preference :
        systemLanguage.StartsWith("ru", StringComparison.OrdinalIgnoreCase) ? "ru" : "en";
    public static void Set(string preference)
    {
        var language = Resolve(preference, SystemLanguage);
        if (Language == language) return;
        Language = language;
        Changed?.Invoke();
    }
    public static string T(string text) => Language == "en" && English.TryGetValue(text, out var english) ? english : text;
    public static string F(string text, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(text), args);
    // Used only for app-owned labels. User-entered text and device output are never translated.
    public static string Label(string text)
    {
        if (English.ContainsKey(text)) return T(text);
        return Russian.TryGetValue(text, out var russian) ? T(russian) : text;
    }
    private static Dictionary<string, string> LoadEnglish()
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream("AndroidDesk.Strings.en.json") ?? throw new IOException("English language resources are missing.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }
}
