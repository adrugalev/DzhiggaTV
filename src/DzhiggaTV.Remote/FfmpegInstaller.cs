using L = DzhiggaTV.Localization.Texts;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SharpCompress.Archives.SevenZip;

namespace DzhiggaTV.Remote;

public sealed record RemotePreparation(string Message, double? Percent = null);

internal sealed record FfmpegPackage(Uri Url, string Sha256, string Version, bool SevenZip = false, long? Size = null, Uri[]? Mirrors = null)
{
    // Versioned archive and checksum published by the Windows builder linked from ffmpeg.org/download.html.
    internal static readonly FfmpegPackage Pinned = new(
        new("https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.7z"),
        "4705843ccaaf54257c16ad90f3e952ece33c17df964ecf7bfdbb0f49c7171077", "9.0.2", true, 35430500,
        [new("https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.7z")]);
}

/// <summary>Prepares a private, self-contained FFmpeg; never installs system codecs or changes PATH.</summary>
public sealed class FfmpegInstaller
{
    private const long MaximumExecutable = 250 * 1024 * 1024;
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
            progress?.Report(new(L.T("Проверяем компоненты для просмотра на телефоне…")));
            foreach (var path in new[] { preferredPath, InstalledPath, Path.Combine(_tools, "ffmpeg.exe") }.Distinct(StringComparer.OrdinalIgnoreCase))
                if (path is not null && await UsableAsync(path, token).ConfigureAwait(false)) return Path.GetFullPath(path);

            Directory.CreateDirectory(_tools);
            staging = Path.Combine(_tools, ".ffmpeg-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
            string executable = Path.Combine(staging, "ffmpeg.exe");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(10));
            string archive = await FfmpegDownload.AcquireAsync(_client, _package, _tools, progress, deadline.Token).ConfigureAwait(false);
            progress?.Report(new(L.T("Проверяем загруженные компоненты…")));
            string digest;
            await using (var file = File.OpenRead(archive)) digest = Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false));
            if (!digest.Equals(_package.Sha256, StringComparison.OrdinalIgnoreCase))
            { File.Delete(archive); throw new RemoteException(L.T("Проверка FFmpeg не пройдена. Нажмите «Включить Remote», чтобы скачать компоненты заново.")); }
            progress?.Report(new(L.T("Распаковываем FFmpeg…")));
            await Task.Run(() => _package.SevenZip ? ExtractSevenZipAsync(archive, staging, token) : ExtractAsync(archive, staging, token), token).ConfigureAwait(false);
            progress?.Report(new(L.T("Проверяем кодеки FFmpeg…")));
            if (!await _probe(executable, token).ConfigureAwait(false))
                throw new RemoteException(L.T("FFmpeg не прошёл проверку H.264, AAC и HLS. Попробуйте подготовить Remote ещё раз."));
            token.ThrowIfCancellationRequested();
            string destination = Path.GetDirectoryName(InstalledPath)!; Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Combine(staging, "SOURCE.txt"),
                $"FFmpeg {_package.Version} Windows essentials by Gyan Doshi\n{_package.Url}\nSHA-256: {_package.Sha256}\nSources and build information: https://www.gyan.dev/ffmpeg/builds/\nFFmpeg sources: https://github.com/FFmpeg/FFmpeg/tree/n{_package.Version}\nLicense: GPL v3 or later; see LICENSE.txt.\n", token).ConfigureAwait(false);
            foreach (string name in new[] { "LICENSE.txt", "README.txt", "SOURCE.txt" })
                if (File.Exists(Path.Combine(staging, name))) File.Move(Path.Combine(staging, name), Path.Combine(destination, name), true);
            // A cancelled or incomplete download never replaces a working executable.
            token.ThrowIfCancellationRequested(); File.Move(executable, InstalledPath, true);
            Remember(InstalledPath);
            try { File.Delete(archive); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            progress?.Report(new(L.T("Компоненты Remote готовы"), 100));
            return InstalledPath;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new RemoteException(L.T("Загрузка компонентов заняла слишком много времени. Проверьте интернет и повторите включение Remote.")); }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or SharpCompress.Common.SharpCompressException)
        { throw new RemoteException(L.T("Не удалось загрузить или установить компоненты Remote. Проверьте интернет и свободное место, затем повторите включение.")); }
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

    private static async Task ExtractSevenZipAsync(string path, string destination, CancellationToken token)
    {
        using var archive = SevenZipArchive.Open(path);
        var entries = archive.Entries.ToArray();
        if (entries.Length > 2000 || entries.Any(e => e.IsEncrypted) || entries.Sum(e => e.Size) > 800L * 1024 * 1024)
            throw new InvalidDataException("Unexpected FFmpeg archive.");
        foreach (var entry in entries)
        {
            string key = (entry.Key ?? "").Replace('\\', '/');
            if (key.StartsWith('/') || key.Contains(':') || key.Split('/').Contains("..")) throw new InvalidDataException("Unsafe archive path.");
        }
        using var reader = archive.ExtractAllEntries();
        bool executable = false, license = false, readme = false;
        // Read solid archives sequentially so preceding entries are decompressed only once.
        while (reader.MoveToNextEntry())
        {
            token.ThrowIfCancellationRequested();
            string name = (reader.Entry.Key ?? "").Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Contains("..")) throw new InvalidDataException("Unsafe archive path.");
            string? outputName = name.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase) ? "ffmpeg.exe" :
                name.EndsWith("/LICENSE", StringComparison.OrdinalIgnoreCase) || name.EndsWith("/LICENSE.txt", StringComparison.OrdinalIgnoreCase) ? "LICENSE.txt" :
                name.EndsWith("/README.txt", StringComparison.OrdinalIgnoreCase) ? "README.txt" : null;
            if (outputName is null || reader.Entry.IsDirectory) continue;
            long maximum = outputName == "ffmpeg.exe" ? MaximumExecutable : 1024 * 1024;
            if (reader.Entry.Size is < 1 || reader.Entry.Size > maximum) throw new InvalidDataException("Oversized FFmpeg component.");
            using var input = reader.OpenEntryStream();
            await using var output = new FileStream(Path.Combine(destination, outputName), FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
            byte[] buffer = new byte[131072]; long total = 0; int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            { total += count; if (total > maximum) throw new InvalidDataException("Oversized FFmpeg component."); await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false); }
            executable |= outputName == "ffmpeg.exe"; license |= outputName == "LICENSE.txt";
            readme |= outputName == "README.txt";
            if (executable && license && readme) break;
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
