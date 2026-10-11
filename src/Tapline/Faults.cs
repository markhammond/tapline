using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tapline;

/// <summary>Why a stream stopped being trustworthy. Sent as <c>u16</c> in Error records and <c>Tap.Close</c>.</summary>
public enum FaultCode : ushort
{
    /// <summary>Not a fault: a normal close (only meaningful in <c>Tap.Close</c>).</summary>
    None = 0,
    Unknown = 1,

    /// <summary>An update was addressed to a different upstream version than the source holds (a gap or reordering upstream).</summary>
    VersionMismatch = 2,

    /// <summary>The state after an update (or a client's replica) did not match the expected checksum.</summary>
    ChecksumMismatch = 3,

    /// <summary>A fold threw (trap, fuel exhausted, bug); its transaction was rolled back.</summary>
    FoldFailed = 4,

    /// <summary>The publisher declared the source failed (<see cref="Source.Fail" />), e.g. its upstream disconnected.</summary>
    SourceFailed = 5,

    /// <summary>
    ///     The data provider instance holding the subscription is gone — it stopped answering the router's heartbeats,
    ///     restarted (a new instance id), or was withdrawn: the route is closed; resubscribe (step 1 again).
    /// </summary>
    ServiceDown = 6,

    /// <summary>
    ///     The data provider instance holding the subscription is draining and has let it go — not down: subscribe again now
    ///     (another instance takes it); if none does, wait for the service to be up.
    /// </summary>
    ServiceDrained = 7,

    /// <summary>
    ///     A message stream outran the subscriber: a message it hadn't been sent was evicted from its topic's buffer (or its
    ///     mailbox) — under <see cref="OverflowPolicy.Fault" />. Resubscribe (and recover what matters from a state topic).
    /// </summary>
    Overflowed = 8,

    /// <summary>
    ///     The client made no progress — connected, it acknowledged nothing for <see cref="OutletOptions.StallTimeout" />
    ///     while
    ///     there was something for it; or it was disconnected longer than <see cref="OutletOptions.DisconnectedHold" /> — and
    ///     the router ended its subscriptions (told in one <see cref="Wire.Ended" /> record). Subscribe again.
    /// </summary>
    Stalled = 9,

    /// <summary>
    ///     The router no longer holds the subscription and no longer remembers why it ended (more have ended on this client
    ///     since): told when the client activates it again, after a reconnect. Subscribe again.
    /// </summary>
    Gone = 10,

    /// <summary>
    ///     The data provider declared, accepting the subscription, that it would send the subscriber's initial state itself
    ///     (<see cref="InitialState.Provider" />), and hadn't finished it (<see cref="Mailbox.Ready" />) within the bind
    ///     timeout
    ///     after step 2. Subscribe again.
    /// </summary>
    NoInitialState = 11,

    /// <summary>
    ///     The connection's identity changed — another user signed in, or the one signed in signed out
    ///     (<see cref="Outlet.SetIdentity" />) — and the router ended the subscriptions made under the old one (those whose
    ///     providers were shown it). Subscribe again.
    /// </summary>
    IdentityChanged = 12,

    /// <summary>
    ///     The connection's identity lapsed (<see cref="ClientIdentity.ExpiresAt" />) without a renewal, and the service
    ///     requires a signed-in user of its realm. Sign in, then subscribe again.
    /// </summary>
    SessionExpired = 13,

    /// <summary>
    ///     The client was asked to move the subscription (a move: its instance rebalancing, not retiring) and still held it
    ///     when the grace it was given ran out: the router let it go. Subscribe again.
    /// </summary>
    Moved = 14
}

/// <summary>
///     A client's subscriptions would hold more than its <see cref="OutletOptions.MemoryBudget" />: the subscription (step
///     1)
///     is refused. What it holds is shown by <see cref="Outlet.MemoryBytes" />; unsubscribing frees it.
/// </summary>
public sealed class OutletFullException(int outlet, long held, long budget, long wanted)
    : InvalidOperationException(
        $"Client {outlet}'s subscriptions hold {held:N0} of its {budget:N0} bytes: no room for another {wanted:N0}.")
{
    public long Held { get; } = held;
    public long Budget { get; } = budget;
}

/// <summary>
///     A source's (or a tap's) terminal error. <see cref="Version" /> is the last good version: a faulted source still
///     holds
///     exactly that state (failed transactions are rolled back), which is what degraded snapshots are computed from.
/// </summary>
public sealed record StreamFault(FaultCode Code, long Version, string? Message = null, Exception? Exception = null);

public sealed class StreamFaultException(StreamFault fault)
    : Exception(fault.Message ?? fault.Code.ToString(), fault.Exception)
{
    public StreamFault Fault { get; } = fault;
}

/// <summary>
///     Turns a source's last good state into a safe one to show while the stream is broken. Called once per faulted tap,
///     on the server, with every live row packed little-endian:
///     <code>
/// entries  count × ( u32 flags | key[keySize] | row[rowSize] )      flags bit0 = present
/// </code>
///     Rewrite rows in place and clear the present bit to drop a row. Keys are read-only (changes are ignored). If the
///     function throws, the client gets an empty degraded state. Byte-level so a sandboxed (WebAssembly) module can
///     implement it; <see cref="DegradeFunction.For{TKey,TRow}" /> adapts a typed lambda.
/// </summary>
public interface IDegradeFunction
{
    void Degrade(Span<byte> entries, int count, int keySize, int rowSize);
}

/// <summary>Typed view of one degrade entry (same bytes as the ABI, so a span of entries is a cast, not a copy).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct RowEntry<TKey, TRow> where TKey : unmanaged where TRow : unmanaged
{
    public uint Flags;
    public TKey Key;
    public TRow Row;

    public bool Present
    {
        readonly get => (Flags & FoldEntry.Present) != 0;
        set => Flags = value ? Flags | FoldEntry.Present : Flags & ~FoldEntry.Present;
    }
}

public static class DegradeFunction
{
    /// <summary>Keeps every row as it is (the state replayed).</summary>
    internal static readonly IDegradeFunction Unchanged = new Keep();

    /// <summary>A degrade function over typed rows, e.g. keep the best bid and ask with size zero.</summary>
    public static IDegradeFunction For<TKey, TRow>(Action<Span<RowEntry<TKey, TRow>>> degrade)
        where TKey : unmanaged where TRow : unmanaged
    {
        return new Typed<TKey, TRow>(degrade);
    }

    private sealed class Keep : IDegradeFunction
    {
        public void Degrade(Span<byte> entries, int count, int keySize, int rowSize)
        {
        }
    }

    private sealed class Typed<TKey, TRow>(Action<Span<RowEntry<TKey, TRow>>> degrade) : IDegradeFunction
        where TKey : unmanaged where TRow : unmanaged
    {
        public void Degrade(Span<byte> entries, int count, int keySize, int rowSize)
        {
            if (keySize != Unsafe.SizeOf<TKey>() || rowSize != Unsafe.SizeOf<TRow>() ||
                Unsafe.SizeOf<RowEntry<TKey, TRow>>() != 4 + keySize + rowSize)
                throw new InvalidOperationException(
                    $"Degrade function for {typeof(TKey).Name}/{typeof(TRow).Name} applied to a {keySize}/{rowSize}-byte table.");
            degrade(MemoryMarshal.Cast<byte, RowEntry<TKey, TRow>>(entries)[..count]);
        }
    }
}

/// <summary>
///     A stale-data function that also has a portable form — a WebAssembly module, the same function — so a client can
///     apply it itself while the router is out of its reach (the router applies it otherwise: its state is the authority).
///     The module, fetched by its id from the router while connected (Tapline.AspNetCore: <c>{prefix}/module/{id}</c>),
///     imports
///     nothing and exports:
///     <code>
/// memory                                      its linear memory
/// buffer(size: i32) -> i32                    where to put size bytes of entries (growing its memory to fit)
/// degrade(ptr: i32, count: i32, keySize: i32, rowSize: i32)
///                                             as IDegradeFunction.Degrade, on the entries at ptr
/// </code>
///     A pure function: whether the router runs it in a fresh instance or one it ran before, the result must be the same.
/// </summary>
public interface IPortableDegradeFunction : IDegradeFunction
{
    /// <summary>The module (WebAssembly binary).</summary>
    ReadOnlyMemory<byte> Module { get; }

    /// <summary>Its content address: the first 16 bytes of its SHA-256, lowercase hex.</summary>
    string ModuleId { get; }
}

/// <summary>What a subscriber is shown while its data can't be trusted — its provider degraded or gone, its topic faulted.</summary>
public enum StaleData : byte
{
    /// <summary>
    ///     The state passed through a function (<see cref="ErrorPolicy.Degrade" />): e.g. the book's top, sizes zero. No
    ///     function: nothing.
    /// </summary>
    Function = 0,

    /// <summary>A fixed state the provider supplied (<see cref="ErrorPolicy.State" />): a value, a message, or a table's rows.</summary>
    Static = 1,

    /// <summary>The last value or message again (a table: its state as it is) — with the provider's state saying it's stale.</summary>
    ReplayLast = 2
}

/// <summary>
///     A subscription's policy for stale data — a provider hands it to the router with each route it accepts
///     (<see cref="SubscribeRequest.Accept" />), an app with each tap it binds. While the provider is degraded (late, or
///     unhealthy) the subscriber is shown the stale data and its stream held; when the provider is gone, or the topic
///     faults, the stale data then the error (with <see cref="Payload" />), and the subscription ends. The router applies
///     it,
///     on the state it holds: the authority, whatever a client might do itself. The client is handed it too, in the
///     subscription's binding (<see cref="TapBinding.Stale" />), to apply only while the router is out of its reach: a
///     fixed
///     state, the last again, or a function with a portable form (<see cref="IPortableDegradeFunction" />).
/// </summary>
public sealed class ErrorPolicy
{
    public const int MaxPayload = 8 * 1024;

    /// <summary>A fixed stale state's largest size (<see cref="Static" />): it rides in each subscription's binding.</summary>
    public const int MaxState = 64 * 1024;

    /// <param name="payload">Opaque bytes for the client (a message, a code for the UI...). At most <see cref="MaxPayload" />.</param>
    /// <param name="degrade">
    ///     The stale-data function: the state passed through it (<see cref="StaleData.Function" />). Null:
    ///     no stale data, the error only.
    /// </param>
    public ErrorPolicy(ReadOnlyMemory<byte> payload = default, IDegradeFunction? degrade = null)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, MaxPayload, nameof(payload));
        Payload = payload;
        Degrade = degrade;
    }

    public ReadOnlyMemory<byte> Payload { get; }
    public IDegradeFunction? Degrade { get; }
    public StaleData Kind { get; private init; }
    public ReadOnlyMemory<byte> State { get; private init; }

    /// <summary>
    ///     Stale data as a fixed state: <paramref name="state" /> — a value's bytes, a message, or a table's rows (count ×
    ///     key | row, packed as the table's own). At most <see cref="MaxState" />.
    /// </summary>
    public static ErrorPolicy Static(ReadOnlyMemory<byte> state, ReadOnlyMemory<byte> payload = default)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(state.Length, MaxState, nameof(state));
        return new ErrorPolicy(payload) { Kind = StaleData.Static, State = state };
    }

    /// <summary>Stale data as the last value or message, again (a table: its state as it is).</summary>
    public static ErrorPolicy ReplayLast(ReadOnlyMemory<byte> payload = default)
    {
        return new ErrorPolicy(payload) { Kind = StaleData.ReplayLast };
    }
}

/// <summary>
///     For testing error handling: a fault lifecycle shared by the outlets that join it (test connections). While it is
///     failed, every source looks faulted to those outlets only — each active tap sends its error policy (a degraded
///     snapshot
///     of the source's current state, then the Error record) and ends, taps activated meanwhile end at once, and
///     <see cref="Outlet.FaultOf" /> reports the fault — through the same data pump as a real source fault, for tables,
///     last-value sources and watches alike. Sources and outlets that haven't joined are untouched.
/// </summary>
public sealed class FaultInjector
{
    private readonly Lock _lock = new();
    private StreamFault? _fault;
    private Outlet[] _outlets = [];

    /// <summary>The injected fault, null while healthy. Its version is -1: each tap reports its source's version.</summary>
    public StreamFault? Fault => Volatile.Read(ref _fault);

    /// <summary>Fails every source for the joined outlets (no-op while already failed).</summary>
    public void Fail(FaultCode code, string? message = null)
    {
        if (code == FaultCode.None) throw new ArgumentOutOfRangeException(nameof(code));
        if (Interlocked.CompareExchange(ref _fault, new StreamFault(code, -1, message), null) is not null)
            return; // full fence before the taps look
        foreach (var o in Volatile.Read(ref _outlets)) o.MarkAllDirty();
    }

    /// <summary>Ends the injected fault: taps bound from now on stream normally (the faulted ones have ended).</summary>
    public void Recover()
    {
        Volatile.Write(ref _fault, null);
    }

    internal void Join(Outlet outlet)
    {
        lock (_lock)
        {
            _outlets = [.. _outlets, outlet];
        }
    }

    internal void Leave(Outlet outlet)
    {
        lock (_lock)
        {
            var i = Array.IndexOf(_outlets, outlet);
            if (i >= 0) _outlets = [.. _outlets.AsSpan(0, i), .. _outlets.AsSpan(i + 1)];
        }
    }
}