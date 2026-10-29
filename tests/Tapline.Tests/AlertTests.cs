using Tapline;
using Tapline.Alerts;

/// <summary>An evaluator by hand: it refuses what says "refuse" as it's checked, and what says "later" once watched; it fires as told.</summary>
internal sealed class HandEvaluator : IAlertEvaluator
{
    public int Rewatched;
    public IAlertSink? Sink;
    public AlertWatch[] Watching = [];

    public ValueTask<string?> CheckAsync(AlertWatch watch, CancellationToken cancellationToken)
    {
        return new ValueTask<string?>(watch.Condition.Contains("refuse", StringComparison.Ordinal) ? $"no condition says '{watch.Condition}'" : null);
    }

    public ValueTask<IReadOnlyDictionary<AlertWatch, string>> WatchAsync(IReadOnlyCollection<AlertWatch> watches, IAlertSink sink,
        CancellationToken cancellationToken)
    {
        (Sink, Watching) = (sink, [.. watches]);
        Rewatched++;
        IReadOnlyDictionary<AlertWatch, string> refused = watches.Where(w => w.Condition.Contains("later", StringComparison.Ordinal))
            .ToDictionary(w => w, _ => "its view outgrew its budget");
        return new ValueTask<IReadOnlyDictionary<AlertWatch, string>>(refused);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    /// <summary>Rows firing <paramref name="watch" />, as an evaluator says them.</summary>
    public void Fire(AlertWatch watch, params string[] keys)
    {
        Sink!.Fired(watch, [.. keys.Select(k => new AlertHit(k, $"order {k}"))]);
    }
}

internal static partial class T
{
    private static AlertRule Rule(string name, string condition, TimeSpan throttle, AlertTrigger? trigger = null, string source = "orders")
    {
        return new AlertRule(Guid.CreateVersion7(), name, source, condition, trigger ?? AlertTrigger.Entering.Instance, throttle, "kaching", false);
    }

    private static AlertRow RowOf(AlertDesk desk, string audience, AlertRule rule)
    {
        Assert(desk.InboxOf(audience)!.TryGet(rule.Id, out var row), $"{rule.Name}: a row in {audience}'s inbox");
        return row;
    }

    public static async Task AlertDeskShapes()
    {
        var clock = new ManualClock();
        var evaluator = new HandEvaluator();
        await using var desk = new AlertDesk(evaluator, new AlertDeskOptions { MaxRulesPerAudience = 3, MaxWatches = 4, MaxAudiences = 3 }, clock);
        var registry = new OutletRegistry();

        // Shared by shape: two audiences' rules of one shape — the same source, condition and trigger — one evaluation and
        // one source, fanned out; another shape, another. Each page subscribes to each rule's shape at the rule's throttle.
        var partial = Rule("Partial", "status = 'Partial'", TimeSpan.FromSeconds(10));
        var counted = Rule("Busy", "qty > 1", TimeSpan.FromSeconds(1), new AlertTrigger.Counting(AlertCountKind.AtLeast, 5, TimeSpan.FromSeconds(30)));
        var refused = await desk.SetRulesAsync("browser-a", [partial, counted]);
        var same = partial with { Id = Guid.CreateVersion7(), Name = "Mine too", Throttle = TimeSpan.FromSeconds(3), Muted = true };
        refused = refused.Concat(await desk.SetRulesAsync("browser-b", [same])).ToDictionary();
        Assert(refused.Count == 0 && desk.Shapes == 2 && evaluator.Watching.Length == 2,
            $"three rules, two shapes: evaluated once each ({desk.Shapes}; {string.Join("; ", refused.Values)})");
        using var pageA = desk.Attach("browser-a", NewOutlet(registry));
        using var pageB = desk.Attach("browser-b", NewOutlet(registry));
        var (tapA, tapB) = (pageA.ShapeTap(partial.Id)!, pageB.ShapeTap(same.Id)!);
        Assert(tapA.Source == tapB.Source && tapA.MinInterval == TimeSpan.FromSeconds(10) && tapB.MinInterval == TimeSpan.FromSeconds(3) &&
               pageA.Shapes.Count == 2 && pageB.Shapes.Count == 1,
            "one source for the shape, each page's subscription at its own rule's throttle");
        Assert(pageA.AttachedAt == clock.GetUtcNow(), "a page opened when the desk's clock says: what fired before it, fired while away");

        // Fired: the shape's count up by as many rows, its latest row the last, published at once — each subscription takes
        // it at its own interval, whatever fired meanwhile conflated into it (Tapline's MinInterval).
        var version = tapA.Source.Version;
        evaluator.Fire(partial.Watch, "o1");
        evaluator.Fire(partial.Watch, "o2", "o3");
        Assert(desk.FiredOf(partial.Watch) == 3 && tapA.Source.Version == version + 2, "fired: the count up by the rows, published each time");
        var value = desk.ValueOf(partial.Watch)!.Value;
        Assert(value.Fired == 3 && AlertText.Read(value.Key).Text == "o3" && AlertText.Read(value.Summary).Text == "order o3" && value.FiredAt > 0,
            "the shape's value: its count, its latest row, when it fired");
        for (var i = 0; i < 1_000; i++) evaluator.Fire(partial.Watch, $"q{i}");
        value = desk.ValueOf(partial.Watch)!.Value;
        Assert(value.Fired == 1_003 && AlertText.Read(value.Key).Text == "q999", "a thousand firings: one value, counted on, the latest last");

        // Presented by a page: the row keeps the most any page of the audience says, never past its shape's count; a page
        // behind can't lower it. Another audience's row isn't touched.
        pageA.Presented(partial.Id, 3);
        pageA.Presented(partial.Id, 2);
        Assert(RowOf(desk, "browser-a", partial).Presented == 3, "presented: the most a page said");
        pageA.Presented(partial.Id, 99_999);
        Assert(RowOf(desk, "browser-a", partial).Presented == 1_003 && RowOf(desk, "browser-b", same).Presented == 0,
            "presented: never past the shape's count; another audience's own");

        // A rule renamed, its throttle changed: how far presented kept, its subscriptions' interval as it stands. A rule of
        // another shape, or new: from the count as it stands.
        var renamed = partial with { Name = "Partial orders", Throttle = TimeSpan.FromSeconds(30) };
        await desk.SetRulesAsync("browser-a", [renamed, counted]);
        var row = RowOf(desk, "browser-a", renamed);
        Assert(row.Presented == 1_003 && pageA.ShapeTap(partial.Id)!.MinInterval == TimeSpan.FromSeconds(30) &&
               AlertText.Read(row.Name).Text == "Partial orders",
            "renamed, its throttle changed: presented kept, its interval set");
        var changes = 0;
        pageA.Changed += () => changes++;
        var reshaped = renamed with { Condition = "status = 'New'" };
        await desk.SetRulesAsync("browser-a", [reshaped, counted]);
        Assert(RowOf(desk, "browser-a", reshaped).Presented == 0 && pageA.ShapeTap(reshaped.Id)!.Source != tapB.Source && changes == 1 &&
               desk.Shapes == 3, "its shape changed: a subscription to the new shape, from its count; the old shape kept for the other audience");
        evaluator.Fire(reshaped.Watch, "n1");
        var joined = Rule("Late joiner", "status = 'New'", TimeSpan.Zero);
        await desk.SetRulesAsync("browser-b", [same, joined]);
        Assert(RowOf(desk, "browser-b", joined).Presented == 1, "a rule added to a shape that's fired: from its count as it stands");

        // A count fires its shape as it enters; its throttle is its subscriptions' interval, like any rule's.
        evaluator.Sink!.Fired(counted.Watch, [new AlertHit("", "5 in the last 30 s")]);
        Assert(desk.FiredOf(counted.Watch) == 1 && pageA.ShapeTap(counted.Id)!.MinInterval == TimeSpan.FromSeconds(1), "a count fired, at its throttle");

        // Refused before kept: the evaluator's word as it's checked, and as it's watched; past the caps.
        var badly = Rule("Bad", "refuse me", TimeSpan.Zero);
        var lately = Rule("Late", "later", TimeSpan.Zero);
        var over = Rule("Over", "qty > 4", TimeSpan.Zero);
        refused = await desk.SetRulesAsync("browser-a", [reshaped, counted, badly, lately, over]);
        Assert(refused.TryGetValue(badly.Id, out var why) && why.Contains("refuse me") && refused.ContainsKey(lately.Id) &&
               refused.TryGetValue(over.Id, out var cap) && cap.Contains("MaxRulesPerAudience"),
            $"refused, each saying why: {string.Join("; ", refused.Values)}");
        refused = await desk.SetRulesAsync("browser-c", [Rule("C1", "qty > 5", TimeSpan.Zero)]);
        Assert(refused.Values.Single().Contains("MaxWatches"), $"a fifth distinct shape: refused ({string.Join("; ", refused.Values)})");
        refused = await desk.SetRulesAsync("browser-d", [Rule("D", "qty > 1", TimeSpan.Zero)]);
        Assert(refused.Values.Single().Contains("MaxAudiences"), "a fourth audience: refused");
        Assert(RowOf(desk, "browser-a", lately).Presented == 0 && desk.Shapes == 4, "refused once watched: kept, saying so");
        Assert(await desk.CheckAsync(Rule("Unnamed", "qty > 1", TimeSpan.Zero, new AlertTrigger.Waiting(TimeSpan.FromSeconds(2), From: " "))) is { } blank &&
               blank.Contains("timestamp named"), "a wait from a timestamp left blank: refused before the evaluator is asked");

        // Kept, unattended, for the keep-for time, rules evaluated; then let go, and the shapes only it had with it.
        pageB.Dispose();
        clock.Advance(AlertDeskOptions.Default.KeepFor - TimeSpan.FromSeconds(30));
        Assert(desk.Audiences == (3, 1), $"unattended within the keep-for time: kept ({desk.Audiences})");
        clock.Advance(TimeSpan.FromSeconds(31));
        await Until(() => desk.Audiences.Audiences == 1, "the unattended audiences let go");
        await Until(() => evaluator.Watching.Length == desk.Shapes, "the evaluator told the shapes left");
        Assert(desk.InboxOf("browser-b") is null && desk.InboxOf("browser-a") is not null && desk.FiredOf(joined.Watch) is { } kept && kept >= 0,
            "past it: let go, rules and all; the attended kept");

        // Apart: Tapline.Alerts references nothing of grids; its rows packed as the page reads them.
        var references = typeof(AlertDesk).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        Assert(!references.Any(r => r!.StartsWith("Tapline.Grid", StringComparison.Ordinal)), $"apart from grids: {string.Join(", ", references)}");
        Assert(AlertRow.Size == 105 && AlertFeedValue.Size == 192, $"rows packed as the page reads them: {AlertRow.Size}, {AlertFeedValue.Size} bytes");
    }

    /// <summary>
    ///     An inbox's row and a shape's value as .NET lays them out (fixtures/frames.json, "alerts"), and a rule's key: for
    ///     the JS tests to hold tapline-alerts.js's readers to — counts past 2³², a time, texts whole and cut.
    /// </summary>
    private static object AlertFixture()
    {
        var rule = Guid.Parse("01a18356-af60-7000-86cf-7ea6c6c82c20");
        var row = new AlertRow { Presented = 1_234_567_890_123, Muted = 1 };
        AlertText.Write(row.Source, "fills");
        AlertText.Write(row.Name, "Big fills for FERN — élan");
        AlertText.Write(row.Sound, "kaching");
        var value = new AlertFeedValue { Fired = 9_876_543_210, FiredAt = 1_793_000_000_123_456 };
        AlertText.Write(value.Key, rule.ToString());
        AlertText.Write(value.Summary, string.Concat(Enumerable.Repeat("FERN Buy 0.5 BTC/USD Perp @ 65,053.8 · ", 4)));
        return new
        {
            rule = rule.ToString(), key = Convert.ToBase64String(rule.ToByteArray()),
            row = Convert.ToBase64String(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<AlertRow>(in row))),
            shape = Convert.ToBase64String(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<AlertFeedValue>(in value))),
            presented = row.Presented, name = AlertText.Read(row.Name).Text, fired = value.Fired, firedAt = value.FiredAt / 1000.0,
            summary = AlertText.Read(value.Summary).Text + "…"
        };
    }

    public static Task AlertTexts()
    {
        // Inline text: written whole when it fits, else cut at a character's boundary and flagged; read back as written.
        var field = new AlertText16();
        AlertText.Write(field, "FERN");
        Assert(AlertText.Read(field) == ("FERN", false), "short: whole");
        AlertText.Write(field, "ÉÉÉÉÉÉÉÉ"); // (16 bytes of UTF-8: two each)
        Assert(AlertText.Read(field) == ("ÉÉÉÉÉÉÉ", true), $"long: cut at a character's boundary, flagged ({AlertText.Read(field)})");
        AlertText.Write(field, "ab😀😀😀😀");
        Assert(AlertText.Read(field) == ("ab😀😀😀", true), "a four-byte character never split");
        return Task.CompletedTask;
    }
}
