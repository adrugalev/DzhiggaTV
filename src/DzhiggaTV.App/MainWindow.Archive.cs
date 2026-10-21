using DzhiggaTV.Core;
using DzhiggaTV.Player;

namespace DzhiggaTV.App;

public sealed record ArchiveTimeline(DateTimeOffset Start, DateTimeOffset End, DateTimeOffset Position, bool IsArchive);

public partial class MainWindow
{
    private async void PreviousProgramme_Click(object sender, System.Windows.RoutedEventArgs e) => await GuardAsync(Programmes.GoBackAsync);
    private async void FollowingProgramme_Click(object sender, System.Windows.RoutedEventArgs e) => await GuardAsync(Programmes.GoForwardAsync);
    private async void FollowPlayback_Click(object sender, System.Windows.RoutedEventArgs e) => await GuardAsync(Programmes.FollowPlaybackAsync);
    private async void PlayBrowsedProgramme_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (Programmes.CanPlay && Programmes.Current is { } programme && Model.Active is { } channel)
            await GuardAsync(() => PlayArchiveAsync(channel, programme));
    }

    private sealed record ArchivePlayback(ChannelItem Channel, Programme Programme, DateTimeOffset LoadedStart, DateTimeOffset LoadedEnd);
    private ArchivePlayback? _archivePlayback;
    public bool IsArchivePlayback => _archivePlayback is not null;
    private void ClearArchivePlayback() { _archivePlayback = null; Model.SetArchiveProgramme(null); }

    public ArchiveTimeline? GetArchiveTimeline()
    {
        if (Model.Active is not { Archive: { } source } channel) return null;
        var now = DateTimeOffset.Now;
        if (_archivePlayback is { } archive)
        {
            var native = Player.GetTimeline();
            var position = native.Position is { } seconds ? archive.LoadedStart.AddSeconds(Math.Max(0, seconds)) : Player.State == PlaybackState.Idle ? archive.LoadedEnd : archive.LoadedStart;
            position = position < archive.Programme.Start ? archive.Programme.Start : position > archive.LoadedEnd ? archive.LoadedEnd : position;
            Model.SetArchivePosition(position);
            return new(archive.Programme.Start, archive.LoadedEnd, position, true);
        }
        DateTimeOffset start = channel.Current?.Start ?? now.AddHours(-1);
        if (start < source.Earliest(now)) start = source.Earliest(now);
        return start < now ? new(start, now, now, false) : null;
    }

    public async Task PlayArchiveAsync(ChannelItem channel, Programme programme)
    {
        if (!_engineReady || !Model.IsAllowed(channel)) return;
        if (!Catchup.CanPlay(channel.Channel, programme, DateTimeOffset.Now))
            throw new InvalidOperationException("Эта передача недоступна в архиве канала.");
        await StartArchiveAsync(channel, programme, programme.Start);
    }

    public async Task PlayArchiveAtAsync(ChannelItem channel, DateTimeOffset time)
    {
        if (!_engineReady || !Model.IsAllowed(channel)) return;
        var now = DateTimeOffset.Now;
        if (channel.Archive is not { } source || !source.Contains(time, now)) throw new InvalidOperationException("Выбранное время недоступно в архиве канала.");
        var lookupGeneration = ++_playGeneration;
        Programme? programme = null;
        if (Model.Mappings.TryGetValue(channel.Id, out var mapping))
            programme = (await Model.Store.GetNowNextAsync(mapping.EpgId, time)).Current;
        if (lookupGeneration != _playGeneration) return;
        programme ??= new Programme(mapping?.EpgId ?? channel.Id, "Архив · " + time.ToLocalTime().ToString("dd.MM HH:mm"), time,
            time.AddHours(2) < now ? time.AddHours(2) : now);
        await StartArchiveAsync(channel, programme, time);
    }

    private async Task StartArchiveAsync(ChannelItem channel, Programme programme, DateTimeOffset start, bool paused = false)
    {
        if (!_engineReady || !Model.IsAllowed(channel)) return;
        var now = DateTimeOffset.Now; var end = programme.End < now ? programme.End : now;
        string url = Catchup.BuildUrl(channel.Channel, start, end, now);
        var generation = ++_playGeneration;
        _playCancellation?.Cancel(); _playCancellation?.Dispose(); _playCancellation = new(); var token = _playCancellation.Token;
        await Model.ActivateChannelAsync(channel);
        if (generation != _playGeneration || token.IsCancellationRequested) return;
        NowNext? schedule = null;
        if (Model.Mappings.TryGetValue(channel.Id, out var mapping)) schedule = await Model.Store.GetNowNextAsync(mapping.EpgId, programme.Start.AddSeconds(1), token);
        if (generation != _playGeneration || token.IsCancellationRequested) return;
        _archivePlayback = new(channel, programme, start, end);
        Model.SetArchiveProgramme(programme, schedule?.Current is null ? null : schedule.Next, schedule?.Current is null ? null : schedule.AfterNext);
        HasPlayback = true; Model.PlaybackStatus = "Открываем архив…"; _overlay?.RefreshState();
        await Player.PlayArchiveAsync(url, (end - start).TotalSeconds, token);
        if (generation != _playGeneration || token.IsCancellationRequested) return;
        if (paused) Player.IsPaused = true;
        Model.State.RecordWatched(channel.Id); Model.SetSetting("last.channel", channel.Id); _ = GuardAsync(Model.SaveAsync); _overlay?.Reveal();
    }

    public async Task SeekArchiveToAsync(DateTimeOffset time)
    {
        if (_archivePlayback is not { } archive)
        {
            if (time >= DateTimeOffset.Now.AddSeconds(-1)) return;
            if (Model.Active is { } channel) await PlayArchiveAtAsync(channel, time);
            return;
        }
        var earliest = archive.Channel.Archive!.Earliest(DateTimeOffset.Now);
        if (time < earliest || time < archive.Programme.Start) time = earliest > archive.Programme.Start ? earliest : archive.Programme.Start;
        if (time >= archive.LoadedEnd) time = archive.LoadedEnd.AddSeconds(-1);
        var generation = _playGeneration; bool paused = Player.IsPaused;
        bool reload = time < archive.LoadedStart || Player.State is PlaybackState.Idle or PlaybackState.Error;
        if (!reload) reload = !await Player.SeekAsync((time - archive.LoadedStart).TotalSeconds);
        if (generation != _playGeneration || !ReferenceEquals(_archivePlayback, archive)) return;
        if (reload) await StartArchiveAsync(archive.Channel, archive.Programme, time, paused);
        else if (ReferenceEquals(_archivePlayback, archive)) Model.SetArchivePosition(time);
        _overlay?.RefreshState();
    }
    public Task SeekPlaybackByAsync(double seconds) => GetArchiveTimeline() is { } timeline
        ? SeekArchiveToAsync(timeline.Position.AddSeconds(seconds)) : Task.CompletedTask;
    public Task ReturnToLiveAsync() => Model.Active is { } channel ? PlayAsync(channel) : Task.CompletedTask;
    public async Task TogglePlaybackPauseAsync()
    {
        if (_archivePlayback is { } archive && Player.State is PlaybackState.Idle or PlaybackState.Error) await StartArchiveAsync(archive.Channel, archive.Programme, Player.State == PlaybackState.Error ? archive.LoadedStart : archive.Programme.Start);
        else Player.TogglePause();
    }
    public async Task ChooseArchiveTimeAsync()
    {
        if (Model.Active is not { Archive: { } source } channel) return;
        DateTimeOffset? chosen = null;
        await WithModalAsync(() =>
        {
            var dialog = new ArchiveTimeWindow(this, channel, source, GetArchiveTimeline()?.Position ?? DateTimeOffset.Now.AddMinutes(-5));
            if (dialog.ShowDialog() == true) chosen = dialog.SelectedTime;
            return Task.CompletedTask;
        });
        if (chosen is { } time) await GuardAsync(() => PlayArchiveAtAsync(channel, time));
    }
}
