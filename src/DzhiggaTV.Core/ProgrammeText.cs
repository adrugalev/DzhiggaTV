using System.Text.RegularExpressions;

namespace DzhiggaTV.Core;

/// <summary>Separates recognizable XMLTV film details without shortening the original synopsis.</summary>
public sealed partial record ProgrammeText(string Metadata, string Synopsis, string Credits)
{
    public static ProgrammeText From(Programme? programme)
    {
        var text = (programme?.Description ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        var metadata = new List<string>();
        if (LeadingYear().Match(text) is { Success: true } year)
        {
            metadata.Add("Год: " + year.Groups[1].Value);
            text = text[year.Length..].TrimStart();
        }
        while (LeadingField().Match(text) is { Success: true } field)
        {
            metadata.Add(field.Value.Trim());
            text = text[field.Length..].TrimStart();
        }
        int genreEnd = text.IndexOf('.');
        if (genreEnd > 0 && genreEnd < 180 && text[..genreEnd].Split(',', StringSplitOptions.TrimEntries).All(Genres.Contains))
        {
            metadata.Add("Жанр: " + text[..genreEnd]);
            text = text[(genreEnd + 1)..].TrimStart();
        }

        // Some guides join the film header and plot with dots after the running time.
        var duration = HeaderDuration().Match(text);
        if (duration.Success && HeaderEvidence().IsMatch(duration.Value))
        {
            metadata.Add(duration.Value.Trim().TrimEnd('.', ',', '…', '|'));
            text = text[duration.Length..].TrimStart(' ', '.', ',', '…', '|', '\n');
        }
        else
        {
            int newline = text.IndexOf('\n');
            if (newline > 0 && newline <= 550 && HeaderEvidence().IsMatch(text[..newline]))
            {
                metadata.Add(text[..newline].Trim());
                text = text[(newline + 1)..].TrimStart();
            }
        }

        var credits = CreditsStart().Match(text);
        string details = credits.Success ? text[credits.Index..].Trim() : "";
        string synopsis = credits.Success ? text[..credits.Index].TrimEnd(' ', '\n', ',', '|') : text;
        if (!string.IsNullOrWhiteSpace(programme?.Category) && !metadata.Any(m => m.StartsWith("Жанр", StringComparison.OrdinalIgnoreCase)))
            metadata.Insert(0, programme.Category.Trim());
        return new(string.Join("\n", metadata), synopsis, FieldBreak().Replace(details, "\n").Trim());
    }

    private static readonly HashSet<string> Genres = new(StringComparer.OrdinalIgnoreCase)
    {
        "биография", "драма", "история", "реальные события", "экранизация", "боевик", "триллер", "приключения",
        "комедия", "криминал", "ужасы", "фантастика", "фэнтези", "мелодрама", "детектив", "семейный", "военный",
        "вестерн", "мюзикл", "музыка", "спорт", "документальный", "анимация", "мультфильм", "короткометражный"
    };

    [GeneratedRegex(@"^((?:18|19|20)\d{2}(?:\s*[–—-]\s*(?:18|19|20)\d{2})?)\s*(?:г\.?|год)?\s*(?:[.;]|\n|$)", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingYear();

    [GeneratedRegex(@"^(?:Жанры?|Стран[аы])\s*:\s*[^.\n]+\.?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingField();
    [GeneratedRegex(@"^.{0,450}?\b\d{1,2}\s*(?:ч\.|час(?:а|ов)?|ч)\s*(?:\d{1,3}\s*мин(?:ут(?:а|ы)?)?\.?)?", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderDuration();
    [GeneratedRegex(@"\((?:18|19|20)\d{2}\)|\bГод\s*:\s*\d{4}|\bIMDb\s*\[|^\s*(?:18|19|20)\d{2}\s*(?:г\.|год)?\s*[,.;—–-]", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderEvidence();
    [GeneratedRegex(@"\b(?:Премьера|Продюсеры?|Сценарий|Операторы?|Композиторы?|Бюджет|В ролях|Режисс[её]ры?|Музыка|Российский слоган|Прокатчик)\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex CreditsStart();
    [GeneratedRegex(@"[ \t|]+(?=(?:Премьера|Продюсеры?|Сценарий|Операторы?|Композиторы?|Бюджет|В ролях|Режисс[её]ры?|Музыка|Российский слоган|Прокатчик)\s*:)", RegexOptions.IgnoreCase)]
    private static partial Regex FieldBreak();
}
