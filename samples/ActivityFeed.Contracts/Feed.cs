using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace ActivityFeed.Contracts;

/// <summary>
///     What an activity at the practice is: a visit's steps, a reminder of it, a patient's message, the practice's reply.
///     And, for a clinical section, whether it's a result (its own finding, released by the patient's GP) rather than a
///     visit's.
/// </summary>
public enum ActivityKind : byte
{
    Booked = 1,
    Arrived = 2,
    Called = 3,
    Seen = 4,
    Reminded = 5,
    Messaged = 6,
    Result = 7,
    Replied = 8
}

/// <summary>
///     What a column disclosed to the person reading it, row by row, as ChalkQL's policy decided (its <c>__disclosure</c>
///     columns): the value itself, a mask of it, an aggregate only, or nothing (a placeholder, or empty).
/// </summary>
public enum Disclosed : byte
{
    Full = 0,
    Masked = 1,
    Aggregate = 2,
    Redacted = 3
}

/// <summary>
///     Why a row's patient was disclosed to the person reading it: the purpose it served. <see cref="Care" /> for a GP's own
///     patients (the visits they attend); <see cref="Emergency" /> for a patient a GP broke the glass for;
///     <see cref="Scheduling" />, need to know, to run the day (reception, the manager, and a GP for the rest of their
///     site); <see cref="OwnRecord" /> for a patient's own; <see cref="Carer" /> for whom a carer cares for. The
///     practice's statements work it out, role by role, until ChalkQL's policy reports it itself.
/// </summary>
public enum DisclosureBasis : byte
{
    None = 0,
    Care = 1,
    Scheduling = 2,
    OwnRecord = 3,
    Carer = 4,
    Emergency = 5
}

/// <summary>A <see cref="DisclosureBasis" /> as the practice writes it: <c>care</c>, <c>scheduling</c>, and so on.</summary>
public static class DisclosureBases
{
    public static string Label(this DisclosureBasis basis)
    {
        return basis switch
        {
            DisclosureBasis.Care => "care",
            DisclosureBasis.Scheduling => "scheduling",
            DisclosureBasis.OwnRecord => "own record",
            DisclosureBasis.Carer => "carer",
            DisclosureBasis.Emergency => "emergency",
            _ => ""
        };
    }
}

/// <summary>
///     A row's key in a person's feed (their <c>home</c> topic): its part, and its sequence number. An
///     <see cref="Activity" /> is the schedule's (who, when, with whom: the practice's sequence, in the order things
///     happened); a <see cref="Clinical" /> section is a visit's clinical content, one per problem it addressed (its own
///     sequence). The person's <see cref="Tasks" />, their <see cref="Status" /> row and their <see cref="Access" /> (a
///     patient's consents: who reads what; a GP's emergency access) sit beside them. A manager's statistics (their
///     <c>stats</c> topic) are a site's <see cref="Hours" /> (keyed by the hour and the site) and its visits by
///     <see cref="Reasons" /> (keyed by the site). A dispatcher's (its <c>notify</c> topic) are the dispatch log's pending
///     entries, its shard's <see cref="Notify" /> rows (keyed by the shard's sequence number). A receptionist's
///     <see cref="Outbox" /> holds their replies to patients, each with how its notification went (keyed by the reply).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly record struct FeedKey(byte Section, long Seq)
{
    public const byte Activity = 0, Tasks = 1, Status = 2, Clinical = 3, Access = 4, Hours = 5, Reasons = 6, Notify = 7,
        Outbox = 8;

    /// <summary>A site's hour, as a statistics row's key: the hours since 1970, and the site.</summary>
    public static FeedKey Hour(long atMicros, byte site)
    {
        return new FeedKey(Hours, atMicros / 3_600_000_000 * 4 + site);
    }

    /// <summary>Where each field lies, in bytes from the key's start, by name.</summary>
    public static IReadOnlyDictionary<string, int> Offsets { get; } = new Dictionary<string, int>
        { ["section"] = 0, ["seq"] = 1, ["size"] = Unsafe.SizeOf<FeedKey>() };
}

/// <summary>
///     A row of a person's feed as they may see it, fixed-width, its text zero-padded UTF-8: an activity (the schedule:
///     who, when, with whom, and its note), or a visit's clinical section (what it was for, and what was found). Who the
///     patient is (the policy's <c>pii</c> realm: their id, name, year of birth and sex), an activity's note (<c>admin</c>)
///     and a section's content (<c>clinical</c>) are as the policy disclosed them for this row, and say so: a column
///     withheld is its placeholder, or empty.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FeedRow
{
    /// <summary>An activity's kind; none (0) for a clinical section.</summary>
    public ActivityKind Kind;

    public byte Site;

    /// <summary>An activity's GP (the visit's); a section's, the GP who attended it.</summary>
    public int Clinician;

    /// <summary>The booking's sequence number: every activity of one visit carries it.</summary>
    public long Booking;

    /// <summary>The appointment's time, and when this happened: microseconds since 1970, UTC.</summary>
    public long SlotMicros, AtMicros;

    /// <summary>The visit's ticket (from arrival), and the room it's called to.</summary>
    public short Ticket;

    public Text8 Room;

    public int Patient;
    public Text48 PatientName;
    public short Born;

    /// <summary>'F', 'M' or 'X'; 0 withheld.</summary>
    public byte Sex;

    /// <summary>A section's problem: the patient's, from their problem list.</summary>
    public int Problem;

    /// <summary>A section's reason: what the visit was for, as far as this problem goes.</summary>
    public Text48 Reason;

    /// <summary>A section's detail (what was found), or an activity's note ("Checked in", a message's text).</summary>
    public Text120 Detail;

    /// <summary>What the row disclosed of the patient (their id, name, year of birth and sex), of the reason, and of the detail.</summary>
    public Disclosed PatientDisclosed, ReasonDisclosed, DetailDisclosed;

    /// <summary>Why the patient was disclosed to the person reading it (<see cref="DisclosureBasis" />).</summary>
    public DisclosureBasis Basis;

    /// <summary>
    ///     An activity's acknowledgements, as the person reading it may see them: when it was acknowledged (a reminder, by
    ///     the patient or for them) and by whom; when the practice first read it (a message) and who; how many of the
    ///     site's receptionists have read it. Who acknowledged and who read are the practice's staff names, withheld from
    ///     a patient (<see cref="StaffDisclosed" />).
    /// </summary>
    public long AckedMicros, ReadMicros;

    public Text48 AckedBy, ReadBy;
    public byte Readers;
    public Disclosed StaffDisclosed;

    /// <summary>A section's: whether it's released to the patient (a result, once their GP has; a visit's, always).</summary>
    public byte Released;

    /// <summary>1 if the detail was longer than its 120 bytes: the rest is read on demand (a section's note in full).</summary>
    public byte DetailCut;

    /// <summary>The status row's: the person's open tasks (their badge).</summary>
    public int Count;

    public readonly DateTimeOffset Slot => Time(SlotMicros);
    public readonly DateTimeOffset At => Time(AtMicros);

    public override readonly string ToString()
    {
        var who = PatientDisclosed == Disclosed.Full
            ? $"{PatientName} ({Born}{(Sex == 0 ? "" : (char)Sex)})"
            : $"[a patient: {Name(PatientDisclosed)}]";
        if (Kind == 0)
            return $"Section #{Booking} problem {Problem}, attended by {Clinician}: " +
                   $"{Shown(Reason.ToString(), ReasonDisclosed)} — {Shown(Detail.ToString(), DetailDisclosed)}";
        return $"{Kind} #{Booking} {Slot:HH:mm} site {Site}, {who}, clinician {Clinician}: " +
               $"{Shown(Detail.ToString(), DetailDisclosed)}" +
               (Ticket > 0 ? $", ticket {Ticket}" : "") + (Room.Length > 0 ? $", {Room}" : "") +
               (AckedMicros > 0 ? $", acknowledged {Time(AckedMicros):HH:mm} by {Shown(AckedBy.ToString(), StaffDisclosed)}" : "") +
               (ReadMicros > 0 ? $", read {Time(ReadMicros):HH:mm} by {Shown(ReadBy.ToString(), StaffDisclosed)} ({Readers} at reception)" : "");
    }

    public static DateTimeOffset Time(long micros)
    {
        return DateTimeOffset.UnixEpoch.AddTicks(micros * 10);
    }

    /// <summary>Where each field lies, in bytes from the row's start, by name (a page decodes rows by these).</summary>
    public static IReadOnlyDictionary<string, int> Offsets { get; } = OffsetsOf();

    private static Dictionary<string, int> OffsetsOf()
    {
        var row = new FeedRow();
        ref var start = ref Unsafe.As<FeedRow, byte>(ref row);
        return new Dictionary<string, int>
        {
            ["kind"] = OffsetOf(ref start, ref row.Kind), ["site"] = OffsetOf(ref start, ref row.Site),
            ["clinician"] = OffsetOf(ref start, ref row.Clinician), ["booking"] = OffsetOf(ref start, ref row.Booking),
            ["slot_us"] = OffsetOf(ref start, ref row.SlotMicros), ["at_us"] = OffsetOf(ref start, ref row.AtMicros),
            ["ticket"] = OffsetOf(ref start, ref row.Ticket), ["room"] = OffsetOf(ref start, ref row.Room),
            ["patient"] = OffsetOf(ref start, ref row.Patient), ["patient_name"] = OffsetOf(ref start, ref row.PatientName),
            ["born"] = OffsetOf(ref start, ref row.Born), ["sex"] = OffsetOf(ref start, ref row.Sex),
            ["problem"] = OffsetOf(ref start, ref row.Problem),
            ["reason"] = OffsetOf(ref start, ref row.Reason), ["detail"] = OffsetOf(ref start, ref row.Detail),
            ["patient_disclosed"] = OffsetOf(ref start, ref row.PatientDisclosed),
            ["reason_disclosed"] = OffsetOf(ref start, ref row.ReasonDisclosed),
            ["detail_disclosed"] = OffsetOf(ref start, ref row.DetailDisclosed),
            ["acked_us"] = OffsetOf(ref start, ref row.AckedMicros), ["read_us"] = OffsetOf(ref start, ref row.ReadMicros),
            ["acked_by"] = OffsetOf(ref start, ref row.AckedBy), ["read_by"] = OffsetOf(ref start, ref row.ReadBy),
            ["readers"] = OffsetOf(ref start, ref row.Readers),
            ["staff_disclosed"] = OffsetOf(ref start, ref row.StaffDisclosed),
            ["count"] = OffsetOf(ref start, ref row.Count), ["released"] = OffsetOf(ref start, ref row.Released),
            ["detail_cut"] = OffsetOf(ref start, ref row.DetailCut), ["basis"] = OffsetOf(ref start, ref row.Basis),
            ["size"] = Unsafe.SizeOf<FeedRow>()
        };
    }

    internal static int OffsetOf<T>(ref byte start, ref T field)
    {
        return (int)Unsafe.ByteOffset(ref start, ref Unsafe.As<T, byte>(ref field));
    }

    private static string Shown(string text, Disclosed disclosed)
    {
        return disclosed == Disclosed.Full ? text : $"[{Name(disclosed)}{(text.Length > 0 ? ": " + text : "")}]";
    }

    private static string Name(Disclosed disclosed)
    {
        return disclosed.ToString().ToLowerInvariant();
    }
}

/// <summary>
///     A row of a site's waiting-room screen: a visit's ticket, and the room it's called to. Nothing personal: the
///     screen's statement reads no column the policy protects, and the ticket is a fresh one each visit.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ScreenRow
{
    public ActivityKind Kind;
    public short Ticket;
    public Text8 Room;
    public long AtMicros;

    public override readonly string ToString()
    {
        return Kind == ActivityKind.Called ? $"Ticket {Ticket} → {Room}" : $"Ticket {Ticket}: please wait";
    }

    /// <summary>Where each field lies, in bytes from the row's start, by name.</summary>
    public static IReadOnlyDictionary<string, int> Offsets { get; } = OffsetsOf();

    private static Dictionary<string, int> OffsetsOf()
    {
        var row = new ScreenRow();
        ref var start = ref Unsafe.As<ScreenRow, byte>(ref row);
        return new Dictionary<string, int>
        {
            ["kind"] = FeedRow.OffsetOf(ref start, ref row.Kind), ["ticket"] = FeedRow.OffsetOf(ref start, ref row.Ticket),
            ["room"] = FeedRow.OffsetOf(ref start, ref row.Room), ["at_us"] = FeedRow.OffsetOf(ref start, ref row.AtMicros),
            ["size"] = Unsafe.SizeOf<ScreenRow>()
        };
    }
}

/// <summary>
///     A row of a manager's statistics (their <c>stats</c> topic): a site's figures for an hour (who was checked in, called
///     in and seen, and the mean wait of those called in), or its visits by reason, booked in the last twelve full hours.
///     Counts and means only. The policy lets the manager count a visit's reason, never read one, and withholds a count
///     under its floor of five.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StatsRow
{
    public const int ReasonCount = 12;

    /// <summary>The reasons a site's visits are counted by, in the order of <see cref="ByReason" />.</summary>
    public static readonly string[] Reasons =
    [
        "Diabetes review", "Asthma check", "Blood pressure check", "Medication review", "Flu vaccination",
        "Wound dressing", "Back pain", "Skin rash", "Child immunisation", "Sexual health", "Mental health review",
        "Pregnancy"
    ];

    public byte Site;

    /// <summary>1 for an hour's figures; 0 for the visits by reason.</summary>
    public byte Hourly;

    /// <summary>The hour's start; by reason, the first visit counted: microseconds since 1970, UTC.</summary>
    public long AtMicros;

    /// <summary>An hour's: patients checked in, called in and seen.</summary>
    public int Arrivals, Called, Seen;

    /// <summary>An hour's mean wait, from check-in, of the patients called in: seconds, or -1 if nobody was.</summary>
    public int MeanWaitSeconds;

    /// <summary>Visits by reason, in <see cref="Reasons" />'s order: 0 where the floor withheld the count.</summary>
    public ReasonCounts ByReason;

    /// <summary>What the policy disclosed of each: an aggregate, or nothing (a group under the floor).</summary>
    public ReasonDisclosures ReasonsDisclosed;

    public override readonly string ToString()
    {
        if (Hourly != 0)
            return $"site {Site}, {FeedRow.Time(AtMicros):HH:mm}: {Arrivals} checked in, {Called} called in, {Seen} seen" +
                   (MeanWaitSeconds >= 0 ? $", mean wait {MeanWaitSeconds / 60.0:F1} min" : "");
        var counts = new List<string>();
        for (var i = 0; i < ReasonCount; i++)
            counts.Add($"{Reasons[i]} {(ReasonsDisclosed[i] == Disclosed.Aggregate ? ByReason[i].ToString() : "withheld")}");
        return $"site {Site}, by reason since {FeedRow.Time(AtMicros):HH:mm}: {string.Join(", ", counts)}";
    }

    /// <summary>Where each field lies, in bytes from the row's start, by name (a reason's count and disclosure by index).</summary>
    public static IReadOnlyDictionary<string, int> Offsets { get; } = OffsetsOf();

    private static Dictionary<string, int> OffsetsOf()
    {
        var row = new StatsRow();
        ref var start = ref Unsafe.As<StatsRow, byte>(ref row);
        return new Dictionary<string, int>
        {
            ["site"] = FeedRow.OffsetOf(ref start, ref row.Site), ["hourly"] = FeedRow.OffsetOf(ref start, ref row.Hourly),
            ["at_us"] = FeedRow.OffsetOf(ref start, ref row.AtMicros),
            ["arrivals"] = FeedRow.OffsetOf(ref start, ref row.Arrivals),
            ["called"] = FeedRow.OffsetOf(ref start, ref row.Called), ["seen"] = FeedRow.OffsetOf(ref start, ref row.Seen),
            ["mean_wait_s"] = FeedRow.OffsetOf(ref start, ref row.MeanWaitSeconds),
            ["by_reason"] = FeedRow.OffsetOf(ref start, ref row.ByReason[0]),
            ["reasons_disclosed"] = FeedRow.OffsetOf(ref start, ref row.ReasonsDisclosed[0]),
            ["size"] = Unsafe.SizeOf<StatsRow>()
        };
    }
}

[InlineArray(StatsRow.ReasonCount)]
public struct ReasonCounts
{
    private int _e;
}

[InlineArray(StatsRow.ReasonCount)]
public struct ReasonDisclosures
{
    private Disclosed _e;
}

/// <summary>What a dispatcher is to do with a dispatch log's entry: send it, or record that nothing was sent (and why).</summary>
public enum DispatchAction : byte
{
    Send = 1,
    NoOp = 2
}

/// <summary>
///     A notification's class: <see cref="Transactional" />, about the patient's own care (uncounted); operational, how the
///     practice runs; informational, its news.
/// </summary>
public enum NotificationClass : byte
{
    Transactional = 1,
    Operational = 2,
    Informational = 3
}

/// <summary>
///     A row of a dispatcher's topic (<c>notify:{shard}</c>): one pending entry of the dispatch log, a notification for one
///     device, with its shard's sequence number. A send carries the device's push token (only a dispatcher reads one)
///     and the push's text, which says nothing personal or clinical; a no-op carries no token, and says why nothing is
///     sent. It leaves the topic once the dispatcher has reported what came of it.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct NotifyRow
{
    public byte Shard;

    /// <summary>The recipient's site: a tenancy, for a dispatcher to take in turn (none's burst holding another's back).</summary>
    public byte Site;

    public DispatchAction Action;
    public NotificationClass Class;
    public byte Urgent;
    public long DeviceId;

    /// <summary>When it was written to the log, and the deadline for handing it off: microseconds since 1970, UTC.</summary>
    public long AtMicros, DeliverByMicros;

    public Text8 DeviceType;
    public Text64 Token;

    /// <summary>The notification's id (a UUIDv7: its collapse key), and a no-op's reason.</summary>
    public Text48 NotificationId, Reason;

    public Text120 Title;

    /// <summary>What the policy disclosed of the token, and of the title.</summary>
    public Disclosed TokenDisclosed, TitleDisclosed;

    public override readonly string ToString()
    {
        return Action == DispatchAction.Send
            ? $"send to {DeviceType} {Token.ToString()[..Math.Min(8, Token.ToString().Length)]}…: \"{Title}\" ({Class}{(Urgent != 0 ? ", urgent" : "")}, by {FeedRow.Time(DeliverByMicros):HH:mm:ss})"
            : $"no-op for device {DeviceId}: {Reason} (\"{Title}\")";
    }

    /// <summary>Where each field lies, in bytes from the row's start, by name.</summary>
    public static IReadOnlyDictionary<string, int> Offsets { get; } = OffsetsOf();

    private static Dictionary<string, int> OffsetsOf()
    {
        var row = new NotifyRow();
        ref var start = ref Unsafe.As<NotifyRow, byte>(ref row);
        return new Dictionary<string, int>
        {
            ["shard"] = FeedRow.OffsetOf(ref start, ref row.Shard), ["site"] = FeedRow.OffsetOf(ref start, ref row.Site),
            ["action"] = FeedRow.OffsetOf(ref start, ref row.Action),
            ["class"] = FeedRow.OffsetOf(ref start, ref row.Class), ["urgent"] = FeedRow.OffsetOf(ref start, ref row.Urgent),
            ["device_id"] = FeedRow.OffsetOf(ref start, ref row.DeviceId), ["at_us"] = FeedRow.OffsetOf(ref start, ref row.AtMicros),
            ["deliver_by_us"] = FeedRow.OffsetOf(ref start, ref row.DeliverByMicros),
            ["device_type"] = FeedRow.OffsetOf(ref start, ref row.DeviceType), ["token"] = FeedRow.OffsetOf(ref start, ref row.Token),
            ["notification_id"] = FeedRow.OffsetOf(ref start, ref row.NotificationId),
            ["reason"] = FeedRow.OffsetOf(ref start, ref row.Reason), ["title"] = FeedRow.OffsetOf(ref start, ref row.Title),
            ["token_disclosed"] = FeedRow.OffsetOf(ref start, ref row.TokenDisclosed),
            ["title_disclosed"] = FeedRow.OffsetOf(ref start, ref row.TitleDisclosed),
            ["size"] = Unsafe.SizeOf<NotifyRow>()
        };
    }
}

/// <summary>Fixed-width text: UTF-8, zero-padded, cut at a character boundary.</summary>
public static class FixedText
{
    public static void Write(Span<byte> field, ReadOnlySpan<char> text)
    {
        field.Clear();
        var written = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var n = rune.Utf8SequenceLength;
            if (written + n > field.Length) break;
            rune.EncodeToUtf8(field[written..]);
            written += n;
        }
    }

    public static void Write(Span<byte> field, ReadOnlySpan<byte> utf8)
    {
        field.Clear();
        var n = Math.Min(utf8.Length, field.Length);
        while (n > 0 && n < utf8.Length && (utf8[n] & 0xC0) == 0x80) n--; // (cut at a character's start)
        utf8[..n].CopyTo(field);
    }

    public static string Read(ReadOnlySpan<byte> field)
    {
        return Encoding.UTF8.GetString(Used(field));
    }

    public static ReadOnlySpan<byte> Used(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return end < 0 ? field : field[..end];
    }
}

[InlineArray(Bytes)]
public struct Text8
{
    public const int Bytes = 8;
    private byte _e;

    public readonly int Length => FixedText.Used(this).Length;

    public override readonly string ToString()
    {
        return FixedText.Read(this);
    }
}

[InlineArray(Bytes)]
public struct Text48
{
    public const int Bytes = 48;
    private byte _e;

    public override readonly string ToString()
    {
        return FixedText.Read(this);
    }
}

[InlineArray(Bytes)]
public struct Text64
{
    public const int Bytes = 64;
    private byte _e;

    public override readonly string ToString()
    {
        return FixedText.Read(this);
    }
}

[InlineArray(Bytes)]
public struct Text120
{
    public const int Bytes = 120;
    private byte _e;

    public override readonly string ToString()
    {
        return FixedText.Read(this);
    }
}
