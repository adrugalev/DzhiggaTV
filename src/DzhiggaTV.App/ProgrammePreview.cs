using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DzhiggaTV.Core;

namespace DzhiggaTV.App;

internal sealed class ProgrammePreview
{
    public Border Panel { get; }
    private readonly MainWindow _main;
    private readonly Button[] _buttons;
    private readonly TextBlock _currentDescription;
    private readonly TextBlock _badge = new() { FontSize = 10, Margin = new(0, 0, 0, 7) };
    private readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _time = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 0) };
    private readonly TextBlock _category = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 0) };
    private readonly TextBlock _description = new() { FontSize = 13, LineHeight = 20, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 6, 2) };
    private readonly ScrollViewer _scroll;
    private readonly ColumnDefinition _metadataColumn = new() { Width = new(230) };
    private readonly DispatcherTimer _close = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private int _index;
    private bool _measurementQueued, _keyboardInteraction;
    private Programme? _shown;

    public ProgrammePreview(MainWindow main, Button current, Button next, Button afterNext, TextBlock currentDescription)
    {
        _main = main; _buttons = [current, next, afterNext]; _currentDescription = currentDescription;
        // Upcoming previews use the left card, keeping both right-hand triggers exposed.
        // The current programme can use the full width, above its footer trigger.
        Panel = new() { CornerRadius = new(10), BorderThickness = new(1), Padding = new(14, 10, 14, 10), Margin = new(0, 0, 0, 34), Visibility = Visibility.Collapsed };
        Panel.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); Panel.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        System.Windows.Automation.AutomationProperties.SetName(Panel, "Полное описание передачи под видео");
        var body = new Grid(); body.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        body.ColumnDefinitions.Add(_metadataColumn); body.ColumnDefinitions.Add(new() { Width = new(18) }); body.ColumnDefinitions.Add(new());
        var metadata = new StackPanel();
        foreach (var block in new[] { _badge, _title, _time, _category }) metadata.Children.Add(block);
        _badge.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        foreach (var block in new[] { _time, _category }) block.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        body.Children.Add(metadata); body.Children.Add(_description);
        _scroll = new() { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Panel.Child = _scroll;
        Panel.SizeChanged += (_, _) =>
        {
            bool stacked = Panel.ActualWidth < 650;
            _metadataColumn.Width = new(stacked ? 0 : 230);
            body.ColumnDefinitions[1].Width = new(stacked ? 0 : 18);
            Grid.SetColumn(metadata, stacked ? 2 : 0);
            Grid.SetColumn(_description, 2); Grid.SetRow(_description, stacked ? 1 : 0);
            _description.Margin = new(0, stacked ? 8 : 0, 6, 2);
        };
        Panel.MouseEnter += (_, _) => { _keyboardInteraction = false; _close.Stop(); };
        Panel.MouseLeave += (_, _) => ScheduleClose();
        Panel.PreviewMouseDown += (_, _) => _keyboardInteraction = false;
        Panel.LostKeyboardFocus += (_, _) => ScheduleClose();
        Panel.LostMouseCapture += (_, _) => ScheduleClose();
        _close.Tick += (_, _) =>
        {
            _close.Stop();
            var trigger = _buttons[_index];
            if (!Panel.IsMouseOver && !trigger.IsMouseOver && !Panel.IsMouseCaptureWithin &&
                !(_keyboardInteraction && (Panel.IsKeyboardFocusWithin || trigger.IsKeyboardFocusWithin))) Hide();
        };
        for (int i = 0; i < _buttons.Length; i++) Attach(_buttons[i], i);
        currentDescription.Loaded += (_, _) => QueueMeasurement();
        currentDescription.SizeChanged += (_, _) => QueueMeasurement();
        currentDescription.IsVisibleChanged += (_, _) => QueueMeasurement();
        main.Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Active)) Hide();
            if (e.PropertyName is nameof(MainViewModel.CurrentDescription) or nameof(MainViewModel.NextDescription) or nameof(MainViewModel.AfterNextDescription)) QueueMeasurement();
            if (Panel.IsVisible && e.PropertyName is nameof(MainViewModel.CurrentTitle) or nameof(MainViewModel.NextTitle) or nameof(MainViewModel.AfterNextTitle)) Refresh();
        };
        main.Deactivated += (_, _) => Hide(); main.Closed += (_, _) => Hide();
    }

    private void Attach(Button button, int index)
    {
        button.MouseEnter += (_, _) => { _keyboardInteraction = false; Show(index); };
        button.MouseLeave += (_, _) => ScheduleClose();
        button.PreviewMouseDown += (_, _) => _keyboardInteraction = false;
        // Keyboard users can also hold focus on the same button to read the description.
        button.GotKeyboardFocus += (_, _) => { if (Keyboard.IsKeyDown(Key.Tab)) { _keyboardInteraction = true; Show(index); } };
        button.LostKeyboardFocus += (_, _) => ScheduleClose();
        button.PreviewMouseWheel += (_, e) =>
        {
            if (!Panel.IsVisible) return;
            double distance = e.Delta / 120d * 60;
            _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - distance);
            e.Handled = true;
        };
        button.PreviewKeyDown += (_, e) =>
        {
            if (!Panel.IsVisible) return;
            if (e.Key == Key.Down) _scroll.LineDown();
            else if (e.Key == Key.Up) _scroll.LineUp();
            else if (e.Key == Key.PageDown) _scroll.PageDown();
            else if (e.Key == Key.PageUp) _scroll.PageUp();
            else return;
            e.Handled = true;
        };
    }

    private void ScheduleClose()
    {
        // Allow the pointer to cross the small gap between the button and the description.
        if (!Panel.IsVisible) return;
        _close.Stop(); _close.Start();
    }

    private void QueueMeasurement()
    {
        if (_measurementQueued) return;
        _measurementQueued = true;
        _main.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _measurementQueued = false;
            SetAvailability(0, IsClipped(_currentDescription));
            // Compact upcoming cards omit the synopsis, so any description needs the preview.
            SetAvailability(1, _main.Model.NextDescription.Length > 0);
            SetAvailability(2, _main.Model.AfterNextDescription.Length > 0);
        });
    }

    private void SetAvailability(int index, bool available)
    {
        // Only the current synopsis depends on the footer space for its truncation measurement.
        _buttons[index].Visibility = available ? Visibility.Visible : index == 0 ? Visibility.Hidden : Visibility.Collapsed;
        if (!available && Panel.IsVisible && index == _index) Hide();
    }

    private static bool IsClipped(TextBlock source)
    {
        if (!source.IsVisible || string.IsNullOrWhiteSpace(source.Text) || source.ActualWidth <= 0) return false;
        // Measure the full text with the very same WPF line layout, without the two-line limit.
        var full = new TextBlock
        {
            Text = source.Text, FontFamily = source.FontFamily, FontSize = source.FontSize,
            FontWeight = source.FontWeight, FontStyle = source.FontStyle, FontStretch = source.FontStretch,
            Language = source.Language, FlowDirection = source.FlowDirection, Padding = source.Padding,
            TextWrapping = source.TextWrapping, LineHeight = source.LineHeight, LineStackingStrategy = source.LineStackingStrategy
        };
        full.Measure(new(source.ActualWidth, double.PositiveInfinity));
        return full.DesiredSize.Height > source.ActualHeight + .5 || full.DesiredSize.Width > source.ActualWidth + .5;
    }

    private Programme? GetProgramme(int index) => index switch { 0 => _main.Model.Active?.Current, 1 => _main.Model.Active?.Next, _ => _main.Model.Active?.AfterNext };

    private void Show(int index)
    {
        var button = _buttons[index];
        if (!button.IsVisible || _main.IsFullscreen || _main.IsMini || GetProgramme(index) is null) return;
        _close.Stop();
        bool animate = !Panel.IsVisible;
        _index = index; Grid.SetColumnSpan(Panel, index == 0 ? 3 : 1); Refresh(); Panel.Visibility = Visibility.Visible;
        if (animate && !_main.Model.Flag("reducedMotion") && SystemParameters.ClientAreaAnimation)
            Panel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(100)));
    }

    private void Refresh()
    {
        var programme = GetProgramme(_index);
        if (programme is null) { Hide(); return; }
        if (_shown == programme) return;
        _shown = programme; _badge.Text = _index switch { 0 => "СЕЙЧАС В ЭФИРЕ", 1 => "ДАЛЕЕ", _ => "ЗАТЕМ" };
        _title.Text = programme.Title;
        _time.Text = $"{programme.Start.ToLocalTime():dd.MM · HH:mm} — {programme.End.ToLocalTime():HH:mm}";
        _category.Text = programme.Category?.Trim() ?? ""; _category.Visibility = _category.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _description.Text = programme.Description?.Trim() ?? "";
        _scroll.ScrollToHome();
    }

    public void Hide() { _close.Stop(); Panel.Visibility = Visibility.Collapsed; _shown = null; _keyboardInteraction = false; }
}
