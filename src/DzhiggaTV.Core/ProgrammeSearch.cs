namespace DzhiggaTV.Core;

public sealed record ProgrammeSearchScope(string ChannelId, string EpgChannelId, DateTimeOffset? ArchiveFrom = null)
{
    public static ProgrammeSearchScope From(Channel channel, string epgChannelId, DateTimeOffset now) =>
        new(channel.Id, epgChannelId, Catchup.From(channel)?.Earliest(now));
}

public static class ProgrammeSearch
{
    public static bool IsAvailable(Channel channel, Programme programme, DateTimeOffset now) =>
        programme.Start <= now && programme.End > now || Catchup.CanPlay(channel, programme, now);
}
