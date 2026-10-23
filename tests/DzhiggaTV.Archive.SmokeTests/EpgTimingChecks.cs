using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DzhiggaTV.App;
using DzhiggaTV.Core;
using DzhiggaTV.Player;
using DzhiggaTV.Remote;

internal static partial class Program
{
    private static async Task EpgTimingAndArchiveChecks(MainWindow main, FixtureServer fixture, OverlayWindow overlay, RemoteServer remote, HttpClient client, string[] args)
    {
        var channel = main.Model.AllChannels.Single(c => c.Id == "archive");
        await main.PlayAsync(channel); await Wait(() => main.Player.State == PlaybackState.Playing);
        var original = channel.Current!;
        var modal = main.Dispatcher.InvokeAsync(() => new EpgTimingWindow(main, main, channel).ShowDialog());
        await Wait(() => Application.Current.Windows.OfType<EpgTimingWindow>().Any(w => w.IsVisible));
        var timing = Application.Current.Windows.OfType<EpgTimingWindow>().Single();
        Check(Descendants<ComboBox>(timing).Single().SelectedItem is string name && name == channel.Name, "The correction selector displays the channel name instead of an object type");
        Descendants<TextBox>(timing).Single().Text = "10";
        await Wait(() => Descendants<TextBlock>(timing).Any(t => t.Text.StartsWith("С учётом поправки сейчас:") && t.Text.Contains(original.Start.AddMinutes(10).ToLocalTime().ToString("HH:mm"))));
        int imageIndex = Array.IndexOf(args, "--timing-image");
        if (imageIndex >= 0)
        {
            timing.UpdateLayout(); var content = (FrameworkElement)timing.Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                context.DrawRectangle(timing.Background, null, new Rect(content.RenderSize));
                context.DrawRectangle(new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(content.RenderSize), Stretch = Stretch.Fill }, null, new Rect(content.RenderSize));
            }
            bitmap.Render(drawing);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(args[imageIndex + 1]); png.Save(output);
        }
        Descendants<Button>(timing).Single(b => Equals(b.Content, "Применить")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await modal.Task;
        Check(main.Model.EpgOffset(channel.Id) == 10 && channel.Current?.Start == original.Start.AddMinutes(10), "The actual correction dialog applies +10 minutes to the selected Windows channel");
        Check((await main.Model.Store.LoadUserStateAsync()).EpgOffsets[channel.Id] == 10 && main.Model.AllChannels.Single(c => c.Id == "live").Current?.Start == original.Start, "The correction is persisted and leaves other channels unchanged");
        var catalog = await client.GetFromJsonAsync<RemoteCatalog>("/api/catalog?ids=archive");
        Check(catalog?.Channels.Single().Current?.Start == original.Start.AddMinutes(10), "Remote catalog uses the same corrected current programme");
        var guide = await client.GetFromJsonAsync<RemoteProgramme[]>("/api/guide/archive?from=" + Uri.EscapeDataString(Past.Start.AddHours(-1).ToString("o")));
        Check(guide!.Any(p => p.Title == Past.Title && p.Start == Past.Start.AddMinutes(10)), "Remote guide uses corrected start times for archive selection");
        var search = await client.GetFromJsonAsync<RemoteCatalog>("/api/catalog?search=" + Uri.EscapeDataString(Past.Title));
        Check(search!.Channels.Single(c => c.Id == "archive").Match?.Start == Past.Start.AddMinutes(10), "Remote search returns the corrected archive record");
        main.Model.Search = Past.Title; await main.Model.RefreshFilterAsync();
        Check(channel.SearchProgramme?.Start == Past.Start.AddMinutes(10), "Windows search returns the same corrected archive record");
        main.Model.Search = ""; await main.Model.RefreshFilterAsync();
        var response = await client.PostAsJsonAsync("/api/stream", new { channelId = channel.Id, programmeStart = Past.Start.AddMinutes(10) }); response.EnsureSuccessStatusCode();
        string url = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!; await client.GetStringAsync(url);
        Check(fixture.LastArchiveStart == Past.Start.AddMinutes(10).ToUnixTimeSeconds(), "Mobile archive starts at corrected broadcast UTC without applying the shift twice");
        response = await client.PostAsJsonAsync("/api/command", new RemoteCommand("play", channel.Id, ProgrammeStart: Past.Start.AddMinutes(10))); response.EnsureSuccessStatusCode();
        await Wait(() => main.IsArchivePlayback && main.Player.State == PlaybackState.Playing);
        Check(main.Model.PlayingProgramme?.Start == Past.Start.AddMinutes(10) && fixture.LastArchiveStart == Past.Start.AddMinutes(10).ToUnixTimeSeconds(), "PC control resolves and plays the corrected canonical archive entry");
        await main.Model.SetEpgOffsetAsync(channel.Id, -7);
        Check(channel.Current?.Start == original.Start.AddMinutes(-7), "Negative correction is also applied in the selected channel");
        await main.Model.SetEpgOffsetAsync(channel.Id, 0);
        Check(!main.Model.State.EpgOffsets.ContainsKey(channel.Id) && channel.Current?.Start == original.Start, "Reset removes the override and restores the original source schedule");
        await main.PlayAsync(channel); await Wait(() => main.Player.State == PlaybackState.Playing);
        response = await client.PostAsJsonAsync("/api/stream", new { channelId = "live" }); response.EnsureSuccessStatusCode();
        var liveUrl = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!;
        var streamBefore = remote.Streams.Active.Single();
        fixture.EmptyArchive = true;
        try
        {
            var handle = Descendants<NativeVideoHost>(main).Single().VideoHandle;
            await main.GuardAsync(() => main.PlayArchiveAsync(channel, Past));
            Check(!main.IsArchivePlayback && main.Player.State == PlaybackState.Playing && Descendants<NativeVideoHost>(main).Single().VideoHandle == handle,
                "An empty archive never replaces the working native live stream");
            Check(((TextBlock)overlay.FindName("ArchiveNoticeText")).Text.Contains("пустой архив") && ((Border)overlay.FindName("ArchiveNotice")).Visibility == Visibility.Visible,
                "Windows explains that the provider returned an empty recording");
            response = await client.PostAsJsonAsync("/api/stream", new { channelId = channel.Id, programmeStart = Past.Start });
            Check(response.StatusCode == HttpStatusCode.Conflict && (await response.Content.ReadAsStringAsync()).Contains("пустой архив"), "Remote reports empty archive availability before creating a replacement stream");
            Check(remote.Streams.Active.Single() == streamBefore && (await client.GetStringAsync(liveUrl)).Contains("#EXTINF"), "The existing mobile live stream remains usable after archive refusal");
        }
        finally { fixture.EmptyArchive = false; }
    }
}
