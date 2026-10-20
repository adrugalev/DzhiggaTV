using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.ComponentModel;
using DzhiggaTV.Core;

namespace DzhiggaTV.App;

internal static class GuideDrawing
{
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
    public static SolidColorBrush Color(string dark, string light) => new((Color)ColorConverter.ConvertFromString(Theme.IsLight ? light : dark));
    public static void Text(DrawingContext dc, string value, Point at, double size, Brush brush, double width, double height,
        double dpi, bool bold = false)
    {
        if (width < 3 || height < size) return;
        var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
        var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush, dpi)
        { MaxTextWidth = Math.Max(1, width), MaxTextHeight = height, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(text, at);
    }
}

public sealed class TimelineItem : FrameworkElement
{
    public static readonly DependencyProperty ViewportProperty = DependencyProperty.Register(nameof(Viewport), typeof(GuideViewport), typeof(TimelineItem), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public GuideViewport Viewport { get => (GuideViewport)GetValue(ViewportProperty); set => SetValue(ViewportProperty, value); }
    private Programme? _hover;
    private ChannelItem? _channel;
    private ImageSource? _logo;
    private string? _logoPath;
    public TimelineItem()
    {
        Height = GuideLayout.RowHeight;
        DataContextChanged += (_, _) => { _hover = null; BindChannel(); InvalidateVisual(); };
        Loaded += (_, _) => BindChannel();
        Unloaded += (_, _) => { if (_channel is not null) _channel.PropertyChanged -= ChannelChanged; _channel = null; _logo = null; _logoPath = null; };
        MouseMove += (_, e) =>
        {
            var programme = ProgrammeAt(e.GetPosition(this));
            if (programme == _hover) return;
            _hover = programme; Cursor = programme is null ? Cursors.Arrow : Cursors.Hand; InvalidateVisual();
        };
        MouseLeave += (_, _) => { _hover = null; InvalidateVisual(); };
    }
    private void BindChannel()
    {
        if (_channel is not null) _channel.PropertyChanged -= ChannelChanged;
        _channel = IsLoaded && DataContext is GuideRow row ? row.Channel : null;
        if (_channel is not null) { _channel.PropertyChanged += ChannelChanged; Viewport?.RequestLogo?.Invoke(_channel); }
        _logoPath = null; _logo = null; InvalidateVisual();
    }
    private void ChannelChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName is nameof(ChannelItem.LogoPath) or nameof(ChannelItem.Name)) InvalidateVisual(); }
    private ImageSource? Logo()
    {
        var path = _channel?.LogoPath;
        if (_logoPath == path) return _logo;
        _logoPath = path; _logo = null;
        if (path is null) return null;
        try
        {
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 80;
            bitmap.UriSource = new Uri(path, UriKind.Absolute); bitmap.EndInit(); bitmap.Freeze(); _logo = bitmap;
        }
        catch { }
        return _logo;
    }
    public Programme? ProgrammeAt(Point point) => DataContext is GuideRow row && Viewport is { } state ?
        GuideLayout.Hit(row, state.Range, state.Offset, ActualWidth, point) : null;

    protected override void OnRender(DrawingContext dc)
    {
        if (DataContext is not GuideRow row || Viewport is not { } state) return;
        double width = ActualWidth, dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(GuideDrawing.Brush("BackgroundBrush"), null, new Rect(0, 0, width, ActualHeight));
        dc.DrawLine(new Pen(GuideDrawing.Brush("BorderBrush"), .5), new(0, ActualHeight - .5), new(width, ActualHeight - .5));
        if (width > GuideLayout.ChannelWidth)
        {
            dc.PushClip(new RectangleGeometry(new Rect(GuideLayout.ChannelWidth, 0, width - GuideLayout.ChannelWidth, ActualHeight)));
            foreach (var programme in row.Programmes)
            {
                Rect rect = GuideLayout.Cell(programme, state.Range, state.Offset, width);
                if (rect.IsEmpty) continue;
                bool current = programme.Start <= state.Now && programme.End > state.Now;
                bool selected = state.Selection.Row?.Channel.Id == row.Channel.Id && state.Selection.Programme == programme;
                bool archived = programme.End <= state.Now && row.Channel.Archive?.Contains(programme.Start, state.Now) == true;
                var background = selected ? GuideDrawing.Color("#244846", "#CAE6E4") : current ? GuideDrawing.Color("#193831", "#E0F2EE") : GuideDrawing.Brush("CardBrush");
                Pen? border = selected ? new(GuideDrawing.Brush("AccentBrush"), 1.5) : programme == _hover ? new(GuideDrawing.Brush("MutedBrush"), 1) : null;
                dc.DrawRoundedRectangle(background, border, rect, 7, 7);
                if (rect.Width < 28) continue;
                dc.PushClip(new RectangleGeometry(rect));
                GuideDrawing.Text(dc, programme.Title, new(rect.X + 9, rect.Y + 8), 12.5, GuideDrawing.Brush("TextBrush"), rect.Width - 18, 32, dpi, selected);
                if (rect.Width >= 74)
                    GuideDrawing.Text(dc, $"{(archived ? "↶ " : "")}{programme.Start.ToLocalTime():HH:mm}–{programme.End.ToLocalTime():HH:mm}", new(rect.X + 9, rect.Bottom - 19), 10,
                        GuideDrawing.Brush("MutedBrush"), rect.Width - 18, 15, dpi);
                dc.Pop();
            }
            if (row.Programmes.Count == 0)
                GuideDrawing.Text(dc, "Нет данных телепрограммы", new(GuideLayout.ChannelWidth + 14, 27), 12, GuideDrawing.Brush("MutedBrush"), width - GuideLayout.ChannelWidth - 28, 20, dpi);
            dc.Pop();
        }
        // Channel names stay fixed while only the programme area moves.
        dc.DrawRectangle(GuideDrawing.Brush("PanelBrush"), null, new Rect(0, 0, Math.Min(width, GuideLayout.ChannelWidth - 8), ActualHeight));
        bool channelSelected = state.Selection.Row?.Channel.Id == row.Channel.Id;
        if (channelSelected) dc.DrawRoundedRectangle(GuideDrawing.Brush("AccentBrush"), null, new Rect(0, 21, 3, 34), 1.5, 1.5);
        var logoRect = new Rect(12, 20, 36, 36);
        dc.DrawRoundedRectangle(GuideDrawing.Brush("CardBrush"), null, logoRect, 7, 7);
        if (Logo() is { } logo)
        {
            double scale = Math.Min(30 / logo.Width, 30 / logo.Height);
            var target = new Rect(logoRect.X + (36 - logo.Width * scale) / 2, logoRect.Y + (36 - logo.Height * scale) / 2, logo.Width * scale, logo.Height * scale);
            dc.DrawImage(logo, target);
        }
        else GuideDrawing.Text(dc, row.Channel.Initials, new(logoRect.X + 5, logoRect.Y + 10), 10, GuideDrawing.Brush("MutedBrush"), 28, 17, dpi, true);
        GuideDrawing.Text(dc, row.Channel.Name, new(58, 15), 12.5, GuideDrawing.Brush(channelSelected ? "AccentBrush" : "TextBrush"), GuideLayout.ChannelWidth - 76, row.Channel.Archive is null ? 43 : 32, dpi, true);
        if (row.Channel.Archive is { } archive) GuideDrawing.Text(dc, $"↶ АРХИВ {archive.Days} ДН.", new(58, 52), 9, GuideDrawing.Brush("AccentBrush"), GuideLayout.ChannelWidth - 76, 15, dpi);
    }
}

internal sealed class GuideAxis(GuideViewport viewport) : FrameworkElement
{
    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(GuideDrawing.Brush("PanelBrush"), null, new Rect(0, 0, width, ActualHeight));
        GuideDrawing.Text(dc, "КАНАЛЫ", new(13, 13), 10, GuideDrawing.Brush("MutedBrush"), GuideLayout.ChannelWidth - 20, 18, dpi);
        if (width <= GuideLayout.ChannelWidth) return;
        dc.PushClip(new RectangleGeometry(new Rect(GuideLayout.ChannelWidth, 0, width - GuideLayout.ChannelWidth, ActualHeight)));
        double nowX = GuideLayout.ChannelWidth + viewport.Range.Position(viewport.Now) - viewport.Offset;
        bool showNow = viewport.Range.Contains(viewport.Now) && nowX >= GuideLayout.ChannelWidth && nowX <= width;
        for (double minute = 0; minute < (viewport.Range.End - viewport.Range.Start).TotalMinutes; minute += 30)
        {
            double x = GuideLayout.ChannelWidth + minute / 60 * GuideLayout.PixelsPerHour - viewport.Offset;
            if (x < GuideLayout.ChannelWidth - 50 || x > width) continue;
            dc.DrawLine(new Pen(GuideDrawing.Brush("BorderBrush"), 1), new(x, 34), new(x, 42));
            if (showNow && Math.Abs(x + 20 - nowX) < 50) continue;
            GuideDrawing.Text(dc, viewport.Range.Start.AddMinutes(minute).ToLocalTime().ToString("HH:mm"), new(x + 5, 11), 11,
                GuideDrawing.Brush(minute % 60 == 0 ? "TextBrush" : "MutedBrush"), 54, 18, dpi);
        }
        if (showNow)
        {
            double left = Math.Clamp(nowX - 25, GuideLayout.ChannelWidth, Math.Max(GuideLayout.ChannelWidth, width - 50));
            dc.DrawRoundedRectangle(GuideDrawing.Brush("AccentBrush"), null, new Rect(left, 6, 50, 25), 6, 6);
            GuideDrawing.Text(dc, viewport.Now.ToLocalTime().ToString("HH:mm"), new(left + 8, 10), 11,
                GuideDrawing.Color("#102320", "#FFFFFF"), 40, 18, dpi, true);
        }
        dc.Pop();
    }
}

internal sealed class GuideNowLine(GuideViewport viewport) : FrameworkElement
{
    protected override void OnRender(DrawingContext dc)
    {
        double x = GuideLayout.ChannelWidth + viewport.Range.Position(viewport.Now) - viewport.Offset;
        if (!viewport.Range.Contains(viewport.Now) || x < GuideLayout.ChannelWidth || x > ActualWidth) return;
        var pen = new Pen(GuideDrawing.Brush("AccentBrush"), 1.4) { DashStyle = new DashStyle([4, 4], 0) };
        dc.DrawLine(pen, new(x, 31), new(x, ActualHeight));
    }
}
