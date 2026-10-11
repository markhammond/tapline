using MagicOnion;
using MagicOnion.Server;
using Microsoft.AspNetCore.WebUtilities;
using Tapline;
using Tapline.MagicOnion.Server;

namespace PivotTable.Sample;

/// <summary>
///     The pivot for clients of the stream (<see cref="IPivotService" />): the statement a page's address asks for, as the
///     page makes it (<see cref="PivotCriteria.FromQuery" />); a statement's shape, from the provider (here, in this app:
///     asked directly); and its step 1, bound through the "pivot" service on the client's session.
/// </summary>
public sealed class PivotService(Publisher publisher, TaplineStreamSessions sessions, IServiceProvider services)
    : ServiceBase<IPivotService>, IPivotService
{
    private readonly PivotProvider
        _provider = services.GetRequiredService<PivotProvider>(); // (the app's own: no public type of the service's)

    public UnaryResult<PivotStatement> StatementAsync(string address)
    {
        var at = address.IndexOf('?');
        var criteria = PivotCriteria.FromQuery(QueryHelpers.ParseQuery(at >= 0 ? address[at..] : address));
        return UnaryResult.FromResult(new PivotStatement
        {
            Sql = criteria.ToSql(), Bindings = new Dictionary<string, string[]>(criteria.Bindings()),
            Extras = [.. criteria.Extras.Select(e => e.ToString())]
        });
    }

    public async UnaryResult<PivotDescription> DescribeAsync(string sql, Dictionary<string, string[]> bindings, string[] extras)
    {
        var (shape, problem) =
            await _provider.DescribeAsync(sql, bindings ?? [], extras ?? [], Context.CallContext.CancellationToken);
        return shape is not null
            ? new PivotDescription { Shape = shape.ToJson() }
            : new PivotDescription { Refused = problem };
    }

    public async UnaryResult<PivotBinding> SubscribeAsync(int outletId, uint nonce, string sql,
        Dictionary<string, string[]> bindings, bool incremental, string[] extras, Guid? shard = null,
        double minIntervalMs = 0)
    {
        if (sessions.Find(outletId, nonce) is not { } outlet) return new PivotBinding { UnknownSession = true };
        try
        {
            var tap = await publisher.SubscribeAsync(outlet, "pivot",
                PivotCriteria.Parameters(sql, bindings ?? [], incremental: incremental, extras: extras ?? [], shard: shard),
                minIntervalMs > 0 ? new SubscribeOptions { MinInterval = TimeSpan.FromMilliseconds(minIntervalMs) } : SubscribeOptions.None,
                Context.CallContext.CancellationToken); // (its own rate: on the route as it binds, shown to the provider)
            if (tap is null) return new PivotBinding { Refused = "no reason given" };
            var binding = tap.Binding;
            return new PivotBinding
            {
                TapId = binding.TapId, Topic = binding.Topic, Service = binding.Service, NodeId = binding.NodeId,
                Partition = binding.Partition, Shard = _provider.Shard
            };
        }
        catch (SubscriptionRefusedException e)
        {
            return new PivotBinding { Refused = e.Refusal.Reason, CorrelationId = e.Refusal.CorrelationId };
        }
        catch (ServiceUnavailableException)
        {
            return new PivotBinding { Unavailable = true };
        }
    }

    public async UnaryResult<PivotToken> SetTradesPerDayAsync(double perDay)
    {
        var version = await _provider.SetTradesPerDayAsync(perDay).WaitAsync(Context.CallContext.CancellationToken);
        return new PivotToken { Shard = _provider.Shard, Version = version };
    }

    public UnaryResult<PivotToken> CurrentAsync()
    {
        return UnaryResult.FromResult(new PivotToken { Shard = _provider.Shard, Version = _provider.Version });
    }

    public async UnaryResult<PivotToken> WaitAsync(int outletId, uint nonce, uint tapId, PivotToken token)
    {
        if (token.Shard != _provider.Shard)
            return new PivotToken { Refused = $"shard {token.Shard} isn't this provider's ({_provider.Shard})" };
        if (sessions.Find(outletId, nonce) is not { } outlet ||
            outlet.Taps.FirstOrDefault(t => t.Id == tapId) is not { } tap)
            return new PivotToken { Refused = $"no subscription {tapId} on that session" };
        if (_provider.WaitAsync(tap.Source, token.Version, Context.CallContext.CancellationToken) is not { } reached)
            return new PivotToken { Refused = $"subscription {tapId} isn't a pivot of this provider's" };
        try
        {
            var version = await reached;
            tap.Hurry(); // (its own token: at once, its minimum interval notwithstanding)
            return new PivotToken { Shard = _provider.Shard, Version = version };
        }
        catch (OperationCanceledException)
        {
            return new PivotToken { Refused = "the wait was given up" };
        }
    }
}