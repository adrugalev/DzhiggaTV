using System.Windows;

namespace DzhiggaTV.App;

public static class WindowBoundsGeometry
{
    public static Rect ResizeMini(Rect proposed, int edge, double aspect, Thickness frame, Size minimum, Size maximum)
    {
        if (proposed.IsEmpty || edge is < 1 or > 8 || !double.IsFinite(aspect) || aspect <= 0) return proposed;
        double horizontal = frame.Left + frame.Right, vertical = frame.Top + frame.Bottom;
        double width = Math.Max(1, proposed.Width - horizontal), height = Math.Max(1, proposed.Height - vertical);
        // At a corner, project both pointer coordinates onto the aspect line.
        // At a single edge, the dimension under the pointer remains in control.
        height = edge switch { 1 or 2 => width / aspect, 3 or 6 => height, _ => (width * aspect + height) / (aspect * aspect + 1) };
        double minHeight = Math.Max(1, Math.Max(minimum.Height - vertical, (minimum.Width - horizontal) / aspect));
        double maxHeight = Math.Max(minHeight, Math.Min(maximum.Height - vertical, (maximum.Width - horizontal) / aspect));
        height = Math.Clamp(height, minHeight, maxHeight);
        width = height * aspect + horizontal; height += vertical;
        double left = edge switch { 1 or 4 or 7 => proposed.Right - width, 3 or 6 => proposed.Left + (proposed.Width - width) / 2, _ => proposed.Left };
        double top = edge switch { 3 or 4 or 5 => proposed.Bottom - height, 1 or 2 => proposed.Top + (proposed.Height - height) / 2, _ => proposed.Top };
        return new(left, top, width, height);
    }

    public static Rect Maximized(Rect monitor, Rect workArea, bool fullscreen)
    {
        var area = fullscreen ? monitor : Rect.Intersect(monitor, workArea);
        if (area.IsEmpty || area.Width <= 0 || area.Height <= 0) area = monitor;
        return new(area.X - monitor.X, area.Y - monitor.Y, area.Width, area.Height);
    }
}
