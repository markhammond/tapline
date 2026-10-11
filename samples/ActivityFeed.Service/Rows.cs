using System.Runtime.InteropServices;
using ActivityFeed.Contracts;
using Apache.Arrow;
using Apache.Arrow.Types;
using Chalk.Arrow;
using Chalk.Incremental;
using Tapline;

namespace ActivityFeed.Service;

/// <summary>A row's change, as a view drains it: replaced by <see cref="Row" />, or deleted.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly record struct RowChange<TRow>(TRow Row, byte Deleted) where TRow : unmanaged;

/// <summary>The fold of every feed topic: each row as its change says, replaced or deleted.</summary>
public readonly struct ReplaceOrDelete<TRow> : IRowFold<TRow, RowChange<TRow>> where TRow : unmanaged
{
    public static bool Fold(ref TRow row, bool exists, in RowChange<TRow> change)
    {
        if (change.Deleted != 0) return false;
        row = change.Row;
        return true;
    }
}

/// <summary>A row's cells, read by kind: a view's row, or a run's batch at a row.</summary>
internal interface ICells
{
    bool IsNull(int column);
    bool Bool(int column);
    ReadOnlySpan<byte> Utf8(int column);
    long Int64(int column);
    DateTime Time(int column);
}

internal readonly ref struct ViewCells(ViewRow row) : ICells
{
    private readonly ViewRow _row = row;

    public bool IsNull(int column)
    {
        return _row.IsNull(column);
    }

    public ReadOnlySpan<byte> Utf8(int column)
    {
        return _row.GetUtf8(column);
    }

    public bool Bool(int column)
    {
        return _row.GetBoolean(column);
    }

    public long Int64(int column)
    {
        return _row.GetInt64(column);
    }

    public DateTime Time(int column)
    {
        return _row.GetDateTime(column);
    }
}

internal readonly struct BatchCells(RecordBatch batch, int row) : ICells
{
    public bool IsNull(int column)
    {
        return batch.Column(column).IsNull(row);
    }

    public ReadOnlySpan<byte> Utf8(int column)
    {
        return IsNull(column) ? [] : batch.Column(column).GetUtf8(row);
    }

    public bool Bool(int column)
    {
        return batch.Column(column) is BooleanArray flags && flags.GetValue(row) == true;
    }

    public long Int64(int column)
    {
        return batch.Column(column) switch
        {
            Int8Array a => a.GetValue(row) ?? 0,
            Int16Array a => a.GetValue(row) ?? 0,
            Int32Array a => a.GetValue(row) ?? 0,
            Int64Array a => a.GetValue(row) ?? 0,
            var other => throw new InvalidCastException($"Column {column} is {other.Data.DataType.Name}, not an integer.")
        };
    }

    public DateTime Time(int column)
    {
        return batch.Column(column) is TimestampArray times && times.GetTimestamp(row) is { } at
            ? ((TimestampType)times.Data.DataType).Timezone is { Length: > 0 }
                ? at.UtcDateTime
                : DateTime.SpecifyKind(at.UtcDateTime, DateTimeKind.Unspecified)
            : default;
    }
}

/// <summary>
///     Where a feed statement's columns are, by name, and their companions (<c>__disclosure</c>: what each disclosed,
///     row by row) — none for a column no policy protects. A schedule's statement (its activities, each with its note)
///     or a sections' (a visit's clinical content: its problem, reason and detail, and who attended).
/// </summary>
internal sealed class FeedColumns
{
    public readonly bool IsSection;

    public readonly int Seq, Kind, Site, Clinician, Booking, Slot, At, Ticket, Room, Patient, Name, Born, Sex, Problem,
        Reason, Detail;

    public readonly int[] PatientDisclosure;
    public readonly int ReasonDisclosure, DetailDisclosure;

    /// <summary>A schedule's acknowledgements: when, who (a staff realm), and how many receptionists have read it.</summary>
    public readonly int AckedAt, AckedBy, ReadAt, ReadBy, Readers;

    /// <summary>A section's: whether it's released to the patient.</summary>
    public readonly int Released;

    public readonly int AckedByDisclosure, ReadByDisclosure;

    /// <summary>Why the row's patient is disclosed (<c>basis</c>), as the practice's statement works it out.</summary>
    public readonly int Basis;

    public FeedColumns(Schema schema)
    {
        int At(string name)
        {
            for (var i = 0; i < schema.FieldsList.Count; i++)
                if (string.Equals(schema.FieldsList[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        int Required(string name)
        {
            return At(name) is var i and >= 0 ? i : throw new InvalidOperationException($"The feed's statement has no {name}.");
        }

        IsSection = At("problem_id") >= 0;
        (Seq, Kind, Site, Booking, Slot, Ticket, Room) = (Required("seq"), At("kind"), At("site_id"), At("booking"),
            At("slot"), At("ticket"), At("room"));
        Clinician = IsSection ? At("attending_id") : At("clinician_id");
        this.At = IsSection ? Required("written_at") : Required("occurred_at");
        (Patient, Name, Born, Sex, Problem, Reason) = (At("patient_id"), At("patient_name"), At("born"), At("sex"),
            At("problem_id"), At("reason"));
        Detail = IsSection ? At("detail") : At("note");
        PatientDisclosure =
        [
            .. new[] { "patient_id", "patient_name", "born", "sex" }.Select(c => At(c + "__disclosure"))
                .Where(i => i >= 0)
        ];
        ReasonDisclosure = At("reason__disclosure");
        DetailDisclosure = IsSection ? At("detail__disclosure") : At("note__disclosure");
        (AckedAt, AckedBy, ReadAt, ReadBy, Readers) = (At("acked_at"), At("acked_by"), At("read_at"), At("read_by"),
            At("readers"));
        (AckedByDisclosure, ReadByDisclosure) = (At("acked_by__disclosure"), At("read_by__disclosure"));
        Released = At("released");
        Basis = At("basis");
    }
}

/// <summary>
///     Where a manager's statistics statement's columns are, by name: by the hour (its site and hour, its counts and mean
///     wait), or by reason (its site, the first visit counted, and a count per reason, with their companions).
/// </summary>
internal sealed class StatsColumns
{
    public readonly bool IsHourly;
    public readonly int Site, Hour, Arrivals, Called, Seen, MeanWait, Since;
    public readonly int[] Reasons, ReasonDisclosures;

    public StatsColumns(Schema schema)
    {
        int At(string name)
        {
            for (var i = 0; i < schema.FieldsList.Count; i++)
                if (string.Equals(schema.FieldsList[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        Site = At("site_id") is var site and >= 0 ? site : throw new InvalidOperationException("The statistics' statement has no site_id.");
        (Hour, Arrivals, Called, Seen, MeanWait, Since) = (At("hour_start"), At("arrivals"), At("called_in"), At("seen"), At("mean_wait"),
            At("since"));
        IsHourly = Hour >= 0;
        Reasons = [.. Enumerable.Range(0, StatsRow.ReasonCount).Select(i => At($"r{i}"))];
        ReasonDisclosures = [.. Enumerable.Range(0, StatsRow.ReasonCount).Select(i => At($"r{i}__disclosure"))];
        if (!IsHourly && Reasons.Contains(-1))
            throw new InvalidOperationException($"The statistics' statement counts {Reasons.Count(r => r >= 0)} reasons, not {StatsRow.ReasonCount}.");
    }
}

/// <summary>Where a dispatcher statement's columns are, by name, with the token's and the title's companions.</summary>
internal sealed class NotifyColumns
{
    public readonly int Seq, Shard, Site, Notification, Device, Action, Reason, At, DeviceType, Token, TokenDisclosure, Title,
        TitleDisclosure, Class, Urgent, DeliverBy;

    public NotifyColumns(Schema schema)
    {
        int At(string name)
        {
            for (var i = 0; i < schema.FieldsList.Count; i++)
                if (string.Equals(schema.FieldsList[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        (Seq, Shard, Notification, Device, Action, Reason, this.At) = (At("seq"), At("shard"), At("notification_id"),
            At("device_id"), At("action"), At("reason"), At("written_at"));
        (DeviceType, Token, TokenDisclosure, Title, TitleDisclosure) = (At("device_type"), At("apn_token"),
            At("apn_token__disclosure"), At("title"), At("title__disclosure"));
        (Class, Urgent, DeliverBy, Site) = (At("class"), At("urgent"), At("deliver_by"), At("site_id"));
    }
}

/// <summary>Feed rows made of a statement's: a view's (its map), or a run's (a batch's, row by row).</summary>
internal static class Rows
{
    public static ViewMapper<FeedKey, FeedRow> FeedMapper(FeedColumns at)
    {
        return (in row, out key, out value) => Feed(new ViewCells(row), at, out key, out value);
    }

    public static ViewMapper<FeedKey, ScreenRow> ScreenMapper(FeedColumns at)
    {
        return (in row, out key, out value) => Screen(new ViewCells(row), at, out key, out value);
    }

    public static ViewMapper<FeedKey, StatsRow> StatsMapper(StatsColumns at)
    {
        return (in row, out key, out value) => Stats(new ViewCells(row), at, out key, out value);
    }

    public static ViewMapper<FeedKey, NotifyRow> NotifyMapper(NotifyColumns at)
    {
        return (in row, out key, out value) => Notify(new ViewCells(row), at, out key, out value);
    }

    /// <summary>A dispatcher's row: a pending entry of the dispatch log, keyed by its shard's number.</summary>
    public static void Notify<TCells>(TCells cells, NotifyColumns at, out FeedKey key, out NotifyRow row)
        where TCells : ICells, allows ref struct
    {
        key = new FeedKey(FeedKey.Notify, cells.Int64(at.Seq));
        row = default;
        row.Shard = (byte)cells.Int64(at.Shard);
        row.Site = (byte)cells.Int64(at.Site);
        row.Action = cells.Utf8(at.Action).SequenceEqual("send"u8) ? DispatchAction.Send : DispatchAction.NoOp;
        row.DeviceId = cells.Int64(at.Device);
        row.AtMicros = Micros(cells.Time(at.At));
        row.DeliverByMicros = Micros(cells.Time(at.DeliverBy));
        row.Urgent = cells.Bool(at.Urgent) ? (byte)1 : (byte)0;
        row.Class = cells.Utf8(at.Class) switch
        {
            var c when c.SequenceEqual("operational"u8) => NotificationClass.Operational,
            var c when c.SequenceEqual("informational"u8) => NotificationClass.Informational,
            _ => NotificationClass.Transactional
        };
        FixedText.Write(row.DeviceType, cells.Utf8(at.DeviceType));
        if (!cells.IsNull(at.Token)) FixedText.Write(row.Token, cells.Utf8(at.Token));
        FixedText.Write(row.NotificationId, cells.Utf8(at.Notification));
        FixedText.Write(row.Reason, cells.Utf8(at.Reason));
        if (!cells.IsNull(at.Title)) FixedText.Write(row.Title, cells.Utf8(at.Title));
        row.TokenDisclosed = at.TokenDisclosure < 0 ? Disclosed.Full : DisclosedOf(cells.Utf8(at.TokenDisclosure));
        row.TitleDisclosed = at.TitleDisclosure < 0 ? Disclosed.Full : DisclosedOf(cells.Utf8(at.TitleDisclosure));
    }

    /// <summary>
    ///     A statistics row: a site's hour (its counts, and the mean wait, none if nobody was called in), or its visits by
    ///     reason, each count as the policy disclosed it (nothing, under the floor: a null, its companion saying so).
    /// </summary>
    public static void Stats<TCells>(TCells cells, StatsColumns at, out FeedKey key, out StatsRow row)
        where TCells : ICells, allows ref struct
    {
        row = default;
        row.Site = (byte)cells.Int64(at.Site);
        if (at.IsHourly)
        {
            row.Hourly = 1;
            row.AtMicros = Micros(cells.Time(at.Hour));
            key = FeedKey.Hour(row.AtMicros, row.Site);
            row.Arrivals = (int)cells.Int64(at.Arrivals);
            row.Called = (int)cells.Int64(at.Called);
            row.Seen = (int)cells.Int64(at.Seen);
            row.MeanWaitSeconds = cells.IsNull(at.MeanWait) ? -1 : (int)cells.Int64(at.MeanWait);
            return;
        }

        key = new FeedKey(FeedKey.Reasons, row.Site);
        if (at.Since >= 0 && !cells.IsNull(at.Since)) row.AtMicros = Micros(cells.Time(at.Since));
        for (var i = 0; i < StatsRow.ReasonCount; i++)
        {
            var withheld = cells.IsNull(at.Reasons[i]) ||
                           at.ReasonDisclosures[i] >= 0 && DisclosedOf(cells.Utf8(at.ReasonDisclosures[i])) == Disclosed.Redacted;
            row.ByReason[i] = withheld ? 0 : (int)cells.Int64(at.Reasons[i]);
            row.ReasonsDisclosed[i] = withheld ? Disclosed.Redacted : Disclosed.Aggregate;
        }
    }

    public static void Feed<TCells>(TCells cells, FeedColumns at, out FeedKey key, out FeedRow row)
        where TCells : ICells, allows ref struct
    {
        key = new FeedKey(at.IsSection ? FeedKey.Clinical : FeedKey.Activity, cells.Int64(at.Seq));
        row = default;
        row.Kind = at.Kind < 0 ? 0 : KindOf(cells.Utf8(at.Kind));
        row.Site = at.Site < 0 ? (byte)0 : (byte)cells.Int64(at.Site);
        row.Clinician = at.Clinician < 0 ? 0 : (int)cells.Int64(at.Clinician);
        row.Booking = at.Booking < 0 ? 0 : cells.Int64(at.Booking);
        row.SlotMicros = at.Slot < 0 || cells.IsNull(at.Slot) ? 0 : Micros(cells.Time(at.Slot));
        row.AtMicros = Micros(cells.Time(at.At));
        row.Ticket = at.Ticket < 0 ? (short)0 : (short)cells.Int64(at.Ticket);
        if (at.Room >= 0) FixedText.Write(row.Room, cells.Utf8(at.Room));
        if (at.Patient >= 0 && !cells.IsNull(at.Patient)) row.Patient = (int)cells.Int64(at.Patient);
        if (at.Name >= 0) FixedText.Write(row.PatientName, cells.Utf8(at.Name));
        if (at.Born >= 0 && !cells.IsNull(at.Born)) row.Born = (short)cells.Int64(at.Born);
        if (at.Sex >= 0 && cells.Utf8(at.Sex) is [var sex, ..]) row.Sex = sex;
        if (at.Problem >= 0 && !cells.IsNull(at.Problem)) row.Problem = (int)cells.Int64(at.Problem);
        if (at.Released >= 0 && !cells.IsNull(at.Released)) row.Released = cells.Bool(at.Released) ? (byte)1 : (byte)0;
        if (at.Reason >= 0) FixedText.Write(row.Reason, cells.Utf8(at.Reason));
        if (at.Detail >= 0)
        {
            var detail = cells.Utf8(at.Detail);
            FixedText.Write(row.Detail, detail);
            row.DetailCut = detail.Length > Text120.Bytes ? (byte)1 : (byte)0; // (its note in full: on demand)
        }
        foreach (var column in at.PatientDisclosure)
            row.PatientDisclosed = (Disclosed)Math.Max((byte)row.PatientDisclosed, (byte)DisclosedOf(cells.Utf8(column)));
        row.ReasonDisclosed = at.ReasonDisclosure < 0 ? Disclosed.Full : DisclosedOf(cells.Utf8(at.ReasonDisclosure));
        row.DetailDisclosed = at.DetailDisclosure < 0 ? Disclosed.Full : DisclosedOf(cells.Utf8(at.DetailDisclosure));
        if (at.Basis >= 0 && !cells.IsNull(at.Basis)) row.Basis = BasisOf(cells.Utf8(at.Basis));
        if (at.AckedAt >= 0 && !cells.IsNull(at.AckedAt)) row.AckedMicros = Micros(cells.Time(at.AckedAt));
        if (at.ReadAt >= 0 && !cells.IsNull(at.ReadAt)) row.ReadMicros = Micros(cells.Time(at.ReadAt));
        if (at.AckedBy >= 0 && !cells.IsNull(at.AckedBy)) FixedText.Write(row.AckedBy, cells.Utf8(at.AckedBy));
        if (at.ReadBy >= 0 && !cells.IsNull(at.ReadBy)) FixedText.Write(row.ReadBy, cells.Utf8(at.ReadBy));
        if (at.Readers >= 0 && !cells.IsNull(at.Readers)) row.Readers = (byte)Math.Min(cells.Int64(at.Readers), 255);
        // (who acknowledged and who read: what the policy disclosed of each there is, the more withheld)
        if (at.AckedAt >= 0 && !cells.IsNull(at.AckedAt) && at.AckedByDisclosure >= 0)
            row.StaffDisclosed = (Disclosed)Math.Max((byte)row.StaffDisclosed, (byte)DisclosedOf(cells.Utf8(at.AckedByDisclosure)));
        if (at.ReadAt >= 0 && !cells.IsNull(at.ReadAt) && at.ReadByDisclosure >= 0)
            row.StaffDisclosed = (Disclosed)Math.Max((byte)row.StaffDisclosed, (byte)DisclosedOf(cells.Utf8(at.ReadByDisclosure)));
    }

    public static void Screen<TCells>(TCells cells, FeedColumns at, out FeedKey key, out ScreenRow row)
        where TCells : ICells, allows ref struct
    {
        key = new FeedKey(FeedKey.Activity, cells.Int64(at.Seq));
        row = default;
        row.Kind = KindOf(cells.Utf8(at.Kind));
        row.Ticket = (short)cells.Int64(at.Ticket);
        FixedText.Write(row.Room, cells.Utf8(at.Room));
        row.AtMicros = Micros(cells.Time(at.At));
    }

    /// <summary>A run's rows, as feed rows: its keys and rows appended to the lists.</summary>
    public static void Read(RecordBatch batch, FeedColumns at, List<FeedKey> keys, List<FeedRow> rows)
    {
        for (var i = 0; i < batch.Length; i++)
        {
            Feed(new BatchCells(batch, i), at, out var key, out var row);
            keys.Add(key);
            rows.Add(row);
        }
    }

    public static long Micros(DateTime at)
    {
        return (at.Ticks - DateTime.UnixEpoch.Ticks) / 10;
    }

    public static ActivityKind KindOf(ReadOnlySpan<byte> kind)
    {
        return kind switch
        {
            _ when kind.SequenceEqual("booked"u8) => ActivityKind.Booked,
            _ when kind.SequenceEqual("arrived"u8) => ActivityKind.Arrived,
            _ when kind.SequenceEqual("called"u8) => ActivityKind.Called,
            _ when kind.SequenceEqual("seen"u8) => ActivityKind.Seen,
            _ when kind.SequenceEqual("reminded"u8) => ActivityKind.Reminded,
            _ when kind.SequenceEqual("messaged"u8) => ActivityKind.Messaged,
            _ when kind.SequenceEqual("result"u8) => ActivityKind.Result,
            _ when kind.SequenceEqual("replied"u8) => ActivityKind.Replied,
            _ => 0
        };
    }

    private static DisclosureBasis BasisOf(ReadOnlySpan<byte> basis)
    {
        return basis switch
        {
            _ when basis.SequenceEqual("care"u8) => DisclosureBasis.Care,
            _ when basis.SequenceEqual("scheduling"u8) => DisclosureBasis.Scheduling,
            _ when basis.SequenceEqual("own record"u8) => DisclosureBasis.OwnRecord,
            _ when basis.SequenceEqual("carer"u8) => DisclosureBasis.Carer,
            _ when basis.SequenceEqual("emergency"u8) => DisclosureBasis.Emergency,
            _ => DisclosureBasis.None
        };
    }

    private static Disclosed DisclosedOf(ReadOnlySpan<byte> disclosure)
    {
        return disclosure switch
        {
            _ when disclosure.SequenceEqual("FULL"u8) => Disclosed.Full,
            _ when disclosure.SequenceEqual("MASKED"u8) => Disclosed.Masked,
            _ when disclosure.SequenceEqual("AGGREGATE"u8) => Disclosed.Aggregate,
            _ => Disclosed.Redacted
        };
    }
}

/// <summary>
///     What a view drained for one reader: its whole state after a reset (keys and rows), or its changes (keys and
///     changes), and the view's stamp and checksum after them.
/// </summary>
internal sealed class Drained<TRow> : IViewSink<FeedKey, TRow> where TRow : unmanaged
{
    public readonly List<RowChange<TRow>> Changes = [];
    public readonly List<FeedKey> Keys = [];
    public readonly List<TRow> Rows = [];
    public uint Checksum;
    public ViewStamp Stamp;
    public bool WasReset;

    public bool Any => WasReset || Keys.Count > 0;

    /// <summary>The engine's version the view is at.</summary>
    public long Version => Stamp.Version;

    public void Reset()
    {
        Keys.Clear();
        Rows.Clear();
        Changes.Clear();
        WasReset = true;
    }

    public void Upsert(in FeedKey key, in TRow row)
    {
        Keys.Add(key);
        if (WasReset) Rows.Add(row);
        else Changes.Add(new RowChange<TRow>(row, 0));
    }

    public void Delete(in FeedKey key)
    {
        Keys.Add(key);
        Changes.Add(new RowChange<TRow>(default, 1));
    }

    /// <summary>Level with the view at its stamp: the engine's version, and how many times the view's been rebound.</summary>
    public void Commit(in ViewStamp stamp, uint checksum)
    {
        (Stamp, Checksum) = (stamp, checksum);
    }

    public void Commit(long version, uint checksum)
    {
        Commit(new ViewStamp(version, 0), checksum); // (ChalkQL commits stamped, above)
    }

    public void Clear()
    {
        Keys.Clear();
        Rows.Clear();
        Changes.Clear();
        WasReset = false;
    }
}

/// <summary>The feeds' formats, for the router's viewer: each topic's rows decoded field by field (the rows' own offsets).</summary>
internal static class Formats
{
    public static readonly PayloadLayout Key = new(FeedKey.Offsets["size"],
    [
        new PayloadField("section", FeedKey.Offsets["section"], FieldKind.UInt8)
        {
            Names = new Dictionary<long, string>
                {
                    [0] = "activity", [1] = "tasks", [2] = "status", [3] = "clinical", [4] = "access", [5] = "hours",
                    [6] = "reasons", [7] = "notify", [8] = "outbox"
                }
        },
        new PayloadField("seq", FeedKey.Offsets["seq"], FieldKind.Int64)
    ]);

    public static readonly PayloadFormat Feed = new() { KeyLayout = Key, Layout = FeedLayout() };

    public static readonly PayloadFormat Screen = new()
    {
        KeyLayout = Key,
        Layout = new PayloadLayout(ScreenRow.Offsets["size"],
        [
            new PayloadField("kind", ScreenRow.Offsets["kind"], FieldKind.UInt8) { Names = Kinds() },
            new PayloadField("ticket", ScreenRow.Offsets["ticket"], FieldKind.Int16),
            new PayloadField("room", ScreenRow.Offsets["room"], FieldKind.Utf8) { Length = Text8.Bytes },
            new PayloadField("at_us", ScreenRow.Offsets["at_us"], FieldKind.Int64)
        ])
    };

    public static readonly PayloadFormat Stats = new() { KeyLayout = Key, Layout = StatsLayout() };

    public static readonly PayloadFormat Notify = new() { KeyLayout = Key, Layout = NotifyLayout() };

    /// <summary>The write log's signal (<see cref="LogAt" />): the writer's last value.</summary>
    public static readonly PayloadFormat Log = new()
    {
        Layout = new PayloadLayout(LogAt.Offsets["size"],
        [
            new PayloadField("seq", LogAt.Offsets["seq"], FieldKind.Int64),
            new PayloadField("epoch", LogAt.Offsets["epoch"], FieldKind.Int64),
            new PayloadField("at_us", LogAt.Offsets["at_us"], FieldKind.Int64),
            new PayloadField("resigned", LogAt.Offsets["resigned"], FieldKind.UInt8)
        ])
    };

    private static PayloadLayout NotifyLayout()
    {
        var at = NotifyRow.Offsets;
        var disclosed = Enum.GetValues<Disclosed>().ToDictionary(d => (long)d, d => d.ToString().ToUpperInvariant());
        return new PayloadLayout(at["size"],
        [
            new PayloadField("shard", at["shard"], FieldKind.UInt8),
            new PayloadField("site", at["site"], FieldKind.UInt8),
            new PayloadField("action", at["action"], FieldKind.UInt8)
                { Names = new Dictionary<long, string> { [1] = "send", [2] = "no-op" } },
            new PayloadField("class", at["class"], FieldKind.UInt8)
                { Names = new Dictionary<long, string> { [1] = "transactional", [2] = "operational", [3] = "informational" } },
            new PayloadField("urgent", at["urgent"], FieldKind.UInt8),
            new PayloadField("device_id", at["device_id"], FieldKind.Int64),
            new PayloadField("at_us", at["at_us"], FieldKind.Int64),
            new PayloadField("deliver_by_us", at["deliver_by_us"], FieldKind.Int64),
            new PayloadField("device_type", at["device_type"], FieldKind.Utf8) { Length = Text8.Bytes },
            new PayloadField("token", at["token"], FieldKind.Utf8) { Length = Text64.Bytes },
            new PayloadField("notification_id", at["notification_id"], FieldKind.Utf8) { Length = Text48.Bytes },
            new PayloadField("reason", at["reason"], FieldKind.Utf8) { Length = Text48.Bytes },
            new PayloadField("title", at["title"], FieldKind.Utf8) { Length = Text120.Bytes },
            new PayloadField("token_disclosed", at["token_disclosed"], FieldKind.UInt8) { Names = disclosed },
            new PayloadField("title_disclosed", at["title_disclosed"], FieldKind.UInt8) { Names = disclosed }
        ]);
    }

    private static PayloadLayout StatsLayout()
    {
        var at = StatsRow.Offsets;
        var disclosed = Enum.GetValues<Disclosed>().ToDictionary(d => (long)d, d => d.ToString().ToUpperInvariant());
        List<PayloadField> fields =
        [
            new("site", at["site"], FieldKind.UInt8), new("hourly", at["hourly"], FieldKind.UInt8),
            new("at_us", at["at_us"], FieldKind.Int64), new("arrivals", at["arrivals"], FieldKind.Int32),
            new("called", at["called"], FieldKind.Int32), new("seen", at["seen"], FieldKind.Int32),
            new("mean_wait_s", at["mean_wait_s"], FieldKind.Int32)
        ];
        for (var i = 0; i < StatsRow.ReasonCount; i++) // (each reason's count, and what the policy disclosed of it)
        {
            var name = StatsRow.Reasons[i].ToLowerInvariant().Replace(' ', '_');
            fields.Add(new PayloadField(name, at["by_reason"] + i * sizeof(int), FieldKind.Int32));
            fields.Add(new PayloadField($"{name}_disclosed", at["reasons_disclosed"] + i, FieldKind.UInt8) { Names = disclosed });
        }

        return new PayloadLayout(at["size"], fields);
    }

    private static Dictionary<long, string> Kinds()
    {
        return Enum.GetValues<ActivityKind>().ToDictionary(k => (long)k, k => k.ToString().ToLowerInvariant());
    }

    private static PayloadLayout FeedLayout()
    {
        var at = FeedRow.Offsets;
        var disclosed = Enum.GetValues<Disclosed>().ToDictionary(d => (long)d, d => d.ToString().ToUpperInvariant());
        return new PayloadLayout(at["size"],
        [
            new PayloadField("kind", at["kind"], FieldKind.UInt8) { Names = Kinds() },
            new PayloadField("site", at["site"], FieldKind.UInt8),
            new PayloadField("clinician", at["clinician"], FieldKind.Int32),
            new PayloadField("booking", at["booking"], FieldKind.Int64),
            new PayloadField("slot_us", at["slot_us"], FieldKind.Int64),
            new PayloadField("at_us", at["at_us"], FieldKind.Int64),
            new PayloadField("ticket", at["ticket"], FieldKind.Int16),
            new PayloadField("room", at["room"], FieldKind.Utf8) { Length = Text8.Bytes },
            new PayloadField("patient", at["patient"], FieldKind.Int32),
            new PayloadField("patient_name", at["patient_name"], FieldKind.Utf8) { Length = Text48.Bytes },
            new PayloadField("born", at["born"], FieldKind.Int16),
            new PayloadField("sex", at["sex"], FieldKind.UInt8),
            new PayloadField("problem", at["problem"], FieldKind.Int32),
            new PayloadField("reason", at["reason"], FieldKind.Utf8) { Length = Text48.Bytes },
            new PayloadField("detail", at["detail"], FieldKind.Utf8) { Length = Text120.Bytes },
            new PayloadField("patient_disclosed", at["patient_disclosed"], FieldKind.UInt8) { Names = disclosed },
            new PayloadField("reason_disclosed", at["reason_disclosed"], FieldKind.UInt8) { Names = disclosed },
            new PayloadField("detail_disclosed", at["detail_disclosed"], FieldKind.UInt8) { Names = disclosed },
            new PayloadField("acked_us", at["acked_us"], FieldKind.Int64),
            new PayloadField("acked_by", at["acked_by"], FieldKind.Utf8) { Length = Text48.Bytes },
            new PayloadField("read_us", at["read_us"], FieldKind.Int64),
            new PayloadField("read_by", at["read_by"], FieldKind.Utf8) { Length = Text48.Bytes },
            new PayloadField("readers", at["readers"], FieldKind.UInt8),
            new PayloadField("staff_disclosed", at["staff_disclosed"], FieldKind.UInt8) { Names = disclosed },
            new PayloadField("count", at["count"], FieldKind.Int32),
            new PayloadField("released", at["released"], FieldKind.UInt8),
            new PayloadField("detail_cut", at["detail_cut"], FieldKind.UInt8)
        ]);
    }
}
