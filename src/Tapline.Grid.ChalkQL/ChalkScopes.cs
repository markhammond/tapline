using Chalk.Client;
using Chalk.Incremental;
using Tapline.Grid.Provider;

namespace Tapline.Grid.ChalkQL;

/// <summary>
///     The views' filters over the state tail as ChalkQL standing views (grid-sources.md §3), as the blotter's live scopes
///     are: each view's filter, the one SQL statement its pages push down to the store, kept over the tail's tables
///     (<see cref="ChalkTail{TKey,TRow}" />) — one view of the keys whose newest version passes, one of those whose store
///     row last did — so a row passes as the store would pass it. Each change to the tail is one refresh of the tables
///     and an update of every view; an update costs what changed, not what the tail holds.
/// </summary>
/// <remarks>
///     A view whose update fails (past its state budget, say) is faulted, and the grid's next read of it refused with
///     the failure, the read after opening it again. Driven by its <see cref="StoreAndStream{TKey,TRow,TStamp}" />'s catch-up,
///     one call at a time.
/// </remarks>
public sealed class ChalkScopes<TKey, TRow> : IGridScopes<TKey, TRow> where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged
{
    private readonly ChalkEngine _engine;
    private readonly ViewMaintenance _maintenance;
    private readonly List<Scope> _open = [];
    private readonly ChalkRows<TKey, TRow> _rows;
    private readonly long _stateBudget;
    private readonly ChalkTail<TKey, TRow> _tail;
    private TRow[] _newest = [], _stored = [], _goneNewest = [], _goneStored = [];
    private bool _synced = true; // (the tables the tail as published: false after a refresh failed, till a view opens over it)

    /// <param name="engine">An engine with the tail's <see cref="ChalkTail{TKey,TRow}.Source" /> among its sources.</param>
    /// <param name="tail">The tail's tables.</param>
    /// <param name="rows">The row's columns.</param>
    /// <param name="maintenance">How the views update: incrementally, what changed (ChalkQL's CHALK003), or in full.</param>
    /// <param name="stateBudget">The most a view may hold, in bytes: past it, its update fails and the view is faulted.</param>
    public ChalkScopes(ChalkEngine engine, ChalkTail<TKey, TRow> tail, ChalkRows<TKey, TRow> rows,
        ViewMaintenance maintenance = ViewMaintenance.Incremental, long stateBudget = 64L << 20)
    {
        (_engine, _tail, _rows, _maintenance, _stateBudget) = (engine, tail, rows, maintenance, stateBudget);
    }

    /// <summary>The views open.</summary>
    public int Open
    {
        get
        {
            lock (_open) return _open.Count;
        }
    }

    /// <summary>
    ///     What the views' standing views hold (ChalkQL's count of their state), beside the most they may: two a view, each
    ///     its state budget. The tail's tables they read aren't counted.
    /// </summary>
    public GridMemory Memory
    {
        get
        {
            long held = 0;
            lock (_open)
            {
                foreach (var scope in _open) held += scope.StateBytes;
                return new GridMemory(held, 2 * _stateBudget * _open.Count);
            }
        }
    }

    public async ValueTask<IGridScope<TKey, TRow>> OpenAsync(GridView view, ReadOnlyMemory<GridTailRow<TKey, TRow>> tail,
        CancellationToken cancellationToken)
    {
        var filter = ChalkFilter.Of(view);
        if (!_synced) await ReplaceAsync(tail, cancellationToken).ConfigureAwait(false);
        var newest = await MaterializeAsync(_tail.Newest, filter, cancellationToken).ConfigureAwait(false);
        StandingView<TKey, long> stored;
        try
        {
            stored = await MaterializeAsync(_tail.Stored, filter, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await newest.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var scope = new Scope(this, newest, stored);
        scope.Drain();
        lock (_open) _open.Add(scope);
        return scope;
    }

    public async ValueTask EvaluateAsync(ReadOnlyMemory<GridTailRow<TKey, TRow>> changed, ReadOnlyMemory<GridTailRow<TKey, TRow>> gone,
        CancellationToken cancellationToken)
    {
        Collect(changed.Span, gone.Span);
        try
        {
            await _engine.RefreshAsync(this, static (refresh, s) => s.Plan(refresh), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _synced = false;
            throw;
        }

        foreach (var scope in _open)
            try
            {
                await scope.UpdateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                scope.Fault = failure;
            }
    }

    public void Publish()
    {
        foreach (var scope in _open) scope.Publish();
    }

    private ValueTask<StandingView<TKey, long>> MaterializeAsync(string table, ChalkFilter filter, CancellationToken cancellationToken)
    {
        return _engine.MaterializeAsync($"SELECT {_rows.KeyColumn} FROM {table} WHERE {filter.Where}",
            new ViewOptions<TKey, long>
            {
                Key = [_rows.KeyColumn],
                Map = static (in ViewRow row, out TKey key, out long seen) =>
                {
                    key = ChalkRows<TKey, TRow>.KeyFrom(row, 0);
                    seen = 0;
                },
                LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
                Parameters = filter.Parameters,
                StateBudget = _stateBudget,
                Maintenance = _maintenance,
                ResetWhen = double.PositiveInfinity // (drained after every update: never far enough behind for a reset to pay)
            }, cancellationToken);
    }

    /// <summary>A change's rows for the tables: the newest versions and the store's rows upserted, those let go deleted.</summary>
    private void Collect(ReadOnlySpan<GridTailRow<TKey, TRow>> changed, ReadOnlySpan<GridTailRow<TKey, TRow>> gone)
    {
        int stored = 0, goneStored = 0;
        foreach (ref readonly var row in changed)
            if (row.HasStored)
                stored++;
        foreach (ref readonly var row in gone)
            if (row.HasStored)
                goneStored++;
        // (fresh arrays: a refresh may keep what it's handed)
        (_newest, _stored, _goneNewest, _goneStored) = (new TRow[changed.Length], new TRow[stored], new TRow[gone.Length], new TRow[goneStored]);
        (stored, goneStored) = (0, 0);
        for (var i = 0; i < changed.Length; i++)
        {
            _newest[i] = changed[i].Newest;
            if (changed[i].HasStored) _stored[stored++] = changed[i].Stored;
        }

        for (var i = 0; i < gone.Length; i++)
        {
            _goneNewest[i] = gone[i].Newest;
            if (gone[i].HasStored) _goneStored[goneStored++] = gone[i].Stored;
        }
    }

    private void Plan(RefreshBuilder refresh)
    {
        _tail.Plan(refresh, _goneNewest, _goneStored, _newest, _stored);
    }

    /// <summary>The tables made the tail as published again, after a refresh that failed.</summary>
    private async ValueTask ReplaceAsync(ReadOnlyMemory<GridTailRow<TKey, TRow>> tail, CancellationToken cancellationToken)
    {
        var rows = tail.ToArray();
        (_newest, _stored) = ([.. rows.Select(r => r.Newest)], [.. rows.Where(r => r.HasStored).Select(r => r.Stored)]);
        await _engine.RefreshAsync(this, static (refresh, s) => s._tail.Replace(refresh, s._newest, s._stored), cancellationToken)
            .ConfigureAwait(false);
        _synced = true;
    }

    /// <summary>One view's filter: the keys whose newest version passes, and those whose store row did, as published.</summary>
    private sealed class Scope(ChalkScopes<TKey, TRow> scopes, StandingView<TKey, long> newest, StandingView<TKey, long> stored)
        : IGridScope<TKey, TRow>
    {
        private readonly Sink _newest = new(), _stored = new();
        private readonly HashSet<TKey> _passing = [], _storedPassing = [];
        private bool _closed; // (under the scopes' lock: its views disposed, or about to be)
        private ViewCursor _newestCursor = ViewCursor.Start, _storedCursor = ViewCursor.Start;

        public Exception? Fault { get; set; }

        public bool TryMatch(in TKey key, out bool newest, out bool stored)
        {
            (newest, stored) = (_passing.Contains(key), _storedPassing.Contains(key));
            return newest || stored;
        }

        /// <summary>What its two standing views hold, as ChalkQL counts it.</summary>
        public long StateBytes => newest.StateBytes + stored.StateBytes;

        /// <summary>What its two standing views hold, beside their state budgets: none once closed.</summary>
        public GridMemory Memory
        {
            get
            {
                lock (scopes._open) return _closed ? default : new GridMemory(StateBytes, 2 * scopes._stateBudget);
            }
        }

        public async ValueTask DisposeAsync()
        {
            lock (scopes._open)
            {
                scopes._open.Remove(this);
                _closed = true;
            }

            await newest.DisposeAsync().ConfigureAwait(false);
            await stored.DisposeAsync().ConfigureAwait(false);
        }

        public async ValueTask UpdateAsync(CancellationToken cancellationToken)
        {
            await newest.UpdateAsync(cancellationToken).ConfigureAwait(false);
            await stored.UpdateAsync(cancellationToken).ConfigureAwait(false);
            Drain();
        }

        /// <summary>What the views changed, staged for <see cref="Publish" />.</summary>
        public void Drain()
        {
            while (newest.Drain(ref _newestCursor, _newest, 65536) == DrainOutcome.Partial)
            {
            }

            while (stored.Drain(ref _storedCursor, _stored, 65536) == DrainOutcome.Partial)
            {
            }
        }

        public void Publish()
        {
            _newest.Into(_passing);
            _stored.Into(_storedPassing);
        }
    }

    /// <summary>A view's changes since the last publish: a reset (the whole result follows), then keys in or out.</summary>
    private sealed class Sink : IViewSink<TKey, long>
    {
        private readonly List<(TKey Key, bool In)> _changes = [];
        private bool _reset;

        public void Reset()
        {
            _reset = true;
            _changes.Clear();
        }

        public void Upsert(in TKey key, in long row)
        {
            _changes.Add((key, true));
        }

        public void Delete(in TKey key)
        {
            _changes.Add((key, false));
        }

        public void Commit(long version, uint checksum)
        {
        }

        public void Into(HashSet<TKey> passing)
        {
            if (_reset) passing.Clear();
            foreach (var (key, @in) in _changes)
                if (@in) passing.Add(key);
                else passing.Remove(key);
            _reset = false;
            _changes.Clear();
        }
    }
}
