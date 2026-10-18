using System.Net;
using System.Text;
using DzhiggaTV.Core;
using DzhiggaTV.Services;

var tests = new (string Name, Func<Task> Run)[]
{
    ("EPG.ONE: HTTPS entry follows public HTTP mirror without another HTTPS attempt", PublicMirror),
    ("HTTP: third-party source is upgraded to HTTPS", UpgradeUnknownSource),
    ("HTTP: third-party downgrade is rejected before its request", RejectUnknownDowngrade),
    ("HTTP: lookalike hosts, custom ports and credentials receive no public exception", RejectImpersonation),
    ("HTTP: public mirror cannot redirect beyond its allowlist", RejectPublicMirrorEscape),
    ("HTTP: explicit third-party opt-in remains supported", ExplicitHttp),
    ("HTTP: injected client final address is still validated", ValidateClientFinalAddress),
    ("HTTP: cancellation stops the active request", CancelTransport),
    ("Logos: public HTTP icon resolves and survives service restart", PublicLogo),
    ("Logos: explicit refresh retries failed URLs and retains successful cache", ResetLogoFailures),
    ("Logos: changing HTTP policy retries URLs previously rejected by policy", ChangeLogoPolicy),
    ("EPG: default settings import guide and reference metadata through public mirror", GuideRefresh),
    ("EPG: transport error reports safe detail and preserves the previous guide", GuideFailure),
    ("EPG: cancellation preserves guide and does not register network failure", CancelGuide)
};
var failed = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception exception) { failed++; Console.Error.WriteLine($"FAIL {name}: {exception}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed.");
return failed == 0 ? 0 : 1;

static async Task PublicMirror()
{
    using var handler = new Router((request, _) => Task.FromResult(request.RequestUri!.Scheme == "https"
        ? Redirect("http://ru.epg.one/epg2.xml.gz") : Ok("public metadata")));
    using var client = new HttpClient(handler); using var remote = new RemoteFiles(client);
    using var result = await remote.OpenAsync(EpgOneSources.FullTransparent, false, default);
    Check(await result.Content.ReadAsStringAsync() == "public metadata");
    Equal("https://epg.one/epg2.xml.gz|http://ru.epg.one/epg2.xml.gz", string.Join('|', handler.Requests));
}

static async Task UpgradeUnknownSource()
{
    using var handler = new Router((_, _) => Task.FromResult(Ok("metadata")));
    using var client = new HttpClient(handler); using var remote = new RemoteFiles(client);
    using var result = await remote.OpenAsync(new("http://custom.invalid/guide.xml"), false, default);
    Equal("https://custom.invalid/guide.xml", handler.Requests.Single());
}

static async Task RejectUnknownDowngrade()
{
    using var handler = new Router((_, _) => Task.FromResult(Redirect("http://custom.invalid/guide.xml")));
    using var client = new HttpClient(handler); using var remote = new RemoteFiles(client);
    await Expect<HttpRequestException>(() => remote.OpenAsync(new("https://custom.invalid/guide.xml"), false, default));
    Equal("https://custom.invalid/guide.xml", handler.Requests.Single());
}

static async Task RejectImpersonation()
{
    foreach (var target in new[] { "http://epg.one.attacker.invalid/guide", "http://ru.epg.one:8080/guide", "http://user:password@ru.epg.one/guide" })
    {
        using var handler = new Router((_, _) => Task.FromResult(Redirect(target)));
        using var client = new HttpClient(handler); using var remote = new RemoteFiles(client);
        await Expect<HttpRequestException>(() => remote.OpenAsync(EpgOneSources.FullTransparent, false, default));
        Equal(1, handler.Requests.Count);
    }
}

static async Task RejectPublicMirrorEscape()
{
    foreach (var target in new[] { "http://attacker.invalid/logo", "https://attacker.invalid/logo" })
    {
        using var handler = new Router((request, _) => Task.FromResult(Redirect(request.RequestUri!.Scheme == "https" ? "http://ru.epg.one/guide" : target)));
        using var client = new HttpClient(handler); using var remote = new RemoteFiles(client);
        await Expect<HttpRequestException>(() => remote.OpenAsync(EpgOneSources.FullTransparent, false, default));
        Equal(2, handler.Requests.Count); Check(handler.Requests.All(uri => !uri.Contains("attacker")));
    }
}

static async Task ExplicitHttp()
{
    using var handler = new Router((request, _) => Task.FromResult(request.RequestUri!.Host == "custom.invalid" ? Redirect("http://other.invalid/guide.xml") : Ok("metadata")));
    using var client = new HttpClient(handler); using var remote = new RemoteFiles(client);
    using var result = await remote.OpenAsync(new("http://custom.invalid/guide.xml"), true, default);
    Equal("http://custom.invalid/guide.xml|http://other.invalid/guide.xml", string.Join('|', handler.Requests));
}

static async Task ValidateClientFinalAddress()
{
    using var handler = new Router((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    { RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://custom.invalid/guide.xml"), Content = new StringContent("must not be used") }));
    using var client = new HttpClient(handler); using var remote = new RemoteFiles(client);
    await Expect<HttpRequestException>(() => remote.OpenAsync(EpgOneSources.FullTransparent, false, default));
}

static async Task CancelTransport()
{
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var handler = new Router(async (_, token) => { started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Ok("unreachable"); });
    using var client = new HttpClient(handler); using var remote = new RemoteFiles(client); using var cancellation = new CancellationTokenSource();
    var pending = remote.OpenAsync(EpgOneSources.FullTransparent, false, cancellation.Token);
    await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
    await Expect<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
    Equal(1, handler.Requests.Count);
}

static async Task PublicLogo()
{
    await InDirectory(async directory =>
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jR2kAAAAASUVORK5CYII=");
        using var handler = new Router((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) }));
        using var client = new HttpClient(handler);
        var channel = Source() with { Logo = "http://epg.one/img2/first.png" };
        string? cached;
        using (var logos = new LogoService(directory, client)) cached = await logos.ResolveAsync(channel);
        Check(cached is not null && File.Exists(cached));
        using (var logos = new LogoService(directory, client)) Equal(cached, await logos.ResolveAsync(channel));
        Equal("http://epg.one/img2/first.png", handler.Requests.Single());
    });
}

static async Task GuideRefresh()
{
    await InDirectory(async directory =>
    {
        using var handler = new Router((request, _) => Task.FromResult(request.RequestUri!.Scheme == "https"
            ? Redirect("http://ru.epg.one" + request.RequestUri.PathAndQuery)
            : Ok(request.RequestUri.AbsolutePath.EndsWith(".gz") ? GuideXml() : "#EXTM3U\n#EXTINF:-1 tvg-id=\"first\" tvg-logo=\"http://epg.one/img2/first.png\",Первый канал\nhttps://reference.invalid/metadata\n")));
        using var client = new HttpClient(handler); using var store = new SqliteStore(Path.Combine(directory, "data.sqlite"));
        using var guide = new EpgOneService(store, client);
        var result = await guide.RefreshAsync([Source()]);
        Check(result.Updated && result.Channels == 1 && result.Programmes == 1);
        Equal("first", (await store.GetMappingsAsync()).Single().EpgId);
        Equal("http://epg.one/img2/first.png", guide.GetReferenceLogo("first"));
        Equal(4, handler.Requests.Count);
        Check(handler.Requests.All(uri => new Uri(uri).Host is "epg.one" or "ru.epg.one"));
    });
}

static async Task ResetLogoFailures()
{
    await InDirectory(async directory =>
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jR2kAAAAASUVORK5CYII=");
        bool fail = true;
        using var handler = new Router((_, _) => Task.FromResult(fail ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) }));
        using var client = new HttpClient(handler); using var logos = new LogoService(directory, client);
        var channel = Source() with { Logo = "http://epg.one/img2/first.png" };
        Check(await logos.ResolveAsync(channel) is null); fail = false;
        Check(await logos.ResolveAsync(channel) is null); Equal(1, handler.Requests.Count);
        logos.ResetFailedDownloads(); var cached = await logos.ResolveAsync(channel); Check(cached is not null);
        Equal(2, handler.Requests.Count); logos.ResetFailedDownloads();
        Equal(cached, await logos.ResolveAsync(channel)); Equal(2, handler.Requests.Count);
    });
}

static async Task ChangeLogoPolicy()
{
    await InDirectory(async directory =>
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jR2kAAAAASUVORK5CYII=");
        using var handler = new Router((request, _) => Task.FromResult(request.RequestUri!.Scheme == "https"
            ? Redirect("http://custom.invalid/logo.png") : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) }));
        using var client = new HttpClient(handler); using var logos = new LogoService(directory, client);
        var channel = Source() with { Logo = "http://custom.invalid/logo.png" };
        Check(await logos.ResolveAsync(channel) is null); Equal(1, handler.Requests.Count);
        logos.AllowInsecureHttp = true;
        Check(await logos.ResolveAsync(channel) is not null);
        Equal("https://custom.invalid/logo.png|http://custom.invalid/logo.png", string.Join('|', handler.Requests));
    });
}

static async Task GuideFailure()
{
    await InDirectory(async directory =>
    {
        using var store = new SqliteStore(Path.Combine(directory, "data.sqlite")); await Seed(store);
        using var handler = new Router((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("secret response") }));
        using var client = new HttpClient(handler); using var guide = new EpgOneService(store, client); await guide.InitializeAsync();
        var previous = guide.LastUpdated;
        var result = await guide.RefreshAsync([Source()], source: new("https://custom.invalid/guide.xml?token=private-test-secret"));
        Check(!result.Updated && result.Message.Contains("HTTP 503") && !result.Message.Contains("private-test-secret"));
        Equal(1L, await store.GetProgrammeCountAsync()); Equal(previous, guide.LastUpdated);
        Equal(1, await store.GetSettingAsync("epg.failureCount", 0));
        result = await guide.RefreshAsync([Source()]);
        Check(!result.Updated && result.Message.Contains("минуту")); Equal(1, handler.Requests.Count);
    });
}

static async Task CancelGuide()
{
    await InDirectory(async directory =>
    {
        using var store = new SqliteStore(Path.Combine(directory, "data.sqlite")); await Seed(store);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Router(async (_, token) => { started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Ok("unreachable"); });
        using var client = new HttpClient(handler); using var guide = new EpgOneService(store, client); using var cancellation = new CancellationTokenSource();
        await guide.InitializeAsync(); var previous = guide.LastUpdated;
        var pending = guide.RefreshAsync([Source()], cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        await Expect<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Equal(1L, await store.GetProgrammeCountAsync()); Equal(previous, guide.LastUpdated);
        Equal(0, await store.GetSettingAsync("epg.failureCount", 0));
    });
}

static Channel Source() => new("source", "playlist", "Первый канал", "Новости", "https://provider.invalid/private-stream?token=never-send-this");
static string GuideXml() => "<tv><channel id='first'><display-name>Первый канал</display-name><icon src='http://epg.one/img2/first.png'/></channel><programme channel='first' start='20261018120000 +0300' stop='20261018130000 +0300'><title>Новости</title></programme></tv>";
static Task Seed(SqliteStore store) => store.ImportEpgAsync(async (addChannel, addProgramme, _) =>
{ await addChannel(new("old", "Предыдущий")); await addProgramme(new("old", "Сохранённая программа", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1))); });
static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(text)) };
static HttpResponseMessage Redirect(string location)
{ var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new(location); return response; }
static async Task InDirectory(Func<string, Task> run)
{
    var directory = Path.Combine(Path.GetTempPath(), "DzhiggaTV.Transport.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
    try { await run(directory); }
    finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
}
static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}."); }
static async Task Expect<T>(Func<Task> action) where T : Exception
{ try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }

sealed class Router(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> route) : HttpMessageHandler
{
    internal List<string> Requests { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { Requests.Add(request.RequestUri!.AbsoluteUri); var response = await route(request, cancellationToken); response.RequestMessage ??= request; return response; }
}
