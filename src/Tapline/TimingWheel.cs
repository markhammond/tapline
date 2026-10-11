using System.Numerics;

namespace Tapline;

/// <summary>
///     Something a <see cref="TimingWheel" /> wakes. Its callback runs on the wheel's driver: keep it O(1) (a wake, a
///     signal).
/// </summary>
internal interface IWheelTarget
{
    void OnWheel();
}

/// <summary>A target's place on the wheel: one per target, so rescheduling moves it (never duplicates it).</summary>
internal sealed class WheelNode(IWheelTarget target)
{
    private static int s_next;

    public readonly int
        Spread = Interlocked.Increment(ref s_next); // which stripe it belongs to (nodes dealt round-robin)

    public readonly IWheelTarget Target = target;
    public long DueTick, LinkedDue;
    public WheelNode? NextRequest;

    public int Posted; // in its stripe's requests, not yet taken by the driver

    // The driver's alone:
    public WheelNode? Prev, Next;
    public long Requested; // the due time asked for (0: none) — its schedulers' (atomically)
    public int Slot = -1;
}

/// <summary>
///     Hashed timing wheel for wake-ups, not pacing: an outlet that parks with something held back by time — a frame not
///     yet due, pacing, its Streaming budget in debt, a retry — is woken then. O(1) schedule and cancel, zero allocation,
///     never fires early (due times round up to the next tick; entries more than a revolution out wait for their round).
///     One per <see cref="OutletRegistry" />.
///     <para>
///         Schedulers never lock or wait: a schedule (or a cancel) records the time asked for and posts the node — once,
///         till
///         taken — to its stripe's requests, a lock-free stack; so a scheduler preempted mid-way holds up nobody. The
///         wheel itself
///         is the driver's alone: one thread (its own background thread, only while something is scheduled; or
///         <see cref="Advance(long)" /> — tests, custom hosts; one driver at a time) takes each stripe's requests, links
///         or moves
///         their nodes, then fires what is due. Striped — several wheels, a power of two near the processor count, a node
///         in one
///         — so posts to different stripes don't touch the same cache line. Incremental: a pass fires at most
///         <c>maxPerPass</c> of
///         a stripe's due entries before turning to the next stripe, and comes back for the rest at once — a burst on one
///         stripe
///         delays the others' wake-ups by a pass, not by the burst.
///     </para>
/// </summary>
public sealed class TimingWheel : IDisposable
{
    private readonly bool _autoStart;
    private readonly Lock _driverLock = new();
    private readonly ManualResetEventSlim _signal = new();
    private readonly int _stripeMask, _mask, _maxPerPass;

    private readonly Stripe[] _stripes;
    private readonly long _tickTicks;
    private int _count, _nextStripe;
    private volatile bool _disposed;
    private IWheelTarget?[] _fired;
    private Thread? _thread;

    /// <param name="tick">
    ///     Resolution (default 1 ms: fine enough for a steady 60 Hz frame cadence; Windows sleeps in ~15.6 ms
    ///     steps unless its timer resolution is raised).
    /// </param>
    /// <param name="slots">Slots per revolution, rounded up to a power of two (default 512: a 0.512 s revolution at 1 ms).</param>
    /// <param name="time">Clock (default <see cref="TimeProvider.System" />).</param>
    /// <param name="autoStart">
    ///     Start the background thread on the first schedule; false to drive it with
    ///     <see cref="Advance(long)" />.
    /// </param>
    /// <param name="stripes">
    ///     Stripes, rounded up to a power of two (default: the processor count's, at most 64; 1: a single
    ///     wheel).
    /// </param>
    /// <param name="maxPerPass">Due entries a pass fires per stripe before turning to the next (default 256).</param>
    public TimingWheel(TimeSpan? tick = null, int slots = 512, TimeProvider? time = null, bool autoStart = true,
        int stripes = 0, int maxPerPass = 256)
    {
        Tick = tick ?? TimeSpan.FromMilliseconds(1);
        if (Tick <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(tick));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPerPass, 1);
        TimeProvider = time ?? TimeProvider.System;
        var perStripe = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(slots, 2));
        var count = (int)BitOperations.RoundUpToPowerOf2(
            (uint)(stripes > 0 ? stripes : Math.Min(Environment.ProcessorCount, 64)));
        _mask = perStripe - 1;
        _tickTicks = Math.Max(1, (long)(Tick.TotalSeconds * TimeProvider.TimestampFrequency));
        var current = TimeProvider.GetTimestamp() / _tickTicks;
        _stripes = new Stripe[count];
        for (var i = 0; i < count; i++) _stripes[i] = new Stripe(perStripe) { Current = current };
        _stripeMask = count - 1;
        _maxPerPass = maxPerPass;
        _fired = new IWheelTarget?[Math.Min(maxPerPass, 16)];
        _autoStart = autoStart;
    }

    public TimeSpan Tick { get; }
    public TimeProvider TimeProvider { get; }

    /// <summary>Its stripes (independent wheels).</summary>
    public int Stripes => _stripes.Length;

    /// <summary>Entries scheduled: asked for and not yet fired or cancelled.</summary>
    public int Count => Volatile.Read(ref _count);

    public void Dispose()
    {
        _disposed = true;
        _signal.Set();
    }

    /// <summary>
    ///     Wakes <paramref name="node" />'s target at or after <paramref name="due" /> (a timestamp), replacing any earlier
    ///     schedule. Lock-free: the request is posted for the driver, which places it before firing anything.
    /// </summary>
    internal void Schedule(WheelNode node, long due)
    {
        if (_disposed) return;
        due = Math.Max(1, due);
        if (Interlocked.Exchange(ref node.Requested, due) == 0 && Interlocked.Increment(ref _count) == 1 &&
            _autoStart) Kick();
        Post(node);
    }

    internal void Cancel(WheelNode node)
    {
        if (Interlocked.Exchange(ref node.Requested, 0) == 0) return;
        Interlocked.Decrement(ref _count);
        Post(node); // for the driver to unlink it
    }

    /// <summary>Onto its stripe's requests — once, till the driver takes it (later requests for it change only what it finds).</summary>
    private void Post(WheelNode node)
    {
        if (Interlocked.Exchange(ref node.Posted, 1) != 0) return;
        var stripe = _stripes[node.Spread & _stripeMask];
        WheelNode? head;
        do
        {
            head = Volatile.Read(ref stripe.Requests);
            node.NextRequest = head;
        } while (Interlocked.CompareExchange(ref stripe.Requests, node, head) != head);
    }

    /// <summary>Fires everything due by now. Returns how many fired.</summary>
    public int Advance()
    {
        return Advance(TimeProvider.GetTimestamp());
    }

    /// <summary>
    ///     Takes the requests posted, then fires everything due by <paramref name="now" /> (a timestamp), in passes over the
    ///     stripes — at most <c>maxPerPass</c> per stripe a pass. Returns how many fired.
    /// </summary>
    public int Advance(long now)
    {
        var target = now / _tickTicks;
        var total = 0;
        lock (_driverLock) // one driver at a time: the wheel is its alone
        {
            for (var more = true; more;)
            {
                more = false;
                var start = _nextStripe++; // a burst doesn't always favour the same stripes
                for (var i = 0; i < _stripes.Length; i++)
                {
                    var stripe = _stripes[(start + i) & _stripeMask];
                    Take(stripe);
                    var fired = Collect(stripe, target, out var left);
                    more |= left;
                    total += fired;
                    for (var k = 0; k < fired; k++)
                    {
                        var t = _fired[k]!;
                        _fired[k] = null;
                        t.OnWheel();
                    }
                }
            }
        }

        return total;
    }

    /// <summary>
    ///     A stripe's posted requests, placed: each node linked at (or moved to) the time asked for now, or unlinked if
    ///     cancelled.
    /// </summary>
    private void Take(Stripe stripe)
    {
        for (var node = Interlocked.Exchange(ref stripe.Requests, null); node is not null;)
        {
            var next = node.NextRequest;
            node.NextRequest = null;
            Interlocked.Exchange(ref node.Posted, 0); // full fence: a request after this posts it again
            var due = Volatile.Read(ref node.Requested);
            if (due == 0)
            {
                if (node.Slot >= 0) Unlink(stripe, node);
            }
            else
            {
                var tick = Math.Max(stripe.Current + 1, due / _tickTicks + (due % _tickTicks == 0 ? 0 : 1));
                if (node.Slot < 0 || node.DueTick != tick)
                {
                    if (node.Slot >= 0) Unlink(stripe, node);
                    node.DueTick = tick;
                    Link(stripe, node, (int)(tick & _mask));
                }

                node.LinkedDue = due;
            }

            node = next;
        }
    }

    /// <summary>
    ///     A stripe's due entries, up to the pass's limit, unlinked — each fired only if what it was linked for is still what
    ///     is asked (a schedule since replaces it; its request, posted, places it again). <paramref name="left" />: some due
    ///     were left (its current tick kept before the one it stopped in, so the next pass resumes there).
    /// </summary>
    private int Collect(Stripe stripe, long target, out bool left)
    {
        left = false;
        var fired = 0;
        if (target <= stripe.Current) return 0;
        long first = stripe.Current + 1,
            last = stripe.Current + Math.Min(target - stripe.Current, stripe.Slots.Length); // at most one revolution
        for (var t = first; t <= last; t++)
        for (var node = stripe.Slots[t & _mask]; node is not null;)
        {
            var next = node.Next;
            if (node.DueTick <= target)
            {
                if (fired == _maxPerPass)
                {
                    stripe.Current = t - 1; // this tick unfinished: the next pass starts in it
                    left = true;
                    return fired;
                }

                Unlink(stripe, node);
                var linked = node.LinkedDue;
                if (Interlocked.CompareExchange(ref node.Requested, 0, linked) == linked) // not replaced meanwhile: due
                {
                    Interlocked.Decrement(ref _count);
                    if (fired == _fired.Length) Array.Resize(ref _fired, Math.Min(fired * 2, _maxPerPass));
                    _fired[fired++] = node.Target;
                }
            }

            node = next;
        }

        stripe.Current = target;
        return fired;
    }

    private static void Link(Stripe stripe, WheelNode node, int slot)
    {
        node.Slot = slot;
        node.Prev = null;
        node.Next = stripe.Slots[slot];
        if (node.Next is not null) node.Next.Prev = node;
        stripe.Slots[slot] = node;
    }

    private static void Unlink(Stripe stripe, WheelNode node)
    {
        if (node.Prev is not null) node.Prev.Next = node.Next;
        else stripe.Slots[node.Slot] = node.Next;
        if (node.Next is not null) node.Next.Prev = node.Prev;
        node.Prev = node.Next = null;
        node.Slot = -1;
    }

    private void Kick()
    {
        if (Volatile.Read(ref _thread) is null)
        {
            var thread = new Thread(Run) { IsBackground = true, Name = "Tapline timing wheel" };
            if (Interlocked.CompareExchange(ref _thread, thread, null) is null) thread.Start();
        }

        _signal.Set();
    }

    private void Run()
    {
        while (!_disposed)
        {
            if (Volatile.Read(ref _count) == 0)
            {
                _signal.Wait();
                _signal.Reset();
                continue;
            }

            Thread.Sleep(Tick);
            Advance(TimeProvider.GetTimestamp());
        }
    }

    private sealed class Stripe(int slots)
    {
        public readonly WheelNode?[] Slots = new WheelNode?[slots];
        public long Current; // the last tick it has advanced through (the driver's)
        public WheelNode? Requests; // posted, not yet taken (a lock-free stack)
    }
}