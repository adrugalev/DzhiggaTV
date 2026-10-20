namespace DzhiggaTV.App;

/// <summary>Editable screen positions, shared by checkbox selection, drag and drop and playback.</summary>
public sealed class SportsLayout
{
    private readonly List<string?> _slots;
    public IReadOnlyList<string?> Slots => _slots.AsReadOnly();
    public int Count => _slots.Count(id => id is not null);
    public int Columns => ColumnsFor(_slots.Count);
    public static int ColumnsFor(int screens) => screens <= 1 ? 1 : 2;

    public SportsLayout(IEnumerable<string?> slots)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        _slots = slots.Take(SportsSelection.MaximumChannels)
            .Select(id => !string.IsNullOrEmpty(id) && seen.Add(id) ? id : null).ToList();
        if (_slots.Count == 0) _slots.Add(null);
    }

    public bool Resize(int size)
    {
        if (size is < 1 or > SportsSelection.MaximumChannels || _slots.Skip(size).Any(id => id is not null)) return false;
        if (size < _slots.Count) _slots.RemoveRange(size, _slots.Count - size);
        while (_slots.Count < size) _slots.Add(null);
        return true;
    }

    public bool Select(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        if (_slots.Contains(id)) return true;
        int target = _slots.IndexOf(null);
        if (target >= 0) return Place(id, target);
        if (!Resize(_slots.Count + 1)) return false;
        return Place(id, _slots.Count - 1);
    }

    public void Remove(string id)
    {
        int index = _slots.IndexOf(id);
        if (index >= 0) _slots[index] = null;
    }

    public bool Place(string id, int target)
    {
        if (string.IsNullOrEmpty(id) || target < 0 || target >= _slots.Count) return false;
        int source = _slots.IndexOf(id);
        if (source >= 0) _slots[source] = _slots[target];
        _slots[target] = id;
        return true;
    }

    public void Reset() { for (int i = 0; i < _slots.Count; i++) _slots[i] = null; }
}
