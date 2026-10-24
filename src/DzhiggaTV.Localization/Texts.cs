using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DzhiggaTV.Localization;

public interface ILocalizedViewModel { void RefreshLanguage(); }

/// <summary>Offline UI messages. Imported channel names and programme text never pass through this catalog.</summary>
public static class Texts
{
    public sealed record Translation(string En, string Zh);
    private sealed record Message(string Key, object?[] Arguments);
    private static readonly ConditionalWeakTable<string, Message> Rendered = new();
    private static readonly AsyncLocal<string?> RequestLanguage = new();
    private static readonly IReadOnlyDictionary<string, Translation> Catalog = Load();
    private static readonly IReadOnlyDictionary<string, string> Ids = Catalog.Keys.ToDictionary(IdForKey, StringComparer.Ordinal);
    private static string _language = "ru";
    public static event EventHandler? LanguageChanged;
    public static string Language => RequestLanguage.Value ?? Volatile.Read(ref _language);
    public static string DefaultLanguage => Volatile.Read(ref _language);
    public static CultureInfo Culture => CultureFor(Language);
    private static CultureInfo CultureFor(string code) => CultureInfo.GetCultureInfo(code switch { "en" => "en-US", "zh-Hans" => "zh-CN", _ => "ru-RU" });
    public static IReadOnlyDictionary<string, Translation> Messages => Catalog;
    public static string IdForKey(string key) => "m" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
    public static string KeyFromId(string id) => Ids.GetValueOrDefault(id, id);
    public static string Normalize(string? language) => language?.ToLowerInvariant() switch
    { "en" or "en-us" or "en-gb" => "en", "zh" or "zh-cn" or "zh-hans" or "zh-sg" => "zh-Hans", _ => "ru" };
    public static void SetLanguage(string? language)
    {
        var code = Normalize(language);
        bool changed = Volatile.Read(ref _language) != code;
        Volatile.Write(ref _language, code);
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture = CultureFor(code);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureFor(code);
        if (changed) LanguageChanged?.Invoke(null, EventArgs.Empty);
    }
    public static IDisposable UseLanguage(string? language)
    { var previous = RequestLanguage.Value; RequestLanguage.Value = Normalize(language); return new LanguageScope(previous); }
    private sealed class LanguageScope(string? previous) : IDisposable
    { public void Dispose() => RequestLanguage.Value = previous; }
    public static string T(string key) => Render(new(key, []));
    public static string F(string key, params object?[] arguments) => Render(new(key, arguments));
    public static string Refresh(string value) => Rendered.TryGetValue(value, out var message) ? Render(message) : value;
    public static bool IsMessage(string value) => Rendered.TryGetValue(value, out _);
    private static string Render(Message message)
    {
        string template = Catalog.TryGetValue(message.Key, out var entry) ? Language switch { "en" => entry.En, "zh-Hans" => entry.Zh, _ => message.Key } : message.Key;
        var text = message.Arguments.Length == 0 ? template : string.Format(Culture, template,
            message.Arguments.Select(a => a is string s ? Refresh(s) : a).ToArray());
        if (text.Length == 0) return text;
        // Keep provenance by identity, never by matching the contents of user/provider text.
        var result = new string(text.AsSpan()); Rendered.Add(result, message); return result;
    }
    private static IReadOnlyDictionary<string, Translation> Load()
    {
        using var stream = typeof(Texts).Assembly.GetManifestResourceStream("DzhiggaTV.Localization.Messages.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, Translation>>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}
