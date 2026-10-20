using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DzhiggaTV.App;

public partial class SportsChannelPicker : Window
{
    private readonly List<SportsChannelChoice> _choices;
    private readonly SportsLayout _layout;
    private readonly Dictionary<string, ChannelItem> _channels;
    private readonly MainViewModel _model;
    private readonly ListCollectionView _view;
    private bool _changing;
    private bool _logosQueued, _closed;
    private Point _dragStart;
    private FrameworkElement? _dragSource;
    private ChannelDragCard? _dragCard;
    private ChannelDragCard? _arrivalCard;
    private Border? _arrivalTarget;
    private string? _dragId;
    private FrameworkElement? _lifted;
    private Border? _dropTarget;
    private double _liftedOpacity;
    private Cursor? _previousCursor;
    private bool Animate => SystemParameters.ClientAreaAnimation && !_model.Flag("reducedMotion");
    public IReadOnlyList<ChannelItem?> Layout => _layout.Slots.Select(id => id is not null ? _channels[id] : null).ToArray();
    public IReadOnlyList<ChannelItem> Selection => Layout.OfType<ChannelItem>().ToArray();

    public SportsChannelPicker(Window owner, MainViewModel model, IEnumerable<ChannelItem?> selected)
    {
        InitializeComponent(); Theme.Window(this); WindowAppearance.Attach(this); Owner = owner; _model = model;
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 50);
        _channels = model.AllChannels.Where(model.IsAllowed).ToDictionary(c => c.Id);
        _layout = new(SportsSelection.ResolveLayout(model, selected.Select(c => c?.Id)).Select(c => c?.Id));
        _choices = _channels.Values.Select(c => new SportsChannelChoice(c, _layout.Slots.Contains(c.Id))).ToList();
        foreach (var choice in _choices) choice.PropertyChanged += (_, _) => SelectionChanged(choice);
        _view = new ListCollectionView(_choices); Channels.ItemsSource = _view;
        ScreenCount.ItemsSource = Enumerable.Range(1, SportsSelection.MaximumChannels);
        Category.ItemsSource = new[] { "Все категории", "Избранное" }.Concat(_choices.Select(c => c.Channel.Group).Distinct().OrderBy(g => g)).ToArray();
        Category.SelectedIndex = 0; RefreshSelection();
        Loaded += (_, _) => { Search.Focus(); QueueVisibleLogos(); };
        // ListBox captures the mouse on press. Observe movement on the window so
        // a row can still start dragging after that capture redirects its events.
        PreviewMouseMove += Drag_Move;
        PreviewMouseLeftButtonDown += (_, _) => _dragSource = null;
        PreviewMouseLeftButtonUp += Drag_Up;
        LostMouseCapture += (_, _) => { if (_dragCard is not null && Mouse.Captured != this) CancelDrag(); };
        Deactivated += (_, _) => CancelDrag(); Closed += (_, _) => { _closed = true; CancelDrag(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && _dragCard is not null) { CancelDrag(); e.Handled = true; } };
    }

    private void SelectionChanged(SportsChannelChoice choice)
    {
        if (_changing) return;
        Limit.Visibility = Visibility.Collapsed;
        if (choice.IsSelected)
        {
            if (!_layout.Select(choice.Channel.Id)) ShowHint($"Можно выбрать до {SportsSelection.MaximumChannels} каналов. Снимите одну галочку или замените канал на схеме.");
        }
        else _layout.Remove(choice.Channel.Id);
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        _changing = true;
        foreach (var choice in _choices) choice.IsSelected = _layout.Slots.Contains(choice.Channel.Id);
        ScreenCount.SelectedItem = _layout.Slots.Count;
        _changing = false;
        Count.Text = $"Выбрано: {_layout.Count} из {SportsSelection.MaximumChannels}";
        Watch.IsEnabled = Reset.IsEnabled = _layout.Count > 0;
        RenderScheme();
        // Request selected picons only, rather than the entire library.
        _model.LoadLogos(Selection);
        QueueVisibleLogos();
    }

    private void RenderScheme()
    {
        Scheme.Children.Clear(); Scheme.ColumnDefinitions.Clear(); Scheme.RowDefinitions.Clear();
        int columns = _layout.Columns, rows = (int)Math.Ceiling(_layout.Slots.Count / (double)columns);
        for (int i = 0; i < columns; i++) Scheme.ColumnDefinitions.Add(new());
        for (int i = 0; i < rows; i++) Scheme.RowDefinitions.Add(new());
        for (int i = 0; i < _layout.Slots.Count; i++)
        {
            var id = _layout.Slots[i]; var channel = id is not null ? _channels[id] : null;
            var slot = new Border { Margin = new(4), Padding = new(8), CornerRadius = new(8), BorderThickness = new(1), Tag = i,
                Background = new SolidColorBrush(Colors.Transparent), RenderTransform = new ScaleTransform(1, 1), RenderTransformOrigin = new(.5, .5) };
            slot.SetResourceReference(BorderBrushProperty, channel is not null ? "AccentBrush" : "BorderBrush");
            System.Windows.Automation.AutomationProperties.SetName(slot, $"Экран {i + 1}: {channel?.Name ?? "пусто"}");
            var content = new Grid(); slot.Child = content;
            content.RowDefinitions.Add(new() { Height = GridLength.Auto }); content.RowDefinitions.Add(new());
            var number = new TextBlock { Text = $"{i + 1}", FontSize = 12, Margin = new(2, 0, 0, 0) }; number.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); content.Children.Add(number);
            var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new(2, 8, 2, 8) };
            Grid.SetRow(details, 1); content.Children.Add(details);
            if (channel is not null)
            {
                var logo = new ChannelLogo { Width = 80, Height = 64, Stretch = Stretch.Uniform, Margin = new(0, 0, 0, 10) };
                logo.SetBinding(ChannelLogo.PathProperty, new Binding(nameof(ChannelItem.LogoPath)) { Source = channel }); details.Children.Add(logo);
                var remove = new Button { Content = "×", Padding = new(0), Margin = new(0), Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, ToolTip = "Убрать канал с этого экрана" };
                System.Windows.Automation.AutomationProperties.SetName(remove, $"Очистить экран {i + 1}");
                remove.Click += (_, _) => { _layout.Remove(channel.Id); Limit.Visibility = Visibility.Collapsed; RefreshSelection(); }; content.Children.Add(remove);
                details.Tag = channel.Id; details.Cursor = Cursors.Hand; details.Background = Brushes.Transparent;
                details.PreviewMouseLeftButtonDown += Drag_Down; details.PreviewMouseMove += Drag_Move;
            }
            else
            {
                var plus = new TextBlock { Text = "+", FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center };
                plus.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); details.Children.Add(plus);
            }
            var name = new TextBlock { Text = channel?.Name ?? "Перетащите канал", FontSize = channel is not null ? 15 : 12, FontWeight = channel is not null ? FontWeights.SemiBold : FontWeights.Normal,
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxHeight = 60, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = channel?.Name };
            name.SetResourceReference(TextBlock.ForegroundProperty, channel is not null ? "TextBrush" : "MutedBrush"); details.Children.Add(name);
            Grid.SetRow(slot, i / columns); Grid.SetColumn(slot, i % columns); Scheme.Children.Add(slot);
        }
    }

    public bool PlaceChannel(string id, int position)
    {
        int origin = _layout.Slots.ToList().IndexOf(id);
        if (!_channels.ContainsKey(id) || !_layout.Place(id, position)) return false;
        Limit.Visibility = Visibility.Collapsed; RefreshSelection();
        AnimatePlacement(position);
        if (origin >= 0 && origin != position) AnimatePlacement(origin);
        return true;
    }

    private void Channel_Down(object sender, MouseButtonEventArgs e) => Drag_Down(sender, e);
    private void Channel_Move(object sender, MouseEventArgs e) => Drag_Move(sender, e);
    private void Drag_Down(object sender, MouseButtonEventArgs e) { _dragSource = (FrameworkElement)sender; _dragStart = e.GetPosition(this); }
    private void Drag_Move(object sender, MouseEventArgs e)
    {
        if (_dragCard is not null)
        {
            if (e.LeftButton != MouseButtonState.Pressed) { CancelDrag(); return; }
            FollowDrag(e.GetPosition(this)); e.Handled = true; return;
        }
        if (e.LeftButton != MouseButtonState.Pressed || _dragSource is null) return;
        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var source = _dragSource; _dragSource = null;
        var id = source.Tag as string ?? (source.DataContext as SportsChannelChoice)?.Channel.Id;
        if (id is null || !_channels.TryGetValue(id, out var channel)) return;
        e.Handled = true;
        Mouse.Capture(null);
        if (!Mouse.Capture(this, CaptureMode.SubTree)) return;
        CancelArrival();
        _dragId = id; _lifted = source; _liftedOpacity = source.Opacity; source.Opacity = .3;
        _dragCard = new ChannelDragCard(channel, Animate); DragSurface.Children.Add(_dragCard);
        _previousCursor = Mouse.OverrideCursor; Mouse.OverrideCursor = Cursors.Hand;
        _model.LoadLogos([channel]); FollowDrag(position);
    }

    private void FollowDrag(Point pointer)
    {
        _dragCard?.Follow(TranslatePoint(pointer, DragSurface), DragSurface.RenderSize);
        var target = Scheme.Children.OfType<Border>().FirstOrDefault(slot =>
            new Rect(slot.TranslatePoint(new(), this), slot.RenderSize).Contains(pointer));
        if (_dropTarget == target) return;
        if (_dropTarget is { } old) HighlightSlot(old, false);
        _dropTarget = target;
        if (target is not null) HighlightSlot(target, true);
    }

    private void Drag_Up(object sender, MouseButtonEventArgs e)
    {
        _dragSource = null;
        if (_dragCard is null) return;
        FollowDrag(e.GetPosition(this));
        var id = _dragId; var position = _dropTarget?.Tag as int?;
        var card = _dragCard;
        Rect? destination = null;
        if (_dropTarget is { } cell)
        {
            var center = cell.TranslatePoint(new(cell.ActualWidth / 2, cell.ActualHeight / 2), DragSurface);
            destination = new Rect(center.X - cell.ActualWidth / 2, center.Y - cell.ActualHeight / 2, cell.ActualWidth, cell.ActualHeight);
        }
        CancelDrag(); e.Handled = true;
        if (id is null || position is not { } target || !PlaceChannel(id, target) || !Animate || destination is not { } bounds) return;
        _arrivalCard = card; _arrivalTarget = Scheme.Children.OfType<Border>().Single(b => Equals(b.Tag, target));
        _arrivalTarget.BeginAnimation(OpacityProperty, null); _arrivalTarget.Opacity = 0;
        _arrivalTarget.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(80)) { BeginTime = TimeSpan.FromMilliseconds(180) });
        DragSurface.Children.Add(card);
        card.GrowInto(bounds, () => { if (ReferenceEquals(_arrivalCard, card)) CancelArrival(); });
    }

    private void CancelDrag()
    {
        _dragSource = null;
        CancelArrival();
        if (_dragCard is null) return;
        DragSurface.Children.Remove(_dragCard); _dragCard = null; _dragId = null;
        if (_lifted is not null) _lifted.Opacity = _liftedOpacity;
        _lifted = null;
        if (_dropTarget is not null) HighlightSlot(_dropTarget, false);
        _dropTarget = null;
        Mouse.OverrideCursor = _previousCursor;
        if (Mouse.Captured == this) Mouse.Capture(null);
    }

    private void CancelArrival()
    {
        if (_arrivalCard is not null) DragSurface.Children.Remove(_arrivalCard);
        _arrivalCard = null;
        if (_arrivalTarget is not null) { _arrivalTarget.BeginAnimation(OpacityProperty, null); _arrivalTarget.Opacity = 1; }
        _arrivalTarget = null;
    }

    private void Channels_Scrolled(object sender, ScrollChangedEventArgs e) => QueueVisibleLogos();
    private void Channels_SizeChanged(object sender, SizeChangedEventArgs e) => QueueVisibleLogos();
    private void QueueVisibleLogos()
    {
        if (_model is null || _logosQueued || _closed) return;
        _logosQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _logosQueued = false;
            if (_closed || !IsVisible || Channels.ActualWidth <= 0 || Channels.ActualHeight <= 0) return;
            var viewport = new Rect(Channels.RenderSize);
            var visible = MainWindow.FindVisualChildren<ListBoxItem>(Channels)
                .Where(row => row.IsVisible && row.ActualHeight > 0 &&
                    row.TransformToAncestor(Channels).TransformBounds(new Rect(row.RenderSize)).IntersectsWith(viewport))
                .Select(row => row.DataContext).OfType<SportsChannelChoice>().Select(choice => choice.Channel).ToArray();
            _model.LoadLogos(visible);
        }));
    }

    private void HighlightSlot(Border slot, bool active)
    {
        var color = active ? Color.FromArgb(38, 99, 217, 205) : Colors.Transparent;
        var brush = (SolidColorBrush)slot.Background;
        var scale = (ScaleTransform)slot.RenderTransform;
        if (Animate)
        {
            var duration = TimeSpan.FromMilliseconds(140);
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, duration));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(active ? 1.025 : 1, duration));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(active ? 1.025 : 1, duration));
        }
        else { brush.Color = color; scale.ScaleX = scale.ScaleY = 1; }
    }

    private void AnimatePlacement(int position)
    {
        if (!Animate || Scheme.Children.OfType<Border>().FirstOrDefault(slot => Equals(slot.Tag, position)) is not { } cell) return;
        var scale = (ScaleTransform)cell.RenderTransform;
        DoubleAnimation Settle() => new(.94, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = .35 } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Settle()); scale.BeginAnimation(ScaleTransform.ScaleYProperty, Settle());
        cell.BeginAnimation(OpacityProperty, new DoubleAnimation(.55, 1, TimeSpan.FromMilliseconds(180)));
    }

    private void ScreenCount_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_changing || _layout is null || ScreenCount.SelectedItem is not int size) return;
        Limit.Visibility = Visibility.Collapsed;
        if (!_layout.Resize(size)) ShowHint("Сначала освободите последние экраны или перетащите их каналы в начало схемы.");
        RefreshSelection();
    }
    private void Reset_Click(object sender, RoutedEventArgs e) { _layout.Reset(); Limit.Visibility = Visibility.Collapsed; RefreshSelection(); }
    private void ShowHint(string text) { Limit.Text = text; Limit.Visibility = Visibility.Visible; }
    private void Search_Changed(object sender, TextChangedEventArgs e) => Filter();
    private void Category_Changed(object sender, SelectionChangedEventArgs e) => Filter();
    private void Filter()
    {
        if (_view is null) return;
        var query = Search.Text.Trim(); var group = Category.SelectedItem as string;
        _view.Filter = item => item is SportsChannelChoice choice &&
            (group is null or "Все категории" || (group == "Избранное" ? choice.Channel.IsFavorite : choice.Channel.Group == group)) &&
            (query.Length == 0 || choice.Channel.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        Empty.Visibility = _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        ChannelListScroll.ResetToTop(Channels);
        QueueVisibleLogos();
    }
    private void Watch_Click(object sender, RoutedEventArgs e) { if (_layout.Count > 0) DialogResult = true; }
}

public sealed class SportsChannelChoice(ChannelItem channel, bool selected) : Observable
{
    public ChannelItem Channel { get; } = channel;
    private bool _selected = selected;
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
}
