namespace Tapline.Alerts;

/// <summary>Whether a count fires on enough matches in its window, or on none.</summary>
public enum AlertCountKind
{
    AtLeast,
    None
}

/// <summary>How a wait is shown to be still waiting before it fires: a rule's choice, of those its source offers.</summary>
public enum AlertWaitProof
{
    /// <summary>The store asked as the wait passes its threshold: a row the store is found to hold doesn't fire.</summary>
    Confirmed,

    /// <summary>On what was last learnt of the store, a moment ago: sooner, the store not asked, but a row it has just taken may fire.</summary>
    Trusted
}

/// <summary>
///     What fires a rule (virtual-grid.md §15.1, item 2): a row coming to pass its condition; a row that passes waiting
///     longer than so long (for the store, in the blotter); or the matches in a sliding window coming to be at least so
///     many, or none. An evaluator says which it offers, and refuses the rest.
/// </summary>
public abstract record AlertTrigger
{
    private AlertTrigger()
    {
    }

    /// <summary>A row comes to pass the condition: once, and again only after it has stopped passing.</summary>
    public sealed record Entering : AlertTrigger
    {
        public static readonly Entering Instance = new();
    }

    /// <summary>
    ///     A row that passes the condition has waited longer than <paramref name="Over" />: once a wait. Shown to be still
    ///     waiting as <paramref name="Proof" /> says, and waiting from <paramref name="From" />: a timestamp the row carries,
    ///     one its source offers (none: the version's own).
    /// </summary>
    public sealed record Waiting(TimeSpan Over, AlertWaitProof Proof = AlertWaitProof.Confirmed, string? From = null) : AlertTrigger;

    /// <summary>
    ///     The matches in the last <paramref name="Window" /> come to be at least <paramref name="Matches" />, or none (a
    ///     count of none judged only once a whole window has passed): it fires as that starts, and again only after it
    ///     has stopped.
    /// </summary>
    public sealed record Counting(AlertCountKind Kind, int Matches, TimeSpan Window) : AlertTrigger;
}

/// <summary>
///     What an evaluator watches: a condition on a source's rows, in the evaluator's own language, and its trigger — one
///     watch however many rules, in however many audiences, share it.
/// </summary>
/// <param name="Source">The rows' source, named as the app names it (a grid's, say: "orders").</param>
/// <param name="Condition">The condition, the evaluator's text (SQL, for ChalkQL's): opaque to Tapline, untrusted.</param>
public sealed record AlertWatch(string Source, string Condition, AlertTrigger Trigger);

/// <summary>
///     A rule: a condition on a source's rows, its trigger, a throttle — fired again within it, a rule gathers its rows
///     and changes once the throttle allows, saying how many — and how a page presents it: its name, its sound, muted or
///     not.
/// </summary>
/// <param name="Sound">The sound a page plays for it: the adopter's own name for one (its presenter finds it by URL).</param>
public sealed record AlertRule(Guid Id, string Name, string Source, string Condition, AlertTrigger Trigger, TimeSpan Throttle, string Sound,
    bool Muted)
{
    /// <summary>What it watches, as the evaluator shares it.</summary>
    public AlertWatch Watch => new(Source, Condition, Trigger);
}

/// <summary>
///     A row a watch fired for: its key, as the source's grid would reveal it (text), and a line saying what it was. A
///     count fires with no key, its line saying how many.
/// </summary>
public readonly record struct AlertHit(string Key, string Summary);

/// <summary>Where an evaluator says what its watches fired, on whichever thread it decides them.</summary>
public interface IAlertSink
{
    void Fired(AlertWatch watch, ReadOnlySpan<AlertHit> hits);
}

/// <summary>
///     ① Evaluation (virtual-grid.md §15.2): what decides that something fired — the adopter's, beside its grid source
///     but no part of it. It checks a watch before a rule keeps it (refused: why), keeps evaluating the distinct watches
///     in use, each once however many rules share it, and says which rows fire them. A row fires a watch once, however
///     the evaluator hears of it, and again only after it has stopped passing.
/// </summary>
public interface IAlertEvaluator : IAsyncDisposable
{
    /// <summary>Null if it can evaluate <paramref name="watch" />; else why not, as a person reads it.</summary>
    ValueTask<string?> CheckAsync(AlertWatch watch, CancellationToken cancellationToken);

    /// <summary>
    ///     The distinct watches in use, from now: those new begun (from now: what passes already doesn't fire), those no
    ///     longer listed let go. What they fire goes to <paramref name="sink" />. Those it can't evaluate (any more), and
    ///     why.
    /// </summary>
    ValueTask<IReadOnlyDictionary<AlertWatch, string>> WatchAsync(IReadOnlyCollection<AlertWatch> watches, IAlertSink sink,
        CancellationToken cancellationToken);
}
