using DzhiggaTV.Core;

internal static class EpgCorrectionTests
{
    public static IEnumerable<(string, Func<Task>)> Cases =>
    [
        ("EPG correction: independent shared IDs, midnight, search, persistence and refresh", Corrections),
        ("Archive availability: empty media, master variants, partial and non-HLS streams", Availability)
    ];
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Corrections()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DzhiggaTV-epg-correction-" + Guid.NewGuid().ToString("N")));
        try
        {
            using var store = new SqliteStore(Path.Combine(root, "library.db")); var guide = new ChannelGuide(store);
            var now = new DateTimeOffset(2026, 10, 23, 0, 5, 0, TimeSpan.FromHours(3));
            Programme[] programmes = [new("shared", "Матч", now.AddMinutes(-20), now.AddMinutes(-5)), new("shared", "Новости", now.AddMinutes(-5), now.AddMinutes(40)), new("shared", "Фильм", now.AddMinutes(40), now.AddHours(2))];
            async Task Import() => await store.ImportEpgAsync(async (channel, programme, token) => { await channel(new("shared", "Общая программа")); foreach (var p in programmes) await programme(p); });
            await Import();
            ChannelEpg[] channels = [new("normal", "shared"), new("later", "shared", 10), new("earlier", "shared", -7)];
            var batch = await guide.GetNowNextBatchAsync(channels, now);
            Check(batch["normal"].Current?.Title == "Новости" && batch["later"].Current?.Title == "Матч" && batch["earlier"].Current?.Start == programmes[1].Start.AddMinutes(-7), "Shared XMLTV IDs must not share one channel's time correction.");
            Check(batch["later"].Next?.Start == programmes[1].Start.AddMinutes(10) && batch["later"].AfterNext?.Start == programmes[2].Start.AddMinutes(10), "Both upcoming broadcasts use corrected times.");
            var from = new DateTimeOffset(now.Date, now.Offset);
            var rows = await guide.GetGuideAsync(channels, from, from.AddDays(1));
            Check(rows["later"].Any(p => p.Title == "Матч" && p.End == now.AddMinutes(5)), "A broadcast shifted across midnight must remain in the visible day's range.");
            var previous = await guide.GetPreviousProgrammeAsync(channels[1], programmes[1].Start.AddMinutes(10), now.AddDays(-1));
            Check(previous?.Title == "Матч" && previous.Start == programmes[0].Start.AddMinutes(10), "Previous-card browsing uses corrected chronological boundaries.");
            var upcoming = await guide.GetUpcomingProgrammesAsync(channels[1], now.AddMinutes(5));
            Check(upcoming[0].Title == "Новости" && upcoming[0].Start == now.AddMinutes(5), "Upcoming panel queries source time and returns broadcast time.");
            var scopes = channels.Select(c => new ProgrammeSearchScope(c.ChannelId, c.EpgId, OffsetMinutes: c.OffsetMinutes));
            var search = await store.SearchPlayableProgrammesAsync("Матч", scopes, now);
            Check(search.Keys.SequenceEqual(["later"]) && search["later"].Start == programmes[0].Start.AddMinutes(10), "Search must distinguish an ended raw broadcast from its corrected current broadcast.");
            var boundary = programmes[0].Start.AddMinutes(10);
            var archived = await store.SearchPlayableProgrammesAsync("Матч", [new("later", "shared", boundary, 10)], now.AddHours(1));
            Check(archived["later"].Start == boundary, "Archive retention is compared to corrected playback time.");
            Check((await store.SearchPlayableProgrammesAsync("Матч", [new("later", "shared", boundary.AddMilliseconds(1), 10)], now.AddHours(1))).Count == 0, "Fractional archive boundaries cannot admit an expired corrected record.");
            var state = new UserState(); state.EpgOffsets["later"] = 10; state.EpgOffsets["earlier"] = -7; await store.SaveUserStateAsync(state);
            Check((await store.LoadUserStateAsync()).EpgOffsets["later"] == 10, "Corrections survive profile reload.");
            await store.ExportSettingsAsync(Path.Combine(root, "backup.json"));
            Check(File.ReadAllText(Path.Combine(root, "backup.json")).Contains("EpgOffsets"), "Safe settings backup includes per-channel corrections.");
            await Import();
            Check((await guide.GetNowNextAsync(channels[1], now)).Current?.Start == programmes[0].Start.AddMinutes(10) && (await store.GetProgrammesAsync("shared", now.AddDays(-1), now.AddDays(1)))[0].Start == programmes[0].Start, "Refresh neither destroys nor applies the correction twice to raw EPG.");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root) && root.StartsWith(Path.GetFullPath(Path.GetTempPath()) + "DzhiggaTV-epg-correction-", StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }
    private static async Task Availability()
    {
        var root = new Uri("https://provider.example/recording/index.m3u8");
        Task<ArchiveProbeResult> Probe(string body, int status = 200, bool complete = true) => ArchiveProbe.CheckAsync(root, (uri, _) => Task.FromResult(new ArchiveProbeResponse(uri, status, body, complete)), CancellationToken.None);
        Check((await Probe("#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:0\n")).Availability == ArchiveAvailability.Empty, "An HTTP 200 empty archive is not a playable recording.");
        Check((await Probe("#EXTM3U\n#EXTINF:6,\npart.ts\n")).Availability == ArchiveAvailability.Available, "A playlist with a media segment remains playable.");
        Check((await Probe("#EXTM3U\n", complete: false)).Availability == ArchiveAvailability.Unknown && (await Probe("binary transport")).Availability == ArchiveAvailability.Unknown, "Partial HLS and other formats defer to the player rather than produce false unavailability.");
        Check((await Probe("", 404)).Availability == ArchiveAvailability.Missing && (await Probe("", 403)).Availability == ArchiveAvailability.Denied, "Provider refusal and missing recordings have explicit reasons.");
        int calls = 0;
        var master = await ArchiveProbe.CheckAsync(root, (uri, _) =>
        {
            calls++;
            return Task.FromResult(new ArchiveProbeResponse(uri, 200, calls == 1 ? "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=100\nlow/index.m3u8\n" : "#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:0\n", true));
        }, CancellationToken.None);
        Check(calls == 2 && master.Availability == ArchiveAvailability.Empty, "Availability follows a master to its actual media playlist.");
        calls = 0;
        var alternatives = await ArchiveProbe.CheckAsync(root, (uri, _) => Task.FromResult(new ArchiveProbeResponse(uri, 200,
            ++calls == 1 ? "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=100\nlow.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=200\nhigh.m3u8\n" : "#EXTM3U\n", true)), CancellationToken.None);
        Check(alternatives.Availability == ArchiveAvailability.Unknown, "One empty rendition must never reject untested alternative renditions.");
    }
}
