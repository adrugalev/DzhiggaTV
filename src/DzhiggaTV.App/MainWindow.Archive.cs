using DzhiggaTV.Core;
using DzhiggaTV.Player;
using DzhiggaTV.Services;

namespace DzhiggaTV.App;

public partial class MainWindow
{
    private (string Channel, DateTimeOffset Start)? _openingSearchResult;
    private readonly ArchiveAvailabilityClient _archiveAvailability = new();
    private CancellationTokenSource? _archiveProbe;
    public async Task PlaySearchResultAsync(ChannelItem channel)
    {
        var programme = channel.SearchProgramme;
        if (programme is null || programme.End > DateTimeOffset.UtcNow)
        { if (Model.Active?.Id != channel.Id || IsArchivePlayback) await PlayAsync(channel); return; }
        var key = (channel.Id, programme.Start);
        if (_openingSearchResult == key || _archivePlayback is { } archive && archive.Channel.Id == channel.Id && archive.Programme.Start == programme.Start) return;
        _openingSearchResult = key;
        try { await PlayArchiveAsync(channel, programme); }
        finally { if (_openingSearchResult == key) _openingSearchResult = null; }
    }
    private async void PreviousProgramme_Click(object sender, System.Windows.RoutedEventArgs e) => await GuardAsync(Programmes.GoBackAsync);
    private async void FollowingProgramme_Click(object sender, System.Windows.RoutedEventArgs e) => await GuardAsync(Programmes.GoForwardAsync);
    private async void ProgrammeLive_Click(object sender, System.Windows.RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (IsArchivePlayback) await ReturnToLiveAsync();
        await Programmes.FollowPlaybackAsync();
    });
    private async void PlayBrowsedProgramme_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (Programmes.CanPlay && Programmes.Current is { } programme && Model.Active is { } channel)
            await GuardAsync(() => PlayArchiveAsync(channel, programme));
    }

    private sealed class ArchivePlayback(ChannelItem channel, Programme programme, DateTimeOffset start, DateTimeOffset end, string? epgId)
    {
        public ChannelItem Channel { get; } = channel;
        public Programme Programme { get; set; } = programme;
        public DateTimeOffset LoadedStart { get; } = start;
        public DateTimeOffset LoadedEnd { get; } = end;
        public string? EpgId { get; set; } = epgId;
        public bool GuideLoading { get; set; }
        public bool Continuing { get; set; }
        public int EpgRevision { get; set; } = -1;
    }
    private ArchivePlayback? _archivePlayback;
    public bool IsArchivePlayback => _archivePlayback is not null;
    public bool CanRestartCurrentProgramme => Model.Active is { Current: { } programme } channel &&
        programme.End > DateTimeOffset.Now && Catchup.CanPlay(channel.Channel, programme, DateTimeOffset.Now);
    public Task RestartCurrentProgrammeAsync() => CanRestartCurrentProgramme && Model.Active is { Current: { } programme } channel
        ? PlayArchiveAsync(channel, programme) : Task.CompletedTask;
    private void ClearArchivePlayback() { _archivePlayback = null; Model.SetArchiveProgramme(null); }

    public ArchiveTimeline? GetArchiveTimeline()
    {
        if (Model.Active is not { Archive: { } source } channel) return null;
        var now = DateTimeOffset.Now;
        if (_archivePlayback is { } archive)
        {
            var native = Player.GetTimeline();
            var position = native.Position is { } seconds ? archive.LoadedStart.AddSeconds(Math.Max(0, seconds)) : Player.State == PlaybackState.Idle ? archive.LoadedEnd : archive.LoadedStart;
            position = position < archive.LoadedStart ? archive.LoadedStart : position > archive.LoadedEnd ? archive.LoadedEnd : position;
            RefreshArchiveProgramme(archive, position);
            Model.SetArchivePosition(position);
            var first = archive.Programme.Start > source.Earliest(now) ? archive.Programme.Start : source.Earliest(now);
            var last = archive.Programme.End < now ? archive.Programme.End : now;
            return first < last ? new(first, last, position, true) : null;
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

    public Task PlayArchiveAtAsync(ChannelItem channel, DateTimeOffset time) => OpenArchiveAtAsync(channel, time);

    private async Task OpenArchiveAtAsync(ChannelItem channel, DateTimeOffset time, bool paused = false, bool continuing = false)
    {
        if (!_engineReady || !Model.IsAllowed(channel)) return;
        var now = DateTimeOffset.Now;
        if (channel.Archive is not { } source || !source.Contains(time, now)) throw new InvalidOperationException("Выбранное время недоступно в архиве канала.");
        var lookupGeneration = ++_playGeneration;
        Programme? programme = null;
        var mapping = Model.EpgFor(channel.Id);
        if (mapping is not null) programme = (await Model.Guide.GetNowNextAsync(mapping, time)).Current;
        if (lookupGeneration != _playGeneration) return;
        programme ??= ArchivePlaceholder(mapping?.EpgId ?? channel.Id, time, now);
        await StartArchiveAsync(channel, programme, time, paused, continuing);
    }

    private async Task StartArchiveAsync(ChannelItem channel, Programme programme, DateTimeOffset start, bool paused = false, bool continuing = false)
    {
        if (!_engineReady || !Model.IsAllowed(channel)) return;
        // EPG entries describe the video; they must not bound the recording itself.
        // Bounded provider windows also work for catchup URLs that require a duration.
        var now = DateTimeOffset.Now; var end = start.AddHours(2) < now ? start.AddHours(2) : now;
        string url = Catchup.BuildUrl(channel.Channel, start, end, now);
        var generation = ++_playGeneration;
        _archiveProbe?.Cancel(); _archiveProbe?.Dispose();
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(_remoteLifetime.Token); _archiveProbe = probe;
        _overlay?.HideArchiveNotice();
        try
        {
            var availability = await _archiveAvailability.CheckAsync(channel.Channel, url, probe.Token);
            if (generation != _playGeneration || probe.IsCancellationRequested) return;
            if (availability.Unavailable) throw new ArchiveUnavailableException(channel.Name + ": " + availability.Message);
        }
        finally { if (_archiveProbe == probe) _archiveProbe = null; }
        _playCancellation?.Cancel(); _playCancellation?.Dispose(); _playCancellation = new(); var token = _playCancellation.Token;
        await Model.ActivateChannelAsync(channel);
        if (generation != _playGeneration || token.IsCancellationRequested) return;
        NowNext? schedule = null;
        var mapping = Model.EpgFor(channel.Id);
        if (mapping is not null) schedule = await Model.Guide.GetNowNextAsync(mapping, start, token);
        if (generation != _playGeneration || token.IsCancellationRequested) return;
        programme = schedule?.Current ?? programme;
        if (start < programme.Start || start >= programme.End) programme = ArchivePlaceholder(mapping?.EpgId ?? channel.Id, start, now, schedule?.Next);
        _archivePlayback = new(channel, programme, start, end, mapping?.EpgId) { EpgRevision = Model.EpgTimeRevision };
        Model.SetArchiveProgramme(programme, schedule?.Next, schedule?.AfterNext, continuing);
        HasPlayback = true; Model.PlaybackStatus = "Открываем архив…"; _overlay?.RefreshState();
        await Player.PlayArchiveAsync(url, (end - start).TotalSeconds, token, position =>
        {
            var resume = start.AddSeconds(position);
            return new(Catchup.BuildUrl(channel.Channel, resume, end, DateTimeOffset.Now), (end - resume).TotalSeconds);
        });
        if (generation != _playGeneration || token.IsCancellationRequested) return;
        if (paused) Player.IsPaused = true;
        Model.State.RecordWatched(channel.Id); Model.SetSetting("last.channel", channel.Id); _ = GuardAsync(Model.SaveAsync); _overlay?.Reveal();
    }

    private static Programme ArchivePlaceholder(string epgId, DateTimeOffset time, DateTimeOffset now, Programme? next = null)
    {
        var end = time.AddMinutes(5);
        if (next is not null && next.Start > time && next.Start < end) end = next.Start;
        if (end > now) end = now;
        if (end <= time) end = time.AddSeconds(1);
        return new(epgId, "Архив · " + time.ToLocalTime().ToString("dd.MM HH:mm"), time, end);
    }

    private void RefreshArchiveProgramme(ArchivePlayback archive, DateTimeOffset position)
    {
        var epg = Model.EpgFor(archive.Channel.Id); string? epgId = epg?.EpgId;
        int revision = Model.EpgTimeRevision;
        if (archive.GuideLoading || archive.EpgRevision == revision && archive.EpgId == epgId && position >= archive.Programme.Start && position < archive.Programme.End) return;
        archive.GuideLoading = true;
        var generation = _playGeneration;
        var token = _playCancellation?.Token ?? CancellationToken.None;
        _ = GuardAsync(async () =>
        {
            try
            {
                NowNext? schedule = epg is null ? null : await Task.Run(() => Model.Guide.GetNowNextAsync(epg, position, token), token);
                if (_closing || generation != _playGeneration || !ReferenceEquals(_archivePlayback, archive) || token.IsCancellationRequested ||
                    Model.EpgFor(archive.Channel.Id) != epg || Model.EpgTimeRevision != revision) return;
                archive.Programme = schedule?.Current ?? ArchivePlaceholder(epgId ?? archive.Channel.Id, position, DateTimeOffset.Now, schedule?.Next);
                archive.EpgId = epgId; archive.EpgRevision = revision;
                Model.SetArchiveProgramme(archive.Programme, schedule?.Next, schedule?.AfterNext, continuing: true);
                Model.SetArchivePosition(position);
            }
            finally { archive.GuideLoading = false; }
        });
    }

    private async Task ContinueArchiveAsync()
    {
        if (_closing || _archiveProbe is not null || Player.State != PlaybackState.Idle || _archivePlayback is not { Continuing: false } archive) return;
        // A manual Stop clears the native request. Only a confirmed end of this window may advance it.
        if (Player.GetTimeline().Position is not { } seconds || seconds < (archive.LoadedEnd - archive.LoadedStart).TotalSeconds - .25) return;
        archive.Continuing = true;
        try
        {
            if (DateTimeOffset.Now - archive.LoadedEnd <= TimeSpan.FromSeconds(5)) await ReturnToLiveAsync();
            else await OpenArchiveAtAsync(archive.Channel, archive.LoadedEnd, Player.IsPaused, continuing: true);
        }
        finally { archive.Continuing = false; }
    }

    public async Task SeekArchiveToAsync(DateTimeOffset time)
    {
        if (_archivePlayback is not { } archive)
        {
            if (time >= DateTimeOffset.Now.AddSeconds(-1)) return;
            if (Model.Active is { } channel) await PlayArchiveAtAsync(channel, time);
            return;
        }
        var now = DateTimeOffset.Now;
        var earliest = archive.Channel.Archive!.Earliest(now);
        if (time < earliest) time = earliest;
        var end = now;
        if (time >= end) time = end.AddSeconds(-1);
        var generation = ++_playGeneration; bool paused = Player.IsPaused;
        bool reload = time < archive.LoadedStart || time >= archive.LoadedEnd || Player.State is PlaybackState.Idle or PlaybackState.Error;
        if (!reload) reload = !await Player.SeekWithinCacheAsync((time - archive.LoadedStart).TotalSeconds);
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
        if (_archivePlayback is { } archive && Player.State is PlaybackState.Idle or PlaybackState.Error) await StartArchiveAsync(archive.Channel, archive.Programme, Player.State == PlaybackState.Error ? GetArchiveTimeline()?.Position ?? archive.LoadedStart : archive.Programme.Start);
        else Player.TogglePause();
    }
}
