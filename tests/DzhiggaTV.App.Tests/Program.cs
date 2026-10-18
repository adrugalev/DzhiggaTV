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
            ("First EPG refresh replaces the missing-logo fallback immediately", MissingGuideThenRefreshAsync),
            ("Remapping a loaded channel replaces its programme and logo", RemapAsync),
            ("Removing an automatic mapping clears the previous programme", RemoveMappingAsync),
            ("Settings reload rebinds active and selected channels while preserving user changes", ReloadAsync),
            ("A local picon import replaces the missing-logo fallback immediately", ImportPiconsAsync),
            ("An EPG setup failure restores the retry button and progress state", EpgSetupFailureAsync)
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
        await model.RefreshEpgViewAsync();

        Assert(row.Current?.Title == "Передача один", "The current programme was not refreshed on the existing row.");
        Assert(row.Next?.Title == "Следующая один", "The next programme was not refreshed.");
        Assert(model.CurrentTitle == "Передача один", "The playback panel did not receive the current title.");
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
        Assert(row.Current is null && row.Next is null, "Removing a mapping must clear both previous programmes.");
        Assert(row.NowTitle == "Телепрограмма не назначена" && row.EndTime == "" && row.Progress == 0,
            "Removing a mapping must clear all programme details.");
        Assert(model.CurrentTitle == "Программа пока недоступна", "The playback panel retained a removed programme.");
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
            await addProgramme(new("guide-one", "Передача один", _now.AddMinutes(-20), _now.AddMinutes(20)));
            await addProgramme(new("guide-one", "Следующая один", _now.AddMinutes(20), _now.AddMinutes(60)));
            await addProgramme(new("guide-two", "Передача два", _now.AddMinutes(-15), _now.AddMinutes(30)));
            await addProgramme(new("guide-two", "Следующая два", _now.AddMinutes(30), _now.AddMinutes(90)));
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
