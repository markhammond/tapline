using System.Reflection;
using System.Runtime.InteropServices;
using Wasmtime;
using WasmModule = Wasmtime.Module;

namespace Tapline.Wasm;

/// <summary>What one application of a module's function may use.</summary>
public sealed record WasmLimits
{
    public static WasmLimits Default { get; } = new();

    /// <summary>
    ///     Fuel per application — Wasmtime's, about one unit an instruction (default 50 million: a few tens of milliseconds).
    ///     Run out, the application fails: a fold's update rolls back and its table faults (through an ingest, it folds fewer
    ///     posts first); a stale-data function's subscriber sees an empty stale state (a table), or none (a value, a message).
    /// </summary>
    public long Fuel { get; init; } = 50_000_000;

    /// <summary>The most its linear memory may grow to, in bytes (default 64 MB) — the entries in it, and its own data.</summary>
    public long MemoryBytes { get; init; } = 64L << 20;
}

internal static class WasmEngine
{
    /// <summary>
    ///     Every module's engine. On macOS, Wasmtime would catch its traps through Mach exception ports, registered on each
    ///     thread that runs a module: a thread-pool thread that had run one would then hand .NET's own faults — the hardware
    ///     faults behind a NullReferenceException, caught or not — to Wasmtime, which doesn't know them, and the process dies.
    ///     Unix signals instead: Wasmtime chains to the handler before it (the runtime's) for a fault that isn't its trap.
    /// </summary>
    public static readonly Engine Shared = new(Configure());

    private static Config Configure()
    {
        var config = new Config().WithFuelConsumption(true);
        if (!OperatingSystem.IsMacOS()) return config;
        try
        {
            return config.WithMacosMachPorts(false);
        }
        catch
            (EntryPointNotFoundException) // the binding (48.0.2) calls the C API without its "_set": by its name, then
        {
            var handle =
                (SafeHandle)typeof(Config).GetProperty("NativeHandle", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(config)!;
            wasmtime_config_macos_use_mach_ports_set(handle.DangerousGetHandle(), false);
            return config;
        }
    }

    [DllImport("wasmtime")]
    private static extern void wasmtime_config_macos_use_mach_ports_set(IntPtr config,
        [MarshalAs(UnmanagedType.I1)] bool enable);
}

/// <summary>
///     A module's instances for the one function it's loaded for (a fold, a stale-data function): compiled once and
///     checked
///     when loaded — valid, importing nothing, exporting <c>memory</c>, <c>buffer</c> and the function, of their types —
///     then
///     instantiated as needed, each in a store of its own bounded by the limits, a few kept idle (one per processor). An
///     instance whose application failed (a trap, its fuel out) is discarded rather than reused.
/// </summary>
internal sealed class WasmInstances<TCall> : IDisposable where TCall : Delegate
{
    private readonly Func<Instance, string, TCall?> _bind;
    private readonly string _export, _type;
    private readonly Stack<Runner> _idle = new();
    private readonly Lock _lock = new();
    private readonly int _maxIdle = Math.Clamp(Environment.ProcessorCount, 2, 16);
    private readonly WasmModule _module;
    private bool _disposed;
    private int _failures;

    /// <param name="export">
    ///     The function's export, of type <paramref name="type" /> (for errors), bound by
    ///     <paramref name="bind" />.
    /// </param>
    /// <param name="importsNothing">Why the module may import nothing (for the error).</param>
    public WasmInstances(byte[] bytes, WasmLimits limits, string export, string type,
        Func<Instance, string, TCall?> bind, string importsNothing)
    {
        Limits = limits;
        _export = export;
        _type = type;
        _bind = bind;
        try
        {
            _module = WasmModule.FromBytes(WasmEngine.Shared, export, bytes);
        }
        catch (WasmtimeException e)
        {
            throw new ArgumentException($"Not a valid WebAssembly module: {e.Message}", nameof(bytes), e);
        }

        if (_module.Imports.Count > 0)
        {
            _module.Dispose();
            throw new ArgumentException(
                $"The module imports {string.Join(", ", _module.Imports.Select(i => $"{i.ModuleName}.{i.Name}"))}: {importsNothing}.",
                nameof(bytes));
        }

        try
        {
            _idle.Push(new Runner(this)); // its exports checked now, not at its first application
        }
        catch
        {
            _module.Dispose();
            throw;
        }
    }

    public WasmLimits Limits { get; }

    public int Failures => Volatile.Read(ref _failures);

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            while (_idle.TryPop(out var runner)) runner.Dispose();
        }

        _module.Dispose();
    }

    /// <summary>An idle instance (or a new one), its fuel refilled.</summary>
    public Runner Rent()
    {
        Runner? runner;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _idle.TryPop(out runner);
        }

        runner ??= new Runner(this);
        runner.Store.Fuel = (ulong)Limits.Fuel;
        return runner;
    }

    /// <summary>Kept for the next application if <paramref name="ok" /> (and fewer are idle than kept), else discarded.</summary>
    public void Return(Runner runner, bool ok)
    {
        if (ok)
        {
            lock (_lock)
            {
                if (!_disposed && _idle.Count < _maxIdle)
                {
                    _idle.Push(runner);
                    return;
                }
            }
        }
        else
        {
            Interlocked.Increment(ref _failures);
        }

        runner.Dispose();
    }

    /// <summary>An instance of the module in a store of its own, bounded by the limits.</summary>
    public sealed class Runner : IDisposable
    {
        private readonly Func<int, int> _buffer;
        private readonly Memory _memory;
        public readonly TCall Call;
        public readonly Store Store;

        public Runner(WasmInstances<TCall> of)
        {
            Store = new Store(WasmEngine.Shared);
            try
            {
                Store.SetLimits(of.Limits.MemoryBytes, 10_000, 1, 4, 1);
                Store.Fuel = (ulong)of.Limits.Fuel; // its start function, if it has one, runs on fuel too
                var instance = new Instance(Store, of._module);
                _memory = instance.GetMemory("memory") ?? throw Missing("memory", "a memory");
                _buffer = instance.GetFunction<int, int>("buffer") ??
                          throw Missing("buffer", "a function (i32) -> i32");
                Call = of._bind(instance, of._export) ?? throw Missing(of._export, of._type);
            }
            catch (WasmtimeException e)
            {
                Store.Dispose();
                throw new ArgumentException($"The module can't be instantiated: {e.Message}", "wasm", e);
            }
            catch
            {
                Store.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Store.Dispose();
        }

        private static ArgumentException Missing(string name, string what)
        {
            return new ArgumentException($"The module doesn't export \"{name}\" as {what}.", "wasm");
        }

        /// <summary>Where the module wants <paramref name="size" /> bytes put (its <c>buffer</c>; it grows its memory to fit).</summary>
        public int Buffer(int size)
        {
            return _buffer(size);
        }

        /// <summary>Its memory from <paramref name="at" />; taken again after a call, as its memory may have grown.</summary>
        public Span<byte> Window(int at, int length)
        {
            if (at < 0 || (long)at + length > _memory.GetLength())
                throw new InvalidOperationException(
                    $"The module's buffer ({at}, {length} bytes) is outside its memory ({_memory.GetLength()} bytes).");
            return _memory.GetSpan(at, length);
        }
    }
}