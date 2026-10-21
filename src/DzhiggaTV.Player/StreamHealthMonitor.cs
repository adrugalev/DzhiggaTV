namespace DzhiggaTV.Player;

internal enum StreamHealth { Starting, Healthy, Paused, Stalled, Reconnect }

/// <summary>Uses a monotonic clock and playback timestamps, never pixel changes or wall time.</summary>
internal sealed class StreamHealthMonitor(TimeSpan now)
{
    private TimeSpan _lastProgress = now;
    private TimeSpan? _steadySince, _seekSince;
    private double? _position;
    private bool _hasProgress;
    public bool IsStable(TimeSpan now) => _steadySince is { } since && now - since >= TimeSpan.FromSeconds(30) && now - _lastProgress < TimeSpan.FromSeconds(3);

    public StreamHealth Observe(TimeSpan now, double? position, bool paused, bool seeking)
    {
        if (paused)
        {
            _lastProgress = now; _position = position; _steadySince = _seekSince = null;
            return StreamHealth.Paused;
        }
        if (seeking)
        {
            _seekSince ??= now;
            if (now - _seekSince < TimeSpan.FromSeconds(10))
            {
                _lastProgress = now; _position = position; _steadySince = null;
                return StreamHealth.Starting;
            }
        }
        else _seekSince = null;
        if (position is { } at && double.IsFinite(at) && at >= 0 && (_position is null || Math.Abs(at - _position.Value) >= .05))
        {
            if (now - _lastProgress >= TimeSpan.FromSeconds(3)) _steadySince = null;
            _steadySince ??= now; _lastProgress = now; _position = at; _hasProgress = true;
            return StreamHealth.Healthy;
        }
        var silence = now - _lastProgress;
        if (silence >= TimeSpan.FromSeconds(3)) _steadySince = null;
        return silence >= TimeSpan.FromSeconds(15) ? StreamHealth.Reconnect :
            silence >= TimeSpan.FromSeconds(4) ? StreamHealth.Stalled : _hasProgress ? StreamHealth.Healthy : StreamHealth.Starting;
    }
}
