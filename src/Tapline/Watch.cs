using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tapline;

/// <summary>Per-subscriber state of a key-range member: the hash of each row sent, their sum, the last version sent.</summary>
internal sealed class RangeState
{
    public readonly Dictionary<int, uint> Sent = new();
    public uint Checksum;
    public long SentSeq = -1;

    public void Reset()
    {
        Sent.Clear();
        Checksum = 0;
        SentSeq = -1;
    }
}

internal sealed class MemberState
{
    public Cursor Cursor = Cursor.Snapshot;
    public RangeState? Range;
}

/// <summary>
///     One member of a <see cref="Outlet.Watch(string?, ErrorPolicy?, ReadOnlySpan{WatchMember})" />: a whole table, a key range of a table, or a last-value source. Members
///     are descriptions (reusable across subscriptions); each watch keeps its own cursors.
/// </summary>
public abstract class WatchMember
{
    private protected WatchMember(Source source, IDegradeFunction? degrade)
    {
        Source = source;
        Degrade = degrade;
    }

    public Source Source { get; }

    /// <summary>Degrades this member when the watch faults (tables only).</summary>
    internal IDegradeFunction? Degrade { get; }

    internal virtual MemberState NewState()
    {
        return new MemberState();
    }

    /// <summary>Exact size of the record <see cref="Write" /> would produce (0 = none). Called with the member's lock held.</summary>
    internal abstract int Measure(MemberState state);

    internal abstract int Write(int index, MemberState state, Span<byte> dst);

    /// <summary>Moves past changes that produce no record (e.g. outside a key range).</summary>
    internal virtual void Advance(MemberState state)
    {
    }

    internal virtual int WriteDegraded(int index, Span<byte> dst, bool first)
    {
        return 0;
    }

    public static WatchMember Of(LastValueSource source)
    {
        return new ValueMember(source);
    }

    public static WatchMember Of<TKey, TRow>(Table<TKey, TRow> table, IDegradeFunction? degrade = null)
        where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged
    {
        return new TableMember<TKey, TRow>(table, [], degrade);
    }

    /// <summary>The rows whose key starts with <paramref name="keyPrefix" />' bytes, e.g. one account's positions.</summary>
    public static WatchMember Range<TKey, TRow, TPrefix>(Table<TKey, TRow> table, TPrefix keyPrefix,
        IDegradeFunction? degrade = null)
        where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged where TPrefix : unmanaged
    {
        return Range(table, MemoryMarshal.AsBytes(new ReadOnlySpan<TPrefix>(in keyPrefix)), degrade);
    }

    /// <summary>The rows whose key bytes start with <paramref name="keyPrefix" />.</summary>
    public static WatchMember Range<TKey, TRow>(Table<TKey, TRow> table, ReadOnlySpan<byte> keyPrefix,
        IDegradeFunction? degrade = null)
        where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged
    {
        if (keyPrefix.IsEmpty || keyPrefix.Length > Unsafe.SizeOf<TKey>())
            throw new ArgumentOutOfRangeException(nameof(keyPrefix), "A key prefix is 1 to sizeof(TKey) bytes.");
        return new TableMember<TKey, TRow>(table, keyPrefix.ToArray(), degrade);
    }

    private sealed class ValueMember(LastValueSource source) : WatchMember(source, null)
    {
        internal override int Measure(MemberState state)
        {
            return Source.Measure(state.Cursor);
        }

        internal override int Write(int index, MemberState state, Span<byte> dst)
        {
            return Source.Write((uint)index, ref state.Cursor, dst);
        }
    }

    private sealed class TableMember<TKey, TRow>(Table<TKey, TRow> table, byte[] prefix, IDegradeFunction? degrade)
        : WatchMember(table, degrade)
        where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged
    {
        internal override MemberState NewState()
        {
            return new MemberState { Range = prefix.Length > 0 ? new RangeState() : null };
        }

        internal override int Measure(MemberState state)
        {
            return prefix.Length == 0 ? table.Measure(state.Cursor) : table.MeasureRange(state.Cursor, prefix);
        }

        internal override int Write(int index, MemberState state, Span<byte> dst)
        {
            return prefix.Length == 0
                ? table.Write((uint)index, ref state.Cursor, dst)
                : table.WriteRange((uint)index, ref state.Cursor, dst, prefix, state.Range!);
        }

        internal override void Advance(MemberState state)
        {
            if (prefix.Length > 0) table.WriteRange(0, ref state.Cursor, default, prefix, state.Range!);
        }

        internal override int WriteDegraded(int index, Span<byte> dst, bool first)
        {
            return table.WriteDegraded((uint)index, table.Fault, Degrade, dst, first, prefix);
        }
    }
}

/// <summary>
///     A watch: one subscriber's coherent view of several members. Its records are Group records holding every member that
///     changed, each a consistent cut: complete units of work only. Members are read one lock domain at a time
///     (<see cref="LockDomain" />: sources written together — a group ingest's, a transaction's), each domain under its
///     own
///     members' locks and nothing else's, so a watch never holds one source's lock while reading an unrelated one. Domains
///     are checked again after reading: one changed meanwhile (a join, a leave, a transaction merging them) could have let
///     a
///     unit span parts read apart, so the delivery is dropped and the watch resyncs under all its locks. While no domain
///     has
///     changed since its last delivery, a part whose locks are busy, or that doesn't fit the frame, waits for the next one
///     and
///     the rest go now (no unit can span parts that have been apart throughout); after a change, the next delivery reads
///     every
///     part, under all the locks at once. Per subscription (not shared).
/// </summary>
internal sealed class WatchGroup : Source
{
    private readonly WatchMember[] _members;
    private readonly int[] _partOf; // each source's part: its domain's, in order
    private readonly LockDomain?[] _planned; // each source's domain as this delivery found it
    private readonly long[] _plannedEpoch, _settledEpoch; // …its changes so far; and as of the last delivery checked
    private readonly bool[] _read;
    private readonly int[] _sizes;
    private readonly int[] _sourceOf; // each member's source (index into _sources)
    private readonly Source[] _sources; // distinct, in lock order
    private readonly MemberState[] _states;
    private int _incomplete; // a delivery left parts for the next: still has work
    private bool _settled;

    public WatchGroup(ReadOnlySpan<WatchMember> members)
    {
        if (members.IsEmpty || members.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(members), "A watch has 1 to 65535 members.");
        _members = members.ToArray();
        _states = new MemberState[_members.Length];
        for (var i = 0; i < _members.Length; i++)
        {
            if (_members[i].Source.Gate is null)
                throw new ArgumentException("Only tables and last-value sources can be watched.", nameof(members));
            _states[i] = _members[i].NewState();
        }

        _sizes = new int[_members.Length];
        _sources = _members.Select(m => m.Source).Distinct().OrderBy(s => s.SourceId).ToArray();
        _sourceOf = _members.Select(m => Array.IndexOf(_sources, m.Source)).ToArray();
        _planned = new LockDomain?[_sources.Length];
        _plannedEpoch = new long[_sources.Length];
        _settledEpoch = new long[_sources.Length];
        _partOf = new int[_sources.Length];
        _read = new bool[_sources.Length];
        Label = $"watch of {string.Join(", ", _sources.Select(s => s.Label ?? "?"))}";
    }

    /// <summary>When any member last changed.</summary>
    internal override long ChangedAt
    {
        get
        {
            long at = 0;
            foreach (var s in _sources) at = Math.Max(at, s.ChangedAt);
            return at;
        }
    }

    /// <summary>
    ///     Twice the sum of the members' versions — changes whenever any member changes (a tap's dirty test) — and one more
    ///     while a delivery has left parts for the next.
    /// </summary>
    public override long Version => 2 * Sum() + Volatile.Read(ref _incomplete);

    public override StreamFault? Fault
    {
        get
        {
            if (base.Fault is { } own) return own;
            foreach (var s in _sources)
                if (s.Fault is { } f)
                    return f;
            return null;
        }
    }

    internal override byte RecordType => Wire.Group;

    /// <summary>Worth compressing unless every member is incompressible.</summary>
    internal override bool IsCompressible
    {
        get
        {
            foreach (var s in _sources)
                if (s.IsCompressible)
                    return true;
            return false;
        }
    }

    internal override bool Shared => false;

    private long Sum()
    {
        long v = 0;
        foreach (var s in _sources) v += s.Version;
        return v;
    }

    internal override bool CanResumeFrom(in Cursor cursor)
    {
        return false;
    }

    internal override long AddTap(Tap t)
    {
        foreach (var s in _sources) s.AddTap(t);
        return 0; // a watch is one subscriber's: no topic epoch of its own
    }

    internal override (long Epoch, int Remaining) RemoveTap(Tap t)
    {
        foreach (var s in _sources) s.RemoveTap(t);
        return default;
    }

    /// <summary>
    ///     Takes every member lock without ever blocking while holding one (all TryEnter; on contention release all and spin),
    ///     so a writer that nests locks out of canonical order — e.g. a single-table update that also publishes a value —
    ///     can't deadlock with a watch. False after sustained contention: the caller retries shortly.
    /// </summary>
    private bool TryEnterAll()
    {
        for (var spinner = new SpinWait();; spinner.SpinOnce(-1)) // spins only (see Gates): never yields nor sleeps
        {
            var held = 0;
            while (held < _sources.Length && _sources[held].Gate!.TryEnter()) held++;
            if (held == _sources.Length) return true;
            for (var i = held - 1; i >= 0; i--) _sources[i].Gate!.Exit();
            if (spinner.NextSpinWillYield) return false;
        }
    }

    private void ExitAll()
    {
        for (var i = _sources.Length - 1; i >= 0; i--) _sources[i].Gate!.Exit();
    }

    internal override int Write(uint tapId, ref Cursor cursor, Span<byte> dst)
    {
        if (dst.Length < Wire.GroupHeader) return -1;
        var parts = _sources.Length > 1 ? Plan() : 1;
        if (cursor.Seq >= 0 && parts > 1 && Settled()) return WriteParts(tapId, ref cursor, dst);
        if (!TryEnterAll()) return Busy;
        try
        {
            var version = 2 * Sum();
            var reset = cursor.Seq < 0;
            if (reset)
                foreach (var s in _states)
                {
                    s.Cursor = Cursor.Snapshot;
                    s.Range?.Reset();
                }

            long total = 0;
            var count = 0;
            for (var i = 0; i < _members.Length; i++)
            {
                var m = _members[i].Measure(_states[i]);
                if (m == Busy) return Busy;
                if (m < 0)
                {
                    TrySetFault(new StreamFault(FaultCode.Unknown, version,
                        $"Watch member {i} can't be part of a group record (a value over 64 KB)."));
                    return 0; // the tap now sends its error policy
                }

                _sizes[i] = m;
                if (m > 0)
                {
                    total += m;
                    count++;
                }
            }

            if (count == 0)
            {
                for (var i = 0; i < _members.Length; i++) _members[i].Advance(_states[i]);
                Volatile.Write(ref _incomplete, 0);
                Settle();
                cursor.Seq = version;
                return 0;
            }

            if (Wire.GroupHeader + total > dst.Length) return -1;

            var o = Wire.GroupHeader;
            count = 0;
            for (var i = 0; i < _members.Length; i++)
            {
                if (_sizes[i] == 0)
                {
                    _members[i].Advance(_states[i]);
                    continue;
                }

                var w = _members[i].Write(i, _states[i], dst[o..]);
                if (w < 0) return -1; // e.g. a value over 64 KB: can't be part of a group record
                if (w > 0)
                {
                    o += w;
                    count++;
                }
            }

            WriteHeader(dst, tapId, reset ? Wire.FlagReset : 0, count, o - Wire.GroupHeader);
            Volatile.Write(ref _incomplete, 0);
            Settle();
            cursor.Seq = version;
            cursor.Pos = 0;
            return o;
        }
        finally
        {
            ExitAll();
        }
    }

    /// <summary>No domain changed since the last delivery checked: parts apart now have been apart since.</summary>
    private bool Settled()
    {
        if (!_settled) return false;
        for (var i = 0; i < _sources.Length; i++)
            if (_plannedEpoch[i] != _settledEpoch[i])
                return false;
        return true;
    }

    private void Settle()
    {
        if (_sources.Length < 2) return;
        _plannedEpoch.CopyTo(_settledEpoch, 0);
        _settled = true;
    }

    /// <summary>
    ///     Each source's domain now, and its part: sources sharing a domain one part, each other source its own. Returns
    ///     the parts.
    /// </summary>
    private int Plan()
    {
        var parts = 0;
        for (var i = 0; i < _sources.Length; i++)
        {
            _plannedEpoch[i] = Volatile.Read(ref _sources[i].DomainEpoch);
            var d = _planned[i] = Volatile.Read(ref _sources[i].Domain);
            var part = -1;
            if (d is not null)
                for (var j = 0; j < i; j++)
                    if (ReferenceEquals(_planned[j], d))
                    {
                        part = _partOf[j];
                        break;
                    }

            _partOf[i] = part >= 0 ? part : parts++;
        }

        return parts;
    }

    /// <summary>
    ///     A delivery read one domain at a time (the plan made): each part's members measured and written under that part's
    ///     locks only. A part busy, or not fitting, waits for the next delivery. A domain changed meanwhile: dropped, resync.
    /// </summary>
    private int WriteParts(uint tapId, ref Cursor cursor, Span<byte> dst)
    {
        var version = 2 * Sum();
        var parts = 0;
        foreach (var p in _partOf) parts = Math.Max(parts, p + 1);
        Array.Clear(_read);
        int o = Wire.GroupHeader, count = 0;
        bool busy = false, full = false, moved = false;
        for (var p = 0; p < parts && !full && !moved; p++)
        {
            if (!TryEnterPart(p))
            {
                busy = true;
                continue;
            } // its locks held a while (a long update): the others go now

            try
            {
                for (var i = 0; i < _sources.Length; i++)
                    if (_partOf[i] == p)
                    {
                        _read[i] = true;
                        if (Volatile.Read(ref _sources[i].DomainEpoch) != _plannedEpoch[i]) moved = true;
                    }

                if (moved) break;
                long total = 0;
                var partBusy = false;
                for (var i = 0; i < _members.Length; i++)
                {
                    if (_partOf[_sourceOf[i]] != p) continue;
                    var m = _members[i].Measure(_states[i]);
                    if (m == Busy)
                    {
                        busy = partBusy = true;
                        break;
                    }

                    if (m < 0)
                    {
                        TrySetFault(new StreamFault(FaultCode.Unknown, version,
                            $"Watch member {i} can't be part of a group record (a value over 64 KB)."));
                        return 0; // the tap now sends its error policy
                    }

                    _sizes[i] = m;
                    total += m;
                }

                if (partBusy) continue;
                if (o + total > dst.Length)
                {
                    full = true;
                    break;
                }

                for (var i = 0; i < _members.Length; i++)
                {
                    if (_partOf[_sourceOf[i]] != p) continue;
                    if (_sizes[i] == 0)
                    {
                        _members[i].Advance(_states[i]);
                        continue;
                    }

                    var w = _members[i].Write(i, _states[i], dst[o..]);
                    if (w < 0) return -1;
                    if (w > 0)
                    {
                        o += w;
                        count++;
                    }
                }
            }
            finally
            {
                ExitPart(p);
            }
        }

        for (var i = 0;
             i < _sources.Length && !moved;
             i++) // a domain read changed since: a unit may span what was read apart
            if (_read[i] && Volatile.Read(ref _sources[i].DomainEpoch) != _plannedEpoch[i])
                moved = true;
        if (moved)
        {
            _settled = false;
            foreach (var s in _states)
            {
                s.Cursor = Cursor.Snapshot;
                s.Range?.Reset();
            }

            cursor.Seq = -1; // what was written goes unsent: resync, under all the locks
            return Busy;
        }

        var left = busy || full;
        if (o == Wire.GroupHeader)
        {
            if (busy) return Busy;
            if (full) return -1;
        }

        Volatile.Write(ref _incomplete, left ? 1 : 0);
        Settle();
        cursor.Seq = version;
        cursor.Pos = 0;
        if (count == 0) return 0;
        WriteHeader(dst, tapId, 0, count, o - Wire.GroupHeader);
        return o;
    }

    private bool TryEnterPart(int part)
    {
        for (var spinner = new SpinWait();; spinner.SpinOnce(-1)) // spins only (see Gates)
        {
            var i = 0;
            for (; i < _sources.Length; i++)
                if (_partOf[i] == part && !_sources[i].Gate!.TryEnter())
                    break;
            if (i == _sources.Length) return true;
            for (var j = i - 1; j >= 0; j--)
                if (_partOf[j] == part)
                    _sources[j].Gate!.Exit();
            if (spinner.NextSpinWillYield) return false;
        }
    }

    private void ExitPart(int part)
    {
        for (var i = _sources.Length - 1; i >= 0; i--)
            if (_partOf[i] == part)
                _sources[i].Gate!.Exit();
    }

    internal override int WriteDegraded(uint tapId, StreamFault? fault, IDegradeFunction? degrade, Span<byte> dst,
        bool first)
    {
        if (dst.Length < Wire.GroupHeader) return -1;
        if (!TryEnterAll()) return FaultCleared; // serve it as state: the group write reports Busy and retries
        try
        {
            if (!ReferenceEquals(Fault, fault)) return FaultCleared;
            int o = Wire.GroupHeader, count = 0;
            for (var i = 0; i < _members.Length; i++)
            {
                if (_members[i].Degrade is null) continue;
                var w = _members[i].WriteDegraded(i, dst[o..], first);
                if (w < 0) return -1;
                if (w > 0)
                {
                    o += w;
                    count++;
                }
            }

            if (count == 0) return 0;
            WriteHeader(dst, tapId, Wire.FlagReset | Wire.FlagDegraded, count, o - Wire.GroupHeader);
            return o;
        }
        finally
        {
            ExitAll();
        }
    }

    private static void WriteHeader(Span<byte> dst, uint tapId, int flags, int count, int bodyLength)
    {
        dst[0] = Wire.Group;
        BinaryPrimitives.WriteUInt32LittleEndian(dst[1..], tapId);
        dst[5] = (byte)flags;
        BinaryPrimitives.WriteUInt16LittleEndian(dst[6..], (ushort)count);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[8..], (uint)bodyLength);
    }
}