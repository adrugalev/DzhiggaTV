using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace DzhiggaTV.Remote;

/// <summary>Per-device, expiring capabilities. URLs supplied by a phone are never accepted here.</summary>
public sealed class RemoteStreams : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Viewing> _streams = [];
    private readonly Dictionary<string, long> _starts = [];
    private long _epoch;
    private readonly SourcePolicy _policy;
    private readonly HttpClient _client;
    private readonly IRemoteLibrary _library;
    private readonly Func<RemoteOptions> _options;
    private readonly string _scratch;
    public RemoteStreams(IRemoteLibrary library, Func<RemoteOptions> options, string directory, SourcePolicy? policy = null)
    { _library = library; _options = options; _policy = policy ?? new(); _client = _policy.CreateClient(); _scratch = Path.Combine(directory, "remote-video"); }
    public IReadOnlyList<StreamInfo> Active
    {
        get { lock (_gate) { Prune(); return _streams.Values.Select(v => new StreamInfo(v.DeviceId, v.Source.ChannelId, v.Mode, Interlocked.Read(ref v.Bytes), v.Started)).ToArray(); } }
    }
    public async Task<object> StartAsync(string deviceId, string channelId, bool transcode, string serverUrl, CancellationToken token, bool compatible = false, DateTimeOffset? programmeStart = null)
    {
        var options = _options();
        if (!options.AllowViewing) throw new RemoteException("Просмотр на телефонах отключён на компьютере.", 403);
        long generation, epoch;
        lock (_gate) { generation = _starts.GetValueOrDefault(deviceId) + 1; _starts[deviceId] = generation; epoch = _epoch; }
        var source = await _library.SourceAsync(channelId, programmeStart, token); _policy.Validate(source.Uri); token.ThrowIfCancellationRequested();
        Viewing viewing;
        lock (_gate)
        {
            Prune();
            if (epoch != _epoch || _starts.GetValueOrDefault(deviceId) != generation) throw new OperationCanceledException("Superseded stream request.");
            var old = _streams.Values.FirstOrDefault(v => v.DeviceId == deviceId);
            if (old is not null) Remove(old);
            if (_streams.Count >= options.MaximumStreams) throw new RemoteException("Достигнут лимит мобильных потоков. Остановите просмотр на другом устройстве.", 409);
            if ((transcode || compatible) && (!options.AllowTranscoding || !File.Exists(options.FfmpegPath))) throw new RemoteException("Включите совместимость iPhone и выберите ffmpeg.exe в настройках Remote на ПК.", 409);
            if ((transcode || compatible) && _streams.Values.Any(v => v.UsesEncoder)) throw new RemoteException("Преобразование уже используется другим устройством. Одновременно доступна одна задача.", 409);
            viewing = new(deviceId, source, transcode ? "transcode" : compatible ? "compatible" : "proxy", _policy); _streams.Add(viewing.Id, viewing);
        }
        try
        {
            using var starting = CancellationTokenSource.CreateLinkedTokenSource(token, viewing.Cancellation.Token);
            token = starting.Token;
            if (!transcode && !compatible && options.Delivery == "auto")
            {
                using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(token); probeTimeout.CancelAfter(TimeSpan.FromSeconds(12));
                using var probe = await OpenAsync(viewing, source.Uri, null, probeTimeout.Token);
                if (!probe.IsSuccessStatusCode) throw new RemoteException("Провайдер не предоставил поток. Проверьте лимит одновременных подключений.", 502);
                using var input = await probe.Content.ReadAsStreamAsync(probeTimeout.Token); var header = new byte[32];
                int count = await input.ReadAtLeastAsync(header, 8, false, probeTimeout.Token);
                bool hls = Encoding.UTF8.GetString(header, 0, count).TrimStart('\uFEFF', '\r', '\n', ' ').StartsWith("#EXTM3U", StringComparison.Ordinal);
                if (!hls)
                {
                    if (!options.AllowTranscoding || !File.Exists(options.FfmpegPath)) throw new RemoteException("Это не HLS-поток. Разрешите преобразование через FFmpeg в настройках Remote на компьютере.", 415);
                    lock (_gate)
                    {
                        if (!_streams.ContainsKey(viewing.Id)) throw new OperationCanceledException();
                        if (_streams.Values.Any(v => v.UsesEncoder)) throw new RemoteException("Уже работает одна задача преобразования. Остановите её перед запуском другой.", 409);
                        viewing.Mode = "transcode"; transcode = true;
                    }
                }
            }
            if (!transcode && !compatible && options.Delivery == "direct")
            {
                if (!options.AllowDirectUrls || source.Headers.Count > 0) throw new RemoteException("Прямое воспроизведение требует разрешения адресов и потока без специальных HTTP-заголовков.", 409);
                using var response = await OpenAsync(viewing, source.Uri, null, token);
                var text = await ReadPlaylistAsync(response, token);
                HlsPlaylist.Rewrite(text, response.RequestMessage!.RequestUri!, uri => { _policy.Validate(uri); return uri.AbsoluteUri; });
                viewing.Mode = "direct";
                return new { url = source.Uri.AbsoluteUri, mode = "direct", channelId };
            }
            if (viewing.UsesEncoder)
            {
                Directory.CreateDirectory(_scratch);
                viewing.Directory = Path.Combine(_scratch, viewing.Id); Directory.CreateDirectory(viewing.Directory);
                var start = new ProcessStartInfo(options.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = viewing.Directory };
                // FFmpeg sees only the authenticated local proxy. Source credentials never appear on its command line.
                foreach (var arg in new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-threads", "2", "-protocol_whitelist", "http,https,tcp,tls,crypto", "-allowed_extensions", "ALL", "-extension_picky", "0", "-headers", $"X-DzhiggaTV-Encoder: {viewing.EncoderToken}\r\n", "-i", serverUrl + viewing.Path("root", true), "-map", "0:v:0", "-map", "0:a:0?" }) start.ArgumentList.Add(arg);
                // Copying video preserves UHD resolution and avoids a costly 4K decode/encode.
                // fMP4 also supplies the container Safari requires for HEVC. Audio is always AAC.
                foreach (var arg in compatible && !transcode ? new[] { "-c:v", "copy" } : new[] { "-c:v", "libx264", "-preset", "veryfast", "-threads", "2", "-vf", $"scale=-2:'min({options.TranscodeHeight},ih)'", "-pix_fmt", "yuv420p", "-crf", "24" }) start.ArgumentList.Add(arg);
                foreach (var arg in new[] { "-c:a", "aac", "-b:a", "128k", "-ac", "2", "-f", "hls", "-hls_time", "4", "-hls_list_size", "6", "-hls_flags", "delete_segments+independent_segments+temp_file" }) start.ArgumentList.Add(arg);
                if (compatible && !transcode) foreach (var arg in new[] { "-hls_segment_type", "fmp4", "-hls_fmp4_init_filename", "init.mp4" }) start.ArgumentList.Add(arg);
                foreach (var arg in new[] { "-hls_segment_filename", compatible && !transcode ? "part%06d.m4s" : "part%06d.ts", "index.m3u8" }) start.ArgumentList.Add(arg);
                viewing.Process = Process.Start(start) ?? throw new RemoteException("Не удалось запустить FFmpeg.");
                _ = DrainAsync(viewing.Process.StandardError); _ = DrainAsync(viewing.Process.StandardOutput);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token, viewing.Cancellation.Token); wait.CancelAfter(TimeSpan.FromSeconds(40));
                while (!File.Exists(Path.Combine(viewing.Directory, "index.m3u8")))
                { if (viewing.Process.HasExited) throw new RemoteException("FFmpeg не смог открыть поток. Проверьте поддержку H.264/AAC в выбранной сборке FFmpeg.", 415); await Task.Delay(200, wait.Token); }
                return new { url = $"/converted/{viewing.Id}/index.m3u8", mode = viewing.Mode, channelId };
            }
            return new { url = viewing.Path("root"), mode = "proxy", channelId };
        }
        catch { lock (_gate) { if (_streams.ContainsKey(viewing.Id)) Remove(viewing); } throw; }
    }
    public void Stop(string? deviceId = null)
    {
        lock (_gate)
        {
            if (deviceId is null) { _epoch++; _starts.Clear(); } else _starts[deviceId] = _starts.GetValueOrDefault(deviceId) + 1;
            foreach (var v in _streams.Values.Where(v => deviceId is null || v.DeviceId == deviceId).ToArray()) Remove(v);
        }
    }
    public void Touch(string deviceId) { lock (_gate) foreach (var v in _streams.Values.Where(v => v.DeviceId == deviceId)) v.Touched = DateTimeOffset.UtcNow; }
    public string? EncoderDevice(string id, string key)
    {
        lock (_gate)
        {
            Prune();
            return _streams.TryGetValue(id, out var v) && v.UsesEncoder && key.Length == v.EncoderToken.Length &&
                System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(v.EncoderToken)) ? v.DeviceId : null;
        }
    }
    public async Task ProxyAsync(HttpContext context, string deviceId, string id, string resource, bool encoder = false)
    {
        var viewing = Resolve(deviceId, id, !encoder);
        // Re-evaluate visibility/parental controls for every resource, even an already issued capability.
        await _library.SourceAsync(viewing.Source.ChannelId, context.RequestAborted);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, viewing.Cancellation.Token); timeout.CancelAfter(TimeSpan.FromSeconds(35));
        Uri uri = viewing.Resolve(resource);
        using var response = await OpenAsync(viewing, uri, context.Request.Headers.Range.ToString(), timeout.Token);
        if (!response.IsSuccessStatusCode) throw new RemoteException(response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden ? "IPTV-провайдер отклонил запрос. Возможно, исчерпан лимит подключений." : "IPTV-поток временно недоступен.", 502);
        var type = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        bool playlist = type.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) || response.RequestMessage!.RequestUri!.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || resource == "root" && !viewing.UsesEncoder;
        byte[] prefix = [];
        if (!playlist && type is "application/octet-stream" or "text/plain")
        {
            var sniff = await response.Content.ReadAsStreamAsync(timeout.Token); var probe = new byte[16];
            int size = await sniff.ReadAtLeastAsync(probe, 8, false, timeout.Token); prefix = probe[..size];
            playlist = Encoding.UTF8.GetString(prefix).TrimStart('\uFEFF', '\r', '\n', ' ').StartsWith("#EXTM3U", StringComparison.Ordinal);
        }
        context.Response.Headers.CacheControl = "no-store";
        if (playlist)
        {
            var text = await ReadPlaylistAsync(response, timeout.Token, prefix);
            var rewritten = HlsPlaylist.Rewrite(text, response.RequestMessage!.RequestUri!, uri => viewing.Register(uri, encoder));
            context.Response.ContentType = "application/vnd.apple.mpegurl";
            var bytes = Encoding.UTF8.GetBytes(rewritten); Interlocked.Add(ref viewing.Bytes, bytes.Length);
            await context.Response.Body.WriteAsync(bytes, timeout.Token);
        }
        else
        {
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = type == "application/octet-stream" ? Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() switch { ".ts" => "video/mp2t", ".m4s" or ".mp4" => "video/mp4", ".vtt" => "text/vtt", ".aac" => "audio/aac", _ => type } : type;
            if (response.Content.Headers.ContentRange is { } cr) context.Response.Headers.ContentRange = cr.ToString();
            if (response.Headers.AcceptRanges.Count > 0) context.Response.Headers.AcceptRanges = "bytes";
            if (response.Content.Headers.ContentLength is { } length) context.Response.ContentLength = length;
            if (prefix.Length > 0) { Interlocked.Add(ref viewing.Bytes, prefix.Length); await context.Response.Body.WriteAsync(prefix, timeout.Token); }
            using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[65536];
            int count; while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0) { timeout.CancelAfter(TimeSpan.FromSeconds(35)); Interlocked.Add(ref viewing.Bytes, count); await context.Response.Body.WriteAsync(buffer.AsMemory(0, count), timeout.Token); }
        }
    }
    public async Task ConvertedAsync(HttpContext context, string deviceId, string id, string file)
    {
        var v = Resolve(deviceId, id);
        await _library.SourceAsync(v.Source.ChannelId, context.RequestAborted);
        if (v.Directory is null || file is not ("index.m3u8" or "init.mp4") && !(file.StartsWith("part", StringComparison.Ordinal) && (file.Length == 13 && file.EndsWith(".ts", StringComparison.Ordinal) || file.Length == 14 && file.EndsWith(".m4s", StringComparison.Ordinal)) && file.AsSpan(4, 6).ToString().All(char.IsAsciiDigit))) throw new RemoteException("Фрагмент не найден.", 404);
        var path = Path.Combine(v.Directory, file);
        if (!File.Exists(path)) throw new RemoteException("Фрагмент ещё не готов. Повторите подключение.", 404);
        context.Response.ContentType = file == "index.m3u8" ? "application/vnd.apple.mpegurl" : file == "init.mp4" ? "video/mp4" : file.EndsWith(".m4s", StringComparison.Ordinal) ? "video/iso.segment" : "video/mp2t";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, v.Cancellation.Token);
        var data = await File.ReadAllBytesAsync(path, timeout.Token); Interlocked.Add(ref v.Bytes, data.Length); await context.Response.Body.WriteAsync(data, timeout.Token);
    }
    private async Task<HttpResponseMessage> OpenAsync(Viewing viewing, Uri uri, string? range, CancellationToken token)
    {
        for (int redirect = 0; redirect < 6; redirect++)
        {
            _policy.Validate(uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrEmpty(range))
            { if (!RangeHeaderValue.TryParse(range, out var value) || value.Ranges.Count != 1) throw new RemoteException("Некорректный диапазон.", 416); request.Headers.Range = value; }
            foreach (var header in viewing.Source.Headers)
                if ((header.Key is "User-Agent" or "Referer" or "Origin") || uri.GetLeftPart(UriPartial.Authority) == viewing.Source.Uri.GetLeftPart(UriPartial.Authority) && header.Key is "Authorization" or "Cookie") request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (!request.Headers.Contains("User-Agent")) request.Headers.TryAddWithoutValidation("User-Agent", "DzhiggaTV-Remote/2.0");
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var target = response.Headers.Location; response.Dispose();
                if (target is null) throw new RemoteException("Некорректное перенаправление IPTV.", 502);
                uri = new Uri(uri, target); continue;
            }
            return response;
        }
        throw new RemoteException("Слишком много перенаправлений IPTV.", 502);
    }
    private static async Task<string> ReadPlaylistAsync(HttpResponseMessage response, CancellationToken token, byte[]? prefix = null)
    {
        if (!response.IsSuccessStatusCode) throw new RemoteException("Провайдер не предоставил поток.", 502);
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream(); if (prefix is not null) output.Write(prefix); var buffer = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        { if (output.Length + read > 1024 * 1024) throw new RemoteException("HLS-плейлист слишком велик или формат потока несовместим с Safari.", 415); output.Write(buffer, 0, read); }
        return Encoding.UTF8.GetString(output.ToArray());
    }
    private Viewing Resolve(string deviceId, string id, bool touch = true)
    {
        lock (_gate) { Prune(); if (!_streams.TryGetValue(id, out var v) || v.DeviceId != deviceId) throw new RemoteException("Просмотр завершён. Выберите канал заново.", 404); if (touch) v.Touched = DateTimeOffset.UtcNow; return v; }
    }
    private void Prune() { foreach (var v in _streams.Values.Where(v => DateTimeOffset.UtcNow - v.Touched > TimeSpan.FromSeconds(65)).ToArray()) Remove(v); }
    private void Remove(Viewing v) { _streams.Remove(v.Id); v.Dispose(); }
    private static async Task DrainAsync(StreamReader reader) { try { var buffer = new char[1024]; while (await reader.ReadAsync(buffer) > 0) { } } catch (IOException) { } }
    public void Dispose() { Stop(); _client.Dispose(); }
    private sealed class Viewing(string deviceId, StreamSource source, string mode, SourcePolicy policy) : IDisposable
    {
        public string Id { get; } = DeviceRegistry.Token(); public string DeviceId { get; } = deviceId; public StreamSource Source { get; } = source;
        public string EncoderToken { get; } = DeviceRegistry.Token();
        public string Mode = mode; public long Bytes; public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow; public DateTimeOffset Touched = DateTimeOffset.UtcNow;
        public bool UsesEncoder => Mode is "transcode" or "compatible";
        public CancellationTokenSource Cancellation { get; } = new(); public string? Directory; public Process? Process;
        private readonly object _refsGate = new();
        private readonly Dictionary<string, (Uri Uri, DateTimeOffset Seen)> _refs = [];
        private readonly Dictionary<string, string> _urls = [];
        public string Path(string key, bool encoder = false) => $"/{(encoder ? "encoder" : "media")}/{Id}/{key}";
        public Uri Resolve(string key)
        {
            if (key == "root") return Source.Uri;
            lock (_refsGate) { if (!_refs.TryGetValue(key, out var value)) throw new RemoteException("Ссылка HLS устарела.", 404); return value.Uri; }
        }
        public string Register(Uri uri, bool encoder = false)
        {
            policy.Validate(uri);
            lock (_refsGate)
            {
                if (_urls.TryGetValue(uri.AbsoluteUri, out var key)) { _refs[key] = (uri, DateTimeOffset.UtcNow); return Path(key, encoder); }
                if (_refs.Count >= 4096)
                {
                    var expired = _refs.Where(r => r.Value.Seen < DateTimeOffset.UtcNow.AddMinutes(-2)).Take(1024).ToArray();
                    foreach (var r in expired) { _refs.Remove(r.Key); _urls.Remove(r.Value.Uri.AbsoluteUri); }
                    if (_refs.Count >= 4096) throw new RemoteException("Слишком много ссылок в HLS-плейлисте.", 502);
                }
                key = DeviceRegistry.Token(); _refs.Add(key, (uri, DateTimeOffset.UtcNow)); _urls.Add(uri.AbsoluteUri, key); return Path(key, encoder);
            }
        }
        public void Dispose()
        {
            Cancellation.Cancel();
            try { if (Process is { HasExited: false }) Process.Kill(true); } catch (InvalidOperationException) { }
            Process?.Dispose();
            // Only bounded generated files inside this session's own directory are removed.
            if (Directory is { } folder)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1500);
                    try { foreach (var p in System.IO.Directory.EnumerateFiles(folder)) File.Delete(p); System.IO.Directory.Delete(folder); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                });
            }
        }
    }
}
