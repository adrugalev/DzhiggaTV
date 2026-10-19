using DzhiggaTV.Core;

namespace DzhiggaTV.App;

internal static class ProgrammeTiming
{
    public static string Remaining(Programme? programme, DateTimeOffset now)
    {
        if (programme is null || programme.End <= programme.Start || programme.Start > now) return "—";
        var remaining = TimeSpan.FromSeconds(Math.Max(0, Math.Ceiling((programme.End - now).TotalSeconds)));
        return remaining.TotalHours >= 1
            ? $"{(long)remaining.TotalHours}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"{remaining.Minutes:00}:{remaining.Seconds:00}";
    }
}
