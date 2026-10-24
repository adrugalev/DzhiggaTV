using L = DzhiggaTV.Localization.Texts;
using DzhiggaTV.Core;

namespace DzhiggaTV.App;

internal static class ProgrammeTiming
{
    public static string Remaining(Programme? programme, DateTimeOffset now)
    {
        if (programme is null || programme.End <= programme.Start || programme.Start > now) return "";
        long minutes = (long)Math.Max(0, Math.Ceiling((programme.End - now).TotalMinutes));
        return minutes >= 60 ? L.F("{0} ч {1} мин", minutes / 60, minutes % 60) : L.F("{0} мин", minutes);
    }
}
