using Wasmtime;
using WasmModule = Wasmtime.Module;

namespace Tapline.Wasm;

/// <summary>
///     A fold's finish as a WebAssembly module (<see cref="IBatchFinalizer" />): what subscribers see of the states a fold
///     keeps, made by the router alone — sandboxed by Wasmtime, bounded by <see cref="WasmLimits" /> (fuel, memory),
///     nothing imported. The module exports <c>memory</c>, <c>buffer</c> (as a fold's) and
///     <c>finalize(at, count, stateSize, rowSize)</c>, given the entries at <c>at</c>; it may be the fold's own module,
///     exporting both. One that fails (a trap, its fuel out, the entries outside its memory) throws, failing the batch as
///     a fold that fails does.
///     <para>
///         Finalizers may run concurrently: each takes an instance of its own from a small pool, made as needed; one that
///         fails is discarded rather than reused. The function must be pure — the same states in, the same rows out.
///     </para>
/// </summary>
public sealed class WasmBatchFinalizer : IBatchFinalizer, IDisposable
{
    private readonly WasmInstances<Action<int, int, int, int>> _instances;

    private WasmBatchFinalizer(byte[] bytes, WasmLimits limits)
    {
        _instances = new WasmInstances<Action<int, int, int, int>>(bytes, limits, "finalize",
            "a function (i32, i32, i32, i32)",
            static (instance, name) => instance.GetAction<int, int, int, int>(name),
            "a finalizer imports nothing (it is given nothing but its states)");
    }

    public WasmLimits Limits => _instances.Limits;

    /// <summary>Finalizers that failed (trapped, ran out of fuel, put the entries outside its memory).</summary>
    public int Failures => _instances.Failures;

    public void Dispose()
    {
        _instances.Dispose();
    }

    /// <summary>
    ///     Runs the module on a batch's entries: copies them into its memory where its <c>buffer</c> says, calls
    ///     <c>finalize</c>, copies them back. Throws if it fails.
    /// </summary>
    public void Finalize(Span<byte> entries, int count, int stateSize, int rowSize)
    {
        var runner = _instances.Rent();
        var ok = false;
        try
        {
            var at = runner.Buffer(entries.Length);
            entries.CopyTo(runner.Window(at, entries.Length));
            runner.Call(at, count, stateSize, rowSize);
            runner.Window(at, entries.Length).CopyTo(entries);
            ok = true;
        }
        catch (TrapException e) when (e.Type == TrapCode.OutOfFuel)
        {
            throw new FoldBudgetExceededException($"Out of fuel: finalizing needed more than {Limits.Fuel:N0}.", e);
        }
        finally
        {
            _instances.Return(runner, ok);
        }
    }

    /// <summary>
    ///     A finalizer from a module's binary. Throws <see cref="ArgumentException" /> if it isn't one: invalid, importing
    ///     anything, or without the exports (memory, buffer, finalize) of their types.
    /// </summary>
    public static WasmBatchFinalizer FromBytes(ReadOnlySpan<byte> wasm, WasmLimits? limits = null)
    {
        return new WasmBatchFinalizer(wasm.ToArray(), limits ?? WasmLimits.Default);
    }

    /// <summary>A finalizer from a module's text (WAT).</summary>
    public static WasmBatchFinalizer FromText(string wat, WasmLimits? limits = null)
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

        return new WasmBatchFinalizer(bytes, limits ?? WasmLimits.Default);
    }
}
