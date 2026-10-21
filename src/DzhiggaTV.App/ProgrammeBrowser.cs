using System.ComponentModel;
using DzhiggaTV.Core;

namespace DzhiggaTV.App;

/// <summary>Browsing the cards never changes the programme being played or the library's live guide.</summary>
public sealed class ProgrammeBrowser : Observable, IDisposable
{
    private readonly MainViewModel _model;
    private readonly Func<DateTimeOffset> _clock;
    private Programme? _selection, _next, _afterNext, _previous;
    private string? _selectionEpg;
    private (ChannelItem? Channel, string? Epg, DateTimeOffset? Start) _context;
    private CancellationTokenSource? _pending;
    private long _generation;
    private bool _loading, _disposed;

    public ProgrammeBrowser(MainViewModel model, Func<DateTimeOffset>? clock = null)
    {
        _model = model; _clock = clock ?? (() => DateTimeOffset.UtcNow);
        model.PropertyChanged += ModelChanged;
        _ = RefreshSafelyAsync();
    }

    public Programme? Current => _selection ?? _model.PlayingProgramme;
    public Programme? Next => IsBrowsing ? _next : _model.PlayingNext;
    public Programme? AfterNext => IsBrowsing ? _afterNext : _model.PlayingAfterNext;
    public bool IsBrowsing => _selection is not null;
    public bool FollowsPlayback => !IsBrowsing;
    public bool HasArchive => _model.Active?.Archive is not null;
    public bool CanGoBack => !_loading && _previous is { } p && _model.Active?.Archive?.Contains(p.Start, _clock()) == true;
    public bool CanGoForward => !_loading && IsBrowsing && Next is { } p && p.Start <= _clock();
    public bool CanPlay => !_loading && _selection is { } p && _model.Active is { } c && Catchup.CanPlay(c.Channel, p, _clock());
    public string BackHint => CanGoBack ? "Предыдущая передача" : "Более ранних передач в доступном архиве нет в телепрограмме";
    public string PlaybackProgrammeLabel => IsBrowsing ? Current!.End <= _clock() ? "РАНЕЕ В ЭФИРЕ" : "СЕЙЧАС В ЭФИРЕ" : _model.PlaybackProgrammeLabel;
    public string CurrentTitle => Current?.Title ?? "Программа пока недоступна";
    public string CurrentDescription => Current?.Description?.Trim() ?? "";
    public string CurrentDescriptionPreview => ProgrammeText.From(Current) is { Synopsis.Length: > 0 } text ? text.Synopsis : CurrentDescription;
    public string CurrentTime => IsBrowsing && Current is { } p ? $"{p.Start.ToLocalTime():dd.MM · HH:mm} — {p.End.ToLocalTime():HH:mm}" : _model.CurrentTime;
    public double CurrentProgress => IsBrowsing ? 0 : _model.CurrentProgress;
    public string NextTitle => Next?.Title ?? "Нет данных о следующей передаче";
    public string NextDescription => Next?.Description?.Trim() ?? "";
    public string NextTime => FormatTime(Next);
    public string AfterNextTitle => AfterNext?.Title ?? "Дальнейшая программа пока недоступна";
    public string AfterNextDescription => AfterNext?.Description?.Trim() ?? "";
    public string AfterNextTime => FormatTime(AfterNext);
    public string PlayLabel => Current?.End > _clock() ? "▶ С начала" : "▶ Смотреть";
    public string PlayHint => Current?.End > _clock() ? "Смотреть выбранную передачу с начала" : "Смотреть выбранную передачу в архиве";
    private string FormatTime(Programme? p) => p is null ? "" : IsBrowsing
        ? $"{p.Start.ToLocalTime():dd.MM · HH:mm} — {p.End.ToLocalTime():HH:mm}"
        : $"{p.Start.ToLocalTime():HH:mm} — {p.End.ToLocalTime():HH:mm}";

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Active) or nameof(MainViewModel.IsArchive))
        {
            ResetSelection();
            _ = RefreshSafelyAsync();
        }
        else if (e.PropertyName == nameof(MainViewModel.CurrentTitle)) _ = RefreshSafelyAsync();
        else if (e.PropertyName == nameof(MainViewModel.CurrentProgress))
        { Raise(nameof(CurrentProgress)); Raise(nameof(CanGoBack)); Raise(nameof(CanGoForward)); Raise(nameof(CanPlay)); }
    }

    private void ResetSelection()
    {
        ++_generation; _pending?.Cancel(); _selection = _next = _afterNext = _previous = null;
        _selectionEpg = null; _context = default; _loading = false; Notify();
    }

    public async Task FollowPlaybackAsync()
    {
        ResetSelection(); await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_disposed) return;
        var channel = _model.Active;
        string? epg = channel is null ? null : _model.Mappings.GetValueOrDefault(channel.Id)?.EpgId;
        if (IsBrowsing && _selectionEpg != epg) ResetSelection();
        var context = (channel, epg, Current?.Start);
        if (_context == context) { Notify(); return; }
        _context = context;
        var (generation, token) = BeginRequest();
        try
        {
            var now = _clock();
            var before = Current?.Start ?? now;
            var previous = channel?.Archive is { } source && epg is not null
                ? await Task.Run(() => _model.Store.GetPreviousProgrammeAsync(epg, before, source.Earliest(now), token), token)
                : null;
            if (IsCurrent(generation, channel, epg)) _previous = previous;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch
        {
            if (generation == _generation) { _previous = null; _context = default; }
            throw;
        }
        finally { EndRequest(generation); }
    }

    public Task GoBackAsync() => CanGoBack && _previous is { } previous ? SelectAsync(previous) : Task.CompletedTask;
    public Task GoForwardAsync() => CanGoForward && Next is { } next
        ? next.Start == _model.PlayingProgramme?.Start && next.ChannelId == _model.PlayingProgramme.ChannelId ? FollowPlaybackAsync() : SelectAsync(next)
        : Task.CompletedTask;

    private async Task SelectAsync(Programme programme)
    {
        if (_disposed || _model.Active is not { Archive: { } source } channel ||
            !_model.Mappings.TryGetValue(channel.Id, out var mapping) || !source.Contains(programme.Start, _clock())) return;
        var (generation, token) = BeginRequest();
        try
        {
            var snapshot = await Task.Run(async () =>
            {
                var schedule = await _model.Store.GetNowNextAsync(mapping.EpgId, programme.Start, token).ConfigureAwait(false);
                var previous = await _model.Store.GetPreviousProgrammeAsync(mapping.EpgId, programme.Start, source.Earliest(_clock()), token).ConfigureAwait(false);
                return (schedule, previous);
            }, token);
            if (!IsCurrent(generation, channel, mapping.EpgId)) return;
            _selection = programme; _next = snapshot.schedule.Next; _afterNext = snapshot.schedule.AfterNext;
            _previous = snapshot.previous; _selectionEpg = mapping.EpgId; _context = (channel, mapping.EpgId, programme.Start);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { EndRequest(generation); }
    }

    private (long Generation, CancellationToken Token) BeginRequest()
    {
        _pending?.Cancel(); _pending?.Dispose(); _pending = new(); _loading = true; Notify();
        return (++_generation, _pending.Token);
    }
    private bool IsCurrent(long generation, ChannelItem? channel, string? epg) => !_disposed && generation == _generation &&
        ReferenceEquals(channel, _model.Active) && (channel is null ? null : _model.Mappings.GetValueOrDefault(channel.Id)?.EpgId) == epg;
    private void EndRequest(long generation) { if (!_disposed && generation == _generation) { _loading = false; Notify(); } }
    private async Task RefreshSafelyAsync()
    {
        try { await RefreshAsync(); }
        catch { if (!_disposed) _model.Status = "Не удалось загрузить предыдущие передачи. Попробуйте ещё раз."; }
    }
    private void Notify()
    {
        foreach (string property in new[] { nameof(Current), nameof(Next), nameof(AfterNext), nameof(IsBrowsing), nameof(FollowsPlayback), nameof(HasArchive),
            nameof(CanGoBack), nameof(CanGoForward), nameof(CanPlay), nameof(BackHint), nameof(PlaybackProgrammeLabel), nameof(CurrentTitle), nameof(CurrentDescription),
            nameof(CurrentDescriptionPreview), nameof(CurrentTime), nameof(CurrentProgress), nameof(NextTitle), nameof(NextDescription), nameof(NextTime),
            nameof(AfterNextTitle), nameof(AfterNextDescription), nameof(AfterNextTime), nameof(PlayLabel), nameof(PlayHint) }) Raise(property);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; ++_generation; _pending?.Cancel(); _pending?.Dispose(); _model.PropertyChanged -= ModelChanged;
    }
}
