using L = DzhiggaTV.Localization.Texts;
using System.IO.Compression;
using System.Net.Http;
using DzhiggaTV.Core;

namespace DzhiggaTV.Services;

public static class EpgOneSources
{
    public static readonly Uri FullTransparent = new("https://epg.one/epg2.xml.gz");
    public static readonly Uri FullSquare = new("https://epg.one/epg.xml.gz");
    public static readonly Uri RussianTransparent = new("https://epg.one/ru2.xml.gz");
    public static readonly Uri TransparentLogos = new("https://epg.one/epgone_transparent_logo.zip");
    public static readonly Uri SquareLogos = new("https://epg.one/epgone_dark_logo.zip");
    public static readonly Uri[] ReferencePlaylists = [new("https://epg.one/edem_epg_ico.m3u8"), new("https://epg.one/edem_epg_ico2.m3u8"), new("https://epg.one/edem_epg_ico3.m3u8")];
}

public sealed record EpgProgress(string Stage, long BytesDownloaded, long? TotalBytes = null, int Programmes = 0);
public sealed record EpgRefreshResult(bool Updated, string Message, int Channels, int Programmes, int RejectedProgrammes,
    MappingDiagnostics Diagnostics, DateTimeOffset? LastUpdated);

public sealed class EpgOneService : IDisposable
{
    private readonly SqliteStore _store;
    private readonly RemoteFiles _remote;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private IReadOnlyList<EpgChannel> _channels = [];
    private IReadOnlyList<ReferenceChannel> _references = [];
    private ChannelMatcher _matcher = new([]);
    private MappingDiagnostics _diagnostics = new(0, 0, 0, 0, 0);
    public bool AllowInsecureHttp { get; set; }
    public DateTimeOffset? LastUpdated { get; private set; }
    public IReadOnlyList<EpgChannel> EpgChannels => _channels;
    public IReadOnlyList<ReferenceChannel> References => _references;
    public MappingDiagnostics Diagnostics => _diagnostics;
    public TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromMinutes(15);
    public long MaximumCompressedBytes { get; set; } = 160L * 1024 * 1024;
    public long MaximumDecompressedBytes { get; set; } = XmlTvParser.DefaultMaximumDecompressedBytes;

    public EpgOneService(SqliteStore store, HttpClient? client = null) { _store = store; _remote = new(client); }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _channels = await _store.GetEpgChannelsAsync(cancellationToken).ConfigureAwait(false);
        _references = await _store.GetSettingAsync<List<ReferenceChannel>>("epg.references", [], cancellationToken).ConfigureAwait(false);
        LastUpdated = await _store.GetSettingAsync<DateTimeOffset?>("epg.lastSuccessfulUpdate", null, cancellationToken).ConfigureAwait(false);
        _matcher = new(_channels, _references);
    }

    public async Task<EpgRefreshResult> RefreshAsync(IReadOnlyList<Channel> channels, IProgress<EpgProgress>? progress = null,
        CancellationToken cancellationToken = default, Uri? source = null)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var lastAttempt = await _store.GetSettingAsync<DateTimeOffset?>("epg.lastAttempt", null, cancellationToken).ConfigureAwait(false);
            if (lastAttempt is { } attempt && DateTimeOffset.UtcNow - attempt < TimeSpan.FromSeconds(60))
                return Result(false, L.T("Подождите минуту перед повторным обновлением."), 0, 0);
            await _store.SetSettingAsync("epg.lastAttempt", DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(DownloadTimeout);
            var token = deadline.Token;
            XmlTvParseResult? parsed = null;
            try
            {
                progress?.Report(new(L.T("Загрузка EPG.ONE"), 0));
                using var response = await _remote.OpenAsync(source ?? EpgOneSources.FullTransparent, AllowInsecureHttp, token).ConfigureAwait(false);
                var total = response.Content.Headers.ContentLength;
                if (total > MaximumCompressedBytes) throw new InvalidDataException(L.T("Архив EPG превышает допустимый размер."));
                await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                long previousProgress = 0;
                await using var compressed = new BoundedReadStream(body, MaximumCompressedBytes, true, read =>
                {
                    if (read - previousProgress < 128 * 1024 && read != total) return;
                    previousProgress = read; progress?.Report(new(L.T("Загрузка и импорт EPG.ONE"), read, total));
                }, token);
                // Detect gzip by its signature, not MIME: EPG.ONE serves gzip as application/octet-stream.
                var prefix = new byte[2]; await compressed.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
                await using var replay = new PrefixStream(prefix, compressed);
                var gzip = prefix[0] == 0x1f && prefix[1] == 0x8b;
                await _store.ImportEpgAsync(async (addChannel, addProgramme, importToken) =>
                {
                    parsed = await XmlTvParser.ParseAsync(replay, addChannel, addProgramme, importToken, gzip, MaximumDecompressedBytes).ConfigureAwait(false);
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await RegisterFailureAsync(cancellationToken).ConfigureAwait(false);
                return Result(false, L.T("Истекло время загрузки EPG. Последняя рабочая программа сохранена. Проверьте подключение и повторите обновление."), 0, 0);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or System.Xml.XmlException or InvalidDataException)
            {
                await RegisterFailureAsync(cancellationToken).ConfigureAwait(false);
                var reason = exception switch
                {
                    RemoteFileException transport => transport.Message,
                    InvalidDataException invalid => invalid.Message,
                    System.Xml.XmlException => L.T("Источник вернул повреждённый файл XMLTV."),
                    _ => L.T("Не удалось загрузить или записать файл EPG. Проверьте сеть и свободное место.")
                };
                return Result(false, L.F("Не удалось обновить EPG. {0} Последняя рабочая программа сохранена.", reason), 0, 0);
            }
            // Once the import commits, catalogue decoration cannot turn a successful programme update into a failed update.
            LastUpdated = DateTimeOffset.UtcNow;
            await _store.SetSettingAsync("epg.failureCount", 0, cancellationToken).ConfigureAwait(false);
            await RefreshReferenceMetadataAsync(cancellationToken).ConfigureAwait(false);
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            await MatchChannelsAsync(channels, cancellationToken).ConfigureAwait(false);
            var completed = parsed ?? throw new InvalidOperationException(L.T("Импорт XMLTV не завершен."));
            progress?.Report(new(L.T("Готово"), 0, null, completed.Programmes));
            return Result(true, L.F("EPG обновлена: {0:N0} передач.", completed.Programmes), completed.Programmes, completed.RejectedProgrammes);
        }
        finally { _refreshLock.Release(); }
    }

    private EpgRefreshResult Result(bool updated, string message, int programmes, int rejected) => new(updated, message, _channels.Count, programmes, rejected, _diagnostics, LastUpdated);
    private async Task RegisterFailureAsync(CancellationToken cancellationToken)
    {
        var failures = await _store.GetSettingAsync("epg.failureCount", 0, cancellationToken).ConfigureAwait(false);
        await _store.SetSettingAsync("epg.failureCount", Math.Min(failures + 1, 8), cancellationToken).ConfigureAwait(false);
    }

    public async Task<MappingDiagnostics> MatchChannelsAsync(IReadOnlyList<Channel> channels, CancellationToken cancellationToken = default)
    {
        var previous = (await _store.GetMappingsAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(x => x.ChannelId);
        var mappings = new List<ChannelMapping>(); int manual = 0, ambiguous = 0, unmatched = 0;
        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = _matcher.Match(channel, previous.GetValueOrDefault(channel.Id));
            if (result.Mapping is { } mapping) { mappings.Add(mapping); if (mapping.Manual) manual++; }
            else if (result.Ambiguous) ambiguous++;
            else unmatched++;
        }
        // Manual records are never written by automatic matching, including decisions made while this pass was running.
        await _store.ReplaceAutomaticMappingsAsync(mappings.Where(x => !x.Manual).ToArray(), channels.Select(x => x.Id).ToArray(), cancellationToken).ConfigureAwait(false);
        _diagnostics = new(channels.Count, mappings.Count - manual, manual, ambiguous, unmatched);
        return _diagnostics;
    }

    public IReadOnlyList<MatchCandidate> GetManualCandidates(Channel channel, string? search = null, int limit = 40) => _matcher.Candidates(channel, limit, search);

    public async Task SetManualMappingAsync(Channel channel, string epgId, CancellationToken cancellationToken = default)
    {
        var match = _channels.FirstOrDefault(x => x.Id == epgId) ?? throw new ArgumentException(L.T("Канал EPG не найден."), nameof(epgId));
        await _store.SaveMappingsAsync([new(channel.Id, match.Id, match.Name, L.T("Ручной выбор"), 1, true)], cancellationToken).ConfigureAwait(false);
    }

    public string? GetReferenceLogo(string epgId) => _references.FirstOrDefault(x => x.EpgId == epgId && !string.IsNullOrWhiteSpace(x.Logo))?.Logo;

    private async Task RefreshReferenceMetadataAsync(CancellationToken cancellationToken)
    {
        var last = await _store.GetSettingAsync<DateTimeOffset?>("epg.referenceAttempt", null, cancellationToken).ConfigureAwait(false);
        if (last.HasValue && DateTimeOffset.UtcNow - last.Value < TimeSpan.FromDays(1)) return;
        await _store.SetSettingAsync("epg.referenceAttempt", DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        // The catalogues offer different icon styles for the same metadata. One successful catalogue is sufficient.
        foreach (var uri in EpgOneSources.ReferencePlaylists)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var response = await _remote.OpenAsync(uri, AllowInsecureHttp, timeout.Token).ConfigureAwait(false);
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                await using var bounded = new BoundedReadStream(stream, 16 * 1024 * 1024, true, readCancellationToken: timeout.Token);
                var references = await ReferencePlaylistParser.ParseAsync(bounded, timeout.Token).ConfigureAwait(false);
                if (references.Count == 0) continue;
                _references = references;
                await _store.SetSettingAsync("epg.references", references, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException) { }
        }
    }

    /// <summary>Call only after the user enables EPG. Retries back off to six hours and never replace the last good database on failure.</summary>
    public async Task RunDailyAsync(Func<IReadOnlyList<Channel>> channels, IProgress<EpgProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            var failures = await _store.GetSettingAsync("epg.failureCount", 0, cancellationToken).ConfigureAwait(false);
            var lastAttempt = await _store.GetSettingAsync<DateTimeOffset?>("epg.lastAttempt", null, cancellationToken).ConfigureAwait(false);
            var interval = failures == 0 ? TimeSpan.FromDays(1) : TimeSpan.FromMinutes(Math.Min(360, 15 * Math.Pow(2, Math.Min(failures - 1, 5))));
            var last = failures == 0 ? LastUpdated : lastAttempt;
            if (!last.HasValue || DateTimeOffset.UtcNow - last.Value >= interval) await RefreshAsync(channels(), progress, cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose() { _remote.Dispose(); _refreshLock.Dispose(); }

    private sealed class PrefixStream(byte[] prefix, Stream inner) : Stream
    {
        private int _offset;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { if (_offset < prefix.Length) { var n = Math.Min(count, prefix.Length - _offset); prefix.AsSpan(_offset, n).CopyTo(buffer.AsSpan(offset)); _offset += n; return n; } return inner.Read(buffer, offset, count); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { if (_offset < prefix.Length) { var n = Math.Min(buffer.Length, prefix.Length - _offset); prefix.AsMemory(_offset, n).CopyTo(buffer); _offset += n; return ValueTask.FromResult(n); } return inner.ReadAsync(buffer, cancellationToken); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
