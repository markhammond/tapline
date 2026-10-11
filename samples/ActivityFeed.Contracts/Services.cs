using MagicOnion;
using MessagePack;

namespace ActivityFeed.Contracts;

/// <summary>
///     The practice service's commands, as the app forwards them: Tapline carries no requests. Each acts as a person, the
///     app vouching for them — on a page, the person its connection signed in as (a production app would pass a token
///     the service verifies) — is checked against what that
///     person may see, and, done, asks for a rendezvous on that person's feed with the correlation id given: the caller
///     has the change in front of it when the id comes back.
/// </summary>
public interface IPracticeCommands : IService<IPracticeCommands>
{
    /// <summary>The practice's people, as a persona picker shows them. Synthetic, every one.</summary>
    UnaryResult<Persona[]> PersonasAsync();

    /// <summary>Reception checks a patient in for a booking (the booking's sequence number): a ticket, and the screen says so.</summary>
    UnaryResult<CommandAnswer> ArriveAsync(int person, long booking, byte[] correlation, CommandOrder? order);

    /// <summary>A clinician calls a patient in from the waiting room, to a room.</summary>
    UnaryResult<CommandAnswer> CallAsync(int person, long booking, string room, byte[] correlation, CommandOrder? order);

    /// <summary>A patient acknowledges a reminder of theirs (the reminder's activity).</summary>
    UnaryResult<CommandAnswer> AcknowledgeAsync(int person, long activity, byte[] correlation);

    /// <summary>
    ///     The page reports what it has shown its person, in batches: the messages they've now read. Each is recorded once,
    ///     the first at the practice marked so.
    /// </summary>
    UnaryResult<CommandAnswer> SeenAsync(int person, long[] activities, byte[] correlation);

    /// <summary>A patient writes to the practice: a message of at most 120 bytes, administration (reception reads it).</summary>
    UnaryResult<CommandAnswer> MessageAsync(int person, string text, byte[] correlation, CommandOrder? order);

    /// <summary>
    ///     Reception replies to a patient's message (its activity): the practice's message to them, urgent, pushed to their
    ///     devices unless they see it in the app first.
    /// </summary>
    UnaryResult<CommandAnswer> ReplyAsync(int person, long message, string text, byte[] correlation, CommandOrder? order);

    /// <summary>A patient shares one of their tags (the problems it groups) with a GP: a consent, until withdrawn.</summary>
    UnaryResult<CommandAnswer> ShareAsync(int person, int gp, string tag, byte[] correlation);

    /// <summary>A patient withdraws a consent of theirs (their registration's too).</summary>
    UnaryResult<CommandAnswer> WithdrawAsync(int person, long consent, byte[] correlation);

    /// <summary>A GP breaks the glass for a patient: their whole record, for a while, the reason recorded.</summary>
    UnaryResult<CommandAnswer> BreakGlassAsync(int person, int patient, byte[] correlation);

    /// <summary>A GP, having reviewed a result of their patient's (its section), releases it to them.</summary>
    UnaryResult<CommandAnswer> ReleaseAsync(int person, long section, byte[] correlation);

    /// <summary>A patient revokes a carer's designation: what it showed the carer goes from their feed at once.</summary>
    UnaryResult<CommandAnswer> RevokeCarerAsync(int person, int carer, byte[] correlation);

    /// <summary>
    ///     The manager posts one of the practice's notices (<c>phones</c>, <c>hours</c>, <c>flu</c>) to a site's patients:
    ///     released to each as their budgets allow, by its deadline, or recorded expired.
    /// </summary>
    UnaryResult<CommandAnswer> NoticeAsync(int person, int site, string notice, byte[] correlation);

    /// <summary>
    ///     What the policy shows a person, and why, from ChalkQL's explanation of their statements (nothing run): each
    ///     table's rows as the policy filters them, and each column's disclosure, placeholder and floor.
    /// </summary>
    UnaryResult<Explanation> ExplainAsync(int person);

    /// <summary>A section's note in full, as the person may read it (an entitled read, as them): a row holds 120 bytes.</summary>
    UnaryResult<NoteAnswer> NoteAsync(int person, long section);

    /// <summary>
    ///     The access log as a person may read it (an entitled read, as them: the auditor role reads it, and nobody else
    ///     sees a line): who was shown a record (<paramref name="part" />, 0 an activity or 3 a section; <paramref name="seq" />),
    ///     or with none (0) the log's newest entries.
    /// </summary>
    UnaryResult<AuditAnswer> AuditAsync(int person, byte part, long seq);

    /// <summary>
    ///     A dispatcher reports a send handed off to the last mile (the entry its shard's sequence number names): the entry
    ///     leaves its topic, and once that's in front of the dispatcher (the rendezvous), a reconnect can't make it act on
    ///     the entry again.
    /// </summary>
    UnaryResult<CommandAnswer> HandedOffAsync(string dispatcher, int shard, long seq, byte[] correlation);

    /// <summary>A dispatcher reports a send the last mile refused, saying why: a bad token deactivates its device.</summary>
    UnaryResult<CommandAnswer> FailedAsync(string dispatcher, int shard, long seq, string reason, byte[] correlation);

    /// <summary>A dispatcher reports a no-op recorded in its log: nothing was sent, as the entry said.</summary>
    UnaryResult<CommandAnswer> RecordedAsync(string dispatcher, int shard, long seq, byte[] correlation);
}

/// <summary>
///     The app's service for clients of its router (the headless verifier, say): the personas, a person's feed bound on
///     the client's session (step 1 at the app's router), and the commands, forwarded to the practice service.
/// </summary>
public interface IPracticeFeed : IService<IPracticeFeed>
{
    UnaryResult<Persona[]> PersonasAsync();

    /// <summary>
    ///     Binds <paramref name="person" />'s feed on the session <paramref name="outletId" />.<paramref name="nonce" />: their
    ///     own topic (staff and patients), or their site's screen (a screen); <paramref name="feed" /> <c>"stats"</c>, a
    ///     manager's statistics. Answers its binding, or why not.
    /// </summary>
    UnaryResult<FeedBinding> SubscribeAsync(int outletId, uint nonce, int person, string? feed);

    UnaryResult<CommandAnswer> ArriveAsync(int person, long booking, byte[] correlation, CommandOrder? order);

    UnaryResult<CommandAnswer> CallAsync(int person, long booking, string room, byte[] correlation, CommandOrder? order);

    UnaryResult<CommandAnswer> AcknowledgeAsync(int person, long activity, byte[] correlation);

    UnaryResult<CommandAnswer> SeenAsync(int person, long[] activities, byte[] correlation);

    UnaryResult<CommandAnswer> MessageAsync(int person, string text, byte[] correlation, CommandOrder? order);

    UnaryResult<CommandAnswer> ReplyAsync(int person, long message, string text, byte[] correlation, CommandOrder? order);

    UnaryResult<CommandAnswer> ShareAsync(int person, int gp, string tag, byte[] correlation);

    UnaryResult<CommandAnswer> WithdrawAsync(int person, long consent, byte[] correlation);

    UnaryResult<CommandAnswer> BreakGlassAsync(int person, int patient, byte[] correlation);

    UnaryResult<CommandAnswer> ReleaseAsync(int person, long section, byte[] correlation);

    UnaryResult<CommandAnswer> RevokeCarerAsync(int person, int carer, byte[] correlation);

    UnaryResult<CommandAnswer> NoticeAsync(int person, int site, string notice, byte[] correlation);

    UnaryResult<Explanation> ExplainAsync(int person);

    UnaryResult<NoteAnswer> NoteAsync(int person, long section);

    UnaryResult<AuditAnswer> AuditAsync(int person, byte part, long seq);

    /// <summary>
    ///     Binds a shard's dispatcher topic (<c>notify:{shard}</c>) on the session, for the dispatcher named (which reports
    ///     what it does with each entry); an observer, named so, only watches.
    /// </summary>
    UnaryResult<FeedBinding> DispatchAsync(int outletId, uint nonce, int shard, string dispatcher);

    /// <summary>
    ///     Binds the write log's signal (<c>log</c>: the writer's, a last value) on the session, for the instance of the
    ///     practice service named, which follows the writer. Only the writer takes it: each other instance refuses, so it's
    ///     asked of each in turn. Answers its binding, or (no instance writes now) why not.
    /// </summary>
    UnaryResult<FeedBinding> FollowAsync(int outletId, uint nonce, string follower);

    UnaryResult<CommandAnswer> HandedOffAsync(string dispatcher, int shard, long seq, byte[] correlation);

    UnaryResult<CommandAnswer> FailedAsync(string dispatcher, int shard, long seq, string reason, byte[] correlation);

    UnaryResult<CommandAnswer> RecordedAsync(string dispatcher, int shard, long seq, byte[] correlation);
}

/// <summary>
///     Where a command falls in order, as its sender says: its ordering key (what it's ordered with: a patient's thread with
///     the practice, <c>thread:{patient}</c>; a visit's steps, <c>visit:{booking}</c>), the sender that numbered it (a page's
///     own id, say), and its number among that sender's commands with that key, from 1. The writer applies a sender's
///     commands with a key in their numbers' order, whatever order they come in: one that comes early waits for those before
///     it. A command without one (an acknowledgement, a page's report of what it showed) is in no order but its arrival's.
/// </summary>
[MessagePackObject]
public sealed class CommandOrder
{
    [Key(0)] public string Key { get; set; } = "";
    [Key(1)] public byte[] Sender { get; set; } = [];
    [Key(2)] public long Seq { get; set; }
}

/// <summary>A person of the practice, as a persona picker shows them.</summary>
[MessagePackObject]
public sealed class Persona
{
    [Key(0)] public int Id { get; set; }
    [Key(1)] public string Name { get; set; } = "";

    /// <summary>reception, clinician, manager, patient or screen.</summary>
    [Key(2)]
    public string Role { get; set; } = "";

    /// <summary>The site they work at, are registered at, or stand in; none for someone across sites (the manager).</summary>
    [Key(3)]
    public string? Site { get; set; }

    /// <summary>What they're shown, in a line.</summary>
    [Key(4)]
    public string About { get; set; } = "";

    /// <summary>A clinician's room: where they call patients in to.</summary>
    [Key(5)]
    public string? Room { get; set; }

    /// <summary>A patient's own tags: names they've given groups of their problems ("My diabetes"), to share by.</summary>
    [Key(6)]
    public string[]? Tags { get; set; }
}

/// <summary>
///     What the policy shows a person: whose principal and purpose their statements run as, and each statement's tables
///     as ChalkQL explains them (or why not).
/// </summary>
[MessagePackObject]
public sealed class Explanation
{
    [Key(0)] public string Principal { get; set; } = "";
    [Key(1)] public string Purpose { get; set; } = "";
    [Key(2)] public ExplainedStatement[] Statements { get; set; } = [];
    [Key(3)] public string? Refused { get; set; }
}

/// <summary>One of a person's statements: what it's for, its plan, and the tables it reads, as the policy resolved them.</summary>
[MessagePackObject]
public sealed class ExplainedStatement
{
    [Key(0)] public string Name { get; set; } = "";
    [Key(1)] public ulong Plan { get; set; }
    [Key(2)] public ExplainedSource[] Tables { get; set; } = [];
}

/// <summary>A table a statement reads: its rows as the policy filters them (the folded predicate), and its columns.</summary>
[MessagePackObject]
public sealed class ExplainedSource
{
    [Key(0)] public string Table { get; set; } = "";

    /// <summary>The folded row predicate, its columns by name; empty if every row is visible.</summary>
    [Key(1)]
    public string Rows { get; set; } = "";

    [Key(2)] public ExplainedField[] Columns { get; set; } = [];
}

/// <summary>
///     A column, as the policy discloses it: a name where the fold settled it (FULL, REDACTED, AGGREGATE), else the
///     conditions, row by row; its placeholder, its mask, and the floor its counts take.
/// </summary>
[MessagePackObject]
public sealed class ExplainedField
{
    [Key(0)] public string Column { get; set; } = "";
    [Key(1)] public string Disclosure { get; set; } = "";
    [Key(2)] public string Placeholder { get; set; } = "";
    [Key(3)] public string Mask { get; set; } = "";
    [Key(4)] public int Floor { get; set; }
}

/// <summary>A section's note, as the person may read it: the text (or its placeholder) and what the policy disclosed.</summary>
[MessagePackObject]
public sealed class NoteAnswer
{
    [Key(0)] public string Text { get; set; } = "";
    [Key(1)] public string Disclosed { get; set; } = "";
    [Key(2)] public string? Refused { get; set; }
}

/// <summary>
///     The access log as a person read it (the audit page): as which principal and for what purpose (the audit records
///     both), its lines, or why not. To anyone but the auditor role it holds no line.
/// </summary>
[MessagePackObject]
public sealed class AuditAnswer
{
    [Key(0)] public string Principal { get; set; } = "";
    [Key(1)] public string Purpose { get; set; } = "";
    [Key(2)] public AuditRow[] Rows { get; set; } = [];
    [Key(3)] public string? Refused { get; set; }
}

/// <summary>
///     A line of the access log: a person (and their topic) shown a record, an activity (part 0) or a section (part 3),
///     whose it is (the patient, the visit, the GP attending it), when, and why it was disclosed to them. A record's lines are one per person and topic, first and last, with how many posts carried
///     it; the newest entries, one each (their numbers).
/// </summary>
[MessagePackObject]
public sealed class AuditRow
{
    [Key(0)] public long Seq { get; set; }
    [Key(1)] public int Person { get; set; }
    [Key(2)] public string User { get; set; } = "";
    [Key(3)] public string Topic { get; set; } = "";
    [Key(4)] public byte Part { get; set; }
    [Key(5)] public long Record { get; set; }
    [Key(6)] public int Patient { get; set; }
    [Key(7)] public DateTime First { get; set; }
    [Key(8)] public DateTime Last { get; set; }
    [Key(9)] public long Posts { get; set; }

    /// <summary>The record's visit (its booking): 0 for none, or an entry written before entries named it.</summary>
    [Key(10)] public long Booking { get; set; }

    /// <summary>The GP attending that visit: 0 for none.</summary>
    [Key(11)] public int Clinician { get; set; }

    /// <summary>Why its patient was disclosed to them (<see cref="DisclosureBasis" />): none for an entry written before entries said.</summary>
    [Key(12)] public DisclosureBasis Basis { get; set; }
}

/// <summary>What became of a command: done, or refused, saying why.</summary>
[MessagePackObject]
public sealed class CommandAnswer
{
    [Key(0)] public bool Done { get; set; }
    [Key(1)] public string? Refused { get; set; }

    /// <summary>The activity it wrote: its sequence number.</summary>
    [Key(2)]
    public long Seq { get; set; }

    /// <summary>Not taken: the instance asked doesn't write the practice's records (another does): ask it.</summary>
    [Key(3)]
    public bool NotWriter { get; set; }
}

/// <summary>
///     A feed's step-1 answer: its tap's binding, or that the provider refused it (why, and the needle), the service
///     isn't up yet, or the session is unknown.
/// </summary>
[MessagePackObject]
public sealed class FeedBinding
{
    [Key(0)] public uint TapId { get; set; }
    [Key(1)] public string? Topic { get; set; }
    [Key(2)] public string? Service { get; set; }

    /// <summary>The router's transient id for the provider instance holding it: what its health notices name.</summary>
    [Key(3)]
    public Guid? NodeId { get; set; }

    /// <summary>The partition of the service it's in, if the provider put it in one: ids of the router's run, never names.</summary>
    [Key(4)]
    public string? Partition { get; set; }

    /// <summary>The provider refused it, saying why.</summary>
    [Key(5)]
    public string? Refused { get; set; }

    /// <summary>The refusal's correlation id: the provider and the router logged it with this.</summary>
    [Key(6)]
    public Guid? CorrelationId { get; set; }

    /// <summary>The service isn't up yet: ask again shortly.</summary>
    [Key(7)]
    public bool Unavailable { get; set; }

    /// <summary>The session named isn't held: connect again, then ask.</summary>
    [Key(8)]
    public bool UnknownSession { get; set; }
}

/// <summary>
///     Who's asking: every client signs in to the practice's realm by Tapline's built-in passthrough — a person as their
///     id (<c>"101"</c>), a dispatcher or a follower of the log as its name — before its step 1, which the practice
///     service requires; it serves a person their own feed alone. Passthrough takes the id as given (an insecure
///     identity, marked so): a demo's, on this machine.
/// </summary>
public static class PracticeSignIn
{
    public const string Realm = "practice", Module = "passthrough";

    /// <summary>A person's user id, as they sign in.</summary>
    public static string UserOf(int person)
    {
        return person.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
