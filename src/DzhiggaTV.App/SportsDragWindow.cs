using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DzhiggaTV.App;

/// <summary>An owned transparent surface crosses native video HWNDs without copying their frames.</summary>
internal sealed class SportsDragWindow : Window
{
    private readonly Canvas _surface = new() { Background = Brushes.Transparent };
    private readonly Border _highlight = new() { BorderThickness = new(2), CornerRadius = new(8), Background = new SolidColorBrush(Color.FromArgb(32, 99, 217, 205)), IsHitTestVisible = false };
    private readonly ChannelDragCard _card;
    private readonly IReadOnlyDictionary<string, Rect> _targets;
    private readonly string _source;
    private bool _finished;
    public event Action<string?>? Completed;

    public SportsDragWindow(Window owner, FrameworkElement area, ChannelItem source, IReadOnlyDictionary<string, Rect> targets, bool animate)
    {
        Owner = owner; _source = source.Id; _targets = targets;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
        Cursor = Cursors.SizeAll;
        FontFamily = owner.FontFamily; SetResourceReference(ForegroundProperty, "TextBrush");
        var transform = PresentationSource.FromVisual(owner)!.CompositionTarget!.TransformFromDevice;
        var origin = transform.Transform(area.PointToScreen(new()));
        Left = origin.X; Top = origin.Y; Width = area.ActualWidth; Height = area.ActualHeight;
        Content = _surface;
        _highlight.SetResourceReference(BorderBrushProperty, "AccentBrush"); _surface.Children.Add(_highlight);
        _card = new(source, animate); _surface.Children.Add(_card);
        PreviewMouseMove += (_, e) => { if (e.LeftButton != MouseButtonState.Pressed) Finish(null); else Follow(e.GetPosition(_surface)); };
        PreviewMouseLeftButtonUp += (_, e) => { e.Handled = true; Finish(Target(e.GetPosition(_surface))); };
        LostMouseCapture += (_, _) => { if (!_finished && Mouse.Captured != this) Finish(null); };
        Closed += (_, _) => { if (!_finished) Finish(null); };
    }
    public void Begin(Point screenPosition)
    {
        Show(); Follow(_surface.PointFromScreen(screenPosition));
        if (!Mouse.Capture(this, CaptureMode.Element)) Finish(null);
    }
    public void Cancel() => Finish(null);
    private string? Target(Point pointer) => _targets.FirstOrDefault(p => p.Key != _source && p.Value.Contains(pointer)).Key;
    private void Follow(Point pointer)
    {
        _card.Follow(pointer, _surface.RenderSize);
        var id = Target(pointer);
        _highlight.Visibility = id is null ? Visibility.Collapsed : Visibility.Visible;
        if (id is null) return;
        var bounds = _targets[id]; Canvas.SetLeft(_highlight, bounds.X); Canvas.SetTop(_highlight, bounds.Y);
        _highlight.Width = bounds.Width; _highlight.Height = bounds.Height;
    }
    private void Finish(string? target)
    {
        if (_finished) return; _finished = true;
        if (Mouse.Captured == this) Mouse.Capture(null);
        if (IsVisible) Close(); Completed?.Invoke(target);
    }
}
