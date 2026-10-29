namespace Tapline.Grid;

/// <summary>
///     The rows a reader has open, and where their runs put the grid's rows: a row of the view stands at its top position
///     (where it would with none open) plus the rows of the runs open above it. Each open row is placed where a read last
///     found it, unplaced till one does, with its run's length as last read, none till then: what isn't known isn't
///     counted, so the positions drawn stay as drawn till a read says otherwise. Not thread-safe: its window's loop alone
///     uses it.
/// </summary>
internal sealed class GridOpenRows<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    private readonly Dictionary<TKey, Entry> _open = [];
    private long[] _ats = new long[16], _lengths = new long[16], _before = new long[17]; // the placed ones, by position
    private bool _dirty;
    private int _placed;
    private TKey[] _scratch = new TKey[16];
    private long _shift; // added to every position kept: moving them all costs nothing

    public int Count => _open.Count;

    public bool IsOpen(in TKey key)
    {
        return _open.ContainsKey(key);
    }

    /// <summary>Opens a row, unplaced, its run unread: false if it was open.</summary>
    public bool Open(in TKey key)
    {
        return _open.TryAdd(key, default);
    }

    /// <summary>Closes a row: false if it wasn't open.</summary>
    public bool Close(in TKey key)
    {
        if (!_open.Remove(key, out var was)) return false;
        _dirty |= was.Placed;
        return true;
    }

    /// <summary>Every row closed.</summary>
    public void Clear()
    {
        _open.Clear();
        (_shift, _dirty) = (0, true);
    }

    /// <summary>The rows open, into <paramref name="into" />.</summary>
    public void CopyKeys(List<TKey> into)
    {
        foreach (var key in _open.Keys) into.Add(key);
    }

    /// <summary>An open row's top position (-1: unplaced, or not open).</summary>
    public long AtOf(in TKey key)
    {
        return _open.TryGetValue(key, out var e) && e.Placed ? e.At + _shift : -1;
    }

    /// <summary>How long an open row's run was as last read: false if it isn't open, or its run hasn't been read.</summary>
    public bool TryLength(in TKey key, out long length)
    {
        length = _open.TryGetValue(key, out var e) && e.Read ? e.Length : 0;
        return e.Read;
    }

    public void SetLength(in TKey key, long length)
    {
        if (!_open.TryGetValue(key, out var e) || (e.Read && e.Length == length)) return;
        (e.Length, e.Read) = (length, true);
        _open[key] = e;
        _dirty |= e.Placed;
    }

    /// <summary>
    ///     A read of the view's rows, <paramref name="keys" /> at top positions from <paramref name="first" />: the open ones
    ///     among them placed there; an open one placed among them but not there now (moved, or gone) unplaced.
    /// </summary>
    public void Learn(ReadOnlySpan<TKey> keys, long first)
    {
        if (_open.Count == 0 || keys.IsEmpty) return;
        var end = first + keys.Length;
        var moved = 0;
        foreach (var (key, e) in _open)
            if (e.Placed && e.At + _shift >= first && e.At + _shift < end && !keys[(int)(e.At + _shift - first)].Equals(key))
                Note(ref moved, key);
        for (var i = 0; i < moved; i++) Unplace(_scratch[i]);
        for (var i = 0; i < keys.Length; i++)
            if (_open.ContainsKey(keys[i]))
                Place(keys[i], first + i);
    }

    /// <summary>Every placed one moves by <paramref name="by" /> (rows let in above; positions turned to ranks).</summary>
    public void Shift(long by)
    {
        _shift += by;
    }

    /// <summary>A new sort or filter: every open row unplaced, its run unread (another view's), still open.</summary>
    public void Unplace()
    {
        var n = 0;
        foreach (var key in _open.Keys) Note(ref n, key);
        for (var i = 0; i < n; i++) _open[_scratch[i]] = default;
        (_shift, _dirty) = (0, true);
    }

    /// <summary>The view position of the view's row at top position <paramref name="top" />.</summary>
    public long Display(long top)
    {
        return top + Before(top);
    }

    /// <summary>The rows of the runs open above top position <paramref name="top" />.</summary>
    public long Before(long top)
    {
        if (_open.Count == 0) return 0;
        Sort();
        return _before[Below(top - _shift)];
    }

    /// <summary>The rows of the runs open above the first <paramref name="rows" /> of the view: the total's runs.</summary>
    public long RunRows(long rows)
    {
        return Before(rows);
    }

    /// <summary>
    ///     What stands at view position <paramref name="display" />: the view's row at top position Top (Offset -1), or the
    ///     Offset'th row of its run.
    /// </summary>
    public (long Top, long Offset) Locate(long display)
    {
        if (_open.Count == 0) return (display, -1);
        Sort();
        int low = 0, high = _placed; // (the open rows placed before it: those whose own row stands above it)
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (_ats[middle] + _shift + _before[middle] < display) low = middle + 1;
            else high = middle;
        }

        if (low > 0)
        {
            var i = low - 1;
            var row = _ats[i] + _shift + _before[i];
            if (display <= row + _lengths[i]) return (_ats[i] + _shift, display - row - 1);
        }

        return (display - _before[low], -1);
    }

    /// <summary>How many placed open rows stand above stored position <paramref name="at" />.</summary>
    private int Below(long at)
    {
        int low = 0, high = _placed;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (_ats[middle] < at) low = middle + 1;
            else high = middle;
        }

        return low;
    }

    private void Place(in TKey key, long at)
    {
        var e = _open[key];
        if (e.Placed && e.At == at - _shift) return;
        (e.At, e.Placed) = (at - _shift, true);
        _open[key] = e;
        _dirty = true;
    }

    private void Unplace(in TKey key)
    {
        var e = _open[key];
        if (!e.Placed) return;
        e.Placed = false;
        _open[key] = e;
        _dirty = true;
    }

    private void Note(ref int n, in TKey key)
    {
        if (n == _scratch.Length) Array.Resize(ref _scratch, 2 * n);
        _scratch[n++] = key;
    }

    /// <summary>The placed ones by position, each with the rows of the runs above it (kept till one moves or grows).</summary>
    private void Sort()
    {
        if (!_dirty) return;
        _dirty = false;
        if (_ats.Length < _open.Count)
        {
            var size = Math.Max(_open.Count, 2 * _ats.Length);
            (_ats, _lengths, _before) = (new long[size], new long[size], new long[size + 1]);
        }

        _placed = 0;
        foreach (var (_, e) in _open)
            if (e.Placed)
                (_ats[_placed], _lengths[_placed++]) = (e.At, e.Length);
        Array.Sort(_ats, _lengths, 0, _placed);
        for (var i = 0; i < _placed; i++) _before[i + 1] = _before[i] + _lengths[i];
    }

    private struct Entry
    {
        public long At; // where it stands, less _shift
        public bool Placed, Read;
        public long Length;
    }
}
