using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml;
using DzhiggaTV.Core;
using DzhiggaTV.Services;

internal static class ServicesTests
{
    internal static IEnumerable<(string, Func<Task>)> Cases =>
    [
        ("XMLTV: streaming XML and gzip preserve names, titles, descriptions and offsets", XmlAndGzip),
        ("XMLTV: invalid dates, DTD, corrupt XML and decompression limits rejected", RejectUnsafeXml),
        ("EPG.ONE: refresh, persisted matching and failed refresh retain last guide", EpgLifecycle),
        ("Matching: normalized quality tags, aliases and transliteration", MatchingNames),
        ("Matching: duplicate IDs, time shifts and manual overrides", MatchingAmbiguity),
        ("Logos: persistent cache performs one request across instances", LogoCache),
        ("Logos: user-selected image has precedence", LogoPrecedence),
        ("Logos: archive traversal and oversized responses rejected", LogoSafety),
        ("Logos: picon archive persists an indexed logo", PiconArchive)
    ];

    private const string Xml = """
        <?xml version="1.0" encoding="utf-8"?>
        <tv>
          <channel id="first"><display-name>Первый канал</display-name><display-name>First Channel</display-name><icon src="https://example.invalid/first.png"/></channel>
          <programme channel="first" start="20261018120000 +0300" stop="20261018130000 +0300"><title lang="ru">Новости</title><desc>Подробное описание &amp; новости</desc><category>Информация</category></programme>
          <programme channel="first" start="20261018130000 +0300" stop="20261018140000 +0300"><title>Фильм</title></programme>
        </tv>
        """;
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jR2kAAAAASUVORK5CYII=");

    private static async Task XmlAndGzip()
    {
        foreach (var gzip in new[] { false, true })
        {
            using var input = new MemoryStream();
            if (gzip) { using (var compressor = new GZipStream(input, CompressionLevel.Fastest, true)) compressor.Write(Encoding.UTF8.GetBytes(Xml)); }
            else input.Write(Encoding.UTF8.GetBytes(Xml));
            input.Position = 0;
            var channels = new List<EpgChannel>(); var programmes = new List<Programme>();
            var result = await XmlTvParser.ParseAsync(input, channel => { channels.Add(channel); return Task.CompletedTask; }, programme => { programmes.Add(programme); return Task.CompletedTask; }, gzip: gzip);
            Check(result.Channels == 1 && result.Programmes == 2 && result.RejectedProgrammes == 0);
            Check(channels[0].DisplayNames.Count == 2 && channels[0].DisplayNames[1] == "First Channel");
            Check(programmes[0].Title == "Новости" && programmes[0].Description == "Подробное описание & новости" && programmes[0].Category == "Информация");
            Check(programmes[0].Start == DateTimeOffset.Parse("2026-10-18T09:00:00Z"));
        }
        Check(XmlTvParser.ParseDate("2026") == new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        string longText = string.Concat(Enumerable.Repeat("Текст 🎬 & <>", 900));
        string escaped = System.Security.SecurityElement.Escape(longText)!;
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes("<tv><channel id=\"a\"><display-name>Канал</display-name></channel>" +
            "<programme channel=\"a\" start=\"20261018000000 +0000\" stop=\"20261018010000 +0000\"><title>Фильм</title><desc>" + escaped +
            "</desc><category>Кино</category></programme><programme channel=\"a\" start=\"20261018010000 +0000\" stop=\"20261018020000 +0000\"><title>Новости</title><desc><![CDATA[Коротко <>&]]></desc></programme></tv>")))
        {
            var programmes = new List<Programme>();
            await XmlTvParser.ParseAsync(input, _ => Task.CompletedTask, p => { programmes.Add(p); return Task.CompletedTask; });
            Check(programmes.Count == 2 && programmes[0].Description == longText && programmes[0].Category == "Кино" && programmes[1].Description == "Коротко <>&",
                "Reusable XML text buffer preserves long Unicode fields and does not bleed between records");
        }
        Check(XmlTvParser.ParseDate("20261018120000 -0430").Offset == TimeSpan.FromMinutes(-270));
        Check(!XmlTvParser.TryParseDate("20261018120000 XYZ", out _));
        Check(!XmlTvParser.TryParseDate("20260230120000 +0300", out _));
    }

    private static async Task RejectUnsafeXml()
    {
        await Expect<XmlException>(() => Parse("<!DOCTYPE tv [<!ENTITY private SYSTEM 'file:///should-not-be-read'>]><tv>&private;</tv>"));
        await Expect<XmlException>(() => Parse("<tv><channel id='broken'>"));
        await Expect<InvalidDataException>(() => Parse("<tv/>"));
        await Expect<InvalidDataException>(() => Parse(Xml, maximum: 64));
        using var corruptGzip = new MemoryStream(Encoding.UTF8.GetBytes("not a gzip"));
        await Expect<InvalidDataException>(() => XmlTvParser.ParseAsync(corruptGzip, _ => Task.CompletedTask, _ => Task.CompletedTask, gzip: true));
        // EPG.ONE contains an external legacy DTD. It is ignored and is never requested.
        await Parse(Xml.Replace("<tv>", "<!DOCTYPE tv SYSTEM 'https://example.invalid/xmltv.dtd'><tv>"));
    }

    private static async Task EpgLifecycle()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DzhiggaTV.EpgTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, true)) gzip.Write(Encoding.UTF8.GetBytes(Xml));
            var handler = new EpgHandler(compressed.ToArray()); using var client = new HttpClient(handler);
            using var store = new SqliteStore(Path.Combine(directory, "data.sqlite")); using var service = new EpgOneService(store, client);
            var result = await service.RefreshAsync([Source("Первый канал HD")]);
            Check(result.Updated && result.Channels == 1 && result.Programmes == 2);
            Check((await store.GetMappingsAsync()).Single().EpgId == "first");
            Check(await store.GetProgrammeCountAsync() == 2 && service.LastUpdated.HasValue);
            using (var reopened = new EpgOneService(store, client))
            {
                await reopened.InitializeAsync(); Check(reopened.EpgChannels.Single().Name == "Первый канал");
                Check(reopened.References.Single().EpgId == "first" && reopened.LastUpdated.HasValue);
            }
            var previous = service.LastUpdated;
            await store.SetSettingAsync("epg.lastAttempt", DateTimeOffset.UtcNow.AddDays(-2)); handler.Fail = true;
            result = await service.RefreshAsync([Source("Первый канал HD")]);
            Check(!result.Updated && service.LastUpdated == previous && await store.GetProgrammeCountAsync() == 2);
            Check(await store.GetSettingAsync("epg.failureCount", 0) == 1);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    private static async Task Parse(string xml, long maximum = 1024 * 1024)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        await XmlTvParser.ParseAsync(input, _ => Task.CompletedTask, _ => Task.CompletedTask, maximumDecompressedBytes: maximum);
    }

    private static Task MatchingNames()
    {
        var matcher = new ChannelMatcher([new("first", "Первый канал"), new("russia", "Россия 1"), new("match", "Матч ТВ")]);
        Check(matcher.Match(Source("Первый канал UHD Orig")).Mapping?.EpgId == "first");
        Check(matcher.Match(Source("1 канал HD")).Mapping?.EpgId == "first");
        Check(matcher.Match(Source("Rossiya 1 HD")).Mapping?.EpgId == "russia");
        Check(ChannelMatcher.MatchesSearch("Матч ТВ HD", "match"));
        Check(Source("Первый канал UHD").Url == "https://example.invalid/private?token=same");
        return Task.CompletedTask;
    }

    private static Task MatchingAmbiguity()
    {
        var matcher = new ChannelMatcher([new("a", "Россия 1"), new("b", "Россия 1"), new("shift", "Россия 1 +2"), new("region", "Россия 1 (Урал)")]);
        var ambiguous = matcher.Match(Source("Россия 1 HD")); Check(ambiguous.Mapping is null && ambiguous.Ambiguous);
        Check(matcher.Match(Source("Россия 1 +2 HD")).Mapping?.EpgId == "shift");
        Check(matcher.Match(Source("Россия 1 (Урал) HD")).Mapping?.EpgId == "region");
        var manual = new ChannelMapping("source", "absent", "Previously chosen", "manual", 1, true);
        Check(matcher.Match(Source("Россия 1"), manual).Mapping == manual);
        var direct = Source("Unrelated name") with { TvgId = "a" }; Check(matcher.Match(direct).Mapping?.EpgId == "a");
        return Task.CompletedTask;
    }

    private static async Task LogoCache()
    {
        await InCache(async directory =>
        {
            var handler = new ImageHandler(Png); using var client = new HttpClient(handler);
            string? first;
            using (var logos = new LogoService(directory, client)) first = await logos.ResolveAsync(Source("Test") with { Logo = "https://example.invalid/test.png" });
            Check(first is not null && File.Exists(first));
            using (var logos = new LogoService(directory, client)) Check(first == await logos.ResolveAsync(Source("Test") with { Logo = "https://example.invalid/test.png" }));
            Check(handler.Requests == 1);
        });
    }

    private static async Task LogoPrecedence()
    {
        await InCache(async directory =>
        {
            var userImage = Path.Combine(directory, "user.png"); await File.WriteAllBytesAsync(userImage, Png);
            var handler = new ImageHandler(Png); using var client = new HttpClient(handler); using var logos = new LogoService(directory, client);
            var path = await logos.ResolveAsync(Source("Test") with { Logo = "https://example.invalid/provider.png" }, userImage, new EpgChannel("x", "Test", "https://example.invalid/epg.png"));
            Check(path == userImage && handler.Requests == 0);
        });
    }

    private static async Task LogoSafety()
    {
        await InCache(async directory =>
        {
            foreach (var invalid in new[] { "../escape.png", "folder/../../escape.png", "C:\\escape.png", "/escape.png", "\\\\host\\share\\a.png" })
                ExpectSync<InvalidDataException>(() => LogoService.SafeArchivePath(directory, invalid));
            using var zip = new MemoryStream();
            using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, true)) { using var entry = archive.CreateEntry("../escape.png").Open(); entry.Write(Png); }
            zip.Position = 0;
            using var logos = new LogoService(directory);
            await Expect<InvalidDataException>(() => logos.ImportZipAsync(zip));
            var handler = new ImageHandler(Png); using var client = new HttpClient(handler); using var limited = new LogoService(directory, client) { MaximumImageBytes = 16 };
            Check(await limited.ResolveAsync(Source("Too large") with { Logo = "https://example.invalid/oversized.png" }) is null);
            Check(await limited.ResolveAsync(Source("Too large") with { Logo = "https://example.invalid/oversized.png" }) is null && handler.Requests == 1);
        });
    }

    private static async Task PiconArchive()
    {
        await InCache(async directory =>
        {
            using var zip = new MemoryStream();
            using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, true)) { using var entry = archive.CreateEntry("icons/Первый канал.png").Open(); entry.Write(Png); }
            zip.Position = 0;
            using (var logos = new LogoService(directory)) Check(await logos.ImportZipAsync(zip) == 1);
            using (var logos = new LogoService(directory)) Check(File.Exists(await logos.ResolveAsync(Source("Первый канал HD"))));
        });
    }

    private static Channel Source(string name) => new("source", "playlist", name, "Новости", "https://example.invalid/private?token=same");
    private static async Task InCache(Func<string, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "DzhiggaTV.LogoTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try { await action(directory); } finally { Directory.Delete(directory, true); }
    }
    private static void Check(bool condition, string? message = null) { if (!condition) throw new Exception(message ?? "Assertion failed."); }
    private static async Task Expect<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static void ExpectSync<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private sealed class ImageHandler(byte[] content) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content), RequestMessage = request });
        }
    }
    private sealed class EpgHandler(byte[] gzip) : HttpMessageHandler
    {
        internal bool Fail;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Fail) throw new HttpRequestException("Synthetic network failure");
            var content = request.RequestUri!.AbsolutePath.EndsWith(".gz", StringComparison.Ordinal)
                ? gzip : Encoding.UTF8.GetBytes("#EXTM3U\n#EXTINF:-1 tvg-id=\"first\" tvg-logo=\"https://example.invalid/logo.png\",Первый канал\nhttps://example.invalid/reference\n");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StreamContent(new FragmentedReadStream(content)) });
        }
    }
    private sealed class FragmentedReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
    }
}
