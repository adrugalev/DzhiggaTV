using L = DzhiggaTV.Localization.Texts;
using System.Globalization;

namespace DzhiggaTV.Core;

public sealed record CatchupSource(int Days, string Mode, string? Template = null)
{
    public DateTimeOffset Earliest(DateTimeOffset now) => now.AddDays(-Days);
    public bool Contains(DateTimeOffset time, DateTimeOffset now) => time >= Earliest(now) && time < now;
}

/// <summary>Provider-declared catchup. Unrelated signed parameters remain unchanged.</summary>
public static class Catchup
{
    public static CatchupSource? From(Channel channel)
    {
        string? Attribute(string key) => channel.Attributes?.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
        string? mode = Attribute("catchup")?.ToLowerInvariant(), template = Attribute("catchup-source");
        if (mode is "none" or "disabled" or "off" or "0") return null;
        if (!int.TryParse(Attribute("catchup-days") ?? Attribute("tvg-rec") ?? Attribute("timeshift"), out int days) || days <= 0 || days > 365) return null;
        mode ??= string.IsNullOrWhiteSpace(template) ? "shift" : "default";
        if (mode is not ("shift" or "default" or "append" or "flussonic")) return null;
        if (!Uri.TryCreate(channel.Url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        if (mode is "default" or "append" && string.IsNullOrWhiteSpace(template)) return null;
        return new(days, mode, template);
    }
    public static bool CanPlay(Channel channel, Programme programme, DateTimeOffset now) =>
        programme.End > programme.Start && From(channel) is { } source && source.Contains(programme.Start, now);

    public static string BuildUrl(Channel channel, DateTimeOffset start, DateTimeOffset end, DateTimeOffset now)
    {
        var source = From(channel) ?? throw new InvalidOperationException(L.T("У канала нет поддерживаемого архива."));
        if (!source.Contains(start, now) || end <= start || end > now.AddSeconds(1)) throw new ArgumentOutOfRangeException(nameof(start), L.T("Выбранное время недоступно в архиве."));
        static string Number(long n) => n.ToString(CultureInfo.InvariantCulture);
        string utc = Number(start.ToUnixTimeSeconds()), current = Number(now.ToUnixTimeSeconds()), duration = Number(Math.Max(1, (long)(end - start).TotalSeconds));
        string url = channel.Url.Trim();
        if (source.Mode == "shift")
        {
            int fragment = url.IndexOf('#'); string suffix = fragment >= 0 ? url[fragment..] : "";
            if (fragment >= 0) url = url[..fragment];
            int query = url.IndexOf('?');
            string[] parameters = query < 0 ? [] : url[(query + 1)..].Split('&').Where(p => !p.Split('=')[0].Equals("utc", StringComparison.OrdinalIgnoreCase) && !p.Split('=')[0].Equals("lutc", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (query >= 0) url = url[..query];
            return url + "?" + string.Join("&", parameters.Where(p => p.Length > 0).Concat(["utc=" + utc, "lutc=" + current])) + suffix;
        }
        if (source.Mode == "flussonic")
        {
            int query = url.IndexOfAny(['?', '#']); string suffix = query >= 0 ? url[query..] : ""; string path = query >= 0 ? url[..query] : url;
            if (!path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(L.T("Формат архива канала не поддерживается."));
            return path[..(path.LastIndexOf('/') + 1)] + $"index-{utc}-{duration}.m3u8" + suffix;
        }
        string formatted = source.Template!;
        foreach (var (token, value) in new[] { ("${start}", utc), ("${now}", current), ("${timestamp}", current), ("${end}", Number(end.ToUnixTimeSeconds())), ("${duration}", duration),
            ("{utc}", utc), ("{lutc}", current), ("{utcend}", Number(end.ToUnixTimeSeconds())), ("{duration}", duration),
            ("{Y}", start.UtcDateTime.ToString("yyyy", CultureInfo.InvariantCulture)), ("{m}", start.UtcDateTime.ToString("MM", CultureInfo.InvariantCulture)), ("{d}", start.UtcDateTime.ToString("dd", CultureInfo.InvariantCulture)),
            ("{H}", start.UtcDateTime.ToString("HH", CultureInfo.InvariantCulture)), ("{M}", start.UtcDateTime.ToString("mm", CultureInfo.InvariantCulture)), ("{S}", start.UtcDateTime.ToString("ss", CultureInfo.InvariantCulture)) }) formatted = formatted.Replace(token, value, StringComparison.Ordinal);
        if (formatted.Contains('{') || formatted.Contains('}')) throw new InvalidOperationException(L.T("Шаблон архива содержит неподдерживаемые параметры."));
        string result = source.Mode == "append" ? url + formatted : formatted;
        if (!Uri.TryCreate(result, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https")) throw new InvalidOperationException(L.T("Некорректный адрес архива."));
        return result;
    }
}
