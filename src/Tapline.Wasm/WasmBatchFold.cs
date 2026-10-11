using Wasmtime;
using WasmModule = Wasmtime.Module;

namespace Tapline.Wasm;

/// <summary>
///     A fold as a WebAssembly module (<see cref="IBatchFold" />): a data provider's function conflating its updates into
///     a
///     table's rows, run by the router alone — sandboxed by Wasmtime, bounded by <see cref="WasmLimits" /> (fuel, memory),
///     nothing imported. Hand it to an update (<c>u.Fold(keys, deltas, fold)</c>) or an ingest. It is metered
///     (<see cref="IMeteredFold" />): each call says the fuel it used, so an ingest sizes its slices by it. One that fails
///     (a
///     trap, its fuel out, its memory full) throws — out of fuel, <see cref="FoldBudgetExceededException" /> — so an
///     update
///     rolls back and the table faults (<see cref="FaultCode.FoldFailed" />), and an ingest folds fewer posts first.
///     <para>
///         Folds may run concurrently (tables updated on several threads): each takes an instance of its own from a small
///         pool,
///         made as needed; one that fails is discarded rather than reused. The function must be pure — the same rows and
///         deltas
///         in, the same rows out, whichever instance runs it.
///     </para>
/// </summary>
public sealed class WasmBatchFold : IMeteredFold, IDisposable
{
    private readonly WasmInstances<Action<int, int, int, int, int>> _instances;

    private WasmBatchFold(byte[] bytes, WasmLimits limits)
    {
        _instances = new WasmInstances<Action<int, int, int, int, int>>(bytes, limits, "fold",
            "a function (i32, i32, i32, i32, i32)",
            static (instance, name) => instance.GetAction<int, int, int, int, int>(name),
            "a fold imports nothing (it is given nothing but its rows and deltas)");
    }

    public WasmLimits Limits => _instances.Limits;

    /// <summary>Folds that failed (trapped, ran out of fuel, put the batch outside its memory).</summary>
    public int Failures => _instances.Failures;

    public void Dispose()
    {
        _instances.Dispose();
    }

    /// <summary>Its fuel per call (<see cref="WasmLimits.Fuel" />).</summary>
    public long Budget => _instances.Limits.Fuel;

    /// <summary>
    ///     Runs the module on a batch: copies the rows, then the deltas right after them, into its memory where its
    ///     <c>buffer</c> says, calls <c>fold</c>, copies the rows back. Throws if it fails.
    /// </summary>
    public void Fold(Span<byte> rows, int rowCount, int rowSize, ReadOnlySpan<byte> deltas, int deltaCount,
        int deltaSize)
    {
        FoldMetered(rows, rowCount, rowSize, deltas, deltaCount, deltaSize);
    }

    /// <summary>As <see cref="Fold" />; returns the fuel the call used (its <c>buffer</c> included).</summary>
    public long FoldMetered(Span<byte> rows, int rowCount, int rowSize, ReadOnlySpan<byte> deltas, int deltaCount,
        int deltaSize)
    {
        var runner = _instances.Rent();
        var ok = false;
        try
        {
            var at = runner.Buffer(rows.Length + deltas.Length);
            var batch = runner.Window(at, rows.Length + deltas.Length);
            rows.CopyTo(batch);
            deltas.CopyTo(batch[rows.Length..]);
            runner.Call(at, rowCount, rowSize, deltaCount, deltaSize);
            runner.Window(at, rows.Length).CopyTo(rows);
            var used = Budget - (long)runner.Store.Fuel;
            ok = true;
            return used;
        }
        catch (TrapException e) when (e.Type == TrapCode.OutOfFuel)
        {
            throw new FoldBudgetExceededException($"Out of fuel: the batch needed more than {Budget:N0}.", e);
        }
        finally
        {
            _instances.Return(runner, ok);
        }
    }

    /// <summary>
    ///     A fold from a module's binary. Throws <see cref="ArgumentException" /> if it isn't one: invalid, importing
    ///     anything, or without the exports (memory, buffer, fold) of their types.
    /// </summary>
    public static WasmBatchFold FromBytes(ReadOnlySpan<byte> wasm, WasmLimits? limits = null)
    {
        return new WasmBatchFold(wasm.ToArray(), limits ?? WasmLimits.Default);
    }

    /// <summary>A fold from a module's text (WAT).</summary>
    public static WasmBatchFold FromText(string wat, WasmLimits? limits = null)
    {
        byte[] bytes;
        try
        {
            bytes = WasmModule.ConvertText(wat);
        }
        catch (WasmtimeException e)
        {
            throw new ArgumentException($"Not valid WebAssembly text: {e.Message}", nameof(wat), e);
        }

        return new WasmBatchFold(bytes, limits ?? WasmLimits.Default);
    }
}