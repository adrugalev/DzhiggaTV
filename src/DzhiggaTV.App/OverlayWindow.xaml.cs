using L = DzhiggaTV.Localization.Texts;
using System.Text;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using DzhiggaTV.Player;
using DzhiggaTV.Core;

namespace DzhiggaTV.App;

public partial class OverlayWindow : Window
{
    private readonly MainWindow _main;
    private readonly RemoteQuickConnectView _remoteQuick;
    private DateTimeOffset _archiveNoticeUntil;
    public void ShowArchiveNotice(string message) { ArchiveNoticeText.Text = message; _archiveNoticeUntil = DateTimeOffset.UtcNow.AddSeconds(12); ArchiveNotice.Visibility = Visibility.Visible; }
    public void HideArchiveNotice() => ArchiveNotice.Visibility = Visibility.Collapsed;
    private void ArchiveNotice_Close(object sender, RoutedEventArgs e) => HideArchiveNotice();
    private DateTime _lastMove = DateTime.UtcNow;
    private readonly FullscreenChromeVisibility _fullscreenChrome = new();
    private readonly DispatcherTimer _fullscreenHideTimer = new(DispatcherPriority.Input);
    private readonly PointerRevealGate _pointerReveal = new();
    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPosition { public int X, Y; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out CursorPosition position);
    private Point _pointerPosition;
    private bool _pointerInside, _menuOpen, _keyboardNavigation;
    private ContextMenu? _playerMenu;
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
        _fullscreenHideTimer.Tick += (_, _) => { _fullscreenHideTimer.Stop(); RefreshHoverState(); UpdateVisibility(); };
        _remoteQuick = new(main); OverlayRoot.Children.Add(_remoteQuick); Panel.SetZIndex(_remoteQuick, 10);
        _remoteQuick.Dismissed += () => { if (IsVisible) Reveal(); };
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
        IsVisibleChanged += (_, _) => { if (!IsVisible) { CancelSeek(); ClosePlayerMenu(); _remoteQuick.Dismiss(); } RefreshHoverState(); UpdateVisibility(); };
        Closed += (_, _) => { _fullscreenHideTimer.Stop(); _remoteQuick.Dismiss(); ClosePlayerMenu(); };
        ControlsPanel.SizeChanged += (_, _) => FullscreenProgrammeInfo.Margin = new(16, 0, 0, Math.Max(48, ControlsPanel.ActualHeight + 12));
    }
    public void Reveal()
    {
        // Playback changes and owner shortcuts also call Reveal. They must not show
        // controls while the pointer is over the channel list or another window.
        RefreshHoverState();
        _pointerReveal.Reset(Mouse.GetPosition(OverlayRoot));
        if (_pointerInside) { _lastMove = DateTime.UtcNow; _fullscreenChrome.Reveal(_lastMove); }
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
        if (DateTimeOffset.UtcNow >= _archiveNoticeUntil) HideArchiveNotice();
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
            ? L.F("{0} мин", Math.Max(0, (int)Math.Ceiling((archive.End - archive.Position).TotalMinutes))) : ProgrammeTiming.Remaining(_main.Model.PlayingProgramme, DateTimeOffset.UtcNow);
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
        // WPF's cached position can remain over a button while the cursor is on
        // the native resize frame. Read the current screen position in that case too.
        var point = GetCursorPos(out var cursor) ? OverlayRoot.PointFromScreen(new(cursor.X, cursor.Y)) : Mouse.GetPosition(OverlayRoot);
        _pointerPosition = point;
        _pointerInside = point.X >= 0 && point.Y >= 0 && point.X < OverlayRoot.ActualWidth && point.Y < OverlayRoot.ActualHeight;
    }
    private bool PointerOver(FrameworkElement panel) => _pointerInside && panel.IsVisible &&
        new Rect(panel.TranslatePoint(new Point(), OverlayRoot), panel.RenderSize).Contains(_pointerPosition);
    private void UpdateVisibility()
    {
        if (ControlsPanel is null) return;
        _fullscreenHideTimer.Stop();
        var quickOpen = _remoteQuick.Visibility == Visibility.Visible;
        var keyboardControls = _seeking || _keyboardNavigation && (ControlsPanel.IsKeyboardFocusWithin || FullscreenProgrammeInfo.IsKeyboardFocusWithin || MiniWindowActions.IsKeyboardFocusWithin);
        var autoHide = (_main.IsFullscreen || _main.IsMini) && _main.HasPlayback;
        var held = Drawer.Visibility == Visibility.Visible || _menuOpen || quickOpen || keyboardControls ||
            ControlsPanel.IsMouseCaptureWithin || FullscreenProgrammeInfo.IsMouseCaptureWithin || MiniWindowActions.IsMouseCaptureWithin ||
            (PointerOver(ControlsPanel) || PointerOver(FullscreenProgrammeInfo) || _main.IsMini && PointerOver(MiniWindowActions));
        var remaining = _fullscreenChrome.Remaining(DateTime.UtcNow, held);
        var idle = autoHide && remaining == TimeSpan.Zero;
        var visible = quickOpen || _menuOpen || keyboardControls ||
            !idle && (_pointerInside || autoHide && ControlsPanel.IsVisible);
        ControlsPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (IsVisible && autoHide && visible && remaining is { } delay && delay > TimeSpan.Zero)
        {
            _fullscreenHideTimer.Interval = delay;
            _fullscreenHideTimer.Start();
        }
        RemoteButton.SetResourceReference(ForegroundProperty, _main.Remote.Running || quickOpen ? "AccentBrush" : "TextBrush");
        if (ControlsPanel.Visibility != Visibility.Visible) HideSeekPreview();
        MiniWindowActions.Visibility = _main.IsMini ? ControlsPanel.Visibility : Visibility.Collapsed;
        ToolTipService.SetIsEnabled(MiniPinButton, MiniWindowActions.IsVisible);
        ToolTipService.SetIsEnabled(MiniCloseButton, MiniWindowActions.IsVisible);
        MiniPinButton.Foreground = (System.Windows.Media.Brush)FindResource(_main.Topmost ? "AccentBrush" : "TextBrush");
        MiniPinButton.ToolTip = _main.Topmost ? L.T("Поверх всех окон · выключить") : L.T("Закрепить поверх всех окон");
        System.Windows.Automation.AutomationProperties.SetHelpText(MiniPinButton, _main.Topmost ? L.T("Закреплён поверх окон") : L.T("Без закрепления"));
        FullscreenProgrammeInfo.Visibility = ControlsPanel.Visibility == Visibility.Visible && _main.IsFullscreen && _main.Model.PlayingProgramme is not null ? Visibility.Visible : Visibility.Collapsed;
        Cursor = _pointerInside && idle ? Cursors.None : Cursors.Arrow;
    }
    private void UpdateCompactLayout()
    {
        var layout = (ActualWidth, ActualHeight, FullscreenMetadataScroll.Visibility);
        if (_compactLayout == layout) return;
        _compactLayout = layout;
        _remoteQuick.FitToViewport(ActualWidth, ActualHeight);
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
        if ((_main.IsFullscreen || _main.IsMini) && ControlsPanel.IsVisible) _fullscreenChrome.Reveal(DateTime.UtcNow);
        _pointerInside = false; UpdateVisibility();
    }
    private void Chrome_HoverChanged(object sender, MouseEventArgs e)
    {
        RefreshHoverState(); UpdateVisibility();
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
        if (_remoteQuick.IsMouseOver) return;
        if (ReferenceEquals(e.OriginalSource, OverlayRoot)) HideStreamInformation();
        if (e.OriginalSource is not Grid) return;
        if (e.ClickCount == 2) _main.ToggleFullscreen();
        else if (_main.IsMini && e.LeftButton == MouseButtonState.Pressed) _main.DragMove();
    }
    private void Overlay_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, OverlayRoot)) return;
        ShowVideoMenu(OverlayRoot.PointToScreen(e.GetPosition(OverlayRoot)));
        e.Handled = true;
    }
    private async void Overlay_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _remoteQuick.Visibility == Visibility.Visible) { _remoteQuick.Dismiss(); e.Handled = true; return; }
        if (_remoteQuick.IsKeyboardFocusWithin) return;
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
    private async void Remote_Click(object sender, RoutedEventArgs e)
    {
        if (_remoteQuick.Visibility == Visibility.Visible) _remoteQuick.Dismiss();
        else { ClosePlayerMenu(); Drawer.Visibility = Visibility.Collapsed; StatisticsPanel.Visibility = Visibility.Collapsed; await _remoteQuick.OpenAsync(); }
        UpdateVisibility();
    }
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
        ArchiveBadge.Text = timeline.IsArchive ? _main.Player.State == PlaybackState.Idle ? L.T("АРХИВ · ЗАВЕРШЁН") : _main.Player.State == PlaybackState.Error ? L.T("АРХИВ · НЕ УДАЛОСЬ ЗАГРУЗИТЬ") : L.T("АРХИВ") : L.T("ПРЯМОЙ ЭФИР");
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
    public void ToggleStreamInformation() { StatisticsPanel.Visibility = StatisticsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; UpdateStatistics(); Reveal(); }
    public void HideStreamInformation() => StatisticsPanel.Visibility = Visibility.Collapsed;
    private void More_Click(object sender, RoutedEventArgs e) => OpenPlayerMenu(MoreButton, PlacementMode.Top, default);
    public void ShowVideoMenu(Point screenPoint)
    {
        if (!IsVisible) return;
        OpenPlayerMenu(OverlayRoot, PlacementMode.RelativePoint, OverlayRoot.PointFromScreen(screenPoint));
    }
    private void ClosePlayerMenu() { if (_playerMenu is { } menu) menu.IsOpen = false; }
    private void OpenPlayerMenu(FrameworkElement anchor, PlacementMode placement, Point offset)
    {
        ClosePlayerMenu();
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = placement, HorizontalOffset = offset.X, VerticalOffset = offset.Y };
        void Item(ItemsControl parent, string label, Action action, bool? check = null)
        { var entry = new MenuItem { Header = label, IsCheckable = check.HasValue, IsChecked = check ?? false }; entry.Click += (_, _) => action(); parent.Items.Add(entry); }
        var audio = _main.Player.GetTracks().Where(t => t.Type == "audio").ToArray();
        var audioSection = new MenuItem { Header = L.T("Звуковая дорожка"), IsEnabled = audio.Length > 0 };
        foreach (var track in audio) Item(audioSection, track.DisplayName, () => _main.Player.SelectAudio(track.Id), track.Selected);
        menu.Items.Add(audioSection);
        var aspect = new MenuItem { Header = L.T("Соотношение сторон") };
        foreach (var (label, mode) in new[] { (L.T("По источнику"), AspectMode.Original), ("16:9", AspectMode.Fit16By9), ("4:3", AspectMode.Fit4By3), (L.T("Растянуть"), AspectMode.Stretch) })
            Item(aspect, label, () => { _main.Player.SetAspect(mode); _main.Model.SetSetting("aspect", mode); }, _main.Model.Setting("aspect", "Original") == mode.ToString());
        menu.Items.Add(aspect);
        var information = new MenuItem { Header = L.T("Информация о потоке"), InputGestureText = "I", IsCheckable = true, IsChecked = StatisticsPanel.Visibility == Visibility.Visible };
        information.Click += (_, _) => ToggleStreamInformation();
        menu.Items.Add(information);
        menu.Opened += (_, _) => { _menuOpen = true; UpdateVisibility(); };
        menu.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_playerMenu, menu)) return;
            _playerMenu = null; _menuOpen = false; RefreshHoverState(); UpdateVisibility();
        };
        _playerMenu = menu;
        menu.IsOpen = true; Reveal();
    }
    private void UpdateStatistics()
    {
        if (StatisticsText is null) return;
        var s = _main.Player.GetStatistics();
        static string V(object? value) => value is null ? L.T("недоступно") : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!;
        var text = new StringBuilder();
        void Line(string name, object? value) => text.AppendLine($"{name,-19} {V(value)}");
        Line(L.T("Канал"), _main.Model.Active?.Name ?? L.T("Локальное медиа"));
        Line(L.T("Разрешение"), s.Width is { } w && s.Height is { } h ? $"{w} × {h}" : null);
        Line("FPS", s.FramesPerSecond?.ToString("0.###")); Line(L.T("Видеокодек"), s.VideoCodec); Line(L.T("Аудиокодек"), s.AudioCodec);
        Line(L.T("Декодер"), s.VideoDecoder); Line(L.T("Аппаратный декодер"), s.HardwareDecoder);
        Line(L.T("Видеобитрейт"), s.VideoBitrate is { } bitrate ? L.F("{0:0.00} Мбит/с", bitrate / 1_000_000) : null);
        Line(L.T("Буфер, секунды"), s.BufferedSeconds?.ToString("0.0")); Line(L.T("Пропущено кадров"), s.DroppedFrames);
        Line(L.T("Источник"), s.SourceDynamicRange); Line("Transfer / primaries", $"{V(s.SourceTransfer)} / {V(s.SourcePrimaries)}");
        Line(L.T("Вывод transfer"), s.TargetTransfer); Line(L.T("Вывод primaries"), s.TargetPrimaries); Line(L.T("Видеовывод"), s.VideoOutput); Line(L.T("Аудиодорожки"), s.AudioTrackCount);
        text.AppendLine(L.T("HDR дисплея: требуется проверка Windows/монитора."));
        Line("libmpv", s.MpvVersion); StatisticsText.Text = text.ToString();
    }
}
