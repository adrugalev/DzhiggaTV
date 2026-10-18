using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DzhiggaTV.Core;

/// <summary>Parses provider metadata without rewriting or canonicalizing a stream URL.</summary>
public sealed partial class M3uParser
{
    public static IReadOnlyList<Channel> Parse(string content, string playlistId)
    {
        using var reader = new StringReader(content);
        return ParseLines(ReadLines(reader), playlistId);
    }

    public static async Task<IReadOnlyList<Channel>> ParseAsync(Stream stream, string playlistId,
        CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 65536, leaveOpen: true);
        var parser = new ParserState(playlistId);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            parser.Add(line);
        return parser.Channels;
    }

    public static async Task<IReadOnlyList<Channel>> ParseFileAsync(string path, string playlistId,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ParseAsync(stream, playlistId, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<Channel> ParseLines(IEnumerable<string> lines, string playlistId)
    {
        var parser = new ParserState(playlistId);
        foreach (var line in lines) parser.Add(line);
        return parser.Channels;
    }

    private static IEnumerable<string> ReadLines(TextReader reader)
    {
        while (reader.ReadLine() is { } line) yield return line;
    }

    private sealed class ParserState(string playlistId)
    {
        public List<Channel> Channels { get; } = [];
        private readonly Dictionary<string, int> _duplicates = new(StringComparer.Ordinal);
        private Dictionary<string, string> _attributes = new(StringComparer.OrdinalIgnoreCase);
        private string? _name;
        private string? _group;

        public void Add(string raw)
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0) return;
            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                // A comma inside a quoted attribute is part of that attribute, not the title separator.
                var comma = TitleSeparator(line);
                _name = comma >= 0 ? line[(comma + 1)..].Trim() : null;
                _attributes = new(StringComparer.OrdinalIgnoreCase);
                var metadata = comma >= 0 ? line[..comma] : line;
                foreach (Match match in AttributePattern().Matches(metadata))
                {
                    var value = match.Groups[2].Success ? match.Groups[2].Value :
                        match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value;
                    _attributes[match.Groups[1].Value] = value;
                }
                if (_attributes.TryGetValue("group-title", out var group)) _group = group;
                return;
            }
            if (line.StartsWith("#EXTGRP:", StringComparison.OrdinalIgnoreCase))
            {
                _group = line[8..].Trim();
                return;
            }
            if (line.StartsWith('#')) return;
            if (_name is null) return; // Header URLs and orphan lines are not channels.
            var name = string.IsNullOrWhiteSpace(_name) ? "Канал " + (Channels.Count + 1) : _name;
            var category = string.IsNullOrWhiteSpace(_group) ? "Без категории" : _group;
            var identity = playlistId + "\u001f" + name + "\u001f" + category;
            var duplicate = _duplicates.GetValueOrDefault(identity);
            _duplicates[identity] = duplicate + 1;
            // Signed query strings are deliberately excluded: token refreshes preserve favorites and EPG mappings.
            var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity + "\u001f" + duplicate)))[..32];
            _attributes.TryGetValue("tvg-id", out var tvgId);
            _attributes.TryGetValue("tvg-logo", out var logo);
            Channels.Add(new Channel(id, playlistId, name, category, raw, tvgId, logo,
                new Dictionary<string, string>(_attributes, StringComparer.OrdinalIgnoreCase), Channels.Count));
            _name = null;
            _group = null;
            _attributes = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static int TitleSeparator(string line)
    {
        char quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var character = line[i];
            if (character is '\'' or '"')
            {
                if (quote == '\0') quote = character;
                else if (quote == character) quote = '\0';
            }
            if (character == ',' && quote == '\0') return i;
        }
        return -1;
    }

    [GeneratedRegex("([A-Za-z0-9_-]+)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s]+))", RegexOptions.CultureInvariant)]
    private static partial Regex AttributePattern();
}
