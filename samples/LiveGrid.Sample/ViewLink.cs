using System.Collections.Immutable;
using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;
using Tapline.Grid;
using Tapline.Grid.Provider;
using SortKey = Tapline.Grid.Provider.SortKey;

namespace LiveGrid.Sample;

/// <summary>
///     A view of a tab as a link, as the ChalkQL blotter's: its sort, its filters, the columns hidden, and the row it's held
///     at unless it follows the live top. Everything is in the link, nothing kept for it, so it opens the same view anywhere,
///     for as long as the rows are there.
/// </summary>
/// <remarks>
///     <para>
///         <c>?tab=fills&amp;sort=-client,qty&amp;hide=region,channel&amp;f.client=CEDAR&amp;f.client=FERN&amp;f.qty.min=44&amp;at=…</c>:
///         a tab other than client orders; the columns of the sort, descending with a minus; the hidden columns when they
///         aren't those hidden at first (<c>hide=</c> for none); a filter's values, each once, or with <c>.starts</c> what
///         they start with, <c>.min</c> and <c>.max</c> for a number, <c>.from</c> and <c>.to</c> for a time (UTC); and the
///         row held at, by its key — as LiveGrid holds a row, wherever the sort puts it then.
///     </para>
///     <para>
///         Shrouded (<see cref="LinkShroud" />), the filters' values and the row held at, which are data, go in one opaque
///         <c>s=</c>; the tab, the sort and the hidden columns, which are only how it's laid out, stay as they are. A link may
///         give a filter or the row held at as is, shrouded, or some of each, but not one part both ways.
///     </para>
/// </remarks>
internal sealed record ViewLink(Tab Tab, IReadOnlyList<SortKey> Sort, ViewFilter? Filter, IReadOnlySet<string> Hidden, Guid? At)
{
    private const string TimeFormat = "yyyy-MM-ddTHH:mm:ss.ffffff";

    /// <summary>A tab's columns, by name, as its grid has them: those a filter and a sort read, and those only shown (the orders' Fill%).</summary>
    private static IEnumerable<string> Columns(Tab tab)
    {
        return tab.Columns.Select(c => c.Name).Concat(tab.ShownOnly);
    }

    /// <summary>
    ///     The link's query, without the <c>?</c>: empty for the <paramref name="catalog" />'s home tab as it opens. Given a
    ///     <paramref name="shroud" />, the filters' values and the row held at shrouded.
    /// </summary>
    public string Query(Catalog catalog, LinkShroud? shroud = null)
    {
        var pairs = new List<(string Key, string Value)>();
        if (Tab != catalog.Home) pairs.Add(("tab", Tab.Name));
        if (Sort.Count > 0) pairs.Add(("sort", string.Join(",", Sort.Select(k => (k.Descending ? "-" : "") + k.Column))));
        if (!Hidden.SetEquals(Tab.HiddenAtFirst)) pairs.Add(("hide", string.Join(",", Columns(Tab).Where(Hidden.Contains))));

        // What the view holds rather than how it's laid out: shrouded, if the link is.
        var open = pairs.Count;
        foreach (var filter in Filter?.Columns ?? [])
        {
            var name = "f." + filter.Column;
            pairs.AddRange(filter.Values.Select(v => (filter.Match == TextMatch.StartsWith ? name + ".starts" : name, v)));
            if (filter.Min is { } min) pairs.Add((name + ".min", min.ToString("R", CultureInfo.InvariantCulture)));
            if (filter.Max is { } max) pairs.Add((name + ".max", max.ToString("R", CultureInfo.InvariantCulture)));
            if (filter.From is { } from) pairs.Add((name + ".from", from.ToString(TimeFormat, CultureInfo.InvariantCulture)));
            if (filter.To is { } to) pairs.Add((name + ".to", to.ToString(TimeFormat, CultureInfo.InvariantCulture)));
        }

        if (At is { } at) pairs.Add(("at", at.ToString()));
        if (shroud is not null && pairs.Count > open)
        {
            var held = Join(pairs.GetRange(open, pairs.Count - open));
            pairs.RemoveRange(open, pairs.Count - open);
            pairs.Add(("s", shroud.Shroud(held)));
        }

        return Join(pairs);

        static string Join(IEnumerable<(string Key, string Value)> pairs)
        {
            return string.Join("&", pairs.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        }
    }

    /// <summary>
    ///     The view a link's query describes, of a tab of the <paramref name="catalog" /> (its home, if it names none), its
    ///     shrouded part opened by <paramref name="shroud" />.
    /// </summary>
    /// <exception cref="ArgumentException">Something in the link isn't understood: said what, and nothing of it taken.</exception>
    public static ViewLink Parse(string query, Catalog catalog, LinkShroud? shroud = null)
    {
        var pairs = QueryHelpers.ParseQuery(query);
        if (pairs.Remove("s", out var shrouded))
        {
            if (shrouded.Count != 1) throw Refuse($"'s' is given {shrouded.Count} times");
            var held = QueryHelpers.ParseQuery((shroud ?? throw Refuse("its shrouded part can't be opened here")).Unshroud(shrouded.ToString()));
            foreach (var (key, values) in held)
            {
                if (!key.StartsWith("f.", StringComparison.Ordinal) && key != "at")
                    throw Refuse($"'{key}' isn't a filter or the row held at, which alone are shrouded");
                if (!pairs.TryAdd(key, values)) throw Refuse($"'{key}' is given both as is and shrouded");
            }
        }

        var tab = pairs.TryGetValue("tab", out var named) ? catalog.Of(named.ToString()) ?? throw Refuse($"there's no tab '{named}'") : catalog.Home;
        List<SortKey> sort = [];
        if (pairs.TryGetValue("sort", out var sorted))
            foreach (var part in sorted.ToString().Split(','))
            {
                var key = part.StartsWith('-') ? new SortKey(Named(tab, part[1..]).Name, true) : new SortKey(Named(tab, part).Name);
                if (sort.Any(k => k.Column == key.Column)) throw Refuse($"it sorts by {key.Column} twice");
                sort.Add(key);
            }

        var hidden = tab.HiddenAtFirst;
        if (pairs.TryGetValue("hide", out var hide))
        {
            var names = hide.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            if (names.FirstOrDefault(n => !Columns(tab).Contains(n)) is { } unknown) throw Refuse($"{tab.Title} has no column '{unknown}'");
            if (names.Count >= Columns(tab).Count()) throw Refuse("it hides every column");
            hidden = names;
        }

        var filters = new List<ColumnFilter>();
        foreach (var group in pairs.Keys.Where(k => k.StartsWith("f.", StringComparison.Ordinal)).GroupBy(k => k[2..].Split('.')[0]))
        {
            var column = Named(tab, group.Key);
            var kind = column.Kind;
            var filter = new ColumnFilter { Column = column.Name, Kind = kind, Label = column.Title };
            foreach (var key in group)
            {
                var part = key[(2 + group.Key.Length)..];
                var values = pairs[key].Select(v => v ?? "").ToImmutableArray();
                filter = (part, kind) switch
                {
                    ("", ColumnKind.Id) when values.FirstOrDefault(v => !Guid.TryParse(v, out _)) is { } other =>
                        throw Refuse($"'{other}' isn't an id of {column.Title}"),
                    ("", ColumnKind.Id or ColumnKind.Text) => filter with { Values = filter.Values.AddRange(values) },
                    ("", ColumnKind.Choice) when values.FirstOrDefault(v => !column.Choices.Contains(v)) is { } other =>
                        throw Refuse($"{column.Title} is never '{other}'"),
                    ("", ColumnKind.Choice) => filter with { Values = filter.Values.AddRange(values) },
                    (".starts", ColumnKind.Text) => filter with { Values = filter.Values.AddRange(values), Match = TextMatch.StartsWith },
                    (".min", ColumnKind.Integer or ColumnKind.Number) => filter with { Min = Number(column, Single(key, values)) },
                    (".max", ColumnKind.Integer or ColumnKind.Number) => filter with { Max = Number(column, Single(key, values)) },
                    (".from", ColumnKind.Time) => filter with { From = Time(column, Single(key, values)) },
                    (".to", ColumnKind.Time) => filter with { To = Time(column, Single(key, values)) },
                    _ => throw Refuse($"{column.Title} has no filter '{key}'")
                };
            }

            if (filter.Match == TextMatch.StartsWith && pairs.ContainsKey("f." + group.Key))
                throw Refuse($"{column.Title} is filtered by values and by what they start with at once");
            filters.Add(filter);
        }

        Guid? at = null;
        if (pairs.TryGetValue("at", out var row))
            at = Guid.TryParse(Single("at", [.. row.Select(v => v ?? "")]), out var key) ? key : throw Refuse($"'{row}' isn't a row's key");
        if (pairs.Keys.FirstOrDefault(k => k is not ("tab" or "sort" or "hide" or "at") && !k.StartsWith("f.", StringComparison.Ordinal)) is { } stray)
            throw Refuse($"'{stray}' isn't part of a view");
        return new ViewLink(tab, sort, ViewFilter.Of(filters), hidden, at);
    }

    private static Column Named(Tab tab, string name)
    {
        return tab.ColumnOf(name) ?? throw Refuse($"{tab.Title} has no column '{name}' to sort or filter by");
    }

    private static string Single(string key, ImmutableArray<string> values)
    {
        return values.Length == 1 ? values[0] : throw Refuse($"'{key}' is given {values.Length} times");
    }

    private static double Number(Column column, string text)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number
            : throw Refuse($"'{text}' isn't a number of {column.Title}");
    }

    private static DateTime Time(Column column, string text)
    {
        return DateTime.TryParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var time)
            ? time
            : throw Refuse($"'{text}' isn't a time of {column.Title} ({TimeFormat}, UTC)");
    }

    private static ArgumentException Refuse(string why)
    {
        return new ArgumentException($"the link isn't a view of this page: {why}");
    }
}
