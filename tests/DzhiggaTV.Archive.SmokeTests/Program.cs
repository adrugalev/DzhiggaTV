using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DzhiggaTV.App;
using DzhiggaTV.Core;
using DzhiggaTV.Player;

internal static class Program
{
    private static readonly List<string> Passed = [];
    private static readonly DateTimeOffset SeedNow = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.Now.ToUnixTimeSeconds());
    private static readonly Programme Past = new("guide-archive", "Архивный фильм", SeedNow.AddHours(-2), SeedNow.AddHours(-2).AddMinutes(10), "Год: 2026. Страна: Россия.\n\nОписание архивной передачи. Проверка воспроизведения и перемотки.");
    [STAThread]
    private static int Main(string[] args)
    {
        int profileIndex = Array.IndexOf(args, "--data-dir");
        if (args.Length == 0 || !Directory.Exists(args[0]) || profileIndex < 0 || profileIndex + 1 >= args.Length) return 2;
        string profile = Path.GetFullPath(args[profileIndex + 1]); if (Directory.Exists(profile)) return 2;
        using var server = new FixtureServer(args[0]);
        SeedAsync(profile, server.Url).GetAwaiter().GetResult();
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        app.Startup += async (_, _) =>
        {
            int result = 0; MainWindow? main = null;
            try { await Wait(() => app.MainWindow is MainWindow); main = (MainWindow)app.MainWindow; await Run(main, args, server); }
            catch (Exception error) { Console.WriteLine(JsonSerializer.Serialize(new { failure = error.GetType().Name, stack = error.StackTrace, state = main?.Player.State.ToString(), paused = main?.Player.IsPaused, native = main?.Player.GetTimeline(), archive = main?.GetArchiveTimeline(), passed = Passed })); result = 1; }
            finally { if (main is not null) { main.Close(); await Task.Delay(600); } app.Shutdown(result); }
        };
        return app.Run();
    }
    private static async Task SeedAsync(string profile, string url)
    {
        using var store = new SqliteStore(Path.Combine(profile, "library.db")); await store.InitializeAsync();
        var channels = new[] { new Channel("archive", "local", "Канал с архивом", "Тест", url, Attributes: new Dictionary<string, string> { ["tvg-rec"] = "7" }),
            new Channel("live", "local", "Только прямой эфир", "Тест", url, Attributes: new Dictionary<string, string> { ["tvg-rec"] = "0" }),
            new Channel("three", "local", "Архив три дня", "Тест", url, Attributes: new Dictionary<string, string> { ["tvg-rec"] = "3" }) };
        await store.SavePlaylistAsync(new("local", "Local archive fixture", "local-only"), channels);
        var guide = channels.Select(c => new EpgChannel("guide-" + c.Id, c.Name)).ToArray();
        async IAsyncEnumerable<Programme> Programmes()
        {
            await Task.CompletedTask;
            foreach (var c in guide)
            {
                yield return Past with { ChannelId = c.Id };
                yield return new(c.Id, "Четыре дня назад", SeedNow.AddDays(-4), SeedNow.AddDays(-4).AddHours(1), "Прошедшая передача.");
                yield return new(c.Id, "Текущая передача", SeedNow.AddMinutes(-20), SeedNow.AddMinutes(40), "Описание текущего эфира.");
                yield return new(c.Id, "Далее в архивной программе", Past.End, Past.End.AddMinutes(30), "Описание следующей передачи.");
                yield return new(c.Id, "Затем в архивной программе", Past.End.AddMinutes(30), Past.End.AddHours(1), "Описание второй передачи.");
                yield return new(c.Id, "Будущая передача", SeedNow.AddHours(1), SeedNow.AddHours(2), "Будущая передача.");
            }
        }
        await store.ReplaceEpgAsync(Programmes(), guide);
        await store.SaveMappingsAsync(channels.Select(c => new ChannelMapping(c.Id, "guide-" + c.Id, c.Name, "Test", 1, true)).ToArray());
        var state = new UserState(); state.Settings["resume"] = "True"; state.Settings["last.channel"] = "archive"; state.Settings["epg.enabled"] = "False"; state.Settings["epg.prompted"] = "True"; state.Settings["volume"] = "0";
        await store.SaveUserStateAsync(state);
    }
    private static async Task Run(MainWindow main, string[] args, FixtureServer server)
    {
        await Wait(() => main.Model.AllChannels.Count == 3 && main.Player.State == PlaybackState.Playing);
        var channel = main.Model.AllChannels.Single(c => c.Id == "archive");
        var host = Descendants<NativeVideoHost>(main).Single(); var handle = host.VideoHandle;
        Check(main.GetArchiveTimeline() is { IsArchive: false }, "An archive-capable live channel exposes its rewind range");
        var overlay = Application.Current.Windows.OfType<OverlayWindow>().Single();
        if (args.Contains("--mini-window"))
        {
            await MiniWindowChecks(main, overlay, args);
            Console.WriteLine(JsonSerializer.Serialize(new { passed = Passed, count = Passed.Count }));
            return;
        }
        if (args.Contains("--player-menu"))
        {
            await PlayerMenuChecks(main, overlay, args);
            Console.WriteLine(JsonSerializer.Serialize(new { passed = Passed, count = Passed.Count }));
            return;
        }
        await main.Programmes.GoBackAsync();
        await Wait(() => main.Programmes.IsBrowsing);
        var startLink = (System.Windows.Documents.Hyperlink)overlay.FindName("StartProgrammeLink");
        startLink.RaiseEvent(new RoutedEventArgs(System.Windows.Documents.Hyperlink.ClickEvent));
        await Wait(() => main.IsArchivePlayback && main.Player.State == PlaybackState.Playing);
        Check(main.Model.CurrentTitle == "Текущая передача" && server.LastArchiveStart == channel.Current!.Start.ToUnixTimeSeconds(),
            "The start-of-programme link plays the live programme from its exact beginning even while cards browse older history");
        Check(((Button)main.FindName("CurrentProgrammeGuideButton")).Visibility == Visibility.Collapsed &&
            Equals(((Button)overlay.FindName("PlayerGuideButton")).Content, "Телепрограмма") &&
            !Descendants<Button>(overlay).Any(b => Equals(b.Content, "Дата и время")),
            "Archive channels use a single guide action in the player without duplicate card or date/time actions");
        await main.ReturnToLiveAsync(); await Wait(() => main.Player.State == PlaybackState.Playing);
        server.HoldRequests = true;
        await main.PlayAsync(channel);
        await Wait(() => main.Player.State == PlaybackState.Stalled, 11000);
        var status = (PlaybackStatusView)overlay.FindName("PlaybackStatus");
        Check(status.IsVisible && Descendants<TextBlock>(status).Any(t => t.Text == "Трансляция приостановилась"),
            "A real unresponsive HTTP stream displays the persistent stall indicator over native video");
        var root = (FrameworkElement)overlay.FindName("OverlayRoot");
        var centre = status.TransformToAncestor(root).Transform(new System.Windows.Point(status.ActualWidth / 2, status.ActualHeight / 2));
        Check(Math.Abs(centre.X - root.ActualWidth / 2) < 2 && Math.Abs(centre.Y - root.ActualHeight / 2) < 2,
            "The stall card is centred in the video surface independently of hidden playback controls");
        server.HoldRequests = false;
        await Wait(() => main.Player.State == PlaybackState.Playing, 20000);
        Check(status.Visibility == Visibility.Collapsed && host.VideoHandle == handle,
            "A transient network stall clears automatically when playback resumes and keeps the native surface");
        server.HoldRequests = true;
        int retries = 0;
        void CountRetry(object? sender, PlaybackStateChangedEventArgs e) { if (e.State == PlaybackState.Reconnecting) Interlocked.Increment(ref retries); }
        main.Player.StateChanged += CountRetry;
        await main.PlayAsync(channel);
        await Wait(() => main.Player.State == PlaybackState.Error, 100000);
        main.Player.StateChanged -= CountRetry;
        await Task.Delay(200);
        Check(retries == 3 && status.IsVisible && Descendants<Button>(status).Any(b => Equals(b.Content, "Повторить") && b.IsVisible),
            "A permanently silent server exhausts exactly three retries and shows a usable retry action instead of waiting forever");
        server.HoldRequests = false;
        Descendants<Button>(status).Single(b => Equals(b.Content, "Повторить")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Wait(() => main.Player.State == PlaybackState.Playing, 20000);
        Check(status.Visibility == Visibility.Collapsed && host.VideoHandle == handle,
            "Retry after terminal network failure restarts the channel and clears the error indicator");
        var guide = new GuideWindow(main); guide.Show();
        await Wait(() => guide.SelectProgramme(channel.Id, Past.Start));
        var watch = Descendants<Button>(guide).Single(b => Equals(b.Content, "Смотреть в архиве"));
        Check(watch.IsEnabled, "A past EPG programme within retention is explicitly playable from archive");
        watch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Wait(() => main.IsArchivePlayback && !guide.IsVisible && main.Player.State == PlaybackState.Playing);
        Check(server.ArchiveRequests > 0, "Selecting the EPG programme actually requests the provider archive URL");
        Check(main.Model.CurrentTitle == Past.Title && main.Model.Active?.Current?.Title == "Текущая передача" && main.Model.NextTitle == "Далее в архивной программе",
            "Archive title and next programmes follow the recording without corrupting live EPG state");
        await Wait(() => main.Player.GetTimeline().Seekable);
        main.Player.IsPaused = true;
        await main.SeekArchiveToAsync(Past.Start.AddSeconds(45));
        await Wait(() => main.GetArchiveTimeline()!.Position >= Past.Start.AddSeconds(44));
        Check(main.Player.IsPaused && main.Player.Volume == 0 && host.VideoHandle == handle, "Seeking preserves pause, volume and the persistent video surface");
        await main.SeekArchiveToAsync(Past.Start.AddSeconds(5));
        await Wait(() => main.GetArchiveTimeline()!.Position < Past.Start.AddSeconds(8));
        Check(true, "An archive can seek backwards as well as forwards");
        await main.PlayArchiveAtAsync(channel, Past.Start.AddSeconds(50));
        await Wait(() => main.Player.State == PlaybackState.Playing);
        int beforeReload = server.ArchiveRequests; main.Player.IsPaused = true;
        await main.SeekArchiveToAsync(Past.Start.AddSeconds(10));
        await Wait(() => main.GetArchiveTimeline()!.Position is var at && at >= Past.Start.AddSeconds(9) && at < Past.Start.AddSeconds(15));
        Check(server.ArchiveRequests > beforeReload && main.Player.IsPaused,
            "Seeking before the loaded archive rebuilds its provider URL while preserving pause");
        await main.SeekArchiveToAsync(Past.Start.AddMinutes(5));
        await Wait(() => main.Player.State == PlaybackState.Paused && main.Player.GetTimeline().Position is >= 0 and < 5);
        Check(server.LastArchiveStart == Past.Start.AddMinutes(5).ToUnixTimeSeconds() &&
            main.GetArchiveTimeline()!.Position >= Past.Start.AddMinutes(5),
            "Forward seeking beyond cached media reopens the provider at the exact requested UTC, after a backward seek");
        await main.SeekArchiveToAsync(Past.Start.AddSeconds(15));
        await Wait(() => main.Player.State == PlaybackState.Paused && main.Player.GetTimeline().Position is >= 0 and < 5);
        await main.SeekArchiveToAsync(Past.Start.AddMinutes(4));
        await Wait(() => main.Player.State == PlaybackState.Paused && main.Player.GetTimeline().Position is >= 0 and < 5);
        Check(server.LastArchiveStart == Past.Start.AddMinutes(4).ToUnixTimeSeconds() && host.VideoHandle == handle,
            "Repeated backward and forward archive jumps preserve pause, their final target and the video surface");
        var earlierSeek = main.SeekArchiveToAsync(Past.Start.AddMinutes(7));
        var latestSeek = main.SeekArchiveToAsync(Past.Start.AddSeconds(12));
        try { await Task.WhenAll(earlierSeek, latestSeek); } catch (OperationCanceledException) { }
        await Wait(() => main.Player.State == PlaybackState.Paused && main.Player.GetTimeline().Position is >= 0 and < 5);
        Check(server.LastArchiveStart == Past.Start.AddSeconds(12).ToUnixTimeSeconds() &&
            main.GetArchiveTimeline()!.Position is var finalAt && finalAt >= Past.Start.AddSeconds(12) && finalAt < Past.Start.AddSeconds(17),
            "The latest of overlapping archive seek requests wins without an obsolete URL reload");
        await main.PlayArchiveAtAsync(channel, SeedNow.AddHours(-3));
        await Wait(() => main.Player.State == PlaybackState.Playing);
        Check(main.IsArchivePlayback && main.Model.CurrentTitle.StartsWith("Архив · ", StringComparison.Ordinal) && main.Model.PlayingNext is null,
            "Choosing a date and time also plays an archive when EPG data is absent");
        await main.PlayArchiveAsync(channel, Past); await Wait(() => main.Player.State == PlaybackState.Playing);
        main.ToggleFullscreen(); await Task.Delay(350);
        Check(((FrameworkElement)overlay.FindName("ArchiveTimelinePanel")).Visibility == Visibility.Visible &&
            ((Slider)overlay.FindName("ArchiveSeekSlider")).IsMoveToPointEnabled && ((Button)overlay.FindName("ReturnLiveButton")).Visibility == Visibility.Visible,
            "Fullscreen exposes the archive seek bar and a direct return to live");
        main.ToggleFullscreen();
        await main.ReturnToLiveAsync(); await Wait(() => main.Player.State == PlaybackState.Playing);
        Check(!main.IsArchivePlayback && !main.Model.IsArchive && main.Model.CurrentTitle == "Текущая передача", "Returning to live restores live EPG and playback mode");
        var pendingArchive = main.PlayArchiveAtAsync(channel, Past.Start);
        var newerLive = main.ReturnToLiveAsync();
        try { await Task.WhenAll(pendingArchive, newerLive); } catch (OperationCanceledException) { }
        await Wait(() => main.Player.State == PlaybackState.Playing);
        Check(!main.IsArchivePlayback && !main.Model.IsArchive, "A later live request wins over a pending archive programme lookup");
        await main.PlayArchiveAsync(channel, Past); await Wait(() => main.Player.State == PlaybackState.Playing);
        var pendingSeek = main.SeekArchiveToAsync(Past.Start.AddSeconds(20));
        newerLive = main.ReturnToLiveAsync();
        try { await Task.WhenAll(pendingSeek, newerLive); } catch (OperationCanceledException) { }
        await Wait(() => main.Player.State == PlaybackState.Playing);
        Check(!main.IsArchivePlayback && !main.Model.IsArchive, "A late native seek never reloads the previous recording over a newer live request");
        main.ToggleMini(); await Task.Delay(350);
        Check(main.IsMini && main.WindowStyle == WindowStyle.None && System.Windows.Shell.WindowChrome.GetWindowChrome(main)?.CaptionHeight == 0,
            "The mini-player has a resizable border without a native title bar");
        var pin = (Button)overlay.FindName("MiniPinButton");
        Check(main.Topmost && overlay.Topmost, "The mini-player starts pinned with its controls in the same topmost layer");
        pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!main.Topmost && !overlay.Topmost, "The mini-player pin button unpins both video and controls");
        pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(main.Topmost && overlay.Topmost && overlay.FindName("MiniCloseButton") is Button,
            "The pin toggles back on and a separate close button is available");
        main.RestoreWindow(); await Task.Delay(350);
        Check(!main.IsMini && !main.Topmost && host.VideoHandle == handle && main.Player.GetStatistics().Width > 0 &&
            ((FrameworkElement)overlay.FindName("MiniWindowActions")).Visibility == Visibility.Collapsed,
            "Restoring the main window hides mini actions and preserves native playback");
        guide = new GuideWindow(main); guide.Show(); await Wait(() => guide.SelectProgramme("live", Past.Start));
        var unavailable = Descendants<Button>(guide).Single(b => Equals(b.Content, "Архив недоступен"));
        Check(!unavailable.IsEnabled, "A channel without archive never offers a past recording as playable");
        unavailable.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(100);
        Check(!main.IsArchivePlayback && guide.IsVisible, "An unavailable past programme never silently switches to live");
        Descendants<DatePicker>(guide).Single().SelectedDate = SeedNow.AddDays(-4).LocalDateTime.Date;
        await Wait(() => guide.SelectProgramme("three", SeedNow.AddDays(-4)));
        Check(Descendants<Button>(guide).Any(b => Equals(b.Content, "Архив недоступен") && !b.IsEnabled), "Per-channel archive depth rejects a four-day-old programme on a three-day channel");
        Check(guide.SelectProgramme("archive", SeedNow.AddDays(-4)) && Descendants<Button>(guide).Any(b => Equals(b.Content, "Смотреть в архиве") && b.IsEnabled),
            "The same day stays available for a seven-day channel");
        guide.Close();
        await main.PlayAsync(main.Model.AllChannels.Single(c => c.Id == "live"));
        await Wait(() => main.Player.State == PlaybackState.Playing);
        Check(((Button)main.FindName("CurrentProgrammeGuideButton")).Visibility == Visibility.Visible,
            "Channels without archive retain the programme link in the current-programme card");
        await main.PlayAsync(channel); await Wait(() => main.Player.State == PlaybackState.Playing);
        int providerIndex = Array.IndexOf(args, "--provider-playlist");
        if (providerIndex >= 0 && providerIndex + 1 < args.Length)
        {
            var provider = (await M3uParser.ParseFileAsync(args[providerIndex + 1], "provider-test")).Where(c => Catchup.From(c)?.Days == 7 &&
                (c.Name.Contains("Москва 24", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("РБК", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("BBC", StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(c => c.Name.Contains("Москва 24", StringComparison.OrdinalIgnoreCase)).First();
            await main.Model.Store.SavePlaylistAsync(new("provider-test", "Private archive smoke", "local-only"), [provider]); await main.Model.ReloadAsync();
            var actual = main.Model.AllChannels.Single(c => c.Id == provider.Id); var start = DateTimeOffset.Now.AddHours(-2);
            var programme = new Programme("provider", "Проверка архива провайдера", start, start.AddMinutes(3));
            await main.PlayArchiveAsync(actual, programme);
            await Wait(() => main.Player.State == PlaybackState.Playing && main.Player.GetStatistics().Width > 0, 30000);
            await main.SeekArchiveToAsync(start.AddSeconds(30));
            await Wait(() => main.Player.State == PlaybackState.Playing && main.Player.GetTimeline().Position is >= 0 && main.GetArchiveTimeline()!.Position >= start.AddSeconds(28), 20000);
            Check(main.Player.State == PlaybackState.Playing && main.GetArchiveTimeline()!.Position < start.AddSeconds(40),
                "The private provider decodes a real two-hour-old archive and seeks to the requested position");
            await main.SeekArchiveToAsync(start.AddSeconds(5));
            await Wait(() => main.Player.State == PlaybackState.Playing && main.GetArchiveTimeline()!.Position is var at && at >= start.AddSeconds(4) && at < start.AddSeconds(12), 20000);
            await main.SeekArchiveToAsync(start.AddSeconds(120));
            await Wait(() => main.Player.State == PlaybackState.Playing && main.GetArchiveTimeline()!.Position is var at && at >= start.AddSeconds(119) && at < start.AddSeconds(128), 20000);
            Check(main.Player.GetStatistics().Width > 0, "Real provider archive seeks forward two minutes after rewinding, with decoded video at the chosen time");
        }
        if (args.Contains("--visual-only"))
        {
            if (args.Contains("--visual-stall")) { server.HoldRequests = true; await main.PlayAsync(channel); }
            else if (!main.IsArchivePlayback) await main.PlayArchiveAsync(channel, Past);
            main.Player.IsPaused = false;
            Console.WriteLine("VISUAL_READY"); while (main.IsVisible) await Task.Delay(300);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { passed = Passed, count = Passed.Count }));
    }
    private static async Task MiniWindowChecks(MainWindow main, OverlayWindow overlay, string[] args)
    {
        main.Title = "DzhiggaTV · Проверка мини-плеера 1.5.6";
        var host = Descendants<NativeVideoHost>(main).Single(); var handle = host.VideoHandle;
        var originalState = main.WindowState;
        var root = (FrameworkElement)main.FindName("LayoutRoot");
        bool SizeIs(double width, double height) => Math.Abs(main.ActualWidth - width) < 1 && Math.Abs(main.ActualHeight - height) < 1;
        async Task Settled() => await Wait(() => overlay.IsVisible && Math.Abs(overlay.ActualWidth - (main.ActualWidth - 16)) < 2 &&
            Math.Abs(overlay.ActualHeight - (main.ActualHeight - 16)) < 2);
        main.ToggleMini(); await Settled();
        Check(SizeIs(440, 255), "A fresh mini-player opens at the compact default size");
        Check(main.ResizeMode == ResizeMode.CanResize && System.Windows.Shell.WindowChrome.GetWindowChrome(main) is { CaptionHeight: 0, ResizeBorderThickness.Left: 8 },
            "The borderless mini-player exposes native resize borders on all sides");
        var origin = overlay.PointToScreen(new Point()); var inset = main.PointFromScreen(origin);
        Check(root.Margin == new Thickness(8) && Math.Abs(inset.X - 8) < 1 && Math.Abs(inset.Y - 8) < 1,
            "Neither the native video nor the overlay covers the resize frame");
        main.Width = 620; main.Height = 360; await Settled();
        Check(main.Player.State == PlaybackState.Playing && host.VideoHandle == handle && main.Player.Volume == 0,
            "Resizing a live mini-player preserves native playback, its handle and volume");
        main.RestoreWindow(); await Task.Delay(200);
        Check(main.WindowState == originalState && root.Margin == SystemParameters.WindowResizeBorderThickness,
            "Returning to the maximized main window restores its original frame");
        main.ToggleMini(); await Settled();
        Check(SizeIs(620, 360), "Reopening the mini-player restores its custom size");
        main.RestoreWindow(); main.Model.SetSetting("mini.width", 560.2); main.Model.SetSetting("mini.height", 349.8);
        main.ToggleMini(); await Settled();
        Check(SizeIs(440, 255), "The old automatic default migrates to the compact size");
        main.Width = 100; main.Height = 100; await Settled();
        Check(SizeIs(320, 196), "Resizing stops at the compact usable minimum");
        main.RestoreWindow(); main.Model.SetSetting("mini.width", 50000); main.Model.SetSetting("mini.height", 50000);
        main.ToggleMini(); await Settled();
        Check(main.ActualWidth <= SystemParameters.WorkArea.Width && main.ActualHeight <= SystemParameters.WorkArea.Height,
            "Invalid oversized saved bounds are clamped to the available desktop");
        main.RestoreWindow(); main.Model.SetSetting("mini.width", 440); main.Model.SetSetting("mini.height", 255);
        main.ToggleMini(); main.Left = 100; main.Top = 100; await Settled();
        if (args.Contains("--visual-only"))
        {
            Console.WriteLine("VISUAL_READY"); while (main.IsVisible) await Task.Delay(300);
        }
        else { main.Width = 510; main.Height = 290; await Settled(); main.Close(); await Wait(() => !main.IsVisible); }
        int profileIndex = Array.IndexOf(args, "--data-dir");
        using var store = new SqliteStore(Path.Combine(Path.GetFullPath(args[profileIndex + 1]), "library.db"));
        await store.InitializeAsync(); var saved = await store.LoadUserStateAsync();
        Check(saved.Settings.ContainsKey("mini.width") && saved.Settings.ContainsKey("mini.height") &&
            double.Parse(saved.Settings["mini.width"], System.Globalization.CultureInfo.InvariantCulture) == main.ActualWidth &&
            double.Parse(saved.Settings["mini.height"], System.Globalization.CultureInfo.InvariantCulture) == main.ActualHeight,
            "Closing the mini-player directly persists its final resized bounds");
    }

    private static async Task PlayerMenuChecks(MainWindow main, OverlayWindow overlay, string[] args)
    {
        main.Title = "DzhiggaTV · Проверка меню 1.5.5";
        await main.PlayLocalAsync(Path.GetFullPath("work/test-media.mkv"));
        await Wait(() => main.Player.State == PlaybackState.Playing && main.Player.GetTracks().Count(t => t.Type == "audio") == 2);
        var more = (Button)overlay.FindName("MoreButton");
        async Task<ContextMenu> Menu()
        {
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => Mouse.Captured is ContextMenu { IsOpen: true });
            return (ContextMenu)Mouse.Captured;
        }
        void CheckItems(ContextMenu menu, string mode) => Check(menu.Items.OfType<MenuItem>().Select(i => (string)i.Header)
            .SequenceEqual(["Звуковая дорожка", "Соотношение сторон", "Информация о потоке"]) && menu.Items.Count == 3,
            "The " + mode + " menu contains exactly the three requested actions");
        var menu = await Menu(); CheckItems(menu, "main player");
        var audio = (MenuItem)menu.Items[0]; var tracks = main.Player.GetTracks().Where(t => t.Type == "audio").ToArray();
        Check(audio.Items.Count == 2 && audio.Items.OfType<MenuItem>().Count(i => i.IsChecked) == 1,
            "Audio choices show both real native tracks and mark the selected one");
        int other = Array.FindIndex(tracks, t => !t.Selected);
        ((MenuItem)audio.Items[other]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.IsOpen = false;
        await Wait(() => main.Player.GetTracks().Any(t => t.Type == "audio" && t.Id == tracks[other].Id && t.Selected));
        menu = await Menu();
        Check(((MenuItem)((MenuItem)menu.Items[0]).Items[other]).IsChecked, "Reopening the menu reflects an actual audio track change");
        ((MenuItem)((MenuItem)menu.Items[1]).Items[2]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.IsOpen = false;
        menu = await Menu();
        Check(main.Model.Setting("aspect") == "Fit4By3" && ((MenuItem)((MenuItem)menu.Items[1]).Items[2]).IsChecked,
            "Aspect selection changes the player preference and marks it on reopening");
        ((MenuItem)((MenuItem)menu.Items[1]).Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.IsOpen = false;
        menu = await Menu(); ((MenuItem)menu.Items[2]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.IsOpen = false;
        Check(((FrameworkElement)overlay.FindName("StatisticsPanel")).Visibility == Visibility.Visible &&
            Descendants<TextBlock>((FrameworkElement)overlay.FindName("StatisticsPanel")).Any(t => t.Text == "Информация о потоке") &&
            ((TextBlock)overlay.FindName("StatisticsText")).Text.Contains("1280 × 720"),
            "Stream information opens under its new title and shows real decoded video properties");
        overlay.ToggleStreamInformation();
        main.ToggleFullscreen(); await Task.Delay(250); menu = await Menu(); CheckItems(menu, "fullscreen"); menu.IsOpen = false;
        main.RestoreWindow(); main.ToggleMini(); await Task.Delay(250); menu = await Menu(); CheckItems(menu, "mini player"); menu.IsOpen = false;
        main.RestoreWindow(); await Task.Delay(250);
        var root = (FrameworkElement)overlay.FindName("OverlayRoot");
        overlay.ShowVideoMenu(root.PointToScreen(new Point(90, 75)));
        await Wait(() => Mouse.Captured is ContextMenu { IsOpen: true }); menu = (ContextMenu)Mouse.Captured;
        CheckItems(menu, "video context");
        Check(menu.PlacementTarget == root && Math.Abs(menu.HorizontalOffset - 90) < 1 && Math.Abs(menu.VerticalOffset - 75) < 1,
            "The video context menu anchors to the clicked video point with DPI conversion");
        overlay.Hide(); await Task.Delay(100);
        Check(!menu.IsOpen, "Hiding the overlay also dismisses the context menu"); overlay.Show();
        await main.PlayAsync(main.Model.AllChannels.Single(c => c.Id == "archive"));
        await Wait(() => main.Player.State == PlaybackState.Playing);
        if (args.Contains("--visual-only"))
        {
            Console.WriteLine("VISUAL_READY");
            while (main.IsVisible) await Task.Delay(300);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); if (child is T match) yield return match; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
    private static async Task Wait(Func<bool> condition, int milliseconds = 15000)
    { var timer = Stopwatch.StartNew(); while (!condition()) { if (timer.ElapsedMilliseconds > milliseconds) throw new TimeoutException(); await Task.Delay(70); } }
    private static void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); Passed.Add(name); Console.WriteLine("PASS " + name); }

    private sealed class FixtureServer : IDisposable
    {
        private readonly HttpListener _listener = new(); private readonly CancellationTokenSource _stop = new(); private readonly string _directory;
        private int _archives;
        private long _lastArchiveStart;
        public volatile bool HoldRequests;
        public int ArchiveRequests => Volatile.Read(ref _archives);
        public long LastArchiveStart => Interlocked.Read(ref _lastArchiveStart);
        public string Url { get; }
        public FixtureServer(string directory)
        {
            _directory = Path.GetFullPath(directory); using var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start(); int port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
            string origin = $"http://127.0.0.1:{port}/"; Url = origin + "channel/index.m3u8"; _listener.Prefixes.Add(origin); _listener.Start(); _ = Listen();
        }
        private async Task Listen()
        {
            try { while (!_stop.IsCancellationRequested) { var context = await _listener.GetContextAsync().WaitAsync(_stop.Token); _ = Serve(context); } }
            catch (Exception e) when (e is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
        }
        private async Task Serve(HttpListenerContext context)
        {
            try
            {
                if (long.TryParse(context.Request.QueryString["utc"], out var utc)) { Interlocked.Increment(ref _archives); Interlocked.Exchange(ref _lastArchiveStart, utc); }
                string name = Path.GetFileName(context.Request.Url!.AbsolutePath); string file = Path.Combine(_directory, name);
                while (HoldRequests && name.EndsWith(".m3u8")) await Task.Delay(80, _stop.Token);
                if (!File.Exists(file)) { context.Response.StatusCode = 404; return; }
                context.Response.ContentType = name.EndsWith(".m3u8") ? "application/vnd.apple.mpegurl" : "video/mp2t";
                var bytes = await File.ReadAllBytesAsync(file, _stop.Token); context.Response.ContentLength64 = bytes.Length;
                if (context.Request.HttpMethod != "HEAD") await context.Response.OutputStream.WriteAsync(bytes, _stop.Token);
            }
            catch (Exception e) when (e is IOException or HttpListenerException or OperationCanceledException or ObjectDisposedException) { }
            finally { context.Response.Close(); }
        }
        public void Dispose() { _stop.Cancel(); _listener.Close(); _stop.Dispose(); }
    }
}
