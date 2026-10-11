using ActivityFeed.Contracts;
using Grpc.Net.Client;
using MagicOnion.Client;
using Tapline;
using Tapline.Client;
using Tapline.MagicOnion;

namespace ActivityFeed.Dispatcher;

/// <summary>
///     A dispatcher: its shards' topics, each entry acted on once (sent to the last mile, or recorded) and reported, and
///     forgotten once the rendezvous says its removal is in front of it. Entries it was acting on when it last stopped are
///     in its topics again: at least once, the collapse key (the notification's id) letting a device show it only once.
///     <para>
///         Its order: a device's entries in their shard's order, one at a time (its ordering key: a push never overtakes one
///         written before it), a failed one first again after a pause, holding back that device alone. Across devices, the
///         most pressing first: urgent transactional, transactional, operational, informational. Within each, the sites
///         take turns, so one site's burst (a notice to all its patients) doesn't hold another's back; within a site, the
///         earliest deadline first.
///     </para>
/// </summary>
public sealed class Dispatcher(GrpcChannel channel, Uri app, HttpClient http, string name, int[] shards, bool spawned)
{
    private readonly HashSet<(int Shard, long Seq)> _acting = []; // (in hand: not again, until it's gone)
    private readonly Dictionary<long, SortedList<(int Shard, long Seq), NotifyRow>> _queued = []; // by device, in order
    private readonly HashSet<long> _busy = []; // (devices with an entry in flight, or a failed one's pause)
    private readonly SemaphoreSlim _ready = new(0); // (an entry in, or a device let go: a worker may find one)
    private byte _lastSite; // (the site last served: the next as pressing goes to another, if one has one)
    private readonly List<(int Shard, long Seq, NotifyRow Row)> _heads = []; // (Next's: each free device's first)

    private readonly TaplineStreamConnection _connection = new(channel, new TaplineStreamConnectionOptions
    {
        Client = new TaplineClientOptions { Http = http, BlobBase = new Uri(app, "_tapline/blob") }
    });

    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource> _rendezvous = []; // by correlation (hex)
    private readonly Dictionary<int, (uint TapId, Guid? NodeId)> _bound = []; // (each shard's binding: moved if its node drains)
    private int _handedOff, _failed, _recorded;

    private IPracticeFeed Practice(CancellationToken token)
    {
        return MagicOnionClient.Create<IPracticeFeed>(channel).WithCancellationToken(token);
    }

    public async Task RunAsync(CancellationToken token)
    {
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connection.Connected += (_, _) => connected.TrySetResult();
        _connection.Client.Rendezvous += (correlation, _) =>
        {
            lock (_gate)
            {
                if (_rendezvous.Remove(Convert.ToHexString(correlation.Span), out var done)) done.TrySetResult();
            }
        };
        // A node draining (blue/green): each shard bound through it bound again (another node answers), the old let go once
        // the new one streams. What the old one sent and the new one sends are the same entries: those in hand stay so.
        _connection.Client.ServiceDraining += (node, _, _) =>
        {
            List<int> moving;
            lock (_gate)
            {
                moving = [.. _bound.Where(b => b.Value.NodeId == node).Select(b => b.Key)];
            }

            foreach (var shard in moving) _ = MoveAsync(shard, token);
        };
        // One shard asked to move (its node rebalancing, not draining): the same, for that one.
        _connection.Client.SubscriptionMoving += (subscription, _, _) =>
        {
            int? moving;
            lock (_gate)
            {
                moving = _bound.Where(b => b.Value.TapId == subscription.TapId).Select(b => (int?)b.Key).FirstOrDefault();
            }

            if (moving is { } shard) _ = MoveAsync(shard, token);
        };
        var streaming = _connection.RunAsync(token);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(60), token);
        foreach (var shard in shards) await BindAsync(shard, token);
        // (spawned: where it's bound, for its parent, as a line it reads; else, in words)
        Console.Out.WriteLine(spawned ? $"ACTIVITY-DISPATCHER app={app}"
            : $"{name}: holding shards {string.Join(", ", shards)}");
        Console.Out.Flush();
        var workers = Enumerable.Range(0, 4).Select(_ => WorkAsync(token)).ToArray(); // (a few hand-offs at once)
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(token))
            if (!spawned)
                lock (_gate)
                {
                    Console.Out.WriteLine($"{name}: {_handedOff} handed off, {_failed} failed, {_recorded} recorded; {_acting.Count} in hand");
                }

        await Task.WhenAll(workers);
        await streaming;
    }

    /// <summary>A shard's topic bound on this session (step 1 through the app), asked again while the service starts.</summary>
    private async Task BindAsync(int shard, CancellationToken token, bool moving = false)
    {
        for (var delay = TimeSpan.FromMilliseconds(250);; delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.TicksPerSecond * 2)))
        {
            if (_connection.Session is not { } session || !await _connection.Client.SignedInAsync(name, token))
            {
                await Task.Delay(delay, token); // (no session yet, or not signed in on it: as its name)
                continue;
            }

            var answer = await Practice(token).DispatchAsync(session.OutletId, session.Nonce, shard, name);
            if (answer.Unavailable || answer.UnknownSession)
            {
                await Task.Delay(delay, token);
                continue;
            }

            if (answer.Refused is { } why) throw new InvalidOperationException($"{name}: shard {shard} refused: {why}");
            uint old;
            lock (_gate)
            {
                old = _bound.GetValueOrDefault(shard).TapId;
                _bound[shard] = (answer.TapId, answer.NodeId);
            }

            var binding = new TapBinding(answer.TapId, answer.Topic, null, answer.Service, answer.NodeId, answer.Partition);
            var handler = new SubscriptionHandler
            {
                Table = (subscription, table, _) =>
                {
                    if (Current(shard, subscription)) Applied(shard, table);
                },
                Error = (subscription, error) => // (its provider gone, say: bound again once it's back)
                {
                    if (!Current(shard, subscription)) return; // (one it replaced, moving: its end the move's)
                    Console.Out.WriteLine($"{name}: {answer.Topic} ended ({error.Code}): binding it again");
                    _ = AgainAsync(shard, token);
                }
            };
            if (moving) _connection.Client.Replace(old, binding, handler);
            else _connection.Client.Subscribe(binding, handler);
            return;
        }
    }

    private bool Current(int shard, Subscription subscription)
    {
        lock (_gate)
        {
            return _bound.GetValueOrDefault(shard).TapId == subscription.TapId;
        }
    }

    /// <summary>A shard moved off a draining node: bound again, make before break.</summary>
    private async Task MoveAsync(int shard, CancellationToken token)
    {
        try
        {
            await BindAsync(shard, token, true);
            Console.Out.WriteLine($"{name}: notify:{shard} moved (its node draining, or rebalancing)");
        }
        catch (Exception e) when (e is OperationCanceledException or Grpc.Core.RpcException or InvalidOperationException)
        {
            if (!token.IsCancellationRequested) _ = AgainAsync(shard, token);
        }
    }

    /// <summary>A shard's topic bound again, after a pause (a fault each time would spin), while the service comes back.</summary>
    private async Task AgainAsync(int shard, CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), token);
            await BindAsync(shard, token);
        }
        catch (Exception e) when (e is OperationCanceledException or Grpc.Core.RpcException or InvalidOperationException)
        {
            if (!token.IsCancellationRequested) _ = AgainAsync(shard, token);
        }
    }

    /// <summary>A shard's record applied: each entry it hasn't in hand, logged and queued (under the client's lock: no waiting).</summary>
    private void Applied(int shard, TableReplica table)
    {
        lock (_gate)
        {
            foreach (var row in table)
            {
                var seq = row.KeyAs<FeedKey>().Seq;
                if (!_acting.Add((shard, seq))) continue;
                var entry = row.RowAs<NotifyRow>();
                Console.Out.WriteLine($"{name}: notify:{shard} #{seq} {entry}");
                if (!_queued.TryGetValue(entry.DeviceId, out var queue)) _queued[entry.DeviceId] = queue = [];
                queue[(shard, seq)] = entry;
                _ready.Release();
            }
        }

        Console.Out.Flush();
    }

    private async Task WorkAsync(CancellationToken token)
    {
        while (true)
        {
            await _ready.WaitAsync(token);
            if (Next() is not var (shard, seq, entry)) continue; // (another took it, or each device with one is in flight)
            try
            {
                var correlation = TaplineClient.NewCorrelation();
                var arrived = Arrival(correlation);
                CommandAnswer answer;
                if (entry.Action == DispatchAction.NoOp)
                {
                    answer = await Practice(token).RecordedAsync(name, shard, seq, correlation);
                    Count(answer, ref _recorded);
                }
                else if (await LastMileAsync(entry, token) is { } refused)
                {
                    answer = await Practice(token).FailedAsync(name, shard, seq, refused, correlation);
                    Count(answer, ref _failed);
                }
                else
                {
                    answer = await Practice(token).HandedOffAsync(name, shard, seq, correlation);
                    Count(answer, ref _handedOff);
                }

                if (answer.Done) await arrived.WaitAsync(TimeSpan.FromSeconds(10), token); // (its removal, in front of us)
                else Console.Out.WriteLine($"{name}: notify:{shard} #{seq}: {answer.Refused}");
            }
            catch (Exception e) when (e is TimeoutException or Grpc.Core.RpcException)
            {
                // Not reported (or not known to be): acted on again shortly, still first for its device. A report the
                // service had taken is refused as one already made; a send is sent again (at least once: its collapse key
                // shows it only once).
                Console.Out.WriteLine($"{name}: notify:{shard} #{seq}: not reported ({e.Message}): again shortly");
                _ = AgainAsync(entry.DeviceId, token);
                continue;
            }

            Done(shard, seq, entry.DeviceId); // (its removal is applied: gone from the topic)
        }
    }

    /// <summary>
    ///     The entry to act on next: of the devices with one and none in flight, each's first (its shard's order), the most
    ///     pressing (<see cref="Rank" />); among those, the next site in turn; within it, the earliest deadline. Its device in
    ///     flight until it's done. None: another worker took it, or each device with one is in flight.
    /// </summary>
    private (int Shard, long Seq, NotifyRow Row)? Next()
    {
        lock (_gate)
        {
            _heads.Clear();
            foreach (var (device, queue) in _queued)
                if (queue.Count > 0 && !_busy.Contains(device))
                    _heads.Add((queue.Keys[0].Shard, queue.Keys[0].Seq, queue.Values[0]));
            if (Pick(_heads, _lastSite) is not { } next) return null;
            _busy.Add(next.Row.DeviceId);
            _lastSite = next.Row.Site;
            return next;
        }
    }

    /// <summary>
    ///     Of the devices' first entries (<paramref name="heads" />), the one to act on next: the most pressing
    ///     (<see cref="Rank" />); among those, the next site after <paramref name="lastSite" /> in turn; within it, the
    ///     earliest deadline, then the shard's order. None if there are none.
    /// </summary>
    public static (int Shard, long Seq, NotifyRow Row)? Pick(IEnumerable<(int Shard, long Seq, NotifyRow Row)> heads, byte lastSite)
    {
        (int Shard, long Seq, NotifyRow Row)? best = null;
        var (bestRank, bestTurn) = (int.MaxValue, int.MaxValue);
        foreach (var (shard, seq, row) in heads)
        {
            // (the sites in turn: those after the last served first, then it and those before it)
            var (rank, turn) = (Rank(row), row.Site > lastSite ? row.Site - lastSite : row.Site - lastSite + 256);
            if (best is { } b && (rank, turn, row.DeliverByMicros, shard, seq).CompareTo(
                    (bestRank, bestTurn, b.Row.DeliverByMicros, b.Shard, b.Seq)) >= 0) continue;
            (best, bestRank, bestTurn) = ((shard, seq, row), rank, turn);
        }

        return best;
    }

    /// <summary>How pressing an entry is: urgent transactional (0), transactional, operational, informational (3).</summary>
    private static int Rank(in NotifyRow row)
    {
        return row.Class switch
        {
            NotificationClass.Transactional => row.Urgent != 0 ? 0 : 1,
            NotificationClass.Operational => 2,
            _ => 3
        };
    }

    /// <summary>An entry done: out of its device's queue, the device free for its next.</summary>
    private void Done(int shard, long seq, long device)
    {
        lock (_gate)
        {
            _acting.Remove((shard, seq));
            if (_queued.TryGetValue(device, out var queue) && queue.Remove((shard, seq)) && queue.Count == 0)
                _queued.Remove(device);
            _busy.Remove(device);
        }

        _ready.Release();
    }

    /// <summary>A failed entry's device, free again after a pause: its entry still first.</summary>
    private async Task AgainAsync(long device, CancellationToken token)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), token);
        lock (_gate)
        {
            _busy.Remove(device);
        }

        _ready.Release();
    }

    private Task Arrival(byte[] correlation)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _rendezvous[Convert.ToHexString(correlation)] = done;
        }

        return done.Task;
    }

    private void Count(CommandAnswer answer, ref int count)
    {
        if (!answer.Done) return;
        lock (_gate)
        {
            count++;
        }
    }

    /// <summary>
    ///     The last mile, out of scope: a stand-in that takes a send after a moment, but refuses a token it no longer
    ///     knows (the practice's are "dead…" then). Why not, or null: handed off.
    /// </summary>
    private static async Task<string?> LastMileAsync(NotifyRow entry, CancellationToken token)
    {
        await Task.Delay(Random.Shared.Next(20, 200), token);
        return entry.Token.ToString().StartsWith("dead", StringComparison.Ordinal)
            ? "BadDeviceToken: the last mile no longer knows this token"
            : null;
    }
}
