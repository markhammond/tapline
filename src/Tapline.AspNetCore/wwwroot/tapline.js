// tapline.js — Tapline's browser client (Tapline.AspNetCore), whatever carries its frames: a connection attached with
// tapline.attach(connection) — on Blazor Server, the circuit's own, by BlazorRealtime's startBlazor.
// Decodes Tap.Frame payloads, keeps replicas of tables (sequence + checksum verified) and the latest value per
// stream, and acks frames (cumulative, with heartbeat echo). Domain-agnostic: views decode key/row bytes themselves.
//
//   <script src="_framework/blazor.web.js" autostart="false"></script>
//   <script type="module">
//     import { tapline } from './_content/Tapline.AspNetCore/tapline.js';
//     import { startBlazor } from './_content/BlazorRealtime/blazor-realtime.js';
//     tapline.onServiceUp = service => { /* a service this page waited on is back: subscribe again */ };
//     startBlazor(Blazor, tapline);
//   </script>
//
// Subscribing is two-phase: the server binds a tap and hands its binding to the page (nothing streams yet); the view
// then calls tapline.subscribe(binding, onState, onError), which registers the handlers *before* sending Tap.Activate —
// so the first record (a snapshot) always has somewhere to go and nothing needs buffering. state is a Table replica, or
// a Uint8Array for last-value streams.
//
// Reconnects: binding again, the page says only the last frame it received (Tap.Resume); the router knows what each
// subscription sent in which frame, and goes on from exactly there — a table with its next delta, a value only if it
// changed, a message stream with the next message the page didn't get. Nothing is tracked per subscription here.
//
// Messages: nothing arrives unsolicited — an alert or an event is a message topic the page subscribed to like any other
// (tapline.subscribe(binding, onState, onError, onMessage)), each message handed over in order, once, as
// (bytes, id, { seq, gap, mailbox }); so is a message to one subscription's mailbox (mailbox: true). A message topic
// starts at its next message: it has no snapshot.
//
// Errors are terminal for a subscription: onError(error, state, id) gets { code, version, payload, local } and the last
// state (for a table with a degrade policy: the degraded replica, table.degraded === true); the tap is then forgotten
// and the app resubscribes (in Blazor, the component binds a new tap when Tap.Completion reports the fault).
//   server fault   → [degraded snapshot] + Error record (local: false)
//   bad delta      → Tap.Resync once; the next snapshot heals it
//   bad snapshot   → a resync can't help: Tap.Close(code) to the server, onError (local: true)
//
// Out of band: large records (a large message too) arrive as 47-byte handles. Transparent handles are fetched over HTTP
// (GET {blobBase}/{blobId}/{outletId}/{tag}), checked against their SHA-256 content address when the source made them
// content-addressed (the handle says so; otherwise the id is unique, and there's nothing to check), applied in order, and
// confirmed with Tap.Fetched(0, tapId, ticket) — the server holds that tap meanwhile, so nothing is buffered
// here. A fetch overtaken by a newer record for the same tap, or by a reconnect, is neither applied nor confirmed. Reference
// handles are handed to the app as BlobRef ({ id, length, url, bytes() }), as a value's state.
//
// Service down and up: a subscription through a data provider carries, in its binding, the service's name
// (binding.service), the router's transient id for the provider instance holding it — its node id (binding.nodeId),
// nothing of the instance itself — and, if the provider put it in one of the service's partitions, the partition's path
// as ids of the router's run (binding.partition: hex segments, never its names). Every Service record carries its
// change's correlation id, the one the router logged it with. When the router deems a node dead (silent, restarted,
// withdrawn) it closes those subscriptions and sends a Service record naming it: each subscription through it fails
// with FaultCode.ServiceDown, tapline.onServiceDown(nodeId, correlationId) is called, and a later subscribe with that id
// fails at once. The client then waits: once the service is up again — as the router decides: enough instances, or a
// grace period, so the waiting clients don't all land on the first one back — a Service record names the service (no
// node), and tapline.onServiceUp(service, correlationId) is called: subscribe again then (step 1). Also sent after a
// subscribe found the service unavailable. A draining node (blue/green: its replacement takes the new subscriptions)
// isn't down: it moves its clients one at a time. Make before break: a client is told "draining" (its id, and a grace)
// — nothing fails; tapline.onServiceDraining(nodeId, graceMs, correlationId) is called, and the app subscribes again
// (another node) and lets the old subscription go once the new one streams — tapline.replace(oldId, newBinding,
// onState) does the switch: the old keeps streaming until the new one's first state, then is dropped (both carry their
// own tap ids meanwhile, so nothing mixes). Whatever it still holds there when the grace runs out is closed: a "drained"
// record — those subscriptions fail with FaultCode.ServiceDrained and tapline.onServiceDrained(nodeId, correlationId) is
// called (subscribe again now). With no grace, that is all a client gets (break before make). Should no node take the
// new subscription, step 1 finds the service unavailable, and the up notice follows.
//
// A move is the same for one subscription, its node not retiring (a rebalance): tapline.onMove(tapId, graceMs,
// correlationId, nodeId) is called; the app subscribes again (the router places it by load) and replaces the old one.
// Still held when the grace runs out, it fails with FaultCode.Moved (subscribe again).
//
// A data provider's state: degraded (late, or unhealthy — not gone: its subscriptions shown their stale data, as the
// provider's policy makes it on the router, and held), then serving again (they stream on), draining, down. A node is
// degraded whole, or a scope there: a service path as the page sees it (binding.service, then binding.partition's ids),
// covering the subscriptions at or under it. A Service record says each change, ahead of the records it applies to;
// every state and message handed over carries the provider's state then ({ provider }, tapline.providerOf(id)), and
// tapline.onProviderState(nodeId, state, { scope, correlationId }) is called (scope null: the node).
// A stale message is flagged ({ stale: true }): not a new one. The router itself unreachable — the connection closed,
// timed out on its own heartbeat traffic — tapline.onReachability(false), and true once bound again.
//
// The router's own state — serving; degraded (its host's health checks failing, and why); or draining (going away
// within a grace: move to another router before it ends, make before break) — and the change that made it: a
// correlation id (a UUIDv7), the one the router logged it with. A RouterChange record says it, behind a heartbeat, until
// the page has echoed one it came with (and again on each new connection). tapline.routerHealth is the latest ({ state,
// correlationId, reason, drainsBy }: drainsBy by this page's clock, Date.now() ms); tapline.onRouterHealth(health) is
// called on each change — once per correlation id.
//
// Stale data while the router is out of reach: the router applies a subscription's stale-data policy (the provider's)
// on the state it holds — the authority. Its binding carries the policy too (binding.stale: { kind, state, module }),
// for the page to apply itself only while it can't hear the router: when the connection closes, each subscription with
// one that has received something is handed its stale view — a copy (the replica itself untouched) — with
// { provider: 'unreachable', stale: true } (a subscription bound directly: provider undefined): a table's rows, a value,
// or the last message again (to onMessage, flagged stale), passed through the provider's function (a WebAssembly module,
// fetched from {moduleBase}/{id} at subscribe and checked against its id; it imports nothing), or the fixed state, or as
// they were. Bound again, each is handed its real state (a table, a value) before the router goes on. A function the
// router alone can run (no module) isn't in the binding: nothing is shown then — onReachability says why. Watches: the
// router's only.
//
// Ended by the router: an Ended record (resent until acknowledged) names a range of tap ids and why; each subscription in
// it fails (onError, from the server), then tapline.onEnded(code, fromTapId, toTapId) is called. A page that acknowledges
// nothing for the router's stall timeout (30 s by default) while there was something for it — frozen, or cut off — has
// all its subscriptions ended (FaultCode.Stalled; those bound since stand). And after a reconnect, the activations the
// page sends again reconcile what it holds with what the router does: one that ended while it was away — its error lost
// with the connection, or ended without one — is answered with its own Ended record (why, or FaultCode.Gone).
//
// Refused, saying why: when a provider refuses a subscription of this page's with a reason — or the router does, for a
// service that requires a sign-in — the router sends a Refused record (resent until acknowledged): the service, the
// reason, its kind (RefusalKind: Unauthenticated — sign in, then subscribe again; Forbidden — not allowed; Refused —
// the provider's reason; Elsewhere — every instance of the service said another serves it, none did), and the refusal's correlation id, the one the provider and the router logged it with. The
// page logs it (console.warn, once per id) and calls tapline.onRefused({ correlationId, service, reason, kind }):
// search any of the three logs for the id and find the other two.
//
// Rendezvous: to know when a change it asked for is in front of it, the page makes a correlation id
// (tapline.correlation(): a UUIDv7, so the id the router hands back says when it was made — tapline.correlationTime(id)
// — and the page can tell how long the change took to reach it, by its own clock), awaits it (tapline.rendezvous(id) — before sending the request, so nothing can come first) and
// sends it with its request; whoever applies the change hands the router the id and the sequence the change made
// (Mailbox.TryRendezvous / Tap.TryRendezvous), and the router delivers it through the subscription's mailbox strictly
// after the state that includes the change — applied, an out-of-band record fetched; across a reconnect too. It resolves
// with { tapId, seq, missed } — missed: a message topic's message this subscription was never sent (before it subscribed,
// or skipped). Given { tapId }, it rejects if that subscription ends first; { signal } aborts it. Sequence numbers stay the
// router's: the page never compares them. One that arrives unawaited goes to tapline.onRendezvous(id, info).
//
// Sync: tapline.sync(tapId) asks the router whether the page is up to date on a subscription — a rendezvous the router
// itself makes, after the subscription's state as it stood when the ask arrived: prompt for a quiet subscription,
// delayed by whatever holds its changes back (a rate cap, an interval). How a view tells quiet from stale.
// A router says the control messages it takes on each connection (its RouterChange's controls): sync() fails at once
// against one that doesn't take Tap.Sync, rather than waiting out its timeout.
//
// Watches: a Group record is a coherent cut of several members; they are all applied, then the subscriber is notified
// once with { members: [Table | Uint8Array | BlobRef], degraded }.
//
// Signing in: tapline.signIn(realm, module, message, { next, renewal, signal }) sends the first message of a sign-in
// to the realm's authentication provider, through the router, which relays it as bytes it doesn't read; a module of
// several messages answers each of the provider's with next(message, signal) → its next (a Uint8Array). It resolves
// with { accepted, identity, ticket, reason }: signed in, the connection is identity ({ realm, userId, sessionId, module,
// insecure, authenticatedAt, expiresAt, claims }) — tapline.identity, tapline.onIdentity(identity) — and the ticket is
// kept, to resume with (tapline.resume()): bound to another outlet (its session gone), the page presents it again by
// itself (tapline.resumeSignIn). tapline.signInContext(realm) is what a module binds its proof to (OPAQUE's context). A
// renewal is of the same user: subscriptions carry on. One sign-in at a time: another begun ends the first (refused).
// Ahead of the session's expiry the router warns, once: tapline.onSessionExpiring({ correlationId, sessionId, expiresAt,
// renewal, reason }) — renewal 'silent' (the page renews with its ticket by itself: tapline.renewSilently),
// 'reauthenticate' (ask the user again: signIn(…, { renewal: true })) or 'fixed' (it ends then: reason says why). At
// expiry without a renewal, tapline.onSessionEnded(notice) and onIdentity(null): subscriptions to services that require a
// sign-in end (FaultCode.SessionExpired). A renewal answered elsewhere resolves { accepted: false, redirectTo, ticket }
// and calls tapline.onRedirect(router, ticket): the page decides — a router it knows, or none.
//
// Passkeys (Tapline.Authentication's passkey modules): tapline.signInWithPasskey(realm, { userId, renewal, signal })
// asks the realm for a challenge, has the browser sign it (navigator.credentials.get) and sends the assertion back;
// tapline.registerPasskey(realm, { enrollment, signal }) registers one (navigator.credentials.create) — for the user
// signed in, or for whoever the backend's enrollment token names — and signs in by it. Both resolve as signIn does.
// (Safari wants a user's gesture close before the browser's prompt: call them from a click.)
//
// Transfer rate: tapline.transferRate is the receive rate in bytes per second, exponentially smoothed (2 s time
// constant), and tapline.bytesReceived the total — Tap.Frame payloads as the page sees them, i.e. after the browser has
// inflated any per-message deflate (the wire may carry less) and without Blazor's own messages or HTTP fetches.

const HEARTBEAT = 0x01, VALUE = 0x03, TABLE = 0x04, ERROR = 0x05, GROUP = 0x06, OOB = 0x07, SERVICE = 0x08,
    TOPIC_MESSAGE = 0x09, ENDED = 0x0a, RENDEZVOUS = 0x0b, REFUSED = 0x0c, ROUTER_CHANGE = 0x0d, AUTH = 0x0e, SESSION = 0x0f;
const AUTH_MAX = 16 * 1024, AUTH_BEGIN = 1, AUTH_CONTINUE = 2, AUTH_CANCEL = 3, AUTH_RENEWAL = 0x01, AUTH_HEADER = 14,
    AUTH_CONTINUED = 0, AUTH_ACCEPTED = 1, AUTH_REDIRECTED = 3, SESSION_HEADER = 45;
const RENEWAL_POLICIES = ['silent', 'reauthenticate', 'fixed'];
const FLAG_MISSED = 0x01, RENDEZVOUS_HEADER = 15;
const FLAG_GAP = 0x01, FLAG_MAILBOX = 0x02, MESSAGE_HEADER = 18;
const SERVICE_DOWN = 0, SERVICE_UP = 1, SERVICE_DRAINED = 2, SERVICE_DRAINING = 3, SERVICE_DEGRADED = 4,
    SERVICE_RECOVERED = 5, SERVICE_MOVE = 6;
const ROUTER_CHANGE_HEADER = 22, ROUTER_CHANGE_CONTROLS = 18, ROUTER_DEGRADED = 1, ROUTER_DRAINING = 2,
    ROUTER_STATES = ['serving', 'degraded', 'draining'];
const FLAG_STALE = 0x04;
const CONTROL_SYNC = 7;                                     // (ControlKind: a RouterChange's controls, a bit each)
const FLAG_RESET = 0x01, FLAG_DEGRADED = 0x02, OP_DELETE = 1, OOB_REFERENCE = 1, OOB_CONTENT_ADDRESSED = 0x80,
    OOB_MODE = 0x7f;
const STALE_FUNCTION = 0, STALE_STATIC = 1, STALE_REPLAY = 2, ENTRY_PRESENT = 1;
const FETCH_ATTEMPTS = 3;
const RATE_TAU_MS = 2000;                                    // transfer rate smoothing: exponential, this time constant

/** Same values as C# Tapline.FaultCode. */
export const FaultCode = Object.freeze({
    None: 0,
    Unknown: 1,
    VersionMismatch: 2,
    ChecksumMismatch: 3,
    FoldFailed: 4,
    SourceFailed: 5,
    ServiceDown: 6,
    ServiceDrained: 7,
    Overflowed: 8,
    Stalled: 9,
    Gone: 10,
    NoInitialState: 11,
    IdentityChanged: 12,
    SessionExpired: 13,
    Moved: 14
});
/** Same values as C# Tapline.RefusalKind: why a subscription was refused (tapline.onRefused's kind). */
export const RefusalKind = Object.freeze({
    Refused: 0,
    Unauthenticated: 1,
    Forbidden: 2,
    Elsewhere: 3
});
/** Same values as C# Tapline.StaleData: what a subscription's binding.stale.kind says to show while the router is out of reach. */
export const StaleData = Object.freeze({Function: 0, Static: 1, ReplayLast: 2});
const TWO32 = 4294967296;

function mix(h) {
    h ^= h >>> 16;
    h = Math.imul(h, 0x85EBCA6B);
    h ^= h >>> 13;
    h = Math.imul(h, 0xC2B2AE35);
    h ^= h >>> 16;
    return h >>> 0;
}

function absorb(h, b, o, n) {
    let i = 0;
    for (; i + 4 <= n; i += 4) h = mix(h ^ (b[o + i] | b[o + i + 1] << 8 | b[o + i + 2] << 16 | b[o + i + 3] << 24));
    if (i < n) {
        let w = 0;
        for (let k = 0; i + k < n; k++) w |= b[o + i + k] << (8 * k);
        h = mix(h ^ w);
    }
    return h;
}

/** Same as C# Tapline.RowHash.Of(key, row). */
export function rowHash(keyBytes, keyOffset, keySize, rowBytes, rowOffset, rowSize) {
    return absorb(absorb(mix((0x9E3779B9 + keySize + rowSize) >>> 0), keyBytes, keyOffset, keySize), rowBytes, rowOffset, rowSize);
}

const i64 = (dv, o) => dv.getInt32(o + 4, true) * TWO32 + dv.getUint32(o, true);

function latin1(b, o, n) {
    let s = '';
    for (let i = 0; i < n; i++) s += String.fromCharCode(b[o + i]);
    return s;
}

const fromBase64 = s => {
    const b = atob(s ?? ''), out = new Uint8Array(b.length);
    for (let i = 0; i < b.length; i++) out[i] = b.charCodeAt(i);
    return out;
};

/**
 * Runs a stale-data module (compiled) on `entries` — count × (u32 flags | key | row), flags bit0 = present — in place, in
 * a fresh instance: its buffer(size) says where they go in its memory, degrade(ptr, count, keySize, rowSize) rewrites them.
 */
function degradeWith(module, entries, count, keySize, rowSize) {
    const {memory, buffer, degrade} = new WebAssembly.Instance(module, {}).exports;
    const at = buffer(entries.length) >>> 0;
    new Uint8Array(memory.buffer, at, entries.length).set(entries);
    degrade(at, count, keySize, rowSize);
    entries.set(new Uint8Array(memory.buffer, at, entries.length));   // (its memory may have grown: viewed again)
}

/** Replica of a server Table<TKey, TRow>: rows keyed by their key bytes. */
export class Table {
    constructor() {
        this.rows = new Map();       // latin1(key bytes) → { key: Uint8Array, row: Uint8Array }
        this.keySize = 0;
        this.rowSize = 0;
        this.seq = -1;
        this.checksum = 0;
        this.verified = false;
        this.degraded = false;       // last snapshot was the server's degraded form (an error follows)
        this.awaitingReset = true;   // until the first snapshot, and after any failure
        this.resyncSent = false;
        this.lastWasReset = false;
    }

    get size() {
        return this.rows.size;
    }

    /** A stale view (not a replica: nothing applies to it): `rows` ([key, row] byte arrays) of this one's shape, flagged degraded. */
    staleView(rows) {
        const t = new Table();
        t.keySize = this.keySize;
        t.rowSize = this.rowSize;
        t.seq = this.seq;
        t.degraded = true;
        t.verified = true;
        t.awaitingReset = true;
        let sum = 0;
        for (const [key, row] of rows) {
            t.rows.set(latin1(key, 0, key.length), {key, row});
            sum = (sum + rowHash(key, 0, key.length, row, 0, row.length)) >>> 0;
        }
        t.checksum = sum;
        return t;
    }

    /** Applies the Table record at offset o. False on a sequence gap or checksum mismatch (the replica then awaits a reset). */
    apply(bytes, dv, o) {
        const flags = bytes[o + 5], reset = (flags & FLAG_RESET) !== 0;
        this.lastWasReset = reset;
        const fromSeq = i64(dv, o + 6), toSeq = i64(dv, o + 14);
        const expected = dv.getUint32(o + 22, true), count = dv.getUint32(o + 26, true);
        const K = dv.getUint16(o + 30, true), R = dv.getUint16(o + 32, true);

        if (reset) {
            this.rows.clear();
            this.checksum = 0;
            this.keySize = K;
            this.rowSize = R;
            this.awaitingReset = false;
            this.resyncSent = false;
            this.degraded = (flags & FLAG_DEGRADED) !== 0;
        } else if (this.awaitingReset || fromSeq !== this.seq + 1) {
            this.awaitingReset = true;
            this.verified = false;
            return false;
        }

        let sum = this.checksum;
        for (let i = 0, p = o + 34; i < count; i++, p += 1 + K + R) {
            const k = p + 1, r = k + K;
            const id = latin1(bytes, k, K);
            const entry = this.rows.get(id);
            if (entry) sum = (sum - rowHash(entry.key, 0, K, entry.row, 0, R)) >>> 0;
            if (bytes[p] === OP_DELETE) {
                if (entry) this.rows.delete(id);
            } else {
                if (entry) entry.row.set(bytes.subarray(r, r + R));
                else this.rows.set(id, {key: bytes.slice(k, k + K), row: bytes.slice(r, r + R)});
                sum = (sum + rowHash(bytes, k, K, bytes, r, R)) >>> 0;
            }
        }
        this.checksum = sum;
        this.seq = toSeq;
        this.verified = sum === expected;
        if (!this.verified) this.awaitingReset = true;
        return this.verified;
    }
}

const hex = (b, o, n) => {
    let s = '';
    for (let i = 0; i < n; i++) s += b[o + i].toString(16).padStart(2, '0');
    return s;
};
/** A GUID's 16 bytes (RFC 4122 order), as .NET writes it in JSON: "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx". */
const guid = (b, o) => {
    const h = hex(b, o, 16);
    return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
};
const sameBytes = (a, o, b) => {
    for (let i = 0; i < b.length; i++) if (a[o + i] !== b[i]) return false;
    return true;
};

/** True if `bytes` hash to the content address `idHex` (first 16 bytes of SHA-256). Skipped without WebCrypto (insecure context). */
async function matchesId(bytes, idHex) {
    const subtle = globalThis.crypto?.subtle;
    if (!subtle) return true;
    return hex(new Uint8Array(await subtle.digest('SHA-256', bytes)), 0, 16) === idHex;
}

/** An out-of-band payload handed to the app: fetch it when (or if) you need it. */
export class BlobRef {
    constructor(client, id, tag, length, contentAddressed = false) {
        this.id = id;
        this.length = length;
        this.contentAddressed = contentAddressed;     // the id is the bytes' SHA-256 (else a unique id)
        this.url = `${client.blobBase}/${id}/${client.outlet?.id}/${tag}`;
        this._client = client;
    }

    /** Fetches the bytes — and verifies them, if content-addressed. */
    async bytes() {
        const b = await this._client.fetchBlob(this.url);
        if (this.contentAddressed && !(await matchesId(b, this.id))) throw new Error(`tapline: blob ${this.id} failed verification`);
        return b;
    }
}

export class TaplineClient {
    constructor() {
        this.tables = new Map();      // tapId → Table
        this.values = new Map();      // tapId → Uint8Array | BlobRef (latest)
        this.groups = new Map();      // tapId → { members: [Table | Uint8Array], degraded, resyncSent }
        this.watchers = new Map();    // tapId → Set<{ onState, onError, onMessage }>
        this.confirmed = new Map();   // tapId → the ticket of the last out-of-band record it confirmed (said again on a rebind)
        this.serviceOf = new Map();   // tapId → its binding's nodeId (subscriptions through a data provider)
        this.pathOf = new Map();      // tapId → where it is, as the router shows it: its service, then its partition's ids
        this.servicesDown = new Set();// node ids the server said are down
        this.servicesDrained = new Set(); // …and drained (alive, letting its clients go)
        this.degradedScopes = new Map(); // nodeId → Set of service paths degraded there (as this page sees them)
        this.refusalsSeen = new Set();    // refusals' correlation ids, logged (a resent record: once)
        this.providerStates = new Map(); // nodeId → 'degraded' | 'draining' | 'down' | 'drained' (absent: serving) — the node's own
        this.reachable = false;       // the router: connected (a bind seen, the connection not closed since)
        this.stalePolicies = new Map(); // tapId → { kind, state, module } (its binding's: what to show while the router is out of reach)
        this.lastMessages = new Map();  // tapId → { bytes, seq } — a message stream's last, for a policy that shows it again
        this.modules = new Map();       // module id → Promise<WebAssembly.Module> (compiled once)
        this.compiled = new Map();      // module id → WebAssembly.Module, once fetched and checked
        this.shownStale = new Set();    // tapIds shown their stale view while the router was out of reach
        this.rendezvousWaits = new Map(); // correlation (hex) → { resolve, reject, tapId } — rendezvous awaited
        this.syncs = 0;                   // sync ids handed out (Tap.Sync): the next, one more
        this.onRendezvous = null;     // (correlation hex, { tapId, seq, missed }) => void — one nobody awaited
        this.blobBase = '_tapline/blob';                       // relative to the page's base href (MapTapline)
        this.moduleBase = '_tapline/module';
        this.fetchBlob = async url => {
            const r = await fetch(url, {credentials: 'same-origin'});
            if (!r.ok) throw new Error(`tapline: ${url} → ${r.status}`);
            return new Uint8Array(await r.arrayBuffer());
        };
        this.retryDelayMs = 250;
        this.inflight = new Set();    // pending out-of-band fetches (see idle())
        this.fetchFailures = new Map();
        this.generations = new Map(); // tapId → records seen (a fetch applies only if none arrived since)
        this.epoch = 0;               // bumped on every (re)bind: earlier fetches are stale
        this.outlet = null;           // { id, nonce } — delivered by the server's taplineBind call
        this.connection = null;
        this.heartbeat = {seq: 0, serverMicros: 0, receivedAt: 0};
        this.routerHealth = {state: 'serving', correlationId: null, reason: null, drainsBy: null};   // as the heartbeats carry it
        this.routerCorrelation = new Uint8Array(16);           // its correlation id's bytes: a change, when they differ
        this.routerControls = 0;                                // the control messages it takes, a bit each: 0, not said yet
        this.lastFrame = 0;           // last frame seq received (acked again on bind: an ack sent before it had nowhere to go)
        this.bytesReceived = 0;       // Tap.Frame payload bytes received in total
        this.rate = 0;                // bytes/s, exponentially weighted, as of rateAt (see transferRate)
        this.rateAt = 0;
        this.onHeartbeat = null;      // ({ seq, serverMicros, receivedAt }) => void
        this.onRouterHealth = null;   // ({ state, correlationId, reason, drainsBy }) => void — the router changed state: 'serving', 'degraded' (why), 'draining' (gone by drainsBy: move)
        this.onServiceDown = null;    // (nodeId, correlationId) => void — after its subscriptions' onError
        this.onServiceUp = null;      // (service, correlationId) => void — a service this client waited on is up: subscribe again
        this.onServiceDrained = null; // (nodeId, correlationId) => void — its node draining let these go: subscribe again now
        this.onServiceDraining = null;// (nodeId, graceMs, correlationId) => void — move: subscribe again, then let the old go (replace)
        this.onMove = null;           // (tapId, graceMs, correlationId, nodeId) => void — move this one (a rebalance): subscribe again, then replace
        this.movedBy = new Map();     // tapId → the move it was told of (correlation id): told once
        this.onEnded = null;          // (code, fromTapId, toTapId) => void — after those subscriptions' onError: the router ended them
        this.onRefused = null;        // ({ correlationId, service, reason, kind }) => void — a subscription of ours refused (logged first)
        this.onProviderState = null;  // (nodeId, state, { scope, correlationId }) => void — 'degraded' (its subscriptions show stale data, held), 'serving' again, 'draining', 'down', 'drained'
        this.onReachability = null;   // (reachable) => void — the connection to the router closed (timed out, dropped), or bound again
        this.onError = (error, _state, id) => console.warn(`tapline: tap ${id} failed (code ${error.code})`);   // fallback handler
        // The connection's own timeout (a SignalR HubConnection's serverTimeoutInMilliseconds: on Blazor Server, the
        // circuit's), fed by the router's frames (a heartbeat at least every second): a dead connection noticed in seconds,
        // the transport then reconnecting. It follows the path: four times the longest recent gap between frames
        // (halving every 30 s), at least the floor — never below what a slow path needs — at most SignalR's default.
        this.serverTimeoutFloorMs = 10000;
        this.serverTimeoutCapMs = 30000;
        this.keepAliveMs = 3000;      // the client's SignalR ping, when it has sent nothing (its acks keep it quiet)
        this.now = () => performance.now();
        this.maxGap = 0;              // the longest recent gap between frames, ms (decaying)
        this.lastFrameAt = 0;
        this.firstFrameAt = 0;        // on this connection: the timeout follows the path once 5 s of it are seen
        this.receivedAtWall = 0;      // Date.now() of the last frame (staleMs: across a sleep too)
        this.identity = null;         // who the connection is, as its last sign-in left it (null: anonymous)
        this.ticket = null;           // that sign-in's ticket (Uint8Array), to resume with, and its realm
        this.ticketRealm = null;
        this.resumeSignIn = true;     // bound to another outlet (the session gone), present the ticket again
        this.onIdentity = null;       // (identity | null) => void — signed in, renewed, or lost with the session
        this.exchanges = 0;           // sign-ins begun (each its number)
        this.auth = null;             // the sign-in under way: { exchange, round, next, resolve, reject, abort }
        this.renewSilently = true;    // warned, its policy silent: renew with the ticket
        this.sessionNoticesSeen = new Set(); // the router's session notices' correlation ids (a resent one: once)
        this.onSessionExpiring = null;// ({ correlationId, sessionId, expiresAt, renewal, reason }) => void
        this.onSessionEnded = null;   // (the same) => void — lapsed: anonymous now
        this.onRedirect = null;       // (router, ticket) => void — a renewal answered elsewhere
    }

    /** Receive rate in bytes per second, smoothed: each byte counts e^(−age/2 s)/2 s, so a steady stream reads its true rate. */
    get transferRate() {
        return this.rate * Math.exp(-(performance.now() - this.rateAt) / RATE_TAU_MS);
    }

    /** How long since the router was last heard from (ms; Infinity before): data shown is at least this old. */
    get staleMs() {
        return this.receivedAtWall ? Date.now() - this.receivedAtWall : Infinity;
    }

    /**
     * Called by the server on every (re)connect; re-activates taps in case an activation was lost with the old connection,
     * and acks frames that raced ahead of it (the server holds its next heartbeat until the last one is echoed).
     */
    bind(id, nonce) {
        const another = this.outlet !== null && (this.outlet.id !== id || this.outlet.nonce !== nonce);
        this.outlet = {id, nonce};
        if (another) this.#sessionLost();
        this.epoch++;
        this.#reachability(true);
        for (const [tapId, ticket] of this.confirmed) this.#send('Tap.Fetched', 0, tapId, ticket);   // one sent as the connection fell may be lost
        this.confirmed.clear();
        this.#send('Tap.Resume', this.lastFrame, this.heartbeat.seq);   // the last frame received: every subscription goes on from there
        for (const tapId of this.watchers.keys()) this.#send('Tap.Activate', tapId);   // (an activation lost in flight; duplicates are ignored)
        if (another && this.resumeSignIn && this.ticket) this.resume().catch(() => {});   // (its end: onIdentity)
    }

    /** What binds a sign-in to `realm` to this connection, as the router gives it to the provider: `tapline/1 {realm} {outletId} {nonce}`. */
    signInContext(realm) {
        return this.outlet ? new TextEncoder().encode(`tapline/1 ${realm} ${this.outlet.id} ${this.outlet.nonce}`) : null;
    }

    /**
     * Signs in to `realm` by `module` (see "Signing in" above): `message` (a Uint8Array, or a string as UTF-8) to the
     * realm's authentication provider; `next(message, signal)` answers each of its messages, for a module of several.
     * Resolves with { accepted, identity, ticket, reason }; `signal` aborts it (rejects).
     */
    signIn(realm, module, message, {next = null, renewal = false, signal = null} = {}) {
        const enc = new TextEncoder(), r = enc.encode(realm), m = enc.encode(module);
        const payload = typeof message === 'string' ? enc.encode(message) : message ?? new Uint8Array(0);
        if (!r.length || r.length > 255 || !m.length || m.length > 255) throw new Error('tapline: a realm and a module are 1 to 255 bytes');
        if (8 + r.length + m.length + payload.length > AUTH_MAX) throw new Error('tapline: a sign-in message is at most 16 KB');
        signal?.throwIfAborted();
        const exchange = (this.exchanges = (this.exchanges + 1) >>> 0) || (this.exchanges = 1);
        const b = new Uint8Array(8 + r.length + m.length + payload.length), dv = new DataView(b.buffer);
        b[0] = AUTH_BEGIN;
        dv.setUint32(1, exchange, true);
        b[5] = renewal ? AUTH_RENEWAL : 0;
        b[6] = r.length;
        b.set(r, 7);
        b[7 + r.length] = m.length;
        b.set(m, 8 + r.length);
        b.set(payload, 8 + r.length + m.length);
        this.#endAuth({accepted: false, identity: null, ticket: null, reason: 'Another sign-in began.'});
        return new Promise((resolve, reject) => {
            const abort = new AbortController();
            const wait = {exchange, round: 0, next, resolve, reject, abort};
            this.auth = wait;
            signal?.addEventListener('abort', () => {
                if (this.auth !== wait) return;
                this.auth = null;
                abort.abort(signal.reason);
                this.#send('Tap.Auth', authCancel(exchange));
                reject(signal.reason);
            }, {once: true});
            this.#send('Tap.Auth', b);
        });
    }

    /** Signs in to `realm` with a passkey (module `passkey`): any of the site's the browser offers, or `userId`'s. */
    signInWithPasskey(realm, {userId = null, renewal = false, signal = null, credentials = globalThis.navigator?.credentials} = {}) {
        return this.signIn(realm, 'passkey', userId ? JSON.stringify({userId}) : new Uint8Array(0), {
            renewal, signal, next: async (message, abort) => {
                const o = JSON.parse(new TextDecoder().decode(message));
                const credential = await credentials.get({
                    publicKey: {
                        challenge: fromBase64Url(o.challenge), rpId: o.rpId, timeout: o.timeout,
                        userVerification: o.userVerification, allowCredentials: passkeyList(o.allowCredentials),
                    }, signal: abort,
                });
                const r = credential.response;
                return JSON.stringify({
                    id: toBase64Url(credential.rawId), clientDataJSON: toBase64Url(r.clientDataJSON),
                    authenticatorData: toBase64Url(r.authenticatorData), signature: toBase64Url(r.signature),
                    userHandle: r.userHandle ? toBase64Url(r.userHandle) : null,
                });
            },
        });
    }

    /** Registers a passkey in `realm` (module `passkey-register`) — for the user signed in, or `enrollment`'s — and signs in by it. */
    registerPasskey(realm, {enrollment = null, signal = null, credentials = globalThis.navigator?.credentials} = {}) {
        return this.signIn(realm, 'passkey-register', enrollment ?? new Uint8Array(0), {
            signal, next: async (message, abort) => {
                const o = JSON.parse(new TextDecoder().decode(message));
                const credential = await credentials.create({
                    publicKey: {
                        challenge: fromBase64Url(o.challenge), rp: o.rp, user: {...o.user, id: fromBase64Url(o.user.id)},
                        pubKeyCredParams: o.pubKeyCredParams, timeout: o.timeout, attestation: o.attestation,
                        authenticatorSelection: o.authenticatorSelection, excludeCredentials: passkeyList(o.excludeCredentials),
                    }, signal: abort,
                });
                const r = credential.response;
                return JSON.stringify({
                    id: toBase64Url(credential.rawId), clientDataJSON: toBase64Url(r.clientDataJSON),
                    attestationObject: toBase64Url(r.attestationObject), transports: r.getTransports?.() ?? [],
                });
            },
        });
    }

    /** Presents the ticket of the last sign-in (module `ticket`) to its realm: on a new session, or to renew where allowed. */
    resume({renewal = false, signal = null} = {}) {
        if (!this.ticket || !this.ticketRealm) return Promise.resolve({accepted: false, identity: null, ticket: null, reason: 'No ticket to resume with.'});
        return this.signIn(this.ticketRealm, 'ticket', this.ticket, {renewal, signal});
    }

    #endAuth(result) {
        const wait = this.auth;
        if (!wait) return;
        this.auth = null;
        wait.abort.abort();
        wait.resolve(result);
    }

    #sessionLost() {
        this.#endAuth({accepted: false, identity: null, ticket: null, reason: 'The session was lost.'});
        if (this.identity === null) return;
        this.identity = null;
        this.onIdentity?.(null);
    }

    /** The router's answer to a sign-in's message: a continue for the round awaited, the end of the sign-in awaited — each once. */
    #applyAuth(frame, dv, o, textLen, payloadLen) {
        const status = frame[o + 1], exchange = dv.getUint32(o + 2, true), round = dv.getUint16(o + 6, true);
        const wait = this.auth;
        if (!wait || wait.exchange !== exchange) return;                  // another's, or said again
        const text = new TextDecoder().decode(frame.subarray(o + 10, o + 10 + textLen));
        const payload = frame.slice(o + AUTH_HEADER + textLen, o + AUTH_HEADER + textLen + payloadLen);
        if (status === AUTH_CONTINUED) {
            if (round !== wait.round) return;
            wait.round++;
            if (!wait.next) {
                this.#send('Tap.Auth', authCancel(exchange));
                this.#endAuth({accepted: false, identity: null, ticket: null, reason: 'The provider asked for more than this sign-in says.'});
                return;
            }
            Promise.resolve().then(() => wait.next(payload, wait.abort.signal)).then(reply => {
                if (this.auth !== wait) return;
                const body = typeof reply === 'string' ? new TextEncoder().encode(reply) : reply;
                const c = new Uint8Array(5 + body.length);
                c[0] = AUTH_CONTINUE;
                new DataView(c.buffer).setUint32(1, exchange, true);
                c.set(body, 5);
                this.#send('Tap.Auth', c);
            }, error => {
                if (this.auth !== wait) return;
                this.auth = null;
                this.#send('Tap.Auth', authCancel(exchange));
                wait.reject(error);
            });
        } else if (status === AUTH_ACCEPTED) {
            let identity;
            try {
                identity = JSON.parse(text);
            } catch {
                this.#endAuth({accepted: false, identity: null, ticket: null, reason: "The router's account of the identity is unreadable."});
                return;
            }
            this.identity = identity;
            this.ticket = payload;
            this.ticketRealm = identity.realm;
            this.#endAuth({accepted: true, identity, ticket: payload, reason: null});
            this.onIdentity?.(identity);
        } else if (status === AUTH_REDIRECTED) {
            this.#endAuth({accepted: false, identity: null, ticket: payload, reason: null, redirectTo: text});
            this.onRedirect?.(text, payload);
        } else this.#endAuth({accepted: false, identity: null, ticket: null, reason: text});
    }

    /** The router's word on the session: a warning ahead of its expiry (renewed here if silent), or its end — each once, for the session and expiry held. */
    #applySession(frame, dv, o, reasonLen) {
        const correlationId = guid(frame, o + 3);
        if (this.sessionNoticesSeen.has(correlationId)) return;
        this.sessionNoticesSeen.add(correlationId);
        const at = dv.getBigInt64(o + 35, true);
        const notice = {
            correlationId, sessionId: guid(frame, o + 19),
            expiresAt: at === 0x7fffffffffffffffn ? null : new Date(Number(at)),
            renewal: RENEWAL_POLICIES[frame[o + 2]] ?? 'reauthenticate',
            reason: reasonLen ? new TextDecoder().decode(frame.subarray(o + SESSION_HEADER, o + SESSION_HEADER + reasonLen)) : null,
        };
        const identity = this.identity;
        if (!identity || identity.sessionId !== notice.sessionId) return;
        if (identity.expiresAt && notice.expiresAt && Date.parse(identity.expiresAt) > notice.expiresAt.getTime()) return;   // renewed since
        if (frame[o + 1] === 0) {
            this.onSessionExpiring?.(notice);
            if (notice.renewal === 'silent' && this.renewSilently && this.ticket) this.resume({renewal: true}).catch(() => {});
            return;
        }
        this.identity = null;
        this.onSessionEnded?.(notice);
        this.onIdentity?.(null);
    }

    /**
     * Hooks the connection that carries the frames — anything with on('Tap.Frame', handler), send(target, ...args) and
     * onclose(handler), as a SignalR HubConnection has: the Blazor circuit's, by startBlazor. Returns it, so it can wrap
     * HubConnectionBuilder.build.
     */
    attach(connection) {
        this.connection = connection;
        connection.onclose?.(() => this.#reachability(false));   // timed out (SignalR's, fed by the router's frames), or dropped
        if ('keepAliveIntervalInMilliseconds' in connection) connection.keepAliveIntervalInMilliseconds = this.keepAliveMs;
        this.firstFrameAt = this.lastFrameAt = 0;
        connection.on('Tap.Frame', (seq, frame) => this.receive(seq, frame));
        return connection;
    }

    /**
     * Step 2: registers the handlers for a bound tap, then activates it. `binding` is the step-1 answer — a TapBinding
     * ({ tapId, topic, endpoint, service, nodeId, partition, stale }) or just the tap id. `endpoint` is for clients that activate over a
     * separate request; on a Blazor circuit the activation already rides the connection to the right node, so it is ignored
     * here. `nodeId`: see "Service down" above. onError(error, lastState, id) is called once if the stream fails;
     * the subscription is over after it. Returns a function that removes these handlers.
     */
    subscribe(binding, onState, onError, onMessage) {
        const id = typeof binding === 'number' ? binding : binding.tapId;
        const service = typeof binding === 'number' ? null : binding.nodeId ?? null;
        const off = this.watch(id, onState, onError, onMessage);
        if (service) this.serviceOf.set(id, service);
        if (service && binding.service) this.pathOf.set(id, binding.partition ? `${binding.service}/${binding.partition}` : binding.service);
        if (typeof binding !== 'number' && binding.stale) this.#stalePolicy(id, binding.stale);
        if (service && this.servicesDown.has(service)) queueMicrotask(() => this.#serviceEnded(id, FaultCode.ServiceDown));   // gone before it was activated
        else if (service && this.servicesDrained.has(service)) queueMicrotask(() => this.#serviceEnded(id, FaultCode.ServiceDrained));
        else this.#send('Tap.Activate', id);
        return off;
    }

    /**
     * Make before break: subscribes `binding` (as subscribe does) in place of `oldId` — the old one keeps streaming until
     * the new one's first state, which drops it (unsubscribes it on the server); from then, only the new one. Its states
     * and the old one's never mix: each comes under its own tap id. For moving off a draining instance
     * (onServiceDraining), or to another representation of the same topic. Returns a function that removes the handlers.
     */
    replace(oldId, binding, onState, onError) {
        let switched = oldId === undefined || oldId === null;
        return this.subscribe(binding, (state, id) => {
            if (!switched) {
                switched = true;
                this.drop(oldId);
            }
            onState(state, id);
        }, onError);
    }

    /** Adds handlers without activating (e.g. a second view of an already-subscribed tap). */
    watch(id, onState, onError, onMessage) {
        let set = this.watchers.get(id);
        if (!set) this.watchers.set(id, set = new Set());
        const handler = {onState, onError, onMessage};
        set.add(handler);
        const state = this.#state(id);
        if (state !== undefined) onState(state, id);
        return () => set.delete(handler);
    }

    /**
     * A correlation id for a request: a UUIDv7 (RFC 9562), as hex — this page's clock in its first 48 bits, the rest
     * random. Send it with the request, await it with rendezvous(); correlationTime() reads its time back.
     */
    correlation() {
        const b = new Uint8Array(16);
        if (globalThis.crypto?.getRandomValues) crypto.getRandomValues(b);
        else for (let i = 0; i < 16; i++) b[i] = Math.random() * 256;
        for (let i = 5, ms = Date.now(); i >= 0; i--, ms = Math.floor(ms / 256)) b[i] = ms % 256;   // unix_ts_ms, big-endian
        b[6] = (b[6] & 0x0f) | 0x70;                               // version 7
        b[8] = (b[8] & 0x3f) | 0x80;                               // variant 10
        return hex(b, 0, 16);
    }

    /** When a correlation() id was made: its UUIDv7 time, in ms since the epoch (this page's clock) — undefined for any other id. */
    correlationTime(id) {
        const key = typeof id === 'string' ? id.toLowerCase() : hex(id, 0, id.length);
        return /^[0-9a-f]{12}7[0-9a-f]{3}[89ab][0-9a-f]{15}$/.test(key) ? parseInt(key.slice(0, 12), 16) : undefined;
    }

    /**
     * Resolves with { tapId, seq, missed } when the router delivers the rendezvous `id` (hex, or bytes: at most 64) —
     * strictly after the state the change it names made. Await it before sending the request. `tapId`: rejects if that
     * subscription ends first; `signal`: an AbortSignal. Awaiting an id already awaited returns the same promise.
     */
    rendezvous(id, {tapId, signal} = {}) {
        const key = typeof id === 'string' ? id.toLowerCase() : hex(id, 0, id.length);
        const waiting = this.rendezvousWaits.get(key);
        if (waiting) return waiting.promise;
        let resolve, reject;
        const promise = new Promise((res, rej) => {
            resolve = res;
            reject = rej;
        });
        const entry = {promise, tapId, resolve, reject};
        this.rendezvousWaits.set(key, entry);
        if (signal) {
            const abort = () => {
                if (this.rendezvousWaits.get(key) === entry) {
                    this.rendezvousWaits.delete(key);
                    reject(signal.reason ?? new Error('tapline: rendezvous aborted'));
                }
            };
            if (signal.aborted) abort(); else signal.addEventListener('abort', abort, {once: true});
        }
        return promise;
    }

    /**
     * Is this page up to date on a subscription? Asks the router (Tap.Sync), which answers with a rendezvous strictly
     * after the page has the subscription's state as of when the router received the ask: at once if nothing is held
     * back (a quiet source, a fast path), as late as the delivery of what is otherwise (a rate cap's, an interval's) —
     * so it tells a quiet subscription from one whose changes are waiting. Resolves with { tapId, seq, missed }; rejects
     * if the subscription ends first, and at once if the router says it doesn't take Tap.Sync (an older router);
     * `signal` aborts it. Asked while not bound, nothing is sent: give it a timeout.
     * Its id is the page's own count (4 bytes), never one of correlation()'s.
     */
    sync(tapId, {signal} = {}) {
        if (this.routerControls && !(this.routerControls & (1 << CONTROL_SYNC)))   // an older router: it says so
            return Promise.reject(new Error("tapline: the router doesn't take Tap.Sync"));
        this.syncs = (this.syncs + 1) >>> 0 || 1;                  // (0 is no id)
        const id = new Uint8Array(4);
        new DataView(id.buffer).setUint32(0, this.syncs, true);
        const answered = this.rendezvous(id, {tapId, signal});    // awaited before it's asked
        this.#send('Tap.Sync', tapId, this.syncs);
        return answered;
    }

    /** Rejects the rendezvous awaited through a subscription that ended. */
    #endRendezvous(id, error) {
        for (const [key, entry] of this.rendezvousWaits)
            if (entry.tapId === id) {
                this.rendezvousWaits.delete(key);
                entry.reject(Object.assign(new Error(`tapline: subscription ${id} ended before the rendezvous (code ${error.code})`), {
                    code: error.code,
                    tapId: id
                }));
            }
    }

    /** Resolves once no out-of-band fetch is in flight. */
    async idle() {
        while (this.inflight.size) await Promise.allSettled([...this.inflight]);
    }

    /** Unsubscribes: forgets the tap's handlers and state and tells the server (a no-op if it already closed the tap). */
    drop(id) {
        const known = this.#forget(id);
        this.#endRendezvous(id, {code: FaultCode.None});
        if (known) this.#send('Tap.Close', id, FaultCode.None);
    }

    /** The connection's server timeout, from the gaps between frames seen (once 5 s of this connection are). */
    #adaptTimeout(now) {
        const c = this.connection;
        if (!c || !('serverTimeoutInMilliseconds' in c) || now - this.firstFrameAt < 5000) return;
        const target = Math.round(Math.min(this.serverTimeoutCapMs, Math.max(this.serverTimeoutFloorMs, 4 * this.maxGap)));
        if (Math.abs(target - c.serverTimeoutInMilliseconds) > c.serverTimeoutInMilliseconds * 0.1) c.serverTimeoutInMilliseconds = target;
    }

    receive(seq, frame) {
        const now = this.now();
        if (this.lastFrameAt) {
            const gap = now - this.lastFrameAt;
            this.maxGap = Math.max(gap, this.maxGap * Math.pow(0.5, gap / 30000));
        } else this.firstFrameAt = now;
        this.lastFrameAt = now;
        this.receivedAtWall = Date.now();
        this.#adaptTimeout(now);
        this.rate = this.transferRate + frame.byteLength * 1000 / RATE_TAU_MS;
        this.rateAt = performance.now();
        this.bytesReceived += frame.byteLength;
        const dv = new DataView(frame.buffer, frame.byteOffset, frame.byteLength);
        for (let o = 0; o < frame.byteLength;) {
            switch (frame[o]) {
                case HEARTBEAT: {
                    const hb = this.heartbeat;
                    hb.seq = dv.getUint32(o + 1, true);
                    hb.serverMicros = i64(dv, o + 5);
                    hb.receivedAt = performance.now();
                    this.onHeartbeat?.(hb);
                    o += 13;
                    break;
                }
                case ROUTER_CHANGE: {                               // the router's state, behind a heartbeat till echoed: a change once here
                    const state = frame[o + 1], h = o + ROUTER_CHANGE_HEADER;
                    this.routerControls = dv.getUint32(o + ROUTER_CHANGE_CONTROLS, true);   // what it takes: each connection's
                    if (!sameBytes(frame, o + 2, this.routerCorrelation)) {
                        this.routerCorrelation.set(frame.subarray(o + 2, o + 18));
                        this.routerHealth = {
                            state: ROUTER_STATES[state] ?? 'unknown', correlationId: guid(frame, o + 2),
                            reason: state === ROUTER_DEGRADED ? new TextDecoder().decode(frame.subarray(h + 1, h + 1 + frame[h])) : null,
                            drainsBy: state === ROUTER_DRAINING ? Date.now() + dv.getUint32(h, true) : null,
                        };
                        this.onRouterHealth?.(this.routerHealth);
                    }
                    o += ROUTER_CHANGE_HEADER + (state === ROUTER_DRAINING ? 4 : state === ROUTER_DEGRADED ? 1 + frame[h] : 0);
                    break;
                }
                case VALUE: {
                    const id = dv.getUint32(o + 1, true), len = dv.getUint16(o + 5, true);
                    if (this.watchers.has(id)) {                       // only activated (hence watched) taps stream
                        this.#bump(id);
                        const value = frame.slice(o + 7, o + 7 + len);   // copy: don't pin the whole frame
                        this.values.set(id, value);
                        this.#notify(id, value);
                    }
                    o += 7 + len;
                    break;
                }
                case TABLE: {
                    const id = dv.getUint32(o + 1, true), count = dv.getUint32(o + 26, true);
                    const stride = 1 + dv.getUint16(o + 30, true) + dv.getUint16(o + 32, true);
                    if (this.watchers.has(id)) {
                        this.#bump(id);
                        this.#applyTable(id, frame, dv, o);
                    }
                    o += 34 + stride * count;
                    break;
                }
                case GROUP: {
                    const id = dv.getUint32(o + 1, true);
                    if (this.watchers.has(id)) {
                        this.#bump(id);
                        this.#applyGroup(id, frame, dv, o);
                    }
                    o += 12 + dv.getUint32(o + 8, true);
                    break;
                }
                case OOB: {
                    const id = dv.getUint32(o + 1, true), inner = frame[o + 5], mode = frame[o + 6] & OOB_MODE,
                        ticket = dv.getUint32(o + 43, true);
                    const ref = new BlobRef(this, hex(frame, o + 7, 16), hex(frame, o + 23, 16), dv.getUint32(o + 39, true), (frame[o + 6] & OOB_CONTENT_ADDRESSED) !== 0);
                    if (this.watchers.has(id)) {
                        const generation = this.#bump(id);
                        if (mode === OOB_REFERENCE) {
                            this.values.set(id, ref);
                            this.#notify(id, ref);
                        } else this.#fetchRecord(id, inner, ref, ticket, generation);
                    }
                    o += 47;
                    break;
                }
                case SERVICE: {
                    const status = frame[o + 1], len = dv.getUint16(o + 2, true);
                    if (status === SERVICE_UP) {                       // u8[16] correlation | the service's name
                        this.onServiceUp?.(new TextDecoder().decode(frame.subarray(o + 20, o + 4 + len)), guid(frame, o + 4));
                        o += 4 + len;
                        break;
                    }
                    const node = guid(frame, o + 4), correlationId = guid(frame, o + 20);   // u8[16] node | u8[16] correlation | …
                    if (status === SERVICE_DOWN || status === SERVICE_DRAINED) {
                        const down = status === SERVICE_DOWN, seen = down ? this.servicesDown : this.servicesDrained;
                        if (!seen.has(node)) {
                            seen.add(node);
                            this.#providerState(node, down ? 'down' : 'drained', null, correlationId);
                            for (const [id, s] of this.serviceOf) if (s === node) this.#serviceEnded(id, down ? FaultCode.ServiceDown : FaultCode.ServiceDrained);
                            (down ? this.onServiceDown : this.onServiceDrained)?.(node, correlationId);
                        }
                    } else if (status === SERVICE_DEGRADED || status === SERVICE_RECOVERED) {
                        const scope = len > 32 ? new TextDecoder().decode(frame.subarray(o + 36, o + 4 + len)) : null;   // a service path there; none: the node
                        this.#providerState(node, status === SERVICE_DEGRADED ? 'degraded' : 'serving', scope, correlationId);
                    } else if (status === SERVICE_DRAINING) {
                        this.#providerState(node, 'draining', null, correlationId);
                        this.onServiceDraining?.(node, dv.getUint32(o + 36, true), correlationId);
                    } else if (status === SERVICE_MOVE) {                // … | u32 graceMs | u32 tapId: move that one
                        const tapId = dv.getUint32(o + 40, true);
                        if (this.watchers.has(tapId) && this.movedBy.get(tapId) !== correlationId) {
                            this.movedBy.set(tapId, correlationId);
                            this.onMove?.(tapId, dv.getUint32(o + 36, true), correlationId, node);
                        }
                    }
                    o += 4 + len;
                    break;
                }
                case REFUSED: {                                     // a subscription of ours refused, saying why and of what kind — logged with its correlation id: the needle
                    const kind = frame[o + 1], correlationId = guid(frame, o + 2), serviceLen = dv.getUint16(o + 18, true),
                        reasonLen = dv.getUint16(o + 20 + serviceLen, true);
                    const service = new TextDecoder().decode(frame.subarray(o + 20, o + 20 + serviceLen));
                    const reason = new TextDecoder().decode(frame.subarray(o + 22 + serviceLen, o + 22 + serviceLen + reasonLen));
                    if (!this.refusalsSeen.has(correlationId)) {
                        this.refusalsSeen.add(correlationId);
                        console.warn(`tapline: ${service} refused a subscription: ${reason} (correlation ${correlationId})`);
                        this.onRefused?.({correlationId, service, reason, kind});
                    }
                    o += 22 + serviceLen + reasonLen;
                    break;
                }
                case AUTH: {                                        // the router's answer to a sign-in's message (resent until acknowledged: taken once)
                    const textLen = dv.getUint16(o + 8, true), payloadLen = dv.getUint32(o + 10 + textLen, true);
                    this.#applyAuth(frame, dv, o, textLen, payloadLen);
                    o += AUTH_HEADER + textLen + payloadLen;
                    break;
                }
                case SESSION: {                                     // the router's word on the session: expiring (warned once), or ended
                    const reasonLen = dv.getUint16(o + 43, true);
                    this.#applySession(frame, dv, o, reasonLen);
                    o += SESSION_HEADER + reasonLen;
                    break;
                }
                case ENDED: {                                       // the router ended subscriptions of ours: a range of tap ids (stalled), or one
                    const code = dv.getUint16(o + 1, true), from = dv.getUint32(o + 3, true),
                        to = dv.getUint32(o + 7, true);
                    for (const id of [...this.watchers.keys()]) if (id >= from && id <= to) this.#serviceEnded(id, code);
                    this.onEnded?.(code, from, to);
                    o += 11;
                    break;
                }
                case TOPIC_MESSAGE: {
                    const id = dv.getUint32(o + 1, true), len = dv.getUint32(o + 14, true);
                    if (this.watchers.has(id)) {
                        this.#bump(id);
                        this.#applyMessage(id, frame, dv, o);
                    }
                    o += MESSAGE_HEADER + len;
                    break;
                }
                case RENDEZVOUS: {                                  // after the state it names: nothing here to apply, a promise to keep
                    const id = dv.getUint32(o + 1, true), len = frame[o + 14];
                    const info = {
                        tapId: id,
                        seq: Number(dv.getBigInt64(o + 6, true)),
                        missed: (frame[o + 5] & FLAG_MISSED) !== 0
                    };
                    const key = hex(frame, o + RENDEZVOUS_HEADER, len), entry = this.rendezvousWaits.get(key);
                    if (entry) {
                        this.rendezvousWaits.delete(key);
                        entry.resolve(info);
                    } else this.onRendezvous?.(key, info);
                    o += RENDEZVOUS_HEADER + len;
                    break;
                }
                case ERROR: {
                    const id = dv.getUint32(o + 1, true), len = dv.getUint16(o + 15, true);
                    if (this.watchers.has(id))
                        this.#fail(id, {
                            code: dv.getUint16(o + 5, true),
                            version: i64(dv, o + 7),
                            payload: frame.slice(o + 17, o + 17 + len),
                            local: false
                        });
                    o += 17 + len;
                    break;
                }
                default:
                    console.error(`tapline: unknown record 0x${frame[o].toString(16)} at ${o}`);
                    o = frame.byteLength;
            }
        }
        this.lastFrame = seq;
        this.#send('Tap.Ack', seq, this.heartbeat.seq);
    }

    #applyTable(id, bytes, dv, o) {
        let table = this.tables.get(id);
        if (!table) this.tables.set(id, table = new Table());
        if (table.apply(bytes, dv, o)) this.#notify(id, table);
        else if (table.lastWasReset && !table.degraded) this.#reject(id, FaultCode.ChecksumMismatch, table.seq);   // resync can't fix a bad snapshot
        else if (!table.resyncSent && !table.degraded) {
            table.resyncSent = true;
            this.#send('Tap.Resync', id);
        }
    }

    /** A coherent cut: every member record is applied, then the subscriber is notified once. */
    #applyGroup(id, bytes, dv, o) {
        const flags = bytes[o + 5], count = dv.getUint16(o + 6, true);
        let g = this.groups.get(id);
        if (!g) this.groups.set(id, g = {members: [], degraded: false, resyncSent: false});
        if (flags & FLAG_RESET) {
            g.members = [];
            g.resyncSent = false;
        }
        g.degraded = (flags & FLAG_DEGRADED) !== 0;
        let p = o + 12, ok = true, badSnapshot = false;
        for (let i = 0; i < count; i++) {
            const type = bytes[p], member = dv.getUint32(p + 1, true);
            if (type === TABLE) {
                const n = dv.getUint32(p + 26, true),
                    stride = 1 + dv.getUint16(p + 30, true) + dv.getUint16(p + 32, true);
                let t = g.members[member];
                if (!(t instanceof Table)) g.members[member] = t = new Table();
                if (!t.apply(bytes, dv, p)) {
                    ok = false;
                    badSnapshot ||= t.lastWasReset && !t.degraded;
                }
                p += 34 + stride * n;
            } else if (type === VALUE) {
                const len = dv.getUint16(p + 5, true);
                g.members[member] = bytes.slice(p + 7, p + 7 + len);
                p += 7 + len;
            } else {
                console.error(`tapline: record 0x${type.toString(16)} inside a group`);
                ok = false;
                break;
            }
        }
        if (ok) {
            this.#notify(id, g);
            return;
        }
        if (badSnapshot) this.#reject(id, FaultCode.ChecksumMismatch, -1);
        else if (!g.resyncSent && !g.degraded) {
            g.resyncSent = true;
            this.#send('Tap.Resync', id);
        }
    }

    /**
     * A message — of a message topic, or of the subscription's mailbox: handed to onMessage (else onState) as
     * (bytes, id, { seq, gap, mailbox }), once — the router sees to that, across reconnects too. The bytes are a view into
     * the frame: copy them to keep them.
     */
    #applyMessage(id, bytes, dv, o) {
        const flags = bytes[o + 5], seq = Number(dv.getBigInt64(o + 6, true)), len = dv.getUint32(o + 14, true);
        const mailbox = (flags & FLAG_MAILBOX) !== 0;
        const message = bytes.subarray(o + MESSAGE_HEADER, o + MESSAGE_HEADER + len);
        const meta = {
            seq,
            gap: (flags & FLAG_GAP) !== 0,
            mailbox,
            stale: (flags & FLAG_STALE) !== 0,
            provider: this.providerOf(id)
        };
        const policy = this.stalePolicies.get(id);
        if (policy && !mailbox && !meta.stale) this.lastMessages.set(id, {
            bytes: policy.kind === STALE_STATIC ? null : message.slice(),
            seq
        });
        const set = this.watchers.get(id);
        if (set) for (const h of set) {
            try {
                (h.onMessage ?? h.onState)(message, id, meta);
            } catch (error) {
                console.error('tapline: a message handler threw', error);
            }
        }
    }

    /** A transparent out-of-band record of a tap: fetch, verify, apply in place, confirm (the server holds the tap meanwhile). */
    #fetchRecord(id, inner, ref, ticket, generation) {
        const epoch = this.epoch;
        const current = () => this.epoch === epoch && this.generations.get(id) === generation && this.watchers.has(id);
        this.#track((async () => {
            let bytes;
            try {
                bytes = await ref.bytes();
            } catch (error) {
                if (!current()) return;
                const failures = (this.fetchFailures.get(id) ?? 0) + 1;
                this.fetchFailures.set(id, failures);
                if (failures >= FETCH_ATTEMPTS) this.#reject(id, FaultCode.Unknown, -1);
                else this.#send('Tap.Resync', id);                 // a fresh snapshot, with a fresh handle
                return;
            }
            if (!current()) return;                              // dropped, failed, reconnected or overtaken meanwhile: stale
            this.fetchFailures.delete(id);
            const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
            if (inner === VALUE) {
                this.values.set(id, bytes);
                this.#notify(id, bytes);
            } else if (inner === TABLE) this.#applyTable(id, bytes, dv, 0);
            else if (inner === GROUP) this.#applyGroup(id, bytes, dv, 0);
            else if (inner === TOPIC_MESSAGE) this.#applyMessage(id, bytes, dv, 0);
            this.#send('Tap.Fetched', 0, id, ticket);
            this.confirmed.set(id, ticket);
        })());
    }

    #bump(id) {
        const g = (this.generations.get(id) ?? 0) + 1;
        this.generations.set(id, g);
        return g;
    }

    #track(promise) {
        this.inflight.add(promise);
        promise.finally(() => this.inflight.delete(promise));
    }

    #state(id) {
        return this.tables.get(id) ?? this.groups.get(id) ?? this.values.get(id);
    }

    /**
     * The state of the data provider a subscription comes through: 'serving', 'degraded' (stale data shown, held),
     * 'draining', 'down', 'drained' — or undefined for one bound directly (no provider). Every state and message handed
     * over carries it (the third argument: { provider }).
     */
    providerOf(id) {
        const node = this.serviceOf.get(id);
        if (node === undefined) return undefined;
        if (!this.reachable && this.outlet) return 'unreachable';
        const state = this.providerStates.get(node) ?? 'serving', scopes = this.degradedScopes.get(node),
            path = this.pathOf.get(id);
        if (state !== 'serving' || !scopes || path === undefined) return state;
        for (const scope of scopes) if (path === scope || path.startsWith(scope + '/')) return 'degraded';   // a scope covering it
        return state;
    }

    /** A subscription's stale-data policy, from its binding; its module fetched now, while the router is reachable. */
    #stalePolicy(id, stale) {
        const policy = {
            kind: stale.kind,
            state: stale.state ? fromBase64(stale.state) : new Uint8Array(0),
            module: stale.module ?? null
        };
        if (policy.kind === STALE_FUNCTION && !policy.module) return;
        this.stalePolicies.set(id, policy);
        if (policy.module) this.#loadModule(policy.module).catch(error => console.warn(`tapline: stale-data module ${policy.module}: ${error.message}`));
    }

    /** Fetches, checks (its id is its content address) and compiles a stale-data module, once. */
    #loadModule(moduleId) {
        let pending = this.modules.get(moduleId);
        if (!pending) {
            this.modules.set(moduleId, pending = (async () => {
                const bytes = await this.fetchBlob(`${this.moduleBase}/${moduleId}`);
                if (!(await matchesId(bytes, moduleId))) throw new Error('failed verification');
                const module = await WebAssembly.compile(bytes);
                this.compiled.set(moduleId, module);
                return module;
            })());
            pending.catch(() => this.modules.delete(moduleId));   // a later subscription tries again
            this.#track(pending.catch(() => {
            }));
        }
        return pending;
    }

    /**
     * What a subscription shows while the router is out of reach, by its policy — a copy; undefined for nothing (it
     * received nothing, its module isn't here, its function failed for a value or a message). A table's function failing:
     * no rows (as the router does).
     */
    #staleView(id, policy) {
        const table = this.tables.get(id);
        if (table) {
            const K = table.keySize, R = table.rowSize, stride = K + R;
            if (table.awaitingReset && !table.verified) return undefined;   // no snapshot to start from
            if (policy.kind === STALE_REPLAY) return table.staleView([...table.rows.values()].map(e => [e.key.slice(), e.row.slice()]));
            if (policy.kind === STALE_STATIC) {
                const s = policy.state;
                if (stride === 0 || s.length % stride !== 0) return undefined;   // not of this table's shape: none
                const rows = [];
                for (let o = 0; o < s.length; o += stride) rows.push([s.slice(o, o + K), s.slice(o + K, o + stride)]);
                return table.staleView(rows);
            }
            const module = this.compiled.get(policy.module);
            if (!module) return undefined;
            const all = [...table.rows.values()], entries = new Uint8Array(all.length * (4 + stride));
            all.forEach((e, i) => {
                const o = i * (4 + stride);
                entries[o] = ENTRY_PRESENT;
                entries.set(e.key, o + 4);
                entries.set(e.row, o + 4 + K);
            });
            try {
                degradeWith(module, entries, all.length, K, R);
            } catch (error) {
                console.warn(`tapline: stale-data function failed (tap ${id}): ${error.message}`);
                return table.staleView([]);
            }
            const rows = [];
            all.forEach((e, i) => {
                const o = i * (4 + stride);
                if (entries[o] & ENTRY_PRESENT) rows.push([e.key.slice(), entries.slice(o + 4 + K, o + 4 + stride)]);
            });
            return table.staleView(rows);                         // (keys are read-only: the function's changes to them ignored)
        }
        const last = this.lastMessages.get(id);
        const value = last ? last.bytes : this.values.get(id);
        if (value === undefined || value instanceof BlobRef) return undefined;
        let bytes;
        if (policy.kind === STALE_STATIC) bytes = policy.state.slice();
        else if (policy.kind === STALE_REPLAY) bytes = value.slice();
        else {
            const module = this.compiled.get(policy.module);
            if (!module) return undefined;
            const entry = new Uint8Array(4 + value.length);
            entry[0] = ENTRY_PRESENT;
            entry.set(value, 4);
            try {
                degradeWith(module, entry, 1, 0, value.length);
            } catch (error) {
                console.warn(`tapline: stale-data function failed (tap ${id}): ${error.message}`);
                return undefined;
            }
            if (!(entry[0] & ENTRY_PRESENT)) return undefined;
            bytes = entry.slice(4);
        }
        return last ? {message: bytes, seq: last.seq} : bytes;
    }

    /** The router out of reach: each subscription with a policy is handed its stale view. */
    #showStale() {
        for (const [id, policy] of this.stalePolicies) {
            const set = this.watchers.get(id);
            if (!set) continue;
            const view = this.#staleView(id, policy);
            if (view === undefined) continue;
            this.shownStale.add(id);
            const provider = this.providerOf(id);
            for (const h of set) {
                try {
                    if (view.message) (h.onMessage ?? h.onState)(view.message, id, {
                        seq: view.seq,
                        gap: false,
                        mailbox: false,
                        stale: true,
                        provider
                    });
                    else h.onState(view, id, {provider, stale: true});
                } catch (error) {
                    console.error('tapline: a handler threw', error);
                }
            }
        }
    }

    /** Reachable again: those shown a stale view get their real state back (a message stream: the router goes on). */
    #showLive() {
        for (const id of this.shownStale) {
            const state = this.tables.get(id) ?? this.values.get(id);
            if (state !== undefined && this.watchers.has(id)) this.#notify(id, state);
        }
        this.shownStale.clear();
    }

    #providerState(node, state, scope, correlationId) {
        if (scope !== null) {                               // a service path there: degraded, or serving again
            let scopes = this.degradedScopes.get(node);
            if (state === 'degraded') {
                if (!scopes) this.degradedScopes.set(node, scopes = new Set());
                scopes.add(scope);
            } else if (scopes) {
                scopes.delete(scope);
                if (scopes.size === 0) this.degradedScopes.delete(node);
            }
        } else if (state === 'serving') {
            if (this.providerStates.get(node) === 'degraded') this.providerStates.delete(node);   // (draining, down, drained: as they were)
        } else if (!(state === 'degraded' && this.providerStates.has(node))) this.providerStates.set(node, state);
        this.onProviderState?.(node, scope !== null ? state : this.providerStates.get(node) ?? 'serving', {
            scope,
            correlationId
        });   // the node's: as it is now
    }

    #reachability(reachable) {
        if (this.reachable === reachable) return;
        this.reachable = reachable;
        if (reachable) this.#showLive(); else this.#showStale();
        this.onReachability?.(reachable);
    }

    #notify(id, state) {
        const set = this.watchers.get(id);
        if (set) for (const h of set) h.onState(state, id, {provider: this.providerOf(id)});
    }

    /** Ends a subscription with an error: handlers get the last state once, then everything about the tap is forgotten. */
    #fail(id, error) {
        const set = this.watchers.get(id), state = this.#state(id);
        this.#forget(id);
        this.#endRendezvous(id, error);
        if (set) for (const h of set) (h.onError ?? this.onError)?.(error, state, id);
    }

    /** A subscription whose provider instance is down, or drained: over (the server has closed it already). */
    #serviceEnded(id, code) {
        if (this.watchers.has(id)) this.#fail(id, {code, version: 0, payload: new Uint8Array(0), local: false});
    }

    /** The client gives up on a stream the server considers healthy: tell the server why, then fail locally. */
    #reject(id, code, version) {
        this.#send('Tap.Close', id, code);
        this.#fail(id, {code, version, payload: new Uint8Array(0), local: true});
    }

    #forget(id) {
        const known = this.watchers.has(id);
        this.tables.delete(id);
        this.values.delete(id);
        this.groups.delete(id);
        this.watchers.delete(id);
        this.fetchFailures.delete(id);
        this.generations.delete(id);
        this.serviceOf.delete(id);
        this.pathOf.delete(id);
        this.confirmed.delete(id);
        this.stalePolicies.delete(id);
        this.lastMessages.delete(id);
        this.movedBy.delete(id);
        this.shownStale.delete(id);
        return known;
    }

    #send(target, ...args) {
        const o = this.outlet, c = this.connection;
        if (o && c) c.send(target, o.id, o.nonce, ...args).catch(() => {
        });
    }
}

/** Base64url (no padding) to bytes, and back: WebAuthn's options and answers travel as JSON. */
function fromBase64Url(s) {
    const b = atob(s.replaceAll('-', '+').replaceAll('_', '/') + '==='.slice((s.length + 3) % 4));
    return Uint8Array.from(b, c => c.charCodeAt(0));
}

function toBase64Url(buffer) {
    const bytes = buffer instanceof Uint8Array ? buffer : new Uint8Array(buffer);
    let s = '';
    for (const b of bytes) s += String.fromCharCode(b);
    return btoa(s).replaceAll('+', '-').replaceAll('/', '_').replace(/=+$/, '');
}

const passkeyList = list => (list ?? []).map(c => ({type: c.type ?? 'public-key', id: fromBase64Url(c.id), transports: c.transports}));

/** A sign-in's Cancel message: the client gives exchange `exchange` up. */
function authCancel(exchange) {
    const c = new Uint8Array(5);
    c[0] = AUTH_CANCEL;
    new DataView(c.buffer).setUint32(1, exchange, true);
    return c;
}

export const tapline = new TaplineClient();
globalThis.tapline = tapline;
