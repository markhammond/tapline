# BlazorRealtime.Sample — animation synchronized to streaming music

A field of ridge lines moving to music, served by a Blazor app over each page's own circuit: the server plays a MIDI
arrangement and computes the field, 60 frames a second, encoded once for every viewer.

```bash
dotnet run -c Release --project samples/BlazorRealtime.Sample
```

Then open `http://localhost:5898` and turn the speaker on. The router's ops viewer is on its own port,
`http://localhost:5899/ops/realtime`, on this machine only.

<p align="center">
  <img src="../../assets/waveform.jpeg" width="85%" alt="Joy Division x Resonance">
</p>

## Running it

To try a slow link, add `?bwlimit=0.5` (KiB/s, read as rsync's `--bwlimit`), which caps that page's stream. The
waveform is at `/`, and at `/waveform`, its old address.

It listens on `http://localhost:5898` unless told otherwise — `--urls`, `ASPNETCORE_URLS` — and its ops endpoints on a
port of their own, `http://localhost:5899`. Having no authentication, it says so at start with the addresses it listens
on, warning when they reach beyond this machine, and it keeps a dropped client's circuit, and so its subscriptions, 30 s
rather than Blazor's 3 minutes.

## Move to the rhythm over one circuit

A server-driven stream, to show (and test) what the library is for. The server plays a piece from a MIDI arrangement
and computes a field of ridge lines that moves to it (77 lines × 160 points), computed and encoded once for every
viewer, on one carrier clock.

- **Stations, blue/green.** The stations are the `waveform` service's [data providers](../../docs/provider.md). One is
  on air, `waveform-blue` or `waveform-green`: in process, at an endpoint of that name, with topics of its own.
- **Shared topics, produced on demand.** Each resolution tier, and the music, is a topic every viewer shares, produced
  only while it has listeners. A tier stops on a 202 for the epoch it last accepted a subscriber on, and starts again
  on the next subscribe.
- **A deploy** (`POST /waveform/station/deploy?seconds=10&grace=5`) starts the other colour alongside, on the same
  carrier clock (the deployment's origin). It catches up first: the music replayed from the origin, unpublished, so its
  rows are numbered and timed as the first's, and the field's last five seconds of note trails. Then it registers, and
  the first drains: its pages are told to move one at a time over the ten seconds. Each subscribes again onto the new
  station and drops the old subscription once the new one streams (`tapline.replace`). The ridges' frames are ordered by
  carrier time and the music's rows by number, so the overlap neither shows nor sounds. Then the old station is
  retired: stopped, its endpoint withdrawn.
- **A restart** (`POST /waveform/station/restart`) tells the page the service is down. It waits until it's told the
  service is up again (the router's next heartbeat, about a second), then subscribes again: the ridges and, if on, the
  music.
- **Operator endpoints.** Deploy and restart have no authentication, so, like the router's viewer, they answer on the
  ops port alone (`Tapline:Ops:Port`, 5899), and on one network interface: `WAVEFORM_OPS_ENDPOINTS`, an address or an
  interface's name (`en0`, `eth0`: its addresses), `localhost` by default (the loopback, IPv4 and IPv6), or `off`.
  Asked for on the app's port, they aren't there (404).
- **One port, for a demo.** The `shared-port` launch profile runs it in Development with
  `Tapline:Ops:SharedPortReason` set to "non-production demo": the ops endpoints answer on the app's port too, and every
  viewer page says so, and why:

  ```bash
  dotnet run --project samples/BlazorRealtime.Sample --launch-profile shared-port
  ```

The speaker turns the music on; play/pause (or `p`) unsubscribes from both — the ridges' topic and the music's, through
the station — and play subscribes again: the stream drops to heartbeats, and on play both are live again at once,
nothing to catch up (the ridges resume from their next frame; the music joins afresh, its delay set from what the path
does now, not raised by the gap). Hovering shows frames a second and the data rate; `i` adds the full stats (the
server's fetched only while they show: on a slow path a Blazor render a second would be a good part of the link, and
outside the stream's flow control); a link there sets a slow link (`?bwlimit=16`, or back to no limit: a full page
load, since the cap is read as the circuit opens).
- *Ridges*: a last value, 60 frames a second, encoded once (`compressible: false` on the way out: no per-client
  deflate) by the sample's own near-lossless codec (`RidgeCodec`), each frame decodable alone: ~1.0 KB full, 0.7 lite,
  0.27 tiny; ~0.3 ms to encode all three, ~0.4 ms to decode one. The three tiers carry the same lines at less detail —
  full every other line, lite every fourth, tiny every eighth line and other point — so a change of tier changes the
  detail and nothing more. The browser shows the field as it was a moment ago — far enough behind that the frame after
  that moment has nearly always arrived (the 95th percentile, over 5 s, of how long after one frame's moment the next
  comes) — blended between the frames either side of it, so the motion stays continuous however few frames get
  through: on a fast path about a frame's interval behind; at 16 kbit/s, ~0.4–0.6 s. Each viewer starts tiny — a
  larger frame would hold a slow path longer before it could be measured — and its component checks 0.4 s in, then once
  a second, from its own circuit: lite when flow control paces it below ~40 KiB/s (all 60 full frames take ~60) or
  `bwlimit` caps it there, tiny below ~6 KiB/s. Down only after 3 s in a row that call for it (a moment's stall in the
  browser isn't a slower path); up with twice the headroom, and a move up that falls back within 30 s makes the next
  try wait twice as long (5 s, up to 2 min) — no flapping. The page reports its display refresh, and whether it is
  being shown at all: a display slower than the station gets that many frames a second (`SetMaxFramesPerSecond`), a
  hidden or browser-throttled page one, the music unaffected (High).
- *Music*: the notes on the carrier clock, each at its start with its voice and duration (6 bytes), in rows of up to
  200 ms or 16 notes, each playable on its own, published 250 ms ahead into a rolling window (a 24-row table, row number
  mod 24) tapped on the High class (the ridges making way for it on a slow path; `?priority=off` taps it in Streaming
  instead, to compare): a viewer gets the rows it hasn't seen; one further behind than the window gets just the newest
  (stale music squashed per viewer by the table, never queued) — ~0.45 KiB/s for a dense 13-track arrangement. The
  browser plays each row at its time on the carrier clock (offset = the smallest arrival − stamp seen) plus a playout
  delay: the audio output's own latency (as the browser reports it — Bluetooth headphones add ~200 ms), plus a jitter
  buffer of what the path needs and no more: set from 300 ms of rows, before the first note — the latest arrival plus
  20 ms, 10% to spare; raised before scheduling a row that would play late (a pause, not a stutter); lowered a frame
  (1/60 s) every quarter second toward the latest arrival of the last 10 s; at least 20 ms; at most 2 s in all. A slow
  path shows, as a pause, rather than being hidden behind a deep buffer. The station composes the music 250 ms ahead (it
  is playing a piece, not taking it live), so on a good path rows arrive before they are due and the buffer stays at
  its 20 ms. While the music plays the ridges are shown at least that much behind too, in step with it — unless their
  frames are too sparse to blend that close, as on a 16 kbit/s path, where they trail it. The browser synthesizes the
  notes (Web Audio). Bundled: *Resonance* by HOME, in oni's MIDI arrangement — free to use, its composer says; the
  arrangement's terms unknown, so it is not under the sample's license (see the sample's NOTICE.md). `Waveform:Music`
  (or `WAVEFORM_MUSIC`) set to the path of a MIDI file of your own plays that instead.

## As measured

In Chromium, from a Release build:

- **On localhost:** 60 ridge frames a second, all shown, at ~70 KiB/s (~147 deflated; ~290 at 120 frames a second).
  Blended 23 ms behind: age at paint p50 23.6 ms, p99.5 25.0 ms. Shown unblended, the newest frame is p50 6–15 ms old,
  a frame's interval less, at half the motion's rate.
- **The music:** ~0.5 KiB/s, every row on time. Through Bluetooth headphones, 220 ms behind (195 ms the output, 25 the
  buffer), never late.
- **The station:** ~1.3 ms of one core per frame, for all viewers together.
- **Capped:** with `?bwlimit=16`, lite ridges at ~20 fps (~9 deflated); with `?bwlimit=4`, tiny at ~13.
- **Through a 16 kbit/s proxy:** tiny ridges at ~4 fps with the music off, blended ~0.4 s behind; ~2 fps and ~0.6 s
  with it on. The music is 89 ms behind (31 ms of it the output), its rows at worst 30 ms after due, never late. With
  `?priority=off`, it's 1,003 ms behind, its rows up to 663 ms after due.

It's a showcase of conflation, priority and encode-_once_ fan-out multiplexed over a single TCP connection. Where some
packet loss is acceptable, as it is for live audio, WebRTC over an unreliable transport (UDP, Opus, loss concealment)
is the better fit: a lost packet doesn't stall everything behind it.

## License

Sample artefacts licensed under CC BY-NC-SA 4.0: see [LICENSE.md](LICENSE.md) and [NOTICE.md](NOTICE.md). The
libraries are not covered.
