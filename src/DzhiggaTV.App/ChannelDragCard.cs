using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace DzhiggaTV.App;

/// <summary>A lightweight, non-interactive channel card carried by the pointer.</summary>
internal sealed class ChannelDragCard : Border
{
    private readonly TranslateTransform _position = new();

    public ChannelDragCard(ChannelItem channel, bool animate)
    {
        Width = 248; Height = 88; Padding = new(12); CornerRadius = new(12);
        BorderThickness = new(1.5); IsHitTestVisible = false; Opacity = .98;
        SetResourceReference(BackgroundProperty, "CardBrush");
        SetResourceReference(BorderBrushProperty, "AccentBrush");
        Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 22, ShadowDepth = 7, Opacity = .4 };
        var scale = new ScaleTransform(1, 1);
        RenderTransform = new TransformGroup { Children = { scale, _position } };
        RenderTransformOrigin = new(.16, .5);
        var row = new Grid(); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new()); Child = row;
        var logo = new Image { Width = 62, Height = 56, Stretch = Stretch.Uniform, Margin = new(0, 0, 12, 0) };
        logo.SetBinding(Image.SourceProperty, new Binding(nameof(ChannelItem.LogoPath)) { Source = channel });
        var logoStyle = new Style(typeof(Image));
        var missing = new DataTrigger { Binding = new Binding(nameof(ChannelItem.LogoPath)) { Source = channel }, Value = null };
        missing.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed)); logoStyle.Triggers.Add(missing); logo.Style = logoStyle;
        row.Children.Add(logo);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(text, 1); row.Children.Add(text);
        var number = new TextBlock { Text = $"КАНАЛ {channel.Number}", FontSize = 9, Margin = new(0, 0, 0, 4) };
        number.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); text.Children.Add(number);
        text.Children.Add(new TextBlock { Text = channel.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (animate)
        {
            var duration = TimeSpan.FromMilliseconds(140);
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.9, 1, duration) { EasingFunction = easing });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.9, 1, duration) { EasingFunction = easing });
            BeginAnimation(OpacityProperty, new DoubleAnimation(.45, .98, duration));
        }
    }

    public void Follow(Point pointer, Size bounds)
    {
        // Keep the grabbed logo near the pointer and the card inside the chooser.
        _position.X = Math.Clamp(pointer.X - 34, 4, Math.Max(4, bounds.Width - Width - 4));
        _position.Y = Math.Clamp(pointer.Y - 34, 4, Math.Max(4, bounds.Height - Height - 4));
    }
}
