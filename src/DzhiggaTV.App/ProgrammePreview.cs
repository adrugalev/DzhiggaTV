using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DzhiggaTV.Core;

namespace DzhiggaTV.App;

internal sealed class ProgrammePreview
{
    public Border Panel { get; }
    private readonly MainWindow _main;
    private readonly FrameworkElement _current, _next;
    private readonly DispatcherTimer _open = new() { Interval = TimeSpan.FromMilliseconds(420) };
    private readonly DispatcherTimer _close = new() { Interval = TimeSpan.FromMilliseconds(850) };
    private readonly TextBlock _badge = new() { FontSize = 10 };
    private readonly TextBlock _title = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) };
    private readonly TextBlock _channel = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) };
    private readonly TextBlock _time = new() { FontSize = 12, Margin = new(0, 6, 0, 0) };
    private readonly TextBlock _category = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 0) };
    private readonly TextBlock _description = new() { FontSize = 13, LineHeight = 21, TextWrapping = TextWrapping.Wrap, Margin = new(0, 20, 5, 0) };
    private readonly ScrollViewer _scroll;
    private bool _showNext;
    private Programme? _shown;
    public ProgrammePreview(MainWindow main, FrameworkElement current, FrameworkElement next)
    {
        _main = main; _current = current; _next = next;
        Panel = new() { CornerRadius = new(12), BorderThickness = new(1), Margin = new(16, 20, 12, 8), Padding = new(16), Visibility = Visibility.Collapsed };
        Panel.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); Panel.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        System.Windows.Automation.AutomationProperties.SetName(Panel, "Полное описание передачи");
        var dock = new DockPanel(); Panel.Child = dock;
        var header = new Grid(); header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _badge.VerticalAlignment = VerticalAlignment.Center; _badge.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); header.Children.Add(_badge);
        var dismiss = new Button { Content = "×", ToolTip = "Закрыть описание (Esc)", Width = 28, Height = 28, Padding = new(0), Margin = new(0), FontSize = 19, Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new(0) };
        dismiss.Click += (_, _) => Hide(); Grid.SetColumn(dismiss, 1); header.Children.Add(dismiss); DockPanel.SetDock(header, Dock.Top); dock.Children.Add(header);
        var guide = new Button { Content = "Открыть телепрограмму  →", FontSize = 12, Padding = new(8, 8, 8, 8), Margin = new(0, 14, 0, 0) };
        guide.Click += async (_, _) => { Hide(); await main.ShowGuideAsync(); }; DockPanel.SetDock(guide, Dock.Bottom); dock.Children.Add(guide);
        var content = new StackPanel(); foreach (var block in new[] { _title, _channel, _time, _category, _description }) content.Children.Add(block);
        _channel.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        foreach (var block in new[] { _time, _category }) block.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _scroll = new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; dock.Children.Add(_scroll);
        current.MouseEnter += (_, _) => Begin(false); next.MouseEnter += (_, _) => Begin(true);
        current.MouseLeave += (_, _) => ScheduleClose(); next.MouseLeave += (_, _) => ScheduleClose();
        current.GotKeyboardFocus += (_, _) => Begin(false); next.GotKeyboardFocus += (_, _) => Begin(true);
        Panel.MouseEnter += (_, _) => _close.Stop(); Panel.MouseLeave += (_, _) => ScheduleClose();
        _open.Tick += (_, _) => { _open.Stop(); if (_current.IsMouseOver || _next.IsMouseOver || _current.IsKeyboardFocusWithin || _next.IsKeyboardFocusWithin) Show(); };
        _close.Tick += (_, _) => { _close.Stop(); if (!Panel.IsMouseOver && !_current.IsMouseOver && !_next.IsMouseOver && !Panel.IsKeyboardFocusWithin) Hide(); };
        main.Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Active)) Hide();
            else if (Panel.IsVisible && e.PropertyName is nameof(MainViewModel.CurrentTitle) or nameof(MainViewModel.NextTitle)) Refresh();
        };
        main.Deactivated += (_, _) => Hide(); main.Closed += (_, _) => Hide();
    }
    private void Begin(bool next)
    {
        _showNext = next; _close.Stop(); _open.Stop();
        if (Panel.IsVisible) Refresh(); else _open.Start();
    }
    private void ScheduleClose() { _open.Stop(); _close.Stop(); _close.Start(); }
    private void Show()
    {
        if (_main.IsFullscreen || _main.IsMini || (_showNext ? _main.Model.Active?.Next : _main.Model.Active?.Current) is null) return;
        Refresh(); Panel.Visibility = Visibility.Visible;
        if (!_main.Model.Flag("reducedMotion") && SystemParameters.ClientAreaAnimation)
            Panel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }
    private void Refresh()
    {
        var programme = _showNext ? _main.Model.Active?.Next : _main.Model.Active?.Current;
        if (programme is null) { Hide(); return; }
        if (_shown == programme) return;
        _shown = programme; _badge.Text = _showNext ? "ДАЛЕЕ" : "СЕЙЧАС В ЭФИРЕ";
        _title.Text = programme.Title; _channel.Text = _main.Model.ActiveName;
        _time.Text = $"{programme.Start.ToLocalTime():dd.MM · HH:mm} — {programme.End.ToLocalTime():HH:mm}";
        _category.Text = programme.Category?.Trim() ?? ""; _category.Visibility = _category.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _description.Text = string.IsNullOrWhiteSpace(programme.Description) ? "Подробного описания в телепрограмме нет." : programme.Description.Trim();
        _scroll.ScrollToHome();
    }
    public void Hide() { _open.Stop(); _close.Stop(); Panel.Visibility = Visibility.Collapsed; _shown = null; }
}
