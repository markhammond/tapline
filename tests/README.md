# Tapline Tests

## Verification

`dotnet run --project tests/Tapline.Tests` (157 tests; `TAPLINE_TEST=<part of a name>` runs only those,
`TAPLINE_TEST_REPEAT=n` each n times), then `node tests/js/test.mjs` (38 tests, using the fixtures the C# run writes).
Nothing to install for either: the JS tests use Node's own modules, and the SignalR JS client's side of the wire —
how it encodes every control invocation tapline.js sends over a circuit, at each integer width, and how it reads a frame
envelope — is captured in `tests/js/fixtures/signalr.json` (client 10.0.11), which the C# tests hold the library to.
`tests/js/capture-signalr.mjs` says how to capture it again, outside the repository. (`Tap.Auth`'s bytes argument is
not in that capture yet: the tests write it as MessagePack's bin8/bin16/bin32, which is how the client encodes a
`Uint8Array`; nor is `Tap.Sync`, four integers, written as the captured calls' are.) Highlights:

- Transactions across sources: a writer moving value between positions and cash in 40,000 transactions, watched
  concurrently by an account watch — 0 torn deliveries (two independent taps on the same tables were torn in some
  frames). Rollback of all members, fault-all on a member's fold fault, precondition or postcondition, recovery, value
  staging, refusal of misuse.
- Watch key ranges: subset checksum verified across 20,000 random updates with snapshot fallback and slot recycling;
  changes outside the range send nothing; member faults → degraded members + error in one frame; a 54 KB group goes
  out of band.
- Blob store (the suite runs in Debug: its assertions are live): any size in segments no larger than 16 KB; unique ids
  by default — UUIDv7s, strictly increasing through 5,000 in a burst, each store (or id, when asked) its own entropy —
  content-addressed puts stored once and found before copying; a lease outlives eviction — its segments
  untouched while 20 blobs cycle through the pool, then poisoned once released, a stale sequence refused; 8 threads
  putting, leasing, pinning and sweeping against a full store: 23,000 leased reads, every one its own blob's bytes, all
  memory accounted back; steady state 0 bytes (a lease: 48). Without hashing, a snapshot and a 36 KB delta to 20 taps
  at the same cursor, and a value to taps behind at two points, are each stored once.
- Frame buffer (through `TryBuildFrame`, the transport's path): the outlet's own is 64 KB; a 150 KB `Inline` snapshot
  goes whole in the next frame, built in a rented full-size buffer that's returned at the build after, the tap never
  parked; over `MaxFrame`, oversize as before; 2,000 steps with a full-size frame every tenth: 0 bytes. Buffers are
  counted: an outlet disposed while its frame is still being copied leaves the buffer (its own or a rented one) to the
  copy — intact — until the transport releases it, then pools it (DEBUG: poisoned); 100 clients in turn: one buffer.
- Out of band: one serialization for 20 taps' snapshots; the tap waits for its fetch while changes coalesce; per-outlet
  tags; larger than `MaxFrame` → out of band instead of parked; budget charged; fetch timeout; a large message stored
  once for every subscriber; the real endpoint under Kestrel (bytes, 404 for a wrong tag or outlet, `immutable`,
  Range → 206); stale tickets ignored.
- JS: out-of-band records fetched, verified, applied in order and confirmed with their ticket; references; watch
  notifications coherent on every delivery; a blob failing verification is not applied (the tap resyncs); a fetch
  overtaken by a reconnect is dropped; a rebind says the last frame received (`Tap.Resume`), confirmations of the
  connection before first, then bare activations; a message topic's 500 messages in order, numbered, and a mailbox's, flagged; a
  large message after its fetch, the one behind it held by the server till then; a stalled client's `Ended` record
  fails the subscriptions in its range (Stalled, from the server), not one bound since, then `onEnded`; one naming a
  single subscription fails just that one.
- Edge cases: a writer nesting locks out of order against a watch doesn't deadlock; a stale fetch confirmation doesn't
  release a newer handle; a full blob store or an oversized watch value never stalls a tap silently (it retries, or
  faults); a mailbox post after its outlet is disposed is refused; `LastValueSource.Fail` aborts its transaction.

- Ready queue: first-dirty order with coalescing (a re-dirtied tap keeps its place); a blocked record skipped then
  first in the next frame; 8 publisher threads × 40,000 publishes + table updates + resyncs against a parking consumer
  converge with no `Interrupt` (no lost change or wake-up); a ping-pong stress run on one tap, 29M publishes: no
  lost wake-up (the inbox push is fenced against store→load reordering).
- Rate limit (manual clock): 64 KB/s cap with 64 MB/s offered → 65.7 KB/s incl. the 16 KB burst over 10 s; a High
  mailbox unaffected; a tier change applies immediately even mid-debt; uncapping leaves the wheel; every tap still converges
  to its latest value. The wheel wakes a parked outlet within one tick of its budget recovering.
- Timing wheel: 2,000 entries up to ~9 revolutions out, rescheduled and cancelled, advanced irregularly — each fires
  once, never early, within two ticks; catch-up after a stall of many revolutions. Park-time registration: a frame sent
  and a park with nothing waiting — not on the wheel; data arrives before the next frame is due (a 50 Hz cadence) — on
  it once, woken 20 ms after the last frame, off it; a retry (a value larger than the blob store) — woken when due, its
  tap released by the outlet's own thread. Under contention — the worst case, twice as many threads as cores (36)
  re-arming 9,216 wake-ups the moment they fire, ~1.4 million schedules a second, on a machine also running two
  higher-priority compute-bound processes: with the wheel locked, one wheel, a schedule took p99.9 11.6 µs and at worst
  45 ms (a thread preempted holding the lock); striped, 3.1 µs and 10 ms; lock-free and striped, as built, 0.7–1.0 µs
  and ~1–2 ms at worst — the measuring thread itself preempted, not the wheel. Wake-ups fired a tick or so late at the
  median (1.4–1.6 ms), 3–9 ms at worst — the driver preempted by that load. None early.
- Transactions: 20,000 random updates, a third abandoned, match a reference model; the version advances exactly when
  rows changed (net no-ops unpublished); taps reconstruct bit-for-bit throughout.
- Faults: a version gap, a checksum mismatch, a throwing static fold, a throwing batch fold (with new keys) and
  `Fail()` inside an update each roll back to the last good state and fault the table; a faulted table refuses
  updates; a failed recovery changes nothing; recovery may restart the upstream numbering.
- Error policies: one frame carries the degraded snapshot (best bid/ask, size 0, checksum-verified) immediately
  followed by its Error; taps without a degrade get the error only; `Completion` yields the fault; activation on a
  faulted source errors at once; resubscribing after recovery yields a normal snapshot. Fallbacks: throwing degrade →
  empty degraded snapshot; larger than `MaxFrame` → error only; not first in the frame → whole in the next frame.
  Races: a recovery completing while the frame builder waits on the table yields normal data (no degraded snapshot of
  the recovered state, no error); closing a faulted tap mid-rotation keeps the round-robin order.
- Test connections: while their shared injector is failed, tables, values and watches all look faulted to them (degraded
  + error, the tap ends, activation fails at once, `FaultOf` reports it) while the sources and a production connection
  on the same book carry on; after `Recover` new taps stream normally; real faults still reach everyone. Only connections
  opened under the test path join; the root is refused as one. Live, in BlazingOrderBooks: `/` streamed unbroken through two outages on `/test`.
- JS: degraded replica + `onError` (payload, last good version) and the tap is forgotten (not re-activated on
  reconnect); a corrupted *snapshot* sends one `Tap.Close(ChecksumMismatch)` and no resync; `Tap.Close` encoded by the
  real SignalR client is consumed in place.

- Data providers: step 1 through the service's provider — an unknown service, a refusal, a route bound and counted
  before step 2; a stream topic starts at the next change after step 1 (and after a reconnect, on from what the client
  received), a table at its snapshot; the
  mailbox's snapshot first, the topic's older state never after it; 201/202 with the epoch; unsubscribed → a notice
  after the route is gone (an orphan expiring and a connection ending likewise), and 202 → the provider stops;
  resubscribed → a new epoch, the stale 202 and notice ignored. Epochs, raced (above): never stranded by the rule. Live:
  the station's topics line (`i`) — routes per topic, a tier stopped once its last subscriber unsubscribed (pause) and
  live again on play; the music's likewise.
- Provider endpoints: an endpoint is one provider's; a service's several endpoints each get step 1 in turn; each route
  records the instance that accepted it (or the one it named; one the router can't reach is refused), and its step 2
  and end go there alone, once, in that order — an orphan's end only; the client's binding carries the router's tap id,
  topic, node and a node id per instance, nothing of the instance; a withdrawn endpoint gets no step 1s, and the
  routes it held are closed (service down) with nothing relayed.
- Provider instances: heartbeats only while an instance holds routes, to the instance expected, one in flight at a time;
  answered as itself it lives (past the timeout many times over); a heartbeat answered with a new id, an answer to step
  1 with a new id at the same name and endpoint, or silence past the timeout (the heartbeat in flight then abandoned)
  each deem it dead: its routes closed with `ServiceDown`, the client told once per instance (both routes, one
  `Service` record), the notice sent again after a reconnect before its frame was acked, nothing relayed for those
  routes, the dead id's answers refused, the new instance's routes another node id. JS: the subscription through
  the dead instance fails with `ServiceDown`, `onServiceDown` runs once (the wire's 16 bytes read as the binding's
  string), a subscription on the same topic not through the provider streams on, a later subscribe with the dead id
  fails at once without activating, and the up notice reaches `onServiceUp` by the service's name.
- Service up: told only to the waiting client (not a bystander), by name, after its down; one instance of two up with
  a quorum of two — held for the grace period (400 ms: told after ~415); both up — told at once; a subscribe with every
  endpoint withdrawn — unavailable, no up while there's no instance, up once one is provided, and no further notice once
  subscribed.
- Provider drain: one way — no step 1s to the draining instance, even once it reports serving again; six clients let go
  evenly over 600 ms (at 104, 203, 303, 403, 502, 603 ms), each with all its routes there together, `ServiceDrained`,
  its routes on the other instance untouched, nothing relayed for them; each client told drained — not down, no up to
  wait for — and subscribing again lands on the other instance; a new instance at the endpoint takes step 1s again.
- Blue/green, make before break: green started alongside, blue drained over 500 ms with a 300 ms grace — new
  subscriptions all to green; blue's health live but not ready; its 6 viewers told to move one at a time (at ~90, 175,
  255, 340, 420, 510 ms), each with blue's id and the grace; 5 subscribed again onto green and held both at once — each
  under its own tap id, both streaming — before dropping blue's (their own unsubscribe: never drained, never down);
  the 6th, not moving, closed when its grace ran out (drained); blue never deemed dead; withdrawn once drained, nobody
  told. JS: told draining, nothing fails and the subscription streams on; `replace` activates the new subscription,
  keeps the old streaming until the new one's first state, then drops it (`Tap.Close`) — the old one's later records
  ignored; a drained subscription fails with `ServiceDrained`, `onServiceDrained` runs (not `onServiceDown`), and a
  later subscribe with the drained id fails at once.
- Router identity: UUIDv7 ids time-ordered per name; the router introduced to the provider, named in its messages and
  health checks; the provider's heartbeats answered, past its timeout many times over; a newer router of the name — the
  older's routes handed to `Lost`, the older refused: its step 1 NACKed (unavailable), what it held closed
  (`ServiceDown`), `RefusedBy` raised, its client told down, the newer served; a router behind a partition (heartbeats
  unanswered) presumed dead past the timeout, and told so at its next health check; a router that deemed the instance
  dead — the provider's heartbeat answered "unknown instance", those routes forgotten, the router still served.
- Service up, jittered: 16 waiting clients told over ~410 ms of a 500 ms spread, not at once.
- Message history: a subscription's last sixteen records, newest first — kind, size, version, first bytes, received ≤
  sent ≤ acked (lag, round trip), each in its own frame; the client's frames sent and acked; `MessageHistory` 10 rounded
  up to 16, 0 keeping none. Concurrent: three readers took ~3 million entries while one builder wrote, none torn. Cost:
  ~6–13 ns a record (publish + frame + ack, Release).
- Message topics: every message in order, none conflated, from the next after step 1 (no snapshot, whatever is asked);
  nobody subscribed → 202 and nothing kept, the buffer released with the last subscriber and numbering going on; 201/202;
  overflow — `Fault` ends the subscription, `Skip` flags the gap, `Refuse` answers 507 (also when one large message
  would evict several, the oldest sent, the newest not; but not for a subscriber whose client is away — it overflows
  when it's back); mailboxes — the subscriber's alone, flagged, numbered apart,
  refused when full of unsent, FIFO across 20,000 posts with the ring wrapping (wasted tails), none lost.
- Resume after a reconnect: a frame lost in flight — its messages and the mailbox's sent again, none twice; the table
  on with its delta (no second snapshot), the value that changed sent and the one that didn't not; a frame received
  whose ack was lost — nothing sent again; an out-of-band message whose fetch was abandoned — a new handle, fetched
  once; evicted while away — an overflow; no word from the client — on from what it acknowledged after `BindTimeout`.
- Provider's initial state, sent on step 2: a message topic — a live message published before it held back, the
  provider's history (from its store, through the topic's first message) then the topic from after that cut: 1, 2, 3
  (mailbox), 4 (topic), nothing twice; a last value — no value before the snapshot, the snapshot as of version 2, then
  version 3; never sent — ended after the bind timeout from step 2 (`NoInitialState`), the client told.
- Provider degraded: a book station late to answer (past 100 ms) — the client told first, then the top of the book,
  sizes zero, the subscription open and held over an update; answering again — told, then the book from a snapshot.
  A price station reporting itself not serving — degraded at once, the fixed stale value shown; serving again — its
  current value. A trades station late — the last trade replayed (its own number, flagged stale), the new one held;
  answering again — the held trade, in order; late again past its timeout — dead while degraded: the error and the
  service down, the stale trade not again. JS: the provider's state on every
  state and message, a stale message flagged, reachability on the connection closing and binding again.
- WebAssembly stale data (Release): the top of the book as a module (WAT) gives the C# function's rows, checksum
  verified, on 80 rows and on 4,000 (84 KB of entries, its memory grown to fit); the binding names its module, served
  as `application/wasm`, immutable, while a subscription bound with it is (one entry however many hold it), 404 after;
  out of fuel (5 M: 2–3 ms), a trap, or out of memory (64 KB) → an empty stale table, each counted, the instance
  discarded; refused: not wasm, not WAT, importing, no `degrade`, `degrade` of another type, no memory, over 1 MB;
  8 threads × 200 concurrent applications all exact. JS: the module fetched at subscribe and checked (one failing its
  check refused); the connection closed — the book through the module (best bid and ask, sizes zero), a fixed value,
  the last message again, each flagged `stale`, `provider: 'unreachable'`, the replica untouched; nothing for a
  subscription that received nothing or whose function traps; bound again, the real state. Live (headless Chrome, the
  sample): the 418-byte module fetched at subscribe; the circuit's socket dropped — within 2 ms the book down to its
  best bid and ask, sizes zero, dimmed, "server out of reach"; Blazor back 181 ms later, the full book, checksum
  verified. 0 bytes allocated over 2,000 applications.
- WebAssembly folds (Release): `Add<long>` as a module (WAT) gives the compile-time fold's rows and the C# batch fold's,
  checksum verified, over 2,000 updates of up to 64 deltas (keys repeated in a batch, rows reaching zero); out of fuel,
  a trap, or out of memory (64 KB, 4,000 rows) → `FoldFailed`, the trap its cause, the whole update rolled back (an
  earlier `Set` in it too), counted, the instance discarded, recovered by a fold that works — the out-of-memory one
  itself, on a batch that fits; refused: not wasm, not WAT, importing, no `fold` (a stale-data module), `fold` of
  another type, no memory; 8 tables on 8 threads × 500 updates sharing one, all exact; 0 bytes allocated over 100k
  updates.
- Staged posts: with the router's fold held at a gate, 200 posts return at once and a reader and a tap's frame aren't
  kept waiting. Released, the 200 are folded in one call and applied as one update (the same rows as the same updates
  made directly), and the client's mirror is exact. Two snapshots after three posts (one with a broken link, never
  checked), one post already taken by the router: the last snapshot and the post after it applied, the rest superseded.
  In 256 bytes four posts are staged and the fifth refused (507); a post over the capacity is refused outright; a
  snapshot is staged over them. With `maxDeltas` 16, forty deltas in posts of four are applied as 16 + 16 + 8; a post of
  20 alone; a 10-row snapshot with the posts after it up to 16 entries, then a snapshot supersedes the rest of the
  router's buffer mid-way. A gap in the chain: the two posts before it applied as one update, then
  `VersionMismatch`, deltas refused, a snapshot recovers. Of two conflated posts, only the last one's checksum is
  checked; a wrong one rolls back. A throwing fold after a snapshot: the snapshot applied, then `FoldFailed`. Direct
  updates, transactions and a second ingest are refused. 0 bytes over 100k drains of four posts (with a snapshot every
  50th).
- Finalized ingest: a fold keeping a state per key at the router (sums of size and of price × size) and a finalizer
  making each state's row (size, volume-weighted average price), the sums never in the table: 10 at 100 and 30 at 104
  average 103; a sum moved while the row rounds the same publishes nothing; a state at zero deletes its row; a snapshot
  of states replaces the table and later deltas fold onto them; made over a table that has rows, deltas are refused
  until a snapshot of states, then fold onto it; the same in WebAssembly (one module exporting `fold`
  and `finalize`), byte for byte; a failing finalizer faults the table (`FoldFailed`), a snapshot recovers it, and one
  failing on the snapshot itself faults it with nothing applied. 0 bytes over 2,000 posts once warm, the row kept;
  likewise deleting it and making it again each post, once the deletes fall off the change log (64Ki entries: till
  then the table's queue of deleted rows grows, about 30 bytes a delete).
- Metered slices: a costly WebAssembly fold (about 1,230 fuel a delta) measured on its first slice, then given only the
  posts that fit 100,000 fuel; the rows the same posts make directly. With 30,000 fuel a call, 40 posts (96,000 fuel)
  are fitted by halving twice and applied without a fault, each halving a call out of fuel; the measure learned. A
  post needing 48,000 fuel alone gets a slice of its own from the measure, faults the table (`FoldFailed`, out of
  fuel) after the three posts before it, and the one after it is dropped. A poisoned post through an unmetered C#
  fold is isolated by halving likewise: the six before it applied. 0 bytes over 100k metered drains. Concurrent: 4
  threads posting 20,000 pairs that each sum to zero through a WebAssembly fold, while a reader copied the table
  throughout (40,000 to 1.3 million copies a run): every state read summed to zero, the totals exact.
- Lock domains: two sources a transaction writes share a domain from then on, merged with whatever shares one with
  them (a new domain each time, never edited); a transaction of one makes none. A group's members are its domain: a
  member joining leaves the domain it was in, whose rest stay together; leaving, it is alone again, and a group of one
  is no domain. A source never written with another never has a domain or a writer.
- Router threads never wait: a table held mid-update and a value held in a transaction, as long as their writers like
  — 20 frame builds take tens of milliseconds, retrying each held lock 1 ms later; an unrelated topic is delivered; new
  subscriptions to the held ones wait; a watch over a held table and a free one delivers the free one's part now and
  the held one's after the update.
- Group units: three units across two tables and a value staged meanwhile become one transaction — each member one
  version, the group's chain at 3. A broken link: the unit before it applied, then all three members faulted (the value
  too); units refused; a snapshot (`Rows`, `Publish`) recovers all three, and a unit staged after it applies. A snapshot
  supersedes two units across members but keeps a join between them (applied). A poisoned unit in one member's part is
  isolated by halving across the group: the five before it applied to both tables, then both fault. A member's
  checksum is its last part's: a wrong one rolls the transaction back and faults both. 0 bytes over 100k units of
  three parts.
- Group membership: a table's own ingest closed and its table joined to a group at once — the group's unit waits (no
  thread held) until the ingest's five posts and leave are applied, then goes. Joining a source another group writes,
  joining twice, and a direct update of a member are refused. A table joined later (written directly before) goes in
  one transaction with the group's other member; left, it can't be named, the unit staged before its leave is applied,
  and it is written directly again. Three groups' claims on one table queued before any is applied: each in its turn
  (1, then 10, then 100), a fourth refused while the last isn't leaving. A value member: published in units only,
  directly again once it left.
- Group watched under churn: a provider trading through a group on the thread pool (20,000 units), a reserve table
  joining and leaving it 40 times with value moving between an account's cash and the reserve only while it is a
  member. An account watch and a watch over all three tables: every delivery balances (hundreds to a thousand each),
  with a handful of resyncs a run after a domain changed mid-read; 30 runs in a row, none torn.
- Rendezvous: a topic's message then its rendezvous, in one frame (a mailbox goes before its topic's records, but not
  ahead of its own state); sent already, at once; a table conflated past the version, after the state superseding it; a
  value; out of band, after the fetch (a mailbox message held behind the fetch too); its provider degraded, after the
  recovery's snapshot; another subscription degraded, or a version not yet published, held with the outlet idle, then
  released by the event; missed — published before it subscribed, skipped — flagged; lost with the connection, both
  again in order; through a provider's mailbox; a full mailbox refuses. Each ordering fix checked by undoing it: the
  test fails. JS: resolved after the frame's earlier records are handed over; rejected when its subscription ends, or
  aborted; unawaited to `onRendezvous`.
- Sync (`Tap.Sync`): a quiet subscription answered at once, the client up to date; one its interval holds back not
  answered till what's held is delivered, then up to date as of the ask; each ask its own id; asked unbound, nothing
  sent. Over the circuit (consumed in place, its rendezvous in the tap's mailbox) and MagicOnion (its control round-trips).
  JS: the page's own ids, each ask resolved by its own rendezvous, rejected when its subscription ends. A mailbox entry
  ordered after a state its interval holds waits with it, not letting it out (`MinInterval`; checked by undoing the
  fix: the test fails).
- Disconnected: four times the stall timeout away with a change waiting — held, not stalled; back, resumed, nothing
  ended. With a 150 ms `DisconnectedHold`: released after it, `Stalled`, one `Ended` record when it's back.
- Stale data on instance death: a station's top-of-book function, handed over with its route; the station withdrawn —
  one frame: the degraded book (best bid and ask, sizes zero), the error with the station's payload, the service-down
  notice; that frame lost, all of it again after the reconnect. The circuit hub's SignalR timeouts (10 s, pings at 3 s)
  unless the app set its own; the browser's server timeout — SignalR's until 5 s are seen, then the floor (10 s) at a
  heartbeat a second, four times a 7 s gap (28 s), back to the floor as it eases.
- Reconciled at a rebind: an overflow's error written into a frame lost with the connection, and a subscription the app
  closed server-side meanwhile — the client's re-sent activations answered with an `Ended` record for each (Overflowed;
  None), nothing for the one still held; once forgotten (70 more ended since), `Gone`; an id never issued, ignored.
- Ordering: a fill on High after its position in Streaming — the position pulled into the same frame, ahead of it;
  satisfied → nothing extra; the position out of band → the fill held until its fetch, again until its tap closed, then
  sent; another client with no tap on that source not held; a mailbox reply after one of the client's taps; a
  rate-limited client in debt — pulled forward anyway; a topic's message after a tap, or a mailbox's after another
  client's tap, refused.
- Client bounds: 32 subscriptions of 1,984 bytes fill a 64 KB budget; the next refused (`OutletFullException`, saying
  what it holds); a mailbox's first post refused without room, made with it; a provider's snapshot counted while it
  waits; through a provider, refused before the provider is asked, a step 1 binding nothing holds nothing, a route
  takes its reservation. Stalled: acknowledging every frame for 700 ms (a 300 ms timeout) — not; idle for 700 ms — not;
  a frame unacknowledged with changes waiting — its subscriptions end ~335 ms on, `Stalled`, the provider told, nothing
  held; back on a new connection, one `Ended` record, and a subscription bound since streams.
- Router viewer: the router described without its lock; served — JSON and HTML; from a client's page, links to its
  subscription, topic, service and instance, on to the instance's endpoint (checked now on request: one health check, no
  more) and the routes it holds, back to the subscription; a record's page — an out-of-band record's blob leased, its
  first bytes shown, all of it raw; one no longer in the history said so; 404 for an unknown client; 503 when busy.
  Out of band, in the library: the record's blob id kept; leased while kept; swept after its grace — gone, said so.
- Subscribe lifecycle: a provider that never answers (nor heeds the token) — unavailable after the timeout, its late
  accept refused, no route; accepted then stuck, cancelled or threw — no route left, an unsubscribe relayed; accepted
  after its client went — a closed tap, no route, an unsubscribe relayed; binds racing a dispose, 300 rounds — no
  route left on a disposed outlet. Live: `POST /waveform/station/restart` (on the loopback) — the
  station takes a new id, the router finds it at its next heartbeat, viewers are told and subscribe again.
- Two-phase: a bound tap streams nothing; foreign/unknown/duplicate activations are ignored; activation starts with
  exactly one snapshot; unactivated taps expire. End-to-end, the client activates over the wire after the bind and no
  table or value record arrives before it; the JS client skips records for taps it has no handler for and re-activates
  on rebind.

- A slow client 4,000 changes behind on a ~450-row book churning at its hot levels gets 50 deltas of 18 rows rather
  than 51 snapshots, reconstructing exactly through deletes and recycled slots; a whole-table watch
  agrees; once every row changed, a snapshot.
- Random table updates with fast/slow/very slow taps reconstruct exactly (sequence + checksum on every record),
  including snapshot fallback, slot recycling and client resync.
- `Fold<long, Add<long>>` matches a reference model; a no-op fold on an absent key leaves nothing behind. The batch ABI
  matches the static fold with repeated keys in a batch (one call per batch), and a throwing fold leaves the table
  bit-for-bit unchanged.
- Saturated shares 64.0 / 16.0 / 16.0 / 4.0 %; a 54 KB snapshot gets through in 2 frames under saturated High.
- Scheduler quanta follow the frame budget in use: at 16 kbit/s, SID rows on High behind tiny ridge frames arrive within
  1.1 s at p99.5 (with quanta sized for a fixed 16 KB budget, Streaming's turn would last four or five frames: 3.7 s);
  at 256 kbit/s 90% of 40 ms chunks within 150 ms (65% with fixed quanta). Weighted shares unchanged.
- Frame cadence changed at runtime: uncapped → 60 → 30 → 120 → uncapped holds 60.0 / 30.0 / 120.0 frames/s, and the
  first frame after raising 30 → 120 goes 8.4 ms after the last, not the old period's 33; the same with 3 KB values over
  a 1 KB frame budget, each record filling its frame alone (a full frame doesn't hold the tick open).
- Making way for High (media simulation of the demo: 300 B ridge frames, SID rows on High, 16 kbit/s): rows within
  284 ms at p99.5 (907 without making way), the ridges at 2.0 frames/s; 160 kbit/s lite with 12 KiB/s of audio on High,
  99.7% of chunks within 150 ms (84% without); 2 Mbit/s and faster unchanged. A trade-off: at 256 kbit/s with that heavy audio,
  lite's chunks p99.5 132 → 195 ms (96% within 150).
- Lane taps: a High tap gets every update in the next frame past a rate-limited 6 KB-a-millisecond bulk stream (75/75;
  as a Streaming tap 1/75), and the bulk keeps its cap; a High tap's record larger than the frame budget goes first in a
  High-only frame rather than parking the tap as oversize.
- Media simulation (a heavier `/waveform` workload: 5.8 KB values at 120 Hz in Streaming, a 16-row audio window on
  High): colo
  and broadband deliver every frame and every chunk on time; 2 Mbit/s fills the link with 99.8% of chunks within 150 ms;
  on 256 and 160 kbit/s large frames keep the link 90%+ busy (no stalls) and lite frames get every chunk through within
  the browser's budget; on a path delivering in 50 ms ticks, 100% delivered, p99.5 500 ms, 79% of the link.
- Flow control, simulated over seven paths ([the table](../docs/architecture.md#flow-control--after-bbr-steered-to-the-shortest-queue)): uncapped, colo and broadband run at the data rate with one
  way's delay as the floor and no worse than the fixed window anywhere fast; 60 Hz holds 60.0 frames/s, interval p99
  ≤ 17.2 ms, wherever the path carries it; 16 kbit/s fresher at the median, ≤ 1.2× at p99.5; after a drop to 16 kbit/s
  both settle fresher than the fixed window. `TAPLINE_TRACE_PATH=bytes/s,one-way ms[,frame rate[,at s,to bytes/s]]`
  traces the controller over a path.
- Slow link (one frame per two heartbeats, as under Chrome's 16 kbit/s throttle where every WebSocket message waits
  for a 750 ms packet tick): the backlog stays ≤ 2 frames and a book + value arrive conflated (404 records for 8,000
  updates). Before, heartbeats were sent every interval and held the window: the backlog grew without bound and no
  data was ever sent. JS: frames received before `taplineBind` are acked by it, so their heartbeat echo isn't lost.
- 0 bytes allocated over 100k steady-state steps of set/delete/fold/batch-fold (with upstream preconditions)/
  rollback/publish/transaction/post/build, including a watch over two key ranges and a value, and a rate-limited
  outlet driving its bucket and the timing wheel.
- Wire: the envelope parses with the real BlazorPack protocol and the SignalR JS msgpack client; acks encoded by the
  real JS client are consumed in place.
- Transport: the real `RemoteJSRuntime` bind call carries the token; `Clients.Caller` sends route through the decorated
  lifetime manager under a real `HubConnectionHandler`; end-to-end through `HubConnectionContext` + BlazorPack with
  2,000 interleaved Blazor render batches kept in order.
- Tapline.Client, through an outlet (its control messages looped straight back): a table under churn (one snapshot, 991
  deltas, 8,004 entries in 992 frames, every record verified), a value, a message topic and a mailbox, every frame
  acknowledged; a tampered delta resynced once, healed by a snapshot; a reconnect resumed from the last frame (no
  snapshot, nothing lost); a rendezvous after the state it named; a watch's every cut coherent; snapshots, deltas and a
  large value out of band — fetched, checked, confirmed — and by reference; failing fetches resynced, then failed; a
  fault, a stall behind a half-open connection, and a provider's instance withdrawn, each told.

**Live:** circuits in Chromium against the samples — the book and mark on BlazingOrderBooks' `/`, test connections on its `/test`, `bwlimit`,
16 kbit/s and 256 kbit/s throttling proxies, and `/waveform` (above).

**GC, live (the sample):** the whole server — Kestrel, SignalR, Blazor, the order-book feed and the `/waveform` station
— measured with its circuits as headless Chrome pages on `/waveform` (each a circuit with its own outlet, the ridges at
a full 60 frames a second, every frame delivered), on .NET 10's server GC (DATAS), an 18-core Mac, Release build. Bytes
allocated per second, steady state, over 10–20 s windows for a minute:

| Circuits | Frames sent per second | Allocated | Collections |
|---|---|---|---|
| 0 | — | ≈ 4 KiB/s (1.3–5.9) | none |
| 1 | 60 | ≈ 2.3 KiB/s (1.6–3.4) | none |
| 10 | 600 | ≈ 13 KiB/s (3–29) | none, connecting included |

The frame path allocates nothing; what is left grows with circuits, not frames — Blazor's own work per JS-interop call
(a hub instance and a delegate per call, about one a second per circuit here) — about 1.2 KiB/s a circuit (25 circuits
that could only paint ~9 frames a second allocated more than 10 at 60). At that rate a server GC's gen-0 budget lasts
hours. Connecting is cheap too: a circuit's outlet builds frames in a 64 KB buffer of its own (off the large object
heap), renting a full-size one only for a frame that needs it — 9 circuits connecting at once, then 15 more (25 in
all), brought no collection at all in 4 minutes (heap 44 MiB); with a 1 MB buffer per circuit the first 9 had brought
a gen-2 collection (64 ms). Before this round the same 10 circuits allocated ~500 KiB/s: the circuit's frame pump resumed on Blazor's renderer (an allocation
per wake-up), the compression advice's queue allocated a segment per frame, and the sample's exchange simulator walked
a `SortedDictionary` (its enumerator allocates) several times a step. The `i` overlay on `/waveform` shows the same
figures live (`gc` line: allocation rate, collections, time paused, heap).
