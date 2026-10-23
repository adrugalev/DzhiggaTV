using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DzhiggaTV.App;
using DzhiggaTV.Player;
using DzhiggaTV.Remote;

internal static partial class Program
{
    private static async Task RemoteQuickChecks(MainWindow main, FixtureServer fixture, OverlayWindow overlay, string[] args)
    {
        var actions = (StackPanel)overlay.FindName("PlayerActions");
        Check(actions.Children.IndexOf((UIElement)overlay.FindName("RemoteButton")) == actions.Children.IndexOf((UIElement)overlay.FindName("FavoriteButton")) + 1,
            "Quick Remote is beside Favorite while the settings sidebar launcher remains available");
        main.Model.Search = "Архивный фильм"; await main.Model.RefreshFilterAsync();
        var channel = main.Model.VisibleChannels.Single(c => c.Id == "archive");
        Check(channel.SearchProgramme?.Start == Past.Start, "Windows search exposes the exact retained broadcast");
        var list = (ListBox)main.FindName("ChannelList"); list.ScrollIntoView(channel); list.UpdateLayout();
        var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(channel);
        var label = Descendants<TextBlock>(row).Single(t => t.Text == channel.Name);
        label.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        label.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent });
        await Wait(() => main.IsArchivePlayback && main.Player.State == PlaybackState.Playing && fixture.LastArchiveStart == Past.Start.ToUnixTimeSeconds());
        Check(main.Model.SelectedChannel?.Id == channel.Id && main.Model.PlayingProgramme?.Start == Past.Start, "Clicking a search hit on the already selected channel starts that archive, not live TV");
        main.Model.Search = "Текущая передача"; await main.Model.RefreshFilterAsync();
        await main.PlaySearchResultAsync(channel);
        await Wait(() => !main.IsArchivePlayback && main.Player.State == PlaybackState.Playing);
        Check(!main.IsArchivePlayback, "A current programme search hit returns to live TV");
        main.Model.Search = ""; await main.Model.RefreshFilterAsync();

        var ffmpeg = Environment.GetEnvironmentVariable("DZHIGGATV_TEST_FFMPEG");
        if (!File.Exists(ffmpeg)) throw new InvalidOperationException("Set DZHIGGATV_TEST_FFMPEG to an existing compatible test module.");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        main.Model.SetSetting("remote.options", JsonSerializer.Serialize(new RemoteOptions { Port = port, FfmpegPath = ffmpeg!, DependenciesPrepared = true, AllowViewing = false }));
        await using var remote = new RemoteServer(new DesktopRemoteLibrary(main), Path.Combine(main.Model.DataDirectory, "quick-remote"),
            _ => new("127.0.0.1", 8, "isolated integration", true, false), new SourcePolicy(true));
        var card = new RemoteQuickConnectView(main, remote); var root = (Grid)overlay.Content; root.Children.Add(card); Panel.SetZIndex(card, 20);
        card.FitToViewport(overlay.ActualWidth, overlay.ActualHeight);
        var playing = main.Model.Active?.Id; await card.OpenAsync(); overlay.UpdateLayout();
        var qr = Descendants<Image>(card).Single();
        Check(remote.Running && remote.Options.AllowViewing && remote.Options.AllowTranscoding && main.RemoteOptions().DependenciesPrepared && remote.Options.Delivery == "proxy",
            "One player action enables Remote, viewing, proxy and verified iPhone compatibility");
        Check(qr.Source is not null && card.IsVisible && main.Model.Active?.Id == playing && main.Player.State == PlaybackState.Playing,
            "The QR card appears over the player without stopping native video");
        string DecodeQr()
        {
            var bitmap = new FormatConvertedBitmap((BitmapSource)qr.Source!, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            var luminance = new ZXing.RGBLuminanceSource(pixels, bitmap.PixelWidth, bitmap.PixelHeight, ZXing.RGBLuminanceSource.BitmapFormat.BGRA32);
            return new ZXing.QrCode.QRCodeReader().decode(new ZXing.BinaryBitmap(new ZXing.Common.HybridBinarizer(luminance))).Text;
        }
        var pairingUrl = DecodeQr(); Check(pairingUrl.StartsWith(remote.Url + "/#pair="), "The quick QR decodes to the real one-time LAN pairing link");
        foreach (var size in new[] { new Size(360, 320), new Size(900, 540), new Size(1920, 1080) })
        {
            card.FitToViewport(size.Width, size.Height); card.Measure(size); card.Arrange(new Rect(size));
            Check(card.DesiredSize.Width <= size.Width && card.DesiredSize.Height <= size.Height, $"QR card fits its player viewport at {size.Width} × {size.Height}");
        }
        card.FitToViewport(overlay.ActualWidth, overlay.ActualHeight); overlay.UpdateLayout();
        int imageIndex = Array.IndexOf(args, "--quick-image");
        if (imageIndex >= 0)
        {
            var target = new RenderTargetBitmap((int)Math.Ceiling(card.ActualWidth), (int)Math.Ceiling(card.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen()) context.DrawRectangle(new VisualBrush(card), null, new Rect(0, 0, card.ActualWidth, card.ActualHeight));
            target.Render(drawing); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(target));
            using var image = File.Create(args[imageIndex + 1]); png.Save(image);
        }
        var cookies = new CookieContainer(); using var client = new HttpClient(new HttpClientHandler { CookieContainer = cookies, UseProxy = false }) { BaseAddress = new(remote.Url!) };
        client.DefaultRequestHeaders.Add("Origin", remote.Url);
        var response = await client.PostAsJsonAsync("/api/pair", new { code = new Uri(pairingUrl).Fragment[6..], name = "Тестовый iPhone" });
        var pairId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        await Wait(() => qr.Source is null);
        Check(remote.Devices.PendingRequests.Count == 1, "Scanning the quick QR still requires approval on the PC");
        remote.Devices.Decide(pairId, true); await client.GetAsync("/api/pair/status?id=" + pairId);
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session"); client.DefaultRequestHeaders.Add("X-DzhiggaTV-CSRF", session.GetProperty("csrf").GetString());
        await Wait(() => Descendants<TextBlock>(card).Any(t => t.Text.Contains("Приятного просмотра")));
        response = await client.PostAsJsonAsync("/api/stream", new { channelId = "archive", programmeStart = Past.Start }); response.EnsureSuccessStatusCode();
        var url = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!;
        var manifest = await client.GetStringAsync(url);
        Check(manifest.StartsWith("#EXTM3U") && fixture.LastArchiveStart == Past.Start.ToUnixTimeSeconds() && !main.IsArchivePlayback,
            "Mobile HTTP selection resolves the canonical archived broadcast without changing PC playback");
        card.Dismiss(); Check(card.Visibility == Visibility.Collapsed && remote.Running && remote.Streams.Active.Count == 1, "Closing the QR card leaves Remote and an existing mobile stream running");
        await card.OpenAsync();
        Check(remote.Streams.Active.Count == 1 && (await client.GetStringAsync(url)).StartsWith("#EXTM3U"), "Reopening quick Remote keeps an already compatible server and its active stream");
        foreach (var invalid in new[] { (Channel: "live", Start: Past.Start), (Channel: "archive", Start: Past.Start.AddSeconds(1)), (Channel: "archive", Start: SeedNow.AddHours(10)), (Channel: "three", Start: SeedNow.AddDays(-4)) })
        {
            response = await client.PostAsJsonAsync("/api/stream", new { channelId = invalid.Channel, programmeStart = invalid.Start });
            Check(response.StatusCode == HttpStatusCode.NotFound && remote.Streams.Active.Count == 1, "Unavailable, invented or expired archive selection is rejected without replacing playback");
        }
        response = await client.PostAsJsonAsync("/api/command", new RemoteCommand("play", "archive", ProgrammeStart: Past.Start)); response.EnsureSuccessStatusCode();
        await Wait(() => main.IsArchivePlayback && main.Model.PlayingProgramme?.Start == Past.Start);
        Check(main.IsArchivePlayback, "The web PC-control command also selects the exact archived broadcast");
        await EpgTimingAndArchiveChecks(main, fixture, overlay, remote, client, args);
        card.Dismiss(); root.Children.Remove(card); await remote.StopAsync();
        // A close immediately after opening cannot start the server after asynchronous preparation.
        var cancelled = new RemoteQuickConnectView(main, remote); root.Children.Add(cancelled);
        var opening = cancelled.OpenAsync(); cancelled.Dismiss(); await opening;
        Check(!remote.Running && cancelled.Visibility == Visibility.Collapsed, "Dismissing preparation cannot turn Remote on later");
        root.Children.Remove(cancelled);
    }
}
