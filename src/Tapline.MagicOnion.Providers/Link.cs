using System.Runtime.CompilerServices;
using Grpc.Core;
using Grpc.Net.Client;
using MagicOnion;
using MagicOnion.Client;
using MagicOnion.Server;
using Tapline.Client;

namespace Tapline.MagicOnion.Providers;

/// <summary>A link's control stream, as one side sees it: what it says, in order, and what the other side says.</summary>
public interface IControlStream<in TOut, out TIn> : IAsyncDisposable
{
    /// <summary>Sends one message: awaited, so messages go in the order they're sent.</summary>
    ValueTask WriteAsync(TOut message, CancellationToken cancellationToken);

    /// <summary>The other side's messages, as they come, until the stream ends.</summary>
    IAsyncEnumerable<TIn> ReadAllAsync(CancellationToken cancellationToken);
}

/// <summary>
///     A provider in another process, as the router reaches it: the Tapline stream it takes the provider's topics on (its
///     client of the provider's own router, and that client's session there) and the control stream beside it.
/// </summary>
public interface IProviderLink
{
    /// <summary>The router's client of the provider's router: each topic it mirrors comes on a subscription of its.</summary>
    TaplineClient Client { get; }

    /// <summary>
    ///     The client's session at the provider's router, while its call is bound to it (none between calls): what the
    ///     router's hello names. A session bound before, to a process started again since, is no session there.
    /// </summary>
    (int OutletId, uint Nonce)? Session { get; }

    /// <summary>Raised each time the client's session is bound (again, after a reconnect: perhaps a new one).</summary>
    event Action<int, uint>? Connected;

    /// <summary>Keeps the Tapline stream connected, until cancelled.</summary>
    Task RunAsync(CancellationToken cancellationToken);

    /// <summary>Opens a control stream.</summary>
    Task<IControlStream<RouterMessage, ProviderMessage>> OpenControlAsync(CancellationToken cancellationToken);
}

/// <summary>
///     The link over MagicOnion: one gRPC channel to the provider's address, carrying the Tapline stream
///     (<see cref="ITaplineStream" />) and the control stream (<see cref="ITaplineProviderLink" />), each a duplex call.
/// </summary>
public sealed class MagicOnionProviderLink(GrpcChannel channel, TaplineStreamConnectionOptions? options = null)
    : IProviderLink
{
    private readonly TaplineStreamConnection _stream = new(channel, options);

    public TaplineClient Client => _stream.Client;

    public (int OutletId, uint Nonce)? Session => _stream.Bound;

    public event Action<int, uint>? Connected
    {
        add => _stream.Connected += value;
        remove => _stream.Connected -= value;
    }

    public Task RunAsync(CancellationToken cancellationToken)
    {
        return _stream.RunAsync(cancellationToken);
    }

    public async Task<IControlStream<RouterMessage, ProviderMessage>> OpenControlAsync(
        CancellationToken cancellationToken)
    {
        var call = await MagicOnionClient.Create<ITaplineProviderLink>(channel)
            .WithCancellationToken(cancellationToken).OpenAsync().ConfigureAwait(false);
        return new ClientControl(call);
    }

    private sealed class ClientControl(DuplexStreamingResult<RouterMessage, ProviderMessage> call)
        : IControlStream<RouterMessage, ProviderMessage>
    {
        public async ValueTask WriteAsync(RouterMessage message, CancellationToken cancellationToken)
        {
            await call.RequestStream.WriteAsync(message).ConfigureAwait(false);
        }

        public async IAsyncEnumerable<ProviderMessage> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
                yield return call.ResponseStream.Current;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await call.RequestStream.CompleteAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            } // (the call's gone already)

            call.Dispose();
        }
    }
}

/// <summary>
///     The control stream, the provider's side: one call per router. Its messages are handed to the provider's links
///     (<see cref="ProviderLinks" />), which keep each router's routes across its calls.
/// </summary>
public sealed class TaplineProviderLinkService(ProviderLinks links)
    : ServiceBase<ITaplineProviderLink>, ITaplineProviderLink
{
    public async Task<DuplexStreamingResult<RouterMessage, ProviderMessage>> OpenAsync()
    {
        var stream = GetDuplexStreamingContext<RouterMessage, ProviderMessage>();
        try
        {
            await links.ServeAsync(new ServerControl(stream), Context.CallContext.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (UnknownSessionException e) // (no session of the router's here yet: its call ended saying so, not as a failure)
        {
            Context.CallContext.Status = new Status(StatusCode.FailedPrecondition, e.Message);
        }

        return stream.Result();
    }

    private sealed class ServerControl(DuplexStreamingContext<RouterMessage, ProviderMessage> stream)
        : IControlStream<ProviderMessage, RouterMessage>
    {
        public async ValueTask WriteAsync(ProviderMessage message, CancellationToken cancellationToken)
        {
            await stream.WriteAsync(message).ConfigureAwait(false);
        }

        public async IAsyncEnumerable<RouterMessage> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (await stream.MoveNext(cancellationToken).ConfigureAwait(false)) yield return stream.Current;
        }

        public ValueTask DisposeAsync()
        {
            return default;
        }
    }
}
