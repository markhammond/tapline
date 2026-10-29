using Tapline.Grid.Provider;

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

    /// <summary>
    ///     The most memory the source may take answering one of the grid's reads, in bytes: carried by each read
    ///     (<see cref="GridRead{TKey,TRow}.MaxBytes" />), so the host can budget by who the user is. Counted as the source
    ///     takes it, never estimated; a read past it is refused, saying why. None (0): the source's own bound.
    /// </summary>
    public uint MaxReadBytes { get; init; }

    /// <summary>
    ///     Open rows a session remembers, their runs shown (<see cref="GridSession{TKey,TRow}.SetOpen" />): past so many,
    ///     opening another forgets — closes — the one least recently used (opened, or held by the window), never one the
    ///     window holds. Every row open (<see cref="GridSession{TKey,TRow}.SetAllOpen" />), the rows closed are remembered
    ///     so, one forgotten open again; and the window counts the runs of as many rows read (at least a read's), the
    ///     least recently read forgotten, uncounted till read again. Each costs its session a few dozen bytes, wherever it
    ///     is; those in the window cost their reads, which the window's own bound bounds.
    /// </summary>
    public int MaxOpenRows { get; init; } = 4096;

    /// <summary>At the live top, the least time from one read to the next for the source's changes.</summary>
    public TimeSpan LiveEvery { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Held, the least time from one read to the next for the source's changes.</summary>
    public TimeSpan HeldEvery { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    ///     While the page is passive (hidden, or its grid out of view): the source's changes aren't read — the status says
    ///     once the source has moved (<see cref="GridStatusValue.Moved" />), all that goes out — and the subscription's
    ///     least interval is this.
    /// </summary>
    public TimeSpan HiddenInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Held, rows arrived above are let in once the view's first row is above this position.</summary>
    public int RevealNear { get; init; } = 200;

    /// <summary>At most this many arrived rows let in at a time (0: none, until the grid is back at the top).</summary>
    public int RevealAtOnce { get; init; } = 30;

    /// <summary>
    ///     How long the rows either side of the view (the page's overscan) may take to arrive, both sides at once: the
    ///     session tells the page how many its link carries in this long, at the delivery rate Tapline's adaptive flow
    ///     control measures for the client — none until it has measured it, then more as the link shows it carries them
    ///     (in powers of two), and fewer only while the link is the limit (a link the application leaves idle says nothing
    ///     of what it could carry: a reader at rest keeps the rows held, and gets more while the link has room:
    ///     <see cref="OverscanClimb" />). The page holds no more than its own count
    ///     (<c>LiveGrid.OverscanCount</c>), and, on a link that carries fewer than a view in this long, no more than that
    ///     ahead of the view either, rather than a view: at the live top on 16 kbit/s, the view and a few rows below it.
    ///     Zero, or fixed flow control: its own count, and a view ahead, however slow the link.
    /// </summary>
    public TimeSpan OverscanFill { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     Pre-fetch while the reader rests: once the page has asked with the overscan last told, rested this long since,
    ///     and the watch's state isn't waiting to go, the overscan told climbs a step — doubled, but by no more than the
    ///     link carries in four <see cref="OverscanFill" />s, nor than a quarter of the outlet's in-band bound
    ///     (<see cref="Outlet.OutOfBandThreshold" />) in rows, so a step goes with the stream — up to the page's own count
    ///     (<see cref="GridSession{TKey,TRow}.MostOverscan" />): a step's word goes behind the last step's rows, so the steps
    ///     go as fast as the link delivers them, and no faster. While its state has been waiting longer than a step takes
    ///     (four fills) — its changes come faster than the link carries them — it halves, a step each time the page has
    ///     asked with the last, down to what <see cref="OverscanFill" /> carries. Zero: no climbing, the fill's alone.
    /// </summary>
    public TimeSpan OverscanClimb { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     At the live top, where every row held turns over with the arrivals — an extra row is in every update — the
    ///     reader rests this long for each step instead (<see cref="OverscanClimb" />), and the climb starts afresh from the
    ///     fill as the grid goes back there. Zero: none there.
    /// </summary>
    public TimeSpan OverscanClimbLive { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>Why these can't work, or null.</summary>
    internal string? Broken()
    {
        if (MaxWindowRows < 1) return $"A window of at most {MaxWindowRows} rows can show none (GridOptions.MaxWindowRows).";
        if (MaxSessionsPerClient < 1)
            return $"At most {MaxSessionsPerClient} grids a client: none can open (GridOptions.MaxSessionsPerClient).";
        if (MaxOpenRows < 0) return $"At most {MaxOpenRows} rows open: a bound can't be negative (GridOptions.MaxOpenRows).";
        if (LiveEvery < TimeSpan.Zero || HeldEvery < TimeSpan.Zero || HiddenInterval < TimeSpan.Zero)
            return "A cadence can't be negative (GridOptions.LiveEvery, HeldEvery, HiddenInterval).";
        if (RevealNear < 0 || RevealAtOnce < 0)
            return "Letting rows in can't be by a negative number (GridOptions.RevealNear, RevealAtOnce).";
        if (OverscanFill < TimeSpan.Zero) return "The overscan's fill can't take a negative time (GridOptions.OverscanFill).";
        if (OverscanClimb < TimeSpan.Zero || OverscanClimbLive < TimeSpan.Zero)
            return "The overscan can't climb at a negative pace (GridOptions.OverscanClimb, OverscanClimbLive).";
        return null;
    }
}

/// <summary>Whether the page has all the server published for its grid (Tap.Sync), as its LiveGrid reports it.</summary>
public enum GridFreshness : byte
{
    /// <summary>Not known yet; and always, for a session with no page (<see cref="GridSession{TKey,TRow}.Status" />).</summary>
    Unknown = 0,

    /// <summary>States arriving, or the router has said the page holds all it published.</summary>
    Fresh = 1,

    /// <summary>Asked, and not answered within a second: the page may be behind.</summary>
    Behind = 2
}

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

    /// <summary>Held: rows arrived above the reader hasn't seen — not let in, or let in above the first row shown since.</summary>
    public long Above { get; init; }

    /// <summary>Held: rows let in above so far.</summary>
    public long LetIn { get; init; }

    /// <summary>The source's version (or watermark) as of the last answer.</summary>
    public long Version { get; init; }

    /// <summary>Why the last read was refused (the rows shown are as they were), or null.</summary>
    public string? Refusal { get; init; }

    /// <summary>Whether the page holds all the server published: its LiveGrid's word (a session alone leaves it unknown).</summary>
    public GridFreshness Freshness { get; init; }

    /// <summary>
    ///     The page's lag: the longest a change took from its row's stamp (<c>LiveGrid.ChangedAt</c>) to being drawn, over
    ///     the last second it drew one, as the page said it last (once it moved by 5 %, to two figures); null without
    ///     stamps, or while they don't agree with the page's clock.
    /// </summary>
    public TimeSpan? Lag { get; init; }
}
