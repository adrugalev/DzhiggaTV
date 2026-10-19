using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
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
    private readonly TextBlock _badge = new() { FontSize = 10, Margin = new(0, 0, 0, 7) };
    private readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _time = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 0) };
    private readonly ProgrammeDescriptionView _description = new() { Margin = new(0, 14, 6, 2) };
    private readonly ScrollViewer _scroll;
    private readonly Popup _popup = new()
    {
        Placement = PlacementMode.Custom, AllowsTransparency = true, StaysOpen = true
    };
    private readonly DispatcherTimer _close = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private int _index;
    private bool _keyboardInteraction;
    private Programme? _shown;

    public ProgrammePreview(MainWindow main, Button current, Button next, Button afterNext)
    {
        _main = main; _buttons = [current, next, afterNext];
        // Keep each popup over its own card and its trigger exposed. Explicit placement
        // also avoids the system menu-alignment preference moving a popup to another card.
        _popup.CustomPopupPlacementCallback = (size, target, _) =>
        {
            double left = _index == 0 ? target.Width - size.Width : 0;
            return [new(new(left, -size.Height - 4), PopupPrimaryAxis.Horizontal), new(new(left, target.Height + 4), PopupPrimaryAxis.Horizontal)];
        };
        Panel = new() { CornerRadius = new(10), BorderThickness = new(1), Padding = new(14, 12, 14, 12), Visibility = Visibility.Collapsed };
        Panel.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); Panel.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        Panel.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
        System.Windows.Automation.AutomationProperties.SetName(Panel, "Полное описание передачи");
        var body = new StackPanel();
        foreach (var block in new[] { _badge, _title, _time }) body.Children.Add(block);
        _badge.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        _time.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); body.Children.Add(_description);
        _scroll = new() { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Panel.Child = _scroll; _popup.Child = Panel;
        Panel.MouseEnter += (_, _) => { _keyboardInteraction = false; _close.Stop(); };
        Panel.MouseLeave += (_, _) => ScheduleClose();
        Panel.PreviewMouseDown += (_, _) => _keyboardInteraction = false;
        Panel.LostKeyboardFocus += (_, _) => ScheduleClose();
        Panel.LostMouseCapture += (_, _) => ScheduleClose();
        Panel.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Hide(); e.Handled = true; } };
        _close.Tick += (_, _) =>
        {
            _close.Stop();
            var trigger = _buttons[_index];
            if (!Panel.IsMouseOver && !trigger.IsMouseOver && !Panel.IsMouseCaptureWithin &&
                !(_keyboardInteraction && (Panel.IsKeyboardFocusWithin || trigger.IsKeyboardFocusWithin))) Hide();
        };
        for (int i = 0; i < _buttons.Length; i++) Attach(_buttons[i], i);
        main.Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Active)) Hide();
            if (e.PropertyName is nameof(MainViewModel.CurrentDescription) or nameof(MainViewModel.NextDescription) or nameof(MainViewModel.AfterNextDescription)) RefreshAvailability();
            if (Panel.IsVisible && e.PropertyName is nameof(MainViewModel.CurrentTitle) or nameof(MainViewModel.NextTitle) or nameof(MainViewModel.AfterNextTitle)) Refresh();
        };
        main.Deactivated += (_, _) => Hide(); main.Closed += (_, _) => Hide();
        main.LocationChanged += (_, _) => Hide(); main.SizeChanged += (_, _) => Hide();
        RefreshAvailability();
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

    private void RefreshAvailability()
    {
        SetAvailability(0, _main.Model.CurrentDescription.Length > 0);
        SetAvailability(1, _main.Model.NextDescription.Length > 0);
        SetAvailability(2, _main.Model.AfterNextDescription.Length > 0);
    }

    private void SetAvailability(int index, bool available)
    {
        // Keep the current footer height stable when its description is absent.
        _buttons[index].Visibility = available ? Visibility.Visible : index == 0 ? Visibility.Hidden : Visibility.Collapsed;
        if (!available && Panel.IsVisible && index == _index) Hide();
    }

    private Programme? GetProgramme(int index) => index switch { 0 => _main.Model.Active?.Current, 1 => _main.Model.Active?.Next, _ => _main.Model.Active?.AfterNext };

    private void Show(int index)
    {
        var button = _buttons[index];
        if (!button.IsVisible || _main.IsFullscreen || _main.IsMini || GetProgramme(index) is null) return;
        _close.Stop();
        bool animate = !Panel.IsVisible;
        // WPF can retain the previous position when equally sized previews change target.
        if (_index != index) _popup.IsOpen = false;
        _index = index; _popup.PlacementTarget = button;
        Panel.Width = Math.Clamp(_main.ProgrammePanel.ActualWidth / 2 - 36, 240, 440);
        Panel.MaxHeight = Math.Clamp(_main.ActualHeight * .44, 180, 380);
        Refresh(); Panel.Visibility = Visibility.Visible;
        _popup.IsOpen = true;
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
        _description.Show(programme);
        _scroll.ScrollToHome();
    }

    public void Hide() { _close.Stop(); _popup.IsOpen = false; Panel.Visibility = Visibility.Collapsed; _shown = null; _keyboardInteraction = false; }
}
