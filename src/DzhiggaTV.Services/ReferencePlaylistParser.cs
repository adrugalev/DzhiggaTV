using L = DzhiggaTV.Localization.Texts;
using System.Text.RegularExpressions;

namespace DzhiggaTV.Services;

/// <summary>Reads catalogue metadata only. Stream URLs are intentionally never captured or returned.</summary>
public static partial class ReferencePlaylistParser
{
    public static async Task<IReadOnlyList<ReferenceChannel>> ParseAsync(Stream input, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(input, leaveOpen: true);
        var result = new List<ReferenceChannel>();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length > 1024 * 1024) throw new InvalidDataException(L.T("Слишком длинная строка каталога EPG."));
            if (!line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase)) continue;
            bool quoted = false; char quote = '\0'; int comma = -1;
            for (int i = 8; i < line.Length; i++) { if (line[i] is '\'' or '"') { if (!quoted) { quoted = true; quote = line[i]; } else if (line[i] == quote) quoted = false; } else if (line[i] == ',' && !quoted) { comma = i; break; } }
            if (comma < 0) continue;
            var attributes = Attributes().Matches(line[..comma]).ToDictionary(x => x.Groups[1].Value, x => x.Groups[2].Success ? x.Groups[2].Value : x.Groups[3].Success ? x.Groups[3].Value : x.Groups[4].Value, StringComparer.OrdinalIgnoreCase);
            if (!attributes.TryGetValue("tvg-id", out var id) || string.IsNullOrWhiteSpace(id)) continue;
            result.Add(new(line[(comma + 1)..].Trim(), id, attributes.GetValueOrDefault("tvg-logo"), attributes.GetValueOrDefault("group-title")));
        }
        return result;
    }
    [GeneratedRegex("([\\w-]+)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s,]+))", RegexOptions.CultureInvariant)] private static partial Regex Attributes();
}
