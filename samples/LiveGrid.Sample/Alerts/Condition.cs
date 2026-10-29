using System.Collections.Immutable;
using System.Text;

namespace LiveGrid.Sample.Alerts;

/// <summary>Where in a condition's text: from the first character to the one after the last.</summary>
public readonly record struct TextSpan(int From, int To);

/// <summary>A name the condition reads: a column of its tab, or of another tab along a path.</summary>
public sealed record ConditionName(TextSpan Span, TabPath? Path, Column Column);

/// <summary>What is wrong with a condition, and where.</summary>
public sealed record ConditionError(TextSpan Span, string Message);

/// <summary>
///     An alert's condition: a SQL boolean expression over a tab's columns, and over other tabs' columns
///     along its paths, such as <c>order.status = 'Partial' AND qty &gt;= 10</c> for a fill. The host reads
///     the names in it, so that it can say which it does not know and where, and write them as the
///     statement it runs names them; ChalkQL decides the rest.
/// </summary>
/// <remarks>
///     A condition is an expression, not a statement: no query, no second statement, no comment and no
///     parameter, so that what the browser sends can only ever be a <c>WHERE</c> clause's condition.
/// </remarks>
public sealed class Condition
{
    // Words a condition may use that name nothing: operators, literals' types, and what CASE, CAST,
    // EXTRACT and INTERVAL take.
    private static readonly HashSet<string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        "AND", "OR", "NOT", "IN", "IS", "NULL", "LIKE", "ESCAPE", "BETWEEN", "TRUE", "FALSE", "CASE", "WHEN", "THEN",
        "ELSE", "END", "CAST", "AS", "INTERVAL", "TIMESTAMP", "DATE", "TIME", "DISTINCT", "FROM", "SIMILAR", "TO",
        "SECOND", "MINUTE", "HOUR", "DAY", "MONTH", "YEAR", "CURRENT_TIMESTAMP", "CURRENT_DATE", "UNKNOWN"
    };

    // Words that begin, or belong to, a query or a statement rather than a condition.
    private static readonly HashSet<string> Refused = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "WITH", "VALUES", "TABLE", "UNION", "INTERSECT", "EXCEPT", "WHERE", "GROUP", "HAVING", "ORDER",
        "LIMIT", "OFFSET", "FETCH", "JOIN", "LATERAL", "INSERT", "UPDATE", "DELETE", "MERGE", "CREATE", "DROP",
        "ALTER", "EXISTS"
    };

    private Condition(Tab tab, string text, ImmutableArray<ConditionName> names, ImmutableArray<ConditionError> errors)
    {
        Tab = tab;
        Text = text;
        Names = names;
        Errors = errors;
    }

    public Tab Tab { get; }

    /// <summary>The condition as written.</summary>
    public string Text { get; }

    /// <summary>The names it reads, in order.</summary>
    public ImmutableArray<ConditionName> Names { get; }

    /// <summary>What is wrong with it; none, if the host can hand it to ChalkQL.</summary>
    public ImmutableArray<ConditionError> Errors { get; }

    /// <summary>Nothing written: every row passes.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);

    /// <summary>The paths it reads along, each once.</summary>
    public IEnumerable<TabPath> Paths => Names.Select(n => n.Path).OfType<TabPath>().Distinct();

    public static Condition Parse(Tab tab, string text)
    {
        var tokens = Tokens(text, out var errors);
        var names = ImmutableArray.CreateBuilder<ConditionName>();
        var depth = 0;
        for (var t = 0; t < tokens.Count; t++)
        {
            var token = tokens[t];
            if (token.Kind == TokenKind.Punct)
            {
                depth += token.Text == "(" ? 1 : token.Text == ")" ? -1 : 0;
                if (depth < 0)
                {
                    errors.Add(new ConditionError(token.Span, "a ')' with no '(' before it"));
                    depth = 0;
                }

                continue;
            }

            if (token.Kind is not (TokenKind.Word or TokenKind.Quoted)) continue;

            var next = t + 1 < tokens.Count ? tokens[t + 1] : default;
            var before = t > 0 ? tokens[t - 1] : default;

            // A function, a type after AS, or a word of the language names nothing.
            if ((token.Kind == TokenKind.Word && next.Text == "(" && !Words.Contains(token.Text) &&
                 !Refused.Contains(token.Text))
                || (token.Kind == TokenKind.Word && before.Kind == TokenKind.Word &&
                    before.Text.Equals("AS", StringComparison.OrdinalIgnoreCase)))
                continue;

            if (next.Text == "." && t + 2 < tokens.Count && tokens[t + 2].Kind is TokenKind.Word or TokenKind.Quoted)
            {
                var member = tokens[t + 2];
                var span = new TextSpan(token.Span.From, member.Span.To);
                var path = tab.Paths.FirstOrDefault(p => Same(p.Name, token));
                if (path is null)
                    errors.Add(new ConditionError(token.Span,
                        $"{tab.Title} has no path '{token.Text}'{Offer(tab.Paths.Select(p => p.Name + "."))}"));
                else if (Resolve(path.Target, member, out var error) is { } column)
                    names.Add(new ConditionName(span, path, column));
                else
                    errors.Add(new ConditionError(member.Span, error!));

                t += 2;
                continue;
            }

            if (token.Kind == TokenKind.Word && Refused.Contains(token.Text))
            {
                errors.Add(new ConditionError(token.Span,
                    $"a condition is an expression: '{token.Text.ToUpperInvariant()}' has no place in one"));
                continue;
            }

            if (token.Kind == TokenKind.Word && Words.Contains(token.Text) &&
                tab.Columns.All(c => !Same(c.Name, token))) continue;

            if (Resolve(tab, token, out var unknown) is { } own)
                names.Add(new ConditionName(token.Span, null, own));
            else
                errors.Add(new ConditionError(token.Span, unknown!));
        }

        if (depth > 0)
            errors.Add(new ConditionError(new TextSpan(text.Length, text.Length),
                depth == 1 ? "a '(' is not closed" : $"{depth} '(' are not closed"));

        return new Condition(tab, text, names.ToImmutable(), [.. errors]);
    }

    /// <summary>
    ///     The condition as the statement that runs it names things: each of the tab's columns as
    ///     <paramref name="own" /> writes it, each along a path as <paramref name="along" /> does.
    /// </summary>
    public string Sql(Func<Column, string> own, Func<TabPath, Column, string> along)
    {
        if (!Errors.IsEmpty) throw new InvalidOperationException($"the condition cannot be run: {Errors[0].Message}");

        if (IsEmpty) return "TRUE";

        var sql = new StringBuilder();
        var at = 0;
        foreach (var name in Names)
        {
            sql.Append(Text, at, name.Span.From - at);
            sql.Append(name.Path is null ? own(name.Column) : along(name.Path, name.Column));
            at = name.Span.To;
        }

        return sql.Append(Text, at, Text.Length - at).ToString();
    }

    public override string ToString() => Text;

    private static bool Same(string name, Token token) => token.Kind == TokenKind.Quoted
            ? name == token.Text
            : name.Equals(token.Text, StringComparison.OrdinalIgnoreCase);

    private static Column? Resolve(Tab tab, Token token, out string? error)
    {
        var column = tab.Columns.FirstOrDefault(c => Same(c.Name, token));
        error = column switch
        {
            null => $"{tab.Title} has no column '{token.Text}'",
            { Derived: true } => $"{column.Title} is worked out and shown, not a column a condition can read",
            _ => null
        };
        return error is null ? column : null;
    }

    private static string Offer(IEnumerable<string> names) => names.ToList() is [_, ..] some ? "; it has " + string.Join(", ", some) : "";

    /// <summary>The condition's tokens, and what cannot be one.</summary>
    internal static List<Token> Tokens(string text, out List<ConditionError> errors)
    {
        errors = [];
        var tokens = new List<Token>();
        var at = 0;
        while (at < text.Length)
        {
            var c = text[at];
            var start = at;
            if (char.IsWhiteSpace(c))
            {
                at++;
            }
            else if (c is '\'' or '"')
            {
                at++;
                var value = new StringBuilder();
                var closed = false;
                while (at < text.Length)
                    if (text[at] == c && at + 1 < text.Length && text[at + 1] == c)
                    {
                        value.Append(c);
                        at += 2;
                    }
                    else if (text[at] == c)
                    {
                        at++;
                        closed = true;
                        break;
                    }
                    else
                    {
                        value.Append(text[at++]);
                    }

                if (!closed)
                    errors.Add(new ConditionError(new TextSpan(start, at),
                        c == '\'' ? "this text has no closing quote" : "this name has no closing quote"));

                tokens.Add(new Token(c == '\'' ? TokenKind.Text : TokenKind.Quoted, value.ToString(),
                    new TextSpan(start, at)) { Open = !closed });
            }
            else if (char.IsLetter(c) || c == '_')
            {
                while (at < text.Length && (char.IsLetterOrDigit(text[at]) || text[at] == '_')) at++;

                tokens.Add(new Token(TokenKind.Word, text[start..at], new TextSpan(start, at)));
            }
            else if (char.IsDigit(c) || (c == '.' && at + 1 < text.Length && char.IsDigit(text[at + 1])))
            {
                while (at < text.Length && (char.IsDigit(text[at]) || text[at] == '.'
                                                                   || (text[at] is 'e' or 'E' && at + 1 < text.Length &&
                                                                       (char.IsDigit(text[at + 1]) ||
                                                                        text[at + 1] is '+' or '-'))
                                                                   || (text[at] is '+' or '-' &&
                                                                       text[at - 1] is 'e' or 'E')))
                    at++;

                tokens.Add(new Token(TokenKind.Number, text[start..at], new TextSpan(start, at)));
            }
            else if (c == ';')
            {
                errors.Add(new ConditionError(new TextSpan(at, at + 1), "one condition, no ';'"));
                at++;
            }
            else if ((c == '-' && at + 1 < text.Length && text[at + 1] == '-') ||
                     (c == '/' && at + 1 < text.Length && text[at + 1] == '*'))
            {
                errors.Add(new ConditionError(new TextSpan(at, text.Length), "a condition takes no comments"));
                at = text.Length;
            }
            else if (c is '@' or '?' or '$' or ':')
            {
                errors.Add(new ConditionError(new TextSpan(at, at + 1),
                    "a condition takes no parameters: write the value"));
                at++;
            }
            else
            {
                // Operators of one or two characters, and the rest of the punctuation.
                var two = at + 1 < text.Length ? text.Substring(at, 2) : "";
                var length = two is "<=" or ">=" or "<>" or "!=" or "||" ? 2 : 1;
                tokens.Add(new Token(TokenKind.Punct, text.Substring(at, length), new TextSpan(at, at + length)));
                at += length;
            }
        }

        return tokens;
    }

    internal enum TokenKind
    {
        None,
        Word,
        Quoted,
        Text,
        Number,
        Punct
    }

    internal readonly record struct Token(TokenKind Kind, string Text, TextSpan Span)
    {
        /// <summary>A text literal the condition ends inside: its closing quote not yet written.</summary>
        public bool Open { get; init; }
    }
}