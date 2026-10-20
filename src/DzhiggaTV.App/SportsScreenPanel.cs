using System.Windows;
using System.Windows.Controls;

namespace DzhiggaTV.App;

internal sealed class SportsScreenPanel(SportsProgrammePanel programme) : Panel
{
    private int _count = 1;
    public void SetCount(int count) { _count = count; InvalidateMeasure(); }

    private MultiscreenPlacement Placement(Size available)
    {
        var aspects = Enumerable.Repeat(MultiscreenGeometry.DefaultAspect, _count).ToArray();
        int columns = SportsLayout.ColumnsFor(_count);
        foreach (var tile in Children.OfType<SportsTile>())
        {
            int index = Grid.GetRow(tile) * columns + Grid.GetColumn(tile);
            if (index < aspects.Length) aspects[index] = tile.VideoAspect;
        }
        return MultiscreenGeometry.Fit(available, aspects);
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : 1200,
            double.IsFinite(availableSize.Height) ? availableSize.Height : 700);
        var placement = Placement(size);
        programme.Visibility = placement.Programme.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        foreach (UIElement child in InternalChildren)
        {
            int index = Grid.GetRow(child) * SportsLayout.ColumnsFor(_count) + Grid.GetColumn(child);
            var rect = ReferenceEquals(child, programme) ? placement.Programme : index < placement.Screens.Count ? placement.Screens[index] : Rect.Empty;
            child.Measure(rect.IsEmpty ? new() : rect.Size);
        }
        return size;
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        var placement = Placement(finalSize);
        foreach (UIElement child in InternalChildren)
        {
            int index = Grid.GetRow(child) * SportsLayout.ColumnsFor(_count) + Grid.GetColumn(child);
            var rect = ReferenceEquals(child, programme) ? placement.Programme : index < placement.Screens.Count ? placement.Screens[index] : Rect.Empty;
            child.Arrange(rect.IsEmpty ? new Rect() : rect);
        }
        return finalSize;
    }
}
