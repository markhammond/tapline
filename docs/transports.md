# Tapline Transports

What carries Tapline's frames. MagicOnion is the end-to-end reference.

## Usage (MagicOnion)

Tapline.MagicOnion carries Tapline.Client over MagicOnion — gRPC, HTTP/2 — for .NET clients: a service, a desktop app, a
console. The stream's messages are domain-agnostic; they carry Tapline's wire format, the same records a circuit
carries. One duplex call per connection (`ITaplineStream.OpenAsync()`):

| | Message | |
|---|---|---|
| router → client | `Frame { long Seq; ReadOnlyMemory<byte> Payload }` | a frame: its records (Heartbeat, RouterChange, Table, Value, Group, OobRef, Service, TopicMessage, Ended, Rendezvous, Refused, Error) |
| client → router | `Control { ControlKind Kind; long A; long B; byte[]? Payload }` | `Activate(tapId)`, `Ack(frameSeq, heartbeatEcho)`, `Resume(lastFrame, heartbeatEcho)`, `Resync(tapId)`, `Close(tapId, code)`, `Fetched(tapId, ticket)`, `Auth(payload)`, `Sync(tapId, syncId)`: the kinds the router takes, as its RouterChange says |
| request header | `tapline-session: {outletId}.{nonce}`, `tapline-bwlimit: 64` | the session claimed again (none: a new one); a cap on its Streaming, lower only |
| response header | `tapline-session: {outletId}.{nonce}` | the session the call is bound to |

Batching is the router's: a frame holds what flow control and the classes let it — records of many subscriptions, a
heartbeat, notices — and a Table record is a subscription's conflated changes, its versions and checksum. The
transport moves one gRPC message a frame. Subscribing stays two-phase: step 1 is the app's own service — typed, its
domain's — which finds the session's outlet (`TaplineStreamSessions.Find(outletId, nonce)`) and answers a binding;
the client activates it over the stream. A subscriber reads its rows as its own structs (`TableEntry.RowAs<TRow>()`,
`TableReplica.TryGetRow<TKey, TRow>()`).

```csharp
// The router: a port of HTTP/2 alone for the stream (cleartext gRPC), its sessions, the app's step 1
builder.Configuration["Kestrel:Endpoints:Stream:Url"] = "http://localhost:5902";
builder.Configuration["Kestrel:Endpoints:Stream:Protocols"] = "Http2";
builder.Services.AddMagicOnion();
builder.Services.AddTaplineMagicOnion();                 // registers the router too (AddTapline)
app.MapTaplineMagicOnion();
app.MapMagicOnionService(typeof(MyStepOne));             // its own: sessions.Find(outletId, nonce) → publisher.SubscribeAsync(…)

// A client
var connection = new TaplineStreamConnection(GrpcChannel.ForAddress("http://localhost:5902"),
    new() { Client = new() { BlobBase = new Uri("http://localhost:5900/_tapline/blob") } });   // blobs: another connection
connection.Connected += (outletId, nonce) => { /* step 1, naming the session, then connection.Client.Subscribe(binding, …) */ };
await connection.RunAsync(stopping);                     // connects, carries, connects again — resuming its session
```

**Moving between routers.** `TaplineRouters` streams from one of several routers (`TaplineRouter`: a channel, and its
blob base) and moves — make before break — when that one drains: it connects to the next, binds a session there, and
hands the app both (`Move`: a `RouterMove` — from, to, the router, why, and a cancellation that ends with the old
router's grace). The app subscribes again there — its step 1 over the new router's channel, naming the new session —
and returns once the new subscriptions stream; then the old call ends, its session let go, and the draining router
stops waiting for it. A router out of reach for `FailoverAfter` (10 s) is moved off too — break before make. Each
connection goes to `Starting` before it connects, for the app's handlers on its client; `Moved` and `MoveFailed` say how
it went. A draining router's stream refuses new sessions — gRPC `Unavailable` — and resumes its own: a client moving
off it still holds its subscriptions there until it lets them go.

```csharp
var routers = new TaplineRouters([new(GrpcChannel.ForAddress("http://a:5902"), blobsA), new(GrpcChannel.ForAddress("http://b:5902"), blobsB)]);
routers.Starting += connection => { /* handlers on connection.Client */ };
routers.Move = async move =>                              // subscribe again on move.To, over move.Router.Channel: then return
{
    var binding = await StepOne(move.Router.Channel, move.To.Session!.Value, move.Cancellation);
    var first = new TaskCompletionSource();
    move.To.Client.Subscribe(binding, new SubscriptionHandler { Table = (s, t, b) => { /* … */ first.TrySetResult(); } });
    await first.Task;                                     // streaming there: the old goes now
};
await routers.RunAsync(stopping);
```

Buffering stays short. The router's side has no queue of its own: each frame is built just before its write, and the
write awaited, so at most one frame waits above Kestrel's response buffer (`MaxResponseBufferSize`, 64 KB) — the
outlet's window, not a queue, holds a slow client's backlog, conflated. `CapSocketBacklog()` caps the stream port's
kernel backlog (`TCP_NOTSENT_LOWAT` on Linux). The client keeps one write in flight and only the newest
acknowledgement waiting. Records sent out of band are fetched over HTTP on a connection apart from the stream's, so a
large blob never queues ahead of frames. A session outlives its call for `DisconnectedHold` (30 s): the next call
resumes it exactly; after that, the client's subscriptions end `Gone` and it starts afresh. The client drops a call
silent for `Silence` (10 s; a heartbeat comes every second) and connects again.

`samples/PivotTable.Headless` is such a client, of the pivot sample: a pivot by a page's address, its cells printed as
they come, post→receipt latency from the provider's clock cell; `--verify` fails on anything that didn't check out.
`--stream a,b` names its routers, in order (`TaplineRouters`); `--drain 5s` drains the first that long in (its ops
port's `POST /ops/tapline/drain`), and verifying, the move must be made with nothing lost.
