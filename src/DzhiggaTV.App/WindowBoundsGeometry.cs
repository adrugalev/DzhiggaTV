using System.Windows;

namespace DzhiggaTV.App;

public static class WindowBoundsGeometry
{
    public static Rect Maximized(Rect monitor, Rect workArea, bool fullscreen)
    {
        var area = fullscreen ? monitor : Rect.Intersect(monitor, workArea);
        if (area.IsEmpty || area.Width <= 0 || area.Height <= 0) area = monitor;
        return new(area.X - monitor.X, area.Y - monitor.Y, area.Width, area.Height);
    }
}
