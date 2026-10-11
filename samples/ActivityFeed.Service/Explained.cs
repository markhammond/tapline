using System.Text;
using ActivityFeed.Contracts;
using Chalk.Entitlements;

namespace ActivityFeed.Service;

/// <summary>
///     ChalkQL's explanation of a statement (<c>ExplainAsync</c>: what the policy folds to, nothing run), as a page shows
///     it: each table's row predicate and each column's disclosure in words a reader can follow. Its expressions name a
///     table's columns by position (<c>$2</c>), as its own columns list them (a <c>Through</c> child's, then its
///     parent's), and its operators are calls (<c>=($2, 1)</c>); here they're names, and infix (<c>site_id = 1</c>).
/// </summary>
internal static class Explained
{
    public static ExplainedStatement Of(string name, ulong plan, EntitlementsExplanation explanation)
    {
        var tables = explanation.Tables.ToDictionary(t => t.Table);
        return new ExplainedStatement
        {
            Name = name, Plan = plan,
            Tables =
            [
                .. explanation.Tables.Select(t =>
                {
                    // (its own columns; past them, a Through child's expressions read its parent's, named so)
                    var columns = t.Columns.Select(c => c.Column).ToList();
                    if (t.Parents is [var parent] && tables.TryGetValue(parent.Table, out var of))
                        columns.AddRange(of.Columns.Select(c => $"{parent.Table}.{c.Column}"));
                    return new ExplainedSource
                    {
                        Table = t.Table, Rows = Pretty(t.RowPredicate, columns),
                        Columns =
                        [
                            .. t.Columns.Select(c => new ExplainedField
                            {
                                Column = c.Column, Disclosure = Pretty(c.Disclosure, columns),
                                Placeholder = Pretty(c.Placeholder, columns), Mask = Pretty(c.Mask, columns),
                                Floor = c.MinGroupSize
                            })
                        ]
                    };
                })
            ]
        };
    }

    /// <summary>An explanation's expression, its column positions named and its calls written as operators.</summary>
    public static string Pretty(string text, IReadOnlyList<string> columns)
    {
        if (text.Length == 0) return text;
        var output = new StringBuilder();
        var at = 0;
        while (at < text.Length)
            if (CallAt(text, at) is { } call)
            {
                output.Append(Call(text, ref at, columns, call));
            }
            else if (text[at] == '$' && Number(text, at + 1) is var (n, end) && end > at + 1)
            {
                output.Append(n < columns.Count ? columns[n] : $"${n}");
                at = end;
            }
            else
            {
                output.Append(text[at++]);
            }

        return output.ToString();
    }

    private static readonly string[] Phrases = ["IS NOT NULL", "IS NULL", "IS NOT TRUE", "IS TRUE", "IS NOT FALSE", "IS FALSE"];

    /// <summary>
    ///     A call's name, if one starts here: an operator, a phrase (<c>IS NOT NULL</c>) or a word, then its open
    ///     parenthesis.
    /// </summary>
    private static string? CallAt(string text, int at)
    {
        if (at > 0 && (char.IsLetterOrDigit(text[at - 1]) || text[at - 1] == '_')) return null; // (inside a word)
        foreach (var phrase in Phrases)
            if (string.CompareOrdinal(text, at, phrase + "(", 0, phrase.Length + 1) == 0)
                return phrase;
        var end = at;
        if (end < text.Length && "=<>!".Contains(text[end]))
            while (end < text.Length && "=<>!".Contains(text[end])) end++;
        else
            while (end < text.Length && (char.IsUpper(text[end]) || text[end] == '_')) end++;
        return end > at && end < text.Length && text[end] == '(' ? text[at..end] : null;
    }

    private static string Call(string text, ref int at, IReadOnlyList<string> columns, string name)
    {
        at += name.Length + 1; // (past its open parenthesis)
        List<string> args = [];
        var depth = 0;
        var start = at;
        for (; at < text.Length; at++)
        {
            var c = text[at];
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']' && depth > 0) depth--;
            else if (c == '\'') // (a quoted literal: its parentheses are text)
            {
                at = text.IndexOf('\'', at + 1) is var close and >= 0 ? close : text.Length - 1;
            }
            else if (depth == 0 && c == ',')
            {
                args.Add(Pretty(text[start..at].Trim(), columns));
                start = at + 1;
            }
            else if (depth == 0 && c == ')')
            {
                break;
            }
        }

        if (text[start..Math.Min(at, text.Length)].Trim() is { Length: > 0 } last) args.Add(Pretty(last, columns));
        at++; // (past its close parenthesis)
        return name switch
        {
            "=" or "<>" or "<" or ">" or "<=" or ">=" or "!=" when args.Count == 2 => $"{args[0]} {name} {args[1]}",
            "OR" or "AND" => $"({string.Join($" {name} ", args)})",
            "NOT" when args.Count == 1 => $"NOT {args[0]}",
            "IS NULL" or "IS NOT NULL" or "IS TRUE" or "IS FALSE" when args.Count == 1 => $"{args[0]} {name}",
            "SEARCH" when args.Count == 2 && args[1].StartsWith("Sarg[", StringComparison.Ordinal) =>
                $"{args[0]} IN ({args[1]["Sarg[".Length..].TrimEnd(']')})",
            _ => $"{name}({string.Join(", ", args)})"
        };
    }

    private static (int Value, int End) Number(string text, int at)
    {
        var end = at;
        while (end < text.Length && char.IsDigit(text[end])) end++;
        return (end > at ? int.Parse(text.AsSpan(at, end - at)) : 0, end);
    }
}
