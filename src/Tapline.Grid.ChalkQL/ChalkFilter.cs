using Tapline.Grid.Provider;

namespace Tapline.Grid.ChalkQL;

/// <summary>
///     A view's filter, stated once in SQL (grid-sources.md §7 (b)): a condition over the row's columns, as a <c>WHERE</c>
///     clause writes it, its values bound as parameters and never written into the text, so one shape of filter is one
///     prepared statement whatever its values. The store's pages take it pushed down, and the tail's standing views the
///     same text: a row passes as the store would pass it. Its parameters' names don't start <c>grid_</c>.
/// </summary>
/// <param name="Where">The condition, <c>TRUE</c> for every row.</param>
/// <param name="Parameters">The values it names, by name without the <c>@</c>.</param>
public sealed record ChalkFilter(string Where, IReadOnlyDictionary<string, object?>? Parameters = null)
{
    /// <summary>No filter: every row.</summary>
    public static ChalkFilter All { get; } = new("TRUE");

    /// <summary>A view's filter: none, a <see cref="ChalkFilter" />, else refused.</summary>
    internal static ChalkFilter Of(GridView view)
    {
        var filter = view.Filter switch
        {
            null => All,
            ChalkFilter chalk => chalk,
            var other => throw new GridRefusedException($"A view read through ChalkQL is filtered by a ChalkFilter, not a {other.GetType().Name}.")
        };
        if (filter.Parameters?.Keys.FirstOrDefault(k => k.StartsWith("grid_", StringComparison.OrdinalIgnoreCase)) is { } taken)
            throw new GridRefusedException($"The filter's parameter {taken} is named as the grid's own are (grid_…).");
        return filter;
    }

    /// <summary>
    ///     Equal when it says the same: its text, and each value (a list's, in order) — so grids filtering alike share a
    ///     view, and its scope, however each made its filter.
    /// </summary>
    public bool Equals(ChalkFilter? other)
    {
        if (other is null || !string.Equals(Where, other.Where, StringComparison.Ordinal)) return false;
        if ((Parameters?.Count ?? 0) != (other.Parameters?.Count ?? 0)) return false;
        foreach (var (name, value) in Parameters ?? new Dictionary<string, object?>())
            if (!other.Parameters!.TryGetValue(name, out var theirs) || !Same(value, theirs))
                return false;
        return true;

        static bool Same(object? a, object? b)
        {
            if (a is System.Collections.IEnumerable x and not string && b is System.Collections.IEnumerable y and not string)
                return x.Cast<object?>().SequenceEqual(y.Cast<object?>());
            return Equals(a, b);
        }
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Where.GetHashCode(StringComparison.Ordinal), Parameters?.Count ?? 0);
    }

    /// <summary>Its values, and room for a statement's own.</summary>
    internal Dictionary<string, object?> Values()
    {
        return Parameters is null ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(Parameters, StringComparer.OrdinalIgnoreCase);
    }
}
