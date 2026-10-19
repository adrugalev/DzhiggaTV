using System.Windows;
using System.Windows.Controls;
using DzhiggaTV.Core;

namespace DzhiggaTV.App;

/// <summary>Shared reading layout for programme previews and the guide details.</summary>
internal sealed class ProgrammeDescriptionView : StackPanel
{
    private readonly TextBlock _metadata = new() { FontSize = 12, LineHeight = 19, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _synopsis = new() { FontSize = 13, LineHeight = 21, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _credits = new() { FontSize = 12, LineHeight = 19, TextWrapping = TextWrapping.Wrap };
    private readonly Border _metadataSection;
    private readonly StackPanel _synopsisSection;
    private readonly Border _creditsSection;

    public ProgrammeDescriptionView()
    {
        _metadataSection = new() { Child = _metadata, CornerRadius = new(7), Padding = new(11, 9, 11, 9) };
        _metadataSection.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        _metadata.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Children.Add(_metadataSection);

        _synopsisSection = new();
        _synopsisSection.Children.Add(Heading("ОПИСАНИЕ", "AccentBrush")); _synopsisSection.Children.Add(_synopsis);
        _synopsis.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); Children.Add(_synopsisSection);

        var creditsContent = new StackPanel();
        creditsContent.Children.Add(Heading("СОЗДАТЕЛИ И ДЕТАЛИ", "MutedBrush")); creditsContent.Children.Add(_credits);
        _creditsSection = new() { Child = creditsContent, BorderThickness = new(0, 1, 0, 0), Padding = new(0, 10, 0, 0) };
        _creditsSection.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        _credits.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); Children.Add(_creditsSection);
        Show(null);
    }

    public void Show(Programme? programme, string emptyText = "")
    {
        var text = ProgrammeText.From(programme);
        _metadata.Text = text.Metadata; _synopsis.Text = text.Synopsis; _credits.Text = text.Credits;
        if (string.IsNullOrWhiteSpace(programme?.Description)) _synopsis.Text = emptyText;
        _metadataSection.Visibility = text.Metadata.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _synopsisSection.Visibility = _synopsis.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _synopsisSection.Margin = new(0, text.Metadata.Length > 0 ? 14 : 0, 0, 0);
        _creditsSection.Visibility = text.Credits.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _creditsSection.Margin = new(0, text.Metadata.Length > 0 || _synopsis.Text.Length > 0 ? 14 : 0, 0, 0);
    }

    private static TextBlock Heading(string text, string brush)
    {
        var heading = new TextBlock { Text = text, FontSize = 9, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 6) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, brush); return heading;
    }
}
