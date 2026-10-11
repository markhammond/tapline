using System.Buffers.Binary;
using System.Text;

namespace Tapline.Tests;

/// <summary>Reference decoder mirroring blazor-realtime.js: table replicas verified by sequence and checksum.</summary>
internal sealed class RefClient
{
    private readonly Dictionary<uint, long> _lastMessage = new(), _lastMail = new();

    public readonly List<(byte Type, uint TapId, long Seq, byte[]? Bytes)>
        Applied = new(); // state, messages and rendezvous, in the order applied

    public readonly Dictionary<uint, Lane> Classes = new(); // taps bound into a lane's class, as the test bound them

    public readonly List<(FaultCode Code, uint From, uint To)>
        Ended = new(); // the router ended its subscriptions (a range of tap ids)

    public readonly List<(uint TapId, FaultCode Code, long Version, byte[] Payload)> Errors = new();
    public readonly Dictionary<uint, GroupMirror> Groups = new();
    public readonly List<byte> LastFrameRecords = new();
    public readonly List<uint> LastFrameTaps = new(); // tap id per record (0 for heartbeat/service)
    public readonly List<OobHandle> Pending = new(); // transparent handles to fetch
    public readonly List<string?> ProviderScopes = new(); // …each one's scope: a service path there (null: the node)
    public readonly List<(Guid Instance, string State)> ProviderStates = new(); // degraded / serving again, as told
    public readonly Dictionary<uint, OobHandle> References = new(); // value taps delivered by reference

    public readonly List<(Guid CorrelationId, string Service, string Reason)>
        Refusals = new(); // subscriptions its providers refused, saying why

    public readonly List<RefusalKind> RefusalKinds = new(); // …and of what kind, in the same order

    public readonly List<(AuthOutcome Status, uint Exchange, ushort Round, string Text, byte[] Payload)>
        Auths = new(); // sign-in answers, as received

    public readonly List<(byte Status, RenewalPolicy Renewal, Guid Correlation, Guid Session, long ExpiresAt, string Reason)>
        Sessions = new(); // the router's word on the session, as received

    public readonly List<(uint TapId, byte Flags, long Seq, byte[] Correlation)> Rendezvous = new(); // as received
    public readonly List<Guid> ServiceCorrelations = new(); // every Service record's correlation id, in order
    public readonly List<Guid> ServicesDown = new(); // service instances the server said are gone
    public readonly List<Guid> ServicesDrained = new(); // instances draining that let this client go

    public readonly List<(Guid Instance, uint GraceMs)>
        ServicesDraining = new(); // …that told it to move (make before break)

    public readonly List<string> ServicesUp = new(); // services the server said are up again

    public readonly List<(Guid Instance, uint GraceMs, uint TapId)> Moves = new(); // subscriptions told to move

    public readonly Dictionary<uint, TableMirror> Tables = new();

    public readonly List<(uint TapId, long Seq, byte Flags, byte[] Payload)>
        TopicMessages = new(); // message topics' and mailboxes' messages, as applied

    public readonly Dictionary<uint, byte[]> Values = new();
    public long BytesHigh, BytesNormal, BytesLow, BytesStreaming; // by the class of the tap (Classes; else Streaming)
    public int Duplicates; // messages received again (none, when resume is exact)
    public int Heartbeats, Frames;
    public uint HeartbeatSeq;
    public long LastFrame; // the last frame received (what a resume says)
    public Action<uint, GroupMirror>? OnGroup;
    public int RouterChanges; // RouterChange records received (behind a heartbeat, till echoed)
    public Guid RouterCorrelation; // …and the change that made it
    public RouterState RouterState; // the last heartbeat's: the router's own state…

    /// <summary>A tap's mailbox messages (and its topic's, with <paramref name="mailbox" /> false), as applied.</summary>
    public List<byte[]> Mail(uint tapId, bool mailbox = true)
    {
        return TopicMessages.Where(m => m.TapId == tapId && (m.Flags & Wire.FlagMailbox) != 0 == mailbox)
            .Select(m => m.Payload).ToList();
    }

    private void Count(uint tapId, int n)
    {
        if (!Classes.TryGetValue(tapId, out var lane)) BytesStreaming += n;
        else if (lane == Lane.High) BytesHigh += n;
        else if (lane == Lane.Normal) BytesNormal += n;
        else BytesLow += n;
    }

    public void Decode(ReadOnlySpan<byte> f)
    {
        Frames++;
        LastFrameRecords.Clear();
        LastFrameTaps.Clear();
        for (var o = 0; o < f.Length;)
        {
            var type = f[o];
            if (type != Wire.RouterChange) // rides with its heartbeat: not listed apart
            {
                LastFrameRecords.Add(type);
                LastFrameTaps.Add(type is Wire.Value or Wire.Table or Wire.Error or Wire.Group or Wire.TopicMessage
                    or Wire.OobRef or Wire.Rendezvous
                    ? BinaryPrimitives.ReadUInt32LittleEndian(f[(o + 1)..])
                    : 0);
            }

            o += Record(f[o..], true);
        }
    }

    /// <summary>Applies one record; returns its size.</summary>
    private int Record(ReadOnlySpan<byte> f, bool counted)
    {
        switch (f[0])
        {
            case Wire.Heartbeat:
                HeartbeatSeq = BinaryPrimitives.ReadUInt32LittleEndian(f[1..]);
                Heartbeats++;
                return Wire.HeartbeatSize;
            case Wire.RouterChange:
                (RouterState, RouterCorrelation) = ((RouterState)f[1], new Guid(f.Slice(2, 16), true));
                RouterChanges++;
                return Wire.RouterChangeLength(f);
            case Wire.Value:
            {
                var id = BinaryPrimitives.ReadUInt32LittleEndian(f[1..]);
                int len = BinaryPrimitives.ReadUInt16LittleEndian(f[5..]);
                Values[id] = f.Slice(7, len).ToArray();
                Applied.Add((Wire.Value, id, 0, Values[id]));
                if (counted) Count(id, Wire.ValueHeader + len);
                return Wire.ValueHeader + len;
            }
            case Wire.Table:
            {
                var id = BinaryPrimitives.ReadUInt32LittleEndian(f[1..]);
                var size = TableSize(f);
                if (!Tables.TryGetValue(id, out var t)) Tables[id] = t = new TableMirror();
                Apply(t, f[..size]);
                Applied.Add((Wire.Table, id, t.Seq, null));
                if (counted) Count(id, size);
                return size;
            }
            case Wire.Group:
            {
                var id = BinaryPrimitives.ReadUInt32LittleEndian(f[1..]);
                var size = Wire.GroupHeader + (int)BinaryPrimitives.ReadUInt32LittleEndian(f[8..]);
                if (!Groups.TryGetValue(id, out var g)) Groups[id] = g = new GroupMirror();
                ApplyGroup(id, g, f[..size]);
                if (counted) Count(id, size);
                return size;
            }
            case Wire.Error:
            {
                var id = BinaryPrimitives.ReadUInt32LittleEndian(f[1..]);
                var code = (FaultCode)BinaryPrimitives.ReadUInt16LittleEndian(f[5..]);
                var version = BinaryPrimitives.ReadInt64LittleEndian(f[7..]);
                int len = BinaryPrimitives.ReadUInt16LittleEndian(f[15..]);
                Errors.Add((id, code, version, f.Slice(Wire.ErrorHeader, len).ToArray()));
                return Wire.ErrorHeader + len;
            }
            case Wire.TopicMessage:
            {
                var id = BinaryPrimitives.ReadUInt32LittleEndian(f[1..]);
                var flags = f[5];
                var seq = BinaryPrimitives.ReadInt64LittleEndian(f[6..]);
                var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(f[14..]);
                var seen = (flags & Wire.FlagMailbox) != 0 ? _lastMail : _lastMessage;
                if (seen.TryGetValue(id, out var last) && seq <= last)
                    Duplicates++; // (applied all the same, as the browser would)
                seen[id] = seq;
                TopicMessages.Add((id, seq, flags, f.Slice(Wire.TopicMessageHeader, len).ToArray()));
                Applied.Add((Wire.TopicMessage, id, (flags & Wire.FlagMailbox) != 0 ? -seq : seq,
                    TopicMessages[^1].Payload)); // (a mailbox's numbered apart: negative here)
                if (counted) Count(id, Wire.TopicMessageHeader + len);
                return Wire.TopicMessageHeader + len;
            }
            case Wire.Rendezvous:
            {
                var id = BinaryPrimitives.ReadUInt32LittleEndian(f[1..]);
                var seq = BinaryPrimitives.ReadInt64LittleEndian(f[6..]);
                int len = f[14];
                var correlation = f.Slice(Wire.RendezvousHeader, len).ToArray();
                Rendezvous.Add((id, f[5], seq, correlation));
                Applied.Add((Wire.Rendezvous, id, seq, correlation));
                return Wire.RendezvousHeader + len;
            }
            case Wire.Service:
            {
                int len = BinaryPrimitives.ReadUInt16LittleEndian(f[2..]);
                var payload = f.Slice(Wire.ServiceHeader, len);
                if (f[1] == Wire.ServiceUp) // u8[16] correlation | the service's name
                {
                    ServicesUp.Add(Encoding.UTF8.GetString(payload[16..]));
                    ServiceCorrelations.Add(new Guid(payload[..16], true));
                    return Wire.ServiceHeader + len;
                }

                var node = new Guid(payload[..16], true); // u8[16] node | u8[16] correlation | …
                ServiceCorrelations.Add(new Guid(payload.Slice(16, 16), true));
                if (f[1] == Wire.ServiceDown)
                {
                    ServicesDown.Add(node);
                }
                else if (f[1] == Wire.ServiceDrained)
                {
                    ServicesDrained.Add(node);
                }
                else if (f[1] == Wire.ServiceDraining)
                {
                    ServicesDraining.Add((node, BinaryPrimitives.ReadUInt32LittleEndian(payload[32..])));
                }
                else if (f[1] == Wire.ServiceMove)
                {
                    Moves.Add((node, BinaryPrimitives.ReadUInt32LittleEndian(payload[32..]),
                        BinaryPrimitives.ReadUInt32LittleEndian(payload[36..])));
                }
                else if (f[1] is Wire.ServiceDegraded or Wire.ServiceRecovered)
                {
                    ProviderStates.Add((node, f[1] == Wire.ServiceDegraded ? "degraded" : "serving"));
                    ProviderScopes.Add(len > 32 ? Encoding.UTF8.GetString(payload[32..]) : null);
                }

                return Wire.ServiceHeader + len;
            }
            case Wire.Ended:
                Ended.Add(((FaultCode)BinaryPrimitives.ReadUInt16LittleEndian(f[1..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(f[3..]), BinaryPrimitives.ReadUInt32LittleEndian(f[7..])));
                return Wire.EndedSize;
            case Wire.Refused:
            {
                int serviceLen = BinaryPrimitives.ReadUInt16LittleEndian(f[18..]),
                    reasonLen = BinaryPrimitives.ReadUInt16LittleEndian(f[(20 + serviceLen)..]);
                Refusals.Add((new Guid(f.Slice(2, 16), true), Encoding.UTF8.GetString(f.Slice(20, serviceLen)),
                    Encoding.UTF8.GetString(f.Slice(22 + serviceLen, reasonLen))));
                RefusalKinds.Add((RefusalKind)f[1]);
                return 22 + serviceLen + reasonLen;
            }
            case Wire.Session:
            {
                int reasonLen = BinaryPrimitives.ReadUInt16LittleEndian(f[43..]);
                Sessions.Add((f[1], (RenewalPolicy)f[2], new Guid(f.Slice(3, 16), true), new Guid(f.Slice(19, 16), true),
                    BinaryPrimitives.ReadInt64LittleEndian(f[35..]), Encoding.UTF8.GetString(f.Slice(45, reasonLen))));
                return 45 + reasonLen;
            }
            case Wire.Auth:
            {
                int textLen = BinaryPrimitives.ReadUInt16LittleEndian(f[8..]);
                var payloadLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(f[(10 + textLen)..]);
                Auths.Add(((AuthOutcome)f[1], BinaryPrimitives.ReadUInt32LittleEndian(f[2..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(f[6..]), Encoding.UTF8.GetString(f.Slice(10, textLen)),
                    f.Slice(14 + textLen, payloadLen).ToArray()));
                return 14 + textLen + payloadLen;
            }
            case Wire.OobRef:
            {
                var h = new OobHandle(BinaryPrimitives.ReadUInt32LittleEndian(f[1..]), f[5],
                    (byte)(f[6] & Wire.OobModeMask), BlobId.Read(f.Slice(7, 16)),
                    f.Slice(23, 16).ToArray(), (int)BinaryPrimitives.ReadUInt32LittleEndian(f[39..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(f[43..]),
                    (f[6] & Wire.OobContentAddressed) != 0);
                if (h.Mode == Wire.OobTransparent) Pending.Add(h);
                else References[h.TapId] = h;
                if (counted) Count(h.TapId, Wire.OobRefSize);
                return Wire.OobRefSize;
            }
            default:
                throw new InvalidDataException($"Unknown record 0x{f[0]:X2}");
        }
    }

    private static int TableSize(ReadOnlySpan<byte> f)
    {
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(f[26..]);
        var stride = 1 + BinaryPrimitives.ReadUInt16LittleEndian(f[30..]) +
                     BinaryPrimitives.ReadUInt16LittleEndian(f[32..]);
        return Wire.TableHeader + count * stride;
    }

    /// <summary>
    ///     Fetches every pending transparent handle (as the browser would, by blob id + outlet + tag), applies it in place
    ///     of the record, and confirms with <see cref="Outlet.Fetched" />. Returns how many were fetched.
    /// </summary>
    public int FetchPending(Outlet outlet, OutletRegistry registry, bool confirm = true)
    {
        var n = 0;
        foreach (var h in Pending.ToArray())
        {
            Pending.Remove(h);
            byte[] data;
            if (!registry.Blobs.TryLease(h.Id, outlet.Id, h.Tag, registry, out var lease))
                throw new InvalidOperationException($"blob {h.Id} not fetchable");
            using (lease)
            {
                data = lease.ToArray();
            }

            if (h.ContentAddressed && BlobId.Of(data) != h.Id)
                throw new InvalidDataException("blob content does not match its id");
            ApplyBlob(h, data);
            n++;
            if (confirm) outlet.Fetched(0, h.TapId, h.Ticket);
        }

        return n;
    }

    public void ApplyBlob(OobHandle h, ReadOnlySpan<byte> data)
    {
        switch (h.Inner)
        {
            case Wire.Value:
                Values[h.TapId] = data.ToArray();
                Applied.Add((Wire.Value, h.TapId, 0, Values[h.TapId]));
                break;
            default:
            {
                var record = data.ToArray();
                BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(1), h.TapId); // the blob is shared: tap id 0
                Record(record, false);
                break;
            }
        }
    }

    private void ApplyGroup(uint id, GroupMirror g, ReadOnlySpan<byte> r)
    {
        var flags = r[5];
        int count = BinaryPrimitives.ReadUInt16LittleEndian(r[6..]);
        if ((flags & Wire.FlagReset) != 0)
        {
            g.Tables.Clear();
            g.Values.Clear();
            g.Resets++;
        }

        g.Degraded = (flags & Wire.FlagDegraded) != 0;
        var ok = true;
        var o = Wire.GroupHeader;
        for (var i = 0; i < count; i++)
        {
            var m = r[o..];
            var member = (int)BinaryPrimitives.ReadUInt32LittleEndian(m[1..]);
            if (m[0] == Wire.Table)
            {
                var size = TableSize(m);
                if (!g.Tables.TryGetValue(member, out var t)) g.Tables[member] = t = new TableMirror();
                var failures = t.Failures;
                Apply(t, m[..size]);
                ok &= t.Failures == failures;
                o += size;
            }
            else if (m[0] == Wire.Value)
            {
                int len = BinaryPrimitives.ReadUInt16LittleEndian(m[5..]);
                g.Values[member] = m.Slice(7, len).ToArray();
                o += Wire.ValueHeader + len;
            }
            else
            {
                throw new InvalidDataException($"record 0x{m[0]:X2} inside a group");
            }
        }

        if (o != r.Length) throw new InvalidDataException("group length mismatch");
        if (!ok)
        {
            g.Failures++;
            return;
        }

        g.Deliveries++;
        OnGroup?.Invoke(id, g);
    }

    private static void Apply(TableMirror t, ReadOnlySpan<byte> r)
    {
        var reset = (r[5] & Wire.FlagReset) != 0;
        var from = BinaryPrimitives.ReadInt64LittleEndian(r[6..]);
        var to = BinaryPrimitives.ReadInt64LittleEndian(r[14..]);
        var expected = BinaryPrimitives.ReadUInt32LittleEndian(r[22..]);
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(r[26..]);
        int k = BinaryPrimitives.ReadUInt16LittleEndian(r[30..]), rs = BinaryPrimitives.ReadUInt16LittleEndian(r[32..]);
        if (reset)
        {
            t.Rows.Clear();
            t.Checksum = 0;
            t.KeySize = k;
            t.RowSize = rs;
            t.AwaitingReset = false;
            t.Degraded = (r[5] & Wire.FlagDegraded) != 0;
            if (t.Snapshots > 0) t.LaterSnapshots.Add((to - t.Seq, count));
            t.Snapshots++;
            if (t.Deltas == 0) t.SnapshotsBeforeDelta++;
        }
        else
        {
            if (t.AwaitingReset || from != t.Seq + 1)
            {
                t.Failures++;
                t.AwaitingReset = true;
                return;
            }

            t.Deltas++;
        }

        for (int i = 0, p = Wire.TableHeader; i < count; i++, p += 1 + k + rs)
        {
            var key = r.Slice(p + 1, k);
            var row = r.Slice(p + 1 + k, rs);
            var id = Convert.ToHexString(key);
            if (t.Rows.TryGetValue(id, out var old)) t.Checksum -= RowHash.Of(old.Key, old.Row);
            if (r[p] == Wire.OpDelete)
            {
                t.Rows.Remove(id);
            }
            else
            {
                t.Rows[id] = (key.ToArray(), row.ToArray());
                t.Checksum += RowHash.Of(key, row);
            }

            t.Entries++;
        }

        t.Seq = to;
        if (t.Checksum != expected)
        {
            t.Failures++;
            t.AwaitingReset = true;
        }
    }

    public sealed class TableMirror
    {
        public readonly List<(long Versions, int Rows)>
            LaterSnapshots = new(); // each after the first: the versions since the last record, its rows

        public readonly Dictionary<string, (byte[] Key, byte[] Row)> Rows = new();
        public bool AwaitingReset = true, Degraded;
        public uint Checksum;
        public int KeySize, RowSize, Snapshots, Deltas, Entries, Failures;
        public long Seq = -1;
        public int SnapshotsBeforeDelta; // the activation's, normally one; later ones are the server's choice
    }

    /// <summary>A watch: member replicas (tables or values), applied together per Group record.</summary>
    public sealed class GroupMirror
    {
        public readonly Dictionary<int, TableMirror> Tables = new();
        public readonly Dictionary<int, byte[]> Values = new();
        public bool Degraded;
        public int Deliveries, Failures, Resets;
    }

    public readonly record struct OobHandle(
        uint TapId,
        byte Inner,
        byte Mode,
        BlobId Id,
        byte[] Tag,
        int Length,
        uint Ticket,
        bool ContentAddressed);
}