using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DzhiggaTV.Remote;
using Microsoft.Win32;

namespace DzhiggaTV.App;

public sealed class RemoteSettingsWindow : Window
{
    private readonly MainWindow _main;
    private readonly RemoteServer _remote;
    private readonly ComboBox _address = new(), _delivery = new(), _quality = new();
    private readonly TextBox _port = new(), _limit = new(), _ffmpeg = new();
    private readonly CheckBox _auto = new(), _view = new(), _control = new(), _volume = new(), _multi = new(), _favorites = new(), _direct = new(), _transcode = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14, Foreground = Brushes.Turquoise };
    private readonly TextBlock _network = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 6, 0, 0) };
    private readonly TextBlock _url = new() { TextWrapping = TextWrapping.Wrap, FontSize = 17, Margin = new(0, 12, 0, 12) };
    private readonly TextBlock _diagnostic = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Image _qr = new() { Width = 216, Height = 216, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
    private readonly TextBlock _placeholder = new() { Text = "QR-код появится\nпосле включения Remote", TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 14 };
    private readonly Border _qrFrame = new() { Width = 240, Height = 240, CornerRadius = new(18), Padding = new(12), Margin = new(0, 0, 24, 16) };
    private readonly StackPanel _devices = new();
    private readonly Button _start = new() { MinWidth = 155, Padding = new(17, 10, 17, 10), Margin = new(0, 0, 0, 16) };
    private readonly Button _refreshCode = new() { Content = "Обновить код", IsEnabled = false }, _copy = new() { Content = "Копировать ссылку", IsEnabled = false };
    private readonly Button _compatibility = new() { Content = "Включить совместимость iPhone", Margin = new(10, 0, 0, 16) };
    private readonly Expander _settings = new() { Header = "Настройки сервера, просмотра и пульта", Margin = new(0, 18, 0, 12), IsExpanded = false };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly ProgressBar _preparation = new() { Height = 4, Maximum = 100, Margin = new(0, 8, 0, 6), Visibility = Visibility.Collapsed };
    private readonly Button _cancelPreparation = new() { Content = "Отмена", Margin = new(10, 0, 0, 16), Visibility = Visibility.Collapsed };
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _preparationCancellation;
    private AddressChoice[] _addressChoices = [];
    private string? _pairingUrl;
    private string _deviceSnapshot = "", _lastServerStatus = "";
    private bool _busy, _closed, _dependenciesPrepared;

    public RemoteSettingsWindow(MainWindow main, Window owner, RemoteServer? server = null)
    {
        _main = main; _remote = server ?? main.Remote;
        Theme.Window(this); Owner = owner; Title = "DzhiggaTV Remote · Подключите телефон";
        var area = SystemParameters.WorkArea;
        MaxWidth = Math.Max(360, area.Width - 24); MaxHeight = Math.Max(360, area.Height - 30);
        MinWidth = Math.Min(620, MaxWidth); MinHeight = Math.Min(480, MaxHeight);
        Width = Math.Min(960, MaxWidth); Height = Math.Min(790, MaxHeight); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowBounds.Attach(this, () => false);
        var root = new DockPanel { Margin = new(24) }; Content = root;
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        Heading(heading, "DzhiggaTV Remote", 26); Note(heading, "Телевизор и пульт на вашем телефоне. Подключение по домашнему Wi-Fi.");
        var page = new StackPanel { Margin = new(0, 0, 10, 0) };
        root.Children.Add(new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

        var serverPanel = new StackPanel(); serverPanel.Children.Add(_status); serverPanel.Children.Add(_network);
        _preparation.SetResourceReference(ForegroundProperty, "AccentBrush"); serverPanel.Children.Add(_preparation);
        var networkActions = new WrapPanel { Margin = new(0, 8, 0, 0) };
        AddButton(networkActions, "Обновить сеть", () => { RefreshAddresses(); DescribeNetwork(); return Task.CompletedTask; });
        AddButton(networkActions, "Настройки сети Windows", () => { Process.Start(new ProcessStartInfo("ms-settings:network-status") { UseShellExecute = true }); return Task.CompletedTask; });
        serverPanel.Children.Add(networkActions); page.Children.Add(Card(serverPanel));
        _start.SetResourceReference(BorderBrushProperty, "AccentBrush"); _start.HorizontalAlignment = HorizontalAlignment.Left;
        var serverActions = new WrapPanel(); serverActions.Children.Add(_start); serverActions.Children.Add(_compatibility); serverActions.Children.Add(_cancelPreparation); page.Children.Add(serverActions);
        _cancelPreparation.Click += (_, _) => _preparationCancellation?.Cancel();
        _compatibility.Click += async (_, _) => await RunAsync(EnableCompatibilityAsync);

        var connection = new WrapPanel();
        var qrContent = new Grid(); qrContent.Children.Add(_qr); qrContent.Children.Add(_placeholder); _qrFrame.Child = qrContent;
        _qrFrame.SetResourceReference(BackgroundProperty, "CardBrush"); connection.Children.Add(_qrFrame);
        var instructions = new StackPanel { Width = 330, MaxWidth = Math.Max(260, MinWidth - 100) };
        Heading(instructions, "Подключите телефон", 21);
        Note(instructions, "1. Подключите телефон к тому же роутеру.\n2. Откройте камеру iPhone и наведите на QR-код.\n3. Подтвердите подключение на компьютере.");
        instructions.Children.Add(_url);
        var codeActions = new WrapPanel(); codeActions.Children.Add(_refreshCode); codeActions.Children.Add(_copy); instructions.Children.Add(codeActions);
        _refreshCode.Margin = new(0, 0, 8, 8); _copy.Margin = new(0, 0, 0, 8);
        _refreshCode.Click += async (_, _) => await RunAsync(() => { RefreshCode(); return Task.CompletedTask; });
        _copy.Click += async (_, _) => await RunAsync(() => { if (_pairingUrl is not null) { Clipboard.SetText(_pairingUrl); _status.Text = "Одноразовая ссылка скопирована · действует 3 минуты"; } return Task.CompletedTask; });
        connection.Children.Add(instructions); page.Children.Add(connection);
        Note(page, "HTTP не шифрует трафик. Используйте Remote только в своей домашней сети. Компьютер должен оставаться включённым.");
        Heading(page, "Устройства", 18); page.Children.Add(_devices);
        AddButton(page, "Отозвать доступ у всех", () => { _remote.Devices.Revoke(null); if (_remote.Running) RefreshCode(); Refresh(); return Task.CompletedTask; });

        var optionsPanel = new StackPanel { Margin = new(0, 12, 0, 0) }; _settings.Content = optionsPanel; page.Children.Add(_settings);
        var options = main.RemoteOptions(); _dependenciesPrepared = options.DependenciesPrepared; RefreshAddresses(options.Address);
        _address.SelectionChanged += (_, _) => DescribeNetwork();
        Field(optionsPanel, "Сетевой адаптер", _address); _port.Text = options.Port.ToString(); Field(optionsPanel, "Порт", _port);
        Check(optionsPanel, _auto, "Запускать Remote вместе с DzhiggaTV", options.AutoStart);
        Heading(optionsPanel, "Просмотр", 18); Check(optionsPanel, _view, "Разрешить просмотр ТВ на телефоне", options.AllowViewing);
        _limit.Text = options.MaximumStreams.ToString(); Field(optionsPanel, "Одновременные мобильные потоки · 1–4", _limit);
        _delivery.ItemsSource = new[] { "Локальный HLS-прокси", "Автоматически · HLS или FFmpeg", "Прямое воспроизведение" };
        _delivery.SelectedIndex = options.Delivery switch { "auto" => 1, "direct" => 2, _ => 0 }; Field(optionsPanel, "Передача видео", _delivery);
        Check(optionsPanel, _direct, "Разрешить исходные IPTV-адреса на телефоне", options.AllowDirectUrls);
        Note(optionsPanel, "Прямой режим передаёт телефону адрес и токен провайдера. Рекомендуется локальный прокси.");
        Check(optionsPanel, _transcode, "Разрешить преобразование через FFmpeg", options.AllowTranscoding);
        _ffmpeg.Text = options.FfmpegPath.Length > 0 ? options.FfmpegPath : main.RemoteComponents.InstalledPath; Field(optionsPanel, "Путь к ffmpeg.exe", _ffmpeg);
        Note(optionsPanel, "При первом включении компоненты для просмотра проверяются и при необходимости загружаются автоматически. Повторная загрузка не нужна, пока установленный FFmpeg исправен.");
        AddButton(optionsPanel, "Выбрать ffmpeg.exe…", () => { var d = new OpenFileDialog { Filter = "FFmpeg|ffmpeg.exe", CheckFileExists = true }; if (d.ShowDialog(this) == true) _ffmpeg.Text = d.FileName; return Task.CompletedTask; });
        _quality.ItemsSource = new[] { "480p", "720p", "1080p" }; _quality.SelectedIndex = options.TranscodeHeight switch { 480 => 0, 1080 => 2, _ => 1 }; Field(optionsPanel, "Качество при преобразовании", _quality);
        Note(optionsPanel, "При ошибке кодека iPhone сначала попробует AAC-звук без изменения видео, сохранив UHD. Полное преобразование видео в H.264 запускается отдельной кнопкой на телефоне и может повысить нагрузку CPU. Одновременно работает одна задача.");
        Heading(optionsPanel, "Разрешения пульта", 18);
        Check(optionsPanel, _control, "Управлять Windows-плеером", options.AllowControl);
        Check(optionsPanel, _volume, "Менять громкость и выключать звук", options.AllowVolume);
        Check(optionsPanel, _multi, "Управлять мультиэкраном", options.AllowMultiscreen);
        Check(optionsPanel, _favorites, "Менять избранное и подборки", options.AllowFavorites);
        AddButton(optionsPanel, "Сохранить и применить", async () => { var running = _remote.Running; if (running || _transcode.IsChecked == true) await PrepareAsync(); await SaveAsync(); if (running) { await _remote.StartAsync(Read(), _lifetime.Token); RefreshCode(); } Refresh(); _status.Text = running ? "Настройки применены. Remote работает." : "Настройки сохранены."; });
        Heading(page, "Диагностика", 18); page.Children.Add(_diagnostic);
        AddButton(page, "Проверить подключение", DiagnoseAsync);
        _start.Click += async (_, _) => await RunAsync(async () =>
        {
            if (_remote.Running) { await _remote.StopAsync(); _pairingUrl = null; }
            else { await PrepareAsync(); await SaveAsync(); await _remote.StartAsync(Read(), _lifetime.Token); RefreshCode(); }
            Refresh();
        });
        System.Windows.Automation.AutomationProperties.SetName(_address, "Сетевой адаптер Remote");
        System.Windows.Automation.AutomationProperties.SetName(_ffmpeg, "FFmpeg для совместимости Remote");
        System.Windows.Automation.AutomationProperties.SetName(_qr, "QR-код подключения телефона");
        System.Windows.Automation.AutomationProperties.SetName(_preparation, "Подготовка компонентов Remote");
        _timer.Tick += (_, _) => Refresh(); _timer.Start(); Closed += (_, _) => { _closed = true; _timer.Stop(); _lifetime.Cancel(); };
        if (_remote.Running) RefreshCode(); Refresh(); DescribeNetwork();
    }
    private string SelectedAddress => _address.SelectedIndex >= 0 && _address.SelectedIndex < _addressChoices.Length ? _addressChoices[_address.SelectedIndex].Address : "";
    private void RefreshAddresses(string? preferred = null)
    {
        preferred ??= SelectedAddress;
        _addressChoices = [new("", "Автоматически · домашняя сеть"), .. RemoteNetwork.Addresses().Select(a => new AddressChoice(a.Address, a.ToString()))];
        _address.ItemsSource = _addressChoices.Select(a => a.Label).ToArray();
        _address.SelectedIndex = Math.Max(0, Array.FindIndex(_addressChoices, a => a.Address == preferred));
    }
    private void DescribeNetwork()
    {
        if (_remote.Running) { _network.Text = "Сервер доступен устройствам в выбранной домашней сети."; return; }
        var addresses = RemoteNetwork.Addresses();
        var selected = SelectedAddress.Length > 0 ? addresses.FirstOrDefault(a => a.Address == SelectedAddress) : addresses.FirstOrDefault(a => a.Private && !a.Virtual) ?? addresses.FirstOrDefault(a => !a.Virtual) ?? addresses.FirstOrDefault();
        _network.Text = selected is null ? "Подключите компьютер к домашней сети Ethernet или Wi-Fi и нажмите «Обновить сеть»." : selected.Private ? $"{selected.Adapter} · {selected.Address} · частная сеть. Можно включить Remote." :
            $"{selected.Adapter} · {selected.Address} · Windows использует профиль «Публичная». Для запуска откройте свойства этой домашней сети, выберите «Частная», затем нажмите «Обновить сеть».";
    }
    private RemoteOptions Read(bool requireFfmpeg = true)
    {
        if (!int.TryParse(_port.Text, out int port) || !int.TryParse(_limit.Text, out int limit)) { _settings.IsExpanded = true; throw new RemoteException("Порт и число потоков должны быть целыми числами."); }
        var options = new RemoteOptions { AutoStart = _auto.IsChecked == true, Address = SelectedAddress, Port = port, MaximumStreams = limit,
            Delivery = _delivery.SelectedIndex switch { 1 => "auto", 2 => "direct", _ => "proxy" }, AllowDirectUrls = _direct.IsChecked == true,
            AllowViewing = _view.IsChecked == true, AllowControl = _control.IsChecked == true, AllowVolume = _volume.IsChecked == true, AllowMultiscreen = _multi.IsChecked == true,
            AllowFavorites = _favorites.IsChecked == true, AllowTranscoding = _transcode.IsChecked == true, DependenciesPrepared = _dependenciesPrepared, FfmpegPath = _ffmpeg.Text.Trim(), TranscodeHeight = _quality.SelectedIndex switch { 0 => 480, 2 => 1080, _ => 720 } };
        options.Validate(requireFfmpeg); return options;
    }
    private async Task SaveAsync() { _lifetime.Token.ThrowIfCancellationRequested(); _main.Model.SetSetting("remote.options", JsonSerializer.Serialize(Read())); await _main.Model.SaveAsync(); _lifetime.Token.ThrowIfCancellationRequested(); }
    private async Task PrepareAsync()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _preparationCancellation = cancellation;
        _preparation.Visibility = _cancelPreparation.Visibility = Visibility.Visible;
        _preparation.IsIndeterminate = true;
        var progress = new Progress<RemotePreparation>(p =>
        {
            if (_closed || cancellation.IsCancellationRequested || _preparationCancellation != cancellation) return;
            _status.Text = p.Message; _preparation.IsIndeterminate = p.Percent is null; _preparation.Value = p.Percent ?? 0;
        });
        try
        {
            var options = await _main.PrepareRemoteOptionsAsync(Read(requireFfmpeg: false), progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            _ffmpeg.Text = options.FfmpegPath; _transcode.IsChecked = options.AllowTranscoding; _dependenciesPrepared = options.DependenciesPrepared;
        }
        finally { _preparationCancellation = null; _preparation.Visibility = _cancelPreparation.Visibility = Visibility.Collapsed; }
    }
    private async Task EnableCompatibilityAsync()
    {
        _transcode.IsChecked = true; bool running = _remote.Running;
        await PrepareAsync();
        await SaveAsync();
        if (running) { await _remote.StartAsync(Read(), _lifetime.Token); RefreshCode(); }
        _status.Text = "Совместимость iPhone включена. Обновите страницу на телефоне и выберите канал заново.";
    }
    private void RefreshCode()
    {
        _pairingUrl = _remote.PairingUrl(); _qr.Source = RemoteQrCode.Create(_pairingUrl);
        _status.Text = "Remote работает · код действует 3 минуты";
    }
    private void Refresh()
    {
        _start.Content = _remote.Running ? "Выключить Remote" : "Включить Remote"; _start.IsEnabled = !_busy;
        bool compatibilityEnabled = _main.RemoteOptions().AllowTranscoding;
        _compatibility.Content = compatibilityEnabled ? "Совместимость iPhone включена" : "Включить совместимость iPhone";
        _compatibility.IsEnabled = !_busy && !compatibilityEnabled;
        _url.Text = _remote.Url ?? "Сначала включите Remote";
        _settings.IsEnabled = !_busy;
        if (!_busy && _lastServerStatus != _remote.Status) { _lastServerStatus = _remote.Status; _status.Text = _remote.Status; DescribeNetwork(); }
        bool codeVisible = _remote.Running && _qr.Source is not null;
        _qr.Visibility = codeVisible ? Visibility.Visible : Visibility.Collapsed; _placeholder.Visibility = codeVisible ? Visibility.Collapsed : Visibility.Visible;
        if (codeVisible) _qrFrame.Background = Brushes.White; else _qrFrame.SetResourceReference(BackgroundProperty, "CardBrush");
        _refreshCode.IsEnabled = _copy.IsEnabled = _remote.Running && !_busy;
        var devices = _remote.Devices.Devices; var key = string.Join('|', devices.Select(d => d.Id + d.Connected));
        if (_deviceSnapshot != key || _devices.Children.Count == 0)
        {
            _deviceSnapshot = key; _devices.Children.Clear();
            if (devices.Count == 0) Note(_devices, "Пока нет подключённых устройств.");
            foreach (var d in devices)
            {
                var line = new DockPanel { Margin = new(0, 5, 0, 5) };
                var revoke = new Button { Content = "Отключить", FontSize = 12, Padding = new(9, 5, 9, 5) }; DockPanel.SetDock(revoke, Dock.Right);
                revoke.Click += (_, _) => { _remote.Devices.Revoke(d.Id); Refresh(); }; line.Children.Add(revoke);
                line.Children.Add(new TextBlock { Text = $"{d.Name}\n{(d.Connected ? "В сети" : "Сопряжено")}", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }); _devices.Children.Add(line);
            }
        }
        var streams = _remote.Streams.Active;
        _diagnostic.Text = $"Устройств в сети: {devices.Count(d => d.Connected)} · Потоков: {streams.Count}/{_remote.Options.MaximumStreams} · Передано: {streams.Sum(s => s.Bytes) / 1048576d:F1} МБ" +
            string.Concat(streams.Select(s => $"\n{_main.Model.AllChannels.Find(c => c.Id == s.ChannelId)?.Name ?? "Канал"} · {(s.Mode == "transcode" ? "H.264 / AAC" : s.Mode == "compatible" ? "Исходное видео / AAC" : s.Mode == "direct" ? "Напрямую" : "HLS-прокси")}")) +
            (_remote.LastPlaybackError is { } error ? "\nПоследняя ошибка видео: " + error : "");
    }
    private async Task DiagnoseAsync()
    {
        RefreshAddresses(); DescribeNetwork();
        if (!_remote.Running) { _status.Text = "Remote выключен. Проверьте сведения о сети выше и нажмите «Включить Remote»."; return; }
        using var client = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(4) };
        var response = await client.GetAsync(_remote.Url + "/api/status");
        _status.Text = response.IsSuccessStatusCode ? "Сервер отвечает на компьютере." : "Сервер не ответил. Перезапустите Remote.";
        _network.Text = "На телефоне выберите ту же домашнюю сеть. Если QR не открывается, проверьте изоляцию гостевого Wi-Fi, VPN и разрешение DzhiggaTV в брандмауэре Windows для частных сетей.";
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _closed) return; _busy = true; Refresh();
        try { await action(); }
        catch (OperationCanceledException) { if (!_closed) _status.Text = "Подготовка отменена. Можно повторить включение Remote."; }
        catch (RemoteException e) { if (!_closed) { _status.Text = e.Message; DescribeNetwork(); } }
        catch { if (!_closed) _status.Text = "Не удалось выполнить действие. Проверьте сеть и выбранные параметры."; }
        finally { _busy = false; _lastServerStatus = _remote.Status; if (!_closed) Refresh(); }
    }
    private void AddButton(Panel panel, string text, Func<Task> action)
    { var b = new Button { Content = text, Margin = new(0, 6, 8, 10), HorizontalAlignment = HorizontalAlignment.Left }; b.Click += async (_, _) => { b.IsEnabled = false; await RunAsync(action); b.IsEnabled = true; }; panel.Children.Add(b); }
    private static Border Card(UIElement content)
    { var card = new Border { Child = content, Padding = new(17), CornerRadius = new(14), BorderThickness = new(1), Margin = new(0, 0, 0, 14) }; card.SetResourceReference(BackgroundProperty, "CardBrush"); card.SetResourceReference(BorderBrushProperty, "BorderBrush"); return card; }
    private static void Heading(Panel panel, string text, double size) => panel.Children.Add(new TextBlock { Text = text, FontSize = size, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 12) });
    private static void Note(Panel panel, string text)
    { var note = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 14), LineHeight = 21 }; note.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); panel.Children.Add(note); }
    private static void Check(Panel panel, CheckBox box, string label, bool value) { box.Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }; box.IsChecked = value; panel.Children.Add(box); }
    private static void Field(Panel panel, string label, Control control) { panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 6) }); panel.Children.Add(control); }
    private sealed record AddressChoice(string Address, string Label);
}
