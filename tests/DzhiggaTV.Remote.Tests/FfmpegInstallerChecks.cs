using System.IO.Compression;
using System.Net;
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
            check(await Fails(wrongHash) && File.ReadAllText(wrongHash.InstalledPath) == "old" && NoStaging(Path.Combine(root, "bad-hash")), "A checksum mismatch preserves the previous executable and removes the partial download");

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
