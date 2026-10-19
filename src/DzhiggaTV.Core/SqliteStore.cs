using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DzhiggaTV.Core;

/// <summary>Per-user encrypted playlists and atomic XMLTV replacement in a WAL database.</summary>
public sealed class SqliteStore : IDisposable
{
    private readonly string _connectionString;
    private readonly ISecretProtector _protector;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private bool _initialized;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = false };

    public SqliteStore(string databasePath, ISecretProtector? protector = null)
    {
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private,
            DefaultTimeout = 30, Pooling = true
        }.ToString();
        _protector = protector ?? new DpapiSecretProtector();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, null, """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                CREATE TABLE IF NOT EXISTS playlist_sources(id TEXT PRIMARY KEY, name TEXT NOT NULL, payload TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS channels(id TEXT PRIMARY KEY, playlist_id TEXT NOT NULL, ordinal INTEGER NOT NULL, payload TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_channels_playlist ON channels(playlist_id,ordinal);
                CREATE TABLE IF NOT EXISTS user_state(id INTEGER PRIMARY KEY CHECK(id=1), payload TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, payload TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS mappings(channel_id TEXT PRIMARY KEY, epg_id TEXT NOT NULL, name TEXT NOT NULL,
                    source TEXT NOT NULL, confidence REAL NOT NULL, manual INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_mappings_epg ON mappings(epg_id);
                CREATE TABLE IF NOT EXISTS epg_channels(id TEXT PRIMARY KEY, name TEXT NOT NULL, logo TEXT, display_names TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS programmes(id INTEGER PRIMARY KEY, channel_id TEXT NOT NULL, title TEXT NOT NULL,
                    start INTEGER NOT NULL, end INTEGER NOT NULL, description TEXT, category TEXT);
                CREATE INDEX IF NOT EXISTS ix_programmes_channel_time ON programmes(channel_id,start,end);
                CREATE INDEX IF NOT EXISTS ix_programmes_end ON programmes(end);
                CREATE VIRTUAL TABLE IF NOT EXISTS programme_titles USING fts5(title, content='programmes',content_rowid='id', tokenize='unicode61');
                CREATE TRIGGER IF NOT EXISTS programmes_insert AFTER INSERT ON programmes BEGIN
                    INSERT INTO programme_titles(rowid,title) VALUES(new.id,new.title);
                END;
                CREATE TRIGGER IF NOT EXISTS programmes_delete AFTER DELETE ON programmes BEGIN
                    INSERT INTO programme_titles(programme_titles,rowid,title) VALUES('delete',old.id,old.title);
                END;
                PRAGMA user_version=1;
                """, cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally { _writer.Release(); }
    }

    public async Task SavePlaylistAsync(PlaylistSource source, IReadOnlyList<Channel> channels,
        CancellationToken cancellationToken = default)
    {
        if (channels.Any(channel => channel.PlaylistId != source.Id))
            throw new ArgumentException("A channel belongs to a different playlist.", nameof(channels));
        if (channels.Select(channel => channel.Id).Distinct(StringComparer.Ordinal).Count() != channels.Count)
            throw new ArgumentException("Playlist channel IDs must be unique.", nameof(channels));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO playlist_sources(id,name,payload) VALUES($id,$name,$payload) ON CONFLICT(id) DO UPDATE SET name=excluded.name,payload=excluded.payload";
                command.Parameters.AddWithValue("$id", source.Id);
                command.Parameters.AddWithValue("$name", source.Name);
                command.Parameters.AddWithValue("$payload", Protect(source with { LastUpdated = DateTimeOffset.UtcNow }));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM channels WHERE playlist_id=$id";
                command.Parameters.AddWithValue("$id", source.Id);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO channels(id,playlist_id,ordinal,payload) VALUES($id,$playlist,$order,$payload)";
                var id = command.Parameters.Add("$id", SqliteType.Text);
                var playlist = command.Parameters.AddWithValue("$playlist", source.Id);
                var order = command.Parameters.Add("$order", SqliteType.Integer);
                var payload = command.Parameters.Add("$payload", SqliteType.Text);
                command.Prepare();
                foreach (var channel in channels)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    id.Value = channel.Id; order.Value = channel.Order; payload.Value = Protect(channel);
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            transaction.Commit();
        }
        finally { _writer.Release(); }
    }

    public async Task<IReadOnlyList<Channel>> GetChannelsAsync(string? playlistId = null, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM channels" + (playlistId is null ? " ORDER BY playlist_id,ordinal" : " WHERE playlist_id=$playlist ORDER BY ordinal");
        if (playlistId is not null) command.Parameters.AddWithValue("$playlist", playlistId);
        var result = new List<Channel>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(Unprotect<Channel>(reader.GetString(0)));
        return result;
    }

    public async Task<IReadOnlyList<PlaylistSource>> GetPlaylistSourcesAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM playlist_sources ORDER BY name";
        var result = new List<PlaylistSource>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(Unprotect<PlaylistSource>(reader.GetString(0)));
        return result;
    }

    public async Task RemovePlaylistAsync(string playlistId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM channels WHERE playlist_id=$id; DELETE FROM playlist_sources WHERE id=$id";
            command.Parameters.AddWithValue("$id", playlistId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit();
        }
        finally { _writer.Release(); }
    }

    public async Task SaveUserStateAsync(UserState state, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await WriteSingleAsync("INSERT INTO user_state(id,payload) VALUES(1,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload",
            [("$payload", Protect(state))], cancellationToken).ConfigureAwait(false);
    }

    public async Task<UserState> LoadUserStateAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM user_state WHERE id=1";
        var payload = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return payload is string value ? Unprotect<UserState>(value) : new UserState();
    }

    public async Task SetSettingAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await WriteSingleAsync("INSERT INTO settings(key,payload) VALUES($key,$payload) ON CONFLICT(key) DO UPDATE SET payload=excluded.payload",
            [("$key", key), ("$payload", Protect(value))], cancellationToken).ConfigureAwait(false);
    }

    public async Task<T> GetSettingAsync<T>(string key, T fallback, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM settings WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        var payload = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return payload is string value ? Unprotect<T>(value) : fallback;
    }

    public Task SaveMappingsAsync(IReadOnlyList<ChannelMapping> mappings, CancellationToken cancellationToken = default)
        => WriteMappingsAsync(mappings, null, cancellationToken);

    public Task ReplaceAutomaticMappingsAsync(IReadOnlyList<ChannelMapping> mappings, IReadOnlyList<string> channelIds,
        CancellationToken cancellationToken = default) => WriteMappingsAsync(mappings, channelIds, cancellationToken);

    private async Task WriteMappingsAsync(IReadOnlyList<ChannelMapping> mappings, IReadOnlyList<string>? replaceChannelIds,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            if (replaceChannelIds is not null)
            {
                await using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM mappings WHERE channel_id=$id AND manual=0";
                var deleteId = delete.Parameters.Add("$id", SqliteType.Text);
                delete.Prepare();
                foreach (var channelId in replaceChannelIds)
                {
                    deleteId.Value = channelId;
                    await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO mappings(channel_id,epg_id,name,source,confidence,manual) VALUES($id,$epg,$name,$source,$confidence,$manual)
                ON CONFLICT(channel_id) DO UPDATE SET epg_id=excluded.epg_id,name=excluded.name,source=excluded.source,
                    confidence=excluded.confidence,manual=excluded.manual WHERE mappings.manual=0 OR excluded.manual=1
                """;
            var id = command.Parameters.Add("$id", SqliteType.Text);
            var epg = command.Parameters.Add("$epg", SqliteType.Text);
            var name = command.Parameters.Add("$name", SqliteType.Text);
            var source = command.Parameters.Add("$source", SqliteType.Text);
            var confidence = command.Parameters.Add("$confidence", SqliteType.Real);
            var manual = command.Parameters.Add("$manual", SqliteType.Integer);
            command.Prepare();
            foreach (var mapping in mappings)
            {
                id.Value = mapping.ChannelId; epg.Value = mapping.EpgId; name.Value = mapping.Name;
                source.Value = mapping.Source; confidence.Value = Math.Clamp(mapping.Confidence, 0, 1); manual.Value = mapping.Manual ? 1 : 0;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            transaction.Commit();
        }
        finally { _writer.Release(); }
    }

    public async Task<IReadOnlyList<ChannelMapping>> GetMappingsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT channel_id,epg_id,name,source,confidence,manual FROM mappings";
        var result = new List<ChannelMapping>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new ChannelMapping(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetDouble(4), reader.GetBoolean(5)));
        return result;
    }

    public async Task RemoveMappingAsync(string channelId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await WriteSingleAsync("DELETE FROM mappings WHERE channel_id=$id", [("$id", channelId)], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The producer receives streaming insert callbacks. Any parser/network/cancellation failure rolls back the full update.</summary>
    public async Task ImportEpgAsync(Func<Func<EpgChannel, Task>, Func<Programme, Task>, CancellationToken, Task> producer,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            await ExecuteAsync(connection, transaction, "DELETE FROM programmes; DELETE FROM epg_channels;", cancellationToken).ConfigureAwait(false);
            await using var channelCommand = connection.CreateCommand();
            channelCommand.Transaction = transaction;
            channelCommand.CommandText = "INSERT INTO epg_channels(id,name,logo,display_names) VALUES($id,$name,$logo,$names) ON CONFLICT(id) DO UPDATE SET name=excluded.name,logo=excluded.logo,display_names=excluded.display_names";
            var channelId = channelCommand.Parameters.Add("$id", SqliteType.Text);
            var channelName = channelCommand.Parameters.Add("$name", SqliteType.Text);
            var channelLogo = channelCommand.Parameters.Add("$logo", SqliteType.Text);
            var channelNames = channelCommand.Parameters.Add("$names", SqliteType.Text);
            channelCommand.Prepare();
            await using var programmeCommand = connection.CreateCommand();
            programmeCommand.Transaction = transaction;
            programmeCommand.CommandText = "INSERT INTO programmes(channel_id,title,start,end,description,category) VALUES($id,$title,$start,$end,$description,$category)";
            var programmeId = programmeCommand.Parameters.Add("$id", SqliteType.Text);
            var title = programmeCommand.Parameters.Add("$title", SqliteType.Text);
            var start = programmeCommand.Parameters.Add("$start", SqliteType.Integer);
            var end = programmeCommand.Parameters.Add("$end", SqliteType.Integer);
            var description = programmeCommand.Parameters.Add("$description", SqliteType.Text);
            var category = programmeCommand.Parameters.Add("$category", SqliteType.Text);
            programmeCommand.Prepare();
            var channelCount = 0;
            var programmeCount = 0;
            await producer(async channel =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(channel.Id)) throw new InvalidDataException("XMLTV channel has no ID.");
                channelId.Value = channel.Id; channelName.Value = channel.Name;
                channelLogo.Value = (object?)channel.Logo ?? DBNull.Value;
                channelNames.Value = JsonSerializer.Serialize(channel.DisplayNames, Json);
                await channelCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                channelCount++;
            }, async programme =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (programme.End <= programme.Start) throw new InvalidDataException("XMLTV programme end precedes its start.");
                programmeId.Value = programme.ChannelId; title.Value = programme.Title;
                start.Value = programme.Start.ToUnixTimeSeconds(); end.Value = programme.End.ToUnixTimeSeconds();
                description.Value = (object?)programme.Description ?? DBNull.Value;
                category.Value = (object?)programme.Category ?? DBNull.Value;
                await programmeCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                programmeCount++;
            }, cancellationToken).ConfigureAwait(false);
            if (channelCount == 0 || programmeCount == 0) throw new InvalidDataException("XMLTV contains no usable channels or programmes.");
            await using var setting = connection.CreateCommand();
            setting.Transaction = transaction;
            setting.CommandText = "INSERT INTO settings(key,payload) VALUES('epg.lastSuccessfulUpdate',$value) ON CONFLICT(key) DO UPDATE SET payload=excluded.payload";
            setting.Parameters.AddWithValue("$value", Protect(DateTimeOffset.UtcNow));
            await setting.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        finally { _writer.Release(); }
    }

    public Task ReplaceEpgAsync(IAsyncEnumerable<Programme> programmes, IReadOnlyList<EpgChannel> channels,
        CancellationToken cancellationToken = default) => ImportEpgAsync(async (addChannel, addProgramme, token) =>
    {
        foreach (var channel in channels) await addChannel(channel).ConfigureAwait(false);
        await foreach (var programme in programmes.WithCancellation(token).ConfigureAwait(false))
            await addProgramme(programme).ConfigureAwait(false);
    }, cancellationToken);

    public async Task<IReadOnlyList<EpgChannel>> GetEpgChannelsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,logo,display_names FROM epg_channels ORDER BY name";
        var result = new List<EpgChannel>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new EpgChannel(reader.GetString(0), reader.GetString(1), NullString(reader, 2))
            { DisplayNames = JsonSerializer.Deserialize<string[]>(reader.GetString(3), Json) ?? [reader.GetString(1)] });
        return result;
    }

    public async Task<NowNext> GetNowNextAsync(string epgChannelId, DateTimeOffset? at = null, CancellationToken cancellationToken = default)
    {
        var timestamp = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT channel_id,title,start,end,description,category FROM programmes WHERE channel_id=$id AND start<=$at AND end>$at ORDER BY start DESC LIMIT 1";
        command.Parameters.AddWithValue("$id", epgChannelId); command.Parameters.AddWithValue("$at", timestamp);
        Programme? current = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) current = ReadProgramme(reader);
        command.CommandText = "SELECT channel_id,title,start,end,description,category FROM programmes WHERE channel_id=$id AND start>=$after AND start>$at ORDER BY start LIMIT 1";
        command.Parameters.AddWithValue("$after", current?.End.ToUnixTimeSeconds() ?? timestamp);
        Programme? next = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) next = ReadProgramme(reader);
        return new NowNext(current, next);
    }

    public async Task<IReadOnlyList<Programme>> GetProgrammesAsync(string epgChannelId, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        if (to <= from) return [];
        return await QueryProgrammesAsync("SELECT channel_id,title,start,end,description,category FROM programmes WHERE channel_id=$id AND start<$to AND end>$from ORDER BY start",
            [("$id", epgChannelId), ("$from", from.ToUnixTimeSeconds()), ("$to", to.ToUnixTimeSeconds())], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One consistent guide snapshot for the allowed playlist channels, without opening a connection per row.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<Programme>>> GetGuideAsync(IEnumerable<string> epgChannelIds,
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, IReadOnlyList<Programme>>(StringComparer.Ordinal);
        if (to <= from) return result;
        var ids = epgChannelIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return result;
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        foreach (var batch in ids.Chunk(400))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            var parameters = batch.Select((_, index) => "$channel" + index).ToArray();
            command.CommandText = "SELECT channel_id,title,start,end,description,category FROM programmes WHERE channel_id IN (" +
                string.Join(',', parameters) + ") AND start<$to AND end>$from ORDER BY channel_id,start";
            for (int i = 0; i < batch.Length; i++) command.Parameters.AddWithValue(parameters[i], batch[i]);
            command.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds()); command.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var programme = ReadProgramme(reader);
                if (!result.TryGetValue(programme.ChannelId, out var programmes)) result[programme.ChannelId] = programmes = new List<Programme>();
                ((List<Programme>)programmes).Add(programme);
            }
        }
        transaction.Commit(); return result;
    }

    public async Task<IReadOnlyList<Programme>> SearchProgrammesAsync(string query, int limit = 100, CancellationToken cancellationToken = default)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return [];
        var expression = string.Join(" AND ", terms.Select(term => "\"" + term.Replace("\"", "\"\"") + "\"*"));
        return await QueryProgrammesAsync("SELECT p.channel_id,p.title,p.start,p.end,p.description,p.category FROM programme_titles f JOIN programmes p ON p.id=f.rowid WHERE programme_titles MATCH $query ORDER BY p.start LIMIT $limit",
            [("$query", expression), ("$limit", Math.Clamp(limit, 1, 1000))], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Earliest current or future title match per allowed EPG channel, including channels outside the viewport.</summary>
    public async Task<IReadOnlyDictionary<string, Programme>> SearchUpcomingProgrammesAsync(string query,
        IEnumerable<string> epgChannelIds, DateTimeOffset from, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, Programme>(StringComparer.Ordinal);
        var terms = System.Text.RegularExpressions.Regex.Matches(query, @"[\p{L}\p{Nd}]+")
            .Select(match => "\"" + match.Value + "\"*").ToArray();
        var ids = epgChannelIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        if (terms.Length == 0 || ids.Length == 0) return result;
        var expression = string.Join(" AND ", terms);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        foreach (var batch in ids.Chunk(400))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            var parameters = batch.Select((_, index) => "$channel" + index).ToArray();
            command.CommandText = "SELECT p.channel_id,p.title,p.start,p.end,NULL,NULL FROM programme_titles f JOIN programmes p ON p.id=f.rowid " +
                "WHERE programme_titles MATCH $query AND p.end>$from AND p.channel_id IN (" + string.Join(',', parameters) + ") ORDER BY p.start,p.id";
            command.Parameters.AddWithValue("$query", expression); command.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
            for (int i = 0; i < batch.Length; i++) command.Parameters.AddWithValue(parameters[i], batch[i]);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var programme = ReadProgramme(reader);
                result.TryAdd(programme.ChannelId, programme);
            }
        }
        transaction.Commit(); return result;
    }

    public async Task<long> GetProgrammeCountAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM programmes";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task ExportSettingsAsync(string path, CancellationToken cancellationToken = default)
    {
        var state = await LoadUserStateAsync(cancellationToken).ConfigureAwait(false);
        // Private source URLs, logos, playlist contents, and arbitrary settings are deliberately absent.
        var safeSettingNames = new HashSet<string>(["theme", "volume", "mute", "bufferMode", "resumeLastChannel", "preferredAudioLanguage", "showAdult", "reducedMotion"], StringComparer.OrdinalIgnoreCase);
        var backup = new SettingsBackup(1, state.Favorites, state.Collections, state.Hidden, state.Renames, state.CustomCategories,
            state.Settings.Where(pair => safeSettingNames.Contains(pair.Key) && !pair.Value.Contains("://", StringComparison.Ordinal)).ToDictionary(),
            await GetMappingsAsync(cancellationToken).ConfigureAwait(false));
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, backup, new JsonSerializerOptions(Json) { WriteIndented = true }, cancellationToken).ConfigureAwait(false);
    }

    private sealed record SettingsBackup(int Version, List<string> Favorites, Dictionary<string, List<string>> Collections,
        HashSet<string> Hidden, Dictionary<string, string> Renames, Dictionary<string, string> CustomCategories,
        Dictionary<string, string> Settings, IReadOnlyList<ChannelMapping> Mappings);

    private async Task<IReadOnlyList<Programme>> QueryProgrammesAsync(string sql, (string Key, object Value)[] parameters, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value);
        var result = new List<Programme>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(ReadProgramme(reader));
        return result;
    }

    private static Programme ReadProgramme(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1),
        DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)), NullString(reader, 4), NullString(reader, 5));
    private static string? NullString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private string Protect<T>(T value) => _protector.Protect(JsonSerializer.Serialize(value, Json));
    private T Unprotect<T>(string value) => JsonSerializer.Deserialize<T>(_protector.Unprotect(value), Json) ?? throw new InvalidDataException("Stored data could not be read.");
    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try { await connection.OpenAsync(cancellationToken).ConfigureAwait(false); return connection; }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }
    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
    private async Task WriteSingleAsync(string sql, (string Key, object Value)[] parameters, CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand(); command.CommandText = sql;
            foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writer.Release(); }
    }
    public void Dispose() => _writer.Dispose();
}
