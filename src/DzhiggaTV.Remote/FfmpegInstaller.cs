using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace DzhiggaTV.Remote;

public sealed record RemotePreparation(string Message, double? Percent = null);

internal sealed record FfmpegPackage(Uri Url, string Sha256, string Version)
{
    // Versioned archive and checksum published by the Windows builder linked from ffmpeg.org/download.html.
    internal static readonly FfmpegPackage Pinned = new(
        new("https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip"),
        "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba", "9.0.2");
}

/// <summary>Prepares a private, self-contained FFmpeg; never installs system codecs or changes PATH.</summary>
public sealed class FfmpegInstaller
{
    private const long MaximumArchive = 180 * 1024 * 1024, MaximumExecutable = 250 * 1024 * 1024;
    private static readonly HttpClient DownloadClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(20), AutomaticDecompression = DecompressionMethods.None
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _tools;
    private readonly HttpClient _client;
    private readonly FfmpegPackage _package;
    private readonly Func<string, CancellationToken, Task<bool>> _probe;
    private readonly Dictionary<string, (long Size, DateTime Modified)> _verified = new(StringComparer.OrdinalIgnoreCase);

    public FfmpegInstaller(string toolsDirectory) : this(toolsDirectory, DownloadClient, FfmpegPackage.Pinned, CheckCapabilitiesAsync) { }
    internal FfmpegInstaller(string toolsDirectory, HttpClient client, FfmpegPackage package, Func<string, CancellationToken, Task<bool>> probe)
    { _tools = Path.GetFullPath(toolsDirectory); _client = client; _package = package; _probe = probe; }

    public string InstalledPath => Path.Combine(_tools, "ffmpeg-" + _package.Version, "ffmpeg.exe");

    public async Task<string> EnsureAsync(string? preferredPath = null, IProgress<RemotePreparation>? progress = null, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        string? staging = null;
        try
        {
            progress?.Report(new("Проверяем компоненты для просмотра на телефоне…"));
            foreach (var path in new[] { preferredPath, InstalledPath, Path.Combine(_tools, "ffmpeg.exe") }.Distinct(StringComparer.OrdinalIgnoreCase))
                if (path is not null && await UsableAsync(path, token).ConfigureAwait(false)) return Path.GetFullPath(path);

            Directory.CreateDirectory(_tools);
            staging = Path.Combine(_tools, ".ffmpeg-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
            string archive = Path.Combine(staging, "download.zip"), executable = Path.Combine(staging, "ffmpeg.exe");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(10));
            await DownloadAsync(archive, progress, deadline.Token).ConfigureAwait(false);
            progress?.Report(new("Проверяем загруженные компоненты…"));
            await using (var file = File.OpenRead(archive))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false)).Equals(_package.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new RemoteException("Проверка FFmpeg не пройдена. Нажмите «Включить Remote», чтобы скачать компоненты заново.");
            await ExtractAsync(archive, staging, token).ConfigureAwait(false);
            if (!await _probe(executable, token).ConfigureAwait(false))
                throw new RemoteException("FFmpeg не прошёл проверку H.264, AAC и HLS. Попробуйте подготовить Remote ещё раз.");
            token.ThrowIfCancellationRequested();
            string destination = Path.GetDirectoryName(InstalledPath)!; Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Combine(staging, "SOURCE.txt"),
                $"FFmpeg {_package.Version} Windows essentials by Gyan Doshi\n{_package.Url}\nSHA-256: {_package.Sha256}\nSources and build information: https://www.gyan.dev/ffmpeg/builds/\nFFmpeg sources: https://github.com/FFmpeg/FFmpeg/tree/n{_package.Version}\nLicense: GPL v3 or later; see LICENSE.txt.\n", token).ConfigureAwait(false);
            foreach (string name in new[] { "LICENSE.txt", "README.txt", "SOURCE.txt" })
                if (File.Exists(Path.Combine(staging, name))) File.Move(Path.Combine(staging, name), Path.Combine(destination, name), true);
            // A cancelled or incomplete download never replaces a working executable.
            token.ThrowIfCancellationRequested(); File.Move(executable, InstalledPath, true);
            Remember(InstalledPath);
            progress?.Report(new("Компоненты Remote готовы", 100));
            return InstalledPath;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new RemoteException("Загрузка компонентов заняла слишком много времени. Проверьте интернет и повторите включение Remote."); }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException)
        { throw new RemoteException("Не удалось загрузить или установить компоненты Remote. Проверьте интернет и свободное место, затем повторите включение."); }
        finally
        {
            if (staging is not null) { try { Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            _gate.Release();
        }
    }

    private async Task<bool> UsableAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) return false;
        var info = new FileInfo(path);
        if (_verified.TryGetValue(path, out var known) && known == (info.Length, info.LastWriteTimeUtc)) return true;
        if (!await _probe(path, token).ConfigureAwait(false)) return false;
        Remember(path); return true;
    }
    private void Remember(string path) { var f = new FileInfo(path); _verified[path] = (f.Length, f.LastWriteTimeUtc); }

    private async Task DownloadAsync(string destination, IProgress<RemotePreparation>? progress, CancellationToken token)
    {
        progress?.Report(new("Загружаем FFmpeg для Remote…", 0));
        using var request = new HttpRequestMessage(HttpMethod.Get, _package.Url);
        request.Headers.UserAgent.ParseAdd("DzhiggaTV-Remote/2.0");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? size = response.Content.Headers.ContentLength;
        if (size > MaximumArchive) throw new InvalidDataException("Oversized FFmpeg package.");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
        byte[] buffer = new byte[131072]; long received = 0; var throttle = Stopwatch.StartNew();
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            received += count; if (received > MaximumArchive) throw new InvalidDataException("Oversized FFmpeg package.");
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            if (throttle.ElapsedMilliseconds < 200) continue;
            double? percent = size > 0 ? Math.Min(100, 100d * received / size.Value) : null;
            progress?.Report(new($"Загружаем FFmpeg · {received / 1048576d:F1}" + (size > 0 ? $" из {size / 1048576d:F1}" : "") + " МБ", percent)); throttle.Restart();
        }
        if (size is not null && received != size) throw new InvalidDataException("Incomplete FFmpeg package.");
    }

    private static async Task ExtractAsync(string archive, string destination, CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 2000) throw new InvalidDataException("Unexpected FFmpeg archive.");
        bool executable = false, license = false;
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            string path = entry.FullName.Replace('\\', '/');
            if (path.StartsWith('/') || path.Contains(':') || path.Split('/').Contains("..")) throw new InvalidDataException("Unsafe archive path.");
            string? name = path.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase) ? "ffmpeg.exe" :
                path.EndsWith("/LICENSE", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/LICENSE.txt", StringComparison.OrdinalIgnoreCase) ? "LICENSE.txt" :
                path.EndsWith("/README.txt", StringComparison.OrdinalIgnoreCase) ? "README.txt" : null;
            if (name is null) continue;
            long maximum = name == "ffmpeg.exe" ? MaximumExecutable : 1024 * 1024;
            if (entry.Length is < 1 || entry.Length > maximum) throw new InvalidDataException("Oversized FFmpeg component.");
            await using var input = entry.Open();
            await using var output = new FileStream(Path.Combine(destination, name), FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
            // Bound actual decompressed bytes as well as the ZIP header.
            byte[] buffer = new byte[131072]; long total = 0; int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            { total += count; if (total > maximum) throw new InvalidDataException("Oversized FFmpeg component."); await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false); }
            executable |= name == "ffmpeg.exe"; license |= name == "LICENSE.txt";
        }
        if (!executable || !license) throw new InvalidDataException("Incomplete FFmpeg archive.");
    }

    internal static async Task<bool> CheckCapabilitiesAsync(string path, CancellationToken token)
    {
        try
        {
            string encoders = await InspectAsync(path, "-encoders", token).ConfigureAwait(false);
            if (!Has(encoders, "libx264") || !Has(encoders, "aac")) return false;
            string decoders = await InspectAsync(path, "-decoders", token).ConfigureAwait(false);
            if (!new[] { "h264", "hevc", "aac", "mp2", "ac3" }.All(c => Has(decoders, c))) return false;
            string formats = await InspectAsync(path, "-formats", token).ConfigureAwait(false);
            string protocols = await InspectAsync(path, "-protocols", token).ConfigureAwait(false);
            return Has(formats, "hls") && Has(formats, "mpegts") && protocols.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Any(p => p.Trim() == "http");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or OperationCanceledException) { return false; }
    }
    private static bool Has(string listing, string name) => Regex.IsMatch(listing, @"(?m)^\s*[A-Z.]{1,8}\s+" + Regex.Escape(name) + @"(?:\s|,|$)");
    private static async Task<string> InspectAsync(string path, string argument, CancellationToken token)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-hide_banner"); start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("FFmpeg did not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            var stdout = ReadBoundedAsync(process.StandardOutput, timeout.Token); var stderr = ReadBoundedAsync(process.StandardError, timeout.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("FFmpeg capability check failed.");
            return await stdout.ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(true); await process.WaitForExitAsync().ConfigureAwait(false); }
                catch (InvalidOperationException) { }
            }
        }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var result = new System.Text.StringBuilder(); char[] buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        { if (result.Length + count > 1024 * 1024) throw new IOException("Unexpected FFmpeg output."); result.Append(buffer, 0, count); }
        return result.ToString();
    }
}
