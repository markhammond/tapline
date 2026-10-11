using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace ActivityFeed.Service;

/// <summary>The kinds of the write log's entries (<see cref="IPracticeLog" />).</summary>
internal static class Entries
{
    /// <summary>A writer's turn (<see cref="TurnEntry" />): fenced by its lease.</summary>
    public const string Turn = "turn";

    /// <summary>Whose feeds an instance opened or closed (<see cref="PresenceEntry" />).</summary>
    public const string Presence = "presence";

    /// <summary>An instance started, or one gone (<see cref="InstanceEntry" />).</summary>
    public const string Instance = "instance";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static byte[] Body<T>(T entry)
    {
        return JsonSerializer.SerializeToUtf8Bytes(entry, Json);
    }

    public static T Of<T>(byte[] body)
    {
        return JsonSerializer.Deserialize<T>(body, Json)!;
    }
}

/// <summary>
///     A writer's turn, as the log has it: when, and what it took in (the commands and the dispatchers' reports, in the
///     order taken; whether the practice took a step), and the last of the other instances' entries it had applied before
///     it (<see cref="UpTo" />: they're applied there, by every instance). Every instance runs it the same way, through the
///     same code, so their records, and what they post, are the same. Each turn says what the writer's previous one wrote
///     (its digest), for the others to check theirs against. The practice's first (<see cref="Prime" />) primes its history,
///     and carries its settings: what the log says, every instance does.
/// </summary>
public sealed record TurnEntry
{
    public DateTime Now { get; init; }
    public bool Stepped { get; init; }
    public CommandInput[] Commands { get; init; } = [];
    public ReportInput[] Reports { get; init; } = [];
    public long UpTo { get; init; }
    public long PreviousSeq { get; init; }
    public string? PreviousDigest { get; init; }
    public PracticeSettings? Prime { get; init; }
}

/// <summary>
///     A command, as taken in: its kind, its person, what it's about, its text (or room), its correlation id (hex), and its
///     order, if its sender gave one (its key, the sender's id as hex, and its number: <see cref="ActivityFeed.Contracts.CommandOrder" />).
/// </summary>
public sealed record CommandInput(string Kind, int Person, long Booking, string Text, string Correlation, long[] Activities,
    string OrderKey = "", string Sender = "", long Seq = 0);

/// <summary>A dispatcher's report, as taken in.</summary>
public sealed record ReportInput(string Dispatcher, int Shard, long Seq, string Outcome, string Reason, string Correlation);

/// <summary>
///     The practice's settings, as its first turn fixed them: how far back its history goes and at what pace, how long an
///     emergency's access lasts, a notification's grace in the app, how often the release job runs, its quiet hours and
///     time zone. An instance that follows the log takes them from it, whatever it was started with.
/// </summary>
public sealed record PracticeSettings(double HistoryMinutes, double StepSeconds, double BreakGlassSeconds, double SeenGraceSeconds,
    double ReleaseSeconds, string QuietHours, string TimeZone);

/// <summary>
///     People whose feed an instance opened (its first route) or closed (its last), closes first. Where every instance
///     applies it (before the turn that says so), each opens or closes its feed of theirs: a feed open on any instance is
///     open on every one, and what it's shown is worked out there from the turns that follow (the access log, the same
///     everywhere). And what a notification's grace asks.
/// </summary>
public sealed record PresenceEntry(int[] Opened, int[] Closed);

/// <summary>An instance started (the entry's origin, named so), or one the writer took the lease from, gone: what it held open, forgotten.</summary>
public sealed record InstanceEntry(string Name, string? Gone);

/// <summary>
///     The write log's signal (the writer's <c>log</c> topic: a last value): the last turn it appended, the epoch of the
///     lease it holds, when (microseconds since 1970, UTC), and whether it has let the lease go (a drain: another instance
///     may take it at once). An instance that follows the writer reads the log on when it hears one.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct LogAt
{
    public long Seq;
    public long Epoch;
    public long AtMicros;
    public byte Resigned;

    /// <summary>Where each field lies, in bytes from the value's start, by name.</summary>
    public static IReadOnlyDictionary<string, int> Offsets { get; } = new Dictionary<string, int>
        { ["seq"] = 0, ["epoch"] = 8, ["at_us"] = 16, ["resigned"] = 24, ["size"] = Unsafe.SizeOf<LogAt>() };
}

/// <summary>
///     The write log as an instance has it (<c>/log</c>): its name and id, its role (writing, following, draining), who
///     holds the lease (and its epoch, if this instance does), the last entry read and turn run, how many turns it's run,
///     the store, the log's signal (posting it, or hearing the writer's: how many of its values), how long a turn took from
///     its append to its read here (<see cref="Heard" />) and to its run (<see cref="Lag" />), following, any turn whose
///     writes differed from the writer's, and the access log's own database (entries posted, written, and the loop's waits).
/// </summary>
public sealed record LogStatus(string Name, string Origin, string Role, string? Holder, long Epoch, long Read, long LastTurn,
    long Turns, string Store, string Signal, long Signals, Percentiles? Heard, Percentiles? Lag, string[] Divergences,
    AccessLogReport? AccessLog = null);

/// <summary>Ids every instance replaying the same turns makes alike.</summary>
internal static class Ids
{
    /// <summary>
    ///     A UUIDv7 of a time and a number: its 48-bit timestamp the time's milliseconds, its random bits the number, so it
    ///     sorts by time and is the same wherever the same turn makes it.
    /// </summary>
    public static Guid Version7(DateTime at, long number)
    {
        Span<byte> bytes = stackalloc byte[16];
        var ms = (ulong)new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        BinaryPrimitives.WriteUInt64BigEndian(bytes, ms << 16);
        bytes[6] = 0x70; // (version 7; rand_a 0)
        bytes[7] = 0;
        BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], (ulong)number & 0x3FFF_FFFF_FFFF_FFFF);
        bytes[8] |= 0x80; // (the variant)
        return new Guid(bytes, true);
    }
}

/// <summary>What a turn wrote, digested: every refresh's rows and clock, in order. Equal digests, equal records.</summary>
internal sealed class TurnDigest
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public void Add(Writes writes, DateTimeOffset clock)
    {
        _hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            clock, writes.Activities, writes.Acks, writes.Sections, writes.Granted, writes.Revoked, writes.ActivitiesGranted,
            writes.ActivitiesRevoked, writes.Devices, writes.Notifications, writes.Dispatches, writes.Notices, writes.Shown
        }, Entries.Json));
    }

    /// <summary>The digest so far (its first 8 bytes, hex), and a fresh start.</summary>
    public string Finish()
    {
        return Convert.ToHexString(_hash.GetHashAndReset(), 0, 8);
    }
}
