namespace Tapline;

/// <summary>A source that can take part in a <see cref="Transaction" />: the phases of one atomic update.</summary>
internal interface ITransactional
{
    Source Source { get; }

    /// <summary>The update's pending fault only (postconditions apply to completion).</summary>
    StreamFault? PendingTx { get; }

    /// <summary>Takes the source's lock and opens an update; or returns the precondition's fault (lock released).</summary>
    StreamFault? EnterTx(long? expectedUpstream, bool recovery, bool shared);

    /// <summary>The update's pending fault (a throwing fold, Fail() inside it) or a failed postcondition.</summary>
    StreamFault? PrepareTx();

    /// <summary>Publishes the update. True when taps must be woken.</summary>
    bool CommitTx(long? upstreamVersion);

    void RollbackTx();
    bool FaultTx(StreamFault fault);
    void ExitTx();
    void NotifyTx();
}

/// <summary>
///     One unit of work across several sources — tables and last-value sources — published atomically: every member's
///     change becomes visible together or not at all. Locks are taken in a fixed (process-wide) order, the same order
///     watch
///     readers use, so a <see cref="Outlet.Watch(string?, ErrorPolicy?, ReadOnlySpan{WatchMember})" /> never sees part of a transaction and nothing can deadlock.
///     <code>
/// using (var tx = Transaction.Begin(positions, balances, marks["BTC"]))
/// {
///     tx.Of(positions).Fold&lt;long, Add&lt;long&gt;&gt;(new(acct, sym), +2);
///     tx.Of(balances).Set(acct, newBalance);
///     tx.Of(marks["BTC"]).Publish(markBytes);
///     tx.Complete(upstreamVersion: msg.Seq);
/// }
/// </code>
///     <see cref="Complete" /> checks every member (pending fold faults,
///     <see cref="Table{TKey,TRow}.Updater.ExpectChecksum" />
///     postconditions) before publishing any. If one fails, or a precondition fails at
///     <see cref="Begin(long?, ReadOnlySpan{Source})" />,
///     all members roll back and all fault — they all missed the same unit of work. Disposing without completing rolls
///     back.
///     One open transaction per thread; allocation-free after first use on a thread.
/// </summary>
public ref struct Transaction
{
    private sealed class Scope
    {
        public int Count, Generation;
        public ITransactional[] Members = new ITransactional[8];
        public bool Open;
    }

    [ThreadStatic] private static Scope? t_scope;

    private readonly Scope _scope;
    private readonly int _generation;

    private Transaction(Scope scope)
    {
        _scope = scope;
        _generation = scope.Generation;
    }

    public static Transaction Begin(params ReadOnlySpan<Source> sources)
    {
        return Begin(null, false, sources);
    }

    /// <summary>
    ///     Opens the transaction only if every table member is at <paramref name="expectedUpstream" />; otherwise all
    ///     members fault.
    /// </summary>
    public static Transaction Begin(long? expectedUpstream, params ReadOnlySpan<Source> sources)
    {
        return Begin(expectedUpstream, false, sources);
    }

    /// <summary>Rebuilds faulted members (typically Clear() + an upstream snapshot); completing clears their faults.</summary>
    public static Transaction BeginRecovery(params ReadOnlySpan<Source> sources)
    {
        return Begin(null, true, sources);
    }

    private static Transaction Begin(long? expectedUpstream, bool recovery, scoped ReadOnlySpan<Source> sources)
    {
        var scope = t_scope ??= new Scope();
        if (scope.Open) throw new InvalidOperationException("A transaction is already open on this thread.");
        if (sources.IsEmpty) throw new ArgumentException("A transaction needs at least one source.", nameof(sources));
        if (scope.Members.Length < sources.Length) scope.Members = new ITransactional[sources.Length * 2];
        var members = scope.Members;
        var n = 0;
        foreach (var s in sources)
        {
            if (s is not ITransactional t)
                throw new ArgumentException($"{s.GetType().Name} can't take part in a transaction.", nameof(sources));
            var i = n++; // insertion sort by SourceId: the canonical lock order
            while (i > 0 && members[i - 1].Source.SourceId > s.SourceId)
            {
                members[i] = members[i - 1];
                i--;
            }

            if (i > 0 && members[i - 1].Source.SourceId == s.SourceId)
                throw new ArgumentException("A source appears twice.", nameof(sources));
            members[i] = t;
        }

        scope.Count = n;

        for (var i = 0; i < n; i++)
        {
            StreamFault? fault;
            try
            {
                fault = members[i].EnterTx(expectedUpstream, recovery, true);
            }
            catch
            {
                for (var j = i - 1; j >= 0; j--)
                {
                    members[j].RollbackTx();
                    members[j].ExitTx();
                }

                Array.Clear(members, 0, n);
                throw;
            }

            if (fault is null) continue;
            for (var j = i - 1; j >= 0; j--)
            {
                members[j].RollbackTx();
                members[j].ExitTx();
            }

            FaultAll(members.AsSpan(0, n), fault); // every member missed the same unit of work
            Array.Clear(members, 0, n);
            throw new StreamFaultException(fault);
        }

        if (n > 1 && !LockDomain.Shared(members.AsSpan(0, n)))
            LockDomain.Merge(members.AsSpan(0, n)); // read together from now on
        scope.Open = true;
        scope.Generation++;
        return new Transaction(scope);
    }

    /// <summary>The member table's updater (Set, Delete, Fold, Clear, ExpectChecksum) — not completed on its own.</summary>
    public Table<TKey, TRow>.Updater Of<TKey, TRow>(Table<TKey, TRow> table)
        where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged
    {
        Check();
        return table.SharedUpdater();
    }

    /// <summary>Stages a member last-value source's next value (published on commit).</summary>
    public ValueStager Of(LastValueSource source)
    {
        Check();
        return new ValueStager(source);
    }

    public readonly ref struct ValueStager
    {
        private readonly LastValueSource _source;

        internal ValueStager(LastValueSource source)
        {
            _source = source;
        }

        public void Publish(ReadOnlySpan<byte> value, Delivery delivery = Delivery.Auto, bool compressible = true)
        {
            _source.Stage(value, delivery, compressible);
        }
    }

    /// <summary>
    ///     Publishes every member's update atomically. <paramref name="upstreamVersion" /> becomes each table's
    ///     UpstreamVersion.
    /// </summary>
    public void Complete(long? upstreamVersion = null)
    {
        Check();
        var members = _scope.Members.AsSpan(0, _scope.Count);
        StreamFault? fault = null;
        foreach (var m in members)
            if ((fault = m.PrepareTx()) is not null)
                break;
        if (fault is not null)
        {
            Abort(members, fault);
            throw new StreamFaultException(fault);
        }

        var notify = members.Length <= 64 ? stackalloc bool[members.Length] : new bool[members.Length];
        for (var i = 0; i < members.Length; i++)
            notify[i] = members[i].CommitTx(upstreamVersion); // all validated: publish all
        ExitAll(members);
        for (var i = 0; i < members.Length; i++)
            if (notify[i])
                members[i].NotifyTx();
        members.Clear();
    }

    /// <summary>Rolls back unless completed. A pending fold fault in any member faults all of them.</summary>
    public void Dispose()
    {
        if (_scope is null || !_scope.Open || _scope.Generation != _generation) return;
        var members = _scope.Members.AsSpan(0, _scope.Count);
        StreamFault? fault = null;
        foreach (var m in members)
            if ((fault = m.PendingTx) is not null)
                break;
        if (fault is not null)
        {
            Abort(members, fault);
            return;
        }

        foreach (var m in members) m.RollbackTx();
        ExitAll(members);
        members.Clear();
    }

    private void Check()
    {
        if (_scope is null || !_scope.Open || _scope.Generation != _generation)
            throw new InvalidOperationException("This transaction is no longer open.");
    }

    private void Abort(Span<ITransactional> members, StreamFault fault)
    {
        foreach (var m in members) m.RollbackTx();
        ExitAll(members);
        FaultAll(members, fault);
        members.Clear();
    }

    private void ExitAll(Span<ITransactional> members)
    {
        for (var i = members.Length - 1; i >= 0; i--) members[i].ExitTx();
        _scope.Open = false;
    }

    private static void FaultAll(Span<ITransactional> members, StreamFault cause)
    {
        foreach (var m in members)
            if (m.FaultTx(cause with { Version = m.Source.Version }))
                m.NotifyTx(); // first fault wins per member
    }
}