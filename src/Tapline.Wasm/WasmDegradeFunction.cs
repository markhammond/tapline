using System.Security.Cryptography;
using Wasmtime;
using WasmModule = Wasmtime.Module;

namespace Tapline.Wasm;

/// <summary>
///     A data provider's stale-data function as a WebAssembly module (<see cref="IPortableDegradeFunction" />): run by the
///     router — sandboxed by Wasmtime, bounded by <see cref="WasmLimits" /> (fuel, memory), nothing imported — and, being
///     portable, by the subscriber's browser itself while the router is out of its reach. Hand it to the router in an
///     <see cref="ErrorPolicy" />, like any degrade function.
///     <para>
///         Applications may run concurrently (one per outlet building a frame): each takes an instance of its own from a
///         small
///         pool, made as needed; one that fails (a trap, its fuel out) is discarded rather than reused. The function must
///         be
///         pure — the same entries in, the same out, whichever instance runs it — as the browser runs it in a fresh one.
///     </para>
/// </summary>
public sealed class WasmDegradeFunction : IPortableDegradeFunction, IDisposable
{
    /// <summary>The largest module accepted: it goes to every subscriber whose policy names it.</summary>
    public const int MaxModuleBytes = 1 << 20;

    private readonly byte[] _bytes;
    private readonly WasmInstances<Action<int, int, int, int>> _instances;

    private WasmDegradeFunction(byte[] bytes, WasmLimits limits)
    {
        if (bytes.Length > MaxModuleBytes)
            throw new ArgumentException($"The module is {bytes.Length:N0} bytes: at most {MaxModuleBytes:N0}.",
                nameof(bytes));
        _bytes = bytes;
        _instances = new WasmInstances<Action<int, int, int, int>>(bytes, limits, "degrade",
            "a function (i32, i32, i32, i32)", static (instance, name) => instance.GetAction<int, int, int, int>(name),
            "a stale-data function imports nothing (a browser instantiates it as is)");
        ModuleId = Convert.ToHexStringLower(SHA256.HashData(bytes).AsSpan(0, 16));
    }

    public WasmLimits Limits => _instances.Limits;

    /// <summary>Applications that failed (trapped, ran out of fuel, put the entries outside its memory).</summary>
    public int Failures => _instances.Failures;

    public void Dispose()
    {
        _instances.Dispose();
    }

    public ReadOnlyMemory<byte> Module => _bytes;

    public string ModuleId { get; }

    /// <summary>
    ///     Runs the module on <paramref name="entries" />: copies them into its memory where its <c>buffer</c> says, calls
    ///     <c>degrade</c>, copies them back. Throws if it fails — the router then shows the subscriber an empty stale state
    ///     (a table), or none.
    /// </summary>
    public void Degrade(Span<byte> entries, int count, int keySize, int rowSize)
    {
        var runner = _instances.Rent();
        var ok = false;
        try
        {
            var at = runner.Buffer(entries.Length);
            entries.CopyTo(runner.Window(at, entries.Length));
            runner.Call(at, count, keySize, rowSize);
            runner.Window(at, entries.Length).CopyTo(entries);
            ok = true;
        }
        finally
        {
            _instances.Return(runner, ok);
        }
    }

    /// <summary>
    ///     A function from a module's binary. Throws <see cref="ArgumentException" /> if it isn't one: invalid, importing
    ///     anything, or without the exports (memory, buffer, degrade) of their types.
    /// </summary>
    public static WasmDegradeFunction FromBytes(ReadOnlySpan<byte> wasm, WasmLimits? limits = null)
    {
        return new WasmDegradeFunction(wasm.ToArray(), limits ?? WasmLimits.Default);
    }

    /// <summary>A function from a module's text (WAT), compiled to its binary — the form clients get.</summary>
    public static WasmDegradeFunction FromText(string wat, WasmLimits? limits = null)
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

        return new WasmDegradeFunction(bytes, limits ?? WasmLimits.Default);
    }
}