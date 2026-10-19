using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DzhiggaTV.Services;

internal static class Program
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("MZ updater transport fixture, not an executable");
    private static readonly string Hash = Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();
    private static int _passed;
    private static string Metadata(string tag = "v1.0.14", bool draft = false, bool prerelease = false, string? digest = "auto", bool sidecar = false, string? url = null, long? size = null, string? name = null)
    {
        string assetName = name ?? GitHubUpdateService.InstallerName;
        var assets = new List<object> { new { name = assetName, state = "uploaded", size = size ?? Payload.Length, digest = digest == "auto" ? "sha256:" + Hash : digest, browser_download_url = url ?? $"https://github.com/adrugalev/DzhiggaTV/releases/download/{tag}/{assetName}" } };
        if (sidecar) assets.Add(new { name = assetName + ".sha256", state = "uploaded", size = 100, browser_download_url = $"https://github.com/adrugalev/DzhiggaTV/releases/download/{tag}/{assetName}.sha256" });
        return JsonSerializer.Serialize(new { tag_name = tag, draft, prerelease, body = "## Что нового\n- Удобное обновление", assets });
    }
    private static void Assert(bool value, string name) { if (!value) throw new Exception(name); ++_passed; Console.WriteLine("PASS " + name); }
    private static async Task Reject(Func<Task> run, string name)
    { try { await run(); } catch (UpdateException) { Assert(true, name); return; } throw new Exception(name); }
    private static Task Parse(string json) { _ = GitHubUpdateService.ParseRelease(json); return Task.CompletedTask; }
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private static GitHubUpdateService Service(Func<HttpRequestMessage, HttpResponseMessage> send) => new(new Handler((r, _) => Task.FromResult(send(r))));
    private static async Task<int> Main()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "DzhiggaTV-update-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
        try
        {
            Assert(GitHubUpdateService.TryVersion("v1.0.10", out var ten) && GitHubUpdateService.TryVersion("1.0.9", out var nine) && ten > nine, "numeric version comparison across 9/10");
            Assert(GitHubUpdateService.Normalize(new(1, 2, 3)) == GitHubUpdateService.Normalize(new(1, 2, 3, 0)), "equivalent trailing-zero versions");
            Assert(!GitHubUpdateService.TryVersion("v1.2.3-beta", out _) && !GitHubUpdateService.TryVersion("latest", out _), "reject preview and non-version tags");
            Assert(GitHubUpdateService.ParseRelease(Metadata(draft: true)) is null && GitHubUpdateService.ParseRelease(Metadata(prerelease: true)) is null, "ignore draft and preview releases");
            Assert(GitHubUpdateService.ParseRelease(Metadata(name: "portable.zip")) is null, "do not execute arbitrary release assets");
            await Reject(() => Parse(Metadata(digest: null)), "require a digest or named checksum");
            await Reject(() => Parse(Metadata(url: "https://example.com/setup.exe")), "reject external installer URL");
            await Reject(() => Parse(Metadata(url: "http://github.com/adrugalev/DzhiggaTV/releases/download/v1.0.14/DzhiggaTV-Setup-x64.exe")), "reject HTTP installer URL");
            await Reject(() => Parse(Metadata(url: "https://github.com/attacker/DzhiggaTV/releases/download/v1.0.14/DzhiggaTV-Setup-x64.exe")), "bind installer to the configured repository");
            await Reject(() => Parse(Metadata(size: GitHubUpdateService.MaximumInstallerSize + 1)), "bound advertised download size");
            using (var service = Service(_ => new(HttpStatusCode.NotFound))) Assert(await service.CheckAsync() is null, "missing releases are a normal empty result");
            int etagRequests = 0;
            using (var service = Service(r => { if (++etagRequests == 1) { var response = Json(Metadata()); response.Headers.ETag = new("\"release-one\""); return response; } Assert(r.Headers.IfNoneMatch.Any(), "send conditional release request"); return new(HttpStatusCode.NotModified); }))
            { var first = await service.CheckAsync(); Assert(ReferenceEquals(first, await service.CheckAsync()), "reuse release after 304"); }
            int rateRequests = 0;
            using (var service = Service(_ => { ++rateRequests; return new(HttpStatusCode.TooManyRequests); }))
            { await Reject(async () => { await service.CheckAsync(); }, "handle GitHub rate limit"); await Reject(async () => { await service.CheckAsync(); }, "back off after rate limit"); Assert(rateRequests == 1, "do not retry a rate-limited server immediately"); }
            var release = GitHubUpdateService.ParseRelease(Metadata())!;
            int downloads = 0;
            using (var service = Service(_ => { ++downloads; return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) }; }))
            {
                var path = await service.DownloadAsync(release, Path.Combine(scratch, "good"), null, default);
                Assert(File.ReadAllBytes(path).SequenceEqual(Payload), "download and verify exact asset bytes");
                Assert(await service.DownloadAsync(release, Path.Combine(scratch, "good"), null, default) == path && downloads == 1, "reuse a verified download without another request");
            }
            using (var service = Service(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[Payload.Length]) }))
            {
                await Reject(async () => { await service.DownloadAsync(release, Path.Combine(scratch, "corrupt"), null, default); }, "reject incorrect SHA-256");
                Assert(!Directory.EnumerateFiles(Path.Combine(scratch, "corrupt"), "*", SearchOption.AllDirectories).Any(), "no executable or partial file remains after corruption");
            }
            using (var service = Service(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload[..^1]) }))
                await Reject(async () => { await service.DownloadAsync(release, Path.Combine(scratch, "short"), null, default); }, "reject a truncated download");
            using (var service = Service(_ => { var content = new ByteArrayContent(Payload.Concat(new byte[] { 1 }).ToArray()); content.Headers.ContentLength = Payload.Length; return new(HttpStatusCode.OK) { Content = content }; }))
                await Reject(async () => { await service.DownloadAsync(release, Path.Combine(scratch, "oversized"), null, default); }, "enforce the size limit while streaming even with a false header");
            var sidecarRelease = GitHubUpdateService.ParseRelease(Metadata(digest: null, sidecar: true))!;
            using (var service = Service(r => new(HttpStatusCode.OK) { Content = r.RequestUri!.AbsolutePath.EndsWith(".sha256") ? new StringContent(Hash + "  " + GitHubUpdateService.InstallerName) : new ByteArrayContent(Payload) }))
                Assert(File.Exists(await service.DownloadAsync(sidecarRelease, Path.Combine(scratch, "sidecar"), null, default)), "support published checksum sidecar");
            using (var service = Service(_ => new(HttpStatusCode.OK) { Content = new StringContent(Hash + "  other.exe") }))
                await Reject(async () => { await service.DownloadAsync(sidecarRelease, Path.Combine(scratch, "wrong-sidecar"), null, default); }, "checksum must name the selected installer");
            int hostileRequests = 0;
            using (var service = Service(_ => { ++hostileRequests; var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new("https://example.com/evil.exe"); return response; }))
            { await Reject(async () => { await service.DownloadAsync(release, Path.Combine(scratch, "redirect"), null, default); }, "reject off-site redirects before following"); Assert(hostileRequests == 1, "never contact an unapproved redirect host"); }
            int cdnRequests = 0;
            using (var service = Service(_ => { if (++cdnRequests == 1) { var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new("https://release-assets.githubusercontent.com/release-asset/test"); return response; } return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) }; }))
                Assert(File.Exists(await service.DownloadAsync(release, Path.Combine(scratch, "cdn"), null, default)) && cdnRequests == 2, "follow GitHub asset CDN redirects");
            using (var cancelled = new CancellationTokenSource())
            using (var service = new GitHubUpdateService(new Handler(async (_, token) => { cancelled.Cancel(); await Task.Delay(1000, token); return new(HttpStatusCode.OK); })))
            {
                bool stopped = false;
                try { await service.DownloadAsync(release, Path.Combine(scratch, "cancel"), null, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
                Assert(stopped && !Directory.EnumerateFiles(Path.Combine(scratch, "cancel"), "*", SearchOption.AllDirectories).Any(), "cancel without leaving an executable");
            }
            Console.WriteLine($"{_passed} updater checks passed."); return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL " + error); return 1; }
    }
}
