using L = DzhiggaTV.Localization.Texts;
using System.Text.RegularExpressions;

namespace DzhiggaTV.Remote;

public static partial class HlsPlaylist
{
    [GeneratedRegex("(?<prefix>(?:^|,)\\s*(?:[A-Z0-9-]*URI)=)\"(?<uri>[^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex UriAttribute();
    public static string Rewrite(string playlist, Uri origin, Func<Uri, string> register)
    {
        if (!playlist.TrimStart('\uFEFF', '\r', '\n', ' ').StartsWith("#EXTM3U", StringComparison.Ordinal)) throw new RemoteException(L.T("Источник не является HLS. Попробуйте преобразование через FFmpeg."), 415);
        var lines = playlist.Replace("\r", "").Split('\n');
        if (lines.Length > 20000) throw new RemoteException(L.T("Слишком большой плейлист HLS."), 502);
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("#EXT-X-CONTENT-STEERING", StringComparison.Ordinal) || line.StartsWith("#EXT-X-DEFINE", StringComparison.Ordinal))
                throw new RemoteException(L.T("Провайдер использует неподдерживаемые расширения HLS (steering / variables)."), 415);
            if (line.StartsWith("#EXT-X-KEY", StringComparison.Ordinal) || line.StartsWith("#EXT-X-SESSION-KEY", StringComparison.Ordinal))
                if (line.Contains("SAMPLE-AES", StringComparison.Ordinal) || line.Contains("KEYFORMAT=", StringComparison.Ordinal) && !line.Contains("KEYFORMAT=\"identity\"", StringComparison.Ordinal))
                    throw new RemoteException(L.T("Защищённый DRM-поток недоступен в Remote."), 415);
            if (line.Length == 0) continue;
            if (!line.StartsWith('#')) lines[i] = register(Resolve(origin, line));
            else
            {
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                lines[i] = line[..(colon + 1)] + UriAttribute().Replace(line[(colon + 1)..], m => m.Groups["prefix"].Value + "\"" + register(Resolve(origin, m.Groups["uri"].Value)) + "\"");
            }
        }
        return string.Join('\n', lines);
    }
    private static Uri Resolve(Uri origin, string value)
    {
        if (value.Contains("{$", StringComparison.Ordinal) || !Uri.TryCreate(origin, value, out var uri)) throw new RemoteException(L.T("Неподдерживаемая ссылка HLS."), 415);
        return uri;
    }
}
