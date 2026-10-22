using DzhiggaTV.Core;

internal static class ProgrammeSearchTests
{
    public static IEnumerable<(string, Func<Task>)> Cases => [("Programme search: current and retained archive only, per-channel depths and exact boundaries", AvailabilityAsync)];
    private static async Task AvailabilityAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), "DzhiggaTV-search-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        try
        {
            using var store = new SqliteStore(Path.Combine(path, "library.db")); await store.InitializeAsync();
            var now = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
            Channel Channel(string id, int days = 0, bool disabled = false) => new(id, "fixture", id, "Тест", "https://example.invalid/live",
                Attributes: new Dictionary<string, string> { ["tvg-rec"] = days.ToString(), ["catchup"] = disabled ? "disabled" : "shift" });
            Channel[] channels = [Channel("live"), Channel("short", 1), Channel("long", 7), Channel("disabled", 7, true)];
            Programme Entry(string title, DateTimeOffset start, DateTimeOffset end) => new("shared-epg", title, start, end);
            Programme[] programmes = [
                Entry("Финал сейчас", now.AddMinutes(-5), now.AddMinutes(5)),
                Entry("Финал вчера", now.AddHours(-2), now.AddHours(-1)),
                Entry("Кубок раньше", now.AddDays(-3), now.AddDays(-3).AddHours(1)),
                Entry("Кубок недавно", now.AddHours(-2), now.AddHours(-1)),
                Entry("Глубокий архив", now.AddDays(-4), now.AddDays(-4).AddHours(1)),
                Entry("Истёкшая запись", now.AddDays(-8), now.AddDays(-8).AddHours(1)),
                Entry("Будущая передача", now.AddHours(1), now.AddHours(2)),
                Entry("Граница архива", now.AddDays(-1), now.AddDays(-1).AddHours(1))];
            await store.ImportEpgAsync(async (addChannel, addProgramme, token) =>
            { await addChannel(new("shared-epg", "Общая программа")); foreach (var p in programmes) await addProgramme(p); });
            async Task<IReadOnlyDictionary<string, Programme>> Search(string query, DateTimeOffset? at = null) =>
                await store.SearchPlayableProgrammesAsync(query, channels.Select(c => ProgrammeSearchScope.From(c, "shared-epg", at ?? now)), at ?? now);
            void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
            var current = await Search("Финал");
            Check(current.Count == 4 && current.Values.All(p => p.Title == "Финал сейчас"), "Current matches take priority and work without an archive.");
            var recent = await Search("Кубок");
            Check(recent.Keys.ToHashSet().SetEquals(["short", "long"]) && recent.Values.All(p => p.Title == "Кубок недавно"), "Each channel gets its most recent retained match despite sharing an EPG ID.");
            Check((await Search("Глубокий")).Keys.SequenceEqual(["long"]), "Shared EPG must never grant one channel another channel's retention depth.");
            Check((await Search("Истёкшая")).Count == 0 && (await Search("Будущая")).Count == 0, "Expired and future programmes cannot match.");
            Check((await Search("Граница")).Keys.ToHashSet().SetEquals(["short", "long"]), "The exact oldest playable start is included.");
            Check((await Search("Граница", now.AddMilliseconds(1))).Keys.SequenceEqual(["long"]), "A subsecond-expired start cannot round back into a shorter archive.");
            foreach (var channel in channels)
                foreach (var programme in programmes)
                {
                    var matched = await Search(programme.Title);
                    Check(matched.ContainsKey(channel.Id) == ProgrammeSearch.IsAvailable(channel, programme, now), "SQLite and guide filtering must agree on availability.");
                }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(path, true); }
    }
}
