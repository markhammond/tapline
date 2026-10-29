using System.Globalization;

namespace LiveGrid.Sample.Alerts;

/// <summary>One suggestion: what the list shows, what it inserts, and what it is.</summary>
/// <param name="Type">
///     How the editor marks it: <c>property</c> a column, <c>namespace</c> a path, <c>keyword</c>,
///     <c>text</c> a value.
/// </param>
public sealed record Suggestion(
    string Label,
    string Type,
    string? Detail = null,
    string? Info = null,
    string? Apply = null,
    int Boost = 0);

/// <summary>What fits at the cursor, and where it starts.</summary>
public sealed record Suggestions(int From, IReadOnlyList<Suggestion> Options)
{
    public static readonly Suggestions None = new(0, []);
}

/// <summary>
///     What may come next in a condition, at the cursor: a column or a path where a comparison starts,
///     the column of a path after its dot, an operator after a name, a value after an operator (a
///     choice's own, or a text column's most frequent, which the host looks up), and AND or OR after a
///     comparison. It reads the tokens before the cursor, not a parse, so it answers for half-written
///     conditions too.
/// </summary>
public static class ConditionCompletion
{
    /// <param name="values">A text column's values starting with what is typed, the most frequent first.</param>
    public static async Task<Suggestions> CompleteAsync(
        Tab tab, string text, int cursor, Func<Column, string, CancellationToken, Task<IReadOnlyList<string>>> values,
        CancellationToken ct)
    {
        cursor = Math.Clamp(cursor, 0, text.Length);
        var tokens = Condition.Tokens(text[..cursor], out _);
        var last = tokens.Count > 0 ? tokens[^1] : default;

        // Inside a text literal: the values of the column it is compared with.
        if (last is { Kind: Condition.TokenKind.Text, Open: true })
        {
            var column = Compared(tab, tokens, tokens.Count - 1);
            if (column is null) return Suggestions.None;

            var closing = cursor < text.Length && text[cursor] == '\'' ? "" : "'";
            var found = column.Kind == ColumnKind.Choice
                ? column.Choices.Where(c => c.StartsWith(last.Text, StringComparison.OrdinalIgnoreCase)).ToList()
                : column.Kind == ColumnKind.Text
                    ? await values(column, last.Text, ct).ConfigureAwait(false)
                    : [];
            return new Suggestions(last.Span.From + 1,
            [
                .. found.Select(v => new Suggestion(v, "text", column.Title,
                    Apply: v.Replace("'", "''", StringComparison.Ordinal) + closing))
            ]);
        }

        // The word being typed, if the cursor is at its end.
        var typing = last.Kind is Condition.TokenKind.Word && last.Span.To == cursor;
        var from = typing ? last.Span.From : cursor;
        var prefix = typing ? last.Text : "";
        var before = tokens.Count - (typing ? 2 : 1);

        // After a path's dot: the columns along it.
        if (before >= 1 && tokens[before].Text == "." && tokens[before - 1].Kind == Condition.TokenKind.Word)
        {
            var path = tab.Paths.FirstOrDefault(p =>
                p.Name.Equals(tokens[before - 1].Text, StringComparison.OrdinalIgnoreCase));
            return path is null
                ? Suggestions.None
                : new Suggestions(from,
                    Filter(prefix, path.Target.Columns.Where(c => !c.Derived).Select(c => Of(c, path))));
        }

        var previous = before >= 0 ? tokens[before] : default;
        var options = new List<Suggestion>();
        if (Named(tab, tokens, before) is { } named)
        {
            // After a name: how it may be compared.
            options.AddRange(Operators(named));
        }
        else if (previous.Text == "," || (previous.Text == "(" && before >= 1 && IsWord(tokens[before - 1], "IN")))
        {
            // In a list: more values to compare with, if it is a text column's.
            if (Compared(tab, tokens, before + 1) is { Kind: ColumnKind.Choice or ColumnKind.Text } listed)
                options.AddRange(await ValuesAsync(listed, prefix, values, ct).ConfigureAwait(false));
        }
        else if (previous.Kind is Condition.TokenKind.None || previous.Text == "(" ||
                 IsWord(previous, "AND", "OR", "NOT", "WHEN", "THEN", "ELSE"))
        {
            // Where a comparison starts: the tab's columns, its paths, and NOT.
            options.AddRange(tab.Columns.Where(c => !c.Derived).Select(c => Of(c, null)));
            options.AddRange(tab.Paths.Select(p => new Suggestion(p.Name, "namespace",
                $"to {p.Target.Title.ToLowerInvariant()}", p.Description, p.Name + ".")));
            options.Add(new Suggestion("NOT", "keyword"));
        }
        else if (previous.Kind is Condition.TokenKind.Text or Condition.TokenKind.Number || previous.Text == ")"
                 || IsWord(previous, "NULL", "TRUE", "FALSE"))
        {
            // After a comparison: another.
            options.Add(new Suggestion("AND", "keyword", Apply: "AND "));
            options.Add(new Suggestion("OR", "keyword", Apply: "OR "));
        }
        else if (previous.Kind == Condition.TokenKind.Punct &&
                 previous.Text is "=" or "<>" or "!=" or "<" or "<=" or ">" or ">=")
        {
            // After an operator: values to compare with, quoted, if the column is text; no quote to type first.
            if (Compared(tab, tokens, before + 1) is { Kind: ColumnKind.Choice or ColumnKind.Text } compared)
            {
                options.AddRange(await ValuesAsync(compared, prefix, values, ct).ConfigureAwait(false));
            }
            else if (Compared(tab, tokens, before + 1) is { Kind: ColumnKind.Time } time)
            {
                var now = DateTime.UtcNow;
                options.Add(new Suggestion(
                    string.Create(CultureInfo.InvariantCulture, $"TIMESTAMP '{now:yyyy-MM-dd HH:mm:ss}'"), "text",
                    time.Title + ", UTC", "A time, to the microsecond if you need"));
            }
        }

        return new Suggestions(from, Filter(prefix, options));
    }

    /// <summary>
    ///     A text column's values, quoted as they are inserted: a choice's own, or the most frequent starting with
    ///     <paramref name="typed" />.
    /// </summary>
    private static async Task<IEnumerable<Suggestion>> ValuesAsync(
        Column column, string typed, Func<Column, string, CancellationToken, Task<IReadOnlyList<string>>> values,
        CancellationToken ct)
    {
        var found = column.Kind == ColumnKind.Choice
            ? column.Choices
            : await values(column, typed, ct).ConfigureAwait(false);
        return found.Select(v => new Suggestion(v, "text", column.Title, Apply: Column.Quote(v)));
    }

    private static Suggestion Of(Column column, TabPath? path) => new Suggestion(
            column.Name, "property",
            column.Kind switch
            {
                ColumnKind.Id => "id",
                ColumnKind.Integer or ColumnKind.Number => "number",
                ColumnKind.Time => "time, UTC",
                ColumnKind.Choice => string.Join(", ", column.Choices),
                _ => "text"
            },
            (path is null ? "" : path.Target.Title + ": ") + column.Title.Replace(' ', ' ').Replace(' ', ' ')
                                                           + (column.Description is { } about ? ". " + about : ""),
            Boost: path is null ? 1 : 0);

    private static IEnumerable<Suggestion> Operators(Column column)
    {
        yield return new Suggestion("=", "keyword", Apply: "= ");
        yield return new Suggestion("<>", "keyword", Apply: "<> ");
        switch (column.Kind)
        {
            case ColumnKind.Integer or ColumnKind.Number or ColumnKind.Time:
                yield return new Suggestion(">=", "keyword", Apply: ">= ");
                yield return new Suggestion("<=", "keyword", Apply: "<= ");
                yield return new Suggestion(">", "keyword", Apply: "> ");
                yield return new Suggestion("<", "keyword", Apply: "< ");
                yield return new Suggestion("BETWEEN", "keyword", "… AND …", Apply: "BETWEEN ");
                break;
            case ColumnKind.Text:
                yield return new Suggestion("IN", "keyword", "(…, …)", Apply: "IN (");
                yield return new Suggestion("LIKE", "keyword", "'start%'", Apply: "LIKE '");
                break;
            default:
                yield return new Suggestion("IN", "keyword", "(…, …)", Apply: "IN (");
                break;
        }

        yield return new Suggestion("IS NULL", "keyword", "along a path, nothing yet");
    }

    private static List<Suggestion> Filter(string prefix, IEnumerable<Suggestion> options) => [.. options.Where(o => o.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))];

    private static bool IsWord(Condition.Token token, params string[] words) => token.Kind == Condition.TokenKind.Word &&
               words.Any(w => w.Equals(token.Text, StringComparison.OrdinalIgnoreCase));

    /// <summary>The column the token at <paramref name="at" /> names, alone or at a path's end.</summary>
    private static Column? Named(Tab tab, List<Condition.Token> tokens, int at)
    {
        if (at < 0 || tokens[at].Kind is not (Condition.TokenKind.Word or Condition.TokenKind.Quoted)) return null;

        var name = tokens[at].Text;
        if (at >= 2 && tokens[at - 1].Text == "." &&
            tab.Paths.FirstOrDefault(p => p.Name.Equals(tokens[at - 2].Text, StringComparison.OrdinalIgnoreCase)) is
                { } path)
            return path.Target.Columns.FirstOrDefault(c =>
                !c.Derived && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        return tab.Columns.FirstOrDefault(c => !c.Derived && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     The column a value at <paramref name="at" /> is compared with: before an operator, or before
    ///     <c>IN (</c> and the values already in its list.
    /// </summary>
    private static Column? Compared(Tab tab, List<Condition.Token> tokens, int at)
    {
        var t = at - 1;
        while (t >= 0 && (tokens[t].Kind == Condition.TokenKind.Text || tokens[t].Text == ",")) t--;

        if (t >= 1 && tokens[t].Text == "(" && IsWord(tokens[t - 1], "IN")) return Named(tab, tokens, t - 2);

        if (t >= 0 && ((tokens[t].Kind == Condition.TokenKind.Punct &&
                        tokens[t].Text is "=" or "<>" or "!=" or "<" or "<=" or ">" or ">=") ||
                       IsWord(tokens[t], "LIKE"))) return Named(tab, tokens, t - 1);

        return null;
    }
}