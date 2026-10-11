using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tapline.AspNetCore;

namespace BlazorRealtime;

public static class BlazorRealtimeEndpointExtensions
{
    /// <summary>
    ///     Maps Tapline's endpoints (<see cref="TaplineEndpointRouteBuilderExtensions.MapTapline" />: the out-of-band fetches
    ///     and the stale-data modules), and adds — at this point in the app's middleware, after the app's own — the circuit
    ///     sockets' handling: the compression cut-off (<see cref="BlazorRealtimeOptions.WebSocketCompressionThreshold" />) and
    ///     the opt-in kernel backlog caps (<see cref="BlazorRealtimeOptions.CircuitSendBufferBytes" />,
    ///     <see cref="BlazorRealtimeOptions.CircuitNotSentLowWatermarkBytes" />). That needs the app's builder (a
    ///     <c>WebApplication</c>); otherwise circuit sockets behave as in plain Blazor. Returns the group of Tapline's
    ///     endpoints.
    /// </summary>
    public static IEndpointConventionBuilder MapBlazorRealtime(this IEndpointRouteBuilder endpoints,
        string prefix = "/_tapline")
    {
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<BlazorRealtimeOptions>>().Value;
        if (endpoints is IApplicationBuilder app) CircuitSockets.Install(app, options);
        return endpoints.MapTapline(prefix);
    }
}