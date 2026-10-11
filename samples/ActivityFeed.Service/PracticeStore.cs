using System.Security.Cryptography;
using System.Text;
using ActivityFeed.Contracts;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.Sources.Ado;
using Chalk.Sources.Poco;
using Microsoft.Data.Sqlite;
using CTable = Chalk.Entitlements.Tenancy.Table;
using TRole = Chalk.Entitlements.Tenancy.Role;

namespace ActivityFeed.Service;

/// <summary>
///     An activity at the practice, as ChalkQL holds it (<c>activities</c>, append-only: each row as it happened): the
///     schedule, who, when and with whom, and its note ("Booked online", "Checked in"); a call, how long the patient
///     waited (seconds, from check-in). It carries a copy of the patient's identifying fields, so the row a class sees
///     needs no join. No clinical content: that's the visit's <see cref="Section" />s. <c>occurred_at</c>, not <c>at</c>:
///     a reserved word to the planner's parser.
/// </summary>
public sealed record Activity(
    long Seq,
    string Kind,
    int SiteId,
    int ClinicianId,
    long Booking,
    DateTime Slot,
    DateTime OccurredAt,
    int Ticket,
    string Room,
    int PatientId,
    string PatientUser,
    string PatientName,
    int Born,
    string Sex,
    string Note,
    int Waited = 0);

/// <summary>
///     A patient's clinical content for one problem (<c>sections</c>, keyed): a visit's (what it was for, and, once the
///     patient is seen, what was found), or a result's (a test's finding, which their GP reviews and releases to them).
///     Read by the GP who attended it (a result's: the GP who reviews it), a GP it's shared with, in an emergency, by a
///     carer it's granted to, and by the patient: a result's finding once it's released. <c>is_sensitive</c>, not
///     <c>sensitive</c>: a reserved word to the parser.
/// </summary>
public sealed record Section(
    long Seq,
    long Booking,
    int SiteId,
    int PatientId,
    string PatientUser,
    int AttendingId,
    int ProblemId,
    string Reason,
    string Detail,
    bool IsSensitive,
    DateTime WrittenAt,
    string Kind = "visit",
    bool Released = true);

/// <summary>
///     A section granted to someone (<c>section_grants</c>): to a GP, a row for each section a consent of theirs covers;
///     to a carer, for each their designation's scope allows. Written by the service, and deleted when the share, or
///     the designation, no longer holds. It only says that the section is theirs to read: the history is the
///     consents' and the designations'.
/// </summary>
public sealed record SectionGrant(long Id, long SectionSeq, int GranteeId)
{
    public static SectionGrant Of(long section, int grantee)
    {
        return new SectionGrant(section * 10_000 + grantee, section, grantee);
    }
}

/// <summary>
///     An activity granted to a carer (<c>activity_grants</c>): a row for each of their patient's activities their scope
///     allows (none of a sensitive visit's, without the sensitive scope: hidden, not masked).
/// </summary>
public sealed record ActivityGrant(long Id, long ActivitySeq, int GranteeId)
{
    public static ActivityGrant Of(long activity, int grantee)
    {
        return new ActivityGrant(activity * 10_000 + grantee, activity, grantee);
    }
}

/// <summary>Someone the practice grants rows to (<c>grantees</c>): a GP, or a carer. Where a grant's path ends.</summary>
public sealed record Grantee(int Id);

/// <summary>
///     An acknowledgement (<c>acks</c>, append-only: the auditable record of who saw or acknowledged what, and when): a
///     reminder acknowledged by its patient, a message read by the practice. Entitled through its activity: visible
///     exactly when the activity is, by every way into it. Who it was is a staff realm a patient doesn't read.
///     <c>person_role</c>, <c>is_first</c> and <c>occurred_at</c>, not <c>role</c>, <c>first</c> and <c>at</c>: words the
///     planner's parser reserves.
/// </summary>
public sealed record Ack(
    long Seq,
    long ActivitySeq,
    int PersonId,
    string PersonName,
    string PersonRole,
    string Kind,
    bool IsFirst,
    DateTime OccurredAt);

/// <summary>
///     A person's device, as the practice's app registered it (<c>devices</c>): its type and label, and its push token,
///     which only a dispatcher reads (a <c>device</c> realm). A token the last mile refused is inactive.
/// </summary>
public sealed record Device(
    long Id,
    int PersonId,
    string PersonUser,
    int SiteId,
    string DeviceType,
    string Label,
    string ApnToken,
    bool Active,
    DateTime RegisteredAt);

/// <summary>
///     A notification (<c>notifications</c>), about a patient: their activity (a call in, a reminder) or their result,
///     transactional; or the practice's notice, as it's released to them (counted against their budgets). It goes to
///     them, and a transactional one to each carer whose designation shows what it's about. Its class and urgency; its
///     title, and a carer's, free of personal and health information; when it's valid until, and its deadline for being
///     handed off; its state (<c>pending</c>, <c>ready</c>, <c>seen_in_app</c>, <c>handed_off</c>, <c>failed</c>,
///     <c>expired</c>, <c>bundled</c>: delivered in another's push, <c>opted_out</c>, <c>undeliverable</c>: for no
///     device) and when it got there.
/// </summary>
public sealed record Notification(
    string Id,
    int PatientId,
    string PatientUser,
    int SiteId,
    long ActivitySeq,
    long SectionSeq,
    string Kind,
    string Class,
    bool Urgent,
    string Title,
    string CarerTitle,
    DateTime CreatedAt,
    DateTime ValidUntil,
    DateTime DeliverBy,
    string State,
    DateTime StateAt,
    string NoticeId = "");

/// <summary>
///     The practice's notice to a cohort (<c>notices</c>): its site's patients seen in the past year. Operational (how
///     the practice runs) or informational (its news, which a patient may opt out of), urgent or not; its title, the same
///     for everyone and saying nothing of why anyone's in the cohort; valid until, and its deadline for being handed off.
///     Released to each patient as their budgets allow.
/// </summary>
public sealed record NoticeRow(
    string Id,
    int SiteId,
    string Kind,
    string Class,
    bool Urgent,
    string Title,
    DateTime CreatedAt,
    DateTime ValidUntil,
    DateTime DeliverBy,
    int PostedBy);

/// <summary>
///     One entry of the dispatch log (<c>dispatches</c>): a notification for one device of one recipient, numbered in
///     its shard's sequence (the service's own: Tapline's are the router's, and don't last). What to do (<c>send</c>, or
///     <c>no-op</c> and why), and what came of it (<c>handed_off</c>, <c>failed</c> or <c>recorded</c>; empty while
///     pending), when, and which dispatcher said so. Every notification has one per device it was meant for.
///     <c>written_at</c>, not <c>at</c>: a reserved word to the parser.
/// </summary>
public sealed record Dispatch(
    long Id,
    int Shard,
    long Seq,
    string NotificationId,
    long DeviceId,
    int RecipientId,
    string RecipientUser,
    int SiteId,
    bool ForCarer,
    string Action,
    string Reason,
    DateTime WrittenAt,
    string Outcome,
    DateTime ReportedAt,
    string Dispatcher)
{
    public static long IdOf(int shard, long seq)
    {
        return shard * 1_000_000_000_000L + seq;
    }
}

/// <summary>
///     An entry of the access log (append-only, in its own store: <see cref="AccessLogSink" />): a record (an activity, or a
///     section) made available to someone, posted to their topic. Whose record it is — the patient, the visit (its booking)
///     and the GP attending it, never what it was for — who was shown it, when, and why (its disclosure's basis). "Available",
///     not "seen": the router may conflate a version away before a slow client gets it, so the log errs on the side of
///     saying too much. A struct: a turn's entries, and the store's batches, allocate nothing each.
/// </summary>
public readonly record struct Shown(
    long Seq,
    int PersonId,
    string PersonUser,
    string Topic,
    int Part,
    long RowSeq,
    int PatientId,
    int SiteId,
    DateTime ShownAt,
    long Booking,
    int ClinicianId,
    DisclosureBasis Basis = DisclosureBasis.None);

/// <summary>
///     A class's statement, and the values its own parameters take, by name, in the order they first appear in it (a GP's
///     id, their emergency's patients): bound by name for the life of the class's views, by position when it's run whole.
/// </summary>
public sealed record ClassStatement(string Sql, IReadOnlyList<KeyValuePair<string, object?>> Parameters);

/// <summary>
///     The practice's tenancy policy (ChalkQL's tenancy package), declared once over the catalog. Sites are tenancies,
///     patients and clinicians subjects within them; a GP a section is shared with is a <c>grantee</c>. Who the patient is
///     (<c>pii</c>), an activity's note (<c>admin</c>), a section's content (<c>clinical</c>), who at the practice
///     acknowledged or read something (<c>staff</c>) and a device's push token (<c>device</c>) are realms.
///     <list type="bullet">
///         <item>
///             The schedule (<c>activities</c>): a patient's own rows, in full (their resource owner's); reception, a
///             site's GPs, the GP of the visit, and in an emergency, who and what's noted, in full. The manager: who
///             (need to know, to run the day), not what's noted. The screen: nothing protected (no rule grants it).
///         </item>
///         <item>
///             A visit's sections: their content visible only to the GP who attended it (<c>attending</c>: a grant on
///             their own id, as a clinician, anywhere), a GP it's shared with (<c>consented</c>: their own id, as a
///             grantee, along the section's path through <c>section_grants</c>), in an emergency, and to the patient, in
///             full to each of them. The manager counts them by reason, never reading one, each count under a floor of
///             five withheld. Reception reads no section at all.
///         </item>
///         <item>
///             Notifications, devices and the dispatch log: a dispatcher's (<c>dispatcher</c>), a push token among them,
///             but nobody's identity; a person's own devices and notifications, the token aside. Nobody else's at all.
///         </item>
///     </list>
/// </summary>
public sealed class PracticePolicy
{
    private PracticePolicy(TenancyPolicy policy)
    {
        Policy = policy;
    }

    public TenancyPolicy Policy { get; }
    public Kind Site { get; private set; }
    public Kind Grantee { get; private set; }
    public SubjectKind Patient { get; private set; }
    public SubjectKind Clinician { get; private set; }
    public TRole Reception { get; private set; }
    public TRole Gp { get; private set; }
    public TRole Attending { get; private set; }
    public TRole Consented { get; private set; }
    public TRole Emergency { get; private set; }
    public TRole Manager { get; private set; }
    public TRole Screen { get; private set; }
    public TRole Carer { get; private set; }
    public TRole Dispatcher { get; private set; }
    public TRole Notifier { get; private set; }
    public TRole Auditor { get; private set; }

    public static PracticePolicy Declare(CatalogContext catalog)
    {
        var p = TenancyPolicy.Declare(catalog);
        var self = new PracticePolicy(p);
        self.Site = p.Tenancy("site");
        self.Grantee = p.Tenancy("grantee"); // a GP or a carer rows are granted to: where a grant's path ends
        self.Patient = p.Subject("patient", [self.Site]);
        self.Clinician = p.Subject("clinician", [self.Site]); // the GP who attended
        var pii = p.Realm("pii"); // who the patient is
        var admin = p.Realm("admin"); // what the schedule notes
        var clinical = p.Realm("clinical"); // what they're seen for, and what was found
        var staff = p.Realm("staff"); // who at the practice acknowledged or read it
        var device = p.Realm("device"); // a device's push token
        (self.Reception, self.Gp, self.Attending, self.Consented, self.Emergency, self.Manager, self.Screen, self.Carer) = (
            p.Role("reception"), p.Role("gp"), p.Role("attending"), p.Role("consented"), p.Role("emergency"),
            p.Role("manager"), p.Role("screen"), p.Role("carer"));
        self.Dispatcher = p.Role("dispatcher");
        self.Notifier = p.Role("notifier"); // the service's release job: who's in a cohort, and what's been sent them
        self.Auditor = p.Role("auditor"); // the practice's privacy officer: who was shown what
        var store = p.Source(PracticeStore.Schema);

        // What's granted, as data: a row per section or activity, and grantee, along a path from the row up to its grant
        // rows and down to the grantee. A grantee reads only their own grant rows; the path reads them for existence.
        CTable grantees = store.Table("grantees");
        grantees.Tenancy(r => r.Direct(self.Grantee, grantees.Column("id")));
        CTable grants = store.Table("section_grants");
        grants.Tenancy(r => r.Direct(self.Grantee, grants.Column("grantee_id")));
        CTable activityGrants = store.Table("activity_grants");
        activityGrants.Tenancy(r => r.Direct(self.Grantee, activityGrants.Column("grantee_id")));

        CTable activities = store.Table("activities");
        activities
            .Tenancy(r => r
                .Direct(self.Site, activities.Column("site_id"))
                .Direct(self.Patient, activities.Column("patient_id")) // in an emergency, wherever
                .Direct(self.Clinician, activities.Column("clinician_id")) // a GP's own visits, wherever
                .ResourceOwner(activities.Column("patient_user")) // a patient's own rows, wherever they are
                .Related(self.Grantee).Through(activityGrants).Through(grantees)) // a carer's, granted
            .Realm(pii, activities.Column("patient_id"), activities.Column("patient_user"), activities.Column("patient_name"),
                activities.Column("born"), activities.Column("sex"))
            .Realm(admin, activities.Column("note"))
            .Access(new AccessRule { Roles = [Roles.Owner], Grants = Verdict.Full })
            .Access(new AccessRule
                { Roles = [self.Reception, self.Gp, self.Attending, self.Emergency, self.Carer], Grants = Verdict.Full })
            .Access(new AccessRule { Roles = [self.Notifier], Realm = pii, Grants = Verdict.Full }) // (who: a cohort's)
            .Access(new AccessRule { Roles = [self.Manager], Realm = pii, Grants = Verdict.Full }); // (who: on the schedule, need to know)

        // Acknowledgements, through their activity: visible exactly when it is. Who it was, to the practice's own.
        CTable acks = store.Table("acks");
        acks.Tenancy(r => r.Through(acks.Column("activity_seq")))
            .Realm(staff, acks.Column("person_id"), acks.Column("person_name"))
            .Access(new AccessRule
                { Roles = [self.Reception, self.Gp, self.Attending, self.Emergency], Realm = staff, Grants = Verdict.Full });

        // A visit's clinical content, one section per problem. The site's roles reach a section, but its visibility rule
        // admits none of them but the manager: a site's grants show no section, and the manager only counts.
        CTable sections = store.Table("sections");
        sections
            .Tenancy(r => r
                .Direct(self.Site, sections.Column("site_id")) // (the encounter's site: what confines its subjects)
                .Direct(self.Clinician, sections.Column("attending_id")) // attended by them
                .Direct(self.Patient, sections.Column("patient_id")) // in an emergency
                .ResourceOwner(sections.Column("patient_user"), ResourceOwnerSees.ByRules) // the patient's own, as its rules say
                .Related(self.Grantee).Through(grants).Through(grantees)) // shared with them, or granted to their carer
            .Visible(new VisibilityRule { Roles = [self.Attending, self.Consented, self.Emergency, self.Carer, self.Manager] })
            .Realm(pii, sections.Column("patient_id"), sections.Column("patient_user"))
            .Realm(clinical, sections.Column("problem_id"), sections.Column("reason"), sections.Column("detail"),
                sections.Column("is_sensitive"))
            // The patient's own: a result's finding once their GP has released it, what it's of from the start.
            .Access(new AccessRule
                { Roles = [Roles.Owner], Column = sections.Column("detail"), Grants = Verdict.Full, When = Sql.Of("released") })
            .Access(new AccessRule
            {
                Roles = [Roles.Owner], Column = sections.Column("detail"), Grants = Verdict.None,
                Placeholder = Sql.Of("'Awaiting review by your GP'")
            })
            .Access(new AccessRule { Roles = [Roles.Owner], Realm = clinical, Grants = Verdict.Full })
            .Access(new AccessRule { Roles = [Roles.Owner], Realm = pii, Grants = Verdict.Full })
            .Access(new AccessRule
            {
                Roles = [self.Attending, self.Consented, self.Emergency, self.Carer], Realm = clinical, Grants = Verdict.Full
            })
            .Access(new AccessRule
                { Roles = [self.Attending, self.Consented, self.Emergency, self.Carer], Realm = pii, Grants = Verdict.Full })
            // The manager: a reason counted (a test of it, as a count's FILTER), never read; a count under five withheld.
            // Nothing else protected: no rule, no value.
            .Access(new AccessRule
            {
                Roles = [self.Manager], Column = sections.Column("reason"), Grants = Verdict.AggregateOnly,
                Aggregates = [Aggregate.Count], Tests = [Test.Equals], MinGroupSize = PracticeStore.StatsFloor
            });

        // Notifications, devices and the dispatch log: a dispatcher's, the token included (no rule for who: nothing of
        // it); their person's own, the token aside (no owner's rule for it). No staff role reaches a row of them.
        CTable devices = store.Table("devices");
        devices
            .Tenancy(r => r
                .Direct(self.Site, devices.Column("site_id"))
                .ResourceOwner(devices.Column("person_user"), ResourceOwnerSees.ByRules))
            .Visible(new VisibilityRule { Roles = [self.Dispatcher] })
            .Realm(pii, devices.Column("person_id"), devices.Column("person_user"))
            .Realm(device, devices.Column("apn_token"))
            .Access(new AccessRule { Roles = [Roles.Owner], Realm = pii, Grants = Verdict.Full })
            .Access(new AccessRule { Roles = [self.Dispatcher], Realm = device, Grants = Verdict.Full });
        CTable notifications = store.Table("notifications");
        notifications
            .Tenancy(r => r
                .Direct(self.Site, notifications.Column("site_id"))
                .ResourceOwner(notifications.Column("patient_user"), ResourceOwnerSees.ByRules))
            .Visible(new VisibilityRule { Roles = [self.Dispatcher, self.Notifier] })
            .Realm(pii, notifications.Column("patient_id"), notifications.Column("patient_user"))
            .Access(new AccessRule { Roles = [Roles.Owner, self.Notifier], Realm = pii, Grants = Verdict.Full });
        CTable dispatches = store.Table("dispatches");
        dispatches
            .Tenancy(r => r
                .Direct(self.Site, dispatches.Column("site_id"))
                .ResourceOwner(dispatches.Column("recipient_user"), ResourceOwnerSees.ByRules))
            .Visible(new VisibilityRule { Roles = [self.Dispatcher, self.Notifier] })
            .Realm(pii, dispatches.Column("recipient_id"), dispatches.Column("recipient_user"))
            .Access(new AccessRule { Roles = [Roles.Owner, self.Notifier], Realm = pii, Grants = Verdict.Full });
        // A notice is the practice's, to many: nothing in it is anyone's.
        CTable notices = store.Table("notices");
        notices.Tenancy(r => r.Direct(self.Site, notices.Column("site_id")));

        // The access log, in a database of its own (the audit source): the auditor's, whose record was shown to whom;
        // nobody else's.
        CTable accessLog = p.Source(PracticeStore.AuditSchema).Table("access_log");
        accessLog
            .Tenancy(r => r.Direct(self.Site, accessLog.Column("site_id")))
            .Visible(new VisibilityRule { Roles = [self.Auditor] })
            .Realm(pii, accessLog.Column("person_id"), accessLog.Column("person_user"), accessLog.Column("patient_id"))
            .Access(new AccessRule { Roles = [self.Auditor], Realm = pii, Grants = Verdict.Full });
        return self;
    }

    public TenancyEntitlements Compile(CatalogContext catalog)
    {
        return Policy.Compile(new CatalogContext
        {
            ContextId = catalog.ContextId, Epoch = catalog.Epoch, Schemas = catalog.Schemas,
            Associations = Policy.Associations
        });
    }

    /// <summary>
    ///     What a person holds, from the practice's own records (never from a request): staff a role at each site they
    ///     work at; a GP also the visits they attended (their own id, as a clinician, anywhere), what's shared with them
    ///     (their own id, as a grantee: the shares themselves are rows), and each patient they've broken the glass for;
    ///     a screen its site's <c>screen</c> role; a patient nothing (their own rows are theirs as the resource owner).
    /// </summary>
    public Grant[] GrantsOf(Person person, IReadOnlyCollection<int>? emergencies = null)
    {
        return person.Role switch
        {
            Role.Reception => [.. person.Sites.Select(s => Grant.ForTenancy(Site, s, Reception))],
            Role.Manager => [.. person.Sites.Select(s => Grant.ForTenancy(Site, s, Manager))],
            Role.Screen => [.. person.Sites.Select(s => Grant.ForTenancy(Site, s, Screen))],
            Role.Carer => [Grant.ForTenancy(Grantee, person.Id, Carer)], // (what they're granted is rows)
            Role.Auditor => [.. person.Sites.Select(s => Grant.ForTenancy(Site, s, Auditor))],
            Role.Clinician =>
            [
                .. person.Sites.Select(s => Grant.ForTenancy(Site, s, Gp)),
                Grant.ForSubject(Clinician, person.Id, Attending, Chalk.Entitlements.Tenancy.Tenancy.Anywhere),
                Grant.ForTenancy(Grantee, person.Id, Consented),
                .. (emergencies ?? []).Order().Select(p =>
                    Grant.ForSubject(Patient, p, Emergency, Chalk.Entitlements.Tenancy.Tenancy.Anywhere))
            ],
            _ => []
        };
    }

    /// <summary>
    ///     A person's entitlement class: who else holds exactly their grants. Reception at a site is one class; a GP is a
    ///     class of their own (their id is in their grants), another while they've broken the glass; a patient is never in
    ///     one.
    /// </summary>
    public static string ClassOf(Person person, IReadOnlyCollection<int>? emergencies = null)
    {
        var sites = string.Join(',', person.Sites);
        return person.Role switch
        {
            Role.Clinician => $"gp:{person.Id}@{sites}" + (emergencies is { Count: > 0 } e
                ? $"+emergency#{Digest(string.Join(',', e.Order()))}"
                : ""),
            _ => $"{person.Role.ToString().ToLowerInvariant()}@{sites}"
        };
    }

    /// <summary>
    ///     A class's principal: the class's grants, as one principal (so its plan, and its view, are every member's), with
    ///     the purpose its audit records: care, or, while the glass is broken, emergency. It owns nothing (no user: no owner
    ///     test in its plan), and its audit names the class.
    /// </summary>
    public TenancyPrincipal ForClass(Person member, IReadOnlyCollection<int>? emergencies = null)
    {
        var cls = ClassOf(member, emergencies);
        return new TenancyPrincipal
        {
            User = null, Grants = GrantsOf(member, emergencies), MaskKey = "practice",
            Purpose = PurposeOf(member, emergencies), Actor = $"class:{cls}"
        };
    }

    /// <summary>
    ///     A dispatcher's principal: the dispatcher role at every site (it serves the whole practice), owning nothing, with
    ///     the purpose its audit records.
    /// </summary>
    public TenancyPrincipal ForDispatcher()
    {
        return new TenancyPrincipal
        {
            User = null, Grants = [.. People.Sites.Select(s => Grant.ForTenancy(Site, s.Id, Dispatcher))],
            MaskKey = "practice", Purpose = "notification", Actor = "service:dispatcher"
        };
    }

    /// <summary>
    ///     The release job's principal: the notifier role at every site: who's in a notice's cohort, and what's been sent
    ///     to whom (their budgets), but no note, nothing clinical, and no token. It owns nothing.
    /// </summary>
    public TenancyPrincipal ForNotifier()
    {
        return new TenancyPrincipal
        {
            User = null, Grants = [.. People.Sites.Select(s => Grant.ForTenancy(Site, s.Id, Notifier))],
            MaskKey = "practice", Purpose = "notification release", Actor = "service:notifier"
        };
    }

    /// <summary>
    ///     The service's own auditor: the auditor role at every site, owning nothing, its purpose audit (the operator's
    ///     <c>/access</c>, and the provider's view). A person who audits (<see cref="Role.Auditor" />) is their own principal.
    /// </summary>
    public TenancyPrincipal ForAuditor()
    {
        return new TenancyPrincipal
        {
            User = null, Grants = [.. People.Sites.Select(s => Grant.ForTenancy(Site, s.Id, Auditor))],
            MaskKey = "practice", Purpose = "audit", Actor = "service:auditor"
        };
    }

    /// <summary>A person's own principal: their grants, as themselves, with the purpose their audit records.</summary>
    public TenancyPrincipal For(Person person, IReadOnlyCollection<int>? emergencies = null)
    {
        return new TenancyPrincipal
        {
            User = person.User, Grants = GrantsOf(person, emergencies), MaskKey = "practice",
            Purpose = PurposeOf(person, emergencies), Actor = person.User
        };
    }

    private static string PurposeOf(Person person, IReadOnlyCollection<int>? emergencies)
    {
        return person.Role switch
        {
            Role.Patient => "patient access",
            Role.Carer => "carer access",
            Role.Clinician => emergencies is { Count: > 0 } ? "emergency" : "care",
            Role.Manager => "practice management", // (the appointments, need to know, and the statistics)
            Role.Auditor => "audit",
            _ => "administration"
        };
    }

    private static string Digest(string text)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8];
    }
}

/// <summary>
///     The practice's records in ChalkQL: the schedule, the visits' sections, what's shared with whom, under the tenancy
///     policy; an engine whose clock its refreshes advance; and the statements every feed is made of.
/// </summary>
public sealed class PracticeStore : IAsyncDisposable
{
    public const string Schema = "main";

    /// <summary>The access log's own database, as ChalkQL addresses it.</summary>
    public const string AuditSchema = "audit";

    /// <summary>
    ///     The schedule's activities, each with its acknowledgements as the reader may see them: when it was acknowledged
    ///     and by whom (once, at most), when the practice first read it and who (the first reader's row), and how many
    ///     receptionists have. The names come from the acknowledgement's own row, not an aggregate of them: a row's
    ///     disclosure behind a <c>GROUP BY</c> reads <c>PER_ROW</c> where it's decided by the parent's row, as an ack's is.
    /// </summary>
    private const string ScheduleColumns = """
                                    SELECT a.seq, a.kind, a.site_id, a.clinician_id, a.booking, a.slot, a.occurred_at, a.ticket,
                                           a.room, a.patient_id, a.patient_name, a.born, a.sex, a.note,
                                           g.occurred_at AS acked_at, g.person_name AS acked_by,
                                           k.read_at, f.person_name AS read_by, k.readers
                                    """;

    private const string ScheduleFrom = """
                                    FROM activities a
                                    LEFT JOIN acks g ON g.activity_seq = a.seq AND g.kind = 'acknowledged'
                                    LEFT JOIN acks f ON f.activity_seq = a.seq AND f.kind = 'seen' AND f.is_first
                                    LEFT JOIN (SELECT activity_seq, MIN(occurred_at) AS read_at,
                                                      COUNT(CASE WHEN person_role = 'reception' THEN 1 END) AS readers
                                               FROM acks
                                               WHERE kind = 'seen' AND person_role <> 'patient'
                                               GROUP BY activity_seq) k ON k.activity_seq = a.seq
                                    """;

    private const string SectionColumns =
        "seq, booking, patient_id, attending_id, problem_id, reason, detail, written_at, kind, released";

    // Why each row's patient is disclosed, its basis (DisclosureBasis), as the practice reads its own policy, role by
    // role: until ChalkQL's policy reports it, the statements say it themselves, which duplicates the policy and can
    // drift from it. A GP's are told who they are (@gp) and their emergency's patients (@emergency), parameters bound for
    // a class's views' life: one statement for every GP, as for every class of a shape (a statement reads no @ctx).
    private const string ByScheduling = "'scheduling'";

    private const string ByGp = "CASE WHEN a.clinician_id = @gp THEN 'care' ELSE 'scheduling' END";

    private const string ByGpInEmergency =
        "CASE WHEN a.clinician_id = @gp THEN 'care' WHEN a.patient_id IN @emergency THEN 'emergency' ELSE 'scheduling' END";

    private const string SectionByGpInEmergency =
        "CASE WHEN attending_id = @gp THEN 'care' WHEN patient_id IN @emergency THEN 'emergency' ELSE 'care' END";

    private const string ByPatient = "'own record'", ByCarer = "'carer'"; // (a carer is never a patient here: by role)

    /// <summary>The appointments' activities: what the manager's schedule keeps to, need to know (no messages, no results).</summary>
    private const string Appointments = "a.kind IN ('booked', 'reminded', 'arrived', 'called', 'seen')";

    /// <summary>
    ///     A site's screen: every ticket of the last two hours waiting, or called, whose visit hasn't moved on (the longest
    ///     waiting too: a newest few would drop them first). No protected column.
    /// </summary>
    public const string ScreenFeed = """
                                     SELECT a.seq, a.kind, a.ticket, a.room, a.occurred_at
                                     FROM activities a
                                     WHERE a.kind IN ('arrived', 'called')
                                       AND a.occurred_at > CURRENT_TIMESTAMP - INTERVAL '2' HOUR
                                       AND NOT EXISTS (SELECT 1 FROM activities b
                                                       WHERE b.booking = a.booking AND b.seq > a.seq AND b.kind IN ('called', 'seen'))
                                     """;

    /// <summary>
    ///     A manager's figures by the hour, at each of their sites, over the last twelve (this one too): who was checked in,
    ///     called in and seen, and the mean wait of those called. No protected column: no floor.
    /// </summary>
    public const string StatsHours = """
                                     SELECT site_id, FLOOR(occurred_at TO HOUR) AS hour_start,
                                            COUNT(*) FILTER (WHERE kind = 'arrived') AS arrivals,
                                            COUNT(*) FILTER (WHERE kind = 'called') AS called_in,
                                            COUNT(*) FILTER (WHERE kind = 'seen') AS seen,
                                            AVG(waited) FILTER (WHERE kind = 'called') AS mean_wait
                                     FROM activities
                                     WHERE occurred_at >= FLOOR(CURRENT_TIMESTAMP TO HOUR) - INTERVAL '11' HOUR
                                     GROUP BY site_id, FLOOR(occurred_at TO HOUR)
                                     """;

    /// <summary>
    ///     A manager's visits by reason, at each of their sites, booked in the last twelve full hours: one count per
    ///     reason, a test of it as the count's <c>FILTER</c>, which is all the policy lets the manager do with a reason.
    ///     The policy's floor guards a site's visits as a group, not each reason's count (a test's <c>FILTER</c> is a
    ///     probe's, to ChalkQL), so a count under five is withheld here, by the statement. Full hours only, so a count
    ///     moves on the hour rather than with each booking.
    /// </summary>
    public static readonly string StatsReasons =
        "SELECT site_id, MIN(written_at) AS since, " +
        string.Join(", ", StatsRow.Reasons.Select((reason, i) =>
            $"CASE WHEN COUNT(*) FILTER (WHERE reason = '{reason.Replace("'", "''")}') >= {StatsFloor} " +
            $"THEN COUNT(*) FILTER (WHERE reason = '{reason.Replace("'", "''")}') END AS r{i}")) +
        " FROM sections WHERE kind = 'visit'" +
        " AND written_at >= FLOOR(CURRENT_TIMESTAMP TO HOUR) - INTERVAL '12' HOUR AND written_at < FLOOR(CURRENT_TIMESTAMP TO HOUR)" +
        " GROUP BY site_id";

    /// <summary>
    ///     A shard's dispatcher topic: the dispatch log's pending entries (no outcome reported yet), each with its device's
    ///     type and token (a send's: a no-op carries none) and the push's title (a carer's, for a carer), as a dispatcher
    ///     may read them.
    /// </summary>
    public static string NotifyFeed(int shard)
    {
        return $"""
                SELECT d.seq, d.shard, d.site_id, d.notification_id, d.device_id, d.action, d.reason, d.written_at,
                       v.device_type, CASE WHEN d.action = 'send' THEN v.apn_token END AS apn_token,
                       CASE WHEN d.for_carer THEN n.carer_title ELSE n.title END AS title, n.class, n.urgent, n.deliver_by
                FROM dispatches d
                JOIN devices v ON v.id = d.device_id
                JOIN notifications n ON n.id = d.notification_id
                WHERE d.shard = {shard} AND d.outcome = ''
                """;
    }

    /// <summary>
    ///     The release job: for each patient in a notice's cohort (its site's, seen in the past year) not yet notified of
    ///     it, its place in their queue (urgent or normal): operational before informational, then the soonest deadline;
    ///     with what's been pushed to them of that queue in the past day (their budget's use) and when last. A periodic
    ///     run, not a view: it counts recent sends, and ranks with a window.
    /// </summary>
    public const string Release = """
                                  WITH cohort AS (SELECT DISTINCT site_id, patient_id FROM activities
                                                  WHERE kind = 'seen' AND occurred_at > CURRENT_TIMESTAMP - INTERVAL '365' DAY),
                                       pending AS (SELECT c.patient_id, s.id, s.class, s.urgent, s.deliver_by
                                                   FROM notices s JOIN cohort c ON c.site_id = s.site_id
                                                   WHERE NOT EXISTS (SELECT 1 FROM notifications n
                                                                     WHERE n.notice_id = s.id AND n.patient_id = c.patient_id)),
                                       pushed AS (SELECT d.recipient_id, n.urgent, COUNT(DISTINCT d.notification_id) AS pushes,
                                                         MAX(d.written_at) AS last_push
                                                  FROM dispatches d JOIN notifications n ON n.id = d.notification_id
                                                  WHERE n.class <> 'transactional' AND d.action = 'send'
                                                    AND d.written_at > CURRENT_TIMESTAMP - INTERVAL '1' DAY
                                                  GROUP BY d.recipient_id, n.urgent)
                                  SELECT p.patient_id, p.id AS notice_id, p.class, p.urgent, p.deliver_by,
                                         ROW_NUMBER() OVER (PARTITION BY p.patient_id, p.urgent
                                                            ORDER BY CASE p.class WHEN 'operational' THEN 0 ELSE 1 END,
                                                                     p.deliver_by, p.id) AS place,
                                         COALESCE(s.pushes, 0) AS pushes, s.last_push
                                  FROM pending p LEFT JOIN pushed s ON s.recipient_id = p.patient_id AND s.urgent = p.urgent
                                  """;

    /// <summary>The fewest visits a manager's count by reason shows: under it, the count is withheld.</summary>
    public const int StatsFloor = 5;

    /// <summary>
    ///     A patient's schedule: their own activity over 90 days. One plan, from a context's shape: run per patient (a
    ///     carer's twin, <see cref="CarerSchedule" />, the same but for its basis).
    /// </summary>
    public const string PatientSchedule =
        $"{ScheduleColumns}, {ByPatient} AS basis {ScheduleFrom} WHERE a.occurred_at > CURRENT_TIMESTAMP - INTERVAL '90' DAY";

    public const string CarerSchedule =
        $"{ScheduleColumns}, {ByCarer} AS basis {ScheduleFrom} WHERE a.occurred_at > CURRENT_TIMESTAMP - INTERVAL '90' DAY";

    /// <summary>A patient's sections: their own visits' clinical content over 90 days, run per patient (and a carer's twin).</summary>
    public const string PatientSections =
        $"SELECT {SectionColumns}, {ByPatient} AS basis FROM sections WHERE written_at > CURRENT_TIMESTAMP - INTERVAL '90' DAY";

    public const string CarerSections =
        $"SELECT {SectionColumns}, {ByCarer} AS basis FROM sections WHERE written_at > CURRENT_TIMESTAMP - INTERVAL '90' DAY";

    /// <summary>
    ///     One activity, as whoever runs it may see it: what a change posts to a patient's topic (a carer's, its twin), and
    ///     what a command checks.
    /// </summary>
    public const string OneActivity = $"{ScheduleColumns}, {ByPatient} AS basis {ScheduleFrom} WHERE a.seq = ?";

    public const string CarerActivity = $"{ScheduleColumns}, {ByCarer} AS basis {ScheduleFrom} WHERE a.seq = ?";

    /// <summary>
    ///     Who was shown a record (an activity, part 0; or a section, part 3): each person, the topic, the first and last
    ///     time (ticks), and how many posts carried it — and whose the record is (the patient, the visit, the GP attending:
    ///     the same on every line). From the access log's own database: the auditor's.
    /// </summary>
    public const string ShownTo = """
                                  SELECT person_id, person_user, topic, MIN(shown_at) AS first_shown, MAX(shown_at) AS last_shown,
                                         COUNT(*) AS posts, MIN(patient_id) AS patient_id, MIN(booking) AS booking,
                                         MIN(clinician_id) AS clinician_id, MAX(basis) AS basis
                                  FROM audit.access_log
                                  WHERE part = ? AND row_seq = ?
                                  GROUP BY person_id, person_user, topic
                                  """;

    /// <summary>The access log's newest entries (each a record made available to someone), newest first: the auditor's.</summary>
    public const string RecentAccess = """
                                       SELECT seq, person_id, person_user, topic, part, row_seq, patient_id, site_id, shown_at,
                                              booking, clinician_id, basis
                                       FROM audit.access_log
                                       ORDER BY seq DESC
                                       LIMIT 50
                                       """;

    /// <summary>A section's note, whole, as whoever runs it may read it: what a row's 120 bytes cut.</summary>
    public const string Note = "SELECT seq, detail FROM sections WHERE seq = ?";

    /// <summary>A visit's activities (its booking's), as whoever runs it may see them: the provider's view of a record.</summary>
    public const string VisitActivities = $"{ScheduleColumns} {ScheduleFrom} WHERE a.booking = ?";

    /// <summary>A visit's sections, as whoever runs it may read them.</summary>
    public const string VisitSections = $"SELECT {SectionColumns} FROM sections WHERE booking = ?";

    /// <summary>One section, as whoever runs it may see it (a carer's, its twin).</summary>
    public const string OneSection = $"SELECT {SectionColumns}, {ByPatient} AS basis FROM sections WHERE seq = ?";

    public const string CarerSection = $"SELECT {SectionColumns}, {ByCarer} AS basis FROM sections WHERE seq = ?";

    private readonly int _feedRows; // (a class's schedule and sections: the newest so many)
    private readonly PocoTable<Ack> _acks;
    private readonly PocoTable<ActivityGrant> _activityGrants;
    private readonly PocoTable<Activity> _activities;
    private readonly PocoTable<Device> _devices;
    private readonly PocoTable<Dispatch> _dispatches;
    private readonly PocoTable<SectionGrant> _grants;
    private readonly PocoTable<Notification> _notifications;
    private readonly PocoTable<NoticeRow> _notices;
    private readonly PlannerProcess _planner;
    private readonly PocoTable<Section> _sections;

    private PracticeStore(PlannerProcess planner, ChalkEngine engine, PracticePolicy policy,
        TenancyEntitlements entitlements, Tables tables, int feedRows)
    {
        (_planner, Engine, Policy, Entitlements, _feedRows) = (planner, engine, policy, entitlements, feedRows);
        (_activities, _sections, _grants, _acks, _activityGrants) =
            (tables.Activities, tables.Sections, tables.Grants, tables.Acks, tables.ActivityGrants);
        (_devices, _notifications, _dispatches, _notices) = (tables.Devices, tables.Notifications, tables.Dispatches, tables.Notices);
        Entitled = engine.WithEntitlements(new EntitlementsOptions { IncludeDisclosureColumns = true });
        ReceptionSchedule = Newest(ByScheduling);
        ManagerSchedule = Newest(ByScheduling, Appointments);
        GpSchedule = Newest(ByGp);
        GpScheduleInEmergency = Newest(ByGpInEmergency);
        GpSections = $"SELECT {SectionColumns}, 'care' AS basis FROM sections ORDER BY seq DESC LIMIT {feedRows}";
        GpSectionsInEmergency = $"SELECT {SectionColumns}, {SectionByGpInEmergency} AS basis FROM sections ORDER BY seq DESC LIMIT {feedRows}";
    }

    public ChalkEngine Engine { get; }
    public EntitledEngine Entitled { get; }
    public PracticePolicy Policy { get; }
    public TenancyEntitlements Entitlements { get; }

    /// <summary>
    ///     A class's schedule: the newest activities at the class's sites, and its GP's own visits wherever
    ///     (<c>Practice:FeedRows</c>, 300 by default), as its policy shows them, each saying why its patient is disclosed.
    ///     Newest by the view's key: a view refuses a <c>LIMIT</c> whose <c>ORDER BY</c> leaves its key out, since ties
    ///     would let the input's order choose its rows. No clock in it, so a refresh that only moves the clock reads no row
    ///     again. Reception's and the manager's are need to know (the manager's, the appointments alone); a GP's, care for
    ///     the visits they attend, emergency for the patients whose record they broke the glass for, need to know for the
    ///     rest of their site.
    /// </summary>
    public string ReceptionSchedule { get; }

    public string ManagerSchedule { get; }
    public string GpSchedule { get; }
    public string GpScheduleInEmergency { get; }

    /// <summary>A GP class's sections: the newest it may read (reception and the manager read none), for care.</summary>
    public string GpSections { get; }

    public string GpSectionsInEmergency { get; }

    /// <summary>
    ///     A staff member's class's statements: its schedule, and its sections if it reads any (a GP's: the newest they may
    ///     read, for care, or for an emergency), each with the values its parameters take.
    /// </summary>
    public (ClassStatement Schedule, ClassStatement? Sections) StatementsOf(Person person,
        IReadOnlyCollection<int>? emergencies = null)
    {
        if (person.Role == Role.Manager) return (new ClassStatement(ManagerSchedule, []), null);
        if (person.Role != Role.Clinician) return (new ClassStatement(ReceptionSchedule, []), null);
        if (emergencies is not { Count: > 0 })
            return (new ClassStatement(GpSchedule, [new("gp", person.Id)]), new ClassStatement(GpSections, []));
        KeyValuePair<string, object?>[] told = [new("gp", person.Id), new("emergency", emergencies.Order().ToArray())];
        return (new ClassStatement(GpScheduleInEmergency, told), new ClassStatement(GpSectionsInEmergency, told));
    }

    private string Newest(string basis, string? where = null)
    {
        return $"{ScheduleColumns}, {basis} AS basis {ScheduleFrom}{(where is null ? "" : $" WHERE {where}")} ORDER BY a.seq DESC LIMIT {_feedRows}";
    }

    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync();
        await _planner.DisposeAsync();
    }

    /// <summary>
    ///     ChalkQL up: its planner started, the policy compiled over the catalog (the practice's records, and the access
    ///     log's own database, <paramref name="accessLog" />, as the audit source), the engine made.
    /// </summary>
    public static async Task<PracticeStore> StartAsync(int feedRows, AccessLogSink accessLog, ILoggerFactory? log,
        CancellationToken ct)
    {
        var planner = await PlannerProcess.StartAsync(new PlannerProcessOptions(), ct);
        try
        {
            var catalog = new CatalogContext
            {
                ContextId = "practice", Epoch = 1,
                Schemas = [Source(null, out _).DescribeSchema(), Audit(accessLog, null).DescribeSchema()]
            };
            var policy = PracticePolicy.Declare(catalog);
            var entitlements = policy.Compile(catalog);
            var source = Source(entitlements, out var tables);
            var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
            {
                ContextId = "practice", Sources = [source, Audit(accessLog, entitlements)], Planner = planner.CreatePlanner(),
                LoggerFactory = log,
                Clock = "now" // (CURRENT_TIMESTAMP reads it, in a view and in a plain run: each refresh advances it)
            }, ct);
            return new PracticeStore(planner, engine, policy, entitlements, tables, feedRows);
        }
        catch
        {
            await planner.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    ///     What happened, written in one refresh: activities appended, sections written (a new visit's, a seen visit's
    ///     findings), grants given and taken back. The clock advanced to <paramref name="clock" /> and past every
    ///     activity: a view and its statement agree only while no row is later than the clock. Its planning deferred: the
    ///     rows are published now, and the tables' statistics, the planner's numbers and any statement planned again on them
    ///     follow in ChalkQL's background, off the service's loop (new plans swap in at a later refresh, or when the
    ///     service awaits them, seeding).
    /// </summary>
    public ValueTask<long> WriteAsync(Writes writes, DateTimeOffset clock, CancellationToken ct)
    {
        foreach (var row in writes.Activities)
            if (row.OccurredAt > clock.UtcDateTime)
                clock = new DateTimeOffset(row.OccurredAt, TimeSpan.Zero);
        // Its plan a static lambda reading the state it's given: a turn's refresh makes no closure and no delegate, and
        // its rows go as the lists they're in (each read while the refresh runs: none is changed until it returns).
        return Engine.RefreshAsync((Store: this, Writes: writes, Clock: clock), static (r, s) =>
        {
            var (store, writes) = (s.Store, s.Writes);
            r.AdvanceClock(s.Clock).DeferPlanning();
            if (writes.Activities.Count > 0) r.Append(store._activities, writes.Activities);
            if (writes.Acks.Count > 0) r.Append(store._acks, writes.Acks);
            if (writes.Sections.Count > 0) r.Upsert(store._sections, writes.SectionRows);
            if (writes.Granted.Count > 0) r.Upsert(store._grants, writes.GrantedRows);
            if (writes.Revoked.Count > 0) r.Delete(store._grants, writes.RevokedRows);
            if (writes.ActivitiesGranted.Count > 0) r.Upsert(store._activityGrants, writes.ActivitiesGrantedRows);
            if (writes.ActivitiesRevoked.Count > 0) r.Delete(store._activityGrants, writes.ActivitiesRevokedRows);
            if (writes.Devices.Count > 0) r.Upsert(store._devices, writes.DeviceRows);
            if (writes.Notifications.Count > 0) r.Upsert(store._notifications, writes.NotificationRows);
            if (writes.Dispatches.Count > 0) r.Upsert(store._dispatches, writes.DispatchRows);
            if (writes.Notices.Count > 0) r.Upsert(store._notices, writes.NoticeRows);
        }, ct);
    }

    /// <summary>
    ///     The access log's own database (SQLite), as ChalkQL reads it (<see cref="AuditSchema" />): its one table, under the
    ///     policy (the auditor's), each read on a connection of its own while the store's consumer writes.
    /// </summary>
    private static AdoSource Audit(AccessLogSink accessLog, TenancyEntitlements? entitlements)
    {
        var connection = accessLog.Connection();
        var builder = new AdoSourceBuilder("audit", () => new SqliteConnection(connection), AuditSchema)
            .Dialect(DialectProfiles.Sqlite)
            .Capabilities(AdoCapabilities.For(DialectProfiles.Sqlite))
            .AddTable("access_log", configure: t => t.UniqueKey("seq").Index("access_log_record", false, "part", "row_seq"));
        if (entitlements?.For(AuditSchema, "access_log") is { } descriptor) builder.Entitlement("access_log", descriptor);
        return builder.Build();
    }

    private static PocoSource Source(TenancyEntitlements? entitlements, out Tables tables)
    {
        tables = new Tables();
        var builder = new PocoSourceBuilder("practice", Schema).NamingPolicy(PocoNamingPolicy.SnakeCase);
        builder.AddTable("activities", Array.Empty<Activity>(), out tables.Activities, t =>
        {
            t.OrderedBy(a => a.Seq).UniqueKey(a => a.Seq); // (appended in order)
            Entitle(t, entitlements, "activities");
        });
        builder.AddTable("acks", Array.Empty<Ack>(), out tables.Acks, t =>
        {
            t.OrderedBy(k => k.Seq).UniqueKey(k => k.Seq); // (appended in order)
            t.ForeignKey(k => k.ActivitySeq).References<Activity>(a => a.Seq, verify: true);
            Entitle(t, entitlements, "acks");
        });
        builder.AddTable("sections", Array.Empty<Section>(), out tables.Sections, t =>
        {
            t.Keyed(s => s.Seq);
            Entitle(t, entitlements, "sections");
        });
        builder.AddTable("section_grants", Array.Empty<SectionGrant>(), out tables.Grants, t =>
        {
            t.Keyed(g => g.Id);
            t.ForeignKey(g => g.SectionSeq).References<Section>(s => s.Seq, verify: true);
            t.ForeignKey(g => g.GranteeId).References<Grantee>(g => g.Id, verify: true);
            Entitle(t, entitlements, "section_grants");
        });
        builder.AddTable("activity_grants", Array.Empty<ActivityGrant>(), out tables.ActivityGrants, t =>
        {
            t.Keyed(g => g.Id);
            t.ForeignKey(g => g.ActivitySeq).References<Activity>(a => a.Seq, verify: true);
            t.ForeignKey(g => g.GranteeId).References<Grantee>(g => g.Id, verify: true);
            Entitle(t, entitlements, "activity_grants");
        });
        builder.AddTable("devices", Array.Empty<Device>(), out tables.Devices, t =>
        {
            t.Keyed(d => d.Id);
            Entitle(t, entitlements, "devices");
        });
        builder.AddTable("notifications", Array.Empty<Notification>(), out tables.Notifications, t =>
        {
            t.Keyed(n => n.Id);
            Entitle(t, entitlements, "notifications");
        });
        builder.AddTable("notices", Array.Empty<NoticeRow>(), out tables.Notices, t =>
        {
            t.Keyed(n => n.Id);
            Entitle(t, entitlements, "notices");
        });
        builder.AddTable("dispatches", Array.Empty<Dispatch>(), out tables.Dispatches, t =>
        {
            t.Keyed(d => d.Id);
            t.ForeignKey(d => d.DeviceId).References<Device>(v => v.Id, verify: true);
            t.ForeignKey(d => d.NotificationId).References<Notification>(n => n.Id, verify: true);
            Entitle(t, entitlements, "dispatches");
        });
        builder.AddTable("grantees",
            People.Staff.Where(p => p.Role == Role.Clinician).Concat(People.Carers).Select(p => new Grantee(p.Id)).ToArray(),
            t =>
            {
                t.Keyed(g => g.Id);
                Entitle(t, entitlements, "grantees");
            });
        return builder.Build();
    }

    private static void Entitle<T>(PocoTableBuilder<T> table, TenancyEntitlements? entitlements, string name)
    {
        if (entitlements?.For(Schema, name) is { } descriptor) table.Entitlement(descriptor);
    }

    private sealed class Tables
    {
        public PocoTable<Ack> Acks;
        public PocoTable<ActivityGrant> ActivityGrants;
        public PocoTable<Activity> Activities;
        public PocoTable<Device> Devices;
        public PocoTable<Dispatch> Dispatches;
        public PocoTable<SectionGrant> Grants;
        public PocoTable<Notification> Notifications;
        public PocoTable<NoticeRow> Notices;
        public PocoTable<Section> Sections;
    }
}

/// <summary>
///     What one refresh writes: activities appended, sections written (each once, as it last was: a refresh upserts a key
///     once), grants given and taken back (a grant given and taken back in one refresh is neither), and devices,
///     notifications and the dispatch log's entries, each as it last was.
/// </summary>
public sealed class Writes
{
    private readonly Dictionary<long, ActivityGrant> _activitiesGranted = [], _activitiesRevoked = [];
    private readonly Dictionary<long, Device> _devices = [];
    private readonly Dictionary<long, Dispatch> _dispatches = [];
    private readonly Dictionary<long, SectionGrant> _granted = [], _revoked = [];
    private readonly Dictionary<string, Notification> _notifications = [];
    private readonly Dictionary<string, NoticeRow> _notices = [];
    private readonly Dictionary<long, Section> _sections = [];
    public readonly List<Ack> Acks = [];
    public readonly List<Activity> Activities = [];
    public readonly List<Shown> Shown = []; // (the access log's, as posts are made: digested with the next refresh, then stored)

    public IReadOnlyCollection<Section> Sections => _sections.Values;
    public IReadOnlyCollection<SectionGrant> Granted => _granted.Values;
    public IReadOnlyCollection<SectionGrant> Revoked => _revoked.Values;
    public IReadOnlyCollection<ActivityGrant> ActivitiesGranted => _activitiesGranted.Values;
    public IReadOnlyCollection<ActivityGrant> ActivitiesRevoked => _activitiesRevoked.Values;
    public IReadOnlyCollection<Device> Devices => _devices.Values;
    public IReadOnlyCollection<Notification> Notifications => _notifications.Values;
    public IReadOnlyCollection<Dispatch> Dispatches => _dispatches.Values;
    public IReadOnlyCollection<NoticeRow> Notices => _notices.Values;

    // Each keyed write's rows as the list a refresh reads: the same list each turn, refilled, so a turn allocates none.
    private readonly List<Section> _sectionRows = [];
    private readonly List<SectionGrant> _grantedRows = [], _revokedRows = [];
    private readonly List<ActivityGrant> _activitiesGrantedRows = [], _activitiesRevokedRows = [];
    private readonly List<Device> _deviceRows = [];
    private readonly List<Notification> _notificationRows = [];
    private readonly List<Dispatch> _dispatchRows = [];
    private readonly List<NoticeRow> _noticeRows = [];
    public IReadOnlyList<Section> SectionRows => Rows(_sectionRows, _sections);
    public IReadOnlyList<SectionGrant> GrantedRows => Rows(_grantedRows, _granted);
    public IReadOnlyList<SectionGrant> RevokedRows => Rows(_revokedRows, _revoked);
    public IReadOnlyList<ActivityGrant> ActivitiesGrantedRows => Rows(_activitiesGrantedRows, _activitiesGranted);
    public IReadOnlyList<ActivityGrant> ActivitiesRevokedRows => Rows(_activitiesRevokedRows, _activitiesRevoked);
    public IReadOnlyList<Device> DeviceRows => Rows(_deviceRows, _devices);
    public IReadOnlyList<Notification> NotificationRows => Rows(_notificationRows, _notifications);
    public IReadOnlyList<Dispatch> DispatchRows => Rows(_dispatchRows, _dispatches);
    public IReadOnlyList<NoticeRow> NoticeRows => Rows(_noticeRows, _notices);

    private static List<T> Rows<TKey, T>(List<T> rows, Dictionary<TKey, T> written) where TKey : notnull
    {
        rows.Clear();
        rows.AddRange(written.Values); // (a collection: copied, no enumerator)
        return rows;
    }

    public bool Any => Activities.Count > 0 || Acks.Count > 0 || _sections.Count > 0 || _granted.Count > 0 ||
                       _revoked.Count > 0 || _activitiesGranted.Count > 0 || _activitiesRevoked.Count > 0 ||
                       _devices.Count > 0 || _notifications.Count > 0 || _dispatches.Count > 0 || _notices.Count > 0;

    public void Write(NoticeRow notice)
    {
        _notices[notice.Id] = notice;
    }

    public void Write(Device device)
    {
        _devices[device.Id] = device;
    }

    public void Write(Notification notification)
    {
        _notifications[notification.Id] = notification;
    }

    public void Write(Dispatch dispatch)
    {
        _dispatches[dispatch.Id] = dispatch;
    }

    public void Write(Section section)
    {
        _sections[section.Seq] = section;
    }

    public void Grant(SectionGrant grant)
    {
        if (!_revoked.Remove(grant.Id)) _granted[grant.Id] = grant;
    }

    public void Revoke(SectionGrant grant)
    {
        if (!_granted.Remove(grant.Id)) _revoked[grant.Id] = grant;
    }

    public void Grant(ActivityGrant grant)
    {
        if (!_activitiesRevoked.Remove(grant.Id)) _activitiesGranted[grant.Id] = grant;
    }

    public void Revoke(ActivityGrant grant)
    {
        if (!_activitiesGranted.Remove(grant.Id)) _activitiesRevoked[grant.Id] = grant;
    }

    public void Clear()
    {
        Activities.Clear();
        Acks.Clear();
        Shown.Clear();
        _sections.Clear();
        _granted.Clear();
        _revoked.Clear();
        _activitiesGranted.Clear();
        _activitiesRevoked.Clear();
        _devices.Clear();
        _notifications.Clear();
        _dispatches.Clear();
        _notices.Clear();
    }
}
