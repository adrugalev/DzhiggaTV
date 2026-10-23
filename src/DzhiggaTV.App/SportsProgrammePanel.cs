using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DzhiggaTV.Core;

namespace DzhiggaTV.App;

internal sealed class SportsProgrammePanel : Border, IDisposable
{
    private readonly MainViewModel _model;
    private readonly ChannelLogo _logo = new() { Width = 30, Height = 30, Margin = new(0, 0, 10, 0) };
    private readonly TextBlock _channel = Text(14, true), _title = Text(18, true), _time = Text(11);
    private readonly ProgressBar _progress = new() { Height = 3, Maximum = 100, Margin = new(0, 10, 0, 0) };
    private readonly ProgrammeDescriptionView _description = new();
    private readonly Grid _reading = new();
    private readonly Grid _compact = new() { Name = "MultiscreenProgrammeCards", Visibility = Visibility.Collapsed };
    private readonly SportsProgrammeCard[] _cards;
    private readonly ProgrammePreview _preview;
    private readonly StackPanel _now = new(), _upcoming = new() { Name = "MultiscreenUpcomingProgrammes" };
    private readonly ScrollViewer _nowScroll, _descriptionScroll, _upcomingScroll;
    private ChannelItem? _selected;
    private string? _channelId, _epgId;
    private Programme? _programme;
    private Programme[] _schedule = [];
    private CancellationTokenSource? _query;
    private DateTimeOffset _nextRead;
    private int _generation;
    private bool _bottom, _disposed;

    public SportsProgrammePanel(Window owner, MainViewModel model)
    {
        _model = model;
        Margin = new(4); Padding = new(16); CornerRadius = new(8); BorderThickness = new(1);
        SetResourceReference(BackgroundProperty, "PanelBrush"); SetResourceReference(BorderBrushProperty, "BorderBrush");
        System.Windows.Automation.AutomationProperties.SetName(this, "Телепрограмма канала со звуком");
        var root = new DockPanel(); Child = root;
        var header = new DockPanel { Margin = new(0, 0, 0, 14) }; DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        DockPanel.SetDock(_logo, Dock.Left); header.Children.Add(_logo);
        var heading = new StackPanel(); heading.Children.Add(Label("КАНАЛ СО ЗВУКОМ")); heading.Children.Add(_channel); header.Children.Add(heading);
        _now.Children.Add(Label("СЕЙЧАС В ЭФИРЕ")); _now.Children.Add(_title); _time.Margin = new(0, 7, 0, 0); _now.Children.Add(_time); _now.Children.Add(_progress);
        _time.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _nowScroll = Scroll(_now); _descriptionScroll = Scroll(_description);
        var upcoming = new DockPanel(); var label = Label("ДАЛЕЕ"); DockPanel.SetDock(label, Dock.Top); upcoming.Children.Add(label);
        _upcomingScroll = Scroll(_upcoming); upcoming.Children.Add(_upcomingScroll);
        _reading.Children.Add(_nowScroll); _reading.Children.Add(_descriptionScroll); _reading.Children.Add(upcoming);
        for (int i = 0; i < 3; i++)
        {
            _reading.RowDefinitions.Add(new() { Height = i == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            var section = (FrameworkElement)_reading.Children[i]; Grid.SetRow(section, i); section.Margin = new(0, i == 0 ? 0 : 14, 0, 0);
        }
        _cards = [new("СЕЙЧАС В ЭФИРЕ", "MultiscreenCurrentCard", "Нет телепрограммы на это время", true),
            new("ДАЛЕЕ", "MultiscreenNextCard", "Нет данных о следующей передаче"),
            new("ЗАТЕМ", "MultiscreenAfterNextCard", "Дальнейшая программа пока недоступна")];
        for (int i = 0; i < _cards.Length; i++)
        {
            _compact.ColumnDefinitions.Add(new()); Grid.SetColumn(_cards[i], i); _cards[i].Margin = new(i == 0 ? 0 : 8, 0, 0, 0); _compact.Children.Add(_cards[i]);
        }
        _preview = new(owner, model, _cards.Select(c => c.Details).ToArray(),
            index => index switch { 0 => _selected?.Current, 1 => _selected?.Next, _ => _selected?.AfterNext },
            () => _bottom && IsVisible, () => ActualWidth);
        var content = new Grid(); content.Children.Add(_reading); content.Children.Add(_compact); root.Children.Add(content);
        SizeChanged += (_, _) => LayoutReading();
        IsVisibleChanged += (_, _) => { if (IsVisible && !_disposed) Show(_selected); else _preview.Hide(); };
        LayoutReading();
    }
    private void LayoutReading()
    {
        _descriptionScroll.MaxHeight = Math.Clamp(ActualHeight * .14, 60, 120);
        _nowScroll.MaxHeight = Math.Clamp(ActualHeight * .24, 100, 170);
    }
    public void SetBottomLayout(bool bottom)
    {
        if (_bottom == bottom || _disposed) return;
        _bottom = bottom; _preview.Hide();
        _reading.Visibility = bottom ? Visibility.Collapsed : Visibility.Visible;
        _compact.Visibility = bottom ? Visibility.Visible : Visibility.Collapsed;
        if (bottom) { _query?.Cancel(); _generation++; }
        else _nextRead = DateTimeOffset.MinValue;
        Show(_selected);
    }
    public void Show(ChannelItem? channel)
    {
        if (_disposed) return;
        string? epgId = channel is not null && _model.Mappings.TryGetValue(channel.Id, out var mapping) ? mapping.EpgId : null;
        bool channelChanged = _channelId != channel?.Id || _epgId != epgId;
        bool programmeChanged = _programme != channel?.Current;
        _selected = channel;
        if (channelChanged)
        {
            _preview.Hide();
            _query?.Cancel(); _generation++;
            BindingOperations.ClearBinding(_logo, ChannelLogo.PathProperty);
            if (channel is not null) _logo.SetBinding(ChannelLogo.PathProperty, new Binding(nameof(ChannelItem.LogoPath)) { Source = channel }); else _logo.Path = null;
            _channelId = channel?.Id; _epgId = epgId; _upcomingScroll.ScrollToTop();
        }
        _channel.Text = channel?.Name ?? "Канал не выбран";
        _title.Text = channel?.Current?.Title ?? "Нет телепрограммы на это время";
        _time.Text = Time(channel?.Current);
        _progress.Value = channel?.Current is { } current && current.End > current.Start
            ? Math.Clamp((DateTimeOffset.Now - current.Start).TotalSeconds / (current.End - current.Start).TotalSeconds * 100, 0, 100) : 0;
        _progress.Visibility = channel?.Current is null ? Visibility.Collapsed : Visibility.Visible;
        _cards[0].Show(channel?.Current, _progress.Value); _cards[1].Show(channel?.Next); _cards[2].Show(channel?.AfterNext);
        _preview.RefreshAvailability(); if (_preview.Panel.IsVisible) _preview.Refresh();
        if (programmeChanged || channelChanged)
        {
            _programme = channel?.Current; _description.Show(_programme); _descriptionScroll.ScrollToTop(); _nowScroll.ScrollToTop();
            SetUpcoming(new[] { channel?.Next, channel?.AfterNext }.OfType<Programme>());
            _nextRead = DateTimeOffset.MinValue;
        }
        if (IsVisible && !_bottom && epgId is not null && DateTimeOffset.UtcNow >= _nextRead)
        {
            _nextRead = DateTimeOffset.UtcNow.AddSeconds(30);
            _query?.Cancel(); _query?.Dispose(); _query = new();
            _ = LoadUpcomingAsync(_model.EpgFor(channel!.Id)!, channel.Current?.End ?? DateTimeOffset.UtcNow, ++_generation, _query.Token);
        }
    }
    private async Task LoadUpcomingAsync(ChannelEpg epg, DateTimeOffset from, int generation, CancellationToken token)
    {
        try
        {
            var rows = await _model.Guide.GetUpcomingProgrammesAsync(epg, from, 24, token);
            if (!_disposed && generation == _generation) SetUpcoming(rows);
        }
        catch (OperationCanceledException) { }
        catch { /* Retain the two already loaded cards when an optional schedule read fails. */ }
    }
    private void SetUpcoming(IEnumerable<Programme> programmes)
    {
        var rows = programmes.DistinctBy(p => (p.ChannelId, p.Start, p.Title)).OrderBy(p => p.Start).ToArray();
        if (_schedule.SequenceEqual(rows) && _upcoming.Children.Count > 0) return;
        _schedule = rows; _upcoming.Children.Clear();
        foreach (var programme in rows)
        {
            var content = new StackPanel();
            var time = Text(10); var start = programme.Start.ToLocalTime();
            time.Text = (start.Date == DateTime.Today ? "" : start.ToString("dd.MM · ")) + Time(programme);
            time.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); content.Children.Add(time);
            var title = Text(12, true); title.Text = programme.Title; title.Margin = new(0, 4, 0, 0); title.TextTrimming = TextTrimming.CharacterEllipsis; title.MaxHeight = 38; content.Children.Add(title);
            var card = new Border { Child = content, DataContext = programme, Padding = new(0, 9, 0, 9), BorderThickness = new(0, 0, 0, 1), ToolTip = programme.Title };
            card.SetResourceReference(BorderBrushProperty, "BorderBrush"); _upcoming.Children.Add(card);
        }
        if (rows.Length == 0)
        {
            var empty = Text(12); empty.Text = "Нет данных о следующих передачах"; empty.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); _upcoming.Children.Add(empty);
        }
    }
    public void Dispose() { _disposed = true; _preview.Hide(); _generation++; _query?.Cancel(); _query?.Dispose(); }
    private static ScrollViewer Scroll(UIElement content) => new() { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private static string Time(Programme? programme) => programme is null ? "" : $"{programme.Start.ToLocalTime():HH:mm} — {programme.End.ToLocalTime():HH:mm}";
    private static TextBlock Text(double size, bool bold = false) => new() { FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
    private static TextBlock Label(string text)
    {
        var label = Text(9, true); label.Text = text; label.Margin = new(0, 0, 0, 5); label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); return label;
    }
}
