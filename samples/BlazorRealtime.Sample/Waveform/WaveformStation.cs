using System.Diagnostics;
using Tapline;

namespace BlazorRealtime.Sample.Waveform;

/// <summary>
///     A server-driven audiovisual stream, computed and encoded once for every viewer. On one carrier clock (milliseconds
///     since its deployment's origin — shared by every station of it) the station plays a MIDI file and draws the Unknown
///     Pleasures ridge field to it.
///     <list type="bullet">
///         <item>
///             <see cref="Scene" />: a frame of the field 60 times a second, to the music of that moment — one last value:
///             each
///             viewer gets the newest it can take (conflation), at whatever rate its path allows. <see cref="SceneLite" />
///             and
///             <see cref="SceneTiny" /> are the same field from coarser grids, for viewers on slow paths (each viewer's
///             component
///             picks, from its circuit's flow-control estimates).
///         </item>
///         <item>
///             <see cref="Notes" />: the music as its notes, synthesized in the browser — rows of up to 200 ms published
///             250 ms
///             ahead of their carrier time into a rolling window, a table of <see cref="MidiProgram.Window" /> rows keyed
///             by row
///             number modulo the window. Each viewer receives the rows it hasn't seen; one further behind than the window
///             gets just
///             the newest, the older gone. Stale music is squashed per viewer by the table itself, never queued. Viewers
///             tap it on the
///             High class (<c>Outlet.Tap(…, lane: Lane.High)</c>), so it never waits behind the ridges' frames.
///         </item>
///     </list>
///     <para>
///         The station is a "waveform" service's data provider (<see cref="IDataProvider" />) — one of a blue/green pair
///         (<see cref="WaveformDeployment" />): named <c>waveform-blue</c> or <c>waveform-green</c>, at an endpoint of
///         that name (in
///         process), with topics of its own, running as an instance (<see cref="Instance" />: a random id per start).
///         Starting, it
///         catches up — the music played from the origin to now, unpublished, so its rows are numbered and timed as its
///         predecessor's, and the field's last five seconds of note trails — then registers (<see cref="Ready" />). The
///         router
///         sends it step 1 of each subscription, records the instance on the route it accepts, relays the route's step 2
///         and end
///         to it, and checks its health while it holds routes; the station keeps its routers (
///         <see cref="RouterSessions" />),
///         heartbeats them in turn, and refuses one it presumes dead. It drains (<see cref="DrainAsync" />) when its
///         successor is
///         up. A subscription names a topic —
///         <c>full</c>, <c>lite</c> or <c>tiny</c> (the ridges, shared: every subscriber the same frames, each resuming
///         from the
///         next — no snapshot) or <c>notes</c> (the music's window: a snapshot) — and the station accepts it onto that
///         shared
///         topic, recording the epoch. It produces for a topic only while it has subscribers: a publish answered 202 for
///         the
///         epoch it recorded last stops it (one from an earlier epoch crossed a new subscriber, and is ignored) — no
///         ridges are
///         computed while no tier is subscribed, no music rows published while nobody listens. It ignores unsubscribe
///         notices
///         (<see cref="IDataProvider.UnsubscribeAsync" />, the default; nor needs step 2's,
///         <see cref="IDataProvider.ActivateAsync" />):
///         its next frame, 16 ms on, finds out as well.
///     </para>
/// </summary>
public sealed class WaveformStation : BackgroundService, IDataProvider
{
    public const int SceneRate = 60;

    private const double FrameMs = 1000.0 / 60, LeadMs = 250, TrailMs = 5000;
    private readonly Topic _full, _lite, _tiny, _notes;
    private readonly Lock _gate = new();

    private readonly ILogger<WaveformStation> _log;
    private readonly MidiProgram _midi;
    private readonly long _origin; // carrier zero: a Stopwatch timestamp, the deployment's
    private readonly Publisher _publisher;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly RouterSessions _routers;
    private double _computeMs, _sceneBytes, _liteBytes, _tinyBytes;
    private DateTimeOffset? _drainUntil; // draining (one way) until then
    private readonly LoadMeter _load = new(); // its work over the time that passes: its health reports' load
    private long _scenes;
    private double _startedAt; // carrier time it went on air

    /// <param name="name">Its instance name — and its endpoint's, and its topics' sources'.</param>
    /// <param name="origin">Carrier zero, a <see cref="Stopwatch" /> timestamp: the deployment's, shared by its stations.</param>
    public WaveformStation(Publisher publisher, ILogger<WaveformStation> log, IConfiguration config, string name,
        long origin)
    {
        _log = log;
        _publisher = publisher;
        _origin = origin;
        Endpoint = ProviderEndpoint.InProcess(name);
        Instance = ProviderInstance.Start(name, Endpoint);
        Notes = publisher.Table<byte, NoteRow>($"{name}:notes");
        Notes.Delivery = Delivery.Inline;
        Notes.Compressible = false;
        // The music (Waveform:Music, or WAVEFORM_MUSIC): "resonance" — HOME's Resonance in oni's MIDI arrangement, bundled (the
        // default; see NOTICE.md) — or the path of a MIDI file of your own.
        var music = config["Waveform:Music"] ?? Environment.GetEnvironmentVariable("WAVEFORM_MUSIC") ?? "resonance";
        if (music.Equals("resonance", StringComparison.OrdinalIgnoreCase))
        {
            using var file =
                typeof(WaveformStation).Assembly.GetManifestResourceStream("Waveform.Music.resonance-oni.mid")
                ?? throw new InvalidOperationException("The bundled MIDI file is missing.");
            var bytes = new byte[file.Length];
            file.ReadExactly(bytes);
            _midi = new MidiProgram(MidiFile.Read(bytes), Notes)
                { Title = "Resonance", Credit = "HOME (Randy Goffe), arranged by oni" };
        }
        else
        {
            _midi = new MidiProgram(MidiFile.Read(File.ReadAllBytes(music)), Notes)
                { Title = Path.GetFileNameWithoutExtension(music) };
        }

        Scene = publisher.LastValues<string>(name)["scene"];
        SceneLite = publisher.LastValues<string>(name)["scene-lite"];
        SceneTiny = publisher.LastValues<string>(name)["scene-tiny"];
        _full = new Topic(Scene, "waveform:scene", InitialState.None);
        _lite = new Topic(SceneLite, "waveform:scene-lite", InitialState.None);
        _tiny = new Topic(SceneTiny, "waveform:scene-tiny", InitialState.None);
        _notes = new Topic(Notes, "waveform:notes", InitialState.Snapshot);
        _routers = new RouterSessions(() => Instance)
        {
            Lost = (router, routes) =>
                log.LogInformation("Waveform station {Name}: router {Router} presumed dead, {Routes} routes forgotten",
                    name, router, routes.Length)
        };
    }

    /// <summary>Where the router reaches the station: in this process, by its name.</summary>
    public ProviderEndpoint Endpoint { get; }

    /// <summary>The station as it runs: its name, endpoint, and an id drawn when it started (<see cref="Restart" />: another).</summary>
    public ProviderInstance Instance { get; private set; }

    /// <summary>Caught up, and registered with the router: taking subscribers.</summary>
    public Task Ready => _ready.Task;

    /// <summary>Whether it is draining (no new subscribers).</summary>
    public bool Draining
    {
        get
        {
            lock (_gate)
            {
                return _drainUntil is not null;
            }
        }
    }

    /// <summary>Viewers' routes it holds, over its topics.</summary>
    public int Routes => _full.Source.Routes + _lite.Source.Routes + _tiny.Source.Routes + _notes.Source.Routes;

    /// <summary>
    ///     The music's notes' rolling window, row number modulo <see cref="MidiProgram.Window" /> →
    ///     <see cref="NoteRow" />.
    /// </summary>
    public Table<byte, NoteRow> Notes { get; }

    /// <summary>
    ///     The piece playing, who made it (null: unknown), its length in bars, and bytes of its rows published per
    ///     second.
    /// </summary>
    public string Title => _midi.Title;

    public string? Credit => _midi.Credit;
    public int Bars => _midi.Bars;
    public double NoteBytesPerSecond => _midi.BytesPublished / Math.Max(1, (CarrierMs - _startedAt) / 1000);

    /// <summary>The newest frame of the ridge field (see <see cref="Ridges" />).</summary>
    public LastValueSource Scene { get; }

    /// <summary>The same, lite: every other line and point.</summary>
    public LastValueSource SceneLite { get; }

    /// <summary>The same, tiny: every fourth line and point.</summary>
    public LastValueSource SceneTiny { get; }

    /// <summary>Carrier time: since the deployment's origin (every station of it on the same clock).</summary>
    public double CarrierMs => Stopwatch.GetElapsedTime(_origin).TotalMilliseconds;

    public long ScenesPublished => Interlocked.Read(ref _scenes);

    /// <summary>Scene frames published per second since it went on air.</summary>
    public double ScenesPerSecond => ScenesPublished / Math.Max(1, (CarrierMs - _startedAt) / 1000);

    /// <summary>Server time to compute and encode a scene frame, ms (smoothed).</summary>
    public double ComputeMs => Volatile.Read(ref _computeMs);

    /// <summary>Bytes per scene frame, full, lite and tiny (smoothed).</summary>
    public double SceneBytes => Volatile.Read(ref _sceneBytes);

    public double LiteSceneBytes => Volatile.Read(ref _liteBytes);
    public double TinySceneBytes => Volatile.Read(ref _tinyBytes);
    public int Bar { get; private set; }
    public int Starts { get; private set; }

    /// <summary>
    ///     A router connected: kept (its sessions: <see cref="RouterSessions" />), and heartbeated while it holds routes
    ///     here.
    /// </summary>
    ValueTask IDataProvider.ConnectAsync(IRouterChannel router, CancellationToken cancellationToken)
    {
        _routers.Connected(router);
        return default;
    }

    /// <summary>
    ///     Step 1: onto the topic asked for (a shared one); its epoch recorded, the station producing for it. A router it
    ///     presumes dead (an older instance of its name, or silent) is refused — a NACK; while draining, nothing is accepted.
    /// </summary>
    ValueTask IDataProvider.SubscribeAsync(SubscribeRequest request, CancellationToken cancellationToken)
    {
        if (!_routers.Admits(request.Router))
        {
            request.Nack();
            return default;
        }

        var topic = request.Parameters switch
        {
            "full" => _full, "lite" => _lite, "tiny" => _tiny, "notes" => _notes, _ => null
        };
        if (topic is null) return default; // no such topic: refused
        lock (_gate)
        {
            if (_drainUntil is not null) return default; // draining: no new subscribers
            topic.Epoch = request.Accept(topic.Source, Instance, topic.Name, topic.Initial);
            topic.Live = true;
        }

        _routers.Accepted(request.Router, request.Route);
        return default;
    }

    /// <summary>A route ended: forgotten in its router's session (the topics learn of it from their next publish's 202).</summary>
    ValueTask IDataProvider.UnsubscribeAsync(Unsubscription notice, CancellationToken cancellationToken)
    {
        _routers.Ended(notice.Router, notice.Route);
        return default;
    }

    /// <summary>
    ///     The router's health check: the station as it is now (after a <see cref="Restart" />, not the instance expected),
    ///     serving or draining — and refusing a router it presumes dead. Reports made once per state, not per check.
    /// </summary>
    ValueTask<HealthReport> IProviderHealth.CheckHealthAsync(HealthProbe probe, CancellationToken cancellationToken)
    {
        var refused = _routers.Refuses(probe.Router);
        lock (_gate) // (its drain, as it stands; a struct, from memory: nothing allocated)
        {
            return new ValueTask<HealthReport>(new HealthReport(Instance,
                _drainUntil is null ? ProviderStatus.Serving : ProviderStatus.Draining, refused, _drainUntil,
                Load: _load.Load));
        }
    }

    /// <summary>
    ///     Drains — one way: no new subscribers; the routers move its viewers over <paramref name="over" />, evenly, each told
    ///     to move and given <paramref name="grace" /> to subscribe again (onto its successor) before what it still holds here
    ///     is closed. The station stays on air until then (<see cref="WaveformDeployment" /> retires it after).
    /// </summary>
    public async Task DrainAsync(TimeSpan over, TimeSpan grace)
    {
        lock (_gate)
        {
            if (_drainUntil is not null) return; // draining already: one way, once
            _drainUntil = DateTimeOffset.UtcNow + over;
        }

        _log.LogInformation("Waveform station draining over {Seconds} s, {Grace} s grace: {Instance}",
            over.TotalSeconds, grace.TotalSeconds, Instance);
        await _routers.DrainAsync(over, grace);
    }

    /// <summary>
    ///     As if the station's process restarted: a new instance id, producing nothing until subscribed again. The router
    ///     finds
    ///     out from its next heartbeat, closes the old instance's routes and tells their clients the service is down; the view
    ///     subscribes again, onto this instance. (The sample's <c>POST /waveform/station/restart</c>, when enabled.)
    /// </summary>
    public void Restart()
    {
        lock (_gate)
        {
            Instance = ProviderInstance.Start(Instance.Name, Endpoint);
            foreach (var topic in (ReadOnlySpan<Topic>)[_full, _lite, _tiny, _notes])
            {
                topic.Live = false;
                topic.Epoch = 0;
            }

            _drainUntil = null;
        }

        _routers.Restarted();
        _log.LogInformation("Waveform station restarted (simulated): now {Instance}", Instance);
    }

    /// <summary>A publish's answer: 202 for the epoch last accepted on stops the topic (earlier epochs': stale, ignored).</summary>
    private void Acknowledged(Topic topic, PublishResult result)
    {
        if (result.HasListeners) return;
        lock (_gate)
        {
            if (topic.Epoch == result.Epoch) topic.Live = false;
        }
    }

    /// <summary>The topics, for the stats (i): routes, epoch, producing or not.</summary>
    public string TopicsLine()
    {
        static string Of(Topic t)
        {
            return $"{t.Name["waveform:".Length..]} {t.Source.Routes}{(t.Live ? "" : " (stopped)")}";
        }

        lock (_gate)
        {
            return $"topics: {Of(_full)} · {Of(_lite)} · {Of(_tiny)} · {Of(_notes)} (routes)";
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var thread = new Thread(() => Run(stoppingToken))
            { IsBackground = true, Name = "waveform", Priority = ThreadPriority.AboveNormal };
        thread.Start();
        return Task.CompletedTask;
    }

    private void Run(CancellationToken ct)
    {
        var ridges = new Ridges();
        var cues = new Cue[64];
        var spectra = new float[64][]; // the notes sounding at each of those frames, for the ridges
        for (var i = 0; i < spectra.Length; i++) spectra[i] = new float[Ridges.Width];
        long musicFrames = 0;
        uint tick = 0;
        double scenePeriod = 1000.0 / SceneRate, nextBeat = 0;
        try
        {
            // Catching up: the music from the origin to now (and its lead), unpublished — rows numbered and timed as they
            // were by any station of this deployment — and the field's memory of the last few seconds' notes.
            var start = CarrierMs;
            _midi.Publishing = false;
            for (var catchUp = (long)((start + LeadMs) / FrameMs); musicFrames < catchUp; musicFrames++)
            {
                var (low, high, filter) = _midi.Frame(musicFrames * FrameMs, (musicFrames + 1) * FrameMs);
                cues[musicFrames & 63] = new Cue(low, high, filter, _midi.Bar);
                _midi.Spectrum(musicFrames * FrameMs, spectra[musicFrames & 63]);
                var at = musicFrames * FrameMs;
                if (at >= start - TrailMs && at < start)
                    ridges.Advance(at / 1000, low, high, spectra[musicFrames & 63]);
            }

            var nextScene = _startedAt = CarrierMs;
            _publisher.Provide("waveform", Endpoint, this);
            _ready.TrySetResult();
            _log.LogInformation(
                "Waveform station {Name} on air at {At:F0} s (caught up in {Took:F0} ms): {Title} as notes, ridges at {Fps} frames/s",
                Instance.Name, nextScene / 1000, nextScene - start, _midi.Title, SceneRate);
            while (!ct.IsCancellationRequested)
            {
                var busy = _load.Measure(); // (each pass to its sleep: the station's load, its health reports')
                var now = CarrierMs;

                // The music runs LeadMs ahead of the carrier clock: each row leaves before it is due — published while the
                // notes topic has subscribers.
                _midi.Publishing = _notes.Live;
                while (musicFrames * FrameMs < now + LeadMs)
                {
                    var (low, high, filter) = _midi.Frame(musicFrames * FrameMs, (musicFrames + 1) * FrameMs);
                    cues[musicFrames & 63] = new Cue(low, high, filter, _midi.Bar);
                    _midi.Spectrum(musicFrames * FrameMs, spectra[musicFrames & 63]);
                    musicFrames++;
                }

                Starts = _midi.Starts;
                if (_midi.TakeAcknowledgement() is { } notes) Acknowledged(_notes, notes);
                if (now >= nextBeat) // heartbeats go both ways: the station's, to its routers
                {
                    _routers.HeartbeatRouters();
                    nextBeat = now + 1000;
                }

                // A frame of the field, on the carrier clock, to the music playing now — computed only while a tier has
                // subscribers, encoded and published only for those that have.
                if (now >= nextScene)
                {
                    var started = Stopwatch.GetTimestamp();
                    var cue = cues[(long)(now / FrameMs) & 63];
                    Bar = cue.Bar;
                    bool full = _full.Live, lite = _lite.Live, tiny = _tiny.Live;
                    if (full || lite || tiny)
                    {
                        ridges.Compute(now / 1000, cue.Low, cue.High, Math.Min(1, cue.Filter),
                            spectra[(long)(now / FrameMs) & 63]);
                        if (full)
                            Acknowledged(_full,
                                Scene.Publish(ridges.Encode(tick, now, cue.Bar), Delivery.Inline,
                                    false)); // deflated once, here
                        if (lite)
                            Acknowledged(_lite,
                                SceneLite.Publish(ridges.EncodeLite(tick, now, cue.Bar), Delivery.Inline, false));
                        if (tiny)
                            Acknowledged(_tiny,
                                SceneTiny.Publish(ridges.EncodeTiny(tick, now, cue.Bar), Delivery.Inline, false));
                        Volatile.Write(ref _computeMs,
                            _computeMs * 0.98 + Stopwatch.GetElapsedTime(started).TotalMilliseconds * 0.02);
                        if (full) Volatile.Write(ref _sceneBytes, _sceneBytes * 0.98 + ridges.FrameBytes * 0.02);
                        if (lite) Volatile.Write(ref _liteBytes, _liteBytes * 0.98 + ridges.LiteFrameBytes * 0.02);
                        if (tiny) Volatile.Write(ref _tinyBytes, _tinyBytes * 0.98 + ridges.TinyFrameBytes * 0.02);
                        Interlocked.Increment(ref _scenes);
                    }
                    else
                    {
                        ridges.Advance(now / 1000, cue.Low, cue.High,
                            spectra[(long)(now / FrameMs) & 63]); // nobody watching: its memory kept
                    }

                    tick++;
                    nextScene += scenePeriod;
                    if (now - nextScene > 2 * scenePeriod)
                        nextScene = now + scenePeriod; // fell behind: restart the phase
                }

                busy.Dispose();
                var wait = Math.Min(nextScene, musicFrames * FrameMs - LeadMs) - CarrierMs;
                if (wait >= 1) Thread.Sleep((int)wait);
                else if (wait > 0) Thread.Yield();
            }
        }
        catch (Exception e)
        {
            _log.LogError(e, "Waveform station failed");
            _ready.TrySetException(e);
        }
    }

    /// <summary>
    ///     A topic of the station's, and whether it is producing for it: since the epoch it last accepted a subscriber
    ///     on.
    /// </summary>
    private sealed class Topic(Source source, string name, InitialState initial)
    {
        public readonly InitialState Initial = initial;
        public readonly string Name = name;
        public readonly Source Source = source;
        public long Epoch;
        public bool Live;
    }

    private readonly record struct Cue(float Low, float High, float Filter, int Bar);
}