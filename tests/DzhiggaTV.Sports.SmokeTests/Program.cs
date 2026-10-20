using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DzhiggaTV.App;
using DzhiggaTV.Core;
using DzhiggaTV.Player;

// Opt-in native WPF integration test. Uses only local generated media and an isolated profile.
// dotnet run --project tests/DzhiggaTV.Sports.SmokeTests -- <local audio/video file> --data-dir <new empty profile> [--interactive]
internal static class Program
{
    private static readonly List<string> Passed = [];
    [STAThread]
    private static int Main(string[] args)
    {
        int profileIndex = Array.IndexOf(args, "--data-dir");
        if (args.Length == 0 || !File.Exists(args[0]) || profileIndex < 0 || profileIndex + 1 >= args.Length)
        { Console.WriteLine("A local audio/video fixture and --data-dir with a new empty profile are required."); return 2; }
        string profile = Path.GetFullPath(args[profileIndex + 1]);
        if (Directory.Exists(profile)) { Console.WriteLine("The test profile must not already exist."); return 2; }
        SeedAsync(profile, args[0]).GetAwaiter().GetResult();
        // Real App.OnStartup receives the required --data-dir argument. No second app or default profile is opened.
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        app.Startup += async (_, _) =>
        {
            int result;
            try { result = await RunAsync(args, profile); }
            catch (Exception error) { Console.WriteLine("Harness failure: " + error.GetType().Name); result = 1; }
            app.Shutdown(result);
        };
        return app.Run();
    }
    private static async Task SeedAsync(string profile, string media)
    {
        Directory.CreateDirectory(profile);
        string logo = Path.Combine(profile, "fixture-logo.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            Enumerable.Repeat(new byte[] { 205, 217, 99, 255 }, 4).SelectMany(p => p).ToArray(), 8)));
        using (var output = File.Create(logo)) encoder.Save(output);
        var channels = Enumerable.Range(1, 180).Select(i => new Channel("sport-" + i, "local",
            (i <= 10 ? "Спортивный канал " : "Дополнительный канал ") + i,
            i <= 5 || i > 10 && i % 2 == 1 ? "Спорт" : "Другие", Path.GetFullPath(media), Logo: logo, Order: i - 1)).ToArray();
        using var store = new SqliteStore(Path.Combine(profile, "library.db"));
        await store.InitializeAsync(); await store.SavePlaylistAsync(new("local", "Sports smoke", "local-only.m3u"), channels);
        var guideChannels = channels.Take(10).Select(c => new EpgChannel("guide-" + c.Id, c.Name)).ToArray();
        async IAsyncEnumerable<Programme> Guide()
        {
            await Task.CompletedTask;
            var start = DateTimeOffset.Now.AddMinutes(-20);
            foreach (var channel in guideChannels)
            for (int i = 0; i < 3; i++)
                yield return new(channel.Id, $"Передача {i + 1} · {channel.Name}", start.AddHours(i), start.AddHours(i + 1),
                    "2026 г., Россия. Спорт.\n\nТестовое описание трансляции. Телепрограмма следует за выбранным источником звука.");
        }
        await store.ReplaceEpgAsync(Guide(), guideChannels);
        await store.SaveMappingsAsync(channels.Take(10).Select(c => new ChannelMapping(c.Id, "guide-" + c.Id, c.Name, "Test", 1, true)).ToArray());
        var state = new UserState();
        state.Favorites.AddRange(channels.Take(80).Select(c => c.Id));
        foreach (var (key, value) in new Dictionary<string, string> { ["epg.enabled"] = "False", ["epg.prompted"] = "True", ["resume"] = "True", ["last.channel"] = "sport-1", ["sports.channels"] = JsonSerializer.Serialize(channels.Skip(1).Take(3).Select(c => c.Id)), ["sports.audio"] = "sport-3", ["volume"] = "0" }) state.Settings[key] = value;
        await store.SaveUserStateAsync(state);
    }
    private static async Task<int> RunAsync(string[] args, string profile)
    {
        MainWindow? main = null; SportsEveningWindow? sports = null;
        try
        {
            await WaitAsync(() => Application.Current.MainWindow is MainWindow);
            main = (MainWindow)Application.Current.MainWindow;
            await WaitAsync(() => main.Model.AllChannels.Count == 180 && main.Player.State == PlaybackState.Playing);
            Check(main.Player.Volume == 0, "Opening the main controls preserves saved master volume");
            Check(main.WindowState == WindowState.Maximized && !main.IsFullscreen, "Startup opens maximized while retaining the normal application controls");
            await Task.Delay(700);
            if (!args.Contains("--visual-only")) await VerifyCategoryScrollAsync(main);
            var mainVideoHandle = Descendants<NativeVideoHost>(main).Single().VideoHandle;
            Task? sportsRun = null;
            _ = main.Dispatcher.BeginInvoke(new Action(() => sportsRun = main.ShowSportsEveningAsync()));
            await WaitAsync(() => Application.Current.Windows.OfType<SportsEveningWindow>().Any());
            sports = Application.Current.Windows.OfType<SportsEveningWindow>().Single();
            await WaitAsync(() => Tiles(sports).Length == 3 && Tiles(sports).All(Playing));
            if (args.Contains("--visual-only"))
            {
                sports.SetChannels(main.Model.AllChannels.Take(4));
                await WaitAsync(() => Tiles(sports).Length == 4 && Tiles(sports).All(Playing));
                foreach (var (flag, id) in new[] { ("--aspect-media", "sport-2"), ("--anamorphic-media", "sport-3"), ("--portrait-media", "sport-4") })
                {
                    int index = Array.IndexOf(args, flag);
                    if (index >= 0 && index + 1 < args.Length) await Tiles(sports).Single(t => t.Channel.Id == id).Player.PlayAsync(Path.GetFullPath(args[index + 1]));
                }
                Console.WriteLine("VISUAL_READY " + profile);
                while (sports.IsVisible) await Task.Delay(300);
                return 0;
            }
            Check(!main.Player.IsPaused && main.Player.IsMuted, "Main player keeps playing silently while sports plays");
            var initialMain = Tiles(sports).Single(t => t.Channel.Id == "sport-1");
            Check(Tiles(sports).Count(t => !t.IsMuted) == 1 && !initialMain.IsMuted && Grid.GetRow(initialMain) == 0 && Grid.GetColumn(initialMain) == 0,
                "Opening replaces the first of three saved screens with the main channel and selects its audio");
            Check(Tiles(sports).Select(t => t.Channel.Id).ToHashSet().SetEquals(["sport-1", "sport-3", "sport-4"]) &&
                !Application.Current.Windows.OfType<SportsChannelPicker>().Any(w => w.IsVisible),
                "Opening preserves the other two channels and does not show the channel picker");
            sports.SetChannels(main.Model.AllChannels.Take(6));
            await WaitAsync(() => Tiles(sports).Length == 4 && Tiles(sports).All(Playing));
            await VerifyVideoLayoutAsync(args, main, sports);
            await VerifyRemovalReflowAsync(main, sports);
            var four = Tiles(sports); var positions = four.ToDictionary(t => t.Channel.Id, t => t.Player.GetStatistics().PlaybackPositionSeconds);
            var mainPosition = main.Player.GetStatistics().PlaybackPositionSeconds;
            await Task.Delay(1300);
            Check(four.All(t => t.Player.GetStatistics().PlaybackPositionSeconds > positions[t.Channel.Id]), "All four video timelines advance concurrently");
            Check(mainPosition is not null && main.Player.GetStatistics().PlaybackPositionSeconds > mainPosition + 0.5 && !main.Player.IsPaused && main.Player.IsMuted,
                "Main stream timeline also advances while four sports channels play");
            Check(four.All(t => t.Player.GetTracks().Any(x => x.Type == "audio")), "Each tile has a real audio track");
            foreach (var tile in four)
            {
                sports.SelectAudio(tile.Channel.Id);
                Check(four.Count(t => !t.IsMuted) == 1 && !tile.IsMuted, "Exclusive native audio: " + tile.Channel.Id);
            }
            var handles = four.ToDictionary(t => t.Channel.Id, t => Descendants<NativeVideoHost>(t).Single().VideoHandle);
            sports.ToggleFullscreen(); await Task.Delay(600); sports.ToggleFullscreen(); await Task.Delay(600);
            Check(four.All(t => Descendants<NativeVideoHost>(t).Single().VideoHandle == handles[t.Channel.Id] && Playing(t)), "Fullscreen preserves all four native surfaces and streams");
            sports.SetChannels(main.Model.AllChannels.Take(3)); await Task.Delay(400);
            Check(four.Single(t => t.Channel.Id == "sport-4").Player.State == PlaybackState.Disposed, "Removed tiles release their native players");
            Check(four.Where(t => t.Channel.Id != "sport-4").All(t => Descendants<NativeVideoHost>(t).Single().VideoHandle == handles[t.Channel.Id] && Playing(t)), "Grid resizing preserves retained streams");
            Check(Tiles(sports).Count(t => !t.IsMuted) == 1, "Removing audio source transfers sound to one remaining tile");
            var unavailable = new ChannelItem(new("broken", "local", "Недоступный канал", "Тест", Path.Combine(profile, "missing-media.mkv")));
            main.Model.AllChannels.Add(unavailable);
            sports.SetChannels(main.Model.AllChannels.Take(3).Append(unavailable));
            await WaitAsync(() => Tiles(sports).Single(t => t.Channel.Id == "broken").Player.State == PlaybackState.Error);
            Check(Tiles(sports).Where(t => t.Channel.Id != "broken").All(Playing), "A failed channel does not interrupt other streams");
            var finalChannels = main.Model.AllChannels.Take(4).Reverse().ToArray();
            sports.SetChannels(finalChannels); sports.SelectAudio("sport-3");
            await WaitAsync(() => Tiles(sports).All(Playing));
            foreach (var channel in main.Model.AllChannels.Where(c => !finalChannels.Contains(c))) main.Model.InvalidateLogo(channel.Id);
            _ = sports.Dispatcher.BeginInvoke(new Action(() => Descendants<Button>(sports).Single(b => Equals(b.Content, "Выбрать каналы")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
            await WaitAsync(() => Application.Current.Windows.OfType<SportsChannelPicker>().Any());
            var picker = Application.Current.Windows.OfType<SportsChannelPicker>().Single();
            var scheme = (Grid)picker.FindName("Scheme");
            var chooserChannels = (ListBox)picker.FindName("Channels");
            var search = (TextBox)picker.FindName("Search");
            var choices = chooserChannels.Items.Cast<SportsChannelChoice>().ToArray();
            await WaitAsync(() => main.Model.AllChannels.Single(c => c.Id == "sport-5").LogoPath is not null);
            Check(!choices.Single(c => c.Channel.Id == "sport-5").IsSelected, "An unselected visible channel loads its logo without being checked");
            Check(((ComboBox)picker.FindName("ScreenCount")).Items.Cast<int>().SequenceEqual([1, 2, 3, 4]), "Only one to four screens are offered");
            var fifthCheckbox = Descendants<CheckBox>(picker).Single(c => c.DataContext is SportsChannelChoice choice && choice.Channel.Id == "sport-5");
            fifthCheckbox.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
            await Task.Delay(80);
            Check(picker.Selection.Count == 4 && !choices.Single(c => c.Channel.Id == "sport-5").IsSelected && fifthCheckbox.IsChecked == false,
                "A fifth checkbox stays visibly unchecked and cannot exceed the four-screen limit");
            search.Text = "no matching channels";
            ((Button)picker.FindName("Reset")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(choices.All(c => !c.IsSelected) && !((Button)picker.FindName("Watch")).IsEnabled && scheme.Children.Count == 4,
                "Reset clears even filtered-out checkboxes and preserves four empty positions");
            search.Clear();
            var tenth = choices.Single(c => c.Channel.Id == "sport-10");
            main.Model.InvalidateLogo(tenth.Channel.Id); chooserChannels.ScrollIntoView(tenth);
            await WaitAsync(() => tenth.Channel.LogoPath is not null);
            Check(!tenth.IsSelected, "Scrolling the picker loads newly visible channel logos without selecting them");
            main.Model.InvalidateLogo(tenth.Channel.Id); search.Text = tenth.Channel.Name;
            await WaitAsync(() => tenth.Channel.LogoPath is not null);
            Check(chooserChannels.Items.Count == 1, "Search refreshes logos for its own visible results");
            search.Clear(); chooserChannels.ScrollIntoView(choices[0]);
            void Drop(string id, int slot)
            {
                if (!picker.PlaceChannel(id, slot)) throw new InvalidOperationException("Channel placement failed.");
            }
            Drop("sport-1", 3); Drop("sport-2", 0); Drop("sport-1", 0);
            Check(picker.Layout.Select(c => c?.Id).SequenceEqual(["sport-1", null, null, "sport-2"]) && choices.Count(c => c.IsSelected) == 2,
                "Dropping on precise positions and swapping updates the same checkbox selection");
            Drop("sport-3", 0);
            Check(!choices.Single(c => c.Channel.Id == "sport-1").IsSelected && choices.Single(c => c.Channel.Id == "sport-3").IsSelected,
                "Replacing a screen unchecks only its previous channel");
            choices.Single(c => c.Channel.Id == "sport-4").IsSelected = true;
            Check(picker.Layout[1]?.Id == "sport-4", "Checkbox selection fills the first available screen");
            picker.Close(); await Task.Delay(150);
            Check(sports.IsVisible && Tiles(sports).All(Playing) && Tiles(sports).Length == 4, "Cancelling edited selection leaves the playing session unchanged");
            var retainedHandles = Tiles(sports).ToDictionary(t => t.Channel.Id, t => Descendants<NativeVideoHost>(t).Single().VideoHandle);
            _ = sports.Dispatcher.BeginInvoke(new Action(() => Descendants<Button>(sports).Single(b => Equals(b.Content, "Выбрать каналы")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
            await WaitAsync(() => Application.Current.Windows.OfType<SportsChannelPicker>().Any());
            picker = Application.Current.Windows.OfType<SportsChannelPicker>().Single();
            scheme = (Grid)picker.FindName("Scheme");
            ((Button)picker.FindName("Reset")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Drop("sport-1", 3); Drop("sport-2", 2); Drop("sport-3", 0);
            ((Button)picker.FindName("Watch")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => Tiles(sports).Length == 3 && Tiles(sports).All(Playing));
            var placed = Tiles(sports);
            Check(JsonSerializer.Deserialize<string?[]>(main.Model.Setting("sports.layout"))!.SequenceEqual(["sport-3", null, "sport-2", "sport-1"]) &&
                !Descendants<TextBlock>(sports).Any(t => t.Text.StartsWith("Экран ", StringComparison.Ordinal)),
                "Watch preserves the edited order while compacting empty positions during playback");
            Check(placed.All(t => Descendants<NativeVideoHost>(t).Single().VideoHandle == retainedHandles[t.Channel.Id] && Playing(t)), "Rearranging existing channels preserves their native surfaces and playback");
            sports.SetChannels(finalChannels); sports.SelectAudio("sport-3");
            await WaitAsync(() => Tiles(sports).All(Playing));
            _ = sports.Dispatcher.BeginInvoke(new Action(() => Descendants<Button>(sports).Single(b => Equals(b.Content, "Выбрать каналы")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
            await WaitAsync(() => Application.Current.Windows.OfType<SportsChannelPicker>().Any());
            picker = Application.Current.Windows.OfType<SportsChannelPicker>().Single();
            picker.Close(); await Task.Delay(150);
            Check(sports.IsVisible && Tiles(sports).All(Playing), "Cancelling channel selection leaves the sports session playing");
            var mainPositionBeforeReturn = main.Player.GetStatistics().PlaybackPositionSeconds;
            if (args.Contains("--interactive"))
            {
                Console.WriteLine("INTERACTIVE_READY " + profile);
                while (sports.IsVisible) await Task.Delay(500);
            }
            else sports.Close();
            await WaitAsync(() => sportsRun is { IsCompleted: true });
            Check(four.All(t => t.Player.State == PlaybackState.Disposed), "Closing sports disposes every native player");
            Check(!main.Player.IsPaused && !main.Player.IsMuted, "Returning restores main audio without pausing playback");
            await Task.Delay(300);
            Check(Descendants<NativeVideoHost>(main).Single().VideoHandle == mainVideoHandle && mainPositionBeforeReturn is not null &&
                main.Player.GetStatistics().PlaybackPositionSeconds > mainPositionBeforeReturn,
                "Returning preserves the main video surface and continuously advancing timeline");
            Check(main.Player.Volume == 0, "Sports preserves the master volume on return");
            var saved = await main.Model.Store.LoadUserStateAsync();
            Check(JsonSerializer.Deserialize<string[]>(saved.Settings["sports.channels"])!.Length is >= 1 and <= 4, "Grid selection persists to encrypted user state");
            if (!args.Contains("--interactive")) Check(JsonSerializer.Deserialize<string[]>(saved.Settings["sports.channels"])!.SequenceEqual(finalChannels.Select(c => c.Id)) && saved.Settings["sports.audio"] == "sport-3", "Visible channel order and chosen audio source survive saving");
            if (!args.Contains("--interactive"))
            {
                await main.PlayAsync(main.Model.AllChannels.Single(c => c.Id == "sport-10"));
                await WaitAsync(() => main.Player.State == PlaybackState.Playing && main.Model.Active?.Id == "sport-10");
                Task? reopenedRun = null;
                _ = main.Dispatcher.BeginInvoke(new Action(() => reopenedRun = main.ShowSportsEveningAsync()));
                await WaitAsync(() => Application.Current.Windows.OfType<SportsEveningWindow>().Any());
                sports = Application.Current.Windows.OfType<SportsEveningWindow>().Single();
                await WaitAsync(() => Tiles(sports).Length == 4 && Tiles(sports).All(Playing));
                var reopenedTiles = Tiles(sports);
                var newMain = reopenedTiles.Single(t => t.Channel.Id == "sport-10");
                Check(reopenedTiles.Count(t => !t.IsMuted) == 1 && !newMain.IsMuted && Grid.GetRow(newMain) == 0 && Grid.GetColumn(newMain) == 0,
                    "Reopening follows the new main channel while preserving the previous four-screen count");
                Check(reopenedTiles.Select(t => t.Channel.Id).ToHashSet().SetEquals(["sport-10", "sport-3", "sport-2", "sport-1"]),
                    "Changing the main channel replaces only the first saved screen");
                sports.Close(); await WaitAsync(() => reopenedRun is { IsCompleted: true });
                Check(reopenedTiles.All(t => t.Player.State == PlaybackState.Disposed), "Reopened multiscreen also releases every native player");
                main.Model.SetSetting("sports.channels", "[]");
                Task? firstUseRun = null;
                _ = main.Dispatcher.BeginInvoke(new Action(() => firstUseRun = main.ShowSportsEveningAsync()));
                await WaitAsync(() => Application.Current.Windows.OfType<SportsEveningWindow>().Any());
                sports = Application.Current.Windows.OfType<SportsEveningWindow>().Single();
                await WaitAsync(() => Tiles(sports).Length == 1 && Tiles(sports).All(Playing));
                var firstUseTile = Tiles(sports).Single();
                Check(firstUseTile.Channel.Id == "sport-10" && !firstUseTile.IsMuted && !Application.Current.Windows.OfType<SportsChannelPicker>().Any(w => w.IsVisible),
                    "First use opens the current main channel directly without an automatic picker");
                sports.Close(); await WaitAsync(() => firstUseRun is { IsCompleted: true });
                Check(firstUseTile.Player.State == PlaybackState.Disposed, "First-use multiscreen closes cleanly");
            }
            Console.WriteLine(JsonSerializer.Serialize(new { passed = Passed, nativePlayers = 4 }));
            return 0;
        }
        catch (Exception error) { Console.WriteLine(JsonSerializer.Serialize(new { passed = Passed, failure = error.GetType().Name, stack = error.StackTrace })); return 1; }
        finally
        {
            if (sports?.IsVisible == true) sports.Close();
            if (main is not null) { Console.WriteLine("Closing isolated main window…"); main.Close(); await WaitAsync(() => !main.IsVisible); Console.WriteLine("Main window closed."); }
        }
    }
    private static async Task VerifyRemovalReflowAsync(MainWindow main, SportsEveningWindow sports)
    {
        static NativeVideoHost Host(SportsTile tile) => Descendants<NativeVideoHost>(tile).Single();
        static double Area(SportsTile tile) => Host(tile).ActualWidth * Host(tile).ActualHeight;
        var original = Tiles(sports).ToDictionary(t => t.Channel.Id, t => new { Tile = t, Handle = Host(t).VideoHandle, Area = Area(t) });
        foreach (string id in new[] { "sport-4", "sport-3", "sport-2" })
        {
            var remove = Tiles(sports).Single(t => t.Channel.Id == id);
            sports.SelectAudio(id);
            Descendants<Button>(remove).Single(b => System.Windows.Automation.AutomationProperties.GetName(b).StartsWith("Убрать:", StringComparison.Ordinal))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(350); sports.UpdateLayout();
            var remaining = Tiles(sports);
            Check(remove.Player.State == PlaybackState.Disposed && remaining.All(t => Host(t).VideoHandle == original[t.Channel.Id].Handle && Playing(t)),
                "Removing " + id + " releases only that player and preserves the other streams");
            Check(remaining.Sum(Area) > remaining.Sum(t => original[t.Channel.Id].Area) * 1.2 &&
                !Descendants<TextBlock>(sports).Any(t => t.Text.StartsWith("Экран ", StringComparison.Ordinal)),
                remaining.Length + " remaining videos grow without vacancy placeholders");
            Check(remaining.Count(t => !t.IsMuted) == 1, "Removal transfers sound to a remaining source");
            Check(remaining.All(t => Math.Abs(Host(t).ActualWidth / Host(t).ActualHeight - t.VideoAspect) < .015), "Reflow preserves source proportions");
        }
        sports.SetChannels(main.Model.AllChannels.Take(4));
        await WaitAsync(() => Tiles(sports).Length == 4 && Tiles(sports).All(Playing));
    }

    private static async Task VerifyVideoLayoutAsync(string[] args, MainWindow main, SportsEveningWindow sports)
    {
        static NativeVideoHost Host(SportsTile tile) => Descendants<NativeVideoHost>(tile).Single();
        void CheckAspects(string message)
        {
            sports.UpdateLayout();
            Check(Tiles(sports).All(t => Host(t).ActualHeight > 0 && Math.Abs(Host(t).ActualWidth / Host(t).ActualHeight - t.VideoAspect) < .015), message);
        }
        string? Argument(string flag) { int index = Array.IndexOf(args, flag); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
        var handles = Tiles(sports).ToDictionary(t => t.Channel.Id, t => Host(t).VideoHandle);
        await Task.Delay(300); CheckAspects("Every native video surface fits its display aspect without added side bars");
        Check(Descendants<Slider>(sports).Single().IsMoveToPointEnabled &&
            Application.Current.Windows.OfType<OverlayWindow>().All(w => ((Slider)w.FindName("VolumeSlider")).IsMoveToPointEnabled),
            "Both player volume controls jump directly to a clicked point");
        var guide = Descendants<Border>(sports).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Телепрограмма канала со звуком");
        Check(guide.IsVisible, "Spare window width displays the audio channel programme beside the videos");
        foreach (string id in new[] { "sport-1", "sport-3" })
        {
            sports.SelectAudio(id); await Task.Delay(150);
            string title = main.Model.AllChannels.Single(c => c.Id == id).Current!.Title;
            Check(Descendants<TextBlock>(guide).Any(t => t.Text == title), "Programme details follow the audio source: " + id);
        }
        var originals = new List<SportsTile>();
        foreach (var (flag, id, aspect) in new[] { ("--aspect-media", "sport-2", 4d / 3), ("--anamorphic-media", "sport-3", 16d / 9), ("--portrait-media", "sport-4", 9d / 16) })
        {
            if (Argument(flag) is not { } media) continue;
            var tile = Tiles(sports).Single(t => t.Channel.Id == id); originals.Add(tile);
            await tile.Player.PlayAsync(Path.GetFullPath(media));
            await WaitAsync(() => Playing(tile) && Math.Abs((tile.Player.GetVideoAspectRatio() ?? 0) - aspect) < .001);
            await Task.Delay(1200); CheckAspects("Mixed source proportions are preserved: " + flag);
            if (flag == "--anamorphic-media")
            {
                var statistics = tile.Player.GetStatistics();
                Check(statistics.Width == 360 && statistics.Height == 288 && Math.Abs(tile.VideoAspect - 16d / 9) < .001,
                    "Anamorphic SD uses display aspect instead of raw pixel dimensions");
            }
        }
        var original = new Size(sports.Width, sports.Height);
        sports.Width = Math.Min(1040, SystemParameters.WorkArea.Width - 24); sports.Height = Math.Min(760, SystemParameters.WorkArea.Height - 36);
        await Task.Delay(300); CheckAspects("Window resizing preserves every mixed video aspect");
        sports.ToggleFullscreen(); await Task.Delay(500); CheckAspects("Fullscreen preserves every mixed video aspect");
        sports.ToggleFullscreen(); await Task.Delay(300);
        Check(Tiles(sports).All(t => Host(t).VideoHandle == handles[t.Channel.Id] && Playing(t)), "Aspect-aware layout preserves all native surfaces and uninterrupted streams");
        sports.Width = original.Width; sports.Height = original.Height;
        foreach (var tile in originals) await tile.Player.PlayAsync(Path.GetFullPath(args[0]));
        await WaitAsync(() => Tiles(sports).All(Playing)); await Task.Delay(1200);
    }
    private static async Task VerifyCategoryScrollAsync(MainWindow main)
    {
        static ScrollViewer Scroll(ListBox list) => Descendants<ScrollViewer>(list).First();
        static async Task AtBottom(ListBox list)
        {
            await list.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            list.UpdateLayout(); Scroll(list).ScrollToBottom();
            await Task.Delay(250);
            Console.WriteLine(JsonSerializer.Serialize(new { scrolling = list.Name, items = list.Items.Count, Scroll(list).VerticalOffset, Scroll(list).ScrollableHeight, Scroll(list).ViewportHeight }));
            await WaitAsync(() => Scroll(list).VerticalOffset > 0); await Task.Delay(150);
        }
        static async Task AtTop(ListBox list, string message)
        {
            await Task.Delay(350); list.UpdateLayout();
            Check(Scroll(list).VerticalOffset < .1 && list.Items.Count > 0 &&
                list.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement first &&
                Math.Abs(first.TranslatePoint(new(), Scroll(list)).Y) < 8, message);
        }
        var list = (ListBox)main.FindName("ChannelList");
        var category = (ComboBox)main.FindName("ChannelCategory");
        foreach (string group in new[] { "Спорт", "Другие", "Все категории" })
        {
            await AtBottom(list); category.SetCurrentValue(ComboBox.SelectedItemProperty, group);
            await AtTop(list, "Main category starts at the first row: " + group);
        }
        // Exercise the separate sidebar path, including reselecting the same category.
        await AtBottom(list);
        var categories = Descendants<Button>(main).Single(b => Equals(b.Tag, "categories"));
        categories.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(150);
        var menu = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(s => s.RootVisual)
            .OfType<DependencyObject>().SelectMany(Descendants<ContextMenu>).Single(m => m.IsOpen);
        menu.Items.Cast<MenuItem>().First(m => m.Header is TextBlock { Text: "Все категории" })
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.IsOpen = false;
        await AtTop(list, "Reselecting the current sidebar category also resets scrolling");

        var previousActive = main.Model.Active;
        main.Model.Active = main.Model.AllChannels.Single(c => c.Id == "sport-179");
        var guide = new GuideWindow(main); guide.Show();
        try
        {
            var rows = Descendants<ListBox>(guide).Single();
            await WaitAsync(() => rows.Items.Count == 180); await Task.Delay(350);
            Check(Scroll(rows).VerticalOffset > 0 && rows.SelectedItem is GuideRow row && row.Channel.Id == "sport-179",
                "Opening the guide still reveals the active channel near the end");
            var guideCategory = Descendants<ComboBox>(guide).Single(c => Equals(c.ToolTip, "Категория"));
            foreach (string group in new[] { "Спорт", "Другие", "Все категории" })
            {
                await AtBottom(rows); guideCategory.SelectedItem = group;
                await AtTop(rows, "Guide category starts at the first row: " + group);
            }
            await AtBottom(rows);
            Descendants<ComboBox>(guide).Single(c => Equals(c.ToolTip, "Плейлист")).SelectedIndex = 1;
            await AtTop(rows, "Changing the guide playlist resets scrolling even with identical rows");
            await AtBottom(rows);
            Descendants<Button>(guide).Single(b => Equals(b.Content, "Сбросить")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await AtTop(rows, "Resetting guide filters starts at the first row");
            Descendants<Button>(guide).Single(b => Equals(b.Content, "Сейчас")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(350);
            Check(Scroll(rows).VerticalOffset > 0 && rows.SelectedItem is GuideRow current && current.Channel.Id == "sport-179",
                "The guide Now button still returns to the active channel");
        }
        finally { guide.Close(); main.Model.Active = previousActive; }

        var picker = new SportsChannelPicker(main, main.Model, main.Model.AllChannels.Take(4)); picker.Show();
        try
        {
            var rows = (ListBox)picker.FindName("Channels"); var pickerCategory = (ComboBox)picker.FindName("Category");
            await Task.Delay(250);
            foreach (string group in new[] { "Спорт", "Другие", "Избранное", "Все категории" })
            {
                await AtBottom(rows); pickerCategory.SelectedItem = group;
                await AtTop(rows, "Multiscreen category starts at the first row: " + group);
            }
        }
        finally { picker.Close(); }
    }
    private static bool Playing(SportsTile tile) => tile.Player.State == PlaybackState.Playing && tile.Player.GetStatistics().Width > 0;
    private static SportsTile[] Tiles(SportsEveningWindow window) => Descendants<SportsTile>(window).ToArray();
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static async Task WaitAsync(Func<bool> ready)
    {
        var time = Stopwatch.StartNew();
        while (!ready()) { if (time.Elapsed > TimeSpan.FromSeconds(35)) throw new TimeoutException(); await Task.Delay(80); }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Passed.Add(message); Console.WriteLine("PASS " + message);
    }
}
