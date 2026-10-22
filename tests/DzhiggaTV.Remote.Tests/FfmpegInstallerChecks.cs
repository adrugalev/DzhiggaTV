using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using DzhiggaTV.Remote;

internal static class FfmpegInstallerChecks
{
    internal static async Task RunAsync(Action<bool, string> check, string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "DzhiggaTV-components-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            byte[] archive = Package();
            var source = new FfmpegPackage(new("https://www.gyan.dev/ffmpeg/builds/packages/fixture.zip"), Convert.ToHexString(SHA256.HashData(archive)), "fixture");
            bool Valid(string path) => File.ReadAllText(path) == "ready";
            Task<bool> Probe(string path, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Valid(path)); }
            var handler = new PackageHandler(archive); using var client = new HttpClient(handler);
            var installer = new FfmpegInstaller(Path.Combine(root, "install"), client, source, Probe);
            var preparation = new ProgressCapture();
            string installed = await installer.EnsureAsync(progress: preparation);
            check(Valid(installed) && File.Exists(Path.Combine(Path.GetDirectoryName(installed)!, "LICENSE.txt")) && File.Exists(Path.Combine(Path.GetDirectoryName(installed)!, "SOURCE.txt")), "Remote downloads, verifies and installs FFmpeg with license and source notice");
            check(preparation.Items.Any(p => p.Percent == 0) && preparation.Items.Last().Percent == 100, "Component preparation reports download and completion progress");
            var afterRestart = new FfmpegInstaller(Path.Combine(root, "install"), client, source, Probe);
            check(await afterRestart.EnsureAsync() == installed && handler.Requests == 1, "A later launch checks existing FFmpeg without downloading it again");
            string custom = Path.Combine(root, "custom.exe"); await File.WriteAllTextAsync(custom, "ready");
            check(await installer.EnsureAsync(custom) == custom && handler.Requests == 1, "A compatible user-selected FFmpeg is reused without a network request");
            var concurrent = new FfmpegInstaller(Path.Combine(root, "concurrent"), client, source, Probe);
            int before = handler.Requests; var paths = await Task.WhenAll(concurrent.EnsureAsync(), concurrent.EnsureAsync());
            check(paths[0] == paths[1] && handler.Requests == before + 1, "Concurrent preparations share one installation and one download");

            var wrongHash = new FfmpegInstaller(Path.Combine(root, "bad-hash"), client, source with { Sha256 = new string('0', 64) }, Probe);
            Directory.CreateDirectory(Path.GetDirectoryName(wrongHash.InstalledPath)!); await File.WriteAllTextAsync(wrongHash.InstalledPath, "old");
            check(await Fails(wrongHash) && File.ReadAllText(wrongHash.InstalledPath) == "old" && NoStaging(Path.Combine(root, "bad-hash")) &&
                !File.Exists(FfmpegDownload.CachePath(source with { Sha256 = new string('0', 64) }, Path.Combine(root, "bad-hash"))),
                "A checksum mismatch preserves the previous executable and removes the corrupt cached archive");

            foreach (var pair in new[] { ("traversal", Package("../outside.exe")), ("missing-license", Package(license: false)) })
            {
                using var fixtureClient = new HttpClient(new PackageHandler(pair.Item2));
                var invalid = new FfmpegInstaller(Path.Combine(root, pair.Item1), fixtureClient, source with { Sha256 = Convert.ToHexString(SHA256.HashData(pair.Item2)) }, Probe);
                check(await Fails(invalid) && !File.Exists(invalid.InstalledPath) && !File.Exists(Path.Combine(root, "outside.exe")), "Unsafe or incomplete archive is rejected: " + pair.Item1);
            }
            var unsupported = new FfmpegInstaller(Path.Combine(root, "unsupported"), client, source, (_, _) => Task.FromResult(false));
            check(await Fails(unsupported) && !File.Exists(unsupported.InstalledPath), "Downloaded FFmpeg is checked for required capabilities before installation");

            var retryHandler = new PackageHandler(archive) { FailNext = true }; using var retryClient = new HttpClient(retryHandler);
            var retry = new FfmpegInstaller(Path.Combine(root, "retry"), retryClient, source, Probe);
            check(await Fails(retry) && Valid(await retry.EnsureAsync()) && retryHandler.Requests == 2, "A failed network request leaves preparation retryable");
            var cancelHandler = new PackageHandler(archive) { Wait = true }; using var cancelClient = new HttpClient(cancelHandler);
            var cancelled = new FfmpegInstaller(Path.Combine(root, "cancel"), cancelClient, source, Probe);
            using var cancellation = new CancellationTokenSource(); var pending = cancelled.EnsureAsync(token: cancellation.Token);
            await cancelHandler.Entered.Task; cancellation.Cancel(); bool stopped = false;
            try { await pending; } catch (OperationCanceledException) { stopped = true; }
            check(stopped && !File.Exists(cancelled.InstalledPath) && NoStaging(Path.Combine(root, "cancel")), "Cancelling preparation removes partial files and installs nothing");
            cancelHandler.Wait = false;
            check(Valid(await cancelled.EnsureAsync()), "Preparation can be retried after cancellation");

            foreach (bool ignoreRange in new[] { false, true })
            {
                string tools = Path.Combine(root, ignoreRange ? "restart-full" : "resume");
                var ranges = new RangeHandler(archive) { IgnoreRange = ignoreRange };
                using var rangeClient = new HttpClient(ranges);
                var resumed = new FfmpegInstaller(tools, rangeClient, source, Probe);
                check(await Fails(resumed) && File.Exists(FfmpegDownload.CachePath(source, tools) + ".part"), "Interrupted transfer retains received bytes for retry");
                resumed = new(tools, rangeClient, source, Probe); // A new application instance shares the persisted partial file.
                check(Valid(await resumed.EnsureAsync()) && ranges.Offsets.SequenceEqual(new long[] { 0, archive.Length / 2 }),
                    ignoreRange ? "A server ignoring Range replaces the partial file instead of corrupting it" : "After restart the download requests only the missing bytes and passes SHA-256");
            }
            {
                string tools = Path.Combine(root, "bad-range"); var ranges = new RangeHandler(archive) { BadRange = true };
                using var rangeClient = new HttpClient(ranges); var resumed = new FfmpegInstaller(tools, rangeClient, source, Probe);
                await Fails(resumed);
                check(await Fails(resumed) && !File.Exists(resumed.InstalledPath) && new FileInfo(FfmpegDownload.CachePath(source, tools) + ".part").Length == archive.Length / 2,
                    "An incorrect Content-Range cannot append bytes or install a corrupt module");
            }
            {
                var mirrored = source with { Mirrors = [new("https://github.com/GyanD/codexffmpeg/releases/download/fixture.zip")] };
                var ranges = new RangeHandler(archive); using var rangeClient = new HttpClient(ranges);
                var switched = new FfmpegInstaller(Path.Combine(root, "mirror"), rangeClient, mirrored, Probe);
                check(Valid(await switched.EnsureAsync()) && ranges.Offsets.SequenceEqual(new long[] { 0, archive.Length / 2 }), "Mirror failover resumes the interrupted archive without downloading the prefix again");
            }
            foreach (bool permitted in new[] { true, false })
            {
                var redirect = new RedirectHandler(archive, permitted); using var redirectClient = new HttpClient(redirect);
                var redirected = new FfmpegInstaller(Path.Combine(root, permitted ? "cdn" : "offsite"), redirectClient, source, Probe);
                check(permitted ? Valid(await redirected.EnsureAsync()) && redirect.Requests == 2 : await Fails(redirected) && redirect.Requests == 1,
                    permitted ? "Pinned download follows HTTPS GitHub asset CDN redirects" : "Off-site redirects are rejected before contacting an unapproved server");
            }
            {
                string tools = Path.Combine(root, "cancel-body"); var ranges = new RangeHandler(archive) { PauseBody = true };
                using var rangeClient = new HttpClient(ranges); using var stop = new CancellationTokenSource();
                var resumable = new FfmpegInstaller(tools, rangeClient, source, Probe);
                var downloading = resumable.EnsureAsync(token: stop.Token); await ranges.BodyPaused.Task; stop.Cancel();
                bool stoppedBody = false; try { await downloading; } catch (OperationCanceledException) { stoppedBody = true; }
                check(stoppedBody && new FileInfo(FfmpegDownload.CachePath(source, tools) + ".part").Length == archive.Length / 2 &&
                    !File.Exists(resumable.InstalledPath) && NoStaging(tools), "Cancellation during the body preserves received bytes without installing or leaving extraction files");
                resumable = new(tools, rangeClient, source, Probe);
                check(Valid(await resumable.EnsureAsync()) && ranges.Offsets.Last() == archive.Length / 2, "The next enable resumes bytes saved by a cancelled transfer");
            }

            int packageIndex = Array.IndexOf(args, "--ffmpeg-package");
            if (packageIndex >= 0)
            {
                byte[] realPackage = await File.ReadAllBytesAsync(args[packageIndex + 1]);
                using var realClient = new HttpClient(new PackageHandler(realPackage));
                var real = new FfmpegInstaller(Path.Combine(root, "real"), realClient, FfmpegPackage.Pinned, FfmpegInstaller.CheckCapabilitiesAsync);
                var path = await real.EnsureAsync();
                check(await FfmpegInstaller.CheckCapabilitiesAsync(path, CancellationToken.None), "The pinned upstream Windows package really installs and passes H.264/AAC/HEVC/HLS checks");
                check(await real.EnsureAsync() == path, "The real installed module passes repeat preparation");
            }
        }
        finally { Directory.Delete(root, true); }
    }
    private static bool NoStaging(string tools) => !Directory.EnumerateDirectories(tools, ".ffmpeg-*").Any();
    private static async Task<bool> Fails(FfmpegInstaller installer) { try { await installer.EnsureAsync(); return false; } catch (RemoteException) { return true; } }
    private static byte[] Package(string? extra = null, bool license = true)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            void Entry(string name, string text) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8); writer.Write(text); }
            Entry("ffmpeg-test/bin/ffmpeg.exe", "ready");
            if (license) Entry("ffmpeg-test/LICENSE", "fixture license");
            if (extra is not null) Entry(extra, "unsafe");
        }
        return output.ToArray();
    }
    private sealed class ProgressCapture : IProgress<RemotePreparation>
    { internal readonly List<RemotePreparation> Items = []; public void Report(RemotePreparation value) => Items.Add(value); }
    private sealed class RangeHandler(byte[] archive) : HttpMessageHandler
    {
        internal bool IgnoreRange, BadRange, PauseBody;
        internal readonly TaskCompletionSource BodyPaused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<long> Offsets = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            long offset = request.Headers.Range?.Ranges.Single().From ?? 0; Offsets.Add(offset);
            if (Offsets.Count == 1)
            {
                var broken = new StreamContent(new InterruptedStream(archive, archive.Length / 2, PauseBody ? BodyPaused : null)); broken.Headers.ContentLength = archive.Length;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = broken });
            }
            var response = new HttpResponseMessage(IgnoreRange ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            { Content = new ByteArrayContent(IgnoreRange ? archive : archive[(int)offset..]) };
            if (!IgnoreRange) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset + (BadRange ? 1 : 0), archive.Length - 1, archive.Length);
            return Task.FromResult(response);
        }
    }
    private sealed class InterruptedStream(byte[] bytes, int limit, TaskCompletionSource? pause) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= limit)
            {
                if (pause is null) throw new IOException("Synthetic disconnection");
                pause.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); return 0;
            }
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, limit - (int)Position)], cancellationToken);
        }
    }
    private sealed class RedirectHandler(byte[] archive, bool permitted) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            if (Requests > 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new(permitted ? "https://release-assets.githubusercontent.com/pinned-ffmpeg" : "https://unapproved.example/ffmpeg.exe");
            return Task.FromResult(response);
        }
    }
    private sealed class PackageHandler(byte[] archive) : HttpMessageHandler
    {
        internal int Requests; internal bool FailNext, Wait;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Requests); Entered.TrySetResult();
            if (Wait) await Task.Delay(Timeout.Infinite, token);
            if (FailNext) { FailNext = false; return new(HttpStatusCode.ServiceUnavailable); }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) };
        }
    }
}
