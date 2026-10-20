using System.Diagnostics;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DzhiggaTV.App;
using DzhiggaTV.Core;

// No network or playback engine: a real SQLite library, local PNGs and a WPF dispatcher.
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var run = RunAsync();
        var frame = new DispatcherFrame();
        _ = run.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        return run.GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync()
    {
        (string Name, Func<Task> Run)[] cases =
        [
            ("Sports audio never overlaps when switching, muting, replacing or removing sources", SportsAudioAsync),
            ("Sports selection persists ordered unique allowed channels and safely restores stale settings", SportsSelectionAsync),
            ("Cold startup displays all three cached cards while background EPG import blocks saving", StartupGuideDuringImportAsync),
            ("Late viewport guide loading notifies all active programme cards", ActiveViewportGuideAsync),
            ("Repeated favorite toggles remove the row immediately and persist the off state", FavoriteToggleAsync),
            ("Programme countdown handles missing data, hour boundaries and expired programmes", ProgrammeCountdownAsync),
            ("First EPG refresh replaces the missing-logo fallback immediately", MissingGuideThenRefreshAsync),
            ("Remapping a loaded channel replaces its programme and logo", RemapAsync),
            ("Removing an automatic mapping clears the previous programme", RemoveMappingAsync),
            ("Settings reload rebinds active and selected channels while preserving user changes", ReloadAsync),
            ("A local picon import replaces the missing-logo fallback immediately", ImportPiconsAsync),
            ("An EPG setup failure restores the retry button and progress state", EpgSetupFailureAsync),
            ("Guide focuses the exact current instant and clamps midnight boundaries", GuideClockAsync),
            ("Guide hit testing agrees with scrolled, clipped cells and frozen channel names", GuideHitTestingAsync),
            ("Guide selection shows complete details without playback until double click", GuideSelectionAsync),
            ("Guide day ranges follow daylight-saving offsets", GuideDaylightSavingAsync),
            ("Guide reads only allowed EPG IDs across multiple parameter batches", GuideBatchAsync),
            ("Guide playlist, category and programme search compose without leaking other rows", GuideFiltersAsync),
            ("Playing a guide channel selects it and reveals it through conflicting library filters", PlaybackSelectionAsync),
            ("Library search finds current and future titles across all channels without stale or restricted results", ProgrammeSearchAsync),
            ("Maximized bounds respect taskbars and secondary monitors while F11 fills the monitor", WindowBoundsAsync)
        ];
        var failed = 0;
        foreach (var (name, test) in cases)
        {
            try { await test(); Console.WriteLine("PASS " + name); }
            catch (Exception error) { ++failed; Console.WriteLine("FAIL " + name + ": " + error.GetType().Name + " — " + error.Message); }
        }
        Console.WriteLine($"{cases.Length - failed}/{cases.Length} App regressions passed; {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    private static Task SportsAudioAsync()
    {
        var sources = new List<CheckedAudio>();
        var audio = new SportsAudioFocus();
        CheckedAudio Create() { var source = new CheckedAudio(() => sources.Count(s => !s.IsMuted)); sources.Add(source); return source; }
        var a = Create(); var b = Create(); var c = Create();
        KeyValuePair<string, ISportsAudio> Pair(string id, CheckedAudio source) => new(id, source);
        audio.Replace([Pair("a", a), Pair("b", b), Pair("c", c)], "b");
        Assert(audio.SelectedId == "b" && !b.IsMuted && a.IsMuted && c.IsMuted, "Saved source must be selected exclusively.");
        audio.Select("a"); audio.Select("c"); audio.Select("b");
        audio.Select("missing"); Assert(audio.SelectedId == "b", "Unknown source must not silence the current channel.");
        audio.IsMuted = true; audio.Select("c");
        Assert(sources.All(s => s.IsMuted) && audio.SelectedId == "c", "Switching source while globally muted must remain silent.");
        audio.Volume = 123; audio.IsMuted = false;
        Assert(!c.IsMuted && a.Volume == 100 && c.Volume == 100, "Unmute must restore only the chosen source and apply clamped master volume.");
        var d = Create(); audio.Replace([Pair("a", a), Pair("c", c), Pair("d", d)]);
        Assert(audio.SelectedId == "c" && !c.IsMuted && b.IsMuted && d.IsMuted, "Adding/removing another tile must retain audio focus and mute removed sources.");
        audio.Replace([Pair("a", a), Pair("d", d)]);
        Assert(audio.SelectedId == "a" && !a.IsMuted && c.IsMuted, "Removing the selected channel must select a remaining source.");
        audio.Replace([]);
        Assert(audio.SelectedId is null && sources.All(s => s.IsMuted), "Closing the grid must silence every source before disposal.");
        return Task.CompletedTask;
    }

    private sealed class CheckedAudio(Func<int> audibleCount) : ISportsAudio
    {
        private bool _muted = true;
        public bool IsMuted { get => _muted; set { _muted = value; Assert(audibleCount() <= 1, "Two sources became audible during an intermediate state."); } }
        public double Volume { get; set; }
    }

    private static async Task SportsSelectionAsync()
    {
        using var fixture = await Fixture.CreateAsync(); var model = fixture.Model;
        for (int i = 0; i < 12; i++) model.AllChannels.Add(new(new("sport-" + i, "fixture", "Sport " + i, "Спорт", "https://example.invalid/unused")));
        model.State.Hidden.Add("sport-0");
        model.AllChannels.Add(new(new("adult-sport", "fixture", "Restricted", "Для взрослых", "https://example.invalid/unused")));
        var ids = new[] { "missing", "sport-0", "adult-sport", "sport-3", "sport-3", "sport-2" }.Concat(Enumerable.Range(0, 12).Select(i => "sport-" + i));
        var chosen = SportsSelection.Resolve(model, ids);
        Assert(chosen.Count == 9 && chosen.Select(c => c.Id).Distinct().Count() == 9 && chosen[0].Id == "sport-3" && chosen[1].Id == "sport-2",
            "Selection must preserve order, remove duplicates and limit the grid to nine allowed channels.");
        Assert(chosen.All(model.IsAllowed), "Hidden/adult channels must not bypass library restrictions.");
        SportsSelection.Save(model, chosen, "sport-2"); await model.SaveAsync();
        Assert(SportsSelection.Restore(model).Select(c => c.Id).SequenceEqual(chosen.Select(c => c.Id)) && model.Setting("sports.audio") == "sport-2", "Selected IDs and audio source must round-trip.");
        model.State.Hidden.Add("sport-3");
        Assert(SportsSelection.Restore(model).All(c => c.Id != "sport-3"), "A subsequently hidden channel must not restart from saved selections.");
        model.SetSetting("sports.channels", "broken json");
        Assert(SportsSelection.Restore(model).Count == 0, "Corrupt saved data must leave a usable empty selection.");
        model.SetSetting("sports.channels", "[null,\"\",\"missing\",\"sport-2\"]");
        Assert(SportsSelection.Restore(model).Single().Id == "sport-2", "Null and deleted IDs must not prevent restoring valid channels.");
    }

    private static async Task FavoriteToggleAsync()
    {
        using var fixture = await Fixture.CreateAsync(); var model = fixture.Model;
        var channel = model.AllChannels.Single(); model.Active = channel;
        await model.ToggleFavoriteAsync(channel);
        Assert(channel.IsFavorite && model.State.Favorites.SequenceEqual([channel.Id]), "The first click must add the channel exactly once.");
        model.SetSection("favorites");
        Assert(model.VisibleChannels.Contains(channel), "The selected favorite must be visible.");
        var removal = model.ToggleFavoriteAsync(channel);
        Assert(!channel.IsFavorite && !model.VisibleChannels.Contains(channel), "The second click must clear the star and remove the row before saving completes.");
        await removal;
        Assert(!(await model.Store.LoadUserStateAsync()).Favorites.Contains(channel.Id) && model.Active == channel, "The off state must persist without interrupting playback.");
        await model.ToggleFavoriteAsync(channel);
        Assert(model.VisibleChannels.Contains(channel), "Adding it again must restore the favorite row.");
        // Older imported settings may contain repeated IDs; one click must remove all of them.
        model.State.Favorites.Add(channel.Id);
        await model.ToggleFavoriteAsync(channel);
        await model.ReloadAsync();
        Assert(model.State.Favorites.Count == 0 && model.VisibleChannels.Count == 0 && !model.AllChannels.Single().IsFavorite,
            "Duplicate favorites must not survive removal or library reload.");
    }

    private static Task ProgrammeCountdownAsync()
    {
        var now = new DateTimeOffset(2026, 10, 19, 22, 0, 0, TimeSpan.FromHours(3));
        Programme ProgrammeUntil(DateTimeOffset end) => new("guide", "Film", now.AddHours(-1), end);
        Assert(ProgrammeTiming.Remaining(null, now) == "", "Missing EPG must leave the countdown empty so the entire row can be hidden.");
        Assert(ProgrammeTiming.Remaining(ProgrammeUntil(now.AddHours(2).AddMinutes(3)), now) == "2 ч 3 мин", "Hours and whole minutes must be preserved without seconds.");
        Assert(ProgrammeTiming.Remaining(ProgrammeUntil(now.AddSeconds(3599)), now) == "1 ч 0 мин", "A partial minute must round up across an hour boundary.");
        Assert(ProgrammeTiming.Remaining(ProgrammeUntil(now.AddMinutes(14).AddSeconds(20)), now) == "15 мин", "Minute rounding must not expose seconds.");
        Assert(ProgrammeTiming.Remaining(ProgrammeUntil(now.AddMilliseconds(100)), now) == "1 мин", "A partial final minute must remain visible.");
        Assert(ProgrammeTiming.Remaining(ProgrammeUntil(now.AddSeconds(-2)), now) == "0 мин", "An expired programme must not show a negative countdown.");
        Assert(ProgrammeTiming.Remaining(ProgrammeUntil(now.AddHours(26)), now) == "26 ч 0 мин", "Long programmes must not wrap at midnight.");
        Assert(ProgrammeTiming.Remaining(new("guide", "Future", now.AddHours(1), now.AddHours(2)), now) == "", "A future entry must not act as the current programme.");
        return Task.CompletedTask;
    }

    private static Task GuideClockAsync()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test UTC+3", TimeSpan.FromHours(3), "Test UTC+3", "Test UTC+3");
        var day = new DateTime(2026, 10, 19); var range = GuideRange.ForDay(day, zone);
        var now = new DateTimeOffset(2026, 10, 19, 10, 43, 27, TimeSpan.FromHours(3));
        double offset = range.FocusOffset(now, 720);
        Assert(Math.Abs(range.Position(now) - offset - 720 * .22) < .001, "Now must be at the visible focus position, including minutes and seconds.");
        Assert(range.Start == new DateTimeOffset(day, TimeSpan.FromHours(3)) && range.Contains(now) && !range.Contains(range.End), "The day must use the local timezone with an exclusive end.");
        Assert(range.FocusOffset(range.Start, 720) == 0 && range.FocusOffset(range.End.AddSeconds(-1), 720) == range.Width - 720, "Focus must not scroll outside the day.");
        Assert(range.FocusOffset(now, range.Width + 100) == 0, "A wide viewport must not produce a negative offset.");
        return Task.CompletedTask;
    }
    private static Task GuideFiltersAsync()
    {
        var now = DateTimeOffset.Now;
        var news = new Programme("epg", "Новости", now, now.AddHours(1), "Полное описание");
        var film = news with { Title = "Кино", Description = "Всё описание фильма" };
        GuideRow Row(string id, string playlist, string category) => new(new(new(id, playlist, "Канал " + id, category, "https://example.invalid/unused")), [news, film]);
        var rows = new[] { Row("one", "a", "Новости"), Row("two", "a", "Кино"), Row("three", "b", "Новости"), Row("four", "b", "Спорт") };
        var selected = GuideFiltering.Filter(rows, "a", "Новости", "ново");
        Assert(selected.Count == 1 && selected[0].Channel.Id == "one" && selected[0].Programmes.Single() == news, "All three filters must apply together and retain the full programme.");
        Assert(GuideFiltering.Filter(rows, "b", "Кино", "").Count == 0, "A category from another playlist must not show unrelated channels.");
        Assert(GuideFiltering.Categories(rows, "a").SequenceEqual(new[] { "Кино", "Новости" }), "Category options must only contain the selected playlist's categories.");
        Assert(GuideFiltering.Filter(rows, null, null, "").Count == 4 && rows[0].Programmes.Count == 2, "Reset must restore all rows without mutating cached programmes.");
        return Task.CompletedTask;
    }
    private static async Task PlaybackSelectionAsync()
    {
        using var fixture = await Fixture.CreateAsync(); var model = fixture.Model;
        var first = model.AllChannels[0]; first.IsFavorite = true; model.State.Favorites.Add(first.Id);
        var target = new ChannelItem(new("play-target", "another-playlist", "Новый канал", "Другая категория", "https://example.invalid/unused")); model.AllChannels.Add(target);
        model.SelectedGroup = first.Group; model.SetSection("favorites"); model.Search = first.Name; model.RefreshFilter();
        Assert(!model.VisibleChannels.Contains(target), "The fixture must initially hide the guide's selected channel.");
        await model.RecordPlaybackAsync(target);
        Assert(model.Active == target && model.SelectedChannel == target && model.VisibleChannels.Contains(target), "The playing channel must be selected and visible in the main list.");
        Assert(model.Search == "" && model.SelectedGroup == "Все категории", "Conflicting search and category filters must be reset.");
        model.SelectedGroup = target.Group; model.RefreshFilter(); model.SelectPlaybackChannel(target);
        Assert(model.SelectedGroup == target.Group && model.SelectedChannel == target, "Compatible filters must remain intact.");
        model.State.Hidden.Add(first.Id); model.SelectPlaybackChannel(first);
        Assert(model.SelectedChannel == target, "A hidden channel must never be revealed by selection synchronization.");
    }
    private static async Task ProgrammeSearchAsync()
    {
        using var fixture = await Fixture.CreateAsync(); var model = fixture.Model;
        var now = DateTimeOffset.UtcNow;
        var channels = Enumerable.Range(0, 432).Select(i => new Channel("search-" + i, "search", "Канал эфира " + i,
            i == 431 ? "Взрослые" : i % 2 == 0 ? "Спорт" : "Кино", "https://example.invalid/unused", Order: i)).ToArray();
        await model.Store.SavePlaylistAsync(new("search", "Поиск", "local-only.m3u"), channels);
        await model.Store.ImportEpgAsync(async (addChannel, addProgramme, token) =>
        {
            foreach (var (channel, i) in channels.Select((c, i) => (c, i)))
            {
                string id = "search-guide-" + i; await addChannel(new(id, channel.Name));
                if (i is 0 or 428 or 429 or 430 or 431)
                {
                    var start = i == 0 ? now.AddMinutes(-5) : now.AddHours(2);
                    await addProgramme(new(id, "Финал. ЦСКА — Спартак", start, start.AddHours(2)));
                    await addProgramme(new(id, "Финал. ЦСКА — Спартак. Повтор", start.AddHours(5), start.AddHours(7)));
                }
                else if (i == 1) await addProgramme(new(id, "Финал. ЦСКА — Спартак", now.AddHours(-2), now.AddHours(-1)));
                else if (i == 2) await addProgramme(new(id, "Другой эфир", now.AddMinutes(-5), now.AddHours(1), "Финал ЦСКА Спартак только в описании"));
                else if (i == 3) await addProgramme(new(id, "Финал. ЦСКА — Спартак", now.AddDays(2), now.AddDays(2).AddHours(2)));
            }
            // More than an ordinary result limit on unrelated EPG channels must not hide the user's matches.
            await addChannel(new("unmapped", "Не из библиотеки"));
            for (int i = 0; i < 1050; i++) await addProgramme(new("unmapped", "Финал. ЦСКА — Спартак", now.AddMinutes(-10), now.AddHours(1)));
        });
        await model.Store.SaveMappingsAsync(channels.Select((c, i) => new ChannelMapping(c.Id, "search-guide-" + i, c.Name, "manual", 1, true)).ToArray());
        model.State.Hidden.Add("search-430");
        await model.ReloadAsync(); await model.RefreshEpgViewAsync();
        var distant = model.AllChannels.Single(c => c.Id == "search-428");
        Assert(distant.Current is null && distant.Next is null, "The future match must start outside the loaded viewport.");
        model.Search = "ФИНАЛ цска"; await model.RefreshFilterAsync();
        Assert(model.VisibleChannels.Select(c => c.Id).ToHashSet().SetEquals(["search-0", "search-3", "search-428", "search-429"]),
            "Search must include current/future programmes in later batches, exclude expired/description-only/unmapped/hidden/adult results, and deduplicate repeats.");
        Assert(distant.SearchProgramme?.Title == "Финал. ЦСКА — Спартак" && distant.ProgrammeSummary.Contains(now.AddHours(2).ToLocalTime().ToString("HH:mm")),
            "A programme result must explain which title and start time matched.");
        model.State.Favorites.Add(distant.Id); model.State.Favorites.Add("search-2");
        model.SetSection("favorites"); await model.RefreshFilterAsync();
        Assert(!model.CanSearch && model.VisibleChannels.Select(c => c.Id).ToHashSet().SetEquals(["search-428", "search-2"]) && model.AllChannels.All(c => c.SearchProgramme is null),
            "Favorites must ignore the previous search and clear programme search labels.");
        model.Search = "This must not filter favorites"; await model.RefreshFilterAsync();
        Assert(model.VisibleChannels.Count == 2 && model.Search == "ФИНАЛ цска", "Search input must be inactive in favorites without replacing the saved library query.");
        model.SelectedGroup = "Кино"; await model.RefreshFilterAsync();
        Assert(model.IsFavorites && model.VisibleChannels.Count == 2 && !model.ChannelCountText.Contains("категорий"), "A category selected outside favorites must not hide favorite channels or appear in the favorites heading.");
        model.SetSection("all"); await model.RefreshFilterAsync();
        Assert(model.CanSearch, "Returning to the library must restore search.");
        Assert(model.VisibleChannels.Select(c => c.Id).ToHashSet().SetEquals(["search-3", "search-429"]), "Category restrictions must also apply to programme hits.");
        model.SelectedGroup = "Все категории";
        var renamed = model.AllChannels.Single(c => c.Id == "search-2"); renamed.Name = "Экран Север";
        model.Search = "Финал"; var older = model.RefreshFilterAsync();
        model.Search = "sever"; var latest = model.RefreshFilterAsync();
        await Task.WhenAll(older, latest);
        Assert(model.VisibleChannels.Single() == renamed && renamed.SearchProgramme is null, "An older database result must never replace a newer channel-name/transliteration search.");
        model.Search = "Финал"; await model.RefreshFilterAsync();
        await model.Store.ImportEpgAsync(async (addChannel, addProgramme, token) =>
        { await addChannel(new("search-guide-0", "Новый эфир")); await addProgramme(new("search-guide-0", "Новости", now, now.AddHours(1))); });
        await model.RefreshEpgViewAsync();
        Assert(model.VisibleChannels.Count == 0, "Refreshing EPG must remove stale programme matches from the current search.");
        model.Search = ""; await model.RefreshFilterAsync();
        Assert(model.VisibleChannels.Contains(renamed) && model.AllChannels.All(c => c.SearchProgramme is null), "Clearing search must restore the library and ordinary now/next labels.");
        model.Search = "Экран Север"; await model.RefreshFilterAsync();
        model.SetSection("favorites"); model.Search = ""; model.SetSection("all"); await model.RefreshFilterAsync();
        Assert(model.Search == "" && model.VisibleChannels.Contains(renamed), "Navigation must still be able to clear an old query while leaving favorites.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await model.Store.SearchUpcomingProgrammesAsync("Финал", ["search-guide-0"], now, cancelled.Token); throw new InvalidOperationException("Search ignored cancellation."); }
        catch (OperationCanceledException) { }
        Assert((await model.Store.SearchUpcomingProgrammesAsync("*** \"", ["search-guide-0"], now)).Count == 0, "Punctuation must not be interpreted as FTS operators.");
    }

    private static Task WindowBoundsAsync()
    {
        var primary = new System.Windows.Rect(0, 0, 1920, 1080);
        Assert(WindowBoundsGeometry.Maximized(primary, new(0, 0, 1920, 1032), false) == new System.Windows.Rect(0, 0, 1920, 1032), "Bottom taskbar must remain outside the maximized application.");
        var secondary = new System.Windows.Rect(-2560, -180, 2560, 1440);
        Assert(WindowBoundsGeometry.Maximized(secondary, new(-2500, -180, 2500, 1440), false) == new System.Windows.Rect(60, 0, 2500, 1440), "A left taskbar on a secondary monitor must use monitor-relative physical coordinates.");
        Assert(WindowBoundsGeometry.Maximized(secondary, new(-2560, -120, 2560, 1380), false) == new System.Windows.Rect(0, 60, 2560, 1380), "A top taskbar and negative monitor origin must be respected.");
        Assert(WindowBoundsGeometry.Maximized(primary, new(0, 0, 1920, 1032), true) == primary, "F11 must use the complete monitor, including the taskbar area.");
        return Task.CompletedTask;
    }
    private static Task GuideHitTestingAsync()
    {
        var range = GuideRange.ForDay(new(2026, 10, 19), TimeZoneInfo.Utc);
        var first = new Programme("guide", "First", range.Start.AddHours(9), range.Start.AddHours(10));
        var second = new Programme("guide", "Second", first.End, first.End.AddMinutes(30));
        var row = new GuideRow(new(new("channel", "fixture", "Channel", "TV", "https://example.invalid/unused")), [first, second]);
        double offset = range.Position(first.Start.AddMinutes(30)), width = 800;
        var clipped = GuideLayout.Cell(first, range, offset, width);
        Assert(clipped.Left == GuideLayout.ChannelWidth, "A programme that started off screen must be clipped at the fixed channel column.");
        Assert(GuideLayout.Hit(row, range, offset, width, new(clipped.Left + 20, 20)) == first, "The visible part of a clipped programme must be selectable.");
        Assert(GuideLayout.Hit(row, range, offset, width, new(20, 20)) is null, "Clicking a channel name must never hit a programme behind it.");
        Assert(GuideLayout.Hit(row, range, offset, width, new(clipped.Right + 1, 20)) is null, "The gap between cells must not select an unrelated programme.");
        var next = GuideLayout.Cell(second, range, offset, width);
        Assert(GuideLayout.Hit(row, range, offset, width, new(next.Left + 5, 20)) == second, "Hit testing must include the horizontal offset.");
        Assert(GuideLayout.Hit(row, range, offset, width, new(next.Left + 5, 1)) is null, "Vertical cell padding must not select a programme.");
        return Task.CompletedTask;
    }
    private static async Task GuideSelectionAsync()
    {
        int plays = 0; string? playedId = null;
        var programme = new Programme("guide", "Complete title", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), string.Join("\n\n", Enumerable.Repeat("Полное описание без сокращения.", 40)));
        var channel = new ChannelItem(new("channel", "fixture", "Channel", "TV", "https://example.invalid/unused"));
        var row = new GuideRow(channel, [programme]); var selection = new GuideSelection();
        Task Play(ChannelItem item) { plays++; playedId = item.Id; return Task.CompletedTask; }
        Assert(!await selection.ClickAsync(row, programme, 1, Play) && plays == 0, "A single click must never start playback.");
        Assert(selection.Programme?.Description is { Length: > 1000 } complete && complete == programme.Description, "Selecting a programme must preserve its full description.");
        Assert(await selection.ClickAsync(row, programme, 2, Play) && plays == 1 && playedId == channel.Id, "A double click must activate the exact selected channel once.");
        selection.Clear(); Assert(selection.Row is null && selection.Programme is null, "An empty search must clear a stale selection.");
    }
    private static Task GuideDaylightSavingAsync()
    {
        var begin = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new(2026, 1, 1), new(2026, 12, 31), TimeSpan.FromHours(1), begin, end);
        var zone = TimeZoneInfo.CreateCustomTimeZone("Guide DST", TimeSpan.FromHours(1), "Guide DST", "Guide standard", "Guide daylight", [rule]);
        var spring = GuideRange.ForDay(new(2026, 3, 29), zone); var autumn = GuideRange.ForDay(new(2026, 10, 25), zone);
        Assert((spring.End - spring.Start).TotalHours == 23 && (autumn.End - autumn.Start).TotalHours == 25, "Guide coordinates must follow actual elapsed time on short and long local days.");
        return Task.CompletedTask;
    }
    private static async Task GuideBatchAsync()
    {
        using var fixture = await Fixture.CreateAsync(); var store = fixture.Model.Store;
        var now = DateTimeOffset.UtcNow;
        await store.ImportEpgAsync(async (addChannel, addProgramme, token) =>
        {
            for (int i = 0; i < 820; i++)
            {
                string id = "batch-" + i; await addChannel(new(id, id));
                await addProgramme(new(id, "Current " + i, now.AddMinutes(-30), now.AddMinutes(30), "Complete description " + i));
                await addProgramme(new(id, "Outside range", now.AddDays(-2), now.AddDays(-2).AddHours(1)));
            }
        });
        var ids = Enumerable.Range(0, 805).Select(i => "batch-" + i).Concat(["batch-0", "missing"]).ToArray();
        var rows = await store.GetGuideAsync(ids, now.AddHours(-1), now.AddHours(1));
        Assert(rows.Count == 805 && rows["batch-804"].Single().Description == "Complete description 804", "Large guide requests must preserve every requested channel and description across batches.");
        Assert(!rows.ContainsKey("batch-805") && rows.All(r => r.Value.Count == 1), "Unrequested channels and out-of-range programmes must stay excluded.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); bool stopped = false;
        try { await store.GetGuideAsync(ids, now.AddHours(-1), now.AddHours(1), cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
        Assert(stopped, "Closing the guide must cancel a pending database read.");
    }

    private static async Task StartupGuideDuringImportAsync()
    {
        using var fixture = await Fixture.CreateAsync(); var model = fixture.Model;
        var saved = model.AllChannels.Single().Channel with { Order = 25 };
        var channels = Enumerable.Range(0, 25).Select(i => new Channel("cold-" + i, "fixture", "Канал " + i, "Тест", "https://example.invalid/unused", Order: i)).Append(saved).ToArray();
        await model.Store.SavePlaylistAsync(new("fixture", "Cold startup", "local-only.m3u8"), channels);
        await fixture.SeedGuideAsync(); await fixture.MapAsync("guide-one");
        model.SetSetting("epg.enabled", false); model.SetSetting("resume", true); model.SetSetting("last.channel", saved.Id); await model.SaveAsync();
        // Exercise actual initialization with cached data; networking is explicitly disabled.
        await model.InitializeAsync();
        var channel = model.AllChannels.Single(c => c.Id == saved.Id);
        Assert(model.AllChannels.IndexOf(channel) >= 20 && channel.Current is null, "The resumed channel must start outside the prefetched viewport.");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var now = DateTimeOffset.UtcNow;
        var import = model.Store.ImportEpgAsync(async (addChannel, addProgramme, token) =>
        {
            started.SetResult(); await finish.Task.WaitAsync(token);
            await addChannel(new("guide-one", "Обновлённый канал", fixture.FirstLogo));
            for (int i = 0; i < 3; i++) await addProgramme(new("guide-one", "Новая передача " + i, now.AddMinutes(-5 + i * 30), now.AddMinutes(25 + i * 30), "Новое описание " + i));
        });
        await started.Task;
        var activation = model.RecordPlaybackAsync(channel);
        try
        {
            await WaitUntilAsync(() => model.CurrentTitle == "Передача один" && model.NextTitle == "Следующая один" && model.AfterNextTitle == "Затем один",
                "Cached cards must appear before a background guide import releases the writer.");
            Assert(!activation.IsCompleted && !import.IsCompleted, "The test must verify cards while persistence is still blocked.");
        }
        finally { finish.TrySetResult(); await import; await activation; }
        await model.RefreshEpgViewAsync();
        Assert(model.CurrentTitle == "Новая передача 0" && model.NextTitle == "Новая передача 1" && model.AfterNextTitle == "Новая передача 2",
            "Finishing the background refresh must replace all cached cards without selecting the channel again.");
        await WaitUntilAsync(() => channel.LogoPath == fixture.FirstLogo, "The restored channel logo must finish loading.");
    }

    private static async Task ActiveViewportGuideAsync()
    {
        using var fixture = await Fixture.CreateAsync(); var model = fixture.Model;
        await fixture.MapAsync("guide-one"); await model.RefreshEpgViewAsync();
        var channel = model.AllChannels.Single(); model.Active = channel;
        await fixture.SeedGuideAsync();
        var notified = new HashSet<string?>(); model.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        await model.LoadViewportAsync([channel]);
        Assert(model.CurrentTitle == "Передача один" && model.NextTitle == "Следующая один" && model.AfterNextTitle == "Затем один", "Viewport loading must restore all three programmes.");
        Assert(new[] { nameof(MainViewModel.CurrentTitle), nameof(MainViewModel.NextTitle), nameof(MainViewModel.AfterNextTitle), nameof(MainViewModel.CurrentDescription), nameof(MainViewModel.NextDescription), nameof(MainViewModel.AfterNextDescription) }.All(notified.Contains),
            "Viewport loading must notify the card bindings immediately, without a timer tick.");
    }

    private static async Task MissingGuideThenRefreshAsync()
    {
        using var fixture = await Fixture.CreateAsync();
        var model = fixture.Model;
        var row = model.AllChannels.Single();
        model.Active = row;
        await model.LoadViewportAsync([row], updateViewport: true);
        Assert(row.Current is null && row.LogoPath is null, "An unmapped channel must initially show the fallback.");

        await fixture.SeedGuideAsync();
        await fixture.MapAsync("guide-one");
        var notified = new HashSet<string?>();
        model.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        await model.RefreshEpgViewAsync();

        Assert(row.Current?.Title == "Передача один", "The current programme was not refreshed on the existing row.");
        Assert(row.Next?.Title == "Следующая один", "The next programme was not refreshed.");
        Assert(row.AfterNext?.Title == "Затем один" && model.AfterNextTitle == "Затем один" && model.AfterNextDescription == "Вторая следующая передача.",
            "The second upcoming card must load its programme and trimmed description on EPG refresh.");
        Assert(model.CurrentTitle == "Передача один", "The playback panel did not receive the current title.");
        Assert(model.CurrentDescription == "Описание текущей передачи.\nПодробности из телепрограммы." && model.NextDescription == "Описание следующей передачи.",
            "The playback cards must display both descriptions from SQLite without losing paragraphs.");
        Assert(notified.Contains(nameof(MainViewModel.CurrentDescription)) && notified.Contains(nameof(MainViewModel.NextDescription)),
            "EPG refresh must notify both description bindings.");
        Assert(notified.Contains(nameof(MainViewModel.AfterNextTitle)) && notified.Contains(nameof(MainViewModel.AfterNextTime)) && notified.Contains(nameof(MainViewModel.AfterNextDescription)),
            "The second upcoming card must notify every binding.");
        await WaitUntilAsync(() => row.LogoPath == fixture.FirstLogo,
            "The first EPG refresh must retry the visible logo without waiting for negative-cache expiry.");
        Assert(DecodeWidth(row.LogoPath!) == 2, "The resulting logo must be decodable by WPF.");
    }

    private static async Task RemapAsync()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.SeedGuideAsync();
        await fixture.MapAsync("guide-one", manual: true);
        var model = fixture.Model;
        var row = model.AllChannels.Single();
        model.Active = row;
        await model.RefreshEpgViewAsync();
        await model.LoadViewportAsync([row], updateViewport: true);
        await WaitUntilAsync(() => row.LogoPath == fixture.FirstLogo, "The initial mapped logo was not loaded.");

        await fixture.MapAsync("guide-two", manual: true);
        await model.RefreshEpgViewAsync();

        Assert(row.Current?.Title == "Передача два", "Remapping must replace an already populated current programme.");
        Assert(row.Next?.Title == "Следующая два", "Remapping must replace the next programme.");
        Assert(model.CurrentTitle == "Передача два", "Remapping must refresh the playback panel.");
        Assert(model.CurrentDescription == "Описание передачи два." && model.NextDescription == "", "Remapping must replace the description and clear an unavailable next description.");
        Assert(row.AfterNext is null && model.AfterNextDescription == "" && model.AfterNextTime == "",
            "Remapping to a shorter guide must clear the second upcoming card, including its time and description.");
        await WaitUntilAsync(() => row.LogoPath == fixture.SecondLogo, "Remapping must replace an already loaded EPG logo.");
    }

    private static async Task RemoveMappingAsync()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.SeedGuideAsync();
        await fixture.MapAsync("guide-one");
        var model = fixture.Model;
        var row = model.AllChannels.Single();
        model.Active = row;
        await model.RefreshEpgViewAsync();
        await model.LoadViewportAsync([row], updateViewport: true);
        Assert(row.Current is not null, "The test must start with an active programme.");
        await WaitUntilAsync(() => row.LogoPath == fixture.FirstLogo, "The initial logo was not loaded.");

        await model.Store.ReplaceAutomaticMappingsAsync([], [row.Id]);
        await model.RefreshEpgViewAsync();

        Assert(!model.Mappings.ContainsKey(row.Id), "The fixture mapping was not removed.");
        Assert(row.Current is null && row.Next is null && row.AfterNext is null, "Removing a mapping must clear all three previous programmes.");
        Assert(row.NowTitle == "Телепрограмма не назначена" && row.EndTime == "" && row.Progress == 0,
            "Removing a mapping must clear all programme details.");
        Assert(model.CurrentTitle == "Программа пока недоступна", "The playback panel retained a removed programme.");
        Assert(model.CurrentDescription == "" && model.NextDescription == "" && model.AfterNextDescription == "", "Removing a mapping must clear every description.");
    }

    private static async Task ReloadAsync()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.SeedGuideAsync();
        await fixture.MapAsync("guide-one");
        var model = fixture.Model;
        var previous = model.AllChannels.Single();
        model.SelectedChannel = previous;
        await model.RecordPlaybackAsync(previous);
        await model.ToggleFavoriteAsync(previous);
        model.State.Renames[previous.Id] = "Мой канал";
        model.State.CustomCategories[previous.Id] = "Мои каналы";
        await model.SaveAsync();

        await model.ReloadAsync();
        await model.RefreshEpgViewAsync();

        var replacement = model.AllChannels.Single();
        Assert(!ReferenceEquals(previous, replacement), "Reload must create fresh library rows for this regression.");
        Assert(ReferenceEquals(replacement, model.Active), "Active must be rebound to the refreshed channel row.");
        Assert(ReferenceEquals(replacement, model.SelectedChannel), "SelectedChannel must be rebound to the refreshed row.");
        Assert(replacement.IsFavorite && replacement.Name == "Мой канал" && replacement.Group == "Мои каналы",
            "Settings reload lost favorites or custom channel labels.");
        Assert(model.CurrentTitle == "Передача один", "The rebound active row did not regain its programme.");
        Assert(model.CurrentDescription.Contains("Подробности из телепрограммы.") && model.NextDescription == "Описание следующей передачи.", "Reload must restore both programme descriptions.");
        Assert(replacement.AfterNext?.Title == "Затем один" && model.AfterNextDescription == "Вторая следующая передача.", "Reload must also restore the second upcoming programme.");
        await WaitUntilAsync(() => replacement.LogoPath == fixture.FirstLogo, "The rebound row did not regain its logo.");
        var persisted = await model.Store.LoadUserStateAsync();
        Assert(persisted.Favorites.Contains(replacement.Id) && persisted.History.Contains(replacement.Id),
            "Favorite and playback history changes were not persisted.");
    }

    private static async Task ImportPiconsAsync()
    {
        using var fixture = await Fixture.CreateAsync();
        var model = fixture.Model;
        var row = model.AllChannels.Single();
        await model.LoadViewportAsync([row], updateViewport: true);
        Assert(row.LogoPath is null, "The channel must initially have no available logo.");
        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(row.Name + ".png");
            await using var target = entry.Open();
            await using var source = File.OpenRead(fixture.FirstLogo);
            await source.CopyToAsync(target);
        }
        archive.Position = 0;
        Assert(await model.Logos.ImportZipAsync(archive) == 1, "The fixture picon was not imported.");
        await model.RefreshLogosAsync();
        await WaitUntilAsync(() => row.LogoPath is not null, "The imported picon did not replace the fallback immediately.");
        Assert(DecodeWidth(row.LogoPath!) == 2, "The imported picon is not a decodable PNG.");
    }

    private static async Task EpgSetupFailureAsync()
    {
        using var fixture = await Fixture.CreateAsync();
        var model = fixture.Model;
        var busyStates = new List<bool>();
        model.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(model.IsEpgBusy)) busyStates.Add(model.IsEpgBusy);
        };
        // Uri construction fails before the EPG service or any HTTP request is reached.
        model.SetSetting("epg.source", "://invalid-source");
        await model.RefreshEpgAsync();

        Assert(busyStates.SequenceEqual([true, false]), "An EPG setup failure must publish both busy state transitions.");
        Assert(!model.IsEpgBusy && model.CanRefreshEpg && !model.EpgIndeterminate && model.EpgPercent == 0,
            "An EPG setup failure left loading state or disabled controls behind.");
        Assert(model.EpgButtonText == "Подключить EPG.ONE" && model.EpgStatus.Contains("повторить", StringComparison.Ordinal),
            "The failure must leave a visible retry action and explanatory status.");
    }

    private static async Task WaitUntilAsync(Func<bool> ready, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(3)) throw new InvalidOperationException(message);
            await Task.Delay(10);
        }
    }

    private static int DecodeWidth(string path)
    {
        using var input = File.OpenRead(path);
        return BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0].PixelWidth;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        public MainViewModel Model { get; }
        public string FirstLogo { get; }
        public string SecondLogo { get; }
        private readonly string _directory;
        private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

        private Fixture(string directory)
        {
            _directory = directory;
            Model = new MainViewModel(directory);
            FirstLogo = Path.Combine(directory, "one.png");
            SecondLogo = Path.Combine(directory, "two.png");
            WritePng(FirstLogo, Colors.Crimson);
            WritePng(SecondLogo, Colors.RoyalBlue);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "DzhiggaTV.App.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var fixture = new Fixture(directory);
            await fixture.Model.Store.InitializeAsync();
            await fixture.Model.Store.SavePlaylistAsync(new("fixture", "Local fixture", "local-only.m3u8"),
                [new("channel", "fixture", "Тестовый канал", "Тест", "https://example.invalid/not-used")]);
            // Deliberately avoid InitializeAsync: the daily network updater is outside this test boundary.
            await fixture.Model.ReloadAsync();
            await fixture.Model.RefreshEpgViewAsync();
            return fixture;
        }

        public Task SeedGuideAsync() => Model.Store.ImportEpgAsync(async (addChannel, addProgramme, _) =>
        {
            await addChannel(new("guide-one", "Канал один", FirstLogo));
            await addChannel(new("guide-two", "Канал два", SecondLogo));
            await addProgramme(new("guide-one", "Передача один", _now.AddMinutes(-20), _now.AddMinutes(20), "  Описание текущей передачи.\nПодробности из телепрограммы.  "));
            await addProgramme(new("guide-one", "Следующая один", _now.AddMinutes(20), _now.AddMinutes(60), "Описание следующей передачи."));
            await addProgramme(new("guide-one", "Затем один", _now.AddMinutes(60), _now.AddMinutes(120), "  Вторая следующая передача.  "));
            await addProgramme(new("guide-two", "Передача два", _now.AddMinutes(-15), _now.AddMinutes(30), "Описание передачи два."));
            await addProgramme(new("guide-two", "Следующая два", _now.AddMinutes(30), _now.AddMinutes(90), "  "));
        });

        public Task MapAsync(string epgId, bool manual = false) => Model.Store.SaveMappingsAsync(
            [new("channel", epgId, epgId, manual ? "manual" : "automatic", 1, manual)]);

        private static void WritePng(string path, Color color)
        {
            var pixels = new byte[16];
            for (var i = 0; i < 4; ++i)
            {
                pixels[i * 4] = color.B;
                pixels[i * 4 + 1] = color.G;
                pixels[i * 4 + 2] = color.R;
                pixels[i * 4 + 3] = color.A;
            }
            var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(path);
            encoder.Save(file);
        }

        public void Dispose()
        {
            Model.Dispose();
            Model.Logos.Dispose();
            Model.Epg.Dispose();
            Model.Store.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            // Only the unique, absolute directory created by this fixture is removed.
            Directory.Delete(_directory, recursive: true);
        }
    }
}
