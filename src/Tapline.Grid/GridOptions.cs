namespace Tapline.Grid;

/// <summary>
///     A grid session's bounds and cadences. Bounds broken are refused when the session is made, with the reason
///     (<see cref="GridRefusedException" />), never ignored.
/// </summary>
public sealed record GridOptions
{
    public static GridOptions Default { get; } = new();

    /// <summary>
    ///     Rows a window may span: in view, and pre-fetch either side (a request for more is refused). Its rows are the
    ///     session's memory, which a client's Tapline budget doesn't count: this bounds it.
    /// </summary>
    public int MaxWindowRows { get; init; } = 1000;

    /// <summary>Grid sessions one client (a circuit's outlet) may hold at once: another is refused.</summary>
    public int MaxSessionsPerClient { get; init; } = 8;

    /// <summary>At the live top, the least time from one read to the next for the source's changes.</summary>
    public TimeSpan LiveEvery { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Held, the least time from one read to the next for the source's changes.</summary>
    public TimeSpan HeldEvery { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>While the page is hidden: the source's changes aren't read, and the subscription's least interval is this.</summary>
    public TimeSpan HiddenInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Held, rows arrived above are let in once the view's first row is above this position.</summary>
    public int RevealNear { get; init; } = 200;

    /// <summary>At most this many arrived rows let in at a time (0: none, until the grid is back at the top).</summary>
    public int RevealAtOnce { get; init; } = 30;

    /// <summary>Why these can't work, or null.</summary>
    internal string? Broken()
    {
        if (MaxWindowRows < 1) return $"A window of at most {MaxWindowRows} rows can show none (GridOptions.MaxWindowRows).";
        if (MaxSessionsPerClient < 1)
            return $"At most {MaxSessionsPerClient} grids a client: none can open (GridOptions.MaxSessionsPerClient).";
        if (LiveEvery < TimeSpan.Zero || HeldEvery < TimeSpan.Zero || HiddenInterval < TimeSpan.Zero)
            return "A cadence can't be negative (GridOptions.LiveEvery, HeldEvery, HiddenInterval).";
        if (RevealNear < 0 || RevealAtOnce < 0)
            return "Letting rows in can't be by a negative number (GridOptions.RevealNear, RevealAtOnce).";
        return null;
    }
}

/// <summary>A grid session refused when made: <see cref="Exception.Message" /> says why.</summary>
public sealed class GridRefusedException(string reason) : InvalidOperationException(reason);

/// <summary>What a grid's chrome shows, as of the session's last answer.</summary>
public readonly record struct GridStatus
{
    /// <summary>Following the live top; else held.</summary>
    public bool Live { get; init; }

    /// <summary>How often the grid has gone from the live top to held (or been placed at a row).</summary>
    public int Holds { get; init; }

    /// <summary>Rows in all, as the page scrolls through them.</summary>
    public long Total { get; init; }

    public TotalKind TotalKind { get; init; }

    /// <summary>Held: rows arrived above, not let in.</summary>
    public long Above { get; init; }

    /// <summary>Held: rows let in above so far.</summary>
    public long LetIn { get; init; }

    /// <summary>The source's version (or watermark) as of the last answer.</summary>
    public long Version { get; init; }

    /// <summary>Why the last read was refused (the rows shown are as they were), or null.</summary>
    public string? Refusal { get; init; }
}
