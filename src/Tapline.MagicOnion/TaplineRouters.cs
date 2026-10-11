using Grpc.Core;
using Tapline.Client;

namespace Tapline.MagicOnion;

/// <summary>
///     A router a client may stream from: its channel (the stream, and the app's step 1), and where it fetches
///     records sent out of band.
/// </summary>
/// <param name="Name">
///     Its name, as a renewal's redirect names it (<see cref="TaplineClient.RenewalRedirected" />): only a router named here
///     is moved to.
/// </param>
public sealed record TaplineRouter(ChannelBase Channel, Uri? BlobBase = null, string? Name = null);

/// <summary>
///     A move from one router to another (<see cref="TaplineRouters.Move" />): the connection streaming until now, the one
///     bound on the next router — its session the app's step 1 names, over <see cref="Router" />'s channel — and why.
///     <see cref="Cancellation" /> ends when the move must be over: the old router's grace, or the router set stopping.
/// </summary>
public sealed record RouterMove(
    TaplineStreamConnection From,
    TaplineStreamConnection To,
    TaplineRouter Router,
    string Why,
    CancellationToken Cancellation);

/// <summary>
///     A client's routers, over MagicOnion: it streams from one (<see cref="Current" />) and moves to another — make
///     before
///     break — when that one drains (its heartbeats say so, with a grace): it connects to the next router, binds a session
///     there, and hands the app both (<see cref="Move" />). The app subscribes again there — its step 1 on the new
///     router's
///     channel, naming the new session — and returns once the new subscriptions stream; then the old call ends, its
///     session
///     let go (the draining router stops waiting for it). A router out of reach for <see cref="FailoverAfter" /> is moved
///     off
///     too — break before make: what it held is gone already. With one router, nothing to move to: it waits. Each
///     connection
///     is handed to <see cref="Starting" /> before it connects, for the app's handlers on its client.
/// </summary>
public sealed class TaplineRouters
{
    private readonly Lock _gate = new();
    private readonly TaplineStreamConnectionOptions _options;
    private readonly TaplineRouter[] _routers;
    private Running? _current;
    private int _moving;
    private long _unreachableSince; // Environment.TickCount64 (0: reachable, or not yet asked)

    public TaplineRouters(IEnumerable<TaplineRouter> routers, TaplineStreamConnectionOptions? options = null)
    {
        _routers = [.. routers];
        if (_routers.Length == 0) throw new ArgumentException("A router, at least.", nameof(routers));
        _options = options ?? new TaplineStreamConnectionOptions();
    }

    /// <summary>The connection streaming now (null before <see cref="RunAsync" />): its client holds the subscriptions.</summary>
    public TaplineStreamConnection Current
    {
        get
        {
            lock (_gate)
            {
                return _current?.Connection!;
            }
        }
    }

    /// <summary>The router streamed from now: its channel is the one the app's step 1 goes over.</summary>
    public TaplineRouter Router
    {
        get
        {
            lock (_gate)
            {
                return _routers[_current?.Index ?? 0];
            }
        }
    }

    /// <summary>
    ///     The app's move: subscribe again on <see cref="RouterMove.To" />, returning once those stream (the old ones
    ///     then go, with their router).
    /// </summary>
    public Func<RouterMove, Task>? Move { get; set; }

    /// <summary>How long the current router may be out of reach before moving off it (break before make). 10 s by default.</summary>
    public TimeSpan FailoverAfter { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     How long a move may take — the next router binding a session, and the app subscribing there — at most (the old
    ///     router's grace may cut it shorter). 10 s.
    /// </summary>
    public TimeSpan MoveTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     Each connection as it's made — the first, and each moved to — before it connects: for the app's handlers on
    ///     its client.
    /// </summary>
    public event Action<TaplineStreamConnection>? Starting;

    /// <summary>
    ///     A move done: the router streamed from now (<see cref="Current" />), and the move. Raised after the old call
    ///     ended.
    /// </summary>
    public event Action<RouterMove>? Moved;

    /// <summary>
    ///     A move given up — no other router bound a session in time, or the app's move failed — and why: it stays (or,
    ///     out of reach, tries again).
    /// </summary>
    public event Action<string>? MoveFailed;

    /// <summary>Streams from the first router, moving as its routers say, until <paramref name="cancellationToken" />.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var first = Start(0, cancellationToken);
        lock (_gate)
        {
            _current = first;
        }

        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                var current = Current;
                if (current.Client.Reachable)
                {
                    Volatile.Write(ref _unreachableSince, 0);
                    continue;
                }

                long since = Volatile.Read(ref _unreachableSince), now = Environment.TickCount64;
                if (since == 0)
                {
                    Volatile.Write(ref _unreachableSince, now);
                }
                else if (now - since >= FailoverAfter.TotalMilliseconds && _routers.Length > 1)
                {
                    Volatile.Write(ref _unreachableSince, 0);
                    await MoveAsync("out of reach", null, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Running last;
            lock (_gate)
            {
                last = _current!;
            }

            last.Stop.Cancel();
            await last.Run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>A connection to router <paramref name="index" />, handed to the app (<see cref="Starting" />), then run.</summary>
    private Running Start(int index, CancellationToken cancellationToken)
    {
        var router = _routers[index];
        var connection = new TaplineStreamConnection(router.Channel, new TaplineStreamConnectionOptions
        {
            Client = WithBlobBase(_options.Client, router.BlobBase),
            BandwidthLimit = _options.BandwidthLimit, Silence = _options.Silence, RetryDelay = _options.RetryDelay,
            MaxRetryDelay = _options.MaxRetryDelay
        });
        connection.Client.RenewalRedirected += (name, ticket) =>
        {
            var target = Array.FindIndex(_routers, r => string.Equals(r.Name, name, StringComparison.Ordinal));
            if (target < 0 || target == index) MoveFailed?.Invoke($"a renewal redirected to '{name}': not a router known here");
            else _ = Task.Run(() => ReferenceEquals(Current, connection)
                ? MoveAsync($"renewal redirected to {name}", null, cancellationToken, target, ticket)
                : Task.CompletedTask);
        };
        connection.Client.RouterStatusChanged += status =>
        {
            if (status.State == RouterState.Draining &&
                _routers.Length > 1) // (raised under its client's lock: the move goes off it)
                _ = Task.Run(() =>
                    ReferenceEquals(Current, connection)
                        ? MoveAsync($"draining (correlation {status.Correlation})", status.DrainsBy, cancellationToken)
                        : Task.CompletedTask);
        };
        Starting?.Invoke(connection);
        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        return new Running(connection, stop, connection.RunAsync(stop.Token), index);
    }

    /// <summary>
    ///     Moves to the next router that binds a session in time: the app subscribes again there, then the old call ends. One
    ///     move at a time; given up, it stays.
    /// </summary>
    private async Task MoveAsync(string why, DateTimeOffset? by, CancellationToken cancellationToken,
        int? only = null, ReadOnlyMemory<byte> ticket = default)
    {
        if (Interlocked.Exchange(ref _moving, 1) != 0) return;
        RouterStatus? onto = null; // moved onto a router draining too (its notice came meanwhile): on again
        try
        {
            Running from;
            lock (_gate)
            {
                from = _current!;
            }

            var timeout = MoveTimeout;
            if (by is { } deadline && deadline - DateTimeOffset.UtcNow is var left && left < timeout)
                timeout = left > TimeSpan.Zero ? left : TimeSpan.Zero;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            for (var k = 1; k < _routers.Length && !limit.IsCancellationRequested; k++)
            {
                var index = (from.Index + k) % _routers.Length;
                if (only is { } target && index != target) continue;
                var to = Start(index, cancellationToken);
                try
                {
                    await BoundAsync(to.Connection, limit.Token).ConfigureAwait(false);
                    await SignedInAsync(from.Connection.Client, to.Connection.Client, ticket, limit.Token)
                        .ConfigureAwait(false);
                    var move = new RouterMove(from.Connection, to.Connection, _routers[index], why, limit.Token);
                    if (Move is { } subscribe) await subscribe(move).WaitAsync(limit.Token).ConfigureAwait(false);
                    lock (_gate)
                    {
                        _current = to;
                    }

                    from.Stop.Cancel(); // the old call ends: its session let go
                    await from.Run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    Moved?.Invoke(move);
                    if (to.Connection.Client.Router is { State: RouterState.Draining } status) onto = status;
                    break;
                }
                catch (Exception e) when (!cancellationToken.IsCancellationRequested)
                {
                    to.Stop.Cancel();
                    await to.Run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    MoveFailed?.Invoke(
                        $"router {index}: {(e is OperationCanceledException ? "no session in time" : e.Message)}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            Volatile.Write(ref _moving, 0);
        }

        if (onto is { } draining)
            await MoveAsync($"draining (correlation {draining.Correlation})", draining.DrainsBy, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    ///     The identity carried to the next router before the app subscribes there: its ticket — a redirect's, or the last
    ///     sign-in's — presented on the new connection (none held: nothing to carry). A ticket refused there: the move goes
    ///     on, anonymous.
    /// </summary>
    private static async Task SignedInAsync(TaplineClient from, TaplineClient to, ReadOnlyMemory<byte> ticket,
        CancellationToken cancellationToken)
    {
        var realm = from.Identity?.Realm;
        if (ticket.IsEmpty) ticket = from.Ticket;
        if (realm is null || ticket.IsEmpty) return;
        to.UseTicket(realm, ticket);
        await to.ResumeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Completes once <paramref name="connection" /> has bound a session (its client reaches its router).</summary>
    private static async Task BoundAsync(TaplineStreamConnection connection, CancellationToken cancellationToken)
    {
        while (!connection.Client.Reachable || connection.Session is null)
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
    }

    private static TaplineClientOptions WithBlobBase(TaplineClientOptions options, Uri? blobBase)
    {
        return new TaplineClientOptions
        {
            BlobBase = blobBase ?? options.BlobBase, Http = options.Http, Fetch = options.Fetch,
            FetchAttempts = options.FetchAttempts, Time = options.Time
        };
    }

    private sealed record Running(
        TaplineStreamConnection Connection,
        CancellationTokenSource Stop,
        Task Run,
        int Index);
}