using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tapline;

/// <summary>
///     A staged writer for sources written together — tables and last-value sources, its members — in units of work.
///     A provider stages a unit (<see cref="Begin" />) and goes on: it never waits for a fold, nor for the members'
///     readers. The router drains what is staged on its own threads (the thread pool), folds each member's part of the
///     units outside its lock, and applies them as one transaction across the members they touch, taking their locks
///     only for that — so a watch over any of them sees whole units only.
///     <para>
///         <b>Members come and go.</b> <see cref="Join{TKey,TRow,TDelta}" /> and <see cref="Leave" /> are staged too,
///         applied in order with the units. The group is a member's only writer, from its join being staged until its
///         leave is applied: direct updates, transactions and other ingests are refused. A source another group is
///         leaving can be joined at once; its units wait for that leave. The members are one lock domain
///         (<see cref="LockDomain" />): a watch reads them together, and reads anything else apart.
///     </para>
///     <para>
///         <b>No tearing.</b> A unit is copied into the staging buffer the providers fill; the router takes that buffer
///         whole — swapped for the one it drained last, under a lock held only for a copy or the swap — and from then
///         on reads it alone.
///     </para>
///     <para>
///         <b>Conflation.</b> Whatever is staged while the router is busy is folded together: one fold call per member,
///         one transaction. A snapshot (<see cref="BeginSnapshot" />) supersedes every unit staged before it — dropped
///         unread, and a batch the router has taken but not applied too — though never a join or a leave.
///     </para>
///     <para>
///         <b>Taking turns.</b> One slice folds at most <c>maxDeltas</c> entries (whole units, at least one), and for
///         metered
///         folds (<see cref="IMeteredFold" />) as many as fit <c>sliceFuel</c> by the fuel the router has measured — each
///         member's
///         call kept to half its budget. Then the router gives its thread back. It falls behind knowingly: the staging
///         fills, and
///         units are refused (507).
///     </para>
///     <para>
///         <b>Halving.</b> When a member's fold fails — out of fuel, out of memory, a trap — the router folds half the
///         units,
///         and half again, until they fold; the rest wait for the next slice. Only a unit that fails on its own faults the
///         group,
///         after the units before it.
///     </para>
///     <para>
///         <b>Checked as a transaction is.</b> A unit may say the upstream version it applies to and the one it makes —
///         one chain
///         for the group; units before a broken link are applied, then every member faults (
///         <see cref="FaultCode.VersionMismatch" />).
///         A member's postcondition is its last part's checksum in the slice. While any member is faulted, units are
///         refused
///         (<see cref="StreamFaultException" />) until a snapshot is staged: applied, it recovers the members it names.
///     </para>
/// </summary>
public sealed class IngestGroup : IThreadPoolWorkItem
{
    internal static readonly int RecordSize = Unsafe.SizeOf<RecordHeader>(), PartSize = Unsafe.SizeOf<PartHeader>();

    private static long _nextMember;
    private readonly List<IngestMember> _active = []; // the router's, in lock order

    private readonly Stack<int> _freeSlots = new();
    private readonly List<IngestMember> _posting = []; // the providers' view (under _stage)

    // Rendezvous after what's been posted (TableIngest.TryRendezvous), and what waits on it (WhenApplied): each resolved
    // once the router has finished every post staged before it — applied, or dropped (superseded) — at its member's version
    // then: a mailbox's rendezvous, or a callback. Under _stage.
    private readonly List<(IngestMember Member, object Target, byte[]? Correlation, long Through)> _rendezvous = [];
    private readonly Lock _stage = new();
    private readonly List<IngestMember> _touched = [];

    private IngestMember?[] _bySlot = new IngestMember?[4]; // written under _stage; read by the router
    private bool _chained;
    private Discard _discard;
    private int _drainLength, _drainAt; // the router's buffer: its length, how far applied
    private byte[] _fill, _drain; // the providers', under _stage; the router's, alone
    private int _fillLength, _fillUnits;
    private int _largest;
    private int _metered; // active members with a metered fold: slices sized by fuel
    private int _nextSlot;

    private long _posts, _batches, _applied, _superseded, _dropped, _refused, _halvings;
    private long _postsDone; // posts finished: through the buffer the router last finished
    private long _postsTaken; // posts staged when the router took its buffer
    private int _retryUnits; // a slice whose locks were busy: the units it had folded, tried again
    private bool _scheduled, _parked;
    private long _supersedes, _appliedSupersedes; // snapshots staged; as of the last slice applied
    private long _taken; // snapshots staged when the router took it
    private UnitMark[] _units = new UnitMark[64];
    private long _upstream;

    /// <param name="capacityBytes">What may be staged at once (joins and leaves aside).</param>
    /// <param name="maxDeltas">What one slice folds: deltas, rows and values (whole units, at least one).</param>
    /// <param name="sliceFuel">What one slice's metered folds should use, as measured (default a million).</param>
    public IngestGroup(int capacityBytes = 1 << 20, int maxDeltas = 4096, long? sliceFuel = null)
    {
        if (capacityBytes < RecordSize + PartSize + 16) throw new ArgumentOutOfRangeException(nameof(capacityBytes));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDeltas, 1);
        if (sliceFuel is < 1) throw new ArgumentOutOfRangeException(nameof(sliceFuel));
        Capacity = capacityBytes;
        MaxDeltas = maxDeltas;
        SliceFuel = sliceFuel ?? 1_000_000;
        _fill = new byte[Math.Min(capacityBytes, 4096)];
        _drain = new byte[_fill.Length];
    }

    /// <summary>False only in tests that drain by hand (<see cref="DrainPending" />).</summary>
    internal bool QueueToPool { get; set; } = true;

    /// <summary>The most that may be staged at once, in bytes.</summary>
    public int Capacity { get; }

    /// <summary>The most one slice folds (deltas, rows and values; whole units, at least one).</summary>
    public int MaxDeltas { get; }

    /// <summary>What one slice's metered folds should use, as the router measures them.</summary>
    public long SliceFuel { get; }

    /// <summary>The upstream version as of the last unit applied: the group's one chain.</summary>
    public long UpstreamVersion => Volatile.Read(ref _upstream);

    /// <summary>Its members, as the providers see them (joined, not left).</summary>
    public IngestMember[] Members
    {
        get
        {
            lock (_stage)
            {
                return _posting.ToArray();
            }
        }
    }

    /// <summary>Units staged (refused ones aside).</summary>
    public long Posts => Interlocked.Read(ref _posts);

    /// <summary>Transactions the router applied — each the units it found staged (up to the slice's limits).</summary>
    public long Batches => Interlocked.Read(ref _batches);

    /// <summary>The most units one transaction applied.</summary>
    public int LargestBatch => Volatile.Read(ref _largest);

    /// <summary>Units applied.</summary>
    public long Applied => Interlocked.Read(ref _applied);

    /// <summary>Units a snapshot superseded before they were applied.</summary>
    public long Superseded => Interlocked.Read(ref _superseded);

    /// <summary>
    ///     Units dropped: after a broken link or a unit whose fold failed on its own, or staged for a group faulted
    ///     meanwhile.
    /// </summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Units refused: they didn't fit (507).</summary>
    public long Refused => Interlocked.Read(ref _refused);

    /// <summary>Slices whose fold failed and were tried again with half their units.</summary>
    public long Halvings => Interlocked.Read(ref _halvings);

    /// <summary>Nothing staged, and the router not draining (nor waiting for a source to be let go).</summary>
    public bool Idle
    {
        get
        {
            lock (_stage)
            {
                return !_scheduled;
            }
        }
    }

    // ───────────── the router ─────────────

    void IThreadPoolWorkItem.Execute()
    {
        if (DrainSlice()) Queue(); // more meanwhile: its turn again after the rest of the pool's
    }

    internal static int Pad(int n)
    {
        return (n + 7) & ~7;
    }

    // ───────────── members ─────────────

    /// <summary>A table, folded with <paramref name="fold" /> (deltas of its rows' type).</summary>
    public IngestTable<TKey, TRow, TRow> Join<TKey, TRow>(Table<TKey, TRow> table, IBatchFold fold)
        where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged
    {
        return Join<TKey, TRow, TRow>(table, fold);
    }

    /// <summary>
    ///     A table joins, folded with <paramref name="fold" />: from now on the group is its only writer (if another group is
    ///     leaving it, once that leave is applied). Its units may name it from now; the join is applied in order with them.
    /// </summary>
    public IngestTable<TKey, TRow, TDelta> Join<TKey, TRow, TDelta>(Table<TKey, TRow> table, IBatchFold fold)
        where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged where TDelta : unmanaged
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(fold);
        var member = new IngestTable<TKey, TRow, TDelta>(this, table, fold);
        Enlist(member);
        return member;
    }

    /// <summary>A last-value source joins: its units may publish it.</summary>
    public IngestValue Join(LastValueSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var member = new IngestValue(this, source);
        Enlist(member);
        return member;
    }

    /// <summary>
    ///     The member leaves, after the units staged before this: once that is applied, it is written alone again (or by a
    ///     group that joined it meanwhile). Units can't name it from now.
    /// </summary>
    public void Leave(IngestMember member)
    {
        if (member.Group != this) throw new ArgumentException("Not a member of this group.", nameof(member));
        lock (_stage)
        {
            if (!member.Posting) throw new InvalidOperationException("It has left already.");
            member.Posting = false;
            _posting.Remove(member);
            lock (LockDomain.Changes)
            {
                member.Source.Claims![^1].Leaving = true; // its last claim is this one (nobody could join after it)
            }

            StageMembership(RecordKind.Leave, member);
        }

        Schedule();
    }

    internal void Enlist(IngestMember member)
    {
        var source = member.Source;
        if (source.Gate is null) throw new ArgumentException("Only tables and last-value sources can be members.");
        lock (LockDomain.Changes) // it is the group's next: free, or every group before leaving it
        {
            var claims = source.Claims ??= [];
            if (claims.Count > 0 && claims[^1] is { Leaving: false } last)
                throw new InvalidOperationException(last.Group == this
                    ? "It is a member already."
                    : "It is written by another ingest: let it leave there first.");
            claims.Add(new OwnerClaim(this));
            if (claims.Count == 1) Volatile.Write(ref source.Writer, this);
        }

        lock (_stage)
        {
            member.Slot = _freeSlots.Count > 0 ? _freeSlots.Pop() : _nextSlot++;
            if (member.Slot >= _bySlot.Length)
            {
                var grown = new IngestMember?[_bySlot.Length * 2];
                _bySlot.CopyTo(grown, 0);
                Volatile.Write(ref _bySlot, grown);
            }

            Volatile.Write(ref _bySlot[member.Slot], member);
            member.Posting = true;
            _posting.Add(member);
            StageMembership(RecordKind.Join, member); // applied with the first unit after it (nothing to do before)
        }
    }

    internal static long NewMemberId()
    {
        return Interlocked.Increment(ref _nextMember);
    }

    /// <summary>A join or a leave: always staged (never refused, never superseded). Under _stage.</summary>
    private void StageMembership(RecordKind kind, IngestMember member)
    {
        Fit(_fillLength + RecordSize);
        MemoryMarshal.Write(_fill.AsSpan(_fillLength),
            new RecordHeader { Length = RecordSize, Kind = kind, Slot = member.Slot, Member = member.Id });
        _fillLength += RecordSize;
    }

    private void Schedule()
    {
        bool schedule;
        lock (_stage)
        {
            schedule = !_scheduled;
            _scheduled = true;
        }

        if (schedule) Queue();
    }

    private void Queue()
    {
        if (QueueToPool) ThreadPool.UnsafeQueueUserWorkItem(this, false);
    }

    /// <summary>A source this group joined has been let go by its last writer: its units can go on.</summary>
    internal void Wake()
    {
        lock (_stage)
        {
            if (!_parked) return;
            _parked = false;
        }

        Queue();
    }

    // ───────────── units ─────────────

    /// <summary>
    ///     A unit of work: parts for its members (<see cref="IngestUnit.Post{TKey,TRow,TDelta}" />,
    ///     <see cref="IngestUnit.Rows{TKey,TRow,TDelta}" />, <see cref="IngestUnit.Publish" />), staged whole by
    ///     <see cref="IngestUnit.Complete" />. <paramref name="expectedUpstream" />: the group's upstream version it applies
    ///     to.
    /// </summary>
    public IngestUnit Begin(long? expectedUpstream = null)
    {
        return IngestUnit.Open(this, expectedUpstream, false);
    }

    /// <summary>
    ///     A snapshot: the whole state of the members it names (<see cref="IngestUnit.Rows{TKey,TRow,TDelta}" />,
    ///     <see cref="IngestUnit.Publish" />; no deltas). It supersedes every unit staged before it, and recovers a faulted
    ///     group — name every member whose state matters.
    /// </summary>
    public IngestUnit BeginSnapshot()
    {
        return IngestUnit.Open(this, null, true);
    }

    /// <summary>Stages a whole unit (its header's Length set). Delivered/NoListeners for its members, or Full (507).</summary>
    internal PublishStatus StageUnit(ReadOnlySpan<byte> unit, bool supersedes)
    {
        if (unit.Length > Capacity)
            throw new ArgumentException($"The unit is {unit.Length:N0} bytes: at most {Capacity:N0} may be staged.");
        bool schedule;
        lock (_stage)
        {
            for (var p = RecordSize; p < unit.Length;) // its members still members (and the same ones)
            {
                var part = MemoryMarshal.Read<PartHeader>(unit[p..]);
                if (_bySlot[part.Slot] is not { Posting: true } m || m.Id != part.Member)
                    throw new InvalidOperationException("A member of the unit has left the group.");
                p += part.Length;
            }

            if (!Admit(unit.Length, supersedes)) return PublishStatus.Full;
            unit.CopyTo(_fill.AsSpan(_fillLength));
            schedule = Staged(unit.Length);
        }

        if (schedule) Queue();
        return Listened(unit);
    }

    /// <summary>A unit of one part, written straight into the staging buffer (a table's ingest). Delivered, or Full (507).</summary>
    internal PublishStatus StageSingle(IngestMember member, PartKind kind, ReadOnlySpan<byte> keys,
        ReadOnlySpan<byte> items, int count, bool supersedes,
        long? expectedUpstream, long? upstreamVersion, uint? expectedChecksum)
    {
        var size = RecordSize + PartSize + Pad(keys.Length) + (long)Pad(items.Length);
        if (size > Capacity)
            throw new ArgumentException($"The post is {size:N0} bytes: at most {Capacity:N0} may be staged.");
        bool schedule;
        lock (_stage)
        {
            if (!member.Posting) throw new InvalidOperationException("It has left the group.");
            if (!Admit((int)size, supersedes)) return PublishStatus.Full;
            var record = _fill.AsSpan(_fillLength, (int)size);
            var flags = (supersedes ? UnitFlags.Supersedes : 0) | (kind == PartKind.Rows ? UnitFlags.HasRows : 0);
            if (expectedUpstream.HasValue) flags |= UnitFlags.ExpectedUpstream;
            if (upstreamVersion.HasValue) flags |= UnitFlags.UpstreamVersion;
            MemoryMarshal.Write(record, new RecordHeader
            {
                Length = (int)size, Kind = RecordKind.Unit, Flags = flags, Entries = count,
                ExpectedUpstream = expectedUpstream.GetValueOrDefault(),
                UpstreamVersion = upstreamVersion.GetValueOrDefault()
            });
            MemoryMarshal.Write(record[RecordSize..], new PartHeader
            {
                Length = (int)size - RecordSize, Slot = member.Slot, Kind = kind, Count = count, Member = member.Id,
                ExpectedChecksum = expectedChecksum.GetValueOrDefault(),
                Flags = expectedChecksum.HasValue ? PartFlags.ExpectedChecksum : 0
            });
            keys.CopyTo(record[(RecordSize + PartSize)..]);
            items.CopyTo(record[(RecordSize + PartSize + Pad(keys.Length))..]);
            schedule = Staged((int)size);
        }

        if (schedule) Queue();
        return PublishStatus.Delivered;
    }

    /// <summary>The router's buffer finished: the posts it held are done; the rendezvous waiting on them, due. Under _stage.</summary>
    private List<(IngestMember, object, byte[]?, long)>? Finished()
    {
        _postsDone = _postsTaken;
        if (_rendezvous.Count == 0) return null;
        var due = _rendezvous.Where(r => r.Through <= _postsDone).ToList();
        _rendezvous.RemoveAll(r => r.Through <= _postsDone);
        return due;
    }

    private static void Resolve(List<(IngestMember Member, object Target, byte[]? Correlation, long Through)>? due)
    {
        if (due is null) return;
        foreach (var (member, target, correlation, _) in due)
            if (target is Mailbox mailbox)
                mailbox.TryRendezvous(correlation,
                    member.Source.Version); // (refused — full, ended: the client's own timeout)
            else ((Action<long>)target)(member.Source.Version);
    }

    /// <summary>A rendezvous after every post staged so far: now, if the router has finished them all; else when it has.</summary>
    internal bool Rendezvous(IngestMember member, Mailbox mailbox, ReadOnlySpan<byte> correlation)
    {
        if (correlation.Length is 0 or > Wire.MaxCorrelation)
            throw new ArgumentOutOfRangeException(nameof(correlation),
                $"A correlation is 1 to {Wire.MaxCorrelation} bytes.");
        lock (_stage)
        {
            var through = Interlocked.Read(ref _posts);
            if (through > _postsDone)
            {
                _rendezvous.Add((member, mailbox, correlation.ToArray(), through));
                return true;
            }
        }

        return mailbox.TryRendezvous(correlation, member.Source.Version);
    }

    /// <summary>
    ///     <paramref name="then" />, with its member's version, once every post staged so far is finished (applied, or
    ///     superseded): at once, if the router has finished them all. The provider link's, for what it relays after a
    ///     topic's state (a mailbox's message, after the state it was posted at).
    /// </summary>
    internal void WhenApplied(IngestMember member, Action<long> then)
    {
        lock (_stage)
        {
            var through = Interlocked.Read(ref _posts);
            if (through > _postsDone)
            {
                _rendezvous.Add((member, then, null, through));
                return;
            }
        }

        then(member.Source.Version);
    }

    /// <summary>
    ///     Room for a unit, a snapshot first superseding what's staged. Throws while the group is faulted and nothing
    ///     recovers it. Under _stage.
    /// </summary>
    private bool Admit(int size, bool supersedes)
    {
        if (supersedes)
        {
            Supersede();
        }
        else
        {
            if (Faulted(_posting) is { } fault && _appliedSupersedes == _supersedes)
                throw new StreamFaultException(fault);
            if (_fillLength + size > Capacity)
            {
                Interlocked.Increment(ref _refused);
                return false;
            }
        }

        Fit(_fillLength + size);
        return true;
    }

    private bool Staged(int size)
    {
        _fillLength += size;
        _fillUnits++;
        Interlocked.Increment(ref _posts);
        var schedule = !_scheduled;
        _scheduled = true;
        return schedule;
    }

    /// <summary>What's staged goes — all but its joins and leaves, kept in order. Under _stage.</summary>
    private void Supersede()
    {
        int o = 0, kept = 0;
        while (o < _fillLength)
        {
            var h = MemoryMarshal.Read<RecordHeader>(_fill.AsSpan(o));
            if (h.Kind != RecordKind.Unit)
            {
                if (kept != o) _fill.AsSpan(o, h.Length).CopyTo(_fill.AsSpan(kept));
                kept += h.Length;
            }

            o += h.Length;
        }

        Interlocked.Add(ref _superseded, _fillUnits);
        _fillLength = kept;
        _fillUnits = 0;
        _supersedes++;
    }

    private void Fit(int length)
    {
        if (_fill.Length >= length) return;
        var grown = new byte[(int)Math.Max((ulong)length,
            Math.Min((ulong)Math.Max(Capacity, length), BitOperations.RoundUpToPowerOf2((ulong)length)))];
        _fill.AsSpan(0, _fillLength).CopyTo(grown);
        _fill = grown;
    }

    private static StreamFault? Faulted(List<IngestMember> members)
    {
        foreach (var m in members)
            if (m.Source.Fault is { } f)
                return f;
        return null;
    }

    private PublishStatus Listened(ReadOnlySpan<byte> unit)
    {
        var slots = Volatile.Read(ref _bySlot);
        for (var p = RecordSize; p < unit.Length;)
        {
            var part = MemoryMarshal.Read<PartHeader>(unit[p..]);
            if (slots[part.Slot]?.Source.Routes > 0) return PublishStatus.Delivered;
            p += part.Length;
        }

        return PublishStatus.NoListeners;
    }

    /// <summary>Drains on the calling thread until nothing is staged, or it waits for a source to be let go (tests).</summary>
    internal void DrainPending()
    {
        ByHand();
        while (DrainSlice())
        {
        }
    }

    /// <summary>One slice, on the calling thread (tests): true while there is more.</summary>
    internal bool DrainOnce()
    {
        ByHand();
        return DrainSlice();
    }

    /// <summary>
    ///     Drained by hand only when the pool doesn't drain it too (<see cref="QueueToPool" /> false): the router's buffer and
    ///     lists are one drain's alone, and two at once would fold into each other.
    /// </summary>
    private void ByHand()
    {
        if (QueueToPool)
            throw new InvalidOperationException(
                "A group the pool drains can't be drained by hand too: set QueueToPool to false first.");
    }

    /// <summary>
    ///     Applies joins, leaves and units dropped as they come, up to one slice of units. True while there is more (still
    ///     scheduled: the caller queues it again); false when nothing is staged — or it waits for a source it joined.
    /// </summary>
    private bool DrainSlice()
    {
        while (true)
        {
            if (_drainAt == _drainLength)
            {
                List<(IngestMember, object, byte[]?, long)>? due;
                bool empty;
                lock (_stage)
                {
                    if (_supersedes == _taken) _appliedSupersedes = _taken;
                    due = Finished(); // (the buffer just finished: its posts done)
                    empty = _fillLength == 0;
                    if (empty)
                    {
                        _scheduled = false;
                    }
                    else
                    {
                        (_fill, _drain) = (_drain, _fill);
                        _drainLength = _fillLength;
                        _drainAt = 0;
                        _postsTaken = Interlocked.Read(ref _posts);
                        _taken = _supersedes;
                        _discard = Discard.None;
                        _retryUnits = 0;
                        _fillLength = _fillUnits = 0;
                    }
                }

                Resolve(due);
                if (empty) return false;
            }

            var h = MemoryMarshal.Read<RecordHeader>(_drain.AsSpan(_drainAt));
            if (h.Kind != RecordKind.Unit)
            {
                var step = h.Kind == RecordKind.Join ? ApplyJoin(h.Slot) : ApplyLeave(h.Slot);
                if (step == Step.Parked) return false; // woken by the source's last writer letting go
                if (step == Step.Busy) // its direct writer mid-update: again in a moment, not spinning
                {
                    if (QueueToPool)
                        _ = Task.Delay(1).ContinueWith(static (_, g) => ((IngestGroup)g!).Queue(), this,
                            TaskScheduler.Default);
                    return false;
                }

                _drainAt += h.Length;
                continue;
            }

            if (_discard == Discard.None && Volatile.Read(ref _supersedes) != _taken) _discard = Discard.Superseded;
            if (_discard != Discard.None)
            {
                Interlocked.Increment(ref _discard == Discard.Superseded ? ref _superseded : ref _dropped);
                _drainAt += h.Length;
                continue;
            }

            ApplySlice();
            List<(IngestMember, object, byte[]?, long)>? finished = null;
            lock (_stage)
            {
                if (_supersedes == _taken) _appliedSupersedes = _taken;
                if (_drainAt < _drainLength || _fillLength > 0) return true;
                _scheduled = false;
                finished = Finished();
            }

            Resolve(finished);
            return false;
        }
    }

    private Step ApplyJoin(int slot)
    {
        var member = Volatile.Read(ref _bySlot)[slot]!;
        var source = member.Source;
        if (Volatile.Read(ref source.Writer) != this)
            lock (_stage)
            {
                if (Volatile.Read(ref source.Writer) != this)
                {
                    _parked = true;
                    return Step.Parked;
                }
            }

        if (!Gates.TryEnter(source.Gate!)) return Step.Busy; // a direct writer finishing: never waited on here
        try
        {
            var sources = new List<Source>(_active.Count + 1);
            foreach (var m in _active) sources.Add(m.Source);
            sources.Add(source);
            lock (LockDomain.Changes)
            {
                LockDomain.Regroup(sources, null);
            }

            if (!_chained && member is IngestTableBase table)
            {
                Volatile.Write(ref _upstream, table.UpstreamOf);
                _chained = true;
            }

            var i = _active.Count;
            while (i > 0 && _active[i - 1].Source.SourceId > source.SourceId) i--;
            _active.Insert(i, member);
            member.Active = true;
            if (member is IngestTableBase { FuelBudget: > 0 }) _metered++;
        }
        finally
        {
            source.Gate!.Exit();
        }

        return Step.Applied;
    }

    private Step ApplyLeave(int slot)
    {
        var member = Volatile.Read(ref _bySlot)[slot]!;
        var source = member.Source;
        member.Active = false;
        _active.Remove(member);
        if (member is IngestTableBase { FuelBudget: > 0 }) _metered--;
        IngestGroup? next;
        lock (LockDomain.Changes)
        {
            var rest = new List<Source>(_active.Count);
            foreach (var m in _active) rest.Add(m.Source);
            LockDomain.Regroup(rest, source);
            var claims = source.Claims!;
            claims.RemoveAt(0); // this group's: the first, its join applied
            next = claims.Count > 0 ? claims[0].Group : null;
            Volatile.Write(ref source.Writer, next);
        }

        lock (_stage)
        {
            _bySlot[slot] = null;
            _freeSlots.Push(slot);
        }

        if (next is not null && next != this) next.Wake(); // (this group again: its own join is later in its records)
        return Step.Applied;
    }

    /// <summary>
    ///     Folds whole units from where the router is in its buffer — up to the slice's limits, a join or leave, a unit with a
    ///     member's whole state, or a broken link — and applies them as one transaction; fewer if a fold fails (halving).
    /// </summary>
    private void ApplySlice()
    {
        var drain = _drain;
        var slots = Volatile.Read(ref _bySlot);
        var first = MemoryMarshal.Read<RecordHeader>(drain.AsSpan(_drainAt));
        var replace = (first.Flags & UnitFlags.Supersedes) != 0;
        if (!replace && Faulted(_active) is not null)
        {
            _discard = Discard.Dropped;
            return;
        }

        foreach (var m in _active) m.BeginSlice();
        var chain = _upstream;
        long? upstreamOut = null;
        int o = _drainAt, units = 0, limit = _retryUnits > 0 ? _retryUnits : int.MaxValue;
        long entries = 0;
        double fuel = 0;
        string? broken = null;
        _retryUnits = 0;
        while (o < _drainLength && units < limit)
        {
            var h = MemoryMarshal.Read<RecordHeader>(drain.AsSpan(o));
            if (h.Kind != RecordKind.Unit) break; // a join or leave: between slices
            if (units > 0)
            {
                if ((h.Flags & (UnitFlags.Supersedes | UnitFlags.HasRows)) != 0)
                    break; // a member's whole state starts a slice
                if (entries + h.Entries > MaxDeltas) break;
                if (_metered > 0) // by the fuel measured: the slice's, and each member's call
                {
                    double f = 0;
                    var over = false;
                    for (var p = o + RecordSize; p < o + h.Length;)
                    {
                        var part = MemoryMarshal.Read<PartHeader>(drain.AsSpan(p));
                        if (part.Kind == PartKind.Deltas && slots[part.Slot] is IngestTableBase t)
                        {
                            f += t.FuelFor(part.Count);
                            over |= t.OverBudget(part.Count);
                        }

                        p += part.Length;
                    }

                    if (over || fuel + f > SliceFuel) break;
                }
            }

            if ((h.Flags & UnitFlags.ExpectedUpstream) != 0 && h.ExpectedUpstream != chain)
            {
                broken = $"Update for upstream version {h.ExpectedUpstream}; the group is at {chain}.";
                break;
            }

            for (var p = o + RecordSize; p < o + h.Length;)
            {
                var part = MemoryMarshal.Read<PartHeader>(drain.AsSpan(p));
                if (slots[part.Slot] is { Active: true } m && m.Id == part.Member)
                {
                    m.Gather(drain, p + PartSize, part, units);
                    if (_metered > 0 && part.Kind == PartKind.Deltas && m is IngestTableBase t)
                        fuel += t.FuelFor(part.Count);
                }

                p += part.Length;
            }

            entries += h.Entries;
            if ((h.Flags & UnitFlags.UpstreamVersion) != 0) upstreamOut = chain = h.UpstreamVersion;
            if (_units.Length == units) Array.Resize(ref _units, units * 2);
            _units[units++] = new UnitMark { End = o + h.Length, Chain = chain, Upstream = upstreamOut };
            o += h.Length;
        }

        // Fold the first k units — all of them, or, while a member's fold fails, half as many each time. A member's deltas
        // in a prefix touch only the row entries their keys made (made in order), so a prefix folds from the rows as they start.
        int k = units, fixedUnits = replace ? 1 : 0;
        var faultCode = FaultCode.None;
        string? faultMessage = null;
        Exception? faultException = null;
        while (true)
        {
            Touch(k);
            try
            {
                foreach (var m in _touched) m.Fold(k);
                break;
            }
            catch (Exception e)
            {
                if (k - fixedUnits > 1)
                {
                    k = fixedUnits + (k - fixedUnits) / 2;
                    Interlocked.Increment(ref _halvings);
                    continue;
                }

                k = fixedUnits; // this unit fails on its own: what came before it stands
                faultCode = FaultCode.FoldFailed;
                faultMessage = $"Fold failed: {e.Message}";
                faultException = e;
                Touch(k);
                try
                {
                    foreach (var m in _touched) m.Fold(k); // a snapshot alone: whole states, nothing folded
                }
                catch (Exception again) // its finalizer fails on the snapshot itself: nothing can be applied
                {
                    FaultAll(new StreamFault(FaultCode.FoldFailed, 0, $"Fold failed: {again.Message}", again));
                    _discard = Discard.Dropped;
                    return;
                }

                break;
            }
        }

        if (k == units && broken is not null) // all before the broken link folded: it's next
        {
            faultCode = FaultCode.VersionMismatch;
            faultMessage = broken;
        }

        if (Volatile.Read(ref _supersedes) != _taken)
        {
            _discard = Discard.Superseded;
            return;
        } // a snapshot meanwhile: dropped, unapplied

        var entered = 0; // the members' locks, in order, never waited on
        while (entered < _touched.Count && Gates.TryEnter(_touched[entered].Source.Gate!)) entered++;
        if (entered < _touched.Count)
        {
            for (var i = entered - 1; i >= 0; i--) _touched[i].Source.Gate!.Exit();
            _retryUnits = Math.Max(k, 1); // the same units again, shortly (not refolded wider)
            return;
        }

        var mark = k > 0 ? _units[k - 1] : default;
        StreamFault? failed = null;
        try
        {
            foreach (var m in _touched)
            {
                m.Open(replace);
                m.Apply(k);
            }

            foreach (var m in _touched)
                if ((failed = m.Prepare()) is not null)
                    break;
            if (failed is null)
                foreach (var m in _touched)
                    m.Notify = m.Commit(mark.Upstream);
            else
                foreach (var m in _touched)
                    m.Rollback();
        }
        catch
        {
            foreach (var m in _touched) m.Rollback();
            throw;
        }
        finally
        {
            foreach (var m in _touched) m.Close();
            for (var i = _touched.Count - 1; i >= 0; i--) _touched[i].Source.Gate!.Exit();
        }

        if (failed is not null)
        {
            FaultAll(failed); // the slice rolled back: the rest of the buffer is dropped
            _discard = Discard.Dropped;
            return;
        }

        foreach (var m in _touched)
            if (m.Notify)
                m.Source.NotifyChanged();
        if (k > 0)
        {
            Volatile.Write(ref _upstream, mark.Chain);
            Interlocked.Increment(ref _batches);
            if (k > _largest) Volatile.Write(ref _largest, k);
            Interlocked.Add(ref _applied, k);
            _drainAt = mark.End;
        }

        if (faultCode != FaultCode.None)
        {
            FaultAll(new StreamFault(faultCode, 0, faultMessage, faultException));
            _discard = Discard.Dropped;
        }
    }

    /// <summary>The members the first <paramref name="units" /> units change, in lock order.</summary>
    private void Touch(int units)
    {
        _touched.Clear();
        foreach (var m in _active)
            if (m.Touched(units))
                _touched.Add(m);
    }

    /// <summary>Every member missed the same unit of work: all fault (first fault wins, each).</summary>
    private void FaultAll(StreamFault cause)
    {
        foreach (var m in _active)
            if (m.Source.SetFault(cause with { Version = m.Source.Version }))
                m.Source.NotifyChanged();
    }

    private enum Discard
    {
        None,
        Superseded,
        Dropped
    }

    /// <summary>
    ///     Where a slice's unit ends, and the chain after it (<see cref="Upstream" />: null while no unit in the slice
    ///     set it).
    /// </summary>
    private struct UnitMark
    {
        public int End;
        public long Chain;
        public long? Upstream;
    }

    private enum Step
    {
        Applied,
        Busy,
        Parked
    }
}

internal enum RecordKind
{
    Unit,
    Join,
    Leave
}

[Flags]
internal enum UnitFlags
{
    /// <summary>A snapshot: it supersedes every unit staged before it.</summary>
    Supersedes = 1,
    ExpectedUpstream = 2,
    UpstreamVersion = 4,

    /// <summary>It carries a member's whole state (a Rows part): it starts a slice.</summary>
    HasRows = 8
}

internal enum PartKind
{
    Deltas,
    Rows,
    Value
}

[Flags]
internal enum PartFlags
{
    ExpectedChecksum = 1,
    Compressible = 2
}

/// <summary>A staged record: a unit (then its parts), a join or a leave — each region padded to 8 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RecordHeader
{
    public int Length;
    public RecordKind Kind;
    public UnitFlags Flags;
    public int Slot; // a join's or leave's member
    public int Entries; // a unit's: deltas, rows and values (the slice's measure)
    public int Reserved;
    public long Member;
    public long ExpectedUpstream, UpstreamVersion;
}

/// <summary>A member's part of a unit: this, then its keys and deltas or rows, or a value's bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PartHeader
{
    public int Length, Slot;
    public PartKind Kind;
    public int Count; // entries; a value's bytes
    public uint ExpectedChecksum;
    public PartFlags Flags;
    public long Member;
    public Delivery Delivery;
    public int Reserved;
}

/// <summary>A member of an <see cref="IngestGroup" />: a table or a last-value source it writes.</summary>
public abstract class IngestMember
{
    internal readonly long Id = IngestGroup.NewMemberId();
    internal bool Active; // the router's: its join applied, its leave not yet
    internal bool Notify;
    internal bool Posting; // the providers' view (the group's stage lock)
    internal int Slot;

    private protected IngestMember(IngestGroup group, Source source)
    {
        Group = group;
        Source = source;
    }

    public IngestGroup Group { get; }

    public Source Source { get; }

    /// <summary>Joined, and not left — as the providers see it.</summary>
    public bool IsMember => Volatile.Read(ref Posting);

    // ── the router's, one slice at a time ──
    internal abstract void BeginSlice();
    internal abstract void Gather(byte[] drain, int payload, in PartHeader part, int unit);
    internal abstract bool Touched(int units);
    internal abstract void Fold(int units);
    internal abstract void Open(bool recovery);
    internal abstract void Apply(int units);
    internal abstract StreamFault? Prepare();
    internal abstract bool Commit(long? upstreamVersion);
    internal abstract void Rollback();
    internal abstract void Close();
}

/// <summary>A table member, whatever its types: what the router sizes slices by.</summary>
public abstract class IngestTableBase : IngestMember
{
    private protected IngestTableBase(IngestGroup group, Source source) : base(group, source)
    {
    }

    /// <summary>
    ///     What its fold has used a delta, as the router measures it (recent calls weigh most; 0: not yet, or not
    ///     metered).
    /// </summary>
    public abstract double FuelPerDelta { get; }

    /// <summary>Its fold's budget a call, if metered (0: not).</summary>
    public abstract long FuelBudget { get; }

    internal abstract long UpstreamOf { get; }
    internal abstract double FuelFor(int deltas);

    /// <summary>Whether <paramref name="deltas" /> more would take its call past half its budget, by the measure.</summary>
    internal abstract bool OverBudget(int deltas);
}

/// <summary>A table in an <see cref="IngestGroup" />, folded with its fold: deltas of <typeparamref name="TDelta" />.</summary>
public sealed class IngestTable<TKey, TRow, TDelta> : IngestTableBase
    where TKey : unmanaged, IEquatable<TKey>
    where TRow : unmanaged
    where TDelta : unmanaged
{
    internal static readonly int KeySize = Unsafe.SizeOf<TKey>(),
        RowSize = Unsafe.SizeOf<TRow>(),
        DeltaSize = Unsafe.SizeOf<TDelta>();

    private static readonly int RowStride = FoldEntry.RowStride(RowSize),
        DeltaStride = FoldEntry.DeltaStride(DeltaSize);

    private readonly IBatchFold _fold;
    private readonly IMeteredFold? _metered;

    // Finalized (IBatchFinalizer): what the fold folds — a state per key, kept here — and makes of it. Null for a plain
    // ingest, which pays for it no more than a test where a slice gathers a key and where each of its steps begins.
    private readonly Finalizing? _finalizing;

    // The router's scratch: one row entry per distinct key (as the slice starts; folded into _work), the deltas pointing at them.
    private readonly Dictionary<TKey, int> _rowOf = new(), _snapshotAt = new();
    private byte[] _drain = [];
    private double _fuelPerDelta;
    private TKey[] _keys = new TKey[64];
    private int _markCount, _rowCount, _n, _replaceAt, _replaceCount;
    private Mark[] _marks = new Mark[16];
    private bool _replace, _indexed;

    private byte[] _rows = new byte[64 * RowStride],
        _work = new byte[64 * RowStride],
        _deltas = new byte[64 * DeltaStride];

    internal IngestTable(IngestGroup group, Table<TKey, TRow> table, IBatchFold fold) : base(group, table)
    {
        Table = table;
        _fold = fold;
        _metered = fold as IMeteredFold;
    }

    /// <summary>A member whose fold keeps a state of <paramref name="stateSize" /> bytes per key, finalized into its rows.</summary>
    internal IngestTable(IngestGroup group, Table<TKey, TRow> table, IBatchFold fold, IBatchFinalizer finalizer,
        int stateSize) : this(group, table, fold)
    {
        _finalizing = new Finalizing(finalizer, stateSize);
    }

    public Table<TKey, TRow> Table { get; }

    public override double FuelPerDelta => Volatile.Read(ref _fuelPerDelta);

    public override long FuelBudget => _metered?.Budget ?? 0;

    internal override long UpstreamOf => Table.UpstreamVersion;

    private ReadOnlySpan<TKey> SnapshotKeys =>
        MemoryMarshal.Cast<byte, TKey>(_drain.AsSpan(_replaceAt, _replaceCount * KeySize));

    private ReadOnlySpan<TRow> SnapshotRows =>
        MemoryMarshal.Cast<byte, TRow>(_drain.AsSpan(_replaceAt + IngestGroup.Pad(_replaceCount * KeySize),
            _replaceCount * RowSize));

    internal override double FuelFor(int deltas)
    {
        return deltas * _fuelPerDelta;
    }

    internal override bool OverBudget(int deltas)
    {
        return _metered is not null && _fuelPerDelta > 0 && (_n + deltas) * _fuelPerDelta > _metered.Budget / 2;
    }

    internal override void BeginSlice()
    {
        _rowOf.Clear();
        _replace = _indexed = false;
        _markCount = _rowCount = _n = 0;
    }

    internal override void Gather(byte[] drain, int payload, in PartHeader part, int unit)
    {
        _drain = drain;
        uint? checksum = (part.Flags & PartFlags.ExpectedChecksum) != 0 ? part.ExpectedChecksum : null;
        if (part.Kind == PartKind.Rows) // its whole state (only in a slice's first unit): from here
        {
            BeginSlice();
            _replace = true;
            _replaceAt = payload;
            _replaceCount = part.Count;
            MarkPart(unit, checksum);
            return;
        }

        var count = part.Count;
        var keys = MemoryMarshal.Cast<byte, TKey>(drain.AsSpan(payload, count * KeySize));
        var deltas = drain.AsSpan(payload + IngestGroup.Pad(count * KeySize), count * DeltaSize);
        if (_deltas.Length < (_n + count) * DeltaStride)
            Array.Resize(ref _deltas, (int)BitOperations.RoundUpToPowerOf2((uint)((_n + count) * DeltaStride)));
        for (var i = 0; i < count; i++)
        {
            ref var row = ref CollectionsMarshal.GetValueRefOrAddDefault(_rowOf, keys[i], out var seen);
            if (!seen)
            {
                row = _rowCount;
                AddRow(keys[i], _rowCount++);
            }

            var d = _deltas.AsSpan(_n++ * DeltaStride, DeltaStride);
            BinaryPrimitives.WriteUInt32LittleEndian(d, (uint)row);
            deltas.Slice(i * DeltaSize, DeltaSize).CopyTo(d[4..]);
        }

        MarkPart(unit, checksum);
    }

    private void MarkPart(int unit, uint? checksum)
    {
        if (_markCount > 0 && _marks[_markCount - 1].Unit == unit) _markCount--; // a second part of the same unit
        if (_marks.Length == _markCount) Array.Resize(ref _marks, _markCount * 2);
        _marks[_markCount++] = new Mark { Unit = unit, Rows = _rowCount, Deltas = _n, Checksum = checksum };
    }

    private int MarkFor(int units)
    {
        for (var i = _markCount - 1; i >= 0; i--)
            if (_marks[i].Unit < units)
                return i;
        return -1;
    }

    /// <summary>
    ///     Row entry <paramref name="r" />: the key's row as the slice starts — the snapshot's (with one), else the
    ///     table's.
    /// </summary>
    private void AddRow(in TKey key, int r)
    {
        if (_finalizing is not null)
        {
            AddState(key, r);
            return;
        }

        if (_keys.Length == r)
        {
            Array.Resize(ref _keys, r * 2);
            Array.Resize(ref _rows, r * 2 * RowStride);
            Array.Resize(ref _work, r * 2 * RowStride);
        }

        _keys[r] = key;
        bool exists;
        TRow row;
        if (_replace)
        {
            IndexSnapshot();
            exists = _snapshotAt.TryGetValue(key, out var at);
            row = exists ? SnapshotRows[at] : default;
        }
        else
        {
            exists = Table.PeekRow(key, out row);
        }

        var e = _rows.AsSpan(r * RowStride, RowStride);
        BinaryPrimitives.WriteUInt32LittleEndian(e, exists ? FoldEntry.Present : 0u);
        MemoryMarshal.Write(e[4..], in row);
    }

    /// <summary>A snapshot's keys indexed, the first time a slice needs them: the last of a key's wins.</summary>
    private void IndexSnapshot()
    {
        if (_indexed) return;
        _snapshotAt.Clear();
        var snapshot = SnapshotKeys;
        for (var i = 0; i < snapshot.Length; i++) _snapshotAt[snapshot[i]] = i;
        _indexed = true;
    }

    internal override bool Touched(int units)
    {
        return MarkFor(units) >= 0;
    }

    internal override void Fold(int units)
    {
        var i = MarkFor(units);
        if (i < 0) return;
        var m = _marks[i];
        if (_finalizing is not null)
        {
            FoldStates(m);
            return;
        }

        var rows = _work.AsSpan(0, m.Rows * RowStride);
        _rows.AsSpan(0, rows.Length).CopyTo(rows);
        if (m.Deltas == 0) return;
        FoldDeltas(rows, RowSize, m);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FoldDeltas(Span<byte> rows, int rowSize, Mark m)
    {
        var ds = _deltas.AsSpan(0, m.Deltas * DeltaStride);
        if (_metered is null)
        {
            _fold.Fold(rows, m.Rows, rowSize, ds, m.Deltas, DeltaSize);
            return;
        }

        try
        {
            var reading = (double)_metered.FoldMetered(rows, m.Rows, rowSize, ds, m.Deltas, DeltaSize) / m.Deltas;
            Volatile.Write(ref _fuelPerDelta,
                _fuelPerDelta == 0 ? reading : _fuelPerDelta + (reading - _fuelPerDelta) / 4); // a quarter of the way
        }
        catch (FoldBudgetExceededException) // at least this much a delta
        {
            Volatile.Write(ref _fuelPerDelta, Math.Max(_fuelPerDelta, (double)_metered.Budget / m.Deltas));
            throw;
        }
    }

    internal override void Open(bool recovery)
    {
        Table.OpenStaged(recovery);
    }

    internal override void Apply(int units)
    {
        var i = MarkFor(units);
        var m = i < 0 ? default : _marks[i];
        if (_finalizing is not null)
        {
            ApplyStates(m);
            return;
        }

        Table.ApplyStaged(_replace, _replace ? SnapshotKeys : default, _replace ? SnapshotRows : default,
            _keys.AsSpan(0, m.Rows), _work.AsSpan(0, m.Rows * RowStride), m.Checksum);
    }

    internal override StreamFault? Prepare()
    {
        return ((ITransactional)Table).PrepareTx();
    }

    internal override bool Commit(long? upstreamVersion)
    {
        var notify = ((ITransactional)Table).CommitTx(upstreamVersion);
        if (_finalizing is not null) KeepStates(); // (rolled back, they're as they were: kept nowhere else)
        return notify;
    }

    internal override void Rollback()
    {
        ((ITransactional)Table).RollbackTx();
    }

    internal override void Close()
    {
        Table.CloseStaged();
    }

    // ───────────── finalized: the fold's states, kept here; the table's rows, what the finalizer makes of them ─────────────

    /// <summary>A snapshot's states as posted (its items: <c>stateSize</c> bytes each).</summary>
    private ReadOnlySpan<byte> SnapshotStates(Finalizing f)
    {
        return _drain.AsSpan(_replaceAt + IngestGroup.Pad(_replaceCount * KeySize), _replaceCount * f.StateSize);
    }

    /// <summary>State entry <paramref name="r" />: the key's state as the slice starts — the snapshot's, else the one kept.</summary>
    private void AddState(in TKey key, int r)
    {
        var f = _finalizing!;
        if (_keys.Length == r) Array.Resize(ref _keys, r * 2);
        if (f.Entries.Length < (r + 1) * f.StateStride)
        {
            Array.Resize(ref f.Entries, (r + 1) * 2 * f.StateStride);
            Array.Resize(ref f.Work, (r + 1) * 2 * f.StateStride);
        }

        _keys[r] = key;
        bool exists;
        var e = f.Entries.AsSpan(r * f.StateStride, f.StateStride);
        var state = e[4..];
        if (_replace)
        {
            IndexSnapshot();
            exists = _snapshotAt.TryGetValue(key, out var at);
            if (exists) SnapshotStates(f).Slice(at * f.StateSize, f.StateSize).CopyTo(state);
            else state.Clear();
        }
        else
        {
            exists = f.SlotOf.TryGetValue(key, out var slot);
            if (exists) f.States.AsSpan(slot * f.StateSize, f.StateSize).CopyTo(state);
            else state.Clear();
        }

        BinaryPrimitives.WriteUInt32LittleEndian(e, exists ? FoldEntry.Present : 0u);
    }

    /// <summary>The states folded, then finalized: theirs into rows (flags | row, as the table applies them), and a snapshot's.</summary>
    private void FoldStates(Mark m)
    {
        var f = _finalizing!;
        var states = f.Work.AsSpan(0, m.Rows * f.StateStride);
        f.Entries.AsSpan(0, states.Length).CopyTo(states);
        if (m.Deltas > 0) FoldDeltas(states, f.StateSize, m);
        var stride = FoldEntry.FinalizeStride(f.StateSize, RowSize);
        if (_replace)
        {
            var items = SnapshotStates(f);
            var entries = f.Scratch(_replaceCount * stride);
            for (var j = 0; j < _replaceCount; j++)
            {
                var e = entries.Slice(j * stride, stride);
                BinaryPrimitives.WriteUInt32LittleEndian(e, FoldEntry.Present);
                items.Slice(j * f.StateSize, f.StateSize).CopyTo(e[4..]);
                e[(4 + f.StateSize)..].Clear();
            }

            f.Finalizer.Finalize(entries, _replaceCount, f.StateSize, RowSize);
            if (f.SnapshotRows.Length < _replaceCount)
                Array.Resize(ref f.SnapshotRows, (int)BitOperations.RoundUpToPowerOf2((uint)_replaceCount));
            for (var j = 0; j < _replaceCount; j++)
                f.SnapshotRows[j] = MemoryMarshal.Read<TRow>(entries[(j * stride + 4 + f.StateSize)..]);
        }

        if (m.Rows == 0) return;
        var folded = f.Scratch(m.Rows * stride);
        for (var r = 0; r < m.Rows; r++)
        {
            var e = folded.Slice(r * stride, stride);
            states.Slice(r * f.StateStride, f.StateStride).CopyTo(e); // flags | state
            e[(4 + f.StateSize)..].Clear();
        }

        f.Finalizer.Finalize(folded, m.Rows, f.StateSize, RowSize);
        if (f.Rows.Length < m.Rows * RowStride)
            Array.Resize(ref f.Rows, (int)BitOperations.RoundUpToPowerOf2((uint)(m.Rows * RowStride)));
        for (var r = 0; r < m.Rows; r++)
        {
            var e = folded.Slice(r * stride, stride);
            var o = f.Rows.AsSpan(r * RowStride, RowStride);
            e[..4].CopyTo(o); // present as its state is
            e.Slice(4 + f.StateSize, RowSize).CopyTo(o[4..]);
        }
    }

    private void ApplyStates(Mark m)
    {
        var f = _finalizing!;
        f.Applied = m.Rows;
        Table.ApplyStaged(_replace, _replace ? SnapshotKeys : default,
            _replace ? f.SnapshotRows.AsSpan(0, _replaceCount) : default, _keys.AsSpan(0, m.Rows),
            f.Rows.AsSpan(0, m.Rows * RowStride), m.Checksum);
    }

    /// <summary>
    ///     Once the update is committed: the states it made kept — a snapshot's whole (replacing the rest), then the folded
    ///     keys' (an absent state forgotten, its slot free).
    /// </summary>
    private void KeepStates()
    {
        var f = _finalizing!;
        if (_replace)
        {
            f.SlotOf.Clear();
            f.Free.Clear();
            f.Slots = 0;
            var keys = SnapshotKeys;
            var items = SnapshotStates(f);
            for (var j = 0; j < keys.Length; j++) f.Keep(keys[j], items.Slice(j * f.StateSize, f.StateSize));
        }

        for (var r = 0; r < f.Applied; r++)
        {
            var e = f.Work.AsSpan(r * f.StateStride, f.StateStride);
            if ((BinaryPrimitives.ReadUInt32LittleEndian(e) & FoldEntry.Present) != 0) f.Keep(_keys[r], e[4..]);
            else if (f.SlotOf.Remove(_keys[r], out var slot)) f.Free.Push(slot);
        }
    }

    /// <summary>Where a slice's unit's part ends for it: its row entries and deltas so far, that part's checksum.</summary>
    private struct Mark
    {
        public int Unit, Rows, Deltas;
        public uint? Checksum;
    }

    /// <summary>
    ///     A finalized member's own: its finalizer, the states it keeps (a slot per key), and its scratch — the slice's state
    ///     entries as it starts and folded, the finalizer's entries, the rows they make, and a snapshot's.
    /// </summary>
    private sealed class Finalizing(IBatchFinalizer finalizer, int stateSize)
    {
        public readonly IBatchFinalizer Finalizer = finalizer;
        public readonly Stack<int> Free = new();
        public readonly Dictionary<TKey, int> SlotOf = new();
        public readonly int StateSize = stateSize, StateStride = FoldEntry.RowStride(stateSize);
        public int Applied, Slots; // (the entries the last apply applied: their states kept on commit)
        public byte[] Entries = new byte[64 * FoldEntry.RowStride(stateSize)],
            Work = new byte[64 * FoldEntry.RowStride(stateSize)],
            Rows = [], States = [], Finalize = [];

        public TRow[] SnapshotRows = [];

        /// <summary>The finalizer's entries, <paramref name="bytes" /> of them, grown as needed.</summary>
        public Span<byte> Scratch(int bytes)
        {
            if (Finalize.Length < bytes) Array.Resize(ref Finalize, (int)BitOperations.RoundUpToPowerOf2((uint)bytes));
            return Finalize.AsSpan(0, bytes);
        }

        public void Keep(in TKey key, ReadOnlySpan<byte> state)
        {
            ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(SlotOf, key, out var exists);
            if (!exists && !Free.TryPop(out slot))
            {
                slot = Slots++;
                if (States.Length < Slots * StateSize)
                    Array.Resize(ref States, (int)BitOperations.RoundUpToPowerOf2((uint)(Slots * StateSize)));
            }

            state.CopyTo(States.AsSpan(slot * StateSize, StateSize));
        }
    }
}

/// <summary>A last-value source in an <see cref="IngestGroup" />: a unit's value is published with the unit.</summary>
public sealed class IngestValue : IngestMember
{
    private int _count;
    private byte[] _drain = [];

    private Entry[] _entries = new Entry[4];

    internal IngestValue(IngestGroup group, LastValueSource source) : base(group, source)
    {
        Value = source;
    }

    public LastValueSource Value { get; }

    internal override void BeginSlice()
    {
        _count = 0;
    }

    internal override void Gather(byte[] drain, int payload, in PartHeader part, int unit)
    {
        _drain = drain;
        if (_entries.Length == _count) Array.Resize(ref _entries, _count * 2);
        _entries[_count++] = new Entry
        {
            Unit = unit, At = payload, Length = part.Count, Delivery = part.Delivery,
            Compressible = (part.Flags & PartFlags.Compressible) != 0
        };
    }

    internal override bool Touched(int units)
    {
        return _count > 0 && _entries[0].Unit < units;
    }

    internal override void Fold(int units)
    {
    }

    internal override void Open(bool recovery)
    {
        Value.OpenStaged();
    }

    internal override void Apply(int units)
    {
        for (var i = _count - 1; i >= 0; i--)
            if (_entries[i].Unit < units)
            {
                var e = _entries[i];
                Value.Stage(_drain.AsSpan(e.At, e.Length), e.Delivery, e.Compressible);
                return;
            }
    }

    internal override StreamFault? Prepare()
    {
        return ((ITransactional)Value).PrepareTx();
    }

    internal override bool Commit(long? upstreamVersion)
    {
        return ((ITransactional)Value).CommitTx(upstreamVersion);
    }

    internal override void Rollback()
    {
        ((ITransactional)Value).RollbackTx();
    }

    internal override void Close()
    {
        Value.CloseStaged();
    }

    private struct Entry
    {
        public int Unit, At, Length;
        public Delivery Delivery;
        public bool Compressible;
    }
}

/// <summary>
///     A unit of work being built for an <see cref="IngestGroup" />: one part per member it changes, staged whole by
///     <see cref="Complete" /> (disposing without completing drops it). One open unit per thread; allocation-free after
///     first use.
/// </summary>
public ref struct IngestUnit
{
    private sealed class Scope
    {
        public byte[] Buffer = new byte[1024];
        public int Entries;
        public long? ExpectedUpstream;
        public UnitFlags Flags;
        public IngestGroup? Group;
        public int Length, Generation;
        public bool Supersedes, Open;
    }

    [ThreadStatic] private static Scope? t_scope;

    private readonly Scope _scope;
    private readonly int _generation;

    private IngestUnit(Scope scope)
    {
        _scope = scope;
        _generation = scope.Generation;
    }

    internal static IngestUnit Open(IngestGroup group, long? expectedUpstream, bool supersedes)
    {
        var scope = t_scope ??= new Scope();
        if (scope.Open) throw new InvalidOperationException("A unit is already open on this thread.");
        scope.Open = true;
        scope.Generation++;
        scope.Group = group;
        scope.ExpectedUpstream = expectedUpstream;
        scope.Supersedes = supersedes;
        scope.Flags = supersedes ? UnitFlags.Supersedes : 0;
        scope.Length = IngestGroup.RecordSize;
        scope.Entries = 0;
        return new IngestUnit(scope);
    }

    /// <summary>
    ///     Deltas for a table member, folded in order after the units before. <paramref name="expectedChecksum" />: its
    ///     checksum after this unit.
    /// </summary>
    public readonly void Post<TKey, TRow, TDelta>(IngestTable<TKey, TRow, TDelta> table, ReadOnlySpan<TKey> keys,
        ReadOnlySpan<TDelta> deltas, uint? expectedChecksum = null)
        where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged where TDelta : unmanaged
    {
        var scope = Check();
        if (scope.Supersedes)
            throw new InvalidOperationException(
                "A snapshot carries its members' whole state: rows and values, not deltas.");
        if (keys.Length != deltas.Length) throw new ArgumentException("Keys and deltas differ in length.");
        Part(scope, table, PartKind.Deltas, MemoryMarshal.AsBytes(keys), MemoryMarshal.AsBytes(deltas), keys.Length,
            expectedChecksum, 0, default);
    }

    /// <summary>A table member's whole state: these rows, every other row deleted (a unit carrying one starts a slice).</summary>
    public readonly void Rows<TKey, TRow, TDelta>(IngestTable<TKey, TRow, TDelta> table, ReadOnlySpan<TKey> keys,
        ReadOnlySpan<TRow> rows, uint? expectedChecksum = null)
        where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged where TDelta : unmanaged
    {
        var scope = Check();
        if (keys.Length != rows.Length) throw new ArgumentException("Keys and rows differ in length.");
        scope.Flags |= UnitFlags.HasRows;
        Part(scope, table, PartKind.Rows, MemoryMarshal.AsBytes(keys), MemoryMarshal.AsBytes(rows), keys.Length,
            expectedChecksum, 0, default);
    }

    /// <summary>A last-value member's next value.</summary>
    public readonly void Publish(IngestValue value, ReadOnlySpan<byte> bytes, Delivery delivery = Delivery.Auto,
        bool compressible = true)
    {
        var scope = Check();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes.Length, LastValueSource.MaxValueBytes);
        if (bytes.Length > ushort.MaxValue && delivery == Delivery.Inline)
            throw new ArgumentOutOfRangeException(nameof(bytes), "Values over 64 KB can't be inline.");
        Part(scope, value, PartKind.Value, default, bytes, bytes.Length, null,
            compressible ? PartFlags.Compressible : 0, delivery);
    }

    /// <summary>
    ///     Stages the unit: <see cref="PublishStatus.Delivered" /> if any member it names has a route, else
    ///     <see cref="PublishStatus.NoListeners" />; <see cref="PublishStatus.Full" /> (507) if refused. Throws while the
    ///     group is
    ///     faulted and no snapshot is staged.
    /// </summary>
    public readonly PublishStatus Complete(long? upstreamVersion = null)
    {
        var scope = Check();
        try
        {
            var flags = scope.Flags;
            if (scope.ExpectedUpstream.HasValue) flags |= UnitFlags.ExpectedUpstream;
            if (upstreamVersion.HasValue) flags |= UnitFlags.UpstreamVersion;
            MemoryMarshal.Write(scope.Buffer, new RecordHeader
            {
                Length = scope.Length, Kind = RecordKind.Unit, Flags = flags, Entries = scope.Entries,
                ExpectedUpstream = scope.ExpectedUpstream.GetValueOrDefault(),
                UpstreamVersion = upstreamVersion.GetValueOrDefault()
            });
            return scope.Group!.StageUnit(scope.Buffer.AsSpan(0, scope.Length), scope.Supersedes);
        }
        finally
        {
            scope.Open = false;
            scope.Group = null;
        }
    }

    /// <summary>Drops the unit unless completed.</summary>
    public readonly void Dispose()
    {
        if (_scope is null || !_scope.Open || _scope.Generation != _generation) return;
        _scope.Open = false;
        _scope.Group = null;
    }

    private readonly Scope Check()
    {
        if (_scope is null || !_scope.Open || _scope.Generation != _generation)
            throw new InvalidOperationException("This unit is no longer open.");
        return _scope;
    }

    private static void Part(Scope scope, IngestMember member, PartKind kind, ReadOnlySpan<byte> keys,
        ReadOnlySpan<byte> items, int count, uint? checksum, PartFlags flags, Delivery delivery)
    {
        if (member.Group != scope.Group)
            throw new ArgumentException("Not a member of this unit's group.", nameof(member));
        if (!member.IsMember) throw new InvalidOperationException("It has left the group.");
        var size = IngestGroup.PartSize + IngestGroup.Pad(keys.Length) + IngestGroup.Pad(items.Length);
        if (scope.Buffer.Length < scope.Length + size)
        {
            var grown = new byte[(int)BitOperations.RoundUpToPowerOf2((uint)(scope.Length + size))];
            scope.Buffer.AsSpan(0, scope.Length).CopyTo(grown);
            scope.Buffer = grown;
        }

        var part = scope.Buffer.AsSpan(scope.Length, size);
        MemoryMarshal.Write(part, new PartHeader
        {
            Length = size, Slot = member.Slot, Kind = kind, Count = count, Member = member.Id, Delivery = delivery,
            ExpectedChecksum = checksum.GetValueOrDefault(),
            Flags = flags | (checksum.HasValue ? PartFlags.ExpectedChecksum : 0)
        });
        keys.CopyTo(part[IngestGroup.PartSize..]);
        items.CopyTo(part[(IngestGroup.PartSize + IngestGroup.Pad(keys.Length))..]);
        scope.Length += size;
        scope.Entries += kind == PartKind.Value ? 1 : count;
    }
}

/// <summary>
///     A table's staged writer (<see cref="Table{TKey,TRow}.Ingest{TDelta}" />): an <see cref="IngestGroup" /> of the one
///     table,
///     posted to directly. Everything the group does applies — conflation, taking turns, metered slices, halving,
///     supersede,
///     refusal, checks — with each post a unit. <see cref="Close" /> lets the table go (to be written directly, or joined
///     into another group).
/// </summary>
public sealed class TableIngest<TKey, TRow, TDelta>
    where TKey : unmanaged, IEquatable<TKey>
    where TRow : unmanaged
    where TDelta : unmanaged
{
    private readonly IngestTable<TKey, TRow, TDelta> _member;

    internal TableIngest(Table<TKey, TRow> table, IBatchFold fold, int capacityBytes, int maxDeltas, long? sliceFuel)
    {
        Group = new IngestGroup(capacityBytes, maxDeltas, sliceFuel);
        _member = Group.Join<TKey, TRow, TDelta>(table, fold);
    }

    public Table<TKey, TRow> Table => _member.Table;

    /// <summary>The group of the one table.</summary>
    public IngestGroup Group { get; }

    internal bool QueueToPool
    {
        get => Group.QueueToPool;
        set => Group.QueueToPool = value;
    }

    public int Capacity => Group.Capacity;
    public int MaxDeltas => Group.MaxDeltas;

    /// <summary>
    ///     What one update's fold should use, for a metered fold (0: not metered): the group's, at most half the fold's
    ///     budget.
    /// </summary>
    public long SliceFuel => _member.FuelBudget == 0 ? 0 : Math.Min(Group.SliceFuel, _member.FuelBudget / 2);

    public double FuelPerDelta => _member.FuelPerDelta;
    public long Posts => Group.Posts;
    public long Batches => Group.Batches;
    public int LargestBatch => Group.LargestBatch;
    public long Applied => Group.Applied;
    public long Superseded => Group.Superseded;
    public long Dropped => Group.Dropped;
    public long Refused => Group.Refused;
    public long Halvings => Group.Halvings;
    public bool Idle => Group.Idle;

    /// <summary>
    ///     A rendezvous with a subscriber after what's been posted so far: <paramref name="correlation" /> — the id its client
    ///     gave the request — handed to it (through its <paramref name="mailbox" />) strictly after its state includes every
    ///     post made before this call: applied by the router, or superseded by a snapshot posted after it. A staged post's
    ///     version is made only when the router applies it, so it can't be named when it's posted: this names it then — at
    ///     once, if the router has finished them all already. False if the mailbox refused it then (full, or the subscription
    ///     ended); one resolved later and refused, the client's own timeout answers.
    /// </summary>
    public bool TryRendezvous(Mailbox mailbox, ReadOnlySpan<byte> correlation)
    {
        return Group.Rendezvous(_member, mailbox, correlation);
    }

    /// <summary>
    ///     <paramref name="then" />, with the table's version, once everything posted so far is finished: applied by the
    ///     router, or superseded by a snapshot posted after it — at once, if it all is. On the thread that finishes it: keep
    ///     it short. (What a message after a table's state waits on: a mailbox's post, after the version it's handed.)
    /// </summary>
    public void WhenApplied(Action<long> then)
    {
        Group.WhenApplied(_member, then);
    }

    /// <summary>
    ///     Stages deltas, to be folded in order after everything posted before them. <paramref name="expectedUpstream" />: the
    ///     upstream version they apply to; <paramref name="upstreamVersion" />: the one they make;
    ///     <paramref name="expectedChecksum" />: the table's checksum after them (checked if they're the last of their batch).
    ///     Returns 201/202 as a publish does — or 507, refused: it doesn't fit. Throws <see cref="StreamFaultException" />
    ///     while the table is faulted and no snapshot is posted to recover it.
    /// </summary>
    public PublishResult Post(ReadOnlySpan<TKey> keys, ReadOnlySpan<TDelta> deltas, long? expectedUpstream = null,
        long? upstreamVersion = null, uint? expectedChecksum = null)
    {
        if (keys.Length != deltas.Length) throw new ArgumentException("Keys and deltas differ in length.");
        return Answer(Group.StageSingle(_member, PartKind.Deltas, MemoryMarshal.AsBytes(keys),
            MemoryMarshal.AsBytes(deltas), keys.Length, false, expectedUpstream, upstreamVersion, expectedChecksum));
    }

    /// <summary>
    ///     Stages the table's whole state: <paramref name="keys" /> and their <paramref name="rows" />, every other row
    ///     deleted.
    ///     It supersedes everything staged before it, and is the recovery of a faulted table. Throws if it alone is larger
    ///     than the capacity; otherwise staged, whatever was pending.
    /// </summary>
    public PublishResult PostSnapshot(ReadOnlySpan<TKey> keys, ReadOnlySpan<TRow> rows, long? upstreamVersion = null,
        uint? expectedChecksum = null)
    {
        if (keys.Length != rows.Length) throw new ArgumentException("Keys and rows differ in length.");
        return Answer(Group.StageSingle(_member, PartKind.Rows, MemoryMarshal.AsBytes(keys),
            MemoryMarshal.AsBytes(rows), keys.Length, true, null, upstreamVersion, expectedChecksum));
    }

    private PublishResult Answer(PublishStatus status)
    {
        var ack = Table.Acknowledge();
        return status == PublishStatus.Full ? ack with { Status = PublishStatus.Full } : ack;
    }

    /// <summary>Lets the table go, after what's staged: then it is written directly again, or by a group that joined it.</summary>
    public void Close()
    {
        Group.Leave(_member);
    }

    internal bool DrainOnce()
    {
        return Group.DrainOnce();
    }

    internal void DrainPending()
    {
        Group.DrainPending();
    }
}

/// <summary>
///     A table's staged writer whose fold keeps a state of its own per key (<see cref="Table{TKey,TRow}.Ingest{TState,TDelta}" />):
///     the provider posts deltas to the states, or a snapshot of them; the router folds them, keeps the states, and
///     applies to the table the rows its finalizer makes of them (<see cref="IBatchFinalizer" />) — what subscribers see,
///     the states never sent. Otherwise a <see cref="TableIngest{TKey,TRow,TDelta}" />: an <see cref="IngestGroup" /> of
///     the one table, each post a unit, everything the group does applying.
///     <para>
///         A table that has rows when its ingest is made has no states behind them the router could know (an average has
///         no sums): its deltas are refused until a snapshot of states is posted (<see cref="AwaitingStates" />). An empty
///         table's states are all absent, known: deltas are taken at once.
///     </para>
/// </summary>
public sealed class FinalizedIngest<TKey, TRow, TState, TDelta>
    where TKey : unmanaged, IEquatable<TKey>
    where TRow : unmanaged
    where TState : unmanaged
    where TDelta : unmanaged
{
    private readonly IngestTable<TKey, TRow, TDelta> _member;
    private volatile bool _awaitingStates;

    internal FinalizedIngest(Table<TKey, TRow> table, IBatchFold fold, IBatchFinalizer finalizer, int capacityBytes,
        int maxDeltas, long? sliceFuel)
    {
        Group = new IngestGroup(capacityBytes, maxDeltas, sliceFuel);
        _member = new IngestTable<TKey, TRow, TDelta>(Group, table, fold, finalizer, Unsafe.SizeOf<TState>());
        Group.Enlist(_member);
        _awaitingStates = table.Count > 0; // (its only writer from now on: nothing adds a row it hasn't the state of)
    }

    /// <summary>
    ///     The table had rows when the ingest was made, and no snapshot of states has been posted since: deltas are
    ///     refused, as the states they'd fold onto are unknown.
    /// </summary>
    public bool AwaitingStates => _awaitingStates;

    public Table<TKey, TRow> Table => _member.Table;

    /// <summary>The group of the one table.</summary>
    public IngestGroup Group { get; }

    internal bool QueueToPool
    {
        get => Group.QueueToPool;
        set => Group.QueueToPool = value;
    }

    public double FuelPerDelta => _member.FuelPerDelta;
    public long Posts => Group.Posts;
    public long Batches => Group.Batches;
    public long Applied => Group.Applied;
    public long Superseded => Group.Superseded;
    public long Refused => Group.Refused;

    /// <summary>
    ///     Stages deltas to the states, folded in order after everything posted before them, as
    ///     <see cref="TableIngest{TKey,TRow,TDelta}.Post" />: 201/202 as a publish answers, or 507 (it doesn't fit).
    ///     <paramref name="expectedChecksum" /> is the table's — its rows', as finalized — after them. Throws
    ///     <see cref="InvalidOperationException" /> while <see cref="AwaitingStates" />.
    /// </summary>
    public PublishResult Post(ReadOnlySpan<TKey> keys, ReadOnlySpan<TDelta> deltas, long? expectedUpstream = null,
        long? upstreamVersion = null, uint? expectedChecksum = null)
    {
        if (keys.Length != deltas.Length) throw new ArgumentException("Keys and deltas differ in length.");
        if (_awaitingStates)
            throw new InvalidOperationException(
                $"'{Table.Name}' had rows when its ingest was made, and the router holds no states for them: post a snapshot of states first (PostSnapshot).");
        return Answer(Group.StageSingle(_member, PartKind.Deltas, MemoryMarshal.AsBytes(keys),
            MemoryMarshal.AsBytes(deltas), keys.Length, false, expectedUpstream, upstreamVersion, expectedChecksum));
    }

    /// <summary>
    ///     Stages the whole state: <paramref name="keys" /> and their <paramref name="states" />, every other key's
    ///     forgotten — the table replaced by their rows, finalized. It supersedes everything staged before it, and is the
    ///     recovery of a faulted table.
    /// </summary>
    public PublishResult PostSnapshot(ReadOnlySpan<TKey> keys, ReadOnlySpan<TState> states, long? upstreamVersion = null,
        uint? expectedChecksum = null)
    {
        if (keys.Length != states.Length) throw new ArgumentException("Keys and states differ in length.");
        var answer = Answer(Group.StageSingle(_member, PartKind.Rows, MemoryMarshal.AsBytes(keys),
            MemoryMarshal.AsBytes(states), keys.Length, true, null, upstreamVersion, expectedChecksum));
        _awaitingStates = false; // staged: deltas after it fold onto its states, whatever it supersedes
        return answer;
    }

    /// <summary>As <see cref="TableIngest{TKey,TRow,TDelta}.WhenApplied" />.</summary>
    public void WhenApplied(Action<long> then)
    {
        Group.WhenApplied(_member, then);
    }

    private PublishResult Answer(PublishStatus status)
    {
        var ack = Table.Acknowledge();
        return status == PublishStatus.Full ? ack with { Status = PublishStatus.Full } : ack;
    }

    /// <summary>Lets the table go, after what's staged (its states with it).</summary>
    public void Close()
    {
        Group.Leave(_member);
    }

    internal void DrainPending()
    {
        Group.DrainPending();
    }
}
