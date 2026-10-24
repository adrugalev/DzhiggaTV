using L = DzhiggaTV.Localization.Texts;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DzhiggaTV.Core;

namespace DzhiggaTV.App;

/// <summary>A compact now/next card for the strip below the videos.</summary>
internal sealed class SportsProgrammeCard : Border
{
    public Button Details { get; } = new()
    {
        Content = L.T("Подробнее"), FontSize = 11, Height = 20, Padding = new(6, 0, 6, 0), Margin = new(0),
        Background = Brushes.Transparent, BorderThickness = new(0), HorizontalAlignment = HorizontalAlignment.Right
    };
    private readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
        TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 40, LineHeight = 20, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, Margin = new(0, 3, 0, 3) };
    private readonly TextBlock _time = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(10, 0, 0, 0) };
    private readonly TextBlock _synopsis = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.WordEllipsis,
        MaxHeight = 36, LineHeight = 18, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, Margin = new(0, 4, 0, 4) };
    private readonly ProgressBar _progress = new() { Height = 3, Maximum = 100, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 12, 0) };
    private readonly bool _current;
    private readonly string _empty;
    private Programme? _shown;

    public SportsProgrammeCard(string label, string name, string empty, bool current = false)
    {
        Name = name; _current = current; _empty = empty;
        CornerRadius = new(8); Padding = new(12, 8, 12, 8);
        SetResourceReference(BackgroundProperty, "CardBrush");
        var root = new Grid(); Child = root;
        foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) root.RowDefinitions.Add(new() { Height = height });
        var header = new Grid(); header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var caption = new TextBlock { Text = label, FontSize = 9, VerticalAlignment = VerticalAlignment.Center };
        caption.SetResourceReference(TextBlock.ForegroundProperty, current ? "AccentBrush" : "MutedBrush");
        header.Children.Add(caption); Grid.SetColumn(_time, 1); header.Children.Add(_time); root.Children.Add(header);
        Grid.SetRow(_title, 1); root.Children.Add(_title);
        Grid.SetRow(_synopsis, 2); root.Children.Add(_synopsis);
        var footer = new Grid { Margin = new(0, 3, 0, 0) }; footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.Children.Add(_progress); Grid.SetColumn(Details, 1); footer.Children.Add(Details); Grid.SetRow(footer, 3); root.Children.Add(footer);
        foreach (var text in new[] { _time, _synopsis }) text.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Details.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
        System.Windows.Automation.AutomationProperties.SetName(Details, L.T("Подробнее · ") + label);
        SizeChanged += (_, _) => UpdateSynopsis();
        Show(null);
    }
    public void Show(Programme? programme, double progress = 0)
    {
        if (_shown != programme || DataContext is null)
        {
            _shown = programme; DataContext = programme;
            _title.Text = programme?.Title ?? _empty;
            _time.Text = programme is null ? "" : $"{programme.Start.ToLocalTime():HH:mm} — {programme.End.ToLocalTime():HH:mm}";
            _synopsis.Text = ProgrammeText.From(programme).Synopsis;
            UpdateSynopsis();
        }
        _progress.Value = progress;
        _progress.Visibility = _current && programme is not null ? Visibility.Visible : Visibility.Collapsed;
    }
    private void UpdateSynopsis() => _synopsis.Visibility = _current && ActualHeight >= 132 && _synopsis.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
}
