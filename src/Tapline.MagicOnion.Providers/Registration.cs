using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tapline.MagicOnion.Server;

namespace Tapline.MagicOnion.Providers;

/// <summary>A provider serving routers in other processes, and a router reaching one.</summary>
public static class TaplineProviderLinkExtensions
{
    /// <summary>
    ///     Serves <paramref name="provider" /> to routers in other processes: this process's router and its Tapline stream
    ///     (<see cref="TaplineMagicOnionExtensions.AddTaplineMagicOnion" />), which carries the topics, and the links
    ///     (<see cref="ProviderLinks" />), which carry the rest. MagicOnion and a port for it (HTTP/2) are the app's to add,
    ///     as for the stream. The stream's records travel in band up to a frame
    ///     (<see cref="OutletOptions.OutOfBandThreshold" /> 0, unless <paramref name="configure" /> says otherwise): a
    ///     router fetches nothing out of band over the link.
    /// </summary>
    /// <param name="endpoint">The provider's endpoint: the address the routers reach it at.</param>
    public static IServiceCollection AddTaplineProviderLink(this IServiceCollection services, ProviderEndpoint endpoint,
        Func<IServiceProvider, IDataProvider> provider, Action<TaplineMagicOnionOptions>? configure = null)
    {
        services.AddTaplineMagicOnion(o =>
        {
            o.OutOfBandThreshold = 0; // (in band whatever fits a frame: a table's snapshot of more than 16 KB, say)
            configure?.Invoke(o);
        });
        services.AddSingleton(sp => new ProviderLinks(provider(sp), endpoint,
            sp.GetRequiredService<TaplineStreamSessions>().Find, sp.GetService<ILogger<ProviderLinks>>()));
        return services;
    }

    /// <summary>Maps the Tapline stream and the links' control stream (<see cref="ITaplineProviderLink" />).</summary>
    public static IEndpointConventionBuilder MapTaplineProviderLink(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapTaplineMagicOnion();
        return endpoints.MapMagicOnionService(typeof(TaplineProviderLinkService));
    }

    /// <summary>
    ///     Registers the provider at <paramref name="address" /> for <paramref name="service" />, reached over MagicOnion: one
    ///     gRPC channel to it, carrying its topics' stream and its control stream. Its endpoint is the address. Dispose the
    ///     provider when the router stops. A provider not up yet (blue/green's green, registered ahead of it) is reached as
    ///     soon as it is: the channel's reconnect backoff is the link's own, from a quarter of a second up to five (gRPC's own
    ///     grows to two minutes), unless <paramref name="channel" /> says otherwise.
    /// </summary>
    public static RemoteProvider ProvideRemote(this Publisher publisher, string service, Uri address,
        GrpcChannelOptions? channel = null, TaplineStreamConnectionOptions? stream = null, ILogger? log = null)
    {
        var endpoint = new ProviderEndpoint(address);
        var provider = new RemoteProvider(endpoint,
            new MagicOnionProviderLink(GrpcChannel.ForAddress(address, channel ?? new GrpcChannelOptions
            {
                InitialReconnectBackoff = TimeSpan.FromMilliseconds(250), MaxReconnectBackoff = TimeSpan.FromSeconds(5)
            }), stream), log);
        publisher.Provide(service, endpoint, provider);
        return provider;
    }

    /// <summary>
    ///     Registers the provider at <paramref name="address" /> as an authentication provider of <paramref name="realm" />,
    ///     reached over MagicOnion as <see cref="ProvideRemote" />'s are: a client's sign-in to the realm is relayed to it.
    ///     A provider that serves services too is registered for them with <see cref="ProvideRemote" />, and then for its
    ///     realm with <see cref="Publisher.ProvideAuthentication" /> and the <see cref="RemoteProvider" /> that returned (one
    ///     link to an endpoint).
    /// </summary>
    public static RemoteProvider ProvideRemoteAuthentication(this Publisher publisher, string realm, Uri address,
        GrpcChannelOptions? channel = null, TaplineStreamConnectionOptions? stream = null, ILogger? log = null)
    {
        var endpoint = new ProviderEndpoint(address);
        var provider = new RemoteProvider(endpoint,
            new MagicOnionProviderLink(GrpcChannel.ForAddress(address, channel ?? new GrpcChannelOptions
            {
                InitialReconnectBackoff = TimeSpan.FromMilliseconds(250), MaxReconnectBackoff = TimeSpan.FromSeconds(5)
            }), stream), log);
        publisher.ProvideAuthentication(realm, endpoint, provider);
        return provider;
    }
}
