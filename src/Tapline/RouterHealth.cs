using System.Buffers.Binary;

namespace Tapline;

/// <summary>The router's own state, as every client's heartbeat carries it.</summary>
public enum RouterState : byte
{
    /// <summary>Serving.</summary>
    Serving = 0,

    /// <summary>Under strain — its host's health checks say so, and why: its clients may move to another router, or hold on.</summary>
    Degraded = 1,

    /// <summary>Going away within a grace: its clients move — make before break — to another router before it ends. One way.</summary>
    Draining = 2
}

/// <summary>
///     The router's state, and the change that made it (<see cref="OutletRegistry.SetHealth" />): its correlation id, a
///     UUIDv7 — logged where the change was decided and carried to every client with the state, so a client's log and the
///     router's meet on one id. Ids are this run's alone: never persisted, never compared across runs.
/// </summary>
public sealed class RouterHealth
{
    private readonly long _drainsAt; // TimeProvider timestamp: when the grace ends

    internal RouterHealth(RouterState state, string? reason, DateTimeOffset at, TimeSpan grace, TimeProvider time)
    {
        (State, Reason, At) = (state, reason, at);
        Correlation = Guid.CreateVersion7(at);
        ReasonBytes = state == RouterState.Degraded && reason is not null
            ? RefusedRecord.Utf8(reason, Wire.MaxRouterReason)
            : [];
        if (state == RouterState.Draining)
        {
            DrainsBy = at + grace;
            _drainsAt = time.GetTimestamp() + (long)(grace.TotalSeconds * time.TimestampFrequency);
        }
    }

    public RouterState State { get; }

    /// <summary>The change into this state: its id (a UUIDv7), as logged where it was decided.</summary>
    public Guid Correlation { get; }

    /// <summary>Degraded: why — the host's health checks' account (at most 255 bytes of UTF-8 on the wire).</summary>
    public string? Reason { get; }

    /// <summary>When the change was made.</summary>
    public DateTimeOffset At { get; }

    /// <summary>Draining: when its grace ends — its clients moved by then.</summary>
    public DateTimeOffset? DrainsBy { get; }

    internal byte[] ReasonBytes { get; }

    /// <summary>
    ///     Its RouterChange record (to ride behind a heartbeat): the state, the correlation, the control messages this
    ///     router takes, and the grace left or the reason. Allocation-free.
    /// </summary>
    internal int WriteTo(Span<byte> dst, TimeProvider time)
    {
        dst[0] = Wire.RouterChange;
        dst[1] = (byte)State;
        Correlation.TryWriteBytes(dst.Slice(2, 16), true, out _);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[Wire.RouterChangeControls..], ControlKinds.Supported);
        switch (State)
        {
            case RouterState.Draining:
                var left = time.GetElapsedTime(time.GetTimestamp(), _drainsAt);
                BinaryPrimitives.WriteUInt32LittleEndian(dst[Wire.RouterChangeHeader..],
                    left <= TimeSpan.Zero ? 0 : (uint)Math.Min(uint.MaxValue, left.TotalMilliseconds));
                return Wire.RouterChangeHeader + 4;
            case RouterState.Degraded:
                dst[Wire.RouterChangeHeader] = (byte)ReasonBytes.Length;
                ReasonBytes.CopyTo(dst[(Wire.RouterChangeHeader + 1)..]);
                return Wire.RouterChangeHeader + 1 + ReasonBytes.Length;
            default:
                return Wire.RouterChangeHeader;
        }
    }

    public override string ToString()
    {
        return State switch
        {
            RouterState.Degraded => $"degraded ({Reason}), correlation {Correlation}",
            RouterState.Draining => $"draining (by {DrainsBy:O}), correlation {Correlation}",
            _ => $"serving, correlation {Correlation}"
        };
    }
}