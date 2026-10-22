using System.Collections.ObjectModel;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using DzhiggaTV.Core;
using DzhiggaTV.Services;

namespace DzhiggaTV.App;

public sealed class MainViewModel : Observable, IDisposable
{
    public SqliteStore Store { get; }
    public EpgOneService Epg { get; }
    public LogoService Logos { get; }
    public UserState State { get; private set; } = new();
    public List<ChannelItem> AllChannels { get; } = [];
    public ObservableCollection<ChannelItem> VisibleChannels { get; } = [];
    public ObservableCollection<string> Groups { get; } = ["Все категории"];
    public Dictionary<string, ChannelMapping> Mappings { get; private set; } = [];
    public Dictionary<string, EpgChannel> EpgChannels { get; private set; } = [];
    public string DataDirectory { get; }
    public long RemoteRevision { get; private set; }
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _searchCancellation;
    private string _programmeSearchQuery = "";
    private IReadOnlyDictionary<string, Programme> _programmeMatches = new Dictionary<string, Programme>();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(45) };
    private readonly HashSet<string> _logoLoading = [];
    private readonly Dictionary<string, DateTimeOffset> _logoRetry = [];
    private readonly Dictionary<string, long> _logoVersions = [];
    private readonly HashSet<string> _viewportIds = [];
    private int _catalogGeneration;
    private readonly SemaphoreSlim _saveLock = new(1);
    private bool _epgBusy;
    public bool IsEpgBusy { get => _epgBusy; private set { if (Set(ref _epgBusy, value)) { Raise(nameof(CanRefreshEpg)); Raise(nameof(EpgButtonText)); } } }
    public bool CanRefreshEpg => !IsEpgBusy;
    public string EpgButtonText => IsEpgBusy ? "EPG.ONE · загрузка…" : EpgChannels.Count == 0 ? "Подключить EPG.ONE" : "EPG.ONE  ↻";
    private string _epgStatus = "Телепрограмма и логотипы ещё не загружены";
    public string EpgStatus { get => _epgStatus; private set => Set(ref _epgStatus, value); }
    private double _epgPercent;
    public double EpgPercent { get => _epgPercent; private set => Set(ref _epgPercent, value); }
    private bool _epgIndeterminate;
    public bool EpgIndeterminate { get => _epgIndeterminate; private set => Set(ref _epgIndeterminate, value); }
    private string _search = "";
    public string Search { get => _search; set { if ((CanSearch || value.Length == 0) && Set(ref _search, value)) DebounceFilter(); } }
    private string _selectedGroup = "Все категории";
    public string SelectedGroup { get => _selectedGroup; set { if (Set(ref _selectedGroup, value)) RefreshFilter(); } }
    private string _section = "all";
    public bool IsFavorites => _section == "favorites";
    public bool CanSearch => !IsFavorites;
    public string SectionTitle => _section switch { "favorites" => "Избранное", "recent" => "Недавние", "watch" => "Сейчас смотрю", _ when _section.StartsWith("collection:") => _section[11..], _ => "Все каналы" };
    public string ChannelCountText => IsFavorites ? $"{VisibleChannels.Count:N0} каналов" : $"{VisibleChannels.Count:N0} каналов · {Groups.Count - 1} категорий";
    public string LibraryChannelCountText => $"{AllChannels.Count:N0} каналов";
    private ChannelItem? _selectedChannel;
    public ChannelItem? SelectedChannel { get => _selectedChannel; set => Set(ref _selectedChannel, value); }
    private ChannelItem? _active;
    public ChannelItem? Active { get => _active; set { if (Set(ref _active, value)) RaisePlayback(); } }
    public string? PreviousId { get; set; }
    public string ActiveName => Active?.Name ?? "Сейчас смотрю";
    private Programme? _archiveProgramme, _archiveNext, _archiveAfterNext;
    private DateTimeOffset? _archivePosition;
    public bool IsArchive => _archiveProgramme is not null;
    public string PlaybackProgrammeLabel => IsArchive ? "АРХИВ" : "СЕЙЧАС В ЭФИРЕ";
    public Programme? PlayingProgramme => _archiveProgramme ?? Active?.Current;
    public Programme? PlayingNext => IsArchive ? _archiveNext : Active?.Next;
    public Programme? PlayingAfterNext => IsArchive ? _archiveAfterNext : Active?.AfterNext;
    public void SetArchiveProgramme(Programme? programme, Programme? next = null, Programme? afterNext = null, bool continuing = false)
    {
        bool wasArchive = IsArchive;
        _archiveProgramme = programme; _archiveNext = next; _archiveAfterNext = afterNext; _archivePosition = programme?.Start;
        if (!continuing || wasArchive != IsArchive) { Raise(nameof(IsArchive)); Raise(nameof(PlaybackProgrammeLabel)); }
        RaisePlayback();
    }
    public void SetArchivePosition(DateTimeOffset time) { _archivePosition = time; Raise(nameof(CurrentProgress)); }
    public string CurrentTitle => PlayingProgramme?.Title ?? "Программа пока недоступна";
    public string CurrentDescription => PlayingProgramme?.Description?.Trim() ?? "";
    public string CurrentDescriptionCompact => string.Join(" ", CurrentDescription.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    public ProgrammeText CurrentProgrammeText => ProgrammeText.From(PlayingProgramme);
    public string CurrentDescriptionPreview => CurrentProgrammeText is { Synopsis.Length: > 0 } text ? text.Synopsis : CurrentDescription;
    public string CurrentTime => PlayingProgramme is { } p ? $"{(IsArchive ? p.Start.ToLocalTime().ToString("dd.MM · ") : "")}{p.Start.ToLocalTime():HH:mm} — {p.End.ToLocalTime():HH:mm}" : "Подключите EPG.ONE в настройках";
    public double CurrentProgress => IsArchive && PlayingProgramme is { } p && p.End > p.Start ? Math.Clamp(((_archivePosition ?? p.Start) - p.Start).TotalSeconds / (p.End - p.Start).TotalSeconds * 100, 0, 100) : Active?.Progress ?? 0;
    public string NextTitle => PlayingNext?.Title ?? "Нет данных о следующей передаче";
    public string NextDescription => PlayingNext?.Description?.Trim() ?? "";
    public string NextTime => PlayingNext is { } p ? $"{p.Start.ToLocalTime():HH:mm} — {p.End.ToLocalTime():HH:mm}" : "";
    public string AfterNextTitle => PlayingAfterNext?.Title ?? "Дальнейшая программа пока недоступна";
    public string AfterNextDescription => PlayingAfterNext?.Description?.Trim() ?? "";
    public string AfterNextTime => PlayingAfterNext is { } p ? $"{p.Start.ToLocalTime():HH:mm} — {p.End.ToLocalTime():HH:mm}" : "";
    private string _status = "Готов к просмотру. Добавьте свой плейлист.";
    public string Status { get => _status; set => Set(ref _status, value); }
    private string _playbackStatus = "Выберите канал";
    public string PlaybackStatus { get => _playbackStatus; set => Set(ref _playbackStatus, value); }

    public MainViewModel(string directory)
    {
        DataDirectory = directory;
        Directory.CreateDirectory(directory);
        Store = new(Path.Combine(directory, "library.db"));
        Epg = new(Store);
        Logos = new(Path.Combine(directory, "logos"));
    }
    public string Setting(string key, string fallback = "") => State.Settings.GetValueOrDefault(key, fallback);
    public bool Flag(string key, bool fallback = false) => bool.TryParse(Setting(key), out var value) ? value : fallback;
    public void SetSetting(string key, object value) => State.Settings[key] = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
    public async Task InitializeAsync()
    {
        await Store.InitializeAsync(); State = await Store.LoadUserStateAsync();
        Epg.AllowInsecureHttp = Logos.AllowInsecureHttp = Flag("epg.http");
        await Epg.InitializeAsync();
        await ReloadAsync();
        await RefreshEpgViewAsync();
        _ = DailyEpgAsync();
    }
    public async Task ReloadAsync()
    {
        var channels = await Task.Run(async () => await Store.GetChannelsAsync());
        var activeId = Active?.Id; var selectedId = SelectedChannel?.Id;
        ++_catalogGeneration; _logoLoading.Clear(); _logoRetry.Clear();
        AllChannels.Clear();
        RemoteRevision++;
        AllChannels.AddRange(channels.Select((c, index) => new ChannelItem(c)
        {
            Number = index + 1,
            Name = State.Renames.GetValueOrDefault(c.Id, c.Name),
            Group = State.CustomCategories.GetValueOrDefault(c.Id, c.Group),
            IsFavorite = State.Favorites.Contains(c.Id)
        }));
        Active = AllChannels.Find(c => c.Id == activeId); SelectedChannel = AllChannels.Find(c => c.Id == selectedId);
        Groups.Clear(); Groups.Add("Все категории");
        foreach (var group in AllChannels.Where(IsAllowed).Select(c => c.Group).Distinct().OrderBy(g => g)) Groups.Add(group);
        if (!Groups.Contains(_selectedGroup)) _selectedGroup = "Все категории";
        Raise(nameof(SelectedGroup)); Raise(nameof(LibraryChannelCountText)); RefreshFilter();
        Status = $"Библиотека: {AllChannels.Count:N0} каналов. Пользовательские изменения сохранены локально.";
    }
    public bool IsAllowed(ChannelItem item) => !State.Hidden.Contains(item.Id) && (Flag("adult.enabled") || !IsAdult(item.Channel.Group) && !IsAdult(item.Group));
    public static bool IsAdult(string name) => new[] { "взрос", "adult", "xxx", "18+", "эроти", "porn" }.Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase));
    public void SetSection(string section) { _section = section; Raise(nameof(SectionTitle)); Raise(nameof(IsFavorites)); Raise(nameof(CanSearch)); RefreshFilter(); }
    public void RefreshFilter() => _ = RefreshFilterAsync();
    public Task RefreshFilterAsync() => RefreshFilterAsync(false);
    private async Task RefreshFilterAsync(bool debounce)
    {
        _searchCancellation?.Cancel(); _searchCancellation?.Dispose();
        var cancellation = _searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = cancellation.Token;
        try
        {
            if (debounce) await Task.Delay(130, token);
            string query = CanSearch ? Search.Trim() : "";
            if (query.Length == 0) { _programmeSearchQuery = ""; _programmeMatches = new Dictionary<string, Programme>(); }
            ApplyFilter(query, _programmeSearchQuery == query ? _programmeMatches : new Dictionary<string, Programme>());
            if (query.Length == 0) return;
            var mappings = Mappings; int generation = _catalogGeneration;
            var now = DateTimeOffset.UtcNow;
            var scopes = AllChannels.Where(IsAllowed).Where(c => mappings.ContainsKey(c.Id)).Select(c => ProgrammeSearchScope.From(c.Channel, mappings[c.Id].EpgId, now)).ToArray();
            var matches = await Task.Run(() => Store.SearchPlayableProgrammesAsync(query, scopes, now, token), token);
            if (token.IsCancellationRequested || !ReferenceEquals(_searchCancellation, cancellation) || generation != _catalogGeneration || !ReferenceEquals(mappings, Mappings)) return;
            _programmeSearchQuery = query; _programmeMatches = matches;
            ApplyFilter(query, matches);
        }
        catch (OperationCanceledException) { }
        catch { Status = "Поиск передач временно недоступен. Показаны совпадения по названию каналов."; }
    }
    private void ApplyFilter(string query, IReadOnlyDictionary<string, Programme> matches)
    {
        foreach (var channel in AllChannels)
            channel.SearchProgramme = query.Length > 0 && matches.TryGetValue(channel.Id, out var programme) ? programme : null;
        IEnumerable<ChannelItem> items = AllChannels.Where(IsAllowed);
        List<string>? order = null;
        if (_section == "favorites") { order = State.Favorites; items = items.Where(c => order.Contains(c.Id)); }
        else if (_section == "recent") { order = State.History; items = items.Where(c => order.Contains(c.Id)); }
        else if (_section == "watch") items = items.Where(c => c.Id == Active?.Id);
        else if (_section.StartsWith("collection:")) { order = State.Collections.GetValueOrDefault(_section[11..]) ?? []; items = items.Where(c => order.Contains(c.Id)); }
        if (!IsFavorites && SelectedGroup != "Все категории") items = items.Where(c => c.Group == SelectedGroup);
        if (query.Length > 0) items = items.Where(c => ChannelMatcher.MatchesSearch(c.Name, query) || c.HasSearchProgramme);
        if (order is not null) items = items.OrderBy(c => order.IndexOf(c.Id));
        var result = items.ToArray();
        if (!VisibleChannels.SequenceEqual(result)) { VisibleChannels.Clear(); foreach (var c in result) VisibleChannels.Add(c); }
        Raise(nameof(ChannelCountText));
    }
    private async void DebounceFilter()
    {
        await RefreshFilterAsync(true);
    }
    public async Task ImportAsync(string location, string? existingId = null)
    {
        Status = "Импорт плейлиста…";
        var sources = await Store.GetPlaylistSourcesAsync();
        var previous = sources.FirstOrDefault(s => s.Location == location);
        var id = existingId ?? previous?.Id ?? Guid.NewGuid().ToString("N");
        var remote = Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
        var list = await Task.Run(async () =>
        {
            if (!remote) return await M3uParser.ParseFileAsync(location, id, _lifetime.Token);
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, _lifetime.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 32 * 1024 * 1024) throw new InvalidDataException();
            await using var source = await response.Content.ReadAsStreamAsync(_lifetime.Token);
            using var memory = new MemoryStream(); var buffer = new byte[65536]; int read;
            while ((read = await source.ReadAsync(buffer, _lifetime.Token)) > 0)
            { if (memory.Length + read > 32 * 1024 * 1024) throw new InvalidDataException(); await memory.WriteAsync(buffer.AsMemory(0, read), _lifetime.Token); }
            memory.Position = 0; return await M3uParser.ParseAsync(memory, id, _lifetime.Token);
        });
        if (list.Count == 0) throw new InvalidDataException("Плейлист не содержит каналов.");
        await Store.SavePlaylistAsync(new(id, previous?.Name ?? (remote ? "Сетевой плейлист" : Path.GetFileNameWithoutExtension(location)), location, remote), list);
        await ReloadAsync();
        if (EpgChannels.Count > 0) { await Epg.MatchChannelsAsync(AllChannels.Select(c => c.Channel).ToArray()); await RefreshEpgViewAsync(); }
        Status = $"Импортировано {list.Count:N0} каналов, {list.Select(c => c.Group).Distinct().Count()} категорий.";
    }
    public async Task RefreshEpgAsync()
    {
        if (IsEpgBusy) return; IsEpgBusy = true; EpgPercent = 0; EpgIndeterminate = true;
        try
        {
            SetSetting("epg.enabled", true); await SaveAsync();
            Status = EpgStatus = "EPG.ONE: подключение к источнику…";
            var source = new Uri(Setting("epg.source", "https://epg.one/epg2.xml.gz"));
            var channels = AllChannels.Select(c => c.Channel).ToArray();
            var progress = new Progress<EpgProgress>(p =>
            {
                if (!IsEpgBusy) return;
                EpgIndeterminate = p.TotalBytes is not > 0;
                EpgPercent = p.TotalBytes is > 0 ? Math.Clamp(p.BytesDownloaded * 100d / p.TotalBytes.Value, 0, 100) : 0;
                Status = EpgStatus = p.BytesDownloaded > 0 ? $"{p.Stage} · {p.BytesDownloaded / 1048576d:0.0} МиБ" + (p.TotalBytes is > 0 ? $" / {p.TotalBytes / 1048576d:0.0} МиБ" : "") : p.Stage;
            });
            var result = await Task.Run(async () => await Epg.RefreshAsync(channels, progress, _lifetime.Token, source));
            if (result.Updated) Logos.ResetFailedDownloads();
            await RefreshEpgViewAsync();
            Status = EpgStatus = result.Updated ? $"EPG.ONE подключена · программа для {Mappings.Count:N0} каналов; логотипы загружаются." : result.Message;
        }
        catch (OperationCanceledException) { }
        catch { Status = EpgStatus = "EPG.ONE временно недоступен. Нажмите «Подключить EPG.ONE», чтобы повторить. Последняя программа сохранена."; }
        finally { IsEpgBusy = false; EpgIndeterminate = false; }
    }
    private async Task DailyEpgAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                var last = await Store.GetSettingAsync<DateTimeOffset?>("epg.lastSuccessfulUpdate", null);
                if (AllChannels.Count > 0 && Flag("epg.enabled") && (last is null || DateTimeOffset.UtcNow - last >= TimeSpan.FromHours(24))) await RefreshEpgAsync();
                await Task.Delay(TimeSpan.FromHours(1), _lifetime.Token);
            }
            catch (OperationCanceledException) { break; }
            catch { try { await Task.Delay(TimeSpan.FromHours(1), _lifetime.Token); } catch { break; } }
        }
    }
    public async Task RefreshEpgViewAsync()
    {
        var previousMappings = Mappings;
        var previousCatalog = EpgChannels;
        Mappings = (await Store.GetMappingsAsync()).ToDictionary(x => x.ChannelId);
        EpgChannels = (await Store.GetEpgChannelsAsync()).ToDictionary(x => x.Id);
        foreach (var item in AllChannels)
        {
            var before = previousMappings.GetValueOrDefault(item.Id)?.EpgId;
            var after = Mappings.GetValueOrDefault(item.Id)?.EpgId;
            var changed = before != after || (before is not null && after is not null && previousCatalog.GetValueOrDefault(before)?.Logo != EpgChannels.GetValueOrDefault(after)?.Logo);
            if (changed) InvalidateLogo(item.Id);
            else if (item.LogoPath is null) _logoRetry.Remove(item.Id);
        }
        await UpdateNowNextAsync();
        Raise(nameof(EpgButtonText));
        if (!IsEpgBusy) EpgStatus = EpgChannels.Count == 0 ? "Телепрограмма и логотипы ещё не загружены · нажмите «Подключить EPG.ONE»" : $"EPG.ONE · программа назначена {Mappings.Count:N0} каналам";
        await LoadViewportAsync(RelevantChannels());
    }
    private IEnumerable<ChannelItem> RelevantChannels() => AllChannels.Where(c => _viewportIds.Contains(c.Id)).Concat(VisibleChannels.Take(20)).Append(Active).OfType<ChannelItem>().Distinct();
    public async Task RefreshLogosAsync()
    {
        ++_catalogGeneration; _logoLoading.Clear(); _logoRetry.Clear();
        foreach (var c in AllChannels) if (c.LogoPath is null) _logoVersions[c.Id] = _logoVersions.GetValueOrDefault(c.Id) + 1;
        Logos.ResetFailedDownloads();
        await LoadViewportAsync(RelevantChannels());
    }
    public Task UpdateNowNextAsync() => UpdateNowNextAsync(RelevantChannels());
    public async Task UpdateNowNextAsync(IEnumerable<ChannelItem> channels)
    {
        // At most the visible viewport and active channel: no thousands of queries every tick.
        var relevant = channels.Where(IsAllowed).DistinctBy(c => c.Id).ToArray();
        var guides = await Store.GetNowNextBatchAsync(relevant.Where(c => Mappings.ContainsKey(c.Id)).Select(c => Mappings[c.Id].EpgId), cancellationToken: _lifetime.Token);
        foreach (var c in relevant)
        {
            if (Mappings.TryGetValue(c.Id, out var match))
            {
                var now = guides[match.EpgId];
                c.Current = now.Current; c.Next = now.Next; c.AfterNext = now.AfterNext;
                c.NowTitle = now.Current?.Title ?? "Нет программы на это время";
                c.EndTime = now.Current?.End.ToLocalTime().ToString("HH:mm") ?? "";
                c.Progress = now.Current is { } p ? Math.Clamp((DateTimeOffset.UtcNow - p.Start).TotalSeconds / (p.End - p.Start).TotalSeconds * 100, 0, 100) : 0;
            }
            else
            {
                c.Current = c.Next = c.AfterNext = null; c.NowTitle = "Телепрограмма не назначена"; c.EndTime = ""; c.Progress = 0;
            }
        }
        RaisePlayback();
        if (!string.IsNullOrWhiteSpace(Search)) await RefreshFilterAsync();
    }
    public async Task LoadViewportAsync(IEnumerable<ChannelItem> items, bool updateViewport = false)
    {
        var snapshot = items.ToArray();
        bool activeGuideLoaded = false;
        if (updateViewport) { _viewportIds.Clear(); foreach (var c in snapshot) _viewportIds.Add(c.Id); }
        var guides = await Store.GetNowNextBatchAsync(snapshot.Where(c => c.Current is null && Mappings.ContainsKey(c.Id)).Select(c => Mappings[c.Id].EpgId), cancellationToken: _lifetime.Token);
        foreach (var item in snapshot)
        {
            if (Mappings.TryGetValue(item.Id, out var mapping) && item.Current is null)
            {
                var guide = guides[mapping.EpgId]; item.Current = guide.Current; item.Next = guide.Next; item.AfterNext = guide.AfterNext;
                item.NowTitle = guide.Current?.Title ?? "Нет программы на это время";
                item.EndTime = guide.Current?.End.ToLocalTime().ToString("HH:mm") ?? "";
                item.Progress = guide.Current is { } p ? Math.Clamp((DateTimeOffset.UtcNow - p.Start).TotalSeconds / (p.End - p.Start).TotalSeconds * 100, 0, 100) : 0;
                activeGuideLoaded |= ReferenceEquals(item, Active);
            }
        }
        LoadLogos(snapshot);
        if (activeGuideLoaded) RaisePlayback();
    }
    public void LoadLogos(IEnumerable<ChannelItem> items)
    {
        foreach (var item in items.Where(IsAllowed))
        {
            if (item.LogoPath is not null || _logoRetry.GetValueOrDefault(item.Id) > DateTimeOffset.UtcNow || !_logoLoading.Add(item.Id)) continue;
            _ = LoadLogoAsync(item, _catalogGeneration, _logoVersions.GetValueOrDefault(item.Id));
        }
    }
    private async Task LoadLogoAsync(ChannelItem item, int generation, long version)
    {
        try
        {
            EpgChannel? epg = Mappings.TryGetValue(item.Id, out var m) ? EpgChannels.GetValueOrDefault(m.EpgId) : null;
            var path = await Logos.ResolveAsync(item.Channel, State.UserLogos.GetValueOrDefault(item.Id), epg, epg is null ? null : Epg.GetReferenceLogo(epg.Id), _lifetime.Token);
            if (generation != _catalogGeneration || version != _logoVersions.GetValueOrDefault(item.Id)) return;
            item.LogoPath = path;
            if (path is null) _logoRetry[item.Id] = DateTimeOffset.UtcNow.AddMinutes(15);
        }
        catch { } // Text fallback is intentional; no URL-bearing exception is logged.
        finally { if (generation == _catalogGeneration && version == _logoVersions.GetValueOrDefault(item.Id)) _logoLoading.Remove(item.Id); }
    }
    public void InvalidateLogo(string id) { _logoVersions[id] = _logoVersions.GetValueOrDefault(id) + 1; _logoLoading.Remove(id); _logoRetry.Remove(id); var c = AllChannels.Find(c => c.Id == id); if (c is not null) { c.LogoPath = null; c.Current = c.Next = c.AfterNext = null; } }
    public void RaisePlayback()
    {
        foreach (var name in new[] { nameof(ActiveName), nameof(CurrentTitle), nameof(CurrentDescription), nameof(CurrentDescriptionCompact), nameof(CurrentProgrammeText), nameof(CurrentDescriptionPreview), nameof(CurrentTime), nameof(CurrentProgress), nameof(NextTitle), nameof(NextDescription), nameof(NextTime), nameof(AfterNextTitle), nameof(AfterNextDescription), nameof(AfterNextTime) }) Raise(name);
    }
    public async Task ToggleFavoriteAsync(ChannelItem item)
    {
        bool favorite = !State.Favorites.Contains(item.Id);
        State.Favorites.RemoveAll(id => id == item.Id);
        if (favorite) State.Favorites.Add(item.Id);
        item.IsFavorite = favorite;
        if (_section == "favorites") RefreshFilter();
        await SaveAsync();
    }
    public async Task ActivateChannelAsync(ChannelItem item)
    {
        if (Active?.Id != item.Id) PreviousId = Active?.Id;
        Active = item; SelectPlaybackChannel(item);
        await LoadViewportAsync([item]);
    }
    public async Task RecordPlaybackAsync(ChannelItem item)
    {
        // Cached guide reads must not wait for a state write queued behind an EPG import.
        await ActivateChannelAsync(item);
        State.RecordWatched(item.Id); SetSetting("last.channel", item.Id); await SaveAsync();
    }
    public void SelectPlaybackChannel(ChannelItem item)
    {
        if (!IsAllowed(item) || !AllChannels.Contains(item)) return;
        if (!VisibleChannels.Contains(item))
        {
            _searchCancellation?.Cancel();
            _search = ""; _selectedGroup = "Все категории"; _section = "all";
            Raise(nameof(Search)); Raise(nameof(SelectedGroup)); Raise(nameof(SectionTitle)); Raise(nameof(IsFavorites)); Raise(nameof(CanSearch)); RefreshFilter();
        }
        SelectedChannel = item;
    }
    public async Task MoveAsync(string fromId, string toId)
    {
        List<string>? list = _section == "favorites" ? State.Favorites : _section.StartsWith("collection:") ? State.Collections.GetValueOrDefault(_section[11..]) : null;
        if (list is null || fromId == toId) return;
        var from = list.IndexOf(fromId); var to = list.IndexOf(toId);
        if (from < 0 || to < 0) return; list.RemoveAt(from); list.Insert(to, fromId); await SaveAsync(); RefreshFilter();
    }
    public async Task SaveAsync()
    {
        await _saveLock.WaitAsync();
        try { await Store.SaveUserStateAsync(State); RemoteRevision++; } finally { _saveLock.Release(); }
    }
    public void Dispose() { _lifetime.Cancel(); _http.Dispose(); _searchCancellation?.Cancel(); /* In-flight writers own their connection until completion. */ }
}
