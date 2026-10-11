namespace Tapline;

/// <summary>
///     What a tap sent in frames its client may not have received: per frame, where the tap was after its records in it —
///     its cursor, and its mailbox's last message — so that after a reconnect it resumes from exactly what the client got
///     (<see cref="Outlet.Resume" />). An out-of-band handle is an entry of its own: until fetched, it doesn't count as
///     received. Written by the outlet's frame builder alone (a resume holds the builder off); pruned up to the last frame
///     the client acknowledged, so it holds the frames in flight — four entries to start, more only while that many frames
///     with this tap's records are in flight (the window bounds it).
/// </summary>
internal sealed class Flight(Cursor start)
{
    private const int MaxEntries = 1024;
    private Cursor _base = start; // where the tap was as of the last frame acknowledged
    private long _baseMail;
    private Entry[] _entries = new Entry[4];
    private int _head, _count;

    /// <summary>Whether it ever sent anything (a resume has something to resume).</summary>
    public bool Sent => _count > 0 || Received;

    /// <summary>Whether the client acknowledged anything of it.</summary>
    public bool Received { get; private set; }

    /// <summary>
    ///     A record of the tap's in <paramref name="frame" />, leaving it at <paramref name="after" /> (and its mailbox
    ///     at <paramref name="mail" />).
    /// </summary>
    public void Record(long frame, in Cursor after, long mail, bool handle, long acknowledged)
    {
        Prune(acknowledged, false);
        if (!handle && _count > 0)
        {
            ref var last = ref At(_count - 1);
            if (last.Frame == frame && !last.Handle)
            {
                last.After = after;
                last.Mail = mail;
                return;
            } // the same frame: where it ends
        }

        if (_count == _entries.Length)
        {
            if (_entries.Length < MaxEntries) Grow();
            else Drop(); // (unreachable within a window: the oldest folded into the base)
        }

        At(_count++) = new Entry { Frame = frame, After = after, Mail = mail, Handle = handle };
    }

    /// <summary>
    ///     Where the client is, having received every frame up to <paramref name="lastFrame" /> — a handle it hasn't fetched
    ///     (<paramref name="awaiting" />: the last entry's) not counted; the record restarts from there. <c>Received</c>:
    ///     whether it got anything of the tap at all; <c>All</c>: whether it got all that was sent.
    /// </summary>
    public (Cursor Cursor, long Mail, bool Received, bool All) ResumeAt(long lastFrame, bool awaiting)
    {
        var at = _base;
        var mail = _baseMail;
        var received = Received;
        var got = 0;
        for (; got < _count; got++)
        {
            ref var e = ref At(got);
            if (e.Frame > lastFrame || (e.Handle && awaiting && got == _count - 1)) break;
            at = e.After;
            mail = e.Mail;
            received = true;
        }

        var all = got == _count;
        _base = at;
        _baseMail = mail;
        Received = received;
        _head = _count = 0;
        return (at, mail, received, all);
    }

    /// <summary>
    ///     The topic held for the provider's initial state goes on from <paramref name="cut" />: none of its records went
    ///     before,
    ///     so wherever a resume lands, its cursor is there (its mailbox's stays where the entries say).
    /// </summary>
    public void Restart(in Cursor cut)
    {
        _base.Seq = cut.Seq;
        _base.Pos = cut.Pos;
        for (var i = 0; i < _count; i++) At(i).After = cut;
    }

    /// <summary>A fetch of the handle it awaits (its last entry) timed out: where it was before it — that record goes again.</summary>
    public (Cursor Cursor, long Mail) RewindHandle()
    {
        if (_count > 0 && At(_count - 1).Handle) _count--;
        return _count > 0 ? (At(_count - 1).After, At(_count - 1).Mail) : (_base, _baseMail);
    }

    private void Prune(long acknowledged, bool awaiting)
    {
        while (_count > 0)
        {
            ref var e = ref At(0);
            if (e.Frame > acknowledged || (e.Handle && awaiting && _count == 1)) return;
            Drop();
        }
    }

    private void Drop()
    {
        ref var e = ref At(0);
        _base = e.After;
        _baseMail = e.Mail;
        Received = true;
        _head = (_head + 1) & (_entries.Length - 1);
        _count--;
    }

    private void Grow()
    {
        var bigger = new Entry[_entries.Length * 2];
        for (var i = 0; i < _count; i++) bigger[i] = At(i);
        _entries = bigger;
        _head = 0;
    }

    private ref Entry At(int i)
    {
        return ref _entries[(_head + i) & (_entries.Length - 1)];
    }

    private struct Entry
    {
        public long Frame;
        public Cursor After;
        public long Mail;
        public bool Handle;
    }
}