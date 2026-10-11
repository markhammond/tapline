# Tapline Observability

A navigable view to introspect the router's state, for supportability.

> [!IMPORTANT]
> The viewer and the other ops endpoints answer only on a port of their own, `Tapline:Ops:Port`, which the app must
> also listen on. To serve them on the app's own port instead, for a demo, set `Tapline:Ops:SharedPortReason` to a
> reason, leave `Tapline:Ops:Port` unset, and run in Development: in any other environment the app won't start. See
> [Sharing the app's port](#sharing-the-apps-port).

## Observability — the router viewer

`app.MapTaplineViewer("/ops/realtime")` maps a viewer of the router's ephemeral state, for production support: pages
linked every which way — each also JSON (`?format=json`), `?refresh=2` to reload.

- **The router**: its identity and its own state (serving, degraded, draining — the change's correlation id); services
  (endpoints, clients waiting for up, up policy, access as last declared); provider endpoints (readiness as last reported, refusing this router or
  not, who answered there, when); provider instances (node id, routes held, serving / draining / degraded — whole, or
  service paths there — / dead, last heard) and those deemed dead lately (the last 32); how many clients and
  subscriptions it serves, linked to their list.
- **Its clients** (`/clients`), a page at a time: each its user (user@realm, insecure if its module says so; or
  anonymous), transport, session, flow and memory, and its first eight subscriptions, each linked to its page (and its
  service).
- **A client** (its outlet — with BlazorRealtime, a Blazor circuit's): its session — its transport, remote address (as
  the server sees it), user agent, created,
  connected since or disconnected when, connections so far — its identity (user, realm, module, insecure or secure,
  session, when proved and when it lapses, claims; or anonymous) — its flow (round trip of the last heartbeat, min and
  smoothed, delivery rate, bytes in flight, caps), its memory against its budget and how long it has waited without
  progress (and its stalls), its subscriptions (each linked: topic, service, instance; what it holds; its last
  record's send time, lag and ack) and its last 64 frames (sent, bytes, subscription records, acked after).
- **A subscription**: its topic, state, class, versions, service, its partition (names, and the ids its client sees),
  provider instance, the client's node id, route,
  epoch — and its **last records** (`OutletOptions.MessageHistory`, 16 by default, rounded up to a power of two; 0 keeps
  none): record number, frame, kind, size, version, **received** (when the topic last changed before it was sent — the
  data's arrival from the provider), **sent** (when its frame was built, at the head of the connection's send queue —
  written to the socket's buffer), the **lag** between, **acked after** (the client's acknowledgement of its frame: the
  round trip, client processing included), what of it is kept (whole, its first 32 bytes, or none: below), and its first
  32 bytes.
- **A record** (each of a subscription's last, its own page): its metadata, and its content where it can be had. In-band,
  as its subscription kept it (below): whole — a hex dump of its first 4 KB, all of it raw (`?format=raw`), rendered — or
  its first 32 bytes (its topic's current state is on the topic's page). Out of band, the
  history holds its handle — the blob's id — so the page looks the blob up in the router's store and, if it's still
  kept, **leases** it (a reference counted like a client's fetch, held only while it's read): its length, a hex dump of
  its first 4 KB, and all of it raw (`?format=raw`). Viewed after it's gone — released after its last fetch, or evicted,
  between being sent and now — the page says so; and it never waits on the store (busy: try again).
- **A topic**: label, kind, version, epoch (closed or not), routes, last change, fault, its **state rendered** (below) —
  and its subscribers, a page at a time.
- **A service**, **a provider endpoint** (and, on request — its link — a health check now, which changes nothing the
  router knows), **a provider instance** (its state, services, endpoint's last report, and the routes it holds, a page
  at a time).

**Drains, by hand.** With the drains mapped (`MapTaplineDrain()`), the viewer offers them as forms, for trying moves by
hand: the router page, a drain of the router (its grace) — then, draining, how its drain went; a service's page and an
instance's, a drain of each node (over, grace). A form posts to the drain and comes back to its page. They're POSTs,
never links, and ops endpoints that change things refuse a browser's cross-site request (`RequireOpsPort()`: its
`Sec-Fetch-Site`, or an `Origin` not their own, 403) — a page elsewhere can't post one; the viewer's own pages, and
scripts, can. No page may frame the viewer's (`frame-ancestors 'none'`): a button can't be clicked through another's.

**Records kept whole.** A subscription's history keeps each record as it was sent — conflated, the subscriber's own,
not every message its provider published — whole by default (`OutletOptions.RecordRetention = Whole`): its bytes in a
ring of their own (`HistoryBytes`, 8 KB), the newest records whole while they fit, older ones their first 32 bytes; a
record larger than the ring, only those. In-band records are at most `OutOfBandThreshold` (16 KB); larger ones go out of
band and are kept by handle. The rings come from a budget of the client's own (`HistoryBudget`, 256 KB: 32 rings),
apart from its memory budget — keeping records for the viewer never costs a client a subscription or a mailbox: a
subscription takes its ring at its first record and gives it back when it ends; with no room left, it keeps its records'
first 32 bytes. The frame builder writes the ring without a lock — one copy of the
record, which is being written to the frame anyway — and a reader copies a record and keeps it only if nothing
overwrote it meanwhile: a record shown whole is the record sent. Retention is chosen at three levels, the lowest
winning: the outlet's (`RecordRetention`), a topic's (`source.Retention`: `Prefix` for large payloads, `None` for ones
the router shouldn't hold), and its provider's ceiling, given in step 1 (`PayloadFormat.Retention`: personal data, say)
— `None` keeps a record's metadata alone (an out-of-band handle still: it is metadata).

**Rendered, not just bytes.** The router shows payloads as JSON, only when a page asks, by the topic's **format**
(`PayloadFormat`). A format is data, not types: its provider gives it in step 1 (`request.Accept(…, format: …)`, so the
router has it before the subscriber's first record; the latest given wins), or it's set where the topic is published
(`source.Format`). A provider in another process sends its JSON form (`format.WriteTo`, `PayloadFormat.Parse`), and the
router reads payloads by it without the provider's code — under Native AOT too (Tapline builds with `IsAotCompatible`;
nothing on this path reflects or generates code).

- **A layout** (`PayloadLayout`) describes a fixed-size payload: each field's name, offset and kind — integers, floats,
  bool, char, bytes (as hex), fixed-width UTF-8 text, nested structs, and .NET's decimal, Guid, DateTime and TimeSpan —
  little-endian. An integer can be scaled (`Scale = 2`: 6500000 is 65000.00) or named (`Names`: 0 is "bid"). A table's
  keys and rows use `KeyLayout` and `Layout`, checked against their sizes: one that doesn't fit isn't used, and the
  page says so. A value or a message uses `Layout` when it's exactly that size. A layout is checked when made: fields
  within the payload, names distinct, nesting at most 8 deep, at most 1,024 fields.
- **A table with nothing declared** is shown by layouts derived from its key and row types (`PayloadLayout.Of<T>()`:
  offsets found by generated code, so exact whether packed or padded; a record struct's fields by its properties'
  names, an enum by its members' names, an inline array as bytes). A provider can derive one, adjust it
  (`layout.With("Price", f => f with { Scale = 2 })`) and send it. Under Native AOT nothing is derived (no code is
  generated there): declare the layout, or the rows are shown as hex.
- **A content type** (`ContentType`) renders a value's or a message's payload: JSON as it is, `text/*` as a string;
  without one, a payload is shown as its bytes, hex.
- **A view URL** (`ViewUrl`) is where the provider renders the topic for a person. When a viewer page asks — never
  otherwise — the router POSTs it the topic's state or a record as JSON (`{"kind": "state" | "record", "topic":
  …, "record": …, "format": …, "rendered": …}`: its own rendering, by the format) and shows the answer framed on the
  topic's and the record's pages (and alone, by its link).
  - **The provider's own numbering comes back to it.** `topic.upstream` is the upstream version the state is as of. A
    record carries `upstream`, the version it's as of, and `upstreamSince`, the one its subscriber was at before it. A
    record may fold several publishes into one, so its changes are those from `upstreamSince` through `upstream`; a
    snapshot replaces whatever came before. Each is null if the publish gave none (`upstreamVersion` on
    `Updater.Complete`, an ingest's `Post` and `PostSnapshot`, or a unit's `Complete`).
  - **A record the router keeps only in part is still offered**, if it has an upstream version: `rendered` is null,
    and the provider finds the record by those versions in whatever it keeps, a journal or an event store, if it can.
    The router's history then needn't hold every record whole for each one to be viewable. A record that's neither
    kept whole nor numbered has no view (404). The answer is the provider's, so it's sandboxed: an opaque
  origin (`Content-Security-Policy: sandbox allow-scripts`, in an `<iframe sandbox>`) — scripts may run, nothing of the
  viewer is theirs — a page (`text/*`) or an image, at most 1 MB, within 2 s, redirects not followed; anything else is
  said so in the frame (502, 504). The URL is absolute http(s), without credentials, and on the provider's own host —
  its endpoint's, or the loopback for one in this process — checked at step 1 (`ArgumentException` otherwise): the
  router posts payloads back to their provider, nowhere else, and only to a URL the provider gave, never one from a
  viewer's request. A provider can serve it from an HTTP server of its own on a port the OS allocates
  (`http://127.0.0.1:0`).

A topic's page shows its format — and how each of a table's layouts was chosen — then its version and upstream
version, then its state (and its provider's view of it, if it has one): its rows (100 by
default, `?rows=`, `?rows=all`), its value, or the messages it keeps, newest first. A record is rendered when it's
whole: out of band, while its blob is kept; in band, while its subscription's history holds it whole (or its first 32
bytes are all of it). A table record's page shows its changes, read from the record's bytes. A subscription's records,
and a record's page, show their upstream versions (`upstreamSince → upstream`).

**Built for thousands of clients a node.** The lists that grow with clients — the clients, a topic's subscribers, an
instance's routes — come a page at a time: 100 by default (`?limit=`; `?limit=all` for every one, say from curl), in the
order the clients came, each page linking the next (`?after=` the last shown — keyset paging, so a page doesn't shift
as clients come and go; JSON carries the next page's link in `next`). Every page is written as it's made — HTML in
chunks of about 16 KB, a JSON list item by item, flushed as it goes — so a list without a limit costs a page's memory,
not the list's, and its first rows arrive before its last are made. Clients are named by a serial number, never
reused in the process (an outlet's id is a slot, reused once it's gone), so a link to one that has ended says so — 404,
"it has ended" — rather than leading to whichever client has its slot now.

The viewer is the router's lowest priority, and best effort. It reads without locks — the router's threads never wait
on it: each subscription's records are a ring written only by its outlet's frame builder (one build at a time per
outlet), each client's frames a ring written by the builder and stamped by the ack path, each slot guarded by a
sequence number (a seqlock: a reader copies a slot and keeps it only if the number was even and unchanged — a slot
being written is left out, not waited for); the router's tables are read from copy-on-write copies; the rest are fields
read as they are. So no page is a coherent snapshot of the whole. Its requests are capped (two at once by default; more
are turned away, 503, not queued — a long page, streaming, still holds its place), its lists come a page at a time,
and it calls a provider only when asked to. The rings are
powers of two (an index is a mask), made at a tap's first record; a record costs a few stores and a fence — measured,
publish + frame + ack ~235 ns a record without history, ~245 with (Release, one core) — and nothing allocates once
they're made (the frame path's zero-allocation test still holds).

**On a port of its own.** The viewer is an ops endpoint, as is any the app marks `RequireOpsPort()`: it answers only on
the ops port (`TaplineOptions.Ops.Port`, or `Tapline:Ops:Port` in configuration; the app listens there too
— `http://127.0.0.1:5899`, say). A request for one arriving on another port finds nothing there — the app's own 404,
not even a 405 for another method: routing decides by the port the request's connection arrived on, not its Host
header. The app doesn't start where that can't hold: an ops endpoint mapped but no ops port, an ops port the server
doesn't listen on, or the ops port alone (the app would share it). Listened on beyond the loopback without
authorization on the endpoints, it warns.

### Sharing the app's port

For a demo on one port, the ops endpoints can answer on every port the app listens on,
the client-facing one too. That takes three things, or the app doesn't start:

- **A reason:** `Tapline:Ops:SharedPortReason` (`TaplineOptions.Ops.SharedPortReason`), not blank — "non-production
  demo", say.
- **No ops port:** `Tapline:Ops:Port` unset. Both set is refused: a port of their own, or a shared one.
- **Development:** `ASPNETCORE_ENVIRONMENT=Development`. In any other environment a shared port is refused, and the app
  says to give the ops endpoints a port of their own.

```bash
ASPNETCORE_ENVIRONMENT=Development Tapline__Ops__SharedPortReason="non-production demo" dotnet run --project MyApp
```

The reason is logged at start (a warning), shown on every viewer page (a banner), first in its JSON (`sharedPort`) and
in a header (`Tapline-Ops-Shared-Port`). The BlazorRealtime sample's `shared-port` launch profile runs it so. Shared,
the ops endpoints are as reachable as the app: the drains (`MapTaplineDrain()`) and any the app marks
`RequireOpsPort()` too, though those that change things still refuse a browser's cross-site request.

A port is not an access policy: users are anonymous (no authentication yet), and the viewer shows their addresses, the
provider topology and payloads. Who reaches the ops port is the network's to decide; what they may see, the app's
(`RequireAuthorization` on the group `MapTaplineViewer` returns). The sample listens on it on the loopback only by
default (`WAVEFORM_OPS_ENDPOINTS`): `http://localhost:5899/ops/realtime`.
