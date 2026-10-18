using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DzhiggaTV.Core;

namespace DzhiggaTV.Services;

public sealed record ReferenceChannel(string Name, string EpgId, string? Logo, string? Group = null);
public sealed record MatchCandidate(string EpgId, string Name, string? Logo, double Confidence, string Reason);
public sealed record ChannelMatchResult(ChannelMapping? Mapping, IReadOnlyList<MatchCandidate> Candidates, bool Ambiguous);
public sealed record MappingDiagnostics(int Total, int Automatic, int Manual, int Ambiguous, int Unmatched);

/// <summary>Conservative matcher: ambiguity requires a human decision; quality tags never remove region or shift information.</summary>
public sealed partial class ChannelMatcher
{
    private sealed record NameVariant(string Raw, string Normalized, string Latin, string Alias, string Context, string Numbers);
    private sealed record Entry(EpgChannel Channel, NameVariant[] Names);
    private readonly Entry[] _entries;
    private readonly Dictionary<string, List<Entry>> _exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Entry>> _normalized = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Entry>> _latin = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Entry>> _alias = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EpgChannel> _ids;
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["1 канал"] = "первый канал", ["первый"] = "первый канал", ["1 kanal"] = "pervyy kanal",
        ["perviy kanal"] = "pervyy kanal", ["pervyi kanal"] = "pervyy kanal", ["rossiya 1"] = "rossiya 1",
        ["россия1"] = "россия 1", ["россия24"] = "россия 24", ["russia 1"] = "rossiya 1",
        ["russia 24"] = "rossiya 24", ["rtr planeta"] = "rtr planeta", ["ртр планета"] = "rtr planeta",
        ["ren tv"] = "рен тв", ["рентв"] = "рен тв",
        ["нтв мир"] = "ntv mir", ["матч тв"] = "match tv", ["match tv"] = "match tv",
        ["пятый канал"] = "5 канал", ["5 канал"] = "5 канал", ["тнт"] = "tnt", ["стс"] = "sts"
    };

    public ChannelMatcher(IReadOnlyList<EpgChannel> channels, IReadOnlyList<ReferenceChannel>? references = null)
    {
        var referenceGroups = (references ?? []).GroupBy(x => x.EpgId).ToDictionary(x => x.Key, x => x.ToArray());
        _ids = channels.GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.First());
        _entries = _ids.Values.Select(channel => new Entry(channel,
            channel.DisplayNames.Append(channel.Name).Concat(referenceGroups.GetValueOrDefault(channel.Id)?.Select(x => x.Name) ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Select(Describe).ToArray())).ToArray();
        foreach (var entry in _entries)
        foreach (var name in entry.Names)
        {
            Add(_exact, name.Raw, entry); Add(_normalized, name.Normalized, entry);
            Add(_latin, name.Latin, entry); Add(_alias, name.Alias, entry);
        }
    }

    private static void Add(Dictionary<string, List<Entry>> index, string key, Entry entry)
    { if (key.Length == 0) return; if (!index.TryGetValue(key, out var list)) index[key] = list = []; if (!list.Contains(entry)) list.Add(entry); }

    public ChannelMatchResult Match(Channel source, ChannelMapping? previous = null)
    {
        if (previous?.Manual == true) return new(previous, [], false); // Never overwrite a deliberate mapping, even when a provider removes it.
        if (!string.IsNullOrEmpty(source.TvgId) && _ids.TryGetValue(source.TvgId, out var direct))
            return new(new(source.Id, direct.Id, direct.Name, "tvg-id", 1, false), [new(direct.Id, direct.Name, direct.Logo, 1, "tvg-id")], false);
        var candidates = Candidates(source, 8);
        var best = candidates.FirstOrDefault();
        var ambiguous = candidates.Count > 1 && best!.Confidence - candidates[1].Confidence < .04;
        if (best is null || best.Confidence < .92 || ambiguous) return new(null, candidates, ambiguous);
        return new(new(source.Id, best.EpgId, best.Name, best.Reason, best.Confidence, false), candidates, false);
    }

    public IReadOnlyList<MatchCandidate> Candidates(Channel source, int limit = 40, string? search = null)
    {
        if (!string.IsNullOrWhiteSpace(search))
            return _entries.Where(x => x.Names.Any(n => MatchesSearch(n.Raw, search)) || x.Channel.Id.Contains(search, StringComparison.OrdinalIgnoreCase))
                .Take(Math.Clamp(limit, 1, 200)).Select(x => new MatchCandidate(x.Channel.Id, x.Channel.Name, x.Channel.Logo, 0, "Ручной выбор")).ToArray();
        var target = Describe(source.Name);
        var scores = new Dictionary<string, MatchCandidate>(StringComparer.Ordinal);
        void Collect(Dictionary<string, List<Entry>> index, string key, double confidence, string reason)
        {
            if (!index.TryGetValue(key, out var hits)) return;
            foreach (var hit in hits)
            {
                if (!hit.Names.Any(n => n.Context == target.Context)) continue;
                var candidate = new MatchCandidate(hit.Channel.Id, hit.Channel.Name, hit.Channel.Logo, confidence, reason);
                if (!scores.TryGetValue(candidate.EpgId, out var existing) || existing.Confidence < confidence) scores[candidate.EpgId] = candidate;
            }
        }
        Collect(_exact, source.Name.Trim(), .995, "Точное название");
        Collect(_normalized, target.Normalized, .975, "Нормализованное название");
        Collect(_alias, target.Alias, .95, "Словарь алиасов");
        Collect(_latin, target.Latin, .93, "Транслитерация");
        if (scores.Count == 0 && target.Latin.Length >= 8)
        {
            var maxDistance = target.Latin.Length >= 16 ? 2 : 1;
            foreach (var entry in _entries)
            {
                var distance = entry.Names.Where(n => n.Context == target.Context && n.Numbers == target.Numbers && n.Latin.Split(' ').Length == target.Latin.Split(' ').Length && Math.Abs(n.Latin.Length - target.Latin.Length) <= maxDistance)
                    .Select(n => EditDistance(target.Latin, n.Latin, maxDistance)).DefaultIfEmpty(maxDistance + 1).Min();
                if (distance <= maxDistance) scores[entry.Channel.Id] = new(entry.Channel.Id, entry.Channel.Name, entry.Channel.Logo, .88 - distance * .03, "Похожее название — проверьте вручную");
            }
        }
        return scores.Values.OrderByDescending(x => x.Confidence).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Take(Math.Clamp(limit, 1, 200)).ToArray();
    }

    private static NameVariant Describe(string name)
    {
        var normalized = NormalizeName(name); var latin = Transliterate(normalized);
        var alias = Aliases.GetValueOrDefault(normalized, normalized);
        alias = Transliterate(Aliases.GetValueOrDefault(alias, alias));
        var shift = Shift().Match(name).Value.Replace(" ", "");
        var regions = Qualifiers().Matches(latin).Select(x => x.Value).OrderBy(x => x, StringComparer.Ordinal).Distinct();
        var bracket = Brackets().Matches(name).Select(x => NormalizeName(x.Groups[1].Value)).Where(x => x.Length > 0).Select(Transliterate).OrderBy(x => x, StringComparer.Ordinal);
        var context = shift + "|" + string.Join(',', regions) + "|" + string.Join(',', bracket);
        var numbers = string.Join(',', Digits().Matches(normalized).Select(x => x.Value));
        return new(name.Trim(), normalized, latin, alias, context, numbers);
    }

    public static string NormalizeName(string value)
    {
        value = value.ToLowerInvariant().Normalize(NormalizationForm.FormKC).Replace('ё', 'е');
        value = Quality().Replace(value, " ");
        value = Regex.Replace(value, @"[^\p{L}\p{Nd}+]", " ", RegexOptions.CultureInvariant);
        return Regex.Replace(value, @"\s+", " ").Trim();
    }

    public static bool MatchesSearch(string name, string search)
    {
        var normalized = NormalizeName(name); var query = NormalizeName(search);
        return normalized.Contains(query, StringComparison.OrdinalIgnoreCase) || Transliterate(normalized).Contains(Transliterate(query), StringComparison.OrdinalIgnoreCase);
    }

    public static string Transliterate(string value)
    {
        const string alphabet = "абвгдежзийклмнопрстуфхцчшщъыьэюя";
        string[] latin = ["a", "b", "v", "g", "d", "e", "zh", "z", "i", "y", "k", "l", "m", "n", "o", "p", "r", "s", "t", "u", "f", "h", "ts", "ch", "sh", "sch", "", "y", "", "e", "yu", "ya"];
        var result = new StringBuilder();
        foreach (var character in value.ToLowerInvariant().Replace('ё', 'е')) { var index = alphabet.IndexOf(character); if (index >= 0) result.Append(latin[index]); else result.Append(character); }
        return result.ToString();
    }

    private static int EditDistance(string left, string right, int maximum)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray(); var current = new int[right.Length + 1];
        for (int row = 1; row <= left.Length; row++)
        {
            current[0] = row; var rowMinimum = row;
            for (int column = 1; column <= right.Length; column++) { current[column] = Math.Min(Math.Min(current[column - 1] + 1, previous[column] + 1), previous[column - 1] + (left[row - 1] == right[column - 1] ? 0 : 1)); rowMinimum = Math.Min(rowMinimum, current[column]); }
            if (rowMinimum > maximum) return maximum + 1;
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }

    [GeneratedRegex(@"(?<![\p{L}\p{Nd}])(?:full\s*hd|fhd|uhd|hd|sd|4[kк]|8[kк]|orig|ориг)(?![\p{L}\p{Nd}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Quality();
    [GeneratedRegex(@"\+\s*\d{1,2}(?!\d)", RegexOptions.CultureInvariant)] private static partial Regex Shift();
    [GeneratedRegex(@"\b(?:moskva|moscow|sankt peterburg|spb|ural|sibir|siberia|dalniy vostok|international|int|mir|planeta|europa|europe|ukraina|ukraine|belarus|kazahstan|kazakhstan|armenia|israel|usa|uk|deutschland|germany|ru|rus|ua|by|kz|md|az|am|ge|uz|tj|kg|tr)\b", RegexOptions.CultureInvariant)] private static partial Regex Qualifiers();
    [GeneratedRegex(@"[\[(]([^\])]+)[\])]", RegexOptions.CultureInvariant)] private static partial Regex Brackets();
    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)] private static partial Regex Digits();
}
