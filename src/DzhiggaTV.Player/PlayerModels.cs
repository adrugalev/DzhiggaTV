namespace DzhiggaTV.Player;

public enum BufferPreset { LowLatency, Balanced, Stable }
public enum AspectMode { Original, Fit16By9, Fit4By3, Stretch }
public enum HdrMode { Auto, ToneMapToSdr }
public enum PlaybackState { Idle, Loading, Playing, Paused, Buffering, Reconnecting, Error, Disposed, Stalled }
public enum NativePointerKind { Move, LeftDown, RightDown, Wheel }
public sealed record ArchivePlaybackSource(string Url, double Duration);

public sealed class NativePointerEventArgs(NativePointerKind kind, int wheelDelta = 0, int screenX = 0, int screenY = 0) : EventArgs
{
    public NativePointerKind Kind { get; } = kind;
    public int WheelDelta { get; } = wheelDelta;
    public int ScreenX { get; } = screenX;
    public int ScreenY { get; } = screenY;
}

/// <summary>Callbacks may run on a background thread. Message never contains a media URL.</summary>
public sealed class PlaybackStateChangedEventArgs(PlaybackState state, string message, int reconnectAttempt = 0) : EventArgs
{
    public PlaybackState State { get; } = state;
    public string Message { get; } = message;
    public int ReconnectAttempt { get; } = reconnectAttempt;
}

public sealed record MediaTrack(long Id, string Type, string? Language, string? Title, string? Codec, bool Selected, bool External)
{
    public string DisplayName => string.Join(" · ", new[] { Language, Title, Codec }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } label ? label : $"Дорожка {Id}";
}

public sealed record AudioOutputDevice(string Name, string Description);
public sealed record PlaybackTimeline(double? Position, double? Duration, bool Seekable);

/// <summary>Only actual libmpv values. A null value means unavailable, never zero or an inferred capability.</summary>
public sealed record PlaybackStatistics
{
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? FramesPerSecond { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public string? VideoDecoder { get; init; }
    public string? HardwareDecoder { get; init; }
    public double? VideoBitrate { get; init; }
    public double? AudioBitrate { get; init; }
    public double? BufferedSeconds { get; init; }
    public long? BufferedBytes { get; init; }
    public long? DroppedFrames { get; init; }
    public string? SourceTransfer { get; init; }
    public string? SourcePrimaries { get; init; }
    public double? SourcePeak { get; init; }
    public string? TargetTransfer { get; init; }
    public string? TargetPrimaries { get; init; }
    public string? VideoOutput { get; init; }
    public int AudioTrackCount { get; init; }
    public bool? CachePaused { get; init; }
    public double? PlaybackPositionSeconds { get; init; }
    public string? MpvVersion { get; init; }
    /// <summary>Source transfer metadata; this does not claim the Windows output is HDR.</summary>
    public string SourceDynamicRange => SourceTransfer switch
    {
        "pq" or "smpte2084" => "HDR / PQ",
        "hlg" or "arib-std-b67" => "HLG",
        "bt.1886" or "bt.709" or "srgb" or "gamma1.8" or "gamma2.0" or "gamma2.2" or "gamma2.4" or "gamma2.6" or "gamma2.8" => "SDR",
        null => "Недоступно",
        _ => "Передаточная функция: " + SourceTransfer
    };
}

public sealed class MpvException : Exception
{
    public int ErrorCode { get; }
    internal MpvException(string operation, int error) : base($"libmpv: {operation} (код {error}).") => ErrorCode = error;
}
