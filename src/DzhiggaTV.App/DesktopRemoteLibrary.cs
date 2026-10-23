using System.Globalization;
using DzhiggaTV.Remote;
using DzhiggaTV.Core;
using System.Windows.Threading;

namespace DzhiggaTV.App;

public sealed class DesktopRemoteLibrary(MainWindow main) : IRemoteLibrary
{
    private MainViewModel M => main.Model;
    private Task<T> Ui<T>(Func<T> action, CancellationToken token) => main.Dispatcher.InvokeAsync(action, DispatcherPriority.Background, token).Task;
    private Task UiAsync(Func<Task> action, CancellationToken token) => main.Dispatcher.InvokeAsync(action, DispatcherPriority.Background, token).Task.Unwrap();
    private ChannelItem Allowed(string id) => M.AllChannels.Find(c => c.Id == id && M.IsAllowed(c)) ?? throw new RemoteException("Канал недоступен или скрыт на компьютере.", 404);
    public async Task<RemoteCatalog> CatalogAsync(CatalogQuery query, CancellationToken token)
    {
        var snapshot = await Ui(() =>
        {
            var channels = M.AllChannels.Where(M.IsAllowed).ToArray();
            var favorites = M.State.Favorites.ToArray();
            var collections = M.State.Collections.ToDictionary(p => p.Key, p => p.Value.ToArray());
            return (Channels: channels.Select(c => new Row(c.Id, c.Name, c.Group, c.Channel.PlaylistId, c.Number, M.Mappings.GetValueOrDefault(c.Id)?.EpgId, c.Channel)).ToArray(), Favorites: favorites, Collections: collections, Revision: M.RemoteRevision);
        }, token);
        var rows = snapshot.Channels.AsEnumerable();
        if (query.Ids.Length > 0)
        {
            var requested = query.Ids.Split(',').Take(100).ToArray();
            rows = rows.Where(c => requested.Contains(c.Id)).OrderBy(c => Array.IndexOf(requested, c.Id));
        }
        if (query.Category.Length > 0) rows = rows.Where(c => c.Group == query.Category);
        if (query.Playlist.Length > 0) rows = rows.Where(c => c.Playlist == query.Playlist);
        var ordered = query.Collection == "favorites" ? snapshot.Favorites : snapshot.Collections.GetValueOrDefault(query.Collection);
        if (ordered is not null) rows = rows.Where(c => ordered.Contains(c.Id)).OrderBy(c => Array.IndexOf(ordered, c.Id));
        if (query.Collection.Length > 0 && query.Collection != "favorites" && ordered is null) rows = [];
        IReadOnlyDictionary<string, Programme> programmeMatches = new Dictionary<string, Programme>();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var terms = query.Search.Trim(); var before = rows.ToArray();
            var time = DateTimeOffset.UtcNow;
            var scopes = before.Where(c => c.EpgId is not null).Select(c => ProgrammeSearchScope.From(c.Channel, c.EpgId!, time)).ToArray();
            var matches = await Task.Run(() => M.Store.SearchPlayableProgrammesAsync(terms, scopes, time, token), token);
            programmeMatches = matches;
            rows = before.Where(c => c.Name.Contains(terms, StringComparison.CurrentCultureIgnoreCase) || matches.ContainsKey(c.Id));
        }
        if (query.Sort == "name") rows = rows.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase);
        var filtered = rows.ToArray(); var page = filtered.Skip(query.Offset).Take(query.Limit).ToArray();
        var now = await Task.Run(() => M.Store.GetNowNextBatchAsync(page.Select(c => c.EpgId).OfType<string>(), cancellationToken: token), token);
        var sources = await M.Store.GetPlaylistSourcesAsync(token);
        return new(page.Select(c => new RemoteChannel(c.Id, c.Name, c.Group, snapshot.Favorites.Contains(c.Id), c.Number,
            RemoteProgramme.From(c.EpgId is { } id ? now.GetValueOrDefault(id)?.Current : null), RemoteProgramme.From(c.EpgId is { } nextId ? now.GetValueOrDefault(nextId)?.Next : null),
            RemoteProgramme.From(programmeMatches.GetValueOrDefault(c.Id)))).ToArray(),
            filtered.Length, snapshot.Channels.Select(c => c.Group).Distinct().Order().ToArray(),
            sources.Where(s => snapshot.Channels.Any(c => c.Playlist == s.Id)).Select(s => new RemoteChoice(s.Id, s.Name)).ToArray(), snapshot.Collections.Keys.ToArray(), snapshot.Revision);
    }
    public async Task<IReadOnlyList<RemoteProgramme>> GuideAsync(string channelId, DateTimeOffset from, CancellationToken token)
    {
        var epg = await Ui(() => M.Mappings.GetValueOrDefault(Allowed(channelId).Id)?.EpgId, token);
        if (epg is null) return [];
        return (await Task.Run(() => M.Store.GetProgrammesAsync(epg, from, from.AddHours(24), token), token)).Select(p => RemoteProgramme.From(p)!).ToArray();
    }
    public async Task<string?> LogoAsync(string channelId, CancellationToken token)
    {
        var row = await Ui(() => { var c = Allowed(channelId); return (c.Channel, c.LogoPath, Custom: M.State.UserLogos.GetValueOrDefault(c.Id), Epg: M.Mappings.GetValueOrDefault(c.Id)?.EpgId); }, token);
        if (row.LogoPath is { } path && File.Exists(path)) return path;
        var epg = await Ui(() => row.Epg is { } id ? M.EpgChannels.GetValueOrDefault(id) : null, token);
        return await M.Logos.ResolveAsync(row.Channel, row.Custom, epg, cancellationToken: token);
    }
    private async Task<Programme> PlayableProgrammeAsync(string channelId, DateTimeOffset start, CancellationToken token)
    {
        var row = await Ui(() => { var c = Allowed(channelId); return (c.Channel, Epg: M.Mappings.GetValueOrDefault(c.Id)?.EpgId); }, token);
        var now = DateTimeOffset.UtcNow;
        if (row.Epg is null || start > now) throw new RemoteException("Передача ещё не началась или отсутствует в телепрограмме.", 404);
        var programme = (await Task.Run(() => M.Store.GetNowNextAsync(row.Epg, start, token), token)).Current;
        if (programme is null || programme.Start != start || programme.End <= now && !Catchup.CanPlay(row.Channel, programme, now))
            throw new RemoteException("Выбранная передача недоступна в архиве этого канала.", 404);
        return programme;
    }
    public Task<StreamSource> SourceAsync(string channelId, CancellationToken token) => SourceAsync(channelId, null, token);
    public async Task<StreamSource> SourceAsync(string channelId, DateTimeOffset? programmeStart, CancellationToken token)
    {
        var programme = programmeStart is { } start ? await PlayableProgrammeAsync(channelId, start, token) : null;
        return await Ui(() =>
    {
        var c = Allowed(channelId); var url = c.Channel.Url;
        var now = DateTimeOffset.UtcNow;
        if (programme is not null && programme.End <= now) url = Catchup.BuildUrl(c.Channel, programme.Start, now, now);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in c.Channel.Attributes ?? new Dictionary<string, string>())
        {
            var name = key.ToLowerInvariant() switch { "http-user-agent" or "user-agent" => "User-Agent", "http-referrer" or "http-referer" or "referer" => "Referer", "http-origin" => "Origin", _ => null };
            if (name is not null && !value.Any(x => x is '\r' or '\n')) headers[name] = value;
        }
        var separator = url.IndexOf('|');
        if (separator >= 0)
        {
            foreach (var field in url[(separator + 1)..].Split('&'))
            {
                var p = field.Split('=', 2); if (p.Length != 2) continue;
                var name = p[0].ToLowerInvariant() switch { "user-agent" => "User-Agent", "referer" => "Referer", "origin" => "Origin", "cookie" => "Cookie", "authorization" => "Authorization", _ => null };
                var value = Uri.UnescapeDataString(p[1]); if (name is not null && !value.Any(x => x is '\r' or '\n')) headers[name] = value;
            }
            url = url[..separator];
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new RemoteException("Адрес канала не поддерживается Remote.", 415);
        return new StreamSource(c.Id, c.Name, uri, headers);
    }, token);
    }
    public Task<RemotePlayerState> StateAsync(CancellationToken token) => Ui(main.RemoteState, token);
    public Task CommandAsync(RemoteCommand command, CancellationToken token) => UiAsync(async () =>
    {
        token.ThrowIfCancellationRequested();
        switch (command.Action)
        {
            case "play" when command.ProgrammeStart is { } start:
                var programme = await PlayableProgrammeAsync(command.ChannelId ?? "", start, token);
                var selected = Allowed(command.ChannelId!);
                if (programme.End <= DateTimeOffset.UtcNow) await main.PlayArchiveAsync(selected, programme);
                else await main.PlayAsync(selected);
                break;
            case "favorite":
                var favorite = Allowed(command.ChannelId ?? ""); if (favorite.IsFavorite != (command.Value != 0)) await M.ToggleFavoriteAsync(favorite); break;
            case "collection.add": case "collection.remove":
                var channel = Allowed(command.ChannelId ?? "");
                if (command.Name is null || !M.State.Collections.TryGetValue(command.Name, out var collection)) throw new RemoteException("Подборка не найдена.", 404);
                collection.RemoveAll(id => id == channel.Id); if (command.Action == "collection.add") collection.Add(channel.Id);
                await M.SaveAsync(); M.RefreshFilter(); break;
            default: await main.RemoteCommandAsync(command, token); break;
        }
    }, token);
    private sealed record Row(string Id, string Name, string Group, string Playlist, int Number, string? EpgId, Channel Channel);
}
