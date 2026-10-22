using System.Windows;
using System.Windows.Media;

namespace DzhiggaTV.App;

/// <summary>Vector artwork shares the outline and hover mask used by the navigation icons.</summary>
public static class RemoteNavigationArtwork
{
    public static DrawingImage Create()
    {
        var geometry = Geometry.Parse("M38,14 H72 Q84,14 84,26 V102 Q84,114 72,114 H38 Q26,114 26,102 V26 Q26,14 38,14 Z M46,26 H64 M48,99 H62 M49,48 L49,76 L68,62 Z M89,39 Q101,25 115,39 M93,48 Q102,39 110,48 M100,57 L103,57");
        var group = new DrawingGroup();
        var color = new LinearGradientBrush(Color.FromRgb(122, 196, 255), Color.FromRgb(74, 104, 241), new Point(0, 0), new Point(1, 1));
        var highlight = new LinearGradientBrush(Color.FromRgb(213, 244, 255), Color.FromRgb(138, 176, 255), new Point(0, 0), new Point(1, 1));
        foreach (var (brush, width) in new (Brush, double)[] { (new SolidColorBrush(Color.FromRgb(29, 75, 140)), 8), (color, 5.8), (highlight, 1.4) })
            group.Children.Add(new GeometryDrawing(null, new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, geometry));
        // DrawingImage otherwise crops its canvas to the ink bounds. Leave the
        // same transparent inset as the other icons so 1.13x / -4° hover fits.
        var bounds = group.Bounds;
        double scale = 96 / Math.Max(bounds.Width, bounds.Height);
        group.Transform = new MatrixTransform(scale, 0, 0, scale,
            64 - (bounds.X + bounds.Width / 2) * scale, 64 - (bounds.Y + bounds.Height / 2) * scale);
        var canvas = new DrawingGroup();
        canvas.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 128, 128))));
        canvas.Children.Add(group);
        var image = new DrawingImage(canvas); image.Freeze(); return image;
    }
}
