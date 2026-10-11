using System.Collections.Concurrent;
using System.Net;
using Grpc.Core;
using MagicOnion.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Tapline.MagicOnion.Server;

/// <summary>
///     The stream's sessions: an outlet each, held across its calls — a call names its session to claim it again — and for
///     <see cref="OutletOptions.DisconnectedHold" /> after its last call ends, then disposed (its subscriptions end, their
///     providers told). The app's step 1 finds a session's outlet by its id and nonce (<see cref="Find" />). While the
///     router
///     drains, no new session: a call claiming none (or one gone) is refused, <see cref="StatusCode.Unavailable" /> — its
///     client connects to another router; one claiming its own resumes, to move.
/// </summary>
public sealed class TaplineStreamSessions(OutletRegistry outlets, IOptions<TaplineMagicOnionOptions> options)
{
    private readonly ConcurrentDictionary<int, TaplineStreamSession> _sessions = new();

    /// <summary>Sessions now.</summary>
    public int Count => _sessions.Count;

    /// <summary>The outlet of a session, by its id and nonce — null if there's none (it ended, or never was).</summary>
    public Outlet? Find(int outletId, uint nonce)
    {
        return _sessions.TryGetValue(outletId, out var session) && session.Outlet.Nonce == nonce && !session.Ended
            ? session.Outlet
            : null;
    }

    /// <summary>
    ///     The session a call claims (<paramref name="claimed" />: its header) if it's held — else a new one; bound to
    ///     <paramref name="owner" />, the call.
    /// </summary>
    internal TaplineStreamSession Open(string? claimed, HttpContext? http, object owner)
    {
        if (TaplineStreamHeaders.TryParse(claimed, out var id, out var nonce) && _sessions.TryGetValue(id, out var held)
                                                                              && held.Outlet.Nonce == nonce &&
                                                                              held.TryClaim(owner, http))
            return held;
        if (outlets.Health is { State: RouterState.Draining } draining)
            throw new RpcException(new Status(StatusCode.Unavailable,
                $"This router is draining (correlation {draining.Correlation}): connect to another."));
        var session = new TaplineStreamSession(this, new Outlet(outlets, options.Value));
        _sessions[session.Outlet.Id] = session;
        session.TryClaim(owner, http);
        return session;
    }

    /// <summary>
    ///     Its last call ended: held for <see cref="OutletOptions.DisconnectedHold" /> for the next, then gone unless
    ///     claimed.
    /// </summary>
    internal void Hold(TaplineStreamSession session, long generation)
    {
        var hold = options.Value.DisconnectedHold ?? TaplineMagicOnionOptions.DefaultHold;
        _ = Task.Delay(hold).ContinueWith(_ =>
        {
            if (session.TryEnd(generation))
                _sessions.TryRemove(new KeyValuePair<int, TaplineStreamSession>(session.Outlet.Id, session));
        }, TaskScheduler.Default);
    }
}

/// <summary>A session: its outlet, and the call that has it now (none between calls).</summary>
internal sealed class TaplineStreamSession(TaplineStreamSessions sessions, Outlet outlet)
{
    private readonly Lock _gate = new();
    private long _generation; // each claim and release: a hold outlived by a claim does nothing
    private object? _owner;

    public Outlet Outlet { get; } = outlet;
    public bool Ended { get; private set; }

    public bool TryClaim(object owner, HttpContext? http)
    {
        lock (_gate)
        {
            if (Ended) return false;
            (_owner, _generation) = (owner, _generation + 1);
            Outlet.Attach(owner); // its frames from now on this call's (a call before is refused its next)
            var remote = http?.Connection.RemoteIpAddress is { } ip
                ? new IPEndPoint(ip, http.Connection.RemotePort).ToString()
                : null;
            Outlet.Session = new OutletSession("MagicOnion", remote, http?.Request.Headers.UserAgent.ToString(),
                DateTimeOffset.UtcNow, (Outlet.Session?.Connections ?? 0) + 1);
            Outlet.IdentifyFrom(http?.User); // who it is, if the host says (OutletOptions.Identify)
            return true;
        }
    }

    public bool Owns(object owner)
    {
        return ReferenceEquals(Volatile.Read(ref _owner), owner);
    }

    /// <summary>A call ended: if it still had the session, the session's held for the next.</summary>
    public void Release(object owner)
    {
        long generation;
        lock (_gate)
        {
            if (!ReferenceEquals(_owner, owner)) return; // another call has it now
            (_owner, generation) = (null, ++_generation);
            Outlet.Detach();
            if (Outlet.Session is { } session) Outlet.Session = session with { DisconnectedAt = DateTimeOffset.UtcNow };
        }

        sessions.Hold(this, generation);
    }

    /// <summary>Ends it, if nothing has claimed it since its release: its outlet disposed.</summary>
    public bool TryEnd(long generation)
    {
        lock (_gate)
        {
            if (Ended || _owner is not null || _generation != generation) return false;
            Ended = true;
        }

        Outlet.Dispose();
        return true;
    }

    /// <summary>A control message, applied at once on the reading thread (<see cref="Outlet.Apply" />): a kind it doesn't know, ignored.</summary>
    public void Apply(in Control c)
    {
        Outlet.Apply(new ControlMessage(c.Kind, c.A, c.B, c.Payload));
    }

    /// <summary>
    ///     The call's frames: each built just before it's written, the write itself awaited — so at most one frame waits
    ///     above the transport's own buffer, and the outlet's window, not a queue, holds the backlog. Off the call's context.
    /// </summary>
    public async Task PumpAsync(DuplexStreamingContext<Control, Frame> stream, object owner, CancellationToken token)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        using var interrupt = token.UnsafeRegister(static o => ((Outlet)o!).Interrupt(), Outlet);
        var buffer = new byte[16 * 1024];
        try
        {
            while (!token.IsCancellationRequested && Owns(owner))
            {
                if (!Outlet.IsReady)
                {
                    await Outlet.WaitAsync().ConfigureAwait(false);
                    continue;
                }

                if (!Outlet.TryBuildFrame(owner, false, out var frame, out var seq))
                {
                    await Task.Yield(); // (nothing after all: look again)
                    continue;
                }

                var length = frame.Length;
                try
                {
                    if (buffer.Length < length) buffer = new byte[Math.Max(length, buffer.Length * 2)];
                    frame.CopyTo(buffer);
                }
                finally
                {
                    Outlet.ReleaseFrame(); // copied: its buffer may go back
                }

                await stream.WriteAsync(new Frame { Seq = seq, Payload = buffer.AsMemory(0, length) })
                    .ConfigureAwait(false);
            }
        }
        catch (Exception) when (token.IsCancellationRequested || !Owns(owner))
        {
        } // the call ended, or another has the session
    }
}