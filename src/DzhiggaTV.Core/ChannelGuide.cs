namespace DzhiggaTV.Core;

/// <summary>Immutable channel-specific view of the source schedule. Positive correction moves broadcasts later.</summary>
public sealed record ChannelEpg(string ChannelId, string EpgId, int OffsetMinutes = 0)
{
    public const int MaximumOffsetMinutes = 1440;
    public TimeSpan Offset => TimeSpan.FromMinutes(Math.Clamp(OffsetMinutes, -MaximumOffsetMinutes, MaximumOffsetMinutes));
    public Programme? Adjust(Programme? programme) => programme is null || Offset == TimeSpan.Zero ? programme :
        programme with { Start = programme.Start + Offset, End = programme.End + Offset };
    public NowNext Adjust(NowNext schedule) => new(Adjust(schedule.Current), Adjust(schedule.Next), Adjust(schedule.AfterNext));
}

/// <summary>Queries raw EPG in source time, returning the actual broadcast time for each library channel.</summary>
public sealed class ChannelGuide(SqliteStore store)
{
    public async Task<NowNext> GetNowNextAsync(ChannelEpg channel, DateTimeOffset? at = null, CancellationToken token = default) =>
        channel.Adjust(await store.GetNowNextAsync(channel.EpgId, (at ?? DateTimeOffset.UtcNow) - channel.Offset, token).ConfigureAwait(false));

    public async Task<IReadOnlyDictionary<string, NowNext>> GetNowNextBatchAsync(IEnumerable<ChannelEpg> channels,
        DateTimeOffset? at = null, CancellationToken token = default)
    {
        var result = new Dictionary<string, NowNext>(StringComparer.Ordinal);
        var now = at ?? DateTimeOffset.UtcNow;
        foreach (var group in channels.DistinctBy(c => c.ChannelId).GroupBy(c => c.Offset))
        {
            var rows = await store.GetNowNextBatchAsync(group.Select(c => c.EpgId), now - group.Key, token).ConfigureAwait(false);
            foreach (var channel in group) result[channel.ChannelId] = channel.Adjust(rows[channel.EpgId]);
        }
        return result;
    }

    public async Task<IReadOnlyList<Programme>> GetProgrammesAsync(ChannelEpg channel, DateTimeOffset from, DateTimeOffset to, CancellationToken token = default) =>
        (await store.GetProgrammesAsync(channel.EpgId, from - channel.Offset, to - channel.Offset, token).ConfigureAwait(false))
            .Select(p => channel.Adjust(p)!).ToArray();

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<Programme>>> GetGuideAsync(IEnumerable<ChannelEpg> channels,
        DateTimeOffset from, DateTimeOffset to, CancellationToken token = default)
    {
        var result = new Dictionary<string, IReadOnlyList<Programme>>(StringComparer.Ordinal);
        foreach (var group in channels.DistinctBy(c => c.ChannelId).GroupBy(c => c.Offset))
        {
            var rows = await store.GetGuideAsync(group.Select(c => c.EpgId), from - group.Key, to - group.Key, token).ConfigureAwait(false);
            foreach (var channel in group) result[channel.ChannelId] = rows.GetValueOrDefault(channel.EpgId, []).Select(p => channel.Adjust(p)!).ToArray();
        }
        return result;
    }

    public async Task<Programme?> GetPreviousProgrammeAsync(ChannelEpg channel, DateTimeOffset before, DateTimeOffset earliest, CancellationToken token = default) =>
        channel.Adjust(await store.GetPreviousProgrammeAsync(channel.EpgId, before - channel.Offset, earliest - channel.Offset, token).ConfigureAwait(false));

    public async Task<IReadOnlyList<Programme>> GetUpcomingProgrammesAsync(ChannelEpg channel, DateTimeOffset from, int limit = 24, CancellationToken token = default) =>
        (await store.GetUpcomingProgrammesAsync(channel.EpgId, from - channel.Offset, limit, token).ConfigureAwait(false)).Select(p => channel.Adjust(p)!).ToArray();
}
