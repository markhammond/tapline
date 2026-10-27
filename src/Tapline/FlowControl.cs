using System.Numerics;

namespace Tapline;

/// <summary>How an outlet limits what it has in flight to its client (see <see cref="OutletOptions.FlowControl" />).</summary>
public enum FlowControlMode
{
    /// <summary>
    ///     Model-based, after BBR, steered to the shortest queue rather than the highest throughput. From every frame's ack it
    ///     measures the round trip — from the instant the frame was bound, so every buffer on the way (server pipes, a
    ///     middlebox, the network) is in it — and the delivery rate: a frame sent with nothing in flight measures from its
    ///     own send, not from the last ack before an idle spell; an ack is sampled from the newest data frame it brings home,
    ///     not the heartbeats behind it; and a round of heartbeats alone (nothing to send) leaves the rounds that measured
    ///     the path in the estimate. The bytes in flight are capped at twice rate × minimum
    ///     round trip (taken as at least 5 ms: below that a busy thread looks like a queue) — headroom while the application
    ///     is the limit, when frames go at once, unpaced, whole; while ready data is held back the cap doubles each round trip
    ///     (startup). Data frames are spaced at least minimum round trip ÷ half the <see cref="OutletOptions.Window" /> apart:
    ///     a long path gets an even stream, not a window's burst per round trip. Once the path shows itself to be the
    ///     bottleneck — a standing queue (the round's best round trip, allowing for a larger frame's transmission time, above
    ///     the minimum by more than <see cref="OutletOptions.QueueDelayTarget" />), or a rate that stopped growing — frames
    ///     are
    ///     paced at the rate, each bound as the path frees up, and sized to half its bandwidth × delay; while data is held
    ///     back
    ///     it probes a quarter faster one round in eight, eases off a quarter after a probe or a queue, and drains at half if
    ///     a
    ///     queue survives that. A frame back with its round trip tripled (the path slowed) holds everything: one more frame
    ///     goes behind the backlog, none until it is back, and the rate it measured — the backlog drained ahead of it — caps
    ///     the estimate at once. A stale minimum (10 s) is re-measured: by the next round's best when nothing was held back,
    ///     else by sending at most half the bandwidth × delay for a round.
    /// </summary>
    Adaptive,

    /// <summary>
    ///     At most <see cref="OutletOptions.Window" /> data frames unacked, unpaced, frames up to
    ///     <see cref="OutletOptions.FrameBudget" />.
    /// </summary>
    Fixed
}

/// <summary>
///     The adaptive controller: per-frame send records (a ring indexed by frame sequence number), the path estimates and
///     the
///     resulting pacing rate, in-flight limit and frame size. Frames are recorded when built and released by the client's
///     cumulative acks, under one short lock; the outlet also reports when this controller held back data that was ready
///     (<see cref="MarkLimited" />) — the "wanted more" signal: a frame that merely took what had arrived isn't one.
///     Allocation-free.
/// </summary>
internal sealed class FlowControl
{
    private const int MinFrame = 512, MinLimit = MinFrame, BandwidthRounds = 10;

    // Round trips under this don't tell a queue from a busy thread (scheduling, GC, a browser's main thread): the model's
    // base round trip is at least this — for queue thresholds and bandwidth × delay, so a colo client's limit covers a few
    // ms of jitter at its full rate (and a queue there is at most ~2× this). Frame spacing uses the true minimum.
    private const int RttFloorMs = 5;

    // A collapse is the path slowing down for good: at least this much round trip unexplained, ack after ack. Less is
    // a busy client (a browser's main thread decoding, drawing, collecting garbage), which a hold would misread.
    private const int CollapseFloorMs = 100;
    private const int PacedRounds = 8; // rounds paced after the path last showed itself the bottleneck
    private const int ProbeEvery = 8; // rounds between probes for more bandwidth while paced
    private const double DrainGain = 0.5, ProbeGain = 1.25, AfterProbeGain = 0.75;

    /// <summary>How long, at most, High should wait behind what the other classes already have in flight.</summary>
    public const int HighWaitMs = 50;

    private readonly long[] _bandwidth = new long[BandwidthRounds];

    private readonly long _frequency,
        _target,
        _initialLimit,
        _maxLimit,
        _minRttWindow,
        _slack,
        _rttFloor,
        _collapseFloor,
        _overdue;

    private readonly Lock _lock = new();
    private readonly int _mask, _maxFrame, _spacingFrames;
    private readonly SentFrame[] _sent;
    private int _behind; // consecutive acks far behind (see Release)
    private long _deliveredSentAt; // when the newest delivered frame was sent
    private int _frameBudget;
    private double _gain;
    private bool _hold; // a frame came back far behind: one more, then none until it's back
    private bool _holdToken; // …that one more
    private long _inflight, _delivered, _deliveredTime;
    private long _jitter; // path jitter: the excess of frames that had nothing of ours ahead (smoothed)
    private int _lastSentBytes, _quantum, _roundMaxFrame; // the send quantum: the largest data frame lately (see Apply)
    private long _limit, _pacingRate, _nextSendAt, _spacedUntil, _lastSentAt;
    private bool _limited; // held back data that was ready, since the round began
    private bool _roundData; // a rate sample from a data frame, since the round began
    private long _minRtt, _minRttAt, _srtt;
    private int _minRttBytes; // the size of the frame that measured it

    private long _oldest, _next; // seqs: oldest unacked, next to be recorded
    private long _oldestSentAt; // when the oldest unacked frame was sent (0: none in flight)
    private Phase _phase, _phaseBeforeProbe;
    private long _round, _roundStart, _roundMinRtt, _roundExcess, _roundRate, _fullBandwidth, _probedAt, _probeBase;
    private int _roundMinRttBytes, _fullRounds, _pacedRounds, _easedRounds;
    private long _wireRate; // the bandwidth estimate in force, B/s (for HasRoomBesideHigh)

    public FlowControl(OutletOptions options, long frequency, long wheelTick)
    {
        _sent = new SentFrame[(int)BitOperations.RoundUpToPowerOf2((uint)options.Window + 4)];
        _mask = _sent.Length - 1;
        _maxFrame = options.FrameBudget;
        _spacingFrames = Math.Max(1, options.Window / 2);
        _frequency = frequency;
        _target = (long)(options.QueueDelayTarget.TotalSeconds * frequency);
        _initialLimit = Math.Max(MinLimit, options.InitialInflightBytes);
        _maxLimit = Math.Max(_initialLimit, (long)options.Window * options.MaxFrame);
        _minRttWindow = 10 * frequency;
        _rttFloor = frequency * RttFloorMs / 1000;
        _collapseFloor = frequency * CollapseFloorMs / 1000;
        _overdue = (long)(options.AckOverdue.TotalSeconds * frequency);
        _slack = wheelTick;
        Reset(1, 0);
    }

    public long Inflight => Volatile.Read(ref _inflight);
    public long Limit => Volatile.Read(ref _limit);
    public long PacingRate => Volatile.Read(ref _pacingRate);
    public long MinRtt => Volatile.Read(ref _minRtt) is var m && m == long.MaxValue ? 0 : m;
    public long SmoothedRtt => Volatile.Read(ref _srtt);

    public long DeliveryRate
    {
        get
        {
            lock (_lock)
            {
                return Bandwidth();
            }
        }
    }

    public int FrameBudget => Volatile.Read(ref _frameBudget);
    public long NextSendAt => Volatile.Read(ref _nextSendAt);

    /// <summary>When the next data frame may go, by spacing (see <see cref="FlowControlMode.Adaptive" />).</summary>
    public long SpacedUntil => Volatile.Read(ref _spacedUntil);

    /// <summary>
    ///     Room for another data frame: under the limit (one frame may overshoot it); nothing in flight always has room, and
    ///     a hold lets exactly one more go at once.
    /// </summary>
    public bool HasRoom => Volatile.Read(ref _inflight) is var inflight &&
                           (inflight == 0 || inflight < Volatile.Read(ref _limit) || Volatile.Read(ref _holdToken));

    /// <summary>
    ///     Room for another data frame of the classes that make way for High while it is active. Where one of their frames
    ///     (the send quantum: the largest data frame lately) takes longer than <see cref="HighWaitMs" /> to send — a slow path
    ///     —
    ///     it must fit under the limit, so High's next record finds room at once, and no more than HighWaitMs of the path may
    ///     already be in flight: one frame at a time, sent only once what went before is through, so a High record waits
    ///     behind at most that one. Elsewhere one frame holds High up for less than that: nothing changes. Nothing in flight
    ///     always has room; so does a hold.
    /// </summary>
    public bool HasRoomBesideHigh => Volatile.Read(ref _inflight) is var inflight &&
                                     Volatile.Read(ref _wireRate) is var rate
                                     && (inflight == 0 || rate == 0 || Volatile.Read(ref _holdToken)
                                         || (Volatile.Read(ref _quantum) is var quantum &&
                                             (quantum * 1000L <= rate * HighWaitMs
                                              || (inflight + quantum < Volatile.Read(ref _limit) &&
                                                  inflight * 1000L <= rate * HighWaitMs))));

    /// <summary>
    ///     A frame has waited for its acknowledgement longer than <see cref="OutletOptions.AckOverdue" /> and three smoothed
    ///     round trips (a path with a queue, or a long one, acks that much later as a matter of course): its client is presumed
    ///     busy. Off (false) when that option is zero.
    /// </summary>
    public bool AckOverdue(long now)
    {
        if (_overdue <= 0) return false;
        var sent = Volatile.Read(ref _oldestSentAt);
        return sent != 0 && now - sent > Math.Max(_overdue, 3 * Volatile.Read(ref _srtt));
    }

    /// <summary>The minimum round trip, at least <see cref="RttFloorMs" />; 0 before the first.</summary>
    private long BaseRtt => _minRtt == long.MaxValue ? 0 : Math.Max(_minRtt, _rttFloor);

    /// <summary>A new connection: nothing in flight, estimates start over.</summary>
    public void Reset(long nextSeq, long now)
    {
        lock (_lock)
        {
            _oldest = _next = nextSeq;
            Volatile.Write(ref _oldestSentAt, 0);
            _inflight = _delivered = 0;
            _deliveredTime = now;
            _minRtt = long.MaxValue;
            _minRttBytes = 0;
            _minRttAt = now;
            _srtt = _jitter = 0;
            _behind = 0;
            _deliveredSentAt = now;
            _round = _roundStart = 0;
            StartRound();
            _fullBandwidth = _probedAt = _probeBase = 0;
            _fullRounds = _pacedRounds = _easedRounds = 0;
            _hold = _holdToken = false;
            _wireRate = 0;
            _phase = Phase.Startup;
            Array.Clear(_bandwidth);
            _limit = _initialLimit;
            _pacingRate =
                _nextSendAt =
                    _spacedUntil = _lastSentAt = 0; // unpaced until the path shows itself to be the bottleneck
            _lastSentBytes = _quantum = _roundMaxFrame = 0;
            _gain = 1;
            _frameBudget = (int)Math.Clamp(_limit / 2, MinFrame, _maxFrame); // a path of unknown speed: small frames
        }
    }

    /// <summary>The pacing gate is open (within one wheel tick: the wheel can't wake the outlet any sooner).</summary>
    public bool PaceAllows(long now)
    {
        return now + _slack >= Volatile.Read(ref _nextSendAt);
    }

    /// <summary>Data was ready but this controller (room or pacing) held it back: the path, not the application, is the limit.</summary>
    public void MarkLimited()
    {
        Volatile.Write(ref _limited, true);
    }

    /// <summary>
    ///     A frame was built. <paramref name="data" />: not heartbeat-only (those aren't spaced);
    ///     <paramref name="tookEverything" />:
    ///     nothing was left ready after it.
    /// </summary>
    public void OnSent(long seq, int bytes, bool data, bool tookEverything, long now)
    {
        lock (_lock)
        {
            if (seq != _next) return; // not ours to track (a stale owner, or a reset in between)
            if (_next - _oldest > _mask)
                Release(_oldest + 1, now); // can't happen within the window; never overrun the ring
            if (_inflight == 0)
                // Nothing in flight: its ack measures the path from now, not from the last ack before an idle spell (a
                // heartbeat after a quiet second would read 13 B/s) — and so does what goes after it before that ack.
                (_deliveredTime, _deliveredSentAt) = (now, now);
            ref var f = ref _sent[seq & _mask];
            f.SentAt = now;
            f.Bytes = bytes;
            f.DeliveredAtSend = _delivered;
            f.DeliveredTimeAtSend = _deliveredTime;
            f.InflightAtSend = _inflight; // ahead of it on the path
            f.FirstSentAtSend = _deliveredSentAt;
            f.AppLimited = !data || (tookEverything && !_limited); // a rate sample from it can only raise the estimate
            f.Data = data;
            if (_next == _oldest) Volatile.Write(ref _oldestSentAt, now); // the only one in flight: the oldest
            _next = seq + 1;
            Volatile.Write(ref _inflight, _inflight + bytes);
            if (data) Volatile.Write(ref _holdToken, false);
            if (_pacingRate > 0)
                Volatile.Write(ref _nextSendAt, Math.Max(now, _nextSendAt) + bytes * _frequency / _pacingRate);
            _lastSentAt = now;
            _lastSentBytes = bytes;
            if (data)
            {
                _roundMaxFrame = Math.Max(_roundMaxFrame, bytes);
                if (_minRtt != long.MaxValue) Volatile.Write(ref _spacedUntil, now + _minRtt / _spacingFrames);
            }
        }
    }

    /// <summary>The client has every frame up to <paramref name="seq" />: release them and update the estimates.</summary>
    public void OnAck(long seq, long now)
    {
        lock (_lock)
        {
            if (seq < _oldest) return;
            Release(Math.Min(seq, _next - 1) + 1, now);
        }
    }

    private void Release(long upTo, long now)
    {
        if (upTo <= _oldest) return;
        long bytes = 0, sampled = upTo - 1; // (the frame the rate is sampled from: see below)
        ref var newest = ref _sent[(upTo - 1) & _mask];
        for (var s = _oldest; s < upTo; s++)
        {
            ref var f = ref _sent[s & _mask];
            bytes += f.Bytes;
            if (f.Data) sampled = s;
        }
        _oldest = upTo;
        Volatile.Write(ref _oldestSentAt, upTo < _next ? _sent[upTo & _mask].SentAt : 0);
        Volatile.Write(ref _inflight, Math.Max(0, _inflight - bytes));
        _delivered += bytes;
        _deliveredTime = now;

        long rtt = Math.Max(1, now - newest.SentAt), bandwidth = Bandwidth();
        _srtt = _srtt == 0 ? rtt : _srtt + (rtt - _srtt) / 8;
        if (rtt < _minRtt)
        {
            Volatile.Write(ref _minRtt, rtt);
            _minRttBytes = newest.Bytes;
            _minRttAt = now;
        }

        if (rtt < _roundMinRtt)
        {
            _roundMinRtt = rtt;
            _roundMinRttBytes = newest.Bytes;
        }

        // In excess of the minimum, less what a larger frame takes to transmit — at a rate that, while the application is
        // the limit, understates the path's: a generous allowance, so frame sizes alone never look like a queue.
        long larger = newest.Bytes - _minRttBytes;
        var excess = rtt - _minRtt - (larger > 0 && bandwidth > 0 ? larger * _frequency / bandwidth : 0);
        _roundExcess = Math.Min(_roundExcess, excess);
        // A frame sent with nothing of ours in flight can't have queued behind us: its excess is the path's own jitter (a
        // link that delivers in bursts — cellular, Wi-Fi, a throttle — or cross traffic). Thresholds tolerate that much.
        if (newest.InflightAtSend == 0) _jitter += (Math.Max(0, excess) - _jitter) / 8;

        // Over the longer of the send and the ack intervals (as BBR): acks bunched together — by a link that delivers in
        // bursts, or a busy client — would otherwise look like a faster path. Sampled from the newest data frame released,
        // not a heartbeat sent behind it: on a path where a data frame takes longer than the heartbeat interval (seconds, on
        // 16 kbit/s), the ack that brings one home brings the heartbeats queued after it too — application-limited all —
        // and a sample from those would never measure the path.
        ref var sample = ref _sent[sampled & _mask];
        var interval = Math.Max(1, Math.Max(now - sample.DeliveredTimeAtSend, sample.SentAt - sample.FirstSentAtSend));
        var rate = (_delivered - sample.DeliveredAtSend) * _frequency / interval;
        _deliveredSentAt = newest.SentAt;
        if (!sample.AppLimited || rate > bandwidth) _roundRate = Math.Max(_roundRate, rate);
        _roundData |= sample.Data;

        // Far behind — startup's burst overshot (a frame queued behind more than a frame of its own), or the path slowed
        // down (round trips tripled, beyond what the bytes ahead of them and the path's jitter explain — a small frame
        // behind one of our own large ones isn't that, nor an unlucky tick: three acks in a row): stop adding to the queue
        // at once rather than at the round's end, which behind a deep queue may be seconds away.
        long baseRtt = BaseRtt,
            quantum = bandwidth > 0 ? Math.Max(_quantum, _roundMaxFrame) * _frequency / bandwidth : 0;
        var unexplained = excess - (bandwidth > 0 ? newest.InflightAtSend * _frequency / bandwidth : 0);
        _behind = unexplained > _target + Math.Max(2 * baseRtt, _collapseFloor) + 2 * _jitter ? _behind + 1 : 0;
        if (!_hold && _phase != Phase.ProbeRtt
                   && (_phase == Phase.Startup ? excess > _target + baseRtt / 4 + quantum + _jitter : _behind >= 3))
        {
            Hold();
            Apply(bandwidth);
            _roundStart = _delivered;
            StartRound();
            return;
        }

        if (newest.DeliveredAtSend >= _roundStart)
            EndRound(now, rate, _roundData); // a frame sent after the round began came back: a round trip
    }

    /// <summary>
    ///     Stop adding to a queue and measure the path behind it: one more frame goes at once, behind everything in flight,
    ///     then none until it is back — by when the backlog ahead of it has drained, so the rate it measured is the path's
    ///     (see <see cref="EndRound" />).
    /// </summary>
    private void Hold()
    {
        EnterDrain();
        _hold = true;
        _behind = 0;
        Volatile.Write(ref _holdToken, true);
    }

    /// <param name="rate">
    ///     The delivery rate the ack that ended the round measured (a frame sent after the round began came back): from the
    ///     newest data frame it released, else its newest frame.
    /// </param>
    /// <param name="data">The round measured data: a rate sampled from a data frame, not from heartbeats alone.</param>
    private void EndRound(long now, long rate, bool data)
    {
        // A round of heartbeats alone — nothing to send — measured nothing of the path: it doesn't push a round that did out
        // of the window, or seconds of heartbeats alone would leave the next burst an estimate of the heartbeats.
        if (data) _bandwidth[_round % BandwidthRounds] = _roundRate;
        var bandwidth = Bandwidth();
        var limited = Volatile.Read(ref _limited);
        var queue = _roundExcess >
                    _target + Math.Max(BaseRtt / 16, _jitter); // a standing queue, beyond the target and the jitter
        var bdp = bandwidth * BaseRtt / _frequency;

        switch (_phase)
        {
            case Phase.ProbeRtt: // half the pipe for a round: its best round trip is the base again
                Rebase();
                _phase = _phaseBeforeProbe;
                break;
            case Phase.Startup:
                if (bandwidth >= _fullBandwidth * 5 / 4)
                {
                    _fullBandwidth = bandwidth;
                    _fullRounds = 0;
                }
                else if (limited)
                {
                    _fullRounds++;
                }

                if (queue || _fullRounds >= 3)
                    EnterDrain(); // a queue, or held back yet no faster for 3 rounds: the pipe is full
                else
                    _limit = Math.Clamp(2 * bdp, _limit,
                        _maxLimit); // grows as fast as deliveries do: doubles while held back
                break;
            case Phase.Drain:
                if (_hold) // the held frame is back, the backlog drained ahead of it: its rate is the path's
                {
                    // (less the propagation in it: trusted to cut the estimate at least in half)
                    if (rate < bandwidth / 2) bandwidth = CapBandwidth(rate);
                    _hold = false;
                    _probedAt = _round - ProbeEvery; // the backlog may have been someone else's: probe again soon
                }
                else if (!queue)
                {
                    _phase = Phase.Steady;
                    _gain = 1;
                }
                else
                {
                    Hold(); // still queued at half the estimate: it's too high — measure the path
                }

                break;
            case Phase.Steady:
                if (_gain > 1) // a probe round (its queue is its own)
                {
                    if (bandwidth > _probeBase * 9 / 8)
                    {
                        _probeBase = bandwidth; // it found more: probe again
                    }
                    else
                    {
                        _gain = AfterProbeGain;
                        _easedRounds = 1;
                    } // ease off to drain what it queued
                }
                else if (queue)
                {
                    _pacedRounds = PacedRounds;
                    if (++_easedRounds > 2) EnterDrain(); // eased off two rounds, still queued: drain at half
                    else _gain = AfterProbeGain; // ease off by a quarter
                }
                else if (limited && _pacingRate > 0 &&
                         _round - _probedAt >= ProbeEvery) // paced and wanting more: probe for a round
                {
                    _gain = ProbeGain;
                    _probedAt = _round;
                    _probeBase = bandwidth;
                }
                else
                {
                    _gain = 1;
                    _easedRounds = 0;
                }

                break;
        }

        if (_phase is Phase.Steady && _pacedRounds > 0 && !limited)
            _pacedRounds--; // the application is the limit again

        if (_phase != Phase.ProbeRtt && now - _minRttAt > _minRttWindow) // stale minimum
        {
            if (!limited)
            {
                Rebase(); // nothing held back: no queue of our own in this round's best
            }
            else
            {
                _phaseBeforeProbe = _phase == Phase.Startup ? Phase.Steady : _phase;
                _phase = Phase.ProbeRtt;
            }
        }

        _quantum = Math.Max(_roundMaxFrame, _quantum - _quantum / 8); // decays over rounds without such frames
        Apply(bandwidth);
        _round++;
        _roundStart = _delivered;
        StartRound();
    }

    /// <summary>
    ///     The limit, pacing rate and frame size for the phase and estimates. In steady state the limit is at least one send
    ///     quantum — the largest data frame lately — over the bandwidth × delay, so a record bigger than that (a large value,
    ///     a snapshot) neither leaves the path idle behind it nor everything else waiting for its ack.
    /// </summary>
    private void Apply(long bandwidth)
    {
        long bdp = bandwidth * BaseRtt / _frequency, quantum = _quantum;
        if (bdp > 0)
            _limit = _phase switch
            {
                Phase.Drain => _hold ? MinLimit : Math.Clamp(bdp, MinLimit, _maxLimit),
                Phase.Steady => Math.Clamp(Math.Max(2 * bdp, bdp + quantum), MinLimit, _maxLimit),
                Phase.ProbeRtt => Math.Max(MinLimit, bdp / 2),
                _ => _limit
            };
        var paced = bandwidth > 0 && (_phase == Phase.Drain || _pacedRounds > 0);
        var rate = paced ? (long)(_gain * bandwidth) : 0;
        Volatile.Write(ref _limit, _limit);
        Volatile.Write(ref _pacingRate, rate);
        Volatile.Write(ref _wireRate, bandwidth);
        if (!paced) Volatile.Write(ref _nextSendAt, 0);
        else if (_lastSentAt != 0) // the gate as the new rate has it: a slower rate's wait isn't kept
            Volatile.Write(ref _nextSendAt, Math.Min(_nextSendAt, _lastSentAt + _lastSentBytes * _frequency / rate));
        // Paced: half the bandwidth × delay (fresh data, short waits for heartbeats). Otherwise whole: half the limit.
        Volatile.Write(ref _frameBudget, (int)Math.Clamp(paced && bdp > 0 ? bdp / 2 : _limit / 2, MinFrame, _maxFrame));
    }

    private void EnterDrain()
    {
        _phase = Phase.Drain;
        _gain = DrainGain;
        _easedRounds = 0;
        _pacedRounds = PacedRounds;
    }

    /// <summary>
    ///     A rate measured behind a backlog — the path was busy all the while — is what it can deliver: if less than the
    ///     estimate, the path got slower. Forget the faster rounds now rather than queue at the old rate until they age out;
    ///     the minimum round trip's frame now takes longer to send, so its expected round trip grows to match.
    /// </summary>
    private long CapBandwidth(long rate)
    {
        var before = Bandwidth();
        if (rate <= 0) return before;
        rate = Math.Max(rate, MinFrame); // 512 B/s: as slow as anything is worth streaming to
        for (var i = 0; i < _bandwidth.Length; i++) _bandwidth[i] = Math.Min(_bandwidth[i], rate);
        var after = Bandwidth();
        if (after < before && after > 0 && _minRtt != long.MaxValue)
            Volatile.Write(ref _minRtt,
                _minRtt + _minRttBytes * _frequency / after - _minRttBytes * _frequency / before);
        return after;
    }

    /// <summary>The minimum round trip is this round's best.</summary>
    private void Rebase()
    {
        if (_roundMinRtt == long.MaxValue) return;
        Volatile.Write(ref _minRtt, _roundMinRtt);
        _minRttBytes = _roundMinRttBytes;
        _minRttAt = Volatile.Read(ref _deliveredTime);
    }

    private void StartRound()
    {
        _roundMinRtt = _roundExcess = long.MaxValue;
        _roundMinRttBytes = 0;
        _roundRate = 0;
        _roundMaxFrame = 0;
        _roundData = false;
        Volatile.Write(ref _limited, false);
    }

    private long Bandwidth()
    {
        long max = 0;
        foreach (var b in _bandwidth) max = Math.Max(max, b);
        return max;
    }

    private enum Phase
    {
        Startup,
        Drain,
        Steady,
        ProbeRtt
    }

    private struct SentFrame
    {
        public long SentAt, DeliveredAtSend, DeliveredTimeAtSend, InflightAtSend, FirstSentAtSend;
        public int Bytes;
        public bool AppLimited, Data;
    }
}