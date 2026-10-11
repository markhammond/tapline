using MagicOnion;
using MessagePack;

namespace PivotTable.Contracts;

/// <summary>
///     The pivot over MagicOnion, for clients of the stream (Tapline.MagicOnion): a statement — a page's address's, or one
///     of the client's own — its shape, described before anything streams, and its step 1, bound on the client's session:
///     its stream then carries it, once the client activates it (step 2).
/// </summary>
public interface IPivotService : IService<IPivotService>
{
    /// <summary>
    ///     The statement <paramref name="address" /> asks for (a page's address, or its query:
    ///     <c>?rows=client&amp;columns=instrument</c>),
    ///     as the page makes it, the lists it binds, and what its cells carry besides its values (its Show as entries:
    ///     <c>values=volume.column</c>).
    /// </summary>
    UnaryResult<PivotStatement> StatementAsync(string address);

    /// <summary>
    ///     What a statement's pivot would stream — its shape (<see cref="PivotShape" />, as JSON): what it binds and answers,
    ///     and the cells made of it, their <paramref name="extras" /> (Show as entries, <see cref="ShownValue" />) after their
    ///     figures — without subscribing; or why it can't be a pivot.
    /// </summary>
    UnaryResult<PivotDescription> DescribeAsync(string sql, Dictionary<string, string[]> bindings, string[] extras);

    /// <summary>
    ///     Binds a statement's pivot, <paramref name="bindings" /> bound, on the session <paramref name="outletId" />.
    ///     <paramref name="nonce" />
    ///     — the stream's — maintained incrementally or recomputed (<paramref name="incremental" />), its cells carrying
    ///     <paramref name="extras" /> too (Show as entries: the provider computes them) — at most one state a
    ///     <paramref name="minIntervalMs" /> (0: as they come; the rest conflated, its own token's at once). Answers its
    ///     binding, or why not.
    /// </summary>
    UnaryResult<PivotBinding> SubscribeAsync(int outletId, uint nonce, string sql,
        Dictionary<string, string[]> bindings, bool incremental, string[] extras, Guid? shard = null,
        double minIntervalMs = 0);

    /// <summary>
    ///     Sets the brokerage's pace (client trades a day: the history primed again at it, for every pivot), and answers
    ///     where it took effect: the token of the refresh that applied it — what a client waits for to have it in front
    ///     of it (<see cref="WaitAsync" />).
    /// </summary>
    UnaryResult<PivotToken> SetTradesPerDayAsync(double perDay);

    /// <summary>Where the provider's engine stands now: its token (its shard, and its last refresh's version).</summary>
    UnaryResult<PivotToken> CurrentAsync();

    /// <summary>
    ///     Waits until the pivot subscribed as <paramref name="tapId" /> on the session has posted an answer at or after
    ///     <paramref name="token" /> — run at once, not after its backoff — and answers the token it posted at. Its batch
    ///     follows on the stream, stamped as it says — at once, the subscription's minimum interval notwithstanding. Refused:
    ///     another shard's token, or no such subscription.
    /// </summary>
    UnaryResult<PivotToken> WaitAsync(int outletId, uint nonce, uint tapId, PivotToken token);
}

/// <summary>
///     Where a change took effect, or where a pivot stands: the provider's engine (its shard — a version means something
///     only on the engine that numbered it), its version, and — a pivot's — its view's generation (its rebinds: none, a
///     pivot's criteria changing make a new one). A batch is at or after a token if its version is, on the same shard.
/// </summary>
[MessagePackObject]
public sealed class PivotToken
{
    [Key(0)] public Guid Shard { get; set; }
    [Key(1)] public long Version { get; set; }
    [Key(2)] public long Generation { get; set; }

    /// <summary>Why there's none: another shard's token, no such subscription, the wait given up.</summary>
    [Key(3)]
    public string? Refused { get; set; }

    public override string ToString()
    {
        return Refused ?? $"v{Version}{(Generation > 0 ? $".{Generation}" : "")}@{Shard.ToString()[..8]}";
    }
}

/// <summary>A pivot's statement: its text, in ChalkQL's dialect, the lists it binds, by name, and its cells' extras.</summary>
[MessagePackObject]
public sealed class PivotStatement
{
    [Key(0)] public string Sql { get; set; } = "";
    [Key(1)] public Dictionary<string, string[]> Bindings { get; set; } = [];

    /// <summary>What its cells carry besides its values: Show as entries, in their order (<see cref="ShownValue.Extras" />).</summary>
    [Key(2)] public string[] Extras { get; set; } = [];
}

/// <summary>A statement's shape (<see cref="PivotShape" />, as JSON) — or why it can't be a pivot.</summary>
[MessagePackObject]
public sealed class PivotDescription
{
    [Key(0)] public string? Shape { get; set; }

    /// <summary>Why the statement can't be a pivot: ChalkQL's refusal, or what a pivot's statement answers that it doesn't.</summary>
    [Key(1)]
    public string? Refused { get; set; }
}

/// <summary>
///     A pivot's step-1 answer: its tap's binding — or that the provider refused it (why, and the needle), the
///     service isn't up yet, or the session is unknown.
/// </summary>
[MessagePackObject]
public sealed class PivotBinding
{
    [Key(0)] public uint TapId { get; set; }
    [Key(1)] public string? Topic { get; set; }
    [Key(2)] public string? Service { get; set; }

    /// <summary>The router's transient id for the provider instance holding it: what its health notices name.</summary>
    [Key(3)]
    public Guid? NodeId { get; set; }

    /// <summary>The provider refused it, saying why.</summary>
    [Key(5)]
    public string? Refused { get; set; }

    /// <summary>The refusal's correlation id: the provider and the router logged it with this.</summary>
    [Key(6)]
    public Guid? CorrelationId { get; set; }

    /// <summary>The service isn't up yet (the app starting, a provider restarting): ask again shortly.</summary>
    [Key(7)]
    public bool Unavailable { get; set; }

    /// <summary>The session named isn't held (it ended, or never was): connect again, then ask.</summary>
    [Key(8)]
    public bool UnknownSession { get; set; }

    /// <summary>The partition of the service it's in, if the provider put it in one: ids of the router's run, never names.</summary>
    [Key(9)]
    public string? Partition { get; set; }

    /// <summary>The provider's engine serving it: its batches' versions, and the tokens it answers, are that shard's.</summary>
    [Key(10)]
    public Guid? Shard { get; set; }
}