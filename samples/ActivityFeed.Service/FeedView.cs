using ActivityFeed.Contracts;
using Apache.Arrow;
using Chalk;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.Incremental;
using Tapline;

namespace ActivityFeed.Service;

/// <summary>
///     How the feeds' views are kept: <c>Practice:Maintenance</c>, incremental (the default: an update costs what changed)
///     or recompute (each update runs the statement in full); <c>Practice:Validate</c> (on by default), each incremental
///     view checked at every update against a recomputed twin: its statement run in full at the same epoch; and
///     <c>Practice:PlanSwap</c>, how an incremental view moves to a plan its statement takes: built beside the one it runs,
///     off the loop, and swapped in once built (<c>LowJitter</c>, the default), or computed whole at its next update
///     (<c>AtNextUpdate</c>), which stalls the loop meanwhile.
/// </summary>
public sealed record ViewPolicy(ViewMaintenance Maintenance, bool Validate, ViewPlanSwap PlanSwap = ViewPlanSwap.LowJitter)
{
    public static ViewPolicy From(IConfiguration configuration)
    {
        return new ViewPolicy(configuration.GetValue<ViewMaintenance?>("Practice:Maintenance") ?? ViewMaintenance.Incremental,
            configuration.GetValue<bool?>("Practice:Validate") ?? true,
            configuration.GetValue<ViewPlanSwap?>("Practice:PlanSwap") ?? ViewPlanSwap.LowJitter);
    }
}

/// <summary>
///     A view as the service reports it (<c>/views</c>): how it's kept, its updates and checks, and its rows; recomputed
///     though the policy asks for incremental maintenance, ChalkQL's reason; its statement's plan (a class's statement is
///     every class's of its shape), the digest of the grants it binds (its class's own) and where it stands, its stamp: the
///     engine's version it's at, and how many times it's been rebound.
/// </summary>
public sealed record ViewReport(string View, string Kept, long Updates, long Checks, int Rows, long StateBytes, string? Why = null,
    string? Plan = null, string? Grants = null, ViewStamp? Stamp = null);

/// <summary>A view that gave way to a recomputed one: when, and why.</summary>
public sealed record GaveWay(string View, DateTime At, string Why);

/// <summary>
///     The feeds' views (<c>/views</c>): the policy, each view the service keeps, the last that gave way, and the
///     dispatcher topics' pipes.
/// </summary>
public sealed record ViewsReport(string Maintenance, bool Validate, ViewReport[] Views, GaveWay[] GaveWay,
    long OracleChecks = 0, OracleReport[]? OracleMismatches = null, string? PlanSwap = null, PipeReport[]? Pipes = null);

/// <summary>
///     A dispatcher topic's pipe: its batches, the most ever with its reader at once, the batches handed to it, and how
///     often the loop found every one still with it (the view waiting, its versions held by the engine).
/// </summary>
public sealed record PipeReport(string Topic, int Batches, int MaxInFlight, long Handed, long Waited);

/// <summary>
///     What the service measured (<c>/measure</c>, opted into with <c>Practice:Measure</c>) since it started, or was last
///     asked to start again: the activities that happened; its posts to topics, and their mean time in microseconds; each
///     turn's write (ChalkQL's refresh with it) and its fan-out (the views' update, the posts); and, by kind of topic, its
///     posts, those whole (snapshots), and each post's lateness, from the newest row it carries that happened that turn
///     (an activity, or a dispatch) to its post: the provider's hop; each view's update; and its garbage collections.
/// </summary>
public sealed record MeasureReport(double Seconds, long Activities, long Posts, double PostsPerSecond, double PostMicros,
    Percentiles[] Turns, TopicPosts[] Topics, Percentiles[] Updates, Collections Collections);

/// <summary>A process's garbage collections over a while: by generation, and the time it was paused for them.</summary>
public sealed record Collections(int Gen0, int Gen1, int Gen2, double PausedMs)
{
    public static Collections Now()
    {
        return new Collections(GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
            GC.GetTotalPauseDuration().TotalMilliseconds);
    }

    public Collections Since(Collections then)
    {
        return new Collections(Gen0 - then.Gen0, Gen1 - then.Gen1, Gen2 - then.Gen2, PausedMs - then.PausedMs);
    }

    public override string ToString()
    {
        return $"{Gen0} gen0, {Gen1} gen1, {Gen2} gen2 collections, paused {PausedMs:F1} ms in all";
    }
}

/// <summary>A kind of topic's posts measured: how many, how many whole, and their lateness.</summary>
public sealed record TopicPosts(string Of, long Posts, long Snapshots, Percentiles Lateness);

/// <summary>Samples' percentiles, in milliseconds (by nearest rank).</summary>
public sealed record Percentiles(string Of, int Samples, double P50, double P99, double P995, double Max);

/// <summary>A run of samples, in milliseconds: the last so many kept, each added without allocating.</summary>
public sealed class Samples(string of, int capacity = 1 << 16)
{
    private readonly double[] _ring = new double[capacity];
    private long _count;

    public void Add(double milliseconds)
    {
        _ring[(int)(_count++ % _ring.Length)] = milliseconds;
    }

    public void Clear()
    {
        _count = 0;
    }

    public Percentiles Read()
    {
        var n = (int)Math.Min(_count, _ring.Length);
        var sorted = _ring[..n];
        System.Array.Sort(sorted);
        double At(double q) => n == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(q * n) - 1, 0, n - 1)];
        return new Percentiles(of, n, At(0.5), At(0.99), At(0.995), n == 0 ? 0 : sorted[^1]);
    }
}

/// <summary>
///     What an oracle found of one topic: its statements run whole (as its principal, at the loop's epoch) against what
///     the topic holds, by row count and checksum (Tapline's: the sum of its rows' hashes), its own rows beside them. Held
///     (its site falling behind): not checked.
/// </summary>
public sealed record OracleReport(string Topic, long Version, int Rows, uint Held, int ExpectedRows, uint Expected, bool Ok,
    DateTime At, string? Note = null);

/// <summary>
///     A feed's standing view: a statement prepared for the shape of its principal's grants (the one every principal of
///     that shape's views run), the principal's own grants bound to it for the view's life; or a statement prepared with
///     them folded in. Kept as the policy asks. An incremental view validated is checked at every update against its
///     recomputed twin (the same statement and grants: the same rows, by count and checksum). One that differs from its
///     twin, or fails, gives way to a recomputed one, its readers sent it whole; one whose statement ChalkQL can't maintain
///     is recomputed from the start. Either way the log says why.
/// </summary>
internal sealed class FeedView<TRow> : IAsyncDisposable where TRow : unmanaged
{
    private readonly RequestContext? _grants; // (bound to a statement prepared for a shape; null: folded into it)
    private readonly IReadOnlyDictionary<string, object?>? _named; // (its statement's own parameters, bound for its life)
    private readonly string[] _key;
    private readonly ViewMapper<FeedKey, TRow> _map;
    private readonly PracticeStore _store;
    private readonly (ViewVersions Versions, int MaxBacklog) _versions; // (which it publishes: every view of it alike)
    private StandingView<FeedKey, TRow>? _twin; // (an incremental view's, validated: recomputed)
    private StandingView<FeedKey, TRow> _view;

    private FeedView(PracticeStore store, string name, string sql, EntitledQuery query, RequestContext? grants,
        IReadOnlyList<KeyValuePair<string, object?>> parameters, string[] key, ViewMapper<FeedKey, TRow> map,
        (ViewVersions, int) versions, StandingView<FeedKey, TRow> view, StandingView<FeedKey, TRow>? twin, string? why = null)
    {
        (_store, Name, Sql, Query, _grants, Parameters, _key, _map, _versions, _view, _twin, _why) =
            (store, name, sql, query, grants, parameters, key, map, versions, view, twin, why);
        _named = parameters.Count == 0 ? null : parameters.ToDictionary();
    }

    private readonly string? _why; // (recomputed though the policy asked for incremental: ChalkQL's reason)

    public string Name { get; }

    /// <summary>Its statement's text: what an oracle runs whole, prepared afresh as the principal.</summary>
    public string Sql { get; }

    /// <summary>The values its statement's own parameters take (a GP's id, say), in the order they first appear.</summary>
    public IReadOnlyList<KeyValuePair<string, object?>> Parameters { get; }

    /// <summary>The statement the view runs: its plan, its columns and what each discloses.</summary>
    public EntitledQuery Query { get; }

    /// <summary>The digest of the grants the view binds, or of those folded into its statement (ChalkQL's).</summary>
    public string? Grants => _view.ContextDigest;

    public int Count => _view.Count;

    /// <summary>The sum of its rows' hashes (Tapline's, of the rows it holds): what a reader level with it holds.</summary>
    public uint Checksum => _view.Checksum;

    /// <summary>The version of the epoch it's at.</summary>
    public long Version => _view.Version;

    /// <summary>Where it stands: its version, and how many times it's been rebound (ChalkQL's view stamp).</summary>
    public ViewStamp Stamp => _view.Stamp;

    public long Updates { get; private set; }
    public long Checks { get; private set; }

    /// <summary>How it's kept, in words.</summary>
    public string Kept => (_view.Maintenance != ViewMaintenance.Incremental ? "recomputed"
        : _twin is null ? "incremental"
        : "incremental, validated against a recomputed twin") + (_versions.Versions == ViewVersions.Every ? ", every version" : "");

    public async ValueTask DisposeAsync()
    {
        await _view.DisposeAsync();
        if (_twin is not null) await _twin.DisposeAsync();
    }

    public ViewReport Report()
    {
        return new ViewReport(Name, Kept, Updates, Checks, _view.Count, _view.StateBytes + (_twin?.StateBytes ?? 0), _why,
            $"{Query.PlanDigest:x16}", Grants, Stamp);
    }

    /// <summary>What's changed since a reader's cursor: drained into its sink.</summary>
    public DrainOutcome Drain(ref ViewCursor cursor, IViewSink<FeedKey, TRow> sink, int budget)
    {
        return _view.Drain(ref cursor, sink, budget);
    }

    /// <summary>
    ///     A view of <paramref name="query" /> (<paramref name="sql" />, prepared), binding <paramref name="grants" /> if it
    ///     was prepared for their shape, keyed by <paramref name="key" /> (its columns), its rows mapped as
    ///     <paramref name="map" /> makes of its columns: as the policy asks (or recomputed, saying why). It publishes the
    ///     latest version at each update, or, with <see cref="ViewVersions.Every" />, every version that changes it, one an
    ///     update, the engine holding those it has yet to publish (at most <paramref name="maxBacklog" />: one more, and it's
    ///     made again, its readers sent it whole). Its twin publishes the same, so the two meet at every version.
    /// </summary>
    public static async Task<FeedView<TRow>> MakeAsync(PracticeStore store, string name, string sql, EntitledQuery query,
        RequestContext? grants, string[] key, Func<Schema, ViewMapper<FeedKey, TRow>> map, ViewPolicy policy, ILogger log,
        CancellationToken stop, ViewVersions versions = ViewVersions.Latest, int maxBacklog = 1024,
        IReadOnlyList<KeyValuePair<string, object?>>? parameters = null)
    {
        parameters ??= [];
        var named = parameters.Count == 0 ? null : parameters.ToDictionary();
        var mapper = map(query.OutputSchema);
        string? why = null;
        if (policy.Maintenance == ViewMaintenance.Incremental)
            try
            {
                var view = await ViewOfAsync(store, query, grants, named, key, mapper, ViewMaintenance.Incremental,
                    (versions, maxBacklog), stop, policy.PlanSwap);
                try
                {
                    var twin = policy.Validate
                        ? await ViewOfAsync(store, query, grants, named, key, mapper, ViewMaintenance.Recompute, (versions, maxBacklog),
                            stop)
                        : null;
                    return new FeedView<TRow>(store, name, sql, query, grants, parameters, key, mapper, (versions, maxBacklog), view,
                        twin);
                }
                catch
                {
                    await view.DisposeAsync();
                    throw;
                }
            }
            catch (StandingViewException e) // (a statement ChalkQL can't maintain: recomputed, saying why)
            {
                log.LogWarning("Practice: {View} is recomputed: ChalkQL can't maintain its statement ({Reason})", name,
                    e.Message);
                why = e.Message;
            }

        return new FeedView<TRow>(store, name, sql, query, grants, parameters, key, mapper, (versions, maxBacklog),
            await ViewOfAsync(store, query, grants, named, key, mapper, ViewMaintenance.Recompute, (versions, maxBacklog), stop), null,
            why);
    }

    /// <summary>
    ///     Up to date with the engine's last refresh, and an incremental view validated, checked against its twin. A reason,
    ///     when it gave way to a recomputed view: its readers start over (their cursors from the start), sent it whole.
    /// </summary>
    public async Task<string?> UpdateAsync(CancellationToken stop)
    {
        Updates++;
        if (_view.Maintenance != ViewMaintenance.Incremental)
        {
            try
            {
                await _view.UpdateAsync(stop);
                return null;
            }
            catch (ChalkException e) // (its update failed: further behind than its backlog, say)
            {
                var again = await ViewOfAsync(_store, Query, _grants, _named, _key, _map, ViewMaintenance.Recompute, _versions, stop);
                await again.UpdateAsync(stop);
                var failed = _view;
                _view = again;
                await failed.DisposeAsync();
                return $"its update failed, so it was made again: {e.Message}";
            }
        }

        try
        {
            await _view.UpdateAsync(stop);
        }
        catch (ChalkException e) // (its maintenance failed: past its state budget, say)
        {
            return await GiveWayAsync($"its update failed: {e.Message}", stop);
        }

        if (_twin is null) return null;
        await _twin.UpdateAsync(stop);
        Checks++;
        return _view.Version == _twin.Version && _view.Count == _twin.Count && _view.Checksum == _twin.Checksum
            ? null
            : await GiveWayAsync($"at version {_twin.Version} it holds {_view.Count} rows (checksum {_view.Checksum:x8}, " +
                                 $"version {_view.Version}), its recomputed twin {_twin.Count} ({_twin.Checksum:x8})", stop);
    }

    /// <summary>
    ///     The incremental view let go, and a recomputed one in its place: its twin, if it has one that updates, or one made
    ///     anew (a twin as far behind as a view past its backlog fails as it did).
    /// </summary>
    private async Task<string> GiveWayAsync(string why, CancellationToken stop)
    {
        var recomputed = _twin;
        if (recomputed is not null)
            try
            {
                await recomputed.UpdateAsync(stop);
            }
            catch (ChalkException)
            {
                await recomputed.DisposeAsync();
                recomputed = null;
            }

        if (recomputed is null)
        {
            recomputed = await ViewOfAsync(_store, Query, _grants, _named, _key, _map, ViewMaintenance.Recompute, _versions, stop);
            await recomputed.UpdateAsync(stop);
        }

        var incremental = _view;
        (_view, _twin) = (recomputed, null);
        await incremental.DisposeAsync();
        return why;
    }

    /// <summary>
    ///     A view of the statement (one of the many it serves), the grants and its own parameters bound to it for its life,
    ///     if any; an incremental one moving to a new plan as <paramref name="swap" /> says (a recomputed one takes it at no
    ///     extra cost).
    /// </summary>
    private static async Task<StandingView<FeedKey, TRow>> ViewOfAsync(PracticeStore store, EntitledQuery query,
        RequestContext? grants, IReadOnlyDictionary<string, object?>? parameters, string[] key, ViewMapper<FeedKey, TRow> map, ViewMaintenance maintenance,
        (ViewVersions Versions, int MaxBacklog) versions, CancellationToken stop, ViewPlanSwap swap = ViewPlanSwap.AtNextUpdate)
    {
        return await store.Engine.MaterializeAsync(query.Query, new ViewOptions<FeedKey, TRow>
        {
            Key = key, Map = map, RowHash = RowHash.Of, // (the view's checksum: Tapline's, of the rows it holds)
            Maintenance = maintenance, LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows, Context = grants,
            Parameters = parameters,
            PlanSwap = maintenance == ViewMaintenance.Incremental ? swap : ViewPlanSwap.AtNextUpdate,
            Versions = versions.Versions, MaxBacklog = versions.MaxBacklog
        }, stop);
    }
}
