using System.Text.Json;
using ActivityFeed.Contracts;
using Grpc.Net.Client;
using MagicOnion;
using MagicOnion.Client;
using MagicOnion.Server;
using Tapline;
using Tapline.MagicOnion.Providers;
using Tapline.MagicOnion.Server;

namespace ActivityFeed.Sample;

/// <summary>
///     The practice service, as this app reaches it: its instances' feeds registered with the router as the "practice"
///     service (providers in other processes, over the link: step 1 goes to each serving one in turn), and a client of its
///     commands, which only the instance that writes the practice's records takes. The links are registered as the app
///     starts, and let go as it stops.
/// </summary>
public sealed class Practice(Publisher publisher, IConfiguration configuration, ILogger<Practice> log)
    : IHostedService, IDisposable
{
    public const string Service = "practice";

    private readonly List<RemoteProvider> _providers = [];
    private GrpcChannel?[]? _channels;
    private int _writer; // (the instance that last took a command: asked first)

    /// <summary>
    ///     The practice service's instances: their gRPC ports (Practice: one, or several comma-separated, for blue/green;
    ///     default http://127.0.0.1:5960).
    /// </summary>
    public Uri[] Addresses { get; } =
    [
        .. (configuration["Practice"] ?? "http://127.0.0.1:5960")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(a => new Uri(a))
    ];

    public void Dispose()
    {
        foreach (var channel in _channels ?? []) channel?.Dispose();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var address in Addresses) _providers.Add(publisher.ProvideRemote(Service, address, log: log));
        log.LogInformation("Practice: the \"{Service}\" service is the provider at {Addresses}", Service, string.Join(", ", Addresses.Select(a => a.ToString())));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var provider in _providers) await provider.DisposeAsync();
    }

    /// <summary>A client of the instance that last took a command: for what any instance answers (personas, explanations, notes).</summary>
    public IPracticeCommands Client(CancellationToken cancellationToken)
    {
        return Client(Volatile.Read(ref _writer), cancellationToken);
    }

    private IPracticeCommands Client(int at, CancellationToken cancellationToken)
    {
        var channels = _channels ??= new GrpcChannel?[Addresses.Length];
        var channel = channels[at] ??= GrpcChannel.ForAddress(Addresses[at], new GrpcChannelOptions
        {
            // (an instance not up yet, or back from a restart, asked again within seconds, not gRPC's two minutes)
            InitialReconnectBackoff = TimeSpan.FromMilliseconds(250), MaxReconnectBackoff = TimeSpan.FromSeconds(5)
        });
        return MagicOnionClient.Create<IPracticeCommands>(channel).WithCancellationToken(cancellationToken);
    }

    /// <summary>
    ///     A command, or a dispatcher's report, to the instance that writes the practice's records: the one that last took
    ///     one asked first, then each other in turn while one says another writes, or doesn't answer (stopped, say), for up
    ///     to eight seconds (a drain hands the lease on in a quarter of a second, a writer gone in about four). A retry is
    ///     safe: an instance answers a command it ran (by its correlation id) as it did, without running it again.
    /// </summary>
    public async Task<CommandAnswer> CommandAsync(Func<IPracticeCommands, UnaryResult<CommandAnswer>> command,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        var from = Volatile.Read(ref _writer);
        for (var attempt = 0;; attempt++)
        {
            var at = (from + attempt) % Addresses.Length;
            try
            {
                var answer = await command(Client(at, cancellationToken));
                if (!answer.NotWriter)
                {
                    Volatile.Write(ref _writer, at);
                    return answer;
                }
            }
            catch (Grpc.Core.RpcException e) when (e.StatusCode == Grpc.Core.StatusCode.Unavailable &&
                                                   !cancellationToken.IsCancellationRequested)
            {
            }

            if (DateTime.UtcNow > deadline)
                return new CommandAnswer { Refused = "No instance of the practice service writes its records now: try again shortly." };
            if ((attempt + 1) % Addresses.Length == 0) await Task.Delay(250, cancellationToken); // (a round of them: the lease passing)
        }
    }

    /// <summary>What any instance answers (personas, an explanation, a note): asked of each in turn while one doesn't answer.</summary>
    public async Task<T> AnyAsync<T>(Func<IPracticeCommands, UnaryResult<T>> ask, CancellationToken cancellationToken)
    {
        var from = Volatile.Read(ref _writer);
        for (var attempt = 0;; attempt++)
            try
            {
                return await ask(Client((from + attempt) % Addresses.Length, cancellationToken));
            }
            catch (Grpc.Core.RpcException e) when (e.StatusCode == Grpc.Core.StatusCode.Unavailable &&
                                                   attempt + 1 < Addresses.Length && !cancellationToken.IsCancellationRequested)
            {
            }
    }

    /// <summary>The practice's people, as the service has them: asked once, then kept (they're fixed).</summary>
    public async Task<Persona[]> PersonasAsync(CancellationToken cancellationToken)
    {
        return _personas ??= await AnyAsync(c => c.PersonasAsync(), cancellationToken);
    }

    private Persona[]? _personas;
}

/// <summary>
///     The practice for clients of the stream (<see cref="IPracticeFeed" />): who the personas are, a person's feed bound
///     on the client's session (step 1 through the "practice" service, the person in its parameters: the session signed
///     in as them, by passthrough, beforehand), a dispatcher's shard bound the same way, and the commands and the dispatchers'
///     reports, forwarded to the service.
/// </summary>
public sealed class PracticeFeedService(Publisher publisher, TaplineStreamSessions sessions, Practice practice,
    ILogger<PracticeFeedService> log) : ServiceBase<IPracticeFeed>, IPracticeFeed
{
    public async UnaryResult<Persona[]> PersonasAsync()
    {
        return await practice.AnyAsync(c => c.PersonasAsync(), Context.CallContext.CancellationToken);
    }

    public UnaryResult<FeedBinding> SubscribeAsync(int outletId, uint nonce, int person, string? feed)
    {
        return BindAsync(outletId, nonce, feed is null ? JsonSerializer.Serialize(new { person }) : JsonSerializer.Serialize(new { person, feed }));
    }

    public UnaryResult<FeedBinding> DispatchAsync(int outletId, uint nonce, int shard, string dispatcher)
    {
        return BindAsync(outletId, nonce, JsonSerializer.Serialize(new { dispatcher, shard }));
    }

    public UnaryResult<FeedBinding> FollowAsync(int outletId, uint nonce, string follower)
    {
        // (the writer's alone: another instance refuses it as served elsewhere, and the router asks the next, in this step 1)
        return BindAsync(outletId, nonce, JsonSerializer.Serialize(new { follower }));
    }

    private async UnaryResult<FeedBinding> BindAsync(int outletId, uint nonce, string parameters)
    {
        if (sessions.Find(outletId, nonce) is not { } outlet) return new FeedBinding { UnknownSession = true };
        try
        {
            var tap = await publisher.SubscribeAsync(outlet, Practice.Service, parameters,
                cancellationToken: Context.CallContext.CancellationToken);
            if (tap is null) return new FeedBinding { Refused = "no reason given" };
            var binding = tap.Binding;
            return new FeedBinding
            {
                TapId = binding.TapId, Topic = binding.Topic, Service = binding.Service, NodeId = binding.NodeId,
                Partition = binding.Partition
            };
        }
        catch (SubscriptionRefusedException e)
        {
            return new FeedBinding { Refused = e.Refusal.Reason, CorrelationId = e.Refusal.CorrelationId };
        }
        catch (ServiceUnavailableException)
        {
            return new FeedBinding { Unavailable = true };
        }
        catch (Exception e) when (e is not OperationCanceledException) // (its step 1 failed: the service coming back, say)
        {
            log.LogWarning("Practice: step 1 ({Parameters}) failed: {Error}: asked again shortly", parameters, e.Message);
            return new FeedBinding { Unavailable = true };
        }
    }

    public async UnaryResult<CommandAnswer> ArriveAsync(int person, long booking, byte[] correlation, CommandOrder? order)
    {
        return await practice.CommandAsync(c => c.ArriveAsync(person, booking, correlation, order), Context.CallContext.CancellationToken);
    }

    public async UnaryResult<CommandAnswer> CallAsync(int person, long booking, string room, byte[] correlation, CommandOrder? order)
    {
        return await practice.CommandAsync(c => c.CallAsync(person, booking, room, correlation, order), Context.CallContext.CancellationToken);
    }

    public async UnaryResult<CommandAnswer> AcknowledgeAsync(int person, long activity, byte[] correlation)
    {
        return await practice.CommandAsync(c => c.AcknowledgeAsync(person, activity, correlation), Context.CallContext.CancellationToken);
    }

    public async UnaryResult<CommandAnswer> SeenAsync(int person, long[] activities, byte[] correlation)
    {
        return await practice.CommandAsync(c => c.SeenAsync(person, activities, correlation), Context.CallContext.CancellationToken);
    }

    public async UnaryResult<CommandAnswer> MessageAsync(int person, string text, byte[] correlation, CommandOrder? order)
    {
        return await practice.CommandAsync(c => c.MessageAsync(person, text, correlation, order), Context.CallContext.CancellationToken);
    }

    public async UnaryResult<CommandAnswer> ReplyAsync(int person, long message, string text, byte[] correlation, CommandOrder? order)
    {
        return await practice.CommandAsync(c => c.ReplyAsync(person, message, text, correlation, order), Context.CallContext.CancellationToken);
    }

    public async UnaryResult<CommandAnswer> ShareAsync(int person, int gp, string tag, byte[] correlation)
    {
        return await practice.CommandAsync(c => c.ShareAsync(person, gp, tag, correlation), Context.CallContext.CancellationToken);
    }

    public async UnaryResult<CommandAnswer> WithdrawAsync(int person, long consent, byte[] correlation)
    {
        return await practice.CommandAsync(c => c.WithdrawAsync(person, consent, correlation), Context.CallContext.CancellationToken);
    }

    public async UnaryResult<CommandAnswer> BreakGlassAsync(int person, int patient, byte[] correlation)
    {
        return await practice.CommandAsync(c => c.BreakGlassAsync(person, patient, correlation), Context.CallContext.CancellationToken);
    }

    public async UnaryResult<CommandAnswer> ReleaseAsync(int person, long section, byte[] correlation)
    {
        return await practice.CommandAsync(c => c.ReleaseAsync(person, section, correlation), Context.CallContext.CancellationToken);
    }

    public async UnaryResult<CommandAnswer> RevokeCarerAsync(int person, int carer, byte[] correlation)
    {
        return await practice.CommandAsync(c => c.RevokeCarerAsync(person, carer, correlation), Context.CallContext.CancellationToken);
    }

    public async UnaryResult<Explanation> ExplainAsync(int person)
    {
        return await practice.AnyAsync(c => c.ExplainAsync(person), Context.CallContext.CancellationToken);
    }

    public async UnaryResult<NoteAnswer> NoteAsync(int person, long section)
    {
        return await practice.AnyAsync(c => c.NoteAsync(person, section), Context.CallContext.CancellationToken);
    }

    public async UnaryResult<AuditAnswer> AuditAsync(int person, byte part, long seq)
    {
        return await practice.AnyAsync(c => c.AuditAsync(person, part, seq), Context.CallContext.CancellationToken);
    }

    public async UnaryResult<CommandAnswer> NoticeAsync(int person, int site, string notice, byte[] correlation)
    {
        return await practice.CommandAsync(c => c.NoticeAsync(person, site, notice, correlation), Context.CallContext.CancellationToken);
    }

    public async UnaryResult<CommandAnswer> HandedOffAsync(string dispatcher, int shard, long seq, byte[] correlation)
    {
        return await practice.CommandAsync(c => c.HandedOffAsync(dispatcher, shard, seq, correlation), Context.CallContext.CancellationToken);
    }

    public async UnaryResult<CommandAnswer> FailedAsync(string dispatcher, int shard, long seq, string reason, byte[] correlation)
    {
        return await practice.CommandAsync(c => c.FailedAsync(dispatcher, shard, seq, reason, correlation), Context.CallContext.CancellationToken);
    }

    public async UnaryResult<CommandAnswer> RecordedAsync(string dispatcher, int shard, long seq, byte[] correlation)
    {
        return await practice.CommandAsync(c => c.RecordedAsync(dispatcher, shard, seq, correlation), Context.CallContext.CancellationToken);
    }
}
