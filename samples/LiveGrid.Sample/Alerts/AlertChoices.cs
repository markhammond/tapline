using System.Globalization;
using Tapline.Alerts;

namespace LiveGrid.Sample.Alerts;

/// <summary>
///     The sounds an alert can make: the ChalkQL blotter's recordings in <c>wwwroot/sounds</c> (see NOTICE.md), mono, 8-bit,
///     served with the page's assets. A page loads one the first time it plays it, and the browser keeps it.
/// </summary>
public static class AlertSounds
{
    public const string Default = "kaching";

    /// <summary>Each sound by its file's name, and as the alerts panel names it.</summary>
    public static readonly IReadOnlyList<(string Id, string Label)> All =
    [
        ("kaching", "Ka-ching"), ("classiccoin", "Coin"), ("chimes", "Chimes"), ("notify", "Notify"), ("cowbell", "Cowbell"),
        ("cuckoo", "Cuckoo"), ("rooster", "Rooster"), ("whistledown", "Whistle down"), ("boing", "Boing"), ("drip", "Drip"),
        ("ploop", "Ploop"), ("busy", "Busy tone"), ("oldcarhorn", "Old car horn"), ("vwhorn", "Beetle horn"), ("ship", "Ship's horn"),
        ("laserfire", "Laser"), ("bangecho", "Bang"), ("crash", "Crash"), ("cartooncharge", "Charge"), ("grow", "Grow"),
        ("passing", "Passing"), ("flush", "Flush")
    ];

    public static bool Exists(string id)
    {
        return All.Any(s => s.Id == id);
    }

    /// <summary>Where the page finds a sound, before its assets give it its fingerprinted address.</summary>
    public static string Path(string id)
    {
        return $"sounds/{id}.wav";
    }
}

/// <summary>What the page offers a rule, as the blotter does: its throttles, its waits for the database, its counts' bounds.</summary>
public static class AlertChoices
{
    public const int MostMatches = 1_000_000;

    /// <summary>The throttles a rule may have: its alerts sent no oftener (Tapline's MinInterval), what fired meanwhile counted in.</summary>
    public static readonly TimeSpan[] Throttles =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1)];

    /// <summary>The waits for the database a rule may fire after.</summary>
    public static readonly TimeSpan[] Lags =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    public static readonly TimeSpan ShortestWindow = TimeSpan.FromSeconds(5), LongestWindow = TimeSpan.FromMinutes(60);

    /// <summary>What's wrong with a count, or null: a window out of range or not whole seconds, or too few or many matches.</summary>
    public static string? Problem(AlertTrigger.Counting count)
    {
        return count.Window < ShortestWindow || count.Window > LongestWindow
            ? $"a window runs from {Span(ShortestWindow)} to {Span(LongestWindow)}"
            : count.Window.Ticks % TimeSpan.TicksPerSecond != 0
                ? "a window is a whole number of seconds"
                : count is { Kind: AlertCountKind.AtLeast, Matches: < 1 or > MostMatches }
                    ? $"at least runs from 1 to {MostMatches.ToString("N0", CultureInfo.InvariantCulture)} matches"
                    : null;
    }

    /// <summary>How a wait is proved, as the page says it, and what that means.</summary>
    public static (string Label, string Title) Proof(AlertWaitProof proof)
    {
        return proof == AlertWaitProof.Trusted
            ? ("trusted", "Fires at its threshold on what was last learnt of the database, a second old at most: sooner, but now and then for a row it has just landed")
            : ("confirmed", "Fires once the database is asked at its threshold and found still without it");
    }

    /// <summary>When a rule on <paramref name="tab" /> fires, in words: as a row arrives, over a wait (from when, if not its own time; trusted, if so), or a count.</summary>
    public static string Describe(AlertTrigger trigger, Tab? tab = null)
    {
        return trigger switch
        {
            AlertTrigger.Waiting waiting => $"over {Span(waiting.Over)} behind" +
                                            (waiting.From is { } from ? " " + (tab?.WaitsFrom.FirstOrDefault(a => a.Column == from)?.Label ?? "since " + from) : "") +
                                            (waiting.Proof == AlertWaitProof.Trusted ? ", trusted" : ""),
            AlertTrigger.Counting { Kind: AlertCountKind.AtLeast } count =>
                $"at least {count.Matches.ToString("N0", CultureInfo.InvariantCulture)} in {Span(count.Window)}",
            AlertTrigger.Counting count => $"none in {Span(count.Window)}",
            _ => "as it arrives"
        };
    }

    /// <summary>A span as the page writes it: whole minutes in minutes, otherwise seconds.</summary>
    public static string Span(TimeSpan span)
    {
        return span.TotalSeconds >= 60 && span.Ticks % TimeSpan.TicksPerMinute == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{span.TotalMinutes:0} min")
            : string.Create(CultureInfo.InvariantCulture, $"{span.TotalSeconds:0} s");
    }
}

/// <summary>
///     A rule as the browser keeps it, in its local settings (as the blotter keeps one): its tab and its condition in SQL,
///     its sound and throttle, and when it fires — on a row's arrival, a wait for the database (from when, and how it's
///     proved), or a count.
/// </summary>
public sealed record SavedRule(
    string Id,
    string Name,
    string Tab,
    string Condition,
    string Sound,
    double ThrottleSeconds,
    bool Muted,
    double? LagOverSeconds = null,
    string? CountKind = null,
    int? CountMatches = null,
    double? WindowSeconds = null,
    string? LagFrom = null,
    string? LagProof = null)
{
    public static SavedRule Of(AlertRule rule)
    {
        var count = rule.Trigger as AlertTrigger.Counting;
        var waiting = rule.Trigger as AlertTrigger.Waiting;
        return new SavedRule(rule.Id.ToString(), rule.Name, rule.Source, rule.Condition, rule.Sound, rule.Throttle.TotalSeconds, rule.Muted,
            waiting?.Over.TotalSeconds, count switch { null => null, { Kind: AlertCountKind.AtLeast } => "at-least", _ => "none" },
            count?.Matches, count?.Window.TotalSeconds, waiting?.From, waiting is null ? null : AlertChoices.Proof(waiting.Proof).Label);
    }

    /// <summary>The rule, as a page takes it up.</summary>
    /// <exception cref="ArgumentException">It isn't one the page offers: said why.</exception>
    public AlertRule Load(Catalog catalog)
    {
        if (!Guid.TryParse(Id, out var id)) throw new ArgumentException($"the alert '{Name}' has no id");
        if (catalog.Of(Tab) is not { } tab) throw new ArgumentException($"the alert '{Name}' is on no tab '{Tab}'");
        var condition = Alerts.Condition.Parse(tab, Condition);
        if (!condition.Errors.IsEmpty) throw new ArgumentException($"the alert '{Name}': {condition.Errors[0].Message}");
        if (!AlertSounds.Exists(Sound)) throw new ArgumentException($"the alert '{Name}' has no sound '{Sound}'");
        var throttle = AlertChoices.Throttles.FirstOrDefault(t => t.TotalSeconds >= ThrottleSeconds, AlertChoices.Throttles[^1]);
        AlertTrigger trigger = (LagOverSeconds, CountKind) switch
        {
            (not null, not null) => throw new ArgumentException($"the alert '{Name}' both counts and waits for the database"),
            ({ } seconds, null) => AlertChoices.Lags.FirstOrDefault(l => l.TotalSeconds == seconds) is { Ticks: > 0 } lag
                ? Wait(lag)
                : throw new ArgumentException($"the alert '{Name}' waits {seconds} s for the database, which isn't one of the waits offered"),
            (null, "at-least") => new AlertTrigger.Counting(AlertCountKind.AtLeast, CountMatches ?? 0, TimeSpan.FromSeconds(WindowSeconds ?? 0)),
            (null, "none") => new AlertTrigger.Counting(AlertCountKind.None, 0, TimeSpan.FromSeconds(WindowSeconds ?? 0)),
            (null, null) => AlertTrigger.Entering.Instance,
            (null, var kind) => throw new ArgumentException($"the alert '{Name}' counts '{kind}', which isn't a count offered")
        };
        if (trigger is AlertTrigger.Counting counting && AlertChoices.Problem(counting) is { } problem)
            throw new ArgumentException($"the alert '{Name}': {problem}");
        return new AlertRule(id, Name, tab.Name, condition.Text, trigger, throttle, Sound, Muted);

        // A wait from a time its tab offers (none: the version's own), proved as it offers (none said: the tab's first).
        AlertTrigger.Waiting Wait(TimeSpan lag)
        {
            if (tab.WaitsFrom.Count == 0) throw new ArgumentException($"the alert '{Name}' waits for the database, which {tab.Title} rows aren't");
            if (LagFrom is not null && tab.WaitsFrom.All(a => a.Column != LagFrom))
                throw new ArgumentException($"the alert '{Name}' waits from '{LagFrom}', which isn't a time a wait on {tab.Title} runs from");
            var proof = LagProof is null ? tab.WaitProofs.FirstOrDefault()
                : tab.WaitProofs.FirstOrDefault(p => AlertChoices.Proof(p).Label == LagProof, (AlertWaitProof)(-1));
            if (!tab.WaitProofs.Contains(proof))
                throw new ArgumentException($"the alert '{Name}' proves its wait as '{LagProof}', which isn't a way a wait on {tab.Title} is proved");
            return new AlertTrigger.Waiting(lag, proof, LagFrom);
        }
    }
}
