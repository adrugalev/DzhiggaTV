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
using DzhiggaTV.Services;

// Opt-in benchmark: local media, new isolated profile, real WPF/libmpv windows.
internal static class Program
{
    private static string[] Arguments = [];
    private static string Arg(string key) => Arguments[Array.IndexOf(Arguments, key) + 1];
    [STAThread]
    private static int Main(string[] args)
    {
        Arguments = args;
        if (args.Contains("--clean-update-cache")) return CleanCache();
        if (args.Contains("--xml-benchmark")) return XmlBenchmarkAsync().GetAwaiter().GetResult();
        if (!new[] { "--fixture", "--data-dir", "--json" }.All(k => Array.IndexOf(args, k) is var i && i >= 0 && i + 1 < args.Length)) return 2;
        string profile = Path.GetFullPath(Arg("--data-dir"));
        if (Directory.Exists(profile) || !File.Exists(Arg("--fixture"))) return 2;
        if (args.Contains("--seed-only")) { SeedAsync(profile).GetAwaiter().GetResult(); return 0; }
        // Generate fixture bitmaps/database in a separate process: its temporary allocations
        // must not inflate the WPF player's managed heap or working-set measurements.
        string host = Environment.ProcessPath ?? throw new InvalidOperationException("Missing benchmark host.");
        var setup = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true };
        if (System.IO.Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            setup.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (string argument in args) setup.ArgumentList.Add(argument);
        setup.ArgumentList.Add("--seed-only");
        using (var seed = Process.Start(setup)!) { seed.WaitForExit(); if (seed.ExitCode != 0) return seed.ExitCode; }
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        app.Startup += async (_, _) =>
        {
            try { await RunAsync(); app.Shutdown(0); }
            catch (Exception ex) { Console.WriteLine(ex); app.Shutdown(1); }
        };
        return app.Run();
    }
    private static int CleanCache()
    {
        Console.WriteLine(JsonSerializer.Serialize(UpdateCacheCleaner.CleanAsync(Arg("--clean-update-cache"), Version.Parse(Arg("--installed-version"))).GetAwaiter().GetResult())); return 0;
    }
    private static async Task<int> XmlBenchmarkAsync()
    {
        string path = Arg("--xml-benchmark"); const int count = 100_000;
        if (!File.Exists(path))
        {
            using var writer = new StreamWriter(path);
            writer.Write("<tv><channel id=\"fixture\"><display-name>Канал</display-name></channel>");
            for (int n = 0; n < count; n++) writer.Write("<programme channel=\"fixture\" start=\"20261020000000 +0000\" stop=\"20261020010000 +0000\"><title>Передача &amp; фильм</title><desc>2026 г., Россия. Описание с Unicode и <![CDATA[символами <>&]]>.</desc><category>Кино</category></programme>");
            writer.Write("</tv>");
        }
        for (int iteration = 0; iteration < 3; iteration++)
        {
            await using var input = File.OpenRead(path);
            long allocated = GC.GetTotalAllocatedBytes(true); var clock = Stopwatch.StartNew(); long checksum = 0;
            var result = await XmlTvParser.ParseAsync(input, _ => Task.CompletedTask, p => { checksum += p.Title.Length + (p.Description?.Length ?? 0); return Task.CompletedTask; });
            Console.WriteLine(JsonSerializer.Serialize(new { Iteration = iteration, Seconds = clock.Elapsed.TotalSeconds, AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated, result.Programmes, Checksum = checksum }));
            if (result.Programmes != count || checksum != 6_800_000) return 1;
        }
        return 0;
    }
    private static async Task SeedAsync(string profile)
    {
        Directory.CreateDirectory(profile);
        // Distinct 512px source logos test both virtualization and image retention after scrolling.
        for (int n = 0; n < 96; n++)
        {
            var pixels = new byte[512 * 512 * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = (byte)(n * 7); pixels[i + 1] = 180; pixels[i + 2] = (byte)(n * 13); pixels[i + 3] = 255; }
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(512, 512, 96, 96, PixelFormats.Bgra32, null, pixels, 512 * 4)));
            using var file = File.Create(Path.Combine(profile, $"logo-{n}.png")); encoder.Save(file);
        }
        var channels = Enumerable.Range(1, 3227).Select(n => new Channel("bench-" + n, "local", "Канал " + n,
            "Категория " + n % 11, Path.GetFullPath(Arg("--fixture")), Logo: Path.Combine(profile, $"logo-{n % 96}.png"), Order: n - 1)).ToArray();
        using var store = new SqliteStore(Path.Combine(profile, "library.db")); await store.InitializeAsync();
        await store.SavePlaylistAsync(new("local", "Performance fixture", "local-only.m3u"), channels);
        var epg = channels.Select(c => new EpgChannel(c.Id, c.Name)).ToArray();
        async IAsyncEnumerable<Programme> Guide()
        {
            await Task.CompletedTask; var start = DateTimeOffset.UtcNow.AddMinutes(-10);
            foreach (var c in channels) for (int i = 0; i < 3; i++)
                yield return new(c.Id, $"Передача {i + 1}", start.AddHours(i), start.AddHours(i + 1), "2026 г., Россия.\n\nКонтрольное описание телепрограммы.");
        }
        await store.ReplaceEpgAsync(Guide(), epg);
        await store.SaveMappingsAsync(channels.Select(c => new ChannelMapping(c.Id, c.Id, c.Name, "Fixture", 1, true)).ToArray());
        var state = new UserState();
        foreach (var (key, value) in new Dictionary<string, string> { ["epg.enabled"] = "False", ["epg.prompted"] = "True", ["resume"] = "False", ["volume"] = "0", ["sports.channels"] = JsonSerializer.Serialize(channels.Take(4).Select(c => c.Id)) }) state.Settings[key] = value;
        await store.SaveUserStateAsync(state);
    }
    private static async Task RunAsync()
    {
        await WaitAsync(() => Application.Current.MainWindow is MainWindow m && m.Model.AllChannels.Count == 3227 && Application.Current.Windows.OfType<OverlayWindow>().Any());
        var main = (MainWindow)Application.Current.MainWindow;
        var samples = new List<object>();
        samples.Add(await MeasureAsync("idle", []));
        await main.PlayAsync(main.Model.AllChannels[0]); await WaitAsync(() => main.Player.State == PlaybackState.Playing);
        samples.Add(await MeasureAsync("main-1080p", [main.Player]));
        Task? multi = null; _ = main.Dispatcher.BeginInvoke(new Action(() => multi = main.ShowSportsEveningAsync()));
        await WaitAsync(() => Application.Current.Windows.OfType<SportsEveningWindow>().Any());
        var sports = Application.Current.Windows.OfType<SportsEveningWindow>().Single(); sports.SetChannels(main.Model.AllChannels.Take(4));
        MpvPlayer[] Players() => Descendants<SportsTile>(sports).Select(t => t.Player).Prepend(main.Player).ToArray();
        await WaitAsync(() => Players().Length == 5 && Players().All(p => p.State == PlaybackState.Playing));
        samples.Add(await MeasureAsync("multiscreen-4-plus-live-main", Players()));
        sports.Close(); if (multi != null) await multi;
        // Exercise recycling across many rows, then check memory after returning to the same viewport.
        var list = (ListBox)main.FindName("ChannelList");
        for (int i = 0; i < 96; i += 4) { list.ScrollIntoView(main.Model.AllChannels[i]); await Task.Delay(100); }
        list.ScrollIntoView(main.Model.AllChannels[0]);
        samples.Add(await MeasureAsync("main-after-scrolling", [main.Player]));
        File.WriteAllText(Arg("--json"), JsonSerializer.Serialize(new { Version = typeof(MainWindow).Assembly.GetName().Version?.ToString(), LogicalProcessors = Environment.ProcessorCount, Samples = samples }, new JsonSerializerOptions { WriteIndented = true }));
        main.Close(); await WaitAsync(() => !main.IsVisible);
    }
    private static async Task<object> MeasureAsync(string name, MpvPlayer[] players)
    {
        await Task.Delay(5000);
        var initial = players.Select(p => p.GetStatistics()).ToArray();
        using var process = Process.GetCurrentProcess(); process.Refresh();
        var cpu = process.TotalProcessorTime; long allocations = GC.GetTotalAllocatedBytes(); var clock = Stopwatch.StartNew();
        int count = Arguments.Contains("--seconds") ? int.Parse(Arg("--seconds")) : 30;
        var memory = new List<long>(); var resident = new List<long>();
        for (int i = 0; i < count; i++) { await Task.Delay(1000); process.Refresh(); memory.Add(process.PrivateMemorySize64); resident.Add(process.WorkingSet64); }
        process.Refresh(); double seconds = clock.Elapsed.TotalSeconds;
        double cpuPercent = (process.TotalProcessorTime - cpu).TotalSeconds / seconds / Environment.ProcessorCount * 100;
        var end = players.Select(p => p.GetStatistics()).ToArray();
        var result = new { Phase = name, Seconds = seconds, CpuPercent = cpuPercent, PrivateBytesMean = memory.Average(), WorkingSetBytesMean = resident.Average(), PrivateBytesPeak = memory.Max(), AllocatedBytesPerSecond = (GC.GetTotalAllocatedBytes() - allocations) / seconds,
            Streams = end.Select((s, i) => new { s.Width, s.Height, s.FramesPerSecond, s.HardwareDecoder, s.VideoOutput, DroppedFrames = s.DroppedFrames - initial[i].DroppedFrames, TimelineSeconds = s.PlaybackPositionSeconds - initial[i].PlaybackPositionSeconds }).ToArray() };
        Console.WriteLine(JsonSerializer.Serialize(result)); return result;
    }
    private static async Task WaitAsync(Func<bool> condition)
    { var timeout = Stopwatch.StartNew(); while (!condition()) { if (timeout.Elapsed > TimeSpan.FromSeconds(40)) throw new TimeoutException(); await Task.Delay(100); } }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T t) yield return t; foreach (var item in Descendants<T>(child)) yield return item; } }
}

