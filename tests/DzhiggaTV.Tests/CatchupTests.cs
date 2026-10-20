using DzhiggaTV.Core;

internal static class CatchupTests
{
    public static IEnumerable<(string, Func<Task>)> Cases =>
    [
        ("Archive: provider depth, disabled and unsupported channels", Capabilities),
        ("Archive: UTC boundaries preserve signed live URLs and channel identity", SignedUrls),
        ("Archive: global metadata, explicit templates and Flussonic ranges", Templates)
    ];
    private static Channel Channel(string metadata, string url = "https://tv.example/iptv/secret/25/index.m3u8") => M3uParser.Parse("#EXTM3U\n#EXTINF:0 " + metadata + ",News\n" + url, "test").Single();
    private static void Check(bool result) { if (!result) throw new InvalidOperationException("Catchup regression"); }
    private static Task Capabilities()
    {
        Check(Catchup.From(Channel("tvg-rec=\"7\""))?.Days == 7);
        Check(Catchup.From(Channel("tvg-rec=\"3\""))?.Days == 3);
        foreach (string metadata in new[] { "", "tvg-rec=\"0\"", "tvg-rec=\"-1\"", "tvg-rec=\"huge\"", "tvg-rec=\"7\" catchup=\"disabled\"", "tvg-rec=\"7\" catchup=\"unknown\"" }) Check(Catchup.From(Channel(metadata)) is null);
        Check(Catchup.From(Channel("tvg-rec=\"7\"", "file:///private.ts")) is null);
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero); var channel = Channel("tvg-rec=\"3\"");
        Check(Catchup.CanPlay(channel, new("guide", "At boundary", now.AddDays(-3), now.AddDays(-3).AddHours(1)), now));
        Check(!Catchup.CanPlay(channel, new("guide", "Expired", now.AddDays(-3).AddSeconds(-1), now.AddDays(-3).AddHours(1)), now));
        Check(!Catchup.CanPlay(channel, new("guide", "Future", now.AddHours(1), now.AddHours(2)), now));
        return Task.CompletedTask;
    }
    private static Task SignedUrls()
    {
        const string live = "https://tv.example/iptv/a%2Bb%2F/25/index.m3u8?sig=ab%2Bcd%3D&x=2&utc=old&lutc=old#part";
        var channel = Channel("tvg-rec=\"7\"", live); var now = new DateTimeOffset(2026, 10, 20, 15, 0, 0, TimeSpan.FromHours(3));
        string archive = Catchup.BuildUrl(channel, now.AddHours(-2), now.AddHours(-1), now);
        Check(archive == "https://tv.example/iptv/a%2Bb%2F/25/index.m3u8?sig=ab%2Bcd%3D&x=2&utc=" + now.AddHours(-2).ToUnixTimeSeconds() + "&lutc=" + now.ToUnixTimeSeconds() + "#part");
        Check(channel.Url == live && channel.Id == Channel("tvg-rec=\"7\"", "https://tv.example/newtoken").Id);
        bool rejected = false; try { Catchup.BuildUrl(channel, now.AddDays(-8), now.AddDays(-7), now); } catch (ArgumentOutOfRangeException) { rejected = true; } Check(rejected);
        return Task.CompletedTask;
    }
    private static Task Templates()
    {
        var now = DateTimeOffset.UtcNow; var start = now.AddHours(-2); var end = start.AddHours(1);
        var channels = M3uParser.Parse("#EXTM3U catchup=\"shift\" catchup-days=\"7\"\n#EXTINF:0,A\nhttps://tv.example/a\n#EXTINF:0 catchup-days=\"0\",B\nhttps://tv.example/b", "test");
        Check(Catchup.From(channels[0])?.Days == 7 && Catchup.From(channels[1]) is null);
        var explicitSource = Channel("catchup=\"default\" catchup-days=\"7\" catchup-source=\"https://tv.example/archive/${start}/${duration}.m3u8\"");
        Check(Catchup.BuildUrl(explicitSource, start, end, now) == $"https://tv.example/archive/{start.ToUnixTimeSeconds()}/3600.m3u8");
        var flussonic = Channel("catchup=\"flussonic\" catchup-days=\"7\"", "https://tv.example/show/index.m3u8?token=a%2Bb");
        Check(Catchup.BuildUrl(flussonic, start, end, now) == $"https://tv.example/show/index-{start.ToUnixTimeSeconds()}-3600.m3u8?token=a%2Bb");
        return Task.CompletedTask;
    }
}
