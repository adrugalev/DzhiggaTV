using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        var channels = Enumerable.Range(1, 10).Select(i => new Channel("sport-" + i, "local", "Спортивный канал " + i, i <= 5 ? "Спорт" : "Другие", Path.GetFullPath(media), Order: i - 1)).ToArray();
        using var store = new SqliteStore(Path.Combine(profile, "library.db"));
        await store.InitializeAsync(); await store.SavePlaylistAsync(new("local", "Sports smoke", "local-only.m3u"), channels);
        var state = new UserState();
        foreach (var (key, value) in new Dictionary<string, string> { ["epg.enabled"] = "False", ["epg.prompted"] = "True", ["resume"] = "True", ["last.channel"] = "sport-1", ["sports.channels"] = JsonSerializer.Serialize(channels.Take(4).Select(c => c.Id)), ["sports.audio"] = "sport-3", ["volume"] = "0" }) state.Settings[key] = value;
        await store.SaveUserStateAsync(state);
    }
    private static async Task<int> RunAsync(string[] args, string profile)
    {
        MainWindow? main = null; SportsEveningWindow? sports = null;
        try
        {
            await WaitAsync(() => Application.Current.MainWindow is MainWindow);
            main = (MainWindow)Application.Current.MainWindow;
            await WaitAsync(() => main.Model.AllChannels.Count == 10 && main.Player.State == PlaybackState.Playing);
            Check(main.Player.Volume == 0, "Opening the main controls preserves saved master volume");
            Task? sportsRun = null;
            _ = main.Dispatcher.BeginInvoke(new Action(() => sportsRun = main.ShowSportsEveningAsync()));
            await WaitAsync(() => Application.Current.Windows.OfType<SportsEveningWindow>().Any());
            sports = Application.Current.Windows.OfType<SportsEveningWindow>().Single();
            await WaitAsync(() => Tiles(sports).Length == 4 && Tiles(sports).All(Playing));
            Check(main.Player.IsPaused && main.Player.IsMuted, "Main player is paused and silent while sports plays");
            Check(Tiles(sports).Count(t => !t.IsMuted) == 1 && !Tiles(sports).Single(t => t.Channel.Id == "sport-3").IsMuted, "Four native players decode simultaneously with saved audio source");
            sports.SetChannels(main.Model.AllChannels.Take(9));
            await WaitAsync(() => Tiles(sports).Length == 9 && Tiles(sports).All(Playing));
            var nine = Tiles(sports); var positions = nine.ToDictionary(t => t.Channel.Id, t => t.Player.GetStatistics().PlaybackPositionSeconds);
            await Task.Delay(1300);
            Check(nine.All(t => t.Player.GetStatistics().PlaybackPositionSeconds > positions[t.Channel.Id]), "All nine video timelines advance concurrently");
            Check(nine.All(t => t.Player.GetTracks().Any(x => x.Type == "audio")), "Each tile has a real audio track");
            foreach (var tile in nine)
            {
                sports.SelectAudio(tile.Channel.Id);
                Check(nine.Count(t => !t.IsMuted) == 1 && !tile.IsMuted, "Exclusive native audio: " + tile.Channel.Id);
            }
            var handles = nine.ToDictionary(t => t.Channel.Id, t => Descendants<NativeVideoHost>(t).Single().VideoHandle);
            sports.ToggleFullscreen(); await Task.Delay(600); sports.ToggleFullscreen(); await Task.Delay(600);
            Check(nine.All(t => Descendants<NativeVideoHost>(t).Single().VideoHandle == handles[t.Channel.Id] && Playing(t)), "Fullscreen preserves all nine native surfaces and streams");
            sports.SetChannels(main.Model.AllChannels.Take(4)); await Task.Delay(400);
            Check(nine.Skip(4).All(t => t.Player.State == PlaybackState.Disposed), "Removed tiles release their native players");
            Check(nine.Take(4).All(t => Descendants<NativeVideoHost>(t).Single().VideoHandle == handles[t.Channel.Id] && Playing(t)), "Grid resizing preserves retained streams");
            Check(Tiles(sports).Count(t => !t.IsMuted) == 1, "Removing audio source transfers sound to one remaining tile");
            var unavailable = new ChannelItem(new("broken", "local", "Недоступный канал", "Тест", Path.Combine(profile, "missing-media.mkv")));
            main.Model.AllChannels.Add(unavailable);
            sports.SetChannels(main.Model.AllChannels.Take(3).Append(unavailable));
            await WaitAsync(() => Tiles(sports).Single(t => t.Channel.Id == "broken").Player.State == PlaybackState.Error);
            Check(Tiles(sports).Where(t => t.Channel.Id != "broken").All(Playing), "A failed channel does not interrupt other streams");
            var finalChannels = main.Model.AllChannels.Take(4).Reverse().ToArray();
            sports.SetChannels(finalChannels); sports.SelectAudio("sport-3");
            await WaitAsync(() => Tiles(sports).All(Playing));
            _ = sports.Dispatcher.BeginInvoke(new Action(() => Descendants<Button>(sports).Single(b => Equals(b.Content, "Выбрать каналы")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
            await WaitAsync(() => Application.Current.Windows.OfType<SportsChannelPicker>().Any());
            var picker = Application.Current.Windows.OfType<SportsChannelPicker>().Single();
            picker.Close(); await Task.Delay(150);
            Check(sports.IsVisible && Tiles(sports).All(Playing), "Cancelling channel selection leaves the sports session playing");
            if (args.Contains("--interactive"))
            {
                Console.WriteLine("INTERACTIVE_READY " + profile);
                while (sports.IsVisible) await Task.Delay(500);
            }
            else sports.Close();
            await WaitAsync(() => sportsRun is { IsCompleted: true });
            Check(nine.All(t => t.Player.State == PlaybackState.Disposed), "Closing sports disposes every native player");
            Check(!main.Player.IsPaused && !main.Player.IsMuted, "Returning restores main playback and audio");
            Check(main.Player.Volume == 0, "Sports preserves the master volume on return");
            var saved = await main.Model.Store.LoadUserStateAsync();
            Check(JsonSerializer.Deserialize<string[]>(saved.Settings["sports.channels"])!.Length is >= 1 and <= 9, "Grid selection persists to encrypted user state");
            if (!args.Contains("--interactive")) Check(JsonSerializer.Deserialize<string[]>(saved.Settings["sports.channels"])!.SequenceEqual(finalChannels.Select(c => c.Id)) && saved.Settings["sports.audio"] == "sport-3", "Visible channel order and chosen audio source survive saving");
            Console.WriteLine(JsonSerializer.Serialize(new { passed = Passed, nativePlayers = 9 }));
            return 0;
        }
        catch (Exception error) { Console.WriteLine(JsonSerializer.Serialize(new { passed = Passed, failure = error.GetType().Name, stack = error.StackTrace })); return 1; }
        finally
        {
            if (sports?.IsVisible == true) sports.Close();
            if (main is not null) { Console.WriteLine("Closing isolated main window…"); main.Close(); await WaitAsync(() => !main.IsVisible); Console.WriteLine("Main window closed."); }
        }
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
