using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DzhiggaTV.Core;
using DzhiggaTV.Services;

namespace DzhiggaTV.App;

public sealed record GuideRow(ChannelItem Channel, IReadOnlyList<Programme> Programmes, DateTimeOffset Start, int Hours);

public sealed class TimelineItem : FrameworkElement
{
    public TimelineItem() { Height = 76; Width = 1620; DataContextChanged += (_, _) => InvalidateVisual(); }
    private static FormattedText Text(string text, double size, Brush brush) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, 1);
    protected override void OnRender(DrawingContext dc)
    {
        if (DataContext is not GuideRow row) return;
        var name = Text(row.Channel.Name, 13, (Brush)Application.Current.FindResource("TextBrush")); name.MaxTextWidth = 152; name.MaxTextHeight = 47; name.Trimming = TextTrimming.CharacterEllipsis; dc.DrawText(name, new(6,18));
        foreach (var p in row.Programmes)
        {
            var left = Math.Clamp((p.Start - row.Start).TotalHours, 0, row.Hours) * 240;
            var right = Math.Clamp((p.End - row.Start).TotalHours, 0, row.Hours) * 240;
            if (right <= left) continue;
            var rect = new Rect(170 + left, 5, Math.Max(1, right - left - 3), 63);
            var now = p.Start <= DateTimeOffset.UtcNow && p.End > DateTimeOffset.UtcNow;
            dc.DrawRoundedRectangle(new SolidColorBrush(now ? Color.FromRgb(28,66,67) : Color.FromRgb(30,39,48)), null, rect, 6, 6);
            if (rect.Width < 18) continue;
            dc.PushClip(new RectangleGeometry(rect));
            var title = Text(p.Title, 12, Brushes.WhiteSmoke); title.MaxTextWidth = Math.Max(1,rect.Width-15); title.MaxTextHeight = 30; title.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(title, new(rect.X+8,rect.Y+8));
            var time = Text($"{p.Start.ToLocalTime():HH:mm}–{p.End.ToLocalTime():HH:mm}",10,Brushes.LightSlateGray); dc.DrawText(time,new(rect.X+8,rect.Y+43)); dc.Pop();
        }
        var current = (DateTimeOffset.UtcNow - row.Start).TotalHours * 240;
        if (current >= 0 && current < row.Hours * 240) dc.DrawLine(new Pen(Brushes.Turquoise,1), new(170+current,0),new(170+current,76));
    }
    public Programme? ProgrammeAt(Point point) => DataContext is GuideRow row ? row.Programmes.FirstOrDefault(p => p.Start <= row.Start.AddHours((point.X-170)/240) && p.End > row.Start.AddHours((point.X-170)/240)) : null;
}

public sealed class GuideWindow : Window
{
    private readonly MainWindow _main;
    private readonly ListBox _rows = new();
    private readonly DatePicker _date = new() { SelectedDate = DateTime.Today, Width = 155 };
    private readonly ComboBox _hour = new() { ItemsSource = new[] { "00:00", "06:00", "12:00", "18:00" }, SelectedIndex = DateTime.Now.Hour / 6 };
    private readonly ComboBox _mode = new() { ItemsSource = new[] { "Программа по каналам", "Сейчас в эфире" }, SelectedIndex = 0, Width = 205 };
    private readonly TextBox _search = new() { Width = 260, ToolTip = "Поиск передачи по названию" };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, Margin = new(10), Foreground = Brushes.LightSlateGray };
    private readonly Canvas _axis = new() { Height = 27, Margin = new(8,8,0,0), ClipToBounds = true };
    private double _horizontalOffset;
    private int _generation;
    public GuideWindow(MainWindow main)
    {
        Theme.Window(this);
        _main = main; Owner = main; Title = "DzhiggaTV · Телепрограмма"; Width = 1150; Height = 760; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var dock = new DockPanel { Margin = new(20) }; Content = dock;
        var heading = new TextBlock { Text = "Телепрограмма", FontSize = 27, Margin = new(5,0,0,18) }; DockPanel.SetDock(heading,Dock.Top); dock.Children.Add(heading);
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(toolbar,Dock.Top); dock.Children.Add(toolbar);
        toolbar.Children.Add(_mode); toolbar.Children.Add(_date); toolbar.Children.Add(_hour); toolbar.Children.Add(_search);
        var find = new Button { Content = "Найти передачу" }; find.Click += async (_, _) => await SearchAsync(); toolbar.Children.Add(find);
        DockPanel.SetDock(_axis,Dock.Top); dock.Children.Add(_axis); DockPanel.SetDock(_details,Dock.Bottom); _details.MaxHeight = 135; dock.Children.Add(_details);
        var factory = new FrameworkElementFactory(typeof(TimelineItem)); _rows.ItemTemplate = new DataTemplate { VisualTree = factory };
        ScrollViewer.SetHorizontalScrollBarVisibility(_rows,ScrollBarVisibility.Auto); _rows.SelectionChanged += async (_, _) =>
        {
            if (_rows.SelectedItem is GuideRow row)
            { _details.Text = row.Programmes.FirstOrDefault() is { } p ? Describe(p) : "Программа для этого канала пока не назначена. Используйте диагностику EPG."; await _main.PlayAsync(row.Channel); }
        };
        _rows.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (Math.Abs(e.HorizontalChange) < .01) return; _horizontalOffset = e.HorizontalOffset;
            foreach (TextBlock label in _axis.Children) Canvas.SetLeft(label, (double)label.Tag - _horizontalOffset);
        }));
        _rows.PreviewMouseLeftButtonUp += (_, e) =>
        {
            var source = e.OriginalSource as DependencyObject;
            while (source is not null && source is not TimelineItem) source = VisualTreeHelper.GetParent(source);
            if (source is TimelineItem item && item.ProgrammeAt(e.GetPosition(item)) is { } p) _details.Text = Describe(p);
        };
        dock.Children.Add(_rows);
        _date.SelectedDateChanged += async (_, _) => await LoadAsync(); _hour.SelectionChanged += async (_, _) => await LoadAsync(); _mode.SelectionChanged += async (_, _) => await LoadAsync();
        Loaded += async (_, _) => await LoadAsync();
    }
    private static string Describe(Programme p) => $"{p.Title}  ·  {p.Start.ToLocalTime():dd.MM HH:mm} — {p.End.ToLocalTime():HH:mm}\n{p.Category}\n{p.Description}";
    private async Task LoadAsync()
    {
        var generation = ++_generation;
        var local = DateTime.SpecifyKind((_date.SelectedDate ?? DateTime.Today).Date.AddHours(Math.Max(0,_hour.SelectedIndex)*6),DateTimeKind.Unspecified);
        var start = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        var nowMode = _mode.SelectedIndex == 1;
        if (nowMode) start = DateTimeOffset.Now.AddMinutes(-20);
        _axis.Children.Clear();
        for (var n = 0; n < 6; n++)
        {
            var label = new TextBlock { Text = start.AddHours(n).ToLocalTime().ToString("HH:mm"), Foreground = Brushes.LightSlateGray, FontSize = 11, Tag = 170d + n * 240 };
            Canvas.SetLeft(label,170 + n * 240 - _horizontalOffset); _axis.Children.Add(label);
        }
        _details.Text = "Загрузка программы…";
        var allowed = _main.Model.AllChannels.Where(_main.Model.IsAllowed).ToArray();
        var rows = await Task.Run(async () =>
        {
            var list = new List<GuideRow>();
            foreach (var item in allowed)
            {
                if (generation != _generation) break;
                IReadOnlyList<Programme> programmes = [];
                if (_main.Model.Mappings.TryGetValue(item.Id, out var map)) programmes = await _main.Model.Store.GetProgrammesAsync(map.EpgId,start,start.AddHours(6));
                if (nowMode) programmes = programmes.Where(p => p.Start <= DateTimeOffset.Now && p.End > DateTimeOffset.Now).ToArray();
                list.Add(new(item,programmes,start,6));
            }
            return list;
        });
        if (generation != _generation) return;
        _rows.ItemsSource = rows; _details.Text = $"{rows.Count} каналов. Нажмите канал для просмотра, карточку — для описания. Время показано в часовом поясе Windows.";
    }
    private async Task SearchAsync()
    {
        ++_generation; _axis.Children.Clear();
        var found = await _main.Model.Store.SearchProgrammesAsync(_search.Text,200);
        var allowed = _main.Model.AllChannels.Where(_main.Model.IsAllowed).Where(c => _main.Model.Mappings.ContainsKey(c.Id)).GroupBy(c => _main.Model.Mappings[c.Id].EpgId).ToDictionary(g => g.Key,g => g.First());
        _rows.ItemsSource = found.Where(p => allowed.ContainsKey(p.ChannelId)).Select(p => new GuideRow(allowed[p.ChannelId],[p],p.Start,6)).ToArray();
        _details.Text = "Результаты поиска передач. Прошедшие передачи доступны как информация; телеархив провайдера не заявлен.";
    }
}

public sealed class MappingWindow : Window
{
    public MappingWindow(MainWindow main, ChannelItem channel)
    {
        Theme.Window(this);
        Owner = main; Title = "DzhiggaTV · сопоставление EPG"; Width = 650; Height = 610; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var dock = new DockPanel { Margin = new(24) }; Content = dock;
        var title = new TextBlock { Text = channel.Name, FontSize = 24, Margin = new(0,0,0,15) }; DockPanel.SetDock(title,Dock.Top); dock.Children.Add(title);
        var search = new TextBox { ToolTip = "Название или ID канала EPG.ONE", Margin = new(0,0,0,12) }; DockPanel.SetDock(search,Dock.Top); dock.Children.Add(search);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(buttons,Dock.Bottom); dock.Children.Add(buttons);
        var list = new ListBox { DisplayMemberPath = "Label" }; dock.Children.Add(list);
        var matcher = new ChannelMatcher(main.Model.EpgChannels.Values.ToArray());
        void Update() => list.ItemsSource = matcher.Candidates(channel.Channel, 150, search.Text).Select(c => new MappingChoice(c.EpgId,$"{c.Name}  ·  {c.EpgId}  ·  {c.Confidence:P0} {c.Reason}")).ToArray();
        search.TextChanged += (_, _) => Update(); Update();
        var clear = new Button { Content = "Снять назначение" }; clear.Click += async (_, _) => { await main.Model.Store.RemoveMappingAsync(channel.Id); DialogResult = true; }; buttons.Children.Add(clear);
        var save = new Button { Content = "Назначить", IsDefault = true }; save.Click += async (_, _) =>
        {
            if (list.SelectedItem is MappingChoice match)
            { await main.Model.Store.SaveMappingsAsync([new(channel.Id,match.Id,main.Model.EpgChannels[match.Id].Name,"manual",1,true)]); main.Model.InvalidateLogo(channel.Id); DialogResult = true; }
        }; buttons.Children.Add(save);
    }
    private sealed record MappingChoice(string Id,string Label);
}
