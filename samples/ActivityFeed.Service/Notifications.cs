using ActivityFeed.Contracts;

namespace ActivityFeed.Service;

/// <summary>
///     The dispatch log, as the service's <c>/dispatches</c> reports it: every notification, every entry, and every
///     notice posted.
/// </summary>
public sealed record DispatchLog(Notification[] Notifications, Dispatch[] Entries, NoticeRow[] Notices);

/// <summary>
///     Who was shown a record, as the access log has it (<c>/access</c>): a person, their topic, the first and last post that
///     carried it, how many did, and why its patient was disclosed to them (its basis).
/// </summary>
public sealed record ShownTo(int Person, string User, string Topic, DateTime FirstShown, DateTime LastShown, long Posts,
    DisclosureBasis Basis);

/// <summary>
///     A recipient's limits against notification fatigue, for what's counted (operational and informational, not
///     transactional): urgent pushes a day; normal pushes a day, hours apart, never in quiet hours (the practice's local
///     time: <c>from</c> to <c>to</c>, or none).
/// </summary>
public sealed record Budgets(int Urgent, int Normal, TimeSpan NormalApart, (int From, int To)? QuietHours, TimeZoneInfo Zone)
{
    /// <summary>The budgets, with quiet hours (<c>22-8</c>, or <c>none</c>) in a time zone (its id; this machine's if empty).</summary>
    public static Budgets Of(string quiet, string timeZone)
    {
        var zone = timeZone is { Length: > 0 } id ? TimeZoneInfo.FindSystemTimeZoneById(id) : TimeZoneInfo.Local;
        (int, int)? hours = quiet.Split('-') is [var from, var to] && int.TryParse(from, out var f) && int.TryParse(to, out var t)
            ? (f, t)
            : null; // ("none")
        return new Budgets(3, 2, TimeSpan.FromHours(4), hours, zone);
    }

    /// <summary>Whether it's quiet hours at the practice now: no normal push.</summary>
    public bool Quiet(DateTime utc)
    {
        if (QuietHours is not var (from, to)) return false;
        var hour = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone).Hour;
        return from > to ? hour >= from || hour < to : hour >= from && hour < to;
    }
}

/// <summary>A row of the release job's run: a patient's place in their queue for a notice, and their budget's use.</summary>
public sealed record Waiting(int Patient, string Notice, string Class, bool Urgent, DateTime DeliverBy, long Place, long Pushes,
    DateTime? LastPush);

/// <summary>
///     A notification, as the service tracks it until it ends: its row, and each recipient's part in it (the patient, and
///     each carer whose designation shows what it's about).
/// </summary>
internal sealed class Notified
{
    public required Notification Row { get; set; }
    public required List<Recipient> Recipients { get; init; }
}

/// <summary>
///     A recipient's part in a notification: in the app's grace (their feed was open: a seen or an acknowledgement there
///     ends it, nothing sent), sent to their devices, or ended without a send (seen, or acknowledged, in the app).
/// </summary>
internal sealed class Recipient
{
    public required Person Person { get; init; }
    public bool ForCarer { get; init; }
    public DateTime GraceUntil { get; set; }
    public RecipientState State { get; set; }
}

internal enum RecipientState
{
    InGrace,
    Sent,
    EndedInApp
}

/// <summary>
///     The practice's notifications: who's told of what, on which devices, and how it went. The service's own record,
///     owned by the provider's loop, written to ChalkQL as it changes: the notifications, the dispatch log's entries (each
///     numbered in its recipient's shard's sequence, gapless) and the devices. Transactional notifications go out on
///     their own by their deadline, unless their recipient has seen what they're about in the app within the grace (their
///     feed open as it's raised); an acknowledgement in the app ends one for everyone. An entry still waiting at its
///     notification's deadline becomes a no-op ("expired"): recorded, never sent.
/// </summary>
internal sealed class Notifications(TimeSpan grace, Budgets budgets)
{
    /// <summary>The most notices one push bundles: the top of a queue, counting once.</summary>
    public const int Bundle = 3;

    public const int Shards = 2;

    private const string Practice = "Northgate Practice";

    private readonly Dictionary<long, Device> _devices = People.Devices.ToDictionary(d => d.Id);
    private readonly Dictionary<long, Dispatch> _entries = []; // by id: the dispatch log, every entry as it last was
    private readonly Dictionary<string, List<long>> _entriesOf = []; // by notification: its entries' ids
    private readonly Dictionary<string, Notified> _live = []; // by id: those not ended
    private readonly Dictionary<string, Notification> _ended = []; // by id
    private readonly Dictionary<long, string> _ofActivity = []; // an activity's latest notification, by its number
    private readonly long[] _sequence = new long[Shards]; // (each shard's last number)
    private readonly Dictionary<string, NoticeRow> _notices = []; // by id: every notice posted
    private long _issued; // (ids made: a notice's or a notification's, of its time and this number, alike on every instance)

    /// <summary>Every entry of the dispatch log, as it stands.</summary>
    public IReadOnlyCollection<Dispatch> Entries => _entries.Values;

    /// <summary>Every notification, live or ended.</summary>
    public IEnumerable<Notification> All => _live.Values.Select(n => n.Row).Concat(_ended.Values);

    /// <summary>Every notice posted.</summary>
    public IEnumerable<NoticeRow> Notices => _notices.Values;

    /// <summary>
    ///     A notice posted by the manager to a site's cohort, canned (its text the same for everyone): the phone lines
    ///     down (operational, urgent), the opening hours changing (operational), flu vaccinations (informational). Its
    ///     deadline is its validity, capped by what its class may wait: an hour if urgent, three days, or seven.
    /// </summary>
    public NoticeRow? Post(string kind, Person by, int site, DateTime now, Writes writes)
    {
        (string Class, bool Urgent, string Title, TimeSpan Valid, TimeSpan Wait)? canned = kind switch
        {
            "phones" => ("operational", true, $"Our phone lines are down this morning: please use the app", TimeSpan.FromHours(3),
                TimeSpan.FromHours(1)),
            "hours" => ("operational", false, "Our opening hours are changing from next week", TimeSpan.FromDays(3), TimeSpan.FromDays(3)),
            "flu" => ("informational", false, "Flu vaccinations are available: book at reception", TimeSpan.FromDays(7),
                TimeSpan.FromDays(7)),
            _ => null
        };
        if (canned is not var (cls, urgent, title, valid, wait)) return null;
        var row = new NoticeRow(Ids.Version7(now, ++_issued).ToString(), site,
            kind, cls, urgent, title, now, now + valid, now + (valid < wait ? valid : wait), by.Id);
        _notices[row.Id] = row;
        writes.Write(row);
        return row;
    }

    /// <summary>
    ///     The release job's run, acted on: each patient's queues (urgent, normal), ranked by the run. A notice past its
    ///     deadline, never released to them, is recorded expired (a no-op for each device); an informational one to a
    ///     patient who opted out, recorded so. Then, where their budget has a slot (an urgent one; a normal one, outside
    ///     quiet hours, four hours since the last), the top few go in one push: the first sent, the rest bundled into it.
    ///     The rest wait.
    /// </summary>
    public void Release(IReadOnlyList<Waiting> waiting, DateTime now, Writes writes)
    {
        foreach (var queue in waiting.GroupBy(w => (w.Patient, w.Urgent)))
        {
            if (People.Find(queue.Key.Patient) is not { } patient) continue;
            var ranked = queue.OrderBy(w => w.Place).Where(w => _notices.ContainsKey(w.Notice)).ToList();
            foreach (var expired in ranked.Where(w => w.DeliverBy <= now))
                Notice(patient, _notices[expired.Notice], "expired",
                    $"expired: over the limit, not released by {expired.DeliverBy:HH:mm:ss}", now, writes);
            foreach (var declined in ranked.Where(w => w.DeliverBy > now && w.Class == "informational" && patient.OptedOutOfNews))
                Notice(patient, _notices[declined.Notice], "opted_out", "", now, writes);
            var due = ranked.Where(w => w.DeliverBy > now && !(w.Class == "informational" && patient.OptedOutOfNews)).ToList();
            if (due.Count == 0) continue;
            var (pushes, last) = (queue.First().Pushes, queue.First().LastPush);
            var slot = queue.Key.Urgent
                ? pushes < budgets.Urgent
                : pushes < budgets.Normal && (last is null || now - last >= budgets.NormalApart) && !budgets.Quiet(now);
            if (!slot) continue; // (they wait: for a slot, or their deadline)
            var bundle = due.Take(Bundle).Select(w => _notices[w.Notice]).ToList();
            var top = Notice(patient, bundle[0], "ready",
                bundle.Count > 1 ? $" (and {bundle.Count - 1} more from {Practice})" : "", now, writes);
            foreach (var more in bundle.Skip(1))
                Notice(patient, more, "bundled", $"bundled into push {top.Id}", now, writes);
        }
    }

    /// <summary>
    ///     A notice, as released to one patient: a notification of their own. Ready, it's sent to each of their devices
    ///     (its title the push's); otherwise recorded as it ended, a no-op for each device saying why (none, opted out).
    /// </summary>
    private Notification Notice(Person patient, NoticeRow notice, string state, string detail, DateTime now, Writes writes)
    {
        var ready = state == "ready";
        var row = new Notification(Ids.Version7(now, ++_issued).ToString(),
            patient.Id, patient.User, patient.Sites[0], 0, 0, "notice", notice.Class, notice.Urgent,
            ready ? notice.Title + detail : notice.Title, "", now, notice.ValidUntil, notice.DeliverBy, state, now, notice.Id);
        var notified = new Notified { Row = row, Recipients = [new Recipient { Person = patient, State = RecipientState.Sent }] };
        writes.Write(row);
        if (ready)
        {
            _live[row.Id] = notified;
            foreach (var device in DevicesOf(patient.Id)) Entry(notified, notified.Recipients[0], device, "send", "", now, writes);
            Settle(notified, now, writes); // (undeliverable, for nobody's device)
            return row;
        }

        if (state != "opted_out")
            foreach (var device in DevicesOf(patient.Id))
                Entry(notified, notified.Recipients[0], device, "no-op", detail, now, writes);
        _ended[row.Id] = row;
        return row;
    }

    public static int ShardOf(int person)
    {
        return person % Shards;
    }

    /// <summary>
    ///     A notification raised: about <paramref name="patient" />'s activity or result, to each recipient (the patient,
    ///     and each carer who can see it); in the app's grace for those whose feed is open, sent at once to the others.
    /// </summary>
    public Notification Raise(string kind, Person patient, long activity, long section, DateTime validUntil,
        IReadOnlyList<(Person Person, bool ForCarer, bool Open)> recipients, DateTime now, Writes writes)
    {
        var (title, carerTitle, urgent) = kind switch
        {
            "called" => ($"You've been called in at {Practice}", $"Someone you care for has been called in at {Practice}", true),
            "reminded" => ($"You have an appointment reminder from {Practice}",
                $"Someone you care for has an appointment reminder from {Practice}", false),
            "replied" => ($"New message from {Practice}", $"New message from {Practice} for someone you care for", true),
            _ => ($"A result has been released to you by {Practice}", $"A result has been released to someone you care for by {Practice}",
                false)
        };
        var wait = urgent ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(1); // (what its class may wait, as urgent or not)
        var deliverBy = validUntil < now + wait ? validUntil : now + wait;
        var row = new Notification(Ids.Version7(now, ++_issued).ToString(),
            patient.Id, patient.User, patient.Sites[0], activity, section, kind, "transactional", urgent, title, carerTitle, now,
            validUntil, deliverBy, "pending", now);
        var notified = new Notified
        {
            Row = row,
            Recipients =
            [
                .. recipients.Select(r => new Recipient
                {
                    Person = r.Person, ForCarer = r.ForCarer, GraceUntil = r.Open ? now + grace : now,
                    State = RecipientState.InGrace
                })
            ]
        };
        _live[row.Id] = notified;
        if (activity != 0) _ofActivity[activity] = row.Id;
        writes.Write(row);
        foreach (var recipient in notified.Recipients.Where(r => r.GraceUntil <= now)) Send(notified, recipient, now, writes);
        Settle(notified, now, writes);
        return row;
    }

    /// <summary>A recipient saw an activity in the app: their part in each notification about it, in its grace, ends unsent.</summary>
    public bool Seen(Person person, long activity, DateTime now, Writes writes)
    {
        var any = false;
        foreach (var notified in _live.Values.Where(n => n.Row.ActivitySeq == activity).ToList())
        foreach (var recipient in notified.Recipients.Where(r => r.Person.Id == person.Id && r.State == RecipientState.InGrace))
        {
            EndInApp(notified, recipient, $"seen in the app, {now:HH:mm:ss}", now, writes);
            Settle(notified, now, writes);
            any = true;
        }

        return any;
    }

    /// <summary>Whether a recipient's part in a notification about an activity waits for the app: what a seen report may end.</summary>
    public bool Awaits(Person person, long activity)
    {
        return _live.Values.Any(n => n.Row.ActivitySeq == activity &&
                                     n.Recipients.Any(r => r.Person.Id == person.Id && r.State == RecipientState.InGrace));
    }

    /// <summary>
    ///     An activity acknowledged in the app (a reminder): each notification about it ends for every recipient, a send
    ///     not yet handed off turned into a no-op.
    /// </summary>
    public void Acknowledged(long activity, Person by, DateTime now, Writes writes)
    {
        foreach (var notified in _live.Values.Where(n => n.Row.ActivitySeq == activity).ToList())
        {
            var why = $"acknowledged in the app by {(by.Role == Role.Carer ? "their carer" : "the patient")}, {now:HH:mm:ss}";
            foreach (var recipient in notified.Recipients.Where(r => r.State == RecipientState.InGrace))
                EndInApp(notified, recipient, why, now, writes);
            foreach (var entry in EntriesOf(notified.Row.Id).Where(e => e is { Action: "send", Outcome: "" }))
                Write(entry with { Action = "no-op", Reason = why }, writes);
            Settle(notified, now, writes);
        }
    }

    /// <summary>
    ///     A carer's designation revoked: their part in each live notification about the patient ends, a no-op
    ///     ("designation revoked") for each device that waited, a send not yet handed off turned into one.
    /// </summary>
    public void Revoked(int patient, int carer, DateTime now, Writes writes)
    {
        foreach (var notified in _live.Values.Where(n => n.Row.PatientId == patient).ToList())
        {
            foreach (var recipient in notified.Recipients.Where(r => r.Person.Id == carer && r.State == RecipientState.InGrace))
                EndInApp(notified, recipient, "designation revoked", now, writes);
            foreach (var entry in EntriesOf(notified.Row.Id).Where(e => e is { Action: "send", Outcome: "" } && e.RecipientId == carer))
                Write(entry with { Action = "no-op", Reason = "designation revoked" }, writes);
            Settle(notified, now, writes);
        }
    }

    /// <summary>
    ///     Time passing: a recipient's grace over, their devices sent it; a send still waiting at its notification's
    ///     deadline, a no-op ("expired"): recorded, never sent.
    /// </summary>
    public void Tick(DateTime now, Writes writes)
    {
        foreach (var notified in _live.Values.ToList())
        {
            foreach (var recipient in notified.Recipients.Where(r => r.State == RecipientState.InGrace && r.GraceUntil <= now))
                Send(notified, recipient, now, writes);
            if (notified.Row.DeliverBy <= now)
                foreach (var entry in EntriesOf(notified.Row.Id).Where(e => e is { Action: "send", Outcome: "" }))
                    Write(entry with { Action = "no-op", Reason = $"expired: not handed off by {notified.Row.DeliverBy:HH:mm:ss}" },
                        writes);
            Settle(notified, now, writes);
        }
    }

    /// <summary>
    ///     A dispatcher's report of an entry (its shard's number): handed off, failed (a bad token deactivates its device)
    ///     or recorded (a no-op). Why not, if it isn't one awaiting a report.
    /// </summary>
    public string? Report(string dispatcher, int shard, long seq, string outcome, string reason, DateTime now, Writes writes)
    {
        if (shard is < 0 or >= Shards || !_entries.TryGetValue(Dispatch.IdOf(shard, seq), out var entry))
            return $"No entry {seq} in shard {shard}'s log.";
        if (entry.Outcome.Length > 0) return $"Entry {seq} of shard {shard} is {entry.Outcome} already.";
        if (outcome == "recorded" && entry.Action == "send") return $"Entry {seq} of shard {shard} is a send: nothing to record.";
        // (a hand-off that crossed the expiry, or an acknowledgement, is what it was: handed off, after all)
        Write(entry with
        {
            Outcome = outcome, ReportedAt = now, Dispatcher = dispatcher,
            Reason = outcome == "failed" ? reason : entry.Action == "no-op" && outcome != "recorded" ? $"{entry.Reason}; handed off anyway" : entry.Reason
        }, writes);
        if (outcome == "failed" && reason.Contains("token", StringComparison.OrdinalIgnoreCase) &&
            _devices.TryGetValue(entry.DeviceId, out var device) && device.Active)
            writes.Write(_devices[device.Id] = device with { Active = false });
        if (_live.TryGetValue(entry.NotificationId, out var notified)) Settle(notified, now, writes);
        return null;
    }

    /// <summary>
    ///     How the notification about an activity (a reply) went for its patient, their own part of it: <c>waiting</c>
    ///     (their app's open: its grace), <c>delivered</c> (to how many devices, when last), <c>seen</c> (in the app:
    ///     nothing pushed), <c>sending</c>, <c>expired</c>, <c>failed</c>, or <c>none</c> (no device of theirs); and its
    ///     deadline. Null if there's none.
    /// </summary>
    public (string State, int Delivered, DateTime At, DateTime DeliverBy)? DeliveryOf(long activity)
    {
        if (!_ofActivity.TryGetValue(activity, out var id)) return null;
        var row = _live.TryGetValue(id, out var notified) ? notified.Row : _ended[id];
        var mine = EntriesOf(row.Id).Where(e => e.RecipientId == row.PatientId).ToList();
        var handed = mine.Where(e => e.Outcome == "handed_off").ToList();
        var state = notified?.Recipients.Any(r => r.Person.Id == row.PatientId && r.State == RecipientState.InGrace) == true ? "waiting"
            : handed.Count > 0 ? "delivered"
            : mine.Any(e => e.Outcome == "" && e.Action == "send") ? "sending"
            : mine.Any(e => e.Reason.StartsWith("expired", StringComparison.Ordinal)) ? "expired"
            : mine.Any(e => e.Outcome == "failed") ? "failed"
            : mine.Count > 0 ? "seen" // (no-ops: seen, or acknowledged, in the app)
            : "none";
        return (state, handed.Count, handed.Count > 0 ? handed.Max(e => e.ReportedAt) : default, row.DeliverBy);
    }

    /// <summary>A person's devices that are active: where their notifications go.</summary>
    public IEnumerable<Device> DevicesOf(int person)
    {
        return _devices.Values.Where(d => d.PersonId == person && d.Active);
    }

    private IEnumerable<Dispatch> EntriesOf(string notification)
    {
        return (_entriesOf.GetValueOrDefault(notification) ?? []).Select(id => _entries[id]);
    }

    private void Send(Notified notified, Recipient recipient, DateTime now, Writes writes)
    {
        recipient.State = RecipientState.Sent;
        foreach (var device in DevicesOf(recipient.Person.Id))
            Entry(notified, recipient, device, "send", "", now, writes);
    }

    private void EndInApp(Notified notified, Recipient recipient, string why, DateTime now, Writes writes)
    {
        recipient.State = RecipientState.EndedInApp;
        foreach (var device in DevicesOf(recipient.Person.Id))
            Entry(notified, recipient, device, "no-op", why, now, writes);
    }

    /// <summary>A device's entry in the log: the next number in its recipient's shard's sequence.</summary>
    private void Entry(Notified notified, Recipient recipient, Device device, string action, string reason, DateTime now,
        Writes writes)
    {
        var shard = ShardOf(recipient.Person.Id);
        var seq = ++_sequence[shard];
        Write(new Dispatch(Dispatch.IdOf(shard, seq), shard, seq, notified.Row.Id, device.Id, recipient.Person.Id,
            recipient.Person.User, recipient.Person.Sites[0], recipient.ForCarer, action, reason, now, "", DateTime.MinValue, ""), writes);
    }

    private void Write(Dispatch entry, Writes writes)
    {
        if (_entries.TryAdd(entry.Id, entry))
        {
            if (!_entriesOf.TryGetValue(entry.NotificationId, out var ids)) _entriesOf[entry.NotificationId] = ids = [];
            ids.Add(entry.Id);
        }
        else
        {
            _entries[entry.Id] = entry;
        }

        writes.Write(entry);
    }

    /// <summary>
    ///     A notification's state, as its recipients' parts and its entries now have it: pending while any part waits for
    ///     the app; then ready while any entry awaits its report; then, every entry reported, what came of it all (handed
    ///     off if any send was, else failed, expired, or seen in the app; undeliverable if it was for nobody's device).
    ///     Ended, it's no longer tracked.
    /// </summary>
    private void Settle(Notified notified, DateTime now, Writes writes)
    {
        var entries = EntriesOf(notified.Row.Id).ToList();
        var state = notified.Recipients.Any(r => r.State == RecipientState.InGrace) ? "pending"
            : entries.Any(e => e.Outcome.Length == 0) ? "ready"
            : entries.Any(e => e.Outcome == "handed_off") ? "handed_off"
            : entries.Any(e => e.Outcome == "failed") ? "failed"
            : entries.Any(e => e.Reason.StartsWith("expired", StringComparison.Ordinal)) ? "expired"
            : entries.Count > 0 || notified.Recipients.All(r => r.State == RecipientState.EndedInApp) ? "seen_in_app"
            : "undeliverable";
        if (state != notified.Row.State)
        {
            notified.Row = notified.Row with { State = state, StateAt = now };
            writes.Write(notified.Row);
        }

        if (state is "pending" or "ready") return;
        _live.Remove(notified.Row.Id);
        _ended[notified.Row.Id] = notified.Row;
    }
}
