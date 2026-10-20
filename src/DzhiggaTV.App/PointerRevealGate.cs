using System.Windows;

namespace DzhiggaTV.App;

/// <summary>Ignores pointer jitter without losing deliberate movement across several events.</summary>
internal sealed class PointerRevealGate
{
    public const double FullscreenDistance = 12;
    private Point? _anchor;

    public void Reset(Point point) => _anchor = point;

    public bool Moved(Point point, double distance)
    {
        if (_anchor is not { } anchor) { Reset(point); return false; }
        if ((point - anchor).Length < distance) return false;
        Reset(point);
        return true;
    }
}
