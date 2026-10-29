using System.Collections.Concurrent;
using System.Numerics;
using Chalk.Sources;
using Tapline.Grid.Provider;

namespace Tapline.Grid.ChalkQL;

/// <summary>
///     The arenas a store's statements run in when a read carries a memory budget (<see cref="GridRead{TKey,TRow}.MaxBytes" />):
///     ChalkQL counts every byte an execution rents against its arena's <see cref="ArenaOptions.MaxBytes" /> and refuses the
///     rental past it, so a budget is kept as memory is taken, never estimated. Budgets are taken in classes, each a power
///     of two at or below the one asked — never more than the host allowed — so reads of about the same budget share warm
///     arenas: at most <see cref="Kept" /> idle a class, each keeping what its executions need up to
///     <see cref="RetainBytes" /> (or its budget, if less).
/// </summary>
internal sealed class ChalkArenas : IDisposable
{
    private readonly ConcurrentDictionary<int, ConcurrentBag<ExecutionArena>> _idle = new(); // by the class's power of two
    private bool _disposed;

    /// <summary>Idle arenas kept a class: one a core, by default.</summary>
    public int Kept { get; init; } = Environment.ProcessorCount;

    /// <summary>The most an idle arena keeps warm (64 MiB by default, ChalkQL's own).</summary>
    public long RetainBytes { get; init; } = 64L << 20;

    /// <summary>
    ///     What the idle arenas keep warm, beside the most they may: <see cref="Kept" /> a class in use, each its budget or
    ///     <see cref="RetainBytes" />, the less. An arena a read has rented is the read's, gone with it, and not counted.
    /// </summary>
    public GridMemory Memory
    {
        get
        {
            long held = 0, bound = 0;
            foreach (var (shift, idle) in _idle)
            {
                foreach (var arena in idle) held += arena.RetainedBytes;
                bound += Kept * Math.Min(1L << shift, RetainBytes);
            }

            return new GridMemory(held, bound);
        }
    }

    /// <summary>The budget a read of <paramref name="maxBytes" /> runs under: the power of two at or below it.</summary>
    public static long ClassOf(uint maxBytes)
    {
        return 1L << BitOperations.Log2(maxBytes);
    }

    /// <summary>An arena for one execution under <paramref name="maxBytes" /> (more than none), given back by <see cref="Return" />.</summary>
    public ExecutionArena Rent(uint maxBytes)
    {
        var shift = BitOperations.Log2(maxBytes);
        if (_idle.TryGetValue(shift, out var idle) && idle.TryTake(out var arena)) return arena;
        var budget = 1L << shift;
        return new ExecutionArena(new ArenaOptions { MaxBytes = budget, RetainBytes = Math.Min(budget, RetainBytes) });
    }

    /// <summary>An arena whose execution has ended, its batches disposed: kept for the next, or let go.</summary>
    public void Return(ExecutionArena arena)
    {
        var idle = _idle.GetOrAdd(BitOperations.Log2((ulong)arena.Options.MaxBytes!.Value), static _ => []);
        if (Volatile.Read(ref _disposed) || idle.Count >= Kept)
        {
            arena.Dispose();
            return;
        }

        arena.Trim();
        idle.Add(arena);
    }

    public void Dispose()
    {
        Volatile.Write(ref _disposed, true);
        foreach (var idle in _idle.Values)
            while (idle.TryTake(out var arena))
                arena.Dispose();
    }
}
