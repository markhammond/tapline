namespace Tapline;

/// <summary>
///     A scheduling class a tap may be bound into (<see cref="Outlet.Tap" />), instead of Streaming: saturated byte shares
///     default to High 16 : Normal 4 : (Streaming 4) : Low 1. High isn't held to the frame cadence, and goes before a busy
///     Streaming class — for state, or messages (an alerts topic), that must not wait behind bulk state.
/// </summary>
public enum Lane : byte
{
    High = 0,
    Normal = 1,
    Low = 2
}