using System.Windows;
using System.Windows.Controls;

namespace DzhiggaTV.App;

internal sealed class SportsScreenPanel(SportsProgrammePanel programme) : Panel
{
    private SportsTile[] _screens = [];
    public void SetScreens(IEnumerable<SportsTile> screens) { _screens = screens.ToArray(); InvalidateMeasure(); }

    private MultiscreenPlacement Placement(Size available)
    {
        return MultiscreenGeometry.Fit(available, _screens.Select(t => t.VideoAspect).ToArray());
    }
    private Rect Bounds(UIElement child, MultiscreenPlacement placement)
    {
        if (ReferenceEquals(child, programme)) return placement.Programme;
        int index = child is SportsTile tile ? Array.IndexOf(_screens, tile) : -1;
        return index >= 0 ? placement.Screens[index] : Rect.Empty;
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : 1200,
            double.IsFinite(availableSize.Height) ? availableSize.Height : 700);
        var placement = Placement(size);
        programme.Visibility = placement.Programme.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        foreach (UIElement child in InternalChildren)
        {
            var rect = Bounds(child, placement);
            child.Measure(rect.IsEmpty ? new() : rect.Size);
        }
        return size;
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        var placement = Placement(finalSize);
        foreach (UIElement child in InternalChildren)
        {
            var rect = Bounds(child, placement);
            child.Arrange(rect.IsEmpty ? new Rect() : rect);
        }
        return finalSize;
    }
}
