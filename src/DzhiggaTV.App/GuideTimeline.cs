using System.Windows;
using DzhiggaTV.Core;

namespace DzhiggaTV.App;

public sealed record GuideRange(DateTimeOffset Start, DateTimeOffset End)
{
    public static GuideRange ForDay(DateTime date, TimeZoneInfo zone)
    {
        var start = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        var end = start.AddDays(1);
        return new(new(start, zone.GetUtcOffset(start)), new(end, zone.GetUtcOffset(end)));
    }
    public bool Contains(DateTimeOffset time) => time >= Start && time < End;
    public double Width => (End - Start).TotalHours * GuideLayout.PixelsPerHour;
    public double Position(DateTimeOffset time) => (time - Start).TotalHours * GuideLayout.PixelsPerHour;
    public double FocusOffset(DateTimeOffset time, double viewportWidth) =>
        Math.Clamp(Position(time) - Math.Max(0, viewportWidth) * .22, 0, Math.Max(0, Width - viewportWidth));
}

public sealed record GuideRow(ChannelItem Channel, IReadOnlyList<Programme> Programmes)
{
    public Programme? At(DateTimeOffset time) => Programmes.FirstOrDefault(p => p.Start <= time && p.End > time);
}

public static class GuideLayout
{
    public const double ChannelWidth = 172;
    public const double PixelsPerHour = 240;
    public const double RowHeight = 76;
    public const double HeaderHeight = 42;

    public static Rect Cell(Programme programme, GuideRange range, double offset, double width)
    {
        if (programme.End <= range.Start || programme.Start >= range.End || programme.End <= programme.Start || width <= ChannelWidth) return Rect.Empty;
        double left = ChannelWidth + Math.Max(0, range.Position(programme.Start)) - offset;
        double right = ChannelWidth + Math.Min(range.Width, range.Position(programme.End)) - offset;
        left = Math.Max(ChannelWidth, left); right = Math.Min(width, right - 4);
        return right <= left ? Rect.Empty : new(left, 6, right - left, RowHeight - 12);
    }
    public static Programme? Hit(GuideRow row, GuideRange range, double offset, double width, Point point) =>
        row.Programmes.FirstOrDefault(p => Cell(p, range, offset, width).Contains(point));
}

/// <summary>Selection never starts playback. Only an explicit activation or a double click does.</summary>
public sealed class GuideSelection
{
    public GuideRow? Row { get; private set; }
    public Programme? Programme { get; private set; }
    public event EventHandler? Changed;
    public void Select(GuideRow row, Programme? programme)
    {
        if (programme is not null && !row.Programmes.Contains(programme)) throw new ArgumentException("Programme does not belong to the selected row.");
        if (Row == row && Programme == programme) return;
        Row = row; Programme = programme; Changed?.Invoke(this, EventArgs.Empty);
    }
    public void Clear()
    {
        if (Row is null && Programme is null) return;
        Row = null; Programme = null; Changed?.Invoke(this, EventArgs.Empty);
    }
    public async Task<bool> ClickAsync(GuideRow row, Programme? programme, int clickCount, Func<ChannelItem, Task> play)
    {
        Select(row, programme);
        if (clickCount != 2) return false;
        await play(row.Channel); return true;
    }
}

public sealed class GuideViewport
{
    public GuideRange Range { get; set; } = GuideRange.ForDay(DateTime.Today, TimeZoneInfo.Local);
    public double Offset { get; set; }
    public double Width { get; set; }
    public DateTimeOffset Now { get; set; } = DateTimeOffset.Now;
    public GuideSelection Selection { get; } = new();
}
