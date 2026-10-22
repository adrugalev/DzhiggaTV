using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DzhiggaTV.Remote;
using Microsoft.Win32;
using QRCoder;

namespace DzhiggaTV.App;

public sealed class RemoteSettingsWindow : Window
{
    private readonly MainWindow _main;
    private readonly RemoteServer _remote;
    private readonly ComboBox _address = new(), _delivery = new(), _quality = new();
    private readonly TextBox _port = new(), _limit = new(), _ffmpeg = new();
    private readonly CheckBox _auto = new(), _view = new(), _control = new(), _volume = new(), _multi = new(), _favorites = new(), _direct = new(), _transcode = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Turquoise, Margin = new(0, 0, 0, 12) };
    private readonly TextBlock _url = new() { TextWrapping = TextWrapping.Wrap, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 12, 0, 8) };
    private readonly TextBlock _diagnostic = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, FontSize = 12 };
    private readonly Image _qr = new() { Width = 238, Height = 238, Stretch = Stretch.Uniform };
    private readonly StackPanel _devices = new();
    private readonly Button _start = new() { MinWidth = 140 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string? _pairingUrl;
    private string _deviceSnapshot = "";
    private string _lastServerStatus = "";
    private bool _busy;

    public RemoteSettingsWindow(MainWindow main, Window owner)
    {
        _main = main; _remote = main.Remote;
        Theme.Window(this); Owner = owner; Title = "DzhiggaTV Remote · Подключите телефон";
        Width = 1050; Height = 820; MinWidth = 840; MinHeight = 620; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MaxHeight = Math.Max(620, SystemParameters.WorkArea.Height - 50);
        var root = new Grid { Margin = new(26) }; root.ColumnDefinitions.Add(new()); root.ColumnDefinitions.Add(new() { Width = new(365) }); Content = root;
        var optionsPanel = new StackPanel { Margin = new(0, 0, 28, 0) };
        root.Children.Add(new ScrollViewer { Content = optionsPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Heading(optionsPanel, "DzhiggaTV Remote", 26); Note(optionsPanel, "Телевизор и пульт на вашем телефоне. Компьютер и телефон должны находиться в одной домашней сети.");
        var options = main.RemoteOptions();
        var addresses = new[] { new AddressChoice("", "Автоматически · домашняя сеть") }.Concat(RemoteNetwork.Addresses().Select(a => new AddressChoice(a.Address, a.ToString()))).ToArray();
        _address.ItemsSource = addresses; _address.DisplayMemberPath = "Label"; _address.SelectedItem = addresses.FirstOrDefault(a => a.Address == options.Address) ?? addresses[0];
        Field(optionsPanel, "Сетевой адаптер", _address); _port.Text = options.Port.ToString(); Field(optionsPanel, "Порт", _port);
        Check(optionsPanel, _auto, "Запускать Remote вместе с DzhiggaTV", options.AutoStart);
        Heading(optionsPanel, "Просмотр", 18); Check(optionsPanel, _view, "Разрешить просмотр ТВ на телефоне", options.AllowViewing);
        _limit.Text = options.MaximumStreams.ToString(); Field(optionsPanel, "Одновременные мобильные потоки · 1–4", _limit);
        _delivery.ItemsSource = new[] { "Через локальный прокси", "Автоматически · прокси без раскрытия адресов", "Прямое воспроизведение" };
        _delivery.SelectedIndex = options.Delivery switch { "auto" => 1, "direct" => 2, _ => 0 }; Field(optionsPanel, "Передача видео", _delivery);
        Check(optionsPanel, _direct, "Разрешить исходные IPTV-адреса на телефоне", options.AllowDirectUrls);
        Note(optionsPanel, "Прямой режим раскрывает телефону адрес потока, включая токен провайдера. По умолчанию используется прокси без перекодирования.");
        Check(optionsPanel, _transcode, "Разрешить преобразование через FFmpeg", options.AllowTranscoding);
        _ffmpeg.Text = options.FfmpegPath; Field(optionsPanel, "Путь к ffmpeg.exe", _ffmpeg);
        AddButton(optionsPanel, "Выбрать ffmpeg.exe…", () => { var d = new OpenFileDialog { Filter = "FFmpeg|ffmpeg.exe", CheckFileExists = true }; if (d.ShowDialog(this) == true) _ffmpeg.Text = d.FileName; return Task.CompletedTask; });
        _quality.ItemsSource = new[] { "480p", "720p", "1080p" }; _quality.SelectedIndex = options.TranscodeHeight switch { 480 => 0, 1080 => 2, _ => 1 }; Field(optionsPanel, "Максимальная высота при преобразовании", _quality);
        Note(optionsPanel, "FFmpeg нужен только несовместимым потокам. Одна задача, H.264/AAC, до двух потоков CPU; нагрузка на процессор может увеличиться. Кнопка преобразования доступна на телефоне после ошибки видео.");
        Heading(optionsPanel, "Разрешения пульта", 18);
        Check(optionsPanel, _control, "Управлять Windows-плеером", options.AllowControl);
        Check(optionsPanel, _volume, "Менять громкость и выключать звук", options.AllowVolume);
        Check(optionsPanel, _multi, "Управлять мультиэкраном", options.AllowMultiscreen);
        Check(optionsPanel, _favorites, "Менять избранное и подборки", options.AllowFavorites);
        AddButton(optionsPanel, "Сохранить и применить", async () => { var running = _remote.Running; await SaveAsync(); if (running) { await _remote.StartAsync(Read()); RefreshCode(); } _status.Text = running ? "Настройки применены. Remote перезапущен." : "Настройки сохранены."; });

        var right = new StackPanel(); Grid.SetColumn(right, 1);
        root.Children.Add(new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Heading(right, "Подключите телефон", 23); Note(right, "Откройте камеру iPhone и отсканируйте QR-код. Подтвердите новое устройство на компьютере.");
        right.Children.Add(_status);
        var qrFrame = new Border { Background = Brushes.White, CornerRadius = new(18), Padding = new(14), Child = _qr, HorizontalAlignment = HorizontalAlignment.Center }; right.Children.Add(qrFrame); right.Children.Add(_url);
        var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        AddButton(row, "Обновить код", () => { RefreshCode(); return Task.CompletedTask; });
        AddButton(row, "Копировать ссылку", () => { if (_pairingUrl is null) throw new RemoteException("Сначала включите Remote."); Clipboard.SetText(_pairingUrl); _status.Text = "Одноразовая ссылка скопирована. Она действует 3 минуты."; return Task.CompletedTask; }); right.Children.Add(row);
        _start.Click += async (_, _) => await RunAsync(async () => { if (_remote.Running) { await _remote.StopAsync(); _pairingUrl = null; _qr.Source = null; } else { await SaveAsync(); await _remote.StartAsync(Read()); RefreshCode(); } Refresh(); }); right.Children.Add(_start);
        Note(right, "HTTP в локальной сети не шифрует трафик. Не используйте Remote в чужой или публичной сети. Компьютер должен оставаться включённым.");
        Heading(right, "Устройства", 18); right.Children.Add(_devices);
        AddButton(right, "Отозвать доступ у всех", () => { _remote.Devices.Revoke(null); if (_remote.Running) RefreshCode(); Refresh(); return Task.CompletedTask; });
        Heading(right, "Подключение и диагностика", 18); right.Children.Add(_diagnostic);
        AddButton(right, "Проверить подключение", DiagnoseAsync);
        _timer.Tick += (_, _) => Refresh(); _timer.Start(); Closed += (_, _) => _timer.Stop();
        if (_remote.Running) RefreshCode(); Refresh();
    }
    private RemoteOptions Read()
    {
        if (!int.TryParse(_port.Text, out int port) || !int.TryParse(_limit.Text, out int limit)) throw new RemoteException("Порт и число потоков должны быть целыми числами.");
        var options = new RemoteOptions { AutoStart = _auto.IsChecked == true, Address = (_address.SelectedItem as AddressChoice)?.Address ?? "", Port = port, MaximumStreams = limit,
            Delivery = _delivery.SelectedIndex switch { 1 => "auto", 2 => "direct", _ => "proxy" }, AllowDirectUrls = _direct.IsChecked == true,
            AllowViewing = _view.IsChecked == true, AllowControl = _control.IsChecked == true, AllowVolume = _volume.IsChecked == true, AllowMultiscreen = _multi.IsChecked == true,
            AllowFavorites = _favorites.IsChecked == true, AllowTranscoding = _transcode.IsChecked == true, FfmpegPath = _ffmpeg.Text.Trim(), TranscodeHeight = _quality.SelectedIndex switch { 0 => 480, 2 => 1080, _ => 720 } };
        options.Validate(); return options;
    }
    private async Task SaveAsync() { _main.Model.SetSetting("remote.options", JsonSerializer.Serialize(Read())); await _main.Model.SaveAsync(); }
    private void RefreshCode()
    {
        _pairingUrl = _remote.PairingUrl();
        _qr.Source = RemoteQrCode.Create(_pairingUrl);
        _status.Text = "Код действует 3 минуты · подключение подтверждается на ПК";
    }
    private void Refresh()
    {
        _start.Content = _remote.Running ? "Выключить Remote" : "Включить Remote"; _start.IsEnabled = !_busy;
        _url.Text = _remote.Url ?? "Включите Remote, чтобы получить адрес";
        if (_lastServerStatus != _remote.Status) { _lastServerStatus = _remote.Status; _status.Text = _remote.Status; }
        if (!_remote.Running) _qr.Source = null;
        var devices = _remote.Devices.Devices; var key = string.Join('|', devices.Select(d => d.Id + d.Connected));
        if (_deviceSnapshot != key || _devices.Children.Count == 0)
        {
            _deviceSnapshot = key; _devices.Children.Clear();
            if (devices.Count == 0) Note(_devices, "Подключённых устройств пока нет.");
            foreach (var d in devices)
            {
                var line = new DockPanel { Margin = new(0, 5, 0, 5) };
                var revoke = new Button { Content = "Отключить", FontSize = 11, Padding = new(9, 5, 9, 5) }; DockPanel.SetDock(revoke, Dock.Right);
                revoke.Click += (_, _) => { _remote.Devices.Revoke(d.Id); Refresh(); }; line.Children.Add(revoke);
                line.Children.Add(new TextBlock { Text = $"{d.Name}\n{(d.Connected ? "В сети" : "Сопряжено")}", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }); _devices.Children.Add(line);
            }
        }
        var streams = _remote.Streams.Active;
        _diagnostic.Text = $"{_remote.Status}\nУстройств в сети: {devices.Count(d => d.Connected)} · Потоков: {streams.Count}/{_remote.Options.MaximumStreams}\nПередано: {streams.Sum(s => s.Bytes) / 1048576d:F1} МБ" +
            string.Concat(streams.Select(s => $"\n{_main.Model.AllChannels.Find(c => c.Id == s.ChannelId)?.Name ?? "Канал"} · {(s.Mode == "transcode" ? "FFmpeg" : s.Mode == "direct" ? "Напрямую" : "HLS-прокси")}")) +
            (streams.Any(s => s.Mode == "transcode") ? "\nРаботает FFmpeg · возможна повышенная нагрузка CPU" : "") + (_remote.LastPlaybackError is { } error ? "\nПоследняя ошибка видео: " + error : "");
    }
    private async Task DiagnoseAsync()
    {
        var address = Read().Address;
        var selected = await Task.Run(() => RemoteNetwork.Select(address));
        string result = $"Адаптер: {selected.Adapter}, {selected.Address}. Сеть частная. ";
        if (!_remote.Running) result += "Remote выключен. Нажмите «Включить Remote».";
        else
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            var response = await client.GetAsync(_remote.Url + "/api/status");
            result += response.IsSuccessStatusCode ? "Сервер отвечает на этом компьютере. " : "Сервер не отвечает. ";
            result += "На телефоне выберите ту же домашнюю сеть. Гостевой Wi-Fi может запрещать связь между устройствами. Если телефон не подключается, разрешите DzhiggaTV в брандмауэре Windows только для частных сетей; проверьте VPN и актуальный IP-адрес.";
        }
        _status.Text = result;
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return; _busy = true; _start.IsEnabled = false;
        try { await action(); } catch (RemoteException e) { _status.Text = e.Message; } catch { _status.Text = "Не удалось выполнить действие. Проверьте сеть и выбранные параметры."; }
        finally { _busy = false; _start.IsEnabled = true; }
    }
    private void AddButton(Panel panel, string text, Func<Task> action)
    { var b = new Button { Content = text, Margin = new(0, 8, 7, 8), HorizontalAlignment = HorizontalAlignment.Left }; b.Click += async (_, _) => { b.IsEnabled = false; await RunAsync(action); b.IsEnabled = true; }; panel.Children.Add(b); }
    private static void Heading(Panel panel, string text, double size) => panel.Children.Add(new TextBlock { Text = text, FontSize = size, FontWeight = FontWeights.SemiBold, Margin = new(0, 12, 0, 12) });
    private static void Note(Panel panel, string text) => panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new(0, 4, 0, 14), LineHeight = 20 });
    private static void Check(Panel panel, CheckBox box, string label, bool value) { box.Content = label; box.IsChecked = value; panel.Children.Add(box); }
    private static void Field(Panel panel, string label, Control control) { panel.Children.Add(new TextBlock { Text = label, Margin = new(0, 10, 0, 6) }); panel.Children.Add(control); }
    private sealed record AddressChoice(string Address, string Label);
}
