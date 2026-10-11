using System.Diagnostics;
using Grpc.Core;
using MagicOnion.Client;
using Tapline.Client;

namespace Tapline.MagicOnion;

/// <summary>
///     A <see cref="TaplineStreamConnection" />'s client, cap, and how it notices and recovers from a dead
///     connection.
/// </summary>
public sealed class TaplineStreamConnectionOptions
{
    /// <summary>Its client's: where records sent out of band are fetched (best on a connection apart from the stream's).</summary>
    public TaplineClientOptions Client { get; set; } = new();

    /// <summary>
    ///     A cap on this session's Streaming, written as for rsync's <c>--bwlimit</c> (<c>64</c>: KiB/s; <c>500kb</c>).
    ///     Null: the router's.
    /// </summary>
    public string? BandwidthLimit { get; set; }

    /// <summary>
    ///     No frame for this long — the router sends a heartbeat every second — and the connection is deemed dead:
    ///     dropped, and made again.
    /// </summary>
    public TimeSpan Silence { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The wait before connecting again, doubling after each failure up to <see cref="MaxRetryDelay" />.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
///     Tapline.Client over MagicOnion: a duplex call to the router's <see cref="ITaplineStream" /> per connection — frames
///     into
///     <see cref="Client" />, what it says written back, in order, one write at a time, an acknowledgement coalesced with
///     any
///     newer one waiting (each is cumulative). The session outlives a call: the next names it, and the client resumes from
///     the
///     last frame it received; a session the router no longer holds is a new one (the client's subscriptions answered as
///     gone). Run it with <see cref="RunAsync" />; subscribe on <see cref="Connected" /> — step 1 names its session.
/// </summary>
public sealed class TaplineStreamConnection : ITaplineConnection
{
    private readonly Queue<Control> _commands = new();
    private readonly Lock _gate = new();
    private readonly TaplineStreamConnectionOptions _options;
    private readonly ITaplineStream _service;
    private readonly SemaphoreSlim _waiting = new(0);
    private Control? _ack;
    private CancellationTokenSource? _call;
    private long _heardAt;
    private (int OutletId, uint Nonce)? _session;
    private bool _bound; // (the current call's session named: until the call ends)

    public TaplineStreamConnection(ChannelBase channel, TaplineStreamConnectionOptions? options = null)
    {
        _options = options ?? new TaplineStreamConnectionOptions();
        _service = MagicOnionClient.Create<ITaplineStream>(channel);
        Client = new TaplineClient(this, _options.Client);
    }

    /// <summary>The client this connection carries.</summary>
    public TaplineClient Client { get; }

    /// <summary>The session the current (or last) call is bound to: its outlet's id and nonce — what the app's step 1 names.</summary>
    public (int OutletId, uint Nonce)? Session
    {
        get
        {
            lock (_gate)
            {
                return _session;
            }
        }
    }

    /// <summary>
    ///     The session the current call is bound to, while one is: none between calls. The last one may be gone at the
    ///     router (its process started again, say): the next call finds out, and binds another.
    /// </summary>
    public (int OutletId, uint Nonce)? Bound
    {
        get
        {
            lock (_gate)
            {
                return _bound ? _session : null;
            }
        }
    }

    /// <summary>
    ///     A control message, as its kind's number with its arguments: every kind, one this connection doesn't know too. A
    ///     payload is copied (the caller may reuse it); an acknowledgement only the newest waiting.
    /// </summary>
    void ITaplineConnection.Send(in ControlMessage message)
    {
        var control = new Control
        {
            Kind = message.Kind, A = message.A, B = message.B,
            Payload = message.Payload.IsEmpty ? null : message.Payload.ToArray()
        };
        if (message.Kind != ControlKind.Ack)
        {
            Send(control);
            return;
        }

        lock (_gate)
        {
            _ack = control; // the newest: an older one waiting is said by it
        }

        _waiting.Release();
    }

    /// <summary>
    ///     A call up and its session bound (the client resumed): the session's outlet id and nonce. A new session (the
    ///     old one gone) is a new pair.
    /// </summary>
    public event Action<int, uint>? Connected;

    /// <summary>
    ///     Drops the current call, as a network might: the next resumes its session (for tests, and a client that knows
    ///     its path changed).
    /// </summary>
    public void Drop()
    {
        lock (_gate)
        {
            _call?.Cancel();
        }
    }

    /// <summary>
    ///     Connects, carries frames and control messages until the call ends, and connects again (after
    ///     <see cref="TaplineStreamConnectionOptions.RetryDelay" />, doubling) — until <paramref name="cancellationToken" />.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var delay = _options.RetryDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            using var call = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_gate)
            {
                _call = call;
            }

            var bound = false;
            try
            {
                bound = await StreamAsync(call).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
            } // dropped, refused, timed out: again
            finally
            {
                lock (_gate)
                {
                    (_call, _bound) = (null, false);
                }

                call.Cancel();
                Client.Disconnected();
            }

            if (bound) delay = _options.RetryDelay;
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!bound) delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.MaxRetryDelay.Ticks));
        }
    }

    /// <summary>One call: opened, its session bound, frames received until it ends. True once bound.</summary>
    private async Task<bool> StreamAsync(CancellationTokenSource call)
    {
        var headers = new Metadata();
        lock (_gate)
        {
            if (_session is { } session)
                headers.Add(TaplineStreamHeaders.Session, TaplineStreamHeaders.Format(session.OutletId, session.Nonce));
        }

        if (_options.BandwidthLimit is { Length: > 0 } limit) headers.Add(TaplineStreamHeaders.BandwidthLimit, limit);
        using var duplex = await _service.WithHeaders(headers).WithCancellationToken(call.Token).OpenAsync()
            .ConfigureAwait(false);
        var answer = await duplex.ResponseHeadersAsync.ConfigureAwait(false);
        if (!TaplineStreamHeaders.TryParse(answer.GetValue(TaplineStreamHeaders.Session), out var outletId,
                out var nonce))
            throw new InvalidOperationException("The router named no session.");
        lock (_gate)
        {
            _commands.Clear(); // (what a call before couldn't send: the client says it again as it binds)
            (_ack, _session, _bound) = (null, (outletId, nonce), true);
        }

        Volatile.Write(ref _heardAt, Stopwatch.GetTimestamp());
        var writing = WriteAsync(duplex.RequestStream, call.Token);
        var watching = WatchAsync(call);
        Client.Bind(outletId, nonce);
        Connected?.Invoke(outletId, nonce);
        var frames = duplex.ResponseStream;
        try
        {
            while (await frames.MoveNext(call.Token).ConfigureAwait(false))
            {
                Volatile.Write(ref _heardAt, Stopwatch.GetTimestamp());
                var frame = frames.Current;
                Client.Receive(frame.Seq, frame.Payload.Span);
            }
        }
        finally
        {
            call.Cancel();
            await Task.WhenAll(writing, watching).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        return true;
    }

    /// <summary>Writes what the client says, in order, one write at a time: its commands, then the newest acknowledgement.</summary>
    private async Task WriteAsync(IClientStreamWriter<Control> writer, CancellationToken token)
    {
        try
        {
            while (true)
            {
                await _waiting.WaitAsync(token).ConfigureAwait(false);
                while (TryTake(out var control)) await writer.WriteAsync(control).ConfigureAwait(false);
            }
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
        }
        catch (RpcException)
        {
        } // the call ended under it
    }

    private bool TryTake(out Control control)
    {
        lock (_gate)
        {
            if (_commands.TryDequeue(out control)) return true;
            if (_ack is { } ack)
            {
                (control, _ack) = (ack, null);
                return true;
            }

            return false;
        }
    }

    /// <summary>
    ///     Drops a call that has gone quiet (no frame for <see cref="TaplineStreamConnectionOptions.Silence" />): a
    ///     half-open connection never ends on its own.
    /// </summary>
    private async Task WatchAsync(CancellationTokenSource call)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), call.Token).ConfigureAwait(false);
                if (Stopwatch.GetElapsedTime(Volatile.Read(ref _heardAt)) > _options.Silence)
                {
                    call.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Send(Control control)
    {
        lock (_gate)
        {
            _commands.Enqueue(control);
        }

        _waiting.Release();
    }
}