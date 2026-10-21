using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DzhiggaTV.Player;
using DzhiggaTV.Core;

namespace DzhiggaTV.App;

public partial class OverlayWindow : Window
{
    private readonly MainWindow _main;
    private DateTime _lastMove = DateTime.UtcNow;
    private readonly PointerRevealGate _pointerReveal = new();
    private bool _pointerInside, _menuOpen, _keyboardNavigation;
    private Programme? _descriptionProgramme;
    private bool _volumeReady, _syncingVolume;
    private bool _syncingSeek, _seeking, _mouseSeeking;
    private ArchiveTimeline? _archiveTimeline;
    private string? _seekChannelId;
    private (double Width, double Height, Visibility Metadata)? _compactLayout;
    public OverlayWindow(MainWindow main)
    {
        _main = main;
        var volume = main.Player.Volume;
        InitializeComponent();
        PlaybackStatus.RetryRequested += async (_, _) => await _main.GuardAsync(async () =>
        {
            if (_main.IsArchivePlayback) await _main.TogglePlaybackPauseAsync();
            else if (_main.Model.Active is { } channel) await _main.PlayAsync(channel);
        });
        // Slider's move-to-point class handler marks track presses handled before XAML handlers run.
        ArchiveSeekSlider.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(ArchiveSeek_Down), true);
        ArchiveSeekSlider.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(ArchiveSeek_Up), true);
        VolumeSlider.Value = volume; _volumeReady = true;
        Loaded += (_, _) => { RefreshHoverState(); UpdateVisibility(); };
        SizeChanged += (_, _) => { UpdateCompactLayout(); RefreshHoverState(); UpdateVisibility(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) CancelSeek(); RefreshHoverState(); UpdateVisibility(); };
        ControlsPanel.SizeChanged += (_, _) => FullscreenProgrammeInfo.Margin = new(16, 0, 0, Math.Max(48, ControlsPanel.ActualHeight + 12));
    }
    public void Reveal()
    {
        // Playback changes and owner shortcuts also call Reveal. They must not show
        // controls while the pointer is over the channel list or another window.
        RefreshHoverState();
        _pointerReveal.Reset(Mouse.GetPosition(OverlayRoot));
        if (_pointerInside) _lastMove = DateTime.UtcNow;
        UpdateVisibility();
    }
    public void PointerMoved()
    {
        if (!_pointerReveal.Moved(Mouse.GetPosition(OverlayRoot), _main.IsFullscreen ? PointerRevealGate.FullscreenDistance : 2)) return;
        _keyboardNavigation = false;
        Reveal();
    }
    public void RefreshState()
    {
        if (_descriptionProgramme != _main.Model.PlayingProgramme)
        {
            _descriptionProgramme = _main.Model.PlayingProgramme;
            FullscreenSynopsisScroll.ScrollToHome(); FullscreenMetadataScroll.ScrollToHome();
        }
        Welcome.Visibility = _main.HasPlayback || _main.Player.State == PlaybackState.Error ? Visibility.Collapsed : Visibility.Visible;
        var status = _main.Player.Status;
        PlaybackStatus.ShowState(status.State, status.Message, _main.Model.Active is not null,
            _main.Model.Flag("reducedMotion"), _main.Player.RequiresRestart);
        PauseButton.Content = _main.Player.IsPaused || _main.Player.State is PlaybackState.Idle or PlaybackState.Error ? "▶" : "Ⅱ";
        MuteButton.Content = _main.Player.IsMuted ? "⊘" : "♪";
        if (!VolumeSlider.IsMouseCaptureWithin)
        {
            _syncingVolume = true;
            try { VolumeSlider.Value = _main.Player.Volume; }
            finally { _syncingVolume = false; }
        }
        RefreshArchiveTimeline();
        RemainingTimeValue.Text = _main.IsArchivePlayback && _archiveTimeline is { } archive
            ? $"{Math.Max(0, (int)Math.Ceiling((archive.End - archive.Position).TotalMinutes))} мин" : ProgrammeTiming.Remaining(_main.Model.PlayingProgramme, DateTimeOffset.UtcNow);
        RemainingTimeText.Visibility = RemainingTimeValue.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateVisibility();
    }
    public void Tick()
    {
        if (!IsVisible) return;
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
        var keyboardControls = _seeking || _keyboardNavigation && (ControlsPanel.IsKeyboardFocusWithin || FullscreenProgrammeInfo.IsKeyboardFocusWithin);
        var readingDescription = _pointerInside && FullscreenProgrammeReadingArea.IsMouseOver;
        var idle = (_main.IsFullscreen || _main.IsMini) && _main.HasPlayback &&
            Drawer.Visibility != Visibility.Visible && !_menuOpen && !keyboardControls && !readingDescription &&
            DateTime.UtcNow - _lastMove > TimeSpan.FromSeconds(3.5);
        ControlsPanel.Visibility = _menuOpen || keyboardControls || (_pointerInside && !idle) ? Visibility.Visible : Visibility.Collapsed;
        if (ControlsPanel.Visibility != Visibility.Visible) HideSeekPreview();
        MiniWindowActions.Visibility = _main.IsMini ? ControlsPanel.Visibility : Visibility.Collapsed;
        MiniPinButton.Foreground = (System.Windows.Media.Brush)FindResource(_main.Topmost ? "AccentBrush" : "TextBrush");
        MiniPinButton.ToolTip = _main.Topmost ? "Поверх всех окон · выключить" : "Закрепить поверх всех окон";
        System.Windows.Automation.AutomationProperties.SetHelpText(MiniPinButton, _main.Topmost ? "Закреплён поверх окон" : "Без закрепления");
        FullscreenProgrammeInfo.Visibility = ControlsPanel.Visibility == Visibility.Visible && _main.IsFullscreen && _main.Model.PlayingProgramme is not null ? Visibility.Visible : Visibility.Collapsed;
        Cursor = _pointerInside && idle ? Cursors.None : Cursors.Arrow;
    }
    private void UpdateCompactLayout()
    {
        var layout = (ActualWidth, ActualHeight, FullscreenMetadataScroll.Visibility);
        if (_compactLayout == layout) return;
        _compactLayout = layout;
        FullscreenProgrammeInfo.Width = FullscreenProgrammeInfo.MaxWidth = Math.Max(0, ActualWidth * .35);
        FullscreenSynopsisScroll.MaxHeight = Math.Max(30, Math.Min(180, ActualHeight * .20));
        FullscreenMetadataScroll.MaxHeight = Math.Max(24, Math.Min(110, ActualHeight * .12));
        FullscreenMetadataColumn.Width = FullscreenMetadataGap.Width = new(0);
        Grid.SetColumn(FullscreenMetadataScroll, 0); Grid.SetRow(FullscreenMetadataScroll, 1);
        FullscreenMetadataScroll.Margin = new(0, 12, 0, 0);
        var compact = ActualWidth <= 480;
        FavoriteButton.Visibility = MiniButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        VolumeSlider.Visibility = ActualWidth < 340 ? Visibility.Collapsed : Visibility.Visible;
        bool separateTime = ActualWidth < 700;
        Grid.SetRow(PlaybackButtons, separateTime ? 2 : 1); Grid.SetRow(PlayerActions, separateTime ? 2 : 1);
        Grid.SetColumn(RemainingTimeText, separateTime ? 0 : 1); Grid.SetColumnSpan(RemainingTimeText, separateTime ? 3 : 1);
        RemainingTimeText.Margin = separateTime ? new(8, 2, 8, 6) : new(12, 0, 12, 0);
    }
    private void Overlay_MouseEnter(object sender, MouseEventArgs e)
    {
        _pointerInside = true;
        if (_main.IsFullscreen) { PointerMoved(); UpdateVisibility(); }
        else Reveal();
    }
    private void Description_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scroll) return;
        _lastMove = DateTime.UtcNow;
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta / 120d * 66);
        e.Handled = true;
    }
    private void Overlay_MouseLeave(object sender, MouseEventArgs e)
    {
        _pointerInside = false; UpdateVisibility();
    }
    private void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        PointerMoved();
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
        else if (_main.IsMini && e.LeftButton == MouseButtonState.Pressed) _main.DragMove();
    }
    private async void Overlay_KeyDown(object sender, KeyEventArgs e)
    {
        if (ArchiveSeekSlider.IsKeyboardFocusWithin && IsSeekKey(e.Key)) return;
        if ((FullscreenSynopsisScroll.IsKeyboardFocusWithin || FullscreenMetadataScroll.IsKeyboardFocusWithin) &&
            e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End) return;
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
    private async void ChannelBack_Click(object sender, RoutedEventArgs e) => await _main.NextChannelAsync(-1);
    private async void ChannelNext_Click(object sender, RoutedEventArgs e) => await _main.NextChannelAsync(1);
    private async void Pause_Click(object sender, RoutedEventArgs e) { await _main.GuardAsync(_main.TogglePlaybackPauseAsync); RefreshState(); }
    private void RefreshArchiveTimeline()
    {
        if (_seeking && _seekChannelId != _main.Model.Active?.Id) CancelSeek();
        if (_seeking) return;
        _archiveTimeline = _main.GetArchiveTimeline();
        ArchiveTimelinePanel.Visibility = _archiveTimeline is null ? Visibility.Collapsed : Visibility.Visible;
        if (_archiveTimeline is not { } timeline) { HideSeekPreview(); return; }
        _syncingSeek = true;
        try { ArchiveSeekSlider.Maximum = Math.Max(1, (timeline.End - timeline.Start).TotalSeconds); ArchiveSeekSlider.Value = Math.Clamp((timeline.Position - timeline.Start).TotalSeconds, 0, ArchiveSeekSlider.Maximum); }
        finally { _syncingSeek = false; }
        ArchiveBadge.Text = timeline.IsArchive ? _main.Player.State == PlaybackState.Idle ? "АРХИВ · ЗАВЕРШЁН" : _main.Player.State == PlaybackState.Error ? "АРХИВ · НЕ УДАЛОСЬ ЗАГРУЗИТЬ" : "АРХИВ" : "ПРЯМОЙ ЭФИР";
        ReturnLiveButton.Visibility = timeline.IsArchive ? Visibility.Visible : Visibility.Collapsed;
        if (timeline.IsArchive) ShowSeekTime(timeline.Position);
        else ArchivePositionText.Text = $"{timeline.Start.ToLocalTime():HH:mm} — {timeline.End.ToLocalTime():HH:mm}";
        StartProgrammeAction.Visibility = !timeline.IsArchive && _main.CanRestartCurrentProgramme ? Visibility.Visible : Visibility.Collapsed;
        if (ArchiveSeekSlider.IsMouseOver) UpdateSeekPreview(Mouse.GetPosition(ArchiveSeekSlider));
    }
    private void ShowSeekTime(DateTimeOffset time)
    {
        if (_archiveTimeline is not { } timeline) return;
        ArchivePositionText.Text = $"{time.ToLocalTime():dd.MM · HH:mm:ss}   /   {timeline.End.ToLocalTime():HH:mm:ss}";
    }
    private void ArchiveSeek_Down(object sender, MouseButtonEventArgs e)
    {
        if (_archiveTimeline is null) return;
        e.Handled = true;
        ArchiveSeekSlider.Focus();
        _seeking = _mouseSeeking = true; _seekChannelId = _main.Model.Active?.Id; _lastMove = DateTime.UtcNow;
        if (!ArchiveSeekSlider.CaptureMouse()) { CancelSeek(); return; }
        UpdateSeekPreview(e.GetPosition(ArchiveSeekSlider), changeValue: true);
    }
    private async void ArchiveSeek_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_mouseSeeking) return;
        e.Handled = true;
        UpdateSeekPreview(e.GetPosition(ArchiveSeekSlider), changeValue: true);
        _mouseSeeking = false;
        ArchiveSeekSlider.ReleaseMouseCapture();
        if (!ArchiveSeekSlider.IsMouseOver) HideSeekPreview();
        await CommitSeekAsync();
    }
    private void ArchiveSeek_Move(object sender, MouseEventArgs e) => UpdateSeekPreview(e.GetPosition(ArchiveSeekSlider), _mouseSeeking);
    private void ArchiveSeek_Leave(object sender, MouseEventArgs e) { if (!_mouseSeeking) HideSeekPreview(); }
    private void ArchiveSeek_LostCapture(object sender, MouseEventArgs e) { if (_mouseSeeking) { CancelSeek(); RefreshState(); } }
    private void ArchiveSeek_LostFocus(object sender, KeyboardFocusChangedEventArgs e) { if (!_mouseSeeking) { CancelSeek(); RefreshState(); } }
    private void HideSeekPreview() { if (ArchiveSeekPreview is null) return; ArchiveSeekPreview.IsOpen = false; ArchiveSeekMarker.Visibility = Visibility.Collapsed; }
    private void CancelSeek()
    {
        _mouseSeeking = _seeking = false; _seekChannelId = null;
        if (ArchiveSeekSlider?.IsMouseCaptured == true) ArchiveSeekSlider.ReleaseMouseCapture();
        HideSeekPreview();
    }
    private void UpdateSeekPreview(Point point, bool changeValue = false)
    {
        if (_archiveTimeline is not { } timeline || ArchiveSeekSlider.Template.FindName("PART_Track", ArchiveSeekSlider) is not Track track) return;
        // Map absolute coordinates, independently of a thumb whose layout can lag behind rapid input.
        var value = timeline.OffsetAt(ArchiveSeekSlider.TranslatePoint(point, track).X, track.ActualWidth, track.Thumb.ActualWidth);
        value = Math.Clamp(value, ArchiveSeekSlider.Minimum, ArchiveSeekSlider.Maximum);
        if (changeValue) ArchiveSeekSlider.Value = value;
        var time = timeline.TimeAt(value).ToLocalTime();
        ArchiveSeekPreviewTime.Text = time.ToString("HH:mm:ss");
        ArchiveSeekPreviewDate.Text = time.ToString("dd.MM.yyyy");
        ArchiveSeekPreview.PlacementTarget = ArchiveSeekSlider;
        ArchiveSeekPreview.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        ArchiveSeekPreview.HorizontalOffset = Math.Clamp(point.X - ArchiveSeekPreview.DesiredSize.Width / 2, 0, Math.Max(0, ArchiveSeekSlider.ActualWidth - ArchiveSeekPreview.DesiredSize.Width));
        ArchiveSeekPreview.VerticalOffset = -ArchiveSeekPreview.DesiredSize.Height - 3;
        ArchiveSeekPreview.IsOpen = IsVisible && ControlsPanel.IsVisible;
        ArchiveSeekMarker.Margin = new(Math.Clamp(point.X, 11, Math.Max(11, ArchiveSeekSlider.ActualWidth - 11)), 0, 0, 0);
        ArchiveSeekMarker.Visibility = Visibility.Visible;
        _lastMove = DateTime.UtcNow;
    }
    private static bool IsSeekKey(Key key) => key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown;
    private void ArchiveSeek_KeyDown(object sender, KeyEventArgs e)
    {
        if (!IsSeekKey(e.Key) || _archiveTimeline is null) return;
        e.Handled = true; _seeking = true; _seekChannelId = _main.Model.Active?.Id; _lastMove = DateTime.UtcNow;
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || e.Key is Key.PageUp or Key.PageDown ? 60 : 10;
        ArchiveSeekSlider.Value = e.Key switch
        {
            Key.Home => ArchiveSeekSlider.Minimum, Key.End => ArchiveSeekSlider.Maximum,
            Key.Left or Key.PageDown => Math.Max(ArchiveSeekSlider.Minimum, ArchiveSeekSlider.Value - step),
            _ => Math.Min(ArchiveSeekSlider.Maximum, ArchiveSeekSlider.Value + step)
        };
    }
    private async void ArchiveSeek_KeyUp(object sender, KeyEventArgs e)
    { if (IsSeekKey(e.Key)) { e.Handled = true; await CommitSeekAsync(); } }
    private void ArchiveSeek_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (!_syncingSeek && _archiveTimeline is { } timeline) { if (!_seeking) _seekChannelId = _main.Model.Active?.Id; _seeking = true; ShowSeekTime(timeline.TimeAt(e.NewValue)); } }
    private async Task CommitSeekAsync()
    {
        if (!_seeking) return;
        var timeline = _archiveTimeline; var offset = ArchiveSeekSlider.Value;
        _seeking = false;
        if (timeline is not null && _seekChannelId == _main.Model.Active?.Id) await _main.GuardAsync(() => _main.SeekArchiveToAsync(timeline.TimeAt(offset)));
        _lastMove = DateTime.UtcNow; RefreshState();
    }
    private async void PlayerGuide_Click(object sender, RoutedEventArgs e) => await _main.ShowGuideAsync();
    private async void ReturnLive_Click(object sender, RoutedEventArgs e) => await _main.GuardAsync(_main.ReturnToLiveAsync);
    private async void StartProgramme_Click(object sender, RoutedEventArgs e) => await _main.GuardAsync(_main.RestartCurrentProgrammeAsync);
    private void Mute_Click(object sender, RoutedEventArgs e) { _main.Player.IsMuted = !_main.Player.IsMuted; RefreshState(); }
    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (_volumeReady && !_syncingVolume) _main.Player.Volume = e.NewValue; }
    private async void Favorite_Click(object sender, RoutedEventArgs e) { if (_main.Model.Active is { } c) await _main.Model.ToggleFavoriteAsync(c); }
    private void Mini_Click(object sender, RoutedEventArgs e) => _main.ToggleMini();
    private void MiniPin_Click(object sender, RoutedEventArgs e) { _main.ToggleMiniPin(); Reveal(); }
    private void MiniClose_Click(object sender, RoutedEventArgs e) => _main.Close();
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => _main.ToggleFullscreen();
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
