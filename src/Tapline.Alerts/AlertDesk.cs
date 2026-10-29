using System.Runtime.InteropServices;

namespace Tapline.Alerts;

/// <summary>What an <see cref="AlertDesk" /> holds at most, and for how long.</summary>
public sealed class AlertDeskOptions
{
    public static readonly AlertDeskOptions Default = new();

    /// <summary>The most rules an audience keeps: a rule past it is refused, saying so.</summary>
    public int MaxRulesPerAudience { get; init; } = 32;

    /// <summary>The most distinct shapes (each a standing evaluation, and a source) across every audience: a rule's past it, refused.</summary>
    public int MaxWatches { get; init; } = 256;

    /// <summary>The most audiences held: one more, its rules refused.</summary>
    public int MaxAudiences { get; init; } = 1_000;

    /// <summary>How long an audience no page has open is kept, its rules evaluated, before it's let go, rules and all.</summary>
    public TimeSpan KeepFor { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How often audiences unattended past <see cref="KeepFor" /> are looked for.</summary>
    public TimeSpan SweepEvery { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Why these can't be: else null.</summary>
    public string? Broken()
    {
        if (MaxRulesPerAudience < 1 || MaxWatches < 1 || MaxAudiences < 1) return "An alert desk holds at least one rule, shape and audience.";
        if (KeepFor < TimeSpan.Zero || SweepEvery <= TimeSpan.Zero) return "An alert desk keeps for no less than nothing, and sweeps now and then.";
        return null;
    }
}

/// <summary>
///     ② Rules and their state, and ③ their delivery (virtual-grid.md §15.2–15.3), on Tapline's two strengths, as the owner
///     put them (2026-10-29): "alerts can be shared if they have the same shape" and "alerts can be conflated (they should
///     be debounced according to minimum interval)". Each distinct shape — a source, a condition and a trigger — is one
///     evaluation, given to the evaluator once, and one source (<see cref="AlertFeedValue" />): its running count, when it
///     last fired, its latest row — published at every firing, and fanned out to every page of every audience whose rules
///     have that shape. Each page takes each rule's shape no oftener than the rule's throttle (<c>Tap.MinInterval</c>),
///     whatever fired meanwhile conflated into it, its count up by as many. Per audience — a browser, or a user — an inbox,
///     a row per rule (<see cref="AlertRow" />): its name, sound and mute, and how far the audience has presented its
///     shape's count. Generic: it knows nothing of grids or databases.
/// </summary>
/// <remarks>
///     A page presents the difference between the count it last presented and its shape's, and says back what it
///     presented (<see cref="AlertPage.Presented" />): the rule's row keeps the most any page says, so a page opened later
///     presents only what fired meanwhile, its first records saying it. A rule added, or its shape changed, starts from
///     the count as it stands. An audience no page has open is kept for <see cref="AlertDeskOptions.KeepFor" />, its rules
///     evaluated, then let go. Rules per audience, distinct shapes and audiences are capped, and a rule over a cap refused,
///     saying so: so nothing fired is ever let go. Every clock it reads is one <see cref="TimeProvider" />.
/// </remarks>
public sealed class AlertDesk : IAlertSink, IAsyncDisposable
{
    private readonly Dictionary<string, Audience> _audiences = new(StringComparer.Ordinal);
    private readonly IAlertEvaluator _evaluator;
    private readonly Dictionary<AlertWatch, Feed> _feeds = [];
    private readonly Lock _gate = new();
    private readonly AlertDeskOptions _options;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly SemaphoreSlim _watching = new(1, 1); // (one WatchAsync at a time, the last set in force)
    private bool _disposed;
    private IReadOnlyDictionary<AlertWatch, string> _refused = new Dictionary<AlertWatch, string>();

    public AlertDesk(IAlertEvaluator evaluator, AlertDeskOptions? options = null, TimeProvider? time = null)
    {
        _options = options ?? AlertDeskOptions.Default;
        if (_options.Broken() is { } broken) throw new ArgumentException(broken, nameof(options));
        (_evaluator, _time) = (evaluator, time ?? TimeProvider.System);
        _timer = _time.CreateTimer(static s => ((AlertDesk)s!).Sweep(), this, _options.SweepEvery, Timeout.InfiniteTimeSpan); // (set again each sweep)
    }

    /// <summary>The audiences held, those with a page open among them.</summary>
    public (int Audiences, int Attended) Audiences
    {
        get
        {
            lock (_gate) return (_audiences.Count, _audiences.Values.Count(a => a.Pages.Count > 0));
        }
    }

    /// <summary>The distinct shapes the rules held have: what the evaluator evaluates, a source each.</summary>
    public int Shapes
    {
        get
        {
            lock (_gate) return _feeds.Count;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) _disposed = true;
        await _timer.DisposeAsync().ConfigureAwait(false);
        await _evaluator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Why <paramref name="rule" /> can't be kept — no name, a throttle or a trigger out of range, the evaluator's
    ///     refusal of its shape — or null.
    /// </summary>
    public async ValueTask<string?> CheckAsync(AlertRule rule, CancellationToken cancellationToken = default)
    {
        return Problem(rule) ?? await _evaluator.CheckAsync(rule.Watch, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Why the evaluator can't evaluate <paramref name="watch" />'s shape, or null: a condition checked as it's written.</summary>
    public ValueTask<string?> CheckAsync(AlertWatch watch, CancellationToken cancellationToken = default)
    {
        return _evaluator.CheckAsync(watch, cancellationToken);
    }

    /// <summary>
    ///     <paramref name="audience" />'s rules, from now: each checked (<see cref="CheckAsync(AlertRule, CancellationToken)" />) and kept, or refused —
    ///     the refusals, by rule. A rule kept with the same shape keeps how far it was presented; one new, or of another
    ///     shape, starts from its shape's count as it stands. Its pages' subscriptions follow (<see cref="AlertPage.Changed" />).
    /// </summary>
    public async ValueTask<IReadOnlyDictionary<Guid, string>> SetRulesAsync(string audience, IReadOnlyList<AlertRule> rules,
        CancellationToken cancellationToken = default)
    {
        var refused = new Dictionary<Guid, string>();
        var checks = new Dictionary<AlertWatch, string?>();
        var kept = new List<AlertRule>();
        foreach (var rule in rules)
        {
            if (kept.Any(k => k.Id == rule.Id) || refused.ContainsKey(rule.Id))
            {
                refused.TryAdd(rule.Id, "An audience's rules each have an id of their own.");
                continue;
            }

            if (Problem(rule) is { } problem)
            {
                refused[rule.Id] = problem;
                continue;
            }

            if (!checks.TryGetValue(rule.Watch, out var refusal))
                checks[rule.Watch] = refusal = await _evaluator.CheckAsync(rule.Watch, cancellationToken).ConfigureAwait(false);
            if (refusal is not null) refused[rule.Id] = refusal;
            else if (kept.Count >= _options.MaxRulesPerAudience)
                refused[rule.Id] = $"An audience keeps {_options.MaxRulesPerAudience} rules at most (AlertDeskOptions.MaxRulesPerAudience).";
            else kept.Add(rule);
        }

        List<AlertPage> pages;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_audiences.TryGetValue(audience, out var held))
            {
                if (_audiences.Count >= _options.MaxAudiences)
                {
                    foreach (var rule in kept) refused[rule.Id] = $"The alerts hold {_options.MaxAudiences} audiences at most (AlertDeskOptions.MaxAudiences).";
                    return refused;
                }

                _audiences[audience] = held = new Audience(audience, _time.GetUtcNow());
            }

            // Shapes past the cap: those no other rule has already, the rules last in order first to go.
            var shapes = ShapesHeld(except: held);
            foreach (var rule in kept.ToList())
                if (shapes.Add(rule.Watch) && shapes.Count > _options.MaxWatches)
                {
                    shapes.Remove(rule.Watch);
                    kept.Remove(rule);
                    refused[rule.Id] = $"The alerts evaluate {_options.MaxWatches} conditions at most (AlertDeskOptions.MaxWatches).";
                }

            held.Set(kept, FeedOf);
            DropUnused();
            pages = [.. held.Pages];
        }

        foreach (var page in pages) page.Follow(); // (its subscriptions: one a rule, at its throttle)
        foreach (var (watch, why) in await RewatchAsync(cancellationToken).ConfigureAwait(false))
        foreach (var rule in kept.Where(r => r.Watch == watch))
            refused[rule.Id] = why;
        return refused;
    }

    /// <summary>
    ///     A page of <paramref name="audience" />, opened: through the page's outlet, its audience's inbox and, for each
    ///     rule, its shape's source at the rule's throttle — the page subscribes to each binding, and says back what it
    ///     presents. The audience is attended while a page of it is open. Held anew if it isn't (with no rules, till set).
    /// </summary>
    public AlertPage Attach(string audience, Outlet outlet)
    {
        AlertPage page;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_audiences.TryGetValue(audience, out var held))
            {
                if (_audiences.Count >= _options.MaxAudiences)
                    throw new InvalidOperationException($"The alerts hold {_options.MaxAudiences} audiences at most (AlertDeskOptions.MaxAudiences).");
                _audiences[audience] = held = new Audience(audience, _time.GetUtcNow());
            }

            page = new AlertPage(this, held, outlet, outlet.Watch(held.Topic, WatchMember.Of(held.Inbox)), _time.GetUtcNow());
            held.Pages.Add(page);
        }

        page.Follow();
        return page;
    }

    /// <summary>
    ///     The evaluator's word: rows that fired a shape — its count up by as many, its latest row the last — published at
    ///     once, each subscription taking it at its own rule's throttle.
    /// </summary>
    public void Fired(AlertWatch watch, ReadOnlySpan<AlertHit> hits)
    {
        if (hits.IsEmpty) return;
        var now = _time.GetUtcNow();
        lock (_gate)
            if (_feeds.TryGetValue(watch, out var feed) && !_refused.ContainsKey(watch))
                feed.Fire(hits.Length, hits[^1], now);
    }

    /// <summary>A shape's running count, as its source has it (for tests): none if no rule has the shape.</summary>
    internal long? FiredOf(AlertWatch watch)
    {
        lock (_gate) return _feeds.TryGetValue(watch, out var feed) ? feed.Fired : null;
    }

    /// <summary>A shape's value, as its source last published it (for tests): none if no rule has the shape.</summary>
    internal AlertFeedValue? ValueOf(AlertWatch watch)
    {
        lock (_gate) return _feeds.TryGetValue(watch, out var feed) ? feed.Value : null;
    }

    /// <summary>An audience's inbox, as its pages have it (for tests): none if it isn't held.</summary>
    internal Table<Guid, AlertRow>? InboxOf(string audience)
    {
        lock (_gate) return _audiences.GetValueOrDefault(audience)?.Inbox;
    }

    /// <summary>What a page presented of a rule: the rule's row keeps the most any page says, never past its shape's count.</summary>
    internal void Presented(Audience audience, Guid rule, long count)
    {
        lock (_gate)
            if (audience.Rules.TryGetValue(rule, out var state) && count > state.Presented)
            {
                state.Presented = Math.Min(count, state.Feed.Fired);
                audience.Write(state);
            }
    }

    /// <summary>Each of the audience's rules as a page subscribes to it: its id, its shape's source, its throttle.</summary>
    internal (Guid Rule, Source Feed, TimeSpan Throttle)[] RulesOf(Audience audience)
    {
        lock (_gate) return [.. audience.Rules.Values.Select(s => (s.Rule.Id, (Source)s.Feed.Source, s.Rule.Throttle))];
    }

    /// <summary>A page of the audience closed.</summary>
    internal void Detach(Audience audience, AlertPage page)
    {
        lock (_gate)
            if (audience.Pages.Remove(page) && audience.Pages.Count == 0)
                audience.LeftAt = _time.GetUtcNow();
    }

    /// <summary>Audiences unattended past the keep-for time let go, and the shapes only they had.</summary>
    private void Sweep()
    {
        var now = _time.GetUtcNow();
        var gone = false;
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var audience in _audiences.Values.Where(a => a.Pages.Count == 0 && now - a.LeftAt > _options.KeepFor).ToList())
            {
                _audiences.Remove(audience.Id);
                gone = true;
            }

            if (gone) DropUnused();
            _timer.Change(_options.SweepEvery, Timeout.InfiniteTimeSpan);
        }

        if (gone) _ = RewatchAsync(CancellationToken.None).AsTask(); // (the evaluator lets go of the shapes no rule has)
    }

    /// <summary>The evaluator told the distinct shapes in use: those it refuses, and why.</summary>
    private async ValueTask<IReadOnlyDictionary<AlertWatch, string>> RewatchAsync(CancellationToken cancellationToken)
    {
        await _watching.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AlertWatch[] watches;
            lock (_gate) watches = [.. _feeds.Keys];
            var refused = await _evaluator.WatchAsync(watches, this, cancellationToken).ConfigureAwait(false);
            lock (_gate) _refused = refused;
            return refused;
        }
        finally
        {
            _watching.Release();
        }
    }

    /// <summary>A shape's source: made for its first rule.</summary>
    private Feed FeedOf(AlertWatch watch)
    {
        if (!_feeds.TryGetValue(watch, out var feed)) _feeds[watch] = feed = new Feed();
        return feed;
    }

    /// <summary>The sources of shapes no rule has any more let go (their subscriptions gone with the rules).</summary>
    private void DropUnused()
    {
        var used = ShapesHeld();
        foreach (var watch in _feeds.Keys.Where(w => !used.Contains(w)).ToList()) _feeds.Remove(watch);
    }

    /// <summary>The distinct shapes the rules have, every audience's (but <paramref name="except" />'s).</summary>
    private HashSet<AlertWatch> ShapesHeld(Audience? except = null)
    {
        var shapes = new HashSet<AlertWatch>();
        foreach (var audience in _audiences.Values)
            if (audience != except)
                foreach (var state in audience.Rules.Values)
                    shapes.Add(state.Rule.Watch);
        return shapes;
    }

    /// <summary>What's wrong with a rule before its condition is looked at: else null.</summary>
    private static string? Problem(AlertRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Name)) return "A rule has a name.";
        if (string.IsNullOrWhiteSpace(rule.Source)) return "A rule watches a source.";
        if (rule.Throttle < TimeSpan.Zero || rule.Throttle > TimeSpan.FromHours(1)) return "A rule's throttle is from nothing to an hour.";
        return rule.Trigger switch
        {
            AlertTrigger.Waiting { Over: var over } when over <= TimeSpan.Zero || over > TimeSpan.FromHours(1) =>
                "A wait is more than nothing and at most an hour.",
            AlertTrigger.Counting { Window: var window } when window < TimeSpan.FromSeconds(1) || window > TimeSpan.FromHours(24) =>
                "A count's window is from a second to a day.",
            AlertTrigger.Counting { Kind: AlertCountKind.AtLeast, Matches: < 1 } => "A count of at least so many is of at least one.",
            _ => null
        };
    }

    /// <summary>A shape's source: its running count, when it last fired, its latest row — published at every firing.</summary>
    internal sealed class Feed
    {
        public readonly LastValueSource Source = new() { Label = $"alerts/shape/{Guid.CreateVersion7():N}" };
        public long Fired;
        private AlertFeedValue _value;

        /// <summary>The value as last published.</summary>
        public AlertFeedValue Value => _value;

        public Feed()
        {
            Publish(); // (a first state, nothing fired: a page subscribing has its count from the start)
        }

        public void Fire(int rows, in AlertHit latest, DateTimeOffset now)
        {
            Fired += rows;
            (_value.Fired, _value.FiredAt) = (Fired, (now.UtcTicks - DateTime.UnixEpoch.Ticks) / 10);
            AlertText.Write(_value.Key, latest.Key ?? "");
            AlertText.Write(_value.Summary, latest.Summary ?? "");
            Publish();
        }

        private void Publish()
        {
            Source.Publish(MemoryMarshal.AsBytes(new ReadOnlySpan<AlertFeedValue>(in _value)));
        }
    }

    /// <summary>One audience: its rules, its inbox (a row per rule), its pages.</summary>
    internal sealed class Audience(string id, DateTimeOffset since)
    {
        public readonly string Id = id;
        public readonly Table<Guid, AlertRow> Inbox = new("alerts/inbox", 1 << 8);
        public readonly List<AlertPage> Pages = [];
        public readonly Dictionary<Guid, RuleState> Rules = [];
        public readonly string Topic = $"alerts/{Guid.CreateVersion7():N}"; // (as a page's outlet names its watch: no word of the audience's id, a bearer secret)
        public DateTimeOffset LeftAt = since;

        /// <summary>The rules kept, from now: those of the same shape keep how far they were presented, the rest start from their shape's count.</summary>
        public void Set(List<AlertRule> rules, Func<AlertWatch, Feed> feedOf)
        {
            using var update = Inbox.BeginUpdate();
            foreach (var gone in Rules.Keys.Where(id => rules.All(r => r.Id != id)).ToList())
            {
                Rules.Remove(gone);
                update.Delete(gone);
            }

            foreach (var rule in rules)
            {
                if (!Rules.TryGetValue(rule.Id, out var state) || state.Rule.Watch != rule.Watch)
                {
                    var feed = feedOf(rule.Watch);
                    Rules[rule.Id] = state = new RuleState(rule, feed) { Presented = feed.Fired }; // (from now)
                }
                else
                {
                    state.Rule = rule; // (a name, a sound, a throttle or a mute changed: how far presented kept)
                }

                update.Set(rule.Id, state.Row());
            }

            update.Complete();
        }

        public void Write(RuleState state)
        {
            using var update = Inbox.BeginUpdate();
            update.Set(state.Rule.Id, state.Row());
            update.Complete();
        }
    }

    /// <summary>A rule as its audience has it: its shape's source, and how far a page has presented it.</summary>
    internal sealed class RuleState(AlertRule rule, Feed feed)
    {
        public readonly Feed Feed = feed;
        public long Presented;
        public AlertRule Rule = rule;

        public AlertRow Row()
        {
            var row = new AlertRow { Presented = Presented, Muted = Rule.Muted ? (byte)1 : (byte)0 };
            AlertText.Write(row.Source, Rule.Source);
            AlertText.Write(row.Name, Rule.Name);
            AlertText.Write(row.Sound, Rule.Sound);
            return row;
        }
    }
}

/// <summary>
///     A page of an audience: its inbox's watch (<see cref="Inbox" />) and, for each rule, a subscription to its shape's
///     source at the rule's throttle (<see cref="Shapes" />). The page subscribes to each, presents what each shape's count
///     has moved by since it last presented, and says back what it presented. When the rules change, the subscriptions
///     follow and <see cref="Changed" /> says so. Disposed as the page closes.
/// </summary>
public sealed class AlertPage : IDisposable
{
    private readonly AlertDesk.Audience _audience;
    private readonly AlertDesk _desk;
    private readonly Lock _gate = new();
    private readonly Tap _inbox;
    private readonly Outlet _outlet;
    private readonly Dictionary<Guid, (Source Feed, Tap Tap)> _shapes = [];
    private int _disposed;

    internal AlertPage(AlertDesk desk, AlertDesk.Audience audience, Outlet outlet, Tap inbox, DateTimeOffset attachedAt)
    {
        (_desk, _audience, _outlet, _inbox, AttachedAt) = (desk, audience, outlet, inbox, attachedAt);
    }

    /// <summary>
    ///     When the page was opened, by the desk's clock: what a shape fired before it (its last firing, as its value says,
    ///     earlier) fired while away — what no page of the audience presented — for the page to say so, apart.
    /// </summary>
    public DateTimeOffset AttachedAt { get; }

    /// <summary>The audience's inbox: a table of a row per rule (<see cref="AlertRow" />), keyed by the rule's id.</summary>
    public TapBinding Inbox => _inbox.Binding;

    /// <summary>Each rule's shape (<see cref="AlertFeedValue" />), by the rule's id: taken no oftener than the rule's throttle.</summary>
    public IReadOnlyDictionary<Guid, TapBinding> Shapes
    {
        get
        {
            lock (_gate) return _shapes.ToDictionary(s => s.Key, s => s.Value.Tap.Binding);
        }
    }

    /// <summary>The rules changed: <see cref="Shapes" /> anew, for the page to subscribe to those it hasn't.</summary>
    public event Action? Changed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_gate)
        {
            foreach (var (_, tap) in _shapes.Values) tap.Dispose();
            _shapes.Clear();
        }

        _inbox.Dispose();
        _desk.Detach(_audience, this);
    }

    /// <summary>
    ///     The page presented <paramref name="rule" />'s alerts up to <paramref name="count" />, its shape's running count:
    ///     every page of the audience presents from there; the row keeps the most any page says.
    /// </summary>
    public void Presented(Guid rule, long count)
    {
        _desk.Presented(_audience, rule, count);
    }

    /// <summary>A rule's subscription to its shape (for tests): none if the page has none for it.</summary>
    internal Tap? ShapeTap(Guid rule)
    {
        lock (_gate) return _shapes.TryGetValue(rule, out var held) ? held.Tap : null;
    }

    /// <summary>Its subscriptions made to match the rules: one a rule, on its shape, at its throttle; those of rules gone ended.</summary>
    internal void Follow()
    {
        var changed = false;
        lock (_gate)
        {
            if (_disposed != 0) return;
            var rules = _desk.RulesOf(_audience);
            foreach (var (rule, (feed, tap)) in _shapes.ToList())
                if (rules.All(r => r.Rule != rule || r.Feed != feed))
                {
                    tap.Dispose();
                    _shapes.Remove(rule);
                    changed = true;
                }

            foreach (var (rule, feed, throttle) in rules)
                if (_shapes.TryGetValue(rule, out var held))
                {
                    held.Tap.MinInterval = throttle; // (its throttle changed, perhaps: set as it stands)
                }
                else
                {
                    var tap = _outlet.Tap(feed, feed.Label);
                    tap.MinInterval = throttle;
                    _shapes[rule] = (feed, tap);
                    changed = true;
                }
        }

        if (changed) Changed?.Invoke();
    }
}
