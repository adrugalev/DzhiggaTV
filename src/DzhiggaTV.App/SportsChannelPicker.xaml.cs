using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace DzhiggaTV.App;

public partial class SportsChannelPicker : Window
{
    private readonly List<SportsChannelChoice> _choices;
    private const string ChannelFormat = "DzhiggaTV.Multiscreen.Channel";
    private readonly SportsLayout _layout;
    private readonly Dictionary<string, ChannelItem> _channels;
    private readonly MainViewModel _model;
    private readonly ListCollectionView _view;
    private bool _changing;
    private Point _dragStart;
    private FrameworkElement? _dragSource;
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
        Loaded += (_, _) => Search.Focus();
        // ListBox captures the mouse on press. Observe movement on the window so
        // a row can still start dragging after that capture redirects its events.
        PreviewMouseMove += Drag_Move;
        PreviewMouseLeftButtonDown += (_, _) => _dragSource = null;
        PreviewMouseLeftButtonUp += (_, _) => _dragSource = null;
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
    }

    private void RenderScheme()
    {
        Scheme.Children.Clear(); Scheme.ColumnDefinitions.Clear(); Scheme.RowDefinitions.Clear();
        int columns = _layout.Columns, rows = (int)Math.Ceiling(_layout.Slots.Count / (double)columns);
        for (int i = 0; i < columns; i++) Scheme.ColumnDefinitions.Add(new());
        for (int i = 0; i < rows; i++) Scheme.RowDefinitions.Add(new());
        for (int i = 0; i < _layout.Slots.Count; i++)
        {
            int index = i;
            var id = _layout.Slots[i]; var channel = id is not null ? _channels[id] : null;
            var slot = new Border { Margin = new(4), Padding = new(8), CornerRadius = new(8), BorderThickness = new(1), AllowDrop = true, Tag = index, Background = Brushes.Transparent };
            slot.SetResourceReference(BorderBrushProperty, channel is not null ? "AccentBrush" : "BorderBrush");
            System.Windows.Automation.AutomationProperties.SetName(slot, $"Экран {i + 1}: {channel?.Name ?? "пусто"}");
            var content = new Grid(); slot.Child = content;
            content.RowDefinitions.Add(new() { Height = GridLength.Auto }); content.RowDefinitions.Add(new());
            var number = new TextBlock { Text = $"{i + 1}", FontSize = 11, Margin = new(2, 0, 0, 0) }; number.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); content.Children.Add(number);
            var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new(2, 8, 2, 8) };
            Grid.SetRow(details, 1); content.Children.Add(details);
            if (channel is not null)
            {
                var logo = new Image { Width = 38, Height = 38, Stretch = Stretch.Uniform, Margin = new(0, 0, 0, 8) };
                logo.SetBinding(Image.SourceProperty, new Binding(nameof(ChannelItem.LogoPath)) { Source = channel }); details.Children.Add(logo);
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
            var name = new TextBlock { Text = channel?.Name ?? "Перетащите канал", FontSize = channel is not null ? 12 : 11, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxHeight = 52, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = channel?.Name };
            name.SetResourceReference(TextBlock.ForegroundProperty, channel is not null ? "TextBrush" : "MutedBrush"); details.Children.Add(name);
            slot.DragOver += (_, e) =>
            {
                e.Effects = DraggedChannel(e.Data) is not null ? DragDropEffects.Move : DragDropEffects.None;
                slot.SetResourceReference(Border.BackgroundProperty, e.Effects == DragDropEffects.Move ? "CardBrush" : "PanelBrush"); e.Handled = true;
            };
            slot.DragLeave += (_, _) => slot.Background = Brushes.Transparent;
            slot.Drop += (_, e) =>
            {
                if (DraggedChannel(e.Data) is { } dropped && PlaceChannel(dropped, index)) e.Effects = DragDropEffects.Move;
                e.Handled = true;
            };
            Grid.SetRow(slot, i / columns); Grid.SetColumn(slot, i % columns); Scheme.Children.Add(slot);
        }
    }

    public bool PlaceChannel(string id, int position)
    {
        if (!_channels.ContainsKey(id) || !_layout.Place(id, position)) return false;
        Limit.Visibility = Visibility.Collapsed; RefreshSelection(); return true;
    }

    private string? DraggedChannel(IDataObject data) => data.GetDataPresent(ChannelFormat) && data.GetData(ChannelFormat) is string id && _channels.ContainsKey(id) ? id : null;
    private void Channel_Down(object sender, MouseButtonEventArgs e) => Drag_Down(sender, e);
    private void Channel_Move(object sender, MouseEventArgs e) => Drag_Move(sender, e);
    private void Drag_Down(object sender, MouseButtonEventArgs e) { _dragSource = (FrameworkElement)sender; _dragStart = e.GetPosition(this); }
    private void Drag_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragSource is null) return;
        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var source = _dragSource; _dragSource = null;
        var id = source.Tag as string ?? (source.DataContext as SportsChannelChoice)?.Channel.Id;
        if (id is null) return;
        e.Handled = true;
        Mouse.Capture(null);
        DragDrop.DoDragDrop(source, new DataObject(ChannelFormat, id), DragDropEffects.Move);
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
    }
    private void Watch_Click(object sender, RoutedEventArgs e) { if (_layout.Count > 0) DialogResult = true; }
}

public sealed class SportsChannelChoice(ChannelItem channel, bool selected) : Observable
{
    public ChannelItem Channel { get; } = channel;
    private bool _selected = selected;
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
}
