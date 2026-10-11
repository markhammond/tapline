# Roadmap

Designs accepted but not built yet. The [README](../README.md) and the docs it links describe Tapline as it is; each
design moves there once built.

## Router load: accounting, sharing, and moving clients

Account for the router's work by subscription, share it on the heartbeats already exchanged, and move subscriptions off
a loaded router. Describe it in the [field manual](provider.md) once built.

### Summary

- **Measure** the router's work per topic and per subscription, in CPU time and exact counts, and show it in the ops
  viewer.
- **Reduce it to one number:** the router's load factor.
- **Share both with providers** on the health checks and heartbeats they already exchange, with no broadcasts, so a
  good-citizen provider can shed load without the providers stampeding together.
- **Act first on what the router controls** (frame rates, fold slices, fuel budgets), then ask the largest contributors
  to shed, one at a time.
- **Rebalance subscriptions** across a service's instances, one at a time, each with a grace to subscribe again: asked
  for by a provider or the router, decided by work units (each subscription's cost against each instance's headroom).
  Later, move them between routers too.

```text
                [ Router pressure (load factor > 0.85) ]
                                 │
                ┌────────────────┴────────────────┐
                ▼                                 ▼
    1. Within the router                2. Ask a provider
        ├── lower frame rates               └── the heaviest sheds a fraction
        ├── larger fold slices
        └── tighter fuel budgets
                                 │
                                 ▼
                     3. Rebalance subscriptions
                          ├── one at a time, to other instances
                          └── placed by load (two random choices)
```

### Measure

#### Two ledgers

- **Charge topic work to the topic,** and so to its provider: folding and applying posts, done once per topic however
  many read it.
- **Charge route work to the subscription,** and so to its client: choosing a subscriber's changed rows, encoding them,
  sending them, and serving their out-of-band fetches.
- **Count a provider's share** as its topics' work plus the route work on those topics.
- **Let the provider name the account** a topic's work is charged to (a tenant, a cost centre), at `Accept`, optionally
  per post, which covers fan-out. Intern the names as small ids. Measure the amount in the router, and never send
  accounts to clients. Don't cap accounts for now; if ever, cap them by a memory budget, not a count.

#### CPU time

- **Read thread CPU time** around units the router already has (a frame build for an outlet, a fold slice, a snapshot's
  encoding, a blob fetch), never per row:
  - `clock_gettime(CLOCK_THREAD_CPUTIME_ID)` on Linux and macOS;
  - `QueryThreadCycleTime` on Windows, in cycles (`GetThreadTimes` updates only at clock ticks).

  Not through `Process.Threads` (`ProcessThread.TotalProcessorTime`): it enumerates the process's threads, and
  allocates.
- **Split a unit's time by exact counts:** rows chosen and bytes encoded for routes, WebAssembly fuel for folds.
- **Keep it in counters** per topic and per tap: longs, added without allocating. Make accounting opt-in, costing
  nothing when off.

#### Exact counts beside it

- rows and bytes per second;
- posts in against deliveries out (what conflation saves);
- fuel per slice;
- lag: versions behind, and the age of the oldest change not yet delivered.

#### The load factor

Take the delivery workers' busy time over wall time, smoothed over a few seconds. At 1 the router is saturated: work
arrives faster than it's done, and lag grows.

### Show

- **Add a load page to the ops viewer,** each list heaviest first:
  - per route: its client, CPU ms a second, rows and bytes a second, lag;
  - per topic: subscribers, work, conflation;
  - per provider node: its share;
  - for the router: its load factor and recent history.
- **Serve the same figures** on a metrics endpoint, on the ops port.

### Share with providers

- **Use exchanges that already happen, and broadcast nothing:**
  - add the router's load factor and pressure state to its health check (`HealthProbe`, every second);
  - add the provider's share to the router's answer to the provider's heartbeat (`RouterBeat`); its topics' delays
    reach it by the rules it states ([below](#provider-side-shedding-delay-demand-and-slow-clients)).

  Each provider learns at its own cadence: there's no single moment for a herd to react to.
- **Signal a state with hysteresis, not a level:** pressure on at 0.85, off at 0.7 (both configurable), so it doesn't
  flap.
- **Ask, don't order.** Under pressure, ask the largest contributor to shed a fraction of its share (a tenth, say), then
  the next: one ask per interval, the next only once the last one's effect shows.
- **Have providers react slowly and within bounds:** small steps, with jitter, a gradual recovery, and never in
  response to another provider's ask.

### What each side can do

**The router, first:**
- lower frame rates for subscriptions that allow it;
- make fold slices larger: fewer, bigger updates;
- tighten fuel budgets for the heaviest ingests ([below](#dynamic-fuel-budgets)), which a provider meets as the 507
  back-pressure it already handles;
- move some subscriptions to other instances, or later to other routers
  ([below](#rebalance-subscriptions-across-a-services-instances)).

**A good-citizen provider:**
- post less often (a longer backoff), or post fewer, larger updates;
- share where exactness allows: a topic per class instead of per person, the activity feed's trade-off;
- defer optional work, such as statistics;
- refuse new subscriptions, with a reason;
- ask for some of its subscribers to be moved ([below](#rebalance-subscriptions-across-a-services-instances)).

### Dynamic fuel budgets

Under pressure, shrink each heavy topic's fuel budget per slice in proportion to its share; as pressure eases, grow it
back with the same smoothing. Fuel already meters WebAssembly folds (slices are sized by it, and each metered member's
call is kept within half its budget), so this is deterministic, and fair by construction.

### Rebalance subscriptions across a service's instances

Built: placement by load ([Health](provider.md#health)), and moves a provider or an operator asks for
([Leaving](provider.md#leaving-drain-and-bluegreen-rebalancing-moves)). Left: **let the router decide, under its own
pressure.** Once it measures its load (above), have it move subscriptions through the same queue, asking no one.
Also left: **decide moves by work units,** below.

#### Decide moves by work units

A normalized load suits placement: the router knows little, and that a moment old, and two random choices are robust to
it. It doesn't suit moves. A move sends one subscription to one instance, and a single ratio can't say what that
subscription costs, whether the target still fits once it arrives, or which subscriptions to move at all. Do as Service
Fabric, Shard Manager, Slicer and Cruise Control do: loads in units the application defines, compared only within one
kind of service. Moves never leave a service, so units are never compared across kinds.

This is between providers and the router alone. **Clients don't change:** a moved client is told to move, subscribes
again, and lets the old route go, exactly as now. Only which subscriptions move, and where each lands, changes.

- **Report units, not a ratio.** In the health report, give `Used` and `Capacity` in units of your own (rows a second,
  messages a second, view maintenance time), one dimension or a few (CPU-like, egress, memory), each named. The router
  derives the normalized load, the bottleneck dimension's `Used / Capacity`, so placement doesn't change. `Load` alone
  stays, and keeps today's behaviour.
- **Name the units.** Instances report a unit name per dimension. The router compares only instances naming the same
  units, so a deploy that redefines them (blue and green counting differently) doesn't compare apples with pears: until
  they agree, the other instance's units count as unknown.
- **Give each topic a cost,** at `Accept` (an estimate), and revise it through the topic as it's learned. Report it in
  two parts: what the topic costs to keep (once per instance, however many read it) and what each subscriber on it adds.
  Coalesce revisions; send only what moved, on exchanges that already happen.
- **Count the marginal cost.** The router knows which topics each instance serves. Moving the last subscriber off a
  topic saves its keeping cost; moving one of many saves only its own. A target already serving the topic adds only the
  subscriber's part, and one that doesn't adds the keeping cost too. So prefer moving a topic's subscribers together, to
  a target that serves it already or will.
- **Decide by the projected effect.** Move a subscription only if, afterwards, the source still carries more than the
  target by the margin (`MoveMargin`, in normalized terms): the move must lower the busier of the two, not swap them.
  Skip a target the move would take past a ceiling (0.85 of capacity), and count moves already in flight against it.
- **Let the router choose what moves.** Beside naming a partition or a route, let a provider ask to shed an amount:
  `MoveAsync(shed: units)`. The router picks the routes, heaviest-first among those that fit, until the amount is met,
  so the provider needn't guess which subscriptions matter.
- **Land each move where it was planned.** The router recognises the moved client's new step 1: the same connection,
  the same service and parameters as a route it's moving. It sends that step 1 to the planned target, holding a
  reservation there until it lands or the grace ends. If the target is no longer eligible, place by two random choices
  as for any step 1. Nothing comes back from the client: it subscribes as it does now.
- **Cross-check with what the router sees.** Self-reported units trust the provider, and busy time hides queueing past
  saturation. Once the router accounts per subscription (above), weigh its own signals beside the units: rows and bytes
  a second per topic, overdue acks, health-check round trips, and lag. They're comparable across providers without
  their cooperation, and lag is the symptom that matters (p99.5 staleness). Don't take them alone: a topic costly to
  compute but quiet on the wire (a pivot's views) looks cheap to the router.

### Move subscriptions to another router (later)

Moving between a service's instances doesn't relieve the router itself. Build this only once router load demands it.

#### Start a move from any side

- **The router decides,** under its own pressure.
- **The client chooses:** `MoveAsync(subscription, to, reason)`. With no `to`, pick by two random choices; name a router
  for tests and operators. Tell the router it leaves why, for its viewer. Offer a lag-based policy on the same call, off
  by default: move a subscription whose lag stays high for a while, with a dwell time so it doesn't flap.
- **Let a router refuse a move, saying why.**

#### Cut over across two connections

- **Hold sessions on several routers.** Grow `TaplineRouters` in `Tapline.Client` from one current router (plus one
  during a router drain) to several, each subscription on one. A new session signs in from the old one's ticket, as
  router drains do already.
- **Replace across connections:** subscribe on router B, and switch at B's first state, or first message on a message
  topic: from then, A is dropped and B is the subscription. Make the switch atomic (nothing from A handed over after
  B's first), and call the app's handler for the subscription one at a time.
- **Give messages failover semantics.** Don't deduplicate: a message near the switch may be missed or seen twice, as on
  any failover. What must be exact comes from a state topic.
- **Abandon rendezvous pending on A** at the switch.
- **Report A's end.** If A ends before B's first state, the app gets `OnError`, as for any interruption; B's first state
  is then a recovery. Bound the replacement by the grace.
- **Versions are per router.** B's first state replaces the replica outright.
- **Tell the client its router's id,** the UUIDv7 alone (not its name), as its session binds. If B turns out to be A
  (the same id), back off, pick again a few times, then keep A and say why.

#### Pick a router: a fallback name and two random choices

- **Resolve a fallback name,** a multi-value DNS service, for the candidates. Keep no view of the estate on the routers:
  no gossip, no registry. Trust between routers stays implicit for now.
- **Load-probe two candidates at random,** and connect to the less loaded.
- **Cache the candidates in the client SDK.** Re-resolve DNS in the background at an interval the client chooses, never
  more often than once a minute. Load-probe routers only at placement.
- **Serve the load call router-wide** (no parameters, so the caller needn't name a service), on client-facing ports,
  never ops. Count in it the sessions a router has been chosen for but doesn't hold yet, so a burst of choices can't all
  land on one router.
- **Keep clients near their region,** and never cap long-RTT clients.
- **Keep provider endpoints private:** they stay the router's alone.

**Test it:** give the headless clients `--move-every 10s`, beside `--drop-every` and `--drain` (client-initiated moves on
a timer), and have the verifier check that each cutover loses nothing it shouldn't.

### Protocol changes

| Exchange | Add |
|---|---|
| `HealthProbe` (router to provider, every second) | the router's load factor and pressure state; an ask to shed, with its fraction |
| `HealthReport` (provider to router, every second) | `Used` and `Capacity` per named dimension, beside `Load` |
| `Accept`, and the topic after it (provider to router) | the topic's cost: to keep it, and per subscriber; revisions coalesced |
| `MoveAsync` (provider to router) | an amount to shed, in its units; the router picks the routes |
| `RouterBeat` (the answer to a provider's heartbeat) | the provider's share of the router |
| the load call (client to router, new; later) | the router's load factor, sessions promised included |
| the session's binding (later) | the router's id (its UUIDv7) |
| client to router (new; later) | a move the client is making, and why; back: refused, and why |
| the client (the SDK; later) | sessions on several routers, and a cutover per subscription across them |

### Document it once built

- **Health:** what the probe and the heartbeat's answer carry, how to read them, how to shed, and when to ask for moves.
- **Observability:** the ops viewer's load page.
- **Leaving:** moves a provider requests, beside drains and blue/green.
- **A worked example:** the activity service shedding under a cohort notice's wave, and moving its dispatcher's clients
  before a deadline passes.

### Open questions

- **Charging shared topic work:** to the provider alone, or a share to each route on the topic?
- **Fairness:** may a router refuse a provider's moves for fairness between providers?
- **Several dimensions:** move by the bottleneck alone, or weigh the dimensions (Service Fabric's metric weights)?
- **Estimating a new topic's cost:** the provider's guess at `Accept`, or the mean of its topics so far until measured?
- **Trust between routers:** implicit for now; how to establish it once routers need it.

### The test bed

Use the [activity feed sample](../samples/ActivityFeed.Sample/README.md): it puts load where this design needs it.
- fan-out to a topic per person;
- a cohort notice's wave;
- a dispatcher falling behind its deadlines.

### Why not Kubernetes or a service mesh?

They balance processes and connections; this balances subscriptions. Use both: infrastructure keeps processes alive and
scales nodes, and Tapline keeps each subscription's service level.

- **They see connections, not subscriptions.** A mesh balances TCP streams (L4) or requests (L7): a client with one quiet
  topic and one with fifty busy order books look the same to it. The router sees each subscription's cost, and can move
  one heavy subscription off a connection, leaving the client's others where they are.
- **They don't know the state.** An autoscaler starts a replica blind: it must hydrate snapshots from the providers
  before it can serve, and a burst of new pods can stampede them. The router sheds by asks, one at a time, and moves
  subscriptions by two random choices, at the pace the providers allow.
- **They hide the client's path.** A proxy's buffers absorb a stalled client's frames and report the path clear, so the
  sender keeps pouring data in (bufferbloat). Tapline's flow control is end to end, clocked by the client's own acks, so
  it conflates as soon as a client falls behind.

Accounting per subscription keeps one runaway consumer from monopolising a router: the heaviest is found, asked, and
moved, one at a time, while good-citizen clients keep their latency.

## Provider-side shedding: delay, demand and slow clients

Have the router measure how long its providers' changes queue, cheaply, and tell each provider what it asked to know,
so it can act as a quoting engine does: go indicative, post less often, serve a lighter shape. Tell it too how fast its
topics' readers can take changes, and which of its clients are slow. This is between providers and the router alone:
**clients don't change.**

Built: a subscriber's declared limits at step 1. `SubscribeOptions.MinInterval` asks a rate of its own;
`SubscribeRequest.Limits` shows the provider that rate and its connection's cap (`bwlimit`), carried over the provider
link. The rest is below.

### Measure queuing delay

Three delays, two of them the provider's to act on:

- **Ingest:** from a post arriving at the router until it's applied to the topic, through the fold's slices and staged
  ingest. The provider's doing (it posts faster than the router absorbs): throttling relieves it directly.
- **Delivery, the router's part:** from a change applied until a frame carrying it goes to the transport. Under
  conflation a frame's sample is the age of the oldest change it carries. Subtract the time the route waited on its
  client (an ack overdue, a full window, its rate cap): that wait is the client's.
- **The client's wait:** kept out of the provider's figures. One slow client would otherwise set a topic's p99.5, and
  make it indicative for everyone; client bounds deal with slow clients already, and long-RTT clients are never
  capped.

Report ingest and delivery apart: they call for different actions.

### Keep percentiles cheaply

- **A log-linear histogram per delay** (HDR-style): a sample costs a leading-zero count, a shift and an increment,
  allocating nothing. Four sub-buckets per power of two keep about 12% relative error, ample for thresholds and ratios.
  Every percentile comes from the same counts, so which percentiles are asked doesn't change what's kept; the window
  and the set of topics measured do.
- **A rolling window as a ring of slices** (five of two seconds for ten seconds): advance on a timer tick, clear the
  oldest, sum the slices to evaluate. Exact at slice granularity, unlike exponential decay.
- **Stripes per worker for delivery:** a topic many read is delivered by several workers at once, so each records into
  its own stripe, merged only at evaluation, as the timer wheel's postings are. Ingest has one writer per topic.
- **Opt-in, costing nothing off:** about 1–3 KB per topic measured, and the activity feed has a topic per person. Keep a
  node's aggregate (all its topics) cheaply, and topics alone where a rule asks.

### Let the provider state its rules

Declare them node-wide when the provider connects (a default for all its topics), and override them per topic at
`Accept`. A rule:

| Part | |
|---|---|
| window | the rolling interval (ten seconds, say) |
| delay | ingest or delivery |
| scope | a topic, a partition, or the node's aggregate |
| condition | `pB / pA > ratio` and `pB − pA > floor` (p99.5 against p50: the floor keeps a ratio over tiny delays from tripping), or `p > threshold` (an absolute span) |
| minimum samples | below it, not evaluated: p99.5 of thirty samples is their maximum |
| hysteresis | clear at a lower level, or only after the condition has held clear for some slices; firm ↔ indicative flapping is the most visible failure |

- **Evaluate at each slice tick, and push transitions only:** "rule R tripped on topic T: p50 3 ms, p99.5 41 ms", then
  "cleared". Rare and edge-triggered, on the asynchronous notice path unsubscribes take: no tables each second, and no
  broadcast for providers to stampede on.
- **Answer a pull too:** a topic's current percentiles, for the provider's own logs and dashboards.
- **What the provider does is its own:** mark the topic indicative, post less often, serve a lighter shape, ask for a
  move or a shed (above). The router acts on its own pressure first, as ever.

### Tell the provider a topic's demand ceiling

A subscriber's limit can't slow a shared topic: one fast reader still takes every change. What the provider can act on
is the topic's ceiling: the fastest any of its subscribers can take changes, by their rates asked and what they're
achieving, across every router that mirrors it (each reports its own; the provider's router takes the greatest). With
every reader near a second, the provider can compute and post near a second; one trader at full speed lifts it again.
For a private topic it's that client's rate. Send it as the rules' transitions are: when it moves past a step, with
hysteresis.

### Flag slow clients, and serve them a lighter shape

`bwlimit` covers clients that say they're slow; the router sees those that are: acks overdue for a while, little
delivered against what's waiting. Compartmentalise them by shape, not by tick: per-subscriber conflation already sends
a slow client fewer, fuller deltas, while a fixed low-frequency topic would add up to its period of staleness even when
the client could take a change now. A lighter shape (fewer levels, coarser buckets, fewer columns) cuts the work and
bytes per change.

- **Tell the provider a route is slow,** as a transition, with what it's achieving.
- **The provider asks for it to be moved,** naming the route (rebalancing, above). Its client is told to move, as now,
  and subscribes again.
- **Its new step 1 carries what was observed** (`SubscriberLimits`: say, the bytes per second achieved), so the
  provider places it on the lighter topic. Nothing comes from the client: the router recognises its new step 1 as the
  moved route's (the same connection, service and parameters).

### Protocol changes

| Exchange | Add |
|---|---|
| the provider's connect (provider to router) | its default rules |
| `Accept` (provider to router) | a topic's rules, overriding the defaults |
| a notice (router to provider, new) | a rule tripped or cleared: the rule, the topic, the percentiles observed |
| a notice (router to provider, new) | a topic's demand ceiling, as it moves |
| a notice (router to provider, new) | a route found slow, or no longer, with what it achieves |
| a call (provider to router, new) | a topic's current percentiles |
| `SubscribeRequest.Limits` | what was observed of a moved slow route |

### Open questions

- **Delivery samples' cost:** one per frame per route, on the hot path. Sample a fraction under load, or record per
  frame only?
- **Slow by what measure:** acks overdue for how long, and delivered against what?
- **Ceilings across routers:** how often a mirroring router reports its own, and whether the link's own tap counts.

## Finalizers in ingest groups

A table's own ingest may finalize its fold (`table.Ingest<TState, TDelta>(fold, finalizer)`: a state per key kept by
the router, made into the rows subscribers see). Let an `IngestGroup` join such a table too, so a unit can change a
finalized table, plain tables and values as one transaction.

- **Join with a finalizer:** `group.Join<TKey, TRow, TState, TDelta>(table, fold, finalizer)`, answering a member that
  carries its state's type. The router's member keeps and finalizes states already; only the single-table ingest
  constructs it so today.
- **Snapshots of states in units:** `unit.Post(member, keys, deltas)` stands; `unit.Rows(member, keys, states)` takes a
  finalized member's states, not rows.
- **Atomic as ever:** a slice commits or rolls back across its members together, and a finalized member keeps its
  states on commit alone, so they follow. A finalizer failing mid-unit rolls back every member, as a fold failing does.
- **Taking over a table that has rows.** The router can't make states from rows (an average has no sums). A table's
  own finalized ingest refuses deltas until its first snapshot of states (`AwaitingStates`); a joining member does the
  same, its units' deltas refused while it awaits.
- **Test it:** a unit across a finalized table, a plain one and a value, applied as one; a finalizer failing mid-unit;
  a finalized member leaving and joining again (its states gone with it: a snapshot of states first).
- **Document it:** the field manual's groups, beside finalize.

## Whole-table finalizers: running totals at the router

A finalizer finishes one key at a time, and sees only the keys a batch touched, in no order. Some rows depend on the
rows before them: a VWAP book's cumulative size and its average price to fill, a running balance, a rank. Today the
provider computes those totals itself and posts each one's changes, so one change near the top of a book becomes a
change to every level below it on the way to the router. Let the router run them instead.

- **An ordered finish over the whole table.** Beside `IBatchFinalizer`, a finalizer given every key's state in an order
  the provider declares, with the rows it makes written alongside: a scan (a prefix fold), each row from its own state
  and the running total so far.
- **Declare the order and the runs.** Key fields and directions as the payload format already names them (a book: side,
  then price, bids down and asks up), and the leading fields that start a new run (a side: its own totals).
- **Finish from the first change on.** A batch's earliest touched key in the order bounds the work: the rows before it
  stand; it and the rows after are finished again. A change at the top of a book still finishes the whole side, but at
  the router, in one call, not as many posts.
- **The provider posts plain changes again.** A level's own size and notional; grouping stays a mapping of the book's
  changes, as it is without cumulation.
- **The same guarantees.** Only rows that moved are sent; the states are never sent; WebAssembly or C#, fuel-metered,
  allocation-free once warm; a finish that fails faults the table, as a fold that fails does.
- **Test it:** the order books' cumulative VWAP made by the router from per-level posts, matching the provider-made one
  row for row; a change at the top, and one at the depth's edge; a snapshot of states.

### Open questions

- **Ordering cost:** keep the table's keys sorted for it (a sorted index beside the hash), or sort the touched run per
  batch?
- **Depth at the router too:** with an order, the best N a side could be kept at the router as well, leaving the
  provider to post the whole book's changes. Is a router-side top-N worth it, or is depth the provider's?


## Heartbeat scheduling: one table's slice folded on several cores

Tables fold at once, each on the thread pool, but one table's slice is one fold call on one core: up to `maxDeltas`
(4,096) posts, or `sliceFuel`'s worth, about 70 µs through the WebAssembly `Add<long>` and up to half a millisecond for
a heavier fold. Its subscribers wait for that version meanwhile. Splitting every slice would cost more than most slices
take: 64 deltas fold in 0.2 µs, less than handing work to another core. Heartbeat scheduling (*Heartbeat Scheduling:
Provable Efficiency for Nested Parallelism*, PLDI 2018) splits only work that has already run for a while: a fold runs
sequentially, and at each heartbeat the work still ahead of it is offered to an idle core. A short slice never pays for
parallelism; a long one spreads, and the cost of splitting stays a small, bounded share of the work.

- **Split by key, each key's deltas in order.** The posts still ahead are divided by key (ranges of its hash), all of a
  key's deltas in one part, in post order, so any pure fold, commutative or not, makes the rows the sequential one
  would. Each part folds with an instance of its own from the pool: a WebAssembly fold is pure already.
- **Applied once.** The parts' rows make one update, one version, applied under the table's lock as now. The slice's
  budget (`maxDeltas`, `sliceFuel`) stays the slice's, shared by its parts.
- **A cheap heartbeat.** For a WebAssembly fold, the fuel it has used, metered already: promote every so many units
  (100,000 is 5–50 µs). For a C# fold, a timestamp read between posts. No timers, no signals.
- **A micro-task runtime.** Promoted parts run as micro-tasks on workers of the router's own, near the processor count
  as the timing wheel's stripes are, each with a deque the others steal from, not the thread pool's shared queue.
  Allocation-free once warm: a part is a slot in a worker's deque, its buffers kept.
- **Failures as now.** A part that fails (out of fuel, out of memory, a trap) abandons the split: the slice is folded
  again sequentially, halving as today, so what's applied and a post faulting the table "after the posts before it"
  mean what they do now. The fold is pure, so nothing is undone.
- **Groups too.** A unit's members fold outside their locks, independently: their folds can be parts of one slice.
- **Measure it:** a hot table's version latency (p99.5, from its posts to its applied update) and its throughput, with
  and without; and that a slice under one heartbeat runs exactly as it does now.

### Open questions

- **The heartbeat's period:** fixed, or set from the measured cost of a hand-off on this machine?
- **Split by key, or by posts:** a fold that declares itself associative could split the batch in post order and merge
  partial states, with no regrouping by key. Worth a second contract?
- **Whose workers:** the router's own per-core workers, or the thread pool's with local queues? Could frame building,
  each outlet's independent of the others, share them?
