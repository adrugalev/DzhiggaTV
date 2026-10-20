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
        if (count == 4) return FitFour(available, aspects);
        var ratios = aspects.Take(count).Select(r => double.IsFinite(r) && r > 0 ? r : DefaultAspect).ToArray();
        int[][] groups = count == 3 ? [[0], [1, 2]] : [Enumerable.Range(0, count).ToArray()];
        var horizontal = FitGroups(available, ratios, groups, columns: false);
        var vertical = FitGroups(available, ratios, groups, columns: true);
        return AddProgramme(available, VideoArea(horizontal) >= VideoArea(vertical) ? horizontal : vertical);
    }

    private static double VideoArea(IReadOnlyList<Rect> screens) => screens.Sum(r =>
        Math.Max(0, r.Width - ChromeWidth) * Math.Max(0, r.Height - ChromeHeight));

    private static Rect[] FitGroups(Size available, double[] ratios, int[][] groups, bool columns)
    {
        double width = Math.Max(0, available.Width), height = Math.Max(0, available.Height);
        double chromeW = Math.Min(ChromeWidth, width / ratios.Length), chromeH = Math.Min(ChromeHeight, height / ratios.Length);
        var sums = groups.Select(g => g.Sum(i => columns ? 1 / ratios[i] : ratios[i])).ToArray();
        double span = columns
            ? Math.Min(height, (width - groups.Length * chromeW + groups.Select((g, n) => g.Length * chromeH / sums[n]).Sum()) / sums.Sum(s => 1 / s))
            : Math.Min(width, (height - groups.Length * chromeH + groups.Select((g, n) => g.Length * chromeW / sums[n]).Sum()) / sums.Sum(s => 1 / s));
        var screens = new Rect[ratios.Length]; double offset = 0;
        for (int n = 0; n < groups.Length; n++)
        {
            var group = groups[n];
            double video = Math.Max(0, (span - group.Length * (columns ? chromeH : chromeW)) / sums[n]);
            double along = 0;
            foreach (int i in group)
            {
                double tileW = (columns ? video : video * ratios[i]) + chromeW;
                double tileH = (columns ? video / ratios[i] : video) + chromeH;
                screens[i] = new(columns ? offset : along, columns ? along : offset, tileW, tileH);
                along += columns ? tileH : tileW;
            }
            offset += video + (columns ? chromeW : chromeH);
        }
        return screens;
    }

    private static MultiscreenPlacement AddProgramme(Size available, Rect[] screens)
    {
        double width = available.Width, height = available.Height;
        double gridWidth = screens.Max(r => r.Right), gridHeight = screens.Max(r => r.Bottom);
        double x = Math.Max(0, (width - gridWidth) / 2), y = Math.Max(0, (height - gridHeight) / 2);
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
        foreach (ref var screen in screens.AsSpan()) screen.Offset(x, y);
        return new(screens, programme);
    }

    private static MultiscreenPlacement FitFour(Size available, IReadOnlyList<double> aspects)
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
