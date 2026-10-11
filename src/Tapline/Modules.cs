using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tapline;

/// <summary>
///     The modules of the portable stale-data functions (<see cref="IPortableDegradeFunction" />) that the subscriptions
///     bound
///     in an <see cref="OutletRegistry" /> use, by id — for their clients to fetch (Tapline.AspNetCore serves them at
///     <c>{prefix}/module/{id}</c>, immutable). Each is held while some subscription bound with it is: a client fetches
///     its
///     subscription's at once, while the router is reachable, so that it has it when it isn't.
/// </summary>
public sealed class ModuleRegistry
{
    private readonly Lock _lock = new();

    private readonly Dictionary<string, (ReadOnlyMemory<byte> Module, int Holds)>
        _modules = new(StringComparer.Ordinal);

    /// <summary>Modules held.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _modules.Count;
            }
        }
    }

    /// <summary>The module of that id, if a subscription bound holds it.</summary>
    public bool TryGet(string id, out ReadOnlyMemory<byte> module)
    {
        lock (_lock)
        {
            var found = _modules.TryGetValue(id, out var entry);
            module = entry.Module;
            return found;
        }
    }

    internal void Hold(IPortableDegradeFunction function)
    {
        lock (_lock)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_modules, function.ModuleId, out _);
            entry.Module = function.Module;
            entry.Holds++;
        }
    }

    internal void Release(IPortableDegradeFunction function)
    {
        lock (_lock)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_modules, function.ModuleId);
            if (!Unsafe.IsNullRef(ref entry) && --entry.Holds == 0) _modules.Remove(function.ModuleId);
        }
    }
}