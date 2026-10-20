using System.Text.Json;

namespace DzhiggaTV.App;

public static class SportsSelection
{
    public const int MaximumChannels = 9;

    public static IReadOnlyList<ChannelItem> Resolve(MainViewModel model, IEnumerable<string> ids)
    {
        var allowed = model.AllChannels.Where(model.IsAllowed).ToDictionary(c => c.Id);
        return ids.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).Where(allowed.ContainsKey).Take(MaximumChannels).Select(id => allowed[id]).ToArray();
    }

    public static IReadOnlyList<ChannelItem> Restore(MainViewModel model)
    {
        try { return Resolve(model, JsonSerializer.Deserialize<string[]>(model.Setting("sports.channels", "[]")) ?? []); }
        catch (JsonException) { return []; }
    }

    public static (IReadOnlyList<ChannelItem> Channels, string? AudioId, bool ShowChannelPicker) ForOpening(MainViewModel model)
    {
        var saved = Restore(model);
        var active = model.Active is { } current
            ? model.AllChannels.FirstOrDefault(c => c.Id == current.Id && model.IsAllowed(c)) : null;
        var channels = active is null ? saved : Resolve(model, new[] { active.Id }.Concat(saved.Select(c => c.Id)));
        var savedAudioId = model.Setting("sports.audio", "");
        var audioId = active?.Id ?? (channels.Any(c => c.Id == savedAudioId) ? savedAudioId : channels.FirstOrDefault()?.Id);
        return (channels, audioId, saved.Count == 0);
    }

    public static void Save(MainViewModel model, IEnumerable<ChannelItem> channels, string? audioId)
    {
        var selected = Resolve(model, channels.Select(c => c.Id));
        model.SetSetting("sports.channels", JsonSerializer.Serialize(selected.Select(c => c.Id)));
        model.SetSetting("sports.audio", selected.Any(c => c.Id == audioId) ? audioId! : selected.FirstOrDefault()?.Id ?? "");
    }
}

public interface ISportsAudio
{
    bool IsMuted { get; set; }
    double Volume { get; set; }
}

/// <summary>UI-thread audio ownership. Mute every old source before enabling a new one,
/// including sources that have not initialized their native player yet.</summary>
public sealed class SportsAudioFocus
{
    private Dictionary<string, ISportsAudio> _sources = new(StringComparer.Ordinal);
    private bool _muted;
    private double _volume = 70;
    public string? SelectedId { get; private set; }
    public bool IsMuted { get => _muted; set { _muted = value; Apply(); } }
    public double Volume
    {
        get => _volume;
        set { _volume = double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 70; Apply(); }
    }

    public void Replace(IEnumerable<KeyValuePair<string, ISportsAudio>> sources, string? preferredId = null)
    {
        var next = sources.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        foreach (var source in _sources.Values) source.IsMuted = true;
        _sources = next;
        SelectedId = preferredId is not null && next.ContainsKey(preferredId) ? preferredId :
            SelectedId is not null && next.ContainsKey(SelectedId) ? SelectedId : next.Keys.FirstOrDefault();
        Apply();
    }

    public void Select(string id)
    {
        if (!_sources.ContainsKey(id)) return;
        SelectedId = id; Apply();
    }

    private void Apply()
    {
        // The two passes also prevent a brief overlap when switching to an earlier tile.
        foreach (var source in _sources.Values) source.IsMuted = true;
        foreach (var source in _sources.Values) source.Volume = _volume;
        if (!_muted && SelectedId is not null && _sources.TryGetValue(SelectedId, out var selected)) selected.IsMuted = false;
    }
}
