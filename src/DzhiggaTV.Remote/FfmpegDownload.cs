using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace DzhiggaTV.Remote;

internal static class FfmpegDownload
{
    private const long MaximumArchive = 180L * 1024 * 1024;
    internal static string CachePath(FfmpegPackage package, string tools) => Path.Combine(tools, "downloads", "ffmpeg-" + package.Sha256.ToLowerInvariant() + (package.SevenZip ? ".7z" : ".zip"));

    internal static async Task<string> AcquireAsync(HttpClient client, FfmpegPackage package, string tools, IProgress<RemotePreparation>? progress, CancellationToken token)
    {
        string complete = CachePath(package, tools), partial = complete + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(complete)!);
        if (File.Exists(complete))
        {
            long length = new FileInfo(complete).Length;
            if (length <= MaximumArchive && (package.Size is null || length == package.Size)) return complete; // The caller always checks SHA-256.
            File.Delete(complete);
        }
        var sources = new[] { package.Url }.Concat(package.Mirrors ?? []).ToArray();
        for (int index = 0; index < sources.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await TransferAsync(client, package, sources[index], partial, progress, index + 1 < sources.Length, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested(); File.Move(partial, complete, true); return complete;
            }
            catch (Exception error) when (!token.IsCancellationRequested && index + 1 < sources.Length && error is HttpRequestException or IOException or OperationCanceledException)
            { progress?.Report(new("Пробуем другой сервер загрузки FFmpeg…")); }
        }
        throw new IOException("FFmpeg download failed.");
    }

    private static async Task TransferAsync(HttpClient client, FfmpegPackage package, Uri source, string partial, IProgress<RemotePreparation>? progress, bool canSwitch, CancellationToken token)
    {
        long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset > MaximumArchive || package.Size is { } expected && offset > expected) offset = 0;
        if (package.Size is { } known && offset == known && offset > 0) return;
        progress?.Report(new(offset > 0 ? "Продолжаем загрузку FFmpeg…" : "Загружаем FFmpeg для Remote…", package.Size is > 0 ? 100d * offset / package.Size.Value : 0));
        using var response = await OpenAsync(client, source, offset, token).ConfigureAwait(false);
        long? size = response.Content.Headers.ContentLength;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range?.Unit != "bytes" || range.From != offset || range.To is null || range.Length is null || range.To != range.Length - 1 ||
                size is not null && size != range.To - range.From + 1)
                throw new HttpRequestException("Invalid FFmpeg content range.");
            size = range.Length;
        }
        else if (response.StatusCode == HttpStatusCode.OK) offset = 0; // Server ignored Range; replace, never append.
        else { response.EnsureSuccessStatusCode(); throw new HttpRequestException("Unexpected FFmpeg response."); }
        if (size > MaximumArchive || package.Size is { } pinned && size is { } advertised && advertised != pinned)
            throw new InvalidDataException("Unexpected FFmpeg package size.");
        size ??= package.Size;
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var output = new FileStream(partial, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);
        byte[] buffer = new byte[131072]; long received = offset; var elapsed = Stopwatch.StartNew(); var throttle = Stopwatch.StartNew();
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(token); idle.CancelAfter(TimeSpan.FromSeconds(20));
            int count;
            try { count = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new HttpRequestException("FFmpeg server stopped responding."); }
            if (count == 0) break;
            received += count;
            if (received > MaximumArchive || size is not null && received > size) throw new InvalidDataException("Oversized FFmpeg package.");
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            if (canSwitch && elapsed.Elapsed.TotalSeconds > 8 && (received - offset) / elapsed.Elapsed.TotalSeconds < 128 * 1024)
                throw new HttpRequestException("Slow FFmpeg mirror.");
            if (throttle.ElapsedMilliseconds < 200) continue;
            double speed = (received - offset) / Math.Max(0.1, elapsed.Elapsed.TotalSeconds);
            progress?.Report(new($"Загружаем FFmpeg · {received / 1048576d:F1}" + (size is > 0 ? $" из {size / 1048576d:F1}" : "") + $" МБ · {speed / 1048576d:F1} МБ/с",
                size is > 0 ? Math.Min(100, 100d * received / size.Value) : null)); throttle.Restart();
        }
        if (size is not null && received != size) throw new IOException("Incomplete FFmpeg download.");
    }

    private static async Task<HttpResponseMessage> OpenAsync(HttpClient client, Uri source, long offset, CancellationToken token)
    {
        Uri current = source;
        for (int redirects = 0; redirects <= 4; redirects++)
        {
            if (!Allowed(current)) throw new HttpRequestException("Unapproved FFmpeg download address.");
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("DzhiggaTV-Remote/2.0");
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            using var headers = CancellationTokenSource.CreateLinkedTokenSource(token); headers.CancelAfter(TimeSpan.FromSeconds(25));
            HttpResponseMessage response;
            try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new HttpRequestException("FFmpeg server did not respond."); }
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            Uri? location = response.Headers.Location; response.Dispose();
            if (location is null) throw new HttpRequestException("Missing FFmpeg redirect.");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }
        throw new HttpRequestException("Too many FFmpeg redirects.");
    }
    private static bool Allowed(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Host is "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "www.gyan.dev";
}
