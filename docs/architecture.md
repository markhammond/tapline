# Tapline Architecture

How Tapline behaves, with driving principles. For writing a data provider, see [Data providers](provider.md);
for using Tapline from an app, [Clients](client.md).

## Design principles

- **Nothing unsolicited.** Every record a client receives belongs to a subscription it asked for, bound in two steps so
  that nothing arrives before the listener is ready to receive it ([Two-phase subscribe](#two-phase-subscribe)).
- **State is sent as state.** A source holds state (a value per key, or a table of fixed-size rows); a client's tap is a
  cursor on it, not a queue. Each client is sent what changed since its cursor, conflated, always ending on the latest
  state, so a slow client gets fewer, fuller deltas, never a backlog. Folds run once, at the router, against the
  canonical rows ([Interaction model](#interaction-model), [tables and folds](provider.md#reference-tables-folds-and-ingest)).
- **Messages are never conflated.** A message topic sends every subscriber every message, in order, from a bounded
  buffer read at each one's pace, and a mailbox carries one subscription's own; a message can be ordered after the
  state it's about ([Messages](#messages--topics-and-mailboxes)).
- **State only moves between valid versions.** Updates are transactional and can check what they apply to and what they
  produce; a unit of work spans sources atomically, and a watch reads them coherently. Anything else is an explicit,
  terminal error with a stale-data policy the app chose ([Watches and transactions](#watches-and-transactions--coherent-sets-of-objects),
  [making custom functions safe](provider.md#reference-making-custom-functions-safe)).
- **Deterministic.** Hooks say exactly when a change has taken effect, so nothing has to be guessed or waited out. A
  provider learns when its posts are applied (`WhenApplied`). A client reads its own writes: answered with a
  rendezvous, its await resolves only once its replica holds the change it made. A client waiting for its change is
  sent it at once (`Tap.Hurry()`), and a state no subscriber may skip by its own rate, a day's close say, reaches every
  one of them (`Source.Barrier()`). And every wake-up, pace and estimate runs off one clock, a `TimeProvider`, so a
  test cranks the handle by hand ([Messages](#messages--topics-and-mailboxes),
  [Streaming](#streaming-ready-queue-rate-limit-timing-wheel), [Latency](provider.md#latency-who-sets-the-pace)).
- **Fresh data leaves last.** Transport back-pressure only sees the next hop: proxies and middle-boxes absorb data into
  their own buffers and hide a slow client, and everything queued there is staleness. So a frame is bound only as it's
  sent; flow control measures the whole path from the client's own acknowledgements, caps what's in flight and paces
  to it; and the backlog stays in the outlet, where it conflates, with the pipes and kernel buffers behind it kept
  short. Priority lanes let what matters go first ([Streaming](#streaming-ready-queue-rate-limit-timing-wheel),
  [Flow control](#flow-control--after-bbr-steered-to-the-shortest-queue)).
- **Large payloads go out of band.** A large record in the stream would hold up everything behind it (head-of-line
  blocking), so it travels as a small handle in band and its bytes over HTTP ([Out-of-band delivery](#out-of-band-delivery)).
- **Everything a client holds is bounded:** its subscriptions, its mailboxes, its buffers, and how long it may go
  unacknowledged ([Client bounds](#client-bounds)).
- **Health is told, not guessed.** The router tells each client its own state (serving, degraded, draining) and each
  provider's, every change with a correlation id that the router, the provider and the client all log
  ([The router's own state](#the-routers-own-state--degraded-draining), [Health](provider.md#health)).
- **Shared-nothing scalability.** Tapline affords both horizontal and vertical scalability, with data providers
  determining subscription fan-out and topology. Routers share nothing: each holds its own clients' outlets and the
  topics they subscribe to, and none talks to another, so a router added is capacity added, with nothing to coordinate.
  A provider serves any number of routers. In step 1 it decides which subscriptions share a topic (published once, fanned
  out to each) and which have one of their own; it splits its service into partitions that fail on their own; and it
  moves subscriptions between its instances to rebalance, each placed by load. Within a router, a publisher's push is
  wait-free and the timing wheel is striped near the processor count, so a router grows with its cores
  ([Topological primitives](provider.md#topological-primitives--domain-boundaries), [Step 1](provider.md#step-1),
  [Partitions](provider.md#partitions), [Health](provider.md#health),
  [Streaming](#streaming-ready-queue-rate-limit-timing-wheel)).
- **Allocation-free in steady state.** Posting, folding, scheduling and sending allocate nothing per frame once warm:
  staging buffers are swapped rather than made, scratch is kept, and records are structs. The test suite checks it
  ([Tests](../tests/README.md)).

Areas for future improvement are outlined in [roadmap.md](roadmap.md).

## Interaction model

| Convention | Server API | Semantics |
|---|---|---|
| Message topic | `publisher.Messages(name, capacity, capacityBytes, overflow).Publish(bytes)` | Every message to every subscriber, in order, never conflated; a bounded buffer read at each subscriber's pace; overflow: fault (default), skip or refuse (below). |
| Mailbox | `tap.TryPost(bytes)` · a provider's `request.Mailbox.TryPost(bytes)` | One subscriber's alone, in order with its subscription's records; `false` = full of messages not yet sent. |
| Last value | `publisher.LastValues<TKey>(name).Publish(key, bytes, delivery)` | Conflated per key: only the newest value matters. |
| Replicated table | `table.BeginUpdate()` → `Set` / `Delete` / `Fold` → `Complete()` | Transactional; squashed row deltas with `fromSeq/toSeq` + checksum; snapshot on tap or client resync; after a reconnect, on with deltas from what the client received. |
| Unit of work | `Transaction.Begin(a, b, …)` → `tx.Of(a)…` → `Complete()` | Several sources published atomically (all or none; a failure faults all). |
| Watch | `outlet.Watch(topic, policy, WatchMember.Of(t), WatchMember.Range(t, prefix), …)` | One coherent record per change across members: never part of a transaction. |
| Out of band | `Delivery.Auto / OutOfBand / Reference` | Large payloads: a 47-byte handle in-band, the bytes fetched over HTTP. |
| Fault | `ErrorPolicy` per tap | Degraded snapshot (app-defined) + Error record; the tap ends, the client resubscribes. |
| Heartbeat | automatic | First record of the next frame; exempt from credit and never holds the window; at most one in flight (the next waits for the echo, so they can't pile up on a slow link); echo gives `outlet.Rtt`. |
| Router state | `registry.SetHealth(state, reason, grace)` · Tapline.AspNetCore: from the host's health checks, damped; draining as it stops | Serving, degraded (why) or draining (the grace), and the change's correlation id: behind a heartbeat until the client has it. |

Saturated byte shares: High 16 : Normal 4 : Streaming 4 : Low 1 (deficit round robin with overdraft; turn state kept
across frames; work-conserving; quanta sized from the frame budget in use, so on a slow path a class's turn lasts about
a frame rather than seconds). Streaming serves taps oldest-dirty-first and is strictly ordered within one tap;
it can be rate-limited per client (below). A tap can instead be bound into a lane's class — `outlet.Tap(source, topic,
lane: Lane.High)` — for state that must not wait behind bulk state: it is still a tap (conflated, deltas, snapshots,
faults) but gets that class's share, with the class's other taps, and is outside the Streaming rate limit; on
High it isn't held to `MaxFramesPerSecond` or pacing either. The waveform demo's audio rides High this way while its
ridges fill Streaming: with a 16 KB/s cap and 6 KB of bulk every millisecond, a 500 B update every 40 ms goes in the next
frame 75 times of 75 on High, and 1 of 75 as a Streaming tap.

**Making way for High.** Priority in the scheduler only orders what is still in the outlet; on a slow path what delays
a High record is what was already sent — frames queued at the bottleneck, and the flow-control window they fill. So
while High has sent within the last 2 s, on a path where one frame of the other classes takes longer than 50 ms to send,
those classes send one frame at a time, only once what went before is through (in flight: no more than 50 ms of the
path; and room left under the limit): a High record never waits for room, only behind the one frame on the wire. Never
for more than a second at a stretch — High can't starve them — and on a fast path, where a frame is short, nothing
changes. At 16 kbit/s with the demo's music on High: rows arrive at worst 30 ms after they are due, where with
`?priority=off` (the same tap in Streaming) 663 ms; simulated, p99.5 907 → 284 ms, the ridges 2.6 → 2.0 frames a second.

## Streaming: ready queue, rate limit, timing wheel

**Ready queue — oldest-dirty-first.** Each outlet keeps a coalescing queue of its taps. A tap joins the back once, on
its clean→dirty transition; while queued, further changes coalesce into it and it keeps its age; once served it
leaves, and its next change makes it newly dirty. So the tap that has waited longest goes first, a hot tap can't
starve a quiet one, and building a frame costs O(dirty taps), not O(taps). Publishers push onto a wait-free
intrusive MPSC inbox (Vyukov) — one `CompareExchange` on the clean→dirty transition, one `Exchange` per push, nothing
at all while a tap is already dirty — and the frame builder drains it into a private FIFO it can scan and skip. A
record that doesn't fit keeps its place while smaller ones behind it go; it is not re-examined again in that frame,
and it gets the next frame first.

**A subscription's own rate.** `Tap.MinInterval` (none by default; asked at step 1 by `SubscribeOptions.MinInterval`,
which its provider sees with the connection's cap on `SubscribeRequest.Limits`): a subscription sent a state record isn't sent
another before the interval has passed — then the state as it stands, whatever changed meanwhile conflated into it
(nothing changed: nothing sent). Each subscriber its own, whatever others of the same topic take; its mailbox's
messages aren't held, nor is a message topic's stream (nothing of it conflates). `Tap.Hurry()` lets one through at
once — a client waiting for its own change (the pivot sample's tokens) — and `Source.Barrier()` does for every
subscription of a source (a day's close no subscriber may conflate past; call it once the barrier's state is applied).
It rides the outlet's retry timer: a held subscription costs nothing until it's due.

**Rate limit — per client.** `OutletOptions.StreamingBytesPerSecond` / `StreamingBurstBytes`, or per user at runtime:

```csharp
outlet.SetStreamingRate(tier.BytesPerSecond, burstBytes: 256 * 1024);   // e.g. 16 KB/s … 300 KB/s; 0 = uncapped
```

A `TokenBucket` with overdraft, refilled by calculation rather than by a timer: a snapshot may take the budget
negative, and Streaming then sits out (the other classes keep the frame) until elapsed time repays it. A capped client's taps
simply lag and squash more — fewer, fuller deltas, always ending on the latest state — and the cap also bounds how much
of the circuit's socket the stream takes from Blazor's own render traffic. Size the burst to the largest snapshot so a
new subscriber isn't kept waiting. `TokenBucket` is public and suits a per-user fuel budget the same way.

To try a slow consumer without throttling the page, its static assets or out-of-band fetches, let the page URL lower
the cap: `BlazorRealtimeOptions.StreamingRateQueryParameter = "bwlimit"`, then open `/?bwlimit=64`. The value reads as
rsync's `--bwlimit`: KiB/s unless suffixed — `b` bytes, `k`/`m`/`g` (or `kib`…) powers of 1024, `kb`/`mb`/`gb` powers of
1000, optionally `+1`/`-1` — so `0.5` is 512 B/s (rsync's minimum; about 4 kbit/s), `500kb` 500,000 B/s, `1.5m`
1.5 MiB/s; `0` (no limit) or anything malformed leaves the cap alone. Read when the circuit opens; it can only lower
`StreamingBytesPerSecond`, never raise it; the burst is one second's worth. Only Streaming is capped — heartbeats and
taps bound into a lane's class are not.

**Timing wheel — wake-ups.** An outlet with nothing it may send now parks (its transport awaits `WaitAsync`). Only then,
and only if something is held back by time — its next frame not yet due (pacing, spacing, a frame cadence), its
Streaming budget in debt, a retry — does it register with the registry's `TimingWheel`, once, for the earliest; its
deadlines are its own fields, written by its own frame builder. A busy outlet, or one waiting on data or an
acknowledgement, never touches the wheel. The wheel wakes it within a tick (1 ms) of that time, and its callback is
just that wake: what was due (a retry's tap released) the outlet's own thread sees to.

The wheel is built for the worst case. Schedulers never lock or wait: a schedule records the time asked for and posts
the node — once, till taken — to a lock-free stack; a scheduler preempted mid-way holds up nobody. The wheel itself is
its driver's alone (one thread, running only while something is scheduled): it takes the posted requests, places them,
then fires what is due. It is striped — a power of two of wheels near the processor count, each outlet in one — so posts
spread over cache lines, and a pass fires at most 256 of a stripe's entries before turning to the next, so a burst on one
stripe delays the others' wake-ups by a pass, not by the burst. O(1) schedule and cancel, zero allocation, never early.
The wheel's `TimeProvider` is the clock for every wake-up in that registry and for flow control's budgets and estimates
— pacing, frame spacing, the delivery rate and round trip it steers by (tests pass a manual clock and drive `Advance`).
Housekeeping timeouts — a binding's, a fetch's, a stalled client's — and the heartbeat's round trip (`Outlet.Rtt`) run on
the process clock.

## Flow control — after BBR, steered to the shortest queue

A frame is bound late — filled in when the transport serializes it — so its data is as fresh as the moment it leaves
the outlet. After that it may wait in three buffers on the server (SignalR's pipe, Kestrel's socket pipe, the kernel's
send buffer: 64 KB or more each) and in any on the way, and every byte queued there is staleness the client sees. The
client acks every frame on arrival; from those acks the outlet measures the round trip — from the instant the frame was
bound, so all of that is in it — and the delivery rate.

**Adaptive (default, `OutletOptions.FlowControl`).** Bytes in flight are capped at twice delivery rate × minimum round
trip (taken as at least 5 ms: below that a busy thread looks like a queue). While the application is the limit — the
usual case for a fast client — the cap is headroom and frames go the moment there is data: a colo client gets every
change as fast as it acks them (or as its `bwlimit` allows), and a long, fast path gets an even stream — data frames are
spaced at least minimum round trip ÷ half the `Window` apart (64: at 200 ms, 160 frames a second, each carrying
everything since the last), never a window's burst per round trip. While ready data is held back, the cap doubles each
round trip (startup, from `InitialInflightBytes`, 1 KB). Once the path shows itself to be the bottleneck — a standing
queue (the round's best round trip, allowing for a larger frame's transmission time, above the minimum by more than
`QueueDelayTarget`, 2 ms), or a rate that stopped growing — frames are paced at the measured rate, each bound as the path
frees up, and sized to half its bandwidth × delay (fresh data; a heartbeat never waits long behind one). While data is
held back it probes a quarter faster for one round in eight, eases off a quarter after a probe or a queue, and drains
at half if a queue survives that. Three frames in a row back late by more than the queue they were sent behind
explains — by over 100 ms, or twice the minimum round trip if more, plus twice the jitter seen — mean the path suddenly
slowed (a browser's moment of stall, or its messages handled in batches, is neither three in a row nor that late), and
hold everything: one more frame goes behind the backlog and nothing after it until it is back, by when the backlog has
drained ahead of it, so the rate it measured is the path's; the estimate drops to that at once rather than over ten
rounds of queueing at the old rate. A stale minimum (10 s) is re-measured, for free when nothing was held back.
Records larger than the path's bandwidth × delay (a 6 KB value at 256 kbit/s) are allowed for: in steady state the
limit is at least the largest recent frame over the bandwidth × delay, so the path isn't left idle — nor everything
else waiting for an ack — behind one; a small frame that waited behind our own large one isn't mistaken for a
collapse; and the pacing gate follows a rate change at once rather than keeping a slower rate's wait. Rate samples span
the longer of the send and ack intervals, as in BBR, so acks bunched by the path or a busy client don't read as a
faster path; a frame sent with nothing in flight starts its sample at its own send, also as in BBR, so an idle spell
doesn't read as a slow path. A round that only a heartbeat ended — nothing to send — leaves the rounds that measured the
path in the estimate: seconds of heartbeats alone don't leave the next burst paced at the heartbeats' rate.

**An acknowledgement overdue (adaptive): state waits, messages go.** A client acknowledges a frame once it has applied it — a
browser, on its page's main thread. A frame unacknowledged for longer than `OutletOptions.AckOverdue` (50 ms; zero: off)
and three smoothed round trips means the client is busy (a long task, a collection, a frozen tab), not that the path is
slow, and frames sent meanwhile would only pile up at its end, to be applied all at once, out of date. Until an
acknowledgement comes, state waits — values, tables, watches: what changes meanwhile conflates as ever, and goes as one
record each, the newest — while message topics and mailboxes keep going, past the bytes-in-flight cap and within the
`Window` (64 frames): nothing of theirs conflates, so holding them saves nothing, and a held subscriber would fall
behind its topic's buffer (`Fault`: its subscription ended for a busy moment). A message ordered after state brings that
state with it (`After`). Frozen 300 ms in the tests (`AckOverdueHoldsState`), a value and a table went as one record each
on its return, and all 60 messages published meanwhile went as they were published; off, state took the cap's room, 15
of the 60 went, and the topic overflowed. `outlet.StateHolds` counts the holds.

**Fixed.** `FlowControlMode.Fixed`: at most `Window` data frames unacked, unpaced.

**Frame cadence (opt-in).** `OutletOptions.MaxFramesPerSecond` (0, off, by default): at most that many data frames a
second, phase-locked, each carrying everything that changed since the last — e.g. 60 for a view that can't show more.
`outlet.SetMaxFramesPerSecond(n)` changes it while the outlet runs (0 removes it) — to the client's display refresh as
it reports it, say, or low while its page is hidden; the next frame is due a new period after the last. Heartbeats and
taps on High aren't held to it, and a tick whose data overflows the frame budget continues at once (a single
record larger than the budget, sent alone, doesn't hold it open).

**Diagnostics.** `outlet.MinRtt`, `SmoothedRtt`, `DeliveryRate`, `InflightBytes`, `InflightLimit`, `PacingRate` (0:
unpaced) — the sample page shows them.

Simulated on a manual clock (`PathSimulation`): a bottleneck behind a FIFO of unbounded depth (Kestrel, a middlebox), a
hot book (3 row changes per ms) and a value published every ms; *age at paint* is how old the newest value shown is at
each 60 Hz paint — p99.5 (frames per second):

| Path | Fixed, 2 frames | Adaptive | Adaptive, 60 Hz |
|---|---|---|---|
| colo, 10 Gbit/s, 0.05 ms one way | 1.0 ms (1,000) | 1.0 ms (1,000) | 1.7 ms (60) |
| 50 Mbit/s, 15 ms | 44.8 ms (66) | 16.0 ms (1,000) | 21.5 ms (60) |
| 50 Mbit/s, 100 ms | 296 ms (10) | 106 ms (182) | 101 ms (60) |
| 2 Mbit/s, 40 ms, + 60 KB bursts every 2 s | 305 ms | 277 ms | 280 ms |
| 16 kbit/s, 75 ms | 649 ms (p50 518) | 737 ms (p50 498) | 737 ms (p50 498) |
| 50 → 1 Mbit/s at 6 s, 20 ms | 66.5 ms | 41.8 ms | 28.0 ms |
| 50 Mbit/s → 16 kbit/s at 6 s, 20 ms | 703 ms (p50 556) | 4.7 s (p50 480) | 1.39 s (p50 547) |

Fast paths run at the data rate with one way's delay as the floor. A 60 KB burst ahead in the FIFO is 240 ms at 2 Mbit/s
for whatever is behind it, in any mode. At 16 kbit/s a frame takes ~225 ms to send: fresher at the median, while probing
for more bandwidth costs at the tail; a new connection there overshoots at first (the 1.1 KB activation
snapshot alone takes 0.56 s, and nothing is known about the path until it is acked), clearing in ~3 s, which that run
leaves out. A sudden 3,000× drop turns what was in flight at that instant into queue — rate × round trip, ~4 KB for an
uncapped 100 KB/s stream at 40 ms, seconds at 16 kbit/s — the price of not holding fast clients back; the hold stops it
growing, and a 60 Hz stream had less in flight. Live, through a 16 kbit/s proxy with a 64 KB buffer in front of the
link, the sample's heartbeat round trip stays at 120–160 ms, frames paced at the link's 1.6–1.8 KB/s.

An application that sends in bursts, as a provider's link to its router does (`BurstSimulation`: a turn's records
every half second, seconds of heartbeats alone, a receiver late now and then, on colo), gets each record across as a
fixed window does: 0.1 ms, and at most 2.4 ms after 10–20 s of heartbeats with a 1 ms receiver.

## Two-phase subscribe

1. **Bind** — `outlet.Tap(source, topic)` (or, in a gateway, a provider resolve) creates a tap and returns its
   `Binding` — `TapBinding(TapId, Topic, Endpoint, …)`, with a subscription through a provider its service and
   instance, and with an error policy what the client may apply of it itself (`Stale`: [stale data](provider.md#reference-the-protocol-nchan-style)) — to the caller. Nothing
   streams. `Endpoint` is optional
   (`OutletOptions.Endpoint`, this node's advertised address): clients that send step 2 as a separate request through
   load-balanced or multi-value DNS use it to reach the node holding the binding. Blazor circuits ignore it — their
   activation rides the connection that already reaches this node (load-balancer affinity keeps it there).
2. **Activate** — the client registers its handler for that id, then sends `Tap.Activate(outletId, nonce, tapId)` on
   the same connection (`tapline.subscribe(id, fn)` does both, in that order). The first record is a snapshot (or, for
   a stream that starts at its next change — a message topic always — the first change after step 1).

So no record can arrive before there is somewhere to put it, and the client never buffers. The id is not a capability
on its own: activation is checked against the outlet's nonce, ids it never issued are ignored, one for a subscription
that has ended is answered (below), duplicates are no-ops, and the client re-sends activations on every reconnect
(heals an activation lost with a dropped connection).
Taps bound but not activated within `BindTimeout` (default 30 s) are closed. Server-driven taps can skip step 2 with
`outlet.Tap(source, activate: true)`.

**Reconnects — the router resumes.** Binding again on a new connection, the client says one thing: the last frame it
received (`Tap.Resume(outletId, nonce, lastFrame, hbSeq)`; the bind call goes out on the new connection ahead of any
frame, so that is the last frame of the one before). The client tracks nothing per subscription. The router keeps, per
subscription, where it was after each frame not yet acknowledged — a few entries, the frames in flight — so every
subscription that sent anything is held until then, and goes on from exactly what the client got: a table with a delta
from its replica's version (a snapshot only if the table can't), a value only if it changed, a message stream and a
mailbox from the next message the client didn't get — none twice, none lost while still kept; an out-of-band record
it hadn't fetched goes again. A watch can't be set back (its members' cursors are its own): if the client missed
something of one, a snapshot. A fetch the client confirmed as its connection fell is confirmed again at the bind,
before the resume. No word within `BindTimeout`: on from what the client acknowledged.

**What ended meanwhile.** Binding again, the client also re-sends the activation of every subscription it holds — and
that reconciles the two sides. A subscription the router no longer holds — it ended while the client was away, its
error record lost with the connection, or the app closed it server-side — is answered with an `Ended` record for that
one subscription, saying why (the router remembers why the last 64 ended for each client; older: `FaultCode.Gone`). The
client fails it (`onError`, from the server), and the app subscribes again if it wants to. So after a rebind, the
client holds exactly what the router does. One that ends while connected is told in the stream as before: an error
record, or a notice (a service down or drained, a stall).

**Noticing a dead connection.** No heartbeat of its own beyond the router's: the transport's timeouts act on the
traffic it makes (SignalR's, on a circuit). Each client's outlet sends a heartbeat record every
`TaplineOptions.HeartbeatInterval` (1 s) and the client acknowledges every frame, so neither direction of a live
connection is quiet for long. BlazorRealtime sets the circuit hub's `ClientTimeoutInterval`
to 10 s (`CircuitClientTimeout`; SignalR's default is 30 s) and its keep-alive ping to 3 s (`CircuitKeepAliveInterval`,
for before the heartbeats start) — leaving the app's own hub settings, if it made any. The browser client sets its
connection's server timeout from what it sees: four times the longest recent gap between frames (halving every 30 s),
at least 10 s, at most SignalR's 30 s — seconds on a fast path, longer where a slow one needs it (its timer resets only
on whole messages: one large message on a slow link must not read as a dead connection). A dead connection is dropped
by SignalR within that, Blazor reconnects, and the router resumes. `tapline.staleMs` says how long ago the router was
last heard from (wall clock: across a sleep too) — for a staleness indicator.

## Watches and transactions — coherent sets of objects

A **transaction** publishes one unit of work across several sources — tables and last-value sources:

```csharp
using (var tx = Transaction.Begin(positions, cash, marks["BTC"]))
{
    tx.Of(positions).Fold<long, Add<long>>(new Holding(acct, sym), +2);
    tx.Of(cash).Set(acct, balance - cost);
    tx.Of(marks["BTC"]).Publish(markBytes);
    tx.Complete(upstreamVersion: msg.Seq);
}
```

Locks are taken in one process-wide order. `Complete()` checks every member (pending fold faults, `ExpectChecksum`
postconditions) before publishing any. A failure — or a precondition (`Transaction.Begin(expectedUpstream, …)`) —
rolls back and faults **all** members, since they all missed the same unit of work; `Transaction.BeginRecovery` rebuilds
them. Disposing without completing rolls back. One open transaction per thread, allocation-free after first use.

A **watch** is one subscription over several members, modelled on etcd's Watch (one revision per transaction; "updates
in the same revision over multiple keys will not be split"):

```csharp
var account = outlet.Watch("account:42", AccountPolicy,
    WatchMember.Range(positions, 42),        // the rows whose key starts with 42's bytes (a key range)
    WatchMember.Range(cash, 42),
    WatchMember.Of(marks["BTC"]));
```
```js
tapline.subscribe(account.binding, ({ members, degraded }) => render(members), onError);   // once per coherent update
```

Each delivery is one **Group** record holding every member that changed, written while holding all member locks — so it
is always a consistent cut, never half a transaction. The client applies all members, then notifies once; a member
failing verification resyncs the whole group. A key-range member keeps the checksum of just its rows per subscriber
(O(changes) per delta), sends nothing when only rows outside the range change, and keeps the sequence check intact
across the skipped versions. Everything else works as for a tap: activation, snapshots, resync, rate limits, out of
band (a large group travels as one blob) and faults (members given a degrade function send their
degraded state, then the error). A watch never blocks while holding a member lock (it backs off and retries), so a
writer that nests locks out of order can't deadlock with it.

**Lock domains — contention stays with what is written together.** Sources written together form a lock domain: a
group's members, and sources a transaction has written together (merged on first use, for good). A source written alone
has none, and pays nothing for it. A watch reads one domain at a time, each under its own members' locks only — never
holding one source's lock while reading an unrelated one — so its contention reaches only sources that are written
together anyway. The client knows nothing of domains, and needn't:

- **Each delivery is a consistent cut.** Within a domain, whole units; between domains there are no units to split.
  Every domain change bumps its sources' counters (null to null included); a watch checks them after reading, and if
  one moved — a join, a leave, a transaction merging domains — the delivery is dropped and the watch resyncs, reading
  all its sources under all their locks at once.
- **A busy domain doesn't hold the others back.** While no domain has changed since a watch's last delivery, a domain
  whose locks are busy, or that doesn't fit the frame, waits for the next delivery and the rest go now: domains apart
  throughout share no unit. After a change, the next delivery reads every domain together, once.

**Router threads never wait on a source lock.** Frame builds and ingest drains try a source's lock briefly — spinning
only while a spin is just a spin (`SpinOnce(-1)` until `NextSpinWillYield`: never yielding the timeslice, never
SpinWait's own millisecond sleeps) — and, if it is held — a long update, a busy domain — back off deliberately: a tap is
retried 1 ms later on the outlet's wheel while the rest of the frame goes on, a drain is queued again. Only a provider's own synchronous writer (`BeginUpdate`, a `Transaction`) waits, on its own thread. So a
standalone topic, in no domain, is never delayed by anyone else's contention.

## Out-of-band delivery

Large payloads travel as a 47-byte **handle**; the bytes are fetched over a separate HTTP connection, so there is no
fragmentation layer and big snapshots stop sitting in front of Blazor's render batches on the circuit socket.

| `Delivery` | |
|---|---|
| `Auto` (default) | In-band up to `OutletOptions.OutOfBandThreshold` (16 KB); larger → `OutOfBand`. Also any record bigger than `MaxFrame`. |
| `Inline` | Always in-band (a value: 64 KB at most). |
| `OutOfBand` | Transparent: the client library fetches (and, content-addressed, verifies) and applies the bytes in order; the tap waits for `Tap.Fetched` (nothing buffers on the client; its changes keep coalescing, a message stream's wait behind it). |
| `Reference` | The handle is handed to the app (`BlobRef { id, length, url, bytes() }`); nothing waits. |

- **Shared without hashing.** A source keeps the last record it sent out of band, and any other tap that would write
  the same bytes gets a handle to it instead — every tap at the same cursor (a snapshot; the same delta to every
  caught-up tap) or, for a value, every tap behind — while the source can still resume from where it leaves them. A
  message topic's large message is stored once for every subscriber. None of it hashes anything.
- **Content addressing, by choice.** By default a blob's id is a UUIDv7 — a millisecond timestamp, a counter within the
  millisecond, 62 bits of entropy (the store's own, drawn once; per id with `entropyPerId`) — time-ordered and never
  reused, across stores, processes and restarts: only the copy is paid, and a URL never comes to name other bytes. `Source.ContentAddressed = true` (a table, a value, a message
  topic) names it by the first 128 bits of its SHA-256 instead: identical bytes are found before they are copied
  and stored once, and the browser verifies them with WebCrypto (the handle says which).
- **Pooled, leased.** The store keeps blobs in fixed segments (16 KB; under the large object heap's threshold) from its
  own pool, chained as a `ReadOnlySequence<byte>` — never one large array. A blob's memory has a reference count: the
  store's own until it evicts the blob, and one per open `BlobLease` — the fetch endpoint writes the segments under a
  lease, so an eviction mid-response doesn't recycle bytes still being sent; the last reference returns them to the
  pool. Puts, issues, pins and evictions allocate nothing once the pool is warm; free segments beyond `RetainBytes`
  (32 MB) go to the GC. DEBUG builds assert the counts and ownership and poison recycled segments.
- **Scoped.** The URL is `{blobBase}/{blobId}/{outletId}/{tag}` with tag = HMAC(blob, outlet id, outlet nonce): only
  outlets it was issued to can fetch it, and the URL doesn't reveal the nonce. Responses are privately cacheable
  forever (no id is ever reused) and support Range requests. Map it with `app.MapTapline()` (`app.MapBlazorRealtime()` does).
- **Lifetime.** A blob lives for `BlobStore.Grace` (60 s) after it was last issued. The store is bounded (256 MB, in
  whole segments): a full store backs a tap whose record would go out of band off briefly.
- **Tickets.** Each transparent handle carries a ticket; `Tap.Fetched(0, tapId, ticket)` releases only that handle, and
  the client drops a fetch overtaken by a reconnect or a newer record — a late answer can never roll a view back.
- **Budget.** A handle's blob size is charged to the client's Streaming rate limit when it is sent.

## Messages — topics and mailboxes

Nothing reaches a client unsolicited: everything an outlet sends is a subscription's. An alert, a trade, an event is a
**message topic** (Nchan's channel) the client subscribed to like any other source, and a message for one subscriber
alone goes to its subscription's **mailbox**.

```csharp
var alerts = publisher.Messages("alerts", capacity: 1024, capacityBytes: 1 << 20, overflow: OverflowPolicy.Fault);
alerts.Publish(bytes);                                   // 201/202 like any topic (epochs apply)
var tap = outlet.Tap(alerts, "alerts", lane: Lane.High); // subscribed — on High, if it mustn't wait behind bulk state
tap.TryPost(bytes);                                      // this subscriber's alone (a provider: request.Mailbox.TryPost)
```

- **A message topic** keeps each message once, in a bounded buffer (a power of two of messages, and bytes — the largest
  message, too), numbered from 1; each subscriber reads it at its own pace from its cursor. Nothing conflates: every
  message, in order, from the next one published after step 1 — one published before its activation too. A message
  topic has no snapshot (recent history for a newcomer is the provider's to send, through the mailbox — on step 2 with
  `InitialState.Provider` ([initial state](provider.md#reference-the-protocol-nchan-style)), the router sequencing it ahead of the topic — from its own
  store; or it is state — a table of the last N). With nobody subscribed, a message isn't kept at all (202), and the
  buffer goes back to the pools with the last subscriber. Large messages go out of band, stored once for every
  subscriber.
- **Overflow** — a subscriber falling behind the buffer (a message it hasn't been sent is evicted) meets the topic's
  policy: `Fault` (default) ends its subscription (`FaultCode.Overflowed`) — it never silently misses one; it
  resubscribes, and recovers what matters from a state topic; `Skip` jumps to the oldest kept and flags the gap; `Refuse`
  refuses the publish (507, `PublishStatus.Full`) rather than evict a message an active subscriber hasn't been sent —
  back-pressure on the provider, from subscribers whose clients are connected: one whose client is away (its connection
  gone, or back but not yet resumed) doesn't hold the topic back — it falls behind, and overflows as under `Fault`.
- **A mailbox** is a subscription's own ring (`MailboxMessages` 32, `MailboxBytes` 16 KB), made at its first post,
  numbered apart from its topic, flagged on the wire, and sent before the topic's next record — but never ahead of a
  record of its own being fetched out of band, nor of its own state an entry is ordered after (below). A post is refused
  (`false`) when the mailbox is full of messages not yet sent: the subscriber isn't keeping up.
- **Resumed after a reconnect**, by the router (above): sent messages stay kept until evicted, and each stream goes on
  from the next message its client didn't get; one evicted meanwhile is an overflow.
- In the browser: `tapline.subscribe(binding, onState, onError, onMessage)` — `onMessage(bytes, id, { seq, gap,
  mailbox })` (else `onState`), once per message.

**Why no unsolicited messages.** A client can act only on a message it has code for, and that code knows the service
exists: it subscribes where it registers its handler. Subscribing gives a message what an unsolicited one lacks —
dispatch by topic, ordering after the state it refers to, resume after a reconnect, an overflow policy, the viewer's
history, and a place in its client's budget (below). What stays unsolicited is the router's own control plane —
heartbeats, service notices, a stalled client's `Ended` record — not an app API. `Lane` is a tap's scheduling class,
nothing more.

**Ordering after state.** A message can be ordered after the state it refers to — a fill after the position it changed,
a reply after its order row:

```csharp
fills.Publish(fill, After.Current(positions));            // each subscriber: after its own tap on positions, if it has one
tap.TryPost(reply, After.Current(ordersTap));             // a mailbox: after a source, or one of the same client's taps
```

If the subscriber's tap on that source is behind when the message's turn comes, the tap's record is pulled forward
into the same frame, ahead of the message (overdrawing a rate limit rather than delaying it). One that can't get there
yet holds the message: out of band until its `Tap.Fetched`, its provider degraded until it recovers, its initial state
until the provider completes it, the version not yet published until it is — released by that event, nothing polling.
A mailbox entry ordered after its own subscription's state waits for that state to go first. A subscriber with no tap
on that source isn't held, nor is one whose tap closed or can't be sent (oversize). A constraint lives with its message
— in the topic's ring or the mailbox — and goes when the message does: nothing waits anywhere for subscribers that are
gone. Messages without one cost nothing extra.

**Rendezvous — when a change a client asked for is in front of it.** A client that applies a change through a request
(an order, an edit) needn't guess when it shows: it gives the request a correlation id, and whoever applies the change
hands the router that id with the sequence the change made; the router delivers it to the client strictly after the
subscription's state includes the change — applied, an out-of-band record fetched.

```csharp
var placed = orders.Publish(order);                       // PublishResult.Seq: what it made (a table update: Complete())
request.Mailbox.TryRendezvous(correlation, placed.Seq);   // the provider, on the subscriber's route (an app: tap.TryRendezvous)
```

```js
const id = tapline.correlation();                         // a UUIDv7, hex: tapline.correlationTime(id) is when it was made
const shown = tapline.rendezvous(id, { tapId });          // awaited before the request goes: nothing can come first
await placeOrder({ ...order, correlation: id });          // the request's own answer: accepted, or not
const { missed } = await shown;                           // the change is in front of it now
```

It is a mailbox entry ordered after that state (above), so it inherits all of it: pulled forward in the same frame,
held while the state can't move, resumed exactly across a reconnect, refused (`false`) when the mailbox is full — then
answer the request without it. A table or a value may be conflated past the version: the rendezvous follows a state
that includes or supersedes it. A message topic's message the subscription was never sent — published before it
subscribed, or skipped (`OverflowPolicy.Skip`) — arrives flagged `missed`. Given `tapId`, the promise rejects if that
subscription ends first; `signal` aborts it; one nobody awaits goes to `tapline.onRendezvous`. The page never compares
sequence numbers. **They are the router's**: a source's, on this router, for as long as it lives here — for delivery
(resume, ordering, rendezvous), not identity. To correlate events end to end, carry ids in the payloads.

**Sync — is a client up to date?** A client can ask the router itself for a rendezvous: `Tap.Sync(tapId, syncId)`
(`tapline.sync(tapId)`, `TaplineClient.SyncAsync(tapId)`). The router rendezvouses after the subscription's source
version as it received the ask, so the answer means the client was up to date as of then: prompt when nothing holds the
subscription's changes back (a quiet source, a fast path), as late as their delivery otherwise (a rate cap, an
interval, an overdue acknowledgement). It is how a client tells a quiet subscription from one whose changes are
waiting, which updates alone can't: a quiet source sends nothing, and so does a held one. Its correlation is the
client's own count, four bytes, never an app's id. Asked while unbound, nothing is sent: give it a timeout. The order
books sample's tiles fade on it (their README).

A post to an ingest ([ingest](provider.md#reference-tables-folds-and-ingest)) doesn't know the version it makes — the router makes it when it applies the post — so its
provider asks for a rendezvous after what it has posted: `ingest.TryRendezvous(request.Mailbox, correlation)`. The
router hands it over once it has applied every post made before the call (or a snapshot posted after them, superseding
them), at the version that holds them — at once, if nothing waits. A provider whose subscribers ask for something new
(a query, say) can so tell each when its first answer is in front of it: the page swaps its view then, never showing it
empty.

## Client bounds

Conflation keeps a slow client's backlog out of the router: a tap holds a cursor, not a queue — what the client hasn't
been sent is the topic's, and it gets the latest, squashed. What still grows with a client is how many subscriptions it
holds, and how long it lingers. Two bounds per client (its outlet):

- **Memory budget** — `OutletOptions.MemoryBudget`, 4 MB by default (0: none). Each subscription is charged for what the
  router keeps for it: the tap and its history ring at bind (~2 KB with the default 16-record history: ~2,000 fit), its
  mailbox's ring once its first post makes it (~16.5 KB), and a provider's snapshot while it waits. Over budget, a new
  subscription is refused — `OutletFullException`; through a provider, before the provider is asked (the charge is
  reserved first, and released if step 1 binds nothing) — and so is a mailbox's first post; closing frees it. Shared
  buffers (a message topic's ring, the blob store) are bounded where they are, not per client.
- **Stall timeout** — `OutletOptions.StallTimeout`, 30 s by default. A connected client that acknowledges nothing for
  that long while there is something for it (a data frame in flight, changes or notices waiting) is stalled — frozen,
  or its connection half-open: the router ends its subscriptions (`FaultCode.Stalled`; their providers told), freeing what they
  held and the topics' work for them, and queues one `Ended` record naming them (tap ids 1 to the last), for when it comes back
  (kept until acknowledged, like a service notice). The browser fails each of those subscriptions, then calls
  `tapline.onEnded(code, fromTapId, toTapId)`; subscriptions bound since stand. Progress is an acknowledgement, a heartbeat's
  echo or a new connection: a slow client that keeps acknowledging isn't stalled, nor an idle one. Checked at each
  heartbeat (1 s).
- **Disconnected: held, for a while** — a client whose connection is gone (noticed in ~10 s: above) isn't stalled. Its
  subscriptions are held for it to come back and resume exactly — for as long as its outlet lives; with BlazorRealtime,
  as long as its circuit is kept: Blazor's
  `DisconnectedCircuitRetentionPeriod`, 3 minutes by default, which the host sets
  (`AddInteractiveServerComponents(o => o.DisconnectedCircuitRetentionPeriod = …)`); when Blazor lets the circuit go, its
  subscriptions end and their providers are told. Held subscriptions keep their providers producing (they still count
  as listeners), so at scale a zombie circuit has a cost: shorten the retention, or release the subscriptions sooner
  than the circuit with `OutletOptions.DisconnectedHold` (they end, `Stalled`, told in one `Ended` record when it's back).
  The sample keeps circuits 30 s.

All are configuration (`BlazorRealtime__MemoryBudget`, `BlazorRealtime__StallTimeout`, `BlazorRealtime__DisconnectedHold`),
and the viewer shows each client's memory against its budget, and how long it has waited without progress.

## The router's own state — degraded, draining

Every client is told the router's own state: **serving**; **degraded**, and why (its host's health checks failing); or
**draining** — going away within a grace, its clients to move to another router before it ends (make before break). Each
change has its own correlation id, a UUIDv7: the router logs the change with it (`Router degraded: … (correlation …)`)
and hands it to every client with the state, so the router's log and each client's meet on one id. The viewer shows it
too. Ids are the router run's: never persisted, never compared across runs.

On the wire, a `RouterChange` record rides behind a heartbeat — exempt from credit, as the heartbeat is — until the
client has echoed a heartbeat it came with, and again on each new connection, saying too which control messages the
router takes (a bit each), so a client never waits on one an older router doesn't know. Otherwise a heartbeat is its 13 bytes
alone: nothing a second for a state that hasn't changed, which on a 16 kbit/s path shows in the tail of its staleness. A
change sends a heartbeat at once, as soon as the one in flight is echoed.

**Degraded, damped.** Tapline.AspNetCore reads the host's health checks — ASP.NET Core's, published periodically once the
app calls `AddHealthChecks()` (every `HealthCheckPublisherOptions.Period`, 30 s by default); none, and the router is
always serving — and `TaplineOptions.Health` damps them against flapping: degraded once they've failed, report after
report, for `DegradeAfter` (10 s); serving again once they've passed for `RecoverAfter` (30 s), doubled for each further
degradation within `FlapWindow` (10 min), up to `MaxRecoverAfter` (5 min). A router that keeps tipping over settles
degraded rather than moving its clients back and forth. `Tags` picks the checks that count. The reason told is the
failing checks', as they were when it degraded (at most 255 bytes of UTF-8 on the wire, whole in the log); other checks
failing meanwhile are logged under the same correlation id, not told again. The host can also set the state itself —
`OutletRegistry.SetHealth(state, reason, grace)` — and `HealthChanged` reports each change.

**Draining as it stops.** As the host stops, the router drains: every client told at once, with `Health.DrainGrace` to
move; the host is held until no client still connected holds a subscription here, or the grace runs out (logged either
way). Zero, the default: told, not waited for (break before make). Keep it under `HostOptions.ShutdownTimeout` (30 s by
default): the server stops serving only after. A transport whose connections close as the application stops is gone by
then — SignalR closes its connections on `ApplicationStopping`, which Ctrl+C and SIGTERM fire before the drain begins,
so Blazor Server's circuits are not drained this way.

**Draining on request.** `app.MapTaplineDrain()` maps `POST /ops/tapline/drain?grace=10s`, on the ops port alone: the
same drain (`RouterDrain`), answered once every client has moved or the grace has run out — `{ correlation, clients,
left, took }`. Call it before stopping the host (a container's pre-stop hook, say), and every transport's clients have
the grace, circuits too. One way: the router serves on, telling its clients to move, until it stops. A MagicOnion
stream refuses new sessions meanwhile ([MagicOnion](transports.md#usage-magiconion)). It maps `POST /ops/tapline/nodes/{nodeId}/drain?over=0s&grace=5s` too:
a provider's node drained on this router's side (`publisher.DrainNode`) — its clients moved to the service's other
nodes, one at a time over `over`, each with `grace` to subscribe again, as the provider's own drain would, though it
isn't asked (so what it lets go is told the provider, as for an unsubscribe). Also one way: no new subscriptions to that node from this router until another instance answers at its
endpoint (with none other serving the service, it's unavailable here till then). With the drains mapped, the router's
viewer offers both as forms ([Observability](observability.md)).

**The clients.** The browser: `tapline.routerHealth` (`{ state, correlationId, reason, drainsBy }`, `drainsBy` by the
page's clock) and `tapline.onRouterHealth(health)`, once per change. The .NET client: `Router` (a `RouterStatus`) and
`RouterStatusChanged`, and `RouterHealthCheck()` — an `IHealthCheck` for the app's own checks (`AddCheck`): healthy while
the router serves and its heartbeats come; degraded when it says so, while it drains, or when its heartbeats are late
(3 s); unhealthy when it's silent (10 s) or out of reach. Its data carries the router's state, the change's correlation
id, the reason or the drain's deadline, and how long the router has been quiet.

## Packages

Four packages, each bundling assemblies of its own, versioned together:

| Package | Assemblies | Depends on |
|---|---|---|
| `Tapline.Client` | Tapline, Tapline.Client, Tapline.MagicOnion | MagicOnion.Client, MessagePack, the health checks' abstractions |
| `Tapline` | Tapline.AspNetCore, Tapline.BlazorRealtime, Tapline.Authentication, Tapline.MagicOnion.Server, Tapline.MagicOnion.Providers; the browser's `tapline.js` and `blazor-realtime.js` (`_content/Tapline/`) | Tapline.Client, ASP.NET Core, MagicOnion.Server |
| `Tapline.Client.Opaque` | Tapline.Client.Opaque. Apache-2.0 | Tapline.Client, Wasmtime |
| `Tapline.Wasm` | Tapline.Wasm, Tapline.Authentication.Opaque; the browser's `opaque.mjs`, `opaque.wasm` and `tapline-opaque.mjs` (`_content/Tapline.Wasm/`). EPL-2.0 and Apache-2.0 | Tapline, Tapline.Client.Opaque, Wasmtime |

| Assembly | Contents | Depends on |
|---|---|---|
| `Tapline` | `Source`, `LastValueSource`/`LastValues`, `Table<TKey,TRow>` + folds and finalizers, `TableIngest`/`FinalizedIngest`/`IngestGroup`, `BatchPipe`, `SpscRing`/`SpscByteRing`, `Transaction`, `WatchMember`, `ErrorPolicy`/`IPortableDegradeFunction`, `FaultInjector`, `Delivery`/`BlobStore`, `MessageTopic`, `Outlet`/`Tap`/`Lane`, `Publisher`, wire format | BCL only |
| `Tapline.Wasm` | `WasmBatchFold`, `WasmBatchFinalizer`, `WasmDegradeFunction`, `WasmLimits` — a data provider's fold, its finish, and stale-data function as WebAssembly modules, run by the router under Wasmtime (fuel- and memory-bounded, nothing imported); the stale-data function also by the browser while the router is out of its reach | Tapline, Wasmtime |
| `Tapline.AspNetCore` | `AddTapline()` (`TaplineOptions`: the router's identity, heartbeats, its own state — degraded from the host's health checks, damped; draining as it stops — ops port, injected outages, passthrough sign-in profiles), `MapTapline()` (fetch and module endpoints), `MapTaplineViewer()`, `MapTaplineDrain()` (`RouterDrain`: drained on request), `RequireOpsPort()`, `FaultInjectionService` (test connections), `RouterNames`, `BandwidthLimits` (a client's Streaming cap, as rsync's `--bwlimit`), `SocketBacklog` (`CapSocketBacklog()`: a frame port's kernel backlog), `tapline.js` (the browser client) | Tapline, ASP.NET Core |
| `Tapline.Client` | `TaplineClient` — the .NET client, whatever carries its frames (`ITaplineConnection`): `TableReplica` (sequence and checksum verified), values, `GroupReplica` (watches), message streams, acknowledgements, resync, resume, out-of-band fetches (`BlobRef`), the router's notices and state (`RouterStatus`; `RouterHealthCheck` and `ServiceHealthCheck`: `IHealthCheck`s), rendezvous | Tapline, the health checks' abstractions |
| `Tapline.Authentication` | `AuthenticationService` — a realm's authentication service of modules (`IAuthModule`: `PasskeyModule`, `PasskeyRegistrationModule`, `PassthroughModule`, your own; `AuthModules.Sequence`), its tickets, failure limits and the backend's say (`OnAuthenticated`); passkeys (WebAuthn: ES256, RS256) against your store (`IPasskeyStore`; `InMemoryPasskeyStore`) | Tapline |
| `Tapline.Client.Opaque` | OPAQUE (RFC 9807), a .NET client's side and the crypto both sides run: `OpaqueCrypto` (opaque-ke 4 as WebAssembly, run by Wasmtime), `OpaqueMessages`, `SignInWithPasswordAsync`/`RegisterPasswordAsync`. Apache-2.0 | Tapline.Client, Wasmtime |
| `Tapline.Authentication.Opaque` | password sign-in by OPAQUE, the server's side: `OpaqueModule`, `OpaqueRegistrationModule` against your store (`IOpaqueStore`; `InMemoryOpaqueStore`), and the browser's `opaque.mjs` and `tapline-opaque.mjs` (static web assets). Apache-2.0 | Tapline.Authentication, Tapline.Client.Opaque |
| `Tapline.MagicOnion` | the stream's contract (`ITaplineStream`: `Frame`, `Control`, `TaplineStreamHeaders`) and the client's side, `TaplineStreamConnection` — Tapline.Client over a MagicOnion duplex call: control messages in order, acknowledgements coalesced, its session resumed, a silent call dropped and made again — and `TaplineRouters`: several routers, moved between make before break when one drains | Tapline.Client, MagicOnion.Client |
| `Tapline.MagicOnion.Server` | `AddTaplineMagicOnion()` (`TaplineMagicOnionOptions`), `MapTaplineMagicOnion()`: `TaplineStreamService` (each frame built just before its awaited write), `TaplineStreamSessions` (an outlet a session, held past its call; `Find` for the app's step 1) | Tapline.AspNetCore, MagicOnion.Server |
| `Tapline.MagicOnion.Providers` | a data provider in another process: `AddTaplineProviderLink()`, `MapTaplineProviderLink()` (`ProviderLinks`: your provider served to routers in other processes — its topics run by your own router and streamed to them, its step 1s, notices, mailboxes, health, heartbeats and drains on a control stream, `ITaplineProviderLink`) and `ProvideRemote()` (`RemoteProvider`: a router's provider at an address, each topic it accepts onto mirrored into a copy here) | Tapline.MagicOnion.Server |
| `Tapline.BlazorRealtime` | `AddBlazorRealtime()` (`BlazorRealtimeOptions`: the circuits' outlets, hub timeouts, sockets), `MapBlazorRealtime()` (`MapTapline()` and the circuit sockets), lifetime-manager decorator, protocol wrapper, circuit binding, `blazor-realtime.js` (`startBlazor`) | Tapline.AspNetCore |

## Caveats

- Tapline.Client shows no stale data while the router is out of its reach: the browser client applies the binding's
  stale-data policy then (a fixed state, the last message again, or the provider's WebAssembly function); the router
  applies it on its side while it's in reach, for both.
- Memory per client: an outlet's frame buffer is a heartbeat plus the frame budget or a 64 KB value, whichever is
  larger (64 KB by default: off the large object heap), taken from the registry's pool at its first frame and given back
  when it ends — to the next client; a frame whose first record needs more (an `Inline` source, out of band switched
  off) is built in one of `MaxFrame` rented from the shared pool. A buffer is counted while a frame built in it is out:
  a transport calls `Outlet.ReleaseFrame()` once it has copied the frame (BlazorRealtime does), else the next build
  releases it — and a buffer an outlet still has a frame out in when disposed goes back only once that frame is
  released (a transport that never releases leaves that one to the GC). Its
  subscriptions' history rings and mailboxes count against its `MemoryBudget` ([Client bounds](#client-bounds); the frame buffer doesn't); a
  mailbox's ring is allocated at its first post and held while its subscription lasts — size `MailboxBytes` to what
  you post (past 85 KB it is a large-object-heap array).
- Out-of-band records are copied twice on the way in: serialized into a pooled buffer, then into the store's segments
  (a source writing straight into segments would save one). Each record stored also allocates one small cache entry on
  its source ([Out-of-band delivery](#out-of-band-delivery)).
- BlazorRealtime relies on Blazor internals (the `ComponentHub` type name, the `JS.BeginInvokeJS` target, BlazorPack's
  DI registration). `AddBlazorRealtime` throws if the registration isn't found; the tests detect the rest.
- Not compatible with SignalR stateful reconnect or Azure SignalR Service (both serialize away from the connection's
  head, so a frame can't be bound late).
- A table shares one lock between its writer and tap fills; a snapshot copy holds it briefly. A degrade function also
  runs under it, once per faulted tap over the whole table — keep it O(rows) (a WebAssembly one is fuel-bounded). So
  does a fold inside `BeginUpdate`: a WebAssembly one holds it at most as long as its fuel lasts (50 M by default:
  tens of milliseconds). Through an ingest, the fold runs outside it. The router's threads never wait on it: a held
  lock costs its subscribers a 1 ms retry, nobody else anything.
- A transaction merges its sources' lock domains for good (it can't know they won't be written together again); watches
  over them then read them together. Use a group where membership should come and go.
- A browser has no fuel: a stale-data module that loops forever on the page's state would hang the page (the router's
  own application of it is bounded). The module is the provider's, and the router's checks are of its shape, not its
  termination.
- A rate-limited outlet wakes up to one wheel tick (default 1 ms) after its budget recovers; with nothing else
  pending that is the only added latency.
- Flow control sees only the outlet's own frames: Blazor's render batches on the same circuit are cross traffic to it
  (a burst of them ahead of a frame shows as queue, and a held frame behind one measures less than the path's rate —
  probing recovers it within a few round trips). A sudden large drop in capacity costs one transient of what was in
  flight at the time ([flow control](#flow-control--after-bbr-steered-to-the-shortest-queue)).
- Round trips that jitter with no queue — links that deliver in bursts, as cellular and Wi-Fi aggregation do (and the
  throttling proxy's 50 ms ticks) — read partly as queue: the controller backs off more than it needs to (79% of the
  link in the simulation, ~70% through the proxy). A jitter-aware estimator is future work.
- A degrade function runs once per faulted tap: if even the undegraded state might not fit behind other records, it
  waits for a frame of its own before running.
- `Updater` is a `ref struct` holding the table's lock: complete or dispose it on the same thread, without awaiting.
- Records larger than a frame go out of band by default; with `Delivery.Inline` a record that can't fit even an empty
  frame parks its tap until the next resync or reconnect.
- Inside a single-table update, don't take other sources' locks (publish a value, read another table): use a
  `Transaction`. Watches tolerate it (they back off), but two writers nesting in opposite orders can still deadlock.
- A last-value member of a watch is at most 64 KB (the watch faults otherwise). A key-range member walks the table's
  change log (bounded, then a snapshot), so prefer a separate table for a small hot range of a very busy table.
- Out-of-band fetches go to the node that holds the outlet: Blazor's sticky sessions already route there; other
  clients use `TapBinding.Endpoint`. Size `BlobStore.MaxBytes` for the largest snapshots × the versions in flight.
- Keep the pipe and kernel buffers small so "head of the FIFO" ≈ "head of the wire": `CircuitSendBufferBytes` for the
  kernel, Kestrel's socket transport output pipe (`SocketTransportOptions.MaxWriteBufferSize`, app-wide), and SignalR's
  `TransportMaxBufferSize` (64 KB), which Blazor exposes from .NET 11 (`ServerComponentsEndpointOptions.ConfigureConnection`).
  On Linux, `CircuitNotSentLowWatermarkBytes` (`TCP_NOTSENT_LOWAT`) limits unsent bytes without limiting the window.
