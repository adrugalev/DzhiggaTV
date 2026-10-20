using System.Windows;
using System.Windows.Controls;

namespace DzhiggaTV.App;

internal sealed class SportsScreenPanel(SportsProgrammePanel programme) : Panel
{
    private SportsTile[] _screens = [];
    private SportsTile? _focused;
    public void SetScreens(IEnumerable<SportsTile> screens) { _screens = screens.ToArray(); InvalidateMeasure(); }
    public void Focus(SportsTile? tile) { _focused = tile; InvalidateMeasure(); }

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
        if (_focused is not null)
        {
            foreach (UIElement child in InternalChildren)
            {
                child.Visibility = ReferenceEquals(child, _focused) ? Visibility.Visible : Visibility.Collapsed;
                child.Measure(ReferenceEquals(child, _focused) ? size : new());
            }
            return size;
        }
        foreach (var tile in _screens) tile.Visibility = Visibility.Visible;
        var placement = Placement(size);
        programme.SetBottomLayout(!placement.Programme.IsEmpty && placement.Programme.Y > 0);
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
        if (_focused is not null)
        {
            foreach (UIElement child in InternalChildren) child.Arrange(ReferenceEquals(child, _focused) ? new Rect(finalSize) : new Rect());
            return finalSize;
        }
        var placement = Placement(finalSize);
        foreach (UIElement child in InternalChildren)
        {
            var rect = Bounds(child, placement);
            child.Arrange(rect.IsEmpty ? new Rect() : rect);
        }
        return finalSize;
    }
}
