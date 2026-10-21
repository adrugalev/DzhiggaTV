using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace DzhiggaTV.Player;

/// <summary>Direct D3D11 libmpv output into a persistent HWND. Own and dispose one instance per video surface.</summary>
public sealed class MpvPlayer : IDisposable
{
    private readonly object _nativeGate = new();
    private readonly object _requestGate = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<int>> _commands = new();
    private readonly ConcurrentDictionary<long, MpvEndFile> _earlyEndEvents = new();
    private IntPtr _handle;
    private Task? _eventTask;
    private int _eventThreadId;
    private volatile bool _disposed;
    private long _generation;
    private long _commandId;
    private long _activeEntryId = -1;
    private long _activeGeneration;
    private long _loadedEntryId = -1;
    private PlaybackRequest? _request;
    private volatile bool _pauseRequested, _commandUnresponsive;
    private long _lastHealthPoll;
    private PlaybackRequest? _healthRequest;
    private long _healthSerial;
    private StreamHealthMonitor? _health;
    private HealthProbe? _healthProbe;
    private readonly Dictionary<ulong, (HealthProbe Probe, int Field)> _healthReplies = [];
    private PlaybackState _state = PlaybackState.Idle;
    private PlaybackStateChangedEventArgs _status = new(PlaybackState.Idle, "Готов к воспроизведению");
    private BufferPreset _bufferPreset = BufferPreset.Balanced;
    private HdrMode _hdrMode = HdrMode.Auto;
    private AspectMode _aspect = AspectMode.Original;
    private string _audioLanguage = "ru,rus,en,eng";
    private string _subtitleLanguage = "ru,rus,en,eng";
    private double _volume = 70;
    private bool _muted;

    public event EventHandler<PlaybackStateChangedEventArgs>? StateChanged;
    public event EventHandler? TracksChanged;
    public bool IsInitialized { get { lock (_nativeGate) return !_disposed && _handle != IntPtr.Zero; } }
    public PlaybackState State { get { lock (_requestGate) return _state; } }
    public PlaybackStateChangedEventArgs Status { get { lock (_requestGate) return _status; } }
    public bool RequiresRestart => _commandUnresponsive;

    public void Initialize(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) throw new ArgumentException("Требуется HWND видеоповерхности.", nameof(hwnd));
        lock (_nativeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_handle != IntPtr.Zero) return;
            _handle = NativeMethods.mpv_create();
            if (_handle == IntPtr.Zero) throw new InvalidOperationException("Не удалось создать libmpv.");
            try
            {
                // Never load user scripts/configs, expose URLs in the OSD, or request native log messages.
                Option("config", "no");
                Option("terminal", "no");
                Option("msg-level", "all=no");
                Option("load-scripts", "no");
                Option("ytdl", "no");
                Option("osc", "no");
                Option("osd-level", "0");
                Option("input-default-bindings", "no");
                Option("input-vo-keyboard", "no");
                Option("input-cursor", "no");
                Option("force-media-title", "DzhiggaTV");
                Option("idle", "yes");
                Option("keep-open", "no");
                Option("force-window", "yes");
                Option("wid", hwnd.ToInt64().ToString(CultureInfo.InvariantCulture));
                Option("vo", "gpu-next");
                Option("gpu-api", "d3d11");
                Option("gpu-context", "d3d11");
                Option("hwdec", "d3d11va");
                Option("target-colorspace-hint", "auto");
                Option("network-timeout", "12");
                Option("volume", _volume.ToString(CultureInfo.InvariantCulture));
                Option("mute", _muted ? "yes" : "no");
                Option("alang", _audioLanguage);
                Option("slang", _subtitleLanguage);
                ApplyBuffer((name, value) => Option(name, value));
                Check(NativeMethods.mpv_initialize(_handle), "инициализация");
                NativeMethods.mpv_request_log_messages(_handle, "no");
                // MPV_FORMAT_NONE asks for invalidation only; the event loop reads the actual value safely.
                NativeMethods.mpv_observe_property(_handle, 1, "pause", 0);
                NativeMethods.mpv_observe_property(_handle, 2, "paused-for-cache", 0);
                NativeMethods.mpv_observe_property(_handle, 3, "track-list", 0);
                ApplyAspect();
                ApplyHdr();
                _eventTask = Task.Factory.StartNew(EventLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            catch
            {
                NativeMethods.mpv_terminate_destroy(_handle);
                _handle = IntPtr.Zero;
                throw;
            }
        }
    }

    /// <summary>Accepts an unmodified source URL. Completion means libmpv accepted loadfile; Playing arrives as an event.</summary>
    public Task PlayAsync(string url, CancellationToken cancellationToken = default) => StartPlaybackAsync(url, null, cancellationToken);
    public Task PlayArchiveAsync(string url, double duration, CancellationToken cancellationToken = default, Func<double, ArchivePlaybackSource>? reconnectSource = null)
    {
        if (!double.IsFinite(duration) || duration <= 0) throw new ArgumentOutOfRangeException(nameof(duration));
        return StartPlaybackAsync(url, duration, cancellationToken, reconnectSource);
    }
    private async Task StartPlaybackAsync(string url, double? duration, CancellationToken cancellationToken, Func<double, ArchivePlaybackSource>? reconnectSource = null)
    {
        if (string.IsNullOrWhiteSpace(url) || url.IndexOf('\0') >= 0) throw new ArgumentException("Некорректный адрес потока.", nameof(url));
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();
        PlaybackRequest request;
        PlaybackRequest? previous;
        lock (_requestGate)
        {
            request = new PlaybackRequest(++_generation, url, cancellationToken, duration, reconnectSource);
            previous = _request;
            _request = request;
            _pauseRequested = false;
        }
        previous?.Cancel();
        SetState(PlaybackState.Loading, "Подключение к трансляции…", request);
        try { await LoadRequestAsync(request).ConfigureAwait(false); }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is MpvException or TimeoutException)
        {
            if (IsCurrent(request)) ScheduleReconnect(request);
            throw;
        }
    }

    public void Stop()
    {
        if (_disposed) return;
        PlaybackRequest? previous;
        long generation;
        lock (_requestGate) { generation = ++_generation; previous = _request; _request = null; }
        previous?.Cancel();
        if (IsInitialized) _ = StopSerializedAsync(generation);
        SetState(PlaybackState.Idle, "Воспроизведение остановлено", expectedGeneration: generation);
    }

    private async Task StopSerializedAsync(long generation)
    {
        try
        {
            await _loadGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                lock (_requestGate) if (_generation != generation || _request != null) return;
                await CommandAsync(null, "stop").ConfigureAwait(false);
            }
            finally { _loadGate.Release(); }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or MpvException or TimeoutException) { }
    }

    public bool IsPaused
    {
        get => ReadString("pause") is { } value ? value == "yes" : _pauseRequested;
        set { lock (_nativeGate) { _pauseRequested = value; WriteProperty("pause", value ? "yes" : "no"); } }
    }
    public void TogglePause() => IsPaused = !IsPaused;
    public PlaybackTimeline GetTimeline()
    {
        double? position = ReadDouble("time-pos");
        lock (_requestGate)
        {
            if (_request is not { } request || _activeGeneration != request.Generation) position = null;
            else if (request.Duration is not null && _state is PlaybackState.Error or PlaybackState.Idle) position = request.ResumePosition;
            else if (_activeEntryId < 0 || _loadedEntryId != _activeEntryId) position = null;
            else if (request.Duration is not null && position >= 0) { position += request.NativeOffset; request.ResumePosition = position.Value; }
        }
        return new(position, ReadDouble("duration"), ReadString("seekable") == "yes");
    }
    /// <summary>An IPTV archive may expose only a short, rolling HLS window even when seekable is yes.
    /// Outside confirmed cached ranges the caller must reopen the provider URL at the desired UTC time.</summary>
    public async Task<bool> SeekWithinCacheAsync(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return false;
        double offset; lock (_requestGate) offset = _request?.NativeOffset ?? 0;
        double nativeTarget = seconds - offset;
        if (ReadNode("demuxer-cache-state") is not Dictionary<string, object?> cache ||
            cache.GetValueOrDefault("seekable-ranges") is not List<object?> ranges ||
            !ranges.OfType<Dictionary<string, object?>>().Any(range =>
                range.GetValueOrDefault("start") is double start && range.GetValueOrDefault("end") is double end &&
                nativeTarget >= start && nativeTarget < end)) return false;
        try { return await SeekAsync(seconds).ConfigureAwait(false); }
        catch (MpvException) { return false; }
    }
    public async Task<bool> SeekAsync(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return false;
        PlaybackRequest? request; lock (_requestGate) request = _activeGeneration == _generation && _activeEntryId >= 0 && _loadedEntryId == _activeEntryId ? _request : null;
        if (request is null || ReadString("seekable") != "yes") return false;
        double nativeTarget = seconds - request.NativeOffset;
        if (nativeTarget < 0) return false;
        await CommandAsync(request, "seek", nativeTarget.ToString("0.###", CultureInfo.InvariantCulture), "absolute+exact").ConfigureAwait(false);
        lock (_requestGate) if (IsCurrentLocked(request)) request.ResumePosition = seconds;
        return IsCurrent(request);
    }
    public double Volume
    {
        get => ReadDouble("volume") ?? _volume;
        set { _volume = Math.Clamp(value, 0, 100); WriteProperty("volume", _volume.ToString(CultureInfo.InvariantCulture), allowBeforeInitialize: true); }
    }
    public bool IsMuted
    {
        get => ReadString("mute") is { } value ? value == "yes" : _muted;
        set { _muted = value; WriteProperty("mute", value ? "yes" : "no", allowBeforeInitialize: true); }
    }
    public void SelectAudio(long id) => WriteProperty("aid", id.ToString(CultureInfo.InvariantCulture));
    public void SelectSubtitle(long? id) => WriteProperty("sid", id?.ToString(CultureInfo.InvariantCulture) ?? "no");

    public void SetBufferPreset(BufferPreset preset)
    {
        _bufferPreset = preset;
        if (IsInitialized) ApplyBuffer((name, value) => WriteProperty(name, value));
    }
    public void SetAspect(AspectMode aspect) { _aspect = aspect; if (IsInitialized) ApplyAspect(); }
    public void SetHdrMode(HdrMode mode) { _hdrMode = mode; if (IsInitialized) ApplyHdr(); }
    public void SetAudioDevice(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOf('\0') >= 0) throw new ArgumentException("Не выбрано устройство.", nameof(name));
        WriteProperty("audio-device", name);
    }
    public void SetPreferredLanguages(string audio, string subtitles)
    {
        _audioLanguage = ValidateLanguage(audio);
        _subtitleLanguage = ValidateLanguage(subtitles);
        WriteProperty("alang", _audioLanguage, allowBeforeInitialize: true);
        WriteProperty("slang", _subtitleLanguage, allowBeforeInitialize: true);
    }

    public IReadOnlyList<MediaTrack> GetTracks()
    {
        if (ReadNode("track-list") is not List<object?> list) return [];
        return list.OfType<Dictionary<string, object?>>().Select(track => new MediaTrack(
            GetLong(track, "id") ?? 0, GetText(track, "type") ?? "unknown", GetText(track, "lang"),
            GetText(track, "title"), GetText(track, "codec"), GetBool(track, "selected"), GetBool(track, "external"))).ToArray();
    }
    public IReadOnlyList<AudioOutputDevice> GetAudioDevices()
    {
        if (ReadNode("audio-device-list") is not List<object?> list) return [];
        return list.OfType<Dictionary<string, object?>>().Where(item => GetText(item, "name") != null)
            .Select(item => new AudioOutputDevice(GetText(item, "name")!, GetText(item, "description") ?? GetText(item, "name")!)).ToArray();
    }

    public double? GetVideoAspectRatio()
    {
        // Display aspect includes non-square pixels (for example anamorphic SD TV).
        double? aspect = ReadDouble("video-out-params/aspect") ?? ReadDouble("video-params/aspect");
        if (aspect is not > 0) return null;
        long rotation = ReadLong("video-out-params/rotate") ?? ReadLong("video-params/rotate") ?? 0;
        return Math.Abs(rotation % 180) == 90 ? 1 / aspect : aspect;
    }

    public PlaybackStatistics GetStatistics()
    {
        // Poll at most once per second in the UI. Unknown/unsupported properties stay null.
        var cache = ReadNode("demuxer-cache-state") as Dictionary<string, object?>;
        return new PlaybackStatistics
        {
            Width = ToInt(ReadLong("video-params/w")), Height = ToInt(ReadLong("video-params/h")),
            FramesPerSecond = ReadDouble("estimated-vf-fps") ?? ReadDouble("container-fps"),
            VideoCodec = ReadString("current-tracks/video/codec"), AudioCodec = ReadString("current-tracks/audio/codec"),
            VideoDecoder = ReadString("current-tracks/video/decoder"), HardwareDecoder = ReadString("hwdec-current"),
            VideoBitrate = ReadDouble("video-bitrate"), AudioBitrate = ReadDouble("audio-bitrate"),
            BufferedSeconds = ReadDouble("demuxer-cache-duration"), BufferedBytes = cache == null ? null : GetLong(cache, "fw-bytes"),
            DroppedFrames = ReadLong("frame-drop-count"),
            SourceTransfer = ReadString("video-params/gamma"), SourcePrimaries = ReadString("video-params/primaries"), SourcePeak = ReadDouble("video-params/sig-peak"),
            TargetTransfer = ReadString("video-target-params/gamma"), TargetPrimaries = ReadString("video-target-params/primaries"),
            VideoOutput = ReadString("current-vo"), AudioTrackCount = GetTracks().Count(t => t.Type == "audio"),
            CachePaused = ReadString("paused-for-cache") is { } paused ? paused == "yes" : null,
            PlaybackPositionSeconds = ReadDouble("time-pos"),
            MpvVersion = ReadString("mpv-version")
        };
    }

    private async Task LoadRequestAsync(PlaybackRequest request)
    {
        await _loadGate.WaitAsync(request.Token).ConfigureAwait(false);
        try
        {
            if (!IsCurrent(request)) throw new OperationCanceledException(request.Token);
            double resumePosition = request.Duration is { } total ? Math.Min(request.ResumePosition, Math.Max(0, total - 1)) : 0;
            var source = request.ReconnectSource is not null && resumePosition > 0 ? request.ReconnectSource(resumePosition) : null;
            if (source is not null && (string.IsNullOrWhiteSpace(source.Url) || source.Url.IndexOf('\0') >= 0 || !double.IsFinite(source.Duration) || source.Duration <= 0))
                throw new InvalidOperationException("Не удалось восстановить адрес архива.");
            lock (_requestGate)
            {
                ++request.LoadSerial; request.HealthStalled = false;
                request.NativeOffset = source is null ? 0 : resumePosition;
                _activeEntryId = _loadedEntryId = -1; _activeGeneration = request.Generation;
            }
            if (request.Duration is { } duration)
                await CommandAsync(request, "loadfile", source?.Url ?? request.Url, "replace", "-1", $"start={(source is null ? Math.Min(request.ResumePosition, Math.Max(0, duration - 1)) : 0).ToString("0.###", CultureInfo.InvariantCulture)},end={(source?.Duration ?? duration).ToString("0.###", CultureInfo.InvariantCulture)}").ConfigureAwait(false);
            else await CommandAsync(request, "loadfile", request.Url, "replace").ConfigureAwait(false);
            if (!IsCurrent(request)) throw new OperationCanceledException(request.Token);
            var entry = ReadLong("playlist/0/id");
            lock (_requestGate)
            {
                if (!IsCurrentLocked(request)) return;
                _activeEntryId = entry ?? -1;
                _activeGeneration = request.Generation;
            }
            // A reconnect preserves an explicit pause; only a new user playback request clears it.
            lock (_nativeGate) WriteProperty("pause", _pauseRequested ? "yes" : "no");
            if (entry.HasValue && _earlyEndEvents.TryRemove(entry.Value, out var ended)) HandleEnded(ended);
            if (entry.HasValue && Interlocked.Read(ref _loadedEntryId) == entry.Value) MarkPlaying(request);
        }
        finally { _loadGate.Release(); }
    }

    private async Task CommandAsync(PlaybackRequest? request, params string[] args)
    {
        ulong id = unchecked((ulong)Interlocked.Increment(ref _commandId));
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands[id] = completion;
        try
        {
            int result;
            lock (_nativeGate)
            {
                EnsureInitializedLocked();
                if (request != null && !IsCurrent(request)) throw new OperationCanceledException(request.Token);
                result = NativeMethods.Command(_handle, id, args);
            }
            Check(result, "команда воспроизведения");
            using var registration = request?.Token.Register(() => AbortCommand(id));
            // Do not release the load gate when a request becomes stale. Wait for its native acknowledgement,
            // otherwise libmpv is permitted to reorder old and new asynchronous loadfile commands.
            try { result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(20), _lifetime.Token).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                AbortCommand(id);
                try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(3), _lifetime.Token).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    // Releasing the load gate and submitting more commands without an acknowledgement
                    // could let an obsolete channel win later. Stop accepting commands instead.
                    _commandUnresponsive = true;
                    SetState(PlaybackState.Error, "Видеодвижок не отвечает. Перезапустите приложение.");
                }
                throw;
            }
            if (request?.Token.IsCancellationRequested == true) throw new OperationCanceledException(request.Token);
            Check(result, "команда воспроизведения");
        }
        finally { _commands.TryRemove(id, out _); }
    }

    private void AbortCommand(ulong id)
    {
        lock (_nativeGate) if (!_disposed && _handle != IntPtr.Zero) NativeMethods.mpv_abort_async_command(_handle, id);
    }

    private void EventLoop()
    {
        _eventThreadId = Environment.CurrentManagedThreadId;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                bool monitor = PollStreamHealth();
                // Idle/local players sleep until an event. Network players check health once a second.
                var ptr = NativeMethods.mpv_wait_event(_handle, monitor ? 1 : -1);
                if (ptr == IntPtr.Zero) continue;
                var evt = Marshal.PtrToStructure<MpvEvent>(ptr);
                switch (evt.EventId)
                {
                    case 1:
                        if (!_lifetime.IsCancellationRequested)
                        { _commandUnresponsive = true; SetState(PlaybackState.Error, "Видеодвижок остановился. Перезапустите приложение."); }
                        return;
                    case 3: HandleHealthReply(evt); break;
                    case 5: if (_commands.TryGetValue(evt.ReplyUserdata, out var command)) command.TrySetResult(evt.Error); break;
                    case 7: if (evt.Data != IntPtr.Zero) HandleEnded(Marshal.PtrToStructure<MpvEndFile>(evt.Data)); break;
                    case 8:
                    {
                        var entry = ReadLong("playlist/0/id");
                        Interlocked.Exchange(ref _loadedEntryId, entry ?? -1);
                        PlaybackRequest? request;
                        lock (_requestGate) request = entry == _activeEntryId && _activeGeneration == _generation ? _request : null;
                        if (request != null) MarkPlaying(request);
                        RaiseTracksChanged();
                        break;
                    }
                    case 21:
                        if (evt.Data != IntPtr.Zero) HandleProperty(Marshal.PtrToStructure<MpvPropertyEvent>(evt.Data));
                        break;
                    case 24: // overflow: refresh observable state, never parse a native log payload.
                        RaiseTracksChanged();
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is not AccessViolationException)
        {
            if (!_lifetime.IsCancellationRequested)
            { _commandUnresponsive = true; SetState(PlaybackState.Error, "Видеодвижок остановился. Перезапустите приложение."); }
        }
        finally { foreach (var command in _commands.Values) command.TrySetCanceled(); }
    }

    private void HandleProperty(MpvPropertyEvent property)
    {
        var name = Marshal.PtrToStringUTF8(property.Name);
        if (name == "track-list") { RaiseTracksChanged(); return; }
        PlaybackRequest? request;
        lock (_requestGate) request = _state is not (PlaybackState.Error or PlaybackState.Idle) && _activeGeneration == _generation && _activeEntryId >= 0 && _loadedEntryId == _activeEntryId ? _request : null;
        if (request == null || !IsCurrent(request)) return;
        RefreshLoadedState(request);
    }

    private void RefreshLoadedState(PlaybackRequest request)
    {
        if (_pauseRequested || ReadString("pause") == "yes") SetState(PlaybackState.Paused, "Пауза", request);
        else if (request.HealthStalled) SetState(PlaybackState.Stalled, "Поток не отвечает. Проверяем подключение…", request);
        else if (ReadString("paused-for-cache") == "yes") SetState(PlaybackState.Buffering, "Буферизация…", request);
        else if (_loadedEntryId >= 0) SetState(PlaybackState.Playing, "В эфире", request);
    }

    private void MarkPlaying(PlaybackRequest request)
    {
        if (!IsCurrent(request) || request.Attempts > 3 || _commandUnresponsive) return;
        RefreshLoadedState(request);
    }

    private static TimeSpan MonotonicNow => Stopwatch.GetElapsedTime(0);
    private bool PollStreamHealth()
    {
        PlaybackRequest? request; long serial;
        lock (_requestGate)
        {
            request = _request; serial = request?.LoadSerial ?? 0;
            if (request is null || !IsCurrentLocked(request) || !request.IsNetwork || _commandUnresponsive ||
                _state is PlaybackState.Idle or PlaybackState.Error or PlaybackState.Disposed) return false;
            // The retry task can clear this flag after the last native event. Keep a heartbeat
            // so a silent server cannot leave the event loop asleep indefinitely at that point.
            if (Volatile.Read(ref request.ReconnectPending) != 0) return true;
        }
        if (_lastHealthPoll != 0 && Stopwatch.GetElapsedTime(_lastHealthPoll) < TimeSpan.FromSeconds(1)) return true;
        _lastHealthPoll = Stopwatch.GetTimestamp();
        var now = MonotonicNow;
        if (!ReferenceEquals(_healthRequest, request) || _healthSerial != serial)
        {
            _healthRequest = request; _healthSerial = serial; _health = new(now); _healthProbe = null; _healthReplies.Clear();
        }
        if (_healthProbe is { } previous)
        {
            if (now - previous.Started < TimeSpan.FromSeconds(3)) return true;
            _healthReplies.Clear(); _healthProbe = null;
            ApplyHealth(previous, now); // No property replies are also a failure to make progress.
            if (Volatile.Read(ref request.ReconnectPending) != 0) return true;
        }
        var probe = new HealthProbe(request, serial, now); _healthProbe = probe;
        string[] names = ["time-pos", "pause", "paused-for-cache", "seeking"];
        lock (_nativeGate)
        {
            if (_disposed || _handle == IntPtr.Zero) return false;
            for (int field = 0; field < names.Length; field++)
            {
                ulong id = unchecked((ulong)Interlocked.Increment(ref _commandId));
                if (NativeMethods.mpv_get_property_async(_handle, id, names[field], field == 0 ? 5 : 3) >= 0) _healthReplies[id] = (probe, field);
                else --probe.Remaining;
            }
        }
        if (probe.Remaining == 0) { _healthProbe = null; ApplyHealth(probe, now); }
        return true;
    }
    private void HandleHealthReply(MpvEvent evt)
    {
        if (!_healthReplies.Remove(evt.ReplyUserdata, out var reply) || !ReferenceEquals(_healthProbe, reply.Probe)) return;
        if (evt.Error >= 0 && evt.Data != IntPtr.Zero)
        {
            var property = Marshal.PtrToStructure<MpvPropertyEvent>(evt.Data);
            if (property.Data != IntPtr.Zero)
            {
                if (reply.Field == 0 && property.Format == 5) reply.Probe.Position = Marshal.PtrToStructure<double>(property.Data);
                else if (reply.Field > 0 && property.Format == 3) reply.Probe.Flags[reply.Field - 1] = Marshal.ReadInt32(property.Data) != 0;
            }
        }
        if (--reply.Probe.Remaining != 0) return;
        _healthProbe = null; ApplyHealth(reply.Probe, MonotonicNow);
    }
    private void ApplyHealth(HealthProbe probe, TimeSpan now)
    {
        var request = probe.Request;
        bool loaded;
        lock (_requestGate)
        {
            if (!IsCurrentLocked(request) || request.LoadSerial != probe.Serial || Volatile.Read(ref request.ReconnectPending) != 0 ||
                _state is PlaybackState.Idle or PlaybackState.Error or PlaybackState.Disposed || _health is null) return;
            loaded = _activeGeneration == request.Generation && _activeEntryId >= 0 && _loadedEntryId == _activeEntryId;
        }
        double? position = loaded ? probe.Position + request.NativeOffset : null;
        bool paused = _pauseRequested || loaded && probe.Flags[0];
        var health = _health.Observe(now, position, paused, loaded && probe.Flags[2]);
        if (position is { } at && double.IsFinite(at) && at >= 0 && request.Duration is not null) request.ResumePosition = at;
        if (_health.IsStable(now)) request.Attempts = 0;
        request.HealthStalled = health is StreamHealth.Stalled or StreamHealth.Reconnect;
        if (health == StreamHealth.Reconnect) ScheduleReconnect(request);
        else if (health == StreamHealth.Stalled) SetState(PlaybackState.Stalled, "Поток не отвечает. Проверяем подключение…", request);
        else if (paused) SetState(PlaybackState.Paused, "Пауза", request);
        else if (loaded) SetState(probe.Flags[1] ? PlaybackState.Buffering : PlaybackState.Playing, probe.Flags[1] ? "Буферизация…" : "В эфире", request);
    }

    private sealed class HealthProbe(PlaybackRequest request, long serial, TimeSpan started)
    {
        public PlaybackRequest Request { get; } = request;
        public long Serial { get; } = serial;
        public TimeSpan Started { get; } = started;
        public double? Position;
        public bool[] Flags { get; } = new bool[3];
        public int Remaining = 4;
    }

    private void HandleEnded(MpvEndFile ended)
    {
        if (ended.Reason is 2 or 3 or 5) return; // stop, quit, redirect: never reconnect a replaced channel.
        PlaybackRequest? request;
        lock (_requestGate)
        {
            request = ended.PlaylistEntryId == _activeEntryId && _activeGeneration == _generation ? _request : null;
            if (request == null)
            {
                if (_earlyEndEvents.Count > 16) _earlyEndEvents.Clear();
                _earlyEndEvents[ended.PlaylistEntryId] = ended;
                return;
            }
        }
        if (!IsCurrent(request)) return;
        if (request.Duration is { } duration && ended.Error >= 0 && (!request.IsNetwork || request.ResumePosition >= duration - 2))
        { request.ResumePosition = duration; SetState(PlaybackState.Idle, "Просмотр архива завершён", request); return; }
        if (!request.IsNetwork) { SetState(ended.Error < 0 ? PlaybackState.Error : PlaybackState.Idle, ended.Error < 0 ? "Не удалось открыть видео." : "Воспроизведение завершено", request); return; }
        ScheduleReconnect(request);
    }

    private void ScheduleReconnect(PlaybackRequest request)
    {
        if (!IsCurrent(request) || _commandUnresponsive || request.Attempts > 3) return;
        if (Interlocked.Exchange(ref request.ReconnectPending, 1) != 0) { Interlocked.Exchange(ref request.ReconnectAgain, 1); return; }
        int attempt = ++request.Attempts;
        if (attempt > 3)
        {
            Interlocked.Exchange(ref request.ReconnectPending, 0);
            SetState(PlaybackState.Error, "Поток недоступен. Попробуйте подключиться ещё раз.", request);
            return;
        }
        SetState(PlaybackState.Reconnecting, $"Переподключение {attempt}/3…", request, attempt);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1)), request.Token).ConfigureAwait(false);
                if (!IsCurrent(request)) return;
                await LoadRequestAsync(request).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is MpvException or InvalidOperationException or TimeoutException)
            {
                Interlocked.Exchange(ref request.ReconnectAgain, 1);
            }
            finally
            {
                Interlocked.Exchange(ref request.ReconnectPending, 0);
                if (Interlocked.Exchange(ref request.ReconnectAgain, 0) != 0 && IsCurrent(request)) ScheduleReconnect(request);
            }
        });
    }

    private void ApplyBuffer(Action<string, string> set)
    {
        // These are conservative starting limits, not measured latency guarantees. mpv's actual cache is observable.
        var (seconds, bytes, wait) = _bufferPreset switch
        {
            BufferPreset.LowLatency => (2, "32MiB", "0.3"),
            BufferPreset.Stable => (30, "128MiB", "3"),
            _ => (10, "64MiB", "1")
        };
        set("cache", "yes");
        set("cache-secs", seconds.ToString(CultureInfo.InvariantCulture));
        set("demuxer-readahead-secs", seconds.ToString(CultureInfo.InvariantCulture));
        set("demuxer-max-bytes", bytes);
        set("demuxer-max-back-bytes", "8MiB");
        set("cache-pause", "yes");
        set("cache-pause-wait", wait);
    }

    private void ApplyAspect()
    {
        WriteProperty("keepaspect", _aspect == AspectMode.Stretch ? "no" : "yes");
        WriteProperty("video-aspect-override", _aspect switch { AspectMode.Fit16By9 => "16:9", AspectMode.Fit4By3 => "4:3", _ => "-1" });
    }
    private void ApplyHdr()
    {
        WriteProperty("target-colorspace-hint", "auto");
        WriteProperty("target-trc", _hdrMode == HdrMode.ToneMapToSdr ? "bt.1886" : "auto");
        WriteProperty("target-prim", _hdrMode == HdrMode.ToneMapToSdr ? "bt.709" : "auto");
        // libplacebo handles HDR10/HLG and the detected target. We never claim that a requested mode is active.
    }

    private string? ReadString(string name)
    {
        lock (_nativeGate)
        {
            if (_disposed || _commandUnresponsive || _handle == IntPtr.Zero) return null;
            IntPtr str = NativeMethods.mpv_get_property_string(_handle, name);
            if (str == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUTF8(str); }
            finally { NativeMethods.mpv_free(str); }
        }
    }
    private long? ReadLong(string name)
    {
        lock (_nativeGate)
            return !_disposed && !_commandUnresponsive && _handle != IntPtr.Zero && NativeMethods.GetInt64(_handle, name, 4, out var value) >= 0 ? value : null;
    }
    private double? ReadDouble(string name)
    {
        lock (_nativeGate)
            return !_disposed && !_commandUnresponsive && _handle != IntPtr.Zero && NativeMethods.GetDouble(_handle, name, 5, out var value) >= 0 && double.IsFinite(value) ? value : null;
    }
    private static int? ToInt(long? value) => value is >= 0 and <= int.MaxValue ? (int)value.Value : null;

    private object? ReadNode(string name)
    {
        lock (_nativeGate)
        {
            if (_disposed || _commandUnresponsive || _handle == IntPtr.Zero) return null;
            if (NativeMethods.mpv_get_property(_handle, name, 6, out var node) < 0) return null;
            try { return CopyNode(node, 0); }
            finally { NativeMethods.mpv_free_node_contents(ref node); }
        }
    }
    private static object? CopyNode(MpvNode node, int depth)
    {
        if (depth > 8) return null;
        switch (node.Format)
        {
            case 1: return Marshal.PtrToStringUTF8(node.Pointer);
            case 3: return node.Flag != 0;
            case 4: return node.Int64;
            case 5: return node.Double;
            case 7:
            case 8:
            {
                if (node.Pointer == IntPtr.Zero) return null;
                var list = Marshal.PtrToStructure<MpvNodeList>(node.Pointer);
                if (list.Count < 0 || list.Count > 4096) return null;
                var array = new List<object?>(list.Count);
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (int i = 0; i < list.Count; i++)
                {
                    object? value = CopyNode(Marshal.PtrToStructure<MpvNode>(list.Values + i * 16), depth + 1);
                    if (node.Format == 7) array.Add(value);
                    else if (Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(list.Keys, i * IntPtr.Size)) is { } key) map[key] = value;
                }
                return node.Format == 7 ? array : map;
            }
            default: return null;
        }
    }
    private static string? GetText(Dictionary<string, object?> value, string key) => value.GetValueOrDefault(key) as string;
    private static long? GetLong(Dictionary<string, object?> value, string key) => value.GetValueOrDefault(key) is long number ? number : null;
    private static bool GetBool(Dictionary<string, object?> value, string key) => value.GetValueOrDefault(key) is true;

    private void Option(string name, string value) => Check(NativeMethods.mpv_set_option_string(_handle, name, value), "параметр " + name);
    private void WriteProperty(string name, string value, bool allowBeforeInitialize = false)
    {
        lock (_nativeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_handle == IntPtr.Zero && allowBeforeInitialize) return;
            EnsureInitializedLocked();
            Check(NativeMethods.mpv_set_property_string(_handle, name, value), "параметр " + name);
        }
    }
    private static void Check(int result, string operation) { if (result < 0) throw new MpvException(operation, result); }
    private void EnsureInitialized() { lock (_nativeGate) EnsureInitializedLocked(); }
    private void EnsureInitializedLocked()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("Сначала инициализируйте видеоповерхность.");
        if (_commandUnresponsive) throw new MpvException("видеодвижок не отвечает", -20);
    }
    private bool IsCurrent(PlaybackRequest request) { lock (_requestGate) return IsCurrentLocked(request); }
    private bool IsCurrentLocked(PlaybackRequest request) => !_lifetime.IsCancellationRequested && ReferenceEquals(_request, request) && !request.Token.IsCancellationRequested;
    private static string ValidateLanguage(string value)
    {
        if (value.Length > 100 || value.Any(c => !char.IsAsciiLetter(c) && c is not ',' and not '-')) throw new ArgumentException("Языки задаются кодами через запятую.");
        return value;
    }
    private void SetState(PlaybackState state, string message, PlaybackRequest? request = null, int attempt = 0, long? expectedGeneration = null)
    {
        PlaybackStateChangedEventArgs status;
        lock (_requestGate)
        {
            if (request != null && !IsCurrentLocked(request)) return;
            if (expectedGeneration.HasValue && expectedGeneration.Value != _generation) return;
            if (_disposed && state != PlaybackState.Disposed) return;
            if (_state == state && state is not PlaybackState.Reconnecting) return;
            _state = state;
            _status = status = new(state, message, attempt);
        }
        try { StateChanged?.Invoke(this, status); } catch { /* UI subscribers must not stop the native event loop. */ }
    }
    private void RaiseTracksChanged() { try { TracksChanged?.Invoke(this, EventArgs.Empty); } catch { } }

    public void Dispose()
    {
        IntPtr handle;
        lock (_nativeGate)
        {
            if (_disposed) return;
            _disposed = true;
            handle = _handle;
            _lifetime.Cancel();
            if (handle != IntPtr.Zero) NativeMethods.mpv_wakeup(handle);
        }
        lock (_requestGate) { _request?.Cancel(); _request = null; ++_generation; }
        void Finish()
        {
            try { _eventTask?.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            lock (_nativeGate)
            {
                if (handle != IntPtr.Zero) NativeMethods.mpv_terminate_destroy(handle);
                _handle = IntPtr.Zero;
            }
            foreach (var command in _commands.Values) command.TrySetCanceled();
            SetState(PlaybackState.Disposed, "Видеодвижок закрыт");
        }
        if (Environment.CurrentManagedThreadId == _eventThreadId) _ = Task.Run(Finish);
        else Finish();
        GC.SuppressFinalize(this);
    }

    private sealed class PlaybackRequest
    {
        private readonly CancellationTokenSource _cancellation;
        public long Generation { get; }
        public string Url { get; }
        public CancellationToken Token { get; }
        public bool IsNetwork { get; }
        public double? Duration { get; }
        public double ResumePosition { get; set; }
        public double NativeOffset { get; set; }
        public Func<double, ArchivePlaybackSource>? ReconnectSource { get; }
        public int Attempts;
        public int ReconnectPending;
        public int ReconnectAgain;
        public long LoadSerial;
        public bool HealthStalled;
        private int _cancelled;
        public PlaybackRequest(long generation, string url, CancellationToken token, double? duration = null, Func<double, ArchivePlaybackSource>? reconnectSource = null)
        {
            Generation = generation;
            Duration = duration;
            ReconnectSource = reconnectSource;
            Url = url;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            Token = _cancellation.Token;
            IsNetwork = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
        }
        public void Cancel()
        {
            if (Interlocked.Exchange(ref _cancelled, 1) != 0) return;
            try { _cancellation.Cancel(); }
            finally { _cancellation.Dispose(); }
        }
    }
}
