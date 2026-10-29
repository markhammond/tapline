namespace Tapline.Grid;

/// <summary>
///     Rows kept to the most recently used: past <see cref="Most" />, adding one forgets the least recently used — added,
///     or held by the window published — never one the window holds (a window holding more keeps them all, till they
///     leave it). A session's rows the reader set apart: opened, or closed with every row open; a window's rows whose
///     runs it counts, every row open. Each step costs the same however many there are, and nothing is allocated once it
///     has grown to its most. Not thread-safe: its owner's lock, or loop, guards it.
/// </summary>
internal sealed class GridRecentSet<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    private const int None = -1;
    private readonly Dictionary<TKey, int> _slots = []; // each row's slot
    private Slot[] _all = new Slot[16];
    private int[] _held = new int[16]; // the slots of the rows the window last published held (a freed one's: skipped)
    private int _heldCount, _taken, _free = None; // slots taken so far, and one free (the rest chained by Next)
    private int _least = None, _most = None; // the rows not held, by their last use: least recent first

    public GridRecentSet(int most)
    {
        Most = most;
    }

    /// <summary>The most rows kept (more while the window holds more).</summary>
    public int Most { get; }

    public int Count => _slots.Count;

    public bool Contains(in TKey key)
    {
        return _slots.ContainsKey(key);
    }

    /// <summary>
    ///     Adds a row, used now: false if it was there (used now all the same). Room made first, past <see cref="Most" />:
    ///     the least recently used not held by the window forgotten, each into <paramref name="forgotten" />.
    /// </summary>
    public bool Open(in TKey key, List<TKey> forgotten)
    {
        if (_slots.TryGetValue(key, out var s))
        {
            if (!_all[s].Held) // (to the most recent end)
            {
                Unlink(s);
                Link(s);
            }

            return false;
        }

        while (_slots.Count >= Most && _least != None) forgotten.Add(Forget(_least));
        if (_free != None) (s, _free) = (_free, _all[_free].Next);
        else
        {
            if (_taken == _all.Length) Array.Resize(ref _all, 2 * _taken);
            s = _taken++;
        }

        _all[s] = new Slot { Key = key };
        Link(s);
        _slots.Add(key, s);
        return true;
    }

    /// <summary>Removes a row: false if it wasn't there.</summary>
    public bool Close(in TKey key)
    {
        if (!_slots.Remove(key, out var s)) return false;
        Free(s);
        return true;
    }

    /// <summary>Every row removed.</summary>
    public void Clear()
    {
        _slots.Clear();
        (_heldCount, _taken, _free, _least, _most) = (0, 0, None, None, None);
    }

    /// <summary>
    ///     A window published, holding <paramref name="held" /> of the rows: used now, and kept while it holds them. Those
    ///     it held before and doesn't now are used now too, the most recent of the rest.
    /// </summary>
    public void Shown(ReadOnlySpan<TKey> held)
    {
        if (_slots.Count == 0 && _heldCount == 0) return;
        foreach (var key in held)
            if (_slots.TryGetValue(key, out var s))
                _all[s].Marked = true;
        for (var i = 0; i < _heldCount; i++)
        {
            var s = _held[i];
            if (!_all[s].Held || _all[s].Marked) continue;
            _all[s].Held = false;
            Link(s);
        }

        _heldCount = 0;
        foreach (var key in held)
        {
            if (!_slots.TryGetValue(key, out var s) || !_all[s].Marked) continue; // (gone since; or a key twice)
            _all[s].Marked = false;
            if (!_all[s].Held)
            {
                Unlink(s);
                _all[s].Held = true;
            }

            if (_heldCount == _held.Length) Array.Resize(ref _held, 2 * _heldCount);
            _held[_heldCount++] = s;
        }
    }

    private TKey Forget(int s)
    {
        var key = _all[s].Key;
        _slots.Remove(key);
        Free(s);
        return key;
    }

    private void Free(int s)
    {
        if (!_all[s].Held) Unlink(s);
        _all[s] = new Slot { Next = _free };
        _free = s;
    }

    /// <summary>To the most recent end.</summary>
    private void Link(int s)
    {
        (_all[s].Prev, _all[s].Next) = (_most, None);
        if (_most != None) _all[_most].Next = s;
        else _least = s;
        _most = s;
    }

    private void Unlink(int s)
    {
        var (prev, next) = (_all[s].Prev, _all[s].Next);
        if (prev != None) _all[prev].Next = next;
        else _least = next;
        if (next != None) _all[next].Prev = prev;
        else _most = prev;
    }

    private struct Slot
    {
        public TKey Key;
        public int Prev, Next; // by last use, among those not held (Next: the free ones' chain too)
        public bool Held; // the window published holds it: kept, out of the chain
        public bool Marked; // the window being published holds it (as that's worked out)
    }
}
