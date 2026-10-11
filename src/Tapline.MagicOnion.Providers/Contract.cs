using MagicOnion;

namespace Tapline.MagicOnion.Providers;

/// <summary>
///     A provider in another process, as a router reaches it: a duplex call per router, its control stream — the router's
///     step 1s, notices and health checks one way, the provider's answers, mailboxes, heartbeats and drains the other. The
///     topics themselves come on a Tapline stream of their own (<see cref="ITaplineStream" />): the router is a client of
///     the provider's own router, and mirrors each topic it accepts onto into one of its own.
/// </summary>
public interface ITaplineProviderLink : IService<ITaplineProviderLink>
{
    /// <summary>Opens the control stream: the router's messages in, the provider's out.</summary>
    Task<DuplexStreamingResult<RouterMessage, ProviderMessage>> OpenAsync();
}
