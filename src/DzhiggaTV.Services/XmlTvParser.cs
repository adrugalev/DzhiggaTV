using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using DzhiggaTV.Core;

namespace DzhiggaTV.Services;

public sealed record XmlTvParseResult(int Channels, int Programmes, int RejectedProgrammes);

public static partial class XmlTvParser
{
    public const long DefaultMaximumDecompressedBytes = 1024L * 1024 * 1024;

    /// <summary>Parses one record at a time. Callbacks must participate in the caller's atomic transaction.</summary>
    public static async Task<XmlTvParseResult> ParseAsync(Stream input, Func<EpgChannel, Task> addChannel,
        Func<Programme, Task> addProgramme, CancellationToken cancellationToken = default,
        bool gzip = false, long maximumDecompressedBytes = DefaultMaximumDecompressedBytes)
    {
        await using var decompressor = gzip ? new GZipStream(input, CompressionMode.Decompress, true) : null;
        await using var bounded = new BoundedReadStream(decompressor ?? input, maximumDecompressedBytes, true, readCancellationToken: cancellationToken);
        using var reader = XmlReader.Create(bounded, new XmlReaderSettings
        {
            // Real EPG.ONE files declare a legacy external DTD. Ignore it entirely and never resolve entities or fetch it.
            Async = true, DtdProcessing = DtdProcessing.Ignore, XmlResolver = null,
            IgnoreComments = true, IgnoreWhitespace = true, MaxCharactersInDocument = maximumDecompressedBytes,
            CloseInput = false
        });
        // Parsing is sequential. Reuse one chunk buffer for the whole feed instead of
        // allocating 4 KiB for every title/description/category in a multi-day guide.
        var textBuffer = new char[2048];
        var text = new StringBuilder(256);
        int channels = 0, programmes = 0, rejected = 0;
        bool tv = false;
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.LocalName == "tv") { tv = true; continue; }
            if (reader.LocalName == "channel" && reader.Depth == 1)
            {
                var id = reader.GetAttribute("id");
                var names = new List<string>(); string? logo = null;
                using var sub = reader.ReadSubtree();
                while (await sub.ReadAsync().ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (sub.NodeType != XmlNodeType.Element || sub.Depth != 1) continue;
                    if (sub.LocalName == "display-name") names.Add(await ReadTextAsync(sub, textBuffer, text, cancellationToken).ConfigureAwait(false));
                    else if (sub.LocalName == "icon") logo ??= sub.GetAttribute("src");
                }
                if (!string.IsNullOrWhiteSpace(id))
                {
                    names = names.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    await addChannel(new EpgChannel(id, names.FirstOrDefault() ?? id, logo) { DisplayNames = names.Count > 0 ? names : [id] }).ConfigureAwait(false);
                    channels++;
                }
            }
            else if (reader.LocalName == "programme" && reader.Depth == 1)
            {
                var id = reader.GetAttribute("channel");
                var validStart = TryParseDate(reader.GetAttribute("start"), out var start);
                var validEnd = TryParseDate(reader.GetAttribute("stop"), out var end);
                string title = ""; string? description = null, category = null;
                using var sub = reader.ReadSubtree();
                while (await sub.ReadAsync().ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (sub.NodeType != XmlNodeType.Element || sub.Depth != 1) continue;
                    if (sub.LocalName == "title") { var value = await ReadTextAsync(sub, textBuffer, text, cancellationToken).ConfigureAwait(false); if (title.Length == 0) title = value; }
                    else if (sub.LocalName == "desc") description ??= await ReadTextAsync(sub, textBuffer, text, cancellationToken).ConfigureAwait(false);
                    else if (sub.LocalName == "category") category ??= await ReadTextAsync(sub, textBuffer, text, cancellationToken).ConfigureAwait(false);
                }
                if (validStart && validEnd && end > start && !string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(title))
                {
                    await addProgramme(new Programme(id, title, start, end, description, category)).ConfigureAwait(false);
                    programmes++;
                }
                else rejected++;
            }
        }
        if (!tv || channels == 0 || programmes == 0) throw new InvalidDataException("XMLTV не содержит каналов и корректных передач; сохранена предыдущая программа.");
        return new(channels, programmes, rejected);
    }

    private static async Task<string> ReadTextAsync(XmlReader reader, char[] buffer, StringBuilder builder, CancellationToken cancellationToken)
    {
        if (reader.IsEmptyElement) return "";
        builder.Clear();
        var depth = reader.Depth;
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
            if (reader.NodeType is not (XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace)) continue;
            int read;
            while ((read = await reader.ReadValueChunkAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                if (builder.Length + read > 65536) throw new InvalidDataException("Слишком большое текстовое поле XMLTV.");
                builder.Append(buffer, 0, read);
            }
        }
        return builder.ToString().Trim();
    }

    public static DateTimeOffset ParseDate(string value) => TryParseDate(value, out var result) ? result : throw new FormatException("Некорректная дата XMLTV или неизвестный часовой пояс.");

    /// <summary>XMLTV partial dates default omitted month/day to 1 and time to 00; absent timezone means UTC per XMLTV.</summary>
    public static bool TryParseDate(string? value, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var match = XmlTvDate().Match(value.Trim());
        if (!match.Success) return false;
        var digits = match.Groups[1].Value;
        if (digits.Length is not (4 or 6 or 8 or 10 or 12 or 14)) return false;
        int Part(int index, int fallback) => digits.Length >= index + 2 ? int.Parse(digits.AsSpan(index, 2), CultureInfo.InvariantCulture) : fallback;
        var zone = match.Groups[2].Value.ToUpperInvariant();
        TimeSpan offset;
        if (zone is "" or "UTC" or "GMT" or "Z") offset = TimeSpan.Zero;
        else if (zone.Length == 5 && zone[0] is '+' or '-' && int.TryParse(zone.AsSpan(1, 2), out var hours) && int.TryParse(zone.AsSpan(3, 2), out var minutes) && minutes < 60 && hours <= 14 && (hours < 14 || minutes == 0))
            offset = new TimeSpan(hours, minutes, 0) * (zone[0] == '-' ? -1 : 1);
        else return false;
        try { result = new DateTimeOffset(int.Parse(digits.AsSpan(0, 4), CultureInfo.InvariantCulture), Part(4, 1), Part(6, 1), Part(8, 0), Part(10, 0), Part(12, 0), offset); return true; }
        catch (ArgumentException) { return false; }
    }
    [GeneratedRegex(@"^(\d{4,14})(?:\s+([A-Za-z]+|[+-]\d{4}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex XmlTvDate();
}
