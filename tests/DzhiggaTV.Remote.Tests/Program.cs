using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DzhiggaTV.Remote;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

var results = new List<string>();
void Assert(bool condition, string name) { if (!condition) throw new Exception(name); results.Add(name); Console.WriteLine("PASS " + name); }
int Port() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
string directory = Path.Combine(Path.GetTempPath(), "DzhiggaTV-remote-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
if (args.Contains("--networks")) { Console.WriteLine(JsonSerializer.Serialize(RemoteNetwork.Addresses())); return; }
var upstreamPort = Port(); var appBuilder = WebApplication.CreateSlimBuilder(); appBuilder.Logging.ClearProviders(); appBuilder.WebHost.UseUrls($"http://127.0.0.1:{upstreamPort}");
await using var upstream = appBuilder.Build();
var segment = Enumerable.Range(0, 4096).Select(n => (byte)(n % 256)).ToArray();
bool rangeSeen = false, headerSeen = false, redirected = false;
string root = $"http://127.0.0.1:{upstreamPort}";
upstream.MapGet("/entry", (HttpContext c) => { redirected = true; c.Response.Redirect("/hls/master.m3u8?secret=never-on-phone"); return Task.CompletedTask; });
upstream.MapGet("/hls/master.m3u8", (HttpContext c) => { headerSeen = c.Request.Headers["User-Agent"] == "FixturePlayer"; return Results.Text("#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"a\",NAME=\"Русский\",URI=\"audio/main.m3u8\"\n#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"s\",NAME=\"Русский\",URI=\"sub/main.m3u8\"\n#EXT-X-STREAM-INF:BANDWIDTH=1000000,AUDIO=\"a\",CODECS=\"avc1.4d401f,mp4a.40.2\"\nvideo/main.m3u8?secret=private\n", "application/vnd.apple.mpegurl"); });
upstream.MapGet("/hls/video/main.m3u8", () => Results.Text("#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXT-X-MAP:URI=\"../init.mp4\",BYTERANGE=\"10@0\"\n#EXT-X-KEY:METHOD=AES-128,URI=\"../key.bin?secret=key\"\n#EXTINF:6,\n../segment.ts?secret=segment\n#EXT-X-ENDLIST\n", "application/vnd.apple.mpegurl"));
upstream.MapGet("/hls/audio/main.m3u8", () => Results.Text("#EXTM3U\n#EXTINF:6,\n../segment.ts\n#EXT-X-ENDLIST\n", "application/vnd.apple.mpegurl"));
upstream.MapGet("/hls/sub/main.m3u8", () => Results.Text("#EXTM3U\n#EXTINF:6,\n../sub.vtt\n#EXT-X-ENDLIST\n", "application/vnd.apple.mpegurl"));
upstream.MapGet("/hls/sub.vtt", () => Results.Text("WEBVTT\n\n00:00.000 --> 00:06.000\nSubtitle\n", "text/vtt"));
upstream.MapGet("/hls/key.bin", () => Results.Bytes(new byte[16]));
upstream.MapGet("/hls/init.mp4", () => Results.Bytes(segment, "video/mp4", enableRangeProcessing: true));
upstream.MapGet("/hls/segment.ts", (HttpContext c) => { rangeSeen |= c.Request.Headers.Range == "bytes=5-14"; return Results.Bytes(segment, "video/mp2t", enableRangeProcessing: true); });
upstream.MapGet("/bad.m3u8", () => Results.Text("#EXTM3U\nhttp://169.254.169.254/latest/meta-data/\n", "application/vnd.apple.mpegurl"));
upstream.MapGet("/slow.m3u8", async (HttpContext c) => { await Task.Delay(20000, c.RequestAborted); return Results.Text("#EXTM3U\n"); });
upstream.MapGet("/drm.m3u8", () => Results.Text("#EXTM3U\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://content\"\n", "application/vnd.apple.mpegurl"));
upstream.MapGet("/raw", () => Results.Bytes(segment, "video/mp2t"));
upstream.MapGet("/private-redirect", (HttpContext c) => { c.Response.Redirect("http://169.254.169.254/latest/meta-data"); return Task.CompletedTask; });
var mediaArgument = Array.IndexOf(args, "--media");
if (mediaArgument >= 0 && mediaArgument + 1 < args.Length)
{
    var mediaRoot = Path.GetFullPath(args[mediaArgument + 1]);
    upstream.MapGet("/sample/{file}", (string file) =>
    {
        if (!Regex.IsMatch(file, "^(index\\.m3u8|segment[0-9]+\\.ts)$")) return Results.NotFound();
        var path = Path.Combine(mediaRoot, file);
        return File.Exists(path) ? Results.File(path, file.EndsWith("m3u8") ? "application/vnd.apple.mpegurl" : "video/mp2t", enableRangeProcessing: true) : Results.NotFound();
    });
}
await upstream.StartAsync();
var library = new FixtureLibrary(new Uri(root + "/entry"), Path.Combine(directory, "logo.img"));
await File.WriteAllBytesAsync(library.Logo, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
bool privateProfile = true;
await using var server = new RemoteServer(library, directory, _ => new("127.0.0.1", 8, "isolated test", privateProfile, false), new SourcePolicy(true));
var options = new RemoteOptions { Port = Port(), MaximumStreams = 2 };
int ffmpegIndex = Array.IndexOf(args, "--ffmpeg");
if (ffmpegIndex >= 0 && ffmpegIndex + 1 < args.Length) options = options with { AllowTranscoding = true, FfmpegPath = Path.GetFullPath(args[ffmpegIndex + 1]) };
await server.StartAsync(options);
var cookies = new CookieContainer();
using var handler = new HttpClientHandler { CookieContainer = cookies, UseProxy = false };
using var client = new HttpClient(handler) { BaseAddress = new(server.Url!), Timeout = TimeSpan.FromSeconds(45) };
client.DefaultRequestHeaders.Add("Origin", server.Url);
if (args.Contains("--serve"))
{
    if (mediaArgument >= 0) library.Uri = new(root + "/sample/index.m3u8");
    library.Demo = true;
    Console.WriteLine("ISOLATED_FIXTURE " + server.PairingUrl());
    Console.WriteLine("Only synthetic catalog data; fixture pairing is approved by this test process.");
    using var serveLifetime = new CancellationTokenSource(TimeSpan.FromMinutes(25));
    try
    {
        while (!serveLifetime.IsCancellationRequested)
        {
            foreach (var pending in server.Devices.PendingRequests) server.Devices.Decide(pending.Id, true);
            await Task.Delay(400, serveLifetime.Token);
        }
    }
    catch (OperationCanceledException) { }
    return;
}
async Task<HttpResponseMessage> Post(string path, object body, string? csrf = null)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
    if (csrf is not null) request.Headers.Add("X-DzhiggaTV-CSRF", csrf);
    return await client.SendAsync(request);
}
async Task<string> JsonString(HttpResponseMessage response, string name) => (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty(name).GetString()!;
async Task<string> Pair()
{
    var code = new Uri(server.PairingUrl()).Fragment[6..];
    var response = await Post("/api/pair", new { code, name = "iPhone fixture" });
    var id = await JsonString(response, "id"); server.Devices.Decide(id, true);
    var approved = await client.GetAsync("/api/pair/status?id=" + id); Assert(await JsonString(approved, "status") == "approved", "desktop approval issues a device cookie");
    return await JsonString(await client.GetAsync("/api/session"), "csrf");
}
try
{
    Assert(server.Running, "server starts on exactly the chosen interface");
    var html = await client.GetStringAsync("/"); Assert(html.Contains("viewport-fit=cover") && html.Contains("assets/"), "embedded mobile application is served without Node");
    var icon = await client.GetByteArrayAsync("/icon-192.png");
    Assert(icon.Length > 1000 && System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(icon.AsSpan(16, 4)) == 192, "home screen assets include a real 192px PNG icon");
    Assert((await client.GetAsync("/api/catalog")).StatusCode == HttpStatusCode.Unauthorized, "unauthenticated catalog is blocked");
    Assert((await Post("/api/command", new { action = "next" })).StatusCode == HttpStatusCode.Unauthorized, "unauthenticated control is blocked");
    Assert((await client.GetAsync("/encoder/unknown/root")).StatusCode == HttpStatusCode.Unauthorized, "internal encoder endpoint rejects ordinary browser requests");
    var code = new Uri(server.PairingUrl()).Fragment[6..];
    var begun = await Post("/api/pair", new { code, name = "Safari" }); var pendingId = await JsonString(begun, "id");
    Assert(server.Devices.PendingRequests.Single().Id == pendingId, "pairing waits for desktop confirmation");
    Assert((await Post("/api/pair", new { code, name = "imposter" })).StatusCode == HttpStatusCode.Forbidden, "QR code is consumed atomically");
    Assert(await JsonString(await client.GetAsync("/api/pair/status?id=" + pendingId), "status") == "pending", "phone cannot approve itself");
    server.Devices.Decide(pendingId, false);
    Assert(await JsonString(await client.GetAsync("/api/pair/status?id=" + pendingId), "status") == "denied", "desktop denial is delivered");
    var csrf = await Pair();
    var persisted = await File.ReadAllTextAsync(Path.Combine(directory, "remote-devices.json"));
    var credential = cookies.GetCookies(client.BaseAddress!)[DeviceRegistry.CookieName]!.Value;
    Assert(!persisted.Contains(credential), "persistent registry stores only hashed session credentials");
    var registry = new DeviceRegistry(directory); Assert(registry.Authenticate(credential) is not null, "trusted device reconnects after registry reload");
    Assert((await client.GetAsync("/api/catalog")).IsSuccessStatusCode, "paired device receives catalog JSON");
    var catalog = await client.GetStringAsync("/api/catalog"); Assert(catalog.Contains("Fixture TV") && !catalog.Contains("never-on-phone") && !catalog.Contains("http:"), "catalog contains EPG but no provider URLs: " + catalog.Length + " bytes");
    var logo = await client.GetAsync("/api/logo/one"); Assert(logo.IsSuccessStatusCode && logo.Content.Headers.ContentType?.MediaType == "image/png", "cached .img logo uses its detected image type");
    Assert((await Post("/api/command", new { action = "next" })).StatusCode == HttpStatusCode.Forbidden, "CSRF header is mandatory for commands");
    using (var bad = new HttpRequestMessage(HttpMethod.Post, "/api/command") { Content = JsonContent.Create(new { action = "mute" }) })
    { bad.Headers.Add("Origin", "https://untrusted.example"); bad.Headers.Add("X-DzhiggaTV-CSRF", csrf); Assert((await client.SendAsync(bad)).StatusCode == HttpStatusCode.Forbidden, "foreign origin is rejected even with a CSRF token"); }
    using (var badHost = new HttpRequestMessage(HttpMethod.Get, "/api/session")) { badHost.Headers.Host = "rebound.example"; Assert((await client.SendAsync(badHost)).StatusCode == HttpStatusCode.Forbidden, "DNS rebinding host header is rejected"); }
    Assert((await Post("/api/command", new { action = "next" }, csrf)).IsSuccessStatusCode && library.Commands == 1, "remote control reaches the desktop bridge");
    var stream = await Post("/api/stream", new { channelId = "one" }, csrf); var url = await JsonString(stream, "url");
    Assert(library.Commands == 1, "starting mobile playback does not change the PC channel");
    var master = await client.GetStringAsync(url);
    Assert(redirected && headerSeen, "upstream redirect and required source headers work");
    Assert(!master.Contains("secret") && !master.Contains(root) && master.Contains("#EXT-X-MEDIA"), "master variants and audio/subtitle URIs are rewritten without tokens");
    var mediaPath = master.Split('\n').First(x => x.StartsWith("/media/"));
    var media = await client.GetStringAsync(mediaPath);
    Assert(media.Contains("#EXT-X-MAP:URI=\"/media/") && media.Contains("#EXT-X-KEY:METHOD=AES-128,URI=\"/media/"), "fMP4 init and AES key references are rewritten");
    var segmentPath = media.Split('\n').First(x => x.StartsWith("/media/"));
    using (var request = new HttpRequestMessage(HttpMethod.Get, segmentPath))
    { request.Headers.Range = new(5, 14); var response = await client.SendAsync(request); Assert(response.StatusCode == HttpStatusCode.PartialContent && rangeSeen && (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(segment[5..15]), "Range bytes and 206 are preserved exactly"); }
    var uris = Regex.Matches(media, "URI=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();
    Assert((await client.GetByteArrayAsync(uris[1])).Length == 16, "encrypted HLS key is available only through its capability");
    using (var stranger = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = client.BaseAddress })
        Assert((await stranger.GetAsync(segmentPath)).StatusCode == HttpStatusCode.Unauthorized, "media capabilities require the paired cookie");
    var firstDevice = server.Devices.Devices.Last().Id;
    var csrf2 = await Pair();
    Assert((await client.GetAsync(segmentPath)).StatusCode == HttpStatusCode.NotFound, "one device cannot reuse another device's media capability");
    await Post("/api/stream", new { channelId = "one" }, csrf2);
    var csrf3 = await Pair();
    Assert((await Post("/api/stream", new { channelId = "one" }, csrf3)).StatusCode == HttpStatusCode.Conflict, "simultaneous mobile stream limit is enforced");
    server.Streams.Stop();
    var second = await Post("/api/stream", new { channelId = "one" }, csrf3); var previousUrl = await JsonString(second, "url");
    library.Uri = new(root + "/drm.m3u8");
    var third = await Post("/api/stream", new { channelId = "one" }, csrf3); var newUrl = await JsonString(third, "url");
    Assert((await client.GetAsync(previousUrl)).StatusCode == HttpStatusCode.NotFound, "channel switch invalidates old resource capabilities");
    Assert((await client.GetAsync(newUrl)).StatusCode == HttpStatusCode.UnsupportedMediaType, "DRM HLS fails with a clear unsupported response");
    library.Uri = new(root + "/bad.m3u8");
    var malicious = await JsonString(await Post("/api/stream", new { channelId = "one" }, csrf3), "url");
    Assert((await client.GetAsync(malicious)).StatusCode == HttpStatusCode.BadRequest, "HLS dependency cannot reach cloud metadata / local services");
    library.Uri = new(root + "/private-redirect");
    var redirectUrl = await JsonString(await Post("/api/stream", new { channelId = "one" }, csrf3), "url");
    Assert((await client.GetAsync(redirectUrl)).StatusCode == HttpStatusCode.BadRequest, "redirect targets are validated before opening a private connection");
    library.Uri = new(root + "/entry");
    var hidden = await JsonString(await Post("/api/stream", new { channelId = "one" }, csrf3), "url"); library.Visible = false;
    Assert((await client.GetAsync(hidden)).StatusCode == HttpStatusCode.NotFound, "new parental/hidden restrictions also stop existing media"); library.Visible = true;
    using (var automatic = new RemoteStreams(library, () => new RemoteOptions { Delivery = "auto" }, directory, new SourcePolicy(true)))
    {
        await automatic.StartAsync("automatic", "one", false, server.Url!, CancellationToken.None);
        Assert(automatic.Active.Single().Mode == "proxy", "automatic mode recognizes compatible HLS without decoding it");
        library.Uri = new(root + "/raw"); bool unsupported = false;
        try { await automatic.StartAsync("automatic", "one", false, server.Url!, CancellationToken.None); } catch (RemoteException e) { unsupported = e.Status == 415; }
        Assert(unsupported && automatic.Active.Count == 0, "automatic mode explains non-HLS sources when FFmpeg is disabled"); library.Uri = new(root + "/entry");
    }
    if (options.AllowTranscoding && mediaArgument >= 0)
    {
        library.Uri = new(root + "/sample/index.m3u8");
        var compatible = await Post("/api/stream", new { channelId = "one", compatible = true }, csrf3);
        var compatibleResult = await compatible.Content.ReadFromJsonAsync<JsonElement>();
        Assert(compatible.IsSuccessStatusCode && compatibleResult.GetProperty("mode").GetString() == "compatible", "Safari compatibility starts a video-copy / AAC session");
        var compatibleUrl = compatibleResult.GetProperty("url").GetString()!;
        var compatibleManifest = await client.GetStringAsync(compatibleUrl);
        Assert(compatibleManifest.Contains("#EXT-X-MAP:URI=\"init.mp4\"") && compatibleManifest.Contains(".m4s"), "Safari compatibility emits fMP4 initialization and media segments");
        var compatibleBase = compatibleUrl[..(compatibleUrl.LastIndexOf('/') + 1)];
        var init = await client.GetAsync(compatibleBase + "init.mp4");
        var compatibleMedia = await client.GetAsync(compatibleBase + compatibleManifest.Split('\n').First(line => line.StartsWith("part", StringComparison.Ordinal)));
        Assert(init.Content.Headers.ContentType?.MediaType == "video/mp4" && compatibleMedia.Content.Headers.ContentType?.MediaType == "video/iso.segment", "Safari receives exact fMP4 MIME types");
        var inspectionFile = Path.Combine(directory, "compatible.mp4");
        await File.WriteAllBytesAsync(inspectionFile, [.. await init.Content.ReadAsByteArrayAsync(), .. await compatibleMedia.Content.ReadAsByteArrayAsync()]);
        var inspect = new System.Diagnostics.ProcessStartInfo(options.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-hide_banner", "-nostdin", "-i", inspectionFile, "-t", "0", "-f", "null", "-" }) inspect.ArgumentList.Add(arg);
        using (var process = System.Diagnostics.Process.Start(inspect)!)
        {
            var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
            using var inspectionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(inspectionTimeout.Token); } catch { process.Kill(true); throw; }
            var description = await stderr; await stdout;
            Assert(process.ExitCode == 0 && description.Contains("Video: h264") && description.Contains("Audio: aac"), "Safari output decodes with the original H.264 video and AAC audio");
            if (args.Contains("--uhd")) Assert(description.Contains("3840x2160"), "4K MP2 regression keeps the full UHD video resolution");
        }
        bool encoderBusy = false;
        try { await server.Streams.StartAsync("another-encoder", "one", true, server.Url!, CancellationToken.None); } catch (RemoteException e) { encoderBusy = e.Status == 409; }
        Assert(encoderBusy, "Audio-only compatibility shares the single encoder limit with video conversion");
        await Post("/api/stream/stop", new { }, csrf3);
        Assert((await client.GetAsync(compatibleBase + "init.mp4")).StatusCode == HttpStatusCode.NotFound && server.Streams.Active.Count == 0, "Stopping compatibility invalidates its initialization segment and releases the slot");
        var converted = await Post("/api/stream", new { channelId = "one", transcode = true }, csrf3);
        Assert(converted.IsSuccessStatusCode, "FFmpeg starts only after an explicit conversion request" + (converted.IsSuccessStatusCode ? "" : ": " + await converted.Content.ReadAsStringAsync()));
        var convertedUrl = await JsonString(converted, "url");
        var convertedManifest = await client.GetStringAsync(convertedUrl);
        var part = convertedManifest.Split('\n').First(line => line.StartsWith("part", StringComparison.Ordinal));
        Assert((await client.GetByteArrayAsync(convertedUrl[..(convertedUrl.LastIndexOf('/') + 1)] + part)).Length > 188, "bounded H.264/AAC fallback emits playable HLS segments");
        await Post("/api/stream/stop", new { }, csrf3);
        Assert(server.Streams.Active.Count == 0, "stopping a converted stream releases its task and stream slot");
        library.Uri = new(root + "/entry");
    }
    else Assert((await Post("/api/stream", new { channelId = "one", compatible = true }, csrf3)).StatusCode == HttpStatusCode.Conflict, "Safari compatibility cannot bypass the desktop FFmpeg permission");
    var ws = new ClientWebSocket(); ws.Options.Cookies = cookies; ws.Options.SetRequestHeader("Origin", server.Url!);
    await ws.ConnectAsync(new Uri(server.Url!.Replace("http:", "ws:") + "/events"), CancellationToken.None);
    var buffer = new byte[8192]; using (var timeout = new CancellationTokenSource(5000)) { var received = await ws.ReceiveAsync(buffer, timeout.Token); Assert(received.Count > 0 && Encoding.UTF8.GetString(buffer, 0, received.Count).Contains("Fixture TV"), "WebSocket publishes player state automatically"); }
    server.Devices.Revoke(server.Devices.Authenticate(credential)?.Id ?? firstDevice);
    var currentDevice = server.Devices.Devices.Last().Id; server.Devices.Revoke(currentDevice);
    Assert((await client.GetAsync("/api/session")).StatusCode == HttpStatusCode.Unauthorized && server.Streams.Active.Count == 0, "revoking a device immediately blocks API and cancels its stream");
    ws.Abort(); ws.Dispose();
    await server.StopAsync(); Assert(!server.Running && server.Url is null, "disabling Remote closes the server");
    options = options with { AllowControl = false, AllowViewing = false, Port = Port() };
    await server.StartAsync(options);
    using var disabled = new HttpClient(new HttpClientHandler { CookieContainer = cookies, UseProxy = false }) { BaseAddress = new(server.Url!) };
    disabled.DefaultRequestHeaders.Add("Origin", server.Url);
    var disabledCode = new Uri(server.PairingUrl()).Fragment[6..];
    var disabledBegin = await disabled.PostAsJsonAsync("/api/pair", new { code = disabledCode, name = "disabled permissions" });
    var disabledId = await JsonString(disabledBegin, "id"); server.Devices.Decide(disabledId, true); await disabled.GetAsync("/api/pair/status?id=" + disabledId);
    var disabledCsrf = await JsonString(await disabled.GetAsync("/api/session"), "csrf");
    disabled.DefaultRequestHeaders.Add("X-DzhiggaTV-CSRF", disabledCsrf);
    Assert((await disabled.PostAsJsonAsync("/api/command", new { action = "next" })).StatusCode == HttpStatusCode.Forbidden, "desktop control permission is enforced on the server");
    Assert((await disabled.PostAsJsonAsync("/api/stream", new { channelId = "one" })).StatusCode == HttpStatusCode.Forbidden, "viewing permission is enforced on the server");
    Assert((await disabled.PostAsJsonAsync("/api/command", new { action = "favorite", channelId = "one", value = 1 })).IsSuccessStatusCode, "favorites permission is independent of desktop playback control");
    await server.StopAsync(); privateProfile = false;
    bool profileDenied = false; try { await server.StartAsync(options); } catch (RemoteException) { profileDenied = true; }
    Assert(profileDenied && !server.Running, "public or unknown Windows network profile cannot start Remote");
}
catch (Exception e) { Console.Error.WriteLine("FAIL " + e.Message); Environment.ExitCode = 1; }
finally { await server.StopAsync(); await upstream.StopAsync(); }

// Pure validation catches private, encoded/mapped IP and unsupported URI schemes before any request.
if (Environment.ExitCode == 0)
{
    var policy = new SourcePolicy();
    foreach (var address in new[] { "127.0.0.1", "10.0.0.1", "192.168.1.1", "169.254.169.254", "100.100.100.200", "::1", "::ffff:127.0.0.1", "fe80::1", "fc00::1" }) Assert(!policy.Allowed(IPAddress.Parse(address)), "SSRF blocks " + address);
    Assert(policy.Allowed(IPAddress.Parse("8.8.8.8")), "public provider address is allowed");
    bool denied = false; try { policy.Validate(new Uri("file:///etc/passwd")); } catch (RemoteException) { denied = true; } Assert(denied, "non-HTTP source schemes are rejected");
    var delayedLibrary = new FixtureLibrary(new Uri("https://example.com/stream.m3u8"), "");
    using var ordered = new RemoteStreams(delayedLibrary, () => new RemoteOptions(), directory);
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); delayedLibrary.BlockNext = gate.Task;
    var obsolete = ordered.StartAsync("one", "one", false, "", CancellationToken.None);
    await ordered.StartAsync("one", "one", false, "", CancellationToken.None); gate.SetResult();
    bool superseded = false; try { await obsolete; } catch (OperationCanceledException) { superseded = true; }
    Assert(superseded && ordered.Active.Count == 1, "late source lookup cannot replace a newer stream selection");
    gate = new(TaskCreationOptions.RunContinuationsAsynchronously); delayedLibrary.BlockNext = gate.Task;
    var stopped = ordered.StartAsync("one", "one", false, "", CancellationToken.None); ordered.Stop("one"); gate.SetResult();
    bool cancelled = false; try { await stopped; } catch (OperationCanceledException) { cancelled = true; }
    Assert(cancelled && ordered.Active.Count == 0, "stop or revocation invalidates pending stream creation as well as active requests");
    Console.WriteLine($"{results.Count} Remote checks passed.");
}

sealed class FixtureLibrary(Uri uri, string logo) : IRemoteLibrary
{
    public Uri Uri = uri; public string Logo = logo; public bool Visible = true; public int Commands; public bool Demo; public Task? BlockNext;
    private static RemoteProgramme Programme => new("Fixture show", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), "Fixture description", "News");
    public Task<RemoteCatalog> CatalogAsync(CatalogQuery query, CancellationToken token)
    {
        if (!Demo) return Task.FromResult(new RemoteCatalog([new("one", "Fixture TV", "News", false, 1, Programme, null)], 1, ["News"], [], [], 1));
        var names = new[] { "Первый канал", "Россия 1", "НТВ", "РБК", "Матч ТВ", "Кино ТВ", "Моя планета", "Культура", "Пятница!", "Суббота", "Спорт 1" };
        var channels = names.Select((name, i) => new RemoteChannel("one" + i, name, i < 4 ? "Федеральные" : "Тематические", i == 2, i + 1,
            Programme with { Title = i == 4 ? "Футбол. Лига чемпионов" : i == 5 ? "Удивительная жизнь Уолтера Митти" : "Сейчас в эфире · Новости", Description = "Подробное описание передачи. Это изолированный тест интерфейса DzhiggaTV Remote." }, null));
        if (query.Search.Length > 0) channels = channels.Where(c => c.Name.Contains(query.Search, StringComparison.OrdinalIgnoreCase));
        if (query.Category.Length > 0) channels = channels.Where(c => c.Category == query.Category);
        if (query.Collection == "favorites") channels = channels.Where(c => c.Favorite);
        var list = channels.ToArray();
        return Task.FromResult(new RemoteCatalog(list.Skip(query.Offset).Take(query.Limit).ToArray(), list.Length, ["Федеральные", "Тематические"], [], [], Commands));
    }
    public Task<IReadOnlyList<RemoteProgramme>> GuideAsync(string channelId, DateTimeOffset from, CancellationToken token) => Task.FromResult<IReadOnlyList<RemoteProgramme>>([Programme]);
    public Task<string?> LogoAsync(string channelId, CancellationToken token) => Task.FromResult<string?>(Logo);
    public async Task<StreamSource> SourceAsync(string channelId, CancellationToken token)
    {
        var waiting = Interlocked.Exchange(ref BlockNext, null); if (waiting is not null) await waiting.WaitAsync(token);
        if (!Visible || !Demo && channelId != "one") throw new RemoteException("Channel hidden", 404);
        return new StreamSource(channelId, "Fixture TV", Uri, new Dictionary<string, string> { ["User-Agent"] = "FixturePlayer" });
    }
    public Task<RemotePlayerState> StateAsync(CancellationToken token) => Task.FromResult(new RemotePlayerState("one", "Fixture TV", Programme, "Playing", 70, false, false, false, [], new(false, false, null, null, []), Commands));
    public Task CommandAsync(RemoteCommand command, CancellationToken token) { Commands++; return Task.CompletedTask; }
}
