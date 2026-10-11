using Grpc.Net.Client;
using MagicOnion.Client;
using PivotTable.Contracts;
using Tapline;
using Tapline.Client;
using Tapline.MagicOnion;

namespace PivotTable.Headless;

/// <summary>
///     The pivot streamed: from the first router — each connection's handlers the watch's — moved to the next as it drains
///     (make before break: subscribed there, then the old let go); subscribed again whenever there's no subscription and a
///     session (step 1, then step 2: the client activates it); its call dropped and its router drained, if asked; a line a
///     second; and the run's end, its exit code.
/// </summary>
internal sealed class PivotRun
{
    private readonly HttpClient _blobs = new(); // out-of-band records: the app's port, a connection of their own
    private readonly Endpoints _endpoints;
    private readonly Options _options;
    private readonly TaplineRouters _routers;
    private readonly PivotStatement _statement;
    private readonly CancellationTokenSource _stop;
    private readonly SemaphoreSlim _want = new(0); // a subscription wanted (step 1): no subscription, and a session
    private readonly Watch _watch;
    private Subscription? _current;

    public PivotRun(Options options, Endpoints endpoints, GrpcChannel[] channels, Described described,
        CancellationTokenSource stop)
    {
        (_options, _endpoints, _statement, _stop) = (options, endpoints, described.Statement, stop);
        _watch = new Watch(options, described.Shape);
        _routers = new TaplineRouters(channels.Select((channel, i) => new TaplineRouter(channel, endpoints.Blobs[i])),
            new TaplineStreamConnectionOptions
            {
                BandwidthLimit = options.BandwidthLimit,
                Client = new TaplineClientOptions { Http = _blobs }
            });
        _routers.Starting += Handle;
        _routers.Move = MoveAsync;
        _routers.Moved += move => _watch.Moved(move);
        _routers.MoveFailed += why => _watch.Note($"a move given up: {why}");
    }

    /// <summary>Streams until stopped (Ctrl+C, the verification's time up, a refusal): 0 if nothing failed, else 1.</summary>
    public async Task<int> RunAsync()
    {
        var token = _stop.Token;
        var streaming = _routers.RunAsync(token);
        var subscribing = SubscribeAsync(token);
        var dropping = _options.DropEvery is { } every ? DropAsync(every.Value, token) : Task.CompletedTask;
        var draining = _options.Drain is { } after ? DrainAsync(after.Value, token) : Task.CompletedTask;
        var pacing = _options.Pace is { } pace ? PaceAsync(pace, token) : Task.CompletedTask;
        var waiting = _options.WaitEvery is { } often ? WaitEveryAsync(often.Value, token) : Task.CompletedTask;
        using (var timer = new PeriodicTimer(TimeSpan.FromSeconds(1)))
        {
            try
            {
                while (await timer.WaitForNextTickAsync(token)) _watch.Second(_routers.Current.Client);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await Task.WhenAll(streaming, subscribing, dropping, draining, pacing, waiting)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _blobs.Dispose();
        return _watch.End(_routers.Current.Client, _options.Verify is not null);
    }

    /// <summary>The subscription's own rate, as asked (--min-interval): 0, none.</summary>
    private double MinIntervalMs => _options.MinInterval?.Value.TotalMilliseconds ?? 0;

    private bool IsCurrent(TaplineStreamConnection connection)
    {
        return ReferenceEquals(_routers.Current, connection);
    }

    /// <summary>
    ///     Each router's connection, as it's made: the watch's handlers on its client — and a subscription wanted once
    ///     it's bound.
    /// </summary>
    private void Handle(TaplineStreamConnection connection)
    {
        var client = connection.Client;
        client.HandlerFailed += (_, e) => _watch.Failed($"a handler threw: {e.Message}");
        client.Malformed += what => _watch.Failed(what);
        client.Refused += r => _watch.Note($"refused by {r.Service}: {r.Reason} (correlation {r.CorrelationId})");
        client.ServiceDown += (node, correlation) =>
            _watch.Note(
                $"the pivot's provider node is gone ({node}, correlation {correlation}): subscribing again once it's up");
        client.ServiceUp += (service, correlation) =>
        {
            _watch.Note($"{service} is up again (correlation {correlation})");
            if (IsCurrent(connection)) _want.Release();
        };
        client.ReachabilityChanged += reachable =>
        {
            if (!reachable && !_stop.IsCancellationRequested && IsCurrent(connection))
                _watch.Note("the router out of reach: connecting again");
        };
        client.RouterStatusChanged += router => _watch.Note(
            router.State switch // its correlation id: the router logged the change with it
            {
                RouterState.Degraded => $"the router is degraded: {router.Reason} (correlation {router.Correlation})",
                RouterState.Draining =>
                    $"the router is draining, gone by {router.DrainsBy?.ToLocalTime():HH:mm:ss} (correlation {router.Correlation})",
                _ => $"the router is serving (correlation {router.Correlation})"
            });
        connection.Connected += (outletId, nonce) =>
        {
            _watch.Connected(outletId, nonce);
            if (IsCurrent(connection) && Volatile.Read(ref _current) is null or { IsEnded: true }) _want.Release();
        };
    }

    /// <summary>Make before break: subscribed on the next router, streaming, then the old let go.</summary>
    private async Task MoveAsync(RouterMove move)
    {
        _watch.Note($"moving: the router is {move.Why}");
        var session = move.To.Session!.Value;
        var answer = await MagicOnionClient.Create<IPivotService>(move.Router.Channel)
            .WithCancellationToken(move.Cancellation)
            .SubscribeAsync(session.OutletId, session.Nonce, _statement.Sql, _statement.Bindings, _options.Incremental,
                _statement.Extras, null, MinIntervalMs);
        if (answer.TapId == 0)
            throw new InvalidOperationException(answer.Refused ??
                                                (answer.Unavailable
                                                    ? "the pivot isn't up there yet"
                                                    : "no session there"));
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var moved = move.To.Client.Subscribe(Binding(answer), Handler(first));
        await first.Task.WaitAsync(move.Cancellation);
        _watch.Subscribed(answer);
        Volatile.Write(ref _current, moved);
    }

    /// <summary>Step 1 whenever there's no subscription and a session: the statement, then step 2 (the client activates it).</summary>
    private async Task SubscribeAsync(CancellationToken token)
    {
        var delay = TimeSpan.FromMilliseconds(250);
        try
        {
            while (true)
            {
                await _want.WaitAsync(token);
                var connection = _routers.Current;
                if (Volatile.Read(ref _current) is { IsEnded: false } ||
                    connection?.Session is not { } session) continue;
                PivotBinding answer;
                try
                {
                    answer = await MagicOnionClient.Create<IPivotService>(_routers.Router.Channel)
                        .WithCancellationToken(token)
                        .SubscribeAsync(session.OutletId, session.Nonce, _statement.Sql, _statement.Bindings,
                            _options.Incremental, _statement.Extras, null, MinIntervalMs);
                }
                catch (Exception e) when (!token.IsCancellationRequested)
                {
                    _watch.Note($"step 1 failed ({e.GetType().Name}): asking again");
                    await Task.Delay(delay, token);
                    _want.Release();
                    continue;
                }

                if (answer.UnknownSession) continue; // the stream is connecting again: Connected asks
                if (answer.Unavailable)
                {
                    _watch.Note($"pivot: starting — subscribing again in {delay.TotalSeconds:0.##} s");
                    await Task.Delay(delay, token);
                    delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(2).Ticks));
                    _want.Release();
                    continue;
                }

                if (answer.Refused is { } why)
                {
                    _watch.Refused(why, answer.CorrelationId);
                    _stop.Cancel();
                    return;
                }

                delay = TimeSpan.FromMilliseconds(250);
                _watch.Subscribed(answer);
                Volatile.Write(ref _current, connection.Client.Subscribe(Binding(answer), Handler()));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static TapBinding Binding(PivotBinding answer)
    {
        return new TapBinding(answer.TapId, answer.Topic, null, answer.Service, answer.NodeId, answer.Partition);
    }

    /// <summary>The pivot's handler: each batch to the watch (the first, if asked, said); an error, and subscribing again.</summary>
    private SubscriptionHandler Handler(TaskCompletionSource? first = null)
    {
        return new SubscriptionHandler
        {
            Table = (_, table, batch) =>
            {
                _watch.Batch(table, batch);
                first?.TrySetResult();
            },
            Error = (subscription, error) =>
            {
                first?.TrySetException(new InvalidOperationException($"ended: {error.Code}"));
                _watch.Ended(error);
                if (ReferenceEquals(Volatile.Read(ref _current), subscription))
                    _ = Task.Delay(250)
                        .ContinueWith(_ =>
                            _want.Release()); // subscribe again (step 1) — the provider back, or a new session
            }
        };
    }

    /// <summary>
    ///     Waits for <paramref name="token" />: the provider asked to run the pivot at once (its <c>WaitAsync</c>, on this
    ///     subscription), and a batch at or after it in front of this client — whichever comes, the batch is what counts.
    ///     The time from <paramref name="asked" />, or null if not within <paramref name="limit" />.
    /// </summary>
    private async Task<double?> InFrontAsync(PivotToken token, long asked, TimeSpan limit, CancellationToken stop)
    {
        var inFront = _watch.WaitForAsync(token);
        if (Volatile.Read(ref _current) is { IsEnded: false } current && _routers.Current.Session is { } session)
            _ = MagicOnionClient.Create<IPivotService>(_routers.Router.Channel).WithCancellationToken(stop)
                .WaitAsync(session.OutletId, session.Nonce, current.TapId, token).ResponseAsync
                .ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully && t.Result.Refused is { } why)
                        _watch.Note($"the provider's wait for {token} refused: {why}");
                    else if (t.IsCompletedSuccessfully && _options.CellsShown > 0 || Environment.GetEnvironmentVariable("PIVOT_WAITS") == "1")
                        _watch.Note($"the provider's wait for {token}: posted and applied at v{t.Result.Version}, answered after {System.Diagnostics.Stopwatch.GetElapsedTime(asked).TotalMilliseconds:N1} ms");
                }, TaskScheduler.Default);
        try
        {
            await inFront.WaitAsync(limit, stop);
            return System.Diagnostics.Stopwatch.GetElapsedTime(asked).TotalMilliseconds;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>Five seconds in, the pace set — the token of the refresh applying it answered — and waited for.</summary>
    private async Task PaceAsync(double perDay, CancellationToken stop)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stop);
            var asked = System.Diagnostics.Stopwatch.GetTimestamp();
            var token = await MagicOnionClient.Create<IPivotService>(_routers.Router.Channel).WithCancellationToken(stop)
                .SetTradesPerDayAsync(perDay);
            var applied = System.Diagnostics.Stopwatch.GetElapsedTime(asked).TotalMilliseconds;
            var took = await InFrontAsync(token, asked, TimeSpan.FromSeconds(30), stop);
            _watch.Waited(took, took is { } ms
                ? $"pace set to {perDay:N0} trades a day: applied at {token} in {applied:N0} ms (the history primed again); in front of this client after {ms:N0} ms"
                : $"pace set to {perDay:N0} trades a day, applied at {token}: not in front of this client within 30 s");
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException e)
        {
            _watch.Failed($"the pace: {e.Message}");
        }
    }

    /// <summary>That often, the engine's current token asked for and waited for: token → in front, timed.</summary>
    private async Task WaitEveryAsync(TimeSpan every, CancellationToken stop)
    {
        try
        {
            while (true)
            {
                await Task.Delay(every, stop);
                if (Volatile.Read(ref _current) is not { IsEnded: false }) continue;
                var asked = System.Diagnostics.Stopwatch.GetTimestamp();
                var token = await MagicOnionClient.Create<IPivotService>(_routers.Router.Channel)
                    .WithCancellationToken(stop).CurrentAsync();
                double? took;
                try
                {
                    took = await InFrontAsync(token, asked, TimeSpan.FromSeconds(10), stop);
                }
                catch (InvalidOperationException e) // (another shard's: moved meanwhile)
                {
                    _watch.Note($"a wait skipped: {e.Message}");
                    continue;
                }

                _watch.Waited(took, took is { } ms
                    ? $"token {token}: in front of this client after {ms:N1} ms"
                    : $"token {token}: not in front of this client within 10 s");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The call dropped every so often, as a network might: the session resumed.</summary>
    private async Task DropAsync(TimeSpan every, CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(every, token);
                _watch.Note("dropping the call");
                _routers.Current.Drop();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    ///     That long in, the first router drained on request — its ops endpoints' drain — answering once its clients have
    ///     moved.
    /// </summary>
    private async Task DrainAsync(TimeSpan after, CancellationToken token)
    {
        try
        {
            await Task.Delay(after, token);
            _watch.Note($"draining the first router ({_endpoints.Ops})");
            using var ops = new HttpClient { BaseAddress = _endpoints.Ops };
            using var answer = await ops.PostAsync("ops/tapline/drain?grace=10s", null, token);
            _watch.Note(
                $"the drain answered {(int)answer.StatusCode}: {await answer.Content.ReadAsStringAsync(token)}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (HttpRequestException e)
        {
            _watch.Failed($"the drain failed: {e.Message}");
        }
    }
}