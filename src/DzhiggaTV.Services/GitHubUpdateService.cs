using L = DzhiggaTV.Localization.Texts;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DzhiggaTV.Services;

public sealed class UpdateException(string message) : Exception(message);
public sealed record AppRelease(Version Version, string Tag, string Notes, Uri Page, string AssetName,
    Uri Download, long Size, string? Sha256, Uri? ChecksumDownload, UpdateAsset? Delta = null);
public sealed record UpdateAsset(string Name, Uri Download, long Size, string? Sha256, Uri? ChecksumDownload);
public sealed record UpdateDownloadProgress(long Received, long Total)
{
    public double Percent => Total > 0 ? Math.Min(100, Received * 100d / Total) : 0;
}

/// <summary>Public, stable GitHub Releases only; no account tokens or playlist data.</summary>
public sealed class GitHubUpdateService : IDisposable
{
    public const string Repository = "adrugalev/DzhiggaTV";
    public const string InstallerName = "DzhiggaTV-Setup-x64.exe";
    public const string DeltaName = "DzhiggaTV-Update-x64.zip";
    public const long MaximumInstallerSize = 512L * 1024 * 1024;
    private static readonly Uri Latest = new($"https://api.github.com/repos/{Repository}/releases/latest");
    private readonly HttpClient _http;
    private string? _etag;
    private AppRelease? _cached;
    private DateTimeOffset _retryAfter;

    public GitHubUpdateService(HttpMessageHandler? handler = null)
    {
        _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DzhiggaTV-Updater/1.0");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public static bool TryVersion(string? tag, out Version version)
    {
        version = new(0, 0, 0, 0);
        if (tag is null || !Regex.IsMatch(tag, @"^[vV]?\d+\.\d+\.\d+(?:\.\d+)?$")) return false;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var parsed)) return false;
        version = Normalize(parsed); return true;
    }
    public static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    public async Task<AppRelease?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (DateTimeOffset.UtcNow < _retryAfter) throw new UpdateException(L.T("GitHub временно ограничил проверки. Попробуйте позже."));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, Latest);
        if (_etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", _etag);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified) return _cached;
        if (response.StatusCode == HttpStatusCode.NotFound) { _cached = null; _etag = null; return null; }
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            _retryAfter = DateTimeOffset.UtcNow.AddHours(1);
            throw new UpdateException(L.T("GitHub временно ограничил проверки. Попробуйте позже."));
        }
        if (!response.IsSuccessStatusCode) throw new UpdateException(L.T("Не удалось проверить обновления. Попробуйте позже."));
        var json = Encoding.UTF8.GetString(await ReadLimitedAsync(response.Content, 1024 * 1024, timeout.Token).ConfigureAwait(false));
        var release = ParseRelease(json);
        _cached = release; _etag = response.Headers.ETag?.ToString();
        return release;
    }

    public static AppRelease? ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (!root.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
            !root.TryGetProperty("prerelease", out var preview) || preview.ValueKind != JsonValueKind.False ||
            !TryVersion(Text(root, "tag_name"), out var version)) return null;
        var tag = Text(root, "tag_name")!;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        var candidates = assets.EnumerateArray().Where(a => Text(a, "state") == "uploaded" &&
            (Text(a, "name") == InstallerName || Text(a, "name") == $"DzhiggaTV-{tag.TrimStart('v', 'V')}-Setup-x64.exe")).ToArray();
        // Prefer the canonical installer if a release also contains a versioned copy.
        var asset = candidates.OrderByDescending(a => Text(a, "name") == InstallerName).FirstOrDefault();
        if (asset.ValueKind == JsonValueKind.Undefined) return null;
        string name = Text(asset, "name")!;
        if (!asset.TryGetProperty("size", out var sizeValue) || !sizeValue.TryGetInt64(out var size) || size is <= 0 or > MaximumInstallerSize)
            throw new UpdateException(L.T("Установщик в выпуске имеет недопустимый размер."));
        var download = AssetUri(Text(asset, "browser_download_url"), tag, name);
        var digest = Text(asset, "digest");
        string? hash = digest is not null && Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$") ? digest[7..].ToLowerInvariant() : null;
        Uri? checksum = null;
        foreach (var candidate in assets.EnumerateArray())
            if (Text(candidate, "name") == name + ".sha256" && Text(candidate, "state") == "uploaded")
            { checksum = AssetUri(Text(candidate, "browser_download_url"), tag, name + ".sha256"); break; }
        if (hash is null && checksum is null) throw new UpdateException(L.T("Для обновления нужна контрольная сумма установщика. Она ещё не опубликована."));
        string notes = Text(root, "body") ?? L.T("Описание изменений пока не добавлено.");
        if (notes.Length > 64000) notes = notes[..64000] + "\n…";
        UpdateAsset? delta = null;
        // Optional optimization: a malformed delta must never hide a valid full installer.
        try
        {
            var matches = assets.EnumerateArray().Where(a => Text(a, "name") == DeltaName && Text(a, "state") == "uploaded").ToArray();
            if (matches.Length == 1 && matches[0].TryGetProperty("size", out var deltaSize) && deltaSize.TryGetInt64(out long bytes) && bytes > 0 && bytes < size)
            {
                var deltaUri = AssetUri(Text(matches[0], "browser_download_url"), tag, DeltaName);
                string? deltaDigest = Text(matches[0], "digest");
                string? deltaHash = deltaDigest is not null && Regex.IsMatch(deltaDigest, "^sha256:[0-9a-fA-F]{64}$") ? deltaDigest[7..].ToLowerInvariant() : null;
                Uri? deltaChecksum = null;
                var sidecars = assets.EnumerateArray().Where(a => Text(a, "name") == DeltaName + ".sha256" && Text(a, "state") == "uploaded").ToArray();
                if (sidecars.Length == 1) deltaChecksum = AssetUri(Text(sidecars[0], "browser_download_url"), tag, DeltaName + ".sha256");
                if (deltaHash is not null || deltaChecksum is not null) delta = new(DeltaName, deltaUri, bytes, deltaHash, deltaChecksum);
            }
        }
        catch (UpdateException) { }
        return new(version, tag, notes, new($"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(tag)}"), name, download, size, hash, checksum, delta);
    }

    public Task<string> DownloadDeltaAsync(AppRelease release, string cacheDirectory, IProgress<UpdateDownloadProgress>? progress, CancellationToken token)
    {
        var delta = release.Delta ?? throw new UpdateException(L.T("Пакет изменений отсутствует."));
        return DownloadAsync(release with { AssetName = delta.Name, Download = delta.Download, Size = delta.Size, Sha256 = delta.Sha256, ChecksumDownload = delta.ChecksumDownload, Delta = null }, cacheDirectory, progress, token);
    }

    public async Task<string> DownloadAsync(AppRelease release, string cacheDirectory, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        _ = AssetUri(release.Download.AbsoluteUri, release.Tag, release.AssetName);
        if (release.AssetName != InstallerName && release.AssetName != DeltaName && release.AssetName != $"DzhiggaTV-{release.Tag.TrimStart('v', 'V')}-Setup-x64.exe")
            throw new UpdateException(L.T("Недопустимое имя пакета обновления."));
        if (release.Size is <= 0 or > MaximumInstallerSize) throw new UpdateException(L.T("Недопустимый размер обновления."));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(20));
        var token = timeout.Token;
        string? expected = release.Sha256;
        if (expected is null && release.ChecksumDownload is not null)
        {
            _ = AssetUri(release.ChecksumDownload.AbsoluteUri, release.Tag, release.AssetName + ".sha256");
            using var checksumResponse = await GetAssetAsync(release.ChecksumDownload, token).ConfigureAwait(false);
            string checksumText = Encoding.UTF8.GetString(await ReadLimitedAsync(checksumResponse.Content, 4096, token).ConfigureAwait(false));
            var match = Regex.Match(checksumText.Trim(), @"^([a-fA-F0-9]{64})\s+\*?" + Regex.Escape(release.AssetName) + @"$");
            if (match.Success) expected = match.Groups[1].Value;
        }
        if (expected is null || !Regex.IsMatch(expected, "^[0-9a-fA-F]{64}$")) throw new UpdateException(L.T("Не удалось проверить контрольную сумму обновления."));
        string directory = Path.Combine(Path.GetFullPath(cacheDirectory), release.Version.ToString(4));
        Directory.CreateDirectory(directory);
        string final = Path.Combine(directory, release.AssetName), partial = final + ".part";
        if (File.Exists(final) && new FileInfo(final).Length == release.Size && await HasHashAsync(final, expected, token).ConfigureAwait(false))
        { progress?.Report(new(release.Size, release.Size)); return final; }
        try
        {
            using var response = await GetAssetAsync(release.Download, token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is { } length && length != release.Size) throw new UpdateException(L.T("Размер обновления не совпадает с опубликованным."));
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0, reported = -1024 * 1024;
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            {
                byte[] buffer = new byte[65536]; int count;
                while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    received += count;
                    if (received > release.Size) throw new UpdateException(L.T("Размер обновления превышает опубликованный."));
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false); hash.AppendData(buffer, 0, count);
                    if (received - reported >= 1024 * 1024) { progress?.Report(new(received, release.Size)); reported = received; }
                }
            }
            if (received != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException(L.T("Файл обновления повреждён. Повторите загрузку."));
            File.Move(partial, final, true); progress?.Report(new(received, release.Size)); return final;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    private async Task<HttpResponseMessage> GetAssetAsync(Uri uri, CancellationToken token)
    {
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            if (!IsDownloadHost(uri)) throw new UpdateException(L.T("GitHub вернул недопустимый адрес обновления."));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Clear(); request.Headers.Accept.Add(new("application/octet-stream"));
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            // Also validate injected handlers' final URI, without relying on automatic redirects.
            if (response.RequestMessage?.RequestUri is { } actual && !IsDownloadHost(actual)) { response.Dispose(); throw new UpdateException(L.T("Недопустимое перенаправление обновления.")); }
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location; response.Dispose();
                if (location is null) throw new UpdateException(L.T("GitHub не вернул адрес загрузки."));
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location); continue;
            }
            if (!response.IsSuccessStatusCode) { response.Dispose(); throw new UpdateException(L.T("Не удалось скачать обновление. Попробуйте ещё раз.")); }
            return response;
        }
        throw new UpdateException(L.T("Слишком много перенаправлений при загрузке обновления."));
    }

    private static bool IsDownloadHost(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" && uri.Port == 443 && string.IsNullOrEmpty(uri.UserInfo) &&
        uri.Host is "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";
    private static Uri AssetUri(string? value, string tag, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsDownloadHost(uri) || uri.Host != "github.com" ||
            !uri.AbsolutePath.Equals($"/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}", StringComparison.Ordinal) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new UpdateException(L.T("Адрес установщика не принадлежит выпуску DzhiggaTV."));
        return uri;
    }
    private static string? Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int maximum, CancellationToken token)
    {
        if (content.Headers.ContentLength > maximum) throw new UpdateException(L.T("Ответ сервера обновлений слишком большой."));
        using var output = new MemoryStream(); await using var input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        byte[] buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        { if (output.Length + count > maximum) throw new UpdateException(L.T("Ответ сервера обновлений слишком большой.")); await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false); }
        return output.ToArray();
    }
    private static async Task<bool> HasHashAsync(string path, string expected, CancellationToken token)
    { await using var file = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false)).Equals(expected, StringComparison.OrdinalIgnoreCase); }
    public void Dispose() => _http.Dispose();
}
