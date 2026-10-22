using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using DzhiggaTV.App;
using DzhiggaTV.Player;
using DzhiggaTV.Remote;

internal static partial class Program
{
    private static async Task RemoteChecks(MainWindow main, FixtureServer fixture, string[] args)
    {
        Check(!main.Remote.Running && !main.RemoteOptions().AutoStart, "Remote is disabled by default in a newly created Windows profile");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        await using var remote = new RemoteServer(new DesktopRemoteLibrary(main), System.IO.Path.Combine(main.Model.DataDirectory, "remote-integration"),
            _ => new("127.0.0.1", 8, "isolated integration", true, false), new SourcePolicy(true));
        await remote.StartAsync(new() { Port = port });
        var cookies = new CookieContainer();
        using var client = new HttpClient(new HttpClientHandler { CookieContainer = cookies, UseProxy = false }) { BaseAddress = new(remote.Url!), Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.Add("Origin", remote.Url);
        var pairUrl = remote.PairingUrl();
        var qr = new System.Windows.Media.Imaging.FormatConvertedBitmap(RemoteQrCode.Create(pairUrl), System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var pixels = new byte[qr.PixelWidth * qr.PixelHeight * 4]; qr.CopyPixels(pixels, qr.PixelWidth * 4, 0);
        var luminance = new ZXing.RGBLuminanceSource(pixels, qr.PixelWidth, qr.PixelHeight, ZXing.RGBLuminanceSource.BitmapFormat.BGRA32);
        var decoded = new ZXing.QrCode.QRCodeReader().decode(new ZXing.BinaryBitmap(new ZXing.Common.HybridBinarizer(luminance)));
        Check(decoded?.Text == pairUrl && pairUrl.Contains("/#pair="), "The production QR bitmap decodes to its exact one-time pairing URL");
        var code = new Uri(pairUrl).Fragment[6..];
        var pair = await client.PostAsJsonAsync("/api/pair", new { code, name = "Integration Safari" });
        var pairId = (await pair.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        Check(remote.Devices.PendingRequests.Count == 1 && !cookies.GetCookies(client.BaseAddress!).Any(), "Windows integration requires explicit approval before exposing the library");
        remote.Devices.Decide(pairId, true); await client.GetAsync("/api/pair/status?id=" + pairId);
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session"); client.DefaultRequestHeaders.Add("X-DzhiggaTV-CSRF", session.GetProperty("csrf").GetString());
        var catalog = await client.GetFromJsonAsync<RemoteCatalog>("/api/catalog");
        Check(catalog is { Total: 3 } && catalog.Channels.All(c => c.Current?.Title == "Текущая передача"), "Remote reads the real shared SQLite catalog and existing mapped EPG");
        var guide = await client.GetFromJsonAsync<RemoteProgramme[]>("/api/guide/archive");
        Check(guide is { Length: > 0 } && guide.Any(p => p.Title == "Будущая передача"), "Remote guide uses the existing programme database");
        var found = await client.GetFromJsonAsync<RemoteCatalog>("/api/catalog?search=" + Uri.EscapeDataString("Будущая"));
        Check(found is { Total: 3 } && found.Channels.All(c => c.Match?.Title == "Будущая передача"), "Mobile search returns the matching future programme alongside the current broadcast");
        var pc = main.Model.Active?.Id; var position = main.Player.GetTimeline().Position;
        var response = await client.PostAsJsonAsync("/api/stream", new { channelId = "live" }); var url = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!;
        var manifest = await client.GetStringAsync(url);
        Check(manifest.StartsWith("#EXTM3U") && !manifest.Contains(fixture.Url) && main.Model.Active?.Id == pc && main.Player.State == PlaybackState.Playing,
            "Mobile HLS is delivered through the proxy without changing or stopping native PC playback");
        main.Model.State.Hidden.Add("three"); await main.Model.SaveAsync();
        catalog = await client.GetFromJsonAsync<RemoteCatalog>("/api/catalog");
        Check(catalog?.Total == 2 && (await client.GetAsync("/api/guide/three")).StatusCode == HttpStatusCode.NotFound, "Hidden Windows channels are excluded from Remote catalog and guide");
        main.Model.State.Collections["Матчи"] = []; await main.Model.SaveAsync();
        async Task<RemotePlayerState> Command(RemoteCommand command)
        { var result = await client.PostAsJsonAsync("/api/command", command); result.EnsureSuccessStatusCode(); return (await result.Content.ReadFromJsonAsync<RemotePlayerState>())!; }
        await Command(new("favorite", "live", 1)); await Command(new("collection.add", "live", Name: "Матчи"));
        catalog = await client.GetFromJsonAsync<RemoteCatalog>("/api/catalog?collection=favorites");
        Check(main.Model.State.Favorites.Contains("live") && main.Model.State.Collections["Матчи"].SequenceEqual(new[] { "live" }) && catalog?.Channels.Single().Id == "live",
            "Favorite and collection commands update the Windows user state and preserve its ordering");
        main.Player.IsMuted = true;
        var state = await Command(new("play", "live")); await Wait(() => main.Player.State == PlaybackState.Playing);
        Check(main.Model.Active?.Id == "live" && main.Model.SelectedChannel?.Id == "live" && state.ChannelId == "live", "PC channel command selects the actual Windows channel row");
        state = await Command(new("volume", Value: 17)); Check(Math.Abs(main.Player.Volume - 17) < .1 && state.Volume == main.Player.Volume && state.Muted, "PC volume command reaches libmpv while respecting mute");
        await Command(new("volume", Value: 0)); state = await Command(new("mute")); Check(!state.Muted && !main.Player.IsMuted, "PC mute command and state stay synchronized");
        main.Player.IsMuted = true;
        state = await Command(new("pause")); Check(state.Paused && main.Player.IsPaused, "Remote pause controls the existing native player"); await Command(new("pause"));
        state = await Command(new("fullscreen", Value: 1)); Check(state.Fullscreen && main.IsFullscreen, "Remote enters the existing Windows fullscreen mode"); await Command(new("fullscreen", Value: 0));
        main.Model.State.Hidden.Remove("three"); await main.Model.SaveAsync();
        await Command(new("multiscreen.open")); await Wait(() => Application.Current.Windows.OfType<SportsEveningWindow>().Any());
        state = await Command(new("multiscreen.layout", Channels: ["archive", "live", null, null]));
        Check(state.Multiscreen.Enabled && state.Multiscreen.Channels.SequenceEqual(new string?[] { "archive", "live", null, null }), "Remote opens the existing multiscreen manager and edits its four slots");
        state = await Command(new("multiscreen.audio", "live")); Check(state.Multiscreen.AudioId == "live", "Remote chooses the existing multiscreen audio source");
        state = await Command(new("multiscreen.focus", "archive")); Check(state.Multiscreen.ExpandedId == "archive", "Remote expands a selected existing multiscreen tile");
        await Command(new("multiscreen.focus", "archive")); await Command(new("multiscreen.close"));
        await Wait(() => !Application.Current.Windows.OfType<SportsEveningWindow>().Any());
        main.WindowState = WindowState.Minimized;
        Check((await client.GetAsync("/api/state")).IsSuccessStatusCode && main.Player.State == PlaybackState.Playing, "Remote and native playback continue while the Windows application is minimized");
        main.WindowState = WindowState.Normal;
        remote.Devices.Revoke(null); Check((await client.GetAsync(url)).StatusCode == HttpStatusCode.Unauthorized && remote.Streams.Active.Count == 0, "Revocation closes integrated mobile streams without stopping the Windows player");
        await remote.StopAsync(); Check(!remote.Running && main.Player.IsInitialized, "Stopping Remote leaves the native video engine initialized");
    }
}
