using ActivityFeed.Contracts;
using Tapline;

namespace ActivityFeed.Service;

/// <summary>
///     The access log at the posts themselves, for systems whose access log can't be worked out by deterministic replay:
///     an intent before each post to a person's topic goes out (whose topic, and the records it carries), and the post's
///     outcome after: applied by the router (the topic's version then), or refused (its whole state follows, intended
///     anew). A store of its own keeps both, apart from the operational one; on start, an intent with no outcome is
///     possibly shown (<see cref="ReconcileAsync" />). The practice works its access log out from the write log instead,
///     so its own (<see cref="ReplayedAccessLog" />) keeps nothing here.
/// </summary>
public interface IAccessLog
{
    /// <summary>Whether it wants each post's outcome: the router asked, for each, to say once it's applied.</summary>
    bool Outcomes { get; }

    /// <summary>
    ///     Before a post to <paramref name="person" />'s topic: the records it carries (an activity's, a section's, or the
    ///     topic's own rows), each with its change beside it (a row, or its deletion), or none (the topic's whole state).
    ///     An id for its outcome.
    /// </summary>
    long Intend(int person, string topic, ReadOnlySpan<FeedKey> keys, ReadOnlySpan<RowChange<FeedRow>> changes);

    /// <summary>The post applied by the router: the topic's version once it was.</summary>
    void Applied(long intent, long version);

    /// <summary>The post refused (it didn't fit its staging, or its topic is faulted): its whole state follows, intended anew.</summary>
    void Refused(long intent);

    /// <summary>On start, before any post: each intent with no outcome, recorded as possibly shown.</summary>
    ValueTask ReconcileAsync(CancellationToken ct);
}

/// <summary>
///     The practice's: its access log is worked out from the write log by every instance (deterministic replay), so nothing
///     is kept at the posts, and nothing asked of the router.
/// </summary>
public sealed class ReplayedAccessLog : IAccessLog
{
    public bool Outcomes => false;

    public long Intend(int person, string topic, ReadOnlySpan<FeedKey> keys, ReadOnlySpan<RowChange<FeedRow>> changes)
    {
        return 0;
    }

    public void Applied(long intent, long version)
    {
    }

    public void Refused(long intent)
    {
    }

    public ValueTask ReconcileAsync(CancellationToken ct)
    {
        return ValueTask.CompletedTask;
    }
}
