using L = DzhiggaTV.Localization.Texts;
using DzhiggaTV.Core;

namespace DzhiggaTV.App;

public sealed partial class MainViewModel
{
    public int EpgTimeRevision { get; private set; }
    public int EpgOffset(string channelId) => Math.Clamp(State.EpgOffsets.GetValueOrDefault(channelId), -ChannelEpg.MaximumOffsetMinutes, ChannelEpg.MaximumOffsetMinutes);
    public ChannelEpg? EpgFor(string channelId) => Mappings.TryGetValue(channelId, out var mapping) ? new(channelId, mapping.EpgId, EpgOffset(channelId)) : null;
    public async Task SetEpgOffsetAsync(string channelId, int minutes)
    {
        if (minutes is < -ChannelEpg.MaximumOffsetMinutes or > ChannelEpg.MaximumOffsetMinutes) throw new ArgumentOutOfRangeException(nameof(minutes));
        if (!AllChannels.Any(c => c.Id == channelId)) throw new ArgumentException(L.T("Канал не найден."), nameof(channelId));
        if (minutes == EpgOffset(channelId)) return;
        if (minutes == 0) State.EpgOffsets.Remove(channelId); else State.EpgOffsets[channelId] = minutes;
        ++EpgTimeRevision; ++_catalogGeneration; _programmeSearchQuery = ""; _programmeMatches = new Dictionary<string, Programme>();
        var channel = AllChannels.Single(c => c.Id == channelId); channel.SearchProgramme = null;
        await SaveAsync();
        await UpdateNowNextAsync([channel]);
        Raise(nameof(EpgTimeRevision));
    }
}
