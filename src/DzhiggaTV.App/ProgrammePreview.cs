using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DzhiggaTV.Core;

namespace DzhiggaTV.App;

internal sealed class ProgrammePreview
{
    public Border Panel { get; }
    private readonly MainWindow _main;
    private readonly FrameworkElement _current, _next, _host;
    private readonly DispatcherTimer _open = new() { Interval = TimeSpan.FromMilliseconds(420) };
    private readonly DispatcherTimer _close = new() { Interval = TimeSpan.FromMilliseconds(240) };
    private readonly Button _currentTab, _nextTab;
    private readonly TextBlock _title = new() { FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _time = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 7, 0, 0) };
    private readonly TextBlock _category = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 0) };
    private readonly TextBlock _description = new() { FontSize = 13, LineHeight = 20, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 6, 2) };
    private readonly ScrollViewer _scroll, _metadataScroll;
    private readonly ColumnDefinition _metadataColumn = new() { Width = new(250) };
    private bool _showNext, _dismissed, _keyboardInteraction;
    private Programme? _shown;

    public ProgrammePreview(MainWindow main, FrameworkElement current, FrameworkElement next, FrameworkElement host)
    {
        _main = main; _current = current; _next = next; _host = host;
        // Reuse the existing programme area without changing the video layout.
        Panel = new() { CornerRadius = new(10), BorderThickness = new(1), Padding = new(14, 10, 14, 12), Visibility = Visibility.Collapsed };
        Panel.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); Panel.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        System.Windows.Automation.AutomationProperties.SetName(Panel, "Полное описание передачи под видео");
        var layout = new Grid(); layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new()); Panel.Child = layout;
        var header = new Grid(); header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        _currentTab = SmallButton("Сейчас в эфире", "Описание текущей передачи"); _nextTab = SmallButton("Далее", "Описание следующей передачи");
        _currentTab.Click += (_, _) => Switch(false); _nextTab.Click += (_, _) => Switch(true);
        tabs.Children.Add(_currentTab); tabs.Children.Add(_nextTab); header.Children.Add(tabs);
        var guide = SmallButton("Программа  →", "Открыть телепрограмму");
        guide.Click += async (_, _) => { Hide(); await main.ShowGuideAsync(); }; Grid.SetColumn(guide, 1); header.Children.Add(guide);
        var dismiss = SmallButton("×", "Свернуть описание (Esc)"); dismiss.Width = 28; dismiss.FontSize = 18; dismiss.Padding = new(0); dismiss.Margin = new(8, 0, 0, 0);
        dismiss.Click += (_, _) => Hide(); Grid.SetColumn(dismiss, 2); header.Children.Add(dismiss); layout.Children.Add(header);
        var body = new Grid { Margin = new(0, 10, 0, 0) };
        body.ColumnDefinitions.Add(_metadataColumn); body.ColumnDefinitions.Add(new() { Width = new(20) }); body.ColumnDefinitions.Add(new());
        var metadata = new StackPanel(); metadata.Children.Add(_title); metadata.Children.Add(_time); metadata.Children.Add(_category);
        foreach (var block in new[] { _time, _category }) block.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _metadataScroll = new() { Content = metadata, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        body.Children.Add(_metadataScroll);
        _scroll = new() { Content = _description, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(_scroll, 2); body.Children.Add(_scroll); Grid.SetRow(body, 1); layout.Children.Add(body);
        Panel.SizeChanged += (_, _) => _metadataColumn.Width = new(Panel.ActualWidth < 650 ? 180 : 250);
        current.MouseEnter += (_, _) => Begin(false); next.MouseEnter += (_, _) => Begin(true);
        current.MouseLeave += (_, _) => ScheduleClose(); next.MouseLeave += (_, _) => ScheduleClose();
        current.GotKeyboardFocus += (_, _) => { _keyboardInteraction = Keyboard.IsKeyDown(Key.Tab); Begin(false); };
        next.GotKeyboardFocus += (_, _) => { _keyboardInteraction = Keyboard.IsKeyDown(Key.Tab); Begin(true); };
        Panel.MouseEnter += (_, _) => _close.Stop(); Panel.MouseLeave += (_, _) => ScheduleClose();
        Panel.PreviewMouseDown += (_, _) => _keyboardInteraction = false;
        Panel.PreviewKeyDown += (_, _) => _keyboardInteraction = true;
        Panel.LostKeyboardFocus += (_, _) => ScheduleClose();
        host.MouseLeave += (_, _) => { _dismissed = false; ScheduleClose(); };
        _open.Tick += (_, _) => { _open.Stop(); if (current.IsMouseOver || next.IsMouseOver || current.IsKeyboardFocusWithin || next.IsKeyboardFocusWithin) Show(); };
        _close.Tick += (_, _) => { _close.Stop(); if (!host.IsMouseOver && (!Panel.IsKeyboardFocusWithin || !_keyboardInteraction)) Hide(false); };
        main.Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Active)) Hide();
            else if (Panel.IsVisible && e.PropertyName is nameof(MainViewModel.CurrentTitle) or nameof(MainViewModel.NextTitle)) Refresh();
        };
        main.Deactivated += (_, _) => Hide(); main.Closed += (_, _) => Hide();
    }

    private static Button SmallButton(string text, string tip) => new()
    { Content = text, ToolTip = tip, Height = 26, FontSize = 11, Padding = new(8, 2, 8, 2), Margin = new(0, 0, 4, 0), Background = Brushes.Transparent, BorderThickness = new(0), VerticalAlignment = VerticalAlignment.Center };

    private void Begin(bool next)
    {
        if (_dismissed) return;
        _showNext = next; _close.Stop(); _open.Stop();
        if (Panel.IsVisible) Refresh(); else _open.Start();
    }
    private void Switch(bool next)
    {
        if ((next ? _main.Model.Active?.Next : _main.Model.Active?.Current) is null) return;
        _showNext = next; _shown = null; Refresh();
    }
    private void ScheduleClose() { _open.Stop(); _close.Stop(); _close.Start(); }
    private void Show()
    {
        if (_dismissed || _main.IsFullscreen || _main.IsMini || (_showNext ? _main.Model.Active?.Next : _main.Model.Active?.Current) is null) return;
        Refresh(); Panel.Visibility = Visibility.Visible;
        if (!_main.Model.Flag("reducedMotion") && SystemParameters.ClientAreaAnimation)
            Panel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }
    private void Refresh()
    {
        var programme = _showNext ? _main.Model.Active?.Next : _main.Model.Active?.Current;
        if (programme is null) { Hide(); return; }
        _currentTab.IsEnabled = _main.Model.Active?.Current is not null; _nextTab.IsEnabled = _main.Model.Active?.Next is not null;
        foreach (var (button, selected) in new[] { (_currentTab, !_showNext), (_nextTab, _showNext) })
        {
            button.SetResourceReference(Control.BackgroundProperty, selected ? "CardBrush" : "PanelBrush");
            button.SetResourceReference(Control.ForegroundProperty, selected ? "AccentBrush" : "MutedBrush");
            System.Windows.Automation.AutomationProperties.SetHelpText(button, selected ? "Выбрано" : "Показать описание");
        }
        if (_shown == programme) return;
        _shown = programme; _title.Text = programme.Title;
        _time.Text = $"{programme.Start.ToLocalTime():dd.MM · HH:mm} — {programme.End.ToLocalTime():HH:mm}";
        _category.Text = programme.Category?.Trim() ?? ""; _category.Visibility = _category.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _description.Text = string.IsNullOrWhiteSpace(programme.Description) ? "Подробного описания в телепрограмме нет." : programme.Description.Trim();
        _scroll.ScrollToHome(); _metadataScroll.ScrollToHome();
    }
    public void Hide() => Hide(true);
    private void Hide(bool dismissed)
    {
        _dismissed = dismissed && _host.IsMouseOver; _open.Stop(); _close.Stop(); Panel.Visibility = Visibility.Collapsed; _shown = null;
    }
}
