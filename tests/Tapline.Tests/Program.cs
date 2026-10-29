using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BlazorRealtime;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Tapline;
using Tapline.AspNetCore;
using Tapline.Client;
using Tapline.Tests;
using Tapline.Wasm;
using Wasmtime;
using Tapline.Grid.Provider;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Table: random updates reconstruct exactly (small log → snapshot fallback)", () => T.TableReconstruction(64, 1)),
    ("Table: random updates reconstruct exactly (large log → deltas)", () => T.TableReconstruction(4096, 2)),
    ("Table: resync request yields a snapshot", T.ResyncYieldsSnapshot),
    ("Two-phase: a bound tap streams nothing until activated; activation is idempotent, owner-checked and starts with a snapshot; unactivated taps expire",
        T.TwoPhaseSubscribe),
    ("Fold: Add<T> folds relative changes, deletes at zero, leaves no slot behind", T.StaticFold),
    ("Fold: batch ABI matches the static fold; repeated keys compose (one call per batch)", T.BatchFold),
    ("Transactions: random updates with abandons match a reference; rollback restores everything; net no-ops publish nothing",
        T.Transactions),
    ("Preconditions: upstream version gap and checksum mismatch roll back and fault; faulted refuses updates; recovery",
        T.Preconditions),
    ("Fold faults: a throwing static or batch fold rolls back the whole update and faults the table", T.FoldFaults),
    ("Error policy: degraded snapshot + error in one frame, tap ends with its fault; activation on a faulted source; resubscribe after recovery",
        T.ErrorPolicies),
    ("Error policy fallbacks: throwing degrade → empty; too big → error only; doesn't fit → next frame",
        T.ErrorFallbacks),
    ("Error policy races: rotation continues in order after a fault close; a fault cleared mid-build streams normally",
        T.ErrorRaces),
    ("Slow client far behind: a delta of the rows changed (one pass over the slots, no more CPU than a snapshot) — hot rows, deletes, recycled slots; watch agrees; snapshot once all rows changed",
        T.SlowClientScanDelta),
    ("Unheld deletes: a client behind gets no delete for a row that came and went meanwhile (it never had it) — close behind (a walk of the log) and far behind (a pass over the slots), plain tap and watch alike; a delete for each row it held; a key gone and back again since keeps its delete (conservative); the replica verified",
        T.UnheldDeletes),
    ("Key-only deletes: a delete on the wire is its op and key — the header counts the deletes, the record as long as it says — and each record's size is measured exactly first: a walk of the log, a pass over the slots (a snapshot once that's no more bytes), a key range (no delete for a row never sent it); the client's replica rebuilds the table",
        T.KeyOnlyDeletes),
    ("Fault injection: test connections (same sources, same pump) see a shared injected fault — tables, values, watches — while sources and production carry on; recover; real faults reach all",
        T.FaultInjection),
    ("Review regressions: degrade runs once per faulted tap even when blocked; a tier change applies mid-debt",
        T.ReviewRegressions),
    ("Out of band, tables: a large snapshot becomes a handle, serialized once for 20 taps; the tap waits for its fetch; tags are per outlet; larger than a frame; budget charged; fetch timeout",
        T.OutOfBandTables),
    ("Out of band, values: >64 KB transparent; Reference handed over without waiting; superseded while fetching",
        T.OutOfBandValues),
    ("Out of band, fetch endpoint (Kestrel): bytes, per-outlet tag, immutable caching, Range; Tap.Fetched from JS",
        T.FetchEndpoint),
    ("Transaction: multi-source unit of work is atomic to a concurrent watch (invariant never torn); independent taps can tear",
        T.TransactionAtomicity),
    ("Transaction: rollback all; a member's fold fault or a precondition faults all; recovery clears all; values staged; misuse refused",
        T.TransactionSemantics),
    ("Watch: key-range member — subset checksum verified, changes outside the range send nothing, slot recycling + snapshot fallback",
        T.WatchRange),
    ("Watch: member fault → degraded members + error in one frame; resubscribe after recovery; a large group goes out of band",
        T.WatchFaultsAndOutOfBand),
    ("Review regressions II: out-of-order nested writer vs watch (no deadlock); stale Tap.Fetched ignored; store full retries; >64 KB watch value faults; no mailbox post after dispose; value Fail aborts a transaction",
        T.ReviewRegressions2),
    ("Client close: Tap.Close (JS-encoded) ends a tap with the client's fault code, or normally with 0", T.ClientClose),
    ("Last value: latest value per key, at most one record per key per frame", T.Conflation),
    ("Scheduler: saturated byte shares follow 16:4:4:1", T.WeightedShares),
    ("Ready queue: taps go oldest-dirty-first; further changes coalesce in place; a blocked record keeps its place",
        T.ReadyOrder),
    ("Ready queue: 8 publisher threads, no lost changes or wake-ups (parked consumer converges on its own)",
        T.ReadyConcurrency),
    ("Rate limit: Streaming holds the configured bytes/s (+burst); the other classes unaffected; tier change; the wheel wakes a parked outlet",
        T.RateLimit),
    ("Lane taps: a tap bound to High gets every update in the next frame past a rate-limited bulk stream (a Streaming tap waits behind it); bulk keeps its cap",
        T.LaneTaps),
    ("Timing wheel: fires once, never early, within a tick; reschedule, cancel, beyond one revolution", T.Wheel),
    ("Park-time wake-ups: an outlet registers with the wheel only when it parks with something held back by time (a frame not yet due, a retry) — once; busy, or waiting on data, never; woken at that time, its retry released by its own thread",
        T.ParkTimeWakeups),
    ("Timing wheel under contention (the worst case: twice as many threads as cores re-arming wake-ups as they fire): one wheel vs striped — schedule latency to the max, wake-up lateness; none early",
        T.WheelContention),
    ("Scheduler: a lone class gets the whole budget (work-conserving)", T.WorkConserving),
    ("Scheduler: oversize snapshot gets through under High saturation", T.OversizeUnderLoad),
    ("Scheduler: deferring to queued traffic → High only", T.DeferServesHighOnly),
    ("Heartbeat: first in frame, exempt from credit; ack restores credit and measures RTT — the router serving, and degraded (a longer heartbeat: its reason)",
        T.HeartbeatAndCredit),
    ("Router state behind the heartbeat: a RouterChange record — the state, a UUIDv7 correlation id, why (cut on a character's boundary at 255 bytes) or the grace left — behind each heartbeat till one is echoed, then heartbeats alone (13 bytes); a change at once without a tick (after the heartbeat in flight), again on a new connection and to a client arriving; each change its own id, told once (HealthChanged); the same again no change; draining one way",
        T.RouterStateOnHeartbeat),
    ("Router health, damped: degraded once the host's checks fail for DegradeAfter report after report (a blip: nothing); serving once they pass for RecoverAfter, doubled per degradation within the flap window up to MaxRecoverAfter, back after the window; other checks failing meanwhile logged under the episode's id, not told; draining: reports change nothing; a report summarized — failing checks in order, descriptions or exceptions, by tag",
        T.RouterHealthDamping),
    ("Partitions: accepted into a service's partition, the binding carries the service by name and the partition as ids — 16 hex digits a segment, an HMAC chained from the service's and the segments before it, keyed at random as the router starts (another router: other ids); one node, a UUIDv7; the viewer has the names; malformed partitions refused, a service's name one segment; a topic in a partition is in that one alone, one in none under several services",
        T.Partitions),
    ("Scoped health: a partition reported degraded — its clients told (the scope as they see it, the change's id, logged with it: ServiceHealthChanged), its routes held, the rest streaming, other clients told nothing, a client arriving told at once; no longer reported, recovered at once; flapping, its recoveries held (the hold doubling); the node degraded too, its recovery frees only the routes degraded by nothing else",
        T.ScopedHealth),
    ("Router drain on stop: draining at once (logged, with its id; its first state logged at start); held while a connected client holds a subscription, stopped once it lets go; the away and the idle not waited for; no grace: not waited; the grace run out: stopped, those left logged",
        T.RouterDrainOnStop),
    ("Heartbeat: on a link slower than the heartbeat rate they don't pile up or hold the window; book + value conflated, backlog bounded",
        T.SlowLinkHeartbeats),
    ("Ack overdue: a client that stops acknowledging (a blocked page) — state waits, then goes as one record each, the newest; a busy topic's messages and a mailbox's keep going, in order, none overflowing; a message ordered after state brings it along; nothing spins; off, state goes on",
        T.AckOverdueHoldsState),
    ("Path simulation: fixed window vs adaptive vs adaptive 60 Hz, colo to 16 kbit/s, bursts, a capacity drop — update latency, cadence, queue (on 16 kbit/s, the medians of 13 walks of the book)",
        T.PathSimulation),
    ("Flow control's delivery rate on 16 kbit/s, data frames longer on the path than the heartbeat interval, each acknowledgement covering a data frame and the heartbeats behind it (a transport sending only the newest waiting): measured from the data frames, within a quarter of the path's",
        T.FlowControlCoalescedAcks),
    ("Path simulation: an app-limited link on colo (a turn's records every half second, seconds of heartbeats alone, a receiver late now and then) — no turn held back",
        T.BurstSimulation),
    ("Frame cadence changed at runtime: uncapped → 60 → 30 → 120 → uncapped holds each rate; a higher cap applies from the last frame, not the old period",
        T.FrameRateAtRuntime),
    ("Media simulation: 5.8 KB frames at 120 Hz in Streaming + 500 B every 40 ms on High, colo to 160 kbit/s (and lite frames); SID rows behind tiny frames at 16 kbit/s — ridge rate and age, chunks on time, link use",
        T.MediaSimulation),
    ("Mailbox: FIFO across its ring's wrap-around (wasted tails), none lost; refused while full of unsent",
        T.MessageFifo),
    ("Allocations: steady-state set/fold/batch-fold/publish, message topic publish, mailbox post + build allocate nothing",
        T.ZeroAllocSteadyState),
    ("Blob store: any size in pooled segments (none larger than one); unique ids (UUIDv7) by default; content-addressed puts stored once, found before copying; capacity in whole segments",
        T.BlobStoreSegments),
    ("Blob store: a lease outlives eviction — its segments aren't reused until released (DEBUG: then poisoned); released twice is safe, read after release refused; pins hold blobs past the grace period",
        T.BlobStoreLeases),
    ("Blob store: 8 threads putting, leasing, issuing, pinning, sweeping — every leased read is its own blob's bytes; all memory accounted back",
        T.BlobStoreConcurrent),
    ("Blob store: steady-state put/issue/pin/unpin/sweep allocate nothing; a lease allocates only itself",
        T.BlobStoreZeroAlloc),
    ("Out of band without hashing: a snapshot and a delta to taps at the same cursor, and a value to every tap behind, each stored once",
        T.OutOfBandSharedWithoutHashing),
    ("Frame buffer: the outlet's own is 64 KB (off the large object heap); a larger first record (a 150 KB Inline snapshot) goes whole in the next frame, rented full-size and returned after; over MaxFrame still oversize; with no out-of-band threshold, an Auto table's 150 KB snapshot goes the same way (no fetch), and one over MaxFrame out of band",
        T.FrameBuffer),
    ("Frame buffer: steady state through TryBuildFrame, with a full-size frame every tenth step, allocates nothing",
        T.FrameBufferZeroAlloc),
    ("Data providers: step 1 through the service's provider (unknown service, refused, accepted onto its topic: bound, counted); a stream starts at the next change (and after a reconnect); 201/202 with the epoch; unsubscribed → a notice after the route is gone, and 202 — the provider stops; resubscribed → a new epoch; stale 202s and notices ignored; a table's snapshot by default; the mailbox's snapshot first, nothing older after; orphans expire (a notice); a connection ending (a notice)",
        T.DataProviders),
    ("Provider endpoints: registered at an address (one provider's; a service's several, step 1 to each in turn); the answer names the endpoint holding the route — its own, or another the router knows — recorded on the route; step 2 and the end relayed there alone, in order (an orphan: its end only); withdrawn: no more step 1s, its routes closed (service down), nothing relayed; the client's binding names none of it",
        T.ProviderEndpoints),
    ("Service up: told only to the clients waiting (told down, or found the service unavailable), by name, after the down; once the quorum of endpoints is up, or the grace period after the first; a subscribe that finds no instance throws unavailable and waits too; subscribed again, no longer waiting",
        T.ServiceUpNotices),
    ("Provider drain: one way — the instance gets no step 1s from then on (whatever it reports later); its clients let go one at a time, evenly over the time it asked, each with all its routes there, told drained (not down: subscribe again now — no up to wait for); nothing relayed for them; its other routes elsewhere untouched; a new instance at the endpoint takes step 1s again",
        T.ProviderDrain),
    ("A provider reached over a link, saying whether it's reached: step 1 goes to no endpoint whose provider isn't — each to the one reached, at once, none waiting out SubscribeTimeout — and none reached is unavailable at once; reached again, it's probed at once (no heartbeat): the waiting client told the service is up, its step 1 answered there",
        T.ProviderReachable),
    ("Blue/green, make before break: green starts alongside (its own instance name and endpoint), blue drains — new subscriptions all to green, blue alive and not down (live, not ready); its viewers told to move one at a time (its id, the grace), each subscribing again onto green while blue's streams on — both at once for a while, under their own tap ids — then dropping blue's; one that doesn't move is closed when its grace runs out (drained); withdrawn once drained, nobody to tell",
        T.BlueGreen),
    ("Router identity: UUIDv7, time-ordered per name; every message names the router; a provider introduced to it heartbeats it while holding its routes; a newer router of the name supersedes the older — the provider forgets the older's routes and NACKs it (step 1, health), and the older closes what it held there (RefusedBy); a router silent to the provider is presumed dead likewise — and, the partition healed, told once, then admitted afresh (no routes), its instance taken back by the router as a new node, served and kept; a router that no longer knows the instance tells it, and the provider forgets those routes — and, the provider answering again, the router takes it back",
        T.RouterIdentity),
    ("Step 1 elsewhere: an instance refusing it as served by another (RefusalKind.Elsewhere: a writer's alone, say) has the router ask the next one not asked, in the same call — it lands on the one that serves it, the others never told to the client; every one saying so: each asked once, the last refusal told, of its kind; any other refusal the answer, no other instance asked",
        T.StepOneElsewhere),
    ("Load-aware placement: a step 1 goes to the least loaded of a few of the service's instances picked at random, by the load each reports on its health checks (0 idle, 1 at capacity) — two random choices by default: the busiest of three never, the middle a third, the idlest two thirds; all picked, the least loaded always; one, at random; an instance not reporting counts as idle; none reporting, each in turn; a health check (a struct, from memory) allocating nothing",
        T.LoadAwarePlacement),
    ("Subscriber limits: step 1 asks a rate of its own (SubscribeOptions.MinInterval) — set on the route as it binds, before its first record; the provider shown it and the connection's cap (bwlimit) on SubscribeRequest.Limits; none asked, none shown; the lane still taken; a negative rate refused",
        T.SubscriberLimitsCarried),
    ("Finalized ingest: the fold keeps a state per key at the router (a sum of size, a sum of price × size) and its finalizer makes the row subscribers see (their volume-weighted average price), the sums never in the table; a state that moves while its row doesn't publishes nothing; a state at zero deletes its row; a snapshot of states replaces the table, deltas after it folding onto them; made over a table with rows, deltas refused till a snapshot of states; the same in WebAssembly (one module, fold and finalize), byte for byte; a finalizer that fails faults the table (FoldFailed), a snapshot recovers it; allocation-free once warm, rows kept or deleted and made again",
        T.FinalizedIngest),
    ("LoadMeter: a provider's load as its work over the time that passes — half busy about 0.5, as it was within the window, decaying when idle, per worker, a scope's time counted; measured and read allocating nothing",
        T.LoadMeterReads),
    ("Rebalancing: a provider asks to move some of its subscriptions (it keeps serving) — refused with one instance (nowhere to go); with another, each subscription's client told to move, one at a time, with its grace, nothing closed meanwhile; subscribed again, placed by load on the idle instance; one not moved in time let go (FaultCode.Moved), its provider told; the instance moved from keeps serving; those made within the dwell time stay put; one route by name; loaded alike, or less by under the margin, refused (nowhere better), by more taken; refused by hand for an unknown node or an empty partition",
        T.RebalanceMoves),
    ("Pauses aren't silence: a router paused three timeouts long (a debugger, a machine too loaded to run it) doesn't deem its provider dead on resuming, nor a paused provider its router — the time it didn't run doesn't count; heartbeats answered both ways after, kept",
        T.OwnPauseNotSilence),
    ("Service up, jittered: the waiting clients told at random moments spread over the policy's window, not at once",
        T.ServiceUpSpread),
    ("Subscribe lifecycle: a provider that never answers → unavailable after the timeout, its late accept refused, no route; accepted then stuck, cancelled, or threw → the route closed at once (an unsubscribe relayed), not left to expire; accepted after its client went → no route, told as an unsubscribe; binds racing a dispose leave no route behind",
        T.SubscribeLifecycle),
    ("Provider instances: the answer names the instance (name, endpoint, random id); the client gets the router's transient id for it, nothing else; heartbeats while it holds routes (none idle, one at a time); restarted (a new id in a heartbeat, or in an answer) or silent past the timeout → dead: its routes closed (ServiceDown), each client told once (resent after a reconnect until acked), nothing relayed, its id refused; a new instance, a new transient id",
        T.ProviderInstances),
    ("Data providers: epoch arbitration — subscribers coming and going against a publishing provider (acting on 202s, and on unsubscribe notices too): by the epoch rule never left stopped with a route bound (stopping on any 202 is)",
        T.EpochArbitration),
    ("Frame buffer: counted — a dispose while a frame is out leaves its buffer (own or rented) to the copy until released, then pooled (DEBUG: poisoned); the next client takes it: 100 clients, one buffer",
        T.FrameBufferLifetime),
    ("Wire: frame envelope parses with the real BlazorPack protocol — and is the one the SignalR JS client read as Tap.Frame(seq, bytes) (captured: tests/js/fixtures/signalr.json)",
        T.FrameParsesWithBlazorPack),
    ("Wire: control invocations as the SignalR JS client encodes them (captured: every one blazor-realtime.js sends, each integer width) — the C# replica writes the same bytes, each consumed in place; Tap.Ack/Activate/Resync/Resume act; Tap.Sync (encoded as the others, not in the capture) puts its rendezvous in the tap's mailbox",
        T.ControlFromJs),
    ("Wire: ack-only input commits via Ping; ack + message returns the message", T.ControlInterleaving),
    ("Transport: real RemoteJSRuntime bind call is matched by token", T.RemoteJsRuntimeBind),
    ("Transport: Clients.Caller routes through the decorated lifetime manager (real HubConnectionHandler)",
        T.CallerRoutesThroughLifetimeManager),
    ("Transport: end-to-end through HubConnectionContext + BlazorPack with live updates", T.EndToEnd),
    ("DI: AddBlazorRealtime wraps blazorpack and the lifetime manager; the circuit hub's SignalR timeouts (10 s, 3 s pings) unless the app set its own",
        T.DependencyInjection),
    ("Packages: core references the BCL alone, Tapline.Client core, the BCL and the health checks' abstractions (its router check), Tapline.Client.Opaque core, Tapline.Client and Wasmtime, Tapline.Authentication core and the BCL, Tapline.Authentication.Opaque those and Tapline.Client.Opaque, Tapline.AspNetCore nothing of Blazor (Components, SignalR, JS interop, Tapline.BlazorRealtime); the client's packages (Tapline.Client, Tapline.Client.Opaque) nothing of the server's or of ASP.NET Core; internals shared with the tests alone — no assembly with another",
        T.PackageSplit),
    ("Tapline.Client: a table, a value, a message topic and a mailbox replicated through an outlet under churn — every record verified (one snapshot, then deltas), every frame acknowledged; subscribed before bound, activated once bound; a delta that doesn't check out resynced once, healed by a snapshot; a reconnect resumed from the last frame received (no snapshot, nothing lost); a rendezvous after the state it named; dropped: closed",
        T.ClientReplicas),
    ("Tapline.Client, Tap.Sync: a client asks whether it's up to date on a subscription — a quiet one answered at once, up to date; one whose changes its interval holds back not answered till they're delivered, then up to date as of the ask; each ask its own id; asked unbound, nothing sent; and against a router that says it doesn't take Tap.Sync, failed at once, nothing sent",
        T.ClientSync),
    ("Tapline.Client, a watch: a table and a value written together in transactions — every cut handed over coherent (the value's row in step), the members' final state the sources'",
        T.ClientWatch),
    ("Tapline.Client, out of band: a snapshot, deltas and a large value fetched, applied and confirmed (the router going on); by reference, a handle fetched on demand and checked; fetches failing — resynced, then failed",
        T.ClientOutOfBand),
    ("Tapline.Client, the router's health: its state off the heartbeats (told once per change: its correlation id, why, gone by when), and a health check — healthy serving and heard, degraded when it says so or its heartbeats are late, unhealthy silent or out of reach",
        T.ClientRouterHealth),
    ("Tapline.Client, a service's health: subscriptions in partitions — a scope degraded covers those under it alone (told once, its correlation id); its check degraded (which scopes, the needle), healthy once recovered; the node gone, unhealthy waiting for up, told with the change's id; up again, healthy; the router out of reach, unhealthy",
        T.ClientServiceHealth),
    ("Tapline.Client, notices: a fault (the router's error, the subscription over); stalled behind a half-open connection — ended, told once whole again; a provider's instance withdrawn — ServiceDown, then the notice by its transient id; a provider's state on each subscription through one; a subscription to move — told once, with its grace and the move's correlation id",
        T.ClientNotices),
    ("Tapline.Client, status reads: a handler waiting on a lock of the app's while a thread holding it reads the client's status (reachable, router, last frame, since, bytes, rate, resyncs) — each answers, none waits on the client's lock",
        T.ClientStatusReads),
    ("Message history: a subscription's last sixteen records (the default; sizes rounded up to a power of two), newest first — kind, size, version, first bytes; when the data arrived, when sent, the lag between, when acked (joined from the client's frame log); the client's last frames; none kept at MessageHistory 0",
        T.MessageHistory),
    ("Message topics: every message in order, none conflated, from the next after step 1 (no snapshot, whatever is asked); nobody subscribed → 202 and nothing kept, the buffer released with the last subscriber; 201/202; overflow — Fault ends the subscription, Skip flags the gap, Refuse answers 507 (not for a subscriber whose client is away: it overflows); the mailbox — the subscriber's alone, flagged, numbered apart, refused when full of unsent",
        T.MessageTopics),
    ("Provider's initial state (InitialState.Provider): declared at step 1, sent on step 2 (Activation.Mailbox) however late — the topic held for the subscriber meanwhile; a message topic's history then the topic from after its cut (Ready(through)), in order, nothing twice; a last value's snapshot as of a version, nothing older after it; not sent within the bind timeout after step 2 → ended, NoInitialState, told; refused for a table or a direct tap",
        T.ProviderInitialState),
    ("Stale data on instance death: a route's error policy (the station's stale-data function, handed over at accept) run by the router on the state it holds when the instance is deemed dead — the client gets the degraded state, the error with the station's payload, then the service-down notice, in that order; unacknowledged, sent again after a reconnect",
        T.StaleDataOnInstanceDeath),
    ("Provider degraded — late (past ProviderDegradedAfter) or unhealthy by its own word — not gone: the client told (a provider-state record), then shown its route's stale data, run by the router as the provider's policy says (its function: the top of the book; a fixed value; the last message again, flagged stale), the stream held; healthy again: told, then a table from a snapshot, a value its current one, the messages held meanwhile; dead while degraded: the error, the stale data not again",
        T.ProviderDegraded),
    ("Provider health, paced as a host paces it (a check at most once an interval, degraded past twice that): a sweep a little early skips a check, the next sends it — an instance answering every check at once never late, however the sweeps fall; a check never answered, or checks failing, late past ProviderDegradedAfter, not before; answered, recovered",
        T.ProviderHealthPacing),
    ("Provider link, in process: step 1 answered by a provider in another process — a table and a value mirrored into the router's copies (the first state applied before the route is bound), their changes, a rendezvous after a change, a second route sharing the copy and the provider's one tap, a refusal with the provider's reason and correlation id; step 2 and the routes' ends relayed with the provider's epoch and the routes left; its health and heartbeats across the link; the last route's end lets its tap go",
        T.ProviderLinkInProcess),
    ("Provider link over MagicOnion: the same, the provider served by Kestrel (HTTP/2 cleartext), the router a gRPC client of it — its topics on a Tapline stream, the rest on the control stream",
        T.ProviderLinkMagicOnion),
    ("Provider link, identities: the declaration asked once and kept; a required service refused to the anonymous by the router (no step 1 sent); the identity carried whole — realm, user, session, module, insecure, times, claims; Forbidden carried back to the client; a renewal told to the provider; the subscriber's limits (its rate, its connection's cap) carried, the rate on the router's route — keys only added, an older link skipping them, a newer one reading none sent as none",
        T.ProviderLinkIdentity),
    ("Renewal redirected, over MagicOnion: signed in on router a through the stream's control messages; a renewal answered \"renew on b\" with a ticket b takes — a client of both moves there, make before break, the ticket presented before the app subscribes: the same sign-in on b; redirected to a router it doesn't know: it stays",
        T.RenewalRedirect),
    ("Provider link sign-ins: relayed over MagicOnion to an authentication provider in its own process — passthrough's identity and ticket, the ticket taken back, a module of two rounds bound to the connection's context, the current identity and the router carried, a refusal carried back",
        T.ProviderLinkSignIn),
    ("Provider link over MagicOnion, a table larger than the out-of-band threshold (16 KB) and an outlet's own frame buffer (64 KB): its snapshot, and a delta as large, travel in band, in full-size frames — a router fetches nothing out of band over the link",
        T.ProviderLinkLargeTable),
    ("Provider link, step 1's ends: timed out at the router before the provider answered — its late accept let go there, the provider told (an unsubscribe, its epoch), its tap let go; NACKed — unavailable, the router refused by that endpoint, what it held there closed as down and let go there untold",
        T.ProviderLinkStepEnds),
    ("Provider link, the provider's own drain: one way (no step 1s to it after), its routes let go as drained over the time it asked, nothing relayed for them (it asked), its taps there let go",
        T.ProviderLinkDrain),
    ("Provider link, control stream broken and opened again: the topics' stream carries on meanwhile, health checks unanswered, a route's end owed and told once it's open again; health answered after, a step 1 onto the same copy",
        T.ProviderLinkControlBreak),
    ("Provider link, the provider's process gone: its checks unanswered — degraded, then dead past its timeout — its route down, the client shown the provider's stale data (its policy, crossed at accept) with its payload, then told the service is down",
        T.ProviderLinkDeath),
    ("Provider link over MagicOnion, a control stream naming a session the provider's router doesn't hold: it ends with FailedPrecondition, saying so, and the provider logs no failure",
        T.ProviderLinkUnknownSession),
    ("Provider link over MagicOnion, the provider's process started again at its address (three times): the router greets it only once its Tapline stream is bound to a session there, a new one — never with the session it had of the process before; a step 1 after answered by the new instance; no failure logged at the provider",
        T.ProviderLinkRestart),
    ("Provider link over MagicOnion, a service's second provider registered before it runs: step 1 goes to the one reached, every time, at once (none to the other, none waiting out SubscribeTimeout); once it runs, its link up, step 1s go to it too",
        T.ProviderLinkNotRunning),
    ("Disconnected: not stalled — subscriptions held (as long as the outlet: Blazor's circuit) and resumed exactly; with a DisconnectedHold, released once it passes (Stalled), the client told when it's back",
        T.DisconnectedHold),
    ("Reconciled at a rebind: an activation re-sent for a subscription the router no longer holds — its error lost with the connection, or ended server-side meanwhile — is answered with an Ended record for it, saying why (Gone once forgotten); one still held, nothing; an id never issued, ignored",
        T.ReconciledAtRebind),
    ("Ordering: a message pulls the state it's ordered after (After) ahead of it into the same frame — a topic's after a source, each subscriber's own tap on it; a mailbox's after a tap; satisfied → nothing extra; out of band → held for the fetch, the tap closed → it goes; no tap on that source → not held; overdraws a rate limit; misuse refused",
        T.OrderingConstraints),
    ("Resume after a reconnect: the client says only the last frame it received; message streams and mailboxes go on from the next message it didn't get (none twice — a frame received but unacknowledged isn't sent again; an out-of-band one not fetched is), a table with a delta (no snapshot), a value only if it changed; evicted while away: an overflow; no word from the client: on from what it acknowledged",
        T.MessageResume),
    ("WebAssembly stale data (Tapline.Wasm): a provider's function as a module — the router runs it (the same rows as the C# function), its binding names it, its client fetches it from the router (application/wasm, immutable, content-addressed) while a subscription bound with it is; out of fuel, a trap, out of memory → an empty stale state, the instance discarded; invalid, importing, or without its exports → refused; concurrent applications, each its own instance",
        T.WasmStaleData),
    ("WebAssembly fold (Tapline.Wasm): a provider's fold as a module, run by the router — the same rows as the compile-time and C# folds (keys repeated in a batch, rows reaching zero); out of fuel, a trap, out of memory → the update rolled back, the table faulted (FoldFailed), the instance discarded, recovered with a fold that works; invalid, importing, or without its exports → refused; tables folding concurrently share it; steady state allocates nothing; a .NET fault on a thread that ran a module still the runtime's (caught), before and after a trap; against C#",
        T.WasmFold),
    (
        "Staged ingest: a provider posts and goes on — while the router's fold is held up, its posts return at once and the table's readers aren't kept waiting (the fold runs outside the lock); what was posted meanwhile folded in one call, one update; the client's mirror exact. A snapshot supersedes what's staged (and a batch taken, not applied), the next overwrites it; one update folds at most maxDeltas (whole posts, at least one; a snapshot's rows counted), the rest in turns after — a snapshot supersedes what's left; full → 507, a post larger than the capacity refused, a snapshot still staged; the upstream chain checked as it drains (the posts before a broken link applied, then VersionMismatch; deltas refused while faulted; a snapshot recovers); a batch's checksum its last post's; a fold that throws after a snapshot — the snapshot stands, then FoldFailed; direct updates refused; steady state allocates nothing",
        T.StagedIngest),
    ("Staged ingest, metered: a WebAssembly fold's slices sized by the fuel the router measured (fuel a delta learned from the calls); a slice out of fuel folded again with half its posts, and half again, until they fit — the rest after, the table never faulted; a post out of fuel on its own (a slice of its own: sized by the fuel learned) faults the table after those before it; a trap (a poisoned post, a C# fold) isolated likewise: the posts before it applied; steady state allocates nothing",
        T.StagedIngestMetered),
    ("Staged ingest, concurrent: 4 provider threads posting balanced pairs through a WebAssembly fold while readers copy the table — every state read sums to zero (no post torn, none split across updates), the totals exact",
        T.StagedIngestConcurrent),
    ("Lock domains: sources a transaction writes together share a domain from then on (merged with whatever shares one with them: a new domain, never edited); a transaction of one, none; a group's members are its domain — a member joining leaves the domain it was in, the rest staying together; leaving, alone again; a source never written with another never has one, nor a writer",
        T.LockDomains),
    ("Router threads never wait on a source lock: a writer holding a table mid-update as long as it likes — frames still built at once; an unrelated topic delivered; a new subscription to the held table retried, not waited for; a watch over the held table and a free one delivers the free one's part now, the held one's after the update; a held value likewise",
        T.RouterNeverWaits),
    ("Group ingest: a unit across tables and a value applied as one transaction (each member a version, the group one upstream chain); a broken link — the units before it applied, then every member faults, units refused until a snapshot (Rows, Publish) recovers them; a snapshot supersedes staged units across members but never a join or leave; a poisoned unit isolated by halving across members (those before it applied, then all fault); a member's checksum its last part's — wrong, rolled back, all fault; steady state allocates nothing; drained by hand only if the pool doesn't drain it too",
        T.GroupUnits),
    ("Group membership: joins and leaves staged in order with the units — a table's own ingest closed and its table joined at once: the group's units wait for that leave (all the ingest's posts first), then go on; joining a source another group writes (not leaving it) refused, and direct updates while a member; a member joined later named by units after its join, across the group in one transaction; one left can't be named, and once its leave is applied is written alone again (units staged before the leave applied first); joins and leaves by several groups queued before any is applied — each group in its turn",
        T.GroupMembership),
    ("Group ingest, watched under churn: a provider thread trading through a group (positions, cash) on the thread pool while a reserve table joins and leaves it, value moving between an account's cash and the reserve only while it's a member — an account watch and a watch over all three never see a unit torn (every delivery balances), however domains change mid-read (resyncs counted); converged",
        T.GroupWatchAtomicity),
    ("Viewer rendering: a topic's state as JSON by its format — a table's keys and rows by layouts derived from their types (a record struct's by its properties' names, an inline array as hex) or as its provider declared them (a price scaled, a side by name; one that doesn't fit not used, said so), a value by its layout or content type (JSON as it is, text a string, anything else its bytes), a message topic's kept messages newest first; a record rendered when it's whole — an out-of-band table snapshot's changes read from its bytes, a small in-band value",
        T.ViewerRendering),
    ("Payload formats: layouts derived with generated code (offsets exact — packed, padded; enums by name; inline arrays as bytes), payloads rendered from bytes alone (no types: every kind), the JSON form round-tripped, bad layouts and bad JSON refused; a view URL absolute http(s) without credentials, on its provider's own host (else refused at step 1); a provider's format given in step 1, on the topic before its route",
        T.PayloadFormats),
    ("Ops port: the viewer and endpoints marked RequireOpsPort answered on the ops port alone — on the app's port not there (404), whatever the Host header says; the app refused at startup with no ops port, one not listened on, the ops port alone, both, a shared one outside Development or for no reason; shared in Development for a reason: answered on the app's port, a banner on every page, the reason in the JSON and a header",
        T.OpsPort),
    ("Provider views: a topic's state and a whole record POSTed to its provider's view URL (the router's rendering, by its format) when the viewer asks, the answer relayed sandboxed and framed on the topic's and the record's pages; a record kept only in part: asked all the same, \"rendered\" null, by its upstream version (none: 404); redirects not followed, other content types, too large, slow or unreachable: said so in the frame (502, 504); no view URL: 404",
        T.ProviderViews),
    ("Upstream versions: each version marked with the provider's (as its publish gave it; none: null) — tables and values, through an ingest group too — its last 64 kept; a subscriber's record keeps the one it's as of and the one it was at before, through conflation (a snapshot: none before); none given: nothing kept",
        T.UpstreamVersions),
    ("Record retention: records kept whole by default, as sent — the newest while the subscription's ring holds them, older ones their first bytes, one larger than the ring its first bytes; per topic Prefix or None, a provider's ceiling lowering it; rings from a history budget of the client's own (never its memory budget; full: first bytes; given back as subscriptions end); read while written, never torn",
        T.KeptRecords),
    ("Snapshot deltas: a provider posting successive snapshots with their checksums — the router applies the newest against the state it holds and sends the cells that changed (one changed, one gone, one new: three, not every row); answers faster than it applies them superseded, one difference; a checksum mismatch faults the table, the update rolled back; the next snapshot recovers",
        T.SnapshotDeltas),
    ("Identities: a required service refused to a connection not signed in to its realm (the router's, unasked: Unauthenticated); step 1 carries the identity, its module and insecure flag, to services of its realm alone; providers refuse Unauthenticated or Forbidden; lapsed is anonymous; another user or a sign-out ends what was shown the old identity (IdentityChanged), a renewal nothing; the host's callback sets it",
        T.Identities),
    ("A subscription's own rate (Tap.MinInterval): its state sent at most once an interval, the rest conflated into what stands at its end (nothing changed: nothing sent); another subscription of the same source not held; a mailbox entry ordered after its state (a rendezvous) waiting with it, not letting it out; Hurry and a source's Barrier letting one through at once; a message topic's stream never held; taken away, at once again",
        T.SubscriptionMinInterval),
    ("SpscRing: one writer, one reader, no lock — a million items through eight slots across threads, both sides waiting, every item in order, little allocated (waits pooled); a slot read let go of; full, a write waits for room; a wait cancelled; completed, reading ends; one thread round, nothing allocated",
        T.SpscRingRoundTrip),
    ("SpscByteRing: variable-length messages in place, one writer, one reader, no lock — two million through 4 KiB across threads, wrapping, the reader spinning then parking: every one whole, in order; full, a claim fails and counts, reading makes room; too long, refused; each 8-byte aligned; an async reader, its waits pooled, completed and empty answering false; one thread round, nothing allocated",
        T.SpscByteRingRoundTrip),
    ("BatchPipe (two SpscRings): reusable batches going round between a producer that mustn't conflate and a slow reader — every batch, in order, whole; one unpublished kept for the next rent; the producer held back once every batch is with the reader, never more in flight; going round allocates nothing",
        T.BatchPipeRoundTrip),
    ("Sign-ins: relayed to the realm's authentication provider, one exchange at a time — passthrough enabled by AcceptAnyCredential alone, insecure; one authentication service a realm; the identity set on the connection and told to the client with a ticket; refusals (no provider, another realm, a module it lacks, a renewal by another user); a renewal keeps the session and every subscription; another user ends what alice was shown; the ticket resumes on a new session by itself, an altered or foreign one doesn't; a module of two rounds bound to the connection's context; cancelled, timed out, superseded; refused before read",
        T.SignIns),
    ("Passkeys (Tapline.Authentication): a realm's service of modules — a passkey registered by an invitation (the creation options; stored) and signed in by, by name or discoverable; refused from another origin, for another site, with a tampered signature, a counter gone back, an answer to another sign-in, unknown, unverified where required; a passkey and a code in sequence; a renewal by a fresh proof, not a ticket, under reauthenticate; resumed by ticket with the backend asked again; the backend's refusal; failures counted",
        T.Passkeys),
    ("OPAQUE (Tapline.Authentication.Opaque): a password set by an invitation, or changed signed in — its file kept, the password never sent; signed in by it; a wrong password or no such user refused alike, at the client; a login bound to another connection refused; a renewal keeps the session; attempts counted until they succeed",
        T.OpaqueSignIns),
    ("Session expiry: warned once ahead (the policy, why, a correlation id), a silent session renewed by the client with its ticket — the same session, a later expiry, every subscription on, its providers told; a fixed one can't be renewed and lapses — what requires a sign-in ends (SessionExpired), the rest anonymous, their providers told; the client told; a redirect for a renewal alone; the timer itself",
        T.SessionExpiry),
    ("Passthrough from configuration: by realm (Tapline:Authentication:Passthrough:{realm}), each its lifetime and policy — on only by AcceptAnyCredential, kept off without it; a near miss or a blank realm doesn't start; the router page lists them",
        T.PassthroughConfiguration),
    ("Wire: Tap.Auth(outletId, nonce, bin) as the SignalR JS client encodes a Uint8Array (bin8, bin16): consumed in place, relayed; a spoofed nonce acted on by none; past 16 KB not ours; MagicOnion's Control.Auth carries it as its payload",
        T.SignInFromJs),
    ("Refusals: a provider refusing with a reason — SubscribeAsync throws it, the router raises it, the client is sent it (once), each with the correlation id Refuse returned (a UUIDv7); nothing bound or charged; without a reason, null and nothing raised or sent; no accepting after; a long reason kept within bounds",
        T.Refusals),
    ("Ingest rendezvous: a provider posting staged snapshots asks for a rendezvous after what it has posted — delivered once the router applies it, at the version that holds it, after the table's record; at once when nothing waits; after a superseding snapshot, following the one applied",
        T.IngestRendezvous),
    (
        "Rendezvous: a requester's correlation id handed to the subscriber strictly after the state its change made (PublishResult.Seq; a table update's version) — a topic's message, then it, in the same frame (its mailbox goes before its topic's records, but not ahead of its own state); already sent, at once; a table conflated past it, after the state that supersedes it; a value; out of band, after the fetch; its provider degraded, after the recovery's snapshot; another subscription degraded, or the version not there yet, held without spinning; missed — before it subscribed, or skipped — said so; lost with the connection, both again in order; through a provider's mailbox; a full mailbox refuses; a mailbox message waits for a record being fetched",
        T.Rendezvous),
    ("Message topics, out of band: a large message stored once for every subscriber, fetched and applied; in the viewer's history",
        T.MessagesOutOfBand),
    ("Client bounds: a client's subscriptions charged to its memory budget (a tap and its history ring; a mailbox's ring once made; a provider's snapshot while it waits) — over it a subscription is refused (through a provider, before the provider is asked; a failed step 1 holds nothing) and so is a mailbox's first post; closing frees it. Stalled — nothing acknowledged for the timeout while there was something for it — its subscriptions end (their providers told), what they held freed, one Ended record told when it's back; acknowledging, or idle, never; bound since, they stand",
        T.CircuitBounds),
    ("Message history, out of band: the record a handle — its inner kind and blob id; the content leased while kept; after its grace, swept: gone (said so); an unknown blob: gone",
        T.MessageHistoryBlobs),
    ("Message history, concurrent: readers copying the history without locks while the builder writes — every entry kept is whole (a seqlock per slot: a torn one is left out)",
        T.MessageHistoryConcurrent),
    (
        "Router viewer: the router described without its directory lock (services, endpoints, instances with node ids and routes; its own state and correlation id) and a provider's subscription (service, instance, route); served — the router and clients as JSON and HTML; links from a client to its subscription, topic, service and instance, on to the instance's endpoint (checked now on request) and the routes it holds; 404 for none; 503 when busy; at scale — the clients a page of their own, 100 a page in the order they came (keyset: each page linking the next), every one with limit=all, HTML and JSON streamed; a topic's subscribers paged likewise; clients named by serial, never reused: a link to an ended one says so, its slot's next client under a new number; drained on request (ops port alone; a grace that isn't a duration refused): answered as its clients moved or the grace ran out, with its id; the drains offered as forms (the router's, each node's) on pages never framed — a page elsewhere posting one refused, a node's drained by the form and back to its page (another site's never gone to; its provider told what it let go), the outcome shown; a node's move refused with nowhere to go (409), for an unknown node (404), from a page elsewhere",
        T.RouterViewer),
    ("Router identity via DI: a registered RouterInstance as is (before or after AddTapline); else RouterName — code over configuration (the Tapline section, or Tapline__RouterName in the environment), set before the transport's registration or after; else host:port from configuration (urls, Kestrel endpoints, container ports); else the host alone; the Publisher uses it",
        T.RouterNaming),
    ("Compression cut-off: small circuit messages uncompressed (RSV1 clear), larger and fragmented compressed, a deflate client decodes all; wrapped on …/_blazor only, with compression on; send buffer (and on Linux TCP_NOTSENT_LOWAT) on circuit sockets only",
        T.CircuitCompressionCutoff),
    ("Compressible: content declared not compressible (tables, values, transactions, message topics; handles; all-incompressible watches) is counted per frame and, through protocol and socket, goes uncompressed",
        T.CompressibleContent),
    ("Rate from the URL: ?bwlimit= parsed like rsync --bwlimit (KiB/s; b, k/m/g, kb/mb/gb, ±1; min 512 B/s) caps the circuit's Streaming (lower only, one second of burst); 0, absent, malformed or unconfigured → unchanged",
        T.StreamingRateFromQuery),
    ("Fault injection: only connections opened under /test become test connections (a circuit: by its page); invalid options rejected; the shared cycle runs",
        T.FaultInjectionPages),
    ("Grid rules: a held total kept within 1 % of the one held (a row past it either way taken afresh; set where the rows end; grown by rows let in); a read for changes due its cadence after the last began (a microsecond before: not; on it and after: yes); the order value and the status value written and read back, another format or one cut short not one; the overscan told, what the link's delivery rate carries in a second in powers of two (up once the next fits; down once a quarter fewer than told fit, only while the link is the limit), and a resting reader's step (doubled, by no more than four fills, to the page's most; halved, the link the limit, never below the fill)",
        T.GridRules),
    ("Grid window, live and held: the live top at ranks, arrivals pushing rows down; held, arrivals above counted and nothing sent, a row in the window changed sent alone (the order unchanged), rows deleted in it and above keeping the rest in place, far reads placed by rank past the rows not shown; back at the top, live; a refusal kept stale with its reason until answered; a source's key twice refused; a new sort and filter from its top, the old rows gone; a key located, held about it — not let go by the page's asks from the top before it has scrolled there, then live once it has and scrolls back up — or not found and nothing changed",
        T.GridWindowLiveHeld),
    ("Grid window, held on the page's row: on a slow link the page draws a live window the session has moved on from; asked from it with the key of the row at its view's first position, the hold keeps that row there (thirty arrived since, above and not shown) — unanchored, the row now at that rank stands there instead; the row gone, placed by rank",
        T.GridWindowAnchoredHold),
    ("Grid letting rows in: held with fifty above — at 200 none, at 199 thirty, no more until the page has applied them (its request read in its own positions), then no more than it fetches above, none fetching none, none at rest however wide its pre-fetch (only as the reader scrolls up); a window reaching the top takes them; those let in counted unseen till the page shows them (fifty, then thirty at 30, twenty at 20); back at the top, live. At the first row shown with a view's worth or more above, held, thirty let in, the window from the top taking them; fewer, live; the live top asked for (the pill, Home), live with five hundred above. Racing up: with fewer above than the view holds that the reader hasn't seen (those let in above the view among them), live at once, the window the top's, wherever the reader was; with as many or more, held, as many let in as it fetches above near the top, three views at a time at the first row shown, none further down",
        T.GridLetIn),
    ("Grid by key: a source whose own order is its keys', greatest first — at the live top the order its header alone (40 bytes), the rows held the oracle's window placed by their keys; five arriving, five rows in at the top and five out, by key still; held, with its keys; back at the top from far away, its view first with its keys while the rows before linger, then its rest by key; sorted another way, with its keys",
        T.GridByKey),
    ("Grid kept place: held, a filter reads the new view about where the page's first row sorts in it — failing it, its rows from where it would be; passing, it first — in a hold of their own, the page asking round it held still; past the view's end, its last rows; at the top, held still — asked there, three arriving above counted — till the pill; asked for before it's placed, placed first; at the live top, the live top; a sort, the row first at its rank in the new order; no place kept, the top; a run's row first on the page, kept by its open row, its run after it",
        T.GridKeptPlace),
    ("Grid held, the view moving past every row the window holds (they stop passing its filter, new ones arriving above): read about the last row it held, where that sorts now — the rows above counted as they stand, never more than the view has, the total never below none, the rows still below shown, read after read; the pill, live, every row",
        T.GridHeldEmptied),
    ("Grid against an oracle: five seeds of random scrolling, jumps, arrivals, updates, deletes, sorts of up to three columns and filters of up to two, its own order by key — every window a run of the view's order worked out apart, at its ranks, the table its rows alone as the source has them (by key at the live top, placed by their keys); held still, every window the oracle's exactly",
        T.GridOracleRuns),
    ("Grid nested rules: the rows open, placed where a read found them, their runs as last read — a row of the view after the runs open above it, a view position a row of the view or of a run, moved all at once, unplaced when read elsewhere, unread for another view; as a session remembers them, three at most, one opened forgetting the least recently used (opened, or held by the window), never one the window holds — four while it holds three — and none remembered, those it holds kept; cleared, as new; a run's rows one to thirty-two deep",
        T.GridNestedRules),
    ("Grid nested rows: live, a row opened shows its run after it, the total counting it, arrivals pushing both down, by key too (the order's header and its runs); a chain thirty-two deep; a million under one, the window anywhere in it, a slice read and no more; held in it, the run growing or shrinking at its head moves every position and the page's first row stays where it's drawn; a million and two closed above, the same; from the live top, held on a run's row, kept there with four arrived above; its open row closed, drawn where that row was",
        T.GridNested),
    ("Grid every row open: live, the window's rows open, their runs after them, counted as read (the total at least the rows and the runs read); held further down, more counted; runs coming to be counted above the page's first row moving every position, the page's row where it's drawn; a row closed — a closed run on the wire — and opened again; a million rows' run read a few rows first, then by slice; the runs counted bounded, jumping here and there; every row closed again, the page's row where it's drawn",
        T.GridNestedAll),
    ("Grid store and stream merged: an append-only store, the stream ahead of it (half its versions not landed) and behind it (five landed, not sent) — in four views, the top, a page from a rank and one about a row the truth's, worked out apart: each key once at its newest, ranked and counted exactly; a key the stream alone has found about it, filtered out not; the store caught up and seen a haircut on, the state tail let go, the event log never kept",
        T.GridStoreMerge),
    ("Grid store and stream's watermark: no reads, the state tail's oldest row two haircuts behind the stream's newest — the store asked its newest stamp once, not again within a haircut, then the rows a haircut behind it let go, no recheck; a read bringing the store's newest stamp, unasked, and asking a recheck, none other within a Recheck, another a Recheck on",
        T.GridStoreWatermark),
    ("Grid store and stream, a read under way: its store rows taken before a version landed, the watermark passing that version by a haircut meanwhile — kept in the state tail till the read is done, so the read shows it, not the store's older one, and a row another read found the store holds; then let go",
        T.GridStoreInFlight),
    ("Grid store and stream bounded: the store stalled, past the state tail's cap the oldest row pruned, reads served (the pruned key as the store has it); caught up a haircut past it, the grids told to read again with no batch sent, the truth; a read's memory budget carried by each statement, one past it refused saying why; the event log kept for a view, at most its versions, back its reach from the newest, past the policy's refused, let go once no view keeps it; a policy that can't be kept to refused",
        T.GridStoreBounds),
    ("Grid store and stream's gap fill: a version written as the grid subscribed, which the stream never sends, landing after the first page — a haircut on, taken as if streamed, the grid told and the version shown; the event log the stream's alone",
        T.GridStoreGapFill),
    ("Grid store and stream against an oracle: four seeds of versions made, streamed in order and landed out of stamp order within the haircut (the store behind the stream, ahead of it, stalled), reads from ranks, about rows and about keys in four views — every page in order, no key twice, each row passing, none older than a version sent before the read; quiet, every page the truth's at its ranks, the state tail let go",
        T.GridStoreOracle),
    ("Grid store and stream's catch-up: the filters slow — what the stream sends unpublished (the tail and the version as they were), reads waiting for what was sent before them, a view asked for meanwhile opened after it; an order leaving the view, its store row past a recheck's reach, kept as the store counts it (an estimate), then, landed and read again, counted out exactly; a filter failed, its view's next read refused saying why and the one after opening it again; an evaluation failing outright, every view opened again by its next read",
        T.GridStoreCatchUp),
    ("Grid store and stream sized without counting: a page short of its limit counting the view, nothing asked of the store; past it the store's size, asked once; a store that can't say, at least the rows seen; about a row near the top, its place counted from the rows read back, far down the store's",
        T.GridStoreSizes),
    ("Grid store and stream's kept windows: what a read brings kept, changed by every change the stream sends — the live top and a held page each step the truth's, placed, the store asked nothing; read again past a window's edge; a version written to the store directly healed by a refresh off a read's path (the grids told, placed by the store's count); a window of final rows read again while the store's clock stands, settled once it moves on past them (the store's judgment); let go with its view, idle, past the policy's rows, and all when the tail prunes; past a view's own rows, its least recently read let go, another view's untouched; the rows held counted once each throughout; once closed, a read refused saying so",
        T.GridStoreWindows),
    ("Grid store and stream warm: each millisecond a version streamed, the store landing each eight on, a view keeping a second of history — the tails trimmed, and (optimised) the live top and a page about a row read each step — 10,000 steps allocating nothing",
        T.GridStoreAllocation),
    ("Alerts shared by shape: rules of one source, condition and trigger, in two audiences, one evaluation and one source, each page's subscription at its rule's throttle (Tapline's MinInterval: conflated, debounced); fired, the shape's count up by the rows, its latest last, a thousand firings one value; presented, the most a page says, never past the count, each audience its own; renamed or rethrottled, kept, its interval set; reshaped or added, from the count as it stands; a count fired at its throttle; refused before kept (checked, watched, past each cap); unattended, kept for the keep-for time, then let go with the shapes only it had; apart from grids, rows packed",
        T.AlertDeskShapes),
    ("Alert texts: inline, whole when it fits, else cut at a character's boundary and flagged, four-byte characters never split",
        T.AlertTexts),
    ("Grid nested against an oracle: six seeds of scrolling, jumps, rows opened and closed (in the window, and anywhere), orders arriving, changing and going, runs growing and shrinking, sorts and filters, the last two with three remembered at most (rows forgotten as others open, never one the window holds) — every window a run of the view as drawn, each row at its depth, its first row where the view has it, less the rows not shown above and the rows of runs above it the window doesn't know of: exactly",
        T.GridNestedOracle),
    ("Grid session's nested rows on the wire: a row opened or closed read at once, the page's replica (verified) the view as drawn, the run after its open row at its depths; a jump into a million rows' run; three remembered at most, a fourth forgetting the row open least recently used, out of the window — above it, every position up by its run, the page's rows kept where they're drawn — and the window's own outliving rows used since; closed, its run gone; every row open at the live top, one closed on the wire and the rest open, then every row closed again",
        T.GridSessionNested),
    ("Grid session on the wire: each request answered by a rendezvous, the page's replica (rows verified, order) the oracle's window — live, by key, the order its header alone and the rows placed by their keys; held, with its keys; held, arrivals above read at its cadence and the status value alone sent (five above; not the rows, nor their order); a row in view changed: one cut; back at the top, live, none above",
        T.GridSessionWire),
    ("Grid session's kept place on the wire: held, a filter — the replica's new view held where the page's first row is in it, that row first; the page asking round it, held still, the window widened; a sort, the row first at its rank in the new order; no place kept, the top, live",
        T.GridSessionKeptPlace),
    ("Grid session's first answers alone: on a slow link a window's answers merge on their way, the first waiting for the rest — the live top's view alone, its rest once the page has it; held from it on the page's row with thirty arrived since, the view and a view below alone, thirty above in the status; a request whose view the window holds answered as it stands, not widened; the page having the first, the last request's window",
        T.GridSessionFirstAlone),
    ("Grid overscan told by the link: under fixed flow control, or with no time for the fill, none (the page's own count); adaptive, none before the link's measured, then what its delivery rate carries in a second, both sides, in powers of two",
        T.GridOverscan),
    ("Grid overscan climbing while the reader rests: held, the page having asked with the overscan last told and rested a second since, its state sent, the overscan told climbs a step in the status alone — doubled, by no more than four fills — to the page's most; not before the reader has rested a second, nor again before the page has asked with it; its state waiting longer than a step takes, halved with the rows; back at the live top, afresh, a step each four seconds",
        T.GridOverscanClimb),
    ("Grid session's cadence on a clock moved by hand: live, a change read 100 ms after the last read began (a microsecond before: not), at once past it; held, 500 ms likewise; hidden, nothing read and the subscription's interval long; shown, read at once; a request, at once",
        T.GridSessionCadence),
    ("Grid session passive (hidden, or out of view): the source's changes not read, the status saying once that the source moved past the page's rows (alone: rows and order as they were), nothing more as it moves again; back in sight, its window asked again, read at once, its answer the rows now and the flag unsaid — the request alone saying it's shown, should the word of that be lost",
        T.GridSessionPassive),
    ("Grid session cancelled: a jump cancels the read in flight (its token in the source), the older never answered; a newer request touching it lets it finish and is read next; disposed mid-read, the read cancelled, the session ended and its subscription closed; no unobserved exception",
        T.GridSessionCancel),
    ("Grid session's jumps, the view first: a jump's view read and sent alone with its rendezvous, then the rows round it read from the view's; a scroll the window holds read whole, in one; one past it (outrunning the pre-fetch), its view first, the window's other rows left on the page until the rest and none sent again; a newer jump between a jump's stages gives up the first's rest (its read cancelled), the newer's view read, the rows left gone with it",
        T.GridSessionJumps),
    ("Grid session's views: a new sort or filter numbered, its rows' order saying which; the read in flight cancelled, a request waiting dropped, and one asked in the view before, crossing it, dropped unread — none answered; the page's request in the new view answered; the source told once of each view the grid is done with — once a newer one's answered, not as it's read — and as the grid closes",
        T.GridSessionViews),
    ("Grid session's bounds: options that can't work refused when made, saying why (a negative overscan fill among them); a client's grids at most so many (another client's its own); a window wider than its most refused, saying why; the session over with its subscription",
        T.GridSessionBounds),
    ("Grid allocation: a held window sliding through 100,000 rows, a row in and out and the order each answer — 10,000 answers on the thread, nothing allocated; 2,000 through the session's loop; nested, held in a million rows' run, the window's way to the wire allocating nothing either",
        T.GridAllocation),
    ("Grid memory: a session holds its window, not its view — as much over 50,000,000 rows as over 5,000",
        T.GridMemory),
    ("Grid on the wire, uncompressed, the blotter's rows (136 B) and UUID keys: bytes a row entering, scrolling a view at a time (S1: at most 400), and an arrival at the live top, with every key and by key (a row in, a row out and the order's header) — a row out its key alone",
        T.GridWireCost),
    ("LiveGrid's columns resolved against their rows: each field where it is in the bytes Tapline sends (laid out by the runtime, not as marshalled; read back as written), its kind from its type (integers signed or not, floats, bools, enums and their names, DateTime ticks, Guids, inline byte arrays as text, a record struct's properties), a scale making a fixed, a percentage of fields by place; formats .NET's (none: its default, a time's G spelt out), alignment, classes, overflow, figures; each as wide, in ch, as its format writes a wide value of its kind (figured, its widest over magnitudes), or its label",
        T.LiveColumnsResolved),
    ("LiveGrid's formats in a culture, as the page gets it: what differs from the invariant culture's (a date's names only where a pattern writes them; genitive months where a culture has them); a culture of another calendar in its Gregorian one; a time's standard format spelt out, o, r, s and u in the invariant culture's names",
        T.GridFormatsCulture),
    ("LiveGrid's columns refused, saying why: none, no name, a name twice, frozen after not, no such field (naming those there are), a decimal, a struct of its own, a kind that can't read its field, fixed without a scale, an enum without names, names or Of where they don't belong, a percentage of nothing or of text or sorted by, formats their values' types refuse (or of too many digits, or for kinds that have none), figures out of range or of what isn't a number written as a decimal, widths that aren't CSS lengths, scales out of range, more columns than the stylesheet places; an arrival time from a field that isn't one, from a field and the key both, or from a key that isn't a Guid; two expanders, a field saying which rows open without one, of no such field, or of text — and nested rows' columns resolved: the expander's with room for its chevron and a level, the field saying which rows open, a column a run's rows leave empty",
        T.LiveColumnsRefused),
    ("LiveGrid's keys as the page holds them (a character a byte, as tapline.js keys its replica) and back, anything else no key; a UUIDv7's time where the page reads it from its key; text written as the page reads it, cut at a character's end and flagged",
        T.LiveGridKeysAndText),
    ("LiveGrid rendered statically: refused where it would be, with the reason and the render mode to give it; no grid",
        T.LiveGridStaticRefused),
    ("LiveGrid's columns as a chooser anywhere on the page shows them (ILiveGridColumns, LiveColumnChooser): every column in order, shown at first as its Visible says; shown or hidden by name, in any case, the last shown kept; the chooser's ticks as the choices are, the last shown not to be unticked, its button alone till it has a grid",
        T.LiveGridColumnChooser),
    ("LiveGrid's sorts by a heading's click: ascending first (sorted by it alone, then reversed, and again), or descending first as a blotter sorts (then ascending, then the source's own order); shift-clicked, added the first way, reversed, taken out; a column named in any case",
        T.LiveGridSorts),
    ("LiveGrid's first window, read before the page asks, from the body's height: the rows in view and a quarter of them ahead (as the page's module asks first), in the view asked in, within the window's bound; no height, none",
        T.LiveGridFirstWindow),
    ("Fixtures: frames for the JS client test, and .NET's formats for the grid's cells", T.WriteJsFixtures)
};

// TAPLINE_TEST=<part of a name>: only those; TAPLINE_TEST_REPEAT=n: each n times (an intermittent failure).
var only = Environment.GetEnvironmentVariable("TAPLINE_TEST");
var repeat = int.TryParse(Environment.GetEnvironmentVariable("TAPLINE_TEST_REPEAT"), out var r) && r > 1 ? r : 1;
var failed = 0;
foreach (var (name, run) in tests.Where(t => only is null || t.Name.Contains(only, StringComparison.OrdinalIgnoreCase))
             .SelectMany(t => Enumerable.Repeat(t, repeat)))
{
    var sw = Stopwatch.StartNew();
    try
    {
        await run();
        Console.WriteLine($"PASS  {name}  ({sw.ElapsedMilliseconds} ms)");
    }
    catch (Exception e)
    {
        failed++;
        Console.WriteLine($"FAIL  {name}\n      {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
    }
}

Console.WriteLine();
foreach (var note in T.Notes) Console.WriteLine($"  · {note}");
Console.WriteLine(failed == 0 ? $"\nAll {tests.Length} tests passed." : $"\n{failed} of {tests.Length} tests FAILED.");
return failed == 0 ? 0 : 1;

/// <summary>Domain types live in the app, not the library: an L2 order book is a table keyed by (side, price).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly record struct Level(byte Side, long Price);

/// <summary>A position: keyed by (account, symbol), so one account's positions are a key range.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly record struct Holding(int Account, int Symbol);

/// <summary>A row with plain fields and an inline array (rendered as hex), for the viewer's rendering.</summary>
internal struct Tagged
{
    public ushort Count;
    public Four Bytes;
}

[InlineArray(4)]
internal struct Four
{
    private byte _first;
}

/// <summary>A struct with padding, for layouts derived as the runtime lays it out.</summary>
internal readonly record struct Padded(byte A, long B, bool C, char D);

internal enum Side : byte
{
    Bid,
    Ask
}

/// <summary>An enum field (rendered by name) and a nested struct, for derived layouts.</summary>
internal struct Order
{
    public Side Side;
    public Level At;
    public Guid Id;
}

internal static partial class T
{
    // ─────────────────────────────── transactions & watches ───────────────────────────────

    private const long Endowment = 1_000_000;

    private const long EnvelopeSeq = 123_456_789_012L;

    /// <summary>
    ///     The top of the book as a WebAssembly module: the best bid and ask kept, sizes zero (entries: u32 flags | side
    ///     u8 | price i64 | size i64).
    /// </summary>
    internal const string TopOfBookWat = """
                                         (module
                                           (memory (export "memory") 1)
                                           ;; The entries go at 0: memory grown to fit (or a trap).
                                           (func (export "buffer") (param $size i32) (result i32)
                                             (local $short i32)
                                             (local.set $short (i32.sub (i32.shr_u (i32.add (local.get $size) (i32.const 65535)) (i32.const 16)) (memory.size)))
                                             (if (i32.gt_s (local.get $short) (i32.const 0))
                                               (then (if (i32.eq (memory.grow (local.get $short)) (i32.const -1)) (then unreachable))))
                                             (i32.const 0))
                                           (func (export "degrade") (param $at i32) (param $count i32) (param $keySize i32) (param $rowSize i32)
                                             (local $i i32) (local $e i32) (local $bid i32) (local $ask i32) (local $p i64) (local $bp i64) (local $ap i64)
                                             (if (i32.or (i32.ne (local.get $keySize) (i32.const 9)) (i32.ne (local.get $rowSize) (i32.const 8))) (then unreachable))
                                             (local.set $bid (i32.const -1))
                                             (local.set $ask (i32.const -1))
                                             (block $scanned
                                               (loop $scan
                                                 (br_if $scanned (i32.ge_u (local.get $i) (local.get $count)))
                                                 (local.set $e (i32.add (local.get $at) (i32.mul (local.get $i) (i32.const 21))))
                                                 (local.set $p (i64.load offset=5 align=1 (local.get $e)))
                                                 (if (i32.eqz (i32.load8_u offset=4 (local.get $e)))
                                                   (then (if (i32.or (i32.lt_s (local.get $bid) (i32.const 0)) (i64.gt_s (local.get $p) (local.get $bp)))
                                                     (then (local.set $bid (local.get $i)) (local.set $bp (local.get $p)))))
                                                   (else (if (i32.or (i32.lt_s (local.get $ask) (i32.const 0)) (i64.lt_s (local.get $p) (local.get $ap)))
                                                     (then (local.set $ask (local.get $i)) (local.set $ap (local.get $p))))))
                                                 (local.set $i (i32.add (local.get $i) (i32.const 1)))
                                                 (br $scan)))
                                             (local.set $i (i32.const 0))
                                             (block $marked
                                               (loop $mark
                                                 (br_if $marked (i32.ge_u (local.get $i) (local.get $count)))
                                                 (local.set $e (i32.add (local.get $at) (i32.mul (local.get $i) (i32.const 21))))
                                                 (i32.store align=1 (local.get $e)
                                                   (i32.or (i32.and (i32.load align=1 (local.get $e)) (i32.const -2))
                                                           (i32.or (i32.eq (local.get $i) (local.get $bid)) (i32.eq (local.get $i) (local.get $ask)))))
                                                 (i64.store offset=13 align=1 (local.get $e) (i64.const 0))
                                                 (local.set $i (i32.add (local.get $i) (i32.const 1)))
                                                 (br $mark)))))
                                         """;

    /// <summary>
    ///     Add&lt;long&gt; (and <see cref="BytesAdd" />) as a WebAssembly fold: rows (u32 flags | i64), then deltas (u32
    ///     row | i64); each delta added to its row, present while non-zero.
    /// </summary>
    private const string AddFoldWat = """
                                      (module
                                        (memory (export "memory") 1)
                                        ;; The batch goes at 0: memory grown to fit (or a trap).
                                        (func (export "buffer") (param $size i32) (result i32)
                                          (local $short i32)
                                          (local.set $short (i32.sub (i32.shr_u (i32.add (local.get $size) (i32.const 65535)) (i32.const 16)) (memory.size)))
                                          (if (i32.gt_s (local.get $short) (i32.const 0))
                                            (then (if (i32.eq (memory.grow (local.get $short)) (i32.const -1)) (then unreachable))))
                                          (i32.const 0))
                                        (func (export "fold") (param $at i32) (param $rowCount i32) (param $rowSize i32) (param $deltaCount i32) (param $deltaSize i32)
                                          (local $d i32) (local $end i32) (local $row i32) (local $q i64)
                                          (if (i32.or (i32.ne (local.get $rowSize) (i32.const 8)) (i32.ne (local.get $deltaSize) (i32.const 8))) (then unreachable))
                                          (local.set $d (i32.add (local.get $at) (i32.mul (local.get $rowCount) (i32.const 12))))
                                          (local.set $end (i32.add (local.get $d) (i32.mul (local.get $deltaCount) (i32.const 12))))
                                          (block $done
                                            (loop $next
                                              (br_if $done (i32.ge_u (local.get $d) (local.get $end)))
                                              (local.set $row (i32.add (local.get $at) (i32.mul (i32.load align=1 (local.get $d)) (i32.const 12))))
                                              (local.set $q (i64.add (i64.load offset=4 align=1 (local.get $row)) (i64.load offset=4 align=1 (local.get $d))))
                                              (i64.store offset=4 align=1 (local.get $row) (local.get $q))
                                              (i32.store align=1 (local.get $row) (i64.ne (local.get $q) (i64.const 0)))
                                              (local.set $d (i32.add (local.get $d) (i32.const 12)))
                                              (br $next)))))
                                      """;

    public static readonly List<string> Notes = new();

    /// <summary>App-side degrade function: keep the best bid and best ask, with size zero (prices known, liquidity not).</summary>
    private static readonly IDegradeFunction TopOfBook = DegradeFunction.For<Level, long>(rows =>
    {
        int bid = -1, ask = -1;
        for (var i = 0; i < rows.Length; i++)
        {
            var k = rows[i].Key;
            if (k.Side == 0)
            {
                if (bid < 0 || k.Price > rows[bid].Key.Price) bid = i;
            }
            else if (ask < 0 || k.Price < rows[ask].Key.Price)
            {
                ask = i;
            }
        }

        for (var i = 0; i < rows.Length; i++)
        {
            rows[i].Present = i == bid || i == ask;
            rows[i].Row = 0;
        }
    });

    private static void Assert([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    /// <summary>
    ///     <see cref="GC.GetAllocatedBytesForCurrentThread" /> to measure from, with this thread's allocation context first
    ///     retired
    ///     by a gen-0 collection, which subtracts its unused rest. A background GC voids every thread's context at the end of
    ///     its mark phase (gc.cpp: <c>repair_allocation_contexts(FALSE)</c>) without subtracting it, so a thread that
    ///     allocates
    ///     nothing would count what was left of its last allocation quantum — up to ~8 KB, once, at a random point (about one
    ///     full run in ten). Retired first, an allocation-free window has nothing to void; one that allocates takes a new
    ///     context, and counts.
    /// </summary>
    private static long AllocationBaseline()
    {
        GC.Collect(0);
        return GC.GetAllocatedBytesForCurrentThread();
    }

    /// <summary>
    ///     Whether <paramref name="assembly" /> was compiled optimised (Release). Unoptimised (Debug), the C# compiler makes
    ///     each async method's state machine a class, allocated at every call (for Edit and Continue), so a pooled method
    ///     builder (<see cref="PoolingAsyncValueTaskMethodBuilder" />) has nothing to pool: only optimised is a method's
    ///     wait allocation-free.
    /// </summary>
    private static bool Optimised(Assembly assembly)
    {
        return assembly.GetCustomAttribute<DebuggableAttribute>() is not { IsJITOptimizerDisabled: true };
    }

    /// <summary>A free TCP port on the loopback, for a listener of a test's own (an ops port, say).</summary>
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>A frame's heartbeat bytes: the heartbeat leading it, and the router's change riding behind it (none: 0).</summary>
    private static int HeartbeatBytes(ReadOnlySpan<byte> f)
    {
        return f.Length < Wire.HeartbeatSize || f[0] != Wire.Heartbeat
            ? 0
            : Wire.HeartbeatSize + (f.Length > Wire.HeartbeatSize && f[Wire.HeartbeatSize] == Wire.RouterChange
                ? Wire.RouterChangeLength(f[Wire.HeartbeatSize..])
                : 0);
    }

    private static Outlet NewOutlet(OutletRegistry registry, Action<OutletOptions>? configure = null)
    {
        var o = new OutletOptions();
        configure?.Invoke(o);
        return new Outlet(registry, o);
    }

    /// <summary>
    ///     A subscription in <paramref name="lane" />'s class (null: Streaming), activated, to a message topic of its own that
    ///     never publishes: its mailbox (<see cref="Tap.TryPost" />) puts messages in that class. <paramref name="c" />, if
    ///     given,
    ///     counts its bytes in that class.
    /// </summary>
    private static Tap Poster(Outlet s, Lane? lane, RefClient? c = null)
    {
        var tap = s.Tap(new MessageTopic(1, 1), lane: lane, activate: true);
        if (lane is { } l && c is not null) c.Classes[tap.Id] = l;
        return tap;
    }

    /// <summary>A blob's bytes as the endpoint would serve them to <paramref name="outlet" /> (null: not fetchable).</summary>
    private static byte[]? Fetch(OutletRegistry registry, BlobId id, int outlet, byte[] tag)
    {
        if (!registry.Blobs.TryLease(id, outlet, tag, registry, out var lease)) return null;
        using (lease)
        {
            return lease.ToArray();
        }
    }

    private static int Drain(Outlet s, RefClient c, byte[] scratch, int max = 10_000)
    {
        var frames = 0;
        for (; frames < max; frames++)
        {
            var n = s.Build(scratch);
            if (n == 0) return frames;
            c.Decode(scratch.AsSpan(0, n));
        }

        throw new Exception("Drain did not converge");
    }

    private static Dictionary<string, (byte[] Key, byte[] Row)> Canonical<TKey, TRow>(Table<TKey, TRow> table)
        where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged
    {
        var list = new List<KeyValuePair<TKey, TRow>>();
        table.CopyTo(list);
        return list.ToDictionary(kv => Convert.ToHexString(Bytes(kv.Key)), kv => (Bytes(kv.Key), Bytes(kv.Value)));
    }

    private static byte[] Bytes<T0>(T0 value) where T0 : unmanaged
    {
        return MemoryMarshal.AsBytes(new ReadOnlySpan<T0>(in value)).ToArray();
    }

    private static void AssertMirror<TKey, TRow>(RefClient.TableMirror m, Table<TKey, TRow> table, string who)
        where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged
    {
        var canon = Canonical(table);
        Assert(m.Failures == 0, $"{who}: {m.Failures} verification failures");
        Assert(m.Seq == table.Version, $"{who}: seq {m.Seq} != {table.Version}");
        Assert(m.Checksum == table.Checksum, $"{who}: checksum mismatch");
        Assert(
            m.Rows.Count == canon.Count && canon.All(kv =>
                m.Rows.TryGetValue(kv.Key, out var r) && r.Row.AsSpan().SequenceEqual(kv.Value.Row)),
            $"{who}: rows differ");
    }

    // ─────────────────────────────── tables ───────────────────────────────

    public static Task TableReconstruction(int logCapacity, int seed)
    {
        var registry = new OutletRegistry();
        var book = new Table<Level, long>("TEST", logCapacity);
        var rnd = new Random(seed);
        var outlets = Enumerable.Range(0, 3).Select(_ => NewOutlet(registry, o => o.FrameBudget = 2048)).ToArray();
        var taps = outlets.Select(o => o.Tap(book, activate: true)).ToArray();
        var clients = outlets.Select(_ => new RefClient()).ToArray();
        double[] drainProbability = [1.0, 0.2, 0.01];
        var scratch = new byte[1 << 20];
        long changes = 0;

        for (var step = 0; step < 30_000; step++)
        {
            using (var u = book.BeginUpdate())
            {
                if (rnd.Next(1000) == 0) u.Clear();
                for (var i = rnd.Next(1, 6); i > 0; i--, changes++)
                {
                    var level = new Level((byte)rnd.Next(2), 1000 + rnd.Next(80));
                    if (rnd.Next(4) == 0) u.Delete(level);
                    else u.Set(level, rnd.Next(1, 5_000_000));
                }

                u.Complete();
            }

            for (var i = 0; i < outlets.Length; i++)
                if (rnd.NextDouble() < drainProbability[i])
                    Drain(outlets[i], clients[i], scratch);
        }

        for (var i = 0; i < outlets.Length; i++)
        {
            Drain(outlets[i], clients[i], scratch);
            var m = clients[i].Tables[taps[i].Id];
            AssertMirror(m, book, $"outlet {i}");
            Notes.Add(
                $"log={logCapacity} outlet {i}: {m.Snapshots} snapshots, {m.Deltas} deltas, {m.Entries} entries for {changes} row changes");
        }

        var fast = clients[0].Tables[taps[0].Id];
        var slow = clients[2].Tables[taps[2].Id];
        Assert(slow.Entries < changes / 2 && slow.Entries < fast.Entries / 2, "slow tap should receive squashed data");
        if (logCapacity == 64) Assert(slow.Snapshots > 10, "small log should force snapshot fallback");
        else Assert(fast.Snapshots == 1, "fast tap on a large log should need only the initial snapshot");
        return Task.CompletedTask;
    }

    public static Task ResyncYieldsSnapshot()
    {
        var registry = new OutletRegistry();
        var book = new Table<Level, long>("TEST");
        var s = NewOutlet(registry);
        var tap = s.Tap(book, activate: true);
        var c = new RefClient();
        var scratch = new byte[1 << 20];
        using (var u = book.BeginUpdate())
        {
            u.Set(new Level(0, 100), 5);
            u.Complete();
        }

        Drain(s, c, scratch);
        using (var u = book.BeginUpdate())
        {
            u.Set(new Level(1, 101), 7);
            u.Complete();
        }

        Drain(s, c, scratch);
        var m = c.Tables[tap.Id];
        Assert(m.Snapshots == 1 && m.Deltas == 1, "expected snapshot then delta");
        s.Resync(tap.Id);
        Drain(s, c, scratch);
        Assert(m.Snapshots == 2, "resync should produce a snapshot");
        AssertMirror(m, book, "resynced");
        return Task.CompletedTask;
    }

    public static Task TwoPhaseSubscribe()
    {
        var registry = new OutletRegistry();
        var a = NewOutlet(registry, o =>
        {
            o.BindTimeout = TimeSpan.FromMilliseconds(50);
            o.Endpoint = "wss://gw-7.example.net";
        });
        var other = NewOutlet(registry);
        var book = new Table<Level, long>("TWO-PHASE");
        using (var u = book.BeginUpdate())
        {
            u.Set(new Level(0, 1), 10);
            u.Complete();
        }

        var protocol = new RealtimeProtocol(BlazorPack(), registry);
        var c = new RefClient();
        var scratch = new byte[1 << 20];

        var tap = a.Tap(book, "book:BTC-PERP"); // step 1: bound, binding handed to the caller
        Assert(tap.Binding == new TapBinding(tap.Id, "book:BTC-PERP", "wss://gw-7.example.net"),
            "binding should carry id, topic and endpoint");
        var json = JsonSerializer.Serialize(tap.Binding, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert(
            json ==
            $"{{\"tapId\":{tap.Id},\"topic\":\"book:BTC-PERP\",\"endpoint\":\"wss://gw-7.example.net\",\"service\":null,\"nodeId\":null,\"partition\":null,\"stale\":null}}",
            $"binding JSON shape the JS client expects: {json}");
        Assert(NewOutlet(registry).Tap(book).Binding.Endpoint is null, "endpoint is optional");
        using (var u = book.BeginUpdate())
        {
            u.Set(new Level(0, 2), 20);
            u.Complete();
        }

        Assert(!tap.IsActive && !a.IsReady && a.Build(scratch) == 0, "a bound tap must not stream");

        void Send(string target, params long[] args)
        {
            var input = new ReadOnlySequence<byte>(EncodeWithJs(target, args));
            protocol.TryParseMessage(ref input, new Binder(), out _);
        }

        Send("Tap.Activate", other.Id, other.Nonce, tap.Id); // another outlet can't activate it
        Send("Tap.Activate", a.Id, a.Nonce ^ 1, tap.Id); // nor a wrong nonce
        Send("Tap.Activate", a.Id, a.Nonce, tap.Id + 1000); // unknown ids are ignored
        Send("Tap.Resync", a.Id, a.Nonce, tap.Id); // resync of an inactive tap is ignored
        Assert(!tap.IsActive && !a.IsReady, "foreign/unknown activation must be ignored");

        Send("Tap.Activate", a.Id, a.Nonce, tap.Id); // step 2: the client registered its handler
        Assert(tap.IsActive && a.IsReady, "activation should start streaming");
        Drain(a, c, scratch);
        var m = c.Tables[tap.Id];
        Assert(m.Snapshots == 1 && m.Deltas == 0, "first record after activation must be a snapshot");
        AssertMirror(m, book, "two-phase");

        Send("Tap.Activate", a.Id, a.Nonce, tap.Id); // duplicate (e.g. re-sent on reconnect)
        Assert(a.Build(scratch) == 0, "duplicate activation must be ignored");

        var abandoned = a.Tap(book); // bound but never activated
        Thread.Sleep(80);
        a.Heartbeat();
        Assert(abandoned.IsClosed && !a.Taps.Contains(abandoned) && tap.IsActive,
            "unactivated taps expire; active ones don't");
        abandoned.Dispose(); // disposing an expired tap is harmless
        return Task.CompletedTask;
    }

    public static Task StaticFold()
    {
        var registry = new OutletRegistry();
        var book = new Table<Level, long>("FOLD", 256);
        var s = NewOutlet(registry);
        var tap = s.Tap(book, activate: true);
        var c = new RefClient();
        var scratch = new byte[1 << 20];
        var reference = new Dictionary<Level, long>();
        var rnd = new Random(7);

        for (var step = 0; step < 20_000; step++)
        {
            using (var u = book.BeginUpdate())
            {
                for (var i = rnd.Next(1, 4); i > 0; i--)
                {
                    var level = new Level((byte)rnd.Next(2), 2000 + rnd.Next(40));
                    reference.TryGetValue(level, out var q);
                    var delta = q > 0 && rnd.Next(3) == 0 ? -q : rnd.Next(-3, 10); // sometimes exactly cancel → delete
                    u.Fold<long, Add<long>>(level, delta);
                    if (q + delta == 0) reference.Remove(level);
                    else reference[level] = q + delta;
                }

                u.Complete();
            }

            if (rnd.Next(5) == 0) Drain(s, c, scratch);
        }

        Drain(s, c, scratch);
        AssertMirror(c.Tables[tap.Id], book, "fold");
        Assert(book.Count == reference.Count && reference.All(kv => book.TryGet(kv.Key, out var q) && q == kv.Value),
            "fold result differs from reference");

        // Folding into an absent key that ends at zero must not leave a slot (or a log entry) behind.
        var version = book.Version;
        using (var u = book.BeginUpdate())
        {
            u.Fold<long, Add<long>>(new Level(9, 9), 0);
            u.Complete();
        }

        Assert(book.Version == version && !book.TryGet(new Level(9, 9), out _), "no-op fold must not change the table");
        return Task.CompletedTask;
    }

    public static Task BatchFold()
    {
        var a = new Table<Level, long>("A");
        var b = new Table<Level, long>("B");
        var fold = new BytesAdd();
        var rnd = new Random(8);
        var keys = new Level[16];
        var deltas = new long[16];
        for (var step = 0; step < 5000; step++)
        {
            var n = rnd.Next(1, keys.Length);
            for (var i = 0; i < n; i++)
            {
                keys[i] = new Level((byte)rnd.Next(2), 3000 + rnd.Next(12)); // few prices → frequent repeats in a batch
                deltas[i] = rnd.Next(-5, 8);
            }

            using (var u = a.BeginUpdate())
            {
                for (var i = 0; i < n; i++) u.Fold<long, Add<long>>(keys[i], deltas[i]);
                u.Complete();
            }

            using (var u = b.BeginUpdate())
            {
                u.Fold(keys.AsSpan(0, n), deltas.AsSpan(0, n), fold);
                u.Complete();
            }
        }

        var ca = Canonical(a);
        var cb = Canonical(b);
        Assert(
            a.Checksum == b.Checksum && ca.Count == cb.Count && ca.All(kv =>
                cb.TryGetValue(kv.Key, out var r) && r.Row.AsSpan().SequenceEqual(kv.Value.Row)),
            "batch fold must equal the static fold (including repeated keys within a batch)");
        Assert(fold.Calls == 5000, $"one call per batch expected, got {fold.Calls}");

        return Task.CompletedTask;
    }

    // ─────────────────────────────── transactions & faults ───────────────────────────────

    private static bool SameRows(Dictionary<string, (byte[] Key, byte[] Row)> a,
        Dictionary<string, (byte[] Key, byte[] Row)> b)
    {
        return a.Count == b.Count &&
               a.All(kv => b.TryGetValue(kv.Key, out var r) && r.Row.AsSpan().SequenceEqual(kv.Value.Row));
    }

    private static bool Threw<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }

    public static Task Transactions()
    {
        var registry = new OutletRegistry();
        var book = new Table<Level, long>("TX", 256);
        var s = NewOutlet(registry);
        var tap = s.Tap(book, activate: true);
        var c = new RefClient();
        var scratch = new byte[1 << 20];
        var reference = new Dictionary<Level, long>();
        var rnd = new Random(11);
        int abandoned = 0, noOps = 0;

        for (var step = 0; step < 20_000; step++)
        {
            var abandon = rnd.Next(3) == 0;
            var pending = new Dictionary<Level, long>(reference);
            var version = book.Version;
            using (var u = book.BeginUpdate())
            {
                for (var i = rnd.Next(1, 6); i > 0; i--)
                {
                    var level = new Level((byte)rnd.Next(2),
                        100 + rnd.Next(30)); // few keys, small values → frequent net no-ops
                    switch (rnd.Next(4))
                    {
                        case 0:
                            u.Delete(level);
                            pending.Remove(level);
                            break;
                        case 1:
                            long d = rnd.Next(-1, 2);
                            u.Fold<long, Add<long>>(level, d);
                            var q = pending.GetValueOrDefault(level) + d;
                            if (q == 0) pending.Remove(level);
                            else pending[level] = q;
                            break;
                        default:
                            long v = rnd.Next(1, 3);
                            u.Set(level, v);
                            pending[level] = v;
                            break;
                    }
                }

                if (abandon) abandoned++;
                else u.Complete();
            }

            var changed = !abandon && !(pending.Count == reference.Count &&
                                        pending.All(kv => reference.TryGetValue(kv.Key, out var r) && r == kv.Value));
            if (!abandon) reference = pending;
            if (!abandon && !changed) noOps++;
            Assert(book.Version == version + (changed ? 1 : 0),
                $"step {step}: version must advance exactly when rows changed");
            if (rnd.Next(3) == 0) Drain(s, c, scratch);
        }

        Drain(s, c, scratch);
        AssertMirror(c.Tables[tap.Id], book, "transactions");
        Assert(book.Count == reference.Count && reference.All(kv => book.TryGet(kv.Key, out var q) && q == kv.Value),
            "table differs from the reference");
        Notes.Add(
            $"transactions: {abandoned} abandoned, {noOps} net no-ops (unpublished), {book.Version} published of 20000");

        // API guards: one open update per table; an updater dies with its update.
        using (var u = book.BeginUpdate())
        {
            Assert(Threw<InvalidOperationException>(() => book.BeginUpdate()), "nested update must be refused");
            u.Set(new Level(9, 9), 9);
            u.Complete();
            bool refused;
            try
            {
                u.Set(new Level(9, 9), 8);
                refused = false;
            }
            catch (InvalidOperationException)
            {
                refused = true;
            }

            Assert(refused && book.TryGet(new Level(9, 9), out var nine) && nine == 9,
                "a completed updater must refuse changes");
        }

        return Task.CompletedTask;
    }

    public static Task Preconditions()
    {
        var book = new Table<Level, long>("PRE");
        var provider = new Table<Level, long>("PROVIDER"); // the upstream's own copy: supplies the expected checksum
        for (long v = 0; v < 3; v++)
        {
            using (var p = provider.BeginUpdate())
            {
                p.Set(new Level(0, 100 + v), 10 + v);
                p.Complete();
            }

            using (var u = book.BeginUpdate(v))
            {
                u.Set(new Level(0, 100 + v), 10 + v);
                u.Complete(v + 1, provider.Checksum);
            }
        }

        Assert(
            book.UpstreamVersion == 3 && book.Version == 3 && book.Checksum == provider.Checksum && book.Fault is null,
            "in-order updates apply");

        // Postcondition: the state after the update must have the provider's checksum.
        var good = Canonical(book);
        var checksum = book.Checksum;
        var version = book.Version;
        try
        {
            using var u = book.BeginUpdate(3);
            u.Set(new Level(1, 200), 5);
            u.Complete(4, 0xBAD);
            throw new Exception("expected a checksum fault");
        }
        catch (StreamFaultException e)
        {
            Assert(e.Fault is { Code: FaultCode.ChecksumMismatch } f && f.Version == version, "checksum fault");
        }

        Assert(book.Fault?.Code == FaultCode.ChecksumMismatch, "table faulted");
        Assert(
            book.Version == version && book.UpstreamVersion == 3 && book.Checksum == checksum &&
            SameRows(Canonical(book), good), "rolled back to the last good version");
        Assert(Threw<StreamFaultException>(() => book.BeginUpdate()), "a faulted table refuses updates");

        // Recovery from an upstream snapshot; the upstream numbering may jump (or restart).
        using (var u = book.BeginRecovery())
        {
            u.Clear();
            u.Set(new Level(0, 1), 1);
            u.Complete(50);
        }

        Assert(book.Fault is null && book.UpstreamVersion == 50 && book.Version == version + 1 && book.Count == 1,
            "recovered");

        // Precondition: an update for another upstream version (a gap) faults without applying anything.
        try
        {
            using var u = book.BeginUpdate(52);
            throw new Exception("expected a version fault");
        }
        catch (StreamFaultException e)
        {
            Assert(e.Fault is { Code: FaultCode.VersionMismatch }, "version fault");
        }

        Assert(book.Fault?.Code == FaultCode.VersionMismatch && book.Version == version + 1 && book.Count == 1,
            "nothing applied");

        // A failed recovery leaves the table faulted (first fault wins) and unchanged.
        try
        {
            using var u = book.BeginRecovery();
            u.Set(new Level(0, 2), 2);
            u.Complete(expectedChecksum: 1);
        }
        catch (StreamFaultException)
        {
        }

        Assert(book.Fault?.Code == FaultCode.VersionMismatch && book.Count == 1, "failed recovery changes nothing");
        using (var u = book.BeginRecovery())
        {
            u.Set(new Level(0, 2), 2);
            u.Complete(1);
        } // upstream restarted at 1

        Assert(book.Fault is null && book.UpstreamVersion == 1 && book.Version == version + 2,
            "upstream numbering restarted; wire version still advances");

        // Fail() from inside an open update: it rolls back and the fault applies when it ends.
        try
        {
            using var u = book.BeginUpdate();
            u.Set(new Level(0, 3), 3);
            book.Fail(FaultCode.SourceFailed, "upstream disconnected");
            u.Complete();
        }
        catch (StreamFaultException e)
        {
            Assert(e.Fault.Code == FaultCode.SourceFailed, "Complete reports the pending fault");
        }

        Assert(
            book.Fault?.Code == FaultCode.SourceFailed && !book.TryGet(new Level(0, 3), out _) &&
            book.Version == version + 2, "rolled back");
        return Task.CompletedTask;
    }

    public static Task FoldFaults()
    {
        var a = new Table<Level, long>("STATIC");
        using (var u = a.BeginUpdate())
        {
            u.Set(new Level(0, 1), 1);
            u.Complete();
        }

        var good = Canonical(a);
        var version = a.Version;
        try
        {
            using var u = a.BeginUpdate();
            u.Set(new Level(0, 1), 99); // changes earlier in the same update
            u.Fold<long, Explode>(new Level(0, 2), 5); // and new keys
            try
            {
                u.Fold<long, Explode>(new Level(0, 3), 666);
            }
            catch (StreamFaultException)
            {
            } // swallowed by the writer...

            u.Set(new Level(0, 4), 4); // ...but the update refuses to go on
            throw new Exception("expected refusal");
        }
        catch (StreamFaultException e)
        {
            Assert(e.Fault.Code == FaultCode.FoldFailed && e.InnerException is OverflowException,
                "fold fault carries the cause");
        }

        Assert(
            a.Fault?.Code == FaultCode.FoldFailed && a.Version == version && a.Count == 1 &&
            SameRows(Canonical(a), good), "whole update rolled back");

        var b = new Table<Level, long>("BATCH");
        var fold = new BytesAdd();
        using (var u = b.BeginUpdate())
        {
            u.Fold([new Level(0, 3000)], [5L], fold);
            u.Complete();
        }

        var checksum = b.Checksum;
        version = b.Version;
        fold.Throw = true; // e.g. a sandboxed module out of fuel
        try
        {
            using var u = b.BeginUpdate();
            u.Set(new Level(0, 1), 1);
            u.Fold([new Level(0, 3000), new Level(1, 99_999)], [5L, 5L], fold);
            u.Complete();
        }
        catch (StreamFaultException e)
        {
            Assert(e.Fault.Code == FaultCode.FoldFailed && e.InnerException is InvalidOperationException,
                "batch fold fault");
        }

        Assert(b.Fault?.Code == FaultCode.FoldFailed && b.Checksum == checksum && b.Version == version && b.Count == 1,
            "batch fault rolled back");
        Assert(!b.TryGet(new Level(1, 99_999), out _) && !b.TryGet(new Level(0, 1), out _), "no stray rows or slots");
        fold.Throw = false;
        using (var u = b.BeginRecovery())
        {
            u.Fold([new Level(1, 99_999)], [5L], fold);
            u.Complete();
        }

        Assert(b.Fault is null && b.TryGet(new Level(1, 99_999), out var q) && q == 5, "usable after recovery");
        return Task.CompletedTask;
    }

    private static void FillBook(Table<Level, long> book, int levels, long upstream = 0)
    {
        using var u = book.BeginUpdate();
        for (var i = 1; i <= levels; i++)
        {
            u.Set(new Level(0, 100 - i), i);
            u.Set(new Level(1, 100 + i), i);
        }

        u.Complete(upstream);
    }

    public static async Task ErrorPolicies()
    {
        var registry = new OutletRegistry();
        var s = NewOutlet(registry);
        var book = new Table<Level, long>("EP");
        var marks = new LastValues<int>("m");
        FillBook(book, 10, 1);
        marks.Publish(1, [42]);
        var policy = new ErrorPolicy("feed interrupted"u8.ToArray(), TopOfBook);
        var degraded = s.Tap(book, "book", policy, true);
        var plain = s.Tap(book, "book", activate: true); // no policy: error only
        var late = s.Tap(book, "book", policy); // bound, activated after the fault
        var mark = s.Tap(marks[1], "mark", new ErrorPolicy("stale"u8.ToArray(), TopOfBook),
            true); // degrade n/a for values
        var c = new RefClient();
        var scratch = new byte[1 << 20];
        Drain(s, c, scratch);
        var good = book.Version;

        try
        {
            using var u = book.BeginUpdate(7);
        }
        catch (StreamFaultException)
        {
        } // upstream gap

        marks[1].Fail(FaultCode.SourceFailed, "feed down");
        Assert(s.IsReady, "a fault is work for active taps");
        var frames = c.Frames;
        Drain(s, c, scratch);
        Assert(
            c.Frames == frames + 1 && c.LastFrameRecords.Count(r => r == Wire.Error) == 3 &&
            c.LastFrameRecords.Count(r => r == Wire.Table) == 1,
            "one frame: a degraded snapshot and three errors");
        var ti = c.LastFrameRecords.IndexOf(Wire.Table);
        Assert(c.LastFrameRecords[ti + 1] == Wire.Error, "the degraded snapshot is immediately followed by its error");

        var dm = c.Tables[degraded.Id];
        Assert(dm.Degraded && dm.Failures == 0 && dm.Seq == good,
            "degraded snapshot verifies (checksum over degraded rows) at the last good version");
        Assert(dm.Rows.Count == 2 && dm.Rows.Values.All(r => BitConverter.ToInt64(r.Row) == 0)
                                  && dm.Rows.ContainsKey(Convert.ToHexString(Bytes(new Level(0, 99)))) &&
                                  dm.Rows.ContainsKey(Convert.ToHexString(Bytes(new Level(1, 101)))),
            "degraded = best bid and ask with size zero");
        Assert(!c.Tables[plain.Id].Degraded && c.Tables[plain.Id].Rows.Count == 20,
            "no policy: the replica keeps the last good state");

        var errors = c.Errors.ToDictionary(e => e.TapId);
        Assert(
            errors[degraded.Id] is { Code: FaultCode.VersionMismatch } e1 && e1.Version == good &&
            e1.Payload.AsSpan().SequenceEqual("feed interrupted"u8), "book error + payload");
        Assert(errors[plain.Id] is { Code: FaultCode.VersionMismatch } e2 && e2.Payload.Length == 0,
            "default policy: empty payload");
        Assert(
            errors[mark.Id] is { Code: FaultCode.SourceFailed } e3 && e3.Version == 1 &&
            e3.Payload.AsSpan().SequenceEqual("stale"u8), "value error + payload");
        Assert(!c.Values.ContainsKey(mark.Id) || c.Values[mark.Id][0] == 42, "values have no degraded form");

        Assert((await degraded.Completion)?.Code == FaultCode.VersionMismatch && (await plain.Completion)?.Code ==
                                                                              FaultCode.VersionMismatch
                                                                              && (await mark.Completion)?.Code ==
                                                                              FaultCode.SourceFailed,
            "Completion yields the fault");
        Assert(degraded.IsClosed && s.Taps.SequenceEqual([late]), "faulted taps end");
        Assert(!s.IsReady && s.Build(scratch) == 0, "nothing more to send");

        s.Activate(late.Id); // activation on a faulted source
        Drain(s, c, scratch);
        Assert(
            c.Tables[late.Id].Degraded && c.Errors.Count == 4 &&
            (await late.Completion)?.Code == FaultCode.VersionMismatch, "errors immediately");

        using (var u = book.BeginRecovery()) // upstream snapshot
        {
            u.Clear();
            u.Set(new Level(0, 98), 7);
            u.Set(new Level(1, 102), 7);
            u.Complete(100);
        }

        marks.Publish(1, [43]); // a whole value recovers a last-value source
        var again = s.Tap(book, "book", policy, true); // the client resubscribes
        var markAgain = s.Tap(marks[1], "mark", activate: true);
        Drain(s, c, scratch);
        Assert(!c.Tables[again.Id].Degraded && c.Tables[again.Id].Snapshots == 1, "normal snapshot after recovery");
        AssertMirror(c.Tables[again.Id], book, "resubscribed");
        Assert(c.Values[markAgain.Id][0] == 43, "value resubscribed");
        again.Dispose();
        Assert(await again.Completion is null, "a normal close completes with null");
    }

    public static Task ErrorFallbacks()
    {
        var registry = new OutletRegistry();
        var s = NewOutlet(registry, o =>
        {
            o.MaxFrame = 70_000;
            o.FrameBudget = 4096;
        });
        var huge = new Table<Level, long>("HUGE");
        var medium = new Table<Level, long>("MEDIUM");
        var small = new Table<Level, long>("SMALL");
        FillBook(huge, 3000); // 6000 rows ≈ 108 KB > MaxFrame
        FillBook(medium, 1000); // 2000 rows ≈ 36 KB: needs a frame of its own
        FillBook(small, 5);
        huge.Fail(FaultCode.SourceFailed);
        medium.Fail(FaultCode.SourceFailed);
        small.Fail(FaultCode.SourceFailed);
        var c = new RefClient();
        var scratch = new byte[70_000]; // = MaxFrame, as TryBuildFrame uses

        var tooBig = s.Tap(huge, errorPolicy: new ErrorPolicy(degrade: new KeepAll()), activate: true);
        Drain(s, c, scratch);
        Assert(!c.Tables.ContainsKey(tooBig.Id) && c.Errors.Single().TapId == tooBig.Id && tooBig.IsClosed,
            "too big even for an empty frame → error only");

        var throws = s.Tap(small, errorPolicy: new ErrorPolicy(degrade: new Throwing()), activate: true);
        Drain(s, c, scratch);
        Assert(
            c.Tables[throws.Id] is { Degraded: true, Failures: 0 } m && m.Rows.Count == 0 &&
            c.Errors.Last().TapId == throws.Id, "throwing degrade → empty degraded snapshot");

        var next = s.Tap(medium, errorPolicy: new ErrorPolicy(degrade: new KeepAll()), activate: true);
        Poster(s, Lane.High).TryPost(new byte[1000]); // the fault isn't first in the frame
        c.Decode(scratch.AsSpan(0, s.Build(scratch)));
        Assert(c.LastFrameRecords.SequenceEqual([Wire.TopicMessage]) && !next.IsClosed,
            "doesn't fit behind other records → waits");
        c.Decode(scratch.AsSpan(0, s.Build(scratch)));
        Assert(
            c.LastFrameRecords.SequenceEqual([Wire.Table, Wire.Error]) &&
            c.Tables[next.Id] is { Degraded: true, Failures: 0 } mm && mm.Rows.Count == 2000 && next.IsClosed,
            "…and gets the next frame whole");
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Test connections tap the real sources through the same pump as production ones; while their shared injector is
    ///     failed every source — tables, values, watches — looks faulted to them only (degraded + error, the tap ends; taps
    ///     activated meanwhile fail at once; FaultOf reports it) while the sources and a production connection carry on
    ///     untouched. After Recover new taps stream normally; the sources' own faults reach everyone.
    /// </summary>
    public static async Task FaultInjection()
    {
        var registry = new OutletRegistry();
        var book = new Table<Level, long>("FI");
        var marks = new LastValues<int>("fi");
        FillBook(book, 10, 1);
        marks.Publish(1, [7]);
        var policy = new ErrorPolicy("feed interrupted"u8.ToArray(), TopOfBook);
        var scratch = new byte[1 << 20];

        void Move()
        {
            using var u = book.BeginUpdate();
            u.Set(new Level(0, 50 - book.Version % 40), 5);
            u.Complete();
        }

        var injector = new FaultInjector();
        var prod = NewOutlet(registry);
        var prodTap = prod.Tap(book, "book", policy, true);
        var pc = new RefClient();
        var tests = Enumerable.Range(0, 2).Select(_ => NewOutlet(registry)).ToArray();
        foreach (var o in tests) o.JoinFaultInjector(injector);
        var taps = tests.Select(o => (Book: o.Tap(book, "book", policy, true),
            Mark: o.Tap(marks[1], "mark", activate: true),
            Watch: o.Watch("w", WatchMember.Of(book, TopOfBook), WatchMember.Of(marks[1])))).ToArray();
        foreach (var t in taps) t.Watch.Activate();
        var tc = tests.Select(_ => new RefClient()).ToArray();
        Drain(prod, pc, scratch);
        for (var i = 0; i < 2; i++) Drain(tests[i], tc[i], scratch);
        Assert(Enumerable.Range(0, 2).All(i => tc[i].Tables[taps[i].Book.Id].Rows.Count == 20 &&
                                               tc[i].Values[taps[i].Mark.Id][0] == 7
                                               && tc[i].Groups[taps[i].Watch.Id].Tables[0].Rows.Count == 20),
            "test connections stream the real sources");
        Assert(prod.FaultInjector is null && tests.All(o => o.FaultInjector == injector),
            "only test connections joined");

        injector.Fail(FaultCode.SourceFailed, "simulated");
        var failedAt = book.Version;
        Assert(book.Fault is null && marks[1].Fault is null && prod.FaultOf(book) is null &&
               tests[0].FaultOf(book)?.Code == FaultCode.SourceFailed
               && tests[0].FaultOf(book)!.Version == failedAt,
            "the sources aren't faulted; to test connections they look it");

        Drain(prod, pc, scratch);
        Assert(pc.Errors.Count == 0 && prodTap.IsActive, "production connection: nothing to do with it");
        for (var i = 0; i < 2; i++)
        {
            Drain(tests[i], tc[i], scratch);
            var m = tc[i].Tables[taps[i].Book.Id];
            Assert(
                m.Degraded && m.Failures == 0 && m.Rows.Count == 2 &&
                m.Rows.Values.All(r => BitConverter.ToInt64(r.Row) == 0),
                $"test connection {i}: degraded top of book, verified");
            var g = tc[i].Groups[taps[i].Watch.Id];
            Assert(g.Degraded && g.Tables[0].Rows.Count == 2 && g.Failures == 0,
                $"test connection {i}: the watch's degraded member");
            var errors = tc[i].Errors.ToDictionary(e => e.TapId);
            Assert(errors[taps[i].Book.Id] is { Code: FaultCode.SourceFailed } e && e.Version == failedAt &&
                   e.Payload.AsSpan().SequenceEqual("feed interrupted"u8)
                   && errors.ContainsKey(taps[i].Mark.Id) && errors.ContainsKey(taps[i].Watch.Id),
                $"test connection {i}: every tap gets its error");
            Assert(
                (await taps[i].Book.Completion)?.Code == FaultCode.SourceFailed && taps[i].Mark.IsClosed &&
                taps[i].Watch.IsClosed, $"test connection {i}: taps end");
        }

        Move(); // the source carries on throughout
        Drain(prod, pc, scratch);
        Assert(pc.Errors.Count == 0 && pc.Tables[prodTap.Id].Rows.Count == 21 && pc.Tables[prodTap.Id].Failures == 0,
            "production keeps streaming");
        var late = tests[0].Tap(book, "book", policy, true); // resubscribing during the outage
        Drain(tests[0], tc[0], scratch);
        Assert(late.IsClosed && tc[0].Errors.Count == 4, "fails at once while failed");

        injector.Recover();
        Assert(tests[0].FaultOf(book) is null, "recovered");
        var again = tests[0].Tap(book, "book", policy, true);
        Drain(tests[0], tc[0], scratch);
        Assert(again.IsActive && !tc[0].Tables[again.Id].Degraded, "after Recover: a normal snapshot");
        AssertMirror(tc[0].Tables[again.Id], book, "test connection after recovery");
        Move();
        Drain(tests[0], tc[0], scratch);
        AssertMirror(tc[0].Tables[again.Id], book, "test connection deltas");

        try
        {
            using var u = book.BeginUpdate(999);
        }
        catch (StreamFaultException)
        {
        } // a real fault

        Drain(prod, pc, scratch);
        Drain(tests[0], tc[0], scratch);
        Assert(
            tc[0].Errors.Last().TapId == again.Id && tc[0].Errors.Last().Code == FaultCode.VersionMismatch &&
            pc.Errors.Single().Code == FaultCode.VersionMismatch,
            "the sources' own faults reach production and test connections alike");

        tests[1].Dispose();
        injector.Fail(FaultCode.SourceFailed); // a disposed outlet left the injector
        Assert(Threw<InvalidOperationException>(() => tests[0].JoinFaultInjector(new FaultInjector())),
            "an outlet joins once");
    }

    /// <summary>
    ///     A delete is its key alone on the wire: a delta's header says how many it carries, each entry is its op and key (an
    ///     upsert's row after), and the record is as long as the header says. Its size is measured exactly before it's
    ///     written, however it's written — a walk of the log, a pass over the slots (a snapshot once that's no more bytes),
    ///     a key range (no delete for a row never sent that subscriber) — and the client's replica rebuilds the table.
    /// </summary>
    public static Task KeyOnlyDeletes()
    {
        const int upsert = 1 + sizeof(long) + 16, delete = 1 + sizeof(long); // (op, key and a Guid's row; op and key)
        var buffer = new byte[1 << 16];

        // The record: as long as its header says, its deletes those it counts — each its key alone.
        int Deletes(ReadOnlySpan<byte> record, string what)
        {
            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[26..]),
                deletes = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[34..]), seen = 0, at = Wire.TableHeader;
            for (var i = 0; i < count; i++)
                if (record[at] == Wire.OpDelete) (seen, at) = (seen + 1, at + delete);
                else at += upsert;
            Assert(seen == deletes && at == record.Length && Tapline.Client.TableReplica.LengthOf(record) == record.Length,
                $"{what}: {deletes} deletes said, {seen} sent; {record.Length} B, the entries {at} B");
            return deletes;
        }

        var table = new Table<long, Guid>("KD", 1 << 10);
        void Change(Action<Table<long, Guid>.Updater> change)
        {
            using var u = table.BeginUpdate();
            change(u);
            u.Complete();
        }

        for (long k = 0; k < 100; k++) Change(u => u.Set(k, Guid.CreateVersion7()));
        var replica = new Tapline.Client.TableReplica();
        var cursor = Cursor.Snapshot;
        (int Bytes, int Deletes, bool Reset) Send(string what)
        {
            var measured = table.Measure(cursor);
            var n = table.Write(7, ref cursor, buffer);
            Assert(n == measured, $"{what}: measured {measured} B, written {n} B");
            var record = buffer.AsSpan(0, n);
            var deletes = Deletes(record, what);
            Assert(replica.Apply(record) && replica.Checksum == table.Checksum && replica.Count == table.Count,
                $"{what}: the replica rebuilt ({replica.Count} rows of {table.Count})");
            return (n - Wire.TableHeader, deletes, (record[5] & Wire.FlagReset) != 0);
        }

        Assert(Send("the snapshot") == (100 * upsert, 0, true), "the snapshot: its rows");

        // Close behind (a walk of the log): ten go, five come, three change; a row comes and goes (never had: no delete).
        for (long k = 0; k < 10; k++) Change(u => u.Delete(k));
        for (long k = 100; k < 105; k++) Change(u => u.Set(k, Guid.CreateVersion7()));
        for (long k = 20; k < 23; k++) Change(u => u.Set(k, Guid.CreateVersion7()));
        Change(u => u.Set(500L, Guid.CreateVersion7()));
        Change(u => u.Delete(500L));
        var close = Send("close behind");
        Assert(close == (10 * delete + 8 * upsert, 10, false),
            $"close behind: ten deletes, {delete} B each, and eight rows: {close}");

        // Far behind (a pass over the slots): a row changed often, 30 go.
        for (var i = 0; i < 600; i++) Change(u => u.Set(50L, Guid.CreateVersion7()));
        for (long k = 60; k < 90; k++) Change(u => u.Delete(k));
        var far = Send("far behind");
        Assert(far == (30 * delete + upsert, 30, false), $"far behind: thirty deletes and the row changed: {far}");

        // Far behind again, every row changed: no fewer bytes than a snapshot, so a snapshot — its rows alone.
        for (var i = 0; i < 600; i++) Change(u => u.Set(51L, Guid.CreateVersion7()));
        var live = table.Count;
        for (long k = 0; k < 105; k++)
            if (k is < 10 or (>= 60 and < 90)) continue;
            else Change(u => u.Set(k, Guid.CreateVersion7()));
        Change(u => u.Delete(104L));
        var whole = Send("every row changed");
        Assert(whole == ((live - 1) * upsert, 0, true), $"every row changed: a snapshot of {live - 1} rows: {whole}");

        // A key range (a watch member's): no delete for a row never sent it; its size measured exactly, with the state.
        var ranged = new Table<long, Guid>("KR", 1 << 10);
        void ChangeRange(Action<Table<long, Guid>.Updater> change)
        {
            using var u = ranged.BeginUpdate();
            change(u);
            u.Complete();
        }

        for (long i = 0; i < 20; i++)
            ChangeRange(u =>
            {
                u.Set(i << 8 | 1, Guid.CreateVersion7()); // (in range: its first byte 1)
                u.Set(i << 8, Guid.CreateVersion7());
            });
        byte[] prefix = [1];
        var state = new RangeState();
        var at = Cursor.Snapshot;
        (int Bytes, int Deletes) SendRange(string what)
        {
            var measured = ranged.MeasureRange(at, prefix, state);
            var n = ranged.WriteRange(9, ref at, buffer, prefix, state);
            Assert(n == measured, $"{what}: measured {measured} B, written {n} B");
            return n == 0 ? (0, 0) : (n - Wire.TableHeader, Deletes(buffer.AsSpan(0, n), what));
        }

        Assert(SendRange("the range's snapshot") == (20 * upsert, 0), "the range's snapshot: its twenty rows");
        for (long i = 0; i < 3; i++) ChangeRange(u => u.Delete(i << 8 | 1));
        for (long i = 3; i < 6; i++) ChangeRange(u => u.Delete(i << 8));
        ChangeRange(u => u.Set(99L << 8 | 1, Guid.CreateVersion7()));
        ChangeRange(u => u.Delete(99L << 8 | 1));
        var ranges = SendRange("the range behind");
        Assert(ranges == (3 * delete, 3), $"the range behind: three deletes in range, none for the row never sent it: {ranges}");
        ChangeRange(u => u.Delete(7L << 8));
        Assert(SendRange("only out of range") == (0, 0), "only out of range: no record");
        return Task.CompletedTask;
    }

    /// <summary>
    ///     A slow client far behind (well past the point where walking the log costs more than a pass over the slots) gets a
    ///     delta of just the rows changed since its cursor — hot levels, deletions, re-created and brand-new keys in recycled
    ///     slots — not a snapshot of the whole table, and reconstructs exactly; a whole-table watch (sized by Measure first)
    ///     agrees; once as many rows changed as are live, it gets the snapshot.
    /// </summary>
    public static Task UnheldDeletes()
    {
        var registry = new OutletRegistry();
        var table = new Table<long, long>("UD", 1 << 13);
        void Change(Action<Table<long, long>.Updater> change)
        {
            using var u = table.BeginUpdate();
            change(u);
            u.Complete();
        }

        for (long k = 0; k < 100; k++) Change(u => u.Set(k, k));
        var s = NewOutlet(registry);
        var tap = s.Tap(table, activate: true);
        var watch = s.Watch("w", WatchMember.Of(table));
        watch.Activate();
        var c = new RefClient();
        var scratch = new byte[1 << 20];
        Drain(s, c, scratch);

        // Behind, close (its delta a walk of the log): 200 rows come and go; ten it holds go; five new stay; one it holds
        // goes, comes back and goes again; and one it never held does the same.
        void Behind(long born)
        {
            for (var k = born; k < born + 200; k++) Change(u => u.Set(k, 1));
            for (var k = born; k < born + 200; k++) Change(u => u.Delete(k));
        }

        Behind(1_000);
        for (long k = 0; k < 10; k++) Change(u => u.Delete(k));
        for (long k = 2_000; k < 2_005; k++) Change(u => u.Set(k, 2));
        Change(u => u.Delete(50L));
        Change(u => u.Set(50L, 5));
        Change(u => u.Delete(50L));
        Change(u => u.Set(3_000L, 3));
        Change(u => u.Delete(3_000L));
        Change(u => u.Set(3_000L, 3));
        Change(u => u.Delete(3_000L));
        Drain(s, c, scratch);
        foreach (var (mirror, what) in new[] { (c.Tables[tap.Id], "tap"), (c.Groups[watch.Id].Tables[0], "watch") })
        {
            AssertMirror(mirror, table, $"close behind, its {what}");
            Assert(mirror is { Failures: 0, Deletes: 12, UnheldDeletes: 1 },
                $"close behind, its {what}: a delete for each row it held that went (eleven) — none for the 200 that came and " +
                $"went, but one for the key it never held that went and came back (kept: conservative) ({mirror.Deletes} deletes, " +
                $"{mirror.UnheldDeletes} of keys it didn't hold)");
        }

        // Behind, far (its delta a pass over the slots): many changes to a row it holds, 200 rows coming and going.
        var (deletes, unheld) = (c.Tables[tap.Id].Deletes, c.Tables[tap.Id].UnheldDeletes);
        for (var i = 0; i < 3_000; i++) Change(u => u.Set(99L, i));
        Behind(5_000);
        Change(u => u.Delete(98L));
        Drain(s, c, scratch);
        foreach (var (mirror, what) in new[] { (c.Tables[tap.Id], "tap"), (c.Groups[watch.Id].Tables[0], "watch") })
        {
            AssertMirror(mirror, table, $"far behind, its {what}");
            Assert(mirror.Failures == 0 && mirror.Deletes - deletes == 1 && mirror.UnheldDeletes == unheld,
                $"far behind, its {what}: the one it held that went — none for the 200 ({mirror.Deletes - deletes} deletes)");
        }

        return Task.CompletedTask;
    }

    public static Task SlowClientScanDelta()
    {
        var registry = new OutletRegistry();
        var book = new Table<Level, long>("SD", 1 << 13); // a short log: deletes recycle within the run
        using (var u = book.BeginUpdate())
        {
            for (var i = 0; i < 200; i++)
            {
                u.Set(new Level(0, 1000 - i), 1 + i);
                u.Set(new Level(1, 1001 + i), 1 + i);
            }

            u.Complete();
        }

        var s = NewOutlet(registry);
        var tap = s.Tap(book, activate: true);
        var watch = s.Watch("w", WatchMember.Of(book));
        watch.Activate();
        var c = new RefClient();
        var scratch = new byte[1 << 20];
        Drain(s, c, scratch);
        var rnd = new Random(9);
        long fresh = 0;
        var bytesDeltas = 0;
        for (var round = 0; round < 50; round++)
        {
            for (var k = 0; k < 4000; k++) // > 4 × slots + 64 (≤ 2664) log entries between frames
            {
                using var u = book.BeginUpdate();
                var side = (byte)rnd.Next(2);
                var hot = new Level(side, side == 0 ? 1000 - rnd.Next(5) : 1001 + rnd.Next(5));
                if (rnd.Next(20) == 0) u.Delete(hot); // a hot level goes (and comes back below)
                else u.Fold<long, Add<long>>(hot, rnd.Next(1, 50));
                if (k % 800 == 0) // a deep level is replaced by a new key
                {
                    u.Delete(new Level(0, 801 + round * 5 + k / 800));
                    u.Set(new Level(0, -1 - fresh++), 7);
                }

                u.Complete();
            }

            var before = c.Tables[tap.Id].Entries;
            Drain(s, c, scratch);
            bytesDeltas += (c.Tables[tap.Id].Entries - before) * 18;
            AssertMirror(c.Tables[tap.Id], book, $"round {round}");
            AssertMirror(c.Groups[watch.Id].Tables[0], book, $"watch, round {round}");
        }

        var m = c.Tables[tap.Id];
        Assert(m.Failures == 0 && m.Snapshots == 1 && m.Deltas == 50,
            $"deltas, not snapshots: {m.Snapshots} snapshots, {m.Deltas} deltas");
        Assert(bytesDeltas < 50 * 400 * 18 / 5,
            $"each delta a fraction of a snapshot ({bytesDeltas / 50} B of rows vs {book.Count * 18})");

        for (var k = 0; k < 8; k++) // every row changes, many times: the snapshot is no bigger
            using (var u = book.BeginUpdate())
            {
                var rows = new List<KeyValuePair<Level, long>>();
                book.CopyTo(rows);
                foreach (var (key, _) in rows) u.Fold<long, Add<long>>(key, 1);
                u.Complete();
            }

        Drain(s, c, scratch);
        AssertMirror(c.Tables[tap.Id], book, "after a full churn");
        Assert(c.Tables[tap.Id].Snapshots == 2 && c.Groups[watch.Id].Failures == 0,
            "a snapshot once every row changed");
        Notes.Add(
            $"slow client 4000 changes behind on a 400-row book: 50 deltas of {bytesDeltas / 50 / 18} rows avg (a snapshot: {book.Count} rows)");
        return Task.CompletedTask;
    }

    public static Task ErrorRaces()
    {
        // Closing a faulted tap mid-rotation must not skip the tap after it.
        var registry = new OutletRegistry();
        var s = NewOutlet(registry);
        var tables = Enumerable.Range(0, 3).Select(i => new Table<Level, long>($"RR{i}")).ToArray();
        var taps = tables.Select(t => s.Tap(t, activate: true)).ToArray();
        var c = new RefClient();
        var scratch = new byte[1 << 20];
        Drain(s, c, scratch); // empty snapshots A, B, C → rotation back at A
        tables[0].Fail(FaultCode.SourceFailed);
        FillBook(tables[1], 1);
        FillBook(tables[2], 1);
        c.Decode(scratch.AsSpan(0, s.Build(scratch)));
        Assert(c.LastFrameTaps.SequenceEqual([taps[0].Id, taps[1].Id, taps[2].Id]),
            $"rotation after a close: {string.Join(",", c.LastFrameTaps)}");

        // A recovery that completes while the frame builder waits for the table: no degraded snapshot of the recovered
        // state and no error — the tap just streams the recovered state.
        var book = new Table<Level, long>("RACE");
        FillBook(book, 3);
        var tap = s.Tap(book, errorPolicy: new ErrorPolicy(degrade: new KeepAll()), activate: true);
        Drain(s, c, scratch);
        book.Fail(FaultCode.SourceFailed);
        using var inUpdate = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var writer = new Thread(() =>
        {
            using var u = book.BeginRecovery();
            u.Clear();
            u.Set(new Level(0, 42), 42);
            inUpdate.Set();
            release.Wait();
            u.Complete();
        });
        writer.Start();
        inUpdate.Wait();
        var n = 0;
        var builder = new Thread(() => n = s.Build(scratch)); // sees the fault, then blocks on the table's lock
        builder.Start();
        Thread.Sleep(200);
        release.Set();
        writer.Join();
        builder.Join();
        c.Decode(scratch.AsSpan(0, n));
        Drain(s, c, scratch);
        Assert(!tap.IsClosed && c.Errors.All(e => e.TapId != tap.Id) && !c.Tables[tap.Id].Degraded,
            "a cleared fault must not end the tap");
        AssertMirror(c.Tables[tap.Id], book, "recovered mid-build");
        return Task.CompletedTask;
    }

    /// <summary>Review regressions: the degrade function runs once per faulted tap; a tier change applies during a debt.</summary>
    public static Task ReviewRegressions()
    {
        var registry = new OutletRegistry();
        var s = NewOutlet(registry, o => o.FrameBudget = 1024);
        var book = new Table<Level, long>("ONCE");
        FillBook(book, 50); // 100 rows ≈ 1.8 KB undegraded
        var values = new LastValues<int>("once");
        var degrade = new CountingKeepAll();
        var faulted = s.Tap(book, errorPolicy: new ErrorPolicy(degrade: degrade), activate: true);
        var c = new RefClient();
        var scratch = new byte[1 << 20];
        Drain(s, c, scratch);
        book.Fail(FaultCode.SourceFailed);
        var small = Enumerable.Range(0, 50).Select(k => s.Tap(values[k], activate: true)).ToArray();
        for (var k = 0; k < 50; k++) values.Publish(k, [1]);
        Poster(s, Lane.High).TryPost(new byte[700]); // the fault isn't first in the frame
        Drain(s, c, scratch);
        Assert(faulted.IsClosed && c.Tables[faulted.Id].Degraded && small.All(t => c.Values.ContainsKey(t.Id)),
            "all delivered");
        Assert(degrade.Calls == 1, $"degrade ran {degrade.Calls} times; once per faulted tap expected");

        var clock = new ManualTime();
        var wheel = new TimingWheel(TimeSpan.FromMilliseconds(5), time: clock, autoStart: false);
        var r = NewOutlet(new OutletRegistry(wheel), o =>
        {
            o.StreamingBytesPerSecond = 1000;
            o.StreamingBurstBytes = 1000;
        });
        var big = new LastValues<int>("debt");
        r.Tap(big[0], activate: true);
        big.Publish(0, new byte[20_000], Delivery.Inline); // ~19 s of debt at 1000 B/s
        r.Build(scratch);
        big.Publish(0, new byte[10]);
        Assert(!r.IsReady, "in debt");
        r.SetStreamingRate(0);
        Assert(r.IsReady && r.Build(scratch) > 0, "a tier change applies at once, even mid-debt");
        return Task.CompletedTask;
    }

    public static async Task ReviewRegressions2()
    {
        // 1. A writer nesting locks out of canonical order (table update, then a value publish) runs against a watch
        //    over both: the watch must never block while holding a lock, so neither side deadlocks.
        {
            var registry = new OutletRegistry();
            var value = new LastValueSource(); // lower SourceId: first in canonical order
            var table = new Table<int, long>("nest");
            var s = NewOutlet(registry);
            var watch = s.Watch("w", WatchMember.Of(value), WatchMember.Of(table));
            watch.Activate();
            var stop = false;
            var reader = Task.Run(() =>
            {
                var scratch = new byte[1 << 16];
                while (!Volatile.Read(ref stop)) s.Build(scratch);
            });
            var writer = Task.Run(() =>
            {
                for (var i = 0; i < 20_000; i++)
                {
                    using var u = table.BeginUpdate();
                    u.Set(i & 63, i);
                    value.Publish(BitConverter.GetBytes(i)); // out of order: table's lock is held
                    u.Complete();
                }
            });
            var finished = await Task.WhenAny(writer, Task.Delay(TimeSpan.FromSeconds(20))) == writer;
            Volatile.Write(ref stop, true);
            Assert(finished, "deadlock: nested writer vs watch reader");
            await reader;
            await Task.Delay(20);
            s.Heartbeat(); // releases any pending retry
            s.Resync(watch.Id); // a fresh client view: snapshot of the group
            var c = new RefClient();
            Drain(s, c, new byte[1 << 20]);
            Assert(c.Groups[watch.Id].Tables[1].Seq == table.Version && c.Groups[watch.Id].Values[0].Length == 4,
                "the watch caught up");
        }

        // 2. A confirmation for an older handle (here after a resync issued a new one) doesn't release the newer one.
        {
            var registry = new OutletRegistry();
            var book = new Table<Level, long>("tickets");
            FillBook(book, 1500);
            var s = NewOutlet(registry);
            var tap = s.Tap(book, activate: true);
            var c = new RefClient();
            var scratch = new byte[1 << 20];
            Drain(s, c, scratch);
            var first = c.Pending.Single();
            s.Resync(tap.Id);
            Drain(s, c, scratch);
            var second = c.Pending.Last();
            Assert(second.Ticket != first.Ticket, "a new ticket per handle");
            s.Fetched(0, tap.Id, first.Ticket);
            using (var u = book.BeginUpdate())
            {
                u.Set(new Level(0, 1), 1);
                u.Complete();
            }

            Assert(!s.IsReady && s.Build(scratch) == 0,
                "the stale confirmation is ignored: still waiting for the second fetch");
            s.Fetched(0, tap.Id, second.Ticket);
            Assert(s.IsReady, "the right one releases it");
        }

        // 3. A full blob store backs off and retries instead of parking the tap forever.
        {
            var clock = new ManualTime();
            var wheel = new TimingWheel(time: clock, autoStart: false);
            var store = new BlobStore(100_000, TimeSpan.FromSeconds(1), clock);
            var registry = new OutletRegistry(wheel, store);
            var s = NewOutlet(registry);
            var values = new LastValues<int>("full");
            var a = s.Tap(values[0], activate: true);
            var b = s.Tap(values[1], activate: true);
            values.Publish(0, new byte[80_000], Delivery.OutOfBand);
            values.Publish(1, new byte[80_000].Select((_, i) => (byte)i).ToArray(), Delivery.OutOfBand);
            var c = new RefClient();
            var scratch = new byte[1 << 20];
            Drain(s, c, scratch);
            Assert(c.Pending.Count == 1, "only one fits the store");
            c.FetchPending(s, registry);
            clock.Advance(TimeSpan.FromSeconds(2)); // the first blob expires
            wheel.Advance(clock.Now);
            Drain(s, c, scratch);
            Assert(c.Pending.Count == 1 && c.Pending[0].TapId == b.Id && b.IsActive && !b.Source.Version.Equals(-1),
                "the other tap got through on retry");
        }

        // 4. A value over 64 KB can't be part of a group record: the watch faults instead of stalling silently.
        {
            var registry = new OutletRegistry();
            var s = NewOutlet(registry);
            var value = new LastValueSource();
            value.Publish(new byte[70_000]);
            var watch = s.Watch("big value", WatchMember.Of(value));
            watch.Activate();
            var c = new RefClient();
            Drain(s, c, new byte[1 << 20]);
            Assert(
                c.Errors.Single().TapId == watch.Id && watch.IsClosed &&
                (await watch.Completion)?.Code == FaultCode.Unknown, "watch faulted with an error");
        }

        // 5. Posting to a subscription's mailbox once its outlet is disposed fails.
        {
            var s = NewOutlet(new OutletRegistry());
            var t = Poster(s, Lane.Normal);
            s.Dispose();
            Assert(!t.TryPost(new byte[100]), "refused after dispose");
        }

        // 6. A last-value source failing inside a transaction aborts it, like a table would.
        {
            var table = new Table<int, long>("vf");
            var value = new LastValueSource();
            try
            {
                using var tx = Transaction.Begin(table, value);
                tx.Of(table).Set(1, 1);
                value.Fail(FaultCode.SourceFailed, "feed down");
                tx.Complete();
                throw new Exception("expected a fault");
            }
            catch (StreamFaultException e)
            {
                Assert(e.Fault.Code == FaultCode.SourceFailed, "fault");
            }

            Assert(table.Version == 0 && table.Fault is not null && value.Fault is not null, "rolled back and faulted");
        }
    }

    public static Task ClientClose()
    {
        var registry = new OutletRegistry();
        var s = NewOutlet(registry);
        var values = new LastValues<int>("cc");
        var a = s.Tap(values[0], activate: true);
        var b = s.Tap(values[1], activate: true);
        var protocol = new RealtimeProtocol(BlazorPack(), registry);

        var close = EncodeWithJs("Tap.Close", s.Id, s.Nonce, a.Id, (long)FaultCode.ChecksumMismatch);
        var input = new ReadOnlySequence<byte>(close);
        Assert(protocol.TryParseMessage(ref input, new Binder(), out var m) && m is PingMessage && input.IsEmpty,
            "Tap.Close consumed in place");
        Assert(a.IsClosed && a.Completion.Result is { Code: FaultCode.ChecksumMismatch },
            "client rejection → Completion fault");

        input = new ReadOnlySequence<byte>(ControlBytes("Tap.Close", s.Id, s.Nonce ^ 1, b.Id, 0));
        protocol.TryParseMessage(ref input, new Binder(), out _);
        Assert(!b.IsClosed, "wrong nonce ignored");
        input = new ReadOnlySequence<byte>(ControlBytes("Tap.Close", s.Id, s.Nonce, b.Id, 0));
        protocol.TryParseMessage(ref input, new Binder(), out _);
        Assert(b.IsClosed && b.Completion.Result is null && s.Taps.Count == 0, "code 0 = unsubscribe → null");
        return Task.CompletedTask;
    }

    // ─────────────────────────────── out of band ───────────────────────────────

    public static Task OutOfBandTables()
    {
        var registry = new OutletRegistry();
        var book = new Table<Level, long>("OOB");
        FillBook(book, 1500); // 3000 rows ≈ 54 KB > 16 KB threshold
        var outlets = Enumerable.Range(0, 20).Select(_ => NewOutlet(registry)).ToArray();
        var taps = outlets.Select(o => o.Tap(book, activate: true)).ToArray();
        var clients = outlets.Select(_ => new RefClient()).ToArray();
        var scratch = new byte[1 << 20];
        for (var i = 0; i < outlets.Length; i++)
        {
            clients[i].Decode(scratch.AsSpan(0, outlets[i].Build(scratch)));
            Assert(clients[i].LastFrameRecords.SequenceEqual([Wire.OobRef]) && clients[i].Pending.Count == 1,
                "snapshot sent as a 43-byte handle");
        }

        Assert(registry.Blobs.Stats.Stored == 1 && registry.Blobs.Count == 1,
            $"one serialization for 20 snapshots: {registry.Blobs.Stats.Stored}");

        for (var i = 0; i < 50; i++)
            using (var u = book.BeginUpdate())
            {
                u.Set(new Level(0, 1 + i), 7);
                u.Complete();
            }

        Assert(!outlets[0].IsReady && outlets[0].Build(scratch) == 0,
            "a tap waiting for its fetch sends nothing (changes coalesce)");
        var h0 = clients[0].Pending[0];
        Assert(Fetch(registry, h0.Id, outlets[1].Id, h0.Tag) is null,
            "a handle works only for the outlet it was issued to");
        for (var i = 0; i < outlets.Length; i++)
        {
            Assert(clients[i].FetchPending(outlets[i], registry) == 1, "fetched");
            Drain(outlets[i], clients[i], scratch);
            var m = clients[i].Tables[taps[i].Id];
            Assert(m.Snapshots == 1 && m.Deltas == 1, "then one squashed delta, in-band");
            AssertMirror(m, book, $"oob {i}");
        }

        // Larger than a whole frame: out of band rather than parked as oversize.
        var big = new Table<Level, long>("OOB-BIG");
        FillBook(big, 3000); // ≈ 108 KB
        var small = NewOutlet(registry, o =>
        {
            o.MaxFrame = 70_000;
            o.OutOfBandThreshold = 0;
        });
        var bigTap = small.Tap(big, activate: true);
        var c = new RefClient();
        var frame = new byte[70_000];
        c.Decode(frame.AsSpan(0, small.Build(frame)));
        Assert(c.FetchPending(small, registry) == 1, "oversize snapshot went out of band");
        AssertMirror(c.Tables[bigTap.Id], big, "oversize via out of band");

        // Out-of-band bytes count against the client's Streaming budget.
        var limited = NewOutlet(registry, o =>
        {
            o.StreamingBytesPerSecond = 1024;
            o.StreamingBurstBytes = 100_000;
        });
        limited.Tap(book, activate: true);
        limited.Build(scratch);
        Assert(limited.StreamingTokens < 100_000 - 50_000,
            $"budget charged for the blob: {limited.StreamingTokens} left");

        // A client that never confirms its fetch gets a fresh snapshot after FetchTimeout.
        var slow = NewOutlet(registry, o => o.FetchTimeout = TimeSpan.FromMilliseconds(30));
        var slowTap = slow.Tap(book, activate: true);
        var sc = new RefClient();
        Drain(slow, sc, scratch);
        Assert(sc.Pending.Count == 1 && !slow.IsReady, "waiting");
        Thread.Sleep(60);
        slow.Heartbeat();
        Drain(slow, sc, scratch);
        Assert(sc.Pending.Count == 2 && slowTap.IsActive, "timed out: a new snapshot handle");
        return Task.CompletedTask;
    }

    public static Task OutOfBandValues()
    {
        var registry = new OutletRegistry();
        var s = NewOutlet(registry);
        var values = new LastValues<int>("ov");
        var small = s.Tap(values[0], activate: true);
        var large = s.Tap(values[1], activate: true);
        var reference = s.Tap(values[2], activate: true);
        var c = new RefClient();
        var scratch = new byte[1 << 20];
        var big = new byte[200_000];
        big[7] = 7;
        values.Publish(0, [1, 2, 3]);
        values.Publish(1, big); // > 64 KB: out of band
        values.Publish(2, [4, 5], Delivery.Reference); // the app fetches it if it wants
        Drain(s, c, scratch);
        Assert(
            c.Values[small.Id].Length == 3 && c.Pending.Single().TapId == large.Id &&
            c.References[reference.Id].Mode == Wire.OobReference, "inline, transparent, reference");

        var big2 = new byte[200_000];
        big2[7] = 8;
        values.Publish(1, big2);
        values.Publish(2, [6], Delivery.Reference);
        Drain(s, c, scratch);
        Assert(c.Pending.Count == 1, "the transparent tap waits for its fetch");
        var r = c.References[reference.Id];
        Assert(Fetch(registry, r.Id, s.Id, r.Tag) is { } rb && rb.AsSpan().SequenceEqual(new byte[] { 6 }),
            "a newer reference went straight out");

        c.FetchPending(s, registry);
        Assert(c.Values[large.Id][7] == 7, "fetched the older value");
        Drain(s, c, scratch);
        c.FetchPending(s, registry);
        Assert(c.Values[large.Id][7] == 8, "then the latest");
        Assert(Threw<ArgumentOutOfRangeException>(() => values.Publish(3, new byte[70_000], Delivery.Inline)),
            "inline values are at most 64 KB");
        return Task.CompletedTask;
    }

    public static async Task FetchEndpoint()
    {
        var registry = new OutletRegistry();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(registry);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapBlazorRealtime();
        await app.StartAsync();
        var s = NewOutlet(registry);
        var payload = Enumerable.Range(0, 100_000).Select(i => (byte)(i * 7)).ToArray();
        var value = new LastValueSource();
        value.Publish(payload, Delivery.OutOfBand);
        var tap = s.Tap(value, activate: true);
        var c = new RefClient();
        Drain(s, c, new byte[1 << 20]);
        var h = c.Pending.Single();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        var url = $"/_tapline/blob/{h.Id}/{s.Id}/{Convert.ToHexString(h.Tag)}";

        var ok = await http.GetAsync(url);
        Assert(
            ok.StatusCode == HttpStatusCode.OK &&
            (await ok.Content.ReadAsByteArrayAsync()).AsSpan().SequenceEqual(payload), "bytes");
        Assert(ok.Headers.GetValues("Cache-Control").Single().Contains("immutable"), "immutable");

        var badTag = h.Tag.ToArray();
        badTag[0] ^= 1;
        Assert(
            (await http.GetAsync($"/_tapline/blob/{h.Id}/{s.Id}/{Convert.ToHexString(badTag)}")).StatusCode ==
            HttpStatusCode.NotFound, "wrong tag → 404");
        var other = NewOutlet(registry);
        Assert(
            (await http.GetAsync($"/_tapline/blob/{h.Id}/{other.Id}/{Convert.ToHexString(h.Tag)}")).StatusCode ==
            HttpStatusCode.NotFound, "another outlet → 404");

        var ranged = new HttpRequestMessage(HttpMethod.Get, url);
        ranged.Headers.Range = new RangeHeaderValue(1000, 1999);
        var part = await http.SendAsync(ranged);
        Assert(
            part.StatusCode == HttpStatusCode.PartialContent && (await part.Content.ReadAsByteArrayAsync()).AsSpan()
            .SequenceEqual(payload.AsSpan(1000, 1000)), "Range → 206");

        // The client confirms with Tap.Fetched (as encoded by the real SignalR JS client): the tap resumes — but only for
        // the handle's own ticket, so a late confirmation of an older handle can't release a newer one.
        value.Publish([5]);
        Assert(!s.IsReady, "the tap waiting for its fetch");
        var protocol = new RealtimeProtocol(BlazorPack(), registry);
        var stale = new ReadOnlySequence<byte>(ControlBytes("Tap.Fetched", s.Id, s.Nonce, 0, tap.Id, h.Ticket + 1));
        protocol.TryParseMessage(ref stale, new Binder(), out _);
        Assert(!s.IsReady, "a confirmation with another ticket is ignored");
        var fetched = EncodeWithJs("Tap.Fetched", s.Id, s.Nonce, 0, tap.Id, h.Ticket);
        var input = new ReadOnlySequence<byte>(fetched);
        Assert(protocol.TryParseMessage(ref input, new Binder(), out var m) && m is PingMessage, "consumed in place");
        Assert(s.IsReady, "resumed");
        await app.StopAsync();
    }

    /// <summary>Buys or sells <paramref name="qty" /> of a symbol: position and cash move in one unit of work.</summary>
    private static void Trade(Table<Holding, long> positions, Table<int, long> cash, Dictionary<int, long> balances,
        int account, int symbol, long qty)
    {
        using var tx = Transaction.Begin(positions, cash);
        tx.Of(positions).Fold<long, Add<long>>(new Holding(account, symbol), qty);
        balances[account] -= qty;
        tx.Of(cash).Set(account, balances[account]);
        tx.Complete();
    }

    private static long Wealth(RefClient.GroupMirror g)
    {
        return g.Tables.TryGetValue(0, out var p) && g.Tables.TryGetValue(1, out var b) && b.Rows.Count == 1
            ? p.Rows.Values.Sum(r => BitConverter.ToInt64(r.Row)) + BitConverter.ToInt64(b.Rows.Values.Single().Row)
            : long.MinValue;
    }

    public static async Task TransactionAtomicity()
    {
        var registry = new OutletRegistry();
        var positions = new Table<Holding, long>("positions", 1024);
        var cash = new Table<int, long>("cash");
        var balances = Enumerable.Range(0, 4).ToDictionary(a => a, _ => Endowment);
        using (var u = cash.BeginUpdate())
        {
            foreach (var (a, b) in balances) u.Set(a, b);
            u.Complete();
        }

        var s = NewOutlet(registry, o => o.FrameBudget = 4096);
        var watch = s.Watch("account:2", WatchMember.Range(positions, 2), WatchMember.Range(cash, 2));
        watch.Activate();
        var separate = NewOutlet(registry); // the same data as two independent taps
        var pTap = separate.Tap(positions, activate: true);
        var cTap = separate.Tap(cash, activate: true);
        var c = new RefClient();
        var sc = new RefClient();
        int deliveries = 0, torn = 0, separateFrames = 0, separateTorn = 0;
        c.OnGroup = (_, g) =>
        {
            deliveries++;
            if (Wealth(g) != Endowment) torn++;
        };
        var stop = false;

        async Task Consume(Outlet o, RefClient client, Action? afterFrame)
        {
            var scratch = new byte[1 << 20];
            while (true)
            {
                if (o.IsReady)
                {
                    client.Decode(scratch.AsSpan(0, o.Build(scratch)));
                    afterFrame?.Invoke();
                    continue;
                }

                if (Volatile.Read(ref stop)) return;
                await o.WaitAsync();
            }
        }

        var consumer = Task.Run(() => Consume(s, c, null));
        var separateConsumer = Task.Run(() => Consume(separate, sc, () =>
        {
            if (!sc.Tables.TryGetValue(pTap.Id, out var p) || !sc.Tables.TryGetValue(cTap.Id, out var b)) return;
            separateFrames++;
            var pos = p.Rows.Values.Where(r => BitConverter.ToInt32(r.Key) == 2).Sum(r => BitConverter.ToInt64(r.Row));
            var bal = b.Rows.TryGetValue(Convert.ToHexString(BitConverter.GetBytes(2)), out var row)
                ? BitConverter.ToInt64(row.Row)
                : 0;
            if (pos + bal != Endowment) separateTorn++;
        }));

        // Trades go on until the watch has been read under them often enough for "never torn" to mean something: 40,000
        // at least, and until it has delivered 100 groups meanwhile — counted, not timed (on a loaded machine the
        // consumer gets less of the CPU than the producer; a minute of trades means it's stuck, and the assert says so).
        var rnd = new Random(21);
        var trading = Stopwatch.StartNew();
        for (var i = 0;
             i < 40_000 || (Volatile.Read(ref deliveries) <= 100 && trading.Elapsed < TimeSpan.FromMinutes(1));
             i++)
        {
            int account = rnd.Next(4), symbol = rnd.Next(8);
            long qty = rnd.Next(-50, 100);
            if (positions.TryGet(new Holding(account, symbol), out var held) && held + qty < 0)
                qty = -held; // can't sell what isn't held
            Trade(positions, cash, balances, account, symbol, qty);
            if ((i & 255) == 0) await Task.Yield();
        }

        // Caught up: the watch's state as of the last trade written (the router's word — a key range's record carries the
        // table's version only when the range changed, so the replica's sequence numbers can't say so; nor is the
        // consumer's replica the test thread's to read while it runs).
        var sw = Stopwatch.StartNew();
        while (watch.Waiting && sw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(5);
        var caughtUp = !watch.Waiting;

        Volatile.Write(ref stop, true);
        s.Interrupt();
        separate.Interrupt();
        await Task.WhenAll(consumer, separateConsumer).WaitAsync(TimeSpan.FromSeconds(10));

        var group = c.Groups[watch.Id];
        Assert(group.Failures == 0 && deliveries > 100 && torn == 0,
            $"watch: {deliveries} deliveries, {torn} torn, {group.Failures} verification failures");
        Assert(caughtUp && Wealth(group) == Endowment, "converged");
        var expected = new List<KeyValuePair<Holding, long>>();
        positions.CopyTo(expected);
        var replica = group.Tables[0].Rows.Values.ToDictionary(
            r => new Holding(BitConverter.ToInt32(r.Key), BitConverter.ToInt32(r.Key, 4)), r => BitConverter.ToInt64(r.Row));
        var mine = expected.Where(kv => kv.Key.Account == 2).ToList();
        Assert(replica.Count == mine.Count && mine.All(kv => replica.TryGetValue(kv.Key, out var q) && q == kv.Value),
            "the range holds exactly account 2's positions");
        Notes.Add(
            $"atomicity: watch {deliveries} deliveries, 0 torn; two independent taps were torn in {separateTorn} of {separateFrames} frames");
    }

    public static Task TransactionSemantics()
    {
        var positions = new Table<Holding, long>("ts-positions");
        var cash = new Table<int, long>("ts-cash");
        var mark = new LastValueSource();
        using (var tx = Transaction.Begin(positions, cash, mark))
        {
            tx.Of(positions).Set(new Holding(1, 1), 10);
            tx.Of(cash).Set(1, 990);
            tx.Of(mark).Publish([1]);
            tx.Complete(1);
        }

        Assert(
            positions.Version == 1 && cash.Version == 1 && mark.Version == 1 && positions.UpstreamVersion == 1 &&
            cash.UpstreamVersion == 1, "published together");

        using (var tx = Transaction.Begin(positions, cash, mark)) // abandoned: everything rolls back
        {
            tx.Of(positions).Set(new Holding(1, 1), 20);
            tx.Of(cash).Delete(1);
            tx.Of(mark).Publish([2]);
        }

        Assert(
            positions.TryGet(new Holding(1, 1), out var q) && q == 10 && cash.TryGet(1, out var b) && b == 990 &&
            mark.Version == 1, "rolled back");

        // Misuse is refused while a transaction is open.
        using (var tx = Transaction.Begin(positions, cash))
        {
            var u = tx.Of(positions);
            bool completeRefused, nestedRefused, beginRefused;
            try
            {
                u.Complete();
                completeRefused = false;
            }
            catch (InvalidOperationException)
            {
                completeRefused = true;
            }

            try
            {
                var nested = Transaction.Begin(mark);
                nested.Dispose();
                nestedRefused = false;
            }
            catch (InvalidOperationException)
            {
                nestedRefused = true;
            }

            try
            {
                var single = cash.BeginUpdate();
                single.Dispose();
                beginRefused = false;
            }
            catch (InvalidOperationException)
            {
                beginRefused = true;
            }

            Assert(completeRefused && nestedRefused && beginRefused,
                "a member can't complete alone; no nesting; no separate update");
            tx.Complete();
        }

        using (var tx = Transaction.Begin(mark))
        {
            bool refused;
            try
            {
                mark.Publish([3]);
                refused = false;
            }
            catch (InvalidOperationException)
            {
                refused = true;
            }

            Assert(refused, "a member value publishes through the transaction");
        }

        Assert(Threw<ArgumentException>(() =>
        {
            using var tx = Transaction.Begin(cash, cash);
        }), "a source twice");

        // A member's fold fault rolls back every member and faults every member.
        try
        {
            using var tx = Transaction.Begin(positions, cash);
            tx.Of(cash).Set(1, 0);
            try
            {
                tx.Of(positions).Fold<long, Explode>(new Holding(1, 2), 666);
            }
            catch (StreamFaultException)
            {
            }

            tx.Complete();
            throw new Exception("expected a fault");
        }
        catch (StreamFaultException e)
        {
            Assert(e.Fault.Code == FaultCode.FoldFailed, "fold fault");
        }

        Assert(positions.Fault?.Code == FaultCode.FoldFailed && cash.Fault?.Code == FaultCode.FoldFailed,
            "all members faulted");
        Assert(cash.TryGet(1, out b) && b == 990 && positions.Version == 1 && cash.Version == 1,
            "all members rolled back");
        Assert(Threw<StreamFaultException>(() =>
        {
            using var tx = Transaction.Begin(positions);
        }), "faulted members refuse updates");

        // Recovery rebuilds all members; a precondition then faults all of them again.
        using (var tx = Transaction.BeginRecovery(positions, cash))
        {
            tx.Of(positions).Clear();
            tx.Of(positions).Set(new Holding(1, 1), 5);
            tx.Of(cash).Set(1, 995);
            tx.Complete(100);
        }

        Assert(
            positions.Fault is null && cash.Fault is null && positions.UpstreamVersion == 100 &&
            cash.UpstreamVersion == 100, "recovered");
        Assert(Threw<StreamFaultException>(() =>
        {
            using var tx = Transaction.Begin(99, positions, cash);
        }), "gap");
        Assert(positions.Fault?.Code == FaultCode.VersionMismatch && cash.Fault?.Code == FaultCode.VersionMismatch,
            "a precondition faults every member");

        // A member's postcondition (the provider's checksum) is checked for all before any publishes.
        using (var tx = Transaction.BeginRecovery(positions, cash))
        {
            tx.Complete();
        }

        var probe = new Table<int, long>("probe");
        using (var u = probe.BeginUpdate())
        {
            u.Set(1, 7);
            u.Complete();
        }

        try
        {
            using var tx = Transaction.Begin(positions, cash);
            tx.Of(cash).Set(1, 7);
            tx.Of(cash).ExpectChecksum(probe.Checksum); // right
            tx.Of(positions).Set(new Holding(9, 9), 1);
            tx.Of(positions).ExpectChecksum(0xBAD); // wrong
            tx.Complete();
        }
        catch (StreamFaultException e)
        {
            Assert(e.Fault.Code == FaultCode.ChecksumMismatch, "postcondition");
        }

        Assert(cash.TryGet(1, out b) && b == 995 && !positions.TryGet(new Holding(9, 9), out _), "nothing published");
        return Task.CompletedTask;
    }

    public static Task WatchRange()
    {
        var registry = new OutletRegistry();
        var positions = new Table<Holding, long>("wr", 64); // small log: snapshot fallback + slot recycling
        var s = NewOutlet(registry);
        var watch = s.Watch("account:7", WatchMember.Range(positions, 7));
        watch.Activate();
        var c = new RefClient();
        var scratch = new byte[1 << 20];
        var rnd = new Random(31);
        var outsideOnly = 0;
        for (var step = 0; step < 20_000; step++)
        {
            var inside = false;
            using (var u = positions.BeginUpdate())
            {
                for (var i = rnd.Next(1, 4); i > 0; i--)
                {
                    var h = new Holding(rnd.Next(5, 10), rnd.Next(6));
                    inside |= h.Account == 7;
                    if (rnd.Next(3) == 0) u.Delete(h);
                    else u.Set(h, rnd.Next(1, 1000));
                }

                u.Complete();
            }

            if (rnd.Next(4) != 0 || step % 2000 < 300) continue; // long pauses: fall off the 64-entry log
            var frames = c.Frames;
            Drain(s, c, scratch);
            if (!inside && c.Frames == frames) outsideOnly++;
        }

        Drain(s, c, scratch);
        var g = c.Groups[watch.Id];
        var expected = new List<KeyValuePair<Holding, long>>();
        positions.CopyTo(expected);
        var mine = expected.Where(kv => kv.Key.Account == 7)
            .ToDictionary(kv => Convert.ToHexString(Bytes(kv.Key)), kv => kv.Value);
        var m = g.Tables[0];
        Assert(g.Failures == 0 && m.Failures == 0,
            $"{g.Failures + m.Failures} verification failures (subset checksum)");
        Assert(
            m.Rows.Count == mine.Count && mine.All(kv =>
                m.Rows.TryGetValue(kv.Key, out var r) && BitConverter.ToInt64(r.Row) == kv.Value),
            "range mirrors account 7 exactly");
        Assert(m.Seq == positions.Version, "sequence carried across skipped versions");
        Assert(m.Snapshots > 5, $"snapshot fallback exercised ({m.Snapshots})");
        Notes.Add(
            $"key range: {m.Snapshots} snapshots, {m.Deltas} deltas; {outsideOnly} drains with changes only outside the range sent nothing");
        return Task.CompletedTask;
    }

    public static async Task WatchFaultsAndOutOfBand()
    {
        var registry = new OutletRegistry();
        var s = NewOutlet(registry);
        var book = new Table<Level, long>("wf-book");
        var marks = new LastValues<int>("wf");
        FillBook(book, 10, 1);
        marks.Publish(0, [42]);
        var watch = s.Watch("book+mark", new ErrorPolicy("feed interrupted"u8.ToArray()),
            WatchMember.Of(book, TopOfBook), WatchMember.Of(marks[0]));
        watch.Activate();
        var c = new RefClient();
        var scratch = new byte[1 << 20];
        Drain(s, c, scratch);
        var g = c.Groups[watch.Id];
        Assert(g.Deliveries == 1 && g.Tables[0].Rows.Count == 20 && g.Values[1][0] == 42,
            "first delivery: both members");

        try
        {
            using var u = book.BeginUpdate(9);
        }
        catch (StreamFaultException)
        {
        }

        Drain(s, c, scratch);
        Assert(c.LastFrameRecords.SequenceEqual([Wire.Group, Wire.Error]),
            $"degraded group then error, one frame: {string.Join(",", c.LastFrameRecords)}");
        Assert(g.Degraded && g.Tables[0].Degraded && g.Tables[0].Rows.Count == 2 && g.Tables[0].Failures == 0,
            "degraded member (best bid/ask, size 0), checksum-verified");
        Assert(
            c.Errors.Single() is { Code: FaultCode.VersionMismatch } e && e.TapId == watch.Id &&
            e.Payload.AsSpan().SequenceEqual("feed interrupted"u8), "error with payload");
        Assert((await watch.Completion)?.Code == FaultCode.VersionMismatch && !s.Taps.Any(), "the watch ended");

        using (var u = book.BeginRecovery())
        {
            u.Clear();
            u.Set(new Level(0, 98), 1);
            u.Complete(50);
        }

        var again = s.Watch("book+mark", WatchMember.Of(book), WatchMember.Of(marks[0]));
        again.Activate();
        Drain(s, c, scratch);
        Assert(c.Groups[again.Id] is { Degraded: false, Deliveries: 1 } g2 && g2.Tables[0].Rows.Count == 1,
            "resubscribed after recovery");

        // A group record over the threshold travels out of band like any other record.
        var big = new Table<Level, long>("wf-big");
        FillBook(big, 1500); // ≈ 54 KB
        var bigWatch = s.Watch("big", WatchMember.Of(big), WatchMember.Of(marks[0]));
        bigWatch.Activate();
        Drain(s, c, scratch);
        Assert(c.Pending.Single() is { Inner: Wire.Group } && !c.Groups.ContainsKey(bigWatch.Id), "group as a handle");
        c.FetchPending(s, registry);
        var bg = c.Groups[bigWatch.Id];
        Assert(
            bg.Deliveries == 1 && bg.Tables[0].Rows.Count == 3000 && bg.Tables[0].Failures == 0 &&
            bg.Values[1][0] == 42, "fetched group applied as one");
        using (var u = big.BeginUpdate())
        {
            u.Set(new Level(0, 1), 1);
            u.Complete();
        }

        Drain(s, c, scratch);
        Assert(bg.Deliveries == 2 && bg.Tables[0].Deltas == 1, "then in-band deltas");
    }

    // ─────────────────────────────── values ───────────────────────────────

    public static Task Conflation()
    {
        var registry = new OutletRegistry();
        var values = new LastValues<int>("ticker");
        var s = NewOutlet(registry);
        var taps = Enumerable.Range(0, 50).Select(k => (Key: k, Tap: s.Tap(values[k], activate: true))).ToArray();
        var latest = new Dictionary<int, int>();
        var rnd = new Random(3);
        var c = new RefClient();
        var scratch = new byte[1 << 20];
        Span<byte> value = stackalloc byte[4];

        for (var round = 0; round < 200; round++)
        {
            for (var i = 0; i < 1000; i++)
            {
                int key = rnd.Next(round % 2 == 0 ? 50 : 5), v = rnd.Next();
                BitConverter.TryWriteBytes(value, v);
                values.Publish(key, value);
                latest[key] = v;
            }

            var n = s.Build(scratch);
            var ids = new HashSet<uint>();
            for (var o = 0; o < n;)
            {
                Assert(scratch[o] == Wire.Value, "only values expected");
                var id = BitConverter.ToUInt32(scratch, o + 1);
                Assert(ids.Add(id), "key sent twice in one frame");
                o += Wire.ValueHeader + BitConverter.ToUInt16(scratch, o + 5);
            }

            c.Decode(scratch.AsSpan(0, n));
            Drain(s, c, scratch);
            foreach (var (key, tap) in taps)
                if (latest.TryGetValue(key, out var v))
                    Assert(BitConverter.ToInt32(c.Values[tap.Id]) == v, $"key {key}: stale value");
        }

        return Task.CompletedTask;
    }

    public static Task ReadyOrder()
    {
        var registry = new OutletRegistry();
        var s = NewOutlet(registry, o =>
        {
            o.FrameBudget = 1024;
            o.FlowControl = FlowControlMode.Fixed;
        }); // the scheduler at a fixed budget
        var values = new LastValues<int>("ro");
        var taps = Enumerable.Range(0, 5).Select(k => s.Tap(values[k], activate: true)).ToArray();
        var c = new RefClient();
        var scratch = new byte[1 << 20];
        Assert(s.Build(scratch) == 0 && !s.IsReady, "nothing published yet");

        values.Publish(3, [1]);
        values.Publish(1, [1]);
        values.Publish(4, [1]);
        values.Publish(1, [2]); // coalesces: tap 1 keeps its age
        values.Publish(0, [1]);
        c.Decode(scratch.AsSpan(0, s.Build(scratch)));
        Assert(c.LastFrameTaps.SequenceEqual([taps[3].Id, taps[1].Id, taps[4].Id, taps[0].Id]),
            $"first-dirty order: {string.Join(",", c.LastFrameTaps)}");
        Assert(c.Values[taps[1].Id][0] == 2, "coalesced to the latest value");

        // A record that doesn't fit behind other records keeps its place; smaller ones behind it go first.
        values.Publish(2, new byte[900]);
        values.Publish(0, new byte[50]);
        values.Publish(4, new byte[50]);
        var high = Poster(s, Lane.High);
        high.TryPost(new byte[300]);
        c.Decode(scratch.AsSpan(0, s.Build(scratch)));
        Assert(c.LastFrameTaps.SequenceEqual([high.Id, taps[0].Id, taps[4].Id]),
            $"blocked record skipped: {string.Join(",", c.LastFrameTaps)}");
        values.Publish(3, [9]); // newer than the blocked one
        c.Decode(scratch.AsSpan(0, s.Build(scratch)));
        Assert(c.LastFrameTaps.SequenceEqual([taps[2].Id, taps[3].Id]),
            $"blocked record first in the next frame: {string.Join(",", c.LastFrameTaps)}");
        Assert(!s.IsReady, "all clean");
        return Task.CompletedTask;
    }

    public static async Task ReadyConcurrency()
    {
        var registry = new OutletRegistry();
        var s = NewOutlet(registry, o => o.FrameBudget = 4096);
        var values = new LastValues<int>("rc");
        const int Keys = 64, Writers = 8, Updates = 40_000;
        var taps = Enumerable.Range(0, Keys).Select(k => s.Tap(values[k], activate: true)).ToArray();
        var books = Enumerable.Range(0, Writers).Select(i => new Table<Level, long>($"RC{i}", 1024)).ToArray();
        var bookTaps = books.Select(b => s.Tap(b, activate: true)).ToArray();
        var last = new int[Keys];
        var c = new RefClient();
        var scratch = new byte[1 << 20];
        var stop = false;
        long frames = 0;

        var consumer = Task.Run(async () =>
        {
            while (true)
            {
                if (s.IsReady)
                {
                    var n = s.Build(scratch);
                    lock (c)
                    {
                        c.Decode(scratch.AsSpan(0, n));
                    }

                    frames++;
                    continue;
                }

                if (Volatile.Read(ref stop)) return;
                await s.WaitAsync();
            }
        });
        var writers = Enumerable.Range(0, Writers).Select(w => Task.Run(() =>
        {
            var rnd = new Random(w);
            Span<byte> v = stackalloc byte[4];
            for (var i = 1; i <= Updates; i++)
            {
                var key = rnd.Next(Keys / Writers) * Writers + w; // single writer per key
                BitConverter.TryWriteBytes(v, i);
                values.Publish(key, v);
                last[key] = i;
                if ((i & 7) == 0)
                    using (var u = books[w].BeginUpdate())
                    {
                        u.Set(new Level((byte)(i & 1), rnd.Next(50)), i);
                        u.Complete();
                    }

                if ((i & 1023) == 0) s.Resync(taps[key].Id);
            }
        })).ToArray();
        await Task.WhenAll(writers);

        // No Interrupt: a parked consumer must be woken by the publishes alone.
        bool Converged()
        {
            lock (c)
            {
                return Enumerable.Range(0, Keys).All(k =>
                           last[k] == 0 || (c.Values.TryGetValue(taps[k].Id, out var v) &&
                                            BitConverter.ToInt32(v) == last[k]))
                       && books.Select((b, i) => (b, i)).All(x =>
                           c.Tables.TryGetValue(bookTaps[x.i].Id, out var m) && m.Seq == x.b.Version);
            }
        }

        var sw = Stopwatch.StartNew();
        while (!Converged() && sw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(5);
        Assert(Converged(), "lost change or wake-up: the client did not converge");
        for (var i = 0; i < books.Length; i++) AssertMirror(c.Tables[bookTaps[i].Id], books[i], $"book {i}");
        Volatile.Write(ref stop, true);
        s.Interrupt();
        await consumer.WaitAsync(TimeSpan.FromSeconds(5));
        Notes.Add(
            $"ready queue: {Writers}×{Updates} publishes + table updates → {frames} frames, converged with no lost wake-ups");
    }

    /// <summary>
    ///     The waveform demo in miniature: a client capped at 16 KB/s, a bulk value of 6 KB published every ms (the ridges),
    ///     and a 500 B value every 40 ms that must not wait (the audio). As a Streaming tap it takes turns with the bulk under
    ///     the cap and keeps missing; bound to <see cref="Lane.High" /> it goes in the next frame every time, outside the cap,
    ///     which the bulk alone then keeps.
    /// </summary>
    public static Task LaneTaps()
    {
        (int Missed, int Delivered, long Bulk) Run(Lane? lane)
        {
            var clock = new ManualTime();
            var wheel = new TimingWheel(TimeSpan.FromMilliseconds(1), time: clock, autoStart: false);
            var s = NewOutlet(new OutletRegistry(wheel), o =>
            {
                o.StreamingBytesPerSecond = 16 * 1024;
                o.StreamingBurstBytes = 4 * 1024;
            });
            var values = new LastValues<int>("lane-taps");
            LastValueSource bulk = values[0], urgent = values[1];
            var bulkTap = s.Tap(bulk, activate: true);
            var urgentTap = s.Tap(urgent, activate: true, lane: lane);
            var c = new RefClient();
            var scratch = new byte[1 << 20];
            var big = new byte[6 * 1024];
            var chunk = new byte[500];
            int missed = 0, delivered = 0, seq = 0;
            long bulkBytes = 0;
            for (var ms = 0; ms < 4000; ms++)
            {
                BitConverter.TryWriteBytes(big, ms);
                bulk.Publish(big, compressible: false);
                if (ms % 40 == 0)
                {
                    BitConverter.TryWriteBytes(chunk, ++seq);
                    urgent.Publish(chunk, compressible: false);
                }

                clock.Advance(TimeSpan.FromMilliseconds(1));
                wheel.Advance(clock.Now);
                for (var spins = 0; s.IsReady && spins < 100; spins++)
                {
                    var n = s.Build(scratch);
                    c.Decode(scratch.AsSpan(0, n));
                    if (c.LastFrameTaps.Contains(bulkTap.Id)) bulkBytes += big.Length;
                }

                if (ms % 40 == 0 && ms >= 1000) // past the burst: did this chunk go in the very next frame?
                {
                    if (c.Values.TryGetValue(urgentTap.Id, out var v) && BitConverter.ToInt32(v) == seq) delivered++;
                    else missed++;
                }
            }

            return (missed, delivered, bulkBytes);
        }

        var streaming = Run(null);
        var high = Run(Lane.High);
        Notes.Add(
            $"16 KB/s client, 6 KB bulk every ms + 500 B every 40 ms: as a Streaming tap {streaming.Delivered} of {streaming.Delivered + streaming.Missed} on time; on High {high.Delivered} of {high.Delivered + high.Missed}; bulk {high.Bulk / 4.0 / 1024:F1} KB/s over 4 s (cap 16 KB/s + 4 KB burst)");
        Assert(streaming.Missed > streaming.Delivered,
            $"a Streaming tap behind the bulk should mostly wait (missed {streaming.Missed})");
        Assert(high.Missed == 0, $"a High tap goes in the next frame (missed {high.Missed})");
        Assert(high.Bulk <= 16 * 1024 * 4 + 4 * 1024 + 6 * 1024,
            $"the bulk keeps its cap ({high.Bulk} B in 4 s: 16 KB/s + burst + one record's overdraft)");

        // Between regular frames (deferring to queued traffic, or before the next is due) a frame serves High only — and a
        // High tap's record larger than the frame budget still goes first in it, rather than parking the tap as oversize.
        var registry = new OutletRegistry();
        var outlet = NewOutlet(registry, o =>
        {
            o.FrameBudget = 1024;
            o.FlowControl = FlowControlMode.Fixed;
        });
        var big = new LastValues<int>("lane-taps-big");
        var bigTap = outlet.Tap(big[0], activate: true, lane: Lane.High);
        var client = new RefClient();
        var buffer = new byte[1 << 20];
        for (var round = 1; round <= 3; round++)
        {
            var value = new byte[3000];
            value[0] = (byte)round;
            big.Publish(0, value, compressible: false);
            client.Decode(buffer.AsSpan(0, outlet.Build(buffer, true)));
            Assert(client.Values.TryGetValue(bigTap.Id, out var got) && got.Length == 3000 && got[0] == round,
                $"a 3 KB High record in a High-only frame, round {round}");
        }

        return Task.CompletedTask;
    }

    public static async Task RateLimit()
    {
        var clock = new ManualTime();
        var wheel = new TimingWheel(TimeSpan.FromMilliseconds(5), time: clock, autoStart: false);
        var registry = new OutletRegistry(wheel);
        const long Rate = 64 * 1024, Burst = 16 * 1024;
        var s = NewOutlet(registry, o =>
        {
            o.StreamingBytesPerSecond = Rate;
            o.StreamingBurstBytes = Burst;
        });
        var values = new LastValues<int>("rl");
        var taps = Enumerable.Range(0, 64).Select(k => s.Tap(values[k], activate: true)).ToArray();
        var c = new RefClient();
        var high = Poster(s, Lane.High, c);
        var scratch = new byte[1 << 20];
        var payload = new byte[1000];

        void Pump()
        {
            for (var spins = 0; s.IsReady; spins++)
            {
                Assert(spins < 1000, "IsReady without progress (spin)");
                c.Decode(scratch.AsSpan(0, s.Build(scratch)));
            }
        }

        long Run(TimeSpan duration, bool post)
        {
            var before = c.BytesStreaming;
            for (var t = TimeSpan.Zero; t < duration; t += TimeSpan.FromMilliseconds(1))
            {
                for (var k = 0; k < 64; k++)
                {
                    BitConverter.TryWriteBytes(payload, (int)(clock.Now / 1000));
                    values.Publish(k, payload);
                } // 64 MB/s offered

                if (post) Assert(high.TryPost(payload), "posted");
                clock.Advance(TimeSpan.FromMilliseconds(1));
                wheel.Advance(clock.Now);
                Pump();
            }

            return c.BytesStreaming - before;
        }

        var bytes = Run(TimeSpan.FromSeconds(10), true);
        double expected = Rate * 10 + Burst;
        Assert(Math.Abs(bytes - expected) < 2 * 1007,
            $"streamed {bytes} B in 10 s, expected {expected:F0} (64 KB/s + burst)");
        Assert(c.BytesHigh == 10_000 * (Wire.TopicMessageHeader + 1000L), "the High class is not rate-limited");
        Notes.Add(
            $"rate limit: {bytes / 10.0 / 1024:F1} KB/s streamed at a 64 KB/s cap with 64 MB/s offered (squashed)");

        // Parked on an empty budget: IsReady is false and only the wheel wakes the waiter.
        values.Publish(0, payload);
        Assert(!s.IsReady, "in debt → not ready");
        var wait = s.WaitAsync();
        Assert(!wait.IsCompleted, "parked");
        int fired = 0, readyAt = -1, firedAt = -1;
        for (var ms = 0; ms < 1000 && fired == 0; ms++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            if (readyAt < 0 && s.IsReady) readyAt = ms; // budget positive again
            if ((fired = wheel.Advance(clock.Now)) > 0) firedAt = ms;
        }

        Assert(fired == 1 && s.IsReady && readyAt >= 0 && firedAt - readyAt <= 5,
            $"the wheel fires within a tick of the budget recovering (ready {readyAt} ms, fired {firedAt} ms)");
        await wait.AsTask().WaitAsync(TimeSpan.FromSeconds(5)); // the wake-up hops to the thread pool

        // A tier change takes effect at the next frame.
        s.SetStreamingRate(300 * 1024, Burst);
        bytes = Run(TimeSpan.FromSeconds(4), false);
        Assert(Math.Abs(bytes - 300 * 1024 * 4) < 300 * 1024 * 4 * 0.02 + 2 * Burst,
            $"after the tier change: {bytes / 4 / 1024} KB/s");
        s.SetStreamingRate(0);
        bytes = Run(TimeSpan.FromMilliseconds(100), false);
        Assert(bytes > 300 * 1024 * 0.1 * 10, $"uncapped: {bytes} B in 100 ms");
        Assert(wheel.Count == 0, "uncapped outlets leave the wheel");

        // Under the cap every tap still ends on its latest value (squashing, not dropping).
        s.SetStreamingRate(Rate, Burst);
        for (var k = 0; k < 64; k++) values.Publish(k, BitConverter.GetBytes(-k));
        for (var i = 0; i < 2000 && (s.IsReady || wheel.Count > 0); i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(5));
            wheel.Advance(clock.Now);
            Pump();
        }

        Assert(taps.Select((t, k) => BitConverter.ToInt32(c.Values[t.Id]) == -k).All(x => x),
            "every tap converges to its latest value");
    }

    public static Task Wheel()
    {
        var clock = new ManualTime();
        var tick = TimeSpan.FromMilliseconds(5);
        var tickTicks = (long)(tick.TotalSeconds * clock.TimestampFrequency);
        var wheel = new TimingWheel(tick, 64, clock, false); // 320 ms revolution
        var rnd = new Random(12);
        var probes = Enumerable.Range(0, 2000).Select(_ => new Probe(clock, true)).ToArray();
        var start = clock.Now;
        foreach (var p in probes)
        {
            p.Due = start + rnd.NextInt64(0, 3 * clock.TimestampFrequency); // up to ~9 revolutions out
            wheel.Schedule(p.Node, p.Due);
        }

        foreach (var p in probes.Take(100))
        {
            p.Due += rnd.NextInt64(0, clock.TimestampFrequency);
            wheel.Schedule(p.Node, p.Due);
        } // reschedule moves

        foreach (var p in probes.Skip(100).Take(100))
        {
            wheel.Cancel(p.Node);
            p.Due = long.MaxValue;
        }

        Assert(wheel.Count == 1900, $"count {wheel.Count}");
        while (clock.Now < start + 5 * clock.TimestampFrequency)
        {
            clock.Now += rnd.NextInt64(1, tickTicks); // irregular, ≤ 1 tick per step
            wheel.Advance(clock.Now);
        }

        foreach (var p in probes)
        {
            if (p.Due == long.MaxValue)
            {
                Assert(p.Fires == 0, "cancelled must not fire");
                continue;
            }

            Assert(p.Fires == 1, $"fired {p.Fires} times");
            Assert(p.FiredAt >= p.Due, "fired early");
            Assert(p.FiredAt - p.Due < 2 * tickTicks, $"fired {(p.FiredAt - p.Due) / 1e6:F1} ms late");
        }

        Assert(wheel.Count == 0, "drained");

        // A driver that falls behind by several revolutions still fires everything due, once.
        var late = new Probe(clock, true);
        wheel.Schedule(late.Node, clock.Now + 50 * tickTicks);
        clock.Now += 1000 * tickTicks;
        Assert(wheel.Advance(clock.Now) == 1 && late.Fires == 1, "catch-up after a stall");
        return Task.CompletedTask;
    }

    public static Task WeightedShares()
    {
        var registry = new OutletRegistry();
        var s = NewOutlet(registry, o =>
        {
            o.FrameBudget = 16 * 1024;
            o.MailboxMessages = 1 << 12;
            o.MailboxBytes = 1 << 20;
        });
        var values = new LastValues<int>("t");
        for (var k = 0; k < 256; k++) s.Tap(values[k], activate: true);
        var payload = new byte[100];
        var c = new RefClient();
        var posters = new[] { Lane.High, Lane.Normal, Lane.Low }.Select(lane => Poster(s, lane, c)).ToArray();
        var scratch = new byte[1 << 20];
        long stamp = 0;

        for (var frame = 0; frame < 3000; frame++)
        {
            foreach (var poster in posters)
                while (poster.TryPost(payload))
                {
                }

            for (var k = 0; k < 256; k++)
            {
                BitConverter.TryWriteBytes(payload, ++stamp);
                values.Publish(k, payload);
            }

            var n = s.Build(scratch);
            if (frame >= 100) c.Decode(scratch.AsSpan(0, n));
        }

        double total = c.BytesHigh + c.BytesNormal + c.BytesStreaming + c.BytesLow;
        double h = c.BytesHigh / total,
            nrm = c.BytesNormal / total,
            st = c.BytesStreaming / total,
            l = c.BytesLow / total;
        Notes.Add(
            $"shares H {h:P1}  N {nrm:P1}  S {st:P1}  L {l:P1}   (target 64% / 16% / 16% / 4%), {total / c.Frames:F0} B/frame");
        Assert(
            Math.Abs(h - 0.64) < 0.015 && Math.Abs(nrm - 0.16) < 0.015 && Math.Abs(st - 0.16) < 0.015 &&
            Math.Abs(l - 0.04) < 0.01, "shares off target");
        return Task.CompletedTask;
    }

    public static async Task ParkTimeWakeups()
    {
        // An outlet registers with the wheel only when it parks with something held back by time — once, for the earliest.
        var clock = new ManualTime();
        var wheel = new TimingWheel(TimeSpan.FromMilliseconds(1), time: clock, autoStart: false);
        var s = NewOutlet(new OutletRegistry(wheel), o =>
        {
            o.MaxFramesPerSecond = 50;
            o.FlowControl = FlowControlMode.Fixed;
        });
        var values = new LastValues<int>("park");
        s.Tap(values[0], activate: true);
        var scratch = new byte[1 << 16];
        values.Publish(0, [1]);
        Assert(s.IsReady && s.Build(scratch) > 0 && wheel.Count == 0,
            "a frame went: nothing registered (it never parked)");
        var idle = s.WaitAsync();
        Assert(!idle.IsCompleted && wheel.Count == 0, "parked with nothing waiting: not on the wheel (data wakes it)");
        values.Publish(0, [2]);
        await idle.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert(!s.IsReady, "woken by data — not ready: the next frame is due 20 ms after the last");
        var cadence = s.WaitAsync();
        Assert(!cadence.IsCompleted && wheel.Count == 1, "parked waiting on the cadence: on the wheel, once");
        var ms = 0;
        for (var fired = false; ms < 30 && !fired; ms++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            fired = wheel.Advance(clock.Now) > 0;
        }

        await cadence.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert(s.IsReady && ms is >= 19 and <= 21 && wheel.Count == 0,
            $"woken at the frame's time ({ms} ms), off the wheel");
        Assert(s.Build(scratch) > 0, "the frame");

        // A retry: registered when it parks, released by the outlet's own thread when woken.
        var full = new BlobStore(10_000, time: clock);
        var r = NewOutlet(new OutletRegistry(wheel, full), o => o.FlowControl = FlowControlMode.Fixed);
        var big = new LastValues<int>("park-retry");
        var bigTap = r.Tap(big[0], activate: true);
        big.Publish(0, new byte[20_000], Delivery.OutOfBand); // larger than the store: backs off, retries
        r.Build(scratch);
        Assert(!r.IsReady, "backing off");
        var retry = r.WaitAsync();
        Assert(!retry.IsCompleted && wheel.Count == 1, "parked: its retry on the wheel");
        ms = 0;
        for (var fired = false; ms < 300 && !fired; ms++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            fired = wheel.Advance(clock.Now) > 0;
        }

        await retry.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert(ms is >= 249 and <= 252, $"woken when the retry was due ({ms} ms)");
        Assert(r.IsReady, "its tap released by the outlet itself, then ready");
    }

    public static Task WheelContention()
    {
        // The worst case for scheduling: twice as many threads as cores (a thread preempted holding a lock), each re-arming
        // its outlets' wake-ups the moment they fire — every park with a deadline — on one wheel, then on striped wheels.
        // How long a schedule takes, and how late wake-ups fire; none may fire early.
        var threads = Environment.ProcessorCount * 2;
        const int PerThread = 256;
        double us = 1e6 / Stopwatch.Frequency, ms = 1e3 / Stopwatch.Frequency;
        foreach (var stripes in new[] { 1, 0 })
        {
            using var wheel = new TimingWheel(TimeSpan.FromMilliseconds(1), stripes: stripes);
            var late = new long[1 << 22];
            var samples = new long[threads][];
            var counts = new int[threads];
            var stop = new ManualResetEventSlim();
            var workers = Enumerable.Range(0, threads).Select(w => new Thread(() =>
            {
                var probes = Enumerable.Range(0, PerThread).Select(_ => new LateProbe(late)).ToArray();
                var mine = samples[w] = new long[1 << 20];
                var rnd = new Random(w);
                var n = 0;
                while (!stop.IsSet)
                {
                    foreach (var p in probes)
                    {
                        if (Volatile.Read(ref p.Fired) == 0) continue;
                        p.Fired = 0;
                        var now = Stopwatch.GetTimestamp();
                        p.Due = now + (long)((1 + rnd.NextDouble() * 3) * Stopwatch.Frequency / 1000); // 1–4 ms out
                        var t0 = Stopwatch.GetTimestamp();
                        wheel.Schedule(p.Node, p.Due);
                        var t1 = Stopwatch.GetTimestamp();
                        if (n < mine.Length) mine[n++] = t1 - t0;
                    }

                    Thread.Yield();
                }

                counts[w] = n;
            }) { IsBackground = true }).ToList();
            workers.ForEach(t => t.Start());
            Thread.Sleep(1500);
            stop.Set();
            workers.ForEach(t => t.Join());
            var all = samples.SelectMany((a, i) => a.Take(counts[i])).OrderBy(x => x).ToArray();
            var fired = late.Take(Math.Min(late.Length, all.Length)).Where(x => x != 0 || true).ToArray();

            double P(long[] v, double q)
            {
                return v.Length == 0 ? 0 : v[Math.Min(v.Length - 1, (int)(q * v.Length))];
            }

            var lateness = late.Where(x => x != 0).OrderBy(x => x).ToArray();
            Assert(lateness.All(x => x >= 0), "a wake-up fired early");
            Notes.Add(
                $"timing wheel, {threads} threads re-arming {threads * PerThread:N0} wake-ups as they fire, {(stripes == 1 ? "one wheel" : $"{wheel.Stripes} stripes")}: " +
                $"{all.Length / 1.5:N0} schedules/s — schedule p50 {P(all, 0.5) * us:F2} µs, p99 {P(all, 0.99) * us:F2}, p99.9 {P(all, 0.999) * us:F2}, max {all[^1] * us:F0} µs; " +
                $"wake-ups late p50 {P(lateness, 0.5) * ms:F2} ms, p99.5 {P(lateness, 0.995) * ms:F2}, max {(lateness.Length > 0 ? lateness[^1] : 0) * ms:F1} ms");
        }

        return Task.CompletedTask;
    }

    public static Task WorkConserving()
    {
        var registry = new OutletRegistry();
        var s = NewOutlet(registry, o =>
        {
            o.FrameBudget = 16 * 1024;
            o.MailboxMessages = 1 << 12;
            o.MailboxBytes = 1 << 20;
            o.FlowControl = FlowControlMode.Fixed;
        });
        var low = Poster(s, Lane.Low);
        var payload = new byte[100];
        var scratch = new byte[1 << 20];
        long bytes = 0;
        for (var frame = 0; frame < 100; frame++)
        {
            while (low.TryPost(payload))
            {
            }

            bytes += s.Build(scratch);
        }

        Assert(bytes / 100 > 16 * 1024 * 0.95, $"lone Low class got only {bytes / 100} B/frame");
        return Task.CompletedTask;
    }

    public static Task OversizeUnderLoad()
    {
        var registry = new OutletRegistry();
        var s = NewOutlet(registry, o =>
        {
            o.FrameBudget = 16 * 1024;
            o.MailboxMessages = 1 << 12;
            o.MailboxBytes = 1 << 20;
            o.FlowControl = FlowControlMode.Fixed;
        });
        var book = new Table<Level, long>("BIG")
            { Delivery = Delivery.Inline }; // in-band: the scheduler's overdraft at work
        using (var u = book.BeginUpdate())
        {
            for (var i = 0; i < 3000; i++) u.Set(new Level((byte)(i & 1), 10_000 + i), 1 + i); // ~54 KB snapshot
            u.Complete();
        }

        var tap = s.Tap(book, activate: true);
        var c = new RefClient();
        var high = Poster(s, Lane.High, c);
        var scratch = new byte[1 << 20];
        var payload = new byte[200];
        var frames = 0;
        while (!c.Tables.ContainsKey(tap.Id) && frames < 16)
        {
            while (high.TryPost(payload))
            {
            }

            c.Decode(scratch.AsSpan(0, s.Build(scratch)));
            frames++;
        }

        Assert(c.Tables.TryGetValue(tap.Id, out var m) && m.Snapshots == 1, "snapshot starved");
        AssertMirror(m!, book, "oversize");
        Assert(c.BytesHigh > 0, "High should keep flowing");
        Notes.Add($"54 KB snapshot delivered in frame {frames} under saturated High");
        return Task.CompletedTask;
    }

    public static Task DeferServesHighOnly()
    {
        var registry = new OutletRegistry();
        var s = NewOutlet(registry);
        var values = new LastValues<int>("t");
        s.Tap(values[1], activate: true);
        values.Publish(1, [1, 2, 3]);
        var high = Poster(s, Lane.High);
        var normal = Poster(s, Lane.Normal);
        high.TryPost([9]);
        normal.TryPost([8]);
        var scratch = new byte[1 << 20];
        var c = new RefClient();
        c.Decode(scratch.AsSpan(0, s.Build(scratch, true)));
        Assert(c.Mail(high.Id).Count == 1 && c.Mail(normal.Id).Count == 0 && c.Values.Count == 0,
            "only High while deferring");
        c.Decode(scratch.AsSpan(0, s.Build(scratch)));
        Assert(c.Mail(normal.Id).Count == 1 && c.Values.Count == 1, "everything else afterwards");
        return Task.CompletedTask;
    }

    public static Task HeartbeatAndCredit()
    {
        HeartbeatAndCredit(false);
        HeartbeatAndCredit(true);
        return Task.CompletedTask;
    }

    private static void HeartbeatAndCredit(bool degraded)
    {
        var registry = new OutletRegistry();
        if (degraded)
            registry.SetHealth(RouterState.Degraded,
                new string('x',
                    200)); // the router's change behind its first heartbeat (232 bytes): still not a data frame
        var s = NewOutlet(registry, o => o.Window = 2);
        var owner = new object();
        s.Attach(owner); // attach queues an immediate heartbeat
        var c = new RefClient();
        var high = Poster(s, Lane.High); // after the attach (one before is held till its client re-activates)
        high.TryPost([1]);
        Assert(s.TryBuildFrame(owner, false, out var f1, out var seq1), "frame 1");
        c.Decode(f1);
        Assert(c.LastFrameRecords[0] == Wire.Heartbeat && c.LastFrameRecords.Count == 2,
            "heartbeat must lead the frame");
        s.Heartbeat();
        Assert(!s.TryBuildFrame(owner, false, out _, out _) && !s.IsReady,
            "at most one heartbeat in flight: the next waits for the echo");
        s.Ack(seq1, c.HeartbeatSeq); // frame 1 acked, its heartbeat echoed: the due one may go
        Assert(s.TryBuildFrame(owner, false, out var hb, out var hbSeq), "echo releases the next heartbeat");
        c.Decode(hb);
        Assert(c.LastFrameRecords.SequenceEqual(new[] { Wire.Heartbeat }), "heartbeat-only frame");

        high.TryPost([2]);
        Assert(s.TryBuildFrame(owner, false, out var f2, out _), "frame 2");
        c.Decode(f2);
        high.TryPost([3]);
        Assert(s.TryBuildFrame(owner, false, out var f3, out _),
            "frame 3: a heartbeat-only frame in flight doesn't hold the window");
        c.Decode(f3);
        high.TryPost([4]);
        Assert(!s.TryBuildFrame(owner, false, out _, out _), "window of 2 data frames exhausted: no data frame");
        s.Ack(hbSeq, c.HeartbeatSeq); // the heartbeat-only frame arrives: echoed, no data frame acked
        s.Heartbeat();
        Assert(s.TryBuildFrame(owner, false, out var f4, out var seq4), "heartbeat goes without credit");
        c.Decode(f4);
        Assert(c.LastFrameRecords.SequenceEqual(new[] { Wire.Heartbeat }), "without credit only the heartbeat goes");
        Assert(!s.TryBuildFrame(new object(), false, out _, out _), "a stale owner must be refused");

        Thread.Sleep(5);
        s.Ack(seq4, c.HeartbeatSeq);
        Assert(s.Rtt >= TimeSpan.FromMilliseconds(4), $"rtt {s.Rtt}");
        Assert(s.TryBuildFrame(owner, false, out var f5, out _), "ack restores credit");
        c.Decode(f5);
        Assert(c.LastFrameRecords.SequenceEqual(new[] { Wire.TopicMessage }) && seq1 == 1, "data after ack");
        Assert(
            c.RouterState == (degraded ? RouterState.Degraded : RouterState.Serving) &&
            c.RouterCorrelation == registry.Health.Correlation, "the router's state on each");
    }

    /// <summary>
    ///     A link slower than the heartbeat rate, as under Chrome's 16 kbit/s throttle (every WebSocket message waits for at
    ///     least one 750 ms packet tick): it carries one frame per two heartbeats. Heartbeats must neither pile up on it nor
    ///     hold the data window, so the backlog stays within Window data frames + one heartbeat, and the book and the value
    ///     keep arriving — conflated, ending on the latest state.
    /// </summary>
    public static Task SlowLinkHeartbeats()
    {
        // On a clock of its own: a tick — a heartbeat — is 5 ms, the link carries one frame every other tick. Flow control
        // estimates the path from that clock, so what it lets through doesn't hang on how fast this machine runs the loop.
        var clock = new ManualTime();
        var registry = new OutletRegistry(new TimingWheel(TimeSpan.FromMilliseconds(1), time: clock, autoStart: false));
        var tickTime = TimeSpan.FromMilliseconds(5);
        var s = NewOutlet(registry, o => o.Window = 2);
        var owner = new object();
        s.Attach(owner);
        var book = new Table<Level, long>("slow");
        var values = new LastValues<int>("slow");
        var bookTap = s.Tap(book, activate: true);
        var valueTap = s.Tap(values[0], activate: true);
        var c = new RefClient();
        var link = new Queue<(long Seq, byte[] Frame)>();
        var rnd = new Random(11);
        const int ticks = 400, perTick = 10;
        int maxBacklog = 0, records = 0;

        void Send()
        {
            while (s.IsReady && s.TryBuildFrame(owner, false, out var f, out var seq)) link.Enqueue((seq, f.ToArray()));
            maxBacklog = Math.Max(maxBacklog, link.Count);
        }

        void Deliver()
        {
            var (seq, frame) = link.Dequeue();
            c.Decode(frame);
            records += c.LastFrameRecords.Count(r => r is Wire.Table or Wire.Value);
            s.Ack(seq, c.HeartbeatSeq);
        }

        for (var tick = 0; tick < ticks; tick++)
        {
            for (var i = 0; i < perTick; i++)
            {
                using (var u = book.BeginUpdate())
                {
                    u.Fold<long, Add<long>>(new Level((byte)rnd.Next(2), 100 + rnd.Next(20)), rnd.Next(1, 100));
                    u.Complete();
                }

                values.Publish(0, BitConverter.GetBytes(tick * perTick + i));
            }

            s.Heartbeat();
            Send();
            if (tick % 2 == 1 && link.Count > 0) Deliver();
            clock.Advance(tickTime);
        }

        var whileSlow = records;
        Assert(maxBacklog <= 3, $"backlog grew to {maxBacklog} frames (bounded: Window data frames + one heartbeat)");
        Assert(whileSlow >= ticks / 4, $"book and value kept arriving on the slow link ({whileSlow} records)");
        Assert(c.Heartbeats >= ticks / 8, $"heartbeats kept flowing ({c.Heartbeats})");

        // Drain: flow control may be pacing at this moment — wait out its gate (on the link's clock) rather than stop early.
        Send();
        for (var i = 0; (link.Count > 0 || c.Tables[bookTap.Id].Seq != book.Version) && i < 10_000; i++)
        {
            if (link.Count > 0) Deliver();
            else clock.Advance(TimeSpan.FromMilliseconds(1));
            Send();
        }

        AssertMirror(c.Tables[bookTap.Id], book, "slow link");
        Assert(BitConverter.ToInt32(c.Values[valueTap.Id]) == ticks * perTick - 1, "the latest value arrives");
        Assert(records * 10 < 2 * ticks * perTick, $"conflated: {records} records for {2 * ticks * perTick} updates");
        Notes.Add(
            $"slow link (1 frame per 2 heartbeats): backlog ≤ {maxBacklog} frames, {records} records for {2 * ticks * perTick} updates, {c.Heartbeats} heartbeats");
        return Task.CompletedTask;
    }

    /// <summary>
    ///     A client that stops acknowledging — a page's main thread blocked, a tab frozen — while it holds a value, a table,
    ///     a busy message topic, a mailbox and a message ordered after other state. Once a frame is overdue
    ///     (<see cref="OutletOptions.AckOverdue" />), state waits: no value or table records while it's frozen, then each as
    ///     one record, the newest; the topic's messages and the mailbox's keep going, every one, in order, none overflowing
    ///     a topic buffer smaller than what was published meanwhile; a message ordered after state brings that state into
    ///     its frame. Nothing to send but state: not ready (no spinning). Off (zero), state goes on as before.
    /// </summary>
    public static Task AckOverdueHoldsState()
    {
        var held = FreezeAndThaw(TimeSpan.FromMilliseconds(50));
        var off = FreezeAndThaw(TimeSpan.Zero);

        Assert(held.Holds == 1, $"one hold for one freeze ({held.Holds})");
        Assert(held.StateWhileHeld == 0,
            $"no value or table records once a frame was overdue ({held.StateWhileHeld})");
        Assert(held.IdleWhileHeld, "state alone waiting while held: not ready (an acknowledgement wakes it)");
        Assert(held.MessagesWhileHeld == held.PublishedWhileHeld,
            $"every message published while held went while held ({held.MessagesWhileHeld} of {held.PublishedWhileHeld})");
        Assert(held.MailWhileHeld > 0, "the held value's mailbox kept going");
        Assert(held.PulledForward, "a message ordered after state brought that state into its frame while held");
        Assert(held.ValueRecordsAfter == 1 && held.TableRecordsAfter == 1,
            $"thawed: one value record and one table record ({held.ValueRecordsAfter}, {held.TableRecordsAfter})");
        Assert(held.MessagesInOrder, "the topic's messages, every one, in order");
        Assert(held.MailInOrder, "the mailbox's messages, every one, in order");
        Assert(held.Overflows == 0, $"nothing overflowed ({held.Overflows})");
        Assert(held.Converged && off.Converged, "thawed, the client has the newest value and the table as it is");
        Assert(held.MessagesFrozen == 60, $"all 60 messages published while frozen went then ({held.MessagesFrozen})");
        Assert(off.Holds == 0 && off.StateWhileHeld > 0 && off.MessagesFrozen < held.MessagesFrozen,
            $"off: state took the byte limit's room, the messages held behind it ({off.MessagesFrozen} of 60 went)");
        Notes.Add(
            $"ack overdue: frozen 300 ms — held: {held.StateWhileHeld} state records while frozen, then {held.ValueRecordsAfter} value + {held.TableRecordsAfter} table; {held.MessagesWhileHeld}/{held.PublishedWhileHeld} messages and {held.MailWhileHeld} mailbox entries meanwhile, {held.Overflows} overflows; off: {off.MessagesFrozen} of 60 messages went while frozen, {off.Overflows} overflows (held at the byte limit behind state)");
        return Task.CompletedTask;
    }

    private sealed record FreezeRun(
        long Holds,
        int StateWhileHeld,
        bool IdleWhileHeld,
        int MessagesWhileHeld,
        int PublishedWhileHeld,
        int MessagesFrozen,
        int MailWhileHeld,
        bool PulledForward,
        int ValueRecordsAfter,
        int TableRecordsAfter,
        bool MessagesInOrder,
        bool MailInOrder,
        int Overflows,
        bool Converged);

    private static FreezeRun FreezeAndThaw(TimeSpan ackOverdue)
    {
        var clock = new ManualTime();
        var registry = new OutletRegistry(new TimingWheel(TimeSpan.FromMilliseconds(1), time: clock, autoStart: false));
        var s = NewOutlet(registry, o => o.AckOverdue = ackOverdue);
        var owner = new object();
        s.Attach(owner);
        var values = new LastValues<int>("freeze");
        var book = new Table<Holding, long>("freeze-book");
        var positions = new Table<Holding, long>("freeze-positions");
        var alerts = new MessageTopic(16, 16 * 1024); // a buffer smaller than what's published while frozen
        var fills = new MessageTopic(16, 16 * 1024);
        var valueTap = s.Tap(values[0], activate: true);
        var bookTap = s.Tap(book, activate: true);
        var positionsTap = s.Tap(positions, activate: true);
        var alertsTap = s.Tap(alerts, activate: true);
        var fillsTap = s.Tap(fills, activate: true);
        var c = new RefClient();
        var unacked = new List<long>();
        var tick = TimeSpan.FromMilliseconds(5);
        int published = 0, mailPosted = 0;

        // Every frame ready, decoded as it's built (the client's copy: the server sees only what's acknowledged).
        List<(List<byte> Records, List<uint> Taps)> Send()
        {
            var frames = new List<(List<byte>, List<uint>)>();
            while (s.IsReady && s.TryBuildFrame(owner, false, out var f, out var seq))
            {
                c.Decode(f);
                c.LastFrame = seq;
                s.ReleaseFrame();
                unacked.Add(seq);
                frames.Add(([.. c.LastFrameRecords], [.. c.LastFrameTaps]));
            }

            return frames;
        }

        void AckAll()
        {
            if (unacked.Count > 0) s.Ack(unacked[^1], c.HeartbeatSeq);
            unacked.Clear();
        }

        void Change(int i)
        {
            values.Publish(0, BitConverter.GetBytes(i));
            using var u = book.BeginUpdate();
            for (var k = 0; k < 4; k++) u.Set(new Holding(k, 1), i * 10 + k); // the same rows, again and again
            u.Complete();
        }

        // Live: changes and acknowledgements every tick, a round trip of a millisecond.
        for (var i = 0; i < 40; i++)
        {
            Change(i);
            alerts.Publish(BitConverter.GetBytes(published++));
            Send();
            clock.Advance(TimeSpan.FromMilliseconds(1));
            AckAll();
            clock.Advance(tick - TimeSpan.FromMilliseconds(1));
        }

        // Frozen for 300 ms: nothing acknowledged. Changes, messages and mailbox entries go on.
        int stateWhileHeld = 0, messagesWhileHeld = 0, publishedWhileHeld = 0, mailWhileHeld = 0, messagesFrozen = 0;
        bool idle = true, pulled = false;
        for (var i = 40; i < 100; i++)
        {
            Change(i);
            alerts.Publish(BitConverter.GetBytes(published++));
            if (i % 5 == 0) valueTap.TryPost(BitConverter.GetBytes(mailPosted++));
            var orderedAfter = i == 70;
            if (orderedAfter)
            {
                using (var u = positions.BeginUpdate())
                {
                    u.Set(new Holding(1, 1), i);
                    u.Complete();
                }

                fills.Publish("fill"u8, After.Current(positions));
            }

            var frames = Send();
            if (s.HoldingState) publishedWhileHeld++; // (this tick's message: counted as the frames that carry it are)
            // (off: the same stretch of the freeze — past 60 ms — where a hold would be)
            var counted = s.HoldingState || (ackOverdue == TimeSpan.Zero && i >= 52);
            foreach (var (records, taps) in frames)
            {
                for (var r = 0; r < records.Count; r++)
                    if (records[r] == Wire.TopicMessage && taps[r] == alertsTap.Id)
                        messagesFrozen++;
                for (var r = 0; r < records.Count && counted; r++)
                    if (records[r] is Wire.Value or Wire.Table && taps[r] != positionsTap.Id)
                        stateWhileHeld++;
                if (!s.HoldingState) continue;
                for (var r = 0; r < records.Count; r++)
                {
                    var tap = taps[r];
                    if (records[r] == Wire.TopicMessage && tap == alertsTap.Id) messagesWhileHeld++;
                    if (records[r] == Wire.TopicMessage && tap == valueTap.Id) mailWhileHeld++;
                }

                if (orderedAfter)
                {
                    var p = taps.IndexOf(positionsTap.Id);
                    pulled |= p >= 0 && taps.IndexOf(fillsTap.Id) > p;
                }
            }

            if (s.HoldingState && i % 5 != 4) // (not a tick that posted to the mailbox: that may wait to go)
            {
                Change(i + 1000); // state alone changes: nothing may go, and nothing spins
                idle &= !s.IsReady;
            }

            clock.Advance(tick);
        }

        // Thawed: the client takes everything it was sent, and acknowledges it.
        var holds = s.StateHolds;
        Change(1000_000);
        AckAll();
        int valueRecords = 0, tableRecords = 0;
        for (var round = 0; round < 10; round++)
        {
            foreach (var (records, taps) in Send())
                for (var r = 0; r < records.Count; r++)
                {
                    if (records[r] == Wire.Value && taps[r] == valueTap.Id) valueRecords++;
                    if (records[r] == Wire.Table && taps[r] == bookTap.Id) tableRecords++;
                }

            clock.Advance(TimeSpan.FromMilliseconds(1));
            AckAll();
        }

        var messages = c.Mail(alertsTap.Id, false).Select(m => BitConverter.ToInt32(m)).ToList();
        var mail = c.Mail(valueTap.Id).Select(m => BitConverter.ToInt32(m)).ToList();
        var converged = c.Values.TryGetValue(valueTap.Id, out var v) && BitConverter.ToInt32(v) == 1000_000 &&
                        c.Tables[bookTap.Id].Seq == book.Version;
        return new FreezeRun(holds, stateWhileHeld, idle, messagesWhileHeld, publishedWhileHeld, messagesFrozen,
            mailWhileHeld, pulled,
            valueRecords, tableRecords, messages.SequenceEqual(Enumerable.Range(0, published)),
            mail.SequenceEqual(Enumerable.Range(0, mailPosted)), c.Errors.Count, converged);
    }

    public static Task MessageFifo()
    {
        var registry = new OutletRegistry();
        var s = NewOutlet(registry, o =>
        {
            o.MailboxMessages = 64;
            o.MailboxBytes = 1024;
            o.FrameBudget = 512;
        });
        var scratch = new byte[1 << 20];
        var c = new RefClient();
        var t = Poster(s, Lane.Normal);
        var rnd = new Random(4);
        int sent = 0, rejected = 0;
        var expected = new Queue<int>();
        for (var i = 0; i < 20_000; i++)
        {
            var payload = new byte[rnd.Next(4, 200)];
            BitConverter.TryWriteBytes(payload, sent);
            if (t.TryPost(payload)) expected.Enqueue(sent++);
            else rejected++;
            if (rnd.Next(3) == 0) c.Decode(scratch.AsSpan(0, s.Build(scratch)));
        }

        Drain(s, c, scratch);
        var got = c.Mail(t.Id);
        Assert(got.Count == sent, "lost messages");
        foreach (var p in got) Assert(BitConverter.ToInt32(p) == expected.Dequeue(), "out of order");
        Assert(rejected > 0, "ring should have filled at least once");
        return Task.CompletedTask;
    }

    // ─────────────────────────────── frame buffer ───────────────────────────────

    /// <summary>
    ///     Builds through the transport's path until nothing is ready; each frame decoded (if a client is given) and
    ///     acked.
    /// </summary>
    private static int DrainFrames(Outlet s, object owner, RefClient? c, Func<bool>? onFrame = null)
    {
        var frames = 0;
        for (var idle = 0; idle < 3 && frames < 10_000;)
        {
            if (!s.TryBuildFrame(owner, false, out var f, out var seq))
            {
                idle++;
                continue;
            }

            idle = 0;
            frames++;
            onFrame?.Invoke();
            var echo = f[0] == Wire.Heartbeat ? BinaryPrimitives.ReadUInt32LittleEndian(f[1..]) : 0;
            c?.Decode(f);
            if (c is not null) c.LastFrame = seq;
            s.ReleaseFrame(); // copied, as the transport does
            s.Ack(seq, c?.HeartbeatSeq ?? echo);
        }

        return frames;
    }

    public static Task FrameBuffer()
    {
        var registry = new OutletRegistry();
        var s = NewOutlet(registry, o => o.FlowControl = FlowControlMode.Fixed);
        var owner = new object();
        s.Attach(owner);
        var c = new RefClient();
        var values = new LastValues<int>("fb");
        var vtap = s.Tap(values[0], activate: true);
        values.Publish(0, new byte[1000]);
        DrainFrames(s, owner, c);
        Assert(s.FrameBufferBytes == Wire.MaxHeartbeatSize + Wire.ValueHeader + ushort.MaxValue,
            $"the outlet's own frame buffer: {s.FrameBufferBytes} bytes");
        Assert(s.FrameBufferBytes < 85_000 && !s.HoldsLargeFrame, "off the large object heap; no large frame");

        var book = new Table<Level, long>("FB-INLINE") { Delivery = Delivery.Inline };
        FillBook(book, 3000); // 6,000 rows: a ~150 KB snapshot, always in-band
        var btap = s.Tap(book, activate: true);
        values.Publish(0, new byte[10]);
        var large = false;
        DrainFrames(s, owner, c, () => large |= s.HoldsLargeFrame);
        Assert(large, "a full-size frame was rented for it");
        Assert(c.Tables[btap.Id].Seq == book.Version && c.Tables[btap.Id].Rows.Count == book.Count && !btap.Oversize,
            "the snapshot arrived whole");
        Assert(c.Values[vtap.Id].Length == 10, "the small value alongside");
        Assert(!s.HoldsLargeFrame && s.FrameBufferBytes < 85_000,
            "returned at the next build; the outlet's own buffer unchanged");

        var huge = new Table<Level, long>("FB-HUGE") { Delivery = Delivery.Inline };
        FillBook(huge, 30_000); // ~1.5 MB: more than MaxFrame
        var htap = s.Tap(huge, activate: true);
        DrainFrames(s, owner, c);
        Assert(htap.Oversize && !c.Tables.ContainsKey(htap.Id) && !s.HoldsLargeFrame,
            "larger than MaxFrame: oversize, as before");

        // No threshold (in band whatever fits a frame): an Auto table's snapshot larger than the outlet's own buffer takes
        // a full-size frame too, not a fetch; one larger than MaxFrame goes out of band.
        var open = NewOutlet(registry, o => (o.FlowControl, o.OutOfBandThreshold) = (FlowControlMode.Fixed, 0));
        var openOwner = new object();
        open.Attach(openOwner);
        var oc = new RefClient();
        var auto = new Table<Level, long>("FB-AUTO");
        FillBook(auto, 3000); // ~150 KB
        var atap = open.Tap(auto, activate: true);
        large = false;
        DrainFrames(open, openOwner, oc, () => large |= open.HoldsLargeFrame);
        Assert(large && oc.Pending.Count == 0 && oc.Tables[atap.Id].Rows.Count == auto.Count,
            "no threshold: a ~150 KB snapshot in band, in a full-size frame");
        var beyond = new Table<Level, long>("FB-AUTO-HUGE");
        FillBook(beyond, 30_000); // ~1.5 MB
        var ytap = open.Tap(beyond, activate: true);
        DrainFrames(open, openOwner, oc);
        Assert(oc.Pending.Any(p => p.TapId == ytap.Id), "no threshold, larger than MaxFrame: out of band");
        return Task.CompletedTask;
    }

    public static Task FrameBufferLifetime()
    {
        var registry = new OutletRegistry();
        var pool = registry.FrameBuffers;

        Outlet Attached(out object owner)
        {
            var o = NewOutlet(registry, x => x.FlowControl = FlowControlMode.Fixed);
            o.Attach(owner = new object());
            return o;
        }

        var a = Attached(out var ownerA);
        Poster(a, Lane.Normal).TryPost([1, 2, 3]);
        Assert(a.TryBuildFrame(ownerA, false, out var f, out _), "a frame");
        var sent = f.ToArray();
        a.Dispose(); // while the transport is still copying it
        Assert(pool.Stats.IdleBytes == 0 && f.SequenceEqual(sent),
            "disposed with its frame out: the buffer stays the frame's");
        a.ReleaseFrame();
        Assert(pool.Stats.IdleBytes > 0, "released: back in the pool");
#if DEBUG
        Assert(f[0] == 0xDD, "DEBUG: poisoned once pooled");
#endif
        var idle = pool.Stats.IdleBytes;
        a.ReleaseFrame(); // twice: nothing more
        Assert(pool.Stats.IdleBytes == idle, "a second release changes nothing");

        var b = Attached(out var ownerB);
        Poster(b, Lane.Normal).TryPost([4]);
        Assert(b.TryBuildFrame(ownerB, false, out _, out _), "the next circuit's frame");
        Assert(pool.Stats is { Created: 1, Reused: 1, IdleBytes: 0 }, $"…in the same buffer: {pool.Stats}");
        b.ReleaseFrame();
        b.Dispose(); // nothing out: back at once
        Assert(pool.Stats.IdleBytes == idle, "disposed with nothing out: pooled at once");

        for (var i = 0; i < 100; i++)
        {
            var c = Attached(out var owner);
            Poster(c, Lane.Normal).TryPost([5]);
            c.TryBuildFrame(owner, false, out _, out _);
            if ((i & 1) == 0) c.ReleaseFrame();
            c.Dispose();
            if ((i & 1) != 0) c.ReleaseFrame(); // half released after the dispose
        }

        Assert(pool.Stats.Created == 1 && pool.Stats.IdleBytes == idle, $"100 clients, one buffer: {pool.Stats}");

        // A large frame out at dispose: its rented buffer, likewise.
        var d = Attached(out var ownerD);
        var book = new Table<Level, long>("FBL-INLINE") { Delivery = Delivery.Inline };
        FillBook(book, 3000);
        d.Tap(book, activate: true);
        Assert(d.TryBuildFrame(ownerD, false, out _, out _), "the heartbeat first");
        d.ReleaseFrame();
        Assert(d.TryBuildFrame(ownerD, false, out var big, out _) && d.HoldsLargeFrame && big.Length > 100_000,
            "then the snapshot, in a large frame");
        var head = big[..64].ToArray();
        d.Dispose();
        Assert(d.HoldsLargeFrame && big[..64].SequenceEqual(head), "disposed with it out: still the frame's");
        d.ReleaseFrame();
        Assert(!d.HoldsLargeFrame, "released: back to the shared pool");
#if DEBUG
        Assert(big[0] == 0xDD, "DEBUG: poisoned once returned");
#endif
        return Task.CompletedTask;
    }

    public static async Task DataProviders()
    {
        var registry = new OutletRegistry();
        var publisher = new Publisher(registry);
        var s = NewOutlet(registry, o => o.BindTimeout = TimeSpan.FromMilliseconds(50));
        var scratch = new byte[1 << 20];
        var c = new RefClient();
        var stream = new LastValueSource();
        var provider = new TestProvider(stream, InitialState.None) { Refuse = "no" };
        Provide(publisher, "stream", "stream", provider);
        bool twice = false, unknown = false;
        try
        {
            publisher.Provide("stream", At("stream"), provider);
        }
        catch (InvalidOperationException)
        {
            twice = true;
        }

        try
        {
            await publisher.SubscribeAsync(s, "nothing");
        }
        catch (ArgumentException)
        {
            unknown = true;
        }

        Assert(twice && unknown, "an endpoint provides a service once; an unknown service is an error");
        Assert(await publisher.SubscribeAsync(s, "stream", "no") is null, "refused: no route");

        var tap = await publisher.SubscribeAsync(s, "stream");
        Assert(
            tap is { IsActive: false, Topic: "stream" } && stream.Routes == 1 && provider.Live && provider.Epoch == 1,
            "accepted: a bound route, counted, epoch 1");
        Assert(tap.Provider?.Endpoint == At("stream"), "the route records the endpoint that accepted it");
        Assert(stream.Publish([1]) is { Status: PublishStatus.Delivered, Epoch: 1, Seq: 1 },
            "201: a route between its two handshakes counts (and the version it made)");
        tap!.Activate();
        Drain(s, c, scratch);
        Assert(c.Values[tap.Id].SequenceEqual(new byte[] { 1 }),
            "a stream starts at the next change after step 1: one published between the handshakes is sent");
        stream.Publish([2]);
        Drain(s, c, scratch);
        Assert(c.Values[tap.Id].SequenceEqual(new byte[] { 2 }), "…and the next");
        s.Attach(new object());
        s.Resume(0, 0); // (frames built directly aren't numbered: as if none arrived)
        Drain(s, c, scratch);
        Assert(c.Values[tap.Id].SequenceEqual(new byte[] { 2 }),
            "after a reconnect, on from what the client received: the latest value again");
        stream.Publish([3]);
        Drain(s, c, scratch);
        Assert(c.Values[tap.Id].SequenceEqual(new byte[] { 3 }) && c.Frames > 0, "…then the next change");

        tap.Dispose();
        var gone = await NextNotice(provider);
        Assert(gone is { Notice: { Service: "stream", Epoch: 1, Remaining: 0 }, RoutesThen: 0 } &&
               gone.Value.Notice.Topic == stream
               && gone.Value.Notice.Route == new RouteId(s.Id, tap.Id),
            "unsubscribed: the provider told after the route was removed — which route, epoch 1, none left");
        var nack = stream.Publish([4]);
        Assert(nack is { Status: PublishStatus.NoListeners, Epoch: 1 } && stream.Routes == 0,
            "…and its publish: 202, epoch 1");
        provider.Acknowledged(nack);
        Assert(!provider.Live, "…the provider stops");
        Assert(stream.Publish([5]) is { Status: PublishStatus.NoListeners } idle && idle.Epoch == nack.Epoch,
            "202 again (the same epoch) while nobody subscribes");

        var again = await publisher.SubscribeAsync(s, "stream");
        Assert(provider.Live && provider.Epoch == 2 && stream.Epoch == 2, "resubscribed: a new epoch");
        provider.Acknowledged(nack); // the old 202, arriving late
        Assert(provider.Live, "a 202 from epoch 1 is stale: still producing");
        Assert(stream.Publish([6]) is { Status: PublishStatus.Delivered, Epoch: 2 }, "201, epoch 2");
        again!.Dispose();

        // A table: its initial state is its snapshot (the default); a table can't start without one.
        var book = new Table<Level, long>("PROVIDED");
        FillBook(book, 10);
        var tables = new TestProvider(book);
        Provide(publisher, "book", "book", tables);
        var bookTap = await publisher.SubscribeAsync(s, "book");
        bookTap!.Activate();
        Drain(s, c, scratch);
        Assert(c.Tables[bookTap.Id].Snapshots == 1 && c.Tables[bookTap.Id].Rows.Count == book.Count,
            "a table: its snapshot first");
        var needsBase = false;
        try
        {
            s.Tap(book, initial: InitialState.None);
        }
        catch (ArgumentException)
        {
            needsBase = true;
        }

        Assert(needsBase, "a table can't start without its state");

        // The mailbox: the provider's own snapshot, first; nothing of the topic's older than it after it.
        var shared = new LastValueSource();
        shared.Publish([9, 9]); // the topic's state: not this subscriber's
        bool early = false, notTable = false;
        var mailing = new TestProvider(shared) { Then = r => r.Mailbox.Send([7, 7, 7]) };
        Provide(publisher, "mail", "mail", mailing);
        var mailTap = await publisher.SubscribeAsync(s, "mail");
        mailTap!.Activate();
        Drain(s, c, scratch);
        Assert(c.Values[mailTap.Id].SequenceEqual(new byte[] { 7, 7, 7 }),
            "the mailbox's snapshot, not the topic's state");
        shared.Publish([8]);
        Drain(s, c, scratch);
        Assert(c.Values[mailTap.Id].SequenceEqual(new byte[] { 8 }), "then the topic's next change");
        publisher.Provide("early", At("early"), new EarlyMail());
        try
        {
            await publisher.SubscribeAsync(s, "early");
        }
        catch (InvalidOperationException)
        {
            early = true;
        }

        tables.Then = r => r.Mailbox.Send([1]);
        try
        {
            await publisher.SubscribeAsync(s, "book");
        }
        catch (InvalidOperationException)
        {
            notTable = true;
        }

        Assert(early && notTable, "a mailbox snapshot needs the topic (accepted first), and a whole-value topic");

        // A route never completed (no step 2) expires: then nobody listens.
        var lonely = new LastValueSource();
        Provide(publisher, "lonely", "lonely", new TestProvider(lonely, InitialState.None));
        var orphan = await publisher.SubscribeAsync(s, "lonely");
        Assert(lonely.Publish([1]).HasListeners, "bound: counted");
        await Task.Delay(120);
        s.Heartbeat(); // expires unactivated taps
        Assert(orphan!.IsClosed && lonely.Routes == 0 && !lonely.Publish([2]).HasListeners,
            "an orphaned route expires: 202");

        // Notices for routes that end otherwise: an orphan expiring, a connection ending; a stale one after a resubscribe.
        var noticed = new LastValueSource();
        var told = new TestProvider(noticed, InitialState.None, actOnNotices: true);
        Provide(publisher, "told", "told", told);
        var expiring = await publisher.SubscribeAsync(s, "told");
        await Task.Delay(120);
        s.Heartbeat();
        Assert(await NextNotice(told) is { Notice.Remaining: 0 }, "an orphan's expiry is a notice too: none left");
        Assert(!told.Live, "…the provider (acting on notices) stops");
        var other = NewOutlet(registry);
        await publisher.SubscribeAsync(other, "told");
        var renewed = told.Epoch;
        other.Dispose(); // its connection ended
        var ended = await NextNotice(told);
        Assert(ended is { Notice.Remaining: 0 } && ended.Value.Notice.Epoch == renewed,
            "a connection ending: a notice for each of its routes");
        var late = new Unsubscription(publisher.Router, default, told.Instance!, "told", null, noticed, renewed, 0);
        await publisher.SubscribeAsync(s, "told"); // a new subscriber first…
        await told.UnsubscribeAsync(late, default); // …then the earlier notice, late
        Assert(told.Live && told.Epoch == renewed + 1,
            "the last route's removal ended the epoch: a late notice from it is stale");
    }

    private static ProviderEndpoint At(string name)
    {
        return ProviderEndpoint.InProcess(name);
    }

    /// <summary>
    ///     Registers <paramref name="provider" /> for <paramref name="service" /> at endpoint <paramref name="at" />, as
    ///     instance <paramref name="at" /> (once).
    /// </summary>
    private static void Provide(Publisher publisher, string service, string at, TestProvider provider)
    {
        provider.Instance ??= ProviderInstance.Start(at, At(at));
        publisher.Provide(service, At(at), provider);
    }

    /// <summary>The next step 2 or end relayed to <paramref name="provider" />, or null if none comes within a second.</summary>
    /// <summary>The provider's next relayed step 2 or end, or null: waited for up to 10 s (<see cref="Eventually" />).</summary>
    private static async Task<object?> NextRelayed(TestProvider provider)
    {
        object? m = null;
        return await Eventually(() => provider.Relayed.TryDequeue(out m)) ? m : null;
    }

    public static async Task ProviderEndpoints()
    {
        var registry = new OutletRegistry();
        var publisher = new Publisher(registry);
        var s = NewOutlet(registry, o => o.BindTimeout = TimeSpan.FromMilliseconds(50));
        var topic = new LastValueSource();
        var a = new TestProvider(topic, InitialState.None);
        var b = new TestProvider(topic, InitialState.None);
        Provide(publisher, "svc", "a", a);
        Provide(publisher, "svc", "b", b);
        var taken = false;
        try
        {
            publisher.Provide("other", At("a"), b);
        }
        catch (InvalidOperationException)
        {
            taken = true;
        }

        Provide(publisher, "also", "a", a); // one provider, two services, one endpoint
        Assert(
            taken && publisher.EndpointsOf("svc").SequenceEqual([At("a"), At("b")]) &&
            publisher.EndpointsOf("also").SequenceEqual([At("a")]),
            "an endpoint is one provider's; a service may have several, an endpoint several services");

        // Step 1 to each endpoint in turn; each answer names its own, recorded on the route.
        var taps = new Tap[4];
        for (var i = 0; i < taps.Length; i++) taps[i] = (await publisher.SubscribeAsync(s, "svc"))!;
        Assert(
            a.Subscribes == 2 && b.Subscribes == 2 && taps.Select(t => t.Provider?.Endpoint)
                .SequenceEqual([At("a"), At("b"), At("a"), At("b")]),
            "step 1 to each endpoint in turn; each route records the one that accepted it");
        Assert(taps.All(t => t.Binding == new TapBinding(t.Id, "svc", s.Endpoint, "svc", t.Binding.NodeId)) &&
               taps[0].Binding.NodeId == taps[2].Binding.NodeId
               && taps[0].Binding.NodeId != taps[1].Binding.NodeId && taps.All(t =>
                   t.Binding.NodeId != a.Instance!.Id && t.Binding.NodeId != b.Instance!.Id),
            "the client's binding is the router's alone: its tap, the topic, this node, and a transient id per instance — nothing of which endpoint or instance answered");

        // Step 2 and the end, relayed to that endpoint alone, in order.
        taps[0].Activate();
        taps[0].Activate(); // a re-sent activation: once
        taps[1].Dispose();
        var first = await NextRelayed(a);
        var second = await NextRelayed(b);
        Assert(
            first is Activation { Service: "svc", Epoch: 1 } act && act.Route == new RouteId(s.Id, taps[0].Id) &&
            act.Topic == topic,
            "step 2 relayed to the endpoint holding the route: which route, its topic and epoch");
        Assert(second is Unsubscription { Remaining: 3 } u && u.Route == new RouteId(s.Id, taps[1].Id),
            "an end relayed to the endpoint holding that route");
        await Task.Delay(30);
        Assert(a.Relayed.IsEmpty && b.Relayed.IsEmpty, "…and to no other (nor twice)");
        taps[2].Activate();
        taps[2].Dispose();
        Assert(
            await NextRelayed(a) is Activation { Route.Tap: var t2 } && t2 == taps[2].Id &&
            await NextRelayed(a) is Unsubscription { Route.Tap: var t2e } && t2e == taps[2].Id,
            "a route's step 2 and end, in that order");
        await Task.Delay(120);
        s.Heartbeat(); // taps[3], never activated: expires
        Assert(taps[3].IsClosed && await NextRelayed(b) is Unsubscription { Route.Tap: var t3 } && t3 == taps[3].Id,
            "an orphan: its end only");
        taps[3].Activate();
        await Task.Delay(30);
        Assert(b.Relayed.IsEmpty, "an activation after the end: nothing to relay");

        // An answer may name another endpoint the router knows; one it doesn't is refused.
        var shard = new TestProvider(topic, InitialState.None);
        Provide(publisher, "shard", "shard", shard);
        var front = new TestProvider(topic, InitialState.None) { AcceptAs = shard.Instance };
        Provide(publisher, "front", "front", front);
        var routed = (await publisher.SubscribeAsync(s, "front"))!;
        routed.Activate();
        Assert(
            routed.Provider?.Endpoint == At("shard") && await NextRelayed(shard) is Activation && front.Relayed.IsEmpty,
            "named another endpoint: its step 2 goes there");
        routed.Dispose();
        Assert(await NextRelayed(shard) is Unsubscription && front.Relayed.IsEmpty, "…and its end");
        front.AcceptAs = ProviderInstance.Start("nowhere", At("nowhere"));
        var unreachable = false;
        try
        {
            await publisher.SubscribeAsync(s, "front");
        }
        catch (ArgumentException)
        {
            unreachable = true;
        }

        Assert(unreachable, "an endpoint the router can't reach is refused");

        // Withdrawn: no more step 1s to it; its routes' notices have nowhere to go.
        var held = (await publisher.SubscribeAsync(s, "svc"))!; // svc's turn: a
        Assert(held.Provider?.Endpoint == At("a"), "a's turn");
        publisher.Withdraw(At("a"));
        var before = b.Subscribes;
        var after1 = await publisher.SubscribeAsync(s, "svc");
        var after2 = await publisher.SubscribeAsync(s, "svc");
        Assert(
            b.Subscribes == before + 2 && after1!.Provider?.Endpoint == At("b") &&
            after2!.Provider?.Endpoint == At("b") && publisher.EndpointsOf("also").Count == 0,
            "withdrawn: every step 1 to the endpoints left (a service with none left is gone)");
        Assert(held.IsClosed && (await held.Completion)?.Code == FaultCode.ServiceDown && taps[0].IsClosed,
            "withdrawn: the instance there dead — its routes closed, the service down");
        held.Activate();
        held.Dispose();
        await Task.Delay(30);
        Assert(a.Relayed.IsEmpty, "a withdrawn endpoint's routes: nothing relayed");
        var gone = false;
        try
        {
            await publisher.SubscribeAsync(s, "also");
        }
        catch (ServiceUnavailableException e)
        {
            gone = e.Service == "also";
        }

        publisher.Withdraw(At("b"));
        var none = false;
        try
        {
            await publisher.SubscribeAsync(s, "svc");
        }
        catch (ServiceUnavailableException)
        {
            none = true;
        }

        Assert(gone && none, "no endpoint left: the service unavailable (known, but down — not unknown)");
    }

    /// <summary>
    ///     Calls the router's provider heartbeat every 10 ms until <paramref name="done" /> (at most 2 s); false if it
    ///     never was.
    /// </summary>
    /// <summary>Heartbeats the providers every 10 ms until <paramref name="done" /> holds, or 10 s pass (<see cref="Eventually" />).</summary>
    private static Task<bool> HeartbeatUntil(Publisher publisher, Func<bool> done)
    {
        return Eventually(done, publisher.HeartbeatProviders, every: 10);
    }

    /// <summary>
    ///     Waits for <paramref name="done" />, looked at every <paramref name="every" /> ms — <paramref name="each" /> run
    ///     between looks (a heartbeat, frames drained) — up to <paramref name="ms" /> (10 s): true if it held. What a test
    ///     waits for happens in milliseconds; the deadline is for a machine too loaded to run it sooner, and a passing test
    ///     never waits it out. (What a test expects not to happen it waits a set time for instead: a fixed delay.)
    /// </summary>
    private static async Task<bool> Eventually(Func<bool> done, Action? each = null, int ms = 10_000, int every = 5)
    {
        var until = Deadline(ms);
        while (true)
        {
            if (done()) return true;
            if (Stopwatch.GetTimestamp() >= until) return false;
            each?.Invoke();
            await Task.Delay(every);
        }
    }

    /// <summary>The Stopwatch timestamp <paramref name="ms" /> from now (10 s): a wait's deadline.</summary>
    private static long Deadline(int ms = 10_000)
    {
        return Stopwatch.GetTimestamp() + ms * Stopwatch.Frequency / 1000;
    }

    /// <summary>
    ///     Runs <paramref name="each" /> every 10 ms for <paramref name="ms" />: a stretch of time a test needs to pass with
    ///     something going on (heartbeats past a timeout many times over), by the clock, however few rounds it gets.
    /// </summary>
    private static async Task For(int ms, Action each)
    {
        var until = Deadline(ms);
        while (Stopwatch.GetTimestamp() < until)
        {
            each();
            await Task.Delay(10);
        }
    }

    public static async Task ProviderInstances()
    {
        var registry = new OutletRegistry();
        var publisher = new Publisher(registry)
            { ProviderHeartbeatInterval = TimeSpan.Zero, ProviderTimeout = TimeSpan.FromMilliseconds(150) };
        var s = NewOutlet(registry);
        var owner = new object();
        s.Attach(owner);
        var c = new RefClient();
        var topic = new LastValueSource();
        var station = new TestProvider(topic, InitialState.None);
        Provide(publisher, "svc", "station", station);
        var idle = new TestProvider(new LastValueSource(), InitialState.None);
        Provide(publisher, "idle", "idle", idle);

        // The answer names the instance; the router tracks it and gives the client a transient id for it — nothing more.
        var t1 = (await publisher.SubscribeAsync(s, "svc"))!;
        var t2 = (await publisher.SubscribeAsync(s, "svc"))!;
        var first = station.Instance!;
        var service = t1.Binding.NodeId ?? Guid.Empty;
        Assert(t1.Provider == first && service != Guid.Empty && service != first.Id && t2.Binding.NodeId == service,
            "the route held by the instance that answered; the client's binding: the router's transient id for it, the same for its other routes");
        t1.Activate();
        t2.Activate();
        DrainFrames(s, owner, c);

        // Heartbeats while it holds routes — to the instance expected, one at a time — answered as itself: alive.
        await For(2_000, publisher.HeartbeatProviders); // (its timeout many times over)
        Assert(
            !t1.IsClosed && station.Heartbeats > 10 &&
            station.LastExpected == first && idle.Heartbeats == 0,
            "answered as itself, past the timeout many times over: alive; heartbeats to the instance expected; none to an instance without routes");

        // Restarted: a heartbeat answered with a new id — the old instance dead: its routes closed, the client told once, nothing relayed.
        while (station.Relayed.TryDequeue(out _))
        {
        }

        station.Instance = ProviderInstance.Start("station", At("station"));
        Assert(await HeartbeatUntil(publisher, () => t1.IsClosed && t2.IsClosed),
            "a new id at the instance's address: its routes closed");
        Assert(
            (await t1.Completion)?.Code == FaultCode.ServiceDown &&
            (await t2.Completion)?.Code == FaultCode.ServiceDown && topic.Routes == 0 && publisher.InstancesDown == 1,
            "…with ServiceDown (the server side sees it in Tap.Completion); no route left on the topic");
        DrainFrames(s, owner, c);
        Assert(c.ServicesDown.SequenceEqual([service]),
            "the client told the service is down, once for both its routes — by the transient id");
        await Task.Delay(30);
        Assert(station.Relayed.IsEmpty,
            "nothing relayed for the dead instance's routes (its successor never held them)");

        // The dead instance can't accept; the new one does — another transient id.
        station.AcceptAs = first;
        var refused = false;
        try
        {
            await publisher.SubscribeAsync(s, "svc");
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }

        station.AcceptAs = null;
        var t3 = (await publisher.SubscribeAsync(s, "svc"))!;
        Assert(
            refused && t3.Provider == station.Instance && t3.Binding.NodeId is { } service3 && service3 != service &&
            service3 != Guid.Empty, "a dead instance's answer refused; the new instance's route, a new transient id");
        t3.Activate();

        // Silent: no answer within the timeout — dead; one heartbeat in flight at a time, abandoned when it is.
        var abandoned = false;
        var before = station.Heartbeats;
        station.Health = async (probe, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                abandoned = true;
                throw;
            }

            throw new UnreachableException();
        };
        Assert(await HeartbeatUntil(publisher, () => t3.IsClosed), "silent past the timeout: dead");
        await Eventually(() => abandoned); // (the check in flight cancelled on the router's own time)
        Assert(
            (await t3.Completion)?.Code == FaultCode.ServiceDown && station.Heartbeats == before + 1 && abandoned &&
            publisher.InstancesDown == 2,
            "its routes closed (ServiceDown); a single heartbeat was in flight, abandoned once it was deemed dead");
        station.Health = null;

        // A new id answering step 1 at the instance's name and endpoint supersedes it (a restart, learned from an answer).
        station.Instance = ProviderInstance.Start("station", At("station"));
        var t4 = (await publisher.SubscribeAsync(s, "svc"))!;
        station.Instance = ProviderInstance.Start("station", At("station"));
        var t5 = (await publisher.SubscribeAsync(s, "svc"))!;
        Assert(
            t4.IsClosed && (await t4.Completion)?.Code == FaultCode.ServiceDown && !t5.IsClosed &&
            t5.Binding.NodeId != t4.Binding.NodeId && publisher.InstancesDown == 3,
            "an answer from a new id at the same name and endpoint: the instance before it dead");

        // Withdrawn: the instance there dead. Its notice, in a frame lost with the connection (never acked), goes again.
        DrainFrames(s, owner, c);
        c.ServicesDown.Clear();
        t5.Activate();
        publisher.Withdraw(At("station"));
        Assert(t5.IsClosed && (await t5.Completion)?.Code == FaultCode.ServiceDown, "withdrawn: its routes closed");
        Assert(s.TryBuildFrame(owner, false, out var lost, out _), "a frame with the notice");
        c.Decode(lost);
        s.ReleaseFrame(); // …never acked: the connection drops
        var owner2 = new object();
        s.Attach(owner2);
        DrainFrames(s, owner2, c);
        DrainFrames(s, owner2, c);
        Assert(c.ServicesDown.Count == 2 && c.ServicesDown.All(x => x == t5.Binding.NodeId),
            "the notice sent again on the new connection, then (acked) no more");

        // Heartbeats only while the instance holds routes.
        var quiet = new TestProvider(new LastValueSource(), InitialState.None);
        Provide(publisher, "quiet", "quiet", quiet);
        var q = (await publisher.SubscribeAsync(s, "quiet"))!;
        await HeartbeatUntil(publisher, () => quiet.Heartbeats >= 3);
        q.Dispose();
        var sent = quiet.Heartbeats;
        for (var i = 0; i < 10; i++)
        {
            publisher.HeartbeatProviders();
            await Task.Delay(5);
        }

        Assert(sent >= 3 && quiet.Heartbeats == sent, "its last route gone: no more heartbeats");
    }

    public static async Task ServiceUpNotices()
    {
        var registry = new OutletRegistry();
        var publisher = new Publisher(registry)
        {
            ProviderHeartbeatInterval = TimeSpan.Zero, ProviderTimeout = TimeSpan.FromMilliseconds(150),
            ServiceUp = new ServiceUpPolicy()
        };

        (Outlet Outlet, object Owner, RefClient Client) Viewer()
        {
            var o = NewOutlet(registry);
            var owner = new object();
            o.Attach(owner);
            return (o, owner, new RefClient());
        }

        var topic = new LastValueSource();

        // One instance, the default policy (a quorum of one): down, then — the instance back — up, to the waiting client alone.
        var solo = new TestProvider(topic, InitialState.None);
        Provide(publisher, "solo", "solo", solo);
        var (s, owner, c) = Viewer();
        var (bystander, bOwner, bc) = Viewer();
        var tap = (await publisher.SubscribeAsync(s, "solo"))!;
        Assert(tap.Binding.Service == "solo", "the binding names the service (what an up notice names)");
        tap.Activate();
        solo.Instance = ProviderInstance.Start("solo", At("solo")); // restarted
        Assert(await HeartbeatUntil(publisher, () =>
        {
            DrainFrames(s, owner, c);
            return c.ServicesUp.Count > 0;
        }), "up, once the instance is back");
        DrainFrames(bystander, bOwner, bc);
        Assert(
            c.ServicesDown.Count == 1 && c.ServicesUp.SequenceEqual(["solo"]) && bc.ServicesUp.Count == 0 &&
            bc.ServicesDown.Count == 0,
            "down, then up by name (no instance) — to the client that was waiting, not to one that wasn't");
        var again = (await publisher.SubscribeAsync(s, "solo"))!;
        Assert(again.Binding.NodeId != tap.Binding.NodeId, "subscribed again: the new instance");

        // Two instances, a quorum of two, a grace period: one of them gone, one up — held until the grace period has passed.
        publisher.SetServiceUp("pair", new ServiceUpPolicy(2, TimeSpan.FromMilliseconds(400)));
        var p1 = new TestProvider(topic, InitialState.None);
        var p2 = new TestProvider(topic, InitialState.None);
        Provide(publisher, "pair", "p1", p1);
        Provide(publisher, "pair", "p2", p2);
        var (s2, owner2, c2) = Viewer();
        var r1 = (await publisher.SubscribeAsync(s2, "pair"))!;
        var r2 = (await publisher.SubscribeAsync(s2, "pair"))!;
        r1.Activate();
        r2.Activate();
        var (silent, live) = r1.Provider!.Endpoint == At("p2") ? (r1, r2) : (r2, r1);
        p2.Health = async (probe, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new UnreachableException();
        }; // p2 silent: dead in time
        Assert(await HeartbeatUntil(publisher, () => silent.IsClosed), "the silent instance's route down");
        var downAt = Stopwatch.GetTimestamp();
        Assert(await HeartbeatUntil(publisher, () =>
        {
            DrainFrames(s2, owner2, c2);
            return c2.ServicesUp.Count > 0;
        }), "up, in the end");
        var held = Stopwatch.GetElapsedTime(downAt);
        Notes.Add(
            $"service up: one instance of two up — held {held.TotalMilliseconds:F0} ms (grace 400 ms; a quorum of two not met)");
        Assert(held >= TimeSpan.FromMilliseconds(350) && c2.ServicesUp.SequenceEqual(["pair"]) && !live.IsClosed,
            "one of two instances up: held until the grace period passed");

        // Both up: no wait for the grace period.
        var (s3, owner3, c3) = Viewer();
        p2.Health = null;
        p2.Instance = ProviderInstance.Start("p2", At("p2")); // p2 back (a new instance)
        var q1 = (await publisher.SubscribeAsync(s3, "pair"))!;
        var q2 = (await publisher.SubscribeAsync(s3, "pair"))!;
        Assert(!q1.IsClosed && !q2.IsClosed && q1.Provider?.Endpoint != q2.Provider?.Endpoint,
            "both instances answering");
        q1.Activate();
        q2.Activate();
        p1.Instance = ProviderInstance.Start("p1", At("p1"));
        Assert(await HeartbeatUntil(publisher, () => q1.IsClosed || q2.IsClosed), "one restarted: its route down");
        downAt = Stopwatch.GetTimestamp();
        Assert(await HeartbeatUntil(publisher, () =>
        {
            DrainFrames(s3, owner3, c3);
            return c3.ServicesUp.Count > 0;
        }), "up");
        held = Stopwatch.GetElapsedTime(downAt);
        Assert(held < TimeSpan.FromMilliseconds(350),
            $"a quorum up (the other instance, and the restarted one back): told without waiting out the grace period ({held.TotalMilliseconds:F0} ms)");

        // A subscribe finding no instance: unavailable, and the outlet waits — told when an endpoint is provided.
        var (s4, owner4, c4) = Viewer();
        publisher.Withdraw(At("solo"));
        var unavailable = false;
        try
        {
            await publisher.SubscribeAsync(s4, "solo");
        }
        catch (ServiceUnavailableException e)
        {
            unavailable = e.Service == "solo";
        }

        for (var i = 0; i < 10; i++)
        {
            publisher.HeartbeatProviders();
            await Task.Delay(10);
        }

        DrainFrames(s4, owner4, c4);
        Assert(unavailable && c4.ServicesUp.Count == 0, "no instance: unavailable — and not up while there is none");
        var successor = new TestProvider(topic, InitialState.None);
        Provide(publisher, "solo", "solo-2", successor);
        Assert(await HeartbeatUntil(publisher, () =>
        {
            DrainFrames(s4, owner4, c4);
            return c4.ServicesUp.Count > 0;
        }), "an instance provided: the waiting client told");
        Assert(await publisher.SubscribeAsync(s4, "solo") is { IsClosed: false }, "…and it subscribes");
        for (var i = 0; i < 5; i++)
        {
            publisher.HeartbeatProviders();
            await Task.Delay(10);
        }

        DrainFrames(s4, owner4, c4);
        Assert(c4.ServicesUp.Count == 1, "subscribed again: no longer waiting (no further notice)");
    }

    public static async Task SubscribeLifecycle()
    {
        var registry = new OutletRegistry();
        var publisher = new Publisher(registry) { SubscribeTimeout = TimeSpan.FromMilliseconds(100) };
        var s = NewOutlet(registry);
        var topic = new LastValueSource();
        var provider = new TestProvider(topic, InitialState.None);
        Provide(publisher, "svc", "svc", provider);
        SubscribeRequest? kept = null;

        // Never answers (nor heeds the token): unavailable after the timeout; its late accept refused; no route.
        var never = new TaskCompletionSource();
        provider.Instead = (r, _) =>
        {
            kept = r;
            return new ValueTask(never.Task);
        };
        bool unavailable = false, late = false;
        try
        {
            await publisher.SubscribeAsync(s, "svc");
        }
        catch (ServiceUnavailableException)
        {
            unavailable = true;
        }

        try
        {
            kept!.Accept(topic, provider.Instance!);
        }
        catch (InvalidOperationException)
        {
            late = true;
        }

        Assert(unavailable && late && topic.Routes == 0,
            "a provider that never answers: unavailable after the timeout, a late accept refused, no route");

        // Accepted, then stuck: the route closed at once (told as an unsubscribe), not left to expire.
        provider.Instead = (r, _) =>
        {
            r.Accept(topic, provider.Instance!);
            return new ValueTask(never.Task);
        };
        unavailable = false;
        try
        {
            await publisher.SubscribeAsync(s, "svc");
        }
        catch (ServiceUnavailableException)
        {
            unavailable = true;
        }

        Assert(unavailable && topic.Routes == 0 && await NextRelayed(provider) is Unsubscription { Remaining: 0 },
            "accepted, then stuck: the route closed at once, an unsubscribe relayed");

        // Accepted, then the caller gave up (cancelled): likewise.
        using (var cts = new CancellationTokenSource())
        {
            provider.Instead = async (r, ct) =>
            {
                r.Accept(topic, provider.Instance!);
                cts.Cancel();
                await Task.Delay(Timeout.Infinite, ct);
            };
            var cancelled = false;
            try
            {
                await publisher.SubscribeAsync(s, "svc", cancellationToken: cts.Token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            Assert(cancelled && topic.Routes == 0 && await NextRelayed(provider) is Unsubscription,
                "cancelled after the accept: no route left");
        }

        // Accepted, then threw: the exception the caller's, the route closed.
        provider.Instead = (r, _) =>
        {
            r.Accept(topic, provider.Instance!);
            throw new InvalidDataException("provider bug");
        };
        var threw = false;
        try
        {
            await publisher.SubscribeAsync(s, "svc");
        }
        catch (InvalidDataException)
        {
            threw = true;
        }

        Assert(threw && topic.Routes == 0 && await NextRelayed(provider) is Unsubscription,
            "accepted, then threw: the route closed");

        // The circuit ended while the provider decided: its accept binds nothing, and it's told as an unsubscribe.
        var gone = NewOutlet(registry);
        provider.Instead = async (r, _) =>
        {
            await Task.Yield();
            gone.Dispose();
            r.Accept(topic, provider.Instance!);
        };
        var orphan = await publisher.SubscribeAsync(gone, "svc");
        Assert(
            orphan is { IsClosed: true } && topic.Routes == 0 &&
            await NextRelayed(provider) is Unsubscription { Remaining: 0 },
            "accepted after the circuit ended: no route; an unsubscribe relayed");
        provider.Instead = null;

        // Binds racing a dispose: none left behind.
        var leaked = 0;
        for (var round = 0; round < 300; round++)
        {
            var o = NewOutlet(registry);
            var racing = new LastValueSource();
            using var go = new ManualResetEventSlim();
            var binders = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
            {
                go.Wait();
                for (var i = 0; i < 20; i++) o.Tap(racing);
            })).ToArray();
            var disposer = Task.Run(() =>
            {
                go.Wait();
                Thread.SpinWait(Random.Shared.Next(0, 2000));
                o.Dispose();
            });
            go.Set();
            Task.WaitAll([.. binders, disposer]);
            if (racing.Routes > 0) leaked++;
        }

        Assert(leaked == 0, $"{leaked} of 300 rounds left routes bound on a disposed outlet");
    }

    public static async Task ProviderDrain()
    {
        var registry = new OutletRegistry();
        var publisher = new Publisher(registry)
        {
            ProviderHeartbeatInterval = TimeSpan.Zero, ProviderTimeout = TimeSpan.FromSeconds(2),
            ServiceUp = new ServiceUpPolicy()
        };
        var topic = new LastValueSource();
        var pa = new TestProvider(topic, InitialState.None);
        var pb = new TestProvider(topic, InitialState.None);
        Provide(publisher, "svc", "a", pa);
        Provide(publisher, "svc", "b", pb);

        (Outlet Outlet, object Owner, RefClient Client) Viewer()
        {
            var o = NewOutlet(registry);
            var owner = new object();
            o.Attach(owner);
            return (o, owner, new RefClient());
        }

        // Six clients, a route on each instance (step 1 alternates) — and client 0 a second one on a.
        var clients = Enumerable.Range(0, 6).Select(_ => Viewer()).ToArray();
        var onA = new List<(int Client, Tap Tap)>();
        var onB = new List<Tap>();

        async Task<Tap> Subscribe(int client)
        {
            var t = (await publisher.SubscribeAsync(clients[client].Outlet, "svc"))!;
            t.Activate();
            if (t.Provider!.Endpoint == At("a")) onA.Add((client, t));
            else onB.Add(t);
            return t;
        }

        for (var i = 0; i < clients.Length; i++)
        {
            await Subscribe(i);
            await Subscribe(i);
        }

        while (onA.Count(x => x.Client == 0) < 2) await Subscribe(0);
        Assert(onA.Count == 7 && onB.Count >= 6, $"routes: {onA.Count} on a, {onB.Count} on b");
        var serviceA = onA[0].Tap.Binding.NodeId;
        await Eventually(() => pa.Relayed.Count >= onA.Count);
        while (pa.Relayed.TryDequeue(out _))
        {
        } // (their activations)

        // a drains over 600 ms: it stops taking subscribers, and tells its routers — one way.
        var closedAt = new ConcurrentDictionary<Tap, TimeSpan>();
        var started = Stopwatch.GetTimestamp();
        foreach (var (_, t) in onA)
            _ = t.Completion.ContinueWith(_ => closedAt[t] = Stopwatch.GetElapsedTime(started),
                TaskContinuationOptions.ExecuteSynchronously);
        pa.Status = ProviderStatus.Draining;
        await pa.Routers.DrainAsync(TimeSpan.FromMilliseconds(600));

        // No step 1s to it from now on — even once it says it serves again.
        var (fresh, _, _) = Viewer();
        var asked = pa.Subscribes;
        var allB = true;
        for (var i = 0; i < 4; i++)
            allB &= (await publisher.SubscribeAsync(fresh, "svc"))?.Provider?.Endpoint == At("b");
        pa.Status = ProviderStatus.Serving;
        for (var i = 0; i < 3; i++)
        {
            publisher.HeartbeatProviders();
            await Task.Delay(10);
        }

        allB &= (await publisher.SubscribeAsync(fresh, "svc"))?.Provider?.Endpoint == At("b");
        Assert(allB && pa.Subscribes == asked, "draining: no step 1s to it — one way, whatever it reports after");

        // Its clients let go one at a time, evenly over the 600 ms — each with all its routes there at once.
        await Eventually(() => closedAt.Count >= onA.Count);
        Assert(closedAt.Count == onA.Count, $"all its routes let go ({closedAt.Count} of {onA.Count})");
        var perClient = onA.GroupBy(x => x.Client)
            .Select(g => (Min: g.Min(x => closedAt[x.Tap]), Max: g.Max(x => closedAt[x.Tap]))).OrderBy(x => x.Min)
            .ToArray();
        var at = perClient.Select(x => x.Min.TotalMilliseconds).ToArray();
        var gaps = at.Zip(at.Skip(1), (x, y) => y - x).ToArray();
        Notes.Add(
            $"drain over 600 ms, 6 clients: let go at {string.Join(", ", at.Select(x => $"{x:F0}"))} ms (evenly: every 100)");
        Assert(perClient.All(x => (x.Max - x.Min).TotalMilliseconds < 30), "a client's routes there let go together");
        Assert(at[0] >= 50 && at[^1] >= 550 && at[^1] <= 900 && gaps.All(g => g >= 40 && g <= 250),
            $"evenly spaced over the drain: {string.Join(", ", at.Select(x => $"{x:F0}"))}");
        Assert(
            onA.All(x => x.Tap.Completion.Result?.Code == FaultCode.ServiceDrained) && onB.All(t => !t.IsClosed) &&
            publisher.RoutesDrained == onA.Count,
            "each closed as drained (not down); the routes on b untouched");
        await Task.Delay(30);
        Assert(!pa.Relayed.Any(m => m is Unsubscription), "nothing relayed for the routes it let go (it asked for it)");

        // Each client told drained — not down, and not waiting for up: it subscribes again at once, onto b.
        await Eventually(() =>
        {
            foreach (var c in clients) DrainFrames(c.Outlet, c.Owner, c.Client);
            return clients.All(c => c.Client.ServicesDrained.Count > 0);
        }, publisher.HeartbeatProviders, every: 10);
        Assert(
            clients.All(c =>
                c.Client.ServicesDrained.SequenceEqual([serviceA!.Value]) && c.Client.ServicesDown.Count == 0 &&
                c.Client.ServicesUp.Count == 0),
            "each told drained (the draining instance's id) — not down, no up to wait for");
        Assert((await publisher.SubscribeAsync(clients[0].Outlet, "svc"))?.Provider?.Endpoint == At("b"),
            "subscribing again: onto b");

        // A new instance at a: it takes step 1s again.
        pa.Instance = ProviderInstance.Start("a", At("a"));
        var backOnA = false;
        for (var until = Deadline(); !backOnA && Stopwatch.GetTimestamp() < until;)
        {
            publisher.HeartbeatProviders();
            await Task.Delay(10);
            backOnA = (await publisher.SubscribeAsync(fresh, "svc"))?.Provider?.Endpoint == At("a");
        }

        Assert(backOnA, "a new instance at the endpoint: step 1s again");
    }

    /// <summary>
    ///     A provider reached over a link, saying whether it's reached (<see cref="IReachableProvider" />): step 1 goes to no
    ///     endpoint whose provider isn't — each goes to the one reached, at once, none waiting out SubscribeTimeout — and with
    ///     none reached, it's unavailable at once. Reached again, it's probed at once, not at the next heartbeat (the test
    ///     beats none): the client waiting on the service told it's up, and its step 1 answered there.
    /// </summary>
    public static async Task ProviderReachable()
    {
        var registry = new OutletRegistry();
        var publisher = new Publisher(registry)
            { ProviderHeartbeatInterval = TimeSpan.FromMinutes(1), ServiceUp = new ServiceUpPolicy() };
        var topic = new LastValueSource();
        var (pa, pb) = (new TestProvider(topic, InitialState.None), new TestProvider(topic, InitialState.None));
        (pa.Instance, pb.Instance) = (ProviderInstance.Start("a", At("a")), ProviderInstance.Start("b", At("b")));
        var (a, b) = (new Reachability(pa, true), new Reachability(pb, false)); // (b registered before it runs)
        publisher.Provide("svc", At("a"), a);
        publisher.Provide("svc", At("b"), b);
        var outlet = NewOutlet(registry);
        var owner = new object();
        outlet.Attach(owner);
        var client = new RefClient();

        var timer = Stopwatch.StartNew();
        var onA = 0;
        for (var i = 0; i < 6; i++)
            if ((await publisher.SubscribeAsync(outlet, "svc"))?.Provider?.Endpoint == At("a"))
                onA++;
        Assert(onA == 6 && pb.Subscribes == 0 && timer.Elapsed < TimeSpan.FromSeconds(1),
            $"b not reached: every step 1 to a, at once ({onA} of 6; {pb.Subscribes} to b; {timer.ElapsedMilliseconds} ms)");

        b.Set(true);
        var onB = 0;
        for (var i = 0; i < 6; i++)
            if ((await publisher.SubscribeAsync(outlet, "svc"))?.Provider?.Endpoint == At("b"))
                onB++;
        Assert(onB > 0, "b reached: step 1s go to it too");

        a.Set(false);
        b.Set(false);
        timer.Restart();
        var unavailable = await ThrewAsync<ServiceUnavailableException>(() => publisher.SubscribeAsync(outlet, "svc").AsTask());
        Assert(unavailable is not null && timer.Elapsed < TimeSpan.FromSeconds(1),
            $"none reached: unavailable at once, not after SubscribeTimeout ({timer.ElapsedMilliseconds} ms)");

        a.Set(true); // (its link up: probed at once, its answer the waiting client's up)
        await Eventually(() => client.ServicesUp.Count > 0, () => DrainFrames(outlet, owner, client), every: 10);

        Assert(client.ServicesUp.SequenceEqual(["svc"]), "a reached again: the waiting client told the service is up, with no heartbeat");
        Assert((await publisher.SubscribeAsync(outlet, "svc"))?.Provider?.Endpoint == At("a"), "its step 1 answered there");
    }

    public static async Task BlueGreen()
    {
        var registry = new OutletRegistry();
        var publisher = new Publisher(registry)
        {
            ProviderHeartbeatInterval = TimeSpan.Zero, ProviderTimeout = TimeSpan.FromMilliseconds(300),
            ServiceUp = new ServiceUpPolicy()
        };
        var blueTopic = new LastValueSource();
        var greenTopic = new LastValueSource();
        var blue = new TestProvider(blueTopic, InitialState.None);
        Provide(publisher, "waveform", "waveform-blue", blue);
        var viewers = new (Outlet Outlet, object Owner, RefClient Client, Tap Old)[6]; // the last won't move
        for (var i = 0; i < viewers.Length; i++)
        {
            var o = NewOutlet(registry);
            var owner = new object();
            o.Attach(owner);
            var t = (await publisher.SubscribeAsync(o, "waveform"))!;
            t.Activate();
            viewers[i] = (o, owner, new RefClient(), t);
        }

        var blueId = viewers[0].Old.Binding.NodeId!.Value;
        Assert(viewers.All(v => v.Old.Provider!.Name == "waveform-blue"), "blue serves them all");

        // Green starts alongside — another instance name, its own endpoint — then blue drains: 500 ms, each viewer 300 ms to move.
        var green = new TestProvider(greenTopic, InitialState.None);
        Provide(publisher, "waveform", "waveform-green", green);
        blue.Status = ProviderStatus.Draining;
        var started = Stopwatch.GetTimestamp();
        await blue.Routers.DrainAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(300));
        var newcomer = NewOutlet(registry);
        var allGreen = true;
        for (var i = 0; i < 4; i++)
            allGreen &= (await publisher.SubscribeAsync(newcomer, "waveform"))?.Provider?.Name == "waveform-green";
        Assert(allGreen, "new subscriptions: all to green, the one ready");
        var report = await blue.CheckHealthAsync(new HealthProbe(publisher.Router, blue.Instance), default);
        Assert(report is { Status: ProviderStatus.Draining, Ready: false, RouterRefused: false },
            "draining: alive, not ready (no new subscribers) — not down");

        // Make before break: each viewer, told to move, subscribes again onto green while its blue subscription streams on;
        // once green streams to it, it lets blue's go. The last viewer doesn't move: its grace runs out.
        var toldAt = new TimeSpan?[viewers.Length];
        var fresh = new Tap?[viewers.Length];
        var overlapped = new bool[viewers.Length];
        var dropped = new bool[viewers.Length];
        byte n = 0;
        for (var until = Deadline(); Stopwatch.GetTimestamp() < until && (dropped.Take(5).Any(d => !d) || !viewers[5].Old.IsClosed);)
        {
            publisher.HeartbeatProviders(); // blue checked all along: not deemed dead
            n++;
            blueTopic.Publish([n]);
            greenTopic.Publish([n]);
            for (var i = 0; i < viewers.Length; i++)
            {
                var (o, owner, c, old) = viewers[i];
                DrainFrames(o, owner, c);
                if (toldAt[i] is null && c.ServicesDraining.Count > 0) toldAt[i] = Stopwatch.GetElapsedTime(started);
                if (i == 5 || toldAt[i] is null || dropped[i]) continue;
                if (fresh[i] is null)
                {
                    fresh[i] = (await publisher.SubscribeAsync(o, "waveform"))!; // onto green
                    fresh[i]!.Activate();
                }
                else if (c.Values.TryGetValue(fresh[i]!.Id, out var g) && g[0] == n) // green streams to it…
                {
                    overlapped[i] =
                        !old.IsClosed && c.Values.TryGetValue(old.Id, out var b) && b[0] == n; // …and blue still does
                    o.Close(old.Id, FaultCode.None); // let blue's go (the client's unsubscribe)
                    dropped[i] = true;
                }
            }

            await Task.Delay(10);
        }

        foreach (var v in viewers) DrainFrames(v.Outlet, v.Owner, v.Client);
        var told = toldAt.Select(t => t?.TotalMilliseconds ?? double.NaN).Order().ToArray();
        Notes.Add(
            $"blue/green, make before break: blue drained over 500 ms — viewers told to move at {string.Join(", ", told.Select(x => $"{x:F0}"))} ms; 5 moved with an overlap, 1 left to its 300 ms grace");
        Assert(viewers.All(v => v.Client.ServicesDraining.SequenceEqual([(blueId, 300u)])),
            "each told to move: blue's id, the grace");
        Assert(told.All(double.IsFinite) && told[^1] - told[0] >= 250,
            $"told one at a time over the drain: {string.Join(", ", told.Select(x => $"{x:F0}"))}");
        Assert(
            Enumerable.Range(0, 5)
                .All(i => fresh[i]?.Provider?.Name == "waveform-green" && overlapped[i] && dropped[i]),
            "each mover: onto green, both streaming at once for a while (each under its own tap id), then blue's let go");
        Assert(
            Enumerable.Range(0, 5).All(i =>
                viewers[i].Old.Completion.Result is null && viewers[i].Client.ServicesDrained.Count == 0 &&
                viewers[i].Client.ServicesDown.Count == 0),
            "the movers: blue's subscription ended by their own unsubscribe — never drained, never down");
        var lazy = viewers[5];
        Assert(
            lazy.Old.IsClosed && lazy.Old.Completion.Result?.Code == FaultCode.ServiceDrained &&
            lazy.Client.ServicesDrained.SequenceEqual([blueId]),
            "the one that didn't move: closed when its grace ran out — drained (subscribe again now)");
        Assert(publisher.ClientsMoved == 6 && publisher.RoutesDrained == 1 && publisher.InstancesDown == 0,
            "six told to move, one closed by the router; blue never deemed dead");
        Assert(blue.Notices.Count >= 5, "the movers' unsubscribes relayed to blue, as any");

        // Drained, blue goes: withdrawn, with nobody left to tell.
        publisher.Withdraw(At("waveform-blue"));
        foreach (var v in viewers) DrainFrames(v.Outlet, v.Owner, v.Client);
        Assert(viewers.All(v => v.Client.ServicesDown.Count == 0) && publisher.InstancesDown == 1,
            "withdrawn once drained: its instance gone, nobody told");
    }

    public static async Task RouterIdentity()
    {
        // Time-ordered per name.
        var r1 = RouterInstance.Start("edge");
        Thread.Sleep(2);
        var r2 = RouterInstance.Start("edge");
        Assert(r2.Supersedes(r1) && !r1.Supersedes(r2) && !r1.Supersedes(r1) &&
               !RouterInstance.Start("core").Supersedes(r1)
               && r2.Started >= r1.Started && Math.Abs((r1.Started - DateTimeOffset.UtcNow).TotalSeconds) < 5,
            "UUIDv7: newer ids of a name supersede older; its timestamp, when it started");

        var registry = new OutletRegistry();
        var topic = new LastValueSource();

        (Outlet Outlet, object Owner, RefClient Client) Viewer()
        {
            var o = NewOutlet(registry);
            var owner = new object();
            o.Attach(owner);
            return (o, owner, new RefClient());
        }

        Publisher Router(RouterInstance? id = null, TimeSpan? timeout = null)
        {
            return new Publisher(registry)
            {
                Router = id ?? RouterInstance.Start("r"), ProviderHeartbeatInterval = TimeSpan.Zero,
                ProviderTimeout = timeout ?? TimeSpan.FromSeconds(2), ServiceUp = new ServiceUpPolicy()
            };
        }

        TestProvider Provider(TimeSpan timeout, Func<IRouterChannel, IRouterChannel>? wrap = null)
        {
            var p = new TestProvider(topic, InitialState.None) { Wrap = wrap };
            p.Routers.HeartbeatInterval = TimeSpan.Zero;
            p.Routers.Timeout = timeout;
            return p;
        }

        // Introduced, named in every message; heartbeats both ways while it holds routes.
        var older = Router(r1);
        var refusedBy = new List<ProviderEndpoint>();
        older.RefusedBy += (e, _) =>
        {
            lock (refusedBy)
            {
                refusedBy.Add(e);
            }
        };
        var provider = Provider(TimeSpan.FromMilliseconds(150));
        Provide(older, "svc", "p", provider);
        Assert(provider.Connections.Count == 1 && provider.Connections[0].Router == r1,
            "introduced: the router's identity, and a channel back");
        var (o, owner, c) = Viewer();
        var tap = (await older.SubscribeAsync(o, "svc"))!;
        tap.Activate();
        Assert(await NextRelayed(provider) is Activation { Router: var named } && named == r1,
            "its messages name the router");
        older.HeartbeatProviders();
        Assert(provider.LastProbe?.Router == r1 && provider.Routers.RoutesOf(r1) == 1,
            "its health checks too; the provider keeps the route under it");
        await For(2_000, provider.Routers.HeartbeatRouters); // the provider's heartbeats, 2 s — its timeout many times over
        Assert(provider.Lost.IsEmpty && provider.Routers.RoutesOf(r1) == 1,
            "the router answering the provider's heartbeats: alive");

        // A newer router of the name: the older superseded — its routes forgotten, its step 1s NACKed, what it held closed.
        var newer = Router(r2);
        Provide(newer, "svc", "p", provider);
        Assert(
            provider.Lost.TryDequeue(out var lost) && lost.Router == r1 && lost.Routes.Length == 1 &&
            provider.Routers.RoutesOf(r1) == 0,
            "a newer router of the name: the older's routes forgotten");
        Assert(!provider.Routers.Admits(r1) && provider.Routers.Admits(r2), "the older refused from then on");
        var nacked = false;
        try
        {
            await older.SubscribeAsync(o, "svc");
        }
        catch (ServiceUnavailableException)
        {
            nacked = true;
        }

        Assert(
            nacked && tap.IsClosed && (await tap.Completion)?.Code == FaultCode.ServiceDown &&
            refusedBy.SequenceEqual([At("p")]),
            "the older's step 1 NACKed: unavailable — and what it held there closed, RefusedBy raised");
        DrainFrames(o, owner, c);
        Assert(c.ServicesDown.Count == 1, "its client told the service is down");
        var (o2, _, _) = Viewer();
        Assert(await newer.SubscribeAsync(o2, "svc") is { IsClosed: false }, "the newer served");

        // A router silent to the provider (a partition): presumed dead past the timeout; it finds out at its next health check.
        var far = Router();
        var farRefused = 0;
        far.RefusedBy += (_, _) => Interlocked.Increment(ref farRefused);
        Partitioned? partition = null;
        var cut = Provider(TimeSpan.FromMilliseconds(150), ch => partition = new Partitioned(ch));
        var farHealth = new ConcurrentQueue<ServiceHealthChange>();
        far.ServiceHealthChanged += farHealth.Enqueue;
        Provide(far, "far", "cut", cut);
        var (o3, _, _) = Viewer();
        var t3 = (await far.SubscribeAsync(o3, "far"))!;
        t3.Activate();
        Assert(await Eventually(() => !cut.Lost.IsEmpty, cut.Routers.HeartbeatRouters, every: 10),
            "the provider's heartbeats unanswered: the router presumed dead");
        Assert(
            cut.Lost.TryDequeue(out var gone) && gone.Router == far.Router && gone.Routes.Length == 1 &&
            !cut.Routers.Admits(far.Router),
            "its routes forgotten, and it refused from then on");
        Assert(
            await HeartbeatUntil(far, () => t3.IsClosed) && (await t3.Completion)?.Code == FaultCode.ServiceDown &&
            farRefused == 1,
            "the router's next health check: refused — it closes what it held there");

        // The partition heals: the router alive after all. Asked again, the provider admits it afresh — no routes, what it
        // held forgotten — and the router takes the instance it deemed dead back, as a new node: served again, and kept.
        partition!.Healed = true;
        var (o3b, _, _) = Viewer();
        var t3b = await SubscribedAsync(far, o3b, "far");
        Assert(t3b is { IsClosed: false } && cut.Routers.Admits(far.Router) && cut.Routers.RoutesOf(far.Router) == 1,
            "healed: admitted afresh — its step 1 served again, its one route the new one");
        Assert(farHealth.Any(c => c.Event == ServiceHealthEvent.Recovered && c.Reason.Contains("taken back")),
            "the router's log: the instance taken back");
        t3b!.Activate();
        await For(2_000, () =>
        {
            cut.Routers.HeartbeatRouters();
            far.HeartbeatProviders();
        });
        Assert(cut.Lost.IsEmpty && !t3b.IsClosed, "heartbeats answered both ways again: kept");

        // A router that no longer knows the instance (deemed it dead): the provider, heartbeating it, forgets those routes.
        var near = Router(timeout: TimeSpan.FromMilliseconds(150));
        var mute = Provider(TimeSpan.FromSeconds(2));
        Provide(near, "near", "mute", mute);
        var (o4, _, _) = Viewer();
        var t4 = (await near.SubscribeAsync(o4, "near"))!;
        t4.Activate();
        mute.Health = async (probe, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new UnreachableException();
        }; // it can't answer the router…
        Assert(await HeartbeatUntil(near, () => t4.IsClosed), "…which deems it dead");
        Assert(await Eventually(() => !mute.Lost.IsEmpty, mute.Routers.HeartbeatRouters, every: 10) &&
               mute.Lost.TryDequeue(out var dropped) && dropped.Routes.Length == 1
               && mute.Routers.Admits(near.Router) && mute.Routers.RoutesOf(near.Router) == 0,
            "its own heartbeat answered 'unknown instance': those routes forgotten — the router still served");

        // The provider answers again (it was alive, only slow): the router takes the instance it deemed dead back.
        mute.Health = null;
        var (o4b, _, _) = Viewer();
        Assert(await SubscribedAsync(near, o4b, "near") is { IsClosed: false } && mute.Routers.RoutesOf(near.Router) == 1,
            "answering healthy again: taken back as a new node, its step 1s served");
    }

    /// <summary>A step 1 retried, heartbeats between, until the service is up again (null if it isn't within ~2 s).</summary>
    private static async Task<Tap?> SubscribedAsync(Publisher router, Outlet outlet, string service)
    {
        for (var i = 0; i < 200; i++)
        {
            router.HeartbeatProviders();
            try
            {
                if (await router.SubscribeAsync(outlet, service) is { } tap) return tap;
            }
            catch (ServiceUnavailableException)
            {
            }

            await Task.Delay(10);
        }

        return null;
    }

    public static async Task StepOneElsewhere()
    {
        var registry = new OutletRegistry();
        var topic = new LastValueSource();
        var router = new Publisher(registry)
        {
            ProviderHeartbeatInterval = TimeSpan.Zero, ProviderTimeout = TimeSpan.FromSeconds(30),
            ServiceUp = new ServiceUpPolicy()
        };
        TestProvider[] providers = [.. "abc".Select(_ => new TestProvider(topic, InitialState.None))];
        for (var i = 0; i < providers.Length; i++) Provide(router, "svc", "abc"[i].ToString(), providers[i]);
        ValueTask NotHere(SubscribeRequest r, CancellationToken _)
        {
            r.Refuse(RefusalKind.Elsewhere, "not the writer", null);
            return default;
        }

        (providers[0].Instead, providers[1].Instead) = (NotHere, NotHere); // c alone serves it (a writer's, say)
        var o = NewOutlet(registry);
        o.Attach(new object());
        var charged = o.Describe().MemoryBytes;
        var told = new List<SubscriptionRefusal>();
        router.SubscriptionRefused += told.Add;

        // Wherever a step 1 goes first, the router asks the next instance not asked: it lands on c, in the one call.
        for (var i = 0; i < 12; i++)
        {
            var tap = (await router.SubscribeAsync(o, "svc"))!;
            Assert(tap.Provider!.Endpoint == At("c"), $"step 1 {i}: on the instance that serves it ({tap.Provider.Endpoint})");
            tap.Dispose();
        }

        Assert(providers[2].Subscribes == 12 && providers[0].Subscribes + providers[1].Subscribes is > 0 and <= 24 && told.Count == 0,
            $"each on c; a and b asked on the way, never told ({providers[0].Subscribes}, {providers[1].Subscribes})");

        // Every instance says another serves it: each asked once, then the last refusal told, of its kind.
        providers[2].Instead = NotHere;
        var before = providers.Select(p => p.Subscribes).ToArray();
        var refused = await ThrewAsync<SubscriptionRefusedException>(() => router.SubscribeAsync(o, "svc").AsTask());
        Assert(refused?.Refusal is { Kind: RefusalKind.Elsewhere, Reason: "not the writer" } && told.Count == 1 &&
               providers.Select((p, i) => p.Subscribes - before[i]).All(n => n == 1),
            $"none serves it: each asked once, the refusal told ({refused?.Refusal.Kind})");

        // Any other refusal is the answer: not asked elsewhere.
        foreach (var p in providers)
            p.Instead = (r, _) =>
            {
                r.Refuse("no such thing");
                return default;
            };
        before = [.. providers.Select(p => p.Subscribes)];
        var plain = await ThrewAsync<SubscriptionRefusedException>(() => router.SubscribeAsync(o, "svc").AsTask());
        Assert(plain?.Refusal.Kind == RefusalKind.Refused && providers.Select((p, i) => p.Subscribes - before[i]).Sum() == 1,
            "a plain refusal: told at once, no other instance asked");
        Assert(o.Describe().MemoryBytes == charged, "no reservation left held: each step 1's let go, bound or refused");
    }

    public static async Task LoadAwarePlacement()
    {
        var registry = new OutletRegistry();
        var topic = new LastValueSource();
        var router = new Publisher(registry)
        {
            ProviderHeartbeatInterval = TimeSpan.Zero, ProviderTimeout = TimeSpan.FromSeconds(30),
            ServiceUp = new ServiceUpPolicy()
        };
        TestProvider[] providers = [.. "abc".Select(_ => new TestProvider(topic, InitialState.None))];
        for (var i = 0; i < providers.Length; i++) Provide(router, "svc", "abc"[i].ToString(), providers[i]);
        var o = NewOutlet(registry);
        o.Attach(new object());

        // None says how loaded it is: each in turn.
        var held = new List<Tap>();
        for (var i = 0; i < 6; i++) held.Add((await router.SubscribeAsync(o, "svc"))!);
        Assert(providers.All(p => p.Subscribes == 2), "no loads reported: each instance in turn");

        // They say (heard on the health checks of the instances holding routes): 0.9, 0.5, 0.1.
        (providers[0].Load, providers[1].Load, providers[2].Load) = (0.9, 0.5, 0.1);
        Assert(await HeartbeatUntil(router, () => router.Describe().Endpoints.All(e => e.Load is not null)),
            "the loads heard on the health checks");

        async Task<int[]> Place(int choices, int n)
        {
            router.ProviderChoices = choices;
            var before = providers.Select(p => p.Subscribes).ToArray();
            for (var i = 0; i < n; i++) (await router.SubscribeAsync(o, "svc"))!.Dispose();
            return [.. providers.Select((p, i) => p.Subscribes - before[i])];
        }

        var two = await Place(2, 900);
        Assert(two[0] == 0 && two[1] is > 230 and < 370 && two[2] is > 530 and < 670,
            $"two random choices: the busiest never, the middle a third, the idlest two thirds ({string.Join(", ", two)})");
        var all = await Place(3, 60);
        Assert(all[2] == 60, $"all three picked: the least loaded always ({string.Join(", ", all)})");
        var one = await Place(1, 900);
        Assert(one.All(c => c is > 230 and < 370), $"one picked: at random ({string.Join(", ", one)})");

        // An instance that hasn't said counts as idle.
        var quiet = new TestProvider(topic, InitialState.None);
        Provide(router, "svc", "d", quiet);
        var withQuiet = await Place(4, 40);
        Assert(quiet.Subscribes == 40 && withQuiet.All(c => c == 0), "one not reporting a load counts as idle");
        foreach (var t in held) t.Dispose();

        // A health check answered from memory, a struct: nothing allocated.
        IProviderHealth health = providers[0];
        var probe = new HealthProbe(router.Router, providers[0].Instance);
        for (var i = 0; i < 100; i++) _ = health.CheckHealthAsync(probe, default).Result;
        var start = AllocationBaseline();
        for (var i = 0; i < 100_000; i++) _ = health.CheckHealthAsync(probe, default).Result;
        var spent = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert(spent < 8 * 1024, $"100,000 health checks answered: {spent:N0} B allocated");
    }

    public static async Task SubscriberLimitsCarried()
    {
        var registry = new OutletRegistry();
        var publisher = new Publisher(registry);
        var source = new LastValueSource();
        source.Publish([1]);
        var provider = new TestProvider(source);
        Provide(publisher, "svc", "svc", provider);

        // None asked, no cap: none shown.
        var plain = NewOutlet(registry);
        var free = (await publisher.SubscribeAsync(plain, "svc"))!;
        Assert(provider.Last!.Limits.IsNone && free.MinInterval == TimeSpan.Zero, "none asked, none capped: none shown");

        // A rate of its own, on a capped connection (bwlimit 64 KiB/s), in the high lane.
        var capped = NewOutlet(registry);
        capped.SetStreamingRate(64 * 1024);
        var tap = (await publisher.SubscribeAsync(capped, "svc", "book",
            new SubscribeOptions { MinInterval = TimeSpan.FromMilliseconds(250), Lane = Lane.High }))!;
        Assert(provider.Last!.Limits is { BytesPerSecond: 64 * 1024 } limits &&
               limits.MinInterval == TimeSpan.FromMilliseconds(250),
            $"the provider shown its rate and its connection's cap ({provider.Last.Limits})");
        Assert(tap.MinInterval == TimeSpan.FromMilliseconds(250), "the rate set on the route as it binds");
        Assert(tap.Lane == Lane.High, "and the lane taken");

        // The connection's cap alone: shown, the rate none.
        var unasked = (await publisher.SubscribeAsync(capped, "svc"))!;
        Assert(provider.Last!.Limits is { MinInterval.Ticks: 0, BytesPerSecond: 64 * 1024 } && unasked.MinInterval == TimeSpan.Zero,
            "the cap alone: shown; no rate");

        var negative = await ThrewAsync<ArgumentOutOfRangeException>(() =>
            publisher.SubscribeAsync(plain, "svc", null, new SubscribeOptions { MinInterval = TimeSpan.FromSeconds(-1) }).AsTask());
        Assert(negative is not null, "a negative rate refused");
        foreach (var t in new[] { free, tap, unasked }) t.Dispose();
    }

    public static Task LoadMeterReads()
    {
        // Half busy: 50 ms of work a 100 ms window — about 0.5 (sleeps overshoot: a little less).
        var meter = new LoadMeter(window: TimeSpan.FromMilliseconds(100));
        double load = 0;
        for (var i = 0; i < 3; i++)
        {
            meter.AddTicks(Stopwatch.Frequency / 20);
            Thread.Sleep(100);
            load = meter.Load;
        }

        Assert(load is > 0.3 and < 0.6, $"half busy: about 0.5 ({load:F2})");
        Assert(meter.Load == load, "read again within the window: as it was");

        // Idle: halfway to nothing each window.
        Thread.Sleep(110);
        var once = meter.Load;
        Thread.Sleep(110);
        var twice = meter.Load;
        Assert(once < load * 0.6 && twice < once * 0.6, $"idle: it decays ({load:F2} → {once:F2} → {twice:F2})");

        // Per worker: four workers' second of work in a quarter of a second, on four — all busy, about 1.
        var pool = new LoadMeter(4, TimeSpan.FromMilliseconds(250));
        pool.AddTicks(Stopwatch.Frequency);
        Thread.Sleep(250);
        Assert(pool.Load is > 0.8 and <= 1.0, $"four workers, all busy: about 1 ({pool.Load:F2})");

        // A scope: its time counted.
        var timed = new LoadMeter(window: TimeSpan.FromMilliseconds(100));
        using (timed.Measure()) Thread.Sleep(60);
        Thread.Sleep(50);
        Assert(timed.Load is > 0.4 and < 0.7, $"a scope of 60 ms in about 110: its share ({timed.Load:F2})");

        // Allocation-free: measuring and reading.
        var quick = new LoadMeter(window: TimeSpan.Zero);
        for (var i = 0; i < 100; i++)
        {
            using (quick.Measure())
            {
            }

            _ = quick.Load;
        }

        var start = AllocationBaseline();
        for (var i = 0; i < 100_000; i++)
        {
            using (quick.Measure())
            {
            }

            _ = quick.Load;
        }

        var spent = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert(spent < 8 * 1024, $"measured and read 100,000 times: {spent:N0} B allocated");
        return Task.CompletedTask;
    }

    public static async Task RebalanceMoves()
    {
        var registry = new OutletRegistry();
        var router = new Publisher(registry)
        {
            ProviderHeartbeatInterval = TimeSpan.Zero, ProviderTimeout = TimeSpan.FromSeconds(30),
            ServiceUp = new ServiceUpPolicy(), MoveDwell = TimeSpan.Zero
        };
        var topic = new LastValueSource();
        var pa = new TestProvider(topic, InitialState.None);
        var pb = new TestProvider(topic, InitialState.None);
        Provide(router, "svc", "a", pa);

        (Outlet Outlet, object Owner, RefClient Client) Viewer()
        {
            var o = NewOutlet(registry);
            var owner = new object();
            o.Attach(owner);
            return (o, owner, new RefClient());
        }

        var clients = Enumerable.Range(0, 4).Select(_ => Viewer()).ToArray();
        var old = new Tap[clients.Length];
        for (var i = 0; i < clients.Length; i++)
        {
            old[i] = (await router.SubscribeAsync(clients[i].Outlet, "svc"))!;
            old[i].Activate();
        }

        // One instance: nowhere to move to — refused, saying so.
        var alone = await pa.Routers.MoveAsync(null, null, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1), "rebalance");
        Assert(alone is [{ Answer: { Accepted: false, Refused: { } why } }] && why.Contains("nowhere"),
            $"one instance: refused, nowhere to move to ({string.Join(", ", alone.Select(x => x.Answer))})");

        // A second instance, idle; a busy: its four subscriptions moved over 200 ms, each with a grace of a second.
        Provide(router, "svc", "b", pb);
        (pa.Load, pb.Load) = (0.9, 0.1);
        await HeartbeatUntil(router, () => router.Describe().Endpoints.Any(e => e.Load is not null));
        var moving = await pa.Routers.MoveAsync(null, null, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1), "rebalance");
        Assert(moving is [{ Answer: { Accepted: true, Routes: 4 } }], $"taken: four to move ({string.Join(", ", moving.Select(x => x.Answer))})");
        var told = await Eventually(() =>
        {
            foreach (var (o, owner, c) in clients) DrainFrames(o, owner, c);
            return clients.All(c => c.Client.Moves.Count == 1);
        }, every: 10);
        Assert(told && clients.Select((c, i) => c.Client.Moves[0]).All(m => m.GraceMs == 1000) &&
               clients.Select((c, i) => c.Client.Moves[0].TapId == old[i].Id).All(x => x),
            "each client told to move its one subscription, with its grace");
        Assert(old.All(t => !t.IsClosed), "nothing closed meanwhile: the instance keeps serving them");

        // Three move: subscribed again (placed by load: the idle instance), then the old let go. The fourth doesn't.
        var moved = new Tap[3];
        for (var i = 0; i < 3; i++)
        {
            moved[i] = (await router.SubscribeAsync(clients[i].Outlet, "svc"))!;
            moved[i].Activate();
            old[i].Dispose();
        }

        Assert(moved.All(t => t.Provider!.Endpoint == At("b")), "subscribed again: placed on the less loaded instance");
        var code = await old[3].Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(code?.Code == FaultCode.Moved && router.RoutesMoved == 4 && router.RoutesLetGo == 1,
            $"the one not moved in time: let go (Moved) at the grace's end ({code?.Code}, {router.RoutesMoved} moved, {router.RoutesLetGo} let go)");
        Assert(await Eventually(() => pa.Relayed.Any(m => m is Unsubscription u && u.Route == new RouteId(clients[3].Outlet.Id, old[3].Id))),
            "its provider told it ended, as for an unsubscribe");
        DrainFrames(clients[3].Outlet, clients[3].Owner, clients[3].Client);
        Assert(clients[3].Client.Ended.Contains((FaultCode.Moved, old[3].Id, old[3].Id)),
            "its client told it ended (an Ended record naming it alone, Moved): it subscribes again");

        // a keeps serving. Holding nothing now, it isn't checked: its last load (0.9) goes stale past the timeout, and counts
        // as idle — b, checked, says 0.9 — so it takes the next step 1.
        pb.Load = 0.9;
        router.ProviderTimeout = TimeSpan.FromMilliseconds(300);
        await Task.Delay(400);
        await HeartbeatUntil(router, () => router.Describe().Endpoints.Single(e => e.Endpoint == At("b").ToString()).Load == 0.9);
        var (fresh, _, _) = Viewer();
        var back = (await router.SubscribeAsync(fresh, "svc"))!;
        Assert(back.Provider!.Endpoint == At("a"), "the instance moved from keeps serving: placed on it again, its load stale");
        router.ProviderTimeout = TimeSpan.FromSeconds(30);

        // Made within the dwell time: stays put.
        router.MoveDwell = TimeSpan.FromSeconds(30);
        var settling = await pb.Routers.MoveAsync(null, null, TimeSpan.Zero, TimeSpan.FromSeconds(1), "again");
        Assert(settling is [{ Answer: { Accepted: false, Refused: { } dwell } }] && dwell.Contains("dwell"),
            $"made within the dwell time: refused ({string.Join(", ", settling.Select(x => x.Answer))})");

        // One route by name: just that one (a, lighter now, somewhere better to go).
        router.MoveDwell = TimeSpan.Zero;
        pa.Load = 0.2;
        await HeartbeatUntil(router, () => router.Describe().Endpoints.Any(e => e.Load == 0.2));
        var one = await pb.Routers.MoveAsync(null, new RouteId(clients[0].Outlet.Id, moved[0].Id), TimeSpan.Zero, TimeSpan.FromSeconds(1), "one");
        Assert(one is [{ Answer: { Accepted: true, Routes: 1 } }], $"one route named: that one ({string.Join(", ", one.Select(x => x.Answer))})");

        // Loaded alike: nowhere better to go — refused, so instances don't swap subscriptions to no end. Less loaded by under
        // the margin (0.1): likewise. By more: taken.
        (pa.Load, pb.Load) = (0.5, 0.5);
        await HeartbeatUntil(router, () => router.Describe().Endpoints.All(e => e.Load == 0.5));
        var alike = await pb.Routers.MoveAsync(null, null, TimeSpan.Zero, TimeSpan.FromSeconds(1), "alike");
        Assert(alike is [{ Answer: { Accepted: false, Refused: { } level } }] && level.Contains("nowhere better"),
            $"loaded alike: refused, nowhere better ({string.Join(", ", alike.Select(x => x.Answer))})");
        pa.Load = 0.45;
        await HeartbeatUntil(router, () => router.Describe().Endpoints.Any(e => e.Load == 0.45));
        var barely = await pb.Routers.MoveAsync(null, null, TimeSpan.Zero, TimeSpan.FromSeconds(1), "barely");
        Assert(barely is [{ Answer.Accepted: false }], "less loaded by under the margin: refused too");
        pa.Load = 0.3;
        await HeartbeatUntil(router, () => router.Describe().Endpoints.Any(e => e.Load == 0.3));
        var lighter = await pb.Routers.MoveAsync(null, moved[1] is { } m ? new RouteId(clients[1].Outlet.Id, m.Id) : null,
            TimeSpan.Zero, TimeSpan.FromSeconds(1), "lighter");
        Assert(lighter is [{ Answer: { Accepted: true, Routes: 1 } }],
            $"less loaded by more than the margin: taken ({string.Join(", ", lighter.Select(x => x.Answer))})");

        // At the router, by hand: an unknown node, or a partition with nothing in it, refused.
        Assert(!router.MoveNode(Guid.NewGuid(), null, null, TimeSpan.Zero, TimeSpan.Zero, "by hand").Accepted,
            "an unknown node: refused");
        var none = router.MoveNode(moved[1].Binding.NodeId!.Value, "eu", null, TimeSpan.Zero, TimeSpan.Zero, "by hand");
        Assert(!none.Accepted && none.Refused!.Contains("in eu"), $"a partition with nothing in it: refused ({none.Refused})");
    }

    public static async Task OwnPauseNotSilence()
    {
        var registry = new OutletRegistry();
        var topic = new LastValueSource();
        var router = new Publisher(registry)
        {
            Router = RouterInstance.Start("r"), ProviderHeartbeatInterval = TimeSpan.Zero,
            ProviderTimeout = TimeSpan.FromMilliseconds(300), ServiceUp = new ServiceUpPolicy()
        };
        var provider = new TestProvider(topic, InitialState.None);
        provider.Routers.HeartbeatInterval = TimeSpan.Zero;
        provider.Routers.Timeout = TimeSpan.FromMilliseconds(300);
        Provide(router, "svc", "p", provider);
        var o = NewOutlet(registry);
        o.Attach(new object());
        var tap = (await router.SubscribeAsync(o, "svc"))!;
        tap.Activate();
        for (var i = 0; i < 10; i++) // (both ways, answered)
        {
            router.HeartbeatProviders();
            provider.Routers.HeartbeatRouters();
            await Task.Delay(10);
        }

        // The router paused (a debugger, a machine too loaded to run it) three timeouts long: its providers' silence
        // meanwhile its own — not counted against them.
        Thread.Sleep(900);
        router.HeartbeatProviders();
        Assert(!tap.IsClosed && tap.Completion is { IsCompleted: false }, "the router back from a pause: its provider not deemed dead");

        // The provider paused likewise: its router's silence its own.
        Thread.Sleep(900);
        provider.Routers.HeartbeatRouters();
        Assert(provider.Lost.IsEmpty && provider.Routers.Admits(router.Router) && provider.Routers.RoutesOf(router.Router) == 1,
            "the provider back from a pause: its router not presumed dead");

        // Both still heartbeating after: kept.
        for (var i = 0; i < 40; i++)
        {
            router.HeartbeatProviders();
            provider.Routers.HeartbeatRouters();
            await Task.Delay(10);
        }

        Assert(!tap.IsClosed && provider.Lost.IsEmpty, "and kept after, heartbeats answered both ways");
    }

    public static async Task ServiceUpSpread()
    {
        var registry = new OutletRegistry();
        var publisher = new Publisher(registry)
        {
            ProviderHeartbeatInterval = TimeSpan.Zero, ProviderTimeout = TimeSpan.FromSeconds(2),
            ServiceUp = new ServiceUpPolicy(Spread: TimeSpan.FromMilliseconds(500))
        };
        var p = new TestProvider(new LastValueSource(), InitialState.None);
        Provide(publisher, "svc", "p", p);
        var clients = new (Outlet Outlet, object Owner, RefClient Client)[16];
        for (var i = 0; i < clients.Length; i++)
        {
            var o = NewOutlet(registry);
            var owner = new object();
            o.Attach(owner);
            clients[i] = (o, owner, new RefClient());
            (await publisher.SubscribeAsync(o, "svc"))!.Activate();
        }

        p.Instance = ProviderInstance.Start("p", At("p")); // restarted: every client down — then, it being back, up
        var arrived = new TimeSpan?[clients.Length];
        var start = Stopwatch.GetTimestamp();
        for (var until = Deadline(); Stopwatch.GetTimestamp() < until && arrived.Any(a => a is null);)
        {
            publisher.HeartbeatProviders();
            for (var i = 0; i < clients.Length; i++)
            {
                DrainFrames(clients[i].Outlet, clients[i].Owner, clients[i].Client);
                if (arrived[i] is null && clients[i].Client.ServicesUp.Count > 0)
                    arrived[i] = Stopwatch.GetElapsedTime(start);
            }

            await Task.Delay(5);
        }

        Assert(arrived.All(a => a is not null), "every client told up");
        var times = arrived.Select(a => a!.Value.TotalMilliseconds).Order().ToArray();
        var span = times[^1] - times[0];
        Notes.Add(
            $"service up, spread 500 ms, 16 clients: told over {span:F0} ms ({times[0]:F0}–{times[^1]:F0} ms after the restart)");
        Assert(span >= 150 && span <= 650, $"spread over the window, not at once: {span:F0} ms");
    }

    /// <summary>
    ///     The provider's next unsubscription notice, or null: told in milliseconds, on the router's own time — waited for
    ///     up to 10 s, as long as a loaded machine may keep its thread pool from it.
    /// </summary>
    private static async Task<(Unsubscription Notice, int RoutesThen)?> NextNotice(TestProvider provider)
    {
        (Unsubscription, int) n = default;
        return await Eventually(() => provider.Notices.TryDequeue(out n)) ? n : null;
    }

    public static Task EpochArbitration()
    {
        int Rounds(bool naive, int rounds, bool notices = false)
        {
            var stranded = 0;
            for (var round = 0; round < rounds; round++)
            {
                var registry = new OutletRegistry();
                var publisher = new Publisher(registry);
                var topic = new LastValueSource();
                var provider = new TestProvider(topic, InitialState.None, naive, notices);
                Provide(publisher, "t", "t", provider);
                using var stop = new CancellationTokenSource();
                var producing = Task.Run(() =>
                {
                    var rnd = new Random(round);
                    while (!stop.IsCancellationRequested)
                    {
                        if (!Volatile.Read(ref provider.Live))
                        {
                            Thread.SpinWait(20);
                            continue;
                        }

                        var answer = topic.Publish([1]);
                        Thread.SpinWait(rnd.Next(0, 400)); // the answer's way back (a provider elsewhere)
                        provider.Acknowledged(answer);
                    }
                });
                var subscribers = Enumerable.Range(0, 3).Select(w => Task.Run(() =>
                {
                    var rnd = new Random(round * 7 + w);
                    var outlet = NewOutlet(registry);
                    Tap? tap = null;
                    for (var i = 0; i < 200; i++)
                    {
                        if (tap is null)
                        {
                            tap = publisher.SubscribeAsync(outlet, "t").AsTask().GetAwaiter().GetResult();
                        }
                        else
                        {
                            tap.Dispose();
                            tap = null;
                        }

                        Thread.SpinWait(rnd.Next(1, 200));
                    }

                    if (tap is null && rnd.Next(2) == 0)
                        publisher.SubscribeAsync(outlet, "t").AsTask().GetAwaiter()
                            .GetResult(); // ends subscribed, or not
                })).ToArray();
                Task.WaitAll(subscribers);
                Thread.Sleep(notices ? 20 : 1); // the producer's last answers (and the notices)
                stop.Cancel();
                producing.Wait();
                if (topic.Routes > 0 && !provider.Live) stranded++; // a subscriber nobody produces for
            }

            return stranded;
        }

        int byEpoch = Rounds(false, 400), byAny = Rounds(true, 400);
        int noticesByEpoch = Rounds(false, 200, true), noticesByAny = Rounds(true, 200, true);
        Notes.Add(
            $"epoch arbitration: 400 rounds each — by the epoch rule {byEpoch} left stopped with a route bound; stopping on any 202, {byAny}. "
            + $"Acting on unsubscribe notices too, 200 rounds each: by the rule {noticesByEpoch}; on any 'none left', {noticesByAny}");
        Assert(byEpoch == 0 && noticesByEpoch == 0,
            $"{byEpoch} + {noticesByEpoch} rounds ended with the provider stopped while a route was bound");
        Assert(byAny > 0, "the race wasn't reproduced: the test can't tell the rule from its absence");
        return Task.CompletedTask;
    }

    public static Task FrameBufferZeroAlloc()
    {
        var registry = new OutletRegistry();
        var s = NewOutlet(registry, o => o.FlowControl = FlowControlMode.Fixed);
        var owner = new object();
        s.Attach(owner);
        var values = new LastValues<int>("fbz");
        s.Tap(values[0], activate: true);
        var book = new Table<Level, long>("FBZ-INLINE") { Delivery = Delivery.Inline };
        FillBook(book, 3000);
        var btap = s.Tap(book, activate: true);
        var value = new byte[200];
        var large = 0;
        var count = () =>
        {
            if (s.HoldsLargeFrame) large++;
            return true;
        }; // made once: a lambda per step would allocate

        void Step(int i)
        {
            values.Publish(0, value);
            if (i % 10 == 0) s.Resync(btap.Id); // a 150 KB snapshot again: a full-size frame
            if ((i & 7) == 0) s.Heartbeat();
            DrainFrames(s, owner, null, count);
        }

        for (var i = 0; i < 2_000; i++) Step(i); // warm: the pool, JIT tiers
        large = 0;
        var before = AllocationBaseline();
        for (var i = 0; i < 2_000; i++) Step(i);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Notes.Add(
            $"frame buffer: {allocated} bytes allocated over 2,000 steps through TryBuildFrame, {large} of their frames full-size (rented)");
        Assert(large >= 150, $"full-size frames: {large}");
        Assert(allocated == 0, $"{allocated} bytes allocated");
        return Task.CompletedTask;
    }

    // ─────────────────────────────── blob store ───────────────────────────────

    /// <summary>Bytes a test can check without keeping them: byte i of blob <paramref name="seed" />.</summary>
    private static byte PatternByte(long seed, int i)
    {
        return (byte)(seed * 31 + i * 7 + (i >> 9));
    }

    private static byte[] Pattern(int length, long seed)
    {
        var b = new byte[length];
        for (var i = 0; i < length; i++) b[i] = PatternByte(seed, i);
        return b;
    }

    private static bool MatchesPattern(in ReadOnlySequence<byte> data, long seed)
    {
        var i = 0;
        foreach (var m in data)
        foreach (var b in m.Span)
            if (b != PatternByte(seed, i++))
                return false;
        return true;
    }

    public static Task BlobStoreSegments()
    {
        var clock = new ManualTime();
        var store = new BlobStore(64L << 20, TimeSpan.FromSeconds(10), clock);
        var seg = store.SegmentBytes;
        foreach (var size in new[] { 0, 1, seg - 1, seg, seg + 1, 3 * seg + 5, 100_000, 1 << 20 })
        {
            Assert(store.TryPut(Pattern(size, size), false, false, out var id), $"put {size}");
            Assert(store.TryLease(id, out var lease) && lease.Length == size && !lease.ContentAddressed,
                $"lease {size}");
            using (lease)
            {
                var data = lease.Data;
                Assert(data.Length == size && MatchesPattern(data, size), $"{size} bytes intact");
                var pieces = 0;
                foreach (var m in data)
                {
                    Assert(m.Length <= seg, "no piece larger than a segment");
                    if (m.Length > 0) pieces++;
                }

                Assert(pieces == (size + seg - 1) / seg, $"{size} bytes in {pieces} segments");
            }
        }

        var same = Pattern(40_000, 1);
        Assert(store.TryPut(same, false, false, out var a) && store.TryPut(same, false, false, out var b) && a != b,
            "unique ids: the same bytes twice are two blobs");
        var (stored, shared) = store.Stats;
        var bytes = store.Bytes;
        bool put1 = store.TryPut(same, false, true, out var c1), put2 = store.TryPut(same, false, true, out var c2);
        Assert(put1 && put2, "content-addressed puts");
        Assert(c1 == c2 && c1 == BlobId.Of(same) && store.Stats == (stored + 1, shared + 1),
            "content-addressed: stored once, the second found before copying");
        Assert(store.Bytes == bytes + 3 * seg, "40,000 bytes take three 16 KB segments");
        Assert(store.TryLease(c1, out var hashed) && hashed.ContentAddressed, "a content-addressed lease says so");
        hashed.Dispose();

        // Unique ids are UUIDv7s: version 7, variant 0b10, strictly increasing (past 4,096 in a millisecond too), stamped now.
        var timed = new BlobStore(time: clock);
        var ids = new List<BlobId>();
        for (var i = 0; i < 5000; i++)
        {
            Assert(timed.TryPut([], false, false, out var u), "empty put");
            ids.Add(u);
        }

        static ulong Entropy(BlobId u)
        {
            return (ulong)u.Value & ((1UL << 62) - 1);
        }

        foreach (var u in ids)
            Assert((((ulong)(u.Value >> 64) >> 12) & 0xF) == 7 && (ulong)u.Value >> 62 == 0b10,
                $"UUIDv7 version and variant: {u}");
        Assert(ids.Zip(ids.Skip(1)).All(x => x.First.Value < x.Second.Value), "strictly increasing");
        var stamped = (long)((ulong)(ids[0].Value >> 64) >> 16);
        Assert(Math.Abs(stamped - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) < 60_000, "stamped with the time");
        Assert(new BlobStore(time: clock).TryPut([], false, false, out var o) && Entropy(o) != Entropy(ids[0]),
            "another store draws its own entropy");
        var fresh = new BlobStore(time: clock, entropyPerId: true);
        Assert(
            fresh.TryPut([], false, false, out var f1) && fresh.TryPut([], false, false, out var f2) &&
            Entropy(f1) != Entropy(f2), "per-id entropy, when asked");

        var small = new BlobStore(4 * seg, time: clock);
        Assert(small.TryPut(Pattern(3 * seg, 2), false, false, out _), "three segments fit");
        Assert(!small.TryPut(Pattern(seg + 1, 3), false, false, out _), "two more don't");
        Assert(small.TryPut(Pattern(seg, 4), false, false, out _), "one does");
#if DEBUG
        store.AssertConsistent();
        small.AssertConsistent();
#endif
        return Task.CompletedTask;
    }

    public static Task BlobStoreLeases()
    {
        var clock = new ManualTime();
        var store = new BlobStore(4 << 20, TimeSpan.FromSeconds(1), clock, retainBytes: 4 << 20);
        var seg = store.SegmentBytes;
        Assert(store.TryPut(Pattern(50_000, 10), false, false, out var id), "put");
        Assert(store.TryLease(id, out var lease), "leased");
        clock.Advance(TimeSpan.FromSeconds(2));
        store.Sweep();
        Assert(store.Count == 0 && !store.TryLease(id, out _) && !store.TryIssue(id),
            "evicted: no longer fetchable or issuable");
        Assert(store.Bytes == 4 * seg, "…but its four segments are still held, by the lease");

        for (var i = 0; i < 20; i++) // blobs come and go through the pool meanwhile
        {
            Assert(store.TryPut(Pattern(50_000, 100 + i), false, false, out var other), "another blob");
            Assert(store.TryLease(other, out var l) && MatchesPattern(l.Data, 100 + i), "its own bytes");
            l.Dispose();
            clock.Advance(TimeSpan.FromSeconds(2));
            store.Sweep();
        }

        var held = lease.Data;
        Assert(MatchesPattern(held, 10), "the leased bytes are intact after 20 blobs used the pool");
        var pieces = new List<ReadOnlyMemory<byte>>();
        foreach (var m in held) pieces.Add(m);
        lease.Dispose();
        lease.Dispose(); // twice: no second release
        Assert(store.Bytes == 0 && store.PooledBytes >= 4 * seg, "released: its segments back in the pool");
        var broken = false;
        try
        {
            foreach (var _ in held)
            {
            }
        }
        catch (InvalidOperationException)
        {
            broken = true;
        }

        Assert(broken, "a sequence kept past its lease can't be walked: the recycled chain is unlinked");
#if DEBUG
        foreach (var m in pieces) foreach (byte x in m.Span) Assert(x == 0xDD, "DEBUG: recycled segments are poisoned, so a stale read can't pass for data");
        store.AssertConsistent();
#endif
        var refused = false;
        try
        {
            _ = lease.Data;
        }
        catch (ObjectDisposedException)
        {
            refused = true;
        }

        Assert(refused, "a released lease refuses reads");

        Assert(store.TryPut(Pattern(10, 5), true, false, out var pinned), "pinned put");
        clock.Advance(TimeSpan.FromMinutes(1));
        store.Sweep();
        Assert(store.TryIssue(pinned), "pinned: kept past the grace period");
        store.Unpin(pinned);
        clock.Advance(TimeSpan.FromSeconds(2));
        store.Sweep();
        Assert(!store.TryIssue(pinned) && store.Bytes == 0, "unpinned: expired after the grace period");
        return Task.CompletedTask;
    }

    public static Task BlobStoreConcurrent()
    {
        var clock = new ManualTime();
        var store = new BlobStore(64 << 20, TimeSpan.FromMilliseconds(200), clock, retainBytes: 8 << 20);
        const int Threads = 8, Ops = 6000, Slots = 256;
        var slots = new (BlobId Id, long Seed, int Size)[Slots];
        var gate = new object();
        long seeds = 0, reads = 0, mismatches = 0, refusals = 0;
        using var done = new CancellationTokenSource();
        var sweeper = Task.Run(() =>
        {
            while (!done.IsCancellationRequested)
            {
                clock.Advance(TimeSpan.FromMilliseconds(1));
                store.Sweep();
                Thread.Sleep(1);
            } // about real time
        });
        Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, w =>
        {
            var rnd = new Random(w);
            var pins = new List<BlobId>();
            for (var i = 0; i < Ops; i++)
            {
                var op = rnd.Next(10);
                if (op < 4)
                {
                    var seed = Interlocked.Increment(ref seeds);
                    var size = rnd.Next(0, 100_000);
                    var pin = rnd.Next(8) == 0;
                    if (!store.TryPut(Pattern(size, seed), pin, rnd.Next(4) == 0, out var id))
                    {
                        Interlocked.Increment(ref refusals);
                        continue;
                    }

                    if (pin) pins.Add(id);
                    lock (gate)
                    {
                        slots[rnd.Next(Slots)] = (id, seed, size);
                    }
                }
                else if (op < 9)
                {
                    (BlobId Id, long Seed, int Size) s;
                    lock (gate)
                    {
                        s = slots[rnd.Next(Slots)];
                    }

                    if (s.Seed == 0 || !store.TryLease(s.Id, out var lease)) continue;
                    using (lease)
                    {
                        Interlocked.Increment(ref reads);
                        if (lease.Length != s.Size || !MatchesPattern(lease.Data, s.Seed))
                            Interlocked.Increment(ref mismatches);
                        if (rnd.Next(4) == 0) Thread.Yield(); // hold it a while: the blob may be evicted meanwhile
                        if (!MatchesPattern(lease.Data, s.Seed)) Interlocked.Increment(ref mismatches);
                    }
                }
                else if (pins.Count > 0)
                {
                    var k = rnd.Next(pins.Count);
                    store.Unpin(pins[k]);
                    pins.RemoveAt(k);
                }
            }

            foreach (var id in pins) store.Unpin(id);
        });
        done.Cancel();
        sweeper.Wait();
        clock.Advance(TimeSpan.FromSeconds(1));
        store.Sweep();
#if DEBUG
        store.AssertConsistent();
#endif
        Notes.Add(
            $"blob store: {Threads} threads, {reads:N0} leased reads, {mismatches} mismatches, {refusals} puts refused (full)");
        Assert(mismatches == 0, $"{mismatches} leased reads didn't match their blob");
        Assert(reads > 10_000, $"enough reads to mean something: {reads}");
        Assert(store.Bytes == 0 && store.Count == 0,
            $"everything accounted back: {store.Bytes} bytes, {store.Count} blobs left");
        return Task.CompletedTask;
    }

    public static Task BlobStoreZeroAlloc()
    {
        var clock = new ManualTime();
        var store = new BlobStore(16 << 20, TimeSpan.FromSeconds(1), clock);
        var data = Pattern(40_000, 1);
        long sum = 0;

        void Step(int i, bool lease)
        {
            store.TryPut(data, (i & 1) == 0, i % 3 == 0, out var id);
            store.TryIssue(id);
            if ((i & 1) == 0) store.Unpin(id);
            if (lease && store.TryLease(id, out var l))
            {
                foreach (var m in l.Data) sum += m.Span[0];
                l.Dispose();
            }

            clock.Advance(TimeSpan.FromMilliseconds(100));
            store.Sweep();
        }

        for (var i = 0; i < 20_000; i++) Step(i, true); // warm: the pool, the table, JIT tiers
        var before = AllocationBaseline();
        for (var i = 0; i < 10_000; i++) Step(i, false);
        var withoutLeases = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++) Step(i, true);
        var perLease = (GC.GetAllocatedBytesForCurrentThread() - before) / 10_000;
        Notes.Add(
            $"blob store steady state: {withoutLeases} bytes over 10k put/issue/pin/unpin/sweep steps (40 KB blobs, a third content-addressed); {perLease} bytes per lease");
        Assert(withoutLeases == 0, $"{withoutLeases} bytes allocated");
        Assert(perLease <= 64, $"a lease allocates only itself: {perLease} bytes");
        return Task.CompletedTask;
    }

    public static Task OutOfBandSharedWithoutHashing()
    {
        var registry = new OutletRegistry();
        var outlets = Enumerable.Range(0, 20).Select(_ => NewOutlet(registry)).ToArray();
        var clients = outlets.Select(_ => new RefClient()).ToArray();
        var scratch = new byte[1 << 20];
        var book = new Table<Level, long>("SHARED-NOHASH"); // not content-addressed (the default)
        FillBook(book, 3000);
        var taps = outlets.Select(o => o.Tap(book, activate: true)).ToArray();

        void Round()
        {
            for (var k = 0; k < outlets.Length; k++)
            {
                Drain(outlets[k], clients[k], scratch);
                Assert(clients[k].Pending.All(h => !h.ContentAddressed), "handles say: not content-addressed");
                clients[k].FetchPending(outlets[k], registry);
                Drain(outlets[k], clients[k], scratch);
            }
        }

        Round();
        Assert(registry.Blobs.Stats.Stored == 1, $"one snapshot blob for 20 taps: {registry.Blobs.Stats.Stored}");

        using (var u = book.BeginUpdate()) // a delta over the threshold, the same for every tap
        {
            for (var i = 1; i <= 2000; i++) u.Set(new Level(0, 100 - i), i + 7);
            u.Complete();
        }

        Round();
        Assert(registry.Blobs.Stats.Stored == 2,
            $"one delta blob for 20 taps at the same cursor: {registry.Blobs.Stats.Stored - 1}");
        for (var k = 0; k < outlets.Length; k++)
            Assert(
                clients[k].Tables[taps[k].Id].Seq == book.Version &&
                clients[k].Tables[taps[k].Id].Rows.Count == book.Count, $"client {k} has the table");

        var values = new LastValues<int>("shared-nohash");
        var vtaps = outlets.Select(o => o.Tap(values[0], activate: true)).ToArray();
        values.Publish(0, Pattern(40_000, 1));
        for (var k = 0; k < 10; k++)
        {
            Drain(outlets[k], clients[k], scratch);
            clients[k].FetchPending(outlets[k], registry);
        } // half see the first value

        values.Publish(0, Pattern(40_000, 2));
        Round(); // all behind, at two different points: the same bytes
        Assert(registry.Blobs.Stats.Stored == 4, $"each value stored once: {registry.Blobs.Stats.Stored - 2}");
        for (var k = 0; k < outlets.Length; k++)
            Assert(clients[k].Values[vtaps[k].Id].AsSpan().SequenceEqual(Pattern(40_000, 2)),
                $"client {k} has the latest value");
        return Task.CompletedTask;
    }

    public static Task ZeroAllocSteadyState()
    {
        var registry = new OutletRegistry();
        var s = NewOutlet(registry);
        var clock = new ManualTime();
        var wheel = new TimingWheel(TimeSpan.FromMilliseconds(5), time: clock, autoStart: false);
        var limited = NewOutlet(new OutletRegistry(wheel), o =>
        {
            o.StreamingBytesPerSecond = 32 * 1024;
            o.StreamingBurstBytes = 4096;
        });
        var book = new Table<Level, long>("ALLOC");
        var folded = new Table<Level, long>("FOLDED");
        var values = new LastValues<int>("t");
        s.Tap(book, activate: true);
        s.Tap(folded, activate: true);
        for (var k = 0; k < 16; k++) s.Tap(values[k], activate: true);
        limited.Tap(book, activate: true);
        for (var k = 0; k < 16; k++) limited.Tap(values[k], activate: true);
        var positions = new Table<Holding, long>("ALLOC-POS");
        var cash = new Table<int, long>("ALLOC-CASH");
        s.Watch("acct", WatchMember.Range(positions, 1), WatchMember.Range(cash, 1), WatchMember.Of(values[0]))
            .Activate();
        var alerts = new MessageTopic(64, 16 * 1024);
        s.Tap(alerts, activate: true);
        var posters = new[] { Lane.High, Lane.Normal, Lane.Low }.Select(lane => Poster(s, lane)).ToArray();
        var scratch = new byte[1 << 20];
        var value = new byte[32];
        var msg = new byte[64];
        var batchFold = new BytesAdd();
        var keys = new Level[4];
        var deltas = new long[4];

        void Step(int i)
        {
            using (var u = book.BeginUpdate())
            {
                var a = new Level((byte)(i & 1), 1000 + i % 50);
                if (i % 7 == 0) u.Delete(a);
                else u.Set(a, i);
                u.Fold<long, Add<long>>(new Level((byte)((i >> 1) & 1), 1000 + i * 7 % 50), 1);
                if (i % 5 == 0) u.Set(a, -1); // every 5th is abandoned: rolled back on dispose
                else u.Complete();
            }

            for (var k = 0; k < keys.Length; k++)
            {
                keys[k] = new Level((byte)(k & 1), 500 + (i + k) % 30);
                deltas[k] = (i & 1) == 0 ? 2 : -1;
            }

            using (var u = folded.BeginUpdate(i))
            {
                u.Fold(keys, deltas, batchFold);
                u.Complete(i + 1);
            }

            values.Publish(i & 15, value);
            using (var tx = Transaction.Begin(positions, cash))
            {
                tx.Of(positions).Fold<long, Add<long>>(new Holding(i & 3, i % 5), 1);
                tx.Of(cash).Set(i & 3, i);
                tx.Complete();
            }

            posters[i % 3].TryPost(msg);
            if ((i & 7) == 0) alerts.Publish(msg);
            if ((i & 3) == 0) s.Heartbeat();
            s.Build(scratch);
            clock.Advance(TimeSpan.FromMilliseconds(1)); // rate-limited outlet: bucket, wheel, wake-ups
            wheel.Advance(clock.Now);
            if (limited.IsReady) limited.Build(scratch);
        }

        for (var i = 0; i < 100_000; i++) Step(i); // warm up: dictionaries, queues, JIT tiers
        var before = AllocationBaseline();
        for (var i = 100_000; i < 200_000; i++) Step(i);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Notes.Add(
            $"steady state: {allocated} bytes allocated over 100k update+fold+batch-fold+publish+transaction+message+post+build steps (incl. a watch, a rate-limited outlet + wheel)");
        Assert(allocated == 0, $"{allocated} bytes allocated");
        return Task.CompletedTask;
    }

    // ─────────────────────────────── wire / protocol ───────────────────────────────

    private static IHubProtocol BlazorPack()
    {
        return (IHubProtocol)Activator.CreateInstance(
            typeof(CircuitOptions).Assembly
                .GetType("Microsoft.AspNetCore.Components.Server.BlazorPack.BlazorPackHubProtocol",
                    true)!, true)!;
    }

    public static Task FrameParsesWithBlazorPack()
    {
        var bp = BlazorPack();
        foreach (var size in new[] { 0, 1, 100, 70_000, 300_000 })
        {
            var payload = new byte[size];
            new Random(size).NextBytes(payload);
            var w = new ArrayBufferWriter<byte>();
            var seq = (1L << 40) | (uint)size;
            FrameWriter.Write(w, seq, payload);
            var input = new ReadOnlySequence<byte>(w.WrittenMemory);
            Assert(bp.TryParseMessage(ref input, new Binder(), out var msg), $"size {size}: not parsed");
            Assert(input.IsEmpty, "trailing bytes");
            var inv = (InvocationMessage)msg!;
            Assert(
                inv.Target == BindRegistry.FrameTarget && (long)inv.Arguments[0]! == seq &&
                ((byte[])inv.Arguments[1]!).AsSpan().SequenceEqual(payload), $"size {size}: content mismatch");
        }

        // The SignalR JS client read this very envelope as Tap.Frame(seq, bytes) — captured, so no client is needed here.
        var capture = SignalRCapture().GetProperty("envelope");
        var fixedEnvelope = new ArrayBufferWriter<byte>();
        FrameWriter.Write(fixedEnvelope, EnvelopeSeq, EnvelopePayload());
        var read = capture.GetProperty("read");
        Assert(Convert.ToHexString(fixedEnvelope.WrittenSpan) == capture.GetProperty("hex").GetString(),
            "the envelope changed since the JS client read it: capture again (tests/js/capture-signalr.mjs)");
        Assert(read.GetProperty("type").GetInt32() == 1 &&
               read.GetProperty("target").GetString() == BindRegistry.FrameTarget &&
               read.GetProperty("seq").GetInt64() == EnvelopeSeq
               && read.GetProperty("payloadIsBytes").GetBoolean() && read.GetProperty("payloadHex").GetString() ==
               Convert.ToHexString(EnvelopePayload()),
            "as the JS client read it: Tap.Frame, the sequence a number, the payload a Uint8Array of the same bytes");

        var protocol = new RealtimeProtocol(bp, new OutletRegistry());
        var link = new CircuitLink(null!);
        var a = new ArrayBufferWriter<byte>();
        var b = new ArrayBufferWriter<byte>();
        object?[] args = [42L, new byte[] { 1, 2, 3 }];
        protocol.WriteMessage(new TaggedInvocation("JS.RenderBatch", args, link), a);
        bp.WriteMessage(new InvocationMessage("JS.RenderBatch", args), b);
        Assert(a.WrittenSpan.SequenceEqual(b.WrittenSpan) && link.Written == 1, "tagged invocation altered");
        return Task.CompletedTask;
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "tests", "js")))
                return d.FullName;
        throw new DirectoryNotFoundException("repo root");
    }

    /// <summary>
    ///     What the SignalR JS client wrote and read, captured once (tests/js/capture-signalr.mjs): no client needed to
    ///     test against it.
    /// </summary>
    private static JsonElement SignalRCapture()
    {
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "tests", "js", "fixtures", "signalr.json")))
            .RootElement;
    }

    /// <summary>
    ///     A control invocation as the JS client sends it — the C# replica, which <see cref="ControlFromJs" /> holds to
    ///     the captured bytes.
    /// </summary>
    private static byte[] EncodeWithJs(string target, params long[] args)
    {
        return ControlBytes(target, args);
    }

    public static Task ControlFromJs()
    {
        // Every invocation the client sends, at each integer width it may choose: the replica writes the same bytes, and
        // each is consumed in place (no outlet 3: acted on by none).
        var captured = new RealtimeProtocol(BlazorPack(), new OutletRegistry());
        var sends = 0;
        foreach (var send in SignalRCapture().GetProperty("sends").EnumerateArray())
        {
            var target = send.GetProperty("target").GetString()!;
            long[] argv = [.. send.GetProperty("args").EnumerateArray().Select(a => a.GetInt64())];
            var bytes = Convert.FromHexString(send.GetProperty("hex").GetString()!);
            Assert(bytes.AsSpan().SequenceEqual(ControlBytes(target, argv)),
                $"{target}({string.Join(", ", argv)}): the C# replica drifted from the client's encoding");
            var one = new ReadOnlySequence<byte>(bytes);
            Assert(
                captured.TryParseMessage(ref one, new Binder(), out var consumed) && consumed is PingMessage &&
                one.IsEmpty, $"{target}: consumed in place");
            sends++;
        }

        Assert(sends >= 20, "the capture's sends");

        var registry = new OutletRegistry();
        var outlets = Enumerable.Range(0, 5).Select(_ => NewOutlet(registry, o => o.Window = 1)).ToArray();
        var s = outlets[3];
        var values = new LastValues<int>("t");
        var tap = s.Tap(values[0], activate: true);
        values.Publish(0, [1]);
        var owner = new object();
        s.Attach(owner);

        Assert(s.TryBuildFrame(owner, false, out _, out _), "frame expected");
        values.Publish(0, [2]);
        Assert(!s.TryBuildFrame(owner, false, out _, out _), "window=1 must block the second data frame until acked");

        var protocol = new RealtimeProtocol(BlazorPack(), registry);
        var ack = EncodeWithJs("Tap.Ack", s.Id, s.Nonce, 1, 0);
        var input = new ReadOnlySequence<byte>(ack);
        Assert(protocol.TryParseMessage(ref input, new Binder(), out var m) && m is PingMessage && input.IsEmpty,
            "ack not consumed");
        Assert(s.TryBuildFrame(owner, false, out _, out _), "ack should restore credit");

        var spoof = new ReadOnlySequence<byte>(EncodeWithJs("Tap.Ack", s.Id, s.Nonce ^ 1, 99, 0));
        protocol.TryParseMessage(ref spoof, new Binder(), out _);
        Assert(!tap.HasWork, "tap should be up to date");
        var resync = new ReadOnlySequence<byte>(EncodeWithJs("Tap.Resync", s.Id, s.Nonce, tap.Id));
        Assert(protocol.TryParseMessage(ref resync, new Binder(), out _), "resync not consumed");
        Assert(tap.HasWork, "resync should mark the tap");

        // A reconnect: held till the client says the last frame it received (Tap.Resume, as the JS client sends it).
        var owner2 = new object();
        s.Attach(owner2);
        Assert(!tap.HasWork && tap.Resuming == 1, "held since the reconnect");
        var resume = EncodeWithJs("Tap.Resume", s.Id, s.Nonce, 1, 0);
        var resumed = new ReadOnlySequence<byte>(resume);
        Assert(protocol.TryParseMessage(ref resumed, new Binder(), out var r) && r is PingMessage && resumed.IsEmpty,
            "resume consumed in place");
        Assert(tap.Resuming == 0 && tap.HasWork, "resumed: on from what it received");

        // Tap.Sync (as the JS client sends it, its id up to a u32): consumed in place, a rendezvous in the tap's mailbox.
        Assert(tap.Mail is null, "no mailbox yet");
        var sync = new ReadOnlySequence<byte>(EncodeWithJs("Tap.Sync", s.Id, s.Nonce, tap.Id, uint.MaxValue));
        Assert(protocol.TryParseMessage(ref sync, new Binder(), out var y) && y is PingMessage && sync.IsEmpty,
            "sync consumed in place");
        Assert(tap.Mail is not null, "sync: its rendezvous in the tap's mailbox");
        return Task.CompletedTask;
    }

    public static Task ControlInterleaving()
    {
        var registry = new OutletRegistry();
        var s = NewOutlet(registry);
        var bp = BlazorPack();
        var protocol = new RealtimeProtocol(bp, registry);
        var ack = EncodeWithJs("Tap.Ack", s.Id, s.Nonce, 0, 0);

        var w = new ArrayBufferWriter<byte>();
        w.Write(ack);
        bp.WriteMessage(new InvocationMessage("OnRenderCompleted", [7L, "ok"]), w);
        w.Write(ack);
        var input = new ReadOnlySequence<byte>(w.WrittenMemory);
        Assert(
            protocol.TryParseMessage(ref input, new Binder(), out var m1) && m1 is InvocationMessage
            {
                Target: "OnRenderCompleted"
            }, "message after ack");
        Assert(protocol.TryParseMessage(ref input, new Binder(), out var m2) && m2 is PingMessage && input.IsEmpty,
            "trailing ack");
        Assert(!protocol.TryParseMessage(ref input, new Binder(), out _), "nothing left");

        var partial = new ReadOnlySequence<byte>(ack.AsMemory(0, ack.Length - 1));
        Assert(!protocol.TryParseMessage(ref partial, new Binder(), out _) && partial.Length == ack.Length - 1,
            "partial ack must not be consumed");
        return Task.CompletedTask;
    }

    /// <summary>A frame envelope's payload, fixed: 300 bytes, i × 7.</summary>
    private static byte[] EnvelopePayload()
    {
        return Enumerable.Range(0, 300).Select(i => (byte)(i * 7)).ToArray();
    }

    /// <summary>C# replica of the JS client's send() encoding (held to the captured bytes in ControlFromJs).</summary>
    private static byte[] ControlBytes(string target, params long[] args)
    {
        var body = new List<byte> { 0x95, 0x01, 0x80, 0xC0, (byte)(0xA0 | target.Length) };
        body.AddRange(Encoding.ASCII.GetBytes(target));
        body.Add((byte)(0x90 | args.Length));
        foreach (var a in args)
            if (a is >= 0 and <= 0x7F)
            {
                body.Add((byte)a);
            }
            else if (a is >= 0 and <= byte.MaxValue)
            {
                body.Add(0xCC);
                body.Add((byte)a);
            }
            else if (a is >= 0 and <= ushort.MaxValue)
            {
                body.Add(0xCD);
                body.AddRange(BitConverter.GetBytes((ushort)a).Reverse());
            }
            else if (a is >= 0 and <= uint.MaxValue)
            {
                body.Add(0xCE);
                body.AddRange(BitConverter.GetBytes((uint)a).Reverse());
            }
            else
            {
                body.Add(0xCF);
                body.AddRange(BitConverter.GetBytes((ulong)a).Reverse());
            }

        var result = new List<byte>();
        var v = (uint)body.Count;
        while (v >= 0x80)
        {
            result.Add((byte)(v | 0x80));
            v >>= 7;
        }

        result.Add((byte)v);
        result.AddRange(body);
        return result.ToArray();
    }

    public static Task RemoteJsRuntimeBind()
    {
        var asm = typeof(CircuitOptions).Assembly;
        var hubType = asm.GetType("Microsoft.AspNetCore.Components.Server.ComponentHub", true)!;
        var runtimeType = asm.GetType("Microsoft.AspNetCore.Components.Server.Circuits.RemoteJSRuntime", true)!;
        var proxyType = asm.GetType("Microsoft.AspNetCore.Components.Server.Circuits.CircuitClientProxy", true)!;

        static object OptionsOf(Type t)
        {
            return typeof(Options).GetMethod(nameof(Options.Create))!.MakeGenericMethod(t)
                .Invoke(null, [Activator.CreateInstance(t)])!;
        }

        var runtime = Activator.CreateInstance(runtimeType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [
                OptionsOf(typeof(CircuitOptions)),
                OptionsOf(typeof(HubOptions<>).MakeGenericType(hubType)),
                Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(runtimeType))
            ], null)!;
        var capture = new CapturingProxy();
        var client = Activator.CreateInstance(proxyType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [capture, "c1"], null)!;
        runtimeType.GetMethod("Initialize", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(runtime, [client]);

        var binds = new BindRegistry();
        var transport = new CircuitTransport(NewOutlet(new OutletRegistry()), binds);
        _ = ((IJSRuntime)runtime).InvokeVoidAsync("taplineBind", transport.BindToken, transport.Outlet.Id,
            transport.Outlet.Nonce);
        var (method, args) = capture.Sent.Single();
        Assert(method == "JS.BeginInvokeJS", $"target was {method}");
        Assert(ReferenceEquals(binds.Match(args), transport), "token not found in the real interop arguments");
        Notes.Add(
            $"JS.BeginInvokeJS args: [{string.Join(", ", args.Select(a => a is string str ? $"\"{str}\"" : a?.ToString()))}]");
        return Task.CompletedTask;
    }

    public static async Task CallerRoutesThroughLifetimeManager()
    {
        CircuitHubPolicy.IsCircuitHub = _ => true;
        var services = new ServiceCollection().AddLogging();
        services.AddSignalR();
        services.AddSingleton<BindRegistry>();
        services.AddSingleton<TokenHolder>();
        services.AddSingleton(typeof(DefaultHubLifetimeManager<>));
        services.AddSingleton(typeof(HubLifetimeManager<>), typeof(CircuitLifetimeManager<>));
        using var sp = services.BuildServiceProvider();
        var outlet = NewOutlet(new OutletRegistry());
        var transport = new CircuitTransport(outlet, sp.GetRequiredService<BindRegistry>());
        sp.GetRequiredService<TokenHolder>().Token = transport.BindToken;
        var handler = sp.GetRequiredService<HubConnectionHandler<CallerHub>>();

        var toClient = new Pipe();
        var toServer = new Pipe();
        var connection = new DefaultConnectionContext("caller-1", new DuplexPipe(toServer.Reader, toClient.Writer),
            new DuplexPipe(toClient.Reader, toServer.Writer));
        var run = handler.OnConnectedAsync(connection);
        Assert(!outlet.IsReady, "nothing to send before attach");
        await toServer.Writer.WriteAsync(Encoding.UTF8.GetBytes(
            "{\"protocol\":\"json\",\"version\":1}\u001e{\"type\":1,\"target\":\"Bind\",\"arguments\":[]}\u001e"));

        var sw = Stopwatch.StartNew();
        while (!outlet.IsReady && sw.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
        Assert(outlet.IsReady,
            "Clients.Caller send did not reach CircuitLifetimeManager.SendConnectionAsync (attach queues a heartbeat)");

        toServer.Writer.Complete();
        toClient.Reader.Complete();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public static async Task EndToEnd()
    {
        CircuitHubPolicy.IsCircuitHub = _ => true;
        var outlets = new OutletRegistry();
        var binds = new BindRegistry();
        var bp = BlazorPack();
        var protocol = new RealtimeProtocol(bp, outlets);

        var toClient = new Pipe(new PipeOptions(pauseWriterThreshold: 16 * 1024, resumeWriterThreshold: 8 * 1024));
        var toServer = new Pipe();
        var connection = new DefaultConnectionContext("c1", new DuplexPipe(toServer.Reader, toClient.Writer),
            new DuplexPipe(toClient.Reader, toServer.Writer));
        var hubConnection = new HubConnectionContext(connection,
            new HubConnectionContextOptions
            {
                KeepAliveInterval = TimeSpan.FromSeconds(15), ClientTimeoutInterval = TimeSpan.FromSeconds(30),
                StreamBufferCapacity = 10
            }, NullLoggerFactory.Instance)
        {
            Protocol = protocol
        };
        var manager = new CircuitLifetimeManager<TestHub>(
            new DefaultHubLifetimeManager<TestHub>(NullLogger<DefaultHubLifetimeManager<TestHub>>.Instance), binds);
        await manager.OnConnectedAsync(hubConnection);

        var outlet = NewOutlet(outlets, o => o.FrameBudget = 4096); // adaptive flow control, the default
        var transport = new CircuitTransport(outlet, binds);
        var book = new Table<Level, long>("E2E");
        var marks = new LastValues<string>("mark");
        var bookTap = outlet.Tap(book);
        var markTap = outlet.Tap(marks["BTC"]);
        var alerts = new MessageTopic(256);
        var alertTap = outlet.Tap(alerts); // replays what it kept on activation
        var client = new RefClient();
        long renderBatches = 0, lastRenderBatch = -1, frames = 0;
        bool bound = false, activated = false, streamedEarly = false;

        var clientTask = Task.Run(async () =>
        {
            var binder = new Binder();
            while (true)
            {
                var result = await toClient.Reader.ReadAsync();
                var buffer = result.Buffer;
                while (bp.TryParseMessage(ref buffer, binder, out var msg))
                {
                    if (msg is not InvocationMessage inv) continue;
                    switch (inv.Target)
                    {
                        case BindRegistry.FrameTarget:
                            lock (client)
                            {
                                client.Decode((byte[])inv.Arguments[1]!);
                                if (!activated && client.LastFrameRecords.Any(r =>
                                        r is Wire.Table or Wire.Value or Wire.TopicMessage)) streamedEarly = true;
                            }

                            frames++;
                            var ack = new ReadOnlySequence<byte>(ControlBytes("Tap.Ack", outlet.Id, outlet.Nonce,
                                (long)inv.Arguments[0]!, client.HeartbeatSeq));
                            protocol.TryParseMessage(ref ack, binder, out _);
                            break;
                        case "JS.RenderBatch":
                            var id = (long)inv.Arguments[0]!;
                            if (id != lastRenderBatch + 1) throw new Exception("Blazor message reordered");
                            lastRenderBatch = id;
                            renderBatches++;
                            break;
                        case "JS.BeginInvokeJS":
                            bound = true;
                            // As blazor-realtime.js does: register handlers for the tap ids, then activate (step 2).
                            Thread.Sleep(20); // let updates accumulate meanwhile
                            activated = true;
                            foreach (var t in new[] { bookTap.Id, markTap.Id, alertTap.Id })
                            {
                                var act = new ReadOnlySequence<byte>(ControlBytes("Tap.Activate", outlet.Id,
                                    outlet.Nonce, t));
                                protocol.TryParseMessage(ref act, binder, out _);
                            }

                            break;
                    }
                }

                toClient.Reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted) return;
            }
        });

        await manager.SendConnectionAsync("c1", "JS.BeginInvokeJS",
            [1L, "taplineBind", $"[\"{transport.BindToken}\",{outlet.Id},{outlet.Nonce}]", 0, 0L]);

        var rnd = new Random(5);
        var renderTask = Task.Run(async () =>
        {
            for (long i = 0; i < 2000; i++)
            {
                await manager.SendConnectionAsync("c1", "JS.RenderBatch", [i, new byte[rnd.Next(10, 2000)]]);
                if (i % 50 == 0) await Task.Yield();
            }
        });
        for (var step = 0; step < 20_000; step++)
        {
            using (var u = book.BeginUpdate())
            {
                for (var i = rnd.Next(1, 4); i > 0; i--)
                {
                    var level = new Level((byte)rnd.Next(2), 50_000 + rnd.Next(200));
                    if (rnd.Next(5) == 0) u.Delete(level);
                    else u.Fold<long, Add<long>>(level, rnd.Next(1, 1000));
                }

                u.Complete();
            }

            marks.Publish("BTC", BitConverter.GetBytes(step));
            if (step % 100 == 0) alerts.Publish(BitConverter.GetBytes(step));
            if (step % 1000 == 0) outlet.Heartbeat();
            if (step % 200 == 0) await Task.Delay(1);
        }

        await renderTask;

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            lock (client)
            {
                if (client.Tables.TryGetValue(bookTap.Id, out var mm) && mm.Seq == book.Version &&
                    client.Mail(alertTap.Id, false).Count == 200
                    && client.Values.TryGetValue(markTap.Id, out var v) && BitConverter.ToInt32(v) == 19_999)
                    break;
            }

            await Task.Delay(10);
        }

        lock (client)
        {
            Assert(bound, "bind message should pass through to the client");
            Assert(!streamedEarly, "taps streamed before the client activated them");
            // One snapshot at activation; a later one is the server's choice for a client so far behind that a delta would be
            // no smaller — at the first delta's turn, even, under load. Never sooner: the book changes at most three rows a
            // version, so a later snapshot comes a third of its rows' worth of versions after the record before it, at least.
            var later = client.Tables[bookTap.Id].LaterSnapshots;
            Assert(client.Tables[bookTap.Id].SnapshotsBeforeDelta >= 1 && later.All(s => 3 * s.Versions + 1 >= s.Rows),
                $"activation should start with one snapshot, and another only once a delta would be no smaller (later ones: {string.Join(", ", later.Select(s => $"{s.Rows} rows after {s.Versions} versions"))})");
            Assert(client.Tables[bookTap.Id].Failures == 0,
                $"{client.Tables[bookTap.Id].Failures} records failed verification");
            AssertMirror(client.Tables[bookTap.Id], book, "end-to-end");
            Assert(
                client.Mail(alertTap.Id, false).Select(p => BitConverter.ToInt32(p))
                    .SequenceEqual(Enumerable.Range(0, 200).Select(i => i * 100)), "messages lost or reordered");
            Assert(BitConverter.ToInt32(client.Values[markTap.Id]) == 19_999, "latest mark not delivered");
            Assert(renderBatches == 2000, $"render batches {renderBatches}");
            var m = client.Tables[bookTap.Id];
            Notes.Add(
                $"end-to-end: {frames} frames, {m.Snapshots} snapshot(s){(later.Count > 0 ? $" (the later ones {later.Min(s => s.Versions)}+ versions behind)" : "")}, {m.Deltas} deltas, {m.Entries} entries; {renderBatches} Blazor batches in order");
        }

        await manager.OnDisconnectedAsync(hubConnection);
        toClient.Writer.Complete();
        await clientTask;
    }

    public static Task DependencyInjection()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddBlazorRealtime(o => o.FrameBudget = 8192);
        using var app = builder.Build();

        var protocols = app.Services.GetServices<IHubProtocol>().ToArray();
        Assert(
            protocols.Count(p => p.Name == "blazorpack") == 1 &&
            protocols.Single(p => p.Name == "blazorpack") is RealtimeProtocol, "blazorpack not wrapped");
        Assert(
            app.Services.GetRequiredService<IHubProtocolResolver>().GetProtocol("blazorpack", null) is RealtimeProtocol,
            "resolver doesn't pick the wrapper");
        Assert(app.Services.GetRequiredService<HubLifetimeManager<TestHub>>() is CircuitLifetimeManager<TestHub>,
            "lifetime manager not decorated");
        using var scope = app.Services.CreateScope();
        var outlet = scope.ServiceProvider.GetRequiredService<Outlet>();
        Assert(ReferenceEquals(scope.ServiceProvider.GetRequiredService<CircuitTransport>().Outlet, outlet),
            "transport should wrap the scoped outlet");
        Assert(scope.ServiceProvider.GetServices<CircuitHandler>().Any(h => h is RealtimeCircuitHandler),
            "circuit handler missing");

        // The circuit hub's SignalR timeouts: ours, unless the app set its own.
        static (TimeSpan? Timeout, TimeSpan? KeepAlive) HubTimeouts(IServiceProvider services)
        {
            var hub = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("Microsoft.AspNetCore.Components.Server.ComponentHub"))
                .First(t => t is not null)!;
            var accessor =
                services.GetRequiredService(
                    typeof(IOptions<>).MakeGenericType(typeof(HubOptions<>).MakeGenericType(hub)));
            var options = (HubOptions)accessor.GetType().GetProperty("Value")!.GetValue(accessor)!;
            return (options.ClientTimeoutInterval, options.KeepAliveInterval);
        }

        Assert(HubTimeouts(app.Services) == (TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3)),
            $"the circuit hub's timeouts: {HubTimeouts(app.Services)}");
        var own = WebApplication.CreateBuilder();
        own.Services.AddRazorComponents().AddInteractiveServerComponents()
            .AddHubOptions(o => o.ClientTimeoutInterval = TimeSpan.FromSeconds(20));
        own.Services.AddBlazorRealtime();
        using var ownApp = own.Build();
        Assert(HubTimeouts(ownApp.Services) == (TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(3)),
            $"the app's own kept: {HubTimeouts(ownApp.Services)}");
        return Task.CompletedTask;
    }

    public static Task PackageSplit()
    {
        static string[] References(Type type)
        {
            return [.. type.Assembly.GetReferencedAssemblies().Select(a => a.Name!)];
        }

        var core = References(typeof(Publisher));
        Assert(
            core.All(n =>
                n == "System" || n.StartsWith("System.", StringComparison.Ordinal) || n is "netstandard" or "mscorlib"),
            $"core references the BCL alone: {string.Join(", ", core)}");
        var hosting = References(typeof(TaplineOptions));
        string[] blazor =
        [
            "Microsoft.AspNetCore.Components", "Microsoft.AspNetCore.SignalR", "Microsoft.JSInterop", "Tapline.BlazorRealtime"
        ];
        Assert(!hosting.Any(n => blazor.Any(b => n.StartsWith(b, StringComparison.Ordinal))),
            $"Tapline.AspNetCore references nothing of Blazor: {string.Join(", ", hosting)}");
        var client = References(typeof(TaplineClient));
        Assert(client.All(n =>
                n is "Tapline" or "System" or "netstandard" or "mscorlib"
                    or "Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions"
                || n.StartsWith("System.", StringComparison.Ordinal)),
            $"Tapline.Client references core, the BCL and the health checks' abstractions alone: {string.Join(", ", client)}");
        var authentication = References(typeof(Tapline.Authentication.AuthenticationService));
        Assert(authentication.All(n =>
                n is "Tapline" or "System" or "netstandard" or "mscorlib" || n.StartsWith("System.", StringComparison.Ordinal)),
            $"Tapline.Authentication references core and the BCL alone: {string.Join(", ", authentication)}");
        var clientOpaque = References(typeof(Tapline.Client.Opaque.OpaqueCrypto));
        Assert(clientOpaque.All(n =>
                n is "Tapline" or "Tapline.Client" or "Wasmtime.Dotnet" or "Wasmtime" or "System" or "netstandard"
                    or "mscorlib" || n.StartsWith("System.", StringComparison.Ordinal)),
            $"Tapline.Client.Opaque references core, Tapline.Client, Wasmtime and the BCL alone: {string.Join(", ", clientOpaque)}");
        var opaque = References(typeof(Tapline.Authentication.Opaque.OpaqueModule));
        Assert(opaque.All(n =>
                n is "Tapline" or "Tapline.Authentication" or "Tapline.Client.Opaque" or "System" or "netstandard"
                    or "mscorlib" || n.StartsWith("System.", StringComparison.Ordinal)),
            $"Tapline.Authentication.Opaque references core, Tapline.Authentication, Tapline.Client.Opaque and the BCL alone: {string.Join(", ", opaque)}");
        // The client's packages (Tapline.Client: core, the client, MagicOnion's client side; Tapline.Client.Opaque) take
        // nothing of the server's (Tapline, Tapline.Wasm) nor of ASP.NET Core.
        string[] server =
        [
            "Tapline.AspNetCore", "Tapline.BlazorRealtime", "Tapline.Authentication", "Tapline.MagicOnion.Server",
            "Tapline.MagicOnion.Providers", "Tapline.Wasm", "Tapline.Authentication.Opaque"
        ];
        foreach (var type in new[]
                 {
                     typeof(Publisher), typeof(TaplineClient), typeof(Tapline.MagicOnion.TaplineStreamConnection),
                     typeof(Tapline.Client.Opaque.OpaqueCrypto)
                 })
        {
            var taken = References(type)
                .Where(n => server.Contains(n) || n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)).ToArray();
            Assert(taken.Length == 0,
                $"{type.Assembly.GetName().Name} takes nothing of the server's packages: {string.Join(", ", taken)}");
        }
        foreach (var package in new[]
                 {
                     typeof(Publisher), typeof(TaplineOptions), typeof(BlazorRealtimeOptions), typeof(TaplineClient),
                     typeof(Tapline.Authentication.AuthenticationService), typeof(Tapline.Client.Opaque.OpaqueCrypto),
                     typeof(Tapline.Authentication.Opaque.OpaqueModule)
                 })
        {
            var friends = package.Assembly.GetCustomAttributes<InternalsVisibleToAttribute>()
                .Select(a => a.AssemblyName).ToArray();
            Assert(friends.SequenceEqual(["Tapline.Tests"]),
                $"{package.Assembly.GetName().Name}'s internals: the tests' alone ({string.Join(", ", friends)})");
        }

        return Task.CompletedTask;
    }

    public static Task RouterNaming()
    {
        static (RouterInstance Service, RouterInstance Publisher) Build(Dictionary<string, string?> config,
            Action<TaplineOptions>? configure = null,
            Action<IServiceCollection>? before = null, Action<IServiceCollection>? after = null)
        {
            var builder = WebApplication.CreateBuilder();
            foreach (var (k, v) in config) builder.Configuration[k] = v;
            builder.Services.AddRazorComponents().AddInteractiveServerComponents();
            before?.Invoke(builder.Services);
            builder.Services.AddBlazorRealtime();
            builder.Services.AddTapline(configure); // the router's options: after the transport's registration
            after?.Invoke(builder.Services);
            using var app = builder.Build();
            return (app.Services.GetRequiredService<RouterInstance>(),
                app.Services.GetRequiredService<Publisher>().Router);
        }

        var host = Environment.MachineName.ToLowerInvariant();
        Assert(
            Build(new Dictionary<string, string?> { ["urls"] = "http://localhost:5898;http://localhost:5899" })
                .Publisher.Name == $"{host}:5898", "host and the first configured port");
        Assert(
            Build(new Dictionary<string, string?> { ["urls"] = "http://*:7000" }).Publisher.Name == $"{host}:7000" &&
            Build(new Dictionary<string, string?> { ["urls"] = "http://+:7001" }).Publisher.Name == $"{host}:7001",
            "a wildcard host");
        Assert(
            Build(new Dictionary<string, string?> { ["Kestrel:Endpoints:Web:Url"] = "http://0.0.0.0:6000" }).Publisher
                .Name == $"{host}:6000", "Kestrel's configured endpoints");
        Assert(Build(new Dictionary<string, string?> { ["http_ports"] = "8080" }).Publisher.Name == $"{host}:8080",
            "container ports (ASPNETCORE_HTTP_PORTS)");
        Assert(
            Build(new Dictionary<string, string?> { ["urls"] = "http://localhost:0" }).Publisher.Name == host &&
            Build(new Dictionary<string, string?>()).Publisher.Name == host,
            "no port in configuration (or 0): the host alone");
        Assert(
            Build(new Dictionary<string, string?>
                { ["urls"] = "http://localhost:5898", ["Tapline:RouterName"] = "edge-a" }).Publisher.Name == "edge-a",
            "RouterName from configuration");
        Assert(
            Build(new Dictionary<string, string?> { ["Tapline:RouterName"] = "edge-a" }, o => o.RouterName = "edge-b")
                .Publisher.Name == "edge-b", "set in code: over configuration");
        Assert(
            Build(new Dictionary<string, string?> { ["Tapline:RouterName"] = "edge-a" },
                before: s => s.AddTapline(o => o.RouterName = "edge-c")).Publisher.Name == "edge-c",
            "set in code before the transport's registration: kept (registered once, the code's over configuration)");
        Environment.SetEnvironmentVariable("Tapline__RouterName", "from-env");
        try
        {
            Assert(Build(new Dictionary<string, string?>()).Publisher.Name == "from-env",
                "from the environment: Tapline__RouterName");
        }
        finally
        {
            Environment.SetEnvironmentVariable("Tapline__RouterName", null);
        }

        var given = RouterInstance.Start("given");
        var registeredAfter = Build(new Dictionary<string, string?> { ["Tapline:RouterName"] = "edge-a" },
            o => o.RouterName = "edge-b", after: s => s.AddSingleton(given));
        var registeredBefore = Build(new Dictionary<string, string?> { ["Tapline:RouterName"] = "edge-a" },
            before: s => s.AddSingleton(given));
        Assert(
            registeredAfter.Publisher == given && registeredBefore.Publisher == given &&
            registeredAfter.Service == given, "a RouterInstance registered: used as is, whatever else is set");
        var made = Build(new Dictionary<string, string?> { ["urls"] = "http://localhost:5898" });
        Assert(made.Service == made.Publisher, "the Publisher's router is the RouterInstance service");
        return Task.CompletedTask;
    }

    public static Task MessageHistory()
    {
        var registry = new OutletRegistry();
        var s = NewOutlet(registry);
        var owner = new object();
        s.Attach(owner);
        var c = new RefClient();
        var values = new LastValues<int>("hist");
        var tap = s.Tap(values[0], "mark", activate: true);
        var before = Stopwatch.GetTimestamp();
        for (var i = 1; i <= 20; i++)
        {
            values.Publish(0, new byte[i]); // i bytes: the record is 7 + i
            DrainFrames(s, owner, c); // built, decoded, acked
        }

        var view = s.DescribeTaps().Single();
        var m = view.Messages;
        Assert(view is
            {
                Id: var id, Topic: "mark", Source: "hist[0]", State: "active", Class: "Streaming", Version: 20,
                TopicVersion: 20
            } && id == tap.Id
              && view.TopicId == values[0].Id, "the subscription: its topic, source, state, versions");
        Assert(
            m.Count == 16 && m.Select(x => x.N).SequenceEqual(Enumerable.Range(5, 16).Reverse().Select(i => (long)i)),
            "its last sixteen records (the default, a power of two), newest first");
        Assert(
            m.All(x => x.Kind == "value" && x.Bytes == Wire.ValueHeader + (int)x.Version && x.Preview.StartsWith("03")),
            "each: a value record, its size, its first bytes");
        Assert(
            m.All(x => x.ReceivedAt is { } r && r <= x.SentAt && x.LagMs >= 0 && x.AckedAt is { } a && a >= x.SentAt &&
                       x.AckedAfterMs >= 0),
            "received ≤ sent ≤ acked: the lag at the router, the round trip after");
        Assert(m.Zip(m.Skip(1)).All(p => p.First.Frame > p.Second.Frame), "each in its own frame, in order");
        var frames = s.RecentFrames();
        Assert(frames.Count >= 20 && frames.Take(10).All(f => f.AckedAt is not null) && frames.Any(f => f.Records == 1),
            "the circuit's last frames: sent, acked, their records");
        var outlet = s.Describe();
        Assert(outlet.Taps == 1 && outlet.FramesSent >= 20 && outlet.CreatedAt <= DateTimeOffset.UtcNow,
            "the circuit: its subscriptions, frames");
        var odd = NewOutlet(registry, o => o.MessageHistory = 10);
        odd.Attach(owner);
        odd.Tap(values[0], activate: true);
        for (var i = 0; i < 20; i++)
        {
            values.Publish(0, [1]);
            DrainFrames(odd, owner, null);
        }

        Assert(odd.DescribeTaps().Single().Messages.Count == 16,
            "MessageHistory 10: rounded up to a power of two (16)");

        // Its cost: a frame a value, built and acked, with history kept and without (best of three runs of 100k each).
        double NsPerRecord(int depth)
        {
            var o = NewOutlet(new OutletRegistry(), x =>
            {
                x.MessageHistory = depth;
                x.FlowControl = FlowControlMode.Fixed;
            });
            var w = new object();
            o.Attach(w);
            var v = new LastValueSource();
            o.Tap(v, activate: true);
            var payload = new byte[16];
            var best = double.MaxValue;
            for (var run = 0; run < 4; run++)
            {
                var t0 = Stopwatch.GetTimestamp();
                for (var i = 0; i < 100_000; i++)
                {
                    v.Publish(payload);
                    if (o.TryBuildFrame(w, false, out _, out var seq))
                    {
                        o.ReleaseFrame();
                        o.Ack(seq, 0);
                    }
                }

                if (run > 0)
                    best = Math.Min(best,
                        Stopwatch.GetElapsedTime(t0).TotalNanoseconds / 100_000); // (the first warms up)
            }

            return best;
        }

        double with = NsPerRecord(10), without = NsPerRecord(0);
        Notes.Add(
            $"message history: publish + frame + ack {with:F0} ns a record with it kept, {without:F0} ns without (+{with - without:F0} ns)");

        var quiet = NewOutlet(registry, o => o.MessageHistory = 0);
        quiet.Attach(owner);
        quiet.Tap(values[0], activate: true);
        values.Publish(0, [1]);
        DrainFrames(quiet, owner, null);
        Assert(quiet.DescribeTaps().Single().Messages.Count == 0, "MessageHistory 0: nothing kept");
        return Task.CompletedTask;
    }

    public static async Task MessageHistoryBlobs()
    {
        // An out-of-band record: the history keeps its handle — the blob's id — and the viewer leases the content while it
        // is kept; viewed after it's gone (released, evicted), it says so.
        var registry = new OutletRegistry(new TimingWheel(), new BlobStore(grace: TimeSpan.FromMilliseconds(50)));
        var s = NewOutlet(registry);
        var owner = new object();
        s.Attach(owner);
        var source = new LastValueSource();
        s.Tap(source, activate: true);
        var content = Enumerable.Range(0, 100_000).Select(i => (byte)(i * 7)).ToArray();
        source.Publish(content, Delivery.Reference);
        DrainFrames(s, owner, null);
        var record = s.DescribeTaps().Single().Messages.Single();
        Assert(
            record is { Kind: "out-of-band", Inner: "value", Bytes: Wire.OobRefSize } && record.Blob is { Length: 32 },
            "the record: a handle — its inner kind and blob id");
        BlobId.TryParse(record.Blob, out var id);
        Assert(
            s.TryLeaseBlob(id, out var lease) == BlobLookup.Leased && lease!.ToArray().AsSpan().SequenceEqual(content),
            "kept: leased, its content");
        lease.Dispose();
        await Task.Delay(150);
        registry.Blobs.Sweep();
        Assert(s.TryLeaseBlob(id, out var late) == BlobLookup.Gone && late is null,
            "after its grace, swept: gone — said so, not an error");
        Assert(s.TryLeaseBlob(default, out _) == BlobLookup.Gone, "an unknown blob: gone");
    }

    private static List<(uint TapId, long Seq, byte Flags, byte[] Payload)> Of(RefClient c, Tap t)
    {
        return c.TopicMessages.Where(m => m.TapId == t.Id).ToList();
    }

    public static Task MessageTopics()
    {
        var registry = new OutletRegistry();
        var s = NewOutlet(registry);
        var owner = new object();
        s.Attach(owner);
        var c = new RefClient();
        var alerts = new MessageTopic(8, 4096) { Label = "alerts" };
        Assert(alerts.Publish([1]).Status == PublishStatus.NoListeners && alerts.Version == 0,
            "nobody subscribed: 202 — and nothing kept");
        var early = s.Tap(alerts); // step 1
        Assert(alerts.Publish([2]).Status == PublishStatus.Delivered, "bound (between its handshakes): 201");
        early.Activate();
        var asked = s.Tap(alerts, initial: InitialState.Snapshot, activate: true);
        for (byte b = 3; b <= 9; b++) alerts.Publish([b]);
        DrainFrames(s, owner, c);
        Assert(Of(c, early).Select(m => m.Payload[0]).SequenceEqual(new byte[] { 2, 3, 4, 5, 6, 7, 8, 9 }),
            "from the next message after step 1 — the one before its activation too — every one, in order, none conflated");
        Assert(
            Of(c, asked).Select(m => m.Payload[0]).SequenceEqual(new byte[] { 3, 4, 5, 6, 7, 8, 9 }) &&
            Of(c, asked)[0].Seq == 2,
            "a message topic has no snapshot: from its next message, whatever was asked");
        early.Dispose();
        asked.Dispose();
        Assert(alerts.Publish([10]).Status == PublishStatus.NoListeners && alerts.Version == 8,
            "the last subscriber gone: nothing kept again (its buffer released)");
        var later = s.Tap(alerts, activate: true);
        alerts.Publish([11]);
        DrainFrames(s, owner, c);
        Assert(Of(c, later).Single() is { Seq: 9 } m11 && m11.Payload[0] == 11,
            "subscribed again: from the next message, numbered on");
        later.Dispose();

        // Overflow: under Fault, a subscriber that falls behind the buffer ends — an error, not a silent gap.
        var strict = new MessageTopic(4, 4096);
        var slow = s.Tap(strict, initial: InitialState.None, activate: true);
        for (byte b = 1; b <= 10; b++) strict.Publish([b]);
        DrainFrames(s, owner, c);
        Assert(
            slow.IsClosed && slow.Completion.Result?.Code == FaultCode.Overflowed &&
            c.Errors.Any(e => e.TapId == slow.Id && e.Code == FaultCode.Overflowed) && Of(c, slow).Count == 0,
            "Fault: fell behind (10 published, 4 kept, none sent) — its subscription ends, Overflowed");
        var lenient = new MessageTopic(4, 4096, OverflowPolicy.Skip);
        var skipper = s.Tap(lenient, initial: InitialState.None, activate: true);
        for (byte b = 1; b <= 10; b++) lenient.Publish([b]);
        DrainFrames(s, owner, c);
        var skipped = Of(c, skipper);
        Assert(
            skipped.Select(m => m.Payload[0]).SequenceEqual(new byte[] { 7, 8, 9, 10 }) &&
            (skipped[0].Flags & Wire.FlagGap) != 0 && skipped.Skip(1).All(m => m.Flags == 0),
            "Skip: on from the oldest kept, the gap flagged");
        var careful = new MessageTopic(4, 4096, OverflowPolicy.Refuse);
        s.Tap(careful, initial: InitialState.None, activate: true);
        for (byte b = 1; b <= 4; b++) careful.Publish([b]);
        Assert(careful.Publish([5]).Status == PublishStatus.Full,
            "Refuse: it would evict a message a subscriber hasn't been sent — 507");
        DrainFrames(s, owner, c);
        Assert(careful.Publish([5]).Status == PublishStatus.Delivered, "…sent: room again");
        var bulky = new MessageTopic(8, 300, OverflowPolicy.Refuse);
        s.Tap(bulky, initial: InitialState.None, activate: true);
        bulky.Publish(new byte[100]);
        bulky.Publish(new byte[100]);
        DrainFrames(s, owner, c);
        bulky.Publish(new byte[100]); // not sent yet
        Assert(bulky.Publish(new byte[250]).Status == PublishStatus.Full,
            "Refuse: one large message would evict three — the oldest sent, the newest not: 507");
        DrainFrames(s, owner, c);
        Assert(bulky.Publish(new byte[250]).Status == PublishStatus.Delivered, "…all sent: room");
        // A subscriber whose client is away doesn't hold a Refuse topic back: it overflows.
        var away = NewOutlet(registry);
        var awayOwner = new object();
        away.Attach(awayOwner);
        var ac = new RefClient();
        var guarded = new MessageTopic(4, 4096, OverflowPolicy.Refuse);
        var here = s.Tap(guarded, activate: true);
        var gone = away.Tap(guarded, activate: true);
        guarded.Publish([1]);
        DrainFrames(s, owner, c);
        DrainFrames(away, awayOwner, ac);
        away.Detach(); // its connection ends
        for (byte b = 2; b <= 4; b++) guarded.Publish([b]);
        DrainFrames(s, owner, c);
        Assert(guarded.Publish([5]).Status == PublishStatus.Delivered,
            "the client away: not refused — its subscriber falls behind");
        for (byte b = 6; b <= 8; b++) guarded.Publish([b]);
        Assert(guarded.Publish([9]).Status == PublishStatus.Full,
            "…while a connected one not yet sent what it would evict (5) still refuses");
        DrainFrames(s, owner, c);
        away.Attach(awayOwner = new object());
        away.Resume(ac.LastFrame, ac.HeartbeatSeq);
        DrainFrames(away, awayOwner, ac);
        Assert(
            gone.IsClosed && gone.Completion.Result?.Code == FaultCode.Overflowed &&
            ac.Errors.Any(e => e.TapId == gone.Id && e.Code == FaultCode.Overflowed) && !here.IsClosed,
            "back: what it missed was evicted — an overflow, its subscription ended (as under Fault)");

        // The mailbox: the subscriber's alone, in order, before its topic's next record; full of unsent messages, refused.
        var mark = new LastValueSource();
        var marked = s.Tap(mark, activate: true);
        mark.Publish([42]);
        Assert(marked.TryPost("hello"u8) && marked.TryPost("again"u8), "posted");
        DrainFrames(s, owner, c);
        var mail = Of(c, marked);
        Assert(mail.Count == 2 && mail.All(m => (m.Flags & Wire.FlagMailbox) != 0) &&
               mail.Select(m => m.Seq).SequenceEqual(new long[] { 1, 2 })
               && Encoding.ASCII.GetString(mail[0].Payload) == "hello" && c.Values[marked.Id][0] == 42,
            "its mailbox: flagged, numbered apart, alongside its state");
        var tight = NewOutlet(registry, o =>
        {
            o.MailboxMessages = 4;
            o.MailboxBytes = 1024;
        });
        tight.Attach(owner);
        var held = tight.Tap(mark, activate: true);
        var posted = 0;
        while (held.TryPost([1, 2, 3])) posted++;
        Assert(posted == 4 && !held.TryPost(new byte[2000]),
            "full of unsent messages (or a message too large): refused — back-pressure");
        DrainFrames(tight, owner, null);
        Assert(held.TryPost([4]), "sent: room again (the sent ones kept for a reconnect, evicted as needed)");
        return Task.CompletedTask;
    }

    public static Task MessageResume()
    {
        // A reconnect: the client says only the last frame it received (Resume), and every subscription goes on from exactly
        // there — a message stream and a mailbox from the next message it didn't get (none twice), a table with a delta (no
        // snapshot), a value only if it changed.
        var registry = new OutletRegistry();
        var s = NewOutlet(registry);
        var owner = new object();
        s.Attach(owner);
        var c = new RefClient();
        var topic = new MessageTopic(16, 4096);
        var t = s.Tap(topic, activate: true);
        var book = new Table<Level, long>("RESUME");
        FillBook(book, 50);
        var bookTap = s.Tap(book, activate: true);
        var still = new LastValueSource();
        still.Publish([7]);
        var stillTap = s.Tap(still, activate: true);
        var moving = new LastValueSource();
        moving.Publish([1]);
        var movingTap = s.Tap(moving, activate: true);
        for (byte b = 1; b <= 3; b++) topic.Publish([b]);
        Assert(t.TryPost([100]), "a mailbox message");
        DrainFrames(s, owner, c);
        var snapshots = c.Tables[bookTap.Id].Snapshots;
        for (byte b = 4; b <= 6; b++) topic.Publish([b]);
        Assert(t.TryPost([101]), "another");
        using (var u = book.BeginUpdate())
        {
            u.Set(new Level(0, 1), 5);
            u.Complete();
        }

        moving.Publish([2]);
        Assert(s.TryBuildFrame(owner, false, out _, out _),
            "a frame with 4–6, the mailbox's second, the book's delta, the new value…");
        s.ReleaseFrame(); // …lost with the connection
        var owner2 = new object();
        s.Attach(owner2);
        Assert(!s.TryBuildFrame(owner2, false, out var hb, out _) || hb.Length == HeartbeatBytes(hb),
            "held, reconnected: nothing for them till the client says what it received");
        s.ReleaseFrame();
        s.Resume(c.LastFrame, c.HeartbeatSeq); // binding again: the last frame it received
        var sent = new List<uint>();
        for (var i = 0; i < 20 && s.TryBuildFrame(owner2, false, out var f, out var seq); i++)
        {
            c.Decode(f);
            c.LastFrame = seq;
            sent.AddRange(c.LastFrameTaps);
            s.ReleaseFrame();
            s.Ack(seq, c.HeartbeatSeq);
        }

        var got = Of(c, t);
        Assert(got.Where(m => m.Flags == 0).Select(m => m.Payload[0]).SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6 })
               && got.Where(m => m.Flags == Wire.FlagMailbox).Select(m => m.Payload[0])
                   .SequenceEqual(new byte[] { 100, 101 }) && c.Duplicates == 0,
            "messages: the lost ones sent again, none twice");
        Assert(c.Tables[bookTap.Id] is { Failures: 0 } mirror && mirror.Snapshots == snapshots && mirror.Deltas > 0,
            "the table: its delta again, no snapshot");
        AssertMirror(c.Tables[bookTap.Id], book, "resumed");
        Assert(
            !sent.Contains(stillTap.Id) && sent.Count(id => id == movingTap.Id) == 1 && c.Values[movingTap.Id][0] == 2,
            "values: the one that changed sent, the one that didn't not");

        // A frame received whose acknowledgement was lost with the connection: not sent again.
        topic.Publish([7]);
        Assert(s.TryBuildFrame(owner2, false, out var seven, out var sevenSeq), "a frame…");
        c.Decode(seven);
        c.LastFrame = sevenSeq;
        s.ReleaseFrame(); // …received; its ack never arrives
        var owner3 = new object();
        s.Attach(owner3);
        s.Resume(c.LastFrame, c.HeartbeatSeq);
        DrainFrames(s, owner3, c);
        Assert(
            Of(c, t).Where(m => m.Flags == 0).Select(m => m.Payload[0])
                .SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7 }) && c.Duplicates == 0,
            "received, unacknowledged: not sent again");

        // An out-of-band message whose fetch the reconnect abandoned: sent again, under a new handle.
        var large = new MessageTopic(4);
        var lt = s.Tap(large, activate: true);
        var big = Enumerable.Range(0, 40_000).Select(i => (byte)i).ToArray();
        large.Publish(big);
        DrainFrames(s, owner3, c);
        Assert(c.Pending.Count == 1, "a handle, not yet fetched…");
        c.Pending.Clear(); // …and the fetch abandoned with the connection
        var owner4 = new object();
        s.Attach(owner4);
        s.Resume(c.LastFrame, c.HeartbeatSeq);
        DrainFrames(s, owner4, c);
        Assert(c.Pending.Count == 1, "sent again");
        c.FetchPending(s, registry);
        Assert(Of(c, lt).Single().Payload.AsSpan().SequenceEqual(big) && c.Duplicates == 0, "fetched: once");

        // Evicted while it was away: an overflow.
        var small = new MessageTopic(4, 4096);
        var behind = s.Tap(small, activate: true);
        small.Publish([1]);
        DrainFrames(s, owner4, c);
        var owner5 = new object();
        s.Attach(owner5);
        for (byte b = 2; b <= 9; b++) small.Publish([b]); // meanwhile, more than it keeps
        s.Resume(c.LastFrame, c.HeartbeatSeq);
        DrainFrames(s, owner5, c);
        Assert(behind.IsClosed && behind.Completion.Result?.Code == FaultCode.Overflowed,
            "evicted while it was away: an overflow");

        // A client that never says: after the bind timeout, from what it acknowledged.
        var quiet = NewOutlet(registry, o => o.BindTimeout = TimeSpan.FromMilliseconds(50));
        var q1 = new object();
        quiet.Attach(q1);
        var qc = new RefClient();
        var qt = quiet.Tap(topic, activate: true);
        topic.Publish([8]);
        DrainFrames(quiet, q1, qc);
        topic.Publish([9]);
        Assert(quiet.TryBuildFrame(q1, false, out _, out _), "a frame lost");
        quiet.ReleaseFrame();
        var q2 = new object();
        quiet.Attach(q2);
        Thread.Sleep(80);
        quiet.Heartbeat();
        DrainFrames(quiet, q2, qc);
        Assert(Of(qc, qt).Select(m => m.Payload[0]).SequenceEqual(new byte[] { 8, 9 }),
            "no word from the client: on from what it acknowledged");
        return Task.CompletedTask;
    }

    public static async Task ProviderInitialState()
    {
        // A message topic: the provider declares at step 1 it sends the initial state, and sends it on step 2 — its recent
        // history, running through a message the topic delivered after step 1 too. Live messages wait; nothing reordered or twice.
        var registry = new OutletRegistry();
        var publisher = new Publisher(registry);
        var s = NewOutlet(registry, o => o.BindTimeout = TimeSpan.FromMilliseconds(100));
        var owner = new object();
        s.Attach(owner);
        var c = new RefClient();
        var trades = new MessageTopic(64, 4096);
        var provider = new StepTwoProvider("trades-station", trades, InitialState.Provider);
        publisher.Provide("trades", provider.Instance.Endpoint, provider);
        var tap = (await publisher.SubscribeAsync(s, "trades"))!;
        trades.Publish([3]); // between the handshakes: kept for it
        tap.Activate();
        var step2 = await provider.Activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        trades.Publish([4]); // live, before the initial state is done
        DrainFrames(s, owner, c);
        Assert(Of(c, tap).Count == 0 && s.DescribeTap(tap.Id)!.State.Contains("awaiting"),
            "held: nothing of the topic before the initial state");
        foreach (var h in new byte[] { 1, 2, 3 })
            Assert(step2.Mailbox.TryPost([h]), "history posted"); // 1, 2 from its store; 3 the topic's first
        step2.Mailbox.Ready(1); // it runs through the topic's message 1 (the 3)
        DrainFrames(s, owner, c);
        var got = Of(c, tap);
        Assert(got.Select(m => m.Payload[0]).SequenceEqual(new byte[] { 1, 2, 3, 4 }) &&
               got.Take(3).All(m => (m.Flags & Wire.FlagMailbox) != 0)
               && got[3] is { Seq: 2, Flags: 0 } && c.Duplicates == 0,
            "the initial state first, then the topic from after its cut — in order, nothing twice");
        trades.Publish([5]);
        DrainFrames(s, owner, c);
        Assert(Of(c, tap).Last().Payload[0] == 5, "then on, live");

        // A last value: its snapshot on step 2, as of the version it reflects; changes before it never sent after it.
        var price = new LastValueSource();
        var priceProvider = new StepTwoProvider("price-station", price, InitialState.Provider);
        publisher.Provide("price", priceProvider.Instance.Endpoint, priceProvider);
        var priceTap = (await publisher.SubscribeAsync(s, "price"))!;
        price.Publish([1]);
        priceTap.Activate();
        var priceStep2 = await priceProvider.Activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        price.Publish([2]);
        DrainFrames(s, owner, c);
        Assert(!c.Values.ContainsKey(priceTap.Id), "held: no value before the snapshot");
        priceStep2.Mailbox.Send([20], price.Version); // its snapshot, as of version 2
        DrainFrames(s, owner, c);
        Assert(c.Values[priceTap.Id].SequenceEqual(new byte[] { 20 }),
            "the snapshot first — the changes it covers not after it");
        price.Publish([3]);
        DrainFrames(s, owner, c);
        Assert(c.Values[priceTap.Id].SequenceEqual(new byte[] { 3 }), "then the topic's next change");

        // Declared, never sent: the subscription ends once the bind timeout after step 2 passes — told reliably.
        var silent = new StepTwoProvider("silent-station", new MessageTopic(), InitialState.Provider);
        publisher.Provide("silent", silent.Instance.Endpoint, silent);
        var silentTap = (await publisher.SubscribeAsync(s, "silent"))!;
        silentTap.Activate();
        await silent.Activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Thread.Sleep(150);
        s.Heartbeat();
        DrainFrames(s, owner, c);
        Assert(
            silentTap.IsClosed && silentTap.Completion.Result?.Code == FaultCode.NoInitialState &&
            c.Ended.Contains((FaultCode.NoInitialState, silentTap.Id, silentTap.Id)),
            "no initial state within the bind timeout after step 2: ended, NoInitialState, the client told");

        // Misuse: a table's initial state is its snapshot; a direct tap has no provider to send one.
        Assert(Threw<ArgumentException>(() => s.Tap(new Table<Level, long>("init"), initial: InitialState.Provider))
               && Threw<ArgumentException>(() => s.Tap(new LastValueSource(), initial: InitialState.Provider)),
            "refused where it can't mean anything");
    }

    public static Task StaleDataOnInstanceDeath()
    {
        // A station hands the router its stale-data function with each route (an error policy: top of the book, sizes 0).
        // Deemed dead (here: withdrawn), the router runs it on the book it holds; the client gets the degraded book, the
        // error with the station's payload, then the service-down notice — in that order, reliably.
        var registry = new OutletRegistry();
        var publisher = new Publisher(registry);
        var s = NewOutlet(registry);
        var owner = new object();
        s.Attach(owner);
        var c = new RefClient();
        var book = new Table<Level, long>("STALE");
        FillBook(book, 20);
        var station = new TestProvider(book) { Policy = new ErrorPolicy("station down"u8.ToArray(), TopOfBook) };
        Provide(publisher, "book", "book-station", station);
        var tap = publisher.SubscribeAsync(s, "book").AsTask().GetAwaiter().GetResult()!;
        var instance = tap.Binding.NodeId!.Value;
        tap.Activate();
        DrainFrames(s, owner, c);
        Assert(c.Tables[tap.Id].Rows.Count == book.Count && !c.Tables[tap.Id].Degraded, "the book, live");

        publisher.Withdraw(At("book-station")); // the station gone
        Assert(tap.IsClosed && tap.Completion.Result?.Code == FaultCode.ServiceDown,
            "its route ended at once (the router's)");
        Assert(s.TryBuildFrame(owner, false, out var f, out var seq), "a frame");
        c.Decode(f);
        c.LastFrame = seq;
        s.ReleaseFrame();
        Assert(c.LastFrameRecords.SequenceEqual([Wire.Table, Wire.Error, Wire.Service]),
            $"degraded state, error, then down: {string.Join(",", c.LastFrameRecords)}");
        var degraded = c.Tables[tap.Id];
        Assert(
            degraded.Degraded && degraded.Failures == 0 && degraded.Rows.Count == 2 &&
            degraded.Rows.Values.All(r => BitConverter.ToInt64(r.Row) == 0),
            "the station's stale-data function, run by the router: the top of the book, sizes zero");
        Assert(c.Errors.Single() is { Code: FaultCode.ServiceDown } e && e.TapId == tap.Id &&
               Encoding.ASCII.GetString(e.Payload) == "station down"
               && c.ServicesDown.Single() == instance,
            "the error with the station's payload; the service down (its transient id)");

        // That frame lost with the connection: it all comes again, in order.
        var again = new RefClient();
        s.Attach(owner = new object());
        DrainFrames(s, owner, again);
        Assert(
            again.Tables[tap.Id].Degraded && again.Errors.Single().Code == FaultCode.ServiceDown &&
            again.ServicesDown.Single() == instance,
            "unacknowledged: sent again after the reconnect");
        return Task.CompletedTask;
    }

    public static Task ProviderDegraded()
    {
        var registry = new OutletRegistry();
        var publisher = new Publisher(registry)
        {
            ProviderHeartbeatInterval = TimeSpan.Zero, ProviderDegradedAfter = TimeSpan.FromMilliseconds(100),
            ProviderTimeout = TimeSpan.FromMilliseconds(800)
        };
        var s = NewOutlet(registry);
        var owner = new object();
        s.Attach(owner);
        var c = new RefClient();

        void Pump(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                publisher.HeartbeatProviders();
                Thread.Sleep(10);
            }
        }

        // Heartbeats until a step's outcome — the router's, or the client's, frames drained as it goes — 10 s at most.
        void PumpUntil(Func<bool> done, bool drain = false)
        {
            for (var until = Deadline(); !done() && Stopwatch.GetTimestamp() < until;)
            {
                publisher.HeartbeatProviders();
                Thread.Sleep(10);
                if (drain) DrainFrames(s, owner, c);
            }
        }

        // Late to answer: the router's sweep marks it degraded, and tells its clients, in the one heartbeat.
        bool Late(Guid node)
        {
            return publisher.Describe().Instances.Any(i => i.NodeId == node && i.Degraded);
        }

        // The book's station hands over its stale-data function (the top of the book, sizes zero) …
        var book = new Table<Level, long>("DEGRADED-BOOK");
        FillBook(book, 10);
        var bookStation = new TestProvider(book) { Policy = new ErrorPolicy("book stale"u8.ToArray(), TopOfBook) };
        Provide(publisher, "book", "book-station", bookStation);
        var bookTap = publisher.SubscribeAsync(s, "book").AsTask().GetAwaiter().GetResult()!;
        var bookInstance = bookTap.Binding.NodeId!.Value;
        bookTap.Activate();
        // … the price's a fixed value …
        var price = new LastValueSource();
        price.Publish([1]);
        var priceStation = new TestProvider(price) { Policy = ErrorPolicy.Static(new byte[] { 0xEE }) };
        Provide(publisher, "price", "price-station", priceStation);
        var priceTap = publisher.SubscribeAsync(s, "price").AsTask().GetAwaiter().GetResult()!;
        priceTap.Activate();
        // … the trades' the last one again.
        var trades = new MessageTopic(16, 4096);
        var tradesStation = new TestProvider(trades) { Policy = ErrorPolicy.ReplayLast() };
        Provide(publisher, "trades", "trades-station", tradesStation);
        var tradesTap = publisher.SubscribeAsync(s, "trades").AsTask().GetAwaiter().GetResult()!;
        tradesTap.Activate();
        trades.Publish([1]);
        trades.Publish([2]);
        Pump(30);
        DrainFrames(s, owner, c);
        Assert(
            c.Tables[bookTap.Id].Rows.Count == book.Count && c.Values[priceTap.Id][0] == 1 &&
            Of(c, tradesTap).Count == 2, "live");

        // Late to answer its health check: degraded — told first, then shown its stale data; held, not ended.
        var late = new TaskCompletionSource<HealthReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        bookStation.Health = (_, _) => new ValueTask<HealthReport>(late.Task);
        PumpUntil(() => Late(bookInstance));
        Assert(s.TryBuildFrame(owner, false, out var f, out var seq), "a frame");
        c.Decode(f);
        c.LastFrame = seq;
        s.ReleaseFrame();
        s.Ack(seq, c.HeartbeatSeq);
        Assert(
            c.LastFrameRecords.Take(2).SequenceEqual([Wire.Service, Wire.Table]) &&
            c.ProviderStates.Single() == (bookInstance, "degraded"),
            "the provider's state first, then the stale data");
        Assert(
            c.Tables[bookTap.Id] is { Degraded: true, Failures: 0 } stale && stale.Rows.Count == 2 &&
            stale.Rows.Values.All(r => BitConverter.ToInt64(r.Row) == 0) && !bookTap.IsClosed,
            "the station's function, run by the router: the top of the book, sizes zero — the subscription open");
        using (var u = book.BeginUpdate())
        {
            u.Set(new Level(0, 1), 7);
            u.Complete();
        }

        DrainFrames(s, owner, c);
        Assert(c.Tables[bookTap.Id].Degraded, "held: nothing over the stale state while degraded");
        bookStation.Health = null;
        late.SetResult(new HealthReport(bookStation.Instance!, ProviderStatus.Serving)); // it answers again
        PumpUntil(() => c.ProviderStates.Last() == (bookInstance, "serving") && !c.Tables[bookTap.Id].Degraded, drain: true);
        DrainFrames(s, owner, c);
        Assert(
            c.ProviderStates.Last() == (bookInstance, "serving") &&
            c.Tables[bookTap.Id] is { Degraded: false, Failures: 0 } back && back.Rows.Count == book.Count,
            "recovered: told, then the book from a snapshot");
        AssertMirror(c.Tables[bookTap.Id], book, "recovered");

        // Unhealthy by its own word: degraded at once; healthy again: recovered. A fixed stale value meanwhile.
        priceStation.Status = ProviderStatus.NotServing;
        PumpUntil(() => c.ProviderStates.Contains((priceTap.Binding.NodeId!.Value, "degraded")), drain: true);
        price.Publish([2]);
        DrainFrames(s, owner, c);
        Assert(
            c.ProviderStates.Contains((priceTap.Binding.NodeId!.Value, "degraded")) && c.Values[priceTap.Id][0] == 0xEE,
            "not serving: degraded, the fixed value shown");
        priceStation.Status = ProviderStatus.Serving;
        PumpUntil(() => c.ProviderStates.Last() == (priceTap.Binding.NodeId!.Value, "serving"), drain: true);
        DrainFrames(s, owner, c);
        Assert(c.ProviderStates.Last() == (priceTap.Binding.NodeId!.Value, "serving") && c.Values[priceTap.Id][0] == 2,
            "serving again: its current value");

        // The last trade again, flagged stale; trades meanwhile held, then in order; gone while degraded: the error, once.
        var lateTrades = new TaskCompletionSource<HealthReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        tradesStation.Health = (_, _) => new ValueTask<HealthReport>(lateTrades.Task);
        PumpUntil(() => Late(tradesTap.Binding.NodeId!.Value));
        trades.Publish([3]);
        DrainFrames(s, owner, c);
        var got = Of(c, tradesTap);
        Assert(got.Count == 3 && got[2] is { Seq: 2, Flags: Wire.FlagStale } replay && replay.Payload[0] == 2,
            "the last trade replayed, flagged stale (not a new one); the new one held");
        tradesStation.Health = null;
        lateTrades.SetResult(new HealthReport(tradesStation.Instance!, ProviderStatus.Serving));
        PumpUntil(() => Of(c, tradesTap).Count == 4, drain: true);
        DrainFrames(s, owner, c);
        Assert(Of(c, tradesTap).Count == 4 && Of(c, tradesTap)[3] is { Seq: 3, Flags: 0 } held && held.Payload[0] == 3,
            "recovered: the trade held meanwhile, in order");
        var gone = new TaskCompletionSource<HealthReport>();
        tradesStation.Health = (_, _) => new ValueTask<HealthReport>(gone.Task);
        PumpUntil(() => tradesTap.IsClosed); // degraded (the last trade again), then past its timeout: deemed dead
        DrainFrames(s, owner, c);
        Assert(tradesTap.IsClosed && c.Errors.Single() is { Code: FaultCode.ServiceDown } e &&
               e.TapId == tradesTap.Id && Of(c, tradesTap).Count == 5
               && c.ServicesDown.Contains(tradesTap.Binding.NodeId!.Value),
            "gone while degraded: the error and the service down — the stale data not again");
        return Task.CompletedTask;
    }

    public static Task DisconnectedHold()
    {
        // Disconnected, a client isn't stalled: its subscriptions are held — for as long as its outlet lives (Blazor's
        // circuit) — and resume exactly when it's back.
        var registry = new OutletRegistry();
        var s = NewOutlet(registry, o => o.StallTimeout = TimeSpan.FromMilliseconds(100));
        var owner = new object();
        s.Attach(owner);
        var c = new RefClient();
        var value = new LastValueSource();
        value.Publish([1]);
        var t = s.Tap(value, activate: true);
        DrainFrames(s, owner, c);
        s.Detach(); // its connection gone
        value.Publish([2]); // something for it, unsent
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 400)
        {
            s.Heartbeat();
            Thread.Sleep(20);
        }

        Assert(!t.IsClosed && s.Stalls == 0, "disconnected four times the stall timeout: held, not stalled");
        s.Attach(owner = new object());
        s.Resume(c.LastFrame, c.HeartbeatSeq);
        DrainFrames(s, owner, c);
        Assert(c.Values[t.Id][0] == 2 && c.Ended.Count == 0, "back: resumed, nothing ended");

        // With a hold: released once it passes, the outlet (the circuit) living on; told when it's back.
        var h = NewOutlet(registry, o => o.DisconnectedHold = TimeSpan.FromMilliseconds(150));
        var ho = new object();
        h.Attach(ho);
        var hc = new RefClient();
        var ht = h.Tap(value, activate: true);
        DrainFrames(h, ho, hc);
        h.Detach();
        sw.Restart();
        while (!ht.IsClosed && sw.ElapsedMilliseconds < 2000)
        {
            h.Heartbeat();
            Thread.Sleep(20);
        }

        Assert(
            ht.IsClosed && ht.Completion.Result?.Code == FaultCode.Stalled && sw.ElapsedMilliseconds >= 130 &&
            h.Stalls == 1, $"released after its hold ({sw.ElapsedMilliseconds} ms)");
        h.Attach(ho = new object());
        h.Resume(hc.LastFrame, hc.HeartbeatSeq);
        DrainFrames(h, ho, hc);
        Assert(hc.Ended.Single() == (FaultCode.Stalled, 1u, ht.Id), "told when it's back: one Ended record");
        return Task.CompletedTask;
    }

    public static Task ReconciledAtRebind()
    {
        var registry = new OutletRegistry();
        var s = NewOutlet(registry);
        var owner = new object();
        s.Attach(owner);
        var c = new RefClient();
        var small = new MessageTopic(4, 4096);
        var behind = s.Tap(small, activate: true);
        var value = new LastValueSource();
        value.Publish([1]);
        var dropped = s.Tap(value, activate: true);
        var kept = s.Tap(new LastValueSource(), activate: true);
        small.Publish([1]);
        DrainFrames(s, owner, c);
        for (byte b = 2; b <= 9; b++) small.Publish([b]); // more than it keeps: an overflow…
        Assert(s.TryBuildFrame(owner, false, out _, out _) && behind.IsClosed, "…its error written into a frame…");
        s.ReleaseFrame(); // …lost with the connection
        dropped.Dispose(); // and one the app closed meanwhile (nothing sent for it)
        var owner2 = new object();
        s.Attach(owner2);
        s.Resume(c.LastFrame, c.HeartbeatSeq);
        foreach (var t in new[] { behind, dropped, kept })
            s.Activate(t.Id); // the client re-activates all it holds, binding again
        DrainFrames(s, owner2, c);
        Assert(
            c.Ended.Count == 2 && c.Ended.Contains((FaultCode.Overflowed, behind.Id, behind.Id)) &&
            c.Ended.Contains((FaultCode.None, dropped.Id, dropped.Id)),
            "told at the rebind: each that ended while it was away, and why");
        Assert(!kept.IsClosed && !c.Ended.Any(e => e.From <= kept.Id && kept.Id <= e.To),
            "the one still held: nothing");

        // One it no longer remembers (more have ended since): Gone. An id never issued: ignored.
        foreach (var t in Enumerable.Range(0, 70).Select(_ => s.Tap(new LastValueSource())).ToList()) t.Dispose();
        c.Ended.Clear();
        s.Activate(behind.Id);
        s.Activate(1_000_000);
        DrainFrames(s, owner2, c);
        Assert(c.Ended.Single() == (FaultCode.Gone, behind.Id, behind.Id), "forgotten: Gone; never issued: nothing");
        return Task.CompletedTask;
    }

    public static Task OrderingConstraints()
    {
        // A message ordered after the state it refers to — a fill after the position it changed.
        var registry = new OutletRegistry();
        var s = NewOutlet(registry);
        var c = new RefClient();
        var scratch = new byte[1 << 20];
        var positions = new Table<Holding, long>("ORD-POS");
        var fills = new MessageTopic(64, 64 * 1024);
        var posTap = s.Tap(positions, activate: true);
        var fillTap = s.Tap(fills, activate: true, lane: Lane.High); // High: served before Streaming…
        Drain(s, c, scratch);
        using (var u = positions.BeginUpdate())
        {
            u.Set(new Holding(1, 1), 10);
            u.Complete();
        }

        fills.Publish("fill 1"u8, After.Current(positions));
        c.Decode(scratch.AsSpan(0, s.Build(scratch)));
        Assert(
            c.LastFrameTaps.SequenceEqual([posTap.Id, fillTap.Id]) &&
            c.LastFrameRecords.SequenceEqual([Wire.Table, Wire.TopicMessage]),
            "…but the position first, pulled into the same frame");
        fills.Publish("fill 2"u8, After.Current(positions));
        c.Decode(scratch.AsSpan(0, s.Build(scratch)));
        Assert(c.LastFrameRecords.SequenceEqual([Wire.TopicMessage]),
            "the client has that state already: nothing extra");

        // Out of band: the message held for the state's fetch; that tap closed, it goes.
        var big = new Table<Holding, long>("ORD-BIG");
        var bigTap = s.Tap(big, activate: true);
        Drain(s, c, scratch);
        using (var u = big.BeginUpdate())
        {
            for (var i = 0; i < 3000; i++) u.Set(new Holding(i, 1), i);
            u.Complete();
        }

        fills.Publish("book ready"u8, After.Current(big));
        Drain(s, c, scratch);
        var before = Of(c, fillTap).Count;
        Assert(c.Pending.Count == 1 && before == 2 && !s.IsReady,
            "the state went out of band: the message held for its fetch");
        c.FetchPending(s, registry);
        Drain(s, c, scratch);
        Assert(Of(c, fillTap).Count == 3 && c.Tables[bigTap.Id].Rows.Count == 3000,
            "fetched: delivered after the state it refers to");
        using (var u = big.BeginUpdate())
        {
            for (var i = 0; i < 3000; i++) u.Set(new Holding(i, 1), i + 1);
            u.Complete();
        }

        fills.Publish("book again"u8, After.Current(big));
        Drain(s, c, scratch);
        Assert(c.Pending.Count == 1 && Of(c, fillTap).Count == 3, "held for the fetch again…");
        bigTap.Dispose();
        Drain(s, c, scratch);
        Assert(Of(c, fillTap).Count == 4 && Of(c, fillTap)[3].Payload.AsSpan().SequenceEqual("book again"u8),
            "…the tap closed: it goes");
        c.Pending.Clear();

        // A subscriber with no tap on that source isn't held.
        var other = NewOutlet(registry);
        var oc = new RefClient();
        var otherFills = other.Tap(fills, activate: true);
        using (var u = big.BeginUpdate())
        {
            u.Set(new Holding(0, 1), 99);
            u.Complete();
        }

        fills.Publish("fill 3"u8, After.Current(big));
        Drain(other, oc, scratch);
        Assert(Of(oc, otherFills).Single().Payload.AsSpan().SequenceEqual("fill 3"u8),
            "no tap on that source: not held");

        // A mailbox message after one of the client's taps.
        var orders = new LastValueSource();
        var orderTap = s.Tap(orders, activate: true);
        Drain(s, c, scratch);
        orders.Publish([1]);
        Assert(fillTap.TryPost("reply"u8, After.Current(orderTap)), "a reply, after the order it refers to");
        c.Decode(scratch.AsSpan(0, s.Build(scratch)));
        Assert(c.LastFrameTaps.SequenceEqual([orderTap.Id, fillTap.Id]), "the order first, then the reply");
        Assert(Threw<ArgumentException>(() => fillTap.TryPost("x"u8, After.Current(other.Tap(orders)))),
            "after another client's tap: refused");
        Assert(Threw<ArgumentException>(() => fills.Publish("x"u8, After.Current(orderTap))),
            "a topic's message names a source, not one client's tap");

        // It overdraws a rate limit rather than wait behind it.
        var clock = new ManualTime();
        var wheel = new TimingWheel(TimeSpan.FromMilliseconds(5), time: clock, autoStart: false);
        var r = NewOutlet(new OutletRegistry(wheel), o =>
        {
            o.StreamingBytesPerSecond = 1000;
            o.StreamingBurstBytes = 1000;
        });
        var rc = new RefClient();
        var bulk = new Table<Level, long>("ORD-BULK") { Delivery = Delivery.Inline };
        FillBook(bulk, 400);
        r.Tap(bulk, activate: true);
        var rPos = r.Tap(positions, activate: true);
        var rFill = r.Tap(fills, activate: true, lane: Lane.High);
        while (r.IsReady) rc.Decode(scratch.AsSpan(0, r.Build(scratch)));
        using (var u = positions.BeginUpdate())
        {
            u.Set(new Holding(1, 1), 11);
            u.Complete();
        }

        Assert(!r.IsReady, "Streaming sits out while in debt");
        fills.Publish("fill 4"u8, After.Current(positions));
        rc.Decode(scratch.AsSpan(0, r.Build(scratch)));
        Assert(rc.LastFrameTaps.SequenceEqual([rPos.Id, rFill.Id]), "pulled forward despite the debt");
        return Task.CompletedTask;
    }

    /// <summary>
    ///     A module of the ABI whose degrade does <paramref name="body" /> (and whose buffer is at 0, its memory one
    ///     page).
    /// </summary>
    private static string DegradeWat(string body)
    {
        return $$"""
                 (module
                   (memory (export "memory") 1)
                   (func (export "buffer") (param i32) (result i32) (i32.const 0))
                   (func (export "degrade") (param i32 i32 i32 i32) {{body}}))
                 """;
    }

    public static async Task WasmStaleData()
    {
        using var top = WasmDegradeFunction.FromText(TopOfBookWat);
        Assert(
            top.Module.Span[..4].SequenceEqual("\0asm"u8) && top.ModuleId ==
            Convert.ToHexStringLower(SHA256.HashData(top.Module.Span).AsSpan(0, 16)),
            "its binary (what clients get), content-addressed");

        // The router runs it: the same stale state as the C# function, verified by its checksum.
        var registry = new OutletRegistry();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(registry);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapBlazorRealtime();
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        var s = NewOutlet(registry);
        var book = new Table<Level, long>("WASM");
        FillBook(book, 40, 1);
        var big = new Table<Level, long>("WASM-BIG");
        FillBook(big, 2000, 1); // 4,000 rows: 84 KB of entries
        using var spin = WasmDegradeFunction.FromText(DegradeWat("(loop $forever (br $forever))"),
            new WasmLimits { Fuel = 5_000_000 });
        using var trap = WasmDegradeFunction.FromText(DegradeWat("unreachable"));
        using var small = WasmDegradeFunction.FromText(TopOfBookWat, new WasmLimits { MemoryBytes = 64 * 1024 });
        var wasmTap = s.Tap(book, "book", new ErrorPolicy("stale"u8.ToArray(), top), true);
        var csTap = s.Tap(book, "book", new ErrorPolicy("stale"u8.ToArray(), TopOfBook), true);
        var spinTap = s.Tap(book, "book", new ErrorPolicy("stale"u8.ToArray(), spin), true);
        var trapTap = s.Tap(book, "book", new ErrorPolicy("stale"u8.ToArray(), trap), true);
        var smallTap = s.Tap(big, "big", new ErrorPolicy("stale"u8.ToArray(), small), true);
        var bigTap = s.Tap(big, "big", new ErrorPolicy("stale"u8.ToArray(), top), true);
        var c = new RefClient();
        var scratch = new byte[1 << 20];
        Drain(s, c, scratch);

        // Its binding names its module; the client fetches it from the router while a subscription bound with it is.
        var binding = wasmTap.Binding;
        Assert(
            binding.Stale is { Kind: StaleData.Function, Module: var moduleId } && moduleId == top.ModuleId &&
            csTap.Binding.Stale is null,
            "the binding names the module (a C# function: nothing for the client to apply)");
        var json = JsonSerializer.Serialize(binding, JsonSerializerOptions.Web);
        Assert(json.Contains($"\"stale\":{{\"kind\":0,\"state\":\"\",\"module\":\"{top.ModuleId}\"}}"),
            $"as the page gets it: {json}");
        Assert(
            JsonSerializer
                .Serialize(s.Tap(book, "book", ErrorPolicy.Static(new byte[] { 1, 2, 3 })).Binding,
                    JsonSerializerOptions.Web).Contains("\"stale\":{\"kind\":1,\"state\":\"AQID\""),
            "a fixed state rides in the binding (base64)");
        var served = await http.GetAsync($"/_tapline/module/{top.ModuleId}");
        Assert(served.StatusCode == HttpStatusCode.OK && served.Content.Headers.ContentType?.MediaType ==
                                                      "application/wasm"
                                                      && served.Headers.CacheControl?.ToString()
                                                          .Contains("immutable") == true &&
                                                      (await served.Content.ReadAsByteArrayAsync()).AsSpan()
                                                      .SequenceEqual(top.Module.Span),
            "served: application/wasm, immutable");
        Assert((await http.GetAsync($"/_tapline/module/{new string('0', 32)}")).StatusCode == HttpStatusCode.NotFound,
            "an unknown module: 404");
        Assert(registry.Modules.Count == 3 && small.ModuleId == top.ModuleId,
            $"one entry a module, however many subscriptions (and functions: the same module, other limits) hold it: {registry.Modules.Count}");

        try
        {
            using var u = book.BeginUpdate(7);
        }
        catch (StreamFaultException)
        {
        } // the books fault: stale data, then the error

        try
        {
            using var u = big.BeginUpdate(7);
        }
        catch (StreamFaultException)
        {
        }

        var sw = Stopwatch.StartNew();
        Drain(s, c, scratch);
        var faultedMs = sw.ElapsedMilliseconds;
        var w = c.Tables[wasmTap.Id];
        var cs = c.Tables[csTap.Id];
        Assert(w is { Degraded: true, Failures: 0 } && w.Rows.Count == 2 &&
               w.Rows.Values.All(r => BitConverter.ToInt64(r.Row) == 0)
               && w.Rows.Keys.Order().SequenceEqual(cs.Rows.Keys.Order()),
            "the module's stale state: the top of the book, sizes zero — as the C# function's");
        Assert(c.Tables[bigTap.Id] is { Degraded: true, Failures: 0 } b && b.Rows.Count == 2,
            "84 KB of entries: its memory grown to fit");
        foreach (var (tap, why) in new[]
                     { (spinTap, "out of fuel"), (trapTap, "a trap"), (smallTap, "out of memory (64 KB)") })
            Assert(c.Tables[tap.Id] is { Degraded: true, Failures: 0 } e && e.Rows.Count == 0,
                $"{why}: an empty stale state");
        Assert(spin.Failures == 1 && trap.Failures == 1 && small.Failures == 1 && top.Failures == 0,
            "each failure counted, its instance discarded");
        Assert(c.Errors.Count == 6, "then each its error");
        Assert(registry.Modules.Count == 0 && (await http.GetAsync($"/_tapline/module/{top.ModuleId}")).StatusCode ==
                                           HttpStatusCode.NotFound
                                           && registry.Modules.Count == 0,
            "their subscriptions ended: no longer served");
        var after = s.Tap(book, "book", new ErrorPolicy("stale"u8.ToArray(), trap));
        Assert(registry.Modules.TryGet(trap.ModuleId, out _), "a failed function still runs: a new instance");
        after.Dispose();
        Assert(!registry.Modules.TryGet(trap.ModuleId, out _), "released with the subscription");

        // Refused: not a module; importing anything (a browser instantiates it as is); without its exports, or of other types.
        Assert(Threw<ArgumentException>(() => WasmDegradeFunction.FromBytes("not wasm"u8)), "not a module");
        Assert(Threw<ArgumentException>(() => WasmDegradeFunction.FromText("(module (func")), "not WebAssembly text");
        Assert(
            Threw<ArgumentException>(() =>
                WasmDegradeFunction.FromText(
                    """(module (import "env" "now" (func)) (memory (export "memory") 1) (func (export "buffer") (param i32) (result i32) (i32.const 0)) (func (export "degrade") (param i32 i32 i32 i32)))""")),
            "importing");
        Assert(
            Threw<ArgumentException>(() =>
                WasmDegradeFunction.FromText(
                    """(module (memory (export "memory") 1) (func (export "buffer") (param i32) (result i32) (i32.const 0)))""")),
            "no degrade");
        Assert(
            Threw<ArgumentException>(() =>
                WasmDegradeFunction.FromText(
                    """(module (memory (export "memory") 1) (func (export "buffer") (param i32) (result i32) (i32.const 0)) (func (export "degrade") (param i32)))""")),
            "degrade of another type");
        Assert(
            Threw<ArgumentException>(() =>
                WasmDegradeFunction.FromText(
                    """(module (func (export "buffer") (param i32) (result i32) (i32.const 0)) (func (export "degrade") (param i32 i32 i32 i32)))""")),
            "no memory");
        Assert(
            Threw<ArgumentException>(() =>
                WasmDegradeFunction.FromBytes(new byte[WasmDegradeFunction.MaxModuleBytes + 1])), "larger than 1 MB");
        Assert(Threw<ArgumentOutOfRangeException>(() => ErrorPolicy.Static(new byte[ErrorPolicy.MaxState + 1])),
            "a fixed state larger than 64 KB");

        // Concurrent applications (outlets building frames at once): each its own instance, every result the C# function's.
        const int rows = 400, stride = 4 + 9 + 8;
        var input = new byte[rows * stride];
        var rng = new Random(5);
        for (var i = 0; i < rows; i++)
        {
            var e = input.AsSpan(i * stride, stride);
            e[0] = 1; // present
            e[4] = (byte)(i & 1); // side
            BinaryPrimitives.WriteInt64LittleEndian(e[5..], rng.NextInt64(1, 1_000_000));
            BinaryPrimitives.WriteInt64LittleEndian(e[13..], i + 1);
        }

        var expected = input.ToArray();
        TopOfBook.Degrade(expected, rows, 9, 8);
        var wrong = 0;
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
        {
            var mine = new byte[input.Length];
            for (var k = 0; k < 200; k++)
            {
                input.CopyTo(mine, 0);
                top.Degrade(mine, rows, 9, 8);
                if (!mine.AsSpan().SequenceEqual(expected)) Interlocked.Increment(ref wrong);
            }
        });
        Assert(wrong == 0 && top.Failures == 0, $"8 threads × 200 applications: {wrong} wrong");

        var one = new byte[input.Length];
        const int n = 2000;
        for (var warm = 0; warm < 3; warm++) // both warm: the C# function at its final JIT tier
        {
            for (var k = 0; k < n; k++)
            {
                input.CopyTo(one, 0);
                top.Degrade(one, rows, 9, 8);
                TopOfBook.Degrade(one, rows, 9, 8);
            }

            Thread.Sleep(150);
        }

        sw.Restart();
        for (var k = 0; k < n; k++)
        {
            input.CopyTo(one, 0);
            top.Degrade(one, rows, 9, 8);
        }

        var wasmUs = sw.Elapsed.TotalMicroseconds / n;
        sw.Restart();
        for (var k = 0; k < n; k++)
        {
            input.CopyTo(one, 0);
            TopOfBook.Degrade(one, rows, 9, 8);
        }

        var csUs = sw.Elapsed.TotalMicroseconds / n;
        var before = AllocationBaseline();
        for (var k = 0; k < n; k++)
        {
            input.CopyTo(one, 0);
            top.Degrade(one, rows, 9, 8);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert(allocated == 0, $"{allocated} bytes allocated over {n} applications");
        sw.Restart();
        using (WasmDegradeFunction.FromBytes(top.Module.Span))
        {
        }

        var loadMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        try
        {
            spin.Degrade(one, rows, 9, 8);
        }
        catch (TrapException)
        {
        }

        var fuelMs = sw.Elapsed.TotalMilliseconds;
        Notes.Add(
            $"wasm stale data: the top of a {rows}-row book {wasmUs:F1} µs an application (the C# function {csUs:F1} µs); a module compiled and instantiated in {loadMs:F1} ms; 5 M fuel spent in {fuelMs:F1} ms; six faulted books drained in {faultedMs} ms; {allocated} bytes allocated over {n} applications");

        File.WriteAllBytes(Path.Combine(RepoRoot(), "tests", "js", "fixtures", "top-of-book.wasm"),
            top.Module.ToArray()); // for the JS client's test
    }

    /// <summary>
    ///     A module of the fold ABI whose fold does <paramref name="body" /> (and whose buffer is at 0, its memory one
    ///     page).
    /// </summary>
    private static string FoldWat(string body)
    {
        return $$"""
                 (module
                   (memory (export "memory") 1)
                   (func (export "buffer") (param i32) (result i32) (i32.const 0))
                   (func (export "fold") (param i32 i32 i32 i32 i32) {{body}}))
                 """;
    }

    public static Task WasmFold()
    {
        using var add = WasmBatchFold.FromText(AddFoldWat);
        var cs = new BytesAdd();

        // The same rows as the compile-time fold and the C# batch fold, update after update.
        var wasmBook = new Table<Level, long>("WASM-FOLD");
        var csBook = new Table<Level, long>("CS-FOLD");
        var refBook = new Table<Level, long>("REF-FOLD");
        var rng = new Random(11);
        var keys = new Level[64];
        var deltas = new long[64];
        for (var step = 0; step < 2000; step++)
        {
            var n = rng.Next(1, keys.Length + 1);
            for (var i = 0; i < n; i++)
            {
                keys[i] = new Level((byte)rng.Next(2), 1000 + rng.Next(40));
                deltas[i] = rng.Next(-3, 4);
            }

            using (var u = wasmBook.BeginUpdate())
            {
                u.Fold(keys.AsSpan(0, n), deltas.AsSpan(0, n), add);
                u.Complete();
            }

            using (var u = csBook.BeginUpdate())
            {
                u.Fold(keys.AsSpan(0, n), deltas.AsSpan(0, n), cs);
                u.Complete();
            }

            using (var u = refBook.BeginUpdate())
            {
                for (var i = 0; i < n; i++) u.Fold<long, Add<long>>(keys[i], deltas[i]);
                u.Complete();
            }
        }

        Assert(wasmBook.Checksum == refBook.Checksum && wasmBook.Version == refBook.Version &&
               SameRows(Canonical(wasmBook), Canonical(refBook))
               && SameRows(Canonical(csBook), Canonical(refBook)) && add.Failures == 0,
            "the module's rows: the compile-time fold's, and the C# batch fold's");

        // Failures: the whole update rolled back, the table faulted, the instance discarded (counted); recovered, it folds again.
        using var spin = WasmBatchFold.FromText(FoldWat("(loop $forever (br $forever))"),
            new WasmLimits { Fuel = 5_000_000 });
        using var trap = WasmBatchFold.FromText(FoldWat("unreachable"));
        using var small = WasmBatchFold.FromText(AddFoldWat, new WasmLimits { MemoryBytes = 64 * 1024 });
        var many = Enumerable.Range(0, 4000).Select(i => new Level(0, i)).ToArray(); // 4,000 rows + deltas: 96 KB
        var ones = Enumerable.Repeat(1L, many.Length).ToArray();
        foreach (var (fold, why, ks, ds) in new[]
                 {
                     (spin, "out of fuel", keys[..1], deltas[..1]), (trap, "a trap", keys[..1], deltas[..1]),
                     (small, "out of memory (64 KB)", many, ones)
                 })
        {
            var t = new Table<Level, long>("WASM-FAULT");
            using (var u = t.BeginUpdate())
            {
                u.Set(new Level(0, 1), 1);
                u.Complete();
            }

            var good = Canonical(t);
            var version = t.Version;
            try
            {
                using var u = t.BeginUpdate();
                u.Set(new Level(1, 2), 2);
                u.Fold(ks, ds, fold);
                u.Complete();
                throw new Exception($"{why}: expected a fault");
            }
            catch (StreamFaultException e)
            {
                Assert(e.Fault.Code == FaultCode.FoldFailed && (fold == spin
                        ? e.InnerException is FoldBudgetExceededException
                        {
                            InnerException: TrapException { Type: TrapCode.OutOfFuel }
                        }
                        : e.InnerException is TrapException),
                    $"{why}: FoldFailed, the trap its cause — out of fuel, as the budget's ({e.InnerException?.GetType().Name})");
            }

            Assert(t.Fault?.Code == FaultCode.FoldFailed && t.Version == version && SameRows(Canonical(t), good),
                $"{why}: the whole update rolled back");
            Assert(fold.Failures == 1, $"{why}: counted, its instance discarded");
            using (var u = t.BeginRecovery())
            {
                u.Fold([new Level(0, 1)], [4L], fold == small ? small : add);
                u.Complete();
            }

            Assert(t.Fault is null && t.TryGet(new Level(0, 1), out var q) && q == 5, $"{why}: recovered");
        }

        Assert(small.Failures == 1, "a fold whose instance failed runs again in a new one");

        // A .NET fault on a thread that has run a module — the hardware fault behind a NullReferenceException — is the
        // runtime's, caught as ever (on macOS Wasmtime's Mach exception ports would take it, and the process die): before
        // a trap and after one.
        var none = taggedNull();
        var rowBytes1 = new byte[12];
        add.Fold(rowBytes1, 1, 8, new byte[12], 1, 8);
        Assert(Threw<NullReferenceException>(() => _ = none!.Length),
            "a NullReferenceException after a module ran on this thread: caught");
        Assert(
            Threw<FoldBudgetExceededException>(() => spin.Fold(rowBytes1, 1, 8, new byte[12], 1, 8)) &&
            Threw<NullReferenceException>(() => _ = none!.Length),
            "a trap still caught as the module's, and a NullReferenceException after it as the runtime's");

        static string? taggedNull()
        {
            return Environment.TickCount64 < 0 ? "never" : null;
        }

        // Refused: not a module; importing anything; without its exports, or of other types (a stale-data function isn't a fold).
        Assert(Threw<ArgumentException>(() => WasmBatchFold.FromBytes("not wasm"u8)), "not a module");
        Assert(Threw<ArgumentException>(() => WasmBatchFold.FromText("(module (func")), "not WebAssembly text");
        Assert(
            Threw<ArgumentException>(() =>
                WasmBatchFold.FromText(
                    """(module (import "env" "now" (func)) (memory (export "memory") 1) (func (export "buffer") (param i32) (result i32) (i32.const 0)) (func (export "fold") (param i32 i32 i32 i32 i32)))""")),
            "importing");
        Assert(Threw<ArgumentException>(() => WasmBatchFold.FromText(TopOfBookWat)), "no fold (a stale-data function)");
        Assert(
            Threw<ArgumentException>(() =>
                WasmBatchFold.FromText(
                    """(module (memory (export "memory") 1) (func (export "buffer") (param i32) (result i32) (i32.const 0)) (func (export "fold") (param i32 i32 i32 i32)))""")),
            "fold of another type");
        Assert(
            Threw<ArgumentException>(() =>
                WasmBatchFold.FromText(
                    """(module (func (export "buffer") (param i32) (result i32) (i32.const 0)) (func (export "fold") (param i32 i32 i32 i32 i32)))""")),
            "no memory");

        // Tables folding on several threads at once share it: each fold its own instance, every table the compile-time fold's rows.
        var wrong = 0;
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, t =>
        {
            var mine = new Table<Level, long>($"WASM-PAR-{t}");
            var reference = new Table<Level, long>($"REF-PAR-{t}");
            var r = new Random(t);
            var ks = new Level[32];
            var ds = new long[32];
            for (var step = 0; step < 500; step++)
            {
                for (var i = 0; i < ks.Length; i++)
                {
                    ks[i] = new Level((byte)r.Next(2), r.Next(20));
                    ds[i] = r.Next(-2, 3);
                }

                using (var u = mine.BeginUpdate())
                {
                    u.Fold(ks, ds, add);
                    u.Complete();
                }

                using (var u = reference.BeginUpdate())
                {
                    for (var i = 0; i < ks.Length; i++) u.Fold<long, Add<long>>(ks[i], ds[i]);
                    u.Complete();
                }
            }

            if (mine.Checksum != reference.Checksum || !SameRows(Canonical(mine), Canonical(reference)))
                Interlocked.Increment(ref wrong);
        });
        Assert(wrong == 0 && add.Failures == 0, $"8 threads × 500 updates: {wrong} tables wrong");

        // Steady state: an update folded by the module allocates nothing (instances pooled, its memory outside the GC heap).
        var book = new Table<Level, long>("WASM-ALLOC");

        void Update(int step, int n, IBatchFold fold)
        {
            for (var i = 0; i < n; i++)
            {
                keys[i] = new Level((byte)(i & 1), 1000 + (step + i * 7) % 32);
                deltas[i] = (step & 1) == 0 ? 2 : -1;
            }

            using var u = book.BeginUpdate();
            u.Fold(keys.AsSpan(0, n), deltas.AsSpan(0, n), fold);
            u.Complete();
        }

        for (var step = 0; step < 20_000; step++) Update(step, 16, add);
        var before = AllocationBaseline();
        for (var step = 0; step < 100_000; step++) Update(step, 16, add);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert(allocated == 0, $"{allocated} bytes allocated over 100k folded updates");

        // Against C#: the fold alone, on one batch (rows, then deltas), and whole updates (the table's own work included).
        const int rows = 32, batch = 64, runs = 20_000;
        var rowBytes = new byte[rows * 12];
        var deltaBytes = new byte[batch * 12];
        for (var i = 0; i < batch; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(deltaBytes.AsSpan(i * 12), (uint)(i % rows));
            BinaryPrimitives.WriteInt64LittleEndian(deltaBytes.AsSpan(i * 12 + 4), 1);
        }

        double Time(Action step)
        {
            var sw = Stopwatch.StartNew();
            for (var k = 0; k < runs; k++) step();
            return sw.Elapsed.TotalMicroseconds / runs;
        }

        Action wasmOne = () => add.Fold(rowBytes, 1, 8, deltaBytes.AsSpan(0, 12), 1, 8),
            csOne = () => cs.Fold(rowBytes, 1, 8, deltaBytes.AsSpan(0, 12), 1, 8);
        Action wasmBatch = () => add.Fold(rowBytes, rows, 8, deltaBytes, batch, 8),
            csBatch = () => cs.Fold(rowBytes, rows, 8, deltaBytes, batch, 8);
        var s = 0;
        Action wasmUpdate = () => Update(s++, batch, add), csUpdate = () => Update(s++, batch, cs);
        var staticUpdate = () =>
        {
            s++;
            for (var i = 0; i < batch; i++)
            {
                keys[i] = new Level((byte)(i & 1), 1000 + (s + i * 7) % 32);
                deltas[i] = (s & 1) == 0 ? 2 : -1;
            }

            using var u = book.BeginUpdate();
            for (var i = 0; i < batch; i++) u.Fold<long, Add<long>>(keys[i], deltas[i]);
            u.Complete();
        };
        Action[] all = [wasmOne, csOne, wasmBatch, csBatch, wasmUpdate, csUpdate, staticUpdate];
        for (var warm = 0; warm < 3; warm++) // all warm: the C# at its final JIT tier
        {
            foreach (var a in all) Time(a);
            Thread.Sleep(150);
        }

        var us = all.Select(Time).ToArray();
        Notes.Add(
            $"wasm fold: a call {us[0] * 1000:F0} ns (C# {us[1] * 1000:F0}); a batch of {batch} deltas over {rows} rows {us[2]:F2} µs (C# {us[3]:F2}); "
            + $"an update of {batch} deltas {us[4]:F2} µs (C# batch {us[5]:F2}, compile-time Add<long> {us[6]:F2}); 0 bytes allocated over 100k updates");
        return Task.CompletedTask;
    }

    private static void WaitIdle<TKey, TRow, TDelta>(TableIngest<TKey, TRow, TDelta> ingest)
        where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged where TDelta : unmanaged
    {
        Assert(SpinWait.SpinUntil(() => ingest.Idle, 10_000), "the router never drained the ingest");
    }

    public static Task StagedIngest()
    {
        var add = Tapline.BatchFold.Of<long, long, Add<long>>();
        var book = new Table<Level, long>("STAGED");
        var reference = new Table<Level, long>("STAGED-REF");
        var gated = new GatedFold(add);
        var ingest = book.Ingest<long>(gated);
        var registry = new OutletRegistry();
        var s = NewOutlet(registry);
        var tap = s.Tap(book, activate: true);
        var c = new RefClient();
        var scratch = new byte[1 << 20];
        Drain(s, c, scratch);

        // The router's fold held up: posts return at once, readers aren't kept waiting, and what's posted meanwhile is folded together.
        gated.Gate.Reset();
        ingest.Post([new Level(0, 100)], [5L], upstreamVersion: 1);
        using (var u = reference.BeginUpdate())
        {
            u.Fold<long, Add<long>>(new Level(0, 100), 5);
            u.Complete(1);
        }

        Assert(gated.Entered.Wait(5000), "the router took the first post");
        var rng = new Random(3);
        var keys = new Level[8];
        var deltas = new long[8];
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 200; i++)
        {
            for (var k = 0; k < keys.Length; k++)
            {
                keys[k] = new Level((byte)rng.Next(2), 100 + rng.Next(30));
                deltas[k] = rng.Next(-3, 4);
            }

            Assert(ingest.Post(keys, deltas, i + 1, i + 2).Status == PublishStatus.Delivered,
                "staged: 201 (a tap is bound)");
            using var u = reference.BeginUpdate();
            for (var k = 0; k < keys.Length; k++) u.Fold<long, Add<long>>(keys[k], deltas[k]);
            u.Complete(i + 2);
        }

        Assert(sw.ElapsedMilliseconds < 2000, $"200 posts while the fold is held: {sw.ElapsedMilliseconds} ms");
        var reading = Task.Run(() =>
        {
            book.TryGet(new Level(0, 100), out _);
            return book.Count;
        });
        Assert(reading.Wait(2000) && reading.Result == 0,
            "a reader isn't kept waiting while the fold runs (nothing applied yet)");
        Assert(Task.Run(() => Drain(s, c, scratch)).Wait(2000), "nor is a tap's frame");
        Assert(book.Version == 0 && ingest.Batches == 0, "nothing applied while the fold is held");
        gated.Gate.Set();
        WaitIdle(ingest);
        Assert(
            ingest.Batches == 2 && ingest.Applied == 201 && gated.Calls == 2 && book.Version == 2 &&
            ingest.LargestBatch == 200,
            $"the first post, then the 200 posted meanwhile in one fold call, one update: {ingest.Batches} batches, {gated.Calls} calls");
        Assert(
            book.Checksum == reference.Checksum && SameRows(Canonical(book), Canonical(reference)) &&
            book.UpstreamVersion == 201, "the rows the same updates make directly");
        Drain(s, c, scratch);
        AssertMirror(c.Tables[tap.Id], book, "staged");

        // A snapshot supersedes what's staged — and a batch the router took but hasn't applied; the next snapshot overwrites it.
        gated.Gate.Reset();
        gated.Entered.Reset();
        ingest.Post([new Level(0, 1)], [1L], 201, 202);
        Assert(gated.Entered.Wait(5000), "the router took it");
        ingest.Post([new Level(0, 2)], [1L], 202, 203);
        ingest.Post([new Level(0, 3)], [1L], 999, 1000); // a broken link: never checked, superseded
        ingest.PostSnapshot([new Level(1, 7), new Level(1, 8)], [70L, 80L], 500);
        ingest.PostSnapshot([new Level(0, 9), new Level(1, 9)], [90L, 91L], 600);
        ingest.Post([new Level(0, 9), new Level(0, 10)], [-90L, 5L], 600, 601);
        gated.Gate.Set();
        WaitIdle(ingest);
        var rows = new List<KeyValuePair<Level, long>>();
        book.CopyTo(rows);
        Assert(book.Fault is null && book.UpstreamVersion == 601 && rows.Count == 2 &&
               book.TryGet(new Level(1, 9), out var r9) && r9 == 91
               && book.TryGet(new Level(0, 10), out var r10) && r10 == 5,
            "the last snapshot, then the deltas after it (one at zero: gone)");
        Assert(ingest.Superseded == 4 && ingest.Applied == 203 && ingest.Dropped == 0,
            $"superseded: the batch taken, two staged, the first snapshot ({ingest.Superseded})");
        Drain(s, c, scratch);
        AssertMirror(c.Tables[tap.Id], book, "superseded");

        // Taking turns: at most maxDeltas an update (whole posts, at least one; a snapshot's rows count), the rest after.
        var sliced = new Table<Level, long>("STAGED-SLICED");
        var slicedIngest = sliced.Ingest<long>(add, maxDeltas: 16);
        slicedIngest.QueueToPool = false;
        var slicedRef = new Table<Level, long>("STAGED-SLICED-REF");

        void PostSliced(int count, int at)
        {
            var ks = new Level[count];
            var ds = new long[count];
            for (var k = 0; k < count; k++)
            {
                ks[k] = new Level(0, at + k);
                ds[k] = at + k + 1;
            }

            slicedIngest.Post(ks, ds);
            using var u = slicedRef.BeginUpdate();
            for (var k = 0; k < count; k++) u.Fold<long, Add<long>>(ks[k], ds[k]);
            u.Complete();
        }

        for (var p = 0; p < 10; p++) PostSliced(4, p * 4); // 40 deltas
        slicedIngest.DrainPending();
        Assert(slicedIngest.Batches == 3 && sliced.Version == 3 && sliced.Checksum == slicedRef.Checksum,
            $"16 + 16 + 8 deltas: {slicedIngest.Batches} updates");
        PostSliced(20, 100); // larger than the cap: alone
        PostSliced(4, 200);
        slicedIngest.DrainPending();
        Assert(slicedIngest.Batches == 5 && sliced.Checksum == slicedRef.Checksum,
            "a post over the cap applied alone, at least one an update");
        var snapKeys = Enumerable.Range(0, 10).Select(i => new Level(1, i)).ToArray();
        slicedIngest.PostSnapshot(snapKeys, Enumerable.Repeat(1L, 10).ToArray());
        for (var p = 0; p < 3; p++) slicedIngest.Post([new Level(1, p)], [1L]);
        for (var p = 0; p < 4; p++) slicedIngest.Post([new Level(1, 0), new Level(1, 1)], [1L, 1L]);
        slicedIngest.DrainOnce(); // 10 rows, 3 × 1, 1 × 2: 15 — the next would make 17
        Assert(slicedIngest.Batches == 6 && slicedIngest.Applied == 12 + 5,
            $"the snapshot (10 rows) and the posts up to 16 entries: {slicedIngest.Applied}");
        slicedIngest.PostSnapshot([new Level(1, 0)], [5L]); // what's left of the buffer: superseded
        slicedIngest.DrainPending();
        Assert(
            slicedIngest.Superseded == 3 && sliced.Count == 1 && sliced.TryGet(new Level(1, 0), out var five) &&
            five == 5,
            $"the rest of the buffer superseded mid-way: {slicedIngest.Superseded}");

        // Bounded: full → 507; a post larger than the capacity can never fit; a snapshot always can (it replaces what's staged).
        var small = new Table<Level, long>("STAGED-SMALL");
        var smallGate = new GatedFold(add);
        var smallIngest = small.Ingest<long>(smallGate, 480);
        smallGate.Gate.Reset();
        smallIngest.Post([new Level(0, 1)], [1L]);
        Assert(smallGate.Entered.Wait(5000), "the router took it");
        var staged = 0;
        while (smallIngest.Post([new Level(0, 1)], [1L]).Status != PublishStatus.Full) staged++;
        Assert(staged == 4 && smallIngest.Refused == 1,
            $"four 104-byte posts staged in 480 bytes, the fifth refused: {staged}");
        Assert(Threw<ArgumentException>(() => smallIngest.Post(new Level[30], new long[30])),
            "larger than the capacity: refused outright");
        Assert(smallIngest.PostSnapshot([new Level(0, 1)], [7L]).Status != PublishStatus.Full,
            "a snapshot staged over them");
        smallGate.Gate.Set();
        WaitIdle(smallIngest);
        Assert(
            small.TryGet(new Level(0, 1), out var seven) && seven == 7 && small.Count == 1 &&
            smallIngest.Superseded == 5,
            $"the snapshot, superseding the four staged and the batch taken: {seven}, {small.Count} rows, {smallIngest.Superseded} superseded");

        // The upstream chain, checked as it drains (by hand here: one batch).
        var chain = new Table<Level, long>("STAGED-CHAIN");
        var chainIngest = chain.Ingest<long>(add);
        chainIngest.QueueToPool = false;
        Level a = new(0, 1), b = new(0, 2), d = new(0, 4);
        chainIngest.Post([a], [1L], upstreamVersion: 1);
        chainIngest.Post([a], [1L], 1, 2);
        chainIngest.Post([b], [1L], 5, 6); // a gap
        chainIngest.Post([d], [1L], 6, 7);
        chainIngest.DrainPending();
        Assert(chain.Fault?.Code == FaultCode.VersionMismatch && chain.UpstreamVersion == 2 &&
               chain.TryGet(a, out var two) && two == 2 && chain.Count == 1
               && chainIngest.Applied == 2 && chainIngest.Dropped == 2 && chain.Version == 1,
            $"the posts before the broken link applied (one update), then the fault: {chain.Fault?.Code} at {chain.UpstreamVersion}, {chain.Count} rows, v{chain.Version}, {chainIngest.Applied} applied, {chainIngest.Dropped} dropped");
        Assert(Threw<StreamFaultException>(() => chainIngest.Post([a], [1L])), "deltas refused while faulted");
        chainIngest.PostSnapshot([a, d], [10L, 20L], 100);
        chainIngest.Post([d], [1L], 100, 101); // accepted: a snapshot is staged
        chainIngest.DrainPending();
        Assert(
            chain.Fault is null && chain.UpstreamVersion == 101 && chain.TryGet(d, out var d21) && d21 == 21 &&
            chain.TryGet(a, out var a10) && a10 == 10,
            "recovered by the snapshot, the deltas after it applied");

        // A batch's postcondition is its last post's checksum: the states between are never made.
        var expected = new Table<Level, long>("STAGED-EXPECTED");
        using (var u = expected.BeginUpdate())
        {
            u.Set(a, 12);
            u.Set(d, 21);
            u.Complete();
        }

        chainIngest.Post([a], [1L], 101, 102, 0xBAD);
        chainIngest.Post([a], [1L], 102, 103, expected.Checksum);
        chainIngest.DrainPending();
        Assert(chain.Fault is null && chain.Checksum == expected.Checksum && chain.UpstreamVersion == 103,
            "the last post's checksum holds; the one before it isn't checked");
        var version = chain.Version;
        chainIngest.Post([a], [1L], 103, 104, 0xBAD);
        chainIngest.DrainPending();
        Assert(
            chain.Fault?.Code == FaultCode.ChecksumMismatch && chain.Version == version &&
            chain.UpstreamVersion == 103 && chain.Checksum == expected.Checksum,
            "a wrong checksum: rolled back, faulted");

        // A fold that throws after a snapshot: the snapshot stands, then the table faults.
        var throwing = new BytesAdd { Throw = true };
        var failing = new Table<Level, long>("STAGED-FOLD");
        var failingIngest = failing.Ingest<long>(throwing);
        failingIngest.QueueToPool = false;
        failingIngest.PostSnapshot([a], [3L], 1);
        failingIngest.Post([a], [1L], 1, 2);
        failingIngest.DrainPending();
        Assert(failing.Fault?.Code == FaultCode.FoldFailed && failing.TryGet(a, out var three) && three == 3 &&
               failing.UpstreamVersion == 1
               && failingIngest.Applied == 1 && failingIngest.Dropped == 1, "the snapshot applied, then FoldFailed");

        // The ingest is the table's only writer.
        Assert(Threw<InvalidOperationException>(() =>
        {
            using var u = chain.BeginUpdate();
        }), "BeginUpdate refused");
        Assert(Threw<InvalidOperationException>(() =>
        {
            using var u = chain.BeginRecovery();
        }), "BeginRecovery refused");
        Assert(Threw<InvalidOperationException>(() =>
        {
            using var tx = Transaction.Begin(chain, expected);
        }), "a transaction refused");
        Assert(Threw<InvalidOperationException>(() => chain.Ingest<long>(add)), "a second ingest refused");

        // Steady state: posts and the router's drains allocate nothing.
        var steady = new Table<Level, long>("STAGED-ALLOC");
        var steadyIngest = steady.Ingest<long>(add);
        steadyIngest.QueueToPool = false;

        void Step(int i)
        {
            for (var p = 0; p < 4; p++)
            {
                for (var k = 0; k < keys.Length; k++)
                {
                    keys[k] = new Level((byte)(k & 1), 1000 + (i + k * 7 + p) % 40);
                    deltas[k] = ((i + p) & 1) == 0 ? 2 : -1;
                }

                steadyIngest.Post(keys, deltas);
            }

            if (i % 50 == 0) steadyIngest.PostSnapshot(keys, deltas);
            steadyIngest.DrainPending();
        }

        for (var i = 0; i < 20_000; i++) Step(i);
        var before = AllocationBaseline();
        for (var i = 20_000; i < 120_000; i++) Step(i);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert(allocated == 0, $"{allocated} bytes allocated over 100k drains of four posts");
        long postTicks = 0, drainTicks = 0;
        for (var i = 0; i < 100_000; i++)
        {
            for (var k = 0; k < keys.Length; k++)
            {
                keys[k] = new Level((byte)(k & 1), 1000 + (i + k * 7) % 40);
                deltas[k] = (i & 1) == 0 ? 2 : -1;
            }

            var t0 = Stopwatch.GetTimestamp();
            for (var p = 0; p < 4; p++) steadyIngest.Post(keys, deltas);
            var t1 = Stopwatch.GetTimestamp();
            steadyIngest.DrainPending();
            drainTicks += Stopwatch.GetTimestamp() - t1;
            postTicks += t1 - t0;
        }

        double postNs = postTicks * 1e9 / Stopwatch.Frequency / 400_000,
            drainUs = drainTicks * 1e6 / Stopwatch.Frequency / 100_000;
        using var wasmAdd = WasmBatchFold.FromText(AddFoldWat);
        var slice = new Table<Level, long>("STAGED-SLICE");
        var sliceIngest = slice.Ingest<long>(wasmAdd);
        sliceIngest.QueueToPool = false;
        long sliceTicks = 0;
        for (var round = 0; round < 300; round++)
        {
            for (var p = 0; p < sliceIngest.MaxDeltas / keys.Length; p++)
            {
                for (var k = 0; k < keys.Length; k++)
                {
                    keys[k] = new Level((byte)(k & 1), 1000 + (p * 3 + k * 7) % 40);
                    deltas[k] = (round & 1) == 0 ? 2 : -1;
                }

                sliceIngest.Post(keys, deltas);
            }

            var t0 = Stopwatch.GetTimestamp();
            sliceIngest.DrainOnce();
            if (round >= 100) sliceTicks += Stopwatch.GetTimestamp() - t0;
        }

        Assert(sliceIngest.Batches == 300 && sliceIngest.Idle, "a full slice an update");
        var sliceUs = sliceTicks * 1e6 / Stopwatch.Frequency / 200;
        Notes.Add(
            $"staged ingest: a post of 8 deltas {postNs:F0} ns; the router's drain of four (32 deltas, the C# fold) {drainUs:F2} µs; a full slice ({sliceIngest.MaxDeltas} deltas over 80 rows, the WebAssembly fold) {sliceUs:F0} µs; 0 bytes allocated over 100k drains of four posts (a snapshot every 50th)");
        return Task.CompletedTask;
    }

    /// <summary><see cref="AddFoldWat" /> burning about <paramref name="spins" /> × 6 fuel a delta first: a costly fold.</summary>
    private static string BurningAddWat(int spins)
    {
        return AddFoldWat
            .Replace("(local $d i32) (local $end i32) (local $row i32) (local $q i64)",
                "(local $d i32) (local $end i32) (local $row i32) (local $q i64) (local $spin i32)")
            .Replace("(br_if $done (i32.ge_u (local.get $d) (local.get $end)))",
                $"(br_if $done (i32.ge_u (local.get $d) (local.get $end))) (local.set $spin (i32.const {spins})) (loop $burn (local.set $spin (i32.sub (local.get $spin) (i32.const 1))) (br_if $burn (local.get $spin)))");
    }

    public static Task StagedIngestMetered()
    {
        static (Table<Level, long> Table, TableIngest<Level, long, long> Ingest, Table<Level, long> Reference) Fed(
            string name, IBatchFold fold, long? sliceFuel = null)
        {
            var table = new Table<Level, long>(name);
            var ingest = table.Ingest<long>(fold, sliceFuel: sliceFuel);
            ingest.QueueToPool = false;
            return (table, ingest, new Table<Level, long>(name + "-REF"));
        }

        static void Post(
            (Table<Level, long> Table, TableIngest<Level, long, long> Ingest, Table<Level, long> Reference) t, long seq,
            int count, long delta = 1, bool reference = true)
        {
            var ks = new Level[count];
            var ds = new long[count];
            for (var k = 0; k < count; k++)
            {
                ks[k] = new Level((byte)(k & 1), (seq * 3 + k) % 50);
                ds[k] = delta;
            }

            t.Ingest.Post(ks, ds, seq - 1, seq);
            if (!reference) return;
            using var u = t.Reference.BeginUpdate();
            for (var k = 0; k < count; k++) u.Fold<long, Add<long>>(ks[k], ds[k]);
            u.Complete(seq);
        }

        // Slices sized by fuel: the first takes what's staged and measures it; later ones take what fits the slice's fuel.
        using var burn = WasmBatchFold.FromText(BurningAddWat(200));
        var sized = Fed("METERED-SIZED", burn, 100_000);
        for (var p = 1; p <= 50; p++) Post(sized, p, 4);
        sized.Ingest.DrainPending();
        var perDelta = sized.Ingest.FuelPerDelta;
        Assert(sized.Ingest.Batches == 1 && perDelta is > 800 and < 2000,
            $"the first slice all 50 posts, measured: {perDelta:F0} fuel a delta; {sized.Ingest.Batches} batches, {sized.Ingest.Applied} applied, {sized.Ingest.Dropped} dropped, fault {sized.Table.Fault?.Code} {sized.Table.Fault?.Message}");
        for (var p = 51; p <= 100; p++) Post(sized, p, 4);
        sized.Ingest.DrainPending();
        long slices = sized.Ingest.Batches - 1, perSlice = (long)(100_000 / perDelta) / 4;
        Assert(slices >= 50 / perSlice && slices <= 50 / perSlice + 2 && sized.Ingest.Halvings == 0,
            $"then as many posts as fit 100,000 fuel (about {perSlice}): {slices} slices for 50 posts");
        Assert(
            sized.Table.Checksum == sized.Reference.Checksum && sized.Table.UpstreamVersion == 100 &&
            sized.Table.Fault is null, "the rows the same posts make directly");

        // Out of fuel: half the posts, and half again, until they fit; the rest after. Never faulted.
        using var tight = WasmBatchFold.FromText(BurningAddWat(200), new WasmLimits { Fuel = 30_000 });
        var halved = Fed("METERED-HALVED", tight);
        for (var p = 1; p <= 40; p++) Post(halved, p, 2); // ~96,000 fuel in all
        halved.Ingest.DrainPending();
        Assert(
            halved.Table.Fault is null && halved.Table.UpstreamVersion == 40 &&
            halved.Table.Checksum == halved.Reference.Checksum,
            "every post applied, the table never faulted");
        Assert(
            halved.Ingest.Halvings >= 2 && tight.Failures == halved.Ingest.Halvings && halved.Ingest.LargestBatch <= 12,
            $"{halved.Ingest.Halvings} halvings (each a call out of fuel), slices of at most {halved.Ingest.LargestBatch} posts");
        Assert(halved.Ingest.FuelPerDelta is > 800 and < 2000 && halved.Ingest.SliceFuel == 15_000,
            $"learned: {halved.Ingest.FuelPerDelta:F0} fuel a delta, slices of half the budget");

        // A post out of fuel on its own: the posts before it applied, then the table faults; the rest dropped.
        var halvings = halved.Ingest.Halvings;
        for (var p = 41; p <= 43; p++) Post(halved, p, 2);
        Post(halved, 44, 40, reference: false); // ~48,000 fuel: more than the budget alone
        Post(halved, 45, 2, reference: false);
        halved.Ingest.DrainPending();
        Assert(halved.Table.Fault is { Code: FaultCode.FoldFailed, Exception: FoldBudgetExceededException } &&
               halved.Table.UpstreamVersion == 43
               && halved.Table.Checksum == halved.Reference.Checksum && halved.Ingest.Dropped == 2,
            $"41–43 applied, then FoldFailed (out of fuel), 44 and 45 dropped: at {halved.Table.UpstreamVersion}, {halved.Ingest.Dropped} dropped");
        Assert(halved.Ingest.Halvings == halvings,
            "no halving: sized by the fuel learned, the costly post had a slice of its own");

        // A trap, likewise (a C# fold, unmetered): halved until the poisoned post is alone; the posts before it applied.
        var poison = new PoisonFold(Tapline.BatchFold.Of<long, long, Add<long>>());
        var poisoned = Fed("METERED-POISON", poison);
        for (var p = 1; p <= 10; p++) Post(poisoned, p, 3, p == 7 ? 666 : 1, p < 7);
        poisoned.Ingest.DrainPending();
        Assert(poisoned.Table.Fault is { Code: FaultCode.FoldFailed, Exception: InvalidDataException } &&
               poisoned.Table.UpstreamVersion == 6
               && poisoned.Table.Checksum == poisoned.Reference.Checksum && poisoned.Ingest.Applied == 6 &&
               poisoned.Ingest.Dropped == 4,
            $"1–6 applied, then FoldFailed, 7–10 dropped: at {poisoned.Table.UpstreamVersion}, {poisoned.Ingest.Applied} applied");
        Assert(poisoned.Ingest.FuelPerDelta == 0 && poisoned.Ingest.SliceFuel == 0, "unmetered: no fuel measured");

        // Steady state, metered: posts and the router's slices (a WebAssembly fold) allocate nothing.
        using var add = WasmBatchFold.FromText(AddFoldWat);
        var steady = Fed("METERED-ALLOC", add);
        var keys = new Level[8];
        var deltas = new long[8];

        void Step(int i)
        {
            for (var p = 0; p < 4; p++)
            {
                for (var k = 0; k < keys.Length; k++)
                {
                    keys[k] = new Level((byte)(k & 1), 1000 + (i + k * 7 + p) % 40);
                    deltas[k] = ((i + p) & 1) == 0 ? 2 : -1;
                }

                steady.Ingest.Post(keys, deltas);
            }

            steady.Ingest.DrainPending();
        }

        for (var i = 0; i < 20_000; i++) Step(i);
        var before = AllocationBaseline();
        for (var i = 20_000; i < 120_000; i++) Step(i);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert(allocated == 0, $"{allocated} bytes allocated over 100k metered drains of four posts");
        var sw = Stopwatch.StartNew();
        long fuel = 0;
        var rows = new byte[40 * 12];
        var ds = new byte[4096 * 12];
        for (var i = 0; i < 4096; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(ds.AsSpan(i * 12), (uint)(i % 40));
            BinaryPrimitives.WriteInt64LittleEndian(ds.AsSpan(i * 12 + 4), 1);
        }

        for (var i = 0; i < 2000; i++) fuel += add.FoldMetered(rows, 40, 8, ds, 4096, 8);
        var nsPerFuel = sw.Elapsed.TotalNanoseconds / fuel;
        Notes.Add(
            $"staged ingest, metered: the Add fold {steady.Ingest.FuelPerDelta:F1} fuel a delta (the costly one {perDelta:F0}); {nsPerFuel:F2} ns a unit of fuel; "
            + $"{halved.Ingest.Halvings} halvings fitting 40 posts into 30,000-fuel calls; 0 bytes allocated over 100k metered drains");
        return Task.CompletedTask;
    }

    public static Task StagedIngestConcurrent()
    {
        using var add = WasmBatchFold.FromText(AddFoldWat);
        var book = new Table<Level, long>("STAGED-CONCURRENT");
        var ingest = book.Ingest<long>(add);
        const int threads = 4, posts = 5000, levels = 64;
        long torn = 0, reads = 0, full = 0;
        using var done = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            var rows = new List<KeyValuePair<Level, long>>();
            while (!done.IsCancellationRequested)
            {
                rows.Clear();
                book.CopyTo(rows);
                long sum = 0;
                foreach (var row in rows) sum += row.Value;
                if (sum != 0) Interlocked.Increment(ref torn);
                reads++;
            }
        });
        var expected = new long[threads, levels];
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            var rng = new Random(t);
            Span<Level> keys = stackalloc Level[2];
            Span<long> deltas = stackalloc long[2];
            for (var i = 0; i < posts; i++)
            {
                int from = rng.Next(levels), to = (from + 1 + rng.Next(levels - 1)) % levels;
                long x = rng.Next(1, 100);
                keys[0] = new Level(0, from);
                keys[1] = new Level(0, to);
                deltas[0] = x;
                deltas[1] = -x; // each post sums to zero
                while (ingest.Post(keys, deltas).Status == PublishStatus.Full)
                {
                    Interlocked.Increment(ref full);
                    Thread.Yield();
                }

                expected[t, from] += x;
                expected[t, to] -= x;
            }
        });
        WaitIdle(ingest);
        done.Cancel();
        reader.Wait();
        Assert(torn == 0 && reads > 0, $"{torn} of {reads} states read didn't sum to zero");
        for (var level = 0; level < levels; level++)
        {
            long sum = 0;
            for (var t = 0; t < threads; t++) sum += expected[t, level];
            Assert(book.TryGet(new Level(0, level), out var row) ? row == sum : sum == 0,
                $"level {level}: {row}, expected {sum}");
        }

        Assert(ingest.Applied == threads * posts && add.Failures == 0, "every post applied");
        Notes.Add(
            $"staged ingest, concurrent: {threads * posts} posts from {threads} threads in {ingest.Batches} updates (a WebAssembly fold), {reads} states read, none torn");
        return Task.CompletedTask;
    }

    public static Task LockDomains()
    {
        var a = new Table<int, long>("DOM-A");
        var b = new Table<int, long>("DOM-B");
        var c = new Table<int, long>("DOM-C");
        var alone = new Table<int, long>("DOM-ALONE");
        var v = new LastValueSource();
        using (var u = alone.BeginUpdate())
        {
            u.Set(1, 1);
            u.Complete();
        }

        using (var tx = Transaction.Begin(a))
        {
            tx.Of(a).Set(1, 1);
            tx.Complete();
        }

        Assert(a.Domain is null, "a transaction of one: no domain");
        using (var tx = Transaction.Begin(a, b))
        {
            tx.Of(a).Set(2, 2);
            tx.Of(b).Set(2, 2);
            tx.Complete();
        }

        var ab = a.Domain;
        Assert(ab is not null && ReferenceEquals(b.Domain, ab) && ab.Members.Length == 2,
            "written together: one domain");
        using (var tx = Transaction.Begin(b, v))
        {
            tx.Of(b).Set(3, 3);
            tx.Of(v).Publish([1]);
            tx.Complete();
        }

        Assert(ReferenceEquals(a.Domain, v.Domain) && a.Domain!.Members.Length == 3 && !ReferenceEquals(a.Domain, ab),
            "merged with everything sharing a domain with them: a new domain (replaced, never edited)");

        var g = new IngestGroup { QueueToPool = false };
        var add = Tapline.BatchFold.Of<long, long, Add<long>>();
        var gc = g.Join(c, add);
        var gb = g.Join(b, add);
        using (var u = g.Begin())
        {
            u.Post(gc, [1], [1L]);
            u.Post(gb, [1], [1L]);
            u.Complete();
        }

        g.DrainPending();
        Assert(
            ReferenceEquals(b.Domain, c.Domain) && b.Domain!.Members.Length == 2 &&
            ReferenceEquals(a.Domain, v.Domain) && a.Domain!.Members.Length == 2,
            "b joined the group: {b, c} its domain; a and v still together");
        Assert(b.Writer == g && c.Writer == g && Threw<InvalidOperationException>(() =>
        {
            using var tx = Transaction.Begin(a, b);
        }), "a member is written by its group alone");
        g.Leave(gb);
        g.DrainPending();
        Assert(b.Domain is null && c.Domain is null && b.Writer is null && c.Writer == g,
            "b left: alone; c, a group of one: no domain");
        Assert(alone.Domain is null && alone.Writer is null,
            "a source never written with another: never a domain, never a writer");
        return Task.CompletedTask;
    }

    public static async Task RouterNeverWaits()
    {
        var registry = new OutletRegistry();
        var held = new Table<Level, long>("HELD");
        var other = new Table<Level, long>("OTHER");
        var third = new Table<Level, long>("THIRD");
        var value = new LastValueSource();
        foreach (var t in new[] { held, other, third })
            using (var u = t.BeginUpdate())
            {
                u.Set(new Level(0, 1), 1);
                u.Complete();
            }

        value.Publish([1]);
        var s = NewOutlet(registry);
        var otherTap = s.Tap(other, activate: true);
        var watch = s.Watch("held+third", WatchMember.Of(held), WatchMember.Of(third));
        watch.Activate();
        var c = new RefClient();
        var scratch = new byte[1 << 20];
        Drain(s, c, scratch);

        // A writer holds the table mid-update, as long as it likes; another holds the value inside a transaction (of one:
        // a transaction across both would make them one domain, and a watch then reads its sources together once).
        using var holdingTable = new ManualResetEventSlim();
        using var holdingValue = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var tableWriter = Task.Run(() =>
        {
            using var u = held.BeginUpdate();
            u.Set(new Level(0, 2), 2);
            holdingTable.Set();
            release.Wait();
            u.Complete();
        });
        var valueWriter = Task.Run(() =>
        {
            using var tx = Transaction.Begin(value);
            tx.Of(value).Publish([2]);
            holdingValue.Set();
            release.Wait();
            tx.Complete();
        });
        Assert(holdingTable.Wait(5000) && holdingValue.Wait(5000), "the writers hold them");
        using (var u = other.BeginUpdate())
        {
            u.Set(new Level(0, 2), 2);
            u.Complete();
        }

        using (var u = third.BeginUpdate())
        {
            u.Set(new Level(0, 2), 2);
            u.Complete();
        }

        var heldTap = s.Tap(held, activate: true); // new: needs a snapshot of the held table
        var valueTap = s.Tap(value, activate: true);
        var sw = Stopwatch.StartNew();
        var building = Task.Run(() =>
        {
            for (var i = 0; i < 20; i++)
            {
                Drain(s, c, scratch);
                Thread.Sleep(2);
            }
        });
        Assert(building.Wait(5000), "frames built while the locks are held");
        var ms = sw.ElapsedMilliseconds;
        Assert(c.Tables[otherTap.Id].Seq == other.Version, "an unrelated topic: delivered");
        var g = c.Groups[watch.Id];
        Assert(g.Tables[1].Seq == third.Version && g.Tables[0].Seq == held.Version,
            $"the watch: the free table's part delivered (the held one has nothing new yet): third {g.Tables[1].Seq}/{third.Version}, held {g.Tables[0].Seq}/{held.Version}, "
            + $"{g.Deliveries} deliveries, {g.Resets} resets, {g.Failures} failures, domains {held.DomainEpoch}/{third.DomainEpoch}");
        Assert(!c.Tables.ContainsKey(heldTap.Id) && !c.Values.ContainsKey(valueTap.Id),
            "the held table's and value's new subscriptions: retried, not waited for");

        release.Set();
        await Task.WhenAll(tableWriter, valueWriter);
        await Task.Delay(5);
        for (var i = 0; i < 5; i++)
        {
            Drain(s, c, scratch);
            await Task.Delay(2);
        }

        Assert(
            c.Tables[heldTap.Id].Seq == held.Version && c.Values[valueTap.Id].SequenceEqual(new byte[] { 2 }) &&
            c.Groups[watch.Id].Tables[0].Seq == held.Version,
            "the update done: the held table and value delivered, to the new subscriptions and the watch");
        Notes.Add(
            $"router never waits: 20 frame builds with a table and a value held mid-update took {ms} ms (each lock tried briefly, retried 1 ms later)");
    }

    public static Task GroupUnits()
    {
        var add = Tapline.BatchFold.Of<long, long, Add<long>>();
        var set = Tapline.BatchFold.Of<long, long, Replace<long>>();
        var positions = new Table<Holding, long>("G-POS");
        var cash = new Table<int, long>("G-CASH");
        var mark = new LastValueSource();
        var g = new IngestGroup { QueueToPool = false };
        var p = g.Join(positions, add);
        var c = g.Join(cash, set);
        var m = g.Join(mark);
        var h = new Holding(1, 1);

        // Units across members and a value: one transaction (staged meanwhile: one slice).
        using (var u = g.Begin())
        {
            u.Post(p, [h], [5L]);
            u.Post(c, [1], [995L]);
            u.Publish(m, [1]);
            u.Complete(1);
        }

        using (var u = g.Begin(1))
        {
            u.Post(p, [h], [5L]);
            u.Post(c, [1], [990L]);
            u.Complete(2);
        }

        using (var u = g.Begin(2))
        {
            u.Post(c, [1], [980L]);
            u.Publish(m, [3]);
            u.Complete(3);
        }

        g.DrainPending();
        Assert(positions.TryGet(h, out var held) && held == 10 && cash.TryGet(1, out var bal) && bal == 980 &&
               mark.Version == 1
               && positions.Version == 1 && cash.Version == 1 && g.Batches == 1 && g.Applied == 3 &&
               g.UpstreamVersion == 3 && positions.UpstreamVersion == 3,
            $"three units, one transaction: each member one version, the chain at 3 ({g.Batches} batches)");
        var mirror = new RefClient();
        var registry = new OutletRegistry();
        var s = NewOutlet(registry);
        var tap = s.Tap(mark, activate: true);
        Drain(s, mirror, new byte[1 << 16]);
        Assert(mirror.Values[tap.Id].SequenceEqual(new byte[] { 3 }), "the value: the last unit's");

        // A broken link: the units before it applied, then every member faults — the value too; units refused till a snapshot.
        using (var u = g.Begin(3))
        {
            u.Post(p, [h], [1L]);
            u.Complete(4);
        }

        using (var u = g.Begin(9))
        {
            u.Post(c, [1], [1L]);
            u.Complete(10);
        }

        g.DrainPending();
        Assert(positions.TryGet(h, out held) && held == 11 && positions.Fault?.Code == FaultCode.VersionMismatch &&
               cash.Fault?.Code == FaultCode.VersionMismatch
               && mark.Fault?.Code == FaultCode.VersionMismatch && g.UpstreamVersion == 4 && g.Dropped == 1,
            "the unit before the gap applied; then all three faulted");
        Assert(Threw<StreamFaultException>(() =>
        {
            using var u = g.Begin();
            u.Post(p, [h], [1L]);
            u.Complete();
        }), "units refused while faulted");
        using (var u = g.BeginSnapshot())
        {
            Assert(Threw<InvalidOperationException>(() =>
            {
                using var v = g.Begin();
            }), "one open unit per thread");
            u.Rows(p, [h, new Holding(2, 2)], [7L, 8L]);
            u.Rows(c, [1, 2], [100L, 200L]);
            u.Publish(m, [9]);
            u.Complete(100);
        }

        using (var u = g.Begin(100))
        {
            u.Post(c, [2], [250L]);
            u.Complete(101);
        } // staged after it: fine

        g.DrainPending();
        Assert(positions.Fault is null && cash.Fault is null && mark.Fault is null && positions.Count == 2 &&
               cash.TryGet(2, out bal) && bal == 250
               && g.UpstreamVersion == 101, "the snapshot recovered all three; the unit after it applied");

        // A snapshot supersedes staged units across members, never a join or a leave.
        var extra = new Table<int, long>("G-EXTRA");
        var superseded = g.Superseded;
        using (var u = g.Begin())
        {
            u.Post(c, [1], [1L]);
            u.Complete();
        }

        var x = g.Join(extra, add);
        using (var u = g.Begin())
        {
            u.Post(x, [1], [5L]);
            u.Complete();
        }

        using (var u = g.BeginSnapshot())
        {
            u.Rows(c, [1], [42L]);
            u.Complete();
        }

        g.DrainPending();
        Assert(
            g.Superseded == superseded + 2 && extra.Writer == g && x.Active && extra.Count == 0 &&
            cash.TryGet(1, out bal) && bal == 42 && cash.Count == 1,
            "two units superseded; the join kept (and applied); cash its snapshot's");

        // A poisoned unit in one member's part: halving across the group isolates it — the units before it applied, then all fault.
        var poisonGroup = new IngestGroup { QueueToPool = false };
        var pa = new Table<int, long>("G-PA");
        var pb = new Table<int, long>("G-PB");
        var ma = poisonGroup.Join(pa, new PoisonFold(add));
        var mb = poisonGroup.Join(pb, add);
        for (var i = 1; i <= 8; i++)
            using (var u = poisonGroup.Begin())
            {
                u.Post(ma, [1], [i == 6 ? 666L : 1L]);
                u.Post(mb, [1], [1L]);
                u.Complete();
            }

        poisonGroup.DrainPending();
        pa.TryGet(1, out var av);
        pb.TryGet(1, out var bv);
        Assert(av == 5 && bv == 5 && pa.Fault?.Code == FaultCode.FoldFailed && pb.Fault?.Code == FaultCode.FoldFailed
               && poisonGroup.Applied == 5 && poisonGroup.Dropped == 3 && poisonGroup.Halvings >= 2,
            $"units 1–5 applied to both tables, then both faulted: {av}, {bv}, {poisonGroup.Halvings} halvings");

        // A member's checksum is its last part's: a wrong one rolls the whole transaction back, and every member faults.
        var sumGroup = new IngestGroup { QueueToPool = false };
        var sa = new Table<int, long>("G-SA");
        var sb = new Table<int, long>("G-SB");
        var ka = sumGroup.Join(sa, add);
        var kb = sumGroup.Join(sb, add);
        using (var u = sumGroup.Begin())
        {
            u.Post(ka, [1], [1L], 0xBAD);
            u.Post(kb, [1], [1L]);
            u.Complete();
        }

        using (var u = sumGroup.Begin())
        {
            u.Post(kb, [1], [1L], 0xBAD);
            u.Complete();
        }

        sumGroup.DrainPending();
        Assert(
            sa.Fault?.Code == FaultCode.ChecksumMismatch && sb.Fault?.Code == FaultCode.ChecksumMismatch &&
            sa.Count == 0 && sb.Count == 0,
            "sa's checksum not its last (not checked); sb's last wrong: rolled back, both faulted");

        // Steady state: units of three parts staged and drained allocate nothing.
        var steady = new IngestGroup { QueueToPool = false };
        var t1 = new Table<Holding, long>("G-STEADY-1");
        var t2 = new Table<int, long>("G-STEADY-2");
        var vv = new LastValueSource();
        var s1 = steady.Join(t1, add);
        var s2 = steady.Join(t2, set);
        var s3 = steady.Join(vv);
        var keys = new Holding[4];
        var deltas = new long[4];
        var value = new byte[8];

        void Step(int i)
        {
            for (var k = 0; k < keys.Length; k++)
            {
                keys[k] = new Holding(k, (i + k) % 20);
                deltas[k] = (i & 1) == 0 ? 2 : -1;
            }

            using (var u = steady.Begin())
            {
                u.Post(s1, keys, deltas);
                u.Post(s2, [i & 3], [i]);
                u.Publish(s3, value);
                u.Complete();
            }

            if ((i & 3) == 3) steady.DrainPending();
        }

        for (var i = 0; i < 20_000; i++) Step(i);
        var before = AllocationBaseline();
        for (var i = 20_000; i < 120_000; i++) Step(i);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert(allocated == 0, $"{allocated} bytes allocated over 100k units of three parts");
        Notes.Add(
            "group ingest: 0 bytes allocated over 100k units (a table's deltas, a table's set, a value), drained four at a time");

        // Drained by hand only when the pool doesn't drain it too: two drains at once would fold into each other.
        var pooled = new IngestGroup();
        var pooledTable = new Table<long, long>("POOLED");
        var pooledMember = pooled.Join(pooledTable, Tapline.BatchFold.Of<long, long, Add<long>>());
        Assert(
            Threw<InvalidOperationException>(pooled.DrainPending) &&
            Threw<InvalidOperationException>(() => pooled.DrainOnce()),
            "a group the pool drains refuses to be drained by hand");
        using (var u = pooled.Begin())
        {
            u.Post(pooledMember, [1L], [5L]);
            u.Complete();
        }

        var waited = Stopwatch.StartNew();
        while (!(pooled.Idle && pooledTable.TryGet(1, out var five) && five == 5) &&
               waited.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(1);
        Assert(pooledTable.TryGet(1, out var applied) && applied == 5, "the pool drains it");
        return Task.CompletedTask;
    }

    public static Task GroupMembership()
    {
        var add = Tapline.BatchFold.Of<long, long, Add<long>>();
        var t = new Table<Level, long>("M-T");
        var x = new Table<Level, long>("M-X");
        var key = new Level(0, 1);

        // A table's own ingest closed, its table joined at once: the group's units wait for that leave, all the ingest's posts first.
        var own = t.Ingest<long>(add);
        own.QueueToPool = false;
        for (var i = 0; i < 5; i++) own.Post([key], [1L]);
        own.Close();
        var g = new IngestGroup { QueueToPool = false };
        var gt = g.Join(t, add);
        using (var u = g.Begin())
        {
            u.Post(gt, [key], [100L]);
            u.Complete();
        }

        g.DrainPending();
        Assert(
            !g.Idle && t.Count == 0 && t.Writer == own.Group && t.Claims is [{ Leaving: true }, var next] &&
            next.Group == g, "the group waits for the ingest to let t go");
        own.DrainPending();
        Assert(t.Writer == g && t.TryGet(key, out var v) && v == 5 && own.Applied == 5,
            "the ingest's five posts applied, then t handed over");
        g.DrainPending();
        Assert(t.TryGet(key, out v) && v == 105 && g.Idle, "then the group's unit");

        // Refused: a source another group writes (not leaving it); direct updates while a member.
        Assert(Threw<InvalidOperationException>(() => new IngestGroup().Join(t, add)),
            "joining a source another group writes");
        Assert(Threw<InvalidOperationException>(() => g.Join(t, add)), "joining it twice");
        Assert(Threw<InvalidOperationException>(() =>
        {
            using var u = t.BeginUpdate();
        }), "a direct update of a member");

        // A member joined later: named by units after its join, across the group in one transaction.
        using (var u = x.BeginUpdate())
        {
            u.Set(key, 50);
            u.Complete();
        } // written directly before

        var gx = g.Join(x, add);
        long tv = t.Version, xv = x.Version;
        using (var u = g.Begin())
        {
            u.Post(gt, [key], [1L]);
            u.Post(gx, [key], [1L]);
            u.Complete();
        }

        g.DrainPending();
        Assert(t.TryGet(key, out v) && v == 106 && x.TryGet(key, out var xvalue) && xvalue == 51 &&
               t.Version == tv + 1 && x.Version == xv + 1
               && ReferenceEquals(t.Domain, x.Domain) && t.Domain is not null,
            "one transaction across t and x; one domain");

        // Left: it can't be named; once applied, written alone again. Units staged before the leave applied first.
        using (var u = g.Begin())
        {
            u.Post(gx, [key], [1L]);
            u.Complete();
        }

        g.Leave(gx);
        Assert(!gx.IsMember && Threw<InvalidOperationException>(() =>
        {
            using var u = g.Begin();
            u.Post(gx, [key], [1L]);
            u.Complete();
        }), "a member that left can't be named");
        Assert(Threw<InvalidOperationException>(() => g.Leave(gx)), "leaving twice");
        g.DrainPending();
        Assert(x.TryGet(key, out xvalue) && xvalue == 52 && x.Writer is null && x.Domain is null && t.Domain is null,
            "the unit before the leave applied; x alone again");
        using (var u = x.BeginUpdate())
        {
            u.Set(key, 7);
            u.Complete();
        }

        Assert(x.TryGet(key, out xvalue) && xvalue == 7, "written directly again");

        // Joined, left and joined again before the router gets to any of it: the claims queue up, each in its turn.
        var y = new Table<Level, long>("M-Y");
        var other = new IngestGroup { QueueToPool = false };
        var y1 = g.Join(y, add);
        using (var u = g.Begin())
        {
            u.Post(y1, [key], [1L]);
            u.Complete();
        }

        g.Leave(y1);
        var y2 = other.Join(y, add); // another group, after g's leave
        using (var u = other.Begin())
        {
            u.Post(y2, [key], [10L]);
            u.Complete();
        }

        other.Leave(y2);
        var y3 = g.Join(y, add); // g again, after other's
        using (var u = g.Begin())
        {
            u.Post(y3, [key], [100L]);
            u.Complete();
        }

        Assert(y.Claims!.Count == 3 && Threw<InvalidOperationException>(() => other.Join(y, add)),
            "three claims queued; a fourth refused while the last isn't leaving");
        g.DrainPending(); // g's first unit and leave; its rejoin waits for other
        Assert(y.TryGet(key, out var yv) && yv == 1 && y.Writer == other && !g.Idle,
            "g's turn done; other's next; g waits");
        other.DrainPending();
        g.DrainPending();
        Assert(y.TryGet(key, out yv) && yv == 111 && y.Writer == g && y.Claims.Count == 1 && g.Idle && other.Idle,
            "each in its turn: 1, then 10, then 100");

        // A value member, joined and left likewise.
        var mark = new LastValueSource();
        var gm = g.Join(mark);
        Assert(Threw<InvalidOperationException>(() => mark.Publish([1])), "a member value: published in units only");
        using (var u = g.Begin())
        {
            u.Publish(gm, [4]);
            u.Complete();
        }

        g.Leave(gm);
        g.DrainPending();
        mark.Publish([5]);
        Assert(mark.Version == 2 && mark.Writer is null, "published in a unit, then directly once it left");
        return Task.CompletedTask;
    }

    public static async Task GroupWatchAtomicity()
    {
        var add = Tapline.BatchFold.Of<long, long, Add<long>>();
        var set = Tapline.BatchFold.Of<long, long, Replace<long>>();
        var registry = new OutletRegistry();
        var positions = new Table<Holding, long>("GW-POS", 1024);
        var cash = new Table<int, long>("GW-CASH");
        var reserve = new Table<int, long>("GW-RESERVE");
        var balances = Enumerable.Range(0, 4).ToDictionary(a => a, _ => Endowment);
        var g = new IngestGroup(64 * 1024);
        var p = g.Join(positions, add);
        var c = g.Join(cash, set);
        using (var u = g.BeginSnapshot())
        {
            u.Rows(c, [0, 1, 2, 3], [Endowment, Endowment, Endowment, Endowment]);
            u.Complete();
        }

        while (!g.Idle) await Task.Delay(1);

        var s = NewOutlet(registry, o => o.FrameBudget = 4096);
        var account = s.Watch("account:2", WatchMember.Range(positions, 2), WatchMember.Range(cash, 2));
        account.Activate();
        var all = s.Watch("all", WatchMember.Of(positions), WatchMember.Of(cash), WatchMember.Of(reserve));
        all.Activate();
        var client = new RefClient();
        int deliveries = 0, torn = 0, allDeliveries = 0, allTorn = 0;
        var total = 4 * Endowment;

        static long Sum(RefClient.TableMirror t)
        {
            return t.Rows.Values.Sum(r => BitConverter.ToInt64(r.Row));
        }

        client.OnGroup = (id, grp) =>
        {
            if (id == account.Id)
            {
                deliveries++;
                if (Wealth(grp) != Endowment) torn++;
            }
            else if (grp.Tables.TryGetValue(0, out var tp) && grp.Tables.TryGetValue(1, out var tc) &&
                     grp.Tables.TryGetValue(2, out var tr))
            {
                allDeliveries++;
                if (Sum(tp) + Sum(tc) + Sum(tr) != total) allTorn++;
            }
        };
        var stop = false;
        var consumer = Task.Run(async () =>
        {
            var scratch = new byte[1 << 20];
            while (true)
            {
                if (s.IsReady)
                {
                    client.Decode(scratch.AsSpan(0, s.Build(scratch)));
                    continue;
                }

                if (Volatile.Read(ref stop)) return;
                await s.WaitAsync();
            }
        });

        // The provider: trades (a position and its cash, one unit); now and then the reserve joins, takes or gives cash, leaves.
        var rnd = new Random(23);
        long refused = 0;
        IngestTable<int, long, long>? r = null;
        for (var i = 0; i < 20_000; i++)
        {
            if (i % 500 == 0) r = g.Join(reserve, add);
            PublishStatus status;
            if (r is not null && rnd.Next(4) == 0)
            {
                var a = 3; // (account 2's wealth stays its own: its watch's invariant)
                long amount = rnd.Next(-100, 100);
                if (balances[a] - amount < 0) amount = 0;
                balances[a] -= amount;
                using var u = g.Begin();
                u.Post(c, [a], [balances[a]]);
                u.Post(r, [0], [amount]);
                status = u.Complete();
                if (status == PublishStatus.Full) balances[a] += amount;
            }
            else
            {
                int a = rnd.Next(4), symbol = rnd.Next(8);
                long qty = rnd.Next(-50, 100);
                if (positions.TryGet(new Holding(a, symbol), out var heldQty) && heldQty + qty < 0) qty = -heldQty;
                balances[a] -= qty;
                using var u = g.Begin();
                u.Post(p, [new Holding(a, symbol)], [qty]);
                u.Post(c, [a], [balances[a]]);
                status = u.Complete();
                if (status == PublishStatus.Full) balances[a] += qty;
            }

            if (status == PublishStatus.Full)
            {
                refused++;
                await Task.Delay(1);
            }

            if (r is not null && i % 500 == 250)
            {
                g.Leave(r);
                r = null;
            }

            if ((i & 255) == 0) await Task.Yield();
        }

        if (r is not null) g.Leave(r);
        var sw = Stopwatch.StartNew();
        while (!g.Idle && sw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(5);
        while (sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (client.Groups.TryGetValue(all.Id, out var grp) && grp.Tables.TryGetValue(2, out var tr) &&
                tr.Seq == reserve.Version
                && grp.Tables[0].Seq == positions.Version && grp.Tables[1].Seq == cash.Version) break;
            await Task.Delay(5);
        }

        Volatile.Write(ref stop, true);
        s.Interrupt();
        await consumer.WaitAsync(TimeSpan.FromSeconds(10));

        var allGroup = client.Groups[all.Id];
        Assert(
            torn == 0 && allTorn == 0 && deliveries > 50 && allDeliveries > 50 &&
            client.Groups[account.Id].Failures == 0 && allGroup.Failures == 0,
            $"account: {deliveries} deliveries, {torn} torn; all: {allDeliveries}, {allTorn} torn");
        Assert(
            Sum(allGroup.Tables[0]) + Sum(allGroup.Tables[1]) + Sum(allGroup.Tables[2]) == total &&
            positions.Fault is null && reserve.Writer is null,
            "converged: everything accounted for; the reserve left");
        Notes.Add(
            $"group watched under churn: 20,000 units ({g.Batches} transactions; {refused} refused, the provider backing off), the reserve joining and leaving 40 times; "
            + $"account watch {deliveries} deliveries, all-three watch {allDeliveries}, none torn; {allGroup.Resets - 1} resyncs after a domain changed mid-read");
    }

    public static Task Rendezvous()
    {
        var registry = new OutletRegistry();
        var s = NewOutlet(registry);
        var c = new RefClient();
        var scratch = new byte[1 << 20];

        static byte[] Id(int n)
        {
            return [(byte)n, 0xC0, 0xFF, 0xEE];
        }

        int At(byte type, uint tap, Func<(byte Type, uint TapId, long Seq, byte[]? Bytes), bool> match)
        {
            return c.Applied.FindIndex(a => a.Type == type && a.TapId == tap && match(a));
        }

        int RendezvousAt(int n)
        {
            return c.Applied.FindIndex(a => a.Type == Wire.Rendezvous && a.Bytes!.AsSpan().SequenceEqual(Id(n)));
        }

        // A message topic: the change's message, then the rendezvous — in one frame, though a mailbox goes before its topic's records.
        var orders = new MessageTopic(64, 64 * 1024);
        var ordersTap = s.Tap(orders, activate: true);
        Drain(s, c, scratch);
        var placed = orders.Publish("order 1"u8);
        Assert(placed is { Status: PublishStatus.Delivered, Seq: 1 }, "the publish says what it made: message 1");
        Assert(ordersTap.TryRendezvous(Id(1), placed.Seq), "the requester's rendezvous, after it");
        c.Decode(scratch.AsSpan(0, s.Build(scratch)));
        Assert(c.LastFrameRecords.SequenceEqual([Wire.TopicMessage, Wire.Rendezvous]) &&
               c.Rendezvous.Single() is { Seq: 1, Flags: 0 } first
               && first.Correlation.AsSpan().SequenceEqual(Id(1)) && first.TapId == ordersTap.Id,
            "the message, then the rendezvous — strictly after, in the same frame");
        var second = orders.Publish("order 2"u8);
        Drain(s, c, scratch);
        ordersTap.TryRendezvous(Id(2), second.Seq);
        c.Decode(scratch.AsSpan(0, s.Build(scratch)));
        Assert(c.LastFrameRecords.SequenceEqual([Wire.Rendezvous]), "the message sent already: the rendezvous at once");

        // A table: its update's version; two updates conflated — after the state that supersedes it.
        var book = new Table<Level, long>("RV-BOOK");
        FillBook(book, 5);
        var bookTap = s.Tap(book, activate: true);
        Drain(s, c, scratch);
        long v1, v2;
        using (var u = book.BeginUpdate())
        {
            u.Set(new Level(0, 90), 1);
            v1 = u.Complete();
        }

        using (var u = book.BeginUpdate())
        {
            u.Set(new Level(0, 90), 2);
            v2 = u.Complete();
        }

        bookTap.TryRendezvous(Id(3), v1);
        Drain(s, c, scratch);
        var table = At(Wire.Table, bookTap.Id, a => a.Seq == v2);
        Assert(
            table >= 0 && RendezvousAt(3) > table &&
            !c.Applied.Any(a => a.Type == Wire.Table && a.TapId == bookTap.Id && a.Seq == v1),
            "conflated: after the state that includes it (no record of its own version)");

        // A value: its publish's version.
        var mark = new LastValueSource();
        var markTap = s.Tap(mark, activate: true);
        Drain(s, c, scratch);
        var marked = mark.Publish([7]);
        Assert(marked.Seq == 1 && markTap.TryRendezvous(Id(4), marked.Seq), "a value's version");
        c.Decode(scratch.AsSpan(0, s.Build(scratch)));
        Assert(c.LastFrameRecords.SequenceEqual([Wire.Value, Wire.Rendezvous]), "the value, then the rendezvous");

        // Out of band: after the fetch; a mailbox message waits for a record being fetched too.
        var big = new Table<Holding, long>("RV-BIG");
        var bigTap = s.Tap(big, activate: true);
        Drain(s, c, scratch);
        long vb;
        using (var u = big.BeginUpdate())
        {
            for (var i = 0; i < 3000; i++) u.Set(new Holding(i, 1), i);
            vb = u.Complete();
        }

        bigTap.TryRendezvous(Id(5), vb);
        bigTap.TryPost("note"u8);
        Drain(s, c, scratch);
        Assert(c.Pending.Count == 1 && RendezvousAt(5) < 0 && c.Mail(bigTap.Id).Count == 0 && !s.IsReady,
            "the state out of band: the rendezvous, and a message behind it, wait for its fetch");
        c.FetchPending(s, registry);
        Drain(s, c, scratch);
        var fetched = At(Wire.Table, bigTap.Id, a => a.Seq == vb);
        Assert(fetched >= 0 && RendezvousAt(5) > fetched && c.Mail(bigTap.Id).Single().AsSpan().SequenceEqual("note"u8),
            "fetched and applied: the rendezvous, then the message");

        // Its provider degraded: its state reset, held — after the recovery's snapshot. Another subscription degraded holds it too.
        var provider = Guid.NewGuid();
        long v3;
        using (var u = book.BeginUpdate())
        {
            u.Set(new Level(0, 91), 3);
            v3 = u.Complete();
        }

        s.ProviderDegraded(provider, null, Guid.CreateVersion7(), [bookTap]);
        bookTap.TryRendezvous(Id(6), v3);
        ordersTap.TryRendezvous(Id(7), new After(book, v3));
        Drain(s, c, scratch);
        Assert(RendezvousAt(6) < 0 && RendezvousAt(7) < 0 && !s.IsReady, "degraded: both held (nothing spinning)");
        s.ProviderRecovered(provider, null, Guid.CreateVersion7(), [bookTap]);
        Drain(s, c, scratch);
        var recovered = At(Wire.Table, bookTap.Id, a => a.Seq >= v3);
        Assert(recovered >= 0 && RendezvousAt(6) > recovered && RendezvousAt(7) > recovered,
            "recovered: the snapshot, then both");

        // A version not there yet: held without spinning, until it is.
        ordersTap.TryRendezvous(Id(8), new After(book, book.Version + 1));
        Drain(s, c, scratch);
        Assert(RendezvousAt(8) < 0 && !s.IsReady, "not published yet: held");
        long v4;
        using (var u = book.BeginUpdate())
        {
            u.Set(new Level(0, 92), 4);
            v4 = u.Complete();
        }

        Drain(s, c, scratch);
        Assert(RendezvousAt(8) > At(Wire.Table, bookTap.Id, a => a.Seq == v4), "published: the delta, then it");

        // Missed: a message from before it subscribed; one skipped while it was behind.
        var feed = new MessageTopic(4, 4096, OverflowPolicy.Skip);
        NewOutlet(registry).Tap(feed, activate: true); // someone else subscribed: messages kept
        var early = feed.Publish("early"u8);
        var feedTap = s.Tap(feed, activate: true);
        Drain(s, c, scratch);
        feedTap.TryRendezvous(Id(9), early.Seq);
        long last = 0, evicted = 0;
        for (var i = 0; i < 10; i++)
        {
            last = feed.Publish([(byte)i]).Seq;
            if (i == 1) evicted = last;
        }

        feedTap.TryRendezvous(Id(10), evicted);
        feedTap.TryRendezvous(Id(11), last);
        Drain(s, c, scratch);
        var byId = c.Rendezvous.ToDictionary(r => r.Correlation[0]);
        Assert((byId[9].Flags & Wire.FlagMissed) != 0, "before it subscribed: missed");
        Assert((byId[10].Flags & Wire.FlagMissed) != 0 && c.Mail(feedTap.Id, false).Count == 4,
            "skipped: missed (four kept, the rest skipped)");
        Assert(byId[11].Flags == 0 && RendezvousAt(11) > At(Wire.TopicMessage, feedTap.Id, a => a.Seq == last),
            "the last: delivered, then it");

        // Lost with the connection: both again, in order.
        var r = NewOutlet(registry);
        var owner = new object();
        r.Attach(owner);
        var rc = new RefClient();
        var rTap = r.Tap(orders, activate: true);
        DrainFrames(r, owner, rc);
        var lost = orders.Publish("order lost"u8);
        rTap.TryRendezvous(Id(12), lost.Seq);
        Assert(r.TryBuildFrame(owner, false, out _, out _), "a frame — lost");
        r.ReleaseFrame();
        r.Attach(owner = new object());
        r.Resume(rc.LastFrame, rc.HeartbeatSeq);
        DrainFrames(r, owner, rc);
        Assert(
            rc.Applied.Count >= 2 && rc.Applied[^1].Type == Wire.Rendezvous &&
            rc.Applied[^2] is { Type: Wire.TopicMessage } m && m.Seq == lost.Seq,
            "resumed: the message, then the rendezvous");

        // Through a provider's mailbox; a full mailbox refuses; misuse refused.
        var publisher = new Publisher(registry);
        var quotes = new LastValueSource();
        SubscribeRequest? request = null;
        Provide(publisher, "quotes", "quote-station", new TestProvider(quotes) { Then = q => request = q });
        var p = NewOutlet(registry, o => o.MailboxMessages = 2);
        var pc = new RefClient();
        var pTap = publisher.SubscribeAsync(p, "quotes").AsTask().GetAwaiter().GetResult()!;
        pTap.Activate();
        Drain(p, pc, scratch);
        var quoted = quotes.Publish([42]);
        Assert(request!.Mailbox.TryRendezvous(Id(13), quoted.Seq),
            "the provider's rendezvous, on the route it accepted");
        Drain(p, pc, scratch);
        Assert(
            pc.Applied.Select(a => a.Type).TakeLast(2).SequenceEqual([Wire.Value, Wire.Rendezvous]) &&
            pc.Rendezvous.Single().Correlation.AsSpan().SequenceEqual(Id(13)),
            "the quote, then the rendezvous");
        Assert(
            pTap.TryRendezvous(Id(14), quoted.Seq + 5) && pTap.TryRendezvous(Id(15), quoted.Seq + 5) &&
            !pTap.TryRendezvous(Id(16), quoted.Seq + 5),
            "a mailbox full of entries not yet sent refuses");
        Assert(Threw<ArgumentOutOfRangeException>(() => pTap.TryRendezvous(new byte[Wire.MaxCorrelation + 1], 1)) &&
               Threw<ArgumentOutOfRangeException>(() => pTap.TryRendezvous([], 1))
               && Threw<ArgumentException>(() => pTap.TryRendezvous(Id(17), default(After))),
            "a correlation of 1 to 64 bytes, after a state");
        pTap.Dispose();
        Assert(!pTap.TryRendezvous(Id(18), 1), "ended: refused");
        return Task.CompletedTask;
    }

    public static Task MessagesOutOfBand()
    {
        // A large message travels out of band — the whole record, stored once for every subscriber — and is applied once fetched.
        var registry = new OutletRegistry();
        var s = NewOutlet(registry);
        var owner = new object();
        s.Attach(owner);
        var c = new RefClient();
        var topic = new MessageTopic(8);
        var a = s.Tap(topic, initial: InitialState.None, activate: true);
        var b = s.Tap(topic, initial: InitialState.None, activate: true);
        var big = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 3)).ToArray();
        topic.Publish(big);
        DrainFrames(s, owner, c);
        Assert(
            c.Pending.Count == 2 && c.Pending.All(h => h.Inner == Wire.TopicMessage) &&
            c.Pending[0].Id == c.Pending[1].Id, "out of band, one blob for both");
        c.FetchPending(s, registry);
        Assert(
            Of(c, a).Single().Payload.AsSpan().SequenceEqual(big) &&
            Of(c, b).Single().Payload.AsSpan().SequenceEqual(big), "fetched, applied: the message");
        var history = s.DescribeTap(a.Id)!.Messages;
        Assert(history.Single() is { Kind: "out-of-band", Inner: "message" },
            "the viewer's history: a message record (out of band)");
        return Task.CompletedTask;
    }

    public static async Task CircuitBounds()
    {
        // The budget: each subscription charged for its tap and history ring at bind.
        var registry = new OutletRegistry();
        const long Budget = 64 * 1024;
        var s = NewOutlet(registry, o =>
        {
            o.MemoryBudget = Budget;
            o.MailboxBytes = 8 * 1024;
            o.MailboxMessages = 16;
        });
        var values = new LastValues<int>("bounds");
        var taps = new List<Tap>();
        OutletFullException? full = null;
        try
        {
            for (var k = 0;; k++) taps.Add(s.Tap(values[k], activate: true));
        }
        catch (OutletFullException e)
        {
            full = e;
        }

        var each = s.MemoryBytes / taps.Count;
        Assert(full is { Budget: Budget } && full.Held == s.MemoryBytes, "refused, saying what it holds");
        Assert(
            taps.Count > 10 && s.MemoryBytes <= Budget && Budget - s.MemoryBytes < each &&
            s.MemoryBytes == each * taps.Count,
            $"filled to the budget: {taps.Count} subscriptions of {each:N0} bytes, {s.MemoryBytes:N0} held");
        Assert(
            s.DescribeTap(taps[0].Id)!.Bytes == each && s.Describe() is { MemoryBudget: Budget } v &&
            v.MemoryBytes == s.MemoryBytes, "the viewer's: per subscription, and the client's against its budget");

        // A mailbox: its ring charged when its first post makes it — refused without room; with room, made.
        Assert(!taps[0].TryPost([1]), "no room for a mailbox: its first post refused");
        for (var i = 0; i < 6; i++)
        {
            taps[^1].Dispose();
            taps.RemoveAt(taps.Count - 1);
        } // ~12 KB freed: more than a mailbox (8 KB and its index)

        var before = s.MemoryBytes;
        Assert(before == each * taps.Count, "closed: released");
        Assert(taps[0].TryPost([1]) && s.MemoryBytes > before + 8 * 1024 && taps[0].TryPost([2]),
            "room made: the mailbox made, and charged");
        taps[0].Dispose();
        taps.RemoveAt(0);
        Assert(s.MemoryBytes == each * taps.Count, "closed: its tap, history and mailbox released");

        // A provider's snapshot for one subscriber: counted while it waits (admitted with its subscription), released once sent.
        var value = new LastValueSource();
        var fresh = s.Tap(value);
        var b0 = s.MemoryBytes;
        s.SendSnapshot(fresh, new byte[1000]);
        Assert(s.MemoryBytes == b0 + 1000 && s.DescribeTap(fresh.Id)!.Bytes == each + 1000,
            "a provider's snapshot, waiting: counted");
        fresh.Activate();
        Drain(s, new RefClient(), new byte[1 << 20]);
        Assert(s.MemoryBytes == b0, "sent: released");

        // Through a provider: its tap's charge reserved before step 1 — refused without asking the provider when there's no room.
        var publisher = new Publisher(registry);
        var topic = new LastValueSource();
        var provider = new TestProvider(topic);
        Provide(publisher, "bounds-svc", "bounds-station", provider);
        try
        {
            for (var k = 1000;; k++) taps.Add(s.Tap(values[k], activate: true));
        }
        catch (OutletFullException)
        {
        }

        var held = s.MemoryBytes;
        Assert(
            Threw<OutletFullException>(() =>
                publisher.SubscribeAsync(s, "bounds-svc").AsTask().GetAwaiter().GetResult()) &&
            provider.Subscribes == 0 && s.MemoryBytes == held,
            "over budget: refused before the provider is asked, nothing held");
        taps[^1].Dispose();
        taps.RemoveAt(taps.Count - 1);
        provider.Refuse = "no";
        Assert(
            await publisher.SubscribeAsync(s, "bounds-svc", "no") is null && provider.Subscribes == 1 &&
            s.MemoryBytes == held - each, "a step 1 that binds nothing: its reservation released");
        var routed = await publisher.SubscribeAsync(s, "bounds-svc");
        Assert(
            routed is { IsClosed: false } && s.MemoryBytes == held &&
            Threw<OutletFullException>(() => s.Tap(values[0])), "room: the route takes the reservation (full again)");
        routed!.Dispose();
        Assert(s.MemoryBytes == held - each, "the route closed: released");

        // Stalled: a client acknowledging nothing for the timeout while there is something for it.
        var st = NewOutlet(registry, o => o.StallTimeout = TimeSpan.FromMilliseconds(300));
        var owner = new object();
        st.Attach(owner);
        var c = new RefClient();
        var marks = new LastValues<int>("stall");
        var a = st.Tap(marks[0], activate: true);
        var b = st.Tap(marks[1], activate: true);
        var stalledRoute = await publisher.SubscribeAsync(st, "bounds-svc");
        stalledRoute!.Activate();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 700) // changing all along, every frame acknowledged
        {
            marks.Publish(0, BitConverter.GetBytes((int)sw.ElapsedMilliseconds));
            DrainFrames(st, owner, c);
            st.Heartbeat();
            Thread.Sleep(20);
        }

        Assert(!a.IsClosed && st.Stalls == 0, "acknowledging: never stalled");
        DrainFrames(st, owner, c);
        sw.Restart();
        while (sw.ElapsedMilliseconds < 700)
        {
            st.Heartbeat();
            Thread.Sleep(20);
        } // nothing for it, nothing acknowledged

        Assert(!a.IsClosed && st.Stalls == 0, "idle: not stalled");

        marks.Publish(1, [7]);
        Assert(st.TryBuildFrame(owner, false, out var sent, out _), "a frame");
        c.Decode(sent); // …never acknowledged
        marks.Publish(0, [8]); // and more waiting
        var heldThen = st.MemoryBytes;
        for (var i = 0; i < 100 && provider.Notices.Count < 1; i++) Thread.Sleep(10); // the earlier route's end, told
        var notices = provider.Notices.Count;
        sw.Restart();
        while (!a.IsClosed && sw.ElapsedMilliseconds < 3000)
        {
            st.Heartbeat();
            Thread.Sleep(20);
        }

        Assert(
            a.IsClosed && b.IsClosed && stalledRoute.IsClosed && (await a.Completion)?.Code == FaultCode.Stalled &&
            st.Stalls == 1 && sw.ElapsedMilliseconds >= 280,
            $"stalled after {sw.ElapsedMilliseconds} ms: its subscriptions ended");
        Assert(heldThen > 0 && st.MemoryBytes == 0 && st.Taps.Count == 0, "what they held, freed");
        for (var i = 0; i < 100 && provider.Notices.Count == notices; i++) Thread.Sleep(10);
        Assert(provider.Notices.Count == notices + 1, "the provider told its route ended");

        var later = st.Tap(marks[2], activate: true); // bound since: it stands
        marks.Publish(2, [9]);
        st.Attach(owner = new object()); // the client back, on a new connection
        DrainFrames(st, owner, c);
        Assert(
            c.Ended.Single() == (FaultCode.Stalled, 1u, stalledRoute.Id) && c.Values.ContainsKey(later.Id) &&
            !later.IsClosed && st.Stalls == 1,
            "told in one record — those up to its last tap id ended; the one bound since streams");
        Notes.Add(
            $"client bounds: {taps.Count + 1} subscriptions of {each:N0} bytes in a 64 KB budget; a client stalled {sw.ElapsedMilliseconds} ms after it stopped acknowledging (300 ms timeout)");
    }

    public static Task MessageHistoryConcurrent()
    {
        // One builder writing records as fast as it can; readers copying the history meanwhile, without locks: every entry
        // they keep must be whole — its size, version and first bytes from the same record.
        var registry = new OutletRegistry();
        var s = NewOutlet(registry, o => o.FlowControl = FlowControlMode.Fixed);
        var owner = new object();
        s.Attach(owner);
        var source = new LastValueSource();
        s.Tap(source, activate: true);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        var payload = new byte[64];
        var writer = Task.Run(() =>
        {
            long v = 0;
            while (!stop.IsCancellationRequested)
            {
                v++;
                BinaryPrimitives.WriteInt64LittleEndian(payload, v); // the record's version, in its first bytes…
                source.Publish(payload.AsSpan(0, 8 + (int)(v % 40))); // …and in its size
                if (s.TryBuildFrame(owner, false, out _, out var seq))
                {
                    s.ReleaseFrame();
                    s.Ack(seq, 0);
                }
            }
        });
        long read = 0, torn = 0;
        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
                foreach (var m in s.DescribeTaps().Single().Messages)
                {
                    Interlocked.Increment(ref read);
                    var preview = Convert.FromHexString(m.Preview);
                    var version = BinaryPrimitives.ReadInt64LittleEndian(preview.AsSpan(Wire.ValueHeader));
                    if (m.Bytes != Wire.ValueHeader + 8 + (int)(version % 40) || m.Version != version)
                        Interlocked.Increment(ref torn);
                }
        })).ToArray();
        Task.WaitAll([writer, .. readers]);
        Notes.Add(
            $"message history, concurrent: {read:N0} entries read by 3 readers while one builder wrote, {torn} inconsistent");
        Assert(read > 1000 && torn == 0, $"{torn} of {read} entries read torn");
        return Task.CompletedTask;
    }

    public static async Task RouterViewer()
    {
        // The router's description: services, endpoints, instances — read without the directory lock.
        var registry = new OutletRegistry();
        var publisher = new Publisher(registry)
            { Router = RouterInstance.Start("viewer-test"), ServiceUp = new ServiceUpPolicy() };
        var p = new TestProvider(new LastValueSource(), InitialState.None);
        Provide(publisher, "svc", "p", p);
        var o = NewOutlet(registry);
        var tap = (await publisher.SubscribeAsync(o, "svc"))!;
        var view = publisher.Describe();
        Assert(view.Router.Name == "viewer-test" && view.Services.Single() is { Name: "svc", Waiting: 0 } &&
               view.Endpoints.Single().Status == "Serving"
               && view.Instances.Single() is { Routes: 1, Draining: false, Dead: false } i &&
               i.Instance == p.Instance && i.NodeId == tap.Binding.NodeId,
            "the router: its service, endpoint and instance — the transient id clients see, its routes");
        var described = o.DescribeTaps().Single();
        Assert(
            described.Provider == p.Instance && described.Service == "svc" &&
            described.Route == new RouteId(o.Id, tap.Id) && described.State == "bound",
            "a subscription through a provider: its service, instance, route, state");

        // The viewer, served (on its own port): the router and its clients as JSON and HTML, a client's page, and a 404 for none.
        var opsPort = FreePort();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0", $"http://127.0.0.1:{opsPort}");
        builder.Services.AddTapline(o =>
        {
            o.Ops.Port = opsPort;
            // No heartbeats of its own: they'd check the provider's health too (every second, while it holds a route),
            // and the health checks counted here are the viewer's alone.
            o.HeartbeatInterval = TimeSpan.FromHours(1);
        });
        await using var app = builder.Build();
        app.MapTaplineViewer();
        app.MapTaplineViewer("/ops/busy", 0); // a viewer with no room: always busy
        app.MapTaplineDrain();
        await app.StartAsync();
        var outlets = app.Services.GetRequiredService<OutletRegistry>();
        var client = new Outlet(outlets, new OutletOptions());
        var owner = new object();
        client.Attach(owner);
        client.Session =
            new OutletSession("test transport", "203.0.113.9:51000", "test-agent", DateTimeOffset.UtcNow, 1);
        var values = new LastValues<int>("viewed");
        client.Tap(values[0], "mark", activate: true);
        values.Publish(0, [1, 2, 3]);
        var served = app.Services.GetRequiredService<Publisher>();
        var providerTopic = new LastValueSource { Label = "provided" };
        var provider = new TestProvider(providerTopic, InitialState.None);
        Provide(served, "svc", "viewed-p", provider);
        var routed = (await served.SubscribeAsync(client, "svc"))!;
        routed.Activate();
        providerTopic.Publish([9]);
        DrainFrames(client, owner, null);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{opsPort}") };
        using var overview = JsonDocument.Parse(await http.GetStringAsync("/ops/realtime/?format=json"));
        var counted = overview.RootElement.GetProperty("clients");
        Assert(counted.GetProperty("count").GetInt32() == 1 && counted.GetProperty("subscriptions").GetInt32() == 2 &&
               counted.GetProperty("list").GetString() == "/ops/realtime/clients"
               && overview.RootElement.GetProperty("router").GetProperty("router").GetProperty("name").GetString() is
                   { Length: > 0 },
            "JSON: the router, and how many clients and subscriptions — their list a page of its own");
        var health = overview.RootElement.GetProperty("health");
        Assert(
            health.GetProperty("state").GetString() == "Serving" &&
            health.GetProperty("correlation").GetGuid() == outlets.Health.Correlation,
            "JSON: the router's state, as its clients are told it — the change's correlation id");
        Assert((await http.GetStringAsync("/ops/realtime/")).Contains($"correlation {outlets.Health.Correlation}"),
            "HTML: the router's state, its correlation id");
        using var clientsJson = JsonDocument.Parse(await http.GetStringAsync("/ops/realtime/clients?format=json"));
        var listed = clientsJson.RootElement.GetProperty("clients").EnumerateArray().Single(e =>
            e.GetProperty("client").GetProperty("serial").GetInt64() == client.Serial);
        Assert(listed.GetProperty("client").GetProperty("session").GetProperty("remote").GetString() ==
               "203.0.113.9:51000"
               && listed.GetProperty("client").GetProperty("session").GetProperty("transport").GetString() ==
               "test transport"
               && listed.GetProperty("subscriptions").EnumerateArray().Any(t =>
                   t.GetProperty("id").GetUInt32() == routed.Id && t.GetProperty("service").GetString() == "svc"),
            "JSON: the clients — each with its session (its transport among it) and its subscriptions (topic, state, service)");
        using var detail =
            JsonDocument.Parse(await http.GetStringAsync($"/ops/realtime/clients/{client.Serial}?format=json"));
        var messages = detail.RootElement.GetProperty("taps")[0].GetProperty("messages");
        Assert(
            messages.GetArrayLength() == 1 && messages[0].GetProperty("bytes").GetInt32() == Wire.ValueHeader + 3 &&
            messages[0].GetProperty("lagMs").GetDouble() >= 0,
            "JSON: the client's subscription and its record");

        // Links everywhere: from the client to its subscription, topic, service and provider instance — and on, to the
        // instance's endpoint (checked now, on request) and the routes it holds, back to the subscription.
        var clientHtml = await http.GetStringAsync($"/ops/realtime/clients/{client.Serial}");
        string tapLink = $"/ops/realtime/clients/{client.Serial}/taps/{routed.Id}",
            instanceLink = $"/ops/realtime/instances/{provider.Instance!.Id}",
            topicLink = $"/ops/realtime/topics/{providerTopic.Id}",
            serviceLink = "/ops/realtime/services/svc";
        Assert(new[] { tapLink, instanceLink, topicLink, serviceLink }.All(l => clientHtml.Contains($"href=\"{l}\"")),
            "the client's page links its subscription, topic, service, instance");
        string tapHtml = await http.GetStringAsync(tapLink),
            instanceHtml = await http.GetStringAsync(instanceLink),
            topicHtml = await http.GetStringAsync(topicLink),
            serviceHtml = await http.GetStringAsync(serviceLink);
        var endpointLink =
            $"/ops/realtime/endpoints?address={Uri.EscapeDataString(provider.Instance.Endpoint.ToString())}";
        Assert(tapHtml.Contains(instanceLink) && tapHtml.Contains(topicLink) && tapHtml.Contains("Its last records"),
            "the subscription's page: its instance, topic, records");
        Assert(
            instanceHtml.Contains("serving") && instanceHtml.Contains(tapLink) &&
            instanceHtml.Contains(WebUtility.HtmlEncode(endpointLink)),
            "the instance's page: its state, endpoint, and the routes it holds — back to the subscription");
        Assert(
            topicHtml.Contains("provided") && topicHtml.Contains(tapLink) &&
            serviceHtml.Contains(WebUtility.HtmlEncode(endpointLink)),
            "the topic's page: its subscribers; the service's: its endpoints");
        var asked = provider.Heartbeats;
        var checkedHtml = await http.GetStringAsync(endpointLink + "&check=now");
        Assert(
            checkedHtml.Contains("Checked now") && checkedHtml.Contains("Serving") && provider.Heartbeats == asked + 1,
            "the endpoint, checked now (on request alone): its report");
        using var instanceJson = JsonDocument.Parse(await http.GetStringAsync(instanceLink + "?format=json"));
        Assert(instanceJson.RootElement.GetProperty("routes").GetArrayLength() == 1, "JSON too");
        Assert((await http.GetAsync("/ops/busy/")).StatusCode == HttpStatusCode.ServiceUnavailable,
            "busy (its requests all taken): turned away, 503 — not queued");

        // A record's page: an out-of-band one shows its blob — leased while read — and offers its bytes; one no longer in
        // the history says so.
        var big = Enumerable.Range(0, 50_000).Select(i => (byte)(i % 251)).ToArray();
        values.Publish(0, big, Delivery.Reference);
        DrainFrames(client, owner, null);
        var markTap = client.DescribeTaps().Single(t => t.Topic == "mark");
        var oob = markTap.Messages[0];
        var recordLink = $"/ops/realtime/clients/{client.Serial}/taps/{markTap.Id}/records/{oob.N}";
        var recordHtml = await http.GetStringAsync(recordLink);
        Assert(
            oob.Blob is not null && recordHtml.Contains("still kept") && recordHtml.Contains("50,000 bytes") &&
            recordHtml.Contains("00000000  00 01 02 03"),
            "an out-of-band record's page: its blob, still kept, its first bytes");
        Assert((await http.GetByteArrayAsync(recordLink + "?format=raw")).AsSpan().SequenceEqual(big),
            "…and all its bytes, raw");
        Assert(
            (await http.GetStringAsync($"/ops/realtime/clients/{client.Serial}/taps/{markTap.Id}")).Contains(
                $"href=\"{recordLink}\""), "the subscription's records link their pages");
        Assert(
            (await http.GetStringAsync($"/ops/realtime/clients/{client.Serial}/taps/{markTap.Id}/records/999"))
            .Contains("No longer in the subscription"), "a record not kept: said so");
        var routerPage = await http.GetStringAsync("/ops/realtime/");
        var page = await http.GetStringAsync("/ops/realtime/clients");
        var clientPage = await http.GetStringAsync($"/ops/realtime/clients/{client.Serial}");
        Assert(routerPage.Contains("href=\"/ops/realtime/clients\"") &&
               page.Contains($"/ops/realtime/clients/{client.Serial}\"") && page.Contains("203.0.113.9")
               && page.Contains("test transport") && clientPage.Contains("test transport")
               && clientPage.Contains("Subscription") && clientPage.Contains("test-agent"),
            "HTML: the router's page links the clients' list; it links the client, its transport shown; the client's page shows its session and subscriptions");
        Assert(page.Contains($"href=\"/ops/realtime/clients/{client.Serial}/taps/{routed.Id}\"") &&
               page.Contains($"href=\"/ops/realtime/clients/{client.Serial}/taps/{markTap.Id}\"")
               && page.Contains("href=\"/ops/realtime/services/svc\""),
            "HTML: the client's row links each of its subscriptions, and its service");
        Assert((await http.GetAsync("/ops/realtime/clients/999999")).StatusCode == HttpStatusCode.NotFound,
            "an unknown client: 404");

        // Thousands of clients a node: their list a page at a time (100), in the order they came, each page linking the
        // next; every one, streamed, on request (limit=all) — a topic's subscribers likewise.
        var crowd = new LastValueSource { Label = "crowded" };
        var many = new List<Outlet>();
        for (var k = 0; k < 250; k++)
        {
            var subscriber = new Outlet(outlets, new OutletOptions());
            subscriber.Tap(crowd, "crowded", activate: true);
            many.Add(subscriber);
        }

        var serials = new List<long>();
        var pages = 0;
        for (var next = "/ops/realtime/clients"; next is not null; pages++)
        {
            using var json =
                JsonDocument.Parse(await http.GetStringAsync(next + (next.Contains('?') ? "&" : "?") + "format=json"));
            var listedPage = json.RootElement.GetProperty("clients");
            Assert(listedPage.GetArrayLength() <= 100 && json.RootElement.GetProperty("count").GetInt32() == 251,
                $"a page of at most 100, of 251: {listedPage.GetArrayLength()}");
            serials.AddRange(listedPage.EnumerateArray()
                .Select(e => e.GetProperty("client").GetProperty("serial").GetInt64()));
            next = json.RootElement.GetProperty("next").GetString();
        }

        Assert(
            pages == 3 && serials.Count == 251 && serials.Distinct().Count() == 251 &&
            serials.SequenceEqual(serials.Order()),
            $"three pages, every client once, in the order they came: {pages}, {serials.Count}");
        using var everyOne =
            JsonDocument.Parse(await http.GetStringAsync("/ops/realtime/clients?format=json&limit=all"));
        Assert(
            everyOne.RootElement.GetProperty("clients").GetArrayLength() == 251 &&
            everyOne.RootElement.GetProperty("next").ValueKind == JsonValueKind.Null,
            "limit=all: every one, no next page");
        string firstHtml = await http.GetStringAsync("/ops/realtime/clients"),
            allHtml = await http.GetStringAsync("/ops/realtime/clients?limit=all");

        int Rows(string html)
        {
            return html.Split("<tr>").Length - 2;
        } // (less the header row)

        Assert(
            Rows(firstHtml) == 100 && firstHtml.Contains($"/ops/realtime/clients?after={serials[99]}\">next page") &&
            Rows(allHtml) == 251 && !allHtml.Contains("next page"),
            $"HTML: a page of 100 and its next page's link; all 251 on one page, streamed: {Rows(firstHtml)}, {Rows(allHtml)}");
        using var crowded =
            JsonDocument.Parse(await http.GetStringAsync($"/ops/realtime/topics/{crowd.Id}?format=json"));
        var more = crowded.RootElement.GetProperty("next").GetString();
        using var crowdedNext = JsonDocument.Parse(await http.GetStringAsync(more + "&format=json"));
        Assert(crowded.RootElement.GetProperty("subscribers").GetArrayLength() == 100 && more is not null &&
               crowdedNext.RootElement.GetProperty("subscribers").GetArrayLength() == 100
               && crowdedNext.RootElement.GetProperty("subscribers")[0].GetProperty("client").GetInt64() ==
               many[100].Serial, "a topic's 250 subscribers: a page at a time, picking up where the last left off");

        // Clients are named by serial, never reused: a link to one that has ended says so — not another's, in its slot now.
        var ended = many[0].Serial;
        var slot = many[0].Id;
        many[0].Dispose();
        var successor = new Outlet(outlets, new OutletOptions());
        var gone = await http.GetAsync($"/ops/realtime/clients/{ended}");
        Assert(
            successor.Id == slot && successor.Serial > serials.Max() && gone.StatusCode == HttpStatusCode.NotFound &&
            (await gone.Content.ReadAsStringAsync()).Contains("has ended"),
            "its slot reused by the next client, under a new number; the old link: ended, 404");
        successor.Dispose();
        foreach (var rest in many.Skip(1)) rest.Dispose();

        // Drained on request, on the ops port alone: told at once; answered once its clients have moved, or the grace ran out.
        var appPort = new Uri(app.Urls.First(u => !u.EndsWith($":{opsPort}", StringComparison.Ordinal))).Port;
        using var appHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{appPort}") };
        Assert((await appHttp.PostAsync("/ops/tapline/drain", null)).StatusCode == HttpStatusCode.NotFound,
            "the drain: not on the app's port");
        Assert((await http.PostAsync("/ops/tapline/drain?grace=soon", null)).StatusCode == HttpStatusCode.BadRequest,
            "a grace that isn't a duration: refused");

        // The viewer offers the drains, as forms (the drains mapped), on pages no other page may frame.
        var offering = await http.GetAsync("/ops/realtime/");
        var offered = await offering.Content.ReadAsStringAsync();
        Assert(offered.Contains("action=\"/ops/tapline/drain\"") && offered.Contains("name=grace value=\"10s\"") &&
               offering.Headers.GetValues("X-Frame-Options").Single() == "DENY"
               && offering.Headers.GetValues("Content-Security-Policy").Single() == "frame-ancestors 'none'",
            "the router page: its drain, a form (a grace); never framed");
        var nodeId = routed.Binding.NodeId!.Value;
        var nodes = await http.GetStringAsync("/ops/realtime/services/svc");
        Assert(
            nodes.Contains($"action=\"/ops/tapline/nodes/{nodeId}/drain\"") && nodes.Contains("name=over value=\"0s\""),
            "the service page: each endpoint's node, drained by a form (over, grace)");

        HttpRequestMessage Post(string path, string? site = null, string? origin = null, params (string, string)[] form)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = form.Length > 0
                    ? new FormUrlEncodedContent(form.Select(f => KeyValuePair.Create(f.Item1, f.Item2)))
                    : null
            };
            if (site is not null) request.Headers.Add("Sec-Fetch-Site", site);
            if (origin is not null) request.Headers.Add("Origin", origin);
            return request;
        }

        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { BaseAddress = http.BaseAddress };
        foreach (var (site, origin) in new[]
                 {
                     ("cross-site", (string?)null), ("same-site", null), (null, "https://elsewhere.example"),
                     (null, "null")
                 })
            Assert(
                (await browser.SendAsync(Post($"/ops/tapline/nodes/{nodeId}/drain", site, origin))).StatusCode ==
                HttpStatusCode.Forbidden,
                $"a page elsewhere posting a drain: refused ({site ?? origin})");
        Assert(served.Describe().Instances.All(i => !i.Draining), "…nothing drained");
        Assert(
            (await browser.SendAsync(Post($"/ops/tapline/nodes/{Guid.CreateVersion7()}/drain"))).StatusCode ==
            HttpStatusCode.NotFound, "no such node: 404");
        var nowhere = await browser.SendAsync(Post($"/ops/tapline/nodes/{nodeId}/move?over=0s&grace=1s"));
        Assert(nowhere.StatusCode == HttpStatusCode.Conflict &&
               (await nowhere.Content.ReadAsStringAsync()).Contains("nowhere"),
            $"a move with no other instance: 409, saying why ({nowhere.StatusCode})");
        Assert(
            (await browser.SendAsync(Post($"/ops/tapline/nodes/{Guid.CreateVersion7()}/move"))).StatusCode ==
            HttpStatusCode.NotFound &&
            (await browser.SendAsync(Post($"/ops/tapline/nodes/{nodeId}/move", "cross-site"))).StatusCode ==
            HttpStatusCode.Forbidden,
            "a move: no such node, 404; from a page elsewhere, refused");
        var posted = await browser.SendAsync(Post($"/ops/tapline/nodes/{nodeId}/drain", "same-origin",
            $"http://127.0.0.1:{opsPort}",
            ("back", "/ops/realtime/services/svc"), ("over", "0s"), ("grace", "0s")));
        Assert(posted.StatusCode == HttpStatusCode.SeeOther && posted.Headers.Location?.OriginalString ==
                                                            "/ops/realtime/services/svc"
                                                            && served.Describe().Instances
                                                                .Single(i => i.NodeId == nodeId).Draining,
            $"the viewer's form: the node drained, back to its page ({posted.StatusCode})");
        for (var wait = 0; wait < 100 && !routed.IsClosed; wait++) await Task.Delay(10);
        Assert(
            routed.IsClosed && (await http.GetStringAsync("/ops/realtime/services/svc")).Contains("<td>draining</td>"),
            "no grace: its subscription here let go at its turn; the page shows the node draining, its form gone");
        for (var wait = 0; wait < 100 && !provider.Notices.Any(n => n.Notice.Route.Tap == routed.Id); wait++)
            await Task.Delay(10);
        Assert(provider.Notices.Any(n => n.Notice.Route.Tap == routed.Id),
            "drained by the router, not by its provider: the provider told what it let go, as for an unsubscribe");
        var away = await browser.SendAsync(Post($"/ops/tapline/nodes/{nodeId}/drain", "same-origin", null,
            ("back", "//elsewhere.example/")));
        Assert(away.StatusCode == HttpStatusCode.OK && away.Headers.Location is null,
            "a page to go back to on another site: not gone to (answered here)");

        var drainStarted = Stopwatch.StartNew();
        using var drained = JsonDocument.Parse(await (await http.PostAsync("/ops/tapline/drain?grace=300ms", null))
            .Content.ReadAsStringAsync());
        Assert(
            outlets.Health.State == RouterState.Draining && drained.RootElement.GetProperty("correlation").GetGuid() ==
                                                         outlets.Health.Correlation
                                                         && drained.RootElement.GetProperty("clients").GetInt32() ==
                                                         1 && drained.RootElement.GetProperty("left").GetInt32() == 1 &&
                                                         drainStarted.ElapsedMilliseconds >= 300,
            $"drained: the client still subscribed didn't move — answered as the grace ran out, its id ({drained.RootElement})");
        offered = await http.GetStringAsync("/ops/realtime/");
        Assert(
            offered.Contains("its drain: the grace ran out with 1 of 1 clients still subscribed here") &&
            !offered.Contains("action=\"/ops/tapline/drain\""),
            "the router page: how its drain went, and no drain to ask for (one way)");
        client.Dispose();
        await app.StopAsync();
    }

    public static async Task ViewerRendering()
    {
        var opsPort = FreePort();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0", $"http://127.0.0.1:{opsPort}");
        builder.Services.AddTapline(o => o.Ops.Port = opsPort);
        await using var app = builder.Build();
        app.MapTaplineViewer();
        await app.StartAsync();
        var outlets = app.Services.GetRequiredService<OutletRegistry>();
        var client = new Outlet(outlets, new OutletOptions { OutOfBandThreshold = 64 });
        var owner = new object();
        client.Attach(owner);

        var book = new Table<Level, long>("rendered-book");
        using (var u = book.BeginUpdate())
        {
            u.Set(new Level(0, 6_500_000), 12);
            u.Set(new Level(1, 6_500_001), 7);
            u.Complete();
        }

        var tagged = new Table<int, Tagged>("rendered-tagged");
        var row = new Tagged { Count = 3 };
        row.Bytes[0] = 0xAB;
        using (var u = tagged.BeginUpdate())
        {
            u.Set(1, row);
            u.Complete();
        }

        var json = new LastValueSource
            { Label = "rendered-json", Format = new PayloadFormat { ContentType = "application/json" } };
        var text = new LastValueSource
            { Label = "rendered-text", Format = new PayloadFormat { ContentType = "text/plain" } };
        var opaque = new LastValueSource { Label = "rendered-opaque" };
        var prefixed = new LastValueSource
        {
            Label = "rendered-prefixed", Format = new PayloadFormat { ContentType = "application/json" },
            Retention = RecordRetention.Prefix
        };
        var alerts = new MessageTopic(8)
            { Label = "rendered-alerts", Format = new PayloadFormat { ContentType = "text/plain; charset=utf-8" } };
        // Declared, as a provider in another process would (no types): a side by name, a price in ticks of 0.01; a row layout that doesn't fit.
        var declared = new Table<Level, long>("rendered-declared")
        {
            Format = new PayloadFormat
            {
                KeyLayout = new PayloadLayout(9,
                [
                    new PayloadField("side", 0, FieldKind.UInt8)
                        { Names = new Dictionary<long, string> { [0] = "bid", [1] = "ask" } },
                    new PayloadField("price", 1, FieldKind.Int64) { Scale = 2 }
                ]),
                Layout = new PayloadLayout(9, [new PayloadField("size", 0, FieldKind.Int64)])
            }
        };
        using (var u = declared.BeginUpdate())
        {
            u.Set(new Level(1, 6_500_001), 7);
            u.Set(new Level(0, 6_499_999), 3);
            u.Set(new Level(0, 6_499_998), 5);
            u.Complete();
        } // (its snapshot out of band)

        var mark = new LastValueSource
        {
            Label = "rendered-mark",
            Format = new PayloadFormat
                { Layout = new PayloadLayout(8, [new PayloadField("", 0, FieldKind.Int64) { Scale = 2 }]) }
        };
        var many = new Table<Level, long>(
            "rendered-many"); // packed rows at every alignment (the crash a reflective read had)
        using (var u = many.BeginUpdate())
        {
            for (var i = 0; i < 1000; i++) u.Set(new Level((byte)(i & 1), 6_000_000 + i), i + 1);
            u.Complete();
        }

        client.Tap(book, "book", activate: true);
        client.Tap(declared, "declared", activate: true);
        client.Tap(mark, "mark", activate: true);
        client.Tap(many, "many", activate: true);
        client.Tap(tagged, "tagged", activate: true);
        client.Tap(json, "json", activate: true);
        client.Tap(text, "text", activate: true);
        client.Tap(opaque, "opaque", activate: true);
        client.Tap(prefixed, "prefixed", activate: true);
        client.Tap(alerts, "alerts", activate: true);
        json.Publish("{\"price\":65000.5,\"side\":\"bid\"}"u8);
        prefixed.Publish("{\"price\":65000.5,\"side\":\"bid\"}"u8);
        text.Publish("hello, router"u8);
        opaque.Publish([1, 2, 3, 250]);
        alerts.Publish("first"u8);
        alerts.Publish("second"u8);
        mark.Publish(BitConverter.GetBytes(6_500_050L));
        DrainFrames(client, owner, null);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{opsPort}") };

        // A client's identity: on its page — who, realm, module, insecure — and in the list, marked insecure.
        client.SetIdentity(new ClientIdentity("north", "alice", Guid.CreateVersion7(), "passthrough", true,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), new Dictionary<string, string> { ["role"] = "gp" }));
        var clientPage = await http.GetStringAsync($"/ops/realtime/clients/{client.Serial}");
        Assert(clientPage.Contains("identity: alice in realm north") && clientPage.Contains("module passthrough") &&
               clientPage.Contains("insecure (its module says it proves nothing)") && clientPage.Contains("claims role=gp"),
            "a client's page shows its identity, its module and insecure flag, its claims");
        var clientsPage = await http.GetStringAsync("/ops/realtime/clients");
        Assert(clientsPage.Contains("alice@north (insecure)"), "the clients list shows its user, marked insecure");
        var clientJson = await http.GetStringAsync($"/ops/realtime/clients/{client.Serial}?format=json");
        Assert(clientJson.Contains("passthrough") && clientJson.Contains("alice"),
            $"…and its JSON: {clientJson[..Math.Min(400, clientJson.Length)]}");

        async Task<JsonElement> Page(Source topic, string query = "")
        {
            using var page =
                JsonDocument.Parse(await http.GetStringAsync($"/ops/realtime/topics/{topic.Id}?format=json{query}"));
            return page.RootElement.Clone();
        }

        async Task<JsonElement> State(Source topic)
        {
            return (await Page(topic)).GetProperty("state");
        }

        var bookState = await State(book);
        Assert(bookState.GetProperty("rows").GetInt32() == 2 && bookState.GetProperty("entries").EnumerateArray()
                .Any(e =>
                    e.GetProperty("key").GetProperty("Side").GetInt32() == 1 &&
                    e.GetProperty("key").GetProperty("Price").GetInt64() == 6_500_001 &&
                    e.GetProperty("row").GetInt64() == 7),
            $"a table's rows: keys field by field (a record struct's by its properties' names), rows as numbers: {bookState}");
        var taggedRow = (await State(tagged)).GetProperty("entries")[0].GetProperty("row");
        Assert(
            taggedRow.GetProperty("Count").GetInt32() == 3 && taggedRow.GetProperty("Bytes").GetString() == "AB000000",
            $"plain fields, and an inline array as hex: {taggedRow}");
        var jsonState = (await State(json)).GetProperty("value");
        Assert(
            jsonState.GetProperty("price").GetDouble() == 65000.5 && jsonState.GetProperty("side").GetString() == "bid",
            $"application/json: as it is: {jsonState}");
        Assert((await State(text)).GetProperty("value").GetString() == "hello, router", "text/plain: a string");
        var opaqueState = (await State(opaque)).GetProperty("value");
        Assert(
            opaqueState.GetProperty("bytes").GetInt32() == 4 &&
            opaqueState.GetProperty("hex").GetString() == "010203FA", $"no content type: its bytes: {opaqueState}");
        var messages = (await State(alerts)).GetProperty("messages");
        Assert(
            messages.GetArrayLength() == 2 && messages[0].GetProperty("payload").GetString() == "second" &&
            messages[1].GetProperty("payload").GetString() == "first",
            "a message topic: its kept messages, newest first, as text");
        var topicHtml = await http.GetStringAsync($"/ops/realtime/topics/{json.Id}");
        Assert(
            topicHtml.Contains("<h2>State") && topicHtml.Contains("65000.5") && topicHtml.Contains("<h2>Format") &&
            topicHtml.Contains("application/json"),
            "HTML: the topic's format and state, rendered");

        // Declared layouts: the key's used (it fits), the row's not (9 bytes for 8-byte rows: derived instead) — and said so.
        var declaredPage = await Page(declared);
        var entry = declaredPage.GetProperty("state").GetProperty("entries").EnumerateArray()
            .Single(e => e.GetProperty("key").GetProperty("side").GetString() == "ask");
        Assert(entry.GetProperty("key").GetProperty("side").GetString() == "ask" &&
               entry.GetProperty("key").GetProperty("price").GetDecimal() == 65000.01m
               && entry.GetProperty("key").GetProperty("price").GetRawText() == "65000.01" &&
               entry.GetProperty("row").GetInt64() == 7, $"a declared key layout: by name, scaled: {entry}");
        var notes = declaredPage.GetProperty("formatNotes").EnumerateArray().Select(n => n.GetString()!).ToList();
        Assert(notes.Contains("keys: as declared") &&
               notes.Any(n => n.StartsWith("rows: the declared layout is 9 bytes, rows are 8")) &&
               notes.Contains("rows: derived from Int64")
               && declaredPage.GetProperty("format").GetProperty("keyLayout").GetProperty("fields")[1]
                   .GetProperty("scale").GetInt32() == 2,
            $"the format shown, and how each layout was chosen: {string.Join("; ", notes)}");
        Assert(
            (await Page(book)).GetProperty("formatNotes").EnumerateArray().Select(n => n.GetString())
            .SequenceEqual(["keys: derived from Level", "rows: derived from Int64"]),
            "nothing declared: derived from the types");
        Assert((await State(mark)).GetProperty("value").GetRawText() == "65000.50",
            "a value by its layout (exactly its size): a scaled price, bare");
        var manyState = (await Page(many, "&rows=all")).GetProperty("state");
        Assert(
            manyState.GetProperty("shown").GetInt32() == 1000 && manyState.GetProperty("entries").EnumerateArray()
                .All(e => e.GetProperty("row").GetInt64() ==
                          e.GetProperty("key").GetProperty("Price").GetInt64() - 5_999_999),
            "a thousand packed rows, at every alignment, read from their bytes");

        // A record rendered when it's whole: a small in-band one (its preview is all of it); an out-of-band one, its blob still kept.
        var taps = client.DescribeTaps();
        var textTap = taps.Single(t => t.Topic == "text");
        using var textRecord = JsonDocument.Parse(await http.GetStringAsync(
            $"/ops/realtime/clients/{client.Serial}/taps/{textTap.Id}/records/{textTap.Messages[0].N}?format=json"));
        Assert(textRecord.RootElement.GetProperty("rendered").GetString() == "hello, router",
            "a small in-band value: rendered whole");
        var bookTap = taps.Single(t => t.Topic == "book");
        var snapshot = bookTap.Messages.Single(m => m.Blob is not null);
        var recordLink = $"/ops/realtime/clients/{client.Serial}/taps/{bookTap.Id}/records/{snapshot.N}";
        using var bookRecord = JsonDocument.Parse(await http.GetStringAsync(recordLink + "?format=json"));
        var rendered = bookRecord.RootElement.GetProperty("rendered");
        Assert(rendered.GetProperty("snapshot").GetBoolean() && rendered.GetProperty("changes").GetArrayLength() == 2
                                                             && rendered.GetProperty("changes").EnumerateArray()
                                                                 .Any(c => c.GetProperty("key").GetProperty("Price")
                                                                               .GetInt64() == 6_500_000 &&
                                                                           c.GetProperty("row").GetInt64() == 12),
            $"an out-of-band table snapshot: its changes rendered: {rendered}");
        Assert((await http.GetStringAsync(recordLink)).Contains("<h2>Rendered"), "HTML: the record, rendered");
        var declaredTap = taps.Single(t => t.Topic == "declared");
        var declaredSnapshot = declaredTap.Messages.Single(m => m.Blob is not null);
        using var declaredRecord = JsonDocument.Parse(await http.GetStringAsync(
            $"/ops/realtime/clients/{client.Serial}/taps/{declaredTap.Id}/records/{declaredSnapshot.N}?format=json"));
        var declaredChanges = declaredRecord.RootElement.GetProperty("rendered").GetProperty("changes");
        Assert(
            declaredChanges.EnumerateArray().Any(c =>
                c.GetProperty("key").GetProperty("price").GetRawText() == "65000.01" &&
                c.GetProperty("key").GetProperty("side").GetString() == "ask"),
            $"a record's changes by the declared layout, read from the record's bytes: {declaredChanges}");
        var jsonTap = taps.Single(t => t.Topic == "json");
        using var jsonRecord = JsonDocument.Parse(await http.GetStringAsync(
            $"/ops/realtime/clients/{client.Serial}/taps/{jsonTap.Id}/records/{jsonTap.Messages[0].N}?format=json"));
        Assert(
            jsonRecord.RootElement.GetProperty("rendered").GetProperty("price").GetDouble() == 65000.5 &&
            jsonRecord.RootElement.GetProperty("record").GetProperty("whole").GetBoolean(),
            "a larger in-band record, kept whole (by default): rendered");
        var jsonRecordHtml =
            await http.GetStringAsync(
                $"/ops/realtime/clients/{client.Serial}/taps/{jsonTap.Id}/records/{jsonTap.Messages[0].N}");
        Assert(
            jsonRecordHtml.Contains("kept whole, as sent") &&
            (await http.GetByteArrayAsync(
                $"/ops/realtime/clients/{client.Serial}/taps/{jsonTap.Id}/records/{jsonTap.Messages[0].N}?format=raw"))
            .Length == jsonTap.Messages[0].Bytes,
            "its page: kept whole, its bytes to download");
        var prefixedTap = taps.Single(t => t.Topic == "prefixed");
        using var prefixedRecord = JsonDocument.Parse(await http.GetStringAsync(
            $"/ops/realtime/clients/{client.Serial}/taps/{prefixedTap.Id}/records/{prefixedTap.Messages[0].N}?format=json"));
        Assert(prefixedRecord.RootElement.GetProperty("rendered").ValueKind == JsonValueKind.Null,
            "a topic keeping prefixes: only its first bytes kept — not rendered (its topic's state is)");
        client.Dispose();
        await app.StopAsync();
    }

    public static async Task OpsPort()
    {
        // Their own port: answered there alone — not on the app's port, whatever the Host header says.
        var opsPort = FreePort();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0", $"http://127.0.0.1:{opsPort}");
        builder.Services.AddTapline(o => o.Ops.Port = opsPort);
        await using (var app = builder.Build())
        {
            app.MapTaplineViewer();
            app.MapGet("/ops/custom", () => "custom").RequireOpsPort();
            app.MapGet("/hello", () => "hello");
            await app.StartAsync();
            using var onApp = new HttpClient { BaseAddress = new Uri(app.Urls.First(u => !u.EndsWith($":{opsPort}"))) };
            using var onOps = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{opsPort}") };
            var overview = await onOps.GetAsync("/ops/realtime/");
            Assert(overview.StatusCode == HttpStatusCode.OK && await onOps.GetStringAsync("/ops/custom") == "custom",
                "on the ops port: answered");
            Assert((await onApp.GetAsync("/ops/realtime/")).StatusCode == HttpStatusCode.NotFound &&
                   (await onApp.GetAsync("/ops/custom")).StatusCode == HttpStatusCode.NotFound
                   && await onApp.GetStringAsync("/hello") == "hello",
                "on the app's port: not there (404) — the app's own endpoints are");
            Assert(
                (await onApp.PostAsync("/ops/realtime/", null)).StatusCode == HttpStatusCode.NotFound &&
                (await onOps.PostAsync("/ops/realtime/", null)).StatusCode == HttpStatusCode.MethodNotAllowed,
                "another method, on the app's port: still not there (404, not 405 — which would say it is); on the ops port, 405");
            var spoofed = new HttpRequestMessage(HttpMethod.Get, "/ops/realtime/");
            spoofed.Headers.Host = $"127.0.0.1:{opsPort}";
            Assert((await onApp.SendAsync(spoofed)).StatusCode == HttpStatusCode.NotFound,
                "a Host header naming the ops port changes nothing: the connection's port decides");
            using var plain = JsonDocument.Parse(await onOps.GetStringAsync("/ops/realtime/?format=json"));
            Assert(!(await overview.Content.ReadAsStringAsync()).Contains("class=shared") &&
                   !overview.Headers.Contains("Tapline-Ops-Shared-Port")
                   && !plain.RootElement.TryGetProperty("sharedPort", out _), "its own port: no banner, no sharedPort");
            await app.StopAsync();
        }

        // Refused at startup — the app doesn't start: no ops port, a port not listened on, the ops port alone, both, shared outside Development.
        async Task<string?> StartFails(string environment, Action<OpsOptions> configure, params string[] urls)
        {
            var b = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
            b.WebHost.UseUrls(urls);
            b.Services.AddTapline(o => configure(o.Ops));
            await using var a = b.Build();
            a.MapTaplineViewer();
            try
            {
                await a.StartAsync();
                await a.StopAsync();
                return null;
            }
            catch (InvalidOperationException e)
            {
                return e.Message;
            }
        }

        var other = FreePort();
        Assert(
            await StartFails("Production", _ => { }, "http://127.0.0.1:0") is { } none &&
            none.Contains("no port of their own"), "no ops port: refused");
        Assert(
            await StartFails("Production", o => o.Port = other, "http://127.0.0.1:0") is { } unheard &&
            unheard.Contains("doesn't listen there"), "a port not listened on: refused");
        Assert(
            await StartFails("Production", o => o.Port = other, $"http://127.0.0.1:{other}") is { } alone &&
            alone.Contains("alone"), "the ops port alone (the app sharing it): refused");
        Assert(await StartFails("Development", o =>
                   {
                       o.Port = other;
                       o.SharedPortReason = "demo";
                   }, "http://127.0.0.1:0", $"http://127.0.0.1:{FreePort()}") is { } both
               && both.Contains("not both"), "both: refused");
        Assert(
            await StartFails("Production", o => o.SharedPortReason = "non-production demo", "http://127.0.0.1:0") is
                { } outside && outside.Contains("Development only"),
            "shared outside Development: refused");
        Assert(
            await StartFails("Development", o => o.SharedPortReason = "  ", "http://127.0.0.1:0") is { } blank &&
            blank.Contains("not a blank"), "shared for no reason: refused");

        // Shared, in Development, for a reason: answered on the app's port — and every page says so, and why.
        var shared = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        shared.WebHost.UseUrls("http://127.0.0.1:0");
        shared.Services.AddTapline(o => o.Ops.SharedPortReason = "non-production demo");
        await using var sharedApp = shared.Build();
        sharedApp.MapTaplineViewer();
        await sharedApp.StartAsync();
        using var one = new HttpClient { BaseAddress = new Uri(sharedApp.Urls.First()) };
        var html = await one.GetStringAsync("/ops/realtime/clients");
        var list = await one.GetAsync("/ops/realtime/clients?format=json");
        using var listed = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        using var routerJson = JsonDocument.Parse(await one.GetStringAsync("/ops/realtime/?format=json"));
        Assert(html.Contains(
                   "<p class=shared role=note>Ops endpoints share the client-facing port: non-production demo.</p>")
               && listed.RootElement.GetProperty("sharedPort").GetString() == "non-production demo" &&
               list.Headers.GetValues("Tapline-Ops-Shared-Port").Single() == "non-production demo"
               && routerJson.RootElement.EnumerateObject().First().Name == "sharedPort" &&
               routerJson.RootElement.TryGetProperty("router", out _),
            "shared: answered on the app's port; a banner on every page; the reason first in its JSON, and in a header");
        await sharedApp.StopAsync();
    }

    public static async Task ProviderViews()
    {
        // A provider's view server: an HTTP server of its own on a port the OS allocates, a handler per route.
        var posted = new ConcurrentQueue<JsonElement>();
        var views = WebApplication.CreateSlimBuilder();
        views.WebHost.UseUrls("http://127.0.0.1:0");
        views.Logging.SetMinimumLevel(LogLevel.Warning);
        await using var viewServer = views.Build();
        viewServer.MapPost("/view", async (HttpRequest request) =>
        {
            using var envelope = await JsonDocument.ParseAsync(request.Body);
            posted.Enqueue(envelope.RootElement.Clone());
            return Results.Content(
                $"<!doctype html><p>viewed: {envelope.RootElement.GetProperty("kind").GetString()} of {envelope.RootElement.GetProperty("topic").GetProperty("label").GetString()}</p>",
                "text/html; charset=utf-8");
        });
        viewServer.MapPost("/moved", () => Results.Redirect("http://127.0.0.1:1/elsewhere"));
        viewServer.MapPost("/json", () => Results.Json(new { not = "a page" }));
        viewServer.MapPost("/big", () => Results.Text(new string('x', 1_200_000), "text/plain"));
        viewServer.MapPost("/slow", async () =>
        {
            await Task.Delay(3000);
            return Results.Text("late", "text/plain");
        });
        await viewServer.StartAsync();
        var at = new Uri(viewServer.Urls.First() + "/");

        var opsPort = FreePort();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0", $"http://127.0.0.1:{opsPort}");
        builder.Services.AddTapline(o => o.Ops.Port = opsPort);
        await using var app = builder.Build();
        app.MapTaplineViewer();
        await app.StartAsync();
        var client = new Outlet(app.Services.GetRequiredService<OutletRegistry>(),
            new OutletOptions { OutOfBandThreshold = 64 });
        var owner = new object();
        client.Attach(owner);
        var book = new Table<Level, long>("viewed-book")
            { Format = new PayloadFormat { ViewUrl = new Uri(at, "view") } };
        using (var u = book.BeginUpdate())
        {
            for (var i = 0; i < 4; i++) u.Set(new Level((byte)(i & 1), 6_500_000 + i), 10 + i);
            u.Complete();
        } // (its snapshot out of band)

        var plain = new LastValueSource { Label = "unviewed" };
        client.Tap(book, "book", activate: true);
        client.Tap(plain, "plain", activate: true);
        plain.Publish([1]);
        DrainFrames(client, owner, null);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{opsPort}") };

        // The topic's state: linked and framed; the view relayed, sandboxed; what the provider was sent.
        using var topic = JsonDocument.Parse(await http.GetStringAsync($"/ops/realtime/topics/{book.Id}?format=json"));
        var view = topic.RootElement.GetProperty("view").GetString()!;
        var topicHtml = await http.GetStringAsync($"/ops/realtime/topics/{book.Id}");
        Assert(
            view == $"/ops/realtime/topics/{book.Id}/view" &&
            topicHtml.Contains("<iframe class=view sandbox=allow-scripts") && topicHtml.Contains($"src=\"{view}\""),
            "the topic's view: linked (JSON) and framed, sandboxed (HTML)");
        var shown = await http.GetAsync(view);
        var shownHtml = await shown.Content.ReadAsStringAsync();
        Assert(shown.StatusCode == HttpStatusCode.OK && shownHtml.Contains("viewed: state of viewed-book") &&
               shown.Headers.GetValues("Content-Security-Policy").Single() == "sandbox allow-scripts"
               && shown.Headers.GetValues("X-Content-Type-Options").Single() == "nosniff",
            $"the provider's answer, relayed in an opaque origin: {shownHtml}");
        Assert(posted.TryDequeue(out var state) && state.GetProperty("kind").GetString() == "state" &&
               state.GetProperty("topic").GetProperty("label").GetString() == "viewed-book"
               && state.GetProperty("format").GetProperty("viewUrl").GetString() == new Uri(at, "view").ToString()
               && state.GetProperty("rendered").GetProperty("entries").EnumerateArray().Any(e =>
                   e.GetProperty("key").GetProperty("Price").GetInt64() == 6_500_003 &&
                   e.GetProperty("row").GetInt64() == 13),
            $"the provider was sent the state as the router renders it, with the format: {state}");

        // A whole record (out of band, still kept): its page frames its view; the provider is sent its changes.
        var tap = client.DescribeTaps().Single(t => t.Topic == "book");
        var snapshot = tap.Messages.Single(m => m.Blob is not null);
        using var record =
            JsonDocument.Parse(await http.GetStringAsync(
                $"/ops/realtime/clients/{client.Serial}/taps/{tap.Id}/records/{snapshot.N}?format=json"));
        var recordView = record.RootElement.GetProperty("view").GetString()!;
        Assert((await http.GetStringAsync(recordView)).Contains("viewed: record of viewed-book") &&
               posted.TryDequeue(out var sent) && sent.GetProperty("kind").GetString() == "record"
               && sent.GetProperty("record").GetProperty("n").GetInt64() == snapshot.N &&
               sent.GetProperty("rendered").GetProperty("changes").GetArrayLength() == 4
               && (await http.GetStringAsync(
                   $"/ops/realtime/clients/{client.Serial}/taps/{tap.Id}/records/{snapshot.N}"))
               .Contains("<iframe class=view"),
            "a record's view: its page frames it; the provider is sent the record as the router renders it");

        // A record not kept whole: its view asked of its provider all the same — by the upstream version its publish gave.
        var looked = new Table<Level, long>("looked-up")
            { Format = new PayloadFormat { ViewUrl = new Uri(at, "view"), Retention = RecordRetention.Prefix } };
        using (var u = looked.BeginUpdate())
        {
            u.Set(new Level(0, 1), 5);
            u.Complete(41);
        }

        var unnumbered = new Table<Level, long>("unnumbered")
            { Format = new PayloadFormat { ViewUrl = new Uri(at, "view"), Retention = RecordRetention.Prefix } };
        using (var u = unnumbered.BeginUpdate())
        {
            u.Set(new Level(0, 1), 5);
            u.Complete();
        }

        client.Tap(looked, "looked", activate: true);
        client.Tap(unnumbered, "unnumbered", activate: true);
        DrainFrames(client, owner, null);
        var lookedTap = client.DescribeTaps().Single(t => t.Topic == "looked");
        var part = lookedTap.Messages.Single(m => m.Kind == "table");
        using var partRecord =
            JsonDocument.Parse(await http.GetStringAsync(
                $"/ops/realtime/clients/{client.Serial}/taps/{lookedTap.Id}/records/{part.N}?format=json"));
        Assert(!part.Whole && part.Upstream == 41 &&
               partRecord.RootElement.GetProperty("view").GetString() is { } partView
               && (await http.GetStringAsync(partView)).Contains("viewed: record of looked-up") &&
               posted.TryDequeue(out var byUpstream)
               && byUpstream.GetProperty("rendered").ValueKind == JsonValueKind.Null &&
               byUpstream.GetProperty("record").GetProperty("upstream").GetInt64() == 41
               && byUpstream.GetProperty("topic").GetProperty("upstream").GetInt64() == 41,
            "a record kept only in part: its view asked of its provider all the same — \"rendered\" null, its upstream version given");
        var unnumberedTap = client.DescribeTaps().Single(t => t.Topic == "unnumbered");
        var bare = unnumberedTap.Messages.Single(m => m.Kind == "table");
        using var bareRecord = JsonDocument.Parse(await http.GetStringAsync(
            $"/ops/realtime/clients/{client.Serial}/taps/{unnumberedTap.Id}/records/{bare.N}?format=json"));
        Assert((!bareRecord.RootElement.TryGetProperty("view", out var bareView) ||
                bareView.ValueKind == JsonValueKind.Null)
               && (await http.GetAsync(
                   $"/ops/realtime/clients/{client.Serial}/taps/{unnumberedTap.Id}/records/{bare.N}/view"))
               .StatusCode == HttpStatusCode.NotFound,
            "neither kept whole nor numbered: no view to ask for (404)");

        // What isn't a view is said so, in the frame: a redirect (not followed), another content type, too large, slow, unreachable; no view URL.
        async Task<(HttpStatusCode Status, string Body)> ViewOf(string path)
        {
            book.Format = new PayloadFormat { ViewUrl = new Uri(at, path) };
            var response = await http.GetAsync(view);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        Assert(
            await ViewOf("moved") is { Status: HttpStatusCode.BadGateway } moved &&
            moved.Body.Contains("(not followed)"), "a redirect: not followed (502)");
        Assert(
            await ViewOf("json") is { Status: HttpStatusCode.BadGateway } json &&
            json.Body.Contains("not a page or an image"), "JSON: not a page (502)");
        Assert(
            await ViewOf("big") is { Status: HttpStatusCode.BadGateway } big &&
            big.Body.Contains("couldn&#39;t be had"), "over 1 MB: refused (502)");
        Assert(
            await ViewOf("slow") is { Status: HttpStatusCode.GatewayTimeout } slow && slow.Body.Contains("within 2 s"),
            "slower than 2 s: given up on (504)");
        book.Format = new PayloadFormat { ViewUrl = new Uri($"http://127.0.0.1:{FreePort()}/view") };
        Assert((await http.GetAsync(view)).StatusCode == HttpStatusCode.BadGateway, "unreachable: 502");
        Assert((await http.GetAsync($"/ops/realtime/topics/{plain.Id}/view")).StatusCode == HttpStatusCode.NotFound
               && !(await http.GetStringAsync($"/ops/realtime/topics/{plain.Id}")).Contains("<iframe"),
            "no view URL: no frame, and its view route 404");
        client.Dispose();
        await app.StopAsync();
        await viewServer.StopAsync();
    }

    public static Task UpstreamVersions()
    {
        // A table: each version marked with the provider's upstream version, as its publish gave it.
        var registry = new OutletRegistry();
        var s = NewOutlet(registry);
        var owner = new object();
        s.Attach(owner);
        var table = new Table<int, long>("upstream");

        void Update(int key, long row, long? upstream)
        {
            using var u = table.BeginUpdate();
            u.Set(key, row);
            u.Complete(upstream);
        }

        Update(1, 10, 100); // version 1
        var tap = s.Tap(table, "upstream", activate: true);
        DrainFrames(s, owner, null); // its snapshot: as of 100
        Update(2, 20, 101); // versions 2 and 3: conflated, one record (100 → 102)
        Update(3, 30, 102);
        DrainFrames(s, owner, null);
        Update(4, 40, null); // version 4: its publish gave none
        DrainFrames(s, owner, null);
        Update(1, 11, 104); // version 5
        DrainFrames(s, owner, null);
        var records = s.DescribeTap(tap.Id)!.Messages.Where(m => m.Kind == "table").Reverse().ToList();
        Assert(records.Count == 4 && records[0] is { Upstream: 100, UpstreamSince: null } &&
               records[1] is { Upstream: 102, UpstreamSince: 100 }
               && records[2] is { Upstream: null, UpstreamSince: 102 } &&
               records[3] is { Upstream: 104, UpstreamSince: null },
            $"each record: the upstream version it's as of, and the one its subscriber was at before (a snapshot: none): {string.Join(", ", records.Select(r => $"{r.UpstreamSince}→{r.Upstream}"))}");
        Assert(
            table.UpstreamAt(1) == 100 && table.UpstreamAt(2) == 101 && table.UpstreamAt(4) is null &&
            table.UpstreamAt(table.Version) == 104 && table.Describe().Upstream == 104,
            "a version's upstream, as its publish gave it (none: null); the topic's, as of its version");
        for (var i = 0; i < 70; i++) Update(5, i, 200 + i); // versions 6–75
        var head = table.Version;
        Assert(
            head == 75 && table.UpstreamAt(head) == 269 && table.UpstreamAt(head - 63) == 206 &&
            table.UpstreamAt(head - 64) is null, "its last 64 versions' kept");

        // A value, through an ingest group: the same.
        var value = new LastValueSource { Label = "upstream value" };
        var group = new IngestGroup { QueueToPool = false }; // (drained by hand, below)
        var member = group.Join(value);
        var valueTap = s.Tap(value, activate: true);
        foreach (var (bytes, upstream) in new[] { (new byte[] { 1, 2, 3 }, 7L), ([4], 8L) })
        {
            using (var unit = group.Begin())
            {
                unit.Publish(member, bytes);
                unit.Complete(upstream);
            }

            group.DrainPending();
            DrainFrames(s, owner, null);
        }

        var values = s.DescribeTap(valueTap.Id)!.Messages.Where(m => m.Kind == "value").Reverse().ToList();
        Assert(
            values is [{ Upstream: 7, UpstreamSince: null }, { Upstream: 8, UpstreamSince: 7 }] &&
            value.Describe().Upstream == 8, "a value's too, through an ingest group");

        // None given: nothing kept, nothing said.
        var plain = new LastValueSource { Label = "plain" };
        var plainTap = s.Tap(plain, activate: true);
        plain.Publish([1]);
        plain.Publish([2]);
        DrainFrames(s, owner, null);
        Assert(
            s.DescribeTap(plainTap.Id)!.Messages.All(m => m.Upstream is null && m.UpstreamSince is null) &&
            plain.UpstreamAt(plain.Version) is null && plain.Describe().Upstream is null,
            "a topic whose publishes give none: none");
        return Task.CompletedTask;
    }

    public static Task KeptRecords()
    {
        // Whole by default: each record as sent (conflated), its bytes kept while the subscription's ring holds them.
        var registry = new OutletRegistry();
        var s = NewOutlet(registry, o => o.HistoryBytes = 256);
        var owner = new object();
        s.Attach(owner);
        var value = new LastValueSource { Label = "kept" };
        var tap = s.Tap(value, activate: true);

        byte[] Payload(int i, int length)
        {
            return Enumerable.Repeat((byte)i, length).ToArray();
        }

        for (var i = 1; i <= 5; i++)
        {
            value.Publish(Payload(i, 100 - Wire.ValueHeader));
            DrainFrames(s, owner, null);
        } // records of 100 bytes

        var kept = s.DescribeTap(tap.Id)!.Messages.Where(m => m.Kind == "value").ToList();
        Assert(
            kept.Count == 5 && kept.All(m => m.Bytes == 100) && kept.Take(2).All(m => m.Whole) &&
            kept.Skip(2).All(m => !m.Whole && m.Preview.Length == 64),
            $"the newest records whole while the ring holds them (256 bytes: two of 100), older ones their first 32 bytes: {string.Join(",", kept.Select(m => m.Whole))}");
        var newest = s.ReadRecord(tap.Id, kept[0].N)!;
        Assert(
            newest.Length == 100 && newest[0] == Wire.Value &&
            newest.AsSpan(Wire.ValueHeader).ToArray().All(b => b == 5) && s.ReadRecord(tap.Id, kept[2].N) is null,
            "a whole record read back exactly as sent; an older one: not whole any more");
        value.Publish(Payload(6, 300)); // larger than the ring: its first bytes alone
        DrainFrames(s, owner, null);
        var large = s.DescribeTap(tap.Id)!.Messages[0];
        Assert(
            large.Bytes == 300 + Wire.ValueHeader && !large.Whole && large.Preview.Length == 64 &&
            s.ReadRecord(tap.Id, large.N) is null, "a record larger than the ring: its first bytes");

        // Chosen per topic, capped by its provider: Prefix, None (an out-of-band handle still kept: it is metadata).
        var prefixed = new LastValueSource { Label = "prefixed", Retention = RecordRetention.Prefix };
        var none = new LastValueSource { Label = "none", Retention = RecordRetention.None };
        var capped = new LastValueSource
        {
            Label = "capped", Retention = RecordRetention.Whole,
            Format = new PayloadFormat { Retention = RecordRetention.Prefix }
        };
        var taps = new[]
            { s.Tap(prefixed, activate: true), s.Tap(none, activate: true), s.Tap(capped, activate: true) };
        foreach (var source in new[] { prefixed, none, capped }) source.Publish(Payload(7, 60));
        DrainFrames(s, owner, null);

        MessageView Last(Tap t)
        {
            return s.DescribeTap(t.Id)!.Messages.First(m => m.Kind == "value");
        }

        Assert(
            Last(taps[0]) is { Whole: false, Preview.Length: 64 } && Last(taps[1]) is { Whole: false, Preview: "" } &&
            Last(taps[2]) is { Whole: false, Preview.Length: 64 },
            "a topic keeping prefixes; one keeping metadata alone; one its provider caps at prefixes (its own choice Whole)");
        Assert(PayloadFormat.Parse("""{"retention": "prefix"}"""u8).Retention == RecordRetention.Prefix,
            "a provider's ceiling, in the format's JSON");
        var unparsed = false;
        try
        {
            PayloadFormat.Parse("""{"retention": "most"}"""u8);
        }
        catch (FormatException)
        {
            unparsed = true;
        }

        Assert(unparsed, "an unknown retention: FormatException");

        // Rings come from a history budget of the circuit's own — never its memory budget: with no room, a subscription keeps
        // its records' first bytes; one ending gives its ring's room back.
        var tight = NewOutlet(new OutletRegistry(), o =>
        {
            o.HistoryBytes = 256;
            o.HistoryBudget = 256;
        });
        var tightOwner = new object();
        tight.Attach(tightOwner);
        var first = new LastValueSource { Label = "first" };
        var second = new LastValueSource { Label = "second" };
        var third = new LastValueSource { Label = "third" };
        var firstTap = tight.Tap(first, activate: true);
        var secondTap = tight.Tap(second, activate: true);
        var memory = tight.MemoryBytes;

        MessageView Newest(Outlet o, Tap t)
        {
            return o.DescribeTap(t.Id)!.Messages.First(m => m.Kind == "value");
        }

        first.Publish(Payload(1, 60));
        DrainFrames(tight, tightOwner, null);
        second.Publish(Payload(2, 60));
        DrainFrames(tight, tightOwner, null);
        Assert(Newest(tight, firstTap).Whole && Newest(tight, secondTap) is { Whole: false, Preview.Length: 64 } &&
               tight.HistoryHeld == 256 && tight.MemoryBytes == memory
               && tight.DescribeTap(firstTap.Id)!.Bytes - tight.DescribeTap(secondTap.Id)!.Bytes == 256,
            "the first subscription's ring takes the history budget; the second keeps first bytes; the memory budget untouched");
        firstTap.Dispose();
        var thirdTap = tight.Tap(third, activate: true);
        third.Publish(Payload(3, 60));
        DrainFrames(tight, tightOwner, null);
        Assert(tight.HistoryHeld == 256 && Newest(tight, thirdTap).Whole,
            "one ending gives its ring's room back: the next subscription keeps whole records");

        // Read while written: a reader never gets bytes the builder overwrote as it read them.
        var racing = new LastValueSource { Label = "racing" };
        var raced = s.Tap(racing, activate: true);
        int reads = 0, torn = 0;
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
                foreach (var m in s.DescribeTap(raced.Id)?.Messages ?? [])
                    if (m.Whole && s.ReadRecord(raced.Id, m.N) is { } bytes)
                    {
                        reads++;
                        var payload = bytes.AsSpan(Wire.ValueHeader);
                        if (bytes[0] != Wire.Value || payload.IndexOfAnyExcept(payload[0]) >= 0) torn++;
                    }
        });
        for (var i = 0; i < 20_000; i++)
        {
            racing.Publish(Payload(i, 90 + i % 37));
            DrainFrames(s, owner, null);
        }

        stop.Cancel();
        reader.Wait();
        Assert(torn == 0 && reads > 0, $"{reads} whole records read while written, none torn ({torn})");
        Notes.Add(
            $"record retention: {reads} whole records read back while 20,000 were written into a 256-byte ring, none torn");
        return Task.CompletedTask;
    }

    public static Task SnapshotDeltas()
    {
        // A provider posts successive snapshots — its whole answer each time, with its checksum; the router applies the
        // newest against the state it holds, and what its subscribers are sent is the difference: the cells that changed.
        var registry = new OutletRegistry();
        var s = NewOutlet(registry);
        var owner = new object();
        s.Attach(owner);
        var c = new RefClient();
        var cells = new Table<Holding, long>("cells");
        var ingest = cells.Ingest<long>(Tapline.BatchFold.Of<long, long, Replace<long>>());
        ingest.QueueToPool = false;
        var tap = s.Tap(cells, activate: true);

        void Post(Holding[] keys, long[] rows, uint? checksum = null)
        {
            ingest.PostSnapshot(keys, rows, expectedChecksum: checksum ?? Table<Holding, long>.ChecksumOf(keys, rows));
        }

        (bool Snapshot, int Entries, uint Checksum) LastRecord()
        {
            var record = s.ReadRecord(tap.Id, s.DescribeTap(tap.Id)!.Messages[0].N)!;
            return ((record[5] & Wire.FlagReset) != 0, (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(26)),
                BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(22)));
        }

        Holding[] a = [new(1, 1), new(1, 2), new(2, 1), new(2, 2)];
        Post(a, [10, 20, 30, 40]);
        ingest.DrainPending();
        DrainFrames(s, owner, c);
        Assert(
            LastRecord() is { Snapshot: true, Entries: 4 } first &&
            first.Checksum == Table<Holding, long>.ChecksumOf(a, [10, 20, 30, 40]) && cells.Checksum == first.Checksum,
            "the first: a snapshot to the subscriber, carrying the provider's checksum");

        // The next answer: one cell changed, one gone, one new, one the same — sent as three changes, not four rows.
        Holding[] b = [new(1, 1), new(1, 2), new(2, 2), new(3, 1)];
        long[] bRows = [10, 25, 40, 50];
        Post(b, bRows);
        ingest.DrainPending();
        DrainFrames(s, owner, c);
        Assert(
            LastRecord() is { Snapshot: false, Entries: 3 } delta &&
            delta.Checksum == Table<Holding, long>.ChecksumOf(b, bRows),
            $"a delta of the cells that changed ({LastRecord()})");
        AssertMirror(c.Tables[tap.Id], cells, "after a delta");
        Assert(c.Tables[tap.Id].Rows.Count == 4, "the subscriber's replica: the provider's answer");

        // Answers faster than the router applies them: the newest supersedes; one difference, from what the subscriber has.
        Post([new Holding(1, 1)], [11]);
        Post([new Holding(1, 1), new Holding(1, 2)], [12, 25]);
        ingest.DrainPending();
        DrainFrames(s, owner, c);
        Assert(
            ingest.Superseded >= 1 && LastRecord() is { Snapshot: false, Entries: 3 } &&
            cells.TryGet(new Holding(1, 1), out var v) && v == 12 && !cells.TryGet(new Holding(3, 1), out _),
            $"superseded ({ingest.Superseded}); applied as one difference: (1,1) changed, (2,2) and (3,1) gone");
        AssertMirror(c.Tables[tap.Id], cells, "after superseding");

        // A snapshot whose checksum isn't what the router holds after applying it: faulted, nothing applied; the next recovers.
        Post([new Holding(9, 9)], [99], 0xBAD);
        ingest.DrainPending();
        Assert(cells.Fault?.Code == FaultCode.ChecksumMismatch && !cells.TryGet(new Holding(9, 9), out _),
            $"a checksum mismatch faults the table, the update rolled back ({cells.Fault?.Code})");
        Post([new Holding(9, 9)], [99]);
        ingest.DrainPending();
        Assert(cells.Fault is null && cells.TryGet(new Holding(9, 9), out var nine) && nine == 99,
            "the next snapshot recovers it");
        return Task.CompletedTask;
    }

    /// <summary>
    ///     A connection's identity and services' declarations (docs/provider.md, signing in: step 1): the router refuses a
    ///     required service to a connection not signed in to its realm (unasked; Unauthenticated to the client); step 1
    ///     carries the identity — its module and insecure flag — to services of its realm alone; providers refuse
    ///     Unauthenticated or Forbidden; a lapsed identity is anonymous; another user, or a sign-out, ends what was shown
    ///     the old identity (IdentityChanged, the providers told) and leaves the rest; a renewal ends nothing; the host's
    ///     callback sets it from the transport's user.
    /// </summary>
    public static async Task Identities()
    {
        var registry = new OutletRegistry();
        var publisher = new Publisher(registry);
        var s = NewOutlet(registry, o => o.Identify = user =>
            user.Identity?.IsAuthenticated == true
                ? new ClientIdentity("north", user.Identity.Name!, Guid.CreateVersion7(), "cookies", false,
                    DateTimeOffset.UtcNow)
                : null);
        var owner = new object();
        s.Attach(owner);
        var c = new RefClient();
        var topic = new LastValueSource { Label = "feed" };
        var provider = new TestProvider(topic)
        {
            Access = service => service switch
            {
                "home" => ServiceAccess.RequiredIn("north"),
                "lobby" => ServiceAccess.In("north"),
                _ => ServiceAccess.Open
            }
        };
        Provide(publisher, "home", "feeds", provider);
        Provide(publisher, "lobby", "feeds", provider);
        Provide(publisher, "public", "feeds", provider);

        async Task<SubscriptionRefusal?> Refused(string service)
        {
            try
            {
                await publisher.SubscribeAsync(s, service);
                return null;
            }
            catch (SubscriptionRefusedException e)
            {
                return e.Refusal;
            }
        }

        ClientIdentity Alice(string realm = "north", DateTimeOffset? expires = null)
        {
            return new ClientIdentity(realm, "alice", Guid.CreateVersion7(), "passthrough", true,
                DateTimeOffset.UtcNow, expires);
        }

        // Anonymous: a required service refused by the router, the provider unasked; its client told to sign in.
        var refused = await Refused("home");
        Assert(refused is { Kind: RefusalKind.Unauthenticated } && provider.Subscribes == 0,
            $"anonymous, a required service: refused here, Unauthenticated, the provider not asked ({refused})");
        DrainFrames(s, owner, c);
        Assert(c.RefusalKinds.SequenceEqual([RefusalKind.Unauthenticated]), "its client told: Unauthenticated");

        // Anonymous, an optional service of the realm, and an open one: served, no identity shown.
        var lobby = await publisher.SubscribeAsync(s, "lobby");
        Assert(lobby is not null && provider.Last is { Identity: null, Access: { Realm: "north", Required: false } },
            "anonymous, an optional service: served, shown no identity");

        // Signed in to another realm: as anonymous to this one.
        s.SetIdentity(Alice("south"));
        refused = await Refused("home");
        Assert(refused is { Kind: RefusalKind.Unauthenticated }, "signed in to another realm: refused, Unauthenticated");

        // Signed in to the realm (passthrough: insecure): served, shown who, by which module, insecure.
        s.SetIdentity(Alice());
        var home = await publisher.SubscribeAsync(s, "home");
        Assert(home is not null && provider.Last?.Identity is
                   { UserId: "alice", Realm: "north", Module: "passthrough", Insecure: true } &&
               provider.Last.Access.Required,
            $"signed in: a required service served, shown the identity, its module and insecure flag ({provider.Last?.Identity})");
        var open = await publisher.SubscribeAsync(s, "public");
        Assert(open is not null && provider.Last?.Identity is null, "an open service: shown no identity, signed in or not");

        // Providers refuse on their own account: Forbidden, Unauthenticated (told even without a reason).
        provider.Instead = (r, _) =>
        {
            r.Forbid();
            return default;
        };
        refused = await Refused("lobby");
        Assert(refused is { Kind: RefusalKind.Forbidden, Reason: "Not allowed." }, $"forbidden ({refused})");
        provider.Instead = (r, _) =>
        {
            r.Refuse(RefusalKind.Unauthenticated, null, null);
            return default;
        };
        refused = await Refused("lobby");
        Assert(refused is { Kind: RefusalKind.Unauthenticated }, "unauthenticated, without a reason: still told");
        provider.Instead = null;
        DrainFrames(s, owner, c);
        Assert(c.RefusalKinds.Skip(2).SequenceEqual([RefusalKind.Forbidden, RefusalKind.Unauthenticated]),
            $"its client told each kind: {string.Join(", ", c.RefusalKinds)}");

        // A renewal — the same user, a later expiry — ends nothing.
        s.SetIdentity(Alice(expires: DateTimeOffset.UtcNow.AddHours(1)));
        Assert(!home!.IsClosed && !open!.IsClosed && !lobby!.IsClosed, "a renewal: every subscription carries on");

        // Another user: what was shown alice ends (IdentityChanged, the provider told); what wasn't carries on.
        var notices = provider.Notices.Count;
        s.SetIdentity(new ClientIdentity("north", "bob", Guid.CreateVersion7(), "passkey", false, DateTimeOffset.UtcNow));
        Assert(home.IsClosed && !open!.IsClosed && !lobby!.IsClosed,
            "another user: the required service's subscription ended; the open one and the one subscribed anonymously carry on");
        DrainFrames(s, owner, c);
        Assert(c.Ended.Any(e => e.Code == FaultCode.IdentityChanged && e.From == home.Id && e.To == home.Id),
            "its client told: IdentityChanged");
        for (var i = 0; i < 100 && provider.Notices.Count == notices; i++) await Task.Delay(10);
        Assert(provider.Notices.Count == notices + 1, "its provider told, as for any end");

        // Lapsed: anonymous to services.
        s.SetIdentity(new ClientIdentity("north", "bob", Guid.CreateVersion7(), "passkey", false,
            DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddMinutes(-1)));
        refused = await Refused("home");
        Assert(refused is { Kind: RefusalKind.Unauthenticated }, "lapsed: refused, Unauthenticated");

        // Signed out: what was shown it ends.
        s.SetIdentity(Alice());
        var again = await publisher.SubscribeAsync(s, "home");
        s.SetIdentity(null);
        Assert(again!.IsClosed && s.Identity is null, "signed out: what was shown the identity ended");

        // The host's callback: the transport's user, signed in — or not, leaving the identity as it is.
        s.IdentifyFrom(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "carol")], "Cookies")));
        Assert(s.Identity is { UserId: "carol", Realm: "north", Module: "cookies", Insecure: false },
            $"the host's callback: carol, by cookies ({s.Identity})");
        s.IdentifyFrom(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity()));
        Assert(s.Identity?.UserId == "carol", "a user not signed in: the callback says nothing, nothing changes");
        Assert(s.Describe().Identity?.UserId == "carol", "the viewer's description has it");
    }

    public static async Task Refusals()
    {
        // A provider refusing with a reason: the subscriber's SubscribeAsync throws it; the router raises it (its log);
        // the client is sent it (its log) — all three with the correlation id the provider got back from Refuse.
        var registry = new OutletRegistry();
        var publisher = new Publisher(registry);
        var s = NewOutlet(registry);
        var owner = new object();
        s.Attach(owner);
        var c = new RefClient();
        var topic = new LastValueSource { Label = "criteria" };
        var provider = new TestProvider(topic);
        Guid minted = default;
        provider.Instead = (r, _) =>
        {
            minted = r.Refuse($"rows and columns are the same dimension ({r.Parameters})");
            return default;
        };
        Provide(publisher, "pivot", "pivot", provider);
        var raised = new List<SubscriptionRefusal>();
        publisher.SubscriptionRefused += raised.Add;
        SubscriptionRefusal? thrown = null;
        try
        {
            await publisher.SubscribeAsync(s, "pivot", "rows=venue&columns=venue");
        }
        catch (SubscriptionRefusedException e)
        {
            thrown = e.Refusal;
        }

        Assert(thrown is
               {
                   Reason: "rows and columns are the same dimension (rows=venue&columns=venue)", Service: "pivot",
                   Parameters: "rows=venue&columns=venue"
               }
               && thrown.CorrelationId == minted && minted.Version == 7,
            $"SubscribeAsync throws the refusal: its reason, and the id the provider was given (a UUIDv7): {thrown}");
        Assert(raised.Count == 1 && raised[0] == thrown && topic.Routes == 0 && s.MemoryBytes == 0,
            "the router raised it once (its log); nothing bound, nothing charged");
        DrainFrames(s, owner, c);
        Assert(c.Refusals.Count == 1 && c.Refusals[0] == (minted, "pivot", thrown!.Reason),
            $"the client is sent it, with the same id: {string.Join("; ", c.Refusals)}");
        DrainFrames(s, owner, c);
        Assert(c.Refusals.Count == 1, "sent once (kept till its frame is acknowledged)");

        // Without a reason: as not accepting — null, nothing raised, nothing sent. Either way, no accepting after.
        var late = false;
        provider.Instead = (r, _) =>
        {
            minted = r.Refuse();
            try
            {
                r.Accept(topic, provider.Instance!);
            }
            catch (InvalidOperationException)
            {
                late = true;
            }

            return default;
        };
        Assert(
            await publisher.SubscribeAsync(s, "pivot", "x") is null && minted == Guid.Empty && late &&
            raised.Count == 1, "refused without a reason: null, no id, nothing raised; accepting after it throws");
        DrainFrames(s, owner, c);
        Assert(c.Refusals.Count == 1, "…nothing sent");

        // A long reason: its first 1,024 characters, and the record's within 1,024 bytes (cut at a character).
        provider.Instead = (r, _) =>
        {
            r.Refuse(new string('é', 2000));
            return default;
        };
        try
        {
            await publisher.SubscribeAsync(s, "pivot", "x");
        }
        catch (SubscriptionRefusedException e)
        {
            thrown = e.Refusal;
        }

        DrainFrames(s, owner, c);
        Assert(thrown!.Reason.Length == 1024 && c.Refusals.Count == 2 && c.Refusals[1].Reason == new string('é', 512),
            $"a long reason kept within bounds ({thrown.Reason.Length} characters; {c.Refusals[^1].Reason.Length} sent)");
    }

    public static async Task IngestRendezvous()
    {
        // A provider posting snapshots through an ingest can't name the version a post makes — the router makes it when
        // it applies the post — so it asks for a rendezvous after what it has posted: delivered once that's applied.
        var registry = new OutletRegistry();
        var publisher = new Publisher(registry);
        var s = NewOutlet(registry);
        var owner = new object();
        s.Attach(owner);
        var c = new RefClient();
        var cells = new Table<Holding, long>("rendezvous-cells");
        var ingest = cells.Ingest<long>(Tapline.BatchFold.Of<long, long, Replace<long>>());
        ingest.QueueToPool = false;
        SubscribeRequest? asked = null;
        var provider = new TestProvider(cells) { Then = r => asked = r };
        Provide(publisher, "cells", "cells", provider);
        var tap = (await publisher.SubscribeAsync(s, "cells"))!;
        tap.Activate();
        DrainFrames(s, owner, c);
        byte[] first = [1, 1, 1, 1], second = [2, 2, 2, 2], third = [3, 3, 3, 3];

        int Seen(byte[] id)
        {
            return c.Rendezvous.FindIndex(r => r.Correlation.AsSpan().SequenceEqual(id));
        }

        int AppliedAt(Func<(byte Type, uint TapId, long Seq, byte[]? Bytes), bool> what)
        {
            return c.Applied.FindIndex(a => what(a));
        }

        // Posted, not yet applied: the rendezvous waits; once applied, it follows the state that holds the post.
        ingest.PostSnapshot([new Holding(1, 1), new Holding(1, 2)], [10L, 20L]);
        Assert(ingest.TryRendezvous(asked!.Mailbox, first), "registered");
        DrainFrames(s, owner, c);
        Assert(Seen(first) < 0 && cells.Version == 0, "the post not applied yet: no rendezvous");
        ingest.DrainPending();
        DrainFrames(s, owner, c);
        var at = Seen(first);
        Assert(at >= 0 && c.Rendezvous[at].Seq == cells.Version && c.Tables[tap.Id].Rows.Count == 2
               && AppliedAt(a => a.Type == Wire.Rendezvous && a.Bytes.AsSpan().SequenceEqual(first)) >
               AppliedAt(a => a.Type == Wire.Table && a.TapId == tap.Id),
            $"applied: the rendezvous at the version that holds it ({c.Rendezvous.ElementAtOrDefault(at).Seq} of {cells.Version}), after the table's record");

        // Nothing waiting: at once.
        Assert(ingest.TryRendezvous(asked.Mailbox, second), "nothing staged: resolved at once");
        DrainFrames(s, owner, c);
        Assert(Seen(second) >= 0 && c.Rendezvous[Seen(second)].Seq == cells.Version, "…delivered without a drain");

        // Superseded: two snapshots, the rendezvous after both — the router applies the second alone, and it follows that.
        ingest.PostSnapshot([new Holding(2, 1)], [5L]);
        ingest.PostSnapshot([new Holding(3, 1)], [7L]);
        ingest.TryRendezvous(asked.Mailbox, third);
        ingest.DrainPending();
        DrainFrames(s, owner, c);
        Assert(
            Seen(third) >= 0 && c.Rendezvous[Seen(third)].Seq == cells.Version && c.Tables[tap.Id].Rows.Count == 1 &&
            ingest.Superseded >= 1,
            "after a superseded snapshot: the rendezvous follows the one applied");
        AssertMirror(c.Tables[tap.Id], cells, "ingest rendezvous");
    }

    public static async Task PayloadFormats()
    {
        string Json(Action<Utf8JsonWriter> write)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var w = new Utf8JsonWriter(buffer,
                       new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                write(w);
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }

        string Fields(PayloadLayout l)
        {
            return string.Join(" ", l.Fields.Select(f => $"{f.Name}:{f.Kind}@{f.Offset}"));
        }

        // Derived with generated code: offsets as the runtime laid the struct out — packed, padded — named as declared.
        var level = PayloadLayout.Of<Level>();
        Assert(level.Size == 9 && Fields(level) == "Side:UInt8@0 Price:Int64@1", $"packed: {Fields(level)}");
        var padded = PayloadLayout.Of<Padded>();
        Assert(padded.Size == 24 && Fields(padded) == "A:UInt8@0 B:Int64@8 C:Bool@16 D:Char@18",
            $"padded, as laid out: {Fields(padded)}");
        var tagged = PayloadLayout.Of<Tagged>();
        Assert(Fields(tagged) == "Count:UInt16@0 Bytes:Bytes@2" && tagged.Fields[1].Length == 4,
            $"an inline array as bytes: {Fields(tagged)}");
        var order = PayloadLayout.Of<Order>();
        var id = Guid.Parse("0198d6a2-7c41-7e3b-9f10-2b4c6d8e0a13");
        var o = new Order { Side = Side.Ask, At = new Level(1, 42), Id = id };
        Assert(order.Size == Unsafe.SizeOf<Order>() &&
               Json(w => order.Render(w, MemoryMarshal.AsBytes(new ReadOnlySpan<Order>(in o))))
               == $$"""{"Side":"Ask","At":{"Side":1,"Price":42},"Id":"{{id}}"}""",
            $"an enum by name, a nested struct, a Guid: {Json(w => order.Render(w, MemoryMarshal.AsBytes(new ReadOnlySpan<Order>(in o))))}");
        Assert(Json(w => PayloadLayout.Of<long>().Render(w, BitConverter.GetBytes(164L))) == "164",
            "a primitive: bare");

        // Rendered from bytes alone — what a router does for a provider in another process: no types, every kind.
        var layout = new PayloadLayout(64,
        [
            new PayloadField("i8", 0, FieldKind.Int8),
            new PayloadField("u8", 1, FieldKind.UInt8) { Names = new Dictionary<long, string> { [2] = "two" } },
            new PayloadField("i16", 2, FieldKind.Int16), new PayloadField("u16", 4, FieldKind.UInt16),
            new PayloadField("i32", 6, FieldKind.Int32) { Scale = 3 },
            new PayloadField("u32", 10, FieldKind.UInt32), new PayloadField("i64", 14, FieldKind.Int64) { Scale = 2 },
            new PayloadField("u64", 22, FieldKind.UInt64),
            new PayloadField("f32", 30, FieldKind.Float32), new PayloadField("f64", 34, FieldKind.Float64),
            new PayloadField("f16", 42, FieldKind.Float16),
            new PayloadField("flag", 44, FieldKind.Bool), new PayloadField("c", 45, FieldKind.Char),
            new PayloadField("text", 47, FieldKind.Utf8) { Length = 8 },
            new PayloadField("raw", 55, FieldKind.Bytes) { Length = 3 },
            new PayloadField("inner", 58, FieldKind.Struct)
            {
                Layout = new PayloadLayout(6,
                    [new PayloadField("x", 0, FieldKind.Int16), new PayloadField("y", 2, FieldKind.Int32)])
            }
        ]);
        var b = new byte[64];
        b[0] = 0xFF;
        b[1] = 2;
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(2), -300);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), 65000);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(6), -12345);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(10), 4_000_000_000);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(14), -650_000_050);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(22), ulong.MaxValue);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(30), 1.5f);
        BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(34), double.NaN);
        BinaryPrimitives.WriteHalfLittleEndian(b.AsSpan(42), (Half)0.25);
        b[44] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(45), 'é');
        "hi"u8.CopyTo(b.AsSpan(47));
        new byte[] { 1, 2, 3 }.CopyTo(b.AsSpan(55));
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(58), 7);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(60), -1);
        const string expected =
            """{"i8":-1,"u8":"two","i16":-300,"u16":65000,"i32":-12.345,"u32":4000000000,"i64":-6500000.50,"u64":18446744073709551615,"f32":1.5,"f64":"NaN","f16":0.25,"flag":true,"c":"é","text":"hi","raw":"010203","inner":{"x":7,"y":-1}}""";
        Assert(Json(w => layout.Render(w, b)) == expected, $"every kind, from bytes: {Json(w => layout.Render(w, b))}");
        var dotnet = new PayloadLayout(64,
        [
            new PayloadField("m", 0, FieldKind.Decimal), new PayloadField("g", 16, FieldKind.Guid),
            new PayloadField("t", 32, FieldKind.DateTime), new PayloadField("span", 40, FieldKind.TimeSpan),
            new PayloadField("big", 48, FieldKind.Int128)
        ]);
        var d = new byte[64];
        MemoryMarshal.Write(d.AsSpan(0), 65000.01m);
        MemoryMarshal.Write(d.AsSpan(16), id);
        MemoryMarshal.Write(d.AsSpan(32), new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc));
        MemoryMarshal.Write(d.AsSpan(40), TimeSpan.FromMinutes(90));
        MemoryMarshal.Write(d.AsSpan(48), Int128.MaxValue);
        Assert(
            Json(w => dotnet.Render(w, d)) ==
            $$"""{"m":65000.01,"g":"{{id}}","t":"2026-10-11T12:00:00.0000000Z","span":"01:30:00","big":170141183460469231731687303715884105727}""",
            $".NET's values as they are in memory: {Json(w => dotnet.Render(w, d))}");

        // Its JSON form — how it travels — round-tripped; a derived layout adjusted before it's sent.
        var format = new PayloadFormat
        {
            ContentType = "application/x-book", KeyLayout = level.With("Price", f => f with { Scale = 2 }),
            Layout = layout
        };
        var once = Json(format.WriteTo);
        var parsed = PayloadFormat.Parse(Encoding.UTF8.GetBytes(once));
        Assert(
            Json(parsed.WriteTo) == once && Json(w => parsed.Layout!.Render(w, b)) == expected &&
            parsed.KeyLayout!.Fields[1].Scale == 2, $"round trip: {once}");
        Assert(
            PayloadFormat.Parse("""{"contentType": "text/plain", "later": {"anything": 1}}"""u8).ContentType ==
            "text/plain", "properties it doesn't know: ignored");
        var viewed = format with { ViewUrl = new Uri("http://127.0.0.1:5000/views/book") };
        Assert(PayloadFormat.Parse(Encoding.UTF8.GetBytes(Json(viewed.WriteTo))).ViewUrl == viewed.ViewUrl,
            "a view URL: round-tripped");

        bool BadView(Func<Uri> url)
        {
            try
            {
                _ = new PayloadFormat { ViewUrl = url() };
                return false;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        Assert(
            BadView(() => new Uri("ftp://127.0.0.1/x")) && BadView(() => new Uri("http://user:secret@127.0.0.1/x")) &&
            BadView(() => new Uri("/relative", UriKind.Relative)),
            "a view URL: absolute http(s), no credentials");
        var inProcess = ProviderEndpoint.InProcess("market");
        var remote = new ProviderEndpoint(new Uri("tcp://10.0.0.7:7400"));
        Assert(PayloadFormat.OnProviderHost(new Uri("http://127.0.0.1:5000/v"), inProcess) &&
               PayloadFormat.OnProviderHost(new Uri("http://localhost:5000/v"), inProcess)
               && !PayloadFormat.OnProviderHost(new Uri("http://10.0.0.7:5000/v"), inProcess) &&
               PayloadFormat.OnProviderHost(new Uri("https://10.0.0.7/v"), remote)
               && !PayloadFormat.OnProviderHost(new Uri("http://10.0.0.8/v"), remote) &&
               !PayloadFormat.OnProviderHost(new Uri("http://169.254.169.254/latest"), remote),
            "a view URL on its provider's own host: in process, the loopback; elsewhere, its endpoint's host");

        // Refused: a layout that doesn't hold together (ArgumentException); JSON that isn't a format (FormatException).
        bool Refused(Func<object> make)
        {
            try
            {
                make();
                return false;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        PayloadLayout deep = new(1, [new PayloadField("v", 0, FieldKind.UInt8)]);
        for (var i = 1; i < PayloadLayout.MaxDepth; i++)
            deep = new PayloadLayout(1, [new PayloadField("n", 0, FieldKind.Struct) { Layout = deep }]);
        Assert(Refused(() => new PayloadLayout(8, [new PayloadField("x", 4, FieldKind.Int64)])) && Refused(() =>
                new PayloadLayout(8,
                    [new PayloadField("x", 0, FieldKind.Int32), new PayloadField("x", 4, FieldKind.Int32)]))
            && Refused(() => new PayloadLayout(8, [new PayloadField("x", 0, FieldKind.Float64) { Scale = 2 }])) &&
            Refused(() => new PayloadLayout(8,
                [new PayloadField("", 0, FieldKind.Int32), new PayloadField("y", 4, FieldKind.Int32)]))
            && Refused(() => new PayloadLayout(8, [new PayloadField("x", 0, FieldKind.Bytes)])) && Refused(() =>
                new PayloadLayout(8, [new PayloadField("x", 0, FieldKind.Struct)]))
            && Refused(() => new PayloadLayout(1, [new PayloadField("n", 0, FieldKind.Struct) { Layout = deep }])) &&
            !Refused(() => deep),
            "a field past the end, two of a name, a scale on a float, an unnamed field among others, bytes without a length, a struct without a layout, nesting past 8: refused");

        bool Unparsed(string json)
        {
            try
            {
                PayloadFormat.Parse(Encoding.UTF8.GetBytes(json));
                return false;
            }
            catch (FormatException)
            {
                return true;
            }
        }

        Assert(Unparsed("""{"layout": {"size": 8, "fields": [{"name": "x", "offset": 0, "kind": "int65"}]}}""") &&
               Unparsed("[1]") && Unparsed("""{"layout": {"size": 8}}""")
               && Unparsed("{") &&
               Unparsed("""{"layout": {"size": 4, "fields": [{"name": "x", "offset": 0, "kind": "int64"}]}}"""),
            "an unknown kind, not an object, no fields, not JSON, a field that doesn't fit: FormatException");

        // Step 1: the provider's format, on the topic before its route exists; a later step 1 without one leaves it.
        var registry = new OutletRegistry();
        var publisher = new Publisher(registry);
        var book = new Table<Level, long>("formatted");
        PayloadFormat? atBind = null;
        var provider = new TestProvider(book) { Format = format, Then = r => atBind = r.Tap!.Source.Format };
        Provide(publisher, "formatted", "formatted", provider);
        Assert(
            await publisher.SubscribeAsync(NewOutlet(registry), "formatted") is not null &&
            ReferenceEquals(book.Format, format) && ReferenceEquals(atBind, format),
            "step 1: the provider's format, on the topic as its route is bound");
        provider.Format = null;
        Assert(
            await publisher.SubscribeAsync(NewOutlet(registry), "formatted") is not null &&
            ReferenceEquals(book.Format, format), "a step 1 without one: left as it was");
        var elsewhere = false;
        provider.Instead = (r, _) =>
        {
            try
            {
                r.Accept(book, provider.Instance!,
                    format: format with { ViewUrl = new Uri("http://10.0.0.8:8080/views/book") });
            }
            catch (ArgumentException)
            {
                elsewhere = true;
            }

            return default;
        };
        Assert(
            await publisher.SubscribeAsync(NewOutlet(registry), "formatted") is null && elsewhere &&
            ReferenceEquals(book.Format, format),
            "a view URL off its provider's host: refused at step 1 (ArgumentException), the topic's format as it was");
    }

    public static async Task StreamingRateFromQuery()
    {
        foreach (var (text, expected) in new (string?, long?)[]
                 {
                     ("64", 65_536), ("0.5", 512), (".5", 512), ("2k", 2048), ("2K", 2048), ("2kib", 2048),
                     ("2KiB", 2048), ("2kb", 2000), ("2KB", 2000),
                     ("800b", 800), ("512b", 512), ("1.5m", 1_572_864), ("2mb", 2_000_000), ("1g", 1L << 30),
                     ("1k+1", 1025), ("1kb-1", 999), ("1+1", 1025),
                     (" 64 ", 65_536),
                     ("0", null), ("500b", null), ("0.4", null), ("1t", null), ("16kbit", null), ("2x", null),
                     ("1k+2", null), ("1k1", null),
                     ("-5", null), ("fast", null), ("", null), (null, null)
                 })
            Assert(BandwidthLimits.Parse(text) == expected,
                $"BandwidthLimits.Parse({text ?? "null"}) = {BandwidthLimits.Parse(text)}, expected {expected}");

        async Task<Outlet> Open(string url, string? parameter = "bwlimit", long serverCap = 64_000)
        {
            var options = new BlazorRealtimeOptions
                { StreamingBytesPerSecond = serverCap, StreamingRateQueryParameter = parameter };
            var outlet = new Outlet(new OutletRegistry(), options);
            var handler = new RealtimeCircuitHandler(new CircuitTransport(outlet, new BindRegistry()), null!,
                new TestNavigation(url), Options.Create(options),
                new FaultInjectionService(Options.Create(new TaplineOptions())));
            await handler.OnCircuitOpenedAsync(null!, default);
            return outlet;
        }

        var limited = await Open("http://host/?bwlimit=2");
        limited.Build(new byte[1 << 20]); // the bucket starts at the next frame
        Assert(limited.StreamingBytesPerSecond == 2048 && limited.StreamingTokens == 2048,
            $"2 → 2 KiB/s, one second of burst ({limited.StreamingTokens})");
        Assert((await Open("http://host/page?x=1&bwlimit=2kb")).StreamingBytesPerSecond == 2000,
            "among other parameters");
        Assert((await Open("http://host/?bwlimit=1m")).StreamingBytesPerSecond == 64_000,
            "lower only: never above the server's cap");
        Assert((await Open("http://host/?bwlimit=1m", serverCap: 0)).StreamingBytesPerSecond == 1_048_576,
            "no server cap: the URL sets one");
        Assert((await Open("http://host/")).StreamingBytesPerSecond == 64_000, "absent: the server's cap stays");
        Assert((await Open("http://host/?bwlimit=fast")).StreamingBytesPerSecond == 64_000, "malformed: ignored");
        Assert((await Open("http://host/?bwlimit=0")).StreamingBytesPerSecond == 64_000,
            "0 (rsync: unlimited) can't raise the cap");
        Assert((await Open("http://host/?bwlimit=2", null)).StreamingBytesPerSecond == 64_000, "off unless configured");
    }

    public static async Task FaultInjectionPages()
    {
        var registry = new OutletRegistry();

        FaultInjectionService Service(FaultInjectionOptions? o)
        {
            return new FaultInjectionService(Options.Create(new TaplineOptions { FaultInjection = o }));
        }

        bool Enlisted(FaultInjectionService faults, string path)
        {
            var outlet = new Outlet(registry, new OutletOptions());
            faults.Enlist(outlet, path);
            return outlet.FaultInjector == faults.Injector;
        }

        var faults = Service(new FaultInjectionOptions { Path = "/test" });
        foreach (var (path, test) in new[]
                 {
                     ("/", false), ("/?bwlimit=2k", false), ("/test", true), ("/TEST/", true),
                     ("/test?bwlimit=2k#x", true),
                     ("/test/book", true), ("/testing", false), ("/x/test", false)
                 })
            Assert(Enlisted(faults, path) == test, $"{path}: test connection expected {test}");
        Assert(!Enlisted(Service(null), "/test"), "off unless configured");
        Assert(Enlisted(faults, "test/book") && !Enlisted(faults, "testing"),
            "base-relative without its leading /, as Blazor's navigation gives it");
        foreach (var (page, test) in new[]
                     { ("http://host/test/book?bwlimit=2k", true), ("http://host/?x=test", false) })
        {
            var opened = new Outlet(registry, new OutletOptions());
            await new RealtimeCircuitHandler(new CircuitTransport(opened, new BindRegistry()), null!,
                    new TestNavigation(page), Options.Create(new BlazorRealtimeOptions()), faults)
                .OnCircuitOpenedAsync(null!, default);
            Assert(opened.FaultInjector == faults.Injector == test,
                $"a circuit opened on {page}: test connection expected {test}");
        }

        foreach (var bad in new FaultInjectionOptions[]
                 {
                     new() { Path = "/" }, new() { Outage = TimeSpan.Zero },
                     new() { Every = TimeSpan.FromSeconds(1), Outage = TimeSpan.FromSeconds(2) },
                     new() { Code = FaultCode.None }
                 })
            Assert(Threw<ArgumentOutOfRangeException>(() => Service(bad)),
                $"rejected: path {bad.Path}, every {bad.Every}, outage {bad.Outage}, code {bad.Code}");

        var fast = Service(new FaultInjectionOptions
            { Every = TimeSpan.FromMilliseconds(300), Outage = TimeSpan.FromMilliseconds(150) });
        var book = new Table<Level, long>("FS");
        var testOutlet = new Outlet(registry, new OutletOptions());
        fast.Enlist(testOutlet, "/test");
        await fast.StartAsync(default);
        bool down = false, upAgain = false;
        for (var sw = Stopwatch.StartNew(); sw.Elapsed < TimeSpan.FromSeconds(2) && !upAgain; await Task.Delay(10))
            if (testOutlet.FaultOf(book) is not null) down = true;
            else if (down) upAgain = true;
        await fast.StopAsync(default);
        Assert(down && upAgain && book.Fault is null,
            "the cycle runs: an outage, then recovery (the source itself never faults)");
    }

    /// <summary>
    ///     The circuit compression cut-off: on the wire, messages under the threshold go uncompressed (RSV1 clear) and the
    ///     rest
    ///     compressed, a fragmented message stays compressed, and a deflate client with history decodes every one exactly; in
    ///     the pipeline, MapBlazorRealtime wraps the socket accepted on …/_blazor only, and only when compression is on.
    /// </summary>
    public static async Task CircuitCompressionCutoff()
    {
        var wire = new MemoryStream();
        var options = new WebSocketCreationOptions
        {
            IsServer = true, DangerousDeflateOptions = new WebSocketDeflateOptions(),
            KeepAliveInterval = Timeout.InfiniteTimeSpan
        };
        var ws = new ThresholdWebSocket(WebSocket.CreateFromStream(new DuplexStream(Stream.Null, wire), options), 256,
            new FrameCompressionAdvice());
        var rnd = new Random(4);

        byte[] Msg(int n)
        {
            return Enumerable.Range(0, n).Select(i => (byte)(i % 13 == 0 ? rnd.Next(256) : i % 7)).ToArray();
        }

        var sent = new List<byte[]> { Msg(40), Msg(255), Msg(256), Msg(2000), Msg(60), Msg(5000), Msg(100) };
        foreach (var m in sent) await ws.SendAsync(m.AsMemory(), WebSocketMessageType.Binary, true, default);
        var fragmented = Msg(120);
        await ws.SendAsync(fragmented.AsMemory(0, 60), WebSocketMessageType.Binary, false, default);
        await ws.SendAsync(fragmented.AsMemory(60), WebSocketMessageType.Binary, true, default);
        sent.Add(fragmented);
        var after = Msg(30); // after a fragmented message: small again
        await ws.SendAsync(after.AsMemory(), WebSocketMessageType.Binary, true, default);
        sent.Add(after);

        var bytes = wire.ToArray();
        var compressed = new List<bool>();
        for (var o = 0; o < bytes.Length;)
        {
            long len = bytes[o + 1] & 0x7F;
            var header = 2;
            if (len == 126)
            {
                len = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(o + 2));
                header = 4;
            }
            else if (len == 127)
            {
                len = (long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(o + 2));
                header = 10;
            }

            if ((bytes[o] & 0x0F) != 0) compressed.Add((bytes[o] & 0x40) != 0); // RSV1 on a message's first frame
            o += header + (int)len;
        }

        Assert(compressed.SequenceEqual([false, false, true, true, false, true, false, true, false]),
            $"RSV1 per message: {string.Join(",", compressed)}");

        var client = WebSocket.CreateFromStream(new DuplexStream(new MemoryStream(bytes), Stream.Null),
            new WebSocketCreationOptions
            {
                IsServer = false, DangerousDeflateOptions = new WebSocketDeflateOptions(),
                KeepAliveInterval = Timeout.InfiniteTimeSpan
            });
        var buffer = new byte[1 << 16];
        foreach (var expected in sent)
        {
            var n = 0;
            ValueWebSocketReceiveResult r;
            do
            {
                r = await client.ReceiveAsync(buffer.AsMemory(n), default);
                n += r.Count;
            } while (!r.EndOfMessage);

            Assert(buffer.AsSpan(0, n).SequenceEqual(expected), $"decoded exactly ({expected.Length} B)");
        }

        // In the pipeline: wrapped on the circuit path only, and only when compression is on (Blazor's decorator turns it on).
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new OutletRegistry());
        builder.Services.Configure<BlazorRealtimeOptions>(o =>
        {
            o.CircuitSendBufferBytes = 24 * 1024;
            o.CircuitNotSentLowWatermarkBytes = 16 * 1024;
        });
        await using var app = builder.Build();
        app.MapBlazorRealtime();
        app.UseWebSockets(); // the app's own, for its other sockets
        var accepted = new ConcurrentDictionary<string, bool>();
        var sendBuffers = new ConcurrentDictionary<string, int>();
        var notSentLowat = new ConcurrentDictionary<string, int>();

        static int Lowat(Socket socket) // TCP_NOTSENT_LOWAT: Linux 25, macOS 0x201
        {
            var value = new byte[4];
            socket.GetRawSocketOption(6, OperatingSystem.IsLinux() ? 25 : 0x201, value);
            return BitConverter.ToInt32(value);
        }

        RequestDelegate Endpoint(bool compression)
        {
            return async ctx =>
            {
                using var socket = await ctx.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
                    { DangerousEnableCompression = compression });
                accepted[ctx.Request.Path + "?" + compression] = socket is ThresholdWebSocket;
                var tcp = ctx.Features.Get<IConnectionSocketFeature>()!
                    .Socket;
                sendBuffers[ctx.Request.Path + "?" + compression] = tcp.SendBufferSize;
                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                    notSentLowat[ctx.Request.Path + "?" + compression] = Lowat(tcp);
                await socket.SendAsync(new byte[40], WebSocketMessageType.Binary, true, default);
                await socket.SendAsync(Enumerable.Repeat((byte)3, 4000).ToArray(), WebSocketMessageType.Binary, true,
                    default);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, default);
            };
        }

        app.Map("/_blazor", ctx => Endpoint(ctx.Request.Query.ContainsKey("plain") ? false : true)(ctx));
        app.Map("/other", Endpoint(true));
        await app.StartAsync();
        foreach (var path in new[] { "/_blazor", "/_blazor?plain", "/other" })
        {
            using var c = new ClientWebSocket();
            c.Options.DangerousDeflateOptions = new WebSocketDeflateOptions();
            await c.ConnectAsync(new Uri(app.Urls.First().Replace("http", "ws") + path), default);
            var got = new List<int>();
            while (true)
            {
                var n = 0;
                WebSocketReceiveResult r;
                do
                {
                    r = await c.ReceiveAsync(new ArraySegment<byte>(buffer, n, buffer.Length - n), default);
                    n += r.Count;
                } while (!r.EndOfMessage);

                if (r.MessageType == WebSocketMessageType.Close) break;
                got.Add(n);
            }

            Assert(got.SequenceEqual([40, 4000]), $"{path}: both messages arrive intact");
        }

        await app.StopAsync();
        Assert(accepted["/_blazor?True"] && !accepted["/_blazor?False"] && !accepted["/other?True"],
            $"wrapped only on the circuit path with compression on: {string.Join(", ", accepted.Select(kv => $"{kv.Key}={kv.Value}"))}");

        bool Capped(int size)
        {
            return size is 24 * 1024 or 48 * 1024;
        } // Linux doubles what it is given

        Assert(
            Capped(sendBuffers["/_blazor?True"]) && Capped(sendBuffers["/_blazor?False"]) &&
            !Capped(sendBuffers["/other?True"]),
            $"send buffer capped on circuit sockets only: {string.Join(", ", sendBuffers.Select(kv => $"{kv.Key}={kv.Value}"))}");
        if (OperatingSystem.IsLinux())
            Assert(notSentLowat["/_blazor?True"] == 16 * 1024 && notSentLowat["/other?True"] == 0,
                $"TCP_NOTSENT_LOWAT on circuit sockets only: {string.Join(", ", notSentLowat)}");
        else if (OperatingSystem.IsMacOS())
            Assert(notSentLowat.Values.All(v => v == 0),
                $"TCP_NOTSENT_LOWAT is Linux-only: left unset here ({string.Join(", ", notSentLowat)})");
    }

    /// <summary>
    ///     Content declared not compressible — tables, values (also staged in a transaction), messages, broadcasts — and
    ///     out-of-band handles are counted per frame; watches only when every member is. Through the protocol and the circuit
    ///     socket, a frame of such content goes uncompressed however large, and a Blazor message sharing the WebSocket message
    ///     is what decides.
    /// </summary>
    public static async Task CompressibleContent()
    {
        var registry = new OutletRegistry();
        var s = NewOutlet(registry);
        var owner = new object();
        s.Attach(owner);

        (int Incompressible, int Length) Next()
        {
            Assert(s.TryBuildFrame(owner, false, out var f, out var seq, out var incompressible), "a frame");
            var length = f.Length - HeartbeatBytes(f);
            s.Ack(seq, 0);
            return (incompressible, length);
        }

        var plain = new Table<Level, long>("C1");
        var hashes = new Table<Level, long>("C2") { Compressible = false };
        FillBook(plain, 5);
        FillBook(hashes, 5);
        var values = new LastValues<int>("cv");
        s.Tap(plain, activate: true);
        Assert(Next().Incompressible == 0, "a table: compressible by default");
        s.Tap(hashes, activate: true);
        Assert(Next() is var h && h.Incompressible == h.Length, "a table marked not compressible");

        s.Tap(values[0], activate: true);
        values.Publish(0, new byte[100], compressible: false);
        Assert(Next().Incompressible == Wire.ValueHeader + 100, "a value published not compressible");
        values.Publish(0, new byte[100]);
        Assert(Next().Incompressible == 0, "…and its next publish, compressible");
        using (var tx = Transaction.Begin(values[0]))
        {
            tx.Of(values[0]).Publish(new byte[50], compressible: false);
            tx.Complete();
        }

        Assert(Next().Incompressible == Wire.ValueHeader + 50, "staged in a transaction");

        var hashed = new MessageTopic { Compressible = false };
        var text = new MessageTopic();
        s.Tap(hashed, activate: true);
        s.Tap(text, activate: true);
        hashed.Publish(new byte[30]);
        text.Publish(new byte[40]);
        Assert(
            Next() is var mixed && mixed.Incompressible == Wire.TopicMessageHeader + 30 &&
            mixed.Length == 2 * Wire.TopicMessageHeader + 70,
            "a frame mixing message topics counts only the incompressible one's message");

        s.Tap(values[1], activate: true);
        values.Publish(1, new byte[2000], Delivery.Reference);
        Assert(Next().Incompressible == Wire.OobRefSize, "an out-of-band handle (mostly random ids)");

        s.Watch("all hashes", WatchMember.Of(hashes)).Activate();
        Assert(Next() is var w && w.Incompressible == w.Length, "a watch whose members are all incompressible");
        s.Watch("mixed", WatchMember.Of(hashes), WatchMember.Of(plain)).Activate();
        Assert(Next().Incompressible == 0, "a watch with a compressible member");

        // Through the protocol and the circuit socket.
        var outlets = new OutletRegistry();
        var bp = BlazorPack();
        var protocol = new RealtimeProtocol(bp, outlets);
        var pipe = new Pipe();
        var hub = new HubConnectionContext(
            new DefaultConnectionContext("cc", new DuplexPipe(pipe.Reader, pipe.Writer),
                new DuplexPipe(pipe.Reader, pipe.Writer)),
            new HubConnectionContextOptions
            {
                KeepAliveInterval = TimeSpan.FromSeconds(15), ClientTimeoutInterval = TimeSpan.FromSeconds(30),
                StreamBufferCapacity = 10
            }, NullLoggerFactory.Instance) { Protocol = protocol };
        var advice = new FrameCompressionAdvice();
        var outlet = new Outlet(outlets, new OutletOptions());
        var shim = new ShimMessage(outlet, new CircuitLink(hub) { Advice = advice });
        outlet.Attach(shim);
        var blobs = new LastValues<int>("blob");
        outlet.Tap(blobs[0], activate: true);
        var wire = new MemoryStream();
        var ws = new ThresholdWebSocket(WebSocket.CreateFromStream(new DuplexStream(Stream.Null, wire),
            new WebSocketCreationOptions
            {
                IsServer = true, DangerousDeflateOptions = new WebSocketDeflateOptions(),
                KeepAliveInterval = Timeout.InfiniteTimeSpan
            }), 256, advice);

        async Task<bool> Send(int blazorBytes) // a Blazor message (if any), then the frame, in one send: compressed?
        {
            var buffer = new ArrayBufferWriter<byte>();
            if (blazorBytes > 0)
                bp.WriteMessage(new InvocationMessage("JS.RenderBatch", [1L, new byte[blazorBytes]]), buffer);
            protocol.WriteMessage(shim, buffer);
            outlet.Ack(long.MaxValue, 0);
            var at = wire.Length;
            await ws.SendAsync(buffer.WrittenMemory, WebSocketMessageType.Binary, true, default);
            return (wire.GetBuffer()[at] & 0x40) != 0;
        }

        blobs.Publish(0, RandomNumberGenerator.GetBytes(2000), compressible: false);
        Assert(!await Send(0), "2 KB of incompressible content: uncompressed");
        blobs.Publish(0, new byte[2000]);
        Assert(await Send(0), "2 KB of compressible content: compressed");
        blobs.Publish(0, RandomNumberGenerator.GetBytes(2000), compressible: false);
        Assert(await Send(600),
            "with a 600 B render batch in the same message: compressed (the batch is compressible)");
        blobs.Publish(0, RandomNumberGenerator.GetBytes(2000), compressible: false);
        Assert(!await Send(50), "with a 50 B one: uncompressed");
        Assert(!advice.HasPending, "every frame's advice consumed");
    }

    /// <summary>
    ///     The path from server to browser, simulated on a manual clock: a bottleneck of <paramref name="bytesPerSecond" />
    ///     behind a FIFO of unbounded depth (Kestrel's pipe, a middlebox), <paramref name="oneWayMs" /> each way, and optional
    ///     uncontrolled bursts sharing the FIFO (Blazor's render traffic). A hot book (3 row changes per ms) and a latency
    ///     probe
    ///     (the server time, published every ms) stream to a client that acks every frame on arrival; each probe value
    ///     received gives an update latency (publish → arrival, so it includes the delay one way).
    /// </summary>
    private static PathResult SimulatePath(Action<OutletOptions> configure, double bytesPerSecond, double oneWayMs,
        double seconds,
        (int Bytes, double Every)? bursts = null, (double At, double BytesPerSecond)? change = null,
        List<string>? trace = null, double warmSeconds = 2, int seed = 17)
    {
        var clock = new ManualTime();
        var wheel = new TimingWheel(TimeSpan.FromMilliseconds(1), time: clock, autoStart: false);
        var options = new OutletOptions();
        configure(options);
        var s = new Outlet(new OutletRegistry(wheel), options);
        var owner = new object();
        s.Attach(owner);
        var book = new Table<Level, long>("sim");
        FillBook(book, 30);
        var probe = new LastValues<int>("probe");
        s.Tap(book, activate: true);
        var probeTap = s.Tap(probe[0], activate: true);
        var c = new RefClient();
        var rnd = new Random(seed); // (the book's walk)

        long f = clock.TimestampFrequency,
            start = clock.Now,
            warm = start + (long)(warmSeconds * f),
            end = start + (long)(seconds * f);
        long delay = (long)(oneWayMs / 1000 * f), dt = Math.Max(f / 100_000, Math.Min(f / 4000, delay / 4));
        var rate = bytesPerSecond;
        long linkFree = start,
            nextMs = start,
            nextHeartbeat = start,
            nextBurst = bursts is { } b0 ? start + (long)(b0.Every * f) : long.MaxValue;
        var arrivals = new Queue<(long At, long Seq, byte[] Frame)>();
        var acks = new Queue<(long At, long Seq)>();
        var latency = new List<double>();
        var intervals = new List<double>();
        var age = new List<double>(); // at each 60 Hz paint: how old is what's shown
        long nextPaint = warm, shown = 0;
        long lastData = 0, bytes = 0, dataFrames = 0;
        double maxQueue = 0;
        for (; clock.Now < end; clock.Now += dt)
        {
            var now = clock.Now;
            if (change is { } ch && now >= start + (long)(ch.At * f)) rate = ch.BytesPerSecond;
            wheel.Advance(now);
            while (now >= nextMs)
            {
                using (var u = book.BeginUpdate())
                {
                    for (var k = 0; k < 3; k++)
                    {
                        var side = (byte)rnd.Next(2);
                        var level = new Level(side, side == 0 ? 99 - rnd.Next(10) : 101 + rnd.Next(10));
                        if (rnd.Next(10) == 0) u.Delete(level);
                        else u.Fold<long, Add<long>>(level, rnd.Next(1, 50));
                    }

                    u.Complete();
                }

                probe.Publish(0, BitConverter.GetBytes(nextMs));
                nextMs += f / 1000;
            }

            if (now >= nextHeartbeat)
            {
                s.Heartbeat();
                nextHeartbeat += f / 2;
                trace?.Add(
                    $"t {(now - start) / (double)f,5:F1}s  limit {s.InflightLimit,6}  inflight {s.InflightBytes,6}  minRtt {s.MinRtt.TotalMilliseconds,7:F1}  srtt {s.SmoothedRtt.TotalMilliseconds,7:F1}  pace {s.PacingRate,8} B/s  rate {s.DeliveryRate,7}  queue {Math.Max(0, linkFree - now) * 1000.0 / f,7:F1} ms  frames {dataFrames,5}");
            }

            if (now >= nextBurst && bursts is { } b)
            {
                linkFree = Math.Max(now, linkFree) + (long)(b.Bytes / rate * f);
                nextBurst += (long)(b.Every * f);
            }

            while (arrivals.Count > 0 && arrivals.Peek().At <= now)
            {
                var (_, seq, frame) = arrivals.Dequeue();
                c.Decode(frame);
                acks.Enqueue((now + delay, seq));
                if (now < warm) continue;
                bytes += frame.Length;
                if (c.LastFrameRecords.Any(r => r is Wire.Table or Wire.Value))
                {
                    dataFrames++;
                    if (lastData > 0) intervals.Add((now - lastData) * 1000.0 / f);
                    lastData = now;
                }

                if (c.LastFrameTaps.Contains(probeTap.Id))
                    latency.Add((now - (shown = BitConverter.ToInt64(c.Values[probeTap.Id]))) * 1000.0 / f);
            }

            if (now >= nextPaint)
            {
                if (shown > 0) age.Add((now - shown) * 1000.0 / f);
                nextPaint += f / 60;
            }

            while (acks.Count > 0 && acks.Peek().At <= now)
            {
                var (_, seq) = acks.Dequeue();
                s.Ack(seq, c.HeartbeatSeq);
            }

            while (s.IsReady && s.TryBuildFrame(owner, false, out var frame, out var seq))
            {
                var send = Math.Max(now, linkFree);
                if (now >= warm) maxQueue = Math.Max(maxQueue, (send - now) * 1000.0 / f);
                linkFree = send + (long)((frame.Length + 40) / rate * f); // + SignalR envelope and WebSocket header
                arrivals.Enqueue((linkFree + delay, seq, frame.ToArray()));
            }
        }

        double P(List<double> v, double q)
        {
            if (v.Count == 0) return double.NaN;
            v.Sort();
            return v[Math.Min(v.Count - 1, (int)(q * v.Count))];
        }

        var measured = (end - warm) / (double)f;
        return new PathResult(dataFrames / measured, P(latency, 0.5), P(latency, 0.99), P(latency, 0.995), maxQueue,
            P(intervals, 0.99), bytes / measured / 1024,
            P(age, 0.5), P(age, 0.99), P(age, 0.995));
    }

    /// <summary>
    ///     <see cref="Outlet.SetMaxFramesPerSecond" /> while a value is published every millisecond to an instant client: each
    ///     cap holds its rate, and raising it re-times the next frame from the last one sent (30 → 120: within 1/120 s). Also
    ///     with a value larger than the frame budget (as the waveform's ridges on a fast path): it goes alone, and the frame it
    ///     fills must not hold the tick open when nothing else is waiting — the cap held nothing back before that was fixed.
    /// </summary>
    public static Task FrameRateAtRuntime()
    {
        var small = Cadence(_ => { }, 4);
        var large = Cadence(o =>
        {
            o.FlowControl = FlowControlMode.Fixed;
            o.FrameBudget = 1024;
        }, 3000);
        foreach (var (name, r) in new[] { ("4 B values", small), ("3 KB values, 1 KB budget", large) })
        {
            Notes.Add(
                $"{name}: frames/s uncapped {r.Uncapped:F0}, 60 → {r.At60:F1}, 30 → {r.At30:F1}, 120 → {r.At120:F1}, uncapped again {r.Free:F0}; 30 → 120 first frame {r.GapMs:F1} ms after the last");
            Assert(r.Uncapped > 900 && r.Free > 900, $"{name}: uncapped follows the publisher");
            Assert(Math.Abs(r.At60 - 60) < 0.5 && Math.Abs(r.At30 - 30) < 0.5 && Math.Abs(r.At120 - 120) < 1,
                $"{name}: each cap holds its rate");
            Assert(r.GapMs <= 1000.0 / 120 + 1.1, $"{name}: raised cap applies from the last frame ({r.GapMs:F1} ms)");
        }

        return Task.CompletedTask;
    }

    private static CadenceResult Cadence(Action<OutletOptions> configure, int valueBytes)
    {
        var clock = new ManualTime();
        var wheel = new TimingWheel(TimeSpan.FromMilliseconds(1), time: clock, autoStart: false);
        var options = new OutletOptions();
        configure(options);
        var s = new Outlet(new OutletRegistry(wheel), options);
        var owner = new object();
        s.Attach(owner);
        var probe = new LastValues<int>("probe");
        s.Tap(probe[0], activate: true);
        var c = new RefClient();
        var value = new byte[valueBytes];
        long f = clock.TimestampFrequency, dt = f / 10_000, nextMs = clock.Now, nextHeartbeat = clock.Now, lastData = 0;

        List<long> Run(double seconds)
        {
            var sent = new List<long>();
            for (var end = clock.Now + (long)(seconds * f); clock.Now < end; clock.Now += dt)
            {
                var now = clock.Now;
                wheel.Advance(now);
                if (now >= nextMs)
                {
                    BitConverter.TryWriteBytes(value, now);
                    probe.Publish(0, value);
                    nextMs += f / 1000;
                }

                if (now >= nextHeartbeat)
                {
                    s.Heartbeat();
                    nextHeartbeat += f / 2;
                }

                while (s.IsReady && s.TryBuildFrame(owner, false, out var frame, out var seq))
                {
                    c.Decode(frame.ToArray());
                    s.Ack(seq, c.HeartbeatSeq); // an instant client
                    if (c.LastFrameRecords.Contains(Wire.Value))
                    {
                        sent.Add(now);
                        lastData = now;
                    }
                }
            }

            return sent;
        }

        double Rate(List<long> sent)
        {
            return sent.Count < 2 ? 0 : (sent.Count - 1) * (double)f / (sent[^1] - sent[0]);
        }

        Run(0.5);
        var uncapped = Run(1);
        s.SetMaxFramesPerSecond(60);
        Run(0.2);
        var at60 = Run(1);
        s.SetMaxFramesPerSecond(30);
        Run(0.2);
        var at30 = Run(1);
        for (var give = clock.Now + f; clock.Now - lastData < 5 * f / 1000 && clock.Now < give;)
            Run(0.0001); // 5 ms after a frame at 30 Hz, the next due in ~28 ms…
        var before = lastData;
        s.SetMaxFramesPerSecond(120);
        var gap = Run(0.05)[0] - before; // …but at 120 Hz it's due 8.3 ms after the last
        Run(0.2);
        var at120 = Run(1);
        s.SetMaxFramesPerSecond(0);
        var free = Run(1);
        Assert(s.MaxFramesPerSecond == 0, "the cap reads back");
        return new CadenceResult(Rate(uncapped), Rate(at60), Rate(at30), Rate(at120), Rate(free), gap * 1000.0 / f);
    }

    /// <summary>
    ///     The waveform's workload on a simulated path: a large incompressible value (the ridge frame) published at 120 Hz in
    ///     Streaming, and a 500 B audio chunk every 40 ms into a rolling window (a table of 16 rows, chunk number mod 16) on a
    ///     tap bound to High — as the demo. A chunk is on time if it arrives within 150 ms of publication (the demo publishes
    ///     120 ms ahead, plus a 30 ms margin, which the browser grows to 400 ms when chunks come late); one overwritten in the
    ///     window before it was sent never arrives. Same path model as
    ///     <see cref="SimulatePath" />: a bottleneck behind a FIFO of unbounded depth, one-way delay each way, an ack per
    ///     frame.
    /// </summary>
    private static MediaResult SimulateMedia(double bytesPerSecond, double oneWayMs, double seconds,
        int sceneBytes = 5800, List<string>? trace = null, double tickMs = 0, bool sid = false,
        double stallMs = 0)
    {
        // stallMs > 0: every half second the client stops processing for that long (a browser's busy main thread) — what
        // arrives meanwhile, and its acks, wait. LowBudgetSeconds: seconds in which the demo's tier logic would see under
        // 150 KiB/s (paced, at the delivery rate) and so ask for smaller frames.
        // sid: the music as SID register rows — 124 B every 200 ms in a 32-row window — instead of 500 B every 40 ms.
        // tickMs > 0: each direction releases what has arrived only at tick boundaries — as the throttle proxy does (and
        // as cellular and Wi-Fi links batch) — so round trips jitter by up to two ticks with no queue at all.
        var clock = new ManualTime();
        var wheel = new TimingWheel(TimeSpan.FromMilliseconds(1), time: clock, autoStart: false);
        var s = new Outlet(new OutletRegistry(wheel), new OutletOptions());
        var owner = new object();
        s.Attach(owner);
        var values = new LastValues<int>("media");
        var ring = new Table<byte, SimChunk>("media-audio") { Delivery = Delivery.Inline, Compressible = false };
        var sidRing = new Table<byte, SimSidRow>("media-sid") { Delivery = Delivery.Inline, Compressible = false };
        double chunkHz = sid ? 5 : 25;
        var sceneTap = s.Tap(values[0], activate: true);
        var chunkTap = sid
            ? s.Tap(sidRing, activate: true, lane: Lane.High)
            : s.Tap(ring, activate: true, lane: Lane.High);
        int chunkSeq = 0, lastSeen = -1;
        var c = new RefClient();
        var rnd = new Random(3);
        var scene = new byte[sceneBytes];
        long f = clock.TimestampFrequency, start = clock.Now, warm = start + 3 * f, end = start + (long)(seconds * f);
        long delay = (long)(oneWayMs / 1000 * f), dt = f / 4000, tick = (long)(tickMs / 1000 * f);

        long Ticked(long at)
        {
            return tick > 0 ? (at - start + tick - 1) / tick * tick + start : at;
        }

        long linkFree = start, nextScene = start, nextChunk = start, nextHeartbeat = start, busy = 0, lastSceneAt = 0;
        var arrivals = new Queue<(long At, long Seq, byte[] Frame)>();
        var acks = new Queue<(long At, long Seq)>();
        var sceneAges = new List<double>();
        var lateness = new List<double>();
        int scenes = 0, chunksOnTime = 0, chunks = 0;
        double maxQueue = 0;
        long shownScene = 0, nextBudgetCheck = warm;
        var lowBudget = 0;
        for (; clock.Now < end; clock.Now += dt)
        {
            var now = clock.Now;
            wheel.Advance(now);
            if (now >= nextScene)
            {
                rnd.NextBytes(scene);
                BitConverter.TryWriteBytes(scene, now);
                values.Publish(0, scene, compressible: false);
                nextScene += f / 120;
            }

            if (now >= nextChunk)
            {
                if (sid)
                    using (var u = sidRing.BeginUpdate())
                    {
                        u.Set((byte)(chunkSeq % 32), new SimSidRow { PublishedAt = now, Seq = chunkSeq });
                        u.Complete();
                    }
                else
                    using (var u = ring.BeginUpdate())
                    {
                        u.Set((byte)(chunkSeq % 16), new SimChunk { PublishedAt = now, Seq = chunkSeq });
                        u.Complete();
                    }

                chunkSeq++;
                nextChunk += (long)(f / chunkHz);
            }

            if (now >= nextHeartbeat)
            {
                s.Heartbeat();
                nextHeartbeat += f / 2;
                trace?.Add(
                    $"t {(now - start) / (double)f,5:F1}s  limit {s.InflightLimit,6}  inflight {s.InflightBytes,6}  minRtt {s.MinRtt.TotalMilliseconds,6:F1}  pace {s.PacingRate,7} B/s  rate {s.DeliveryRate,7}  queue {Math.Max(0, linkFree - now) * 1000.0 / f,6:F1} ms  scenes {scenes,4}  chunks on time {chunksOnTime}/{chunks}");
            }

            var stalled = stallMs > 0 && (now - start) % (f / 2) < (long)(stallMs / 1000 * f);
            if (now >= nextBudgetCheck)
            {
                var budget = s.PacingRate > 0 || s.DeliveryRate < 24 * 1024 ? s.DeliveryRate : double.PositiveInfinity;
                if (budget < 150 * 1024) lowBudget++;
                nextBudgetCheck += f;
            }

            while (!stalled && arrivals.Count > 0 && arrivals.Peek().At <= now)
            {
                var (_, seq, frame) = arrivals.Dequeue();
                c.Decode(frame);
                acks.Enqueue((Ticked(now + delay), seq));
                if (c.LastFrameTaps.Contains(chunkTap.Id)) // chunks not seen before (the window's older rows were)
                {
                    var newest = lastSeen;
                    foreach (var (_, row) in c.Tables[chunkTap.Id].Rows.Values)
                    {
                        var chunkNo = BitConverter.ToInt32(row, 8);
                        if (chunkNo <= lastSeen) continue;
                        if (now >= warm) lateness.Add((now - BitConverter.ToInt64(row, 0)) * 1000.0 / f);
                        newest = Math.Max(newest, chunkNo);
                    }

                    lastSeen = newest;
                }

                if (now < warm) continue;
                if (c.LastFrameTaps.Contains(sceneTap.Id))
                {
                    scenes++;
                    shownScene = BitConverter.ToInt64(c.Values[sceneTap.Id]);
                    lastSceneAt = now;
                }
            }

            if (now >= warm && (now - start) % (f / 60) < dt && shownScene > 0)
                sceneAges.Add((now - shownScene) * 1000.0 / f);
            while (acks.Count > 0 && acks.Peek().At <= now)
            {
                var (_, seq) = acks.Dequeue();
                s.Ack(seq, c.HeartbeatSeq);
            }

            while (s.IsReady && s.TryBuildFrame(owner, false, out var frame, out var seq))
            {
                var send = Math.Max(now, linkFree);
                if (now >= warm)
                {
                    maxQueue = Math.Max(maxQueue, (send - now) * 1000.0 / f);
                    busy += (long)((frame.Length + 40) / bytesPerSecond * f);
                }

                linkFree = send + (long)((frame.Length + 40) / bytesPerSecond * f);
                arrivals.Enqueue((Ticked(linkFree + delay), seq, frame.ToArray()));
            }
        }

        double P(List<double> v, double q)
        {
            if (v.Count == 0) return double.NaN;
            v.Sort();
            return v[Math.Min(v.Count - 1, (int)(q * v.Count))];
        }

        var measured = (end - warm) / (double)f;
        chunks = (int)(measured * chunkHz);
        chunksOnTime = lateness.Count(l => l <= 150);
        return new MediaResult(scenes / measured, P(sceneAges, 0.5), P(sceneAges, 0.995),
            Math.Min(1, (double)lateness.Count / chunks),
            (double)chunksOnTime / chunks, P(lateness, 0.995), busy / (double)(end - warm), maxQueue, lowBudget);
    }

    public static Task MediaSimulation()
    {
        if (Environment.GetEnvironmentVariable("TAPLINE_TRACE_MEDIA") is
            { } which) // "bytes/s,one-way ms[,frame bytes[,tick ms[,sid]]]"
        {
            var p = which.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            var trace = new List<string>();
            var r = SimulateMedia(p[0], p[1], 20, p.Length > 2 ? (int)p[2] : 5800, trace, p.Length > 3 ? p[3] : 0,
                p.Length > 4 && p[4] != 0);
            Notes.AddRange(trace);
            Notes.Add($"traced: {r}");
        }

        var results = new Dictionary<string, MediaResult>();
        foreach (var (name, bps, oneWay, bytes) in new[]
                 {
                     ("colo", 1.25e9, 0.05, 5800), ("50 Mbit/s, 15 ms", 6.25e6, 15.0, 5800),
                     ("2 Mbit/s, 40 ms", 250e3, 40.0, 5800),
                     ("256 kbit/s, 30 ms", 32e3, 30.0, 5800), ("160 kbit/s, 30 ms", 20e3, 30.0, 5800),
                     ("256 kbit/s, lite", 32e3, 30.0, 1450),
                     ("160 kbit/s, lite", 20e3, 30.0, 1450), // the demo's quarter-size frames
                     ("256 kbit/s, lite, 50 ms ticks", 32e3, 5.0, 1450), // jitter: delivery in ticks, no queue
                     ("16 kbit/s, tiny + SID", 2000, 75.0, 580), ("16 kbit/s, tiny + SID, 50 ms ticks", 2000, 5.0, 580),
                     ("16 kbit/s, range-coded tiny + SID", 2000, 30.0, 300), // the demo as it is now
                     ("localhost, client stalls 40 ms / 0.5 s", 1.25e9, 0.1, 5800), // a busy browser, not a slow path
                     ("localhost, 25 ms ticks", 1.25e9, 0.1, 5800) // a browser batching its messages
                 })
        {
            var r = results[name] = SimulateMedia(bps, oneWay, 30, bytes,
                tickMs: name.Contains("50 ms ticks") ? 50 : name.Contains("25 ms ticks") ? 25 : 0,
                sid: name.Contains("SID"),
                stallMs: name.Contains("stalls") ? 40 : 0);
            Notes.Add(
                $"{name,-38} ridges {r.SceneFps,6:F1} frames/s  age at paint p50 {r.SceneAgeP50,6:F1}  p99.5 {r.SceneAgeP995,7:F1} ms  ·  chunks delivered {r.ChunksDelivered,6:P1}, within 150 ms {r.ChunksOnTime,6:P1}, p99.5 {r.ChunkLateP995,6:F1} ms  ·  link {r.LinkUse,6:P0}  queue ≤ {r.MaxQueueMs,6:F1} ms" +
                (r.LowBudgetSeconds > 0 ? $"  ·  {r.LowBudgetSeconds} s under the lite threshold" : ""));
        }

        foreach (var name in new[] { "colo", "50 Mbit/s, 15 ms" })
            Assert(results[name].SceneFps > 119 && results[name].ChunksOnTime > 0.99,
                $"{name}: every frame and chunk ({results[name]})");
        Assert(results["2 Mbit/s, 40 ms"].LinkUse > 0.9 && results["2 Mbit/s, 40 ms"].ChunksOnTime > 0.95,
            $"2 Mbit/s: fills the link, chunks on time ({results["2 Mbit/s, 40 ms"]})");
        foreach (var name in
                 new[] { "256 kbit/s, 30 ms", "160 kbit/s, 30 ms" }) // large frames on a slow path: no stalls
            Assert(results[name].LinkUse > 0.85, $"{name}: link kept busy ({results[name]})");
        foreach (var name in
                 new[]
                 {
                     "256 kbit/s, lite", "160 kbit/s, lite", "256 kbit/s, lite, 50 ms ticks"
                 }) // every chunk, within what the browser can wait
            Assert(results[name].ChunksDelivered > 0.99 && results[name].ChunkLateP995 < 520,
                $"{name}: chunks delivered, within 120 + 400 ms ({results[name]})");
        // 16 kbit/s, the music as SID rows on High behind tiny ridge frames: every row, within the browser's buffer (≤ 2 s).
        // The scheduler's quanta follow the frame budget: before, Streaming's turn lasted seconds there (rows 3.7 s late).
        foreach (var name in new[] { "16 kbit/s, tiny + SID", "16 kbit/s, tiny + SID, 50 ms ticks" })
            Assert(results[name].ChunksDelivered > 0.99 && results[name].ChunkLateP995 < 1500,
                $"{name}: rows delivered within 1.5 s ({results[name]})");
        // …and the ridges make way for it: a row waits behind at most the one ridge frame on the wire (150 ms here) — not
        // behind a queue of them, nor for room in the window (907 ms at p99.5 before they did) — and the ridges still go.
        var demo = results["16 kbit/s, range-coded tiny + SID"];
        Assert(demo.ChunksDelivered > 0.99 && demo.ChunkLateP995 < 350 && demo.SceneFps >= 1.5,
            $"16 kbit/s demo: rows within 350 ms, ridges still going ({demo})");
        // A browser busy 40 ms in every 500 on a fast path is not a slow path: the estimate mustn't collapse (as a hold
        // read it once did, sending the demo's ridges to lite) — at most a stray second under the threshold.
        var stalls = results["localhost, client stalls 40 ms / 0.5 s"];
        Assert(stalls.LowBudgetSeconds <= 1 && stalls.SceneFps > 100,
            $"client stalls: {stalls.LowBudgetSeconds} s under the lite threshold, {stalls.SceneFps:F0} frames/s");
        return Task.CompletedTask;
    }

    /// <summary>
    ///     An app-limited link, as a provider's link to its router carries a practice's turns: a turn's records (each a
    ///     topic's change, 200 B to 2 KB, its publish time in it) every half second or so, now and then seconds with only
    ///     heartbeats, on a fast path, to a receiver that processes each frame in a fraction of a millisecond, now and then
    ///     in 5–20 ms, and every few seconds stalls for 40–120 ms (a busy process on a loaded machine, not a slow path). Each
    ///     record's latency, from its publish to its arrival: what the sender held back.
    /// </summary>
    private static BurstResult SimulateBursts(Action<OutletOptions> configure, double seconds, int seed = 23,
        (int From, int To)? idleMs = null, double processMs = 0.2)
    {
        var clock = new ManualTime();
        var wheel = new TimingWheel(TimeSpan.FromMilliseconds(1), time: clock, autoStart: false);
        var options = new OutletOptions();
        configure(options);
        var s = new Outlet(new OutletRegistry(wheel), options);
        var owner = new object();
        s.Attach(owner);
        const int Topics = 12;
        const double Rate = 1.25e9; // colo
        var values = new LastValues<int>("turns");
        var taps = new uint[Topics];
        for (var k = 0; k < Topics; k++) taps[k] = s.Tap(values[k], activate: true).Id;
        var c = new RefClient();
        var rnd = new Random(seed);
        var (idleFrom, idleTo) = idleMs ?? (2000, 6000);
        long f = clock.TimestampFrequency, start = clock.Now, warm = start + 2 * f, end = start + (long)(seconds * f);
        long delay = f / 20_000, dt = f / 20_000; // 0.05 ms one way; 50 µs steps
        long linkFree = start, receiverFree = start, stallUntil = 0, nextHeartbeat = start, nextTurn = start + f / 2,
            nextStall = start + 3 * f, bytes = 0;
        var arrivals = new Queue<(long At, long Seq, byte[] Frame)>();
        var acks = new Queue<(long At, long Seq)>();
        var latency = new List<double>();
        var payload = new byte[2048];
        for (; clock.Now < end; clock.Now += dt)
        {
            var now = clock.Now;
            wheel.Advance(now);
            if (now >= nextTurn)
            {
                for (var n = rnd.Next(5, Topics + 1); n > 0; n--)
                {
                    BinaryPrimitives.WriteInt64LittleEndian(payload, now);
                    values.Publish(rnd.Next(Topics), payload.AsSpan(0, rnd.Next(200, 2001)));
                }

                nextTurn = now + (rnd.Next(7) == 0 ? rnd.Next(idleFrom, idleTo + 1) : rnd.Next(300, 701)) * f / 1000; // now and then idle
            }

            if (now >= nextHeartbeat)
            {
                s.Heartbeat();
                nextHeartbeat += f;
            }

            if (now >= nextStall)
            {
                stallUntil = now + rnd.Next(40, 121) * f / 1000;
                nextStall = now + rnd.Next(2, 6) * f;
            }

            while (arrivals.Count > 0 && arrivals.Peek().At <= now)
            {
                var (_, seq, frame) = arrivals.Dequeue();
                c.Decode(frame);
                // In order: each frame processed after the one before, sometimes slowly, never during a stall.
                receiverFree = Math.Max(Math.Max(receiverFree, now), stallUntil) +
                               (rnd.Next(20) == 0 ? rnd.Next(5, 21) * f / 1000 : (long)(processMs / 1000 * f));
                acks.Enqueue((receiverFree + delay, seq));
                if (now < warm) continue;
                bytes += frame.Length;
                for (var i = 0; i < c.LastFrameTaps.Count; i++)
                    if (c.LastFrameRecords[i] == Wire.Value && Array.IndexOf(taps, c.LastFrameTaps[i]) >= 0)
                        latency.Add((now - BinaryPrimitives.ReadInt64LittleEndian(c.Values[c.LastFrameTaps[i]])) * 1000.0 / f);
            }

            while (acks.Count > 0 && acks.Peek().At <= now) s.Ack(acks.Dequeue().Seq, c.HeartbeatSeq);
            while (s.IsReady && s.TryBuildFrame(owner, false, out var frame, out var seq))
            {
                linkFree = Math.Max(now, linkFree) + (long)((frame.Length + 40) / Rate * f);
                arrivals.Enqueue((linkFree + delay, seq, frame.ToArray()));
            }
        }

        latency.Sort();
        double P(double q) => latency.Count == 0 ? double.NaN : latency[Math.Min(latency.Count - 1, (int)(q * latency.Count))];
        return new BurstResult(P(0.5), P(0.99), P(0.995), latency.Count == 0 ? double.NaN : latency[^1], latency.Count,
            bytes / ((end - warm) / (double)f) / 1024);
    }

    public sealed record BurstResult(double P50, double P99, double P995, double Max, int Records, double KBps);

    /// <summary>
    ///     A turn's records on an app-limited link to a fast path (<see cref="SimulateBursts" />): adaptive flow control holds
    ///     none back much longer than a fixed window does. Its estimate of the path comes from what the application sent, and
    ///     seconds of heartbeats alone, or a late receiver, mustn't leave it pacing the next turn at a few bytes a second (as
    ///     a rate sample spanning an idle spell, and rounds of heartbeats pushing the measured ones out, once did: 749 ms).
    /// </summary>
    public static Task BurstSimulation()
    {
        foreach (var (link, seconds, idle, processMs) in new[]
                 {
                     ("idle 2–6 s, a 0.2 ms receiver", 90.0, (2000, 6000), 0.2),
                     ("idle 10–20 s, a 1 ms receiver", 240.0, (10_000, 20_000), 1.0) // longer than the estimate's ten rounds
                 })
        {
            var results = new Dictionary<string, BurstResult>();
            foreach (var (name, configure) in new (string, Action<OutletOptions>)[]
                         { ("fixed", o => o.FlowControl = FlowControlMode.Fixed), ("adaptive", _ => { }) })
            {
                var r = results[name] = SimulateBursts(configure, seconds, idleMs: idle, processMs: processMs);
                Notes.Add($"app-limited turns on colo, {link}, {name,-8}: latency p50 {r.P50:F2}  p99 {r.P99:F2}  p99.5 {r.P995:F2}  max {r.Max:F2} ms over {r.Records} records, {r.KBps:F1} KB/s");
            }

            var a = results["adaptive"];
            Assert(a.P995 < 10 && a.Max < 50, $"{link}: adaptive holds no turn back on colo: p99.5 {a.P995:F1}, max {a.Max:F1} ms");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Adaptive flow control on 16 kbit/s (2,000 B/s, 50 ms each way), a manual clock: 7 KB data frames, 3.5 s each on the
    ///     path — longer than the heartbeat interval — to a client whose transport sends only the newest acknowledgement
    ///     waiting (as <c>ITaplineConnection</c> allows, and the .NET client's MagicOnion transport does), so each ack brings
    ///     home a data frame and the heartbeats queued behind it. The delivery rate is measured from the data frames: within
    ///     a quarter of the path's.
    /// </summary>
    public static Task FlowControlCoalescedAcks()
    {
        const long f = 1_000_000; // µs
        const int pathRate = 2_000, frameBytes = 7_000, heartbeatBytes = 13;
        const long oneWay = 50_000;
        var flow = new FlowControl(new OutletOptions(), f, f / 1000);
        var path = new Queue<(long Arrives, long Seq)>(); // at the client
        var acks = new Queue<(long Arrives, long Seq)>(); // at the server
        long seq = 1, linkFree = 0, nextHeartbeat = 0, received = 0, receivedAt = long.MaxValue;
        for (long now = 0; now < 60 * f; now += 1_000)
        {
            while (acks.TryPeek(out var a) && a.Arrives <= now) flow.OnAck(acks.Dequeue().Seq, now);

            void Send(int bytes, bool data)
            {
                flow.OnSent(seq, bytes, data, tookEverything: !data, now);
                linkFree = Math.Max(linkFree, now) + bytes * f / pathRate;
                path.Enqueue((linkFree + oneWay, seq++));
            }

            if (now >= nextHeartbeat) // a heartbeat a second, whatever is in flight
            {
                Send(heartbeatBytes, false);
                nextHeartbeat += f;
            }

            if (flow.HasRoom && flow.PaceAllows(now) && now >= flow.SpacedUntil) Send(frameBytes, true); // (data always ready)
            else flow.MarkLimited();

            while (path.TryPeek(out var p) && p.Arrives <= now) (received, receivedAt) = (path.Dequeue().Seq, now);
            if (received > 0 && now - receivedAt >= 10_000) // quiet 10 ms: the newest acknowledgement waiting goes, alone
            {
                acks.Enqueue((now + oneWay, received));
                (received, receivedAt) = (0, long.MaxValue);
            }
        }

        var rate = flow.DeliveryRate;
        Notes.Add($"coalesced acks on 16 kbit/s, 7 KB frames: delivery rate {rate:N0} B/s (the path's {pathRate:N0})");
        Assert(Math.Abs(rate - pathRate) <= pathRate / 4, $"the delivery rate measured from the data frames: {rate:N0} B/s");
        return Task.CompletedTask;
    }

    /// <summary>
    ///     The old fixed window (2 frames), adaptive flow control, and adaptive at 60 Hz, over paths from colo to 16 kbit/s:
    ///     update latency (probe publish → arrival, delay one way included), data frames per second, worst FIFO queue.
    /// </summary>
    public static Task PathSimulation()
    {
        if (Environment.GetEnvironmentVariable("TAPLINE_TRACE_PATH") is
            { } which) // "bytes/s,one-way ms[,frame rate[,change at s,to bytes/s]]"
        {
            var p = which.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            var trace = new List<string>();
            var r = SimulatePath(o => o.MaxFramesPerSecond = p.Length > 2 ? (int)p[2] : 0, p[0], p[1], 20,
                change: p.Length > 4 ? (p[3], p[4]) : null, trace: trace);
            Notes.AddRange(trace);
            Notes.Add(
                $"traced: {r.FramesPerSecond:F1} frames/s  age at paint p50 {r.AgeP50:F1}  p99.5 {r.AgeP995:F1} ms  interval p99 {r.IntervalP99:F1} ms  queue ≤ {r.MaxQueueMs:F1} ms");
        }

        var configs = new (string Name, Action<OutletOptions> Configure)[]
        {
            ("fixed, 2 frames", o =>
            {
                o.FlowControl = FlowControlMode.Fixed;
                o.Window = 2;
            }),
            ("adaptive", _ => { }),
            ("adaptive, 60 Hz", o => o.MaxFramesPerSecond = 60)
        };
        // Measured after a warm-up — on 16 kbit/s a long one: the activation snapshot alone takes 0.56 s there, and the
        // startup overshoot it causes (nothing is known about the path until it's acked) takes ~3 s to clear.
        var paths =
            new (string Name, double Bps, double OneWayMs, double Seconds, (int, double)? Bursts, (double, double)?
                Change, double Warm)[]
                {
                    ("colo 10 Gbit/s, 0.05 ms", 1.25e9, 0.05, 4, null, null, 2),
                    ("50 Mbit/s, 15 ms", 6.25e6, 15, 10, null, null, 2),
                    ("50 Mbit/s, 100 ms", 6.25e6, 100, 10, null, null, 2),
                    ("2 Mbit/s, 40 ms + 60 KB bursts / 2 s", 250e3, 40, 14, (60_000, 2.0), null, 2),
                    ("16 kbit/s, 75 ms", 2000, 75, 40, null, null, 6),
                    ("50 → 1 Mbit/s at 6 s, 20 ms", 6.25e6, 20, 14, null, (6.0, 125e3), 2),
                    ("50 Mbit/s → 16 kbit/s at 6 s, 20 ms", 6.25e6, 20, 40, null, (6.0, 2000), 2)
                };
        var results = new Dictionary<(string, string), PathResult>();
        foreach (var path in paths)
        foreach (var config in configs)
        {
            var r = SimulatePath(config.Configure, path.Bps, path.OneWayMs, path.Seconds, path.Bursts, path.Change,
                warmSeconds: path.Warm);
            results[(path.Name, config.Name)] = r;
            Notes.Add(
                $"{path.Name,-38} {config.Name,-16} {r.FramesPerSecond,6:F1} frames/s  age at paint p50 {r.AgeP50,7:F1}  p99 {r.AgeP99,7:F1}  p99.5 {r.AgeP995,7:F1} ms  interval p99 {r.IntervalP99,6:F1} ms  queue ≤ {r.MaxQueueMs,7:F1} ms  {r.KBps,6:F1} KB/s");
        }

        PathResult R(int path, int config)
        {
            return results[(paths[path].Name, configs[config].Name)];
        }

        const int Fixed = 0, Adaptive = 1, At60 = 2;
        foreach (var i in new[] { 0, 1, 2 }) // nearby, broadband, long: the data rate, one way's delay
        {
            var (fixedRun, a) = (R(i, Fixed), R(i, Adaptive));
            Assert(a.FramesPerSecond >= (i == 2 ? 150 : 990),
                $"{paths[i].Name}: {a.FramesPerSecond:F0} frames/s uncapped");
            Assert(a.AgeP995 <= paths[i].OneWayMs * 1.1 + 2,
                $"{paths[i].Name}: age p99.5 {a.AgeP995:F1} ms, one way {paths[i].OneWayMs} ms");
            Assert(a.AgeP995 <= fixedRun.AgeP995,
                $"{paths[i].Name}: adaptive p99.5 {a.AgeP995:F1} ms > fixed {fixedRun.AgeP995:F1} ms");
        }

        foreach (var i in new[] { 0, 1, 2, 5 }) // wherever the path carries it, 60 Hz is 60 Hz
        {
            var c = R(i, At60);
            Assert(Math.Abs(c.FramesPerSecond - 60) < 0.5 && c.IntervalP99 <= 17.5,
                $"{paths[i].Name}: {c.FramesPerSecond:F1} frames/s at 60 Hz, interval p99 {c.IntervalP99:F1} ms");
        }

        foreach (var i in new[] { 3, 5 }) // bursts sharing the FIFO, a capacity drop: no worse than the fixed window
            Assert(R(i, Adaptive).AgeP995 <= R(i, Fixed).AgeP995,
                $"{paths[i].Name}: adaptive p99.5 {R(i, Adaptive).AgeP995:F1} ms > fixed {R(i, Fixed).AgeP995:F1} ms");
        // On 16 kbit/s a few bytes more or less in a frame move adaptive's figures by tens of ms either way (the fixed
        // window's hardly), so these are judged over the book's walk with 13 seeds: the median of each figure.
        var walks = new ConcurrentDictionary<(int Path, int Config, int Seed), PathResult>();
        Parallel.ForEach(
            from i in new[] { 4, 6 } from c in new[] { Fixed, Adaptive, At60 } from seed in Enumerable.Range(1, 12)
            select (i, c, seed), new ParallelOptions { MaxDegreeOfParallelism = 8 }, run =>
                walks[run] = SimulatePath(configs[run.c].Configure, paths[run.i].Bps, paths[run.i].OneWayMs,
                    paths[run.i].Seconds, paths[run.i].Bursts, paths[run.i].Change, warmSeconds: paths[run.i].Warm,
                    seed: run.seed));
        double Walked(int path, int config, Func<PathResult, double> figure)
        {
            var all = Enumerable.Range(1, 12).Select(seed => figure(walks[(path, config, seed)]))
                .Append(figure(R(path, config))).Order().ToArray();
            return all[all.Length / 2];
        }

        foreach (var i in new[] { 4, 6 })
        foreach (var c in new[] { Fixed, Adaptive, At60 })
            Notes.Add($"{paths[i].Name,-38} {configs[c].Name,-16} over 13 walks: age at paint p50 " +
                      $"{Walked(i, c, r => r.AgeP50),7:F1}  p99.5 {Walked(i, c, r => r.AgeP995),7:F1} ms (medians)");
        // 16 kbit/s: a frame takes 225 ms to send. Fresher at the median; probing for more bandwidth costs at the tail.
        Assert(Walked(4, Adaptive, r => r.AgeP50) <= Walked(4, Fixed, r => r.AgeP50) &&
               Walked(4, Adaptive, r => r.AgeP995) <= 1.2 * Walked(4, Fixed, r => r.AgeP995),
            $"16 kbit/s over 13 walks: adaptive p50 {Walked(4, Adaptive, r => r.AgeP50):F0} / p99.5 " +
            $"{Walked(4, Adaptive, r => r.AgeP995):F0} ms vs fixed {Walked(4, Fixed, r => r.AgeP50):F0} / " +
            $"{Walked(4, Fixed, r => r.AgeP995):F0} ms");
        // 50 Mbit/s → 16 kbit/s: what was in flight at the drop has to drain (seconds, uncapped), then it settles — fresher
        // than the fixed window at the median; a 60 Hz stream, as fresh within a tenth, its tail far shorter than uncapped
        // (less in flight at the drop).
        Assert(Walked(6, Adaptive, r => r.AgeP50) <= Walked(6, Fixed, r => r.AgeP50),
            $"drop to 16 kbit/s over 13 walks, adaptive: age p50 {Walked(6, Adaptive, r => r.AgeP50):F0} ms > fixed " +
            $"{Walked(6, Fixed, r => r.AgeP50):F0} ms");
        Assert(Walked(6, At60, r => r.AgeP50) <= 1.1 * Walked(6, Fixed, r => r.AgeP50) &&
               Walked(6, At60, r => r.AgeP995) < Walked(6, Adaptive, r => r.AgeP995) / 2,
            $"drop to 16 kbit/s over 13 walks, 60 Hz: age p50 {Walked(6, At60, r => r.AgeP50):F0} ms (fixed " +
            $"{Walked(6, Fixed, r => r.AgeP50):F0}), p99.5 {Walked(6, At60, r => r.AgeP995):F0} ms (uncapped " +
            $"{Walked(6, Adaptive, r => r.AgeP995):F0})");
        return Task.CompletedTask;
    }

    // ─────────────────────────────── fixtures for the JS test ───────────────────────────────

    public static Task WriteJsFixtures()
    {
        var registry = new OutletRegistry();
        var s = NewOutlet(registry, o => o.FrameBudget = 1024);
        var book = new Table<Level, long>("JS", 128);
        var values = new LastValues<int>("t");
        var bookTap = s.Tap(book, activate: true);
        var valueTap = s.Tap(values[7], activate: true);
        var alerts = new MessageTopic(1024, 64 * 1024);
        var alertTap = s.Tap(alerts, activate: true);
        var rnd = new Random(6);
        var frames = new List<string>();
        var scratch = new byte[1 << 20];
        int expectedMessages = 0, expectedMail = 0;

        for (var step = 0; step < 5000; step++)
        {
            using (var u = book.BeginUpdate())
            {
                for (var i = rnd.Next(1, 5); i > 0; i--)
                {
                    var level = new Level((byte)rnd.Next(2), 4_503_599_627_370_000L + rnd.Next(100));
                    if (rnd.Next(4) == 0) u.Delete(level);
                    else u.Set(level, rnd.NextInt64(1, 1L << 50));
                }

                u.Complete();
            }

            values.Publish(7, BitConverter.GetBytes(step));
            if (step % 10 == 0)
            {
                alerts.Publish(BitConverter.GetBytes(step));
                expectedMessages++;
            } // a message topic

            if (step % 50 == 0 && valueTap.TryPost(BitConverter.GetBytes(1_000_000 + step)))
                expectedMail++; // the value tap's mailbox
            if (step % 100 == 0) s.Heartbeat();
            if (rnd.Next(10) == 0)
                for (int n; (n = s.Build(scratch)) > 0;)
                    frames.Add(Convert.ToBase64String(scratch, 0, n));
        }

        for (int n; (n = s.Build(scratch)) > 0;) frames.Add(Convert.ToBase64String(scratch, 0, n));

        var rows = Canonical(book);

        // Fault scenario: a book tap with an error policy (top of book, size 0 + payload) and a mark tap; then an upstream gap.
        var fs = NewOutlet(registry);
        var fbook = new Table<Level, long>("JS-FAULT");
        var fmarks = new LastValues<int>("jm");
        FillBook(fbook, 20, 1);
        fmarks.Publish(0, BitConverter.GetBytes(77));
        var fbookTap = fs.Tap(fbook, "book:BTC-PERP", new ErrorPolicy("feed interrupted"u8.ToArray(), TopOfBook), true);
        var fmarkTap = fs.Tap(fmarks[0], "mark:BTC-PERP", new ErrorPolicy("stale"u8.ToArray()), true);
        var faultFrames = new List<string>();
        for (int n; (n = fs.Build(scratch)) > 0;) faultFrames.Add(Convert.ToBase64String(scratch, 0, n));
        try
        {
            using var u = fbook.BeginUpdate(5);
        }
        catch (StreamFaultException)
        {
        }

        fmarks[0].Fail(FaultCode.SourceFailed, "feed down");
        for (int n; (n = fs.Build(scratch)) > 0;) faultFrames.Add(Convert.ToBase64String(scratch, 0, n));

        // Out of band and a watch: a large snapshot, a large message with a small one behind it on the same topic,
        // a reference value and a watch over two key ranges; then — after the client has fetched — deltas and trades.
        var oobRegistry = new OutletRegistry();
        var os = NewOutlet(oobRegistry);
        var obook = new Table<Level, long>("JS-OOB")
            { ContentAddressed = true }; // verified by the JS client (the reference value below isn't)
        FillBook(obook, 1500);
        var oTap = os.Tap(obook, activate: true);
        var ovalues = new LastValues<int>("js-oob");
        ovalues.Publish(0, [1, 2, 3], Delivery.Reference);
        var refTap = os.Tap(ovalues[0], activate: true);
        var positions = new Table<Holding, long>("js-positions");
        var cash = new Table<int, long>("js-cash");
        var balances = Enumerable.Range(0, 3).ToDictionary(a => a, _ => Endowment);
        using (var u = cash.BeginUpdate())
        {
            foreach (var (a, b) in balances) u.Set(a, b);
            u.Complete();
        }

        Trade(positions, cash, balances, 2, 1, 10);
        var watch = os.Watch("account:2", WatchMember.Range(positions, 2), WatchMember.Range(cash, 2));
        watch.Activate();
        var bigMessage = Enumerable.Range(0, 100_000).Select(i => (byte)(i * 13)).ToArray();
        var oalerts = new MessageTopic(16, 1 << 18) { ContentAddressed = true };
        var oalertTap = os.Tap(oalerts, activate: true);
        oalerts.Publish(bigMessage);
        oalerts.Publish([7]);

        List<string> DrainB64(Outlet o, RefClient rc)
        {
            var list = new List<string>();
            for (int n; (n = o.Build(scratch)) > 0;)
            {
                list.Add(Convert.ToBase64String(scratch, 0, n));
                rc.Decode(scratch.AsSpan(0, n));
            }

            return list;
        }

        var orc = new RefClient();
        var oobFrames1 = DrainB64(os, orc);
        var blobs = new Dictionary<string, string>();
        foreach (var h in orc.Pending.Concat(orc.References.Values))
        {
            var data = Fetch(oobRegistry, h.Id, os.Id, h.Tag);
            Assert(data is not null, "fixture blob");
            blobs[$"_tapline/blob/{h.Id}/{os.Id}/{Convert.ToHexString(h.Tag).ToLowerInvariant()}"] =
                Convert.ToBase64String(data!);
        }

        Assert(orc.Pending.Count == 2 && orc.References.Count == 1, "fixture: two transparent handles and a reference");
        orc.FetchPending(os, oobRegistry); // as the browser will: then the held streams resume
        using (var u = obook.BeginUpdate())
        {
            u.Set(new Level(0, 1), 1);
            u.Delete(new Level(1, 101));
            u.Complete();
        }

        var trades = new Random(9);
        for (var i = 0; i < 40; i++)
            Trade(positions, cash, balances, trades.Next(3), trades.Next(4), trades.Next(1, 20));
        var oobFrames2 = DrainB64(os, orc);

        // A subscription through a data provider, then its instance gone (withdrawn): the binding as Blazor's interop
        // serializes it (its transient id a JSON string), and the frames — the last with the Service record.
        var sregistry = new OutletRegistry();
        var spublisher = new Publisher(sregistry);
        var ss = NewOutlet(sregistry);
        var stopic = new LastValueSource();
        stopic.Publish([5]);
        var sprovider = new TestProvider(stopic);
        Provide(spublisher, "svc", "js-station", sprovider);
        var stap = spublisher.SubscribeAsync(ss, "svc").AsTask().GetAwaiter().GetResult()!;
        var direct = ss.Tap(stopic, "direct", activate: true); // the same topic, not through the provider
        stap.Activate();
        var serviceFrames = new List<string>();
        for (int n; (n = ss.Build(scratch)) > 0;) serviceFrames.Add(Convert.ToBase64String(scratch, 0, n));
        spublisher.Withdraw(At("js-station"));
        stopic.Publish([6]);
        for (int n; (n = ss.Build(scratch)) > 0;) serviceFrames.Add(Convert.ToBase64String(scratch, 0, n));
        Assert(stap.IsClosed && !direct.IsClosed, "fixture: the provider's route closed, the direct tap not");
        var blueTopic = new LastValueSource(); // and an instance draining: the drained notice
        var blueProvider = new TestProvider(blueTopic);
        Provide(spublisher, "svc-blue", "js-blue", blueProvider);
        var blueTap = spublisher.SubscribeAsync(ss, "svc-blue").AsTask().GetAwaiter().GetResult()!;
        blueTap.Activate();
        for (int n; (n = ss.Build(scratch)) > 0;)
        {
        }

        blueProvider.Routers.DrainAsync(TimeSpan.Zero).GetAwaiter().GetResult();
        var drainedFrames = new List<string>();
        for (int n; (n = ss.Build(scratch)) > 0;) drainedFrames.Add(Convert.ToBase64String(scratch, 0, n));
        Assert(blueTap.IsClosed && drainedFrames.Count > 0, "fixture: the drained notice");
        var tealProvider =
            new TestProvider(new LastValueSource()); // draining with a grace: told to move, nothing closed
        Provide(spublisher, "svc-teal", "js-teal", tealProvider);
        var tealTap = spublisher.SubscribeAsync(ss, "svc-teal").AsTask().GetAwaiter().GetResult()!;
        tealTap.Activate();
        for (int n; (n = ss.Build(scratch)) > 0;)
        {
        }

        tealProvider.Routers.DrainAsync(TimeSpan.Zero, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        var drainingFrames = new List<string>();
        for (int n; (n = ss.Build(scratch)) > 0;) drainingFrames.Add(Convert.ToBase64String(scratch, 0, n));
        Assert(!tealTap.IsClosed && drainingFrames.Count > 0, "fixture: the draining notice, the route still open");
        spublisher.ProviderHeartbeatInterval = TimeSpan.Zero;
        spublisher.ServiceUp = new ServiceUpPolicy(); // no jitter: told at once
        Provide(spublisher, "svc", "js-station-2", new TestProvider(stopic)); // another instance: the service up again
        spublisher.HeartbeatProviders(); // probes it (answered at once)…
        spublisher.HeartbeatProviders(); // …and, a quorum of one up, tells the waiting client
        var upFrames = new List<string>();
        for (int n; (n = ss.Build(scratch)) > 0;) upFrames.Add(Convert.ToBase64String(scratch, 0, n));
        Assert(upFrames.Count > 0, "fixture: the up notice");

        // A client that stalls: a frame never acknowledged, the stall timeout passes — its subscriptions end; one bound
        // since stands; back on a new connection, it is told in one Ended record.
        var stalled = NewOutlet(new OutletRegistry(), o => o.StallTimeout = TimeSpan.FromMilliseconds(50));
        var stallOwner = new object();
        stalled.Attach(stallOwner);
        var stallValues = new LastValues<int>("js-stall");
        var stalledTaps = new[]
            { stalled.Tap(stallValues[0], activate: true), stalled.Tap(stallValues[1], activate: true) };
        stallValues.Publish(0, [1]);
        stallValues.Publish(1, [2]);
        var stalledFrames = new List<string>();
        Assert(stalled.TryBuildFrame(stallOwner, false, out var stallFrame, out _), "fixture: a frame");
        stalledFrames.Add(Convert.ToBase64String(stallFrame));
        stallValues.Publish(0, [3]);
        for (var i = 0; i < 400 && !stalledTaps[0].IsClosed; i++)
        {
            stalled.Heartbeat();
            Thread.Sleep(5);
        }

        var since = stalled.Tap(stallValues[2], activate: true);
        stallValues.Publish(2, [4]);
        stalled.Attach(stallOwner = new object());
        while (stalled.TryBuildFrame(stallOwner, false, out stallFrame, out _))
            stalledFrames.Add(Convert.ToBase64String(stallFrame));
        Assert(stalledTaps.All(t => t.IsClosed) && !since.IsClosed, "fixture: stalled");

        // Partitions: a client's subscriptions in "eu/fx" and "us"; "svc-p/eu" reported degraded, then not — the scoped
        // notices (the scope as the client sees it, each change's id) and what streams between them.
        var pregistry = new OutletRegistry();
        var (ppublisher, pprovider, ptopics) = InPartitions(pregistry, "svc-p", "js-parts");
        var pchanges = new List<ServiceHealthChange>();
        ppublisher.ServiceHealthChanged += pchanges.Add;
        var ps = NewOutlet(pregistry);
        var pfx = ppublisher.SubscribeAsync(ps, "svc-p", "eu/fx").AsTask().GetAwaiter().GetResult()!;
        var pus = ppublisher.SubscribeAsync(ps, "svc-p", "us").AsTask().GetAwaiter().GetResult()!;
        pfx.Activate();
        pus.Activate();
        var partitionFrames = new List<string>();

        void PartitionFrames()
        {
            for (int n; (n = ps.Build(scratch)) > 0;) partitionFrames.Add(Convert.ToBase64String(scratch, 0, n));
        }

        PartitionFrames();
        pprovider.Degraded = ["svc-p/eu"];
        ppublisher.HeartbeatProviders();
        ptopics["us"].Publish([9]);
        PartitionFrames();
        pprovider.Degraded = null;
        ppublisher.HeartbeatProviders();
        PartitionFrames();
        Assert(pchanges.Select(c => c.Event).SequenceEqual([ServiceHealthEvent.Degraded, ServiceHealthEvent.Recovered]),
            "fixture: a partition degraded, then recovered");

        // The router's own state behind its heartbeats: serving, degraded (why), the same again (echoed: the heartbeat
        // alone), draining (a grace); a value behind each, in the same frame (a RouterChange record's length follows its state).
        var hregistry = new OutletRegistry();
        var hs = NewOutlet(hregistry);
        var howner = new object();
        hs.Attach(howner);
        var hvalues = new LastValues<int>("js-router");
        var hTap = hs.Tap(hvalues[0], activate: true);
        var routerFrames = new List<string>();

        void Beat(int value)
        {
            hvalues.Publish(0, [(byte)value]);
            hs.Heartbeat();
            Assert(hs.TryBuildFrame(howner, false, out var f, out var seq) && f[0] == Wire.Heartbeat,
                "fixture: a heartbeat, then a value");
            routerFrames.Add(Convert.ToBase64String(f));
            hs.ReleaseFrame();
            hs.Ack(seq, BinaryPrimitives.ReadUInt32LittleEndian(f[1..]));
        }

        var hServing = hregistry.Health;
        Beat(1);
        var hDegraded = hregistry.SetHealth(RouterState.Degraded, "db: Unhealthy, timed out — ünïcode");
        Beat(2);
        Beat(3);
        var hDraining = hregistry.SetHealth(RouterState.Draining, grace: TimeSpan.FromSeconds(10));
        Beat(4);

        var envelope = new ArrayBufferWriter<byte>();
        FrameWriter.Write(envelope, EnvelopeSeq,
            EnvelopePayload()); // fixed: what the SignalR JS client was shown (fixtures/signalr.json)
        var fixture = new
        {
            frames,
            tableId = bookTap.Id,
            valueId = valueTap.Id,
            seq = book.Version,
            checksum = book.Checksum,
            rows = rows.Values.Select(r => new[] { Convert.ToHexString(r.Key), Convert.ToHexString(r.Row) }),
            lastValue = 4999,
            alertsId = alertTap.Id,
            expectedMessages,
            expectedMail,
            envelopeHex = Convert.ToHexString(envelope.WrittenSpan),
            envelopeSeq = EnvelopeSeq,
            fault = new
            {
                frames = faultFrames,
                bookId = fbookTap.Id,
                markId = fmarkTap.Id,
                version = fbook.Version,
                rowCount = fbook.Count,
                degradedRows = new[] { new Level(0, 99), new Level(1, 101) }.Select(l =>
                    new[] { Convert.ToHexString(Bytes(l)), Convert.ToHexString(Bytes(0L)) }),
                bookCode = (int)FaultCode.VersionMismatch,
                markCode = (int)FaultCode.SourceFailed,
                markVersion = fmarks[0].Version
            },
            service = new
            {
                binding = JsonSerializer.SerializeToElement(stap.Binding, JsonSerializerOptions.Web),
                frames = serviceFrames,
                upFrames,
                directId = direct.Id,
                drainedBinding = JsonSerializer.SerializeToElement(blueTap.Binding, JsonSerializerOptions.Web),
                drainedFrames,
                drainingBinding = JsonSerializer.SerializeToElement(tealTap.Binding, JsonSerializerOptions.Web),
                drainingFrames
            },
            partitions = new
            {
                fx = JsonSerializer.SerializeToElement(pfx.Binding, JsonSerializerOptions.Web),
                us = JsonSerializer.SerializeToElement(pus.Binding, JsonSerializerOptions.Web),
                frames = partitionFrames,
                scope = pchanges[0].Scope,
                correlations = pchanges.Select(c => c.Correlation)
            },
            router = new
            {
                frames = routerFrames,
                valueId = hTap.Id,
                correlations = new[] { hServing.Correlation, hDegraded.Correlation, hDraining.Correlation },
                reason = hDegraded.Reason,
                graceMs = 10_000
            },
            stalled = new
            {
                frames = stalledFrames,
                ids = new[] { stalledTaps[0].Id, stalledTaps[1].Id, since.Id }
            },
            oob = new
            {
                outletId = os.Id,
                nonce = os.Nonce,
                frames1 = oobFrames1,
                frames2 = oobFrames2,
                blobs,
                tableId = oTap.Id,
                refTapId = refTap.Id,
                watchId = watch.Id,
                messagesId = oalertTap.Id,
                bookSeq = obook.Version,
                bookChecksum = obook.Checksum,
                bookRows = obook.Count,
                bigMessageHex = Convert.ToHexString(SHA256.HashData(bigMessage)),
                endowment = Endowment,
                positionsSeq = positions.Version,
                cashSeq = cash.Version,
                account2Rows = Enumerable.Range(0, 4).Count(sym => positions.TryGet(new Holding(2, sym), out _))
            },
            auth = AuthFixture(),
            grid = GridFixture(registry, scratch),
            alerts = AlertFixture()
        };
        Directory.CreateDirectory(Path.Combine(RepoRoot(), "tests", "js", "fixtures"));
        File.WriteAllText(Path.Combine(RepoRoot(), "tests", "js", "fixtures", "frames.json"),
            JsonSerializer.Serialize(fixture));
        File.WriteAllText(Path.Combine(RepoRoot(), "tests", "js", "fixtures", "formats.json"),
            JsonSerializer.Serialize(FormatsFixture()));
        Notes.Add($"fixture: {frames.Count} frames, {rows.Count} live rows (9-byte keys, prices near 2^52)");
        return Task.CompletedTask;
    }

    /// <summary>A byte-level fold, as a sandboxed module would implement it: row(i64) += delta(i64); present while non-zero.</summary>
    private sealed class BytesAdd : IBatchFold
    {
        public int Calls;
        public bool Throw;

        public void Fold(Span<byte> rows, int rowCount, int rowSize, ReadOnlySpan<byte> deltas, int deltaCount,
            int deltaSize)
        {
            Calls++;
            if (Throw) throw new InvalidOperationException("fuel exhausted");
            int rs = FoldEntry.RowStride(rowSize), ds = FoldEntry.DeltaStride(deltaSize);
            for (var i = 0; i < deltaCount; i++)
            {
                var d = deltas.Slice(i * ds, ds);
                var e = rows.Slice((int)BinaryPrimitives.ReadUInt32LittleEndian(d) * rs, rs);
                var row = BinaryPrimitives.ReadInt64LittleEndian(e[4..]) +
                          BinaryPrimitives.ReadInt64LittleEndian(d[4..]);
                BinaryPrimitives.WriteInt64LittleEndian(e[4..], row);
                BinaryPrimitives.WriteUInt32LittleEndian(e, row != 0 ? FoldEntry.Present : 0u);
            }
        }
    }

    private readonly struct Explode : IRowFold<long, long>
    {
        public static bool Fold(ref long row, bool exists, in long delta)
        {
            if (delta == 666) throw new OverflowException("bad delta");
            row += delta;
            return row != 0;
        }
    }

    private sealed class Throwing : IDegradeFunction
    {
        public void Degrade(Span<byte> entries, int count, int keySize, int rowSize)
        {
            throw new InvalidOperationException("trap");
        }
    }

    private sealed class KeepAll : IDegradeFunction
    {
        public void Degrade(Span<byte> entries, int count, int keySize, int rowSize)
        {
        }
    }

    private sealed class CountingKeepAll : IDegradeFunction
    {
        public int Calls;

        public void Degrade(Span<byte> entries, int count, int keySize, int rowSize)
        {
            Calls++;
        }
    }

    // ─────────────────────────────── scheduler ───────────────────────────────

    private sealed class ManualTime : TimeProvider
    {
        public long Now = 1_000_000_000;
        public override long TimestampFrequency => 1_000_000_000;

        public override long GetTimestamp()
        {
            return Now;
        }

        public void Advance(TimeSpan by)
        {
            Now += (long)(by.TotalSeconds * TimestampFrequency);
        }
    }

    private sealed class Probe(ManualTime clock) : IWheelTarget
    {
        public readonly WheelNode Node = null!;
        public long Due, FiredAt = -1;
        public int Fires;

        public Probe(ManualTime clock, bool _) : this(clock)
        {
            Node = new WheelNode(this);
        }

        public void OnWheel()
        {
            Fires++;
            FiredAt = clock.Now;
        }
    }

    /// <summary>A wake-up target recording how late it fired (by the driver alone: one at a time).</summary>
    private sealed class LateProbe : IWheelTarget
    {
        public readonly long[] Late;
        public readonly WheelNode Node;
        public int Count;
        public long Due;
        public int Fired = 1;

        public LateProbe(long[] late)
        {
            Late = late;
            Node = new WheelNode(this);
        }

        public void OnWheel()
        {
            var late = Stopwatch.GetTimestamp() - Due;
            if (Count < Late.Length) Late[Count++] = late;
            Volatile.Write(ref Fired, 1);
        }
    }

    // ─────────────────────────────── data providers ───────────────────────────────

    /// <summary>A provider of one topic, following the epoch rule (or, <paramref name="naive" />, stopping on any 202).</summary>
    /// <summary>A provider as it's reached over a link: another's answers, and whether it's reached, as the test says.</summary>
    private sealed class Reachability(IDataProvider inner, bool reachable) : IDataProvider, IReachableProvider
    {
        private volatile bool _reachable = reachable;

        public bool Reachable => _reachable;

        public event Action? ReachableChanged;

        public ValueTask ConnectAsync(IRouterChannel router, CancellationToken cancellationToken)
        {
            return inner.ConnectAsync(router, cancellationToken);
        }

        public ValueTask SubscribeAsync(SubscribeRequest request, CancellationToken cancellationToken)
        {
            return inner.SubscribeAsync(request, cancellationToken);
        }

        public ValueTask ActivateAsync(Activation notice, CancellationToken cancellationToken)
        {
            return inner.ActivateAsync(notice, cancellationToken);
        }

        public ValueTask UnsubscribeAsync(Unsubscription notice, CancellationToken cancellationToken)
        {
            return inner.UnsubscribeAsync(notice, cancellationToken);
        }

        public ValueTask<HealthReport> CheckHealthAsync(HealthProbe probe, CancellationToken cancellationToken)
        {
            return inner.CheckHealthAsync(probe, cancellationToken);
        }

        /// <summary>Reached now, or not: said, as a link says it.</summary>
        public void Set(bool reachable)
        {
            _reachable = reachable;
            ReachableChanged?.Invoke();
        }
    }

    private sealed class TestProvider(
        Source topic,
        InitialState initial = InitialState.Snapshot,
        bool naive = false,
        bool actOnNotices = false) : IDataProvider
    {
        public readonly List<IRouterChannel> Connections = [];
        public readonly Lock Gate = new();
        public readonly ConcurrentQueue<(RouterInstance Router, RouteId[] Routes)> Lost = new();
        public readonly ConcurrentQueue<(Unsubscription Notice, int RoutesThen)> Notices = new();
        public readonly ConcurrentQueue<object> Relayed = new(); // step 2s and ends, in order
        private RouterSessions? _routers;
        public ProviderInstance? AcceptAs; // the instance its answers name (null: itself)
        public long Epoch;
        public PayloadFormat? Format; // its payloads' format, handed to the router in step 1

        public Func<HealthProbe, CancellationToken, ValueTask<HealthReport>>?
            Health; // how it answers health checks (null: as itself)

        public int Heartbeats; // health checks received
        public double? Load; // the load it reports (null: not said)
        public ProviderInstance? Instance; // who it is (set when registered: Provide)

        public Func<SubscribeRequest, CancellationToken, ValueTask>?
            Instead; // step 1 its own way (a misbehaving provider)

        public HealthProbe? LastProbe;
        public bool Live;
        public ErrorPolicy? Policy; // the stale-data function it hands the router with each route
        public string? Refuse;
        public ProviderStatus Status = ProviderStatus.Serving;
        public int Subscribes;
        public Action<SubscribeRequest>? Then;

        public Func<IRouterChannel, IRouterChannel>?
            Wrap; // the channel to a router, as it reaches it (a partition, say)

        public ProviderInstance? LastExpected => LastProbe?.Expected;

        public Func<string, ServiceAccess>? Access; // what it declares of each service (null: open)
        public SubscribeRequest? Last; // the last step 1 it was asked

        public ValueTask<ServiceAccess> AccessAsync(string service, CancellationToken cancellationToken)
        {
            return new ValueTask<ServiceAccess>(Access?.Invoke(service) ?? ServiceAccess.Open);
        }

        /// <summary>Its routers, as it keeps them (the library's helper).</summary>
        public RouterSessions Routers => _routers ??= new RouterSessions(() => Instance!)
            { Lost = (r, routes) => Lost.Enqueue((r, routes)) };

        public ValueTask ConnectAsync(IRouterChannel router, CancellationToken cancellationToken)
        {
            lock (Connections)
            {
                Connections.Add(router);
            }

            Routers.Connected(Wrap?.Invoke(router) ?? router);
            return default;
        }

        public ValueTask<HealthReport> CheckHealthAsync(HealthProbe probe, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Heartbeats);
            LastProbe = probe;
            return Health?.Invoke(probe, cancellationToken) ??
                   new ValueTask<HealthReport>(new HealthReport(Instance!, Status, Routers.Refuses(probe.Router), Load: Load));
        }

        public readonly ConcurrentQueue<IdentityChange> IdentityChanges = new(); // renewals and lapses told

        public ValueTask IdentityChangedAsync(IdentityChange notice, CancellationToken cancellationToken)
        {
            IdentityChanges.Enqueue(notice);
            return default;
        }

        public ValueTask ActivateAsync(Activation notice, CancellationToken cancellationToken)
        {
            Relayed.Enqueue(notice);
            return default;
        }

        public ValueTask UnsubscribeAsync(Unsubscription notice, CancellationToken cancellationToken)
        {
            var routesThen = notice.Topic.Routes;
            Routers.Ended(notice.Router, notice.Route);
            if (actOnNotices && notice.Remaining == 0)
                lock (Gate)
                {
                    if (naive || Epoch == notice.Epoch) Live = false;
                }

            // Kept once acted on: a test that sees the notice sees what the provider made of it.
            Relayed.Enqueue(notice);
            Notices.Enqueue((notice, routesThen));
            return default;
        }

        public ValueTask SubscribeAsync(SubscribeRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Subscribes);
            Last = request;
            if (Instead is { } instead) return instead(request, cancellationToken);
            if (!Routers.Admits(request.Router))
            {
                request.Nack();
                return default;
            } // a router presumed dead

            if (request.Parameters is { } p && p == Refuse) return default;
            if (Status != ProviderStatus.Serving) return default;
            lock (Gate)
            {
                Epoch = request.Accept(topic, AcceptAs ?? Instance!, initial: initial, errorPolicy: Policy,
                    format: Format);
                Live = true;
            }

            Routers.Accepted(request.Router, request.Route);
            Then?.Invoke(request);
            return default;
        }

        public void Acknowledged(PublishResult result)
        {
            if (result.HasListeners) return;
            lock (Gate)
            {
                if (naive || Epoch == result.Epoch) Live = false;
            }
        }
    }

    /// <summary>A router's channel, as a provider on the far side of a partition reaches it: heartbeats never answered.</summary>
    private sealed class Partitioned(IRouterChannel inner) : IRouterChannel
    {
        public volatile bool Healed; // (the partition over: heartbeats through again)
        public RouterInstance Router => inner.Router;

        public async ValueTask<RouterBeat> HeartbeatAsync(ProviderInstance instance, RouterInstance expected,
            CancellationToken cancellationToken)
        {
            if (Healed) return await inner.HeartbeatAsync(instance, expected, cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        }

        public ValueTask DrainAsync(ProviderInstance instance, TimeSpan over, TimeSpan grace,
            CancellationToken cancellationToken)
        {
            return inner.DrainAsync(instance, over, grace, cancellationToken);
        }

        public ValueTask<MoveAnswer> MoveAsync(ProviderInstance instance, string? partition, RouteId? route,
            TimeSpan over, TimeSpan grace, string reason, CancellationToken cancellationToken)
        {
            return inner.MoveAsync(instance, partition, route, over, grace, reason, cancellationToken);
        }
    }

    /// <summary>A provider sending to the mailbox before accepting (a misuse).</summary>
    private sealed class EarlyMail : IDataProvider
    {
        public ValueTask<HealthReport> CheckHealthAsync(HealthProbe probe, CancellationToken cancellationToken)
        {
            return new ValueTask<HealthReport>(new HealthReport(ProviderInstance.Start("early", At("early")),
                ProviderStatus.Serving));
        }

        public ValueTask SubscribeAsync(SubscribeRequest request, CancellationToken cancellationToken)
        {
            request.Mailbox.Send([1]);
            return default;
        }
    }

    private sealed class Binder : IInvocationBinder
    {
        public IReadOnlyList<Type> GetParameterTypes(string methodName)
        {
            return methodName switch
            {
                "Tap.Frame" or "JS.RenderBatch" => [typeof(long), typeof(byte[])],
                "JS.BeginInvokeJS" => [typeof(long), typeof(string), typeof(string), typeof(int), typeof(long)],
                "OnRenderCompleted" => [typeof(long), typeof(string)],
                _ => throw new InvalidOperationException($"No binding for {methodName}")
            };
        }

        public Type GetReturnType(string invocationId)
        {
            return typeof(object);
        }

        public Type GetStreamItemType(string streamId)
        {
            return typeof(object);
        }
    }

    // ─────────────────────────────── transport ───────────────────────────────

    private sealed class CapturingProxy : ISingleClientProxy
    {
        public readonly List<(string Method, object?[] Args)> Sent = new();

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            lock (Sent)
            {
                Sent.Add((method, args));
            }

            return Task.CompletedTask;
        }

        public Task<T0> InvokeCoreAsync<T0>(string method, object?[] args,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class TestHub : Hub
    {
    }

    /// <summary>Stands in for ComponentHub: like CircuitClientProxy, it talks to the client through Clients.Caller.</summary>
    private sealed class CallerHub(TokenHolder holder) : Hub
    {
        public Task Bind()
        {
            return Clients.Caller.SendAsync("JS.BeginInvokeJS", 1L, "taplineBind", $"[\"{holder.Token}\"]", 0, 0L);
        }
    }

    private sealed class TokenHolder
    {
        public string Token = "";
    }

    private sealed class DuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;
        public PipeWriter Output { get; } = output;
    }

    /// <summary>A provider that declares its own initial state at step 1, and sends it on step 2 (whenever the test says).</summary>
    private sealed class StepTwoProvider(string name, Source topic, InitialState initial) : IDataProvider
    {
        public readonly TaskCompletionSource<Activation> Activated =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public readonly ProviderInstance Instance = ProviderInstance.Start(name, ProviderEndpoint.InProcess(name));

        public ValueTask SubscribeAsync(SubscribeRequest request, CancellationToken cancellationToken)
        {
            request.Accept(topic, Instance, initial: initial);
            return default;
        }

        public ValueTask ActivateAsync(Activation notice, CancellationToken cancellationToken)
        {
            Activated.TrySetResult(notice); // step 2: now it may send the initial state
            return default;
        }

        public ValueTask<HealthReport> CheckHealthAsync(HealthProbe probe, CancellationToken cancellationToken)
        {
            return new ValueTask<HealthReport>(new HealthReport(Instance, ProviderStatus.Serving));
        }
    }

    /// <summary>
    ///     A batch fold held at a gate (the router's fold taking its time), then folding as <paramref name="inner" />
    ///     does.
    /// </summary>
    private sealed class GatedFold(IBatchFold inner) : IBatchFold
    {
        public readonly ManualResetEventSlim Entered = new(), Gate = new(true);
        public int Calls;

        public void Fold(Span<byte> rows, int rowCount, int rowSize, ReadOnlySpan<byte> deltas, int deltaCount,
            int deltaSize)
        {
            Interlocked.Increment(ref Calls);
            Entered.Set();
            if (!Gate.Wait(10_000)) throw new TimeoutException("the gate was never opened");
            inner.Fold(rows, rowCount, rowSize, deltas, deltaCount, deltaSize);
        }
    }

    /// <summary>A batch fold that throws on a delta of 666 (a poisoned post), else folds as <paramref name="inner" /> does.</summary>
    private sealed class PoisonFold(IBatchFold inner) : IBatchFold
    {
        public int Calls;

        public void Fold(Span<byte> rows, int rowCount, int rowSize, ReadOnlySpan<byte> deltas, int deltaCount,
            int deltaSize)
        {
            Calls++;
            for (var i = 0; i < deltaCount; i++)
                if (BinaryPrimitives.ReadInt64LittleEndian(deltas[(i * FoldEntry.DeltaStride(deltaSize) + 4)..]) == 666)
                    throw new InvalidDataException("poisoned");
            inner.Fold(rows, rowCount, rowSize, deltas, deltaCount, deltaSize);
        }
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation(string uri)
        {
            Initialize(new Uri(new Uri(uri), "/").ToString(), uri);
        }
    }

    private sealed class DuplexStream(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            write.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return read.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return read.ReadAsync(buffer, cancellationToken);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            write.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return write.WriteAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }
    }

    public sealed record PathResult(
        double FramesPerSecond,
        double P50,
        double P99,
        double P995,
        double MaxQueueMs,
        double IntervalP99,
        double KBps,
        double AgeP50,
        double AgeP99,
        double AgeP995);

    private sealed record CadenceResult(
        double Uncapped,
        double At60,
        double At30,
        double At120,
        double Free,
        double GapMs);

    [InlineArray(488)]
    private struct ChunkPayload
    {
        private byte _first;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SimChunk
    {
        public long PublishedAt;
        public int Seq;
        public ChunkPayload Data;
    } // 500 B, as the demo's

    [InlineArray(112)]
    private struct SidPayload
    {
        private byte _first;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SimSidRow
    {
        public long PublishedAt;
        public int Seq;
        public SidPayload Data;
    } // 124 B, as a SID row

    public sealed record MediaResult(
        double SceneFps,
        double SceneAgeP50,
        double SceneAgeP995,
        double ChunksDelivered,
        double ChunksOnTime,
        double ChunkLateP995,
        double LinkUse,
        double MaxQueueMs,
        int LowBudgetSeconds = 0);
}