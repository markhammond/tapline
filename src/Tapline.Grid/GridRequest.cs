namespace Tapline.Grid;

/// <summary>
///     What the page asks for: the rows in view — <see cref="Count" /> from <see cref="First" /> — and pre-fetch either
///     side, <see cref="Before" /> above and <see cref="After" /> below, in the view positions of the order it last applied
///     (that order's <see cref="Holds" /> and <see cref="LetIn" />), in the view it was told of last (<see cref="View" />:
///     asked in one since replaced, it's dropped — its positions are another sort's). <see cref="Id" /> is its correlation
///     (a UUIDv7, as <c>tapline.correlation()</c> makes one): the answer's rendezvous carries it, in that byte order.
/// </summary>
public readonly record struct GridRequest(
    Guid Id,
    long First,
    int Count,
    int Before,
    int After,
    int Holds = 0,
    long LetIn = 0,
    int View = 0)
{
    /// <summary>Rows the window spans: in view, and either side.</summary>
    public long Rows => (long)Count + Before + After;
}
