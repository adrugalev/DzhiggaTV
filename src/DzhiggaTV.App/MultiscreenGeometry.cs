using System.Windows;

namespace DzhiggaTV.App;

public sealed record MultiscreenPlacement(IReadOnlyList<Rect> Screens, Rect Programme);

public static class MultiscreenGeometry
{
    // Includes the tile's border, margin, header and two compact footer rows.
    public const double ChromeWidth = 12, ChromeHeight = 92;
    public const double DefaultAspect = 16d / 9;

    public static MultiscreenPlacement Fit(Size available, IReadOnlyList<double> aspects)
    {
        int count = Math.Min(SportsSelection.MaximumChannels, aspects.Count);
        if (count == 0) return new([], Rect.Empty);
        double width = Math.Max(0, available.Width), height = Math.Max(0, available.Height);
        int columns = SportsLayout.ColumnsFor(count), rows = (count + columns - 1) / columns;
        var ratios = Enumerable.Range(0, rows * columns).Select(i => i < count && double.IsFinite(aspects[i]) && aspects[i] > 0
            ? aspects[i] : DefaultAspect).ToArray();
        double chromeW = Math.Min(ChromeWidth, width / columns), chromeH = Math.Min(ChromeHeight, height / rows);
        double videoHeight = Math.Max(0, (height - rows * chromeH) / rows);
        for (int row = 0; row < rows; row++)
            videoHeight = Math.Min(videoHeight, Math.Max(0, (width - columns * chromeW) / ratios.Skip(row * columns).Take(columns).Sum()));
        var rowWidths = Enumerable.Range(0, rows).Select(row => ratios.Skip(row * columns).Take(columns).Sum() * videoHeight + columns * chromeW).ToArray();
        double gridWidth = rowWidths.Max(), tileHeight = videoHeight + chromeH, gridHeight = rows * tileHeight;
        double x = (width - gridWidth) / 2, y = (height - gridHeight) / 2;
        var programme = Rect.Empty;
        if (width - gridWidth >= 252)
        {
            x = y = 0;
            programme = new(gridWidth + 12, 0, width - gridWidth - 12, height);
        }
        else if (height - gridHeight >= 162)
        {
            x = y = 0;
            programme = new(0, gridHeight + 12, width, height - gridHeight - 12);
        }
        var screens = new Rect[count];
        for (int row = 0; row < rows; row++)
        {
            double left = x + (gridWidth - rowWidths[row]) / 2;
            for (int col = 0; col < columns; col++)
            {
                int index = row * columns + col;
                double tileWidth = ratios[index] * videoHeight + chromeW;
                if (index < count) screens[index] = new(left, y + row * tileHeight, tileWidth, tileHeight);
                left += tileWidth;
            }
        }
        return new(screens, programme);
    }
}
