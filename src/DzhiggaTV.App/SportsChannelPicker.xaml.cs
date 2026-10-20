using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace DzhiggaTV.App;

public partial class SportsChannelPicker : Window
{
    private readonly List<SportsChannelChoice> _choices;
    private readonly List<string> _order;
    private readonly ListCollectionView _view;
    private bool _changing;
    public IReadOnlyList<ChannelItem> Selection => _order.Select(id => _choices.First(c => c.Channel.Id == id).Channel).ToArray();

    public SportsChannelPicker(Window owner, MainViewModel model, IEnumerable<ChannelItem> selected)
    {
        InitializeComponent(); Theme.Window(this); WindowAppearance.Attach(this); Owner = owner;
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 50);
        _order = SportsSelection.Resolve(model, selected.Select(c => c.Id)).Select(c => c.Id).ToList();
        _choices = model.AllChannels.Where(model.IsAllowed).Select(c => new SportsChannelChoice(c, _order.Contains(c.Id))).ToList();
        foreach (var choice in _choices) choice.PropertyChanged += (_, _) => SelectionChanged(choice);
        _view = new ListCollectionView(_choices); Channels.ItemsSource = _view;
        Category.ItemsSource = new[] { "Все категории", "Избранное" }.Concat(_choices.Select(c => c.Channel.Group).Distinct().OrderBy(g => g)).ToArray();
        Category.SelectedIndex = 0; UpdateCount();
        Loaded += (_, _) => Search.Focus();
        // Reuse cached picons; do not trigger thousands of network requests in a chooser.
        model.LoadLogos(_choices.Where(c => c.IsSelected).Select(c => c.Channel));
    }

    private void SelectionChanged(SportsChannelChoice choice)
    {
        if (_changing) return;
        Limit.Visibility = Visibility.Collapsed;
        if (choice.IsSelected && !_order.Contains(choice.Channel.Id))
        {
            if (_order.Count == SportsSelection.MaximumChannels)
            {
                _changing = true; choice.IsSelected = false; _changing = false;
                Limit.Visibility = Visibility.Visible;
            }
            else _order.Add(choice.Channel.Id);
        }
        else if (!choice.IsSelected) _order.Remove(choice.Channel.Id);
        UpdateCount();
    }

    private void UpdateCount() { Count.Text = $"Выбрано: {_order.Count} из {SportsSelection.MaximumChannels}"; Watch.IsEnabled = _order.Count > 0; }
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
    private void Watch_Click(object sender, RoutedEventArgs e) { if (_order.Count > 0) DialogResult = true; }
}

public sealed class SportsChannelChoice(ChannelItem channel, bool selected) : Observable
{
    public ChannelItem Channel { get; } = channel;
    private bool _selected = selected;
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
}
