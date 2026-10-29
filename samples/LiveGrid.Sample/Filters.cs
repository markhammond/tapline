using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Tapline.Grid.ChalkQL;

namespace LiveGrid.Sample;

/// <summary>How a text column's values are matched.</summary>
public enum TextMatch
{
    /// <summary>The value is one of those given.</summary>
    AnyOf,

    /// <summary>The value starts with one of those given.</summary>
    StartsWith
}

/// <summary>
///     One column's filter, as the blotter's: values to match for ids, text and choices, a range for numbers and times
///     (from at or after, to before). An empty filter filters nothing.
/// </summary>
public sealed record ColumnFilter
{
    public required string Column { get; init; }

    /// <summary>The column's kind (its catalog's): how it's matched, and written in SQL.</summary>
    public required ColumnKind Kind { get; init; }

    /// <summary>The column's heading, as the filter's pill names it.</summary>
    public required string Label { get; init; }

    /// <summary>For ids, text and choices: the values, or the starts of values, to match.</summary>
    public ImmutableArray<string> Values { get; init; } = [];

    public TextMatch Match { get; init; } = TextMatch.AnyOf;

    /// <summary>For numbers: at least, and at most.</summary>
    public double? Min { get; init; }

    public double? Max { get; init; }

    /// <summary>For times (UTC): at or after, and before.</summary>
    public DateTime? From { get; init; }

    public DateTime? To { get; init; }

    public bool IsEmpty => Values.IsEmpty && Min is null && Max is null && From is null && To is null;

    /// <summary>Canonical: its values distinct and in order, its times in UTC to the microsecond.</summary>
    public ColumnFilter Canonical()
    {
        return this with
        {
            Values = [.. Values.Select(v => v.Trim()).Where(v => v.Length > 0).Distinct().Order(StringComparer.Ordinal)],
            From = From is { } from ? Microsecond(from) : null, To = To is { } to ? Microsecond(to) : null
        };
    }

    /// <summary>
    ///     Its conditions as SQL over its column, into <paramref name="where" />, their values into <paramref name="values" />
    ///     (named <c>f0</c>, <c>f1</c>, …): a list of values or of starts, a range of numbers, a span of times (UTC).
    /// </summary>
    public void Sql(List<string> where, Dictionary<string, object?> values)
    {
        var kind = Kind;
        if (!Values.IsEmpty)
        {
            if (kind == ColumnKind.Id)
            {
                where.Add($"{Column} IN @{Bind(values, Values.Select(v => Guid.TryParse(v, out var id) ? id : Guid.Empty).ToArray())}");
            }
            else if (Match == TextMatch.StartsWith && kind == ColumnKind.Text)
            {
                var starts = Values.Select(v => $"{Column} LIKE @{Bind(values, Escape(v) + "%")} ESCAPE '\\'");
                where.Add($"({string.Join(" OR ", starts)})");
            }
            else
            {
                where.Add($"{Column} IN @{Bind(values, Values.ToArray())}");
            }
        }

        if (kind is ColumnKind.Number or ColumnKind.Integer)
        {
            if (Min is { } min) where.Add($"{Column} >= @{Bind(values, kind == ColumnKind.Integer ? (long)Math.Ceiling(min) : min)}");
            if (Max is { } max) where.Add($"{Column} <= @{Bind(values, kind == ColumnKind.Integer ? (long)Math.Floor(max) : max)}");
        }

        if (kind != ColumnKind.Time) return;
        // (the column a TIMESTAMP, as the database holds a time: the UTC wall clock, bound as one)
        if (From is { } from) where.Add($"{Column} >= @{Bind(values, DateTime.SpecifyKind(from, DateTimeKind.Unspecified))}");
        if (To is { } to) where.Add($"{Column} < @{Bind(values, DateTime.SpecifyKind(to, DateTimeKind.Unspecified))}");

        static string Bind(Dictionary<string, object?> values, object value)
        {
            var name = $"f{values.Count}";
            values[name] = value;
            return name;
        }

        static string Escape(string start)
        {
            return start.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        }
    }

    /// <summary>Its conditions as an alert's condition writes them, into <paramref name="conditions" />: the values written in.</summary>
    public void Condition(List<string> conditions)
    {
        var kind = Kind;
        if (!Values.IsEmpty)
        {
            if (Match == TextMatch.StartsWith && kind == ColumnKind.Text)
            {
                var starts = Values.Select(v => Escape(v) is var escaped && escaped != v
                    ? $"{Column} LIKE {Quote(escaped + "%")} ESCAPE '\\'"
                    : $"{Column} LIKE {Quote(v + "%")}");
                conditions.Add($"({string.Join(" OR ", starts)})");
            }
            else
            {
                var values = Values.Select(v => kind == ColumnKind.Id
                    ? $"CAST({Quote((Guid.TryParse(v, out var id) ? id : Guid.Empty).ToString())} AS UUID)"
                    : Quote(v)).ToList();
                conditions.Add(values.Count == 1 ? $"{Column} = {values[0]}" : $"{Column} IN ({string.Join(", ", values)})");
            }
        }

        if (kind is ColumnKind.Number or ColumnKind.Integer)
        {
            if (Min is { } min) conditions.Add($"{Column} >= {Number(kind == ColumnKind.Integer ? Math.Ceiling(min) : min)}");
            if (Max is { } max) conditions.Add($"{Column} <= {Number(kind == ColumnKind.Integer ? Math.Floor(max) : max)}");
        }

        if (kind != ColumnKind.Time) return;
        if (From is { } from) conditions.Add($"{Column} >= {Time(from)}");
        if (To is { } to) conditions.Add($"{Column} < {Time(to)}");

        static string Quote(string text)
        {
            return "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
        }

        static string Escape(string start)
        {
            return start.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        }

        static string Number(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        static string Time(DateTime time)
        {
            return string.Create(CultureInfo.InvariantCulture, $"TIMESTAMP '{time:yyyy-MM-dd HH:mm:ss.ffffff}'");
        }
    }

    /// <summary>What its pill says, as the blotter's do: a column's values (three, and how many more), its range.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (!Values.IsEmpty)
        {
            var shown = string.Join(", ", Values.Take(3)) + (Values.Length > 3 ? $" +{Values.Length - 3}" : "");
            parts.Add(Match == TextMatch.StartsWith ? $"starts {shown}" : shown);
        }

        if (Min is { } min) parts.Add("≥ " + min.ToString("#,0.####", CultureInfo.InvariantCulture));
        if (Max is { } max) parts.Add("≤ " + max.ToString("#,0.####", CultureInfo.InvariantCulture));
        if (From is { } from) parts.Add("from " + from.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture));
        if (To is { } to) parts.Add("to " + to.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture));
        return $"{Label}: {string.Join(" ", parts)}";
    }

    /// <summary>The same filter, written out: its column, values and bounds.</summary>
    public override string ToString()
    {
        var name = new StringBuilder(Column);
        if (!Values.IsEmpty) name.Append(Match == TextMatch.StartsWith ? " starts " : " in ").AppendJoin('|', Values);
        if (Min is { } min) name.Append(" >= ").Append(min.ToString("R", CultureInfo.InvariantCulture));
        if (Max is { } max) name.Append(" <= ").Append(max.ToString("R", CultureInfo.InvariantCulture));
        if (From is { } from) name.Append(" from ").Append(from.ToString("O", CultureInfo.InvariantCulture));
        if (To is { } to) name.Append(" to ").Append(to.ToString("O", CultureInfo.InvariantCulture));
        return name.ToString();
    }

    /// <summary>Equal by what it says: its values compared in order (a record would compare the array they're in).</summary>
    public bool Equals(ColumnFilter? other)
    {
        return other is not null && Column == other.Column && Match == other.Match && Min == other.Min && Max == other.Max &&
               From == other.From && To == other.To && Values.AsSpan().SequenceEqual(other.Values.AsSpan());
    }

    public override int GetHashCode()
    {
        return ToString().GetHashCode(StringComparison.Ordinal);
    }

    private static DateTime Microsecond(DateTime time)
    {
        var utc = DateTime.SpecifyKind(time, DateTimeKind.Utc);
        return utc.AddTicks(-(utc.Ticks % 10));
    }

}

/// <summary>
///     A view's filter, the blotter's way: a filter on any of its columns, all of which must hold — the sources' own
///     model, passed through the grid (as SQL, <see cref="Sql" />, or tested in process). Equal when it says the same, so
///     setting an equal one again reads nothing afresh.
/// </summary>
public sealed class ViewFilter
{
    private ViewFilter(ImmutableArray<ColumnFilter> columns)
    {
        Columns = columns;
        Name = string.Join("; ", columns.Select(c => c.ToString()));
    }

    /// <summary>The columns filtered, by name.</summary>
    public ImmutableArray<ColumnFilter> Columns { get; }

    /// <summary>Canonical: the same for filters that say the same.</summary>
    public string Name { get; }

    /// <summary>The filter of these, each canonical, the empty ones left out: none if none's left.</summary>
    public static ViewFilter? Of(IEnumerable<ColumnFilter> filters)
    {
        ImmutableArray<ColumnFilter> columns =
            [.. filters.Select(f => f.Canonical()).Where(f => !f.IsEmpty).OrderBy(f => f.Column, StringComparer.Ordinal)];
        return columns.IsEmpty ? null : new ViewFilter(columns);
    }

    public ColumnFilter? For(string column)
    {
        return Columns.FirstOrDefault(c => c.Column == column);
    }

    /// <summary>This filter with <paramref name="filter" /> in place of its column's (or without it, empty): none if none's left.</summary>
    public ViewFilter? With(ColumnFilter filter)
    {
        return Of([.. Columns.Where(c => c.Column != filter.Column), filter]);
    }

    public ViewFilter? Without(string column)
    {
        return Of(Columns.Where(c => c.Column != column));
    }

    /// <summary>
    ///     The filter as SQL over the blotter's columns (<see cref="ChalkFilter" />): what the database's pages and the grid's
    ///     tail both run, its values bound — each column's conditions, all of which must hold.
    /// </summary>
    public ChalkFilter Sql => _sql ??= ToSql();

    private ChalkFilter? _sql;

    /// <summary>The same conditions as an alert's condition writes them (the blotter's): in SQL, the values written in.</summary>
    public string Condition()
    {
        List<string> conditions = [];
        foreach (var column in Columns) column.Condition(conditions);
        return string.Join(" AND ", conditions);
    }

    private ChalkFilter ToSql()
    {
        List<string> where = [];
        Dictionary<string, object?> values = [];
        foreach (var column in Columns) column.Sql(where, values);
        return new ChalkFilter(where.Count == 0 ? "TRUE" : string.Join(" AND ", where), values);
    }

    public override bool Equals(object? obj)
    {
        return obj is ViewFilter other && Name == other.Name;
    }

    public override int GetHashCode()
    {
        return Name.GetHashCode(StringComparison.Ordinal);
    }

    public override string ToString()
    {
        return string.Join(" · ", Columns.Select(c => c.Describe()));
    }
}
