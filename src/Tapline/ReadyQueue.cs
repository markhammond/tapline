namespace Tapline;

/// <summary>A tap's links in its outlet's <see cref="ReadyQueue" /> (the stub has no tap).</summary>
internal sealed class ReadyNode(Tap? tap)
{
    public readonly Tap? Tap = tap;
    public ReadyNode? InNext; // inbox (MPSC) link
    public ReadyNode? Prev, Next; // consumer FIFO links
}

/// <summary>
///     An outlet's taps with something to send, oldest-first by when they became dirty — a coalescing queue: a tap enters
///     once on its clean→dirty transition (guarded by <see cref="Tap.Queued" />), keeps its place while further changes
///     coalesce into it, and re-enters at the back after it is served. Publishers (any thread) push onto a wait-free
///     intrusive MPSC inbox (Vyukov); the frame builder — the single consumer — drains it into a private doubly linked
///     FIFO that it can scan, skip (a record that doesn't fit keeps its place) and unlink from freely. O(1) per
///     transition; frame building is O(dirty taps), not O(taps). Zero allocation.
/// </summary>
internal sealed class ReadyQueue
{
    private readonly ReadyNode _stub = new(null);
    private int _count;
    private ReadyNode _inHead, _inTail;

    public ReadyQueue()
    {
        _inHead = _inTail = _stub;
    }

    /// <summary>Taps in the consumer's FIFO (readable from any thread).</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>The inbox holds a linked (poppable) node. False while the only push is still mid-flight; its Wake follows.</summary>
    public bool HasInbound => Volatile.Read(ref Volatile.Read(ref _inHead).InNext) is not null;

    public ReadyNode? First { get; private set; }

    public ReadyNode? Last { get; private set; }

    /// <summary>
    ///     Producer side, any thread. Wait-free. The link is a full fence, so a caller's following read (the outlet's
    ///     <c>_parked</c> in Wake) can't be reordered before it — otherwise producer and parking consumer could each miss
    ///     the other (store→load reordering, which x86 permits).
    /// </summary>
    public void Push(ReadyNode node)
    {
        Volatile.Write(ref node.InNext, null);
        var prev = Interlocked.Exchange(ref _inTail, node);
        Interlocked.Exchange(ref prev.InNext, node);
    }

    /// <summary>Consumer side: moves the inbox, in arrival order, to the back of the FIFO.</summary>
    public void Drain()
    {
        for (ReadyNode? n; (n = Pop()) is not null;) Append(n);
    }

    private ReadyNode? Pop()
    {
        var head = _inHead;
        var next = Volatile.Read(ref head.InNext);
        if (head == _stub)
        {
            if (next is null) return null;
            Volatile.Write(ref _inHead, next);
            head = next;
            next = Volatile.Read(ref next.InNext);
        }

        if (next is not null)
        {
            Volatile.Write(ref _inHead, next);
            return head;
        }

        if (head != Volatile.Read(ref _inTail)) return null; // a push is linking after head
        Push(_stub);
        next = Volatile.Read(ref head.InNext);
        if (next is null) return null;
        Volatile.Write(ref _inHead, next);
        return head;
    }

    public void Append(ReadyNode node)
    {
        node.Prev = Last;
        node.Next = null;
        if (Last is null) First = node;
        else Last.Next = node;
        Last = node;
        Volatile.Write(ref _count, _count + 1);
    }

    public void Remove(ReadyNode node)
    {
        if (node.Prev is null) First = node.Next;
        else node.Prev.Next = node.Next;
        if (node.Next is null) Last = node.Prev;
        else node.Next.Prev = node.Prev;
        node.Prev = node.Next = null;
        Volatile.Write(ref _count, _count - 1);
    }
}