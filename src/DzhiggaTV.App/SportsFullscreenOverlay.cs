using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DzhiggaTV.Core;

namespace DzhiggaTV.App;

/// <summary>A compact reading card above a fullscreen native video surface.</summary>
internal sealed class SportsFullscreenOverlay : Window
{
    private readonly SportsEveningWindow _owner;
    private readonly ChannelItem _channel;
    private readonly TextBlock _title = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxHeight = 52, Margin = new(0, 5, 0, 7) };
    private readonly TextBlock _time = new() { FontSize = 11, Margin = new(0, 0, 0, 10) };
    private readonly ProgrammeDescriptionView _description = new();
    private readonly ScrollViewer _scroll;
    private readonly ProgressBar _progress = new() { Height = 3, Maximum = 100, Margin = new(0, 12, 0, 0) };
    private Programme? _programme;
    private DateTime _lastMove;
    private bool _closed;
    private bool _keyboardNavigation;

    public SportsFullscreenOverlay(SportsEveningWindow owner, ChannelItem channel)
    {
        _owner = owner; _channel = channel; Owner = owner;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = System.Windows.Media.Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; ShowActivated = false; SizeToContent = SizeToContent.Height;
        Title = "DzhiggaTV · Описание передачи"; FontFamily = owner.FontFamily; SetResourceReference(ForegroundProperty, "TextBrush");
        var content = new Grid(); content.RowDefinitions.Add(new() { Height = GridLength.Auto }); content.RowDefinitions.Add(new()); content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel(); var channelName = new TextBlock { Text = channel.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        channelName.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); heading.Children.Add(channelName); heading.Children.Add(_title); heading.Children.Add(_time);
        _time.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); content.Children.Add(heading);
        _scroll = new() { Content = _description, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(_scroll, 1); content.Children.Add(_scroll); Grid.SetRow(_progress, 2); content.Children.Add(_progress);
        Content = new Border { Child = content, Padding = new(14), CornerRadius = new(10), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(230, 17, 25, 31)) };
        MouseMove += (_, _) => { _keyboardNavigation = false; _lastMove = DateTime.UtcNow; };
        PreviewKeyDown += (_, e) => { _keyboardNavigation = true; if (e.Key is Key.Escape or Key.F11) { e.Handled = true; _owner.ToggleChannelFullscreen(channel.Id); } };
        Loaded += (_, _) => Sync(); SizeChanged += (_, _) => Sync(); Closed += (_, _) => _closed = true;
    }
    public void Reveal()
    {
        if (_closed || _channel.Current is null) return;
        _keyboardNavigation = false; _lastMove = DateTime.UtcNow; Refresh(); Sync(); if (!IsVisible) Show();
    }
    public void Tick()
    {
        if (_closed) return;
        if (IsVisible && !IsMouseOver && !(_keyboardNavigation && IsKeyboardFocusWithin) && DateTime.UtcNow - _lastMove > TimeSpan.FromSeconds(3.5)) Hide();
        if (IsVisible) { Refresh(); Sync(); }
    }
    private void Refresh()
    {
        var programme = _channel.Current;
        _title.Text = programme?.Title ?? "";
        _time.Text = programme is null ? "" : $"{programme.Start.ToLocalTime():HH:mm} — {programme.End.ToLocalTime():HH:mm}";
        _progress.Value = programme is not null && programme.End > programme.Start
            ? Math.Clamp((DateTimeOffset.Now - programme.Start).TotalSeconds / (programme.End - programme.Start).TotalSeconds * 100, 0, 100) : 0;
        if (_programme != programme) { _programme = programme; _description.Show(programme); _scroll.ScrollToTop(); }
    }
    public void Sync()
    {
        if (_closed || _owner.Content is not FrameworkElement area || area.ActualWidth <= 0 || area.ActualHeight <= 0) return;
        var transform = PresentationSource.FromVisual(_owner)?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var origin = transform.Transform(area.PointToScreen(new()));
        Width = MaxWidth = area.ActualWidth * .25; MaxHeight = area.ActualHeight * .60;
        _scroll.MaxHeight = Math.Max(30, area.ActualHeight * .30);
        Left = origin.X + 16; Top = origin.Y + area.ActualHeight - Math.Min(ActualHeight > 0 ? ActualHeight : 300, MaxHeight) - 16;
    }
}
