using System.Globalization;
using System.Reflection;
using System.Text;
using PivotTable.Contracts;
using PowerArgs;

namespace PivotTable.Headless;

/// <summary>
///     The command line, declared: an option a property — its name, what it's for, its default and its checks — parsed by
///     PowerArgs (<c>--name value</c>, <c>-name value</c> or <c>/name:value</c>), its help written from the same
///     declarations (<see cref="Usage" />). With none given: a backend of its own, spawned (<see cref="Backend" />), and
///     the
///     pivot of <see cref="DefaultAddress" /> streamed from it.
/// </summary>
internal sealed class Options
{
    public const string DefaultAddress = "?rows=instrument&columns=leg";

    [ArgShortcut("h")]
    [ArgShortcut("?")]
    [ArgDescription("This help.")]
    public bool Help { get; set; }

    [ArgShortcut(ArgShortcutPolicy.NoShortcut)]
    [ArgDefaultValue(DefaultAddress)]
    [Value("<address>")]
    [ArgDescription("The pivot a page's address asks for: paste one from the browser (its query is enough).")]
    public string Address { get; set; } = DefaultAddress;

    [ArgShortcut(ArgShortcutPolicy.NoShortcut)]
    [ArgExistingFile]
    [Value("<file>")]
    [ArgDescription(
        "A statement of your own instead, from a file: ChalkQL's dialect, answering what a pivot is (the sample's README).")]
    public string? Statement { get; set; }

    [ArgShortcut(ArgShortcutPolicy.NoShortcut)]
    [Value("<name=value,…>")]
    [ArgDescription(
        "The lists a --statement binds: a name and a value each, comma-separated — a name again for each of its values (client=Alder,client=Birch).")]
    public string[]? Bind { get; set; }

    [ArgShortcut(ArgShortcutPolicy.NoShortcut)]
    [ArgDescription("The shape printed whole (JSON) before anything streams.")]
    public bool Describe { get; set; }

    [ArgShortcut(ArgShortcutPolicy.NoShortcut)]
    [ArgDescription("The statement printed.")]
    public bool Sql { get; set; }

    [ArgShortcut(ArgShortcutPolicy.NoShortcut)]
    [ArgDescription("Its view recomputed each update, not maintained incrementally.")]
    public bool Recompute { get; set; }

    [ArgShortcut(ArgShortcutPolicy.NoShortcut)]
    [ArgDefaultValue(8)]
    [ArgRange(0, 10_000)]
    [Value("<n>")]
    [ArgDescription("The cells shown of each batch.")]
    public int Cells { get; set; } = 8;

    [ArgShortcut(ArgShortcutPolicy.NoShortcut)]
    [ArgDescription("Summaries only: no cells.")]
    public bool Quiet { get; set; }

    [ArgShortcut(ArgShortcutPolicy.NoShortcut)]
    [Value("<entry,…>")]
    [ArgDescription(
        "What the cells carry besides, as a page's Show as asks — entries as its address has them, computed by the provider: volume.column, volume.row, volume.total (% of the market's column, row or grand total), volume.columnShown, … (of what's shown), volume.running (a running total in Date: Date in the address's rows or columns).")]
    public string[]? Show { get; set; }

    [ArgShortcut(ArgShortcutPolicy.NoShortcut)]
    [Value("<duration>")]
    [ArgDescription(
        "Runs that long (60s, 2m), then exits 0 if every record checked out and nothing failed — no resync, no error, no handler fault — else 1.")]
    public Duration? Verify { get; set; }

    [ArgShortcut("bwlimit")]
    [Value("<rate>")]
    [ArgDescription(
        "The session's stream capped, as rsync's --bwlimit (64: 64 KiB/s; 0.5; 500kb): a slow consumer, its cells conflated.")]
    public string? BandwidthLimit { get; set; }

    [ArgShortcut("drop-every")]
    [Value("<duration>")]
    [ArgDescription(
        "The call dropped that often, as a network might: the session resumed from the last frame received, no snapshot.")]
    public Duration? DropEvery { get; set; }

    [ArgShortcut(ArgShortcutPolicy.NoShortcut)]
    [Value("<url,…>")]
    [ArgDescription(
        "The routers' streams, in order: streamed from the first, moved to the next as it drains (make before break). None: a backend of its own, spawned.")]
    public string[]? Stream { get; set; }

    [ArgShortcut(ArgShortcutPolicy.NoShortcut)]
    [Value("<url,…>")]
    [ArgDescription(
        "Each router's out-of-band fetches, in its order (default: its stream's host, two ports below — the sample's app port).")]
    public string[]? Blobs { get; set; }

    [ArgShortcut(ArgShortcutPolicy.NoShortcut)]
    [Value("<duration>")]
    [ArgDescription(
        "That long in, the first router drained (its ops endpoints' POST): verifying, the move must be made and nothing lost.")]
    public Duration? Drain { get; set; }

    [ArgShortcut(ArgShortcutPolicy.NoShortcut)]
    [Value("<url>")]
    [ArgDescription(
        "The first router's ops endpoints, for --drain (default: its stream's host, one port below — the sample's ops port).")]
    public string? Ops { get; set; }

    [ArgShortcut(ArgShortcutPolicy.NoShortcut)]
    [Value("<trades a day>")]
    [ArgDescription(
        "Five seconds in, the brokerage's pace set (as a page's ?trades=: the history primed again, for every pivot), and its token waited for: the change in front of this client, timed. Verifying: within 30 s, or it fails.")]
    public double? Pace { get; set; }

    [ArgShortcut("min-interval")]
    [Value("<duration>")]
    [ArgDescription(
        "The subscription's own rate: at most one state that often (250ms), the rest conflated into what stands at its end — a token it waits for sent at once.")]
    public Duration? MinInterval { get; set; }

    [ArgShortcut("wait-every")]
    [Value("<duration>")]
    [ArgDescription(
        "That often, the engine's current token asked for and waited for — the provider asked to run the pivot at once, and a batch at or after it in front of this client: timed (token → in front). Verifying: each within 10 s, or it fails.")]
    public Duration? WaitEvery { get; set; }

    /// <summary>The cells shown of each batch: none, quiet.</summary>
    [ArgIgnore]
    public int CellsShown => Quiet ? 0 : Cells;

    /// <summary>The address, its values given --show's entries too (the statement made for them).</summary>
    [ArgIgnore]
    public string AddressShown => Shown.Length == 0 ? Address
        : Address + (Address.Contains('?') ? "&" : "?") + string.Join("&", Shown.Select(s => $"values={Uri.EscapeDataString(s.ToString())}"));

    /// <summary>What --show asks the cells to carry besides (<see cref="ShownValue" />): each entry read.</summary>
    [ArgIgnore]
    public ShownValue[] Shown =>
        [.. (Show ?? []).Select(e => ShownValue.TryParse(e.Trim(), out var shown) ? shown : default).Where(s => s.Value is not null)];

    /// <summary>The address's fields, in rows and in columns (none, for a statement of your own): where Date is.</summary>
    [ArgIgnore]
    public (string[] Rows, string[] Columns) Layout
    {
        get
        {
            if (Statement is not null) return ([], []);
            var query = Address[(Address.IndexOf('?') + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
                .Select(p => (Key: Uri.UnescapeDataString(p[0]), Value: Uri.UnescapeDataString(p[1].Replace('+', ' ')))).ToArray();
            return ([.. query.Where(p => p.Key == "rows").Select(p => p.Value)], [.. query.Where(p => p.Key == "columns").Select(p => p.Value)]);
        }
    }

    /// <summary>Its view maintained incrementally (the default), or recomputed.</summary>
    [ArgIgnore]
    public bool Incremental => !Recompute;

    /// <summary>The lists a statement of your own binds, by name: <see cref="Bind" />'s, a name's values in their order.</summary>
    [ArgIgnore]
    public Dictionary<string, string[]> Bindings =>
        (Bind ?? []).Select(b => b.Split('=', 2)).GroupBy(b => b[0].Trim(), b => b[1].Trim())
        .ToDictionary(g => g.Key, g => g.Distinct().ToArray());

    /// <summary>
    ///     The options <paramref name="args" /> give — or null, having said why (or the help): <paramref name="exit" /> 64 for
    ///     a
    ///     wrong command line, 0 for the help.
    /// </summary>
    public static Options? Parse(string[] args, out int exit)
    {
        string[] dashed =
        [
            .. args.Select(a => a.Length > 2 && a.StartsWith("--", StringComparison.Ordinal) ? a[1..] : a)
        ]; // (--name: PowerArgs reads one dash)
        try
        {
            var options = Args.Parse<Options>(dashed);
            if (options.Help)
            {
                Console.WriteLine(Usage());
                exit = 0;
                return null;
            }

            if (options.Check() is { } wrong) throw new ValidationArgException(wrong);
            exit = 0;
            return options;
        }
        catch (ArgException e)
        {
            Console.Error.WriteLine($"{e.Message} (--help: the options)");
            exit = 64;
            return null;
        }
    }

    /// <summary>What's wrong with the options together, if anything (each is checked as it's read).</summary>
    private string? Check()
    {
        if (Bind is not null && Statement is null)
            return "--bind binds a --statement's lists (an address's statement brings its own).";
        if (Bind?.FirstOrDefault(b => b.IndexOf('=') <= 0) is { } bare)
            return $"--bind takes name=value pairs ({bare} isn't one).";
        if (Show?.Select(e => e.Trim()).FirstOrDefault(e => !ShownValue.TryParse(e, out _)) is { } entry)
            return $"--show takes a page's Values entries: a value, or one with a form — row, column, total; running for volume and trades (not {entry}).";
        if (Shown.Any(s => s.Form == ShowAs.RunningTotalInDate) && !Layout.Rows.Concat(Layout.Columns).Contains("date"))
            return "--show's running totals run along the dates: an address with date in its rows or columns.";
        if (Blobs is not null && Stream is null)
            return "--blobs names the out-of-band fetches of --stream's routers (a spawned backend's are known).";
        if (Stream?.Concat(Blobs ?? []).Append(Ops ?? "http://ok/")
                .FirstOrDefault(u => !Uri.TryCreate(u, UriKind.Absolute, out _)) is
            { } url) return $"{url} isn't a URL.";
        return null;
    }

    /// <summary>The help: what it does, then each option — its names, its value, what it's for and its default — as declared.</summary>
    public static string Usage()
    {
        var text = new StringBuilder("""
                                     PivotTable.Headless — the pivot sample's positions, streamed to a console (Tapline.Client over MagicOnion).
                                     With no options: a backend of its own (the pivot sample, spawned on ports it picks — echoed), and the pivot of
                                     ?rows=instrument&columns=leg streamed from it, until Ctrl+C.

                                     Usage: dotnet run --project samples/PivotTable.Headless -- [options]


                                     """);
        var rows = new CommandLineArgumentsDefinition(typeof(Options)).Arguments.Select(a =>
        {
            var aliases =
                a.Aliases.Skip(1)
                    .ToList(); // (the first is the property's: shown as a word, its shortcut if it has one)
            var name = aliases.FirstOrDefault(s => s.Length > 1) ?? Kebab(a.DefaultAlias);
            var value = typeof(Options).GetProperty(a.DefaultAlias)?.GetCustomAttribute<ValueAttribute>()?.Text ?? "";
            var names = string.Join(", ",
                aliases.Where(s => s.Length == 1).Select(s => $"-{s}")
                    .Prepend($"--{name}{(value.Length > 0 ? " " + value : "")}"));
            var about = a.Description + (a.HasDefaultValue
                ? $" (default: {Convert.ToString(a.DefaultValue, CultureInfo.InvariantCulture)})"
                : "");
            return (Names: names, About: about);
        }).ToList();
        var column = rows.Max(r => r.Names.Length) + 4;
        foreach (var (names, about) in rows)
        {
            text.Append("  ").Append(names.PadRight(column - 2));
            var at = column;
            foreach (var word in about.Split(' '))
            {
                if (at + word.Length > 118 && at > column)
                {
                    text.Length--;
                    text.Append('\n').Append(' ', column);
                    at = column;
                }

                text.Append(word).Append(' ');
                at += word.Length + 1;
            }

            text.Length--;
            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary>A property's name as an option's: DropEvery, drop-every.</summary>
    internal static string Kebab(string name)
    {
        return string.Concat(name.Select((c, i) =>
            char.IsUpper(c) ? (i > 0 ? "-" : "") + char.ToLowerInvariant(c) : c.ToString()));
    }
}

/// <summary>An option's value, as its help shows it (<c>&lt;file&gt;</c>, <c>&lt;duration&gt;</c>…).</summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class ValueAttribute(string text) : Attribute
{
    public string Text { get; } = text;
}

/// <summary>A duration as an option takes it: 60s, 2m, 1h — or seconds, bare.</summary>
[ArgReviverType]
internal sealed record Duration(TimeSpan Value)
{
    [ArgReviver]
    public static Duration Revive(string key, string text)
    {
        double unit = text.Length == 0 ? 0 : text[^1] switch { 's' => 1, 'm' => 60, 'h' => 3600, _ => 0 };
        return double.TryParse(unit > 0 ? text[..^1] : text, NumberStyles.Float, CultureInfo.InvariantCulture,
            out var n) && n > 0
            ? new Duration(TimeSpan.FromSeconds(n * (unit > 0 ? unit : 1)))
            : throw new ValidationArgException($"--{Options.Kebab(key)}: a duration — 60s, 2m, 1h (not {text}).");
    }
}