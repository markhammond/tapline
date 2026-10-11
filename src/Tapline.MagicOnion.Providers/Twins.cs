using System.Runtime.InteropServices;
using System.Text;
using Tapline.Client;

namespace Tapline.MagicOnion.Providers;

/// <summary>
///     The router's copy of a topic of a provider in another process: the source the router's own routes are bound to, fed
///     by a subscription to the topic at the provider's router (one tap there per router, however many routes here). Its
///     state is the provider's, as that router sends it: tables row by row, checksum verified on arrival and again as
///     applied here; values whole.
/// </summary>
internal abstract class Twin
{
    /// <summary>The router's routes on it: the feed goes when the last does.</summary>
    public int Routes;

    /// <summary>The subscription feeding it, at the provider's router.</summary>
    public Subscription? Feed;

    /// <summary>The tap it's fed from at the provider's router, and its key here (the provider instance's, the topic's id there).</summary>
    public uint Tap;

    public (Guid Instance, long Topic) Key;

    /// <summary>Called when its feed ends: no route is bound to it after.</summary>
    public Action? Gone;

    /// <summary>The provider's version of the topic, as far as it's been posted here (-1: nothing yet).</summary>
    public long Posted = -1;

    public abstract Source Source { get; }

    /// <summary>Its first state, applied here: the router binds its routes once it is, so none starts empty.</summary>
    public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>What the feed's records do here.</summary>
    public abstract ISubscriptionHandler Handler { get; }

    /// <summary>Called as each record is posted here, with the provider's version it's as of: what waits on it goes on.</summary>
    public Action<long>? Posting;

    /// <summary><paramref name="then" />, with this copy's version, once all that's been posted here is applied.</summary>
    public abstract void WhenApplied(Action<long> then);

    /// <summary>A rendezvous after all that's been posted here.</summary>
    public abstract bool TryRendezvous(Mailbox mailbox, ReadOnlySpan<byte> correlation);

    /// <summary>
    ///     A copy of a topic of the kind the answer says (its <see cref="TopicView.Kind" />): a table of its key and row
    ///     sizes, or a value. Null for a kind it can't copy.
    /// </summary>
    public static Twin? Of(string? kind, int keySize, int rowSize, string name)
    {
        if (kind == "value") return new ValueTwin(name);
        if (kind != "table" || keySize < 1 || rowSize < 1) return null;
        var type = typeof(TableTwin<,>).MakeGenericType(Bytes.KeyOf(keySize), Bytes.Of(rowSize));
        return (Twin)Activator.CreateInstance(type, name)!;
    }

    /// <summary>
    ///     The feed ended: no route is bound to this copy from now on. By a fault there, the same fault here, for the
    ///     routes on it; by its own end (the provider let its tap go: no routes left), nothing more.
    /// </summary>
    protected void Ended(TapError error)
    {
        Gone?.Invoke();
        if (error.Code is FaultCode.None or FaultCode.Gone) return;
        Source.Fail(error.Code, error.Payload.IsEmpty ? null : Encoding.UTF8.GetString(error.Payload.Span));
    }
}

/// <summary>A table's copy: each record posted to its ingest — a snapshot whole, a delta's rows replaced or deleted.</summary>
internal sealed class TableTwin<TKey, TRow> : Twin
    where TKey : unmanaged, IEquatable<TKey>
    where TRow : unmanaged
{
    private const int Staging = 64 << 20; // (grown as it's needed, not made)
    private readonly TableIngest<TKey, TRow, Change<TRow>> _ingest;
    private readonly Table<TKey, TRow> _table;
    private Change<TRow>[] _changes = new Change<TRow>[64];
    private TKey[] _keys = new TKey[64];
    private TRow[] _rows = new TRow[64];

    public TableTwin(string name)
    {
        _table = new Table<TKey, TRow>(name);
        _ingest = _table.Ingest<Change<TRow>>(BatchFold.Of<TRow, Change<TRow>, ReplaceOrDelete<TRow>>(), Staging);
        Handler = new SubscriptionHandler { Table = Apply, Error = (_, error) => Ended(error) };
    }

    public override Source Source => _table;
    public override ISubscriptionHandler Handler { get; }

    public override void WhenApplied(Action<long> then)
    {
        _ingest.WhenApplied(then);
    }

    public override bool TryRendezvous(Mailbox mailbox, ReadOnlySpan<byte> correlation)
    {
        return _ingest.TryRendezvous(mailbox, correlation);
    }

    /// <summary>A record from the provider's router, posted here: under the feeding client's lock, never waiting.</summary>
    private void Apply(Subscription subscription, TableReplica replica, TableBatch batch)
    {
        if (batch.Degraded) return; // (the provider's router's stale data: the fault behind it is ours to apply)
        var count = batch.Count;
        if (_keys.Length < count)
        {
            var size = Math.Max(count, _keys.Length * 2);
            (_keys, _rows, _changes) = (new TKey[size], new TRow[size], new Change<TRow>[size]);
        }

        var n = 0;
        PublishResult posted;
        if (batch.IsSnapshot)
        {
            foreach (var entry in batch)
            {
                _keys[n] = MemoryMarshal.Read<TKey>(entry.Key);
                _rows[n++] = MemoryMarshal.Read<TRow>(entry.Row);
            }

            posted = _ingest.PostSnapshot(_keys.AsSpan(0, n), _rows.AsSpan(0, n), batch.ToVersion, replica.Checksum);
        }
        else
        {
            foreach (var entry in batch)
            {
                _keys[n] = MemoryMarshal.Read<TKey>(entry.Key);
                _changes[n++] = entry.Deleted
                    ? new Change<TRow>(default, 1)
                    : new Change<TRow>(MemoryMarshal.Read<TRow>(entry.Row), 0);
            }

            posted = _ingest.Post(_keys.AsSpan(0, n), _changes.AsSpan(0, n), upstreamVersion: batch.ToVersion,
                expectedChecksum: replica.Checksum);
            if (posted.Status == PublishStatus.Full) Whole(replica, batch.ToVersion); // (behind: the whole state instead)
        }

        Posted = batch.ToVersion;
        if (!Ready.Task.IsCompleted) _ingest.WhenApplied(_ => Ready.TrySetResult());
        Posting?.Invoke(batch.ToVersion);
    }

    /// <summary>The replica's whole state as a snapshot, superseding what's staged: the router fell that far behind.</summary>
    private void Whole(TableReplica replica, long version)
    {
        if (_keys.Length < replica.Count)
        {
            var size = Math.Max(replica.Count, _keys.Length * 2);
            (_keys, _rows, _changes) = (new TKey[size], new TRow[size], new Change<TRow>[size]);
        }

        var n = 0;
        foreach (var row in replica)
        {
            _keys[n] = MemoryMarshal.Read<TKey>(row.Key.Span);
            _rows[n++] = MemoryMarshal.Read<TRow>(row.Row.Span);
        }

        _ingest.PostSnapshot(_keys.AsSpan(0, n), _rows.AsSpan(0, n), version, replica.Checksum);
    }
}

/// <summary>A value's copy: each value published here as it comes.</summary>
internal sealed class ValueTwin : Twin
{
    private readonly LastValueSource _value;

    public ValueTwin(string name)
    {
        _value = new LastValueSource { Label = name };
        Handler = new SubscriptionHandler { Value = Apply, Error = (_, error) => Ended(error) };
    }

    public override Source Source => _value;
    public override ISubscriptionHandler Handler { get; }

    public override void WhenApplied(Action<long> then)
    {
        then(_value.Version); // (published at once: nothing staged)
    }

    public override bool TryRendezvous(Mailbox mailbox, ReadOnlySpan<byte> correlation)
    {
        return mailbox.TryRendezvous(correlation, _value.Version);
    }

    private void Apply(Subscription subscription, ReadOnlyMemory<byte> value)
    {
        _value.Publish(value.Span);
        Posted = long.MaxValue; // (a value carries no version of the provider's here: whatever waits on one goes on)
        Ready.TrySetResult();
        Posting?.Invoke(Posted);
    }
}

/// <summary>A row's change, as a provider's router sends it: replaced by <see cref="Row" />, or deleted.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct Change<TRow>(TRow row, byte deleted) where TRow : unmanaged
{
    public readonly TRow Row = row;
    public readonly byte Deleted = deleted;
}

/// <summary>The fold of a copy: each row as its change says, replaced or deleted.</summary>
internal readonly struct ReplaceOrDelete<TRow> : IRowFold<TRow, Change<TRow>> where TRow : unmanaged
{
    public static bool Fold(ref TRow row, bool exists, in Change<TRow> change)
    {
        if (change.Deleted != 0) return false;
        row = change.Row;
        return true;
    }
}
