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
    private long _playingSince;
    private PlaybackState _state = PlaybackState.Idle;
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
    public async Task PlayAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url) || url.IndexOf('\0') >= 0) throw new ArgumentException("Некорректный адрес потока.", nameof(url));
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();
        PlaybackRequest request;
        PlaybackRequest? previous;
        lock (_requestGate)
        {
            request = new PlaybackRequest(++_generation, url, cancellationToken);
            previous = _request;
            _request = request;
        }
        previous?.Cancel();
        SetState(PlaybackState.Loading, "Подключение к трансляции…", request);
        try { await LoadRequestAsync(request).ConfigureAwait(false); }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { throw; }
        catch (MpvException)
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
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or MpvException) { }
    }

    public bool IsPaused
    {
        get => ReadString("pause") == "yes";
        set => WriteProperty("pause", value ? "yes" : "no");
    }
    public void TogglePause() => IsPaused = !IsPaused;
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
            await CommandAsync(request, "loadfile", request.Url, "replace").ConfigureAwait(false);
            if (!IsCurrent(request)) throw new OperationCanceledException(request.Token);
            var entry = ReadLong("playlist/0/id");
            lock (_requestGate)
            {
                if (!IsCurrentLocked(request)) return;
                _activeEntryId = entry ?? -1;
                _activeGeneration = request.Generation;
            }
            // Preserve user pause/volume state only through the explicit player properties; a channel starts playing.
            WriteProperty("pause", "no");
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
            result = await completion.Task.WaitAsync(_lifetime.Token).ConfigureAwait(false);
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
                // Native events wake this wait immediately; Dispose explicitly calls mpv_wakeup.
                // A timeout only woke every idle/playing instance ten times a second.
                var ptr = NativeMethods.mpv_wait_event(_handle, -1);
                if (ptr == IntPtr.Zero) continue;
                var evt = Marshal.PtrToStructure<MpvEvent>(ptr);
                switch (evt.EventId)
                {
                    case 1: return; // shutdown
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
            if (!_lifetime.IsCancellationRequested) SetState(PlaybackState.Error, "Видеодвижок остановился. Перезапустите приложение.");
        }
        finally { foreach (var command in _commands.Values) command.TrySetCanceled(); }
    }

    private void HandleProperty(MpvPropertyEvent property)
    {
        var name = Marshal.PtrToStringUTF8(property.Name);
        if (name == "track-list") { RaiseTracksChanged(); return; }
        PlaybackRequest? request;
        lock (_requestGate) request = _activeGeneration == _generation && _loadedEntryId == _activeEntryId ? _request : null;
        if (request == null || !IsCurrent(request)) return;
        if (ReadString("paused-for-cache") == "yes") SetState(PlaybackState.Buffering, "Буферизация…", request);
        else if (ReadString("pause") == "yes") SetState(PlaybackState.Paused, "Пауза", request);
        else if (_loadedEntryId >= 0) SetState(PlaybackState.Playing, "В эфире", request);
    }

    private void MarkPlaying(PlaybackRequest request)
    {
        if (!IsCurrent(request)) return;
        Interlocked.Exchange(ref _playingSince, Stopwatch.GetTimestamp());
        SetState(PlaybackState.Playing, "В эфире", request);
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
        if (!request.IsNetwork) { SetState(ended.Error < 0 ? PlaybackState.Error : PlaybackState.Idle, ended.Error < 0 ? "Не удалось открыть видео." : "Воспроизведение завершено", request); return; }
        ScheduleReconnect(request);
    }

    private void ScheduleReconnect(PlaybackRequest request)
    {
        if (!IsCurrent(request) || Interlocked.Exchange(ref request.ReconnectPending, 1) != 0) return;
        if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _playingSince)).TotalSeconds >= 30 && _playingSince != 0) request.Attempts = 0;
        Interlocked.Exchange(ref _playingSince, 0);
        int attempt = ++request.Attempts;
        if (attempt > 3)
        {
            Interlocked.Exchange(ref request.ReconnectPending, 0);
            SetState(PlaybackState.Error, "Поток недоступен. Выберите канал повторно, чтобы подключиться.", request);
            return;
        }
        SetState(PlaybackState.Reconnecting, $"Переподключение {attempt}/3…", request, attempt);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1)), request.Token).ConfigureAwait(false);
                if (!IsCurrent(request)) return;
                Interlocked.Exchange(ref request.ReconnectPending, 0);
                await LoadRequestAsync(request).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is MpvException or InvalidOperationException)
            {
                Interlocked.Exchange(ref request.ReconnectPending, 0);
                if (IsCurrent(request)) ScheduleReconnect(request);
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
            if (_disposed || _handle == IntPtr.Zero) return null;
            IntPtr str = NativeMethods.mpv_get_property_string(_handle, name);
            if (str == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUTF8(str); }
            finally { NativeMethods.mpv_free(str); }
        }
    }
    private long? ReadLong(string name)
    {
        lock (_nativeGate)
            return !_disposed && _handle != IntPtr.Zero && NativeMethods.GetInt64(_handle, name, 4, out var value) >= 0 ? value : null;
    }
    private double? ReadDouble(string name)
    {
        lock (_nativeGate)
            return !_disposed && _handle != IntPtr.Zero && NativeMethods.GetDouble(_handle, name, 5, out var value) >= 0 && double.IsFinite(value) ? value : null;
    }
    private static int? ToInt(long? value) => value is >= 0 and <= int.MaxValue ? (int)value.Value : null;

    private object? ReadNode(string name)
    {
        lock (_nativeGate)
        {
            if (_disposed || _handle == IntPtr.Zero) return null;
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
        lock (_requestGate)
        {
            if (request != null && !IsCurrentLocked(request)) return;
            if (expectedGeneration.HasValue && expectedGeneration.Value != _generation) return;
            if (_disposed && state != PlaybackState.Disposed) return;
            if (_state == state && state is not PlaybackState.Reconnecting) return;
            _state = state;
        }
        try { StateChanged?.Invoke(this, new(state, message, attempt)); } catch { /* UI subscribers must not stop the native event loop. */ }
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
        public int Attempts;
        public int ReconnectPending;
        private int _cancelled;
        public PlaybackRequest(long generation, string url, CancellationToken token)
        {
            Generation = generation;
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
