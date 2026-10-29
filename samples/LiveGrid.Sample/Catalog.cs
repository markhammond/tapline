using Tapline.Alerts;

namespace LiveGrid.Sample;

/// <summary>A column's kind, as a filter edits it and a condition compares it (the ChalkQL blotter's column kinds).</summary>
public enum ColumnKind
{
    /// <summary>An id: values typed or pasted, matched exactly.</summary>
    Id,

    /// <summary>A whole number: at least, at most.</summary>
    Integer,

    /// <summary>A number with a fraction: at least, at most.</summary>
    Number,

    /// <summary>Text of open-ended values: values picked or typed, matched exactly or by what they start with.</summary>
    Text,

    /// <summary>Text from a short fixed list: the values ticked.</summary>
    Choice,

    /// <summary>A time: from (at or after), to (before), UTC.</summary>
    Time
}

/// <summary>
///     A column of a tab's rows: its name in SQL, as a person reads it (its heading), its kind, a choice's values; and
///     whether its grid hides it at first.
/// </summary>
public sealed record Column(string Name, string Title, ColumnKind Kind, IReadOnlyList<string>? Values = null, string? Description = null,
    bool Hidden = false)
{
    /// <summary>A ticked column's values (region, channel, side, status): else none.</summary>
    public IReadOnlyList<string> Choices => Values ?? [];

    /// <summary>Whether it's worked out and shown, not stored: none of these is.</summary>
    public bool Derived => false;

    /// <summary>A text as SQL writes it, quoted.</summary>
    public static string Quote(string text)
    {
        return "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}

/// <summary>A path from a tab's row to another tab's: a fill's <c>order.</c>, by its order's id.</summary>
public sealed record TabPath(string Name, Tab Target, string Via, string Description);

/// <summary>
///     A time a wait for the database may run from, as a tab offers it: a column of its rows (none: the version's own time),
///     as the rule editor says it, and what it means.
/// </summary>
public sealed record WaitAnchor(string? Column, string Label, string Description);

/// <summary>
///     A tab of the page: a source's rows, and their columns named as SQL names them — as its grid shows them, a filter and
///     a sort read them, an alert's condition reads them, and a link writes them. A row is versioned — its newest version
///     stands for it (<see cref="Overrides" />) — or never changes. The sample's two, client orders and fills, are the
///     domain's (Domain/Tabs.cs).
/// </summary>
public sealed class Tab(string name, string title, Column key, IReadOnlyList<Column> columns, bool overrides, string time,
    IReadOnlyList<string>? shownOnly = null)
{
    public string Name { get; } = name;

    public string Title { get; } = title;

    public Column Key { get; } = key;

    public IReadOnlyList<Column> Columns { get; } = columns;

    /// <summary>Whether a row has versions, the newest standing for it (orders), or never changes (fills).</summary>
    public bool Overrides { get; } = overrides;

    public IReadOnlyList<TabPath> Paths { get; internal set; } = [];

    /// <summary>
    ///     What a wait for the database may run from, as the source offers: the version's own time first (the default), then
    ///     the others (the orders' when placed). None: its rows aren't waited for.
    /// </summary>
    public IReadOnlyList<WaitAnchor> WaitsFrom { get; internal set; } = [];

    /// <summary>How a wait for the database may be shown to be still waiting, as the source offers: the first, a new rule's.</summary>
    public IReadOnlyList<AlertWaitProof> WaitProofs { get; internal set; } = [];

    /// <summary>The column of when each row happened: the period a filter's chooser offers values from.</summary>
    public string Time { get; } = time;

    /// <summary>
    ///     Columns its grid shows that aren't the rows' own — worked out from them on the page (the orders' Fill%) — so
    ///     neither sorted, filtered by, nor read by a condition.
    /// </summary>
    public IReadOnlyList<string> ShownOnly { get; } = shownOnly ?? [];

    /// <summary>The columns its grid hides at first.</summary>
    public IReadOnlySet<string> HiddenAtFirst { get; } = columns.Where(c => c.Hidden).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>A column by name: null for none of its own.</summary>
    public Column? ColumnOf(string name)
    {
        return Columns.FirstOrDefault(c => c.Name == name);
    }
}

/// <summary>
///     The tabs a page shows, the first its home: the domain's (Domain/Tabs.cs), those its host serves. Everything outside
///     Domain knows the rows only by these — their columns, kinds and choices.
/// </summary>
public sealed class Catalog(IReadOnlyList<Tab> tabs)
{
    public IReadOnlyList<Tab> Tabs { get; } = tabs;

    public Tab Home => Tabs[0];

    public Tab? Of(string name)
    {
        return Tabs.FirstOrDefault(t => t.Name == name);
    }
}
