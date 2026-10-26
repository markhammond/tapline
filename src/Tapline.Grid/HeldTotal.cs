namespace Tapline.Grid;

/// <summary>
///     How many rows a held grid shows in all: kept as it was while the grid holds its place, so the scroll bar stays
///     put. Taken afresh only when it drifts by more than 1 % (within, it holds), when a read finds where the rows end
///     (<see cref="Set" />), or when rows are let in above (<see cref="Shift" />).
/// </summary>
internal struct HeldTotal
{
    private long _held;
    private bool _holding;

    /// <summary>The total to show, given the one worked out afresh: the one held, unless it drifted past 1 %.</summary>
    public long Hold(long rows)
    {
        if (_holding && Math.Abs(rows - _held) * 100 <= _held) return _held;
        _held = rows;
        _holding = true;
        return rows;
    }

    /// <summary>A read found where the rows end: that's the total now.</summary>
    public void Set(long rows)
    {
        _held = rows;
        _holding = true;
    }

    /// <summary>Rows were let in above: the total grows by as many.</summary>
    public void Shift(long rows)
    {
        if (_holding) _held += rows;
    }

    /// <summary>The grid follows the top again, or holds a new place: the next total is taken afresh.</summary>
    public void Clear()
    {
        _holding = false;
    }
}
