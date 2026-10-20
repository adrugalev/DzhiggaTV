using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DzhiggaTV.Core;

namespace DzhiggaTV.Services;

/// <summary>Lazy, persistent image cache. Call only for visible rows; at most four downloads run simultaneously.</summary>
public sealed class LogoService : IDisposable
{
    private readonly string _cache;
    private readonly string _images;
    private readonly RemoteFiles _remote;
    private readonly SemaphoreSlim _downloads = new(4, 4);
    private readonly SemaphoreSlim _diskLock = new(1, 1);
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failures = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyDictionary<string, string> _picons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private bool _indexLoaded;
    private bool _allowInsecureHttp;
    public long MaximumCacheBytes { get; }
    public long MaximumImageBytes { get; set; } = 2 * 1024 * 1024;
    public bool AllowInsecureHttp
    {
        get => _allowInsecureHttp;
        set { if (_allowInsecureHttp != value) { _allowInsecureHttp = value; ResetFailedDownloads(); } }
    }
    public TimeSpan CacheLifetime { get; set; } = TimeSpan.FromDays(14);

    public LogoService(string cacheDirectory, HttpClient? client = null, long maxCacheBytes = 256L * 1024 * 1024)
    {
        _cache = Path.GetFullPath(cacheDirectory); _images = Path.Combine(_cache, "images");
        Directory.CreateDirectory(_images); MaximumCacheBytes = maxCacheBytes; _remote = new(client);
    }

    /// <summary>Retry failed URLs after a user refresh or network policy change. Successful disk entries stay cached.</summary>
    public void ResetFailedDownloads() => _failures.Clear();

    public async Task<string?> ResolveAsync(Channel channel, string? userLogo = null, EpgChannel? mappedChannel = null,
        string? referenceLogo = null, CancellationToken cancellationToken = default)
    {
        foreach (var candidate in new[] { userLogo, channel.Logo, mappedChannel?.Logo, referenceLogo }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal))
        {
            var path = await ResolveOneAsync(candidate!, cancellationToken).ConfigureAwait(false);
            if (path is not null) return path;
        }
        await LoadIndexAsync(cancellationToken).ConfigureAwait(false);
        foreach (var key in new[] { mappedChannel?.Id, channel.TvgId, ChannelMatcher.NormalizeName(channel.Name), channel.Name })
            if (key is not null && _picons.TryGetValue(key, out var relative) && SafePath(relative) is { } local && File.Exists(local)) return local;
        return null;
    }

    public static string TextFallback(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => x.Any(char.IsLetterOrDigit)).Take(2).ToArray();
        return words.Length == 0 ? "TV" : string.Concat(words.Select(x => x.First(char.IsLetterOrDigit))).ToUpperInvariant();
    }

    private async Task<string?> ResolveOneAsync(string candidate, CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(candidate))).ToLowerInvariant();
            var path = Path.Combine(_images, key + ".img");
            if (File.Exists(path))
            {
                var fresh = DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < CacheLifetime;
                try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch (IOException) { }
                if (!fresh) _ = DownloadSafelyAsync(key, uri, path); // Coalesced stale-while-revalidate.
                return path;
            }
            if (_failures.TryGetValue(key, out var retryAfter) && retryAfter > DateTimeOffset.UtcNow) return null;
            return await DownloadSafelyAsync(key, uri, path).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        var local = uri?.IsFile == true ? uri.LocalPath : candidate;
        try
        {
            if (Path.IsPathFullyQualified(local) && File.Exists(local) && new FileInfo(local).Length <= MaximumImageBytes)
            {
                await using var input = File.OpenRead(local);
                return await IsSupportedImageAsync(input, cancellationToken).ConfigureAwait(false) ? Path.GetFullPath(local) : null;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }

    private Task<string?> DownloadSafelyAsync(string key, Uri uri, string path)
    {
        if (_failures.TryGetValue(key, out var retryAfter) && retryAfter > DateTimeOffset.UtcNow) return Task.FromResult<string?>(File.Exists(path) ? path : null);
        return _pending.GetOrAdd(key, _ => new Lazy<Task<string?>>(() => DownloadCoreAsync(key, uri, path))).Value;
    }

    private async Task<string?> DownloadCoreAsync(string key, Uri uri, string path)
    {
        string? temporary = null; bool acquired = false;
        try
        {
            await _downloads.WaitAsync(_lifetime.Token).ConfigureAwait(false); acquired = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await _remote.OpenAsync(uri, AllowInsecureHttp, timeout.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > MaximumImageBytes) throw new InvalidDataException("Логотип слишком большой.");
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var remote = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var bounded = new BoundedReadStream(remote, MaximumImageBytes, true, readCancellationToken: timeout.Token))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32768, true))
                await bounded.CopyToAsync(output, timeout.Token).ConfigureAwait(false);
            await using (var verify = File.OpenRead(temporary))
                if (!await IsSupportedImageAsync(verify, timeout.Token).ConfigureAwait(false)) throw new InvalidDataException("Неподдерживаемый формат логотипа.");
            File.Move(temporary, path, true); temporary = null;
            _failures.TryRemove(key, out _);
            await TrimCacheAsync(_lifetime.Token).ConfigureAwait(false);
            return path;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or OperationCanceledException or UnauthorizedAccessException)
        {
            _failures[key] = DateTimeOffset.UtcNow.AddHours(1);
            return File.Exists(path) ? path : null;
        }
        finally
        {
            if (temporary is not null) { try { File.Delete(temporary); } catch (IOException) { } }
            if (acquired) _downloads.Release();
            _pending.TryRemove(key, out _);
        }
    }

    public async Task<int> DownloadPiconsAsync(bool square = false, IProgress<EpgProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromMinutes(8));
        using var response = await _remote.OpenAsync(square ? EpgOneSources.SquareLogos : EpgOneSources.TransparentLogos, AllowInsecureHttp, timeout.Token).ConfigureAwait(false);
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using var bounded = new BoundedReadStream(input, Math.Min(MaximumCacheBytes, 128L * 1024 * 1024), true, bytes => progress?.Report(new("Загрузка пиконов", bytes, response.Content.Headers.ContentLength)), timeout.Token);
        return await ImportZipAsync(bounded, timeout.Token).ConfigureAwait(false);
    }

    public async Task<int> ImportZipAsync(Stream input, CancellationToken cancellationToken = default)
    {
        await _diskLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var archiveFile = Path.Combine(_cache, Guid.NewGuid().ToString("N") + ".zip.tmp");
        var folderName = "picons-" + Guid.NewGuid().ToString("N");
        var extracted = Path.Combine(_cache, folderName);
        bool committed = false;
        try
        {
            await using (var bounded = new BoundedReadStream(input, Math.Min(MaximumCacheBytes, 128L * 1024 * 1024), true, readCancellationToken: cancellationToken))
            await using (var file = new FileStream(archiveFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await bounded.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            using var zip = ZipFile.OpenRead(archiveFile);
            if (zip.Entries.Count > 16000) throw new InvalidDataException("Слишком много файлов в архиве пиконов.");
            Directory.CreateDirectory(extracted);
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var collisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0; int count = 0;
            foreach (var entry in zip.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Validate every entry, including unsupported formats and directory entries.
                var path = SafeArchivePath(extracted, entry.FullName);
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
                if (entry.Length > MaximumImageBytes) throw new InvalidDataException("Файл пикона слишком большой.");
                total += entry.Length;
                if (total > MaximumCacheBytes || total > 256L * 1024 * 1024) throw new InvalidDataException("Архив пиконов превышает допустимый размер.");
                var extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp")) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using (var stream = entry.Open())
                await using (var bounded = new BoundedReadStream(stream, MaximumImageBytes, true, readCancellationToken: cancellationToken))
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32768, true))
                    await bounded.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await using (var verify = File.OpenRead(path))
                    if (!await IsSupportedImageAsync(verify, cancellationToken).ConfigureAwait(false)) { File.Delete(path); continue; }
                var name = Path.GetFileNameWithoutExtension(path); var relative = Path.GetRelativePath(_cache, path);
                foreach (var key in new[] { name, ChannelMatcher.NormalizeName(name) }.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (index.TryGetValue(key, out var existing) && existing != relative) collisions.Add(key);
                    else index[key] = relative;
                }
                count++;
            }
            if (count == 0) throw new InvalidDataException("Архив не содержит поддерживаемых пиконов.");
            foreach (var key in collisions) index.Remove(key);
            var indexFile = Path.Combine(_cache, "picons-index.json"); var temporaryIndex = indexFile + ".tmp";
            await File.WriteAllTextAsync(temporaryIndex, JsonSerializer.Serialize(index), cancellationToken).ConfigureAwait(false);
            File.Move(temporaryIndex, indexFile, true); _picons = index; _indexLoaded = true; committed = true;
            // Clean only the service's validated, direct children. No archive paths participate in deletion.
            foreach (var old in Directory.EnumerateDirectories(_cache, "picons-*", SearchOption.TopDirectoryOnly))
                if (!string.Equals(old, extracted, StringComparison.OrdinalIgnoreCase)) TryDeleteOwnedFolder(old);
            return count;
        }
        finally
        {
            try { File.Delete(archiveFile); } catch (IOException) { }
            if (!committed) TryDeleteOwnedFolder(extracted);
            _diskLock.Release();
            if (committed) await TrimCacheAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void TryDeleteOwnedFolder(string path)
    {
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), _cache, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("picons-", StringComparison.Ordinal)) return;
        try { Directory.Delete(full, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static string SafeArchivePath(string root, string name)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (name.Contains(':') || Path.IsPathRooted(name)) throw new InvalidDataException("Небезопасный путь в архиве.");
        var destination = Path.GetFullPath(Path.Combine(fullRoot, name.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Путь архива выходит за каталог пиконов.");
        return destination;
    }

    private string? SafePath(string relative)
    { try { return SafeArchivePath(_cache, relative); } catch (Exception exception) when (exception is InvalidDataException or ArgumentException) { return null; } }

    private async Task LoadIndexAsync(CancellationToken cancellationToken)
    {
        if (_indexLoaded) return;
        await _diskLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_indexLoaded) return;
            var indexFile = Path.Combine(_cache, "picons-index.json");
            if (File.Exists(indexFile) && new FileInfo(indexFile).Length < 8 * 1024 * 1024)
            {
                try { _picons = new Dictionary<string, string>(JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(indexFile, cancellationToken).ConfigureAwait(false)) ?? [], StringComparer.OrdinalIgnoreCase); }
                catch (JsonException) { }
            }
            _indexLoaded = true;
        }
        finally { _diskLock.Release(); }
    }

    public async Task TrimCacheAsync(CancellationToken cancellationToken = default)
    {
        await _diskLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var files = Directory.EnumerateFiles(_cache, "*", SearchOption.AllDirectories).Select(x => new FileInfo(x)).Where(x => x.Extension is not ".tmp" and not ".json").ToArray();
            var size = files.Sum(x => x.Length);
            if (size <= MaximumCacheBytes) return;
            foreach (var file in files.OrderBy(x => x.LastAccessTimeUtc))
            {
                if (size <= MaximumCacheBytes) break;
                cancellationToken.ThrowIfCancellationRequested();
                try { var length = file.Length; file.Delete(); size -= length; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        finally { _diskLock.Release(); }
    }

    private static async Task<bool> IsSupportedImageAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[32]; int count = await stream.ReadAtLeastAsync(header, 26, false, cancellationToken).ConfigureAwait(false);
        if (count < 26) return false;
        int width, height;
        if (header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) { width = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4)); height = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4)); }
        else if (header.AsSpan(0, 3).SequenceEqual("GIF"u8)) { width = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6, 2)); height = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8, 2)); }
        else if (header[0] == 'B' && header[1] == 'M') { width = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(18, 4)); height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(22, 4))); }
        else if (header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff) return true;
        else return false;
        return width > 0 && height > 0 && width <= 8192 && height <= 8192 && (long)width * height <= 16 * 1024 * 1024;
    }

    public void Dispose() { _lifetime.Cancel(); _remote.Dispose(); _lifetime.Dispose(); /* Semaphores outlive cancelled in-flight tasks. */ }
}
