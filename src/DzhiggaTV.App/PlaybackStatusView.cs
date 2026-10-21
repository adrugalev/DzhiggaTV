using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DzhiggaTV.Player;

namespace DzhiggaTV.App;

/// <summary>A lightweight video status card; its animation runs only while actually visible.</summary>
public sealed class PlaybackStatusView : Border
{
    private readonly Grid _spinner = new() { Width = 42, Height = 42, Margin = new(0, 0, 0, 12) };
    private readonly RotateTransform _rotation = new(0, 21, 21);
    private readonly TextBlock _heading = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detail = new() { FontSize = 11, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 0) };
    private readonly Button _retry = new() { Content = "Повторить", FontSize = 12, Height = 30, Padding = new(14, 2, 14, 2), Margin = new(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
    private bool _animate, _running;
    public event EventHandler? RetryRequested;

    public PlaybackStatusView()
    {
        Visibility = Visibility.Collapsed; MaxWidth = 340; Padding = new(22, 18, 22, 18); CornerRadius = new(14);
        Background = new SolidColorBrush(Color.FromArgb(237, 17, 25, 31)); SetResourceReference(BorderBrushProperty, "BorderBrush"); BorderThickness = new(1);
        _heading.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); _detail.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _spinner.Children.Add(new Ellipse { Margin = new(3), Stroke = new SolidColorBrush(Color.FromRgb(53, 65, 74)), StrokeThickness = 3 });
        var arc = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 21,3 A 18,18 0 0 1 39,21"), StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, RenderTransform = _rotation };
        arc.SetResourceReference(Shape.StrokeProperty, "AccentBrush"); _spinner.Children.Add(arc);
        var panel = new StackPanel(); panel.Children.Add(_spinner); panel.Children.Add(_heading); panel.Children.Add(_detail); panel.Children.Add(_retry); Child = panel;
        _retry.Click += (_, _) => RetryRequested?.Invoke(this, EventArgs.Empty);
        IsVisibleChanged += (_, _) => UpdateAnimation(); Unloaded += (_, _) => StopAnimation();
        System.Windows.Automation.AutomationProperties.SetName(this, "Состояние трансляции");
    }
    public void ShowState(PlaybackState state, string message, bool canRetry, bool reducedMotion, bool requiresRestart)
    {
        bool busy = state is PlaybackState.Loading or PlaybackState.Buffering or PlaybackState.Stalled or PlaybackState.Reconnecting;
        bool error = state == PlaybackState.Error;
        Visibility = busy || error ? Visibility.Visible : Visibility.Collapsed;
        _spinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _heading.Text = state switch
        {
            PlaybackState.Loading => "Подключаемся к трансляции",
            PlaybackState.Buffering => "Ожидаем данные",
            PlaybackState.Stalled => "Трансляция приостановилась",
            PlaybackState.Reconnecting => "Восстанавливаем трансляцию",
            PlaybackState.Error => requiresRestart ? "Видеодвижок не отвечает" : "Не удалось восстановить трансляцию",
            _ => ""
        };
        _detail.Text = state switch
        {
            PlaybackState.Stalled => "Проверяем поток и восстанавливаем подключение…",
            PlaybackState.Buffering => "Воспроизведение продолжится автоматически",
            _ => message
        };
        _retry.Visibility = error && canRetry && !requiresRestart ? Visibility.Visible : Visibility.Collapsed;
        IsHitTestVisible = error;
        _animate = busy && !reducedMotion && SystemParameters.ClientAreaAnimation; UpdateAnimation();
    }
    private void UpdateAnimation()
    {
        bool run = IsVisible && _animate;
        if (run == _running) return;
        if (!run) { StopAnimation(); return; }
        _running = true;
        _rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.15)) { RepeatBehavior = RepeatBehavior.Forever });
    }
    private void StopAnimation() { _running = false; _rotation.BeginAnimation(RotateTransform.AngleProperty, null); }
}
