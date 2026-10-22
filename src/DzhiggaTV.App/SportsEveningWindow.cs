using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DzhiggaTV.Player;

namespace DzhiggaTV.App;

public sealed class SportsEveningWindow : Window
{
    private readonly MainViewModel _model;
    private readonly SportsScreenPanel _screens;
    private readonly SportsProgrammePanel _programme;
    private readonly Grid _root, _toolbar, _footer;
    private readonly Dictionary<string, SportsTile> _tiles = new(StringComparer.Ordinal);
    private IReadOnlyList<ChannelItem?> _layout = [];
    private readonly SportsAudioFocus _audio = new();
    private readonly DispatcherTimer _guideTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _layoutTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _guideLoading;
    private readonly TextBlock _sound = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _empty = new() { Text = "Выберите каналы для одновременного просмотра", FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _mute, _fullscreen;
    private bool _closed, _isFullscreen;
    private string? _expandedChannelId;
    private bool _expandedWasFullscreen;
    private SportsTile? _pressedTile;
    private Point _pressPosition;
    private SportsDragWindow? _drag;
    private SportsFullscreenOverlay? _focusedOverlay;
    public string? ExpandedChannelId => _expandedChannelId;
    public IReadOnlyList<string?> ChannelLayout => _layout.Select(c => c?.Id).ToArray();
    private (Rect Bounds, WindowState State)? _restore;
    public double Volume => _audio.Volume;
    public bool IsMuted => _audio.IsMuted;
    public string? RemoteAudioId => _audio.SelectedId;
    public bool RemoteFullscreen => _isFullscreen;
    public void RemoteVolume(double? volume, bool? muted)
    {
        if (volume is { } v) _audio.Volume = v;
        if (muted is { } m) _audio.IsMuted = m;
        RefreshAudio();
    }

    public SportsEveningWindow(MainWindow main, double volume, bool muted)
    {
        _model = main.Model;
        _programme = new(this, _model);
        _screens = new(_programme); _screens.Children.Add(_programme);
        Theme.Window(this); WindowBounds.Attach(this, () => _isFullscreen);
        Owner = main; Title = "DzhiggaTV · Мультиэкран";
        Width = Math.Min(1440, SystemParameters.WorkArea.Width - 24); Height = Math.Min(900, SystemParameters.WorkArea.Height - 36);
        MinWidth = Math.Min(860, Width); MinHeight = Math.Min(500, Height); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _audio.Volume = volume; _audio.IsMuted = muted;
        var root = _root = new Grid { Margin = new(12) }; Content = root;
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var toolbar = _toolbar = new Grid { Margin = new(4, 0, 0, 12) };
        toolbar.ColumnDefinitions.Add(new()); toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        toolbar.Children.Add(new TextBlock { Text = "Мультиэкран", FontSize = 22, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; Grid.SetColumn(actions, 1); toolbar.Children.Add(actions);
        var choose = Button("Выбрать каналы", "Добавить или убрать каналы"); choose.SetResourceReference(BorderBrushProperty, "AccentBrush");
        _fullscreen = Button("Полный экран", "Полный экран (F11)");
        var close = Button("Вернуться", "Закрыть мультиэкран и вернуться к плееру");
        actions.Children.Add(choose); actions.Children.Add(_fullscreen); actions.Children.Add(close); root.Children.Add(toolbar);
        var viewing = new Grid(); Grid.SetRow(viewing, 1); root.Children.Add(viewing); viewing.Children.Add(_screens); viewing.Children.Add(_empty);
        var footer = _footer = new Grid { Margin = new(4, 10, 4, 0) };
        footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.Children.Add(_sound); _sound.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        var hint = new TextBlock { Text = "Перетащите видео — поменять местами · Двойной щелчок — полный экран", FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new(16, 0, 16, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); Grid.SetColumn(hint, 1); footer.Children.Add(hint);
        var soundControls = new StackPanel { Orientation = Orientation.Horizontal }; Grid.SetColumn(soundControls, 2); footer.Children.Add(soundControls);
        _mute = Button("", "Выключить / включить звук (M)"); _mute.MinWidth = 98; soundControls.Children.Add(_mute);
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = volume, IsMoveToPointEnabled = true, Width = 100, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0), ToolTip = "Громкость" };
        System.Windows.Automation.AutomationProperties.SetName(slider, "Громкость мультиэкрана"); soundControls.Children.Add(slider);
        slider.ValueChanged += (_, e) => _audio.Volume = e.NewValue;
        Grid.SetRow(footer, 2); root.Children.Add(footer); _empty.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        choose.Click += (_, _) => ChooseChannels(); _fullscreen.Click += (_, _) => ToggleFullscreen(); close.Click += (_, _) => Close();
        _mute.Click += (_, _) => ToggleMute();
        PreviewMouseLeftButtonDown += TilePressed;
        PreviewMouseMove += TileMoved;
        PreviewMouseLeftButtonUp += TileReleased;
        LostMouseCapture += (_, _) => { if (_pressedTile is not null && Mouse.Captured != this) CancelInteraction(); };
        Deactivated += (_, _) => CancelInteraction();
        SizeChanged += (_, _) => { CancelInteraction(); _focusedOverlay?.Sync(); };
        LocationChanged += (_, _) => { CancelInteraction(); _focusedOverlay?.Sync(); };
        DpiChanged += (_, _) => { CancelInteraction(); _focusedOverlay?.Sync(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && (_drag is not null || _pressedTile is not null)) { CancelInteraction(); e.Handled = true; }
            else if (e.Key == Key.F11 || e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Alt) { ToggleFullscreen(); e.Handled = true; }
            else if (e.Key == Key.Escape) { if (_expandedChannelId is not null) RestoreChannel(); else if (_isFullscreen) ToggleFullscreen(); else Close(); e.Handled = true; }
            else if (e.Key == Key.M && Keyboard.Modifiers == ModifierKeys.None) { ToggleMute(); e.Handled = true; }
        };
        Loaded += (_, _) =>
        {
            var opening = SportsSelection.ForOpening(_model);
            if (opening.Channels.Count > 0) SetLayout(opening.Layout, opening.AudioId);
            if (opening.ShowChannelPicker) ChooseChannels();
            if (!_closed) { _guideTimer.Start(); _layoutTimer.Start(); }
        };
        _guideTimer.Tick += async (_, _) => await LoadGuideAsync(_tiles.Values.Select(t => t.Channel).ToArray());
        _layoutTimer.Tick += (_, _) =>
        {
            foreach (var tile in _tiles.Values) tile.RefreshVideoAspect();
            RefreshProgramme();
            _focusedOverlay?.Tick();
        };
        Closing += (_, _) =>
        {
            if (_closed) return;
            _closed = true;
            _focusedOverlay?.Close(); _focusedOverlay = null;
            CancelInteraction(); _programme.Dispose();
            _guideTimer.Stop();
            _layoutTimer.Stop();
            SaveSelection();
            _audio.Replace([]);
            foreach (var tile in _tiles.Values) tile.Dispose();
            _tiles.Clear();
        };
        RefreshAudio();
    }

    private void ChooseChannels()
    {
        CancelInteraction();
        var initial = _layout.Count > 0 ? _layout :
            _model.Active is { } active && _model.IsAllowed(active) ? new[] { active } : [];
        var picker = new SportsChannelPicker(this, _model, initial);
        if (picker.ShowDialog() == true && !_closed) SetLayout(picker.Layout);
    }

    public void SetChannels(IEnumerable<ChannelItem> channels, string? preferredAudioId = null)
        => SetLayout(SportsSelection.Resolve(_model, channels.Select(c => c.Id)), preferredAudioId);

    public void SetLayout(IEnumerable<ChannelItem?> slots, string? preferredAudioId = null)
    {
        if (_closed) return;
        CancelInteraction();
        _layout = SportsSelection.ResolveLayout(_model, slots.Select(c => c?.Id));
        var selection = _layout.OfType<ChannelItem>().ToArray();
        if (_expandedChannelId is { } expanded && selection.All(c => c.Id != expanded)) RestoreChannel();
        var next = selection.Select(c => _tiles.TryGetValue(c.Id, out var existing) ? existing : new SportsTile(c, _model)).ToArray();
        _audio.Replace(next.Select(t => new KeyValuePair<string, ISportsAudio>(t.Channel.Id, t)), preferredAudioId);
        foreach (var removed in _tiles.Values.Where(t => !next.Contains(t)).ToArray())
        {
            removed.Dispose(); _screens.Children.Remove(removed); _tiles.Remove(removed.Channel.Id);
        }
        foreach (var tile in next)
        {
            if (_tiles.ContainsKey(tile.Channel.Id)) continue;
            _tiles.Add(tile.Channel.Id, tile);
            tile.AudioRequested += (_, _) => SelectAudio(tile.Channel.Id);
            tile.RemoveRequested += (_, _) => SetLayout(_layout.Select(c => c?.Id == tile.Channel.Id ? null : c).ToArray());
            tile.FullscreenRequested += (_, _) => ToggleChannelFullscreen(tile.Channel.Id);
            tile.VideoPressed += (_, e) => BeginPress(tile, PointFromScreen(new(e.ScreenX, e.ScreenY)));
            tile.VideoMoved += (_, _) => { if (_expandedChannelId == tile.Channel.Id) _focusedOverlay?.PointerMoved(); };
            tile.AspectChanged += (_, _) => _screens.InvalidateMeasure();
            _screens.Children.Add(tile);
        }
        // Dictionary slots may be reused after removals; persist the visible order explicitly.
        _tiles.Clear();
        foreach (var tile in next) _tiles.Add(tile.Channel.Id, tile);
        ArrangeScreens();
        _empty.Visibility = next.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _model.LoadLogos(selection); _ = LoadGuideAsync(selection);
        SaveSelection(); RefreshAudio();
    }

    private void ArrangeScreens()
    {
        var columns = SportsLayout.ColumnsFor(_layout.Count);
        for (int i = 0; i < _layout.Count; i++)
        {
            if (_layout[i] is not { } channel) continue;
            var screen = _tiles[channel.Id];
            Grid.SetRow(screen, i / columns); Grid.SetColumn(screen, i % columns);
        }
        _screens.SetScreens(_layout.OfType<ChannelItem>().Select(c => _tiles[c.Id]));
    }

    public bool SwapChannels(string sourceId, string targetId)
    {
        if (_closed || _expandedChannelId is not null || sourceId == targetId) return false;
        var slots = _layout.ToArray();
        int source = Array.FindIndex(slots, c => c?.Id == sourceId), target = Array.FindIndex(slots, c => c?.Id == targetId);
        if (source < 0 || target < 0) return false;
        (slots[source], slots[target]) = (slots[target], slots[source]); _layout = slots;
        ArrangeScreens(); SaveSelection();
        foreach (string id in new[] { sourceId, targetId })
            if (SystemParameters.ClientAreaAnimation && !_model.Flag("reducedMotion"))
                _tiles[id].PulsePlacement();
        return true;
    }

    private void BeginPress(SportsTile tile, Point position)
    {
        if (_closed || _expandedChannelId is not null || _drag is not null) return;
        _pressedTile = tile; _pressPosition = position;
        if (!Mouse.Capture(this, CaptureMode.Element)) _pressedTile = null;
    }
    private void TilePressed(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        SportsTile? tile = null;
        for (var element = source; element is not null; element = element is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(element))
        {
            if (element is System.Windows.Controls.Button or System.Windows.Controls.Primitives.ScrollBar) return;
            if (element is SportsTile found) { tile = found; break; }
        }
        if (tile is null) return;
        e.Handled = true;
        if (e.ClickCount == 2) ToggleChannelFullscreen(tile.Channel.Id);
        else BeginPress(tile, e.GetPosition(this));
    }
    private void TileMoved(object sender, MouseEventArgs e)
    {
        if (_pressedTile is not { } tile) return;
        if (e.LeftButton != MouseButtonState.Pressed) { CancelInteraction(); return; }
        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _pressPosition.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _pressPosition.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _pressedTile = null; Mouse.Capture(null);
        var targets = _tiles.Values.Where(t => t.IsVisible).ToDictionary(t => t.Channel.Id, t => new Rect(t.TranslatePoint(new(), _root), t.RenderSize));
        var drag = new SportsDragWindow(this, _root, tile.Channel, targets, SystemParameters.ClientAreaAnimation && !_model.Flag("reducedMotion"));
        _drag = drag;
        drag.Completed += target => { if (ReferenceEquals(_drag, drag)) { _drag = null; if (target is not null && !_closed) SwapChannels(tile.Channel.Id, target); } };
        drag.Begin(PointToScreen(position)); e.Handled = true;
    }
    private void TileReleased(object sender, MouseButtonEventArgs e)
    {
        if (_pressedTile is not { } tile) return;
        var position = e.GetPosition(tile);
        _pressedTile = null; if (Mouse.Captured == this) Mouse.Capture(null);
        if (new Rect(tile.RenderSize).Contains(position)) SelectAudio(tile.Channel.Id);
        e.Handled = true;
    }
    private void CancelInteraction()
    {
        _pressedTile = null;
        if (Mouse.Captured == this) Mouse.Capture(null);
        var drag = _drag; _drag = null; drag?.Cancel();
    }

    public void SelectAudio(string id) { _audio.Select(id); SaveSelection(); RefreshAudio(); }
    private void SaveSelection() => SportsSelection.SaveLayout(_model, _layout, _audio.SelectedId);
    private async Task LoadGuideAsync(IReadOnlyList<ChannelItem> channels)
    {
        if (_closed || _guideLoading) return;
        _guideLoading = true;
        try { await _model.UpdateNowNextAsync(channels); }
        catch (OperationCanceledException) { }
        catch { /* Guide availability must not stop independent video streams. */ }
        finally { _guideLoading = false; if (!_closed) RefreshProgramme(); }
    }
    private void ToggleMute() { _audio.IsMuted = !_audio.IsMuted; RefreshAudio(); }
    private void RefreshAudio()
    {
        foreach (var tile in _tiles.Values) tile.SetAudioSelected(tile.Channel.Id == _audio.SelectedId, _audio.IsMuted);
        _sound.Text = _audio.SelectedId is { } id && _tiles.TryGetValue(id, out var selected)
            ? $"{(_audio.IsMuted ? "Звук выключен · выбран" : "Звук")}: {selected.Channel.Name}" : "Звук: канал не выбран";
        _mute.Content = _audio.IsMuted ? "Включить звук" : "Без звука";
        RefreshProgramme();
    }
    private void RefreshProgramme() => _programme.Show(_audio.SelectedId is { } id && _tiles.TryGetValue(id, out var selected) ? selected.Channel : null);

    public void ToggleFullscreen()
    {
        if (_expandedChannelId is not null) { RestoreChannel(); return; }
        SetWindowFullscreen(!_isFullscreen);
    }
    public void ToggleChannelFullscreen(string id)
    {
        if (_closed || !_tiles.ContainsKey(id)) return;
        CancelInteraction();
        if (_expandedChannelId == id) { RestoreChannel(); return; }
        if (_expandedChannelId is not null) RestoreChannel();
        _expandedWasFullscreen = _isFullscreen; _expandedChannelId = id;
        SelectAudio(id);
        _tiles[id].SetExpanded(true); _screens.Focus(_tiles[id]);
        _toolbar.Visibility = _footer.Visibility = Visibility.Collapsed; _root.Margin = new(0);
        if (!_isFullscreen) SetWindowFullscreen(true);
        _focusedOverlay = new(this, _tiles[id].Channel);
        _focusedOverlay.Reveal();
    }
    private void RestoreChannel()
    {
        if (_expandedChannelId is not { } id) return;
        _focusedOverlay?.Close(); _focusedOverlay = null;
        CancelInteraction(); _expandedChannelId = null;
        if (_tiles.TryGetValue(id, out var tile)) tile.SetExpanded(false);
        _screens.Focus(null);
        _toolbar.Visibility = _footer.Visibility = Visibility.Visible; _root.Margin = new(12);
        if (!_expandedWasFullscreen && _isFullscreen) SetWindowFullscreen(false);
    }
    private void SetWindowFullscreen(bool fullscreen)
    {
        CancelInteraction();
        if (fullscreen)
        {
            _restore = (RestoreBounds, WindowState); _isFullscreen = true; WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            _isFullscreen = false; WindowState = WindowState.Normal; WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.CanResize;
            if (_restore is { } restore) { Left = restore.Bounds.Left; Top = restore.Bounds.Top; Width = restore.Bounds.Width; Height = restore.Bounds.Height; WindowState = restore.State; }
        }
        _fullscreen.Content = _isFullscreen ? "Выйти из полного экрана" : "Полный экран";
    }

    internal static Button Button(string text, string tip) => new() { Content = text, ToolTip = tip, FontSize = 12, Padding = new(10, 6, 10, 6), Margin = new(3, 0, 0, 0), MinHeight = 32 };
}

public sealed class SportsTile : Border, ISportsAudio, IDisposable
{
    public ChannelItem Channel { get; }
    public MpvPlayer Player { get; } = new() { IsMuted = true };
    private readonly NativeVideoHost _host = new();
    private readonly MainViewModel _model;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Button _sound;
    private readonly Grid _root, _header;
    private readonly TextBlock _programme;
    private readonly TextBlock _status = new() { FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(10, 0, 10, 4), Text = "Подключение к трансляции…" };
    private bool _disposed, _audioSelected, _compact;
    public event EventHandler? AudioRequested;
    public event EventHandler? RemoveRequested;
    public event EventHandler? FullscreenRequested;
    public event EventHandler<NativePointerEventArgs>? VideoPressed;
    public event EventHandler? VideoMoved;
    public event EventHandler? AspectChanged;
    public double VideoAspect { get; private set; } = MultiscreenGeometry.DefaultAspect;
    public bool IsMuted { get => Player.IsMuted; set => Player.IsMuted = value; }
    public double Volume { get => Player.Volume; set => Player.Volume = value; }

    public SportsTile(ChannelItem channel, MainViewModel model)
    {
        Channel = channel; _model = model;
        Margin = new(4); BorderThickness = new(2); CornerRadius = new(8); SetResourceReference(BorderBrushProperty, "BorderBrush"); SetResourceReference(BackgroundProperty, "PanelBrush");
        var root = _root = new Grid { ClipToBounds = true }; Child = root;
        root.RowDefinitions.Add(new() { Height = new(42) }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = new(22) }); root.RowDefinitions.Add(new() { Height = new(16) });
        var header = _header = new Grid { Margin = new(8, 4, 5, 4) };
        foreach (var width in new[] { new GridLength(28), new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto }) header.ColumnDefinitions.Add(new() { Width = width });
        var logo = new ChannelLogo { Width = 24, Height = 24, Stretch = Stretch.Uniform };
        logo.SetBinding(ChannelLogo.PathProperty, new Binding(nameof(ChannelItem.LogoPath)) { Source = channel }); header.Children.Add(logo);
        var title = new TextBlock { Text = $"{channel.Number}  {channel.Name}", FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new(7, 0, 4, 0), ToolTip = channel.Name };
        Grid.SetColumn(title, 1); header.Children.Add(title);
        _sound = SportsEveningWindow.Button("Звук", $"Выбрать звук: {channel.Name}"); _sound.Width = 64; _sound.Padding = new(5, 3, 5, 3);
        _sound.Click += (_, _) => AudioRequested?.Invoke(this, EventArgs.Empty); Grid.SetColumn(_sound, 2); header.Children.Add(_sound);
        var retry = SportsEveningWindow.Button("↻", "Переподключить этот канал"); retry.Width = 28; retry.Padding = new(0); retry.Click += async (_, _) => await PlayAsync(); Grid.SetColumn(retry, 3); header.Children.Add(retry);
        var remove = SportsEveningWindow.Button("×", $"Убрать канал: {channel.Name}"); remove.Width = 28; remove.Padding = new(0); remove.Click += (_, _) => RemoveRequested?.Invoke(this, EventArgs.Empty); Grid.SetColumn(remove, 4); header.Children.Add(remove);
        SizeChanged += (_, _) =>
        {
            _compact = ActualWidth < 300;
            _sound.Width = _compact ? 32 : 64;
            _sound.Content = _compact ? _audioSelected ? "♪ ✓" : "♪" : _audioSelected ? "Звук ✓" : "Звук";
            title.Visibility = ActualWidth < 180 ? Visibility.Collapsed : Visibility.Visible;
            logo.Visibility = ActualWidth < 145 ? Visibility.Collapsed : Visibility.Visible;
            header.ColumnDefinitions[0].Width = new(ActualWidth < 145 ? 0 : 28);
            retry.Visibility = ActualWidth < 175 ? Visibility.Collapsed : Visibility.Visible;
        };
        System.Windows.Automation.AutomationProperties.SetName(_sound, $"Звук: {channel.Name}");
        System.Windows.Automation.AutomationProperties.SetName(remove, $"Убрать: {channel.Name}");
        root.Children.Add(header);
        var video = new Border { Background = Brushes.Black, Child = _host }; Grid.SetRow(video, 1); root.Children.Add(video);
        var programme = _programme = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(10, 6, 10, 0) };
        programme.SetBinding(TextBlock.TextProperty, new Binding(nameof(ChannelItem.NowTitle)) { Source = channel }); programme.SetBinding(ToolTipProperty, new Binding(nameof(ChannelItem.NowTitle)) { Source = channel });
        Grid.SetRow(programme, 2); root.Children.Add(programme); Grid.SetRow(_status, 3); root.Children.Add(_status); _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _host.HandleReady += async (_, _) => await PlayAsync();
        _host.PointerActivity += (_, e) =>
        {
            if (e.Kind == NativePointerKind.LeftDown) VideoPressed?.Invoke(this, e);
            else if (e.Kind == NativePointerKind.Move) VideoMoved?.Invoke(this, EventArgs.Empty);
        };
        _host.DoubleClicked += (_, _) => FullscreenRequested?.Invoke(this, EventArgs.Empty);
        Player.StateChanged += OnStateChanged;
    }

    public void SetExpanded(bool expanded)
    {
        Margin = new(expanded ? 0 : 4); BorderThickness = new(expanded ? 0 : 2); CornerRadius = new(expanded ? 0 : 8);
        _header.Visibility = _programme.Visibility = _status.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        _root.RowDefinitions[0].Height = new(expanded ? 0 : 42);
        _root.RowDefinitions[2].Height = new(expanded ? 0 : 22);
        _root.RowDefinitions[3].Height = new(expanded ? 0 : 16);
    }
    public void PulsePlacement() => _header.BeginAnimation(OpacityProperty,
        new DoubleAnimation(.45, 1, TimeSpan.FromMilliseconds(220)) { FillBehavior = FillBehavior.Stop });

    private async Task PlayAsync()
    {
        if (_disposed || _host.VideoHandle == IntPtr.Zero) return;
        try
        {
            if (!Player.IsInitialized)
            {
                if (Enum.TryParse<BufferPreset>(_model.Setting("buffer", "Balanced"), out var buffer)) Player.SetBufferPreset(buffer);
                if (Enum.TryParse<HdrMode>(_model.Setting("hdr", "Auto"), out var hdr)) Player.SetHdrMode(hdr);
                Player.SetPreferredLanguages(_model.Setting("audio.language", "rus,ru,eng,en"), _model.Setting("subtitle.language", "rus,ru"));
                Player.Initialize(_host.VideoHandle);
                Player.SetAudioDevice(_model.Setting("audio.device", "auto"));
            }
            await Player.PlayAsync(Channel.Channel.Url, _lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch { if (!_disposed) _status.Text = "Не удалось подключиться. Нажмите ↻ для повтора."; }
    }
    private void OnStateChanged(object? sender, PlaybackStateChangedEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        e = Player.Status;
        if (!_disposed) { _status.Text = e.State == PlaybackState.Playing ? "Прямой эфир" : e.Message; if (e.State == PlaybackState.Playing) RefreshVideoAspect(); }
    });
    public void RefreshVideoAspect()
    {
        if (_disposed) return;
        var aspect = Player.GetVideoAspectRatio();
        if (aspect is not > 0 || Math.Abs(VideoAspect - aspect.Value) < .0001) return;
        VideoAspect = aspect.Value; AspectChanged?.Invoke(this, EventArgs.Empty);
    }
    public void SetAudioSelected(bool selected, bool muted)
    {
        _audioSelected = selected;
        SetResourceReference(BorderBrushProperty, selected ? "AccentBrush" : "BorderBrush");
        _sound.SetResourceReference(Control.BorderBrushProperty, selected ? "AccentBrush" : "BorderBrush");
        _sound.SetResourceReference(Control.ForegroundProperty, selected ? "AccentBrush" : "TextBrush");
        _sound.Content = _compact ? selected ? "♪ ✓" : "♪" : selected ? "Звук ✓" : "Звук";
        _sound.ToolTip = selected ? muted ? "Источник выбран, общий звук выключен" : "Звук идёт с этого канала" : $"Выбрать звук: {Channel.Name}";
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel(); Player.StateChanged -= OnStateChanged;
        Player.Dispose(); _host.Dispose(); _lifetime.Dispose();
    }
}
