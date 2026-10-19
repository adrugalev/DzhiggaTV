namespace DzhiggaTV.Core;

public sealed record Channel(string Id, string PlaylistId, string Name, string Group, string Url,
    string? TvgId = null, string? Logo = null, IReadOnlyDictionary<string, string>? Attributes = null, int Order = 0);

public sealed record EpgChannel(string Id, string Name, string? Logo = null)
{
    public IReadOnlyList<string> DisplayNames { get; init; } = [Name];
}

public sealed record Programme(string ChannelId, string Title, DateTimeOffset Start, DateTimeOffset End,
    string? Description = null, string? Category = null);

public sealed record ChannelMapping(string ChannelId, string EpgId, string Name, string Source,
    double Confidence, bool Manual = false);

public sealed record PlaylistSource(string Id, string Name, string Location, bool IsRemote = false,
    DateTimeOffset? LastUpdated = null);

public sealed record NowNext(Programme? Current, Programme? Next, Programme? AfterNext = null);

public sealed class UserState
{
    public List<string> Favorites { get; set; } = [];
    public Dictionary<string, List<string>> Collections { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> Hidden { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Renames { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> CustomCategories { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> UserLogos { get; set; } = new(StringComparer.Ordinal);
    public List<string> History { get; set; } = [];
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.Ordinal);

    public void RecordWatched(string channelId, int maximum = 100)
    {
        History.Remove(channelId);
        History.Insert(0, channelId);
        if (History.Count > maximum) History.RemoveRange(maximum, History.Count - maximum);
    }

    public void MoveFavorite(int from, int to) => Move(Favorites, from, to);
    public void MoveCollectionChannel(string collection, int from, int to)
    {
        if (Collections.TryGetValue(collection, out var channels)) Move(channels, from, to);
    }

    private static void Move(List<string> items, int from, int to)
    {
        if (from < 0 || from >= items.Count || to < 0 || to >= items.Count || from == to) return;
        var item = items[from];
        items.RemoveAt(from);
        items.Insert(to, item);
    }
}
