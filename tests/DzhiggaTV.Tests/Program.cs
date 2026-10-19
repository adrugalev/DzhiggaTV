using System.Text;
using DzhiggaTV.Core;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("M3U: EXTGRP, attributes, UTF-8, comma in quoted values", TestM3uMetadata),
    ("M3U: signed URLs are unchanged and excluded from stable IDs", TestStableIdentity),
    ("M3U: duplicate entries receive separate stable IDs", TestDuplicateIdentity),
    ("M3U: async UTF-8 BOM and comments", TestAsyncParser),
    ("User state: favorites and collection order", TestOrdering),
    ("DPAPI: current-user round trip", TestDpapi),
    ("SQLite: private playlists encrypted and refresh preserves state", TestPlaylistStorage),
    ("SQLite: manual EPG mappings survive automatic refresh", TestMappings),
    ("SQLite: indexed now/next, range and Cyrillic programme search", TestGuideQueries),
    ("SQLite: next two programmes stay chronological across midnight and missing guide entries", TestNextTwo),
    ("SQLite: EPG import failure leaves the previous guide intact", TestEpgRollback),
    ("SQLite: EPG cancellation leaves the previous guide intact", TestEpgCancellation),
    ("SQLite: guide reads use the previous snapshot during a background import", TestGuideReadDuringImport),
    ("SQLite: replacing guide also replaces FTS results", TestGuideReplacement),
    ("Backup: provider URLs, logos and settings secrets excluded", TestSafeExport),
    ("Private fixture: 3227 channels, 12 EXTGRP categories", TestRealPlaylist)
};
tests.AddRange(ServicesTests.Cases);

var failed = 0;
foreach (var (name, test) in tests)
{
    try { await test(); Console.WriteLine("PASS " + name); }
    catch (SkipTestException exception) { Console.WriteLine("SKIP " + name + ": " + exception.Message); }
    catch (Exception exception) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + exception.GetType().Name + " — " + exception.Message); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} cases without failure; {failed} failed.");
return failed == 0 ? 0 : 1;

static Task TestM3uMetadata()
{
    var channels = M3uParser.Parse("""
        #EXTM3U
        #EXTINF:-1 tvg-id="one" tvg-logo="https://example.invalid/one.png" group-title="Other" tvg-rec="7" custom="a,b",Первый, HD
        #EXTGRP:Новости
        https://example.invalid/live.m3u8?key=a%2Bb&n=1
        #EXTINF:-1 group-title='Кино',Кино HD
        https://example.invalid/movie.m3u8
        """, "fixture");
    Equal(2, channels.Count); Equal("Новости", channels[0].Group); Equal("Первый, HD", channels[0].Name);
    Equal("one", channels[0].TvgId); Equal("a,b", channels[0].Attributes!["custom"]);
    Equal("7", channels[0].Attributes!["tvg-rec"]); Equal("Кино", channels[1].Group);
    return Task.CompletedTask;
}

static Task TestStableIdentity()
{
    const string original = "https://example.invalid/x.m3u8?token=one%2Btwo&x=1";
    var first = M3uParser.Parse("#EXTM3U\n#EXTINF:0,Канал HD\n#EXTGRP:ТВ\n" + original, "provider")[0];
    var second = M3uParser.Parse("#EXTM3U\n#EXTINF:0,Канал HD\n#EXTGRP:ТВ\nhttps://example.invalid/changed?token=new", "provider")[0];
    Equal(original, first.Url); Equal(first.Id, second.Id);
    True(M3uParser.Parse("#EXTINF:0,Канал HD\n#EXTGRP:ТВ\n" + original, "different")[0].Id != first.Id);
    return Task.CompletedTask;
}

static Task TestDuplicateIdentity()
{
    const string content = "#EXTINF:0,Одинаковые\n#EXTGRP:Кино\nhttps://example.invalid/1\n#EXTINF:0,Одинаковые\n#EXTGRP:Кино\nhttps://example.invalid/2";
    var channels = M3uParser.Parse(content, "same");
    Equal(2, channels.Count); True(channels[0].Id != channels[1].Id);
    Equal(channels[1].Id, M3uParser.Parse(content.Replace("invalid/2", "invalid/new"), "same")[1].Id);
    Equal(0, M3uParser.Parse("#EXTM3U\nhttps://example.invalid/orphan\n#comment", "x").Count);
    return Task.CompletedTask;
}

static async Task TestAsyncParser()
{
    using var stream = new MemoryStream(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("#EXTM3U\r\n#EXTINF:0,Тест\r\n#EXTGRP:Категория\r\nhttps://example.invalid/a%2Fb?x=A+B\r\n")).ToArray());
    var channels = await M3uParser.ParseAsync(stream, "async");
    Equal(1, channels.Count); Equal("Тест", channels[0].Name); Equal("Категория", channels[0].Group);
    Equal("https://example.invalid/a%2Fb?x=A+B", channels[0].Url); True(stream.CanRead);
}

static Task TestOrdering()
{
    var state = new UserState { Favorites = ["a", "b", "c"], Collections = new() { ["Мои"] = ["c", "a", "b"] } };
    state.MoveFavorite(0, 2); Equal("b,c,a", string.Join(',', state.Favorites));
    state.MoveCollectionChannel("Мои", 2, 0); Equal("b,c,a", string.Join(',', state.Collections["Мои"]));
    state.RecordWatched("a"); state.RecordWatched("b"); state.RecordWatched("a");
    Equal("a,b", string.Join(',', state.History));
    return Task.CompletedTask;
}

static Task TestDpapi()
{
    var protector = new DpapiSecretProtector();
    var protectedValue = protector.Protect("https://example.invalid/?token=private-123");
    True(!protectedValue.Contains("private-123", StringComparison.Ordinal));
    Equal("https://example.invalid/?token=private-123", protector.Unprotect(protectedValue));
    Throws<System.Security.Cryptography.CryptographicException>(() => protector.Unprotect("plaintext"));
    return Task.CompletedTask;
}

static async Task TestPlaylistStorage()
{
    await WithDatabase(async (store, path) =>
    {
        var first = M3uParser.Parse("#EXTINF:0,Канал\n#EXTGRP:Новости\nhttps://example.invalid/live?token=unique-private-playlist-921", "source");
        await store.SavePlaylistAsync(new PlaylistSource("source", "My source", "https://example.invalid/list?token=unique-private-source-918", true), first);
        await store.SaveUserStateAsync(new UserState
        {
            Favorites = [first[0].Id], Collections = new() { ["Основные"] = [first[0].Id] }, Hidden = [first[0].Id],
            Renames = new() { [first[0].Id] = "Моё название" }, CustomCategories = new() { [first[0].Id] = "Моя категория" },
            UserLogos = new() { [first[0].Id] = "https://example.invalid/logo?token=unique-private-logo-916" }
        });
        var refreshed = M3uParser.Parse("#EXTINF:0,Канал\n#EXTGRP:Новости\nhttps://example.invalid/live?token=rotated", "source");
        await store.SavePlaylistAsync(new PlaylistSource("source", "My source", "private source"), refreshed);
        var state = await store.LoadUserStateAsync();
        Equal(first[0].Id, state.Favorites.Single()); Equal(first[0].Id, state.Collections["Основные"].Single());
        True(state.Hidden.Contains(first[0].Id)); Equal("Моё название", state.Renames[first[0].Id]);
        Equal("https://example.invalid/live?token=rotated", (await store.GetChannelsAsync()).Single().Url);
        Equal("private source", (await store.GetPlaylistSourcesAsync()).Single().Location);
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "data.sqlite*"))
        {
            await using var inspection = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var content = new MemoryStream(); await inspection.CopyToAsync(content);
            var bytes = Encoding.UTF8.GetString(content.ToArray());
            True(!bytes.Contains("unique-private-playlist-921", StringComparison.Ordinal));
            True(!bytes.Contains("unique-private-source-918", StringComparison.Ordinal));
            True(!bytes.Contains("unique-private-logo-916", StringComparison.Ordinal));
        }
        using var reopened = new SqliteStore(path);
        Equal(first[0].Id, (await reopened.LoadUserStateAsync()).Favorites.Single());
    });
}

static async Task TestMappings() => await WithDatabase(async (store, _) =>
{
    await store.SaveMappingsAsync([new ChannelMapping("channel", "manual", "Канал", "manual", 1, true)]);
    await store.SaveMappingsAsync([new ChannelMapping("channel", "automatic", "Канал", "normalized", .95)]);
    Equal("manual", (await store.GetMappingsAsync()).Single().EpgId);
    await store.SaveMappingsAsync([new ChannelMapping("channel", "manual-2", "Канал", "manual", 1, true)]);
    Equal("manual-2", (await store.GetMappingsAsync()).Single().EpgId);
    await store.SaveMappingsAsync([new ChannelMapping("auto", "old", "Auto", "automatic", .99)]);
    await store.ReplaceAutomaticMappingsAsync([], ["auto", "channel"]);
    Equal("manual-2", (await store.GetMappingsAsync()).Single().EpgId);
    await store.SetSettingAsync("epg.test", DateTimeOffset.UnixEpoch);
    Equal(DateTimeOffset.UnixEpoch, await store.GetSettingAsync("epg.test", DateTimeOffset.UtcNow));
});

static async Task TestGuideQueries() => await WithDatabase(async (store, _) =>
{
    var now = DateTimeOffset.Parse("2026-10-18T12:30:00+03:00");
    await SeedGuide(store, now);
    var pair = await store.GetNowNextAsync("epg.one", now);
    Equal("Новости сегодня", pair.Current!.Title); Equal("Кино вечером", pair.Next!.Title);
    Equal(now.AddMinutes(-30).ToUnixTimeSeconds(), pair.Current.Start.ToUnixTimeSeconds());
    Equal(2, (await store.GetProgrammesAsync("epg.one", now.AddHours(-1), now.AddHours(2))).Count);
    Equal(1, (await store.SearchProgrammesAsync("Новост")).Count);
    Equal(0, (await store.SearchProgrammesAsync("' OR 1=1 --")).Count);
    Equal(1, (await store.GetEpgChannelsAsync()).Count); Equal("Alias", (await store.GetEpgChannelsAsync())[0].DisplayNames[1]);
});

static async Task TestNextTwo() => await WithDatabase(async (store, _) =>
{
    var at = DateTimeOffset.Parse("2026-10-19T23:50:00+03:00");
    var current = new Programme("night", "Сейчас", at.AddMinutes(-20), at.AddMinutes(20));
    var next = new Programme("night", "Следующая", current.End, current.End.AddMinutes(30), "Первое описание");
    var after = new Programme("night", "Затем", next.End.AddMinutes(20), next.End.AddMinutes(80), "Второе описание", "Фильм");
    await store.ImportEpgAsync(async (addChannel, addProgramme, _) =>
    {
        await addChannel(new("night", "Ночной канал"));
        // Insertion order must not determine the order on the cards.
        await addProgramme(after); await addProgramme(current); await addProgramme(next);
        await addProgramme(new("night", "Закончилась", at.AddHours(-2), current.Start));
    });
    var guide = await store.GetNowNextAsync("night", at);
    Equal(current.Title, guide.Current!.Title); Equal(next.Title, guide.Next!.Title);
    Equal(after.Title, guide.AfterNext!.Title); Equal(after.Description!, guide.AfterNext.Description!);
    Equal(after.Category!, guide.AfterNext.Category!);
    guide = await store.GetNowNextAsync("night", next.Start);
    Equal(next.Title, guide.Current!.Title); Equal(after.Title, guide.Next!.Title); True(guide.AfterNext is null);
    guide = await store.GetNowNextAsync("night", next.End.AddMinutes(1));
    True(guide.Current is null); Equal(after.Title, guide.Next!.Title); True(guide.AfterNext is null);
    guide = await store.GetNowNextAsync("night", current.Start.AddMinutes(-1));
    Equal(current.Title, guide.Next!.Title); Equal(next.Title, guide.AfterNext!.Title);
    guide = await store.GetNowNextAsync("night", after.End);
    True(guide.Current is null && guide.Next is null && guide.AfterNext is null);
    guide = await store.GetNowNextAsync("unknown", at);
    True(guide.Current is null && guide.Next is null && guide.AfterNext is null);
});

static async Task TestEpgRollback() => await WithDatabase(async (store, _) =>
{
    var now = DateTimeOffset.UtcNow; await SeedGuide(store, now);
    await ThrowsAsync<InvalidDataException>(() => store.ImportEpgAsync(async (channel, programme, _) =>
    {
        await channel(new EpgChannel("broken", "Broken"));
        await programme(new Programme("broken", "Incomplete", now, now.AddHours(1)));
        throw new InvalidDataException("Synthetic truncated XMLTV.");
    }));
    Equal(2L, await store.GetProgrammeCountAsync());
    Equal("epg.one", (await store.GetEpgChannelsAsync()).Single().Id);
    await ThrowsAsync<InvalidDataException>(() => store.ImportEpgAsync((_, _, _) => Task.CompletedTask));
    Equal(2L, await store.GetProgrammeCountAsync());
});

static async Task TestEpgCancellation() => await WithDatabase(async (store, _) =>
{
    var now = DateTimeOffset.UtcNow; await SeedGuide(store, now);
    using var cts = new CancellationTokenSource();
    await ThrowsAsync<OperationCanceledException>(() => store.ImportEpgAsync(async (channel, programme, token) =>
    {
        await channel(new EpgChannel("cancelled", "Cancelled"));
        cts.Cancel(); token.ThrowIfCancellationRequested();
    }, cts.Token));
    Equal(2L, await store.GetProgrammeCountAsync()); Equal("epg.one", (await store.GetEpgChannelsAsync()).Single().Id);
});

static async Task TestGuideReplacement() => await WithDatabase(async (store, _) =>
{
    var now = DateTimeOffset.UtcNow; await SeedGuide(store, now);
    await store.ImportEpgAsync(async (channel, programme, _) =>
    {
        await channel(new EpgChannel("new", "New"));
        await programme(new Programme("new", "Совсем другая передача", now, now.AddHours(1)));
    });
    Equal(0, (await store.SearchProgrammesAsync("Новости")).Count);
    Equal(1, (await store.SearchProgrammesAsync("другая")).Count); Equal(1L, await store.GetProgrammeCountAsync());
});

static async Task TestGuideReadDuringImport() => await WithDatabase(async (store, _) =>
{
    var now = DateTimeOffset.UtcNow; await SeedGuide(store, now);
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var continueImport = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var import = Task.Run(() => store.ImportEpgAsync(async (channel, programme, _) =>
    {
        await channel(new EpgChannel("new", "New"));
        await programme(new Programme("new", "Updated", now, now.AddHours(1)));
        started.SetResult(); await continueImport.Task;
    }));
    await started.Task;
    try
    {
        var read = await store.GetNowNextAsync("epg.one", now).WaitAsync(TimeSpan.FromSeconds(3));
        Equal("Новости сегодня", read.Current!.Title);
        var guide = await store.GetGuideAsync(["epg.one", "new"], now.AddHours(-1), now.AddHours(1)).WaitAsync(TimeSpan.FromSeconds(3));
        True(guide["epg.one"].Any(p => p.Title == "Новости сегодня")); True(!guide.ContainsKey("new"));
    }
    finally { continueImport.TrySetResult(); await import; }
    Equal("Updated", (await store.GetNowNextAsync("new", now)).Current!.Title);
});

static async Task TestSafeExport() => await WithDatabase(async (store, path) =>
{
    var channels = M3uParser.Parse("#EXTINF:0,Тест\nhttps://example.invalid/live?token=unique-export-secret", "source");
    await store.SavePlaylistAsync(new PlaylistSource("source", "Источник", "https://example.invalid/source?token=unique-export-secret", true), channels);
    await store.SaveUserStateAsync(new UserState
    {
        Favorites = [channels[0].Id], UserLogos = new() { [channels[0].Id] = "https://example.invalid/logo?key=unique-export-secret" },
        Settings = new() { ["playlistPassword"] = "unique-export-secret", ["theme"] = "dark" }
    });
    var export = Path.Combine(Path.GetDirectoryName(path)!, "backup.json"); await store.ExportSettingsAsync(export);
    var text = await File.ReadAllTextAsync(export);
    True(!text.Contains("unique-export-secret", StringComparison.Ordinal)); True(!text.Contains("example.invalid", StringComparison.Ordinal));
    True(text.Contains(channels[0].Id, StringComparison.Ordinal)); True(text.Contains("dark", StringComparison.Ordinal));
});

static async Task TestRealPlaylist()
{
    var path = Environment.GetEnvironmentVariable("DZHIGGATV_TEST_PLAYLIST");
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new SkipTestException("Set DZHIGGATV_TEST_PLAYLIST to the private fixture locally.");
    var channels = await M3uParser.ParseFileAsync(path, "private-fixture");
    Equal(3227, channels.Count); Equal(12, channels.Select(channel => channel.Group).Distinct().Count());
    True(channels.All(channel => channel.Attributes!.ContainsKey("tvg-rec")));
    True(channels.All(channel => string.IsNullOrEmpty(channel.TvgId) && string.IsNullOrEmpty(channel.Logo)));
    Equal(3227, channels.Select(channel => channel.Id).Distinct().Count());
}

static Task SeedGuide(SqliteStore store, DateTimeOffset now) => store.ImportEpgAsync(async (channel, programme, _) =>
{
    await channel(new EpgChannel("epg.one", "Первый", "https://example.invalid/logo.png") { DisplayNames = ["Первый", "Alias"] });
    await programme(new Programme("epg.one", "Новости сегодня", now.AddMinutes(-30), now.AddMinutes(30), "Описание", "Новости"));
    await programme(new Programme("epg.one", "Кино вечером", now.AddMinutes(30), now.AddMinutes(120), "Описание", "Кино"));
});

static async Task WithDatabase(Func<SqliteStore, string, Task> test)
{
    var directory = Path.Combine(Path.GetTempPath(), "DzhiggaTV.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory); var path = Path.Combine(directory, "data.sqlite");
    try { using var store = new SqliteStore(path); await store.InitializeAsync(); await test(store, path); }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(directory, true);
    }
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}.");
}
static void True(bool value) { if (!value) throw new Exception("Assertion failed."); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name);
}
static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name);
}
sealed class SkipTestException(string message) : Exception(message);
