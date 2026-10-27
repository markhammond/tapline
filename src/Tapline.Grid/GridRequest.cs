namespace Tapline.Grid;

/// <summary>
///     What the page asks for: the rows in view — <see cref="Count" /> from <see cref="First" /> — and pre-fetch either
///     side, <see cref="Before" /> above and <see cref="After" /> below, in the view positions of the order it last applied
///     (that order's <see cref="Holds" /> and <see cref="LetIn" />), in the view it was told of last (<see cref="View" />:
///     asked in one since replaced, it's dropped — its positions are another sort's). <see cref="Id" /> is its correlation
///     (a UUIDv7, as <c>tapline.correlation()</c> makes one): the answer's rendezvous carries it, in that byte order.
///     <see cref="Racing" />: the reader is scrolling up fast, as the page judges it — a held grid goes back to the live top
///     if fewer rows arrived above than the view holds, and otherwise lets them in faster, keeping pace. <see cref="Top" />:
///     the reader asked for the live top (the pill, Home) — the grid goes there however many arrived above, which scrolling
///     to the first row shown doesn't do while a view's worth or more wait.
/// </summary>
public readonly record struct GridRequest(
    Guid Id,
    long First,
    int Count,
    int Before,
    int After,
    int Holds = 0,
    long LetIn = 0,
    int View = 0,
    bool Racing = false,
    bool Top = false)
{
    /// <summary>Rows the window spans: in view, and either side.</summary>
    public long Rows => (long)Count + Before + After;
}
