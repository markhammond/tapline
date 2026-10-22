# Tapline Clients

Using Tapline from Blazor and from a .NET client.

> [!TIP]
> The samples show it in use, each with its own README:
> - [BlazorRealtime](../samples/BlazorRealtime.Sample/README.md) — animation synchronized to streaming music over one
>   circuit.
> - [BlazingOrderBooks](../samples/BlazingOrderBooks.Sample/README.md) — streaming order books with delta-based
>   conflation.
> - [PivotTable](../samples/PivotTable.Sample/README.md) — a live position viewer using idiomatic SQL.
> - [ActivityFeed](../samples/ActivityFeed.Sample/README.md) — activity feeds from different perspectives with
>   need-to-know access rules.
> - [SignIn](../samples/SignIn.Sample/README.md) — passkeys and OPAQUE as pluggable authentication modules.

## Usage (Blazor)

```csharp
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddBlazorRealtime();                    // after AddInteractiveServerComponents (wraps blazorpack); the router too
builder.Services.AddTapline(o => o.RouterName = "edge-a"); // the router's own options, if any: before or after
// ...
app.MapBlazorRealtime();                                 // MapTapline(): GET /_tapline/blob/... (out of band), /_tapline/module/... (stale-data modules)
```

`AddBlazorRealtime()` registers the circuit transport and the router it serves (`AddTapline()`, from Tapline.AspNetCore,
however often called). The router's options — its name, heartbeats, ops port, injected outages — are `TaplineOptions`
(the `Tapline` configuration section); the circuits' — their outlets, the circuit hub's timeouts, the circuit sockets —
are `BlazorRealtimeOptions` (the `BlazorRealtime` section).

**Circuit compression cut-off.** Blazor negotiates per-message deflate for the circuit's WebSocket and compresses every
message. Deflate costs a few µs per message plus ~55 ns per byte, so per byte saved it is dearest for small messages —
the high-rate frames of fast clients, who have bandwidth to spare. `MapBlazorRealtime()` therefore has messages under
`BlazorRealtimeOptions.WebSocketCompressionThreshold` (256 B by default) sent uncompressed — RFC 7692 allows it per
message, and it leaves both sides' compression history intact — while a constrained client's conflated frames, a few
hundred bytes and up, stay compressed. Data providers can declare content that won't shrink (already compressed,
encrypted, random): `Publish(…, compressible: false)` (also in a transaction), `Table.Compressible = false`,
`MessageTopic.Compressible = false`; out-of-band handles count as such too. The outlet counts
those bytes per frame (`TryBuildFrame(…, out incompressibleBytes)`) and the socket discounts them, so a frame of such
content goes uncompressed however large; only what is left is compared with the threshold (0: only messages with nothing
compressible skip deflate). It decorates the circuit socket's
`IHttpWebSocketFeature` (on `…/_blazor` only, after the app's own middleware, so the app's WebSocket options still apply)
underneath Blazor's own decorator, and only when Blazor has turned compression on. Measured with .NET's WebSocket: a fast
client's 86 B frames cost 4.7 µs to send instead of 13.7 µs (88 B on the wire instead of 25 B); a 512 B/s client's
~300 B deltas still go out at ~95 B.

**Circuit send buffer (opt-in).** `BlazorRealtimeOptions.CircuitSendBufferBytes` caps each circuit socket's kernel send
buffer (`SO_SNDBUF`; other connections keep the OS's auto-tuned size, often hundreds of KB). Bytes queued there are
already serialized, so a smaller buffer moves the point where frames are bound closer to when they leave, and a burst
(render batches, snapshots) delays what follows less. It caps throughput at about this ÷ RTT: size it to at least the
bandwidth × RTT of the clients you serve (64 KB covers 5 Mbit/s at 100 ms). Linux doubles the value it is given.
On Linux, `CircuitNotSentLowWatermarkBytes` (`TCP_NOTSENT_LOWAT`, e.g. 16–32 KB) is the finer tool: the kernel stops
taking more once that much is queued but unsent, while data in flight still grows to the full auto-tuned window — a short
backlog without the throughput cap. It is ignored elsewhere (on macOS the option only delays writability).

```razor
@inject Outlet Outlet
@inject Publisher Publisher
@code {
    protected override async Task OnAfterRenderAsync(bool first)
    {
        if (!first) return;                              // interactive only
        _ = KeepTapped(Publisher.Table<Level, long>("book:BTC-PERP"), _cts.Token);
    }

    async Task KeepTapped(Source book, CancellationToken ct)
    {
        while (true)
        {
            using var tap = Outlet.Tap(book, "book:BTC-PERP", BookPolicy);         // step 1: bind
            await JS.InvokeVoidAsync("bookView.attach", ct, tap.Binding, _table); // the view subscribes → step 2
            if (await tap.Completion.WaitAsync(ct) is null) return;              // closed normally
            while (Outlet.FaultOf(book) is not null) await Task.Delay(250, ct); // faulted: resubscribe after recovery
        }
    }
}
```

```html
<script src="@Assets["_framework/blazor.web.js"]" autostart="false"></script>
<script type="module">
  import { tapline } from './@Assets["_content/Tapline/tapline.js"]';
  import { startBlazor } from './@Assets["_content/Tapline/blazor-realtime.js"]';
  // tapline.subscribe(binding, (state, id) => ..., (error, lastState, id) => ..., (message, id, { seq, gap, mailbox }) => ...)
  // — binding = { tapId, topic, endpoint, service, nodeId, partition, stale } or a tap id; handlers first, then Tap.Activate
  // (endpoint ignored on a circuit); state is a Table replica (rows: Map → {key, row} bytes; .degraded), a Uint8Array,
  // a BlobRef (Delivery.Reference) or, for a watch, { members, degraded }; error = { code, version, payload, local };
  // a message topic's messages (and a mailbox's) go to the fourth handler, in order, once.
  // every state and message also carries { provider } ('serving' | 'degraded' | 'draining' | 'down'; messages: also stale);
  // the router out of reach, a subscription with a stale policy is handed its stale view: { stale: true, provider: 'unreachable' }
  // tapline.onServiceDown/Up/Drained/Draining, tapline.onProviderState(instance, state), tapline.onReachability(reachable),
  // tapline.onEnded(code, fromTapId, toTapId) (the router ended them),
  // tapline.onMove(tapId, graceMs, correlationId, nodeId) (move one: subscribe again, then tapline.replace; else FaultCode.Moved)
  // tapline.correlation() (a UUIDv7), tapline.correlationTime(id) (its ms, this page's clock),
  // tapline.rendezvous(id, { tapId, signal }) → { tapId, seq, missed }, tapline.onRendezvous(id, info)
  // tapline.sync(tapId, { signal }) → { tapId, seq, missed }: up to date on it as of the ask (Tap.Sync), quiet or held back
  // tapline.blobBase (default '_tapline/blob'), tapline.moduleBase (default '_tapline/module')
  // tapline.transferRate: received B/s, smoothed (2 s) — inflated Tap.Frame payloads, as the page sees them
  startBlazor(Blazor, tapline);                       // the circuit's own connection carries tapline's frames
</script>
```

A page restored from the browser's back-forward cache comes back with its circuit's connection gone and its
subscriptions showing their stale view. Safari keeps pages with an open WebSocket there; Chrome doesn't. `startBlazor`
loads such a page again, so it gets a circuit and subscriptions of its own, as if visited anew.
`startBlazor(Blazor, tapline, { reloadOnRestore: false })` leaves that to the page.

## Usage (.NET client)

`Tapline.Client` keeps what a .NET app subscribed to, over whatever carries its frames. The transport implements
`ITaplineConnection` — one method, `Send(in ControlMessage)`: what the client says, each of the wire's control messages
(`ControlKind`: `Activate`, `Ack`, `Resume`, `Resync`, `Close`, `Fetched`, `Auth`, `Sync`). It passes on every kind, one
it doesn't know too, so a kind added later needs no change to it. It never blocks, keeps order (an acknowledgement is
cumulative: only the newest waiting needs sending), takes calls from any thread, and copies a payload it queues. The
router says the kinds it takes on each connection (`TaplineClient.RouterControls`), and a request it doesn't take fails
at once. The transport calls the client back:

```csharp
var client = new TaplineClient(connection, new TaplineClientOptions { BlobBase = new Uri("http://localhost:5900/_tapline/blob") });
client.Bind(outletId, nonce);                  // each connection: the router says which outlet (resumes, activates again)
client.Receive(seq, frame);                    // each frame: applied, handed over, acknowledged
client.Disconnected();                         // the connection closed: the router out of reach until bound again

var book = client.Subscribe(binding, new SubscriptionHandler      // binding: the app's step 1, on its server (a TapBinding)
{
    Table = (s, table, batch) => { /* batch: a snapshot's rows or a delta's changes; table: its rows now */ },
    Error = (s, error) => { /* over: subscribe again (step 1) */ },
});
```

Handlers run one at a time, in order, under the client's lock: don't block in them, nor wait on a lock of the app's that
a thread holds while calling the client. Its status — `Reachable`, `Router`, `LastFrame`, `SinceLastFrame`,
`BytesReceived`, `TransferRate`, `Resyncs` — reads without that lock, from any thread. A table's replica is verified record
by record, sequence and checksum (`RowHash`): a delta that doesn't check out is resynced, a snapshot that doesn't is
rejected (`Close` with `ChecksumMismatch`). Records sent out of band are fetched over HTTP (`BlobBase` — best on a
connection apart from the frames', so a large blob never queues ahead of them), checked against their content address,
applied in place and confirmed; sent by reference, `OnBlob` hands over a `BlobRef` to fetch on demand. The router's
notices are events — `ServiceDown`, `ServiceUp`, `ServiceDrained`, `ServiceDraining` (then `Replace(oldTapId, binding,
handler)` to move, make before break), `SubscriptionMoving` (one subscription to move, its instance rebalancing: the
same, within its grace, or it ends with `FaultCode.Moved`), `Ended`, `Refused`, `ProviderStateChanged` (a `ProviderChange`: the node, its
state, the scope if a service path's, the change's correlation id), `ReachabilityChanged` — each provider notice with
its correlation id — and `RendezvousAsync(correlation)` awaits a change's arrival; `SyncAsync(tapId)` asks the router
whether the client is up to date on a subscription (`Tap.Sync`: answered at once when nothing is held back, after
delivery otherwise). A subscription's `Provider` is its
node's state, or degraded if a degraded scope covers it. `ServiceHealthCheck(service)` makes a service's health a check
of the app's: over its subscriptions to the service — healthy while each streams from a node serving it; degraded while
one is shown stale data, its node drains, or a drain let one go; unhealthy while the service is down for it (waiting to
be told it's up), or the router is out of reach; its data names the nodes, the degraded scopes and the last notice's
correlation id. The router's own state is `Router` (`RouterStatusChanged`), and `RouterHealthCheck()` makes it a health
check of the app's (see *The router's own state*). Noticing a dead connection is
the transport's: `SinceLastFrame` says how long the router has been quiet (a heartbeat every second while it's in reach).
