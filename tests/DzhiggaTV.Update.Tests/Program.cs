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
            byte[] deltaBytes = Encoding.UTF8.GetBytes("delta");
            string deltaHash = Convert.ToHexStringLower(SHA256.HashData(deltaBytes));
            string WithDelta(string? url = null, string? digest = "auto", long? size = null, bool duplicate = false)
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(Metadata())!;
                var asset = new { name = GitHubUpdateService.DeltaName, state = "uploaded", size = size ?? deltaBytes.Length,
                    digest = digest == "auto" ? "sha256:" + deltaHash : digest,
                    browser_download_url = url ?? "https://github.com/adrugalev/DzhiggaTV/releases/download/v1.0.14/DzhiggaTV-Update-x64.zip" };
                node["assets"]!.AsArray().Add(JsonSerializer.SerializeToNode(asset));
                if (duplicate) node["assets"]!.AsArray().Add(JsonSerializer.SerializeToNode(asset));
                return node.ToJsonString();
            }
            var deltaRelease = GitHubUpdateService.ParseRelease(WithDelta())!;
            Assert(deltaRelease.Delta?.Size == deltaBytes.Length, "discover a smaller verified delta alongside the full installer");
            Assert(GitHubUpdateService.ParseRelease(WithDelta(url: "https://example.com/delta.zip"))!.Delta is null, "ignore a delta outside the exact release without hiding the full installer");
            Assert(GitHubUpdateService.ParseRelease(WithDelta(digest: null))!.Delta is null, "do not use a delta without a checksum");
            Assert(GitHubUpdateService.ParseRelease(WithDelta(size: Payload.Length))!.Delta is null, "prefer the full installer when a delta saves no bandwidth");
            Assert(GitHubUpdateService.ParseRelease(WithDelta(duplicate: true))!.Delta is null, "ignore ambiguous duplicate delta assets");
            using (var service = Service(r => new(HttpStatusCode.OK) { Content = new ByteArrayContent(r.RequestUri!.AbsolutePath.EndsWith(".zip") ? deltaBytes : Payload) }))
            {
                string cache = Path.Combine(scratch, "delta");
                string deltaPath = await service.DownloadDeltaAsync(deltaRelease, cache, null, default);
                string fullPath = await service.DownloadAsync(deltaRelease, cache, null, default);
                Assert(deltaPath != fullPath && File.ReadAllBytes(deltaPath).SequenceEqual(deltaBytes) && File.ReadAllBytes(fullPath).SequenceEqual(Payload), "delta and full installer caches cannot overwrite one another");
            }
            using (var service = Service(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[deltaBytes.Length]) }))
                await Reject(async () => { await service.DownloadDeltaAsync(deltaRelease, Path.Combine(scratch, "bad-delta"), null, default); }, "reject a damaged delta download");
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
            string cleanupRoot = Path.Combine(scratch, "obsolete-cleanupRoot"); Directory.CreateDirectory(cleanupRoot);
            string Workspace(string version, string filename = "DzhiggaTV-Update-x64.zip", bool recent = false)
            {
                string directory = Path.Combine(cleanupRoot, version); Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, filename); File.WriteAllText(path, "cached bytes");
                if (!recent) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
                return path;
            }
            string old = Workspace("1.2.10.0"), current = Workspace("1.2.11.0"), future = Workspace("1.2.12.0");
            string active = Workspace("1.2.9.0", "DzhiggaTV-Setup-x64.exe"), unknown = Workspace("1.2.8.0", "keep.txt");
            string recent = Workspace("1.2.7.0", recent: true), partial = Workspace("1.2.6.0", "DzhiggaTV-Update-x64.zip.part");
            string shortVersion = Workspace("1.2");
            string prepared = Path.Combine(cleanupRoot, "1.2.10.0", "prepared-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(prepared);
            File.WriteAllText(Path.Combine(prepared, "application.zip"), "reconstructed payload"); File.SetLastWriteTimeUtc(Path.Combine(prepared, "application.zip"), DateTime.UtcNow.AddHours(-1));
            string runner = Path.Combine(prepared, "runner"); Directory.CreateDirectory(runner);
            File.WriteAllText(Path.Combine(runner, "DzhiggaTV.Setup.dll"), "runner"); File.SetLastWriteTimeUtc(Path.Combine(runner, "DzhiggaTV.Setup.dll"), DateTime.UtcNow.AddHours(-1));
            long oldBytes = Directory.EnumerateFiles(Path.GetDirectoryName(old)!, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
            var cleaned = await UpdateCacheCleaner.CleanAsync(cleanupRoot, new Version(1, 2, 11), activeExecutables: [active]);
            Assert(cleaned.Directories == 1 && cleaned.Bytes == oldBytes && !Directory.Exists(Path.GetDirectoryName(old)), "remove only completed obsolete version, including reconstructed payload and isolated runtime");
            Assert(new[] { current, future, active, unknown, recent, partial, shortVersion }.All(File.Exists), "preserve current/future releases, active installer, recent/partial downloads and unrelated files");
            Assert((await UpdateCacheCleaner.CleanAsync(cleanupRoot, new Version(1, 2, 11), activeExecutables: [active])).Directories == 0, "cache cleanup is idempotent");
            Console.WriteLine($"{_passed} updater checks passed."); return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL " + error); return 1; }
    }
}
