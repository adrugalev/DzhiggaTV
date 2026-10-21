namespace DzhiggaTV.App;

public sealed record ArchiveTimeline(DateTimeOffset Start, DateTimeOffset End, DateTimeOffset Position, bool IsArchive)
{
    public double OffsetAt(double x, double width, double thumbWidth)
    {
        double length = width - thumbWidth;
        if (!double.IsFinite(x) || !double.IsFinite(length) || length <= 0) return 0;
        return Math.Clamp((x - thumbWidth / 2) / length, 0, 1) * Math.Max(0, (End - Start).TotalSeconds);
    }

    // Preview and playback use the same boundary: the exclusive end is not a playable frame.
    public DateTimeOffset TimeAt(double seconds) => Start.AddSeconds(Math.Clamp(
        double.IsFinite(seconds) ? seconds : 0, 0, Math.Max(0, (End - Start).TotalSeconds - 1)));
}
