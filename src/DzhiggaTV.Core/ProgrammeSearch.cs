namespace DzhiggaTV.Core;

public sealed record ProgrammeSearchScope(string ChannelId, string EpgChannelId, DateTimeOffset? ArchiveFrom = null, int OffsetMinutes = 0)
{
    public static ProgrammeSearchScope From(Channel channel, string epgChannelId, DateTimeOffset now, int offsetMinutes = 0) =>
        new(channel.Id, epgChannelId, Catchup.From(channel)?.Earliest(now), offsetMinutes);
}

public static class ProgrammeSearch
{
    public static bool IsAvailable(Channel channel, Programme programme, DateTimeOffset now) =>
        programme.Start <= now && programme.End > now || Catchup.CanPlay(channel, programme, now);
}
