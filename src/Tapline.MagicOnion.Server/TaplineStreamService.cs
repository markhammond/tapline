using Grpc.Core;
using MagicOnion;
using MagicOnion.Server;
using Microsoft.Extensions.Options;
using Tapline.AspNetCore;

namespace Tapline.MagicOnion.Server;

/// <summary>
///     The stream, the router's side: a call claims its session (or starts one), says which in its response headers, then
///     writes the session's frames while it reads what the client says — until the client ends the call or it drops. The
///     session is held for the next call (<see cref="TaplineStreamSessions" />).
/// </summary>
public sealed class TaplineStreamService(TaplineStreamSessions sessions, IOptions<TaplineMagicOnionOptions> options)
    : ServiceBase<ITaplineStream>, ITaplineStream
{
    public async Task<DuplexStreamingResult<Control, Frame>> OpenAsync()
    {
        var stream = GetDuplexStreamingContext<Control, Frame>();
        var call = Context.CallContext;
        var owner = new object();
        var session = sessions.Open(call.RequestHeaders.GetValue(TaplineStreamHeaders.Session), call.GetHttpContext(),
            owner);
        try
        {
            if (BandwidthLimits.Parse(call.RequestHeaders.GetValue(TaplineStreamHeaders.BandwidthLimit)) is { } rate)
            {
                var o = options.Value;
                if (o.StreamingBytesPerSecond > 0) rate = Math.Min(rate, o.StreamingBytesPerSecond); // lower only
                session.Outlet.SetStreamingRate(rate, Math.Min(rate, o.StreamingBurstBytes));
            }

            await call.WriteResponseHeadersAsync(new Metadata
            {
                { TaplineStreamHeaders.Session, TaplineStreamHeaders.Format(session.Outlet.Id, session.Outlet.Nonce) }
            });
            using var end = CancellationTokenSource.CreateLinkedTokenSource(call.CancellationToken);
            var pump = session.PumpAsync(stream, owner, end.Token);
            try
            {
                while (await stream.MoveNext(end.Token)) session.Apply(stream.Current);
            }
            catch (Exception) when (end.IsCancellationRequested || call.CancellationToken.IsCancellationRequested)
            {
            } // the call dropped
            catch (IOException)
            {
            }
            finally
            {
                end.Cancel();
                await pump.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
        finally
        {
            session.Release(owner);
        }

        return stream.Result();
    }
}