using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DzhiggaTV.Player;

namespace DzhiggaTV.App;

public partial class OverlayWindow : Window
{
    private readonly MainWindow _main;
    private DateTime _lastMove = DateTime.UtcNow;
    private Point _lastPoint;
    private bool _pointerInside, _menuOpen, _keyboardNavigation;
    public OverlayWindow(MainWindow main)
    {
        _main = main; InitializeComponent();
        VolumeSlider.Value = main.Player.Volume;
        Loaded += (_, _) => { RefreshHoverState(); UpdateVisibility(); };
        SizeChanged += (_, _) => { UpdateCompactLayout(); RefreshHoverState(); UpdateVisibility(); };
        IsVisibleChanged += (_, _) => { RefreshHoverState(); UpdateVisibility(); };
    }
    public void Reveal()
    {
        // Playback changes and owner shortcuts also call Reveal. They must not show
        // controls while the pointer is over the channel list or another window.
        RefreshHoverState();
        if (_pointerInside) _lastMove = DateTime.UtcNow;
        UpdateVisibility();
    }
    public void RefreshState()
    {
        Welcome.Visibility = _main.HasPlayback ? Visibility.Collapsed : Visibility.Visible;
        PauseButton.Content = _main.Player.IsPaused ? "▶" : "Ⅱ";
        MuteButton.Content = _main.Player.IsMuted ? "⊘" : "♪";
        UpdateVisibility();
    }
    public void Tick()
    {
        RefreshHoverState(); RefreshState();
        if (StatisticsPanel.Visibility == Visibility.Visible) UpdateStatistics();
        UpdateCompactLayout();
    }
    private void RefreshHoverState()
    {
        if (!IsVisible || (!OverlayRoot.IsMouseOver && !_menuOpen)) { _pointerInside = false; return; }
        // The overlay is a separate owned HWND. Use coordinates as well as enter /
        // leave events so moving/resizing the owner never leaves a stale hover flag.
        var point = Mouse.GetPosition(OverlayRoot);
        _pointerInside = point.X >= 0 && point.Y >= 0 && point.X < OverlayRoot.ActualWidth && point.Y < OverlayRoot.ActualHeight;
    }
    private void UpdateVisibility()
    {
        if (ControlsPanel is null) return;
        // A child of ControlsPanel shares all pointer, idle, menu and keyboard visibility rules.
        FullscreenProgrammeInfo.Visibility = _main.IsFullscreen && _main.Model.Active?.Current is not null ? Visibility.Visible : Visibility.Collapsed;
        var keyboardControls = _keyboardNavigation && ControlsPanel.IsKeyboardFocusWithin;
        var idle = (_main.IsFullscreen || _main.IsMini) && _main.HasPlayback &&
            Drawer.Visibility != Visibility.Visible && !_menuOpen && !keyboardControls &&
            DateTime.UtcNow - _lastMove > TimeSpan.FromSeconds(3.5);
        ControlsPanel.Visibility = _menuOpen || keyboardControls || (_pointerInside && !idle) ? Visibility.Visible : Visibility.Collapsed;
        Cursor = _pointerInside && idle ? Cursors.None : Cursors.Arrow;
    }
    private void UpdateCompactLayout()
    {
        var compact = ActualWidth <= 480;
        HistoryButton.Visibility = FavoriteButton.Visibility = MiniButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        VolumeSlider.Visibility = ActualWidth < 340 ? Visibility.Collapsed : Visibility.Visible;
    }
    private void Overlay_MouseEnter(object sender, MouseEventArgs e)
    {
        _pointerInside = true; _lastMove = DateTime.UtcNow; UpdateVisibility();
    }
    private void Overlay_MouseLeave(object sender, MouseEventArgs e)
    {
        _pointerInside = false; UpdateVisibility();
    }
    private void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(this);
        if ((point - _lastPoint).Length <= 2) return;
        _lastPoint = point; _keyboardNavigation = false; Reveal();
    }
    private void Overlay_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _keyboardNavigation = false;
    }
    private void Overlay_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(UpdateVisibility));
    }
    private void Overlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Reveal();
        if (e.OriginalSource is not Grid) return;
        if (e.ClickCount == 2) _main.ToggleFullscreen();
        else if (_main.IsMini && e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private async void Overlay_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
        {
            _keyboardNavigation = true;
            if (!ControlsPanel.IsKeyboardFocusWithin)
            {
                ControlsPanel.Visibility = Visibility.Visible;
                ControlsPanel.MoveFocus(new TraversalRequest(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? FocusNavigationDirection.Last : FocusNavigationDirection.First));
                e.Handled = true;
            }
            UpdateVisibility();
            return;
        }
        await _main.HandleKeyAsync(e);
    }
    private async void Import_Click(object sender, RoutedEventArgs e) => await _main.ImportPlaylistAsync();
    private async void Previous_Click(object sender, RoutedEventArgs e) => await _main.PreviousChannelAsync();
    private async void ChannelBack_Click(object sender, RoutedEventArgs e) => await _main.NextChannelAsync(-1);
    private async void ChannelNext_Click(object sender, RoutedEventArgs e) => await _main.NextChannelAsync(1);
    private void Pause_Click(object sender, RoutedEventArgs e) { _main.Player.TogglePause(); RefreshState(); }
    private void Mute_Click(object sender, RoutedEventArgs e) { _main.Player.IsMuted = !_main.Player.IsMuted; RefreshState(); }
    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (_main is not null) _main.Player.Volume = e.NewValue; }
    private async void Favorite_Click(object sender, RoutedEventArgs e) { if (_main.Model.Active is { } c) await _main.Model.ToggleFavoriteAsync(c); }
    private void Mini_Click(object sender, RoutedEventArgs e) => _main.ToggleMini();
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => _main.ToggleFullscreen();
    private void Channels_Click(object sender, RoutedEventArgs e) => ToggleDrawer();
    public void ToggleDrawer() { Drawer.Visibility = Drawer.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; Reveal(); }
    private async void Drawer_Selected(object sender, SelectionChangedEventArgs e) { if (sender is ListBox { SelectedItem: ChannelItem c }) { await _main.PlayAsync(c); Drawer.Visibility = Visibility.Collapsed; Reveal(); } }
    public void ToggleStatistics() { StatisticsPanel.Visibility = StatisticsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; UpdateStatistics(); Reveal(); }
    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        void Item(ItemsControl parent, string label, Action action, bool? check = null)
        { var entry = new MenuItem { Header = label, IsCheckable = check.HasValue, IsChecked = check ?? false }; entry.Click += (_, _) => action(); parent.Items.Add(entry); }
        var audio = _main.Player.GetTracks().Where(t => t.Type == "audio").ToArray();
        var subtitles = _main.Player.GetTracks().Where(t => t.Type == "sub").ToArray();
        if (audio.Length > 0)
        { var section = new MenuItem { Header = "Звуковая дорожка" }; foreach (var track in audio) Item(section, track.DisplayName, () => _main.Player.SelectAudio(track.Id), track.Selected); menu.Items.Add(section); }
        if (subtitles.Length > 0)
        { var section = new MenuItem { Header = "Субтитры" }; Item(section, "Выключены", () => _main.Player.SelectSubtitle(null), !subtitles.Any(t => t.Selected)); foreach (var track in subtitles) Item(section, track.DisplayName, () => _main.Player.SelectSubtitle(track.Id), track.Selected); menu.Items.Add(section); }
        var aspect = new MenuItem { Header = "Соотношение сторон" };
        foreach (var (label, mode) in new[] { ("По источнику", AspectMode.Original), ("16:9", AspectMode.Fit16By9), ("4:3", AspectMode.Fit4By3), ("Растянуть", AspectMode.Stretch) })
            Item(aspect, label, () => { _main.Player.SetAspect(mode); _main.Model.SetSetting("aspect", mode); });
        menu.Items.Add(aspect);
        menu.Items.Add(new Separator());
        Item(menu, "Предыдущий просмотренный (Backspace)", async () => await _main.PreviousChannelAsync());
        Item(menu, _main.Model.Active?.IsFavorite == true ? "Убрать из избранного (F)" : "Добавить в избранное (F)", async () => { if (_main.Model.Active is { } c) await _main.Model.ToggleFavoriteAsync(c); });
        Item(menu, "Мини-плеер (Ctrl+M)", _main.ToggleMini);
        menu.Items.Add(new Separator());
        Item(menu, "Техническая статистика (I)", ToggleStatistics);
        Item(menu, "Телепрограмма (G)", async () => await _main.ShowGuideAsync());
        Item(menu, "Настройки", async () => await _main.ShowSettingsAsync());
        if (_main.IsMini || _main.IsFullscreen) Item(menu, "Вернуться в обычное окно", _main.RestoreWindow);
        menu.Opened += (_, _) => { _menuOpen = true; UpdateVisibility(); };
        menu.Closed += (_, _) => { _menuOpen = false; RefreshHoverState(); UpdateVisibility(); };
        menu.PlacementTarget = sender as Button; menu.IsOpen = true; Reveal();
    }
    private void UpdateStatistics()
    {
        if (StatisticsText is null) return;
        var s = _main.Player.GetStatistics();
        static string V(object? value) => value is null ? "недоступно" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!;
        var text = new StringBuilder();
        void Line(string name, object? value) => text.AppendLine($"{name,-19} {V(value)}");
        Line("Канал", _main.Model.Active?.Name ?? "Локальное медиа");
        Line("Разрешение", s.Width is { } w && s.Height is { } h ? $"{w} × {h}" : null);
        Line("FPS", s.FramesPerSecond?.ToString("0.###")); Line("Видеокодек", s.VideoCodec); Line("Аудиокодек", s.AudioCodec);
        Line("Декодер", s.VideoDecoder); Line("Аппаратный декодер", s.HardwareDecoder);
        Line("Видеобитрейт", s.VideoBitrate is { } bitrate ? $"{bitrate / 1_000_000:0.00} Мбит/с" : null);
        Line("Буфер, секунды", s.BufferedSeconds?.ToString("0.0")); Line("Пропущено кадров", s.DroppedFrames);
        Line("Источник", s.SourceDynamicRange); Line("Transfer / primaries", $"{V(s.SourceTransfer)} / {V(s.SourcePrimaries)}");
        Line("Вывод transfer", s.TargetTransfer); Line("Вывод primaries", s.TargetPrimaries); Line("Видеовывод", s.VideoOutput); Line("Аудиодорожки", s.AudioTrackCount);
        text.AppendLine("HDR дисплея: требуется проверка Windows/монитора.");
        Line("libmpv", s.MpvVersion); StatisticsText.Text = text.ToString();
    }
}
