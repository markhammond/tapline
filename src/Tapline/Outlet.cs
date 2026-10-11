using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Claims;

namespace Tapline;

public class OutletOptions
{
    /// <summary>Soft per-frame byte budget shared by the weighted classes; bounds head-of-line blocking on the link.</summary>
    public int FrameBudget { get; set; } = 16 * 1024;

    /// <summary>
    ///     Hard cap. Only the first record of a frame may exceed <see cref="FrameBudget" /> (e.g. a large snapshot). Frames
    ///     are
    ///     built in a buffer of the outlet's own sized for the budget (or a 64 KB message); one whose first record needs more
    ///     is built in a buffer of this size rented from the shared pool for that frame.
    /// </summary>
    public int MaxFrame { get; set; } = 1024 * 1024;

    /// <summary>
    ///     Most frames carrying data in flight before waiting for client acks. Under <see cref="FlowControlMode.Adaptive" />
    ///     an
    ///     upper bound that rarely binds — data frames are spaced at least minimum round trip ÷ half of it apart, an even
    ///     stream (32 frames per round trip, by default: 160 a second at 200 ms, each carrying everything since the last);
    ///     under
    ///     <see cref="FlowControlMode.Fixed" /> the limit itself. Heartbeats are exempt and paced by
    ///     their own echo instead (at most one in flight).
    /// </summary>
    public int Window { get; set; } = 64;

    /// <summary>
    ///     How much this outlet has in flight: adaptive (default; see <see cref="FlowControlMode.Adaptive" />) or a fixed
    ///     number of frames.
    /// </summary>
    public FlowControlMode FlowControl { get; set; } = FlowControlMode.Adaptive;

    /// <summary>
    ///     Adaptive flow control: the standing queue tolerated on the path, as round trip in excess of the minimum (a
    ///     sixteenth
    ///     of the minimum is allowed on top, for jitter). Smaller: less buffering, a little less throughput while probing.
    /// </summary>
    public TimeSpan QueueDelayTarget { get; set; } = TimeSpan.FromMilliseconds(2);

    /// <summary>
    ///     Adaptive flow control: a frame unacknowledged this long — and for three smoothed round trips — is overdue: its
    ///     client is presumed busy (a page's main thread blocked, a tab frozen), not the path slow. Until an acknowledgement
    ///     comes, state waits — values, tables, watches: what changes meanwhile conflates, and goes as one record each, the
    ///     newest — while message topics and mailboxes keep going, past the in-flight byte limit, within the
    ///     <see cref="Window" /> (nothing of theirs conflates: held, they'd only arrive a round trip later, and a held
    ///     subscriber falls behind its topic's buffer — <see cref="OverflowPolicy.Fault" /> would end it for a busy moment).
    ///     A message ordered after state still brings that state with it (<see cref="After" />).
    ///     <see cref="TimeSpan.Zero" />: off.
    /// </summary>
    public TimeSpan AckOverdue { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    ///     Adaptive flow control: bytes in flight allowed before the first round trip is measured — kept small, since it goes
    ///     out unpaced into a path of unknown speed (1 KB is 0.5 s of queue at 16 kbit/s); it then doubles per round trip
    ///     while
    ///     ready data is held back (a 60 Hz UI stream needs ~2 KB per 100 ms of round trip, reached in a round or two).
    /// </summary>
    public int InitialInflightBytes { get; set; } = 1024;

    /// <summary>
    ///     Data frames per second, at most — one per period, carrying everything that changed since the last, phase-locked
    ///     while data keeps coming (e.g. 60, a display's refresh: no point sending what can't be shown). 0 (default): as fast
    ///     as
    ///     flow control allows. Heartbeats and taps in the High class aren't held to it (their records go in a High-only frame
    ///     between ticks). Below the rate the path can carry, frames simply carry more each (conflation). Precision is the
    ///     registry's timing wheel tick (1 ms by default).
    /// </summary>
    public int MaxFramesPerSecond { get; set; }

    /// <summary>
    ///     Records kept per subscription for the viewer (<see cref="Outlet.DescribeTaps" />): the last this many sent (rounded
    ///     up
    ///     to a power of two), with when the data arrived, when it was sent and acknowledged, and its first bytes. A ring per
    ///     tap made at its first record, written by the frame builder alone — a few stores a record, no lock. 0: none kept.
    /// </summary>
    public int MessageHistory { get; set; } = 16;

    /// <summary>
    ///     How much of each record a subscription's history keeps (<see cref="Tapline.RecordRetention" />): by default all of
    ///     it —
    ///     the record as sent, conflated, the subscriber's own — so the viewer can render it; a topic may choose otherwise
    ///     (<see cref="Source.Retention" />), and its provider may allow less (<see cref="PayloadFormat.Retention" />).
    /// </summary>
    public RecordRetention RecordRetention { get; set; } = RecordRetention.Whole;

    /// <summary>
    ///     The bytes of whole records a subscription's history holds — a ring made with its history (at its first record), the
    ///     newest records' bytes kept whole while they fit, older ones' first 32 bytes after; a record larger than it, only
    ///     those. In-band records are at most <see cref="OutOfBandThreshold" />; larger ones are kept in the blob store, by
    ///     handle. Taken from <see cref="HistoryBudget" />.
    /// </summary>
    public int HistoryBytes { get; set; } = 8 * 1024;

    /// <summary>
    ///     The most one client's subscriptions keep of whole records, in all (0: none whole) — a budget of its own, apart from
    ///     <see cref="MemoryBudget" />: keeping records for the viewer never costs a client a subscription or a mailbox. A
    ///     subscription's ring (<see cref="HistoryBytes" />) is taken from it at its first record, and given back when it
    ///     ends;
    ///     with no room left, it keeps its records' first 32 bytes instead. 256 KB: 32 rings of 8 KB.
    /// </summary>
    public long HistoryBudget { get; set; } = 256 * 1024;

    /// <summary>
    ///     A subscription's mailbox (<see cref="Tap.TryPost" />): messages kept (rounded up to a power of two) and bytes — the
    ///     largest message, too. Made at its first post; kept after they're sent, for a reconnect to resume from.
    /// </summary>
    public int MailboxMessages { get; set; } = 32;

    public int MailboxBytes { get; set; } = 16 * 1024;

    /// <summary>Relative byte rates when classes compete: High 16 : Normal 4 : Streaming 4 : Low 1.</summary>
    public int WeightHigh { get; set; } = 16;

    public int WeightNormal { get; set; } = 4;
    public int WeightStreaming { get; set; } = 4;
    public int WeightLow { get; set; } = 1;

    /// <summary>
    ///     Per-client cap on Streaming bytes per second (0 = unlimited; change per client with
    ///     <see cref="Outlet.SetStreamingRate" />). A slower client's taps lag and squash more; other classes are unaffected.
    /// </summary>
    public long StreamingBytesPerSecond { get; set; }

    /// <summary>Streaming budget an idle client may spend at once. Size it to at least the largest snapshot.</summary>
    public long StreamingBurstBytes { get; set; } = 256 * 1024;

    /// <summary>
    ///     With <see cref="Delivery.Auto" />, records larger than this travel out of band (0 = only what can't travel in-band:
    ///     values over 64 KB and records larger than <see cref="MaxFrame" />).
    /// </summary>
    public int OutOfBandThreshold { get; set; } = 16 * 1024;

    /// <summary>Largest record serialized out of band; anything larger parks its tap as oversize.</summary>
    public int MaxOutOfBandBytes { get; set; } = 64 << 20;

    /// <summary>A tap waiting this long for the client to fetch an out-of-band record resumes (with a fresh snapshot).</summary>
    public TimeSpan FetchTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A tap bound but not activated by the client within this time is closed.</summary>
    public TimeSpan BindTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     The most memory one client's subscriptions may hold, in bytes (0: no limit) — each subscription is charged for what
    ///     the router keeps for it: the tap itself, its history ring (<see cref="MessageHistory" />), its mailbox's ring
    ///     (<see cref="MailboxBytes" />, once it has one) and a provider's snapshot waiting to be sent. Conflation keeps a
    ///     subscription's backlog out of it (a slow client's tap holds a cursor, not a queue: what it hasn't been sent is the
    ///     topic's), so what grows is the number of subscriptions and mailboxes. Over it, a new subscription is refused
    ///     (<see cref="OutletFullException" />) and so is a mailbox's first post. Shared buffers (a message topic's, the blob
    ///     store's) are bounded where they are, not here.
    /// </summary>
    public long MemoryBudget { get; set; } = 4 << 20;

    /// <summary>
    ///     A connected client that acknowledges nothing for this long while there is something for it — frames it hasn't
    ///     acknowledged, changes waiting to go — is stalled (frozen, or its connection is half-open): the router ends its
    ///     subscriptions (<see cref="FaultCode.Stalled" />), freeing what they hold and the topics' work for them, and tells
    ///     it
    ///     in one record (<see cref="Wire.Ended" />) for when it comes back. Progress is an acknowledgement, a heartbeat's
    ///     echo,
    ///     or a new connection; a slow client that keeps acknowledging isn't stalled, nor an idle one. A disconnected client
    ///     isn't stalled: see <see cref="DisconnectedHold" />. Checked at each <see cref="Outlet.Heartbeat" />. Zero or
    ///     infinite: never.
    /// </summary>
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     The host's source of identities (docs/provider.md, signing in): given the transport's user as its client connects
    ///     — ASP.NET Core's <c>HttpContext.User</c> behind a Blazor circuit or a MagicOnion stream, whatever signed it in
    ///     (cookies, OIDC…) — and again on each reconnect, it returns who the connection is (<see cref="ClientIdentity" />,
    ///     naming its module), or null to leave it as it is. Null (the default): none — identities come from authentication
    ///     providers alone.
    /// </summary>
    public Func<ClaimsPrincipal, ClientIdentity?>? Identify { get; set; }

    /// <summary>
    ///     How long a disconnected client's subscriptions are held for it to come back and resume (
    ///     <see cref="Outlet.Resume" />,
    ///     exactly from what it received), its connection gone (<see cref="Outlet.Detach" />). Null (default): for as long as
    ///     the outlet lives — with Tapline.BlazorRealtime, as long as Blazor keeps the circuit
    ///     (<c>CircuitOptions.DisconnectedCircuitRetentionPeriod</c>, 3 minutes by Blazor's default: shorten it there to
    ///     shorten both). Set, the subscriptions end sooner (<see cref="FaultCode.Stalled" />, told in one record when it's
    ///     back): their providers released — a disconnected client's subscriptions keep them producing — while the circuit
    ///     itself may live on.
    /// </summary>
    public TimeSpan? DisconnectedHold { get; set; }

    /// <summary>
    ///     Advertised address of this node, returned in every <see cref="TapBinding" />. For clients that send step 2 as a
    ///     separate request (e.g. via load-balanced or multi-value DNS) and must reach the node holding the binding.
    ///     Clients on a Blazor circuit ignore it: their activation rides the connection that already reaches this node.
    /// </summary>
    public string? Endpoint { get; set; }
}

/// <summary>
///     The answer to step 1 of a two-phase subscribe: what the caller needs for step 2. <see cref="Topic" /> may be shown
///     to
///     the caller (activation is authorised by the outlet, not the topic). <see cref="Endpoint" /> is optional and only
///     meaningful to clients that activate over a separate request. For a subscription through a data provider:
///     <see cref="Service" />, the service's name — what a service-up notice names; <see cref="NodeId" />, the router's
///     transient id for the provider instance holding it (a node) — what its health notices name, nothing of the instance
///     itself (<see cref="Wire.Service" />); and <see cref="Partition" />, if the provider accepted it into one of the
///     service's partitions: the partition's path as ids of the router's run, segment by segment
///     (<see cref="Publisher.PartitionId" />) — never its names; health told for a scope covers the subscriptions at or
///     under it.
///     <para>
///         <see cref="Stale" />: its stale-data policy as the client may apply it itself, while the router is out of its
///         reach —
///         null if there's nothing it could apply (no policy, or a function only the router can run).
///     </para>
/// </summary>
public readonly record struct TapBinding(
    uint TapId,
    string? Topic,
    string? Endpoint,
    string? Service = null,
    Guid? NodeId = null,
    string? Partition = null,
    StaleBinding? Stale = null);

/// <summary>
///     A subscription's stale-data policy, for its client to apply itself — only while the router is out of its reach (the
///     router applies it otherwise, on the state it holds: the authority). <see cref="Kind" />; for
///     <see cref="StaleData.Static" />
///     the state; for <see cref="StaleData.Function" /> the id of its module (<see cref="IPortableDegradeFunction" />), to
///     fetch
///     from the router while it is reachable.
/// </summary>
public sealed record StaleBinding(StaleData Kind, ReadOnlyMemory<byte> State = default, string? Module = null)
{
    internal static StaleBinding? Of(ErrorPolicy? policy)
    {
        return policy switch
        {
            null => null,
            { Kind: StaleData.Static } => new StaleBinding(StaleData.Static, policy.State),
            { Kind: StaleData.ReplayLast } => new StaleBinding(StaleData.ReplayLast),
            { Degrade: IPortableDegradeFunction portable } => new StaleBinding(StaleData.Function,
                Module: portable.ModuleId),
            _ => null // a function only the router can run, or none
        };
    }
}

internal static class TapState
{
    internal const int Bound = 0, Active = 1, Closed = 2;
}

/// <summary>
///     An outlet's position on one source. Two-phase: a tap is <em>bound</em> (its <see cref="Id" /> handed to the client,
///     nothing streamed) until the client — having registered its handler — activates it; the first record is then a
///     snapshot. The id tags the source's records on the wire. Dispose to close.
///     <para>
///         If the source faults, the tap sends its <see cref="ErrorPolicy" /> (degraded snapshot, then an Error record)
///         and
///         closes; <see cref="Completion" /> then yields the fault, and the client resubscribes with a new tap.
///     </para>
/// </summary>
public sealed class Tap : IDisposable
{
    private readonly TaskCompletionSource<StreamFault?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly StaleBinding? _stale;
    internal readonly long BoundAt = Stopwatch.GetTimestamp();

    internal readonly InitialState Initial;
    internal readonly ReadyNode Node;
    internal long ActivatedAt;
    internal int AwaitingFetch; // sent a transparent out-of-band record; waits for Tap.Fetched

    internal int
        AwaitingInitial; // its topic held till the provider's initial state is complete (InitialState.Provider)

    internal long AwaitingSince;
    internal int BlockedIn; // frame stamp in which its record last didn't fit
    internal long Charged; // bytes charged to its outlet's budget for it (released when it closes)
    internal Cursor Cursor = Cursor.Snapshot;
    internal uint FetchTicket; // ticket of the transparent handle being waited for
    internal Flight? Flight; // what it sent in frames not yet acknowledged (made at its first record)
    internal Tap? HeldBy; // its next message waits on this tap's state (an ordering constraint)
    internal MessageLog? History; // its last records, for the viewer (the builder writes it)

    internal int
        HistoryRing; // the bytes of its history's ring of whole records, taken from its outlet's history budget

    internal MailRing? Mail; // its mailbox's messages (made at the first post)

    internal long
        MailAfterOwn; // its mailbox's next entry waits on its own state at this version (0: none) — the builder's

    internal SubscribeRequest? Origin; // the subscription that made this route, through a provider
    internal bool Oversize;
    internal PendingSnapshot? Pending; // the provider's initial snapshot, through the subscriber's mailbox
    internal int Queued; // in the outlet's ready queue (inbox or FIFO)

    internal long
        RecordedAt = -1,
        RecordedUpstream =
            MessageLog.None; // its last state record's version, and the provider's for it (the builder's)

    internal int Resuming; // reconnected: held till its client says what it received (Outlet.Resume)
    internal long ResumingSince;
    internal int ResyncRequested;
    internal long RetryAt; // timestamp: not sendable until the outlet's retry timer releases it
    internal long ThrottledUntil; // timestamp: its state not sent again before then (its MinInterval); 0: free
    private long _minInterval; // its MinInterval, in its outlet's timestamp units (0: none)
    internal PendingSnapshot? SentPending; // …sent: again, should a reconnect lose it before the client got it

    internal long
        SkippedThrough; // a topic's messages it skipped (OverflowPolicy.Skip): the last of them — the builder's

    internal int Stale; // its provider degraded: its stale data sent, its stream held till it recovers
    internal Cursor Start; // its cursor at step 1: where it starts
    internal int State;

    internal Tap(Outlet outlet, Source source, uint id, string? topic, ErrorPolicy? errorPolicy, Lane? lane,
        InitialState initial = InitialState.Snapshot)
    {
        Outlet = outlet;
        Source = source;
        Id = id;
        Topic = topic;
        ErrorPolicy = errorPolicy;
        Lane = lane;
        Initial = initial;
        Node = new ReadyNode(this);
        _stale = StaleBinding.Of(errorPolicy);
    }

    /// <summary>Its policy's portable function, if it has one (served to its client while it is bound).</summary>
    internal IPortableDegradeFunction? Portable =>
        ErrorPolicy is { Kind: StaleData.Function, Degrade: IPortableDegradeFunction p } ? p : null;

    public uint Id { get; }
    public Source Source { get; }
    public string? Topic { get; }
    public ErrorPolicy? ErrorPolicy { get; }

    /// <summary>The lane whose scheduling class serves this tap, or null for Streaming (see <see cref="Outlet.Tap" />).</summary>
    public Lane? Lane { get; }

    internal Outlet Outlet { get; }

    /// <summary>
    ///     Completes when the tap closes: with the fault it reported to its client (source fault, or the client rejecting
    ///     the stream), or null for a normal close (dispose, client unsubscribe, bind timeout, outlet disposed).
    /// </summary>
    public Task<StreamFault?> Completion => _completion.Task;

    /// <summary>What to hand back to the caller after step 1.</summary>
    public TapBinding Binding =>
        new(Id, Topic, Outlet.Endpoint, Origin?.Service, Origin?.NodeId, Origin?.PartitionId, _stale);

    public bool IsActive => Volatile.Read(ref State) == TapState.Active;
    public bool IsClosed => Volatile.Read(ref State) == TapState.Closed;

    /// <summary>
    ///     The data provider instance holding this route (its step 2 and end are relayed there), or null for a tap made
    ///     directly. The router's: not in <see cref="Binding" />.
    /// </summary>
    public ProviderInstance? Provider => Origin?.AcceptedBy;


    /// <summary>Its client is away: its connection gone, or back but not yet resumed.</summary>
    internal bool IsAway => Outlet.IsAway || Volatile.Read(ref Resuming) != 0;

    internal bool HasWork => Volatile.Read(ref State) == TapState.Active && Volatile.Read(ref Resuming) == 0 &&
                             Volatile.Read(ref HeldBy) is null
                             && (Source.Fault is not null || Outlet.FaultInjected
                                                          || (Volatile.Read(ref Stale) == 0
                                                              && (Volatile.Read(ref ResyncRequested) != 0 ||
                                                                  Volatile.Read(ref Pending) is not null
                                                                  || (Volatile.Read(ref Mail) is { } mail &&
                                                                      mail.HasNext &&
                                                                      Volatile.Read(ref AwaitingFetch) ==
                                                                      0 && // (in order after a record being fetched)
                                                                      Volatile.Read(ref MailAfterOwn) is var own &&
                                                                      (own == 0 || HasReached(own)))
                                                                  || (!Oversize &&
                                                                      Volatile.Read(ref AwaitingFetch) == 0 &&
                                                                      Volatile.Read(ref RetryAt) == 0 &&
                                                                      Volatile.Read(ref ThrottledUntil) == 0 &&
                                                                      Volatile.Read(ref AwaitingInitial) == 0
                                                                      && Source.Version !=
                                                                      Volatile.Read(ref Cursor.Seq)))));

    /// <summary>Its state (not its mailbox) has a record to send now.</summary>
    internal bool HasStateWork => Volatile.Read(ref State) == TapState.Active && Volatile.Read(ref Resuming) == 0 &&
                                  Volatile.Read(ref Stale) == 0
                                  && (Volatile.Read(ref ResyncRequested) != 0 || Volatile.Read(ref Pending) is not null
                                                                              || (!Oversize &&
                                                                                  Volatile.Read(ref AwaitingFetch) ==
                                                                                  0 && Volatile.Read(ref RetryAt) ==
                                                                                  0 && Volatile.Read(ref ThrottledUntil) ==
                                                                                  0 && Volatile.Read(
                                                                                      ref AwaitingInitial) == 0
                                                                                  && Source.Version !=
                                                                                  Volatile.Read(ref Cursor.Seq)));

    public void Dispose()
    {
        Outlet.Close(this);
    }

    /// <summary>Server-side activation (when the client doesn't drive the second step itself).</summary>
    public void Activate()
    {
        Outlet.Activate(this);
    }

    /// <summary>
    ///     Posts a message to this subscription's mailbox — its subscriber's alone, in order with its other records (a
    ///     message record, flagged mailbox). False if the mailbox is full of messages not yet sent (or the message is larger
    ///     than it): the subscriber isn't keeping up. See <see cref="OutletOptions.MailboxMessages" />.
    ///     <paramref name="after" />:
    ///     the subscriber gets it only once it has that state (<see cref="Tapline.After" />: a source, or a tap of the same
    ///     client).
    /// </summary>
    public bool TryPost(ReadOnlySpan<byte> message, After after = default)
    {
        return Outlet.Post(this, message, after);
    }

    /// <summary>
    ///     A rendezvous with this subscription's client: <paramref name="correlation" /> — the requester's id for a change it
    ///     asked for, at most <see cref="Wire.MaxCorrelation" /> bytes — handed to it strictly after it has this
    ///     subscription's
    ///     state at <paramref name="seq" /> or past it (the change's: a value's version or a topic message's number,
    ///     <see cref="PublishResult.Seq" />; a table update's, <c>Complete()</c>) — applied, an out-of-band record fetched. A
    ///     message topic's message it was never sent (published before it subscribed, or skipped) is said so
    ///     (<see cref="Wire.FlagMissed" />). Through its mailbox, in order with what's posted there: false if that is full of
    ///     entries not yet sent, or the subscription has ended.
    /// </summary>
    public bool TryRendezvous(ReadOnlySpan<byte> correlation, long seq)
    {
        return Outlet.Rendezvous(this, correlation, new After(this, seq));
    }

    /// <summary>
    ///     As <see cref="TryRendezvous(ReadOnlySpan{byte}, long)" />, after another state: a source at a version (each of this
    ///     client's subscriptions to it; none: <see cref="Wire.FlagMissed" />), or another of this client's subscriptions.
    /// </summary>
    public bool TryRendezvous(ReadOnlySpan<byte> correlation, After after)
    {
        return Outlet.Rendezvous(this, correlation, after);
    }

    /// <summary>
    ///     Its client has its state at <paramref name="version" /> or past it: sent, applied (nothing out of band unfetched,
    ///     no
    ///     provider's snapshot or initial state outstanding), not set back by a reconnect not yet resumed.
    /// </summary>
    internal bool HasReached(long version)
    {
        return Volatile.Read(ref Cursor.Seq) >= version && Volatile.Read(ref Pending) is null
                                                        && Volatile.Read(ref AwaitingFetch) == 0 &&
                                                        Volatile.Read(ref Resuming) == 0 &&
                                                        Volatile.Read(ref AwaitingInitial) == 0;
    }

    /// <summary>
    ///     Whether a topic's message <paramref name="seq" /> never reached its client: before it subscribed, or skipped.
    ///     (State never is: a later one includes it.)
    /// </summary>
    internal bool Missed(long seq)
    {
        return Source.RecordType == Wire.TopicMessage && (seq <= Start.Seq || seq <= SkippedThrough);
    }

    /// <summary>
    ///     Clean→dirty: joins the back of the outlet's ready queue and wakes it. Already queued: nothing (the change
    ///     coalesces, the tap keeps its age). Not sendable (inactive, waiting for a fetch, oversize): nothing either — the
    ///     event that makes it sendable marks it again. Callers change the tap's state with a full fence first; the consumer
    ///     clears <see cref="Queued" /> with a full fence before re-reading state (Dekker), so no change is missed.
    /// </summary>
    /// <summary>
    ///     The least time between its state records (0, the default: none): sent one, it isn't sent another before this has
    ///     passed — then the state as it stands, whatever changed meanwhile conflated into it (nothing changed: nothing
    ///     sent). Its mailbox's messages aren't held, nor is a message topic's stream (nothing of it conflates). A
    ///     subscriber's own rate, whatever others of its topic take; set any
    ///     time. <see cref="Hurry" /> lets one through at once (a client waiting for its own change), as does
    ///     <see cref="Source.Barrier" /> for every subscription of a source.
    /// </summary>
    public TimeSpan MinInterval
    {
        get => TimeSpan.FromSeconds((double)Volatile.Read(ref _minInterval) / Outlet.TimestampFrequency);
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            Volatile.Write(ref _minInterval, (long)(value.TotalSeconds * Outlet.TimestampFrequency));
            if (value == TimeSpan.Zero) Hurry();
        }
    }

    internal long MinIntervalTicks => Volatile.Read(ref _minInterval);

    /// <summary>Its state sent at once, its <see cref="MinInterval" /> notwithstanding (it applies again after).</summary>
    public void Hurry()
    {
        if (Interlocked.Exchange(ref ThrottledUntil, 0) != 0) MarkDirty();
    }

    internal void MarkDirty()
    {
        if (!HasWork) return;
        if (Volatile.Read(ref Queued) == 0 && Interlocked.CompareExchange(ref Queued, 1, 0) == 0)
            Outlet.Enqueue(this);
        else if (Outlet.HoldingState && CarriesMessages)
            Outlet.MessageWaiting(); // queued already, its state waiting: its messages still go
    }

    /// <summary>
    ///     Its next record may be a message: a message topic's, or its mailbox's. While its client's acknowledgement is
    ///     overdue these go and state waits (<see cref="OutletOptions.AckOverdue" />).
    /// </summary>
    internal bool CarriesMessages => Source is MessageTopic || Volatile.Read(ref Mail) is { HasNext: true };

    internal void Complete(StreamFault? fault)
    {
        _completion.TrySetResult(fault);
    }
}

/// <summary>
///     One per client. Owns the client's outbound scheduling: heartbeat first, then deficit round robin (with overdraft,
///     turn state kept across frames) over four classes of taps — Streaming (by default: oldest-dirty-first, optionally
///     rate-limited per client) and the High/Normal/Low classes a tap may be bound into instead (<see cref="Lane" />).
///     Everything it sends is a subscription's: there are no unsolicited messages — an alert, an event, a notice is a
///     message topic (<see cref="MessageTopic" />) its client subscribed to, or a message to one subscription's mailbox
///     (<see cref="Tap.TryPost" />).
///     Transport-agnostic: an adapter attaches it to a connection, waits for <see cref="IsReady" />, and pulls frames with
///     <see cref="TryBuildFrame(object, bool, out ReadOnlySpan{byte}, out long)" /> as late as it can.
/// </summary>
public sealed class Outlet : IDisposable, IWheelTarget
{
    private const int High = 0, Normal = 1, Streaming = 2, Low = 3; // scheduling classes

    private const int OwnBuffer = 0, LargeBuffer = 1;
    private const int EndedKept = 64;
    private const int NoticesPerFrame = 4;
    private const long Never = long.MinValue / 4;

    // ───────────────────────────── the budget ─────────────────────────────

    /// <summary>An estimate of a tap's own objects (the tap, its queue node, its completion), beside its history ring.</summary>
    private const int TapBytes = 512;

    private const long HighActiveMs = 2000, MakeWayMs = 1000;
    private readonly BlobStore _blobs;
    private readonly FrameBuffer[] _buffers = new FrameBuffer[2];
    private readonly long[] _dataFrames; // ring: seqs of the last Window frames that carried data

    private readonly int[] _deficit = new int[4];
    private readonly Lock _endedLock = new();
    private readonly FlowControl? _flow; // null under FlowControlMode.Fixed
    private readonly FrameLog _frameLog = new(); // the last frames: built, and acked — for the viewer
    private readonly ReadyQueue[] _laneTaps = [new(), new(), new()]; // taps bound into the High/Normal/Low classes

    // Service notices: data provider instances this client had routes through, gone (Wire.Service) — each kept until a
    // frame carrying it is acked, and sent again after a reconnect if none was.
    private readonly Lock _noticeLock = new();
    private readonly OutletOptions _options;
    private readonly int[] _quantum = new int[4];
    private readonly ReadyQueue _ready = new(); // Streaming's taps

    private readonly OutletRegistry _registry;

    private readonly Signal _signal = new();
    private readonly long _slack; // the wheel's resolution, timestamp ticks
    private readonly Lock _tapLock = new();

    private readonly TimeProvider _time;
    private readonly WheelNode _timerNode; // its one place on the wheel: registered when it parks waiting on time
    private readonly int[] _weights;
    private readonly TimingWheel _wheel;
    private int _away; // its transport's connection ended (Detach), till the next Attach
    private long _awaySince;
    private TokenBucket _bucket;
    private int _building;
    private long _builtAt; // the frame being built: when (Stopwatch ticks)…
    private int _builtRecords; // …and its subscriptions' records so far
    private bool _canGrow, _wantLarge; // the frame being built could be larger; a first record wants it
    private long _charged; // bytes its subscriptions hold (OutletOptions.MemoryBudget)
    private bool _clockedFrame; // the frame just built was a regular (cadenced) data frame
    private bool _closing; // disposed (under _tapLock): routes no longer bind
    private long _confirmed; // the last frame the client said it received (its acks; not a reconnect's reset)
    private int _dataNext;
    private int _disposed;
    private (uint Id, FaultCode Code)[]? _ended; // the last taps the router ended here, and why (made at the first)
    private int _endedNext;
    private bool _frameFull; // …and filled its budget with more to come
    private int _frameHeartbeat; // the heartbeat's bytes in the frame being built (0: none)
    private long _framePeriod; // frame cadence, timestamp ticks (0 = none)

    private long _frameSeq, _acked;
    private int _frameStamp; // per built frame: taps blocked in this frame are not re-examined

    private RouterHealth?
        _healthSent,
        _healthKnown; // the router's state behind the last heartbeat; behind the last one echoed (its client has it)

    private int _heartbeatDue, _heartbeatEchoed = 1; // echoed: none in flight, so the next may go
    private long _heartbeatSentAt, _rttTicks;
    private uint _heartbeatSeq;
    private int _held; // taps whose next message an ordering constraint holds
    private long _highSentAt = Never, _othersSentAt = Never; // when High, and the other classes, last sent a record

    private bool
        _highServed, _othersServed, _madeWay; // in the frame being built: who sent; the others made way for High

    // In the frame being built: its client's acknowledgement overdue (OutletOptions.AckOverdue) — state waits, messages go —
    // and whether a message or mailbox record went.
    private bool _holdingState, _messageServed;

    private int _holding; // state waits now (see Holding): its producers say when a message joins a queued tap
    private int _messageWork; // a message or mailbox record may be waiting: what wakes it while state waits
    private long _stateHolds; // times state waited for an overdue acknowledgement

    private long _historyHeld; // what its subscriptions' rings of whole records take from the history budget
    private int _incompressible; // bytes of the frame being built that aren't worth compressing
    private FaultInjector? _injector; // test connections only
    private int _maxFramesPerSecond;
    private long _nextFrameAt;
    private long _nextRetryAt; // the earliest retry due (0: none) — its taps sendable again then
    private uint _nextTapId; // under _tapLock
    private List<(ServiceNotice Notice, long SentIn)>? _notices;
    private int _noticesUnsent;
    private int _oobCharge; // out-of-band bytes of the record just served (rate limit)
    private int _outBuffer = -1; // the buffer the last frame went out in, until released
    private object? _owner;
    private int _parked;

    private long
        _progressAt = Stopwatch.GetTimestamp(); // the client's last progress: an ack, a heartbeat's echo, a connection

    private int _quantumBudget; // the frame budget the quanta were sized for
    private RateLimit? _rateRequested, _rateApplied;
    private OutletSession? _session;
    private ClientIdentity? _identity; // who its client is (null: anonymous)
    private readonly Lock _sessionLock = new(); // its identity's changes, its expiry's timer
    private SessionPolicy _sessionPolicy = SessionPolicy.Default;
    private ITimer? _sessionTimer;
    private (Guid Session, DateTimeOffset? At) _warned; // the session and expiry its client was last warned of (once each)
    private int _stalls;
    private long _streamEligibleAt; // timestamp; 0 = eligible
    private Tap[] _taps = [];
    private int _turn;
    private bool _turnStarted;
    private long _waitingSince; // since when there has been something for it (0: nothing)

    public Outlet(OutletRegistry registry, OutletOptions options)
    {
        if (options.Window < 1) throw new ArgumentOutOfRangeException(nameof(options), "Window must be >= 1.");
        if (options.FrameBudget < 256 || options.MaxFrame < options.FrameBudget ||
            options.MaxFrame < ushort.MaxValue + Wire.ValueHeader + Wire.MaxHeartbeatSize)
            throw new ArgumentOutOfRangeException(nameof(options),
                "Need 256 <= FrameBudget <= MaxFrame, and MaxFrame large enough for a 64 KB value.");
        int[] weights = [options.WeightHigh, options.WeightNormal, options.WeightStreaming, options.WeightLow];
        if (weights.Any(w => w < 1)) throw new ArgumentOutOfRangeException(nameof(options), "Weights must be >= 1.");

        _registry = registry;
        _options = options;
        TapCharge = TapBytes + MessageLog.BytesFor(options.MessageHistory);
        _weights = weights;
        SizeQuanta(options.FrameBudget);
        _dataFrames = new long[options.Window];
        _wheel = registry.Wheel;
        _blobs = registry.Blobs;
        _time = _wheel.TimeProvider; // one clock for budgets and wake-ups
        _timerNode = new WheelNode(this);
        _slack = Math.Max(1, (long)(_wheel.Tick.TotalSeconds * _time.TimestampFrequency));
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxFramesPerSecond);
        _maxFramesPerSecond = options.MaxFramesPerSecond;
        _framePeriod = options.MaxFramesPerSecond > 0 ? _time.TimestampFrequency / options.MaxFramesPerSecond : 0;
        if (options.QueueDelayTarget < TimeSpan.Zero || options.InitialInflightBytes < 0 ||
            options.AckOverdue < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options),
                "QueueDelayTarget, InitialInflightBytes and AckOverdue can't be negative.");
        if (options.FlowControl == FlowControlMode.Adaptive)
            _flow = new FlowControl(options, _time.TimestampFrequency, _slack);
        if (options.OutOfBandThreshold is > 0 and < 64)
            throw new ArgumentOutOfRangeException(nameof(options),
                "OutOfBandThreshold is 0 (off) or at least 64 bytes.");
        if (options.StreamingBytesPerSecond > 0)
            SetStreamingRate(options.StreamingBytesPerSecond, options.StreamingBurstBytes);
        (Id, Nonce, Serial) = registry.Register(this);
    }

    /// <summary>Its clock's timestamp units a second (its taps' intervals are kept in them).</summary>
    internal long TimestampFrequency => _time.TimestampFrequency;

    /// <summary>The fault injector this outlet joined (a test connection), or null.</summary>
    public FaultInjector? FaultInjector => Volatile.Read(ref _injector);

    internal bool FaultInjected => Volatile.Read(ref _injector)?.Fault is not null;

    /// <summary>The frame cadence in force: data frames per second at most (0 = uncapped).</summary>
    public int MaxFramesPerSecond => Volatile.Read(ref _maxFramesPerSecond);

    /// <summary>The Streaming cap requested for this client, in bytes per second (0 = uncapped).</summary>
    public long StreamingBytesPerSecond => Volatile.Read(ref _rateRequested)?.BytesPerSecond ?? 0;

    public int Id { get; }

    /// <summary>
    ///     Its number in this process, never reused (its <see cref="Id" /> is a slot, reused once it's gone): what the
    ///     viewer names it by.
    /// </summary>
    public long Serial { get; }

    /// <summary>When this outlet — its client's session on a transport (a Blazor circuit, say) — was created.</summary>
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>Its client's current connection, as its transport describes it (for the viewer); null until one is set.</summary>
    public OutletSession? Session
    {
        get => Volatile.Read(ref _session);
        set => Volatile.Write(ref _session, value);
    }

    /// <summary>
    ///     Who its client is (null: anonymous): one user of one realm, as an authentication module asserted it — set by
    ///     <see cref="SetIdentity" />. Its subscriptions through providers carry it to services of its realm
    ///     (<see cref="SubscribeRequest.Identity" />).
    /// </summary>
    public ClientIdentity? Identity => Volatile.Read(ref _identity);

    /// <summary>
    ///     Sets who its client is (null: anonymous), and how its session may be renewed (<paramref name="policy" />; null:
    ///     <see cref="SessionPolicy.Default" />). A sign-in from anonymous changes nothing else: its subscriptions carry on.
    ///     A renewal — the same user of the same realm, its expiry moved — neither: the providers shown it are told
    ///     (<see cref="IDataProvider.IdentityChangedAsync" />). Another user, or none (signed out): the subscriptions made
    ///     under the old identity — those whose providers were shown it — end (<see cref="FaultCode.IdentityChanged" />),
    ///     their providers told as for any end, its client told in an <see cref="Wire.Ended" /> record each; those that
    ///     weren't shown it carry on. An identity that expires is watched: its client warned ahead
    ///     (<see cref="SessionPolicy.WarnAt" />: a <see cref="Wire.Session" /> record), and at its expiry it lapses
    ///     (<see cref="FaultCode.SessionExpired" />).
    /// </summary>
    public void SetIdentity(ClientIdentity? identity, SessionPolicy? policy = null)
    {
        ClientIdentity? old;
        lock (_sessionLock)
        {
            old = Interlocked.Exchange(ref _identity, identity);
            Volatile.Write(ref _sessionPolicy, policy ?? SessionPolicy.Default);
            Schedule(identity, _time.GetUtcNow());
        }

        if (old is null) return;
        if (identity is not null && identity.SameUser(old))
        {
            if (old.SessionId != identity.SessionId || old.ExpiresAt != identity.ExpiresAt)
                foreach (var t in Volatile.Read(ref _taps))
                    if (t.Origin is { Identity: { } shown } origin && shown.SameUser(old) && !t.IsClosed)
                        origin.IdentityChanged(t, identity); // renewed: its provider told
            return;
        }

        foreach (var t in Volatile.Read(ref _taps))
            if (t.Origin?.Identity is { } shown && shown.SameUser(old) && !t.IsClosed)
            {
                Close(t, new StreamFault(FaultCode.IdentityChanged, t.Source.Version,
                    identity is null ? "Signed out." : "Another user signed in."));
                Notify(ServiceNotice.Ended(FaultCode.IdentityChanged, t.Id, t.Id));
            }
    }

    /// <summary>How its session may be renewed, and when its client is warned (<see cref="SetIdentity" />).</summary>
    public SessionPolicy SessionPolicy => Volatile.Read(ref _sessionPolicy);

    /// <summary>The session's timer set for <paramref name="identity" />'s next moment: its warning, or its expiry (under the session lock).</summary>
    private void Schedule(ClientIdentity? identity, DateTimeOffset now)
    {
        if (identity?.ExpiresAt is not { } at || IsDisposed)
        {
            _sessionTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        var next = _warned == (identity.SessionId, at) ? at : _sessionPolicy.WarnAt(identity) ?? at;
        var due = next - now;
        if (due < TimeSpan.Zero) due = TimeSpan.Zero;
        if (due > TimeSpan.FromDays(1)) due = TimeSpan.FromDays(1); // (looked at again then: a timer's range is bounded)
        if (_sessionTimer is null)
            _sessionTimer = _time.CreateTimer(static o => ((Outlet)o!).CheckSession(), this, due,
                Timeout.InfiniteTimeSpan);
        else _sessionTimer.Change(due, Timeout.InfiniteTimeSpan);
    }

    private void CheckSession()
    {
        CheckSession(_time.GetUtcNow());
    }

    /// <summary>
    ///     Its session's moment, at <paramref name="now" />: its client warned once, ahead of its expiry (a
    ///     <see cref="Wire.Session" /> record: when, the renewal policy, why), or its identity lapsed — the subscriptions to
    ///     services that require a sign-in ended (<see cref="FaultCode.SessionExpired" />, their providers told as for any
    ///     end), the others' providers told it's anonymous now (<see cref="IDataProvider.IdentityChangedAsync" />), its
    ///     client told it ended.
    /// </summary>
    internal void CheckSession(DateTimeOffset now)
    {
        ClientIdentity? warn = null, lapsed = null;
        SessionPolicy policy;
        lock (_sessionLock)
        {
            var identity = Volatile.Read(ref _identity);
            policy = _sessionPolicy;
            if (identity?.ExpiresAt is not { } at || IsDisposed) return;
            if (now >= at)
            {
                lapsed = identity;
                Volatile.Write(ref _identity, null);
                Schedule(null, now);
            }
            else
            {
                if (now >= policy.WarnAt(identity) && _warned != (identity.SessionId, at))
                {
                    _warned = (identity.SessionId, at);
                    warn = identity;
                }

                Schedule(identity, now);
            }
        }

        if (warn is not null) Notify(ServiceNotice.OfRecords(SessionRecord.Of(SessionRecord.Expiring, policy, warn)));
        if (lapsed is null) return;
        foreach (var t in Volatile.Read(ref _taps))
            if (t.Origin is { Identity: { } shown } origin && shown.SameUser(lapsed) && !t.IsClosed)
            {
                if (origin.Access.Required)
                {
                    Close(t, new StreamFault(FaultCode.SessionExpired, t.Source.Version,
                        "The session lapsed: sign in again."));
                    Notify(ServiceNotice.Ended(FaultCode.SessionExpired, t.Id, t.Id));
                }
                else origin.IdentityChanged(t, null); // anonymous now: its provider told
            }

        Notify(ServiceNotice.OfRecords(SessionRecord.Of(SessionRecord.Ended, policy, lapsed)));
    }

    /// <summary>
    ///     Its transport's user, as its client connects (<paramref name="user" />; null: none): the host's
    ///     <see cref="OutletOptions.Identify" />, if set, says who it is — its identity set (<see cref="SetIdentity" />) —
    ///     or nothing, leaving it as it was. Called by the transports.
    /// </summary>
    public void IdentifyFrom(ClaimsPrincipal? user)
    {
        if (user is not null && _options.Identify?.Invoke(user) is { } identity) SetIdentity(identity);
    }

    /// <summary>Its client's sign-in messages, in order (made at its first: <see cref="Auth" />).</summary>
    internal System.Threading.Channels.Channel<byte[]>? AuthQueue;

    /// <summary>
    ///     A sign-in message from its client (<see cref="AuthMessages" />): relayed, in order, to the realm's authentication
    ///     provider (<see cref="Publisher.ProvideAuthentication" />); its answers come back as <see cref="Wire.Auth" />
    ///     records, and what the provider asserts is set as its identity. Called by the transports.
    /// </summary>
    public void Auth(ReadOnlySpan<byte> message)
    {
        if (message.Length > AuthMessages.MaxMessage)
            AuthAnswered(AuthRecord.Refused(message, "That sign-in message is too large."));
        else if (_registry.Authentication is { } router) router.Authenticate(this, message.ToArray());
        else AuthAnswered(AuthRecord.Refused(message, "No sign-in on this router."));
    }

    /// <summary>A sign-in's answer, to its client: in order with its other notices, kept until acknowledged.</summary>
    internal void AuthAnswered(byte[] record)
    {
        Notify(ServiceNotice.OfRecords(record));
    }

    /// <summary>This node's advertised endpoint (<see cref="OutletOptions.Endpoint" />), if any.</summary>
    public string? Endpoint => _options.Endpoint;

    /// <summary>Secret paired with <see cref="Id" /> so a client can only ack or resync its own outlet.</summary>
    public uint Nonce { get; }

    /// <summary>Round trip of the most recent heartbeat (server → client frame → client ack), including client processing.</summary>
    public TimeSpan Rtt => TimeSpan.FromTicks(Volatile.Read(ref _rttTicks));

    /// <summary>
    ///     Adaptive flow control: the path's minimum round trip, frame bound → acked (zero before the first ack, or under
    ///     Fixed).
    /// </summary>
    public TimeSpan MinRtt => Elapsed(_flow?.MinRtt ?? 0);

    /// <summary>
    ///     Adaptive flow control: the smoothed round trip (its excess over <see cref="MinRtt" /> is the queue on the
    ///     path).
    /// </summary>
    public TimeSpan SmoothedRtt => Elapsed(_flow?.SmoothedRtt ?? 0);

    /// <summary>Adaptive flow control: the delivery rate, bytes per second (windowed max over the last ten round trips).</summary>
    public long DeliveryRate => _flow?.DeliveryRate ?? 0;

    /// <summary>Adaptive flow control: bytes in flight (built, not yet acked) and the limit on them.</summary>
    public long InflightBytes => _flow?.Inflight ?? 0;

    /// <inheritdoc cref="InflightBytes" />
    public long InflightLimit => _flow?.Limit ?? 0;

    /// <summary>Adaptive flow control: the pacing rate, bytes per second (0: unpaced).</summary>
    public long PacingRate => _flow?.PacingRate ?? 0;

    public IReadOnlyList<Tap> Taps => Volatile.Read(ref _taps);

    /// <summary>Streaming budget left (bytes) as of the last frame, when rate-limited; for diagnostics and tests.</summary>
    internal long StreamingTokens => _bucket.Tokens;

    /// <summary>What this client's subscriptions hold, in bytes, as charged to its <see cref="OutletOptions.MemoryBudget" />.</summary>
    public long MemoryBytes => Volatile.Read(ref _charged);

    /// <summary>Its <see cref="OutletOptions.MemoryBudget" /> (0: no limit).</summary>
    public long MemoryBudget => _options.MemoryBudget;

    /// <summary>Times it stalled (<see cref="OutletOptions.StallTimeout" />): its subscriptions ended.</summary>
    public int Stalls => Volatile.Read(ref _stalls);

    /// <summary>
    ///     For a subscription through a provider: its tap's charge, reserved before step 1 is asked of the provider
    ///     (false: no room).
    /// </summary>
    internal long TapCharge { get; }

    // ───────────────────────────── transport-facing ─────────────────────────────

    /// <summary>
    ///     Fewer than <see cref="OutletOptions.Window" /> data frames unacked (the oldest of the last Window has been acked)
    ///     and,
    ///     under adaptive flow control, room under its byte limit.
    /// </summary>
    private bool HasCredit => WindowOpen && (_flow is null || _flow.HasRoom);

    /// <summary>
    ///     Fewer than <see cref="OutletOptions.Window" /> data frames unacked. While state waits for an overdue
    ///     acknowledgement, all that holds messages back: what its client hasn't taken waits at its end, not on the path —
    ///     and a message held here only falls behind its topic's buffer.
    /// </summary>
    private bool WindowOpen => Volatile.Read(ref _dataFrames[Volatile.Read(ref _dataNext)]) <= Volatile.Read(ref _acked);

    /// <summary>
    ///     Data left after a frame was built (it didn't take everything there was: the frame was limited, not the
    ///     application).
    /// </summary>
    private bool HasPendingWork
    {
        get
        {
            foreach (var q in _laneTaps)
                if (Waiting(q))
                    return true;
            return Waiting(_ready);
        }
    }

    private bool HeartbeatReady => Volatile.Read(ref _heartbeatDue) != 0 && Volatile.Read(ref _heartbeatEchoed) != 0;

    /// <summary>
    ///     True when a frame would carry something (a due heartbeat whose predecessor was echoed, or work with credit
    ///     available — for Streaming, also within the rate limit; the timing wheel wakes the outlet when the budget
    ///     recovers). O(1).
    /// </summary>
    public bool IsReady
    {
        get
        {
            if (HeartbeatReady || Volatile.Read(ref _noticesUnsent) != 0)
                return true; // control records: no credit needed
            var now = _time.GetTimestamp();
            ReleaseDueRetries(now);
            var holding = Holding(now); // state waits; messages go, past the byte limit (OutletOptions.AckOverdue)
            if (!holding && _flow is { } flow && !flow.HasRoom)
            {
                if (HasPendingWork) flow.MarkLimited(); // ready data held back by the path's limit, not the app
                return false; // an ack wakes it
            }

            if (holding ? !WindowOpen : !HasCredit) return false;
            if (!holding && Waiting(_laneTaps[High])) return true; // High isn't held to the frame cadence, nor pacing
            if (_flow is { } paced && !paced.PaceAllows(now))
            {
                if (HasPendingWork && !holding) paced.MarkLimited();
                return false; // its park registers the pace time
            }

            var due = FrameDue(now);
            if (!due) return false; // its park registers the frame's time
            if (MakingWay(now)) return false; // room left for High: an ack wakes it
            if (holding) // only a message or mailbox record may go: one may be waiting (its producer said so), within budget
                return Volatile.Read(ref _messageWork) != 0 &&
                       (Volatile.Read(ref _streamEligibleAt) is var eligible && (eligible == 0 || now >= eligible));
            foreach (var q in _laneTaps)
                if (Waiting(q))
                    return true; // lane classes: outside the Streaming rate limit
            if (!Waiting(_ready)) return false;
            if (!ReferenceEquals(Volatile.Read(ref _rateRequested), Volatile.Read(ref _rateApplied)))
                return true; // a new rate applies at the next frame
            var at = Volatile.Read(ref _streamEligibleAt);
            return at == 0 || _time.GetTimestamp() >= at;
        }
    }

    /// <summary>Its client is away: its transport's connection ended (<see cref="Detach" />), and none has attached since.</summary>
    public bool IsAway => Volatile.Read(ref _away) != 0;

    /// <summary>
    ///     The outlet's own frame buffer: a heartbeat and the frame budget, or a 64 KB value (at most
    ///     <see cref="OutletOptions.MaxFrame" />).
    /// </summary>
    private int SmallFrameBytes => Math.Min(_options.MaxFrame,
        Wire.MaxHeartbeatSize + Math.Max(_options.FrameBudget, Wire.ValueHeader + ushort.MaxValue));

    /// <summary>The outlet's own frame buffer's size (0: none taken yet); for tests.</summary>
    internal int FrameBufferBytes => _buffers[OwnBuffer].Buffer?.Length ?? 0;

    /// <summary>A frame buffer rented for a large frame is held (until the next build, or its frame's release); for tests.</summary>
    internal bool HoldsLargeFrame => _buffers[LargeBuffer].Buffer is not null;

    /// <summary>What its subscriptions keep of whole records now (<see cref="OutletOptions.HistoryBudget" />).</summary>
    public long HistoryHeld => Volatile.Read(ref _historyHeld);

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _closing);

    public void Dispose()
    {
        Tap[] taps;
        lock (_tapLock)
        {
            taps = _taps;
            _taps = [];
            _closing = true; // no route binds from now on
        }

        foreach (var t in taps)
        {
            Volatile.Write(ref t.State, TapState.Closed);
            Removed(t, t.Source.RemoveTap(t));
            if (t.Portable is { } portable) _registry.Modules.Release(portable);
            t.Mail?.Release();
            ReleaseAll(t);
            t.Complete(null);
        }

        while (Interlocked.Exchange(ref _building, 1) != 0) Thread.SpinWait(1); // no build in progress…
        Volatile.Write(ref _disposed, 1); // …nor any after
        if (_buffers[OwnBuffer].Buffer is not null)
            Release(OwnBuffer); // the outlet's holds; a frame still out keeps its own
        if (_buffers[LargeBuffer].Buffer is not null) Release(LargeBuffer);
        Volatile.Write(ref _building, 0);
        _wheel.Cancel(_timerNode);
        _registry.Unregister(this);
        Volatile.Read(ref _injector)?.Leave(this);
        Interlocked.CompareExchange(ref AuthQueue, null, null)?.Writer.TryComplete(); // its relay ends (a full fence)
        lock (_sessionLock)
        {
            _sessionTimer?.Dispose();
            _sessionTimer = null;
        }

        _signal.Set();
    }

    /// <summary>The wheel's callback: O(1) — a wake; what is due the outlet's own thread sees to (<see cref="WaitAsync" />).</summary>
    void IWheelTarget.OnWheel()
    {
        Wake();
    }

    /// <summary>
    ///     Caps this client's Streaming bandwidth (e.g. by subscription tier); 0 removes the cap. Takes effect at the next
    ///     frame; the budget earned so far is kept (clamped to the new burst).
    /// </summary>
    public void SetStreamingRate(long bytesPerSecond, long burstBytes = 256 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytesPerSecond);
        ArgumentOutOfRangeException.ThrowIfLessThan(burstBytes, 1);
        Interlocked.Exchange(ref _rateRequested,
            bytesPerSecond == 0 ? null : new RateLimit(bytesPerSecond, burstBytes)); // full fence before Wake
        Wake();
    }

    /// <summary>
    ///     Makes this outlet a test connection on <paramref name="injector" />'s fault lifecycle: while it is failed, every
    ///     source looks faulted to this outlet only (see <see cref="Tapline.FaultInjector" />). Once; an outlet that never
    ///     joins is never affected.
    /// </summary>
    public void JoinFaultInjector(FaultInjector injector)
    {
        if (Interlocked.CompareExchange(ref _injector, injector, null) is not null)
            throw new InvalidOperationException("This outlet already joined a fault injector.");
        injector.Join(this);
        if (injector.Fault is not null) MarkAllDirty();
    }

    /// <summary>
    ///     How <paramref name="source" /> looks to this client: its own fault, else an injected one (test connections), else
    ///     null. Wait for null before resubscribing after a fault.
    /// </summary>
    public StreamFault? FaultOf(Source source)
    {
        return source.Fault ?? Injected(source);
    }

    private StreamFault? Injected(Source source)
    {
        return Volatile.Read(ref _injector)?.Fault is { } f ? f with { Version = source.Version } : null;
    }

    /// <summary>An injected fault began: every tap has work (its error policy).</summary>
    internal void MarkAllDirty()
    {
        foreach (var t in Volatile.Read(ref _taps)) t.MarkDirty();
    }

    /// <summary>
    ///     Changes the frame cadence (<see cref="OutletOptions.MaxFramesPerSecond" />) while the outlet runs — e.g. to the
    ///     client's display refresh, as it measures and reports it, or low while its page is hidden; 0 removes the cap. The
    ///     next data frame is due a new period after the last (at once if that has passed, or if there was no cap).
    /// </summary>
    public void SetMaxFramesPerSecond(int framesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(framesPerSecond);
        var period = framesPerSecond > 0 ? _time.TimestampFrequency / framesPerSecond : 0;
        while (Interlocked.Exchange(ref _building, 1) != 0) Thread.SpinWait(1);
        if (period != _framePeriod)
        {
            var next = _framePeriod > 0 && period > 0 ? _nextFrameAt - _framePeriod + period : 0; // the phase kept
            Volatile.Write(ref _nextFrameAt, next);
            Volatile.Write(ref _framePeriod, period); // (parked, it is woken below: it registers the new time itself)
        }

        Volatile.Write(ref _maxFramesPerSecond, framesPerSecond);
        Interlocked.Exchange(ref _building, 0); // full fence before Wake
        Wake();
    }

    private TimeSpan Elapsed(long ticks)
    {
        return TimeSpan.FromSeconds((double)ticks / _time.TimestampFrequency);
    }

    // ───────────────────────────── publishing API ─────────────────────────────

    /// <summary>
    ///     Step 1: binds a tap on a source and returns it without streaming. Hand <see cref="Tap.Binding" /> to the client;
    ///     it activates the tap (step 2) once its handler is registered, so no record can arrive before it can be handled.
    ///     The first record after activation is a snapshot (and again after a reconnect or resync). If the source faults,
    ///     the client gets <paramref name="errorPolicy" /> (default: an Error record with no payload) and the tap ends.
    /// </summary>
    /// <param name="lane">
    ///     Null (default): the tap is served in the Streaming class, with the client's other taps, oldest-dirty-first, within
    ///     the Streaming rate limit. A lane: in that class instead — its byte share (High 16 : Normal 4 : Streaming 4 : Low 1
    ///     when all are busy), with the class's other taps, outside the Streaming rate limit — for state (or messages: an
    ///     alerts topic) that must not wait behind bulk state: a tap on High goes before a busy Streaming class, and isn't
    ///     held to
    ///     <see cref="OutletOptions.MaxFramesPerSecond" />. Conflated as any tap (deltas, snapshots, faults).
    /// </param>
    /// <param name="initial">
    ///     What it starts with once activated (and after a reconnect): the source's state as it is (default), or, for a
    ///     whole-state source (a last value), nothing — its first record is the next change (a stream that resumes from its
    ///     next frame).
    /// </param>
    /// <exception cref="OutletFullException">The client's subscriptions hold its <see cref="OutletOptions.MemoryBudget" />.</exception>
    public Tap Tap(Source source, string? topic = null, ErrorPolicy? errorPolicy = null, bool activate = false,
        Lane? lane = null,
        InitialState initial = InitialState.Snapshot)
    {
        if (initial == InitialState.Provider)
            throw new ArgumentException(
                "Only a data provider sends a subscriber its own initial state (SubscribeRequest.Accept).",
                nameof(initial));
        var tap = Bind(source, topic, errorPolicy, lane, initial, out _);
        if (activate) Activate(tap);
        return tap;
    }

    /// <summary>
    ///     Step 1: a tap bound on the source (a route to the topic); <paramref name="epoch" />: the topic's, at this bind.
    ///     Charged to the budget — already, if <paramref name="reserved" /> (<see cref="TryReserveTap" />): the tap takes the
    ///     reservation over (a closed one, the outlet closing, releases it).
    /// </summary>
    internal Tap Bind(Source source, string? topic, ErrorPolicy? errorPolicy, Lane? lane, InitialState initial,
        out long epoch, SubscribeRequest? origin = null,
        bool reserved = false)
    {
        if (initial != InitialState.Snapshot &&
            !source.StartsAtNext) // (a reservation is its owner's to release: no tap took it)
            throw new ArgumentException(
                "Only a last value or a message topic can start without the library's snapshot: a table's deltas need its own.",
                nameof(initial));
        if (!reserved && !TryCharge(TapCharge)) throw Full(TapCharge);
        Tap tap;
        lock (_tapLock) // with the route, atomically: a dispose (or a stall) either takes it down or came first
        {
            tap = new Tap(this, source, ++_nextTapId, topic, errorPolicy, lane, initial)
                { Origin = origin }; // its id under the lock: a stall ends those up to an id
            if (_closing)
            {
                tap.State = TapState.Closed; // the subscriber is gone: nothing bound (the caller sees a closed tap)
                Uncharge(TapCharge);
                epoch = source.Epoch;
                return tap;
            }

            tap.Charged = TapCharge;
            if (initial != InitialState.Snapshot)
                tap.Cursor =
                    source.Head; // from the next change after step 1 (a message topic: always — set with its route)
            if (initial == InitialState.Provider)
                tap.AwaitingInitial = 1; // …held there till the provider's initial state
            _taps = [.. _taps, tap];
            epoch = source.AddTap(tap);
            tap.Start = tap.Cursor;
            if (tap.Portable is { } portable)
                _registry.Modules.Hold(portable); // its module served while it's bound (released after it's removed)
        }

        return tap;
    }

    internal bool TryReserveTap()
    {
        return TryCharge(TapCharge);
    }

    internal void UnreserveTap()
    {
        Uncharge(TapCharge);
    }

    internal OutletFullException Full(long wanted)
    {
        return new OutletFullException(Id, MemoryBytes, _options.MemoryBudget, wanted);
    }

    private bool TryCharge(long bytes)
    {
        var budget = _options.MemoryBudget;
        for (long held;;)
        {
            held = Volatile.Read(ref _charged);
            if (budget > 0 && held + bytes > budget) return false;
            if (Interlocked.CompareExchange(ref _charged, held + bytes, held) == held) return true;
        }
    }

    private void Uncharge(long bytes)
    {
        Interlocked.Add(ref _charged, -bytes);
    }

    /// <summary>Charges more to what a tap holds (within the budget unless <paramref name="force" />); false if over it.</summary>
    private bool Charge(Tap t, long bytes, bool force = false)
    {
        if (force) Interlocked.Add(ref _charged, bytes);
        else if (!TryCharge(bytes)) return false;
        Interlocked.Add(ref t.Charged, bytes);
        if (t.IsClosed) ReleaseAll(t); // closed meanwhile: its close may have released before this was added
        return true;
    }

    /// <summary>Releases part of what a tap holds — unless its close released it all already.</summary>
    private void Discharge(Tap t, long bytes)
    {
        for (long held; (held = Volatile.Read(ref t.Charged)) >= bytes && bytes > 0;)
            if (Interlocked.CompareExchange(ref t.Charged, held - bytes, held) == held)
            {
                Uncharge(bytes);
                return;
            }
    }

    private void ReleaseAll(Tap t)
    {
        var held = Interlocked.Exchange(ref t.Charged, 0);
        if (held != 0) Uncharge(held);
        GiveBackHistory(t); // (its ring stays readable: only its budget is given back)
    }

    /// <summary>The provider's snapshot for one subscriber (its mailbox): its first record, ahead of the topic's next change.</summary>
    internal void SendSnapshot(Tap tap, ReadOnlySpan<byte> snapshot, long? asOf = null)
    {
        Charge(tap, snapshot.Length, true); // counted (a subscription's start, admitted with it), not refused
        var after = asOf is { } version ? new Cursor { Seq = Math.Min(version, tap.Source.Version) } : tap.Source.Head;
        if (Interlocked.Exchange(ref tap.Pending, new PendingSnapshot(snapshot.ToArray(), after)) is
            { } replaced) // full fence before the queued check
            Discharge(tap, replaced.Bytes.Length);
        Interlocked.Exchange(ref tap.AwaitingInitial,
            0); // (the provider's initial state, if it was due: this — the topic goes on after it)
        tap.MarkDirty();
    }

    /// <summary>
    ///     The provider's initial state is complete (<see cref="Mailbox.Ready" />): the topic, held since step 1, goes on from
    ///     after <paramref name="through" /> (default: its last now). Under the build lock: the cursor is the builder's.
    /// </summary>
    internal void InitialReady(Tap t, long? through)
    {
        if (Volatile.Read(ref t.AwaitingInitial) == 0) return;
        while (Interlocked.Exchange(ref _building, 1) != 0) Thread.SpinWait(1);
        try
        {
            if (Volatile.Read(ref t.AwaitingInitial) == 0) return;
            var cut = t.Source.Head;
            if (through is { } at && at < cut.Seq) cut = new Cursor { Seq = Math.Max(0, at) };
            t.Cursor = cut;
            t.Start = cut;
            t.Flight?.Restart(cut); // nothing of the topic's went before: a resume goes on from the cut too
            Interlocked.Exchange(ref t.AwaitingInitial, 0); // full fence before the queued check
        }
        finally
        {
            Volatile.Write(ref _building, 0);
        }

        t.MarkDirty();
        ReleaseHolds();
    }

    /// <summary>
    ///     Step 1 of subscribing to a coherent set of objects: whole tables, key ranges of tables, last-value sources.
    ///     Each record the client gets is a consistent cut across all members — a <see cref="Transaction" /> is never seen
    ///     half-applied — and the client applies it all before notifying once. Activation, snapshots, resync, rate limits,
    ///     out-of-band delivery and faults work as for any tap; on a fault, members with a degrade function send their
    ///     degraded state (the policy's own degrade function is not used), then the Error record.
    /// </summary>
    public Tap Watch(string? topic, ErrorPolicy? errorPolicy, params ReadOnlySpan<WatchMember> members)
    {
        return Tap(new WatchGroup(members), topic, errorPolicy);
    }

    /// <inheritdoc cref="Watch(string?, ErrorPolicy?, ReadOnlySpan{WatchMember})" />
    public Tap Watch(string? topic, params ReadOnlySpan<WatchMember> members)
    {
        return Watch(topic, null, members);
    }

    /// <summary>
    ///     Step 2, from the client: activates a bound tap. Idempotent: re-sent for every subscription the client holds when
    ///     it binds again after a reconnect — so one the router no longer holds (ended while the client was away: its error
    ///     lost with the connection, or ended without one) is answered: an <see cref="Wire.Ended" /> record for it, saying why
    ///     if the router still remembers (the last 64 ended here), else <see cref="FaultCode.Gone" />. Ids never issued here
    ///     are ignored.
    /// </summary>
    public void Activate(uint tapId)
    {
        foreach (var t in Volatile.Read(ref _taps))
            if (t.Id == tapId)
            {
                Activate(t);
                return;
            }

        Tap? late = null;
        lock (_tapLock) // (a bind may have just added it)
        {
            if (tapId == 0 || tapId > _nextTapId) return;
            foreach (var t in _taps)
                if (t.Id == tapId)
                    late = t;
        }

        if (late is not null) Activate(late);
        else Notify(ServiceNotice.Ended(EndedWith(tapId), tapId, tapId));
    }

    /// <summary>Why the router ended tap <paramref name="tapId" /> here, if it still remembers.</summary>
    private FaultCode EndedWith(uint tapId)
    {
        lock (_endedLock)
        {
            if (_ended is { } ended)
                for (var i = 1; i <= Math.Min(_endedNext, EndedKept); i++)
                    if (ended[(_endedNext - i) & (EndedKept - 1)] is var (id, code) && id == tapId)
                        return code;
            return FaultCode.Gone;
        }
    }

    private void RememberEnded(uint tapId, FaultCode code)
    {
        lock (_endedLock)
        {
            _ended ??= new (uint, FaultCode)[EndedKept];
            _ended[_endedNext++ & (EndedKept - 1)] = (tapId, code);
        }
    }

    /// <summary>
    ///     From the client, binding again after a reconnect: the last frame it received (on the connection before) and the
    ///     last heartbeat it saw. An acknowledgement — and where every tap held since the reconnect resumes: from exactly what
    ///     the client got. A table goes on with a delta (a snapshot only if it can't), a value is sent only if it changed, a
    ///     message stream and a mailbox from the next message the client didn't get — none twice, none lost while still kept;
    ///     an out-of-band record it hadn't fetched goes again; a watch (and any source that can't be set back) a snapshot, if
    ///     the client missed something of it. Without it within <see cref="OutletOptions.BindTimeout" />, from what the client
    ///     acknowledged.
    /// </summary>
    public void Resume(long lastFrame, long heartbeatEcho)
    {
        Ack(lastFrame, heartbeatEcho);
        lastFrame = Math.Min(lastFrame, Volatile.Read(ref _frameSeq));
        while (Interlocked.Exchange(ref _building, 1) != 0)
            Thread.SpinWait(1); // not mid-frame: cursors are the builder's
        try
        {
            foreach (var t in Volatile.Read(ref _taps)) ResumeTap(t, lastFrame);
        }
        finally
        {
            Volatile.Write(ref _building, 0);
        }

        foreach (var t in Volatile.Read(ref _taps)) t.MarkDirty();
        ReleaseHolds();
    }

    /// <summary>
    ///     A tap held since a reconnect, from what its client received up to <paramref name="lastFrame" />. Under the
    ///     build lock.
    /// </summary>
    private void ResumeTap(Tap t, long lastFrame)
    {
        if (Volatile.Read(ref t.Resuming) == 0 || t.Flight is not { } flight) return;
        var awaiting =
            Interlocked.Exchange(ref t.AwaitingFetch, 0) != 0; // a fetch the reconnect abandoned: not received
        var (cursor, mail, received, all) = flight.ResumeAt(lastFrame, awaiting);
        if (t.Source.Rewindable) t.Cursor = cursor;
        else if (!all)
            t.Cursor = t.Initial == InitialState.None
                ? t.Source.Head
                : Cursor.Snapshot; // missed what it can't be set back to: a snapshot
        t.Mail?.ResumeAfter(mail);
        if (!received && t.SentPending is { } pending && Volatile.Read(ref t.Pending) is null)
        {
            Charge(t, pending.Bytes.Length, true);
            Volatile.Write(ref t.Pending, pending); // the provider's snapshot, lost with the connection: again
        }

        t.Oversize = false;
        Volatile.Write(ref t.MailAfterOwn, 0); // (its mailbox's next entry looks again)
        Interlocked.Exchange(ref t.Resuming, 0); // full fence before the caller's queued check
    }

    /// <summary>A message to a subscription's mailbox (<see cref="Tap.TryPost" />).</summary>
    internal bool Post(Tap tap, ReadOnlySpan<byte> message, in After after)
    {
        return Append(tap, message, after, MailRing.Message);
    }

    /// <summary>A rendezvous through a subscription's mailbox (<see cref="Tap.TryRendezvous(ReadOnlySpan{byte}, After)" />).</summary>
    internal bool Rendezvous(Tap tap, ReadOnlySpan<byte> correlation, in After after)
    {
        if (correlation.Length is 0 or > Wire.MaxCorrelation)
            throw new ArgumentOutOfRangeException(nameof(correlation),
                $"A correlation is 1 to {Wire.MaxCorrelation} bytes.");
        if (after.Target is null)
            throw new ArgumentException("A rendezvous is after a state: a source or a tap, at a version.",
                nameof(after));
        return Append(tap, correlation, after, MailRing.Rendezvous);
    }

    private bool Append(Tap tap, ReadOnlySpan<byte> entry, in After after, byte kind)
    {
        if (after.Tap is { } target && !ReferenceEquals(target.Outlet, this))
            throw new ArgumentException(
                "A mailbox entry is ordered after its own client's state: the constraint's tap belongs to another.",
                nameof(after));
        if (tap.IsClosed) return false;
        var mail = Volatile.Read(ref tap.Mail) ?? NewMailbox(tap);
        if (mail is null || !mail.TryAppend(entry, after, kind)) return false;
        tap.MarkDirty();
        return true;
    }

    /// <summary>A tap's mailbox, made at its first post — charged to the budget (null: no room).</summary>
    private MailRing? NewMailbox(Tap tap)
    {
        var bytes = MailRing.BytesFor(_options.MailboxMessages, _options.MailboxBytes);
        if (!Charge(tap, bytes)) return null;
        var made = new MailRing(_options.MailboxMessages, _options.MailboxBytes);
        if (Interlocked.CompareExchange(ref tap.Mail, made, null) is { } other)
        {
            Discharge(tap, bytes);
            return other;
        }

        return made;
    }

    internal void Activate(Tap tap)
    {
        if (Volatile.Read(ref tap.State) != TapState.Bound) return; // active already (a re-sent activation), or closed
        Volatile.Write(ref tap.ActivatedAt, Stopwatch.GetTimestamp());
        if (Interlocked.CompareExchange(ref tap.State, TapState.Active, TapState.Bound) != TapState.Bound)
            return; // full fence
        tap.MarkDirty();
        tap.Origin?.Activated(tap); // through a provider: step 2 relayed to the endpoint holding the route
    }

    /// <summary>
    ///     From the client: it no longer wants the tap — unsubscribed (<see cref="FaultCode.None" />) or rejected the stream
    ///     (e.g. a snapshot that failed verification, which a resync cannot fix). Unknown ids are ignored.
    /// </summary>
    public void Close(uint tapId, FaultCode code)
    {
        foreach (var t in Volatile.Read(ref _taps))
            if (t.Id == tapId)
            {
                Close(t,
                    code == FaultCode.None ? null : new StreamFault(code, t.Source.Version, "Rejected by the client."),
                    true);
                return;
            }
    }

    /// <summary>
    ///     Ends a tap: its route removed (its provider told), what it holds freed, its completion set — and, unless its
    ///     client asked, remembered (why), for when the client asks after it.
    /// </summary>
    internal void Close(Tap tap, StreamFault? fault = null, bool byClient = false)
    {
        Volatile.Write(ref tap.State, TapState.Closed);
        bool removed;
        lock (_tapLock)
        {
            var i = Array.IndexOf(_taps, tap);
            removed = i >= 0;
            if (removed) _taps = [.. _taps.AsSpan(0, i), .. _taps.AsSpan(i + 1)];
        }

        if (removed) Removed(tap, tap.Source.RemoveTap(tap));
        if (removed && tap.Portable is { } portable) _registry.Modules.Release(portable);
        if (removed && !byClient) RememberEnded(tap.Id, fault?.Code ?? FaultCode.None);
        tap.Mail?.Release();
        ReleaseAll(tap);
        tap.Complete(fault); // if still queued, the builder drops it when it gets there
        ReleaseHolds(); // a message waiting on its state waits no more
    }

    /// <summary>
    ///     Messages held by an ordering constraint may go — or wait again: each re-checks when it is served. On a fetch, a
    ///     close, a retry, a resume; every heartbeat too, as a backstop. Free while nothing is held.
    /// </summary>
    private void ReleaseHolds()
    {
        if (Volatile.Read(ref _held) == 0) return;
        foreach (var t in Volatile.Read(ref _taps))
            if (Volatile.Read(ref t.HeldBy) is not null && Interlocked.Exchange(ref t.HeldBy, null) is not null)
            {
                Interlocked.Decrement(ref _held);
                t.MarkDirty();
            }
    }

    /// <summary>
    ///     The builder's: <paramref name="t" />'s next entry waits on <paramref name="target" /> reaching
    ///     <paramref name="version" />
    ///     — fetching, retrying, resuming, degraded, awaiting its initial state, or the version not published yet. Released
    ///     when that changes (<see cref="ReleaseHolds" />: a fetch, a close, a retry, a resume, a recovery, an initial state,
    ///     a record of the target's).
    /// </summary>
    private void Hold(Tap t, Tap target, long version)
    {
        if (Interlocked.Exchange(ref t.HeldBy, target) is null)
            Interlocked.Increment(ref _held); // full fence before the re-check (Dekker with a release)
        if ((target.HasReached(version) || target.HasStateWork || target.IsClosed) &&
            Interlocked.Exchange(ref t.HeldBy, null) is not null)
            Interlocked.Decrement(ref _held); // it can move (or has) meanwhile: not held after all
    }

    /// <summary>
    ///     The builder's, after a record of <paramref name="target" />'s: entries it held may go — each re-checks. Free
    ///     while nothing is held.
    /// </summary>
    private void ReleaseHoldsBy(Tap target)
    {
        if (Volatile.Read(ref _held) == 0) return;
        foreach (var t in Volatile.Read(ref _taps))
            if (ReferenceEquals(Volatile.Read(ref t.HeldBy), target) &&
                Interlocked.CompareExchange(ref t.HeldBy, null, target) == target)
            {
                Interlocked.Decrement(ref _held);
                t.MarkDirty();
            }
    }

    /// <summary>A route removed: its provider, if it came through one, is told — after, and asynchronously (it may ignore it).</summary>
    private static void Removed(Tap tap, (long Epoch, int Remaining) left)
    {
        if (tap.Origin is { } origin) origin.Unsubscribed(tap, left.Epoch, left.Remaining);
    }

    internal void Enqueue(Tap tap)
    {
        ReadyOf(tap).Push(tap.Node);
        if (tap.CarriesMessages) Volatile.Write(ref _messageWork, 1); // (before Wake: its fence)
        Wake();
    }

    /// <summary>
    ///     State waits for its client's overdue acknowledgement (<see cref="OutletOptions.AckOverdue" />): its producers
    ///     then say when a message or mailbox record joins a tap already queued (<see cref="MessageWaiting" />). Otherwise
    ///     a read, nothing more.
    /// </summary>
    internal bool HoldingState => Volatile.Read(ref _holding) != 0;

    /// <summary>Times state waited for an overdue acknowledgement (<see cref="OutletOptions.AckOverdue" />).</summary>
    public long StateHolds => Volatile.Read(ref _stateHolds);

    /// <summary>A tap already queued has a message or mailbox record to send while state waits: that is what wakes it.</summary>
    internal void MessageWaiting()
    {
        Interlocked.Exchange(ref _messageWork, 1); // full fence before Wake
        Wake();
    }

    /// <summary>
    ///     Whether state waits now: its client's acknowledgement overdue. As a hold begins, a frame is looked for at once (a
    ///     message may have joined a queued tap just before its producer could see the hold); from then on, its producers
    ///     say.
    /// </summary>
    private bool Holding(long now)
    {
        var holding = _flow is { } flow && flow.AckOverdue(now);
        if (!holding)
        {
            if (Volatile.Read(ref _holding) != 0) Volatile.Write(ref _holding, 0);
        }
        else if (Volatile.Read(ref _holding) == 0 && Interlocked.CompareExchange(ref _holding, 1, 0) == 0)
        {
            Interlocked.Increment(ref _stateHolds);
            Interlocked.Exchange(ref _messageWork, 1);
        }

        return holding;
    }

    /// <summary>A message or mailbox record waits in a queue (consumer side: drained first).</summary>
    private bool AnyMessageWaiting()
    {
        foreach (var q in _laneTaps)
            if (MessageIn(q))
                return true;
        return MessageIn(_ready);

        static bool MessageIn(ReadyQueue q)
        {
            q.Drain();
            for (var node = q.First; node is not null; node = node.Next)
                if (node.Tap!.CarriesMessages)
                    return true;
            return false;
        }
    }

    private ReadyQueue ReadyOf(Tap tap)
    {
        return tap.Lane is { } lane ? _laneTaps[(int)lane] : _ready;
    }

    private static bool Waiting(ReadyQueue q)
    {
        return q.Count > 0 || q.HasInbound;
    }

    /// <summary>
    ///     The tap can't be served right now (blob store full, a watch's locks contended): try again after
    ///     <paramref name="delay" />.
    /// </summary>
    private void ScheduleRetry(Tap t, TimeSpan delay)
    {
        var due = _time.GetTimestamp() + Math.Max(1, (long)(delay.TotalSeconds * _time.TimestampFrequency));
        Volatile.Write(ref t.RetryAt, due);
        LowerRetryAt(due);
    }

    /// <summary>
    ///     Retries due by now, released (a read when none is due) — wherever its own thread looks: readiness, a build, a
    ///     park.
    /// </summary>
    private void ReleaseDueRetries(long now)
    {
        var retry = Volatile.Read(ref _nextRetryAt);
        if (retry != 0 && now >= retry) ReleaseRetries();
    }

    private void LowerRetryAt(long due)
    {
        for (long held;
             ((held = Volatile.Read(ref _nextRetryAt)) == 0 || due < held) &&
             Interlocked.CompareExchange(ref _nextRetryAt, due, held) != held;)
        {
        }
    }

    /// <summary>
    ///     Makes taps whose retry time has come sendable again — on the outlet's own thread, as it parks (and at every
    ///     heartbeat, as a backstop), not on the wheel's driver: it visits every tap.
    /// </summary>
    private void ReleaseRetries()
    {
        Interlocked.Exchange(ref _nextRetryAt, 0); // claimed: a retry scheduled meanwhile lowers it again
        long now = _time.GetTimestamp(), next = long.MaxValue;
        var released = false;
        foreach (var t in Volatile.Read(ref _taps))
        {
            var at = Volatile.Read(ref t.RetryAt);
            if (at != 0)
            {
                if (now >= at)
                {
                    Interlocked.Exchange(ref t.RetryAt, 0); // full fence before MarkDirty's queued check
                    t.MarkDirty();
                    released = true;
                }
                else
                {
                    next = Math.Min(next, at);
                }
            }

            var until = Volatile.Read(ref t.ThrottledUntil); // (its MinInterval: the state as it stands, once it's up)
            if (until == 0) continue;
            if (now >= until)
            {
                if (Interlocked.CompareExchange(ref t.ThrottledUntil, 0, until) == until) t.MarkDirty();
                released = true;
            }
            else
            {
                next = Math.Min(next, until);
            }
        }

        if (next != long.MaxValue) LowerRetryAt(next);
        if (released)
        {
            ReleaseHolds();
            Wake();
        }
    }

    // ───────────────────────────── signals from publishers / the client ─────────────────────────────

    /// <summary>Wakes a parked <see cref="WaitAsync" />. Callers publish with a full fence first (Dekker with the park).</summary>
    internal void Wake()
    {
        if (Volatile.Read(ref _parked) != 0 && Interlocked.Exchange(ref _parked, 0) != 0) _signal.Set();
    }

    /// <summary>
    ///     A heartbeat as soon as it can go — the router's state changed (<see cref="OutletRegistry.SetHealth" />): its next
    ///     frame leads with it, once the last heartbeat is echoed. Nothing else a heartbeat tick does.
    /// </summary>
    internal void HeartbeatSoon()
    {
        Interlocked.Exchange(ref _heartbeatDue, 1);
        Wake();
    }

    /// <summary>
    ///     Queues a heartbeat: the first record of the next frame, sent even without credit — but only once the previous one
    ///     has been echoed, so on a link slower than the heartbeat rate they don't pile up (one per round trip at most).
    /// </summary>
    public void Heartbeat()
    {
        Interlocked.Exchange(ref _heartbeatDue, 1);
        Wake();
        ExpireUnactivated();
        if (Volatile.Read(ref _nextRetryAt) != 0) ReleaseRetries();
        ReleaseHolds();
        CheckStall();
    }

    /// <summary>
    ///     Something for the client it hasn't acknowledged — a data frame in flight, changes or notices waiting — for longer
    ///     than <see cref="OutletOptions.StallTimeout" /> without progress: it is stalled.
    /// </summary>
    private void CheckStall()
    {
        var now = Stopwatch.GetTimestamp();
        if (IsAway) // disconnected: held for it, not stalled — for its hold, if it has one
        {
            Volatile.Write(ref _waitingSince, 0);
            if (_options.DisconnectedHold is { } hold &&
                Stopwatch.GetElapsedTime(Volatile.Read(ref _awaySince), now) >= hold &&
                Volatile.Read(ref _taps).Length > 0)
                Stall("Its client was disconnected longer than its subscriptions are held: they ended.");
            return;
        }

        var timeout = _options.StallTimeout;
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan) return;
        var newest =
            Volatile.Read(
                ref _dataFrames[(Volatile.Read(ref _dataNext) + _dataFrames.Length - 1) % _dataFrames.Length]);
        if (newest <= Volatile.Read(ref _acked) && !HasPendingWork && Volatile.Read(ref _noticesUnsent) == 0)
        {
            Volatile.Write(ref _waitingSince, 0); // nothing for it: idle, not stalled
            return;
        }

        var since = Volatile.Read(ref _waitingSince);
        if (since == 0)
        {
            Volatile.Write(ref _waitingSince, now);
            return;
        }

        if (Stopwatch.GetElapsedTime(Math.Max(since, Volatile.Read(ref _progressAt)), now) < timeout) return;
        Stall("The client acknowledged nothing for the stall timeout: its subscriptions ended.");
    }

    /// <summary>For the viewer: how long the client has had something waiting without progress (null: nothing waiting).</summary>
    private double? WaitingMs()
    {
        var since = Volatile.Read(ref _waitingSince);
        return since == 0
            ? null
            : Stopwatch.GetElapsedTime(Math.Max(since, Volatile.Read(ref _progressAt))).TotalMilliseconds;
    }

    /// <summary>
    ///     Ends every subscription of the stalled client (<see cref="FaultCode.Stalled" />) — what they held freed, their
    ///     providers told — and queues one <see cref="Wire.Ended" /> record naming the last tap id ended; then waits a whole
    ///     timeout again before the next.
    /// </summary>
    private void Stall(string why)
    {
        uint upTo;
        Tap[] taps;
        lock (_tapLock)
        {
            upTo = _nextTapId;
            taps = _taps;
        }

        Interlocked.Increment(ref _stalls);
        Volatile.Write(ref _progressAt, Stopwatch.GetTimestamp());
        Volatile.Write(ref _waitingSince, 0);
        if (taps.Length == 0) return;
        foreach (var t in taps)
            Close(t, new StreamFault(FaultCode.Stalled, t.Source.Version, why));
        Notify(ServiceNotice.Ended(FaultCode.Stalled, 1, upTo));
    }

    /// <summary>Closes taps that were bound but never activated within <see cref="OutletOptions.BindTimeout" />.</summary>
    private void ExpireUnactivated()
    {
        foreach (var t in Volatile.Read(ref _taps))
            if (Volatile.Read(ref t.State) == TapState.Bound &&
                Stopwatch.GetElapsedTime(t.BoundAt) > _options.BindTimeout)
            {
                Close(t);
            }
            else if (Volatile.Read(ref t.AwaitingInitial) != 0 && t.IsActive &&
                     Stopwatch.GetElapsedTime(Volatile.Read(ref t.ActivatedAt)) > _options.BindTimeout)
            {
                Close(t,
                    new StreamFault(FaultCode.NoInitialState, t.Source.Version,
                        "The provider declared it would send the initial state, and hasn't."));
                Notify(ServiceNotice.Ended(FaultCode.NoInitialState, t.Id,
                    t.Id)); // told reliably (nothing of it went in-band)
            }
            else if (Volatile.Read(ref t.Resuming) != 0 &&
                     Stopwatch.GetElapsedTime(Volatile.Read(ref t.ResumingSince)) > _options.BindTimeout)
            {
                while (Interlocked.Exchange(ref _building, 1) != 0) Thread.SpinWait(1);
                try
                {
                    ResumeTap(t, Volatile.Read(ref _confirmed));
                } // its client never said what it received: from what it acknowledged
                finally
                {
                    Volatile.Write(ref _building, 0);
                }

                t.MarkDirty();
            }
            else if (Volatile.Read(ref t.AwaitingFetch) != 0 &&
                     Stopwatch.GetElapsedTime(Volatile.Read(ref t.AwaitingSince)) > _options.FetchTimeout)
            {
                if (t.Source.Rewindable &&
                    t.Flight is { } flight) // the client never confirmed the fetch: that record goes again
                {
                    while (Interlocked.Exchange(ref _building, 1) != 0) Thread.SpinWait(1);
                    try
                    {
                        t.Cursor = flight.RewindHandle().Cursor;
                    }
                    finally
                    {
                        Volatile.Write(ref _building, 0);
                    }
                }
                else
                {
                    Interlocked.Exchange(ref t.ResyncRequested, 1);
                }
                // (one that can't be set back: a snapshot)

                Interlocked.Exchange(ref t.AwaitingFetch, 0);
                t.MarkDirty();
                ReleaseHolds();
            }
    }

    /// <summary>
    ///     From the client: it has fetched (and applied) the out-of-band record of a tap (<paramref name="kind" /> 0 — the
    ///     only
    ///     kind; others are ignored — <paramref name="id" /> = tap id), or given up on it; the tap resumes.
    ///     <paramref name="ticket" /> names the handle: confirming an older one (after a reconnect, timeout or resync) is
    ///     ignored.
    /// </summary>
    public void Fetched(int kind, uint id, uint ticket)
    {
        if (kind != 0) return;
        foreach (var t in Volatile.Read(ref _taps))
            if (t.Id == id)
            {
                if (Volatile.Read(ref t.AwaitingFetch) != 0 && Volatile.Read(ref t.FetchTicket) == ticket
                                                            && Interlocked.CompareExchange(ref t.AwaitingFetch, 0, 1) ==
                                                            1)
                {
                    t.MarkDirty();
                    ReleaseHolds(); // a message ordered after this state may go now
                }

                return;
            }
    }

    /// <summary>Cumulative frame ack (idempotent) carrying the latest heartbeat the client has seen.</summary>
    public void Ack(long frameSeq, long heartbeatEcho)
    {
        if (heartbeatEcho == Volatile.Read(ref _heartbeatSeq) &&
            Interlocked.Exchange(ref _heartbeatEchoed, 1) == 0) // full fence before Wake
        {
            Volatile.Write(ref _rttTicks, Stopwatch.GetElapsedTime(Volatile.Read(ref _heartbeatSentAt)).Ticks);
            Volatile.Write(ref _healthKnown,
                Volatile.Read(ref _healthSent)); // its client has the router's state: heartbeats alone now
            Volatile.Write(ref _progressAt, Stopwatch.GetTimestamp());
            Wake(); // a heartbeat waiting for this echo may go
        }

        frameSeq = Math.Min(frameSeq, Volatile.Read(ref _frameSeq)); // can't ack what wasn't sent
        for (long c;
             frameSeq > (c = Volatile.Read(ref _confirmed)) &&
             Interlocked.CompareExchange(ref _confirmed, frameSeq, c) != c;)
        {
        }

        _flow?.OnAck(frameSeq, _time.GetTimestamp());
        _frameLog.Acked(frameSeq, Stopwatch.GetTimestamp());
        for (long a; frameSeq > (a = Volatile.Read(ref _acked));)
            if (Interlocked.CompareExchange(ref _acked, frameSeq, a) == a)
            {
                Volatile.Write(ref _progressAt, Stopwatch.GetTimestamp()); // progress: not stalled
                Wake();
                return;
            }
    }

    /// <summary>
    ///     The client failed to verify a stream (sequence gap or checksum mismatch): send it a snapshot. (A message
    ///     stream has none: ignored.)
    /// </summary>
    public void Resync(uint tapId)
    {
        foreach (var t in Volatile.Read(ref _taps))
            if (t.Id == tapId && t.IsActive && t.Source.RecordType != Wire.TopicMessage)
            {
                Interlocked.Exchange(ref t.ResyncRequested, 1);
                t.MarkDirty();
                return;
            }
    }

    /// <summary>
    ///     <c>Tap.Sync</c>: the client asks whether it is up to date on a subscription — answered by a rendezvous, its
    ///     correlation the client's sync id (4 bytes, little-endian; an app's own ids are longer: UUIDv7s), handed to it
    ///     strictly after it has the subscription's state at the source's version as the router received the ask, or past
    ///     it (<see cref="Tap.TryRendezvous(ReadOnlySpan{byte}, long)" />). Prompt when nothing is held back — a quiet
    ///     source, a fast client — and as late as the delivery of what's held back otherwise: a rate cap's, an interval's, a
    ///     slow acknowledgement's. False, and nothing answered, if the subscription isn't active, its mailbox is full, or
    ///     it ended: the client's own timeout answers.
    /// </summary>
    public bool Sync(uint tapId, uint syncId)
    {
        foreach (var t in Volatile.Read(ref _taps))
            if (t.Id == tapId && t.IsActive)
            {
                Span<byte> correlation = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(correlation, syncId);
                return t.TryRendezvous(correlation, t.Source.Version);
            }

        return false;
    }

    /// <summary>
    ///     A client's control message, applied by its kind: every kind this router says it takes
    ///     (<see cref="ControlKinds.Supported" />, on each new connection). False for a kind it doesn't know, or an
    ///     <see cref="ControlKind.Auth" /> with no payload: nothing done.
    /// </summary>
    public bool Apply(in ControlMessage message)
    {
        switch (message.Kind)
        {
            case ControlKind.Activate: Activate((uint)message.A); return true;
            case ControlKind.Ack: Ack(message.A, message.B); return true;
            case ControlKind.Resume: Resume(message.A, message.B); return true;
            case ControlKind.Resync: Resync((uint)message.A); return true;
            case ControlKind.Close: Close((uint)message.A, (FaultCode)(ushort)message.B); return true;
            case ControlKind.Fetched: Fetched(0, (uint)message.A, (uint)message.B); return true;
            case ControlKind.Auth when !message.Payload.IsEmpty: Auth(message.Payload.Span); return true;
            case ControlKind.Sync: Sync((uint)message.A, (uint)message.B); return true;
            default: return false;
        }
    }

    /// <summary>
    ///     The next regular data frame may go: by the <see cref="OutletOptions.MaxFramesPerSecond" />, if any, and adaptive
    ///     flow control's
    ///     frame spacing; within one wheel tick, as it can't wake any sooner.
    /// </summary>
    private bool FrameDue(long now)
    {
        var period = Volatile.Read(ref _framePeriod);
        return (period == 0 || now + _slack >= Volatile.Read(ref _nextFrameAt)) &&
               (_flow is null || now + _slack >= _flow.SpacedUntil);
    }

    /// <summary>
    ///     Completes when the outlet may be ready (spurious wake-ups possible; re-check <see cref="IsReady" />).
    ///     Allocation-free.
    ///     Parking, it registers with the registry's timing wheel only if something is held back by time — a frame not yet
    ///     due, pacing, its Streaming budget in debt, a retry — once, for the earliest: a busy outlet, or one waiting only on
    ///     data or acknowledgements, never touches the wheel.
    /// </summary>
    public ValueTask WaitAsync()
    {
        var now = _time.GetTimestamp();
        ReleaseDueRetries(now); // its own thread's work, not the wheel's
        Interlocked.Exchange(ref _parked, 1); // full fence before the re-check
        if (IsReady)
        {
            Volatile.Write(ref _parked, 0);
            return default;
        }

        if (NextWake(now) is var wake and not 0) _wheel.Schedule(_timerNode, wake);
        return _signal.WaitAsync();
    }

    /// <summary>
    ///     When something held back by time may go (0: nothing is): a retry due; and, with work waiting, a frame's time — its
    ///     cadence, the path's pacing and spacing — and the Streaming budget back in credit. What waits on data or an
    ///     acknowledgement is woken by those.
    /// </summary>
    private long NextWake(long now)
    {
        var wake = Volatile.Read(ref _nextRetryAt);
        if (!HasPendingWork) return wake;
        var gate = Volatile.Read(ref _framePeriod) > 0 ? Volatile.Read(ref _nextFrameAt) : 0;
        if (_flow is { } flow) gate = Math.Max(gate, Math.Max(flow.NextSendAt, flow.SpacedUntil));
        if (gate > now) wake = wake == 0 ? gate : Math.Min(wake, gate);
        var eligible = Volatile.Read(ref _streamEligibleAt);
        if (eligible > now) wake = wake == 0 ? eligible : Math.Min(wake, eligible);
        return wake;
    }

    /// <summary>Releases a pending <see cref="WaitAsync" /> (e.g. on cancellation).</summary>
    public void Interrupt()
    {
        _signal.Set();
    }

    /// <summary>
    ///     Its transport's connection ended; the client may come back on another (<see cref="Attach" />). Meanwhile its
    ///     subscriptions don't hold anything back (a <see cref="OverflowPolicy.Refuse" /> topic's publishes: they overflow
    ///     instead).
    /// </summary>
    public void Detach()
    {
        Volatile.Write(ref _awaySince, Stopwatch.GetTimestamp());
        Volatile.Write(ref _away, 1);
    }

    /// <summary>
    ///     Binds the outlet to a new transport connection identified by <paramref name="owner" />. Every tap that sent
    ///     anything
    ///     on a connection before is held until the client says the last frame it received (<see cref="Resume" />), then goes
    ///     on from exactly there; one that never sent starts as it would have. Frames built for a previous owner are refused
    ///     from now on.
    /// </summary>
    public void Attach(object owner)
    {
        while (Interlocked.Exchange(ref _building, 1) != 0) Thread.SpinWait(1);
        foreach (var t in Volatile.Read(ref _taps))
        {
            if (Interlocked.Exchange(ref t.HeldBy, null) is not null) Interlocked.Decrement(ref _held);
            if (t.IsActive && t.Flight is { Sent: true })
            {
                Volatile.Write(ref t.ResumingSince, Stopwatch.GetTimestamp());
                Volatile.Write(ref t.Resuming,
                    1); // held till its client says what it received (it does, binding): from exactly there
                continue; // (a fetch it awaited: settled then)
            }

            t.Oversize = false;
            Interlocked.Exchange(ref t.AwaitingFetch, 0); // full fence before the queued check (Dekker)
            t.MarkDirty();
        }

        Array.Clear(_deficit);
        _turn = 0;
        _turnStarted = false;
        ResendNotices(Volatile.Read(ref _acked)); // what the old connection may have lost
        _frameLog.Reset(_frameSeq); // the old connection's frames: never acked now
        Volatile.Write(ref _acked, _frameSeq); // every data frame counts as acked: full window
        _flow?.Reset(_frameSeq + 1, _time.GetTimestamp()); // a new connection: a new path
        _nextFrameAt = 0; // …and a frame may go at once
        Volatile.Write(ref _heartbeatEchoed, 1); // the old connection's heartbeat will never be echoed
        Volatile.Write(ref _healthKnown, null); // …nor may the router's state it carried have arrived: told again
        Interlocked.Exchange(ref _heartbeatDue, 1); // full fence before Wake
        _owner = owner;
        Volatile.Write(ref _away, 0);
        Volatile.Write(ref _progressAt, Stopwatch.GetTimestamp()); // a new connection: progress
        Volatile.Write(ref _building, 0);
        Wake();
    }

    /// <summary>
    ///     Builds the next frame payload for <paramref name="owner" /> (the attached connection). Call as late as possible —
    ///     ideally when the frame reaches the head of the connection's send queue. Returns false when there is nothing to
    ///     send (or the owner is stale). The span is valid until the next build. <paramref name="deferToOthers" /> restricts
    ///     the frame to the High class (e.g. other traffic is queued behind it on the same connection).
    /// </summary>
    public bool TryBuildFrame(object owner, bool deferToOthers, out ReadOnlySpan<byte> frame, out long seq)
    {
        return TryBuildFrame(owner, deferToOthers, out frame, out seq, out _);
    }

    /// <inheritdoc cref="TryBuildFrame(object, bool, out ReadOnlySpan{byte}, out long)" />
    /// <param name="incompressibleBytes">
    ///     How many of the frame's bytes are records of content declared not compressible (messages, values published or
    ///     tables marked so) and out-of-band handles: a transport that compresses per message can leave a frame alone
    ///     when what remains is too little to be worth it.
    /// </param>
    public bool TryBuildFrame(object owner, bool deferToOthers, out ReadOnlySpan<byte> frame, out long seq,
        out int incompressibleBytes)
    {
        frame = default;
        seq = 0;
        incompressibleBytes = 0;
        if (Interlocked.Exchange(ref _building, 1) != 0) return false;
        try
        {
            if (!ReferenceEquals(owner, _owner) || Volatile.Read(ref _disposed) != 0) return false;
            ReleaseFrame(); // the last frame: by contract, done with
            if (_buffers[LargeBuffer].Buffer is not null) Release(LargeBuffer); // …and the outlet's hold on a large one
            var slot = _wantLarge ? RentLarge() : OwnFrameBuffer();
            var scratch = _buffers[slot].Buffer!;
            var n = Build(scratch.AsSpan(0, Math.Min(scratch.Length, _options.MaxFrame)), deferToOthers,
                scratch.Length < _options.MaxFrame);
            if (n == 0 && _wantLarge) // nothing went: build the large one at once
            {
                scratch = _buffers[slot = RentLarge()].Buffer!;
                n = Build(scratch.AsSpan(0, _options.MaxFrame), deferToOthers);
            }

            if (n == 0) return false;
            Interlocked.Increment(ref _buffers[slot].Refs); // the frame's own reference, until released
            Volatile.Write(ref _outBuffer, slot);
            seq = _frameSeq + 1;
            Volatile.Write(ref _frameSeq, seq);
            _frameLog.Built(seq, _builtAt, n, _builtRecords);
            if (!HeartbeatOnly(n)) // not heartbeat-only: takes a window slot
            {
                Volatile.Write(ref _dataFrames[_dataNext], seq);
                Volatile.Write(ref _dataNext, (_dataNext + 1) % _dataFrames.Length);
            }

            if (_flow is { } flow)
            {
                var now = _time.GetTimestamp();
                // Data held back to make way for High, or state waiting for an overdue acknowledgement, was held by choice,
                // not by the path: such a frame is app-limited.
                flow.OnSent(seq, n, !HeartbeatOnly(n), _madeWay || _holdingState || !HasPendingWork, now);
            }

            frame = scratch.AsSpan(0, n);
            incompressibleBytes = _incompressible;
            return true;
        }
        finally
        {
            Volatile.Write(ref _building, 0);
        }
    }

    /// <summary>The frame just built is a heartbeat alone (it takes no window slot, and no frame period).</summary>
    private bool HeartbeatOnly(int n)
    {
        return n > 0 && n == _frameHeartbeat;
    }

    private int OwnFrameBuffer()
    {
        if (_buffers[OwnBuffer].Buffer is null)
        {
            _buffers[OwnBuffer].Buffer = _registry.FrameBuffers.Rent(SmallFrameBytes);
            _buffers[OwnBuffer].Refs = 1; // the outlet's, until dispose
        }

        return OwnBuffer;
    }

    private int RentLarge()
    {
        _wantLarge = false;
        Debug.Assert(_buffers[LargeBuffer].Buffer is null, "a large frame buffer still held");
        _buffers[LargeBuffer].Buffer = ArrayPool<byte>.Shared.Rent(_options.MaxFrame);
        _buffers[LargeBuffer].Refs = 1; // the outlet's, until the next build
        return LargeBuffer;
    }

    /// <summary>
    ///     The transport has finished with the frame
    ///     <see cref="TryBuildFrame(object, bool, out ReadOnlySpan{byte}, out long, out int)" />
    ///     returned (copied it out): its buffer may be reused. Optional — the next build implies it — but a transport that
    ///     calls it lets a dispose return the buffer as soon as it can, rather than leave it to the GC. Idempotent.
    /// </summary>
    public void ReleaseFrame()
    {
        var slot = Interlocked.Exchange(ref _outBuffer, -1);
        if (slot >= 0) Release(slot);
    }

    private void Release(int slot)
    {
        var refs = Interlocked.Decrement(ref _buffers[slot].Refs);
        Debug.Assert(refs >= 0, "frame buffer reference count below zero");
        if (refs != 0) return;
        var buffer = _buffers[slot].Buffer!;
        _buffers[slot].Buffer = null;
        if (slot == OwnBuffer)
        {
            _registry.FrameBuffers.Return(buffer);
        }
        else
        {
#if DEBUG
            buffer.AsSpan().Fill(0xDD);
#endif
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ───────────────────────────── scheduling ─────────────────────────────

    /// <summary>
    ///     Heartbeat first (exempt from credit and budget), then deficit round robin with overdraft over the four classes.
    ///     On its turn a class adds its quantum and sends while its deficit is positive, going into debt for a record larger
    ///     than what remains and repaying it on later turns — long-run byte rates follow the weights without knowing record
    ///     sizes up front. The turn persists across frames: when the frame fills, the class keeps its turn and opens the
    ///     next frame, the one place a record larger than the budget may go. A rate-limited Streaming class sits out while
    ///     its token bucket is in debt (same overdraft rule, repaid by time); the timing wheel wakes the outlet when it
    ///     is positive again.
    /// </summary>
    internal int Build(Span<byte> dst, bool deferToOthers = false, bool canGrow = false)
    {
        ReleaseDueRetries(_time.GetTimestamp());
        _builtAt = Stopwatch.GetTimestamp();
        _builtRecords = 0;
        _canGrow = canGrow; // a first record that doesn't fit asks for a larger frame, not oversize
        _incompressible = 0;
        var now = _time.GetTimestamp();
        var limited = ApplyRateLimit(now);
        _frameStamp++;
        _clockedFrame = _frameFull = _highServed = _othersServed = false;
        _madeWay = MakingWay(now);
        _holdingState = Holding(now); // its client's acknowledgement overdue: state waits, messages go
        _messageServed = false;
        if (_holdingState) Interlocked.Exchange(ref _messageWork, 0); // (its producers set it again: full fence)
        var paceHolds = _flow is { } flow && !flow.PaceAllows(now); // High goes regardless, in a frame of its own
        if (paceHolds && HasPendingWork && !_holdingState) _flow!.MarkLimited();
        var gated = deferToOthers || !FrameDue(now) || _madeWay || paceHolds;
        var n = BuildFrame(dst, gated, now);
        // Messages left while state waits: another frame at once if this one took some (it filled), or when the gate that
        // held them opens (IsReady checks it). Otherwise — held by an order (After), awaiting a fetch — their own events say.
        if (_holdingState && (_messageServed || gated || (limited && !_bucket.IsPositive)) && AnyMessageWaiting())
            Volatile.Write(ref _messageWork, 1);
        if (_highServed) Volatile.Write(ref _highSentAt, now);
        if (_othersServed) Volatile.Write(ref _othersSentAt, now);
        // A data frame went: the next is due a period on. One the budget cut short leaves the tick open — its rest follows
        // at once, as room and pacing allow (arriving together, they're shown together).
        if (_clockedFrame && !_frameFull && _framePeriod > 0 && n > 0 && !HeartbeatOnly(n))
            _nextFrameAt = now - _nextFrameAt <= _framePeriod / 2
                ? _nextFrameAt + _framePeriod
                : now + _framePeriod; // on schedule: keep the phase; late: restart it
        if (limited)
            Volatile.Write(ref _streamEligibleAt,
                _bucket.IsPositive ? 0 : now + _bucket.TicksUntilPositive); // (a park waiting on it registers it)
        return n;
    }

    private bool ApplyRateLimit(long now)
    {
        var requested = Volatile.Read(ref _rateRequested);
        if (!ReferenceEquals(requested, _rateApplied))
        {
            if (requested is null) Volatile.Write(ref _streamEligibleAt, 0);
            else if (_rateApplied is null)
                _bucket = new TokenBucket(requested.BytesPerSecond, requested.BurstBytes, now,
                    _time.TimestampFrequency);
            else _bucket.Reconfigure(requested.BytesPerSecond, requested.BurstBytes, now);
            Volatile.Write(ref _rateApplied, requested);
        }

        if (_rateApplied is null) return false;
        _bucket.Refill(now);
        return true;
    }

    /// <summary>
    ///     Each class's quantum: its weight's share of the frame budget in use. With adaptive flow control the budget follows
    ///     the path (half its bandwidth × delay when paced), so on a slow path a class's turn lasts about a frame — High's
    ///     records go between Streaming's, not after a turn of several (seconds, on a 16 kbit/s link). Deficits carry over.
    /// </summary>
    private void SizeQuanta(int budget)
    {
        _quantumBudget = budget;
        var sum = _weights.Sum();
        for (var c = 0; c < 4; c++) _quantum[c] = Math.Max(1, (int)((long)budget * _weights[c] / sum));
    }

    private int BuildFrame(Span<byte> dst, bool deferToOthers, long now)
    {
        var pos = 0;
        _frameHeartbeat = 0;
        if (Volatile.Read(ref _heartbeatEchoed) != 0 && Interlocked.Exchange(ref _heartbeatDue, 0) != 0)
            pos = _frameHeartbeat = WriteHeartbeat(dst);
        if (Volatile.Read(ref _notices) is not null) pos += WriteNotices(dst[pos..]);
        if (_flow is { } flow && !flow.PaceAllows(now) && !deferToOthers)
        {
            if (HasPendingWork && !_holdingState) flow.MarkLimited();
            return pos;
        }

        if (_holdingState ? !WindowOpen : !HasCredit) return pos; // (held: messages alone, within the window)

        var budget = _flow?.FrameBudget ?? _options.FrameBudget;
        if (budget != _quantumBudget) SizeQuanta(budget);
        int dataStart = pos, budgetEnd = Math.Min(dst.Length, dataStart + budget);
        if (deferToOthers)
        {
            // As in a regular frame, the first record may take the whole frame: a tap whose record didn't fit would park as oversize.
            for (int n;
                 (n = ServeHigh(dst[pos..(pos == dataStart ? dst.Length : Math.Max(pos, budgetEnd))],
                     pos == dataStart)) > 0;) pos += n;
            return pos;
        }

        _clockedFrame = true;

        Span<bool> empty = stackalloc bool[4];
        var idle = 0; // consecutive turns that sent nothing
        while (true)
        {
            var c = _turn;
            var sent = false;
            if (!empty[c])
            {
                if (!_turnStarted)
                {
                    _deficit[c] = Math.Min(_deficit[c] + _quantum[c], _options.MaxFrame);
                    _turnStarted = true;
                }

                while (_deficit[c] > 0)
                {
                    var first = pos == dataStart;
                    var limit = (first ? dst.Length : budgetEnd) - pos;
                    var n = limit > 0 ? Serve(c, dst.Slice(pos, limit), first) : -1;
                    if (n > 0)
                    {
                        pos += n;
                        _deficit[c] -= n;
                        sent = true;
                        continue;
                    }

                    if (n < 0) // frame full: this class keeps its turn and opens the next frame…
                    {
                        _frameFull = limit > 0 || HasPendingWork; // …if anything is left (one record may fill it alone)
                        return pos;
                    }

                    empty[c] = true;
                    if (_deficit[c] > 0) _deficit[c] = 0; // no hoarding while idle; debt is kept
                    break;
                }
            }

            _turn = (c + 1) & 3;
            _turnStarted = false;
            idle = sent ? 0 : idle + 1;
            if (idle < 4) continue;

            // A full cycle sent nothing: every backlogged class is in debt. Fast-forward whole cycles (work-conserving).
            var cycles = int.MaxValue;
            for (var k = 0; k < 4; k++)
                if (!empty[k])
                    cycles = Math.Min(cycles, -_deficit[k] / _quantum[k] + 1);
            if (cycles == int.MaxValue) return pos;
            for (var k = 0; k < 4; k++)
                if (!empty[k])
                    _deficit[k] += (cycles - 1) * _quantum[k];
            idle = 0;
        }
    }

    private int Serve(int c, Span<byte> dst, bool first)
    {
        if (c == High) return ServeHigh(dst, first);
        var n = c switch
        {
            Normal => ServeClass(Lane.Normal, dst, first),
            Streaming => ServeStreamLimited(dst, first),
            _ => ServeClass(Lane.Low, dst, first)
        };
        if (n > 0) _othersServed = true;
        return n;
    }

    private int ServeHigh(Span<byte> dst, bool first)
    {
        var n = ServeClass(Lane.High, dst, first);
        if (n > 0) _highServed = true;
        return n;
    }

    /// <summary>
    ///     High has sent within the last <see cref="HighActiveMs" />: the other classes make way for it — on a slow path, one
    ///     frame of theirs in flight at a time, only when the wire is clear (<see cref="FlowControl.HasRoomBesideHigh" />), so
    ///     a
    ///     High record never waits for room, only behind a frame already on the wire. Never for longer than
    ///     <see cref="MakeWayMs" /> at a stretch: then one of theirs goes regardless — High can't starve them.
    /// </summary>
    private bool MakingWay(long now)
    {
        return _flow is { } flow
               && now - Volatile.Read(ref _highSentAt) < HighActiveMs * _time.TimestampFrequency / 1000
               && now - Volatile.Read(ref _othersSentAt) < MakeWayMs * _time.TimestampFrequency / 1000
               && !flow.HasRoomBesideHigh;
    }

    /// <summary>A lane's class: a record from a tap bound into it, oldest-dirty-first.</summary>
    private int ServeClass(Lane lane, Span<byte> dst, bool first)
    {
        return ServeTaps(_laneTaps[(int)lane], dst, first);
    }

    private int ServeStreamLimited(Span<byte> dst, bool first)
    {
        var limited = _rateApplied is not null;
        if (limited && !_bucket.IsPositive) return 0; // sits out: as if idle
        _oobCharge = 0;
        var n = ServeTaps(_ready, dst, first);
        if (n > 0 && limited) _bucket.Spend(n + _oobCharge); // out-of-band bytes count against the client's budget
        return n;
    }

    /// <summary>
    ///     One record from the oldest dirty tap whose record fits (ordered across taps by when each became dirty, strictly
    ///     within one). A record that doesn't fit keeps its place while smaller ones behind it go; it gets the next empty
    ///     frame. A record too big even for an empty frame parks its tap as oversize until the next resync/attach. A tap on
    ///     a faulted source sends its error policy instead, then closes. Visits only the taps that were dirty when called.
    /// </summary>
    private int ServeTaps(ReadyQueue ready, Span<byte> dst, bool first)
    {
        ready.Drain();
        var end = ready.Last;
        var blocked = false;
        for (var node = ready.First; node is not null;)
        {
            var next = node == end ? null : node.Next;
            var t = node.Tap!;
            if (_holdingState && !t.CarriesMessages) // its state waits for an overdue acknowledgement: it keeps its place
            {
                node = next;
                continue;
            }

            if (t.BlockedIn == _frameStamp) // already didn't fit this frame (space only shrinks)
            {
                blocked = true;
                node = next;
                continue;
            }

            var w = ServeTap(t, dst, first);
            if (w < 0)
            {
                blocked = true; // keeps its place
                t.BlockedIn = _frameStamp;
                node = next;
                continue;
            }

            ready.Remove(node);
            Settle(t);
            if (w > 0 && _holdingState) _messageServed = true;
            if (w > 0) return w;
            node = next;
        }

        return blocked ? -1 : 0;
    }

    /// <summary>
    ///     One tap's next record: its error policy if its source faulted (then it closes), else its state. -1 = doesn't fit
    ///     (not first in the frame); a record too large even for an empty frame parks the tap as oversize (0).
    /// </summary>
    private int ServeTap(Tap t, Span<byte> dst, bool first, bool pulled = false)
    {
        if (Volatile.Read(ref t.State) != TapState.Active || Volatile.Read(ref t.Resuming) != 0 ||
            Volatile.Read(ref t.HeldBy) is not null) return 0;
        var real = t.Source.Fault;
        var fault = real ?? Injected(t.Source);
        var w = fault is null ? Source.FaultCleared : WriteFault(t, fault, real, dst, first);
        if (w == Source.Busy) return Busy(t, w);
        if (w > 0)
        {
            Recorded(t, dst[..w]);
            Close(t, fault!);
        }
        else if (w == Source.FaultCleared)
        {
            var pre = pulled ? 0 : Constrained(t, dst, first); // a message ordered after state: that state first
            if (pre < 0) return -1;
            if (Volatile.Read(ref t.HeldBy) is not null) return pre; // …which can't go yet: the message waits
            var own = dst[pre..];
            var alone = first && pre == 0;
            var before =
                t.Source is MessageTopic { Overflow: OverflowPolicy.Skip } && Volatile.Read(ref t.Pending) is null
                    ? t.Cursor.Seq
                    : -1;
            var from = t.Cursor.Seq; // (a record of the state: from here to where it leaves the cursor)
            // State waiting for an overdue acknowledgement: a message topic's records go, a state tap's mailbox only — its
            // state too if pulled forward by a message ordered after it.
            w = WriteState(t, own, alone, _holdingState && !pulled && t.Source is not MessageTopic);
            if (before >= 0 && w > 0 && t.Cursor.Seq > before + 1)
                t.SkippedThrough = t.Cursor.Seq - 1; // skipped to it: the gap's the client's loss
            if (w == Source.Overflowed) // a message stream it fell behind: its subscription ends
            {
                var overflow = new StreamFault(FaultCode.Overflowed, t.Cursor.Seq,
                    "Messages it hadn't been sent were evicted: it fell behind.");
                w = WriteFault(t, overflow, null, own, alone);
                if (w > 0)
                {
                    Recorded(t, own[..w]);
                    Close(t, overflow);
                }
                else if (w == Source.FaultCleared || w == Source.Busy)
                {
                    w = Busy(t, w == Source.Busy ? w : 0);
                }

                return w >= 0 ? pre + w : pre > 0 ? pre : alone ? 0 : -1;
            }

            if (w > 0) Recorded(t, own[..w], from);
            if (w > 0 && t.Cursor.Seq != from && t.MinIntervalTicks is var gap and > 0 &&
                t.Source is not MessageTopic) // (its state went: the next waits — a message stream's never do)
            {
                var due = _time.GetTimestamp() + gap;
                Volatile.Write(ref t.ThrottledUntil, due);
                LowerRetryAt(due);
            }
            if (w > 0 && (own[0] == Wire.OobRef || !t.Source.IsCompressible))
                _incompressible += w; // a handle, or content declared so
            if (w == 0 && t.Source.Fault is
                    { } raised) // the write itself faulted the source (e.g. a watch that can't carry a member)
            {
                w = WriteFault(t, raised, raised, own, alone);
                if (w > 0)
                {
                    Recorded(t, own[..w]);
                    Close(t, raised);
                }
                else if (w == Source.FaultCleared || w == Source.Busy)
                {
                    w = Busy(t, w == Source.Busy ? w : 0);
                }
            }

            if (pre > 0)
                return
                    w > 0
                        ? pre + w
                        : pre; // its own record, if it didn't fit, in the next frame (nothing to hold it then)
        }

        if (w >= 0) return w;
        if (!first) return -1;
        if (_canGrow)
        {
            _wantLarge = true;
            return -1;
        } // the next frame is a full-size one: it keeps its place

        t.Oversize = true;
        return 0;
    }

    /// <summary>
    ///     The next message of a message stream, or of a mailbox, ordered after state (<see cref="After" />): if this client's
    ///     tap on that source is behind, its record goes first, into this frame (overdrawing a rate limit rather than delay
    ///     the
    ///     message); one that can't go yet (fetching, retrying, resuming) holds the message (<see cref="Tap.HeldBy" />) until
    ///     it can. A tap that's gone, inactive or oversize holds nothing. Returns the bytes pulled forward, or −1 if a record
    ///     doesn't fit this frame.
    /// </summary>
    private int Constrained(Tap t, Span<byte> dst, bool first)
    {
        object? target;
        long version;
        if (Volatile.Read(ref t.Mail) is { } mail && mail.HasNext)
        {
            if (!mail.TryPeekConstraint(out target, out version)) return 0;
        }
        else if (t.Source is MessageTopic topic)
        {
            if (!topic.TryPeekConstraint(t.Cursor, out target, out version)) return 0;
        }
        else
        {
            return 0;
        }

        var pre = 0;
        if (target is Tap one)
        {
            if (!ReferenceEquals(one, t)) PullForward(t, one, version, dst, first, ref pre);
        } // (its own: WriteState's)
        else
        {
            foreach (var u in Volatile.Read(ref _taps))
                if (!ReferenceEquals(u, t) && ReferenceEquals(u.Source, target) &&
                    !PullForward(t, u, version, dst, first, ref pre))
                    break;
        }

        return pre;
    }

    /// <summary>
    ///     One tap the message is ordered after: false to stop (the message held, or a record that didn't fit —
    ///     <paramref name="pre" /> −1 if nothing went).
    /// </summary>
    private bool PullForward(Tap t, Tap u, long version, Span<byte> dst, bool first, ref int pre)
    {
        if (Volatile.Read(ref u.State) != TapState.Active || u.Oversize) return true; // can't hold it
        if (u.HasReached(version)) return true; // the client has it
        if (Volatile.Read(ref u.AwaitingFetch) != 0 || Volatile.Read(ref u.Resuming) != 0 ||
            Volatile.Read(ref u.RetryAt) != 0
            || Volatile.Read(ref u.Stale) != 0 || Volatile.Read(ref u.AwaitingInitial) != 0)
        {
            Hold(t, u, version);
            return false;
        } // it can't move yet: its state not applied, its provider degraded, its initial state to come

        var charge =
            t.Lane is not null && u.Lane is null &&
            _rateApplied is not null; // a Streaming record ahead of another class's message: its bytes counted here
        var outer = _oobCharge;
        if (charge) _oobCharge = 0;
        var n = ServeTap(u, dst[pre..], first && pre == 0, true);
        if (charge)
        {
            if (n > 0) _bucket.Spend(n + _oobCharge);
            _oobCharge = outer;
        }

        if (n < 0)
        {
            if (pre == 0) pre = -1;
            return false;
        }

        pre += n;
        if (u.Oversize || u.IsClosed) return true; // can't hold it after all
        if (!u.HasReached(version))
        {
            Hold(t, u, version);
            return false;
        } // out of band (waits for its fetch), or the version not there yet

        return true;
    }

    /// <summary>
    ///     The tap's next record, in-band or out of band. Auto: in-band up to <see cref="OutletOptions.OutOfBandThreshold" />;
    ///     larger records (and any that can't fit a frame) are stored as a blob and replaced by a 43-byte handle. A snapshot
    ///     already stored by another tap is reused (serialized and hashed once for all). After a transparent handle the tap
    ///     waits for <see cref="Fetched" />; its changes keep coalescing meanwhile.
    /// </summary>
    /// <param name="mailOnly">
    ///     Its state waits (its client's acknowledgement overdue): only its mailbox's next entry — or its state, if that entry
    ///     is ordered after it.
    /// </param>
    private int WriteState(Tap t, Span<byte> dst, bool first, bool mailOnly = false)
    {
        if (Volatile.Read(ref t.Stale) != 0)
            return 0; // its provider degraded: held (its stale data went with the notice)
        if (!mailOnly && Interlocked.Exchange(ref t.ResyncRequested, 0) != 0)
        {
            t.Cursor = Cursor.Snapshot;
            t.Oversize = false;
            Volatile.Write(ref t.AwaitingFetch, 0);
        }

        var stateFirst = false; // its mailbox's next entry is ordered after its own state: that goes first
        if (Volatile.Read(ref t.AwaitingFetch) == 0 && Volatile.Read(ref t.Mail) is { } mail &&
            mail.HasNext) // its mailbox: before the topic's
            // next record, after one being fetched
        {
            if (!WaitsOnOwn(t, mail, out var flags))
            {
                var m = mail.WriteNext(t.Id, dst, flags);
                if (m != 0) return m; // an entry, −1 (doesn't fit) or Overflowed (evicted unsent)
            }
            else
            {
                stateFirst = true;
            }
        }

        if (mailOnly && !stateFirst) return 0;

        if (Volatile.Read(ref t.Pending) is { } pending) // the provider's snapshot for this subscriber: first, alone
        {
            var n = Wire.ValueHeader + pending.Bytes.Length;
            if (n > dst.Length) return -1;
            dst[0] = Wire.Value;
            BinaryPrimitives.WriteUInt32LittleEndian(dst[1..], t.Id);
            BinaryPrimitives.WriteUInt16LittleEndian(dst[5..], (ushort)pending.Bytes.Length);
            pending.Bytes.CopyTo(dst[Wire.ValueHeader..]);
            t.Cursor = pending.After; // the topic's changes since it was sent follow; nothing older
            if (Interlocked.CompareExchange(ref t.Pending, null, pending) == pending)
                Discharge(t, pending.Bytes.Length);
            t.SentPending = pending;
            return n;
        }

        if (Volatile.Read(ref t.AwaitingInitial) != 0)
            return 0; // the topic held: the provider's initial state not complete yet
        if (Volatile.Read(ref t.ThrottledUntil) != 0)
            return 0; // its interval holds its state — and an entry ordered after it, with it (a mailbox post dirtied it)
        if (t.Source.IsOverflowed(t.Cursor)) return Source.Overflowed;
        var source = t.Source;
        if (t.Oversize || Volatile.Read(ref t.AwaitingFetch) != 0 || source.Version == t.Cursor.Seq) return 0;
        var delivery = source.DeliveryMode;
        if (delivery == Delivery.Inline) return Busy(t, source.Write(t.Id, ref t.Cursor, dst));

        var mode = delivery == Delivery.Reference && source.RecordType == Wire.Value
            ? Wire.OobReference
            : Wire.OobTransparent;
        var start = t.Cursor;
        // The same record, already out of band for another tap (the same snapshot, the same delta — or, for a value, the
        // value): issued again, not written, copied or hashed.
        if (source.Shared && dst.Length >= Wire.OobRefSize && Volatile.Read(ref source.BlobCache) is { } cached
            && (source.RecordIsWholeState || (cached.Start.Seq == start.Seq && cached.Start.Pos == start.Pos))
            && source.CanResumeFrom(cached.After) && _blobs.TryIssue(cached.Id))
        {
            t.Cursor = cached.After;
            return WriteHandle(t, cached.Id, cached.Length,
                cached.ContentAddressed ? (byte)(mode | Wire.OobContentAddressed) : mode, dst);
        }

        var hashed = source.ContentAddressed;
        if (hashed) mode |= Wire.OobContentAddressed;

        var threshold = _options.OutOfBandThreshold;
        if (delivery == Delivery.Auto)
        {
            var w = Busy(t, source.Write(t.Id, ref t.Cursor, dst));
            if (w == Source.Overflowed) return w;
            if (w >= 0 && (threshold <= 0 || w <= threshold)) return w;
            if (w > 0)
            {
                // Written in-band but over the threshold: move the same bytes out of band (when the handle fits where it was).
                if (dst.Length < Wire.OobRefSize) return w;
                var content = source.RecordType == Wire.Value ? dst[Wire.ValueHeader..w] : dst[..w];
                if (source.RecordType != Wire.Value) BinaryPrimitives.WriteUInt32LittleEndian(content[1..], 0);
                if (!_blobs.TryPut(content, false, hashed, out var id))
                {
                    if (source.RecordType != Wire.Value) BinaryPrimitives.WriteUInt32LittleEndian(content[1..], t.Id);
                    return w; // store full: stays in-band
                }

                if (source.Shared)
                    Volatile.Write(ref source.BlobCache,
                        new OutOfBandRecord(start, t.Cursor, id, content.Length, hashed));
                return WriteHandle(t, id, content.Length, mode, dst);
            }

            // Doesn't fit. Small enough to fit a later frame, or, with no threshold (in band whatever fits a frame), the
            // full-size frame this one can grow to? Then wait for it rather than go out of band.
            if (threshold <= 0 ? !first || _canGrow : !first && dst.Length < threshold) return -1;
        }

        if (dst.Length < Wire.OobRefSize) return -1;

        var scratch =
            ArrayPool<byte>.Shared.Rent(Math.Min(Math.Max(threshold * 4, 64 * 1024), _options.MaxOutOfBandBytes));
        try
        {
            int n;
            while ((n = source.WriteBlob(ref t.Cursor, scratch)) < 0)
            {
                if (n == Source.Busy) return Busy(t, n);
                if (n == Source.Overflowed) return n;
                if (scratch.Length >= _options.MaxOutOfBandBytes) return -1; // too large even out of band: oversize
                var size = (int)Math.Min((long)scratch.Length * 2, _options.MaxOutOfBandBytes);
                ArrayPool<byte>.Shared.Return(scratch);
                scratch = ArrayPool<byte>.Shared.Rent(size);
            }

            if (n == 0 && (source.RecordType != Wire.Value || t.Cursor.Seq == 0)) return 0; // nothing to send
            if (!_blobs.TryPut(scratch.AsSpan(0, n), false, hashed, out var id))
            {
                t.Cursor = start;
                ScheduleRetry(t, TimeSpan.FromMilliseconds(250)); // store full: back off, then try again
                return 0;
            }

            if (source.Shared)
                Volatile.Write(ref source.BlobCache, new OutOfBandRecord(start, t.Cursor, id, n, hashed));
            return WriteHandle(t, id, n, mode, dst);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    /// <summary>
    ///     Whether the mailbox's next entry is ordered after this subscription's own state, which its client hasn't reached
    ///     yet — then that state goes first, and the entry waits for it (<see cref="Tap.MailAfterOwn" />). Else, for a
    ///     rendezvous, its flags: whether what it names was missed.
    /// </summary>
    private bool WaitsOnOwn(Tap t, MailRing mail, out byte flags)
    {
        flags = 0;
        if (mail.TryPeekConstraint(out var target, out var version))
        {
            if (ReferenceEquals(target, t) || ReferenceEquals(target, t.Source))
            {
                if (!t.HasReached(version))
                {
                    Volatile.Write(ref t.MailAfterOwn, version);
                    return true;
                }

                if (mail.NextKind == MailRing.Rendezvous && t.Missed(version)) flags = Wire.FlagMissed;
            }
            else if (mail.NextKind == MailRing.Rendezvous && MissedElsewhere(t, target, version))
            {
                flags = Wire.FlagMissed;
            }
        }

        Volatile.Write(ref t.MailAfterOwn, 0);
        return false;
    }

    /// <summary>
    ///     A rendezvous after another of the client's taps, or a source: missed if no tap of its holds that state (none,
    ///     ended, oversize) or one missed the message.
    /// </summary>
    private bool MissedElsewhere(Tap t, object? target, long version)
    {
        if (target is Tap one) return !one.IsActive || one.Oversize || one.Missed(version);
        var held = false;
        foreach (var u in Volatile.Read(ref _taps))
        {
            if (ReferenceEquals(u, t) || !ReferenceEquals(u.Source, target) || !u.IsActive || u.Oversize) continue;
            if (u.Missed(version)) return true;
            held = true;
        }

        return !held;
    }

    /// <summary>
    ///     A tap's record in the frame being built: counted, and kept in its history (its ring made at its first). A record
    ///     of its state — <paramref name="from" /> the version it started from — keeps the provider's upstream versions too:
    ///     the one it's as of, and the one its changes are since (none for a whole state).
    /// </summary>
    private void Recorded(Tap t, ReadOnlySpan<byte> record, long from = long.MinValue)
    {
        _builtRecords++;
        ReleaseHoldsBy(t); // an entry ordered after its state re-checks
        var handle = record[0] == Wire.OobRef && (record[6] & Wire.OobModeMask) == Wire.OobTransparent;
        var flight = t.Flight ??= new Flight(t.Start);
        flight.Record(_frameSeq + 1, t.Cursor, Volatile.Read(ref t.Mail) is { } mail ? mail.Sent : 0, handle,
            Volatile.Read(ref _confirmed));
        if (t.SentPending is not null && flight.Received)
            t.SentPending = null; // the client has the provider's snapshot
        if (_options.MessageHistory <= 0) return;
        var retention = t.Source.RetentionFor(_options.RecordRetention);
        long at = t.Cursor.Seq, upstream = MessageLog.None, since = MessageLog.None;
        if (from != long.MinValue && at != from &&
            at > 0) // it moved the cursor: a record of the state, as of version `at`
        {
            upstream = t.Source.UpstreamAt(at) ?? MessageLog.None;
            if (from > 0)
                since = from == t.RecordedAt
                    ? t.RecordedUpstream
                    : t.Source.UpstreamAt(from) ?? MessageLog.None; // (a whole state: none)
            (t.RecordedAt, t.RecordedUpstream) = (at, upstream);
        }

        (t.History ??= NewHistory(t, retention)).Write(_frameSeq + 1, record.Length, at, t.Source.ChangedAt, _builtAt,
            record, retention, upstream, since);
    }

    /// <summary>
    ///     A tap's history, at its first record: with a ring of whole records if they're kept whole and its history
    ///     budget has room.
    /// </summary>
    private MessageLog NewHistory(Tap t, RecordRetention retention)
    {
        var ring = _options.HistoryBytes;
        if (retention != RecordRetention.Whole || ring <= 0 || !TryTakeHistory(ring))
        {
            ring = 0;
        }
        else
        {
            t.HistoryRing = ring;
            if (t.IsClosed) GiveBackHistory(t); // closed meanwhile: its close may have given back before this was taken
        }

        return new MessageLog(_options.MessageHistory, ring);
    }

    private bool TryTakeHistory(int bytes)
    {
        for (long held; (held = Volatile.Read(ref _historyHeld)) + bytes <= _options.HistoryBudget;)
            if (Interlocked.CompareExchange(ref _historyHeld, held + bytes, held) == held)
                return true;
        return false;
    }

    private void GiveBackHistory(Tap t)
    {
        if (Interlocked.Exchange(ref t.HistoryRing, 0) is > 0 and var ring) Interlocked.Add(ref _historyHeld, -ring);
    }

    // ───────────────────────────── the viewer's ─────────────────────────────

    /// <summary>
    ///     This outlet as the viewer lists it: its session, connection, flow, taps and frames — read without locks, best
    ///     effort.
    /// </summary>
    public OutletView Describe()
    {
        return new OutletView(Id, Serial, CreatedAt, Session, Volatile.Read(ref _taps).Length,
            Volatile.Read(ref _frameSeq),
            Volatile.Read(ref _acked),
            Rtt.TotalMilliseconds, MinRtt.TotalMilliseconds, SmoothedRtt.TotalMilliseconds, DeliveryRate, InflightBytes,
            StreamingBytesPerSecond, MaxFramesPerSecond,
            MemoryBytes, MemoryBudget, WaitingMs(), _options.StallTimeout.TotalMilliseconds, Stalls, HistoryHeld,
            _options.HistoryBudget, StateHolds, HoldingState, Identity, SessionPolicy);
    }

    /// <summary>
    ///     Its subscriptions (taps), each with its last records (<see cref="OutletOptions.MessageHistory" />) — read
    ///     without locks, best effort.
    /// </summary>
    public IReadOnlyList<TapView> DescribeTaps(bool withMessages = true)
    {
        var views = new List<TapView>();
        foreach (var t in Volatile.Read(ref _taps))
            if (Describe(t, withMessages) is { } view)
                views.Add(view);
        return views;
    }

    /// <summary>One of its subscriptions, by tap id (null: none such now).</summary>
    public TapView? DescribeTap(uint id, bool withMessages = true)
    {
        foreach (var t in Volatile.Read(ref _taps))
            if (t.Id == id)
                return Describe(t, withMessages);
        return null;
    }

    private TapView Describe(Tap t, bool withMessages)
    {
        {
            var origin = t.Origin;
            var accepted = origin?.Holder is not null;
            return new TapView(t.Id, t.Topic, t.Source.Id, t.Source.Label,
                Volatile.Read(ref t.State) switch
                {
                    TapState.Bound => "bound",
                    TapState.Active when Volatile.Read(ref t.AwaitingInitial) != 0 =>
                        "active, awaiting the provider's initial state",
                    TapState.Active => "active", _ => "closed"
                },
                t.Lane?.ToString() ?? "Streaming", t.Initial.ToString(), Volatile.Read(ref t.Cursor.Seq),
                t.Source.Version, Clock.At(t.BoundAt),
                origin?.Service, origin?.AcceptedBy, origin?.NodeId, accepted ? origin!.Route : null,
                accepted ? origin!.Epoch : null,
                Volatile.Read(ref t.AwaitingFetch) != 0, withMessages ? t.History?.Read(_frameLog) ?? [] : [],
                Volatile.Read(ref t.Charged) + Volatile.Read(ref t.HistoryRing),
                accepted && origin!.Partition is not null ? origin.ServicePath : null,
                accepted ? origin!.PartitionId : null, Volatile.Read(ref t.Stale) != 0);
        }
    }

    /// <summary>
    ///     For the viewer: record <paramref name="n" /> of subscription <paramref name="tap" />, whole — if its history kept
    ///     it
    ///     so (<see cref="MessageView.Whole" />) and still holds it; null if not. A copy, read without locks.
    /// </summary>
    public byte[]? ReadRecord(uint tap, long n)
    {
        foreach (var t in Volatile.Read(ref _taps))
            if (t.Id == tap)
                return t.History?.ReadWhole(n);
        return null;
    }

    /// <summary>
    ///     For the viewer: a lease on a blob — an out-of-band record's content (<see cref="MessageView.Blob" />) — if it is
    ///     still
    ///     kept: its bytes held, by a reference counted like a client's fetch, until the lease is disposed (do so once read).
    ///     Since it is sent and viewed at different times, it may be <see cref="BlobLookup.Gone" /> by then (released after
    ///     its
    ///     last fetch, or evicted). Doesn't wait on the store: <see cref="BlobLookup.Busy" /> if another thread holds it.
    ///     Server-side, unauthenticated (a client's fetch proves its outlet's tag instead).
    /// </summary>
    public BlobLookup TryLeaseBlob(BlobId id, out BlobLease? lease)
    {
        return _blobs.TryLeaseNow(id, out lease);
    }

    /// <summary>Its last frames (64): sent when, how large, how many records, acked when — read without locks, best effort.</summary>
    public IReadOnlyList<FrameView> RecentFrames()
    {
        return _frameLog.Read(Volatile.Read(ref _frameSeq));
    }

    /// <summary>A source write that reported contention: nothing written, retry the tap shortly.</summary>
    private int Busy(Tap t, int written)
    {
        if (written != Source.Busy) return written;
        ScheduleRetry(t, TimeSpan.FromMilliseconds(1));
        return 0;
    }

    private int WriteHandle(Tap t, BlobId id, int length, byte mode, Span<byte> dst)
    {
        Span<byte> tag = stackalloc byte[16];
        _blobs.Tag(id, Id, Nonce, tag);
        uint ticket = 0;
        if ((mode & Wire.OobModeMask) == Wire.OobTransparent)
        {
            ticket = ++t.FetchTicket;
            Volatile.Write(ref t.AwaitingSince, Stopwatch.GetTimestamp());
            Volatile.Write(ref t.AwaitingFetch, 1);
        }

        Wire.WriteOobRef(dst, t.Id, t.Source.RecordType, mode, id, tag, length, ticket);
        _oobCharge += length;
        return Wire.OobRefSize;
    }

    /// <summary>
    ///     After serving (or dropping) a tap: leave the queue, then re-check — a change that landed after the tap's record
    ///     was written (and found it still queued, so didn't enqueue) puts it at the back, as a newly dirty tap. Through
    ///     the inbox, so it orders after anything producers pushed meanwhile.
    /// </summary>
    private void Settle(Tap t)
    {
        Interlocked.Exchange(ref t.Queued, 0); // full fence before re-reading its state (Dekker)
        if (t.HasWork && Interlocked.CompareExchange(ref t.Queued, 1, 0) == 0)
            ReadyOf(t).Push(t.Node); // same order as producers
    }

    /// <summary>
    ///     The tap's error policy: a degraded snapshot of the last good state (if the policy has a degrade function and the
    ///     source supports one), then the Error record — in the same frame. A degraded snapshot too large even for an empty
    ///     frame is dropped (error only). Returns -1 if it doesn't fit (it gets the next frame), or
    ///     <see cref="Source.FaultCleared" /> if the source's own fault is no longer <paramref name="seen" /> (null for an
    ///     injected fault: the source must still be healthy).
    /// </summary>
    private int WriteFault(Tap t, StreamFault fault, StreamFault? seen, Span<byte> dst, bool first, bool degrade = true)
    {
        var payload = t.ErrorPolicy is { } policy ? policy.Payload.Span : default;
        var errorSize = Wire.ErrorHeader + payload.Length;
        if (errorSize > dst.Length) return -1;
        // First in a frame that could be larger: as if not first — the degrade function runs only if it might fit here
        // (still once per faulted tap), else the next frame is a full-size one.
        var n = !degrade ? 0
            : t.ErrorPolicy is { } stale ? t.Source.WriteStale(t.Id, stale, seen, dst[..^errorSize], first && !_canGrow)
            : t.Source.WriteDegraded(t.Id, seen, null, dst[..^errorSize], first && !_canGrow);
        if (n == Source.FaultCleared || n == Source.Busy) return n;
        if (n < 0)
        {
            if (!first) return -1;
            if (_canGrow)
            {
                _wantLarge = true;
                return -1;
            }

            n = 0;
        }

        var e = dst[n..];
        e[0] = Wire.Error;
        BinaryPrimitives.WriteUInt32LittleEndian(e[1..], t.Id);
        BinaryPrimitives.WriteUInt16LittleEndian(e[5..], (ushort)fault.Code);
        BinaryPrimitives.WriteInt64LittleEndian(e[7..], fault.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(e[15..], (ushort)payload.Length);
        payload.CopyTo(e[Wire.ErrorHeader..]);
        return n + errorSize;
    }

    /// <summary>
    ///     A data provider instance holding <paramref name="t" />'s route is gone (deemed dead: silent, restarted, withdrawn).
    ///     The route's error policy — the provider's stale-data function, supplied when it accepted the route — is run here,
    ///     by the router, on the state it holds (authoritative: whatever a client might do itself), and the client gets, in
    ///     order and reliably (as a notice: until acknowledged, again after a reconnect), the degraded state, the error
    ///     (<see cref="FaultCode.ServiceDown" />, the policy's payload), then the service-down notice; the tap ends. A tap its
    ///     client hasn't activated gets the notice alone. A degraded state too large for a frame of the outlet's own is left
    ///     out (the error alone).
    /// </summary>
    internal void Down(Tap t, StreamFault fault, Guid nodeId, Guid correlation)
    {
        if (t.IsActive)
        {
            byte[]? records = null;
            var limit = SmallFrameBytes - Wire.MaxHeartbeatSize;
            var buffer = ArrayPool<byte>.Shared.Rent(limit);
            while (Interlocked.Exchange(ref _building, 1) != 0)
                Thread.SpinWait(1); // not mid-frame: the tap's cursor is the builder's
            var canGrow = _canGrow;
            try
            {
                _canGrow = false;
                var n = WriteFault(t, fault, null, buffer.AsSpan(0, limit), true,
                    Volatile.Read(ref t.Stale) == 0); // (stale already: shown)
                if (n <= 0)
                    n = WriteFault(t, fault, null, buffer.AsSpan(0, limit), false,
                        false); // (too large: the error alone)
                if (n > 0) records = buffer.AsSpan(0, n).ToArray();
            }
            finally
            {
                _canGrow = canGrow;
                Volatile.Write(ref _building, 0);
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (records is not null) Notify(ServiceNotice.OfRecords(records));
        }

        ServiceDown(nodeId, correlation);
        Close(t, fault);
    }

    /// <summary>
    ///     A data provider instance holding routes here is degraded — late to answer, or reporting itself unhealthy — or, with
    ///     a <paramref name="scope" />, a service path there is (as the client sees it); not gone. The client is told (a
    ///     Service record, the change's correlation id), then each of <paramref name="taps" /> — the routes it covers, not
    ///     stale already — its route's stale data (the policy the provider handed over, run here on the router's state),
    ///     reliably and in that order; their streams are held till it recovers (<see cref="ProviderRecovered" />) — a table to
    ///     go on from a snapshot, a value from its current one (the client shows the stale state meanwhile), a message stream
    ///     from where it was.
    /// </summary>
    internal void ProviderDegraded(Guid nodeId, string? scope, Guid correlation, IReadOnlyList<Tap> taps)
    {
        Notify(new ServiceNotice(Wire.ServiceDegraded, nodeId, null, Correlation: correlation, Scope: scope));
        var limit = SmallFrameBytes - Wire.MaxHeartbeatSize;
        foreach (var t in taps)
        {
            if (t.IsClosed || Interlocked.Exchange(ref t.Stale, 1) != 0) continue;
            byte[]? records = null;
            var buffer = ArrayPool<byte>.Shared.Rent(limit);
            while (Interlocked.Exchange(ref _building, 1) != 0)
                Thread.SpinWait(1); // not mid-frame: its cursor is the builder's
            try
            {
                var n = 0;
                if (t.IsActive && t.ErrorPolicy is { } policy)
                    for (var spinner = new SpinWait();
                         spinner.Count < 200;
                         spinner.SpinOnce(-1)) // rare: a provider degraded
                        if ((n = t.Source.WriteStale(t.Id, policy, t.Source.Fault, buffer.AsSpan(0, limit), true)) !=
                            Source.Busy)
                            break;
                if (n > 0) records = buffer.AsSpan(0, n).ToArray();
                if (t.Source.RecordType != Wire.TopicMessage)
                {
                    t.Cursor = Cursor.Snapshot; // what it shows is stale: on recovery, the state as it is then
                    t.Flight?.Restart(Cursor.Snapshot);
                }
            }
            finally
            {
                Volatile.Write(ref _building, 0);
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (records is not null) Notify(ServiceNotice.OfRecords(records));
        }
    }

    /// <summary>
    ///     That instance, or a scope there, is healthy again: its clients told, the held streams of
    ///     <paramref name="taps" /> (degraded by nothing else) go on.
    /// </summary>
    internal void ProviderRecovered(Guid nodeId, string? scope, Guid correlation, IReadOnlyList<Tap> taps)
    {
        Notify(new ServiceNotice(Wire.ServiceRecovered, nodeId, null, Correlation: correlation, Scope: scope));
        foreach (var t in taps)
            if (Interlocked.Exchange(ref t.Stale, 0) != 0)
                t.MarkDirty();
        ReleaseHolds(); // entries ordered after their state may go, once it's sent
    }

    /// <summary>
    ///     A data provider instance this client had routes through is gone (its routes closed): queues a service-down notice,
    ///     a control record at the head of the next frame (as a heartbeat, without credit), until a frame carrying it is
    ///     acked.
    /// </summary>
    internal void ServiceDown(Guid nodeId, Guid correlation)
    {
        Notify(new ServiceNotice(Wire.ServiceDown, nodeId, null, Correlation: correlation));
    }

    /// <summary>
    ///     A data provider instance this client had routes through is draining and has let them go: subscribe again
    ///     (elsewhere).
    /// </summary>
    internal void ServiceDrained(Guid nodeId, Guid correlation)
    {
        Notify(new ServiceNotice(Wire.ServiceDrained, nodeId, null, Correlation: correlation));
    }

    /// <summary>
    ///     A data provider instance this client has routes through is draining: move — subscribe again (elsewhere), and let
    ///     those go once the new ones stream; they stay open for <paramref name="grace" />.
    /// </summary>
    internal void ServiceDraining(Guid nodeId, Guid correlation, TimeSpan grace)
    {
        Notify(new ServiceNotice(Wire.ServiceDraining, nodeId, null,
            (uint)Math.Min(uint.MaxValue, grace.TotalMilliseconds), Correlation: correlation));
    }

    /// <summary>
    ///     One of this client's subscriptions through a data provider instance (tap <paramref name="tapId" />) is to move —
    ///     the instance rebalancing, not retiring: subscribe again (the router places it by load), and let the old go once
    ///     the new streams; it stays open for <paramref name="grace" />.
    /// </summary>
    internal void ServiceMove(Guid nodeId, Guid correlation, TimeSpan grace, uint tapId)
    {
        Notify(new ServiceNotice(Wire.ServiceMove, nodeId, null,
            (uint)Math.Min(uint.MaxValue, grace.TotalMilliseconds), From: tapId, Correlation: correlation));
    }

    /// <summary>
    ///     A subscription of this client's closed by the router, told by an Ended record naming it alone (sent as notices
    ///     are, till acknowledged): the client fails it with <paramref name="code" />.
    /// </summary>
    internal void Ended(uint tapId, FaultCode code)
    {
        Notify(ServiceNotice.Ended(code, tapId, tapId));
    }

    /// <summary>
    ///     A service this client was waiting on (told it was down, or found it unavailable) is up again — by name, no
    ///     instance:
    ///     queued as <see cref="ServiceDown" /> is, after it.
    /// </summary>
    internal void ServiceUp(string service, Guid correlation)
    {
        Notify(new ServiceNotice(Wire.ServiceUp, default, service, Correlation: correlation));
    }

    /// <summary>
    ///     A provider refused a subscription of this client's, saying why: a Refused record — sent as notices are, till
    ///     acknowledged — for its log.
    /// </summary>
    internal void Refused(SubscriptionRefusal refusal)
    {
        Notify(ServiceNotice.OfRecords(RefusedRecord.Of(refusal)));
    }

    private void Notify(ServiceNotice notice)
    {
        lock (_noticeLock)
        {
            _notices ??= [];
            var state = notice.Status is Wire.ServiceDegraded or Wire.ServiceRecovered
                or ServiceNotice.RecordsStatus; // each in its place: never merged
            for (var i = 0; i < _notices.Count && !state; i++)
                if (_notices[i].SentIn == 0 && _notices[i].Notice == notice)
                {
                    if (i == _notices.Count - 1) return; // queued already…
                    _notices.RemoveAt(i); // …but before what was queued since (a route's degraded state): after it now
                    break;
                }

            _notices.Add((notice, 0));
            Volatile.Write(ref _noticesUnsent, 1);
        }

        Wake();
    }

    /// <summary>In the frame being built (seq: the next): drops notices acked, writes those not yet sent (a few a frame).</summary>
    private int WriteNotices(Span<byte> dst)
    {
        lock (_noticeLock)
        {
            if (_notices is not { } notices) return 0;
            long acked = Volatile.Read(ref _acked), seq = _frameSeq + 1;
            for (var i = notices.Count - 1; i >= 0; i--)
                if (notices[i].SentIn != 0 && notices[i].SentIn <= acked)
                    notices.RemoveAt(i); // delivered
            int pos = 0, written = 0, unsent = 0;
            for (var i = 0; i < notices.Count; i++)
            {
                if (notices[i].SentIn != 0) continue;
                var notice = notices[i].Notice;
                if (unsent > 0 || written == NoticesPerFrame || pos + notice.Size > dst.Length)
                {
                    unsent++;
                    continue;
                } // in order: none overtakes

                pos += notice.Write(dst[pos..]);
                notices[i] = (notice, seq);
                written++;
            }

            if (notices.Count == 0) _notices = null;
            Volatile.Write(ref _noticesUnsent, unsent == 0 ? 0 : 1);
            return pos;
        }
    }

    /// <summary>A new connection: notices sent in frames not acked (perhaps lost with the old one) go again.</summary>
    private void ResendNotices(long acked)
    {
        lock (_noticeLock)
        {
            if (_notices is not { } notices) return;
            for (var i = 0; i < notices.Count; i++)
                if (notices[i].SentIn > acked)
                {
                    notices[i] = (notices[i].Notice, 0);
                    Volatile.Write(ref _noticesUnsent, 1);
                }
        }
    }

    private int WriteHeartbeat(Span<byte> dst)
    {
        var seq = _heartbeatSeq + 1;
        Volatile.Write(ref _heartbeatSentAt, Stopwatch.GetTimestamp());
        Volatile.Write(ref _heartbeatEchoed, 0);
        Volatile.Write(ref _heartbeatSeq, seq);
        dst[0] = Wire.Heartbeat;
        BinaryPrimitives.WriteUInt32LittleEndian(dst[1..], seq);
        BinaryPrimitives.WriteInt64LittleEndian(dst[5..], (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10);
        var health = _registry.Health; // the router's state: behind each heartbeat till its client has it (an echo)
        Volatile.Write(ref _healthSent, health);
        return ReferenceEquals(health, Volatile.Read(ref _healthKnown))
            ? Wire.HeartbeatSize
            : Wire.HeartbeatSize + health.WriteTo(dst[Wire.HeartbeatSize..], _time);
    }

    // Frames are built into a small buffer of the outlet's own — a heartbeat plus the frame budget or a 64 KB message,
    // whichever is larger: under the large object heap's threshold by default — from the registry's pool at the first
    // build (an outlet that never sends, a prerender's, takes none). A frame whose first record needs more (an Inline
    // source; out of band switched off) is built, the frame after, into one of MaxFrame rented from the shared pool. Each
    // buffer is counted: the outlet's hold (the large one's until the next build, its own until dispose), and the frame
    // that went out in it, until the transport releases it (ReleaseFrame; at the latest, the next build). The last
    // reference returns it — so a dispose while a frame is being copied leaves the buffer to the copy, not to the pool.
    private struct FrameBuffer
    {
        public byte[]? Buffer;
        public int Refs;
    }

    private sealed record RateLimit(long BytesPerSecond, long BurstBytes);
}