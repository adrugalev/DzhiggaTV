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
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly Image _logo;
    private readonly TextBlock _title;

    public ChannelDragCard(ChannelItem channel, bool animate)
    {
        Width = Height = 124; Padding = new(10); CornerRadius = new(12);
        BorderThickness = new(1.5); IsHitTestVisible = false; Opacity = .98;
        SetResourceReference(BackgroundProperty, "CardBrush");
        SetResourceReference(BorderBrushProperty, "AccentBrush");
        Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 22, ShadowDepth = 7, Opacity = .4 };
        RenderTransform = new TransformGroup { Children = { _scale, _position } };
        RenderTransformOrigin = new(.5, .5);
        var row = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; Child = row;
        var logo = _logo = new ChannelLogo { Width = 62, Height = 56, Stretch = Stretch.Uniform, Margin = new(0, 0, 0, 8) };
        logo.SetBinding(ChannelLogo.PathProperty, new Binding(nameof(ChannelItem.LogoPath)) { Source = channel });
        var logoStyle = new Style(typeof(Image));
        var missing = new DataTrigger { Binding = new Binding(nameof(ChannelItem.LogoPath)) { Source = channel }, Value = null };
        missing.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed)); logoStyle.Triggers.Add(missing); logo.Style = logoStyle;
        row.Children.Add(logo);
        _title = new TextBlock { Text = channel.Name, FontSize = 12, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, MaxHeight = 34, TextTrimming = TextTrimming.CharacterEllipsis };
        row.Children.Add(_title);
        if (animate)
        {
            var duration = TimeSpan.FromMilliseconds(140);
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.9, 1, duration) { EasingFunction = easing });
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.9, 1, duration) { EasingFunction = easing });
            BeginAnimation(OpacityProperty, new DoubleAnimation(.45, .98, duration));
        }
    }

    public void Follow(Point pointer, Size bounds)
    {
        _position.X = Math.Clamp(pointer.X - Width / 2, 4, Math.Max(4, bounds.Width - Width - 4));
        _position.Y = Math.Clamp(pointer.Y - Height / 2, 4, Math.Max(4, bounds.Height - Height - 4));
    }

    public void GrowInto(Rect target, Action completed)
    {
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        var duration = TimeSpan.FromMilliseconds(260);
        DoubleAnimation To(double value) => new(value, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        BeginAnimation(WidthProperty, To(target.Width)); BeginAnimation(HeightProperty, To(target.Height));
        _position.BeginAnimation(TranslateTransform.XProperty, To(target.X)); _position.BeginAnimation(TranslateTransform.YProperty, To(target.Y));
        _logo.BeginAnimation(WidthProperty, To(80)); _logo.BeginAnimation(HeightProperty, To(64));
        _title.BeginAnimation(TextBlock.FontSizeProperty, To(15)); _title.BeginAnimation(MaxHeightProperty, To(60));
        var fade = new DoubleAnimation(.98, 0, TimeSpan.FromMilliseconds(80)) { BeginTime = TimeSpan.FromMilliseconds(180) };
        fade.Completed += (_, _) => completed(); BeginAnimation(OpacityProperty, fade);
    }
}
