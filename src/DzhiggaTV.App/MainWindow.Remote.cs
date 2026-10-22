using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DzhiggaTV.Remote;

namespace DzhiggaTV.App;

public partial class MainWindow
{
    public RemoteServer Remote { get; private set; } = null!;
    private readonly Dictionary<string, Window> _pairingWindows = [];
    public Task ShowRemoteAsync() => WithModalAsync(() =>
    {
        if (Remote is not null) new RemoteSettingsWindow(this, this).ShowDialog();
        return Task.CompletedTask;
    });
    public RemoteOptions RemoteOptions()
    {
        try { return JsonSerializer.Deserialize<RemoteOptions>(Model.Setting("remote.options", "{}")) ?? new(); }
        catch (JsonException) { return new(); }
    }
    private void InitializeRemote()
    {
        Remote = new(new DesktopRemoteLibrary(this), Model.DataDirectory);
        Remote.Changed += () => Dispatcher.BeginInvoke(DispatcherPriority.Background, ShowRemotePairing);
        if (RemoteOptions() is { AutoStart: true } options) _ = GuardAsync(async () => { try { await Remote.StartAsync(options); } catch (RemoteException e) { Model.Status = e.Message; } });
    }
    private void ShowRemotePairing()
    {
        if (_closing) return;
        var pending = Remote.Devices.PendingRequests;
        foreach (var item in _pairingWindows.ToArray()) if (!pending.Any(p => p.Id == item.Key)) { item.Value.Close(); _pairingWindows.Remove(item.Key); }
        foreach (var request in pending.Where(p => !_pairingWindows.ContainsKey(p.Id)))
        {
            var dialog = new Window { Title = "DzhiggaTV Remote · Новое устройство", Owner = Application.Current.Windows.OfType<Window>().LastOrDefault(w => w.IsActive) ?? this, Width = 470, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
            Theme.Window(dialog);
            var panel = new StackPanel { Margin = new(26) }; dialog.Content = panel;
            panel.Children.Add(new TextBlock { Text = "Разрешить подключение?", FontSize = 24, Margin = new(0, 0, 0, 14) });
            panel.Children.Add(new TextBlock { Text = request.Name, FontSize = 17, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = request.Verification, FontSize = 36, Foreground = System.Windows.Media.Brushes.Turquoise, Margin = new(0, 16, 0, 12) });
            panel.Children.Add(new TextBlock { Text = "Сверьте этот код на телефоне. Разрешайте доступ только знакомому устройству. HTTP в домашней сети не шифрует трафик.", TextWrapping = TextWrapping.Wrap });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 20, 0, 0) };
            var deny = new Button { Content = "Отклонить" }; var allow = new Button { Content = "Разрешить", Margin = new(8, 0, 0, 0) }; allow.SetResourceReference(BorderBrushProperty, "AccentBrush");
            deny.Click += (_, _) => { Remote.Devices.Decide(request.Id, false); dialog.Close(); };
            allow.Click += (_, _) => { Remote.Devices.Decide(request.Id, true); dialog.Close(); };
            buttons.Children.Add(deny); buttons.Children.Add(allow); panel.Children.Add(buttons);
            dialog.Closed += (_, _) => { Remote.Devices.Decide(request.Id, false); _pairingWindows.Remove(request.Id); };
            _pairingWindows.Add(request.Id, dialog); dialog.Show();
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timeout.Tick += (_, _) => { if (!Remote.Devices.PendingRequests.Any(p => p.Id == request.Id)) { timeout.Stop(); if (dialog.IsLoaded) dialog.Close(); } }; timeout.Start();
        }
    }
    internal RemotePlayerState RemoteState()
    {
        var sports = _sports;
        var active = Model.Active is { } c && Model.IsAllowed(c) ? c : null;
        return new(active?.Id, active?.Name ?? "Канал не выбран", RemoteProgramme.From(active is null ? null : Model.PlayingProgramme), Player.Status.Message,
            sports?.Volume ?? Player.Volume, sports?.IsMuted ?? Player.IsMuted, Player.IsPaused, sports?.RemoteFullscreen ?? IsFullscreen,
            Player.GetTracks().Where(t => t.Type == "audio").Select(t => new RemoteTrack(t.Id, t.DisplayName, t.Selected)).ToArray(),
            new(sports is not null, sports?.RemoteFullscreen ?? false, sports?.RemoteAudioId, sports?.ExpandedChannelId,
                sports?.ChannelLayout.Select(id => id is not null && Model.AllChannels.Any(c => c.Id == id && Model.IsAllowed(c)) ? id : null).ToArray() ?? []), Model.RemoteRevision);
    }
    internal async Task RemoteCommandAsync(RemoteCommand command, CancellationToken token)
    {
        if (_closing || !_initialized) throw new RemoteException("Проигрыватель ещё не готов.", 409);
        ChannelItem Channel(string? id) => Model.AllChannels.Find(c => c.Id == id && Model.IsAllowed(c)) ?? throw new RemoteException("Канал недоступен.", 404);
        switch (command.Action)
        {
            case "play": await PlayAsync(Channel(command.ChannelId)); break;
            case "next": case "previous":
                var channels = Model.AllChannels.Where(Model.IsAllowed).ToArray();
                if (channels.Length == 0) break;
                int i = Array.FindIndex(channels, c => c.Id == Model.Active?.Id);
                await PlayAsync(channels[(i + (command.Action == "next" ? 1 : -1) + channels.Length) % channels.Length]); break;
            case "back": if (Model.PreviousId is { } previous) await PlayAsync(Channel(previous)); break;
            case "pause": Player.TogglePause(); break;
            case "volume":
                if (command.Value is not { } volume || !double.IsFinite(volume)) throw new RemoteException("Некорректная громкость.");
                if (_sports is { } volumeSports) volumeSports.RemoteVolume(volume, null); else Player.Volume = Math.Clamp(volume, 0, 100); break;
            case "mute": if (_sports is { } muteSports) muteSports.RemoteVolume(null, !muteSports.IsMuted); else Player.IsMuted = !Player.IsMuted; break;
            case "fullscreen": if (_sports is { } fullSports) { if (fullSports.RemoteFullscreen != (command.Value != 0)) fullSports.ToggleFullscreen(); } else if (IsFullscreen != (command.Value != 0)) ToggleFullscreen(); break;
            case "audio":
                if (_sports is not null) throw new RemoteException("Выберите источник звука в карточках мультиэкрана.", 409);
                if (command.Value is not { } track || !Player.GetTracks().Any(t => t.Type == "audio" && t.Id == track)) throw new RemoteException("Дорожка недоступна.");
                Player.SelectAudio((long)track); break;
            case "multiscreen.open":
                if (_sports is null) _ = Dispatcher.BeginInvoke(new Action(() => _ = ShowSportsEveningAsync())); break;
            case "multiscreen.close": _sports?.Close(); break;
            case "multiscreen.layout":
                if (_sports is null) throw new RemoteException("Сначала включите мультиэкран.", 409);
                if (command.Channels is not { Length: >= 1 and <= 4 } ids) throw new RemoteException("Можно выбрать от одного до четырёх экранов.");
                _sports.SetLayout(ids.Select(id => id is null ? null : Channel(id)), _sports.RemoteAudioId); break;
            case "multiscreen.audio": _sports?.SelectAudio(Channel(command.ChannelId).Id); break;
            case "multiscreen.focus": _sports?.ToggleChannelFullscreen(Channel(command.ChannelId).Id); break;
            default: throw new RemoteException("Неизвестная команда.");
        }
    }
}
