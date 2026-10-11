using ActivityFeed.Contracts;

namespace ActivityFeed.Service;

/// <summary>A visit, as the practice's own system tracks it: its booking, its sections, and how far it's got.</summary>
public sealed class Visit
{
    public required long Booking { get; init; }
    public required Person Patient { get; init; }
    public required Person Clinician { get; init; }
    public required Reason Reason { get; init; }
    public required DateTime Slot { get; init; }

    /// <summary>Its clinical content, one section per problem it addresses.</summary>
    public List<Section> Sections { get; } = [];

    /// <summary>Its reminder (the activity's sequence number), once sent, and whether the patient acknowledged it.</summary>
    public long Reminder { get; set; }

    public bool Acknowledged { get; set; }

    /// <summary>Whether a result of it has come back (its own section, for the GP to review and release).</summary>
    public bool Resulted { get; set; }

    public ActivityKind State { get; set; } = ActivityKind.Booked;
    public DateTime Since { get; set; }
    public int Ticket { get; set; }
    public string Room { get; set; } = "";
}

/// <summary>
///     Why a patient comes: a plain label (no licensed terminology), how often (against the others), whether it's
///     sensitive, what's found, the problem of theirs it's about (from their problem list), and the test whose result may
///     follow (and what it says).
/// </summary>
public sealed record Reason(
    string Name,
    int Weight,
    bool Sensitive,
    string Outcome,
    string Problem,
    string? Test = null,
    string? Result = null);

/// <summary>
///     What one step of the practice's day did: an activity, the sections it wrote (a visit booked, or seen), and the
///     acknowledgements it made (a patient acknowledging their reminder).
/// </summary>
public sealed record Happened(Activity? Activity, IReadOnlyList<Section> Sections, IReadOnlyList<Ack> Acks);

/// <summary>
///     The practice's day, simulated: patients book, are reminded, arrive, are called in and seen, at a steady pace (each
///     step one activity, or none); now and then a patient acknowledges a reminder or writes to the practice. A booking
///     writes the visit's section for the problem it's about; being seen writes what was found. Arrivals, calls and
///     acknowledgements happen on their own, except for the demo's patients (Jane Smith): theirs wait for commands, and
///     they're seen a minute after they're called. Ann Lowe books no visit at random: once live, she's booked and reminded
///     of one, and her old iPad's token is one the last mile no longer knows. Owned by the provider's loop.
/// </summary>
public sealed class Simulator(int seed = 7)
{
    /// <summary>The patients whose arrival waits for reception (a command): the demo's.</summary>
    public static readonly HashSet<int> Demo = [1001];

    /// <summary>Ann Lowe: booked and reminded once live, never at random (the dispatcher's refused token).</summary>
    private const int Ann = 1002;

    /// <summary>
    ///     Why patients come, the commonest most often: a rare reason's count stays under the manager's floor for hours.
    ///     The manager's statistics count visits by these names, in this order (<see cref="StatsRow.Reasons" />).
    /// </summary>
    private static readonly Reason[] Reasons =
    [
        new("Diabetes review", 14, false,
            "HbA1c 8.1%, up from 7.6% in March. Metformin continued at 1 g twice daily; diet discussed. Feet checked: pulses " +
            "present, sensation normal. Retinal screening booked. Review in three months, bloods a week before.",
            "Type 2 diabetes", "Blood test", "HbA1c 7.2%: improving"),
        new("Asthma check", 10, false, "Peak flow 410 L/min, inhaler technique good", "Asthma"),
        new("Blood pressure check", 14, false, "142/88: recheck in 4 weeks", "Hypertension", "Kidney function", "eGFR 78: normal"),
        new("Medication review", 16, false, "Repeat prescriptions renewed", "Medications"),
        new("Flu vaccination", 10, false, "Vaccine given, left arm", "Immunisation"),
        new("Wound dressing", 6, false, "Healing well, dressing changed", "Wound care"),
        new("Back pain", 8, false, "Mechanical back pain: physiotherapy referral", "Back pain"),
        new("Skin rash", 6, false, "Contact dermatitis: emollient advised", "Dermatitis"),
        new("Child immunisation", 6, false, "Routine immunisations given", "Immunisation"),
        new("Sexual health", 2, true, "Screening done, results to follow", "Sexual health", "Screening", "All negative"),
        new("Mental health review", 4, true,
            "Mood improving since the last review; sleeping better. Sertraline continued at 50 mg. Talking therapy: six of " +
            "eight sessions attended. Safety plan reviewed and agreed. Review in four weeks, sooner if needed.", "Low mood"),
        new("Pregnancy", 1, true, "Antenatal booking, bloods taken", "Pregnancy", "Antenatal bloods", "All normal")
    ];

    private static readonly int Weights = Reasons.Sum(r => r.Weight);

    /// <summary>The problems a patient may have, in a fixed order: a problem's id is the patient's id × 100 + its place.</summary>
    public static readonly string[] Problems = [.. Reasons.Select(r => r.Problem).Distinct()];

    static Simulator()
    {
        if (!Reasons.Select(r => r.Name).SequenceEqual(StatsRow.Reasons))
            throw new InvalidOperationException("The manager's statistics count visits by StatsRow.Reasons: the simulator's reasons are those, in that order.");
    }

    /// <summary>What patients write to the practice: administration, not health (reception reads it).</summary>
    private static readonly string[] Messages =
    [
        "Could I move my appointment to later in the week?", "Is my repeat prescription ready to collect?",
        "Could I have a fit note for work, please?", "I'll be ten minutes late today, sorry.",
        "Has my referral letter been sent?"
    ];

    private readonly Random _random = new(seed);
    private readonly Dictionary<int, int> _tickets = []; // by site: today's last
    private long _seq, _sectionSeq, _ackSeq;
    private bool _annReminded;
    private DateOnly _ticketDay;

    /// <summary>Every visit not yet seen, and those seen, by booking.</summary>
    public Dictionary<long, Visit> Visits { get; } = [];

    /// <summary>The next activity's sequence number.</summary>
    public long Next => _seq + 1;

    /// <summary>A patient's problem, as their problem list numbers it.</summary>
    public static int ProblemOf(Person patient, string problem)
    {
        return patient.Id * 100 + Array.IndexOf(Problems, problem);
    }

    /// <summary>A problem's title, from its id.</summary>
    public static string TitleOf(int problem)
    {
        return problem % 100 is var at && at < Problems.Length ? Problems[at] : $"problem {problem}";
    }

    /// <summary>
    ///     One step of the practice's day at <paramref name="now" />: an activity, or none. Live (not priming the history),
    ///     Jane always has a visit to come: the demo's to check in.
    /// </summary>
    public Happened? Step(DateTime now, bool live = true)
    {
        if (live && !Visits.Values.Any(v => Demo.Contains(v.Patient.Id) && v.State != ActivityKind.Seen))
            return Book(People.Find(1001)!, Round(now.AddMinutes(20)), now);
        if (live && Visits.Values.FirstOrDefault(v => Demo.Contains(v.Patient.Id) && v is
                { State: ActivityKind.Booked, Reminder: 0 }) is { } demo) // (the demo's reminder at once: hers to acknowledge)
            return Just(Remind(demo, now));
        if (live && Visits.Values.FirstOrDefault(v => Demo.Contains(v.Patient.Id) && v is
                { State: ActivityKind.Called, Resulted: false }) is { } tested) // (her blood test's result, as she's called in)
            return Result(tested, now);
        if (live && !_annReminded && People.Find(Ann) is { } ann) // (Ann's visit and its reminder, once: to her old iPad too)
        {
            if (Visits.Values.FirstOrDefault(v => v.Patient == ann && v is { State: ActivityKind.Booked, Reminder: 0 }) is { } due)
            {
                _annReminded = true;
                return Just(Remind(due, now));
            }

            return Book(ann, Round(now.AddMinutes(30)), now);
        }
        switch (_random.Next(24))
        {
            case 0 when Pick(v => v is { State: ActivityKind.Booked, Reminder: 0 } && !Demo.Contains(v.Patient.Id) &&
                                  v.Slot <= now.AddMinutes(30)) is { } due:
                return Just(Remind(due, now));
            case 1 when Pick(v => v is { Reminder: > 0, Acknowledged: false } && !Demo.Contains(v.Patient.Id)) is { } reminded:
                reminded.Acknowledged = true;
                return new Happened(null, [], [Ack(reminded.Reminder, reminded.Patient, "patient", "acknowledged", false, now)]);
            case 2 when People.Patients[_random.Next(People.Patients.Length)] is var writer && !Demo.Contains(writer.Id):
                return Just(Message(writer, Messages[_random.Next(Messages.Length)], now));
            case 3 when Pick(v => v is { State: ActivityKind.Seen, Resulted: false, Reason.Test: not null } &&
                                  !Demo.Contains(v.Patient.Id)) is { } seen:
                return Result(seen, now);
        }

        return _random.Next(4) switch
        {
            0 when Pick(v => v.State == ActivityKind.Booked && !Demo.Contains(v.Patient.Id) &&
                             v.Slot <= now.AddMinutes(10)) is { } booked => Just(Arrive(booked, now)),
            1 when Pick(v => v.State == ActivityKind.Arrived && v.Since <= now.AddSeconds(-45) &&
                             !Demo.Contains(v.Patient.Id)) is { } waiting => Just(Call(waiting, waiting.Clinician.Room, now)),
            2 when Pick(v => v.State == ActivityKind.Called && v.Since <= now.AddSeconds(-60)) is { } called =>
                Seen(called, now),
            _ => BookAny(now)
        };
    }

    private static Happened Just(Activity activity)
    {
        return new Happened(activity, [], []);
    }

    /// <summary>
    ///     A test's result, back: a section of its own for the visit's problem, its finding for the patient's GP to review
    ///     and release to them (until then, not theirs to read).
    /// </summary>
    private Happened Result(Visit visit, DateTime now)
    {
        visit.Resulted = true;
        var (reason, patient) = (visit.Reason, visit.Patient);
        var result = new Section(++_sectionSeq, visit.Booking, patient.Sites[0], patient.Id, patient.User, patient.Gp,
            ProblemOf(patient, reason.Problem), reason.Test ?? "Test", reason.Result ?? "", reason.Sensitive, now, "result",
            false);
        return new Happened(null, [result], []);
    }

    /// <summary>A visit's reminder, sent to its patient: theirs to acknowledge.</summary>
    private Activity Remind(Visit visit, DateTime now)
    {
        var reminder = Activity(visit, "reminded", now, "Reminder sent");
        visit.Reminder = reminder.Seq;
        return reminder;
    }

    /// <summary>A patient writes to the practice: a message of its own (its own number is its thread's).</summary>
    public Activity Message(Person patient, string text, DateTime now)
    {
        var seq = ++_seq;
        return new Activity(seq, "messaged", patient.Sites[0], patient.Gp, seq, now, now, 0, "", patient.Id, patient.User,
            patient.Name, patient.Born, patient.Sex.ToString(), text);
    }

    /// <summary>The practice's reply to a patient's message: an activity of the thread (the message's number its booking).</summary>
    public Activity Reply(Person patient, long thread, string text, DateTime now)
    {
        return new Activity(++_seq, "replied", patient.Sites[0], patient.Gp, thread, now, now, 0, "", patient.Id, patient.User,
            patient.Name, patient.Born, patient.Sex.ToString(), text);
    }

    /// <summary>An acknowledgement: someone saw, or acknowledged, an activity.</summary>
    public Ack Ack(long activity, Person person, string role, string kind, bool first, DateTime now)
    {
        return new Ack(++_ackSeq, activity, person.Id, person.Name, role, kind, first, now);
    }

    /// <summary>The demo's or reception's check-in: a ticket for the visit, its site's next today.</summary>
    public Activity Arrive(Visit visit, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        if (today != _ticketDay) // (a new day: tickets from 1 again)
        {
            _ticketDay = today;
            _tickets.Clear();
        }

        var site = visit.Patient.Sites[0];
        var ticket = _tickets[site] = _tickets.GetValueOrDefault(site) % 99 + 1;
        (visit.State, visit.Since, visit.Ticket) = (ActivityKind.Arrived, now, ticket);
        return Activity(visit, "arrived", now, "Checked in");
    }

    /// <summary>A clinician calls the patient in, to a room: how long they waited (from check-in) recorded with it.</summary>
    public Activity Call(Visit visit, string room, DateTime now)
    {
        var waited = (int)Math.Max(0, (now - visit.Since).TotalSeconds);
        (visit.State, visit.Since, visit.Room) = (ActivityKind.Called, now, room);
        return Activity(visit, "called", now, $"Called to {room}", waited);
    }

    /// <summary>Seen: the visit's sections written with what was found.</summary>
    private Happened Seen(Visit visit, DateTime now)
    {
        (visit.State, visit.Since) = (ActivityKind.Seen, now);
        // Seen visits are forgotten a while after: what the practice's system keeps of them is the activity.
        foreach (var old in Visits.Values.Where(v => v.State == ActivityKind.Seen && v.Since < now.AddHours(-3)).ToList())
            Visits.Remove(old.Booking);
        for (var i = 0; i < visit.Sections.Count; i++)
            visit.Sections[i] = visit.Sections[i] with { Detail = visit.Reason.Outcome };
        return new Happened(Activity(visit, "seen", now, "Seen"), [.. visit.Sections], []);
    }

    private Happened? BookAny(DateTime now)
    {
        var patient = People.Patients[_random.Next(People.Patients.Length)];
        if (Demo.Contains(patient.Id) || patient.Id == Ann ||
            Visits.Values.Any(v => v.Patient == patient && v.State != ActivityKind.Seen))
            return null;
        return Book(patient, Round(now.AddMinutes(5 + _random.Next(8) * 5)), now);
    }

    private static DateTime Round(DateTime slot) // (to the five minutes)
    {
        return new DateTime(slot.Year, slot.Month, slot.Day, slot.Hour, slot.Minute / 5 * 5, 0, slot.Kind);
    }

    /// <summary>
    ///     A whole visit, primed into the history: booked at <paramref name="booked" /> with <paramref name="clinician" />
    ///     (a GP the patient also sees), checked in, called and seen in the minutes after.
    /// </summary>
    public IEnumerable<Happened> Visited(Person patient, Person clinician, DateTime booked)
    {
        var happened = Book(patient, Round(booked.AddMinutes(5)), booked, clinician);
        yield return happened;
        var visit = Visits[happened.Activity!.Booking];
        yield return Just(Arrive(visit, booked.AddMinutes(4)));
        yield return Just(Call(visit, clinician.Room, booked.AddMinutes(5)));
        yield return Seen(visit, booked.AddMinutes(6));
    }

    /// <summary>A visit booked, and its section written: what it's for, its findings to come.</summary>
    private Happened Book(Person patient, DateTime slot, DateTime now, Person? clinician = null)
    {
        var reason = Demo.Contains(patient.Id) ? Reasons[0] : AnyReason(); // (Jane: her diabetes review)
        var visit = new Visit
        {
            Booking = Next, Patient = patient, Clinician = clinician ?? People.Find(patient.Gp)!, Reason = reason,
            Slot = slot, Since = now
        };
        visit.Sections.Add(new Section(++_sectionSeq, visit.Booking, patient.Sites[0], patient.Id, patient.User, visit.Clinician.Id,
            ProblemOf(patient, reason.Problem), reason.Name, "", reason.Sensitive, now));
        Visits[visit.Booking] = visit;
        return new Happened(Activity(visit, "booked", now, _random.Next(2) == 0 ? "Booked online" : "Booked by phone"),
            [.. visit.Sections], []);
    }

    /// <summary>A reason, as often as its weight says.</summary>
    private Reason AnyReason()
    {
        var at = _random.Next(Weights);
        foreach (var reason in Reasons)
            if ((at -= reason.Weight) < 0)
                return reason;
        return Reasons[^1];
    }

    private Visit? Pick(Func<Visit, bool> eligible)
    {
        var candidates = Visits.Values.Where(eligible).ToList();
        return candidates.Count == 0 ? null : candidates[_random.Next(candidates.Count)];
    }

    private Activity Activity(Visit visit, string kind, DateTime now, string note, int waited = 0)
    {
        var p = visit.Patient;
        return new Activity(++_seq, kind, p.Sites[0], visit.Clinician.Id, visit.Booking, visit.Slot, now, visit.Ticket,
            visit.Room, p.Id, p.User, p.Name, p.Born, p.Sex.ToString(), note, waited);
    }
}
