namespace DzhiggaTV.App;

/// <summary>Starts a fresh inactivity interval when the pointer leaves playback controls.</summary>
internal sealed class FullscreenChromeVisibility
{
    public static readonly TimeSpan HideDelay = TimeSpan.FromSeconds(1);
    private DateTime _lastActivity = DateTime.UtcNow;
    private bool _held;

    public void Reveal(DateTime now) => _lastActivity = now;

    // Null means held open; zero means the inactivity interval has elapsed.
    public TimeSpan? Remaining(DateTime now, bool held)
    {
        if (held || _held) _lastActivity = now;
        _held = held;
        if (held) return null;
        var remaining = HideDelay - (now - _lastActivity);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}
