# Tapline Wire Format

## Wire format (inside `Tap.Frame(seq, bin)`, little-endian)

```
Heartbeat  u8 0x01 | u32 hbSeq | i64 serverUnixMicros
RouterChange u8 0x0D | u8 state(0 serving, 1 degraded, 2 draining) | u8[16] correlation | u32 controls | tail
                                                                  behind a heartbeat until one it rode with is echoed,
                                                                  and on each new connection; controls: the control
                                                                  messages the router takes, a bit each (1 << ControlKind);
                                                                  tail: degraded u8 len | reason (≤ 255 bytes), draining
                                                                  u32 graceMs (left)
Value      u8 0x03 | u32 tapId | u16 len | payload
Table      u8 0x04 | u32 tapId | u8 flags(bit0 reset, bit1 degraded) | i64 fromSeq | i64 toSeq | u32 checksum | u32 n
           | u16 keySize | u16 rowSize | n × ( u8 op(0 upsert, 1 delete) | key | row )
Error      u8 0x05 | u32 tapId | u16 code | i64 lastGoodVersion | u16 len | payload       terminal for the tap
Group      u8 0x06 | u32 tapId | u8 flags | u16 count | u32 bodyLen | member records (Table/Value; tapId = member index)
OobRef     u8 0x07 | u32 tapId | u8 inner | u8 mode(0 transparent, 1 reference; | 0x80 content-
           addressed: blobId = SHA-256(blob)[0..16], verify) | u8[16] blobId | u8[16] tag | u32 length | u32 ticket
                                                  blob = payload (Value) or record with tapId 0 (Table/Group/TopicMessage)
Service    u8 0x08 | u8 status | u16 len | payload                0 down, 2 drained: u8[16] node | u8[16] correlation;
                                                                  4 degraded, 5 recovered: as down | scope (UTF-8: a
                                                                  service path as the client sees it; none: the node);
                                                                  3 draining: as down | u32 graceMs; 1 up: u8[16]
                                                                  correlation | the service's name
TopicMessage u8 0x09 | u32 tapId | u8 flags(bit0 gap, bit1 mailbox, bit2 stale) | i64 seq | u32 len | payload
Ended      u8 0x0A | u16 code | u32 fromTapId | u32 toTapId       the router ended those subscriptions: all up to an id
                                                                  (stalled), or one (asked after at a rebind, gone)
Rendezvous u8 0x0B | u32 tapId | u8 flags(bit0 missed) | i64 seq | u8 len | correlation (≤ 64 bytes)
                                                                  through the subscription's mailbox, strictly after
                                                                  its state reached seq
Refused    u8 0x0C | u8 kind | u8[16] correlation | u16 len | service | u16 len | reason
                                                                  a subscription refused, saying why (≤ 256 and 1,024
                                                                  bytes) and of what kind (0 refused, 1 unauthenticated,
                                                                  2 forbidden); resent till acked, logged once
Auth       u8 0x0E | u8 status | u32 exchange | u16 round | u16 len | text | u32 len | payload
                                                                  a sign-in's answer: 0 continue (payload: the
                                                                  provider's next message), 1 signed in (text: the
                                                                  identity as JSON, its renewal policy and warnAt;
                                                                  payload: a ticket), 2 refused (text: why), 3
                                                                  redirected (text: the router to renew on; payload:
                                                                  a ticket it takes); round 0xFFFF: refused before
                                                                  read; resent till acked, taken once
Session    u8 0x0F | u8 status | u8 renewal | u8[16] correlation | u8[16] session | i64 expiresAt | u16 len | reason
                                                                  the session: 0 expiring (warned once, ahead), 1
                                                                  ended (lapsed); renewal 0 silent, 1 reauthenticate,
                                                                  2 fixed; expiresAt Unix ms; resent till acked,
                                                                  taken once
Client →   send('Tap.Activate', outletId, nonce, tapId)           step 2 of subscribe; idempotent
           send('Tap.Resume', outletId, nonce, lastFrame, hbSeq)  binding again: the last frame received
           send('Tap.Ack', outletId, nonce, frameSeq, hbSeq)      cumulative, idempotent
           send('Tap.Resync', outletId, nonce, tapId)             delta failed sequence or checksum
           send('Tap.Close', outletId, nonce, tapId, code)        0 = unsubscribe; else the client rejects the stream
           send('Tap.Fetched', outletId, nonce, 0, tapId, ticket) out-of-band record applied
           send('Tap.Sync', outletId, nonce, tapId, syncId)       up to date? answered by a rendezvous, correlation =
                                                                  syncId (u32 LE, 4 bytes), after the source's version
                                                                  as the router received it
           send('Tap.Auth', outletId, nonce, bytes)               a sign-in's message (≤ 16 KB): Begin u8 1 | u32
                                                                  exchange | u8 flags(bit0 renewal) | u8 len | realm |
                                                                  u8 len | module | payload; Continue u8 2 | u32
                                                                  exchange | payload; Cancel u8 3 | u32 exchange
HTTP       GET {blobBase}/{blobId hex}/{outletId}/{tag hex}       the blob's bytes; Range supported
           GET {moduleBase}/{moduleId hex}                        a stale-data module (application/wasm): moduleId =
                                                                  SHA-256(module)[0..16], verify
```

Checksum = Σ hash(key bytes, row bytes) mod 2³² over live rows — order-independent, O(1) to maintain on both ends,
32-bit ops only (the JS client uses `Math.imul`). Fault codes: 1 Unknown, 2 VersionMismatch, 3 ChecksumMismatch,
4 FoldFailed, 5 SourceFailed, 6 ServiceDown, 7 ServiceDrained, 8 Overflowed, 9 Stalled, 10 Gone, 11 NoInitialState,
12 IdentityChanged, 13 SessionExpired.
