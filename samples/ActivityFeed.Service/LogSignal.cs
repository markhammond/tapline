using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ActivityFeed.Contracts;
using Grpc.Core;
using Grpc.Net.Client;
using MagicOnion.Client;
using Tapline;
using Tapline.Client;
using Tapline.MagicOnion;

namespace ActivityFeed.Service;

/// <summary>
///     The write log's signal, as an instance that follows the writer hears it: the writer's <c>log</c> topic (a last value,
///     <see cref="LogAt" />) on a session of this instance's own with the app (Tapline over MagicOnion, as any client's),
///     bound by step 1 through the app (the writer's alone: another instance refuses it, and the app asks each in turn), and
///     bound again whenever it ends (the writer gone, or another writing) or its node drains. Each value wakes the loop to
///     read the log on: a hop through the router after the append, not a look at the store. A value saying the writer let
///     the lease go (a drain), or the topic's end, has the instance ask for the lease at once. Connected the first time it's
///     wanted (an instance that only writes never is); let go with the service.
/// </summary>
internal sealed class LogSignal(Uri stream, string name, Action<LogAt> heard, Action ended, ILogger log) : IAsyncDisposable
{
    private readonly Lock _gate = new(); // (taken inside the client's lock, never around it)
    private readonly SemaphoreSlim _kick = new(0);
    private readonly CancellationTokenSource _stop = new();
    private GrpcChannel? _channel;
    private TaplineStreamConnection? _connection;
    private Task? _running;
    private volatile bool _wanted;
    private uint _tapId; // (the binding heard from; 0: none)
    private Guid? _node; // (its node, the router's label for the writer: moved if it drains)
    private bool _moving;
    private long _values;

    /// <summary>The node it's bound through (the writer's, as the router labels it), while it is.</summary>
    public Guid? Node
    {
        get
        {
            lock (_gate)
            {
                return _tapId == 0 ? null : _node;
            }
        }
    }

    /// <summary>The values heard.</summary>
    public long Values => Interlocked.Read(ref _values);

    /// <summary>Following (bound while so), or writing (let go): the loop's to say, each time round.</summary>
    public void Want(bool following)
    {
        if (_wanted == following) return;
        _wanted = following;
        if (following && _running is null) Start();
        _kick.Release();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_running is not null)
            try
            {
                await _running;
            }
            catch (OperationCanceledException)
            {
            }

        _channel?.Dispose();
        _stop.Dispose();
    }

    private void Start()
    {
        _channel = GrpcChannel.ForAddress(stream, new GrpcChannelOptions
        {
            // (the app not up yet, or back from a restart: asked again within seconds, not gRPC's two minutes)
            InitialReconnectBackoff = TimeSpan.FromMilliseconds(250), MaxReconnectBackoff = TimeSpan.FromSeconds(5)
        });
        _connection = new TaplineStreamConnection(_channel);
        _connection.Connected += (_, _) => _kick.Release(); // (a session up: a new one's binding is gone)
        _connection.Client.ServiceDraining += (node, _, _) =>
        {
            lock (_gate)
            {
                if (_tapId == 0 || node != _node) return;
                _moving = true; // (bound again, make before break: another instance writes now, or will)
            }

            ended();
            _kick.Release();
        };
        _running = RunAsync(_connection, _stop.Token);
    }

    private async Task RunAsync(TaplineStreamConnection connection, CancellationToken stop)
    {
        var streaming = connection.RunAsync(stop);
        var delay = TimeSpan.FromMilliseconds(50);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                uint tap;
                bool moving;
                lock (_gate)
                {
                    (tap, moving) = (_tapId, _moving);
                }

                if (!_wanted || (tap != 0 && !moving)) // (writing, or bound: nothing to do until that changes)
                {
                    if (!_wanted && tap != 0) Let(connection, tap);
                    await _kick.WaitAsync(stop);
                    continue;
                }

                if (await BindAsync(connection, moving, stop))
                {
                    delay = TimeSpan.FromMilliseconds(50);
                    continue;
                }

                await _kick.WaitAsync(delay, stop); // (no writer yet, or no session: asked again, less often each time)
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.TicksPerSecond));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }

        try
        {
            await streaming;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    ///     The writer's log topic bound (step 1 through the app), on this session: false if no instance takes it now (none
    ///     writes) or there's no session yet. Moving: it replaces the one bound through a draining node.
    /// </summary>
    private async Task<bool> BindAsync(TaplineStreamConnection connection, bool moving, CancellationToken stop)
    {
        if (connection.Session is not { } session || !await connection.Client.SignedInAsync(name, stop)) return false;
        FeedBinding answer;
        try
        {
            answer = await MagicOnionClient.Create<IPracticeFeed>(_channel!).WithCancellationToken(stop)
                .FollowAsync(session.OutletId, session.Nonce, name);
        }
        catch (RpcException)
        {
            return false;
        }

        if (answer.Unavailable || answer.UnknownSession || answer.Refused is not null) return false;
        var binding = new TapBinding(answer.TapId, answer.Topic, null, answer.Service, answer.NodeId, answer.Partition);
        var handler = new SubscriptionHandler
        {
            Value = (subscription, value) =>
            {
                if (!Current(subscription) || value.Length != Unsafe.SizeOf<LogAt>()) return;
                Interlocked.Increment(ref _values);
                heard(MemoryMarshal.Read<LogAt>(value.Span));
            },
            Error = (subscription, error) => // (the writer gone, or another writing: bound again, to whichever writes)
            {
                lock (_gate)
                {
                    if (subscription.TapId != _tapId) return; // (one it replaced, moving: its end the move's)
                    _tapId = 0;
                }

                log.LogInformation("Practice: {Name} stopped hearing the log's signal ({Code}): bound again once an instance writes",
                    name, error.Code);
                ended();
                _kick.Release();
            }
        };
        uint old;
        lock (_gate)
        {
            old = _tapId;
            (_tapId, _node, _moving) = (answer.TapId, answer.NodeId, false);
        }

        if (moving && old != 0) connection.Client.Replace(old, binding, handler);
        else connection.Client.Subscribe(binding, handler);
        log.LogInformation("Practice: {Name} hears the log's signal through the router (node {Node}){Moved}", name, answer.NodeId,
            moving ? ", moved off a draining node" : "");
        return true;
    }

    private bool Current(Subscription subscription)
    {
        lock (_gate)
        {
            return subscription.TapId == _tapId;
        }
    }

    /// <summary>Its binding let go: this instance writes now.</summary>
    private void Let(TaplineStreamConnection connection, uint tap)
    {
        lock (_gate)
        {
            if (_tapId != tap) return;
            _tapId = 0;
        }

        connection.Client.Drop(tap);
    }
}
