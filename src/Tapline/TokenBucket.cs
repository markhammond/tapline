namespace Tapline;

/// <summary>
///     Token bucket with overdraft, refilled by calculation rather than by a timer. One large item (a snapshot, a long
///     fold) may take the balance negative; the holder is then ineligible until elapsed time has repaid it — the same
///     "debt" rule as the scheduler's deficit round robin, so long-run rates hold without knowing item sizes up front.
///     Fixed point (tokens × timestamp frequency), so frequent refills lose nothing to rounding. One owner; not
///     thread-safe. Used for per-outlet Streaming bandwidth; equally for per-user fuel.
/// </summary>
public struct TokenBucket
{
    private long _cap, _balance, _stamp;
    private readonly long _frequency;

    /// <param name="ratePerSecond">Refill rate.</param>
    /// <param name="burst">Capacity: what an idle holder may spend at once (size it to at least the largest snapshot).</param>
    /// <param name="now">Current timestamp (<see cref="TimeProvider.GetTimestamp" />).</param>
    /// <param name="timestampFrequency"><see cref="TimeProvider.TimestampFrequency" />.</param>
    public TokenBucket(long ratePerSecond, long burst, long now, long timestampFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ratePerSecond, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(burst, 1);
        _frequency = timestampFrequency;
        RatePerSecond = ratePerSecond;
        _cap = _balance = checked(burst * timestampFrequency);
        _stamp = now;
    }

    public long RatePerSecond { get; private set; }

    public readonly long Burst => _cap / _frequency;

    /// <summary>Whole tokens as of the last refill; negative while in debt.</summary>
    public readonly long Tokens => _balance / _frequency;

    /// <summary>Eligible to spend (any positive fraction counts, consistent with <see cref="TicksUntilPositive" />).</summary>
    public readonly bool IsPositive => _balance > 0;

    /// <summary>Timestamp ticks until <see cref="IsPositive" /> (0 if it already is): when to wake the holder.</summary>
    public readonly long TicksUntilPositive => _balance > 0 ? 0 : -_balance / RatePerSecond + 1;

    /// <summary>Credits the time elapsed since the last refill, up to <see cref="Burst" />. Returns <see cref="Tokens" />.</summary>
    public long Refill(long now)
    {
        if (now > _stamp)
        {
            var elapsed =
                Math.Min(now - _stamp, (_cap - _balance) / RatePerSecond + 1); // enough to fill; bounds the product
            _balance = Math.Min(_cap, _balance + elapsed * RatePerSecond);
            _stamp = now;
        }

        return Tokens;
    }

    public void Spend(long tokens)
    {
        _balance -= tokens * _frequency;
    }

    /// <summary>Changes rate and capacity (e.g. a user's tier), keeping what was earned at the old rate.</summary>
    public void Reconfigure(long ratePerSecond, long burst, long now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ratePerSecond, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(burst, 1);
        Refill(now);
        RatePerSecond = ratePerSecond;
        _cap = checked(burst * _frequency);
        _balance = Math.Min(_balance, _cap);
    }
}