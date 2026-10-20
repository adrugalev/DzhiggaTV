using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DzhiggaTV.Core;

namespace DzhiggaTV.App;

internal sealed class SportsProgrammePanel : Border
{
    private readonly Image _logo = new() { Width = 30, Height = 30, Margin = new(0, 0, 10, 0) };
    private readonly TextBlock _channel = Text(14, true);
    private readonly TextBlock _title = Text(18, true), _time = Text(11), _next = Text(13, true), _after = Text(13, true);
    private readonly ProgressBar _progress = new() { Height = 3, Maximum = 100, Margin = new(0, 12, 0, 0) };
    private readonly ProgrammeDescriptionView _description = new();
    private readonly Grid _reading = new();
    private readonly StackPanel _now = new(), _upcoming = new();
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private string? _channelId;
    private Programme? _programme;
    private bool _wide;

    public SportsProgrammePanel()
    {
        Margin = new(4); Padding = new(16); CornerRadius = new(8); BorderThickness = new(1);
        SetResourceReference(BackgroundProperty, "PanelBrush"); SetResourceReference(BorderBrushProperty, "BorderBrush");
        System.Windows.Automation.AutomationProperties.SetName(this, "Телепрограмма канала со звуком");
        var root = new DockPanel(); Child = root;
        var header = new DockPanel { Margin = new(0, 0, 0, 18) }; DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        DockPanel.SetDock(_logo, Dock.Left); header.Children.Add(_logo);
        var heading = new StackPanel(); heading.Children.Add(Label("КАНАЛ СО ЗВУКОМ")); heading.Children.Add(_channel); header.Children.Add(heading);
        _now.Children.Add(Label("СЕЙЧАС В ЭФИРЕ")); _now.Children.Add(_title); _time.Margin = new(0, 8, 0, 0); _now.Children.Add(_time); _now.Children.Add(_progress);
        _time.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _upcoming.Children.Add(Label("ДАЛЕЕ")); _upcoming.Children.Add(_next);
        var then = Label("ЗАТЕМ"); then.Margin = new(0, 18, 0, 5); _upcoming.Children.Add(then); _upcoming.Children.Add(_after);
        _reading.Children.Add(_now); _reading.Children.Add(_description); _reading.Children.Add(_upcoming);
        _scroll.Content = _reading; root.Children.Add(_scroll);
        SizeChanged += (_, _) => { bool wide = ActualWidth >= 620 && ActualHeight < 340; if (wide != _wide || _reading.RowDefinitions.Count == 0) { _wide = wide; LayoutReading(); } };
        LayoutReading();
    }
    private void LayoutReading()
    {
        _reading.ColumnDefinitions.Clear(); _reading.RowDefinitions.Clear();
        for (int i = 0; i < (_wide ? 3 : 1); i++) _reading.ColumnDefinitions.Add(new());
        for (int i = 0; i < (_wide ? 1 : 3); i++) _reading.RowDefinitions.Add(new() { Height = GridLength.Auto });
        FrameworkElement[] sections = [_now, _description, _upcoming];
        for (int i = 0; i < sections.Length; i++)
        {
            Grid.SetColumn(sections[i], _wide ? i : 0); Grid.SetRow(sections[i], _wide ? 0 : i);
            sections[i].Margin = _wide ? new(i == 0 ? 0 : 20, 0, 0, 0) : new(0, i == 0 ? 0 : 22, 0, 0);
        }
    }
    public void Show(ChannelItem? channel)
    {
        bool changed = _channelId != channel?.Id || _programme != channel?.Current;
        if (_channelId != channel?.Id)
        {
            _logo.SetBinding(Image.SourceProperty, new Binding(nameof(ChannelItem.LogoPath)) { Source = channel });
            _channelId = channel?.Id;
        }
        _channel.Text = channel?.Name ?? "Канал не выбран";
        _title.Text = channel?.Current?.Title ?? "Нет телепрограммы на это время";
        _time.Text = Time(channel?.Current);
        _progress.Value = channel?.Current is { } current && current.End > current.Start
            ? Math.Clamp((DateTimeOffset.Now - current.Start).TotalSeconds / (current.End - current.Start).TotalSeconds * 100, 0, 100) : 0;
        _progress.Visibility = channel?.Current is null ? Visibility.Collapsed : Visibility.Visible;
        _next.Text = Describe(channel?.Next); _after.Text = Describe(channel?.AfterNext);
        if (changed)
        {
            _programme = channel?.Current; _description.Show(_programme); _scroll.ScrollToTop();
        }
    }
    private static string Time(Programme? programme) => programme is null ? "" : $"{programme.Start.ToLocalTime():HH:mm} — {programme.End.ToLocalTime():HH:mm}";
    private static string Describe(Programme? programme) => programme is null ? "Нет данных" : $"{Time(programme)}\n{programme.Title}";
    private static TextBlock Text(double size, bool bold = false) => new() { FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
    private static TextBlock Label(string text)
    {
        var label = Text(9, true); label.Text = text; label.Margin = new(0, 0, 0, 5); label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); return label;
    }
}
