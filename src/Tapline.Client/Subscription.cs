namespace Tapline.Client;

/// <summary>
///     A subscription of a <see cref="TaplineClient" />: the tap step 1 bound (its <see cref="Binding" />), activated and
///     streaming to its handler, and its state now. Dispose to unsubscribe.
/// </summary>
public sealed class Subscription : IDisposable
{
    private readonly TaplineClient _client;
    internal int FetchFailures;
    internal long Generation; // records seen: an out-of-band fetch applies only if none arrived since
    internal uint? Replaces; // make before break: the tap to drop at this one's first state
    internal Guid MovedBy; // the last move it was told of (its correlation id): told once

    internal Subscription(TaplineClient client, TapBinding binding, ISubscriptionHandler handler)
    {
        (_client, Binding, Handler) = (client, binding, handler);
    }

    /// <summary>The step-1 answer it was made from.</summary>
    public TapBinding Binding { get; }

    /// <summary>Its tap's id (the router's, on this client's outlet).</summary>
    public uint TapId => Binding.TapId;

    /// <summary>Its topic's name, if it has one.</summary>
    public string? Topic => Binding.Topic;

    /// <summary>
    ///     The state of the data provider it comes through — or null for one bound directly (no provider):
    ///     <see cref="ProviderState.Unreachable" /> while the router is out of this client's reach.
    /// </summary>
    public ProviderState? Provider => _client.ProviderOf(this);

    /// <summary>Over: ended with an error (<see cref="ISubscriptionHandler.OnError" />), or unsubscribed.</summary>
    public bool IsEnded { get; internal set; }

    /// <summary>A table's: its replica, once its first snapshot came.</summary>
    public TableReplica? Table { get; internal set; }

    /// <summary>A value's: its latest (the client's copy), once it came — empty before.</summary>
    public ReadOnlyMemory<byte> Value { get; internal set; }

    /// <summary>A value's sent by reference (out of band): its handle, the bytes fetched on demand.</summary>
    public BlobRef? Blob { get; internal set; }

    /// <summary>A watch's: its members, once its first cut came.</summary>
    public GroupReplica? Group { get; internal set; }

    internal ISubscriptionHandler Handler { get; }

    /// <summary>Unsubscribes: the router told, nothing more handed over.</summary>
    public void Dispose()
    {
        _client.Drop(TapId);
    }
}

/// <summary>
///     What a subscription hands over, as it comes — each on the thread that received it (a frame's, or an out-of-band
///     fetch's), one at a time, in order, under the client's lock: don't block in them — nor wait on a lock of the app's
///     that
///     a thread holds while calling the client (its status reads wait on nothing). A method not implemented ignores it.
/// </summary>
public interface ISubscriptionHandler
{
    /// <summary>
    ///     A table's record applied — a snapshot or a delta — and its rows now (<paramref name="batch" />: during the
    ///     call only).
    /// </summary>
    void OnTable(Subscription subscription, TableReplica table, TableBatch batch)
    {
    }

    /// <summary>A value: its latest (the client's copy).</summary>
    void OnValue(Subscription subscription, ReadOnlyMemory<byte> value)
    {
    }

    /// <summary>A value sent by reference (out of band): fetch it when, or if, it's needed.</summary>
    void OnBlob(Subscription subscription, BlobRef blob)
    {
    }

    /// <summary>A watch's coherent cut: every member applied.</summary>
    void OnGroup(Subscription subscription, GroupReplica group)
    {
    }

    /// <summary>
    ///     A message of a topic, or of the subscription's mailbox — in order, once (<paramref name="message" />: during
    ///     the call only).
    /// </summary>
    void OnMessage(Subscription subscription, ReadOnlySpan<byte> message, MessageInfo info)
    {
    }

    /// <summary>The subscription ended, with an error (the router's, or this client's): nothing more comes.</summary>
    void OnError(Subscription subscription, TapError error)
    {
    }
}

/// <summary>A table's handler: its record applied, and its rows now.</summary>
public delegate void TableHandler(Subscription subscription, TableReplica table, TableBatch batch);

/// <summary>A message's handler.</summary>
public delegate void MessageHandler(Subscription subscription, ReadOnlySpan<byte> message, MessageInfo info);

/// <summary>An <see cref="ISubscriptionHandler" /> of delegates, for lambdas: those not set ignore what they'd handle.</summary>
public sealed class SubscriptionHandler : ISubscriptionHandler
{
    public TableHandler? Table { get; init; }
    public Action<Subscription, ReadOnlyMemory<byte>>? Value { get; init; }
    public Action<Subscription, BlobRef>? Blob { get; init; }
    public Action<Subscription, GroupReplica>? Group { get; init; }
    public MessageHandler? Message { get; init; }
    public Action<Subscription, TapError>? Error { get; init; }

    void ISubscriptionHandler.OnTable(Subscription subscription, TableReplica table, TableBatch batch)
    {
        Table?.Invoke(subscription, table, batch);
    }

    void ISubscriptionHandler.OnValue(Subscription subscription, ReadOnlyMemory<byte> value)
    {
        Value?.Invoke(subscription, value);
    }

    void ISubscriptionHandler.OnBlob(Subscription subscription, BlobRef blob)
    {
        Blob?.Invoke(subscription, blob);
    }

    void ISubscriptionHandler.OnGroup(Subscription subscription, GroupReplica group)
    {
        Group?.Invoke(subscription, group);
    }

    void ISubscriptionHandler.OnMessage(Subscription subscription, ReadOnlySpan<byte> message, MessageInfo info)
    {
        Message?.Invoke(subscription, message, info);
    }

    void ISubscriptionHandler.OnError(Subscription subscription, TapError error)
    {
        Error?.Invoke(subscription, error);
    }
}

/// <summary>
///     A data provider's state, as the router tells it: serving; degraded (late, or unhealthy — its subscriptions shown
///     their stale data, and held); draining (move: subscribe again, then let these go); down; drained — or the router
///     itself
///     out of this client's reach.
/// </summary>
public enum ProviderState
{
    Serving,
    Degraded,
    Draining,
    Down,
    Drained,
    Unreachable
}

/// <summary>
///     A change of a node's state — a data provider instance, by its node id — or, with a <see cref="Scope" />, of a
///     service
///     path's there, as the client sees it (the service, then its partition's ids): and the change's correlation id, the
///     one
///     the router logged it with.
/// </summary>
public readonly record struct ProviderChange(Guid NodeId, ProviderState State, string? Scope, Guid Correlation);

/// <summary>
///     A message's place: its number (its topic's, or the mailbox's), whether messages before it were skipped (a gap),
///     whether
///     it's the subscription's own (its mailbox), whether it's stale (shown again, not new), and the provider's state
///     then.
/// </summary>
public readonly record struct MessageInfo(long Seq, bool Gap, bool Mailbox, bool Stale, ProviderState? Provider);

/// <summary>
///     Why a subscription ended: its fault code, the last version it was good at, the error policy's payload — and whether
///     this client ended it (<see cref="Local" />: its stream couldn't be trusted), not the router.
/// </summary>
public sealed record TapError(FaultCode Code, long Version, ReadOnlyMemory<byte> Payload, bool Local);

/// <summary>
///     The router's last heartbeat: its number (echoed in each acknowledgement), when the router sent it, and when it
///     arrived (a <see cref="TimeProvider" /> timestamp).
/// </summary>
public readonly record struct Heartbeat(uint Seq, DateTimeOffset SentAt, long ReceivedAt);

/// <summary>
///     The router's own state, as its heartbeats carry it: <see cref="RouterState.Serving" />,
///     <see cref="RouterState.Degraded" />
///     (its <see cref="Reason" />) or <see cref="RouterState.Draining" /> (gone by <see cref="DrainsBy" />, this client's
///     clock) —
///     and the change that made it: its <see cref="Correlation" /> id (a UUIDv7), as the router logged it. Ids are the
///     router
///     run's: never persisted.
/// </summary>
public readonly record struct RouterStatus(
    RouterState State,
    Guid Correlation,
    string? Reason,
    DateTimeOffset? DrainsBy);

/// <summary>
///     A provider — or the router, for a service that requires a sign-in — refused a subscription of this client's, saying
///     why and of what kind (<see cref="RefusalKind.Unauthenticated" />: sign in, then subscribe again;
///     <see cref="RefusalKind.Forbidden" />: not allowed): its correlation id is the one the provider and the router logged
///     it with — a needle for each log.
/// </summary>
public sealed record Refusal(Guid CorrelationId, string Service, string Reason, RefusalKind Kind = RefusalKind.Refused);

/// <summary>
///     A rendezvous delivered: through which subscription, at which of its sequence numbers, and whether the state it
///     names was missed.
/// </summary>
public readonly record struct RendezvousInfo(uint TapId, long Seq, bool Missed);

/// <summary>A rendezvous awaited through a subscription that ended first.</summary>
public sealed class SubscriptionEndedException(uint tapId, FaultCode code)
    : Exception($"Subscription {tapId} ended before the rendezvous ({code}).")
{
    public uint TapId { get; } = tapId;
    public FaultCode Code { get; } = code;
}