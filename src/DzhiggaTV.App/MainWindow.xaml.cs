using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using DzhiggaTV.Player;
using Microsoft.Win32;

namespace DzhiggaTV.App;

public partial class MainWindow : Window
{
    public const double TitleBarHeight = 48;
    public static GridLength TitleBarRowHeight { get; } = new(TitleBarHeight);
    public const double StatusBarHeight = 16;
    private const double MiniResizeBorder = 8;
    private const double MiniDefaultWidth = 440, MiniDefaultHeight = 255;
    public static GridLength StatusBarRowHeight { get; } = new(StatusBarHeight);
    public MainViewModel Model { get; }
    public ProgrammeBrowser Programmes { get; }
    public UpdateCoordinator Updates { get; }
    private readonly CancellationTokenSource _updateLifetime = new();
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromMinutes(15) };
    public MpvPlayer Player { get; } = new();
    public bool IsFullscreen { get; private set; }
    public bool IsMini { get; private set; }
    internal bool IsMiniSizing { get; set; }
    internal double MiniVideoAspectRatio { get; private set; } = 16d / 9;
    internal Thickness MiniResizeFrame => new(MiniResizeBorder);
    public bool HasPlayback { get; private set; }
    private OverlayWindow? _overlay;
    private SportsEveningWindow? _sports;
    private readonly ProgrammePreview _programmePreview;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _playCancellation;
    private readonly string[] _args;
    private WindowSnapshot? _snapshot;
    private bool _initialized, _engineReady, _modal, _closing, _shutdownComplete, _layoutQueued;
    private bool _navigationCollapsed = true;
    private Rect _overlayLayoutBounds = Rect.Empty;
    private Point _dragOrigin;
    private ChannelItem? _dragItem;
    private long _playGeneration;
    private record WindowSnapshot(double Left, double Top, double Width, double Height, WindowState State);

    public MainWindow(string[] args, UpdateCoordinator? updates = null)
    {
        Updates = updates ?? new();
        _args = args;
        var custom = Argument("--data-dir");
        Model = new(custom ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DzhiggaTV"));
        InitializeComponent(); ApplyMainWindowChrome(); WindowBounds.Attach(this); WindowAppearance.Attach(this); DataContext = Model; SetNavigationCollapsed(true);
        Programmes = new(Model); ProgrammePanel.DataContext = Programmes;
        _programmePreview = new(this, CurrentProgrammeDetails, NextProgrammeDetails, AfterNextProgrammeDetails);
        Programmes.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ProgrammeBrowser.CurrentDescription)) UpdateProgrammePanelLayout(); };
        ViewingPanel.SizeChanged += (_, _) => UpdateProgrammePanelLayout();
        var channelGroup = Model.SelectedGroup;
        Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedGroup) && Model.SelectedGroup != channelGroup)
            {
                channelGroup = Model.SelectedGroup;
                if (IsLoaded) ChannelListScroll.ResetToTop(ChannelList);
            }
            if (e.PropertyName == nameof(MainViewModel.SelectedChannel) && Model.SelectedChannel is { } selected)
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (Model.SelectedChannel == selected && Model.VisibleChannels.Contains(selected)) ChannelList.ScrollIntoView(selected); });
        };
        WindowChrome.SetIsHitTestVisibleInChrome(VersionButton, true);
        WindowChrome.SetIsHitTestVisibleInChrome(MinimizeButton, true);
        WindowChrome.SetIsHitTestVisibleInChrome(MaximizeButton, true);
        WindowChrome.SetIsHitTestVisibleInChrome(CloseButton, true);
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, workArea.Width); Height = Math.Min(Height, workArea.Height);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
        Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 2);
        VideoHost.HandleReady += (_, _) => InitializeEngine();
        VideoHost.DoubleClicked += (_, _) => ToggleFullscreen();
        VideoHost.PointerActivity += (_, e) =>
        {
            if (e.Kind == NativePointerKind.Move) _overlay?.PointerMoved(); else _overlay?.Reveal();
            if (e.Kind == NativePointerKind.LeftDown) _overlay?.HideStreamInformation();
            if (e.Kind == NativePointerKind.RightDown) _overlay?.ShowVideoMenu(new Point(e.ScreenX, e.ScreenY));
            if (e.Kind == NativePointerKind.Wheel) Player.Volume = Math.Clamp(Player.Volume + Math.Sign(e.WheelDelta) * 5, 0, 100);
        };
        Player.StateChanged += (_, e) => Dispatcher.BeginInvoke(() =>
        {
            if (_closing) return;
            e = Player.Status; // A delayed dispatcher callback must not restore an obsolete status.
            Model.PlaybackStatus = e.Message;
            if (e.State == PlaybackState.Error) Model.Status = e.Message;
            HasPlayback = e.State is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Buffering or PlaybackState.Stalled or PlaybackState.Reconnecting or PlaybackState.Loading || IsArchivePlayback && e.State is PlaybackState.Idle or PlaybackState.Error;
            if (e.State == PlaybackState.Idle && IsArchivePlayback) _ = GuardAsync(ContinueArchiveAsync);
            if (e.State == PlaybackState.Playing) RefreshMiniVideoAspect();
            _overlay?.RefreshState();
        });
        Loaded += async (_, _) => await LoadedAsync();
        Activated += async (_, _) => { if (_initialized && !_modal) await CheckUpdatesInBackgroundAsync(); };
        _updateTimer.Tick += async (_, _) => { await CheckUpdatesInBackgroundAsync(); await CleanUpdateCacheAsync(); };
        LayoutUpdated += (_, _) =>
        {
            if (!IsLoaded || _closing) return;
            var bounds = VideoArea.TransformToAncestor(this).TransformBounds(new Rect(VideoArea.RenderSize));
            if (_overlayLayoutBounds == bounds) return;
            _overlayLayoutBounds = bounds; QueueOverlayLayout();
        };
        LocationChanged += (_, _) => QueueOverlayLayout();
        DpiChanged += (_, _) => QueueOverlayLayout();
        StateChanged += (_, _) =>
        {
            UpdateWindowFrame();
            QueueOverlayLayout();
        };
        SizeChanged += (_, _) => QueueOverlayLayout();
        _timer.Tick += async (_, _) =>
        {
            ClockText.Text = DateTime.Now.ToString("HH:mm"); _overlay?.Tick();
            RefreshMiniVideoAspect();
            if (IsArchivePlayback && _overlay?.IsVisible != true) GetArchiveTimeline();
            if (_initialized && DateTime.Now.Second % 30 == 0) await GuardAsync(Model.UpdateNowNextAsync);
        };
        Closing += OnClosing;
        WindowState = WindowState.Maximized;
    }
    private string? Argument(string name) { var i = Array.IndexOf(_args, name); return i >= 0 && i + 1 < _args.Length ? _args[i + 1] : null; }
    private async Task LoadedAsync()
    {
        await GuardAsync(async () =>
        {
            await Model.InitializeAsync();
            InitializeRemote();
            ApplySettings(); _initialized = true; InitializeEngine();
            _overlay = new(this) { Owner = this, DataContext = Model }; _overlay.Show(); SyncOverlay(); _timer.Start();
            _updateTimer.Start(); _ = CheckUpdatesInBackgroundAsync(); _ = CleanUpdateCacheAsync();
            if (Argument("--playlist") is { } playlist) await Model.ImportAsync(playlist);
            if (Argument("--media") is { } media)
            {
                await PlayLocalAsync(media);
                if (Argument("--smoke-report") is { } report) _ = SmokeAsync(report, media);
            }
            else if ((Model.Flag("resume") || _args.Contains("--resume")) && Model.AllChannels.Find(c => c.Id == Model.Setting("last.channel") && Model.IsAllowed(c)) is { } last) await PlayAsync(last);
            await OfferEpgConnectionAsync();
            await LoadViewportAsync();
            if (Argument("--epg-report") is { } epgReport) _ = GuardAsync(() => VerifyEpgAsync(epgReport));
        });
    }
    private void InitializeEngine()
    {
        if (_engineReady || VideoHost.VideoHandle == IntPtr.Zero) return;
        try { Player.Initialize(VideoHost.VideoHandle); _engineReady = true; if (_initialized) ApplyPlayerSettings(); }
        catch { Model.PlaybackStatus = "Не удалось загрузить libmpv. Проверьте папку native и разрядность x64."; }
    }
    public async Task GuardAsync(Func<Task> action)
    {
        try { await action(); } catch (OperationCanceledException) { }
        catch { Model.Status = "Действие не завершено. Проверьте доступность файла или сети и повторите."; }
    }
    public async Task PlayAsync(ChannelItem item)
    {
        if (!_engineReady || !Model.IsAllowed(item)) return;
        ClearArchivePlayback();
        var generation = ++_playGeneration;
        _playCancellation?.Cancel(); _playCancellation?.Dispose(); _playCancellation = new();
        var token = _playCancellation.Token;
        HasPlayback = true; Model.PlaybackStatus = "Подключение к каналу…"; _overlay?.RefreshState();
        await GuardAsync(async () =>
        {
            await Model.ActivateChannelAsync(item);
            if (generation != _playGeneration || token.IsCancellationRequested) return;
            _overlay?.RefreshState();
            await Player.PlayAsync(item.Channel.Url, token);
            if (generation != _playGeneration || token.IsCancellationRequested) return;
            await Model.RecordPlaybackAsync(item);
            _overlay?.Reveal();
        });
    }
    public async Task PlayLocalAsync(string file)
    {
        if (!_engineReady) return;
        ClearArchivePlayback();
        await Player.PlayAsync(Path.GetFullPath(file)); HasPlayback = true; _overlay?.RefreshState();
    }
    private async void Channel_Selected(object sender, SelectionChangedEventArgs e)
    { if (_initialized && ChannelList.SelectedItem is ChannelItem item && Model.Active?.Id != item.Id) await PlayAsync(item); }
    public async Task NextChannelAsync(int direction)
    {
        var list = Model.VisibleChannels; if (list.Count == 0) return;
        var index = Model.Active is null ? -1 : list.IndexOf(Model.Active);
        var next = list[(index + direction + list.Count) % list.Count];
        await PlayAsync(next); Model.SelectedChannel = next; ChannelList.ScrollIntoView(next);
    }
    public async Task PreviousChannelAsync() { if (Model.AllChannels.Find(c => c.Id == Model.PreviousId) is { } item) await PlayAsync(item); }
    public async Task ImportPlaylistAsync()
    {
        await WithModalAsync(async () =>
        {
            var dialog = new OpenFileDialog { Filter = "IPTV-плейлисты (*.m3u;*.m3u8)|*.m3u;*.m3u8|Все файлы|*.*", Title = "DzhiggaTV · добавить плейлист" };
            if (dialog.ShowDialog(this) == true) await GuardAsync(() => Model.ImportAsync(dialog.FileName));
        });
        await OfferEpgConnectionAsync();
    }
    private async Task OfferEpgConnectionAsync()
    {
        if (_args.Contains("--no-prompt") || Model.AllChannels.Count == 0 || Model.Flag("epg.prompted")) return;
        var answer = MessageBoxResult.No;
        await WithModalAsync(() =>
        {
            answer = MessageBox.Show(this, "Подключить телепрограмму и логотипы EPG.ONE?\n\nЗагрузка продолжится в фоне, можно сразу смотреть каналы. Публичное зеркало сервиса подключается автоматически; адреса ваших трансляций ему не отправляются.", "DzhiggaTV · телепрограмма", MessageBoxButton.YesNo, MessageBoxImage.Question);
            return Task.CompletedTask;
        });
        Model.SetSetting("epg.prompted", true); Model.SetSetting("epg.enabled", answer == MessageBoxResult.Yes); await Model.SaveAsync();
        if (answer == MessageBoxResult.Yes) _ = GuardAsync(Model.RefreshEpgAsync);
    }
    private async void Settings_Click(object sender, RoutedEventArgs e) => await ShowSettingsAsync();
    private async Task CheckUpdatesInBackgroundAsync()
    {
        if (_closing) return;
        try { await Updates.CheckAsync(false, _updateLifetime.Token); }
        catch (OperationCanceledException) { }
    }
    private async Task CleanUpdateCacheAsync()
    {
        try { await DzhiggaTV.Services.UpdateCacheCleaner.CleanAsync(Path.Combine(Model.DataDirectory, "updates"), typeof(App).Assembly.GetName().Version!, _updateLifetime.Token); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException) { }
    }
    private async void Version_Click(object sender, RoutedEventArgs e) => await WithModalAsync(() =>
    {
        if (Updates.HasUpdate) return ShowUpdateDialogAsync(this, false);
        new ReleaseNotesWindow(this).ShowDialog(); return Task.CompletedTask;
    });
    public Task ShowUpdateDialogAsync(Window owner, bool checkNow)
    {
        var dialog = new AppUpdateWindow(owner, Updates, Path.Combine(Model.DataDirectory, "updates"), checkNow);
        dialog.ShowDialog();
        if (dialog.PreparedUpdate is { } prepared && dialog.SelectedRelease is { } release)
        {
            try
            {
                UpdateLauncher.Start(prepared, release.Version, Model.DataDirectory);
                if (owner != this) owner.Close();
                Close();
            }
            catch (Exception error)
            {
                MessageBox.Show(owner, error is DzhiggaTV.Services.UpdateException ? error.Message : "Не удалось запустить обновление. Повторите попытку.", "DzhiggaTV · Обновление", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        return Task.CompletedTask;
    }
    public async Task ShowSettingsAsync()
    {
        await WithModalAsync(async () => { new SettingsWindow(this).ShowDialog(); await Model.SaveAsync(); ApplySettings(); await Model.ReloadAsync(); await Model.RefreshEpgViewAsync(); });
        await OfferEpgConnectionAsync();
    }
    private async void Guide_Click(object sender, RoutedEventArgs e) => await ShowGuideAsync();
    public async Task ShowGuideAsync() => await WithModalAsync(() => { new GuideWindow(this).ShowDialog(); return Task.CompletedTask; });
    public async Task ShowSportsEveningAsync()
    {
        if (!_initialized || _sports is not null) return;
        await GuardAsync(() => WithModalAsync(async () =>
        {
            var muted = Player.IsMuted;
            // Keep the main stream playing live, but silent even if a pending connection finishes.
            Player.IsMuted = true;
            try
            {
                _sports = new SportsEveningWindow(this, Player.Volume, muted);
                _sports.ShowDialog();
            }
            finally
            {
                if (!_closing)
                {
                    Player.Volume = _sports?.Volume ?? Player.Volume;
                    Player.IsMuted = _sports?.IsMuted ?? muted;
                }
                _sports = null;
            }
            if (!_closing) await Model.SaveAsync();
        }));
    }
    public async Task WithModalAsync(Func<Task> action)
    {
        _programmePreview.Hide();
        _modal = true; _overlay?.Hide();
        try { await action(); } finally { _modal = false; if (!_closing && WindowState != WindowState.Minimized) { _overlay?.Show(); SyncOverlay(); } }
    }
    private async void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } button) return;
        if (tag == "guide") { await ShowGuideAsync(); return; }
        if (tag == "sports") { await ShowSportsEveningAsync(); return; }
        if (tag == "categories")
        {
            var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.Right, MaxHeight = Math.Min(520, SystemParameters.WorkArea.Height - 80) };
            foreach (var group in Model.Groups)
            {
                var entry = new MenuItem { Header = new TextBlock { Text = group }, IsCheckable = true, IsChecked = Model.SelectedGroup == group };
                entry.Click += async (_, _) =>
                {
                    Model.Search = ""; Model.SelectedGroup = group; Model.SetSection("all");
                    ChannelListScroll.ResetToTop(ChannelList);
                    await GuardAsync(LoadViewportAsync);
                };
                menu.Items.Add(entry);
            }
            if (Model.Groups.Count == 1) menu.Items.Add(new MenuItem { Header = "Нет категорий", IsEnabled = false });
            menu.PlacementTarget = button; menu.IsOpen = true; return;
        }
        if (tag == "all") { Model.Search = ""; Model.SelectedGroup = "Все категории"; }
        Model.SetSection(tag); await LoadViewportAsync();
    }
    private void CollapseNav_Click(object sender, RoutedEventArgs e) => SetNavigationCollapsed(!_navigationCollapsed);
    private void SetNavigationCollapsed(bool collapsed)
    {
        _navigationCollapsed = collapsed;
        NavigationColumn.Width = new(collapsed ? 64 : 196);
        NavHeading.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        foreach (var label in FindVisualChildren<TextBlock>(Navigation).Where(t => Equals(t.Tag, "NavLabel")))
            label.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        NavToggleButton.ToolTip = collapsed ? "Развернуть панель" : "Свернуть панель";
        System.Windows.Automation.AutomationProperties.SetName(NavToggleButton, (string)NavToggleButton.ToolTip);
    }
    private void Channel_RightClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindParent<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as ChannelItem; if (item is null) return;
        var menu = new ContextMenu();
        void Add(string label, Func<Task> action) { var option = new MenuItem { Header = label }; option.Click += async (_, _) => await GuardAsync(action); menu.Items.Add(option); }
        Add(item.IsFavorite ? "Убрать из избранного" : "Добавить в избранное", () => Model.ToggleFavoriteAsync(item));
        Add("Переименовать в приложении…", () => WithModalAsync(async () => { if (TextPrompt.Show(this, "Название канала", "Новое название", item.Name) is { Length: > 0 } name) { Model.State.Renames[item.Id] = name; item.Name = name; await Model.SaveAsync(); Model.RaisePlayback(); } }));
        Add("Пользовательская категория…", () => WithModalAsync(async () => { if (TextPrompt.Show(this, "Категория", "Название категории", item.Group) is { Length: > 0 } group) { Model.State.CustomCategories[item.Id] = group; await Model.SaveAsync(); await Model.ReloadAsync(); } }));
        Add("Назначить телепрограмму…", () => WithModalAsync(() => { new MappingWindow(this, item).ShowDialog(); return Model.RefreshEpgViewAsync(); }));
        Add("Установить логотип…", () => WithModalAsync(async () => { var d = new OpenFileDialog { Filter = "Изображения|*.png;*.jpg;*.jpeg;*.webp;*.bmp" }; if (d.ShowDialog(this) == true) { Model.State.UserLogos[item.Id] = d.FileName; Model.InvalidateLogo(item.Id); await Model.SaveAsync(); await Model.LoadViewportAsync([item]); } }));
        Add("Скрыть канал", async () => { Model.State.Hidden.Add(item.Id); await Model.SaveAsync(); Model.RefreshFilter(); });
        menu.PlacementTarget = ChannelList; menu.IsOpen = true; e.Handled = true;
    }
    private void Channel_DragStart(object sender, MouseButtonEventArgs e) { _dragOrigin = e.GetPosition(ChannelList); _dragItem = FindParent<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as ChannelItem; }
    private void Channel_DragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem is null) return;
        if ((e.GetPosition(ChannelList) - _dragOrigin).Length < 9) return;
        var item = _dragItem; _dragItem = null; DragDrop.DoDragDrop(ChannelList, item, DragDropEffects.Move);
    }
    private async void Channel_Drop(object sender, DragEventArgs e)
    { if (e.Data.GetData(typeof(ChannelItem)) is ChannelItem from && FindParent<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is ChannelItem to) await Model.MoveAsync(from.Id, to.Id); }
    private async void Channels_Scrolled(object sender, ScrollChangedEventArgs e) { if (_initialized) await GuardAsync(LoadViewportAsync); }
    private Task LoadViewportAsync() => Model.LoadViewportAsync(FindVisualChildren<ListBoxItem>(ChannelList).Select(c => c.DataContext).OfType<ChannelItem>(), true);
    public static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var c = VisualTreeHelper.GetChild(parent, i); if (c is T match) yield return match; foreach (var child in FindVisualChildren<T>(c)) yield return child; } }
    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    { while (child is not null) { if (child is T match) return match; child = VisualTreeHelper.GetParent(child); } return null; }

    private void ApplyMainWindowChrome() => WindowChrome.SetWindowChrome(this, new WindowChrome
    {
        CaptionHeight = TitleBarHeight,
        ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness,
        GlassFrameThickness = new(0),
        CornerRadius = new(0),
        UseAeroCaptionButtons = false
    });
    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    public void ToggleFullscreen()
    {
        if (IsMini) { RestoreWindow(); return; }
        if (IsFullscreen) { RestoreWindow(); return; }
        RememberWindow(); IsFullscreen = true;
        WindowChrome.SetWindowChrome(this, null);
        WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        CollapseChrome(); WindowState = WindowState.Maximized; _overlay?.Reveal(); QueueOverlayLayout();
    }
    public void ToggleMini()
    {
        if (IsMini) { RestoreWindow(); return; }
        if (IsFullscreen) RestoreWindow();
        RememberWindow(); IsMini = true;
        WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize; Topmost = true;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0, ResizeBorderThickness = new(MiniResizeBorder), GlassFrameThickness = new(0),
            CornerRadius = new(0), UseAeroCaptionButtons = false
        });
        MinWidth = 320; MinHeight = 196;
        var width = Parse("mini.width", MiniDefaultWidth); var height = Parse("mini.height", MiniDefaultHeight);
        // Earlier versions saved their old default even when the user had never resized it.
        if (Math.Abs(width - 560) < 1 && Math.Abs(height - 350) < 1) { width = MiniDefaultWidth; height = MiniDefaultHeight; }
        Width = Math.Clamp(width, MinWidth, Math.Max(MinWidth, SystemParameters.WorkArea.Width));
        Height = Math.Clamp(height, MinHeight, Math.Max(MinHeight, SystemParameters.WorkArea.Height));
        RefreshMiniVideoAspect(fit: true);
        Left = Parse("mini.left", Math.Max(0, SystemParameters.WorkArea.Right - Width - 24)); Top = Parse("mini.top", Math.Max(0, SystemParameters.WorkArea.Bottom - Height - 24));
        ClampToScreen(); CollapseChrome(); WindowAppearance.Apply(this); _overlay?.Reveal(); QueueOverlayLayout();
    }
    public void ToggleMiniPin()
    {
        if (!IsMini) return;
        Topmost = !Topmost;
        if (_overlay is not null) _overlay.Topmost = Topmost;
    }
    internal void RefreshMiniVideoAspect(bool fit = false)
    {
        if (!IsMini || IsMiniSizing || _closing) return;
        var aspect = Model.Setting("aspect", "Original") switch
        {
            "Fit16By9" => 16d / 9, "Fit4By3" => 4d / 3,
            _ => Player.GetVideoAspectRatio() ?? MiniVideoAspectRatio
        };
        if (!double.IsFinite(aspect) || aspect <= 0 || !fit && Math.Abs(aspect - MiniVideoAspectRatio) < .0001) return;
        MiniVideoAspectRatio = aspect;
        var bounds = WindowBoundsGeometry.ResizeMini(new(Left, Top, Width, Height), 2, aspect, MiniResizeFrame,
            new(MinWidth, MinHeight), new(SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height));
        Width = bounds.Width; Height = bounds.Height;
        ClampToScreen();
    }
    private double Parse(string key, double fallback) => double.TryParse(Model.Setting(key), CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : fallback;
    private void RememberWindow() => _snapshot = new(Left, Top, ActualWidth, ActualHeight, WindowState);
    private void UpdateWindowFrame() => LayoutRoot.Margin = IsMini ? new(MiniResizeBorder) :
        WindowState == WindowState.Maximized && !IsFullscreen ? SystemParameters.WindowResizeBorderThickness : new(0);
    private void RememberMiniWindow()
    {
        foreach (var (key, value) in new[] { ("mini.left", Left), ("mini.top", Top), ("mini.width", ActualWidth), ("mini.height", ActualHeight) }) Model.SetSetting(key, value);
    }
    private void CollapseChrome()
    {
        _programmePreview.Hide();
        Header.Visibility = Navigation.Visibility = ChannelPanel.Visibility = Splitter.Visibility = ViewingHeading.Visibility = ProgrammePanel.Visibility = StatusBar.Visibility = Visibility.Collapsed;
        TopRow.Height = StatusRow.Height = ViewingHeadingRow.Height = ProgrammeRow.Height = new(0);
        NavigationColumn.MinWidth = ChannelColumn.MinWidth = 0;
        NavigationColumn.Width = ChannelColumn.Width = SplitterColumn.Width = new(0);
        ViewingPanel.Margin = new(0); VideoBorder.CornerRadius = new(0); VideoBorder.BorderThickness = new(0);
        // Keep both native video and the owned overlay inside the resize frame.
        UpdateWindowFrame();
    }
    public void RestoreWindow()
    {
        if (IsMini) { RememberMiniWindow(); _ = Model.SaveAsync(); }
        IsMini = IsFullscreen = IsMiniSizing = false; Topmost = false; WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize;
        ApplyMainWindowChrome();
        MinWidth = 940; MinHeight = 600;
        Header.Visibility = Navigation.Visibility = ChannelPanel.Visibility = Splitter.Visibility = ViewingHeading.Visibility = ProgrammePanel.Visibility = StatusBar.Visibility = Visibility.Visible;
        TopRow.Height = new(TitleBarHeight); StatusRow.Height = new(StatusBarHeight); ViewingHeadingRow.Height = GridLength.Auto;
        NavigationColumn.MinWidth = 64; SetNavigationCollapsed(_navigationCollapsed); ChannelColumn.Width = new(350); ChannelColumn.MinWidth = 270; SplitterColumn.Width = new(5);
        ViewingPanel.Margin = new(20,20,24,14); VideoBorder.CornerRadius = new(12); VideoBorder.BorderThickness = new(1);
        if (_snapshot is { } s) { Width = s.Width; Height = s.Height; Left = s.Left; Top = s.Top; WindowState = s.State; }
        UpdateWindowFrame();
        UpdateProgrammePanelLayout();
        WindowAppearance.Apply(this);
        _overlay?.Reveal(); QueueOverlayLayout();
    }
    private void UpdateProgrammePanelLayout()
    {
        if (IsMini || IsFullscreen) return;
        bool hasDescription = Programmes.CurrentDescription.Length > 0 || Programmes.NextDescription.Length > 0 || Programmes.AfterNextDescription.Length > 0;
        double compactHeight = ViewingPanel.ActualWidth < 650 ? 150 : 130;
        ProgrammeRow.Height = new(hasDescription ? 180 : compactHeight);
    }
    private void ClampToScreen() { Left = Math.Clamp(Left, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width); Top = Math.Clamp(Top, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height); }
    private void QueueOverlayLayout()
    {
        if (_layoutQueued || _closing) return; _layoutQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { _layoutQueued = false; SyncOverlay(); });
    }
    private void SyncOverlay()
    {
        if (_overlay is null || !IsLoaded || _closing) return;
        if (_modal || WindowState == WindowState.Minimized || !IsVisible) { _overlay.Hide(); return; }
        if (!_overlay.IsVisible) _overlay.Show();
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var location = transform.Transform(VideoArea.PointToScreen(new Point()));
        _overlay.Left = location.X; _overlay.Top = location.Y; _overlay.Width = Math.Max(1, VideoArea.ActualWidth); _overlay.Height = Math.Max(1, VideoArea.ActualHeight); _overlay.Topmost = Topmost;
    }
    private async void OnKeyDown(object sender, KeyEventArgs e) => await HandleKeyAsync(e);
    public async Task HandleKeyAsync(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && _programmePreview.Panel.IsVisible) { _programmePreview.Hide(); e.Handled = true; return; }
        if (key == Key.Escape && (IsFullscreen || IsMini)) { RestoreWindow(); e.Handled = true; return; }
        if (key == Key.F11 || key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) { ToggleFullscreen(); e.Handled = true; return; }
        if (key == Key.M && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { ToggleMini(); e.Handled = true; return; }
        if (key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (Model.CanSearch) { if (IsFullscreen || IsMini) RestoreWindow(); SearchInput.Focus(); }
            e.Handled = true; return;
        }
        if (_programmePreview.Panel.IsKeyboardFocusWithin || ProgrammePanel.IsKeyboardFocusWithin) return;
        if (e.OriginalSource is TextBox || e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase) return;
        if (e.OriginalSource is Slider && key is Key.Left or Key.Right or Key.Home or Key.End) return;
        switch (key)
        {
            case Key.F11: ToggleFullscreen(); break;
            case Key.Enter when Keyboard.Modifiers.HasFlag(ModifierKeys.Alt): ToggleFullscreen(); break;
            case Key.Space: await TogglePlaybackPauseAsync(); break;
            case Key.Left: await SeekPlaybackByAsync(Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? -60 : -10); break;
            case Key.Right: await SeekPlaybackByAsync(Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 60 : 10); break;
            case Key.M when Keyboard.Modifiers.HasFlag(ModifierKeys.Control): ToggleMini(); break;
            case Key.M: Player.IsMuted = !Player.IsMuted; break;
            case Key.I: _overlay?.ToggleStreamInformation(); break;
            case Key.C: _overlay?.ToggleDrawer(); break;
            case Key.F: if (Model.Active is { } c) await Model.ToggleFavoriteAsync(c); break;
            case Key.PageDown: await NextChannelAsync(1); break;
            case Key.PageUp: await NextChannelAsync(-1); break;
            case Key.Back: await PreviousChannelAsync(); break;
            case Key.Up: Player.Volume = Math.Clamp(Player.Volume + 5, 0, 100); break;
            case Key.Down: Player.Volume = Math.Clamp(Player.Volume - 5, 0, 100); break;
            case Key.G: await ShowGuideAsync(); break;
            default: return;
        }
        e.Handled = true; _overlay?.Reveal();
    }
    public void ApplySettings()
    {
        Theme.Apply(Model.Setting("theme", "dark") == "light");
        Model.Epg.AllowInsecureHttp = Model.Logos.AllowInsecureHttp = Model.Flag("epg.http");
        ApplyPlayerSettings();
    }
    public void ApplyPlayerSettings()
    {
        if (!_engineReady) return;
        Player.Volume = Parse("volume", 70); Player.IsMuted = Model.Flag("muted");
        if (Enum.TryParse<BufferPreset>(Model.Setting("buffer", "Balanced"), out var buffer)) Player.SetBufferPreset(buffer);
        if (Enum.TryParse<HdrMode>(Model.Setting("hdr", "Auto"), out var hdr)) Player.SetHdrMode(hdr);
        if (Enum.TryParse<AspectMode>(Model.Setting("aspect", "Original"), out var aspect)) Player.SetAspect(aspect);
        Player.SetPreferredLanguages(Model.Setting("audio.language", "rus,ru,eng,en"), Model.Setting("subtitle.language", "rus,ru"));
        Player.SetAudioDevice(Model.Setting("audio.device", "auto"));
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true; _timer.Stop(); _updateTimer.Stop(); _updateLifetime.Cancel(); _playCancellation?.Cancel(); _sports?.Close(); _overlay?.Close();
        if (IsMini) RememberMiniWindow();
        Programmes.Dispose();
        if (Remote is not null) await Remote.DisposeAsync();
        Model.SetSetting("volume", Player.Volume); Model.SetSetting("muted", Player.IsMuted);
        await GuardAsync(Model.SaveAsync);
        try { Updates.Dispose(); Model.Dispose(); Player.Dispose(); }
        finally
        {
            _shutdownComplete = true;
            // SQLite can complete synchronously. Finish the first Closing event before
            // requesting the final close, otherwise WPF rejects the reentrant call.
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Close));
        }
    }
    private async Task SmokeAsync(string reportPath, string media)
    {
        var report = new Dictionary<string, object?>();
        try
        {
            await Task.Delay(5000);
            var before = Player.GetStatistics(); report["videoDecoded"] = before.Width > 0; report["statistics"] = before;
            var handle = VideoHost.VideoHandle;
            ToggleFullscreen(); await Task.Delay(1500); report["fullscreen"] = IsFullscreen && VideoHost.VideoHandle == handle && Player.GetStatistics().Width > 0;
            RestoreWindow(); ToggleMini(); await Task.Delay(1500); report["mini"] = IsMini && Topmost && VideoHost.VideoHandle == handle && Player.GetStatistics().Width > 0;
            RestoreWindow(); report["restored"] = !IsMini && !IsFullscreen && VideoHost.VideoHandle == handle;
            var audio = Player.GetTracks().Where(t => t.Type == "audio").ToArray(); report["audioTrackCount"] = audio.Length;
            if (audio.Length > 1) { Player.SelectAudio(audio[1].Id); await Task.Delay(500); report["audioSwitch"] = Player.GetTracks().Any(t => t.Id == audio[1].Id && t.Type == "audio" && t.Selected); }
            Player.IsMuted = true; report["mute"] = Player.IsMuted; Player.IsMuted = false;
            Player.TogglePause(); report["pause"] = Player.IsPaused; Player.TogglePause();
            report["channels"] = Model.AllChannels.Count; report["groups"] = Model.AllChannels.Select(c => c.Channel.Group).Distinct().Count();
            await Player.PlayAsync(media); await Task.Delay(1200); report["reload"] = Player.GetStatistics().Width > 0;
            Model.Status = "Автоматическая проверка UI и локального видео завершена.";
        }
        catch (Exception error) { report["errorType"] = error.GetType().Name; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
    private async Task VerifyEpgAsync(string reportPath)
    {
        await Model.RefreshEpgAsync();
        await LoadViewportAsync();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until && !Model.VisibleChannels.Take(20).Any(c => c.LogoPath is not null)) await Task.Delay(250);
        var rows = Model.VisibleChannels.Take(20).ToArray();
        var decoded = 0;
        foreach (var row in rows.Where(c => c.LogoPath is not null))
        {
            try { var bitmap = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(row.LogoPath!)); if (bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0) decoded++; } catch { }
        }
        var report = new { Version = typeof(MainWindow).Assembly.GetName().Version?.ToString(), Channels = Model.AllChannels.Count, MappedChannels = Model.Mappings.Count, EpgChannels = Model.EpgChannels.Count, Programmes = await Model.Store.GetProgrammeCountAsync(), VisibleRows = rows.Length, RowsWithProgramme = rows.Count(c => c.Current is not null), DownloadedLogos = rows.Count(c => c.LogoPath is not null), WpfDecodedLogos = decoded, Model.EpgStatus, Model.IsEpgBusy };
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
