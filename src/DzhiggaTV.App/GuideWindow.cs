using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using DzhiggaTV.Core;

namespace DzhiggaTV.App;

public sealed class GuideWindow : Window
{
    private readonly MainWindow _main;
    private readonly Func<ChannelItem, Task> _play;
    private readonly Func<DateTimeOffset> _clock;
    private readonly GuideViewport _viewport = new();
    private readonly ListBox _rows = new() { Padding = new(0), BorderThickness = new(0) };
    private readonly DatePicker _date = new() { Width = 132, Height = 34, FontSize = 12, SelectedDateFormat = DatePickerFormat.Short, Margin = new(4, 0, 4, 0) };
    private readonly TextBox _search = new() { Height = 34, FontSize = 12, Padding = new(30, 7, 29, 6), MaxLength = 128 };
    private readonly TextBlock _searchHint = new() { Text = "Канал или передача", FontSize = 12, Margin = new(30, 0, 26, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private readonly Button _clearSearch;
    private readonly ComboBox _playlist = new() { Width = 220, Height = 32, FontSize = 12, Margin = new(0), DisplayMemberPath = "Name", ToolTip = "Плейлист" };
    private readonly ComboBox _categoryFilter = new() { Width = 220, Height = 32, FontSize = 12, Margin = new(0), ToolTip = "Категория" };
    private readonly Button _resetFilters;
    private bool _changingFilters;
    private IReadOnlyList<PlaylistSource> _playlistSources = [];
    private readonly ScrollBar _horizontal = new() { Orientation = Orientation.Horizontal, Minimum = 0, SmallChange = 60, Height = 20 };
    private readonly GuideAxis _axis;
    private readonly GuideNowLine _nowLine;
    private readonly Grid _body = new();
    private readonly Grid _timeline = new();
    private readonly Border _details;
    private readonly ColumnDefinition _detailsColumn = new() { Width = new(330) };
    private readonly RowDefinition _detailsRow = new() { Height = new(0) };
    private readonly TextBlock _empty = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new(24), IsHitTestVisible = false };
    private readonly TextBlock _count = new() { FontSize = 11 };
    private readonly TextBlock _badge = new() { FontSize = 10, Margin = new(0, 0, 0, 12) };
    private readonly TextBlock _title = new() { FontSize = 21, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, LineHeight = 27 };
    private readonly TextBlock _channel = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) };
    private readonly TextBlock _time = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 0) };
    private readonly TextBlock _category = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 0) };
    private readonly TextBlock _description = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 21, Margin = new(0, 20, 6, 0) };
    private readonly Grid _detailContent = new();
    private readonly ScrollViewer _descriptionScroll;
    private readonly Button _watch;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private CancellationTokenSource? _loading;
    private List<GuideRow> _allRows = [];
    private string? _preferredChannelId;
    private bool _closed, _changingDate, _changingSelection, _changingOffset, _followNow = true, _activating;
    private int _generation;

    public GuideWindow(MainWindow main, Func<ChannelItem, Task>? play = null, Func<DateTimeOffset>? clock = null)
    {
        _main = main; _play = play ?? main.PlayAsync; _clock = clock ?? (() => DateTimeOffset.Now);
        _viewport.RequestLogo = channel => _main.Model.LoadLogos([channel]);
        Theme.Window(this); Owner = main; Title = "DzhiggaTV · Телепрограмма";
        Width = Math.Min(1280, SystemParameters.WorkArea.Width - 32); Height = Math.Min(800, SystemParameters.WorkArea.Height - 40);
        MinWidth = 900; MinHeight = Math.Min(560, Height); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _date.SelectedDate = _clock().LocalDateTime.Date; _date.DisplayDate = _date.SelectedDate.Value;
        ConfigureDatePicker();
        _axis = new(_viewport) { Height = GuideLayout.HeaderHeight, Margin = new(0, 0, SystemParameters.VerticalScrollBarWidth, 0) };
        _nowLine = new(_viewport) { IsHitTestVisible = false, Margin = new(0, 0, SystemParameters.VerticalScrollBarWidth, 0) };
        var root = new Grid { Margin = new(20, 16, 20, 12) }; Content = root;
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var toolbar = new Grid();
        foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, new GridLength(16), new GridLength(235) }) toolbar.ColumnDefinitions.Add(new() { Width = width });
        toolbar.Children.Add(new TextBlock { Text = "Телепрограмма", FontSize = 22, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 16, 0) });
        var navigation = new StackPanel { Orientation = Orientation.Horizontal };
        var previous = CompactButton("‹", "Предыдущий день", 32); previous.FontSize = 21;
        var next = CompactButton("›", "Следующий день", 32); next.FontSize = 21;
        var now = CompactButton("Сейчас", "Вернуться к текущему эфиру (Ctrl+Home)", 82); now.Margin = new(10, 0, 0, 0); now.SetResourceReference(BorderBrushProperty, "AccentBrush");
        navigation.Children.Add(previous); navigation.Children.Add(_date); navigation.Children.Add(next); navigation.Children.Add(now);
        Grid.SetColumn(navigation, 2); toolbar.Children.Add(navigation);
        var searchBox = new Grid(); Grid.SetColumn(searchBox, 4); toolbar.Children.Add(searchBox);
        searchBox.Children.Add(_search); searchBox.Children.Add(_searchHint);
        searchBox.Children.Add(new TextBlock { Text = "⌕", FontSize = 19, Margin = new(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
        _clearSearch = CompactButton("×", "Очистить поиск", 25); _clearSearch.Height = 28; _clearSearch.FontSize = 17;
        _clearSearch.HorizontalAlignment = HorizontalAlignment.Right; _clearSearch.Margin = new(0, 0, 3, 0); _clearSearch.BorderThickness = new(0); _clearSearch.Background = Brushes.Transparent;
        searchBox.Children.Add(_clearSearch);
        var controls = new StackPanel(); controls.Children.Add(toolbar); root.Children.Add(controls);
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 10, 0, 0) };
        filters.Children.Add(FilterLabel("Плейлист")); filters.Children.Add(_playlist);
        var categoryLabel = FilterLabel("Категория"); categoryLabel.Margin = new(20, 0, 9, 0); filters.Children.Add(categoryLabel); filters.Children.Add(_categoryFilter);
        _resetFilters = CompactButton("Сбросить", "Показать все плейлисты и категории", 82); _resetFilters.Height = 32; _resetFilters.Margin = new(12, 0, 0, 0);
        filters.Children.Add(_resetFilters); controls.Children.Add(filters);
        System.Windows.Automation.AutomationProperties.SetName(_playlist, "Плейлист телепрограммы");
        System.Windows.Automation.AutomationProperties.SetName(_categoryFilter, "Категория телепрограммы");
        _playlist.SelectionChanged += (_, _) => { if (!_changingFilters) { UpdateCategories(); ApplyFilter(true); } };
        _categoryFilter.SelectionChanged += (_, _) => { if (!_changingFilters) ApplyFilter(true); };
        _resetFilters.Click += (_, _) => { _changingFilters = true; _playlist.SelectedIndex = 0; _changingFilters = false; UpdateCategories(false); ApplyFilter(true); };
        System.Windows.Automation.AutomationProperties.SetName(_search, "Поиск по каналам и передачам");
        _search.ToolTip = "Поиск по каналам и передачам выбранного дня (Ctrl+F)";
        Grid.SetRow(_body, 1); _body.Margin = new(0, 14, 0, 0); _body.ColumnDefinitions.Add(new()); _body.ColumnDefinitions.Add(_detailsColumn);
        _body.RowDefinitions.Add(new()); _body.RowDefinitions.Add(_detailsRow); root.Children.Add(_body);
        _timeline.RowDefinitions.Add(new() { Height = new(GuideLayout.HeaderHeight) }); _timeline.RowDefinitions.Add(new()); _timeline.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var timelineBorder = new Border { CornerRadius = new(10), BorderThickness = new(1), ClipToBounds = true, Child = _timeline };
        timelineBorder.SetResourceReference(BorderBrushProperty, "BorderBrush"); _body.Children.Add(timelineBorder);
        _timeline.Children.Add(_axis); Grid.SetRow(_rows, 1); _timeline.Children.Add(_rows);
        Grid.SetRow(_empty, 1); _timeline.Children.Add(_empty);
        _horizontal.Margin = new(GuideLayout.ChannelWidth, 0, SystemParameters.VerticalScrollBarWidth, 0); Grid.SetRow(_horizontal, 2); _timeline.Children.Add(_horizontal);
        Grid.SetRowSpan(_nowLine, 2); _timeline.Children.Add(_nowLine);
        ConfigureRows();
        var detailsDock = new DockPanel { Margin = new(18) };
        _watch = CompactButton("Смотреть канал", "Переключиться на этот канал", double.NaN); _watch.Height = 38; _watch.Margin = new(0, 16, 0, 0);
        _watch.SetResourceReference(BorderBrushProperty, "AccentBrush"); DockPanel.SetDock(_watch, Dock.Bottom); detailsDock.Children.Add(_watch);
        var heading = new StackPanel(); heading.Children.Add(_badge); heading.Children.Add(_title); heading.Children.Add(_channel); heading.Children.Add(_time); heading.Children.Add(_category);
        _detailContent.ColumnDefinitions.Add(new()); _detailContent.ColumnDefinitions.Add(new() { Width = new(0) });
        _detailContent.RowDefinitions.Add(new() { Height = GridLength.Auto }); _detailContent.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _detailContent.Children.Add(heading); Grid.SetRow(_description, 1); _detailContent.Children.Add(_description);
        _descriptionScroll = new() { Content = _detailContent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        detailsDock.Children.Add(_descriptionScroll);
        _details = new Border { CornerRadius = new(10), Child = detailsDock, Margin = new(14, 0, 0, 0) };
        _details.SetResourceReference(BackgroundProperty, "PanelBrush"); Grid.SetColumn(_details, 1); _body.Children.Add(_details);
        var footer = new Grid { Margin = new(2, 9, 0, 0) }; footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.Children.Add(_count);
        var hint = new TextBlock { Text = "Щелчок — описание · двойной щелчок — смотреть", FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); Grid.SetColumn(hint, 1); footer.Children.Add(hint); Grid.SetRow(footer, 2); root.Children.Add(footer);
        foreach (var block in new[] { _searchHint, _count, _empty, _time, _category, _badge }) block.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _channel.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        _viewport.Selection.Changed += (_, _) => ShowSelection();
        _horizontal.ValueChanged += (_, e) => { _viewport.Offset = e.NewValue; if (!_changingOffset) _followNow = false; RefreshTimeline(); };
        _timeline.SizeChanged += (_, _) => UpdateScrollMetrics(); SizeChanged += (_, _) => UpdateDetailsLayout();
        previous.Click += (_, _) => _date.SelectedDate = (_date.SelectedDate ?? _clock().LocalDateTime.Date).AddDays(-1);
        next.Click += (_, _) => _date.SelectedDate = (_date.SelectedDate ?? _clock().LocalDateTime.Date).AddDays(1);
        now.Click += async (_, _) => await GoNowAsync(); _clearSearch.Click += (_, _) => _search.Clear();
        _search.TextChanged += (_, _) => { UpdateSearchHint(); _searchTimer.Stop(); _searchTimer.Start(); };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); ApplyFilter(true); };
        _search.KeyDown += (_, e) => { if (e.Key == Key.Enter) { _searchTimer.Stop(); ApplyFilter(true); e.Handled = true; } };
        _date.SelectedDateChanged += async (_, _) => { if (!_changingDate) await LoadDayAsync(); };
        _watch.Click += async (_, _) => { if (_viewport.Selection.Row is { } row) await PlayChannelAsync(row.Channel); };
        _timer.Tick += async (_, _) => await TickAsync();
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F) { _search.Focus(); _search.SelectAll(); e.Handled = true; }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Home) { await GoNowAsync(); e.Handled = true; }
        };
        Loaded += async (_, _) => { UpdateDetailsLayout(); UpdateSearchHint(); ShowSelection(); await LoadDayAsync(); if (!_closed) _timer.Start(); };
        Closed += (_, _) => { _closed = true; ++_generation; _timer.Stop(); _searchTimer.Stop(); _loading?.Cancel(); _loading?.Dispose(); };
    }

    private static Button CompactButton(string label, string tip, double width) => new()
    { Content = label, ToolTip = tip, Width = width, Height = 34, FontSize = 12, Padding = new(8, 3, 8, 3), Margin = new(0) };
    private static TextBlock FilterLabel(string text)
    {
        var label = new TextBlock { Text = text, FontSize = 11, Margin = new(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); return label;
    }
    private string? SelectedPlaylist => (_playlist.SelectedItem as GuidePlaylistChoice)?.Id;
    private string? SelectedCategory => _categoryFilter.SelectedIndex > 0 ? _categoryFilter.SelectedItem as string : null;
    private void UpdateFilters()
    {
        var selected = SelectedPlaylist;
        var ids = _allRows.Select(row => row.Channel.Channel.PlaylistId).Distinct().ToArray();
        var choices = new List<GuidePlaylistChoice> { new(null, "Все плейлисты") };
        choices.AddRange(ids.Select((id, index) => new GuidePlaylistChoice(id, _playlistSources.FirstOrDefault(source => source.Id == id)?.Name ?? $"Плейлист {index + 1}")));
        _changingFilters = true; _playlist.ItemsSource = choices; _playlist.SelectedItem = choices.FirstOrDefault(choice => choice.Id == selected) ?? choices[0]; _changingFilters = false;
        UpdateCategories();
    }
    private void UpdateCategories(bool preserve = true)
    {
        var selected = preserve ? SelectedCategory : null;
        var categories = new[] { "Все категории" }.Concat(GuideFiltering.Categories(_allRows, SelectedPlaylist)).ToArray();
        _changingFilters = true; _categoryFilter.ItemsSource = categories; _categoryFilter.SelectedItem = categories.Contains(selected) ? selected : categories[0]; _changingFilters = false;
    }

    private void ConfigureDatePicker()
    {
        _date.SetResourceReference(ForegroundProperty, "TextBrush"); _date.SetResourceReference(BackgroundProperty, "CardBrush"); _date.SetResourceReference(BorderBrushProperty, "BorderBrush");
        _date.Language = XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag);
        _date.Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="DatePicker">
             <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="7">
              <Grid><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="28"/></Grid.ColumnDefinitions>
               <DatePickerTextBox x:Name="PART_TextBox" Foreground="{TemplateBinding Foreground}" Background="Transparent" BorderThickness="0" Padding="0" FontSize="12" VerticalContentAlignment="Center">
                <DatePickerTextBox.Template><ControlTemplate TargetType="DatePickerTextBox"><ScrollViewer x:Name="PART_ContentHost" Margin="8,0,0,0" Padding="0" VerticalAlignment="Center"/></ControlTemplate></DatePickerTextBox.Template>
               </DatePickerTextBox>
               <Button x:Name="PART_Button" Grid.Column="1" Content="⌄" Foreground="{TemplateBinding Foreground}" Background="Transparent" BorderThickness="0" Padding="0" Margin="0" Focusable="False"/>
               <Popup x:Name="PART_Popup" Placement="Bottom" AllowsTransparency="True" StaysOpen="False"/>
              </Grid>
             </Border>
            </ControlTemplate>
            """);
        _date.DateValidationError += (_, e) => e.ThrowException = false;
    }

    private void ConfigureRows()
    {
        _rows.Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ListBox">
             <ScrollViewer x:Name="PART_ScrollViewer" Padding="0" Focusable="False" CanContentScroll="True" HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Visible"><ItemsPresenter/></ScrollViewer>
            </ControlTemplate>
            """);
        var container = new Style(typeof(ListBoxItem));
        container.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        container.Setters.Add(new Setter(MarginProperty, new Thickness(0))); container.Setters.Add(new Setter(PaddingProperty, new Thickness(0)));
        container.Setters.Add(new Setter(TemplateProperty, (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ListBoxItem"><ContentPresenter/></ControlTemplate>
            """)));
        _rows.ItemContainerStyle = container;
        VirtualizingPanel.SetIsVirtualizing(_rows, true); VirtualizingPanel.SetVirtualizationMode(_rows, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(_rows, ScrollUnit.Pixel);
        var factory = new FrameworkElementFactory(typeof(TimelineItem)); factory.SetValue(TimelineItem.ViewportProperty, _viewport);
        _rows.ItemTemplate = new DataTemplate { VisualTree = factory };
        _rows.PreviewMouseLeftButtonDown += async (_, e) =>
        {
            if (_activating) return;
            var source = e.OriginalSource as DependencyObject;
            while (source is not null && source is not TimelineItem) source = VisualTreeHelper.GetParent(source);
            if (source is not TimelineItem item || item.DataContext is not GuideRow row) return;
            var point = e.GetPosition(item); var programme = item.ProgrammeAt(point);
            if (programme is null && point.X >= GuideLayout.ChannelWidth) return;
            programme ??= row.At(_viewport.Now);
            e.Handled = true; _changingSelection = true; _rows.SelectedItem = row; _changingSelection = false;
            (_rows.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem)?.Focus();
            await _viewport.Selection.ClickAsync(row, programme, e.ClickCount, PlayChannelAsync);
        };
        _rows.SelectionChanged += (_, _) =>
        {
            if (!_changingSelection && _rows.SelectedItem is GuideRow row)
                _viewport.Selection.Select(row, row.At(ReferenceTime()) ?? row.Programmes.FirstOrDefault());
        };
        _rows.PreviewKeyDown += async (_, e) =>
        {
            if (_viewport.Selection.Row is not { } row) return;
            if (e.Key == Key.Enter) { e.Handled = true; await PlayChannelAsync(row.Channel); }
            else if (e.Key is Key.Left or Key.Right && row.Programmes.Count > 0)
            {
                int index = row.Programmes.ToList().FindIndex(p => p == _viewport.Selection.Programme);
                index = Math.Clamp(index + (e.Key == Key.Left ? -1 : 1), 0, row.Programmes.Count - 1);
                var programme = row.Programmes[index]; _viewport.Selection.Select(row, programme); FocusTime(programme.Start); _followNow = false; e.Handled = true;
            }
        };
    }

    private DateTimeOffset ReferenceTime()
    {
        var now = _clock();
        if (_viewport.Range.Contains(now)) return now;
        var local = DateTime.SpecifyKind((_date.SelectedDate ?? now.LocalDateTime.Date).Date + now.LocalDateTime.TimeOfDay, DateTimeKind.Unspecified);
        return new(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }
    private async Task LoadDayAsync()
    {
        if (_closed) return;
        _loading?.Cancel(); _loading?.Dispose(); _loading = new(); var token = _loading.Token;
        int generation = ++_generation;
        var day = _date.SelectedDate ?? _clock().LocalDateTime.Date;
        _viewport.Range = GuideRange.ForDay(day, TimeZoneInfo.Local); _viewport.Now = _clock(); _followNow = _viewport.Range.Contains(_viewport.Now);
        _allRows = []; _rows.ItemsSource = null; _viewport.Selection.Clear(); _empty.Text = "Загружаем телепрограмму…"; _empty.Visibility = Visibility.Visible; _count.Text = "";
        UpdateScrollMetrics(); FocusTime(ReferenceTime());
        var channels = _main.Model.AllChannels.Where(_main.Model.IsAllowed).Select(c => (Channel: c, EpgId: _main.Model.Mappings.GetValueOrDefault(c.Id)?.EpgId)).ToArray();
        var range = _viewport.Range;
        try
        {
            var programmes = await Task.Run(() => _main.Model.Store.GetGuideAsync(channels.Where(c => c.EpgId is not null).Select(c => c.EpgId!), range.Start, range.End, token), token);
            var sources = await _main.Model.Store.GetPlaylistSourcesAsync(token);
            if (_closed || generation != _generation) return;
            _playlistSources = sources;
            _allRows = channels.Select(c => new GuideRow(c.Channel, c.EpgId is not null && programmes.TryGetValue(c.EpgId, out var entries) ? entries : [])).ToList();
            UpdateFilters();
            ApplyFilter(_search.Text.Trim().Length > 0); if (_search.Text.Trim().Length == 0) SelectInitial();
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (_closed || generation != _generation) return;
            _empty.Text = "Не удалось загрузить программу. Нажмите «Сейчас», чтобы повторить."; _empty.Visibility = Visibility.Visible;
        }
    }

    private void ApplyFilter(bool focusMatch)
    {
        string query = _search.Text.Trim();
        var visible = GuideFiltering.Filter(_allRows, SelectedPlaylist, SelectedCategory, query);
        _resetFilters.Visibility = SelectedPlaylist is not null || SelectedCategory is not null ? Visibility.Visible : Visibility.Collapsed;
        _changingSelection = true; _rows.ItemsSource = visible; _changingSelection = false;
        _count.Text = visible.Count == _allRows.Count ? $"{visible.Count:N0} каналов" : $"{visible.Count:N0} из {_allRows.Count:N0} каналов";
        _empty.Text = _allRows.Count == 0 ? "В библиотеке пока нет каналов" : "Ничего не найдено";
        _empty.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (focusMatch && query.Length == 0 && visible.Count > 0)
        {
            SelectInitial(); FocusTime(ReferenceTime()); _followNow = _viewport.Range.Contains(_clock());
        }
        else if (focusMatch && visible.Count > 0)
        {
            var row = visible[0]; var programme = row.At(ReferenceTime()) ?? row.Programmes.FirstOrDefault(p => p.Start >= ReferenceTime()) ?? row.Programmes.FirstOrDefault();
            Select(row, programme); if (programme is not null) FocusTime(programme.Start); _followNow = false;
        }
        else if (visible.Count == 0) { _viewport.Selection.Clear(); ShowEmptySelection(); }
        RefreshTimeline();
    }
    private void SelectInitial()
    {
        var visible = (_rows.ItemsSource as IEnumerable<GuideRow>)?.ToArray() ?? [];
        var reference = ReferenceTime();
        var row = visible.FirstOrDefault(r => r.Channel.Id == (_preferredChannelId ?? _main.Model.Active?.Id)) ?? visible.FirstOrDefault(r => r.At(reference) is not null) ?? visible.FirstOrDefault();
        if (row is null) { ShowEmptySelection(); return; }
        Select(row, row.At(reference) ?? row.Programmes.FirstOrDefault(p => p.Start >= reference) ?? row.Programmes.FirstOrDefault());
    }
    private void Select(GuideRow row, Programme? programme)
    {
        _changingSelection = true; _rows.SelectedItem = row; _changingSelection = false;
        _viewport.Selection.Select(row, programme); _rows.ScrollIntoView(row);
    }
    private void ShowEmptySelection()
    {
        _title.Text = "Выберите передачу"; _description.Text = "Нажмите на ячейку, чтобы прочитать полное описание.";
        _channel.Text = _time.Text = _category.Text = _badge.Text = ""; _watch.Visibility = Visibility.Collapsed;
    }
    private void ShowSelection()
    {
        if (_viewport.Selection.Row is not { } row) { ShowEmptySelection(); return; }
        _preferredChannelId = row.Channel.Id;
        var programme = _viewport.Selection.Programme;
        _title.Text = programme?.Title ?? "Нет данных о передаче"; _channel.Text = row.Channel.Name;
        _time.Text = programme is null ? "" : $"{programme.Start.ToLocalTime():dd.MM} · {programme.Start.ToLocalTime():HH:mm} — {programme.End.ToLocalTime():HH:mm}";
        _category.Text = programme?.Category?.Trim() ?? "";
        _category.Visibility = _category.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _description.Text = string.IsNullOrWhiteSpace(programme?.Description) ? "Подробного описания в телепрограмме нет." : programme.Description.Trim();
        _watch.Visibility = Visibility.Visible; _descriptionScroll.ScrollToHome(); UpdateBadge(); RefreshTimeline();
    }
    private void UpdateBadge()
    {
        if (_viewport.Selection.Row is null) { _badge.Text = ""; return; }
        var programme = _viewport.Selection.Programme;
        _badge.Text = programme is null ? "ТЕЛЕПРОГРАММА" : programme.Start <= _viewport.Now && programme.End > _viewport.Now ? "СЕЙЧАС В ЭФИРЕ" : programme.Start > _viewport.Now ? "ПРЕДСТОЯЩИЙ ЭФИР" : "ЭФИР ЗАВЕРШЁН";
        _badge.SetResourceReference(TextBlock.ForegroundProperty, programme is not null && programme.Start <= _viewport.Now && programme.End > _viewport.Now ? "AccentBrush" : "MutedBrush");
    }
    private async Task PlayChannelAsync(ChannelItem channel)
    {
        if (_activating || _closed) return;
        _activating = true; _watch.IsEnabled = false;
        try { await _play(channel); if (!_closed) Close(); }
        catch { if (!_closed) _badge.Text = "Не удалось открыть канал. Попробуйте ещё раз."; }
        finally { _activating = false; if (!_closed) _watch.IsEnabled = true; }
    }
    private async Task GoNowAsync()
    {
        _search.Clear(); _searchTimer.Stop(); _followNow = true;
        _preferredChannelId = _main.Model.Active?.Id;
        var today = _clock().LocalDateTime.Date;
        bool changed = _date.SelectedDate != today;
        _changingDate = true; _date.SelectedDate = today; _changingDate = false;
        if (changed || _allRows.Count == 0) await LoadDayAsync();
        else { ApplyFilter(false); SelectInitial(); }
        if (!_closed) FocusTime(_clock());
    }
    private async Task TickAsync()
    {
        _viewport.Now = _clock();
        if (_followNow && !_viewport.Range.Contains(_viewport.Now)) { await GoNowAsync(); return; }
        double x = _viewport.Range.Position(_viewport.Now) - _viewport.Offset;
        if (_followNow && (x < 0 || x > _viewport.Width * .85)) FocusTime(_viewport.Now);
        UpdateBadge(); RefreshTimeline();
    }
    private void FocusTime(DateTimeOffset instant)
    {
        _changingOffset = true;
        _horizontal.Value = _viewport.Range.FocusOffset(instant, _viewport.Width);
        _viewport.Offset = _horizontal.Value; _changingOffset = false; RefreshTimeline();
    }
    private void UpdateScrollMetrics()
    {
        _viewport.Width = Math.Max(1, _rows.ActualWidth - SystemParameters.VerticalScrollBarWidth - GuideLayout.ChannelWidth);
        _changingOffset = true; _horizontal.Maximum = Math.Max(0, _viewport.Range.Width - _viewport.Width);
        _horizontal.ViewportSize = _viewport.Width; _horizontal.LargeChange = _viewport.Width * .8;
        _viewport.Offset = _horizontal.Value; _changingOffset = false; RefreshTimeline();
    }
    private void UpdateDetailsLayout()
    {
        bool narrow = ActualWidth < 1080;
        _detailsColumn.Width = new(narrow ? 0 : 330); _detailsRow.Height = new(narrow ? Math.Clamp(ActualHeight * .3, 168, 210) : 0);
        Grid.SetColumn(_details, narrow ? 0 : 1); Grid.SetRow(_details, narrow ? 1 : 0); Grid.SetColumnSpan(_details, narrow ? 2 : 1);
        _details.Margin = narrow ? new(0, 12, 0, 0) : new(14, 0, 0, 0);
        _detailContent.ColumnDefinitions[0].Width = narrow ? new(240) : new(1, GridUnitType.Star);
        _detailContent.ColumnDefinitions[1].Width = narrow ? new(1, GridUnitType.Star) : new(0);
        Grid.SetRow(_description, narrow ? 0 : 1); Grid.SetColumn(_description, narrow ? 1 : 0);
        _description.Margin = narrow ? new(24, 0, 6, 0) : new(0, 20, 6, 0);
        _watch.Height = narrow ? 30 : 38; _watch.Margin = new(0, narrow ? 10 : 16, 0, 0);
    }
    private void UpdateSearchHint()
    {
        _searchHint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _clearSearch.Visibility = _search.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private void RefreshTimeline()
    {
        _axis.InvalidateVisual(); _nowLine.InvalidateVisual();
        RefreshRows(_rows);
        static void RefreshRows(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is TimelineItem row) row.InvalidateVisual(); else RefreshRows(child);
            }
        }
    }
}
