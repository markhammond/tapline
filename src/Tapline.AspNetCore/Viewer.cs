using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Tapline.AspNetCore;

/// <summary>
///     A viewer of this router's ephemeral state, for observability and production support — pages linked every which way:
///     the router (its services, provider endpoints and instances, the instances lately deemed dead, and how many clients
///     and subscriptions it serves); its clients, a page at a time; a client (its session — its transport, where from,
///     since
///     when, its connections — its flow, subscriptions and last frames); a subscription (its topic, service, provider
///     instance, route,
///     and last records: when the data arrived from the provider, when it was sent, the lag between, when the client
///     acknowledged it); a topic (its version, epoch, and who subscribes, a page at a time); a service, a provider
///     endpoint
///     (its readiness as last reported — or checked now, on request) and a provider instance (its health, services, and
///     the
///     routes it holds, a page at a time). Each page is also JSON (<c>?format=json</c>, or <c>Accept: application/json</c>
///     );
///     <c>?refresh=2</c> reloads one every two seconds.
///     <para>
///         Built for thousands of clients a node. The lists that grow with clients — the clients, a topic's subscribers,
///         an
///         instance's routes — come a page at a time (<c>?limit=</c>, 100 by default; <c>?limit=all</c> for every one, say
///         from
///         curl), in the order the clients came (keyset paging: <c>?after=</c> the last one shown, so a page doesn't shift
///         as
///         clients come and go). Every page is written as it's made — HTML and JSON streamed to the response in chunks, so
///         a
///         list without a limit costs a page's memory, not the list's. Clients are named by their serial, never reused, so
///         a
///         link to one that has ended says so rather than leading to another.
///     </para>
///     <para>
///         It is the router's lowest priority: it reads without locks — the router's threads never wait on it — so what it
///         shows
///         is best effort, never a coherent snapshot of the whole (a record being written as it's read is left out); at
///         most
///         <c>maxConcurrent</c> requests run at once (more are turned away, 503, not queued); and it calls a provider only
///         when
///         asked to (a live health check, by its link).
///     </para>
///     <para>
///         It is an ops endpoint (<see cref="OpsPortEndpointExtensions.RequireOpsPort{TBuilder}" />): it answers only on
///         the ops
///         port (<see cref="OpsOptions.Port" />) — or, in Development, for a stated reason, on every port
///         (<see cref="OpsOptions.SharedPortReason" />: on every page, a banner saying so). The users are anonymous (no
///         authentication yet), and it shows their addresses, the provider topology and payloads:
///         <b>
///             a port is not an access
///             policy
///         </b>
///         — who reaches it is the network's to decide; what they may see, the app's (authorization on the group it
///         returns).
///     </para>
/// </summary>
public static class TaplineViewerEndpoints
{
    private const int DefaultPage = 100; // a list's page, unless ?limit= says otherwise
    private const int RowSubscriptions = 8; // a client's subscriptions listed in its row (the rest: its page)
    private const int ChunkChars = 16 * 1024; // HTML written out in chunks of about this
    private const int JsonFlushEvery = 64; // a JSON list flushed every so many items

    private const int BlobHead = 4096; // a blob's bytes shown on its record's page (the rest: ?format=raw)
    private const int MaxRendered = 4 << 20; // an out-of-band record rendered whole up to this

    private const int MaxView = 1 << 20; // the most of a provider's view relayed
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly TimeSpan ViewTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    ///     Provider views' client: redirects not followed (an answer is the provider's, from where it said), no cookies,
    ///     2 s, 1 MB.
    /// </summary>
    private static readonly HttpClient s_views = new(new SocketsHttpHandler
        { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = ViewTimeout })
    {
        Timeout = ViewTimeout,
        MaxResponseContentBufferSize = MaxView
    };

    /// <summary>
    ///     Maps the viewer at <paramref name="prefix" />; at most <paramref name="maxConcurrent" /> of its requests at
    ///     once.
    /// </summary>
    public static RouteGroupBuilder MapTaplineViewer(this IEndpointRouteBuilder endpoints,
        string prefix = "/ops/realtime", int maxConcurrent = 2)
    {
        var root = prefix.TrimEnd('/');
        var gate = new SemaphoreSlim(maxConcurrent, Math.Max(1, maxConcurrent));
        var group = endpoints.MapGroup(root).RequireOpsPort();
        group.AddEndpointFilter(async (context, next) =>
        {
            if (SharedPort(context.HttpContext) is { } reason)
                context.HttpContext.Response.Headers["Tapline-Ops-Shared-Port"] = Ascii(reason);
            if (!gate.Wait(0))
                return Results.Problem("The viewer is busy: try again.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            try
            {
                var result = await next(context);
                if (result is Streamed streamed)
                    await streamed.ExecuteAsync(context.HttpContext); // written inside the gate
                return result is Streamed ? Results.Empty : result;
            }
            finally
            {
                gate.Release();
            }
        });

        group.MapGet("/", (HttpRequest request, Publisher router, OutletRegistry outlets) =>
        {
            var to = new Links(request, root);
            var view = router.Describe();
            var dead = router.RecentlyDead();
            int clients = 0, subscriptions = 0;
            foreach (var o in outlets.Outlets)
                if (o is not null)
                {
                    clients++;
                    subscriptions += o.Taps.Count;
                }

            var health = outlets.Health;
            var drains = DrainsAt(request);
            var drain = request.HttpContext.RequestServices.GetService<RouterDrain>();
            return Respond(request, new
                {
                    router = view,
                    health = new
                    {
                        state = health.State.ToString(), health.Correlation, health.Reason, health.At, health.DrainsBy,
                        lastDrain = drain?.Last
                    },
                    recentlyDead = dead,
                    clients = new { count = clients, subscriptions, list = to.Clients(null, null) },
                    drains = drains is null
                        ? null
                        : new { router = $"{drains}/drain", node = $"{drains}/nodes/{{nodeId}}/drain" }
                },
                $"Router {view.Router.Name}",
                p => Pages.Router(p, to, view, health, dead, clients, subscriptions, drains, drain));
        });
        group.MapGet("/clients", (HttpRequest request, OutletRegistry outlets) =>
        {
            var to = new Links(request, root);
            var paging = Paging.From(request,
                s => long.TryParse(s, out var a) ? a.ToString(CultureInfo.InvariantCulture) : null);
            var after = paging.After is { } a ? long.Parse(a, CultureInfo.InvariantCulture) : 0;
            var rows = ClientsAfter(outlets, after, paging);
            var count = outlets.Count;
            return WantsJson(request)
                ? JsonList(w =>
                {
                    w.WriteNumber("count", count);
                    w.WriteString("limit", paging.LimitText);
                }, "clients", rows.Select(ClientItem), paging, s => to.Clients(s, paging))
                : Html($"Clients — {count}", p => Pages.Clients(p, to, count, rows, paging));
        });
        group.MapGet("/clients/{serial:long}", (long serial, HttpRequest request, OutletRegistry outlets) =>
        {
            if (outlets.BySerial(serial) is not { } outlet) return Gone(serial);
            var (client, taps, frames) = (outlet.Describe(), outlet.DescribeTaps(), outlet.RecentFrames());
            return Respond(request, new { client, taps, frames }, $"Client {serial}",
                p => Pages.Client(p, new Links(request, root), client, taps, frames));
        });
        group.MapGet("/clients/{serial:long}/taps/{tap:long}",
            (long serial, long tap, HttpRequest request, OutletRegistry outlets) =>
            {
                if (outlets.BySerial(serial) is not { } outlet) return Gone(serial);
                if (outlet.DescribeTap((uint)tap) is not { } view)
                    return Results.Content($"Client {serial} has no subscription {tap} now (it has ended).",
                        "text/plain; charset=utf-8", statusCode: 404);
                return Respond(request, new { client = serial, subscription = view }, $"Subscription {view.Id}",
                    p => Pages.Subscription(p, new Links(request, root), serial, view));
            });
        group.MapGet("/clients/{serial:long}/taps/{tap:long}/records/{n:long}",
            (long serial, long tap, long n, HttpRequest request, OutletRegistry outlets) =>
            {
                if (outlets.BySerial(serial) is not { } outlet) return Gone(serial);
                if (outlet.DescribeTap((uint)tap) is not { } view)
                    return Results.Content($"Client {serial} has no subscription {tap} now (it has ended).",
                        "text/plain; charset=utf-8", statusCode: 404);
                var to = new Links(request, root);
                var title = $"Record {n} of subscription {view.Id}";
                if (view.Messages.FirstOrDefault(m => m.N == n) is not { } record)
                    return Respond(request,
                        new
                        {
                            client = serial, subscription = view.Id, record = (MessageView?)null,
                            kept = view.Messages.Select(m => m.N)
                        },
                        title, p => Pages.Record(p, to, serial, view, n, null, null, null, null, null));
                var source = outlet.Taps.FirstOrDefault(t => t.Id == (uint)tap)?.Source;
                BlobView? blob = null;
                string? rendered = null;
                if (record.Blob is { } hex && BlobId.TryParse(hex, out var blobId))
                {
                    // Its content: in the blob store, if still kept — leased (a reference counted, as a client's fetch) while read.
                    var lookup = outlet.TryLeaseBlob(blobId, out var lease);
                    if (lease is not null && request.Query["format"] == "raw")
                        return new BlobResult(lease, hex); // written whole, then released
                    using (lease)
                    {
                        blob = lease is null
                            ? new BlobView(hex, lookup.ToString(), null, null, null)
                            : new BlobView(hex, lookup.ToString(), lease.Length, lease.ContentAddressed,
                                lease.Data.Slice(0, Math.Min(lease.Length, BlobHead)).ToArray());
                        if (lease is not null && source is not null &&
                            lease.Length <= MaxRendered) // (a value's blob is its payload alone)
                        {
                            var whole = lease.Data.ToArray();
                            rendered = RenderJson(w => source.RenderRecord(w, whole, record.Inner == "value"));
                        }
                    }
                }

                byte[]? inBand = null; // its bytes, kept whole in the subscription's history
                if (record.Blob is null)
                {
                    inBand = record.Whole ? outlet.ReadRecord((uint)tap, n) :
                        record.Preview.Length == record.Bytes * 2 ? Convert.FromHexString(record.Preview) : null;
                    if (inBand is not null && request.Query["format"] == "raw")
                        return Results.Bytes(inBand, "application/octet-stream");
                    if (inBand is not null && source is not null)
                        rendered = RenderJson(w => source.RenderRecord(w, inBand, false));
                }

                // Its provider's view: of the record as kept — or, not kept whole, as its provider finds it by its upstream versions.
                var viewLink =
                    (rendered is not null || record?.Upstream is not null) &&
                    source?.EffectiveFormat()?.ViewUrl is not null
                        ? to.RecordView(serial, view.Id, n)
                        : null;
                return Respond(request,
                    new
                    {
                        client = serial, subscription = view.Id, record,
                        blob = blob is null
                            ? null
                            : new
                            {
                                blob.Id, blob.State, blob.Length, blob.ContentAddressed,
                                head = blob.Head is { } h ? Convert.ToHexString(h) : null
                            },
                        rendered = rendered is null ? (JsonElement?)null : JsonDocument.Parse(rendered).RootElement,
                        view = viewLink
                    },
                    title, p => Pages.Record(p, to, serial, view, n, record, blob, inBand, rendered, viewLink));
            });
        group.MapGet("/clients/{serial:long}/taps/{tap:long}/records/{n:long}/view",
            async (long serial, long tap, long n, HttpContext context, OutletRegistry outlets) =>
            {
                if (outlets.BySerial(serial) is not { } outlet)
                    return ViewProblem(context, 404, $"No client {serial} now: it has ended.");
                var view = outlet.DescribeTap((uint)tap);
                var source = outlet.Taps.FirstOrDefault(t => t.Id == (uint)tap)?.Source;
                if (view?.Messages.FirstOrDefault(m => m.N == n) is not { } record || source is null)
                    return ViewProblem(context, 404, "That record is no longer kept.");
                var format = source.EffectiveFormat();
                if (format?.ViewUrl is not { } url)
                    return ViewProblem(context, 404, "Its topic's provider gave no view URL.");
                // Not kept whole: its provider may find it by the upstream versions its publishes gave (sent; "rendered" null).
                var rendered = RenderWhole(outlet, (uint)tap, record, source);
                if (rendered is null && record.Upstream is null)
                    return ViewProblem(context, 404,
                        "The record isn't kept whole, and its publish gave no upstream version to look it up by: only its topic's state can be shown.");
                var topic = source.Describe();
                return await ProviderView(context, url, w => Envelope(w, "record", topic, format, rendered ?? "null",
                    w =>
                    {
                        w.WritePropertyName("record");
                        JsonSerializer.Serialize(w, record, Json);
                    }));
            });
        group.MapGet("/topics/{id:long}", (long id, HttpRequest request, OutletRegistry outlets) =>
        {
            if (FindTopic(outlets, id) is not { } topic)
                return Results.Content(
                    "No route is bound to that topic now (the viewer finds topics through their subscribers).",
                    "text/plain; charset=utf-8", statusCode: 404);
            var to = new Links(request, root);
            var described = topic.Describe();
            var paging = Paging.From(request, ParseTapCursor);
            var rows = TapsAfter(outlets, TapCursor(paging.After), t => t.Source.Id == id, paging);
            var stateRows = StateRows(request);
            var formatNotes = new List<string>();
            var format =
                topic.EffectiveFormat(
                    formatNotes); // as its provider declared it, or made up for it (a table's, from its types)
            var viewLink = format?.ViewUrl is null ? null : to.TopicView(id, stateRows);
            return WantsJson(request)
                ? JsonList(w =>
                    {
                        w.WritePropertyName("topic");
                        JsonSerializer.Serialize(w, described, Json);
                        w.WritePropertyName("format");
                        if (format is null) w.WriteNullValue();
                        else format.WriteTo(w);
                        if (formatNotes.Count > 0)
                        {
                            w.WriteStartArray("formatNotes");
                            foreach (var note in formatNotes) w.WriteStringValue(note);
                            w.WriteEndArray();
                        }

                        if (viewLink is not null) w.WriteString("view", viewLink);
                        w.WritePropertyName("state");
                        if (!topic.RenderState(w, stateRows)) w.WriteNullValue();
                        w.WriteString("limit", paging.LimitText);
                    },
                    "subscribers",
                    rows.Select(r =>
                        (object)new { client = r.O.Serial, subscription = r.O.DescribeTap(r.T.Id, false) }), paging,
                    s => to.Topic(id, s, paging))
                : Html($"Topic {described.Label ?? id.ToString(CultureInfo.InvariantCulture)}",
                    p => Pages.Topic(p, to, described, format is null
                            ? null
                            : RenderJson(w =>
                            {
                                format.WriteTo(w);
                                return true;
                            }), formatNotes, RenderJson(w => topic.RenderState(w, stateRows)), stateRows, viewLink,
                        rows,
                        paging));
        });
        group.MapGet("/topics/{id:long}/view", async (long id, HttpContext context, OutletRegistry outlets) =>
        {
            if (FindTopic(outlets, id) is not { } topic)
                return ViewProblem(context, 404, "No route is bound to that topic now.");
            var format = topic.EffectiveFormat();
            if (format?.ViewUrl is not { } url) return ViewProblem(context, 404, "Its provider gave no view URL.");
            if (RenderJson(w => topic.RenderState(w, StateRows(context.Request))) is not { } state)
                return ViewProblem(context, 404, "Nothing published yet.");
            var described = topic.Describe();
            return await ProviderView(context, url, w => Envelope(w, "state", described, format, state, null));
        });
        group.MapGet("/services/{name}", (string name, HttpRequest request, Publisher router) =>
        {
            if (router.DescribeService(name) is not { } service) return Results.NotFound();
            var endpointViews = service.Endpoints.Select(router.DescribeEndpoint).OfType<EndpointView>().ToList();
            var nodes = endpointViews.Select(e => e.Instance is { } i ? router.DescribeInstance(i.Id)?.Instance : null)
                .ToList(); // the node there, if tracked
            return Respond(request, new { service, endpoints = endpointViews }, $"Service {service.Name}",
                p => Pages.Service(p, new Links(request, root), service, endpointViews, nodes, DrainsAt(request)));
        });
        group.MapGet("/endpoints", async (string address, string? check, HttpRequest request, Publisher router) =>
        {
            if (router.DescribeEndpoint(address) is not { } endpoint) return Results.NotFound();
            var services = router.Describe().Services.Where(s => s.Endpoints.Contains(address)).Select(s => s.Name)
                .ToList();
            var now = check == "now" ? await router.CheckHealthNowAsync(address, TimeSpan.FromSeconds(2)) : null;
            return Respond(request, new { endpoint, services, checkedNow = now },
                $"Endpoint {endpoint.Endpoint}",
                p => Pages.Endpoint(p, new Links(request, root), endpoint, services, check == "now", now));
        });
        group.MapGet("/instances/{id:guid}", (Guid id, HttpRequest request, Publisher router, OutletRegistry outlets) =>
        {
            if (router.DescribeInstance(id) is not { } instance) return Results.NotFound();
            var to = new Links(request, root);
            var paging = Paging.From(request, ParseTapCursor);
            var rows = TapsAfter(outlets, TapCursor(paging.After), t => t.Provider?.Id == id, paging);
            return WantsJson(request)
                ? JsonList(w =>
                    {
                        w.WritePropertyName("instance");
                        JsonSerializer.Serialize(w, instance, Json);
                        w.WriteString("limit", paging.LimitText);
                    },
                    "routes",
                    rows.Select(r =>
                        (object)new { client = r.O.Serial, subscription = r.O.DescribeTap(r.T.Id, false) }), paging,
                    s => to.Instance(id, s, paging))
                : Html($"Instance {instance.Instance.Instance.Name}",
                    p => Pages.Instance(p, to, instance, rows, paging, DrainsAt(request)));
        });
        return group;
    }

    /// <summary>JSON made by <paramref name="render" />, indented (null if it had nothing to render).</summary>
    private static string? RenderJson(Func<Utf8JsonWriter, bool> render)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer,
                   new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            if (!render(w)) return null;
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>A topic, found through a subscriber (the viewer knows topics only through them).</summary>
    private static Source? FindTopic(OutletRegistry outlets, long id)
    {
        foreach (var o in outlets.Outlets)
        {
            if (o is null) continue;
            foreach (var t in o.Taps)
                if (t.Source.Id == id)
                    return t.Source;
        }

        return null;
    }

    /// <summary>How many of a topic's rows or messages to show: <c>?rows=</c> (all: <c>all</c> or 0), 100 by default.</summary>
    private static int StateRows(HttpRequest request)
    {
        return request.Query["rows"].ToString() switch
        {
            "all" or "0" => int.MaxValue,
            var r when int.TryParse(r, NumberStyles.None, CultureInfo.InvariantCulture, out var k) && k > 0 => k,
            _ => DefaultPage
        };
    }

    /// <summary>
    ///     A record rendered, if it's kept whole: out of band, its blob leased while read; in band, in its subscription's
    ///     history (or its preview all of it).
    /// </summary>
    private static string? RenderWhole(Outlet outlet, uint tap, MessageView record, Source source)
    {
        if (record.Blob is { } hex && BlobId.TryParse(hex, out var blobId))
        {
            outlet.TryLeaseBlob(blobId, out var lease);
            using (lease)
            {
                if (lease is null || lease.Length > MaxRendered) return null;
                var whole = lease.Data.ToArray();
                return RenderJson(w => source.RenderRecord(w, whole, record.Inner == "value"));
            }
        }

        var bytes = record.Whole ? outlet.ReadRecord(tap, record.N) :
            record.Preview.Length == record.Bytes * 2 ? Convert.FromHexString(record.Preview) : null;
        return bytes is null ? null : RenderJson(w => source.RenderRecord(w, bytes, false));
    }

    /// <summary>
    ///     What the router posts to a provider's view URL:
    ///     <c>
    ///         {"kind": "state" | "record", "topic": {…}, "record": {…}
    ///         (a record's), "format": {…}, "rendered": …}
    ///     </c>
    ///     — the router's own rendering, by the topic's format; null for a
    ///     record not kept whole. The provider's own numbering comes back to it: <c>topic.upstream</c>, the upstream version
    ///     its state is as of, and a record's <c>upstream</c> and <c>upstreamSince</c> — what it's as of, and what its
    ///     subscriber was at before it — each as the provider's publishes gave them (<c>upstreamVersion</c>), null if not.
    /// </summary>
    private static void Envelope(Utf8JsonWriter w, string kind, TopicView topic, PayloadFormat format, string rendered,
        Action<Utf8JsonWriter>? more)
    {
        w.WriteStartObject();
        w.WriteString("kind", kind);
        w.WritePropertyName("topic");
        JsonSerializer.Serialize(w, topic, Json);
        more?.Invoke(w);
        w.WritePropertyName("format");
        format.WriteTo(w);
        w.WritePropertyName("rendered");
        w.WriteRawValue(rendered);
        w.WriteEndObject();
    }

    /// <summary>
    ///     A provider's view: <paramref name="envelope" /> POSTed to its view URL — the one it gave in step 1, never one from
    ///     the
    ///     request — and its answer relayed, sandboxed: a page (<c>text/*</c>) or an image, at most 1 MB, within 2 s. Anything
    ///     else is said so, in the frame.
    /// </summary>
    private static async Task<IResult> ProviderView(HttpContext context, Uri url, Action<Utf8JsonWriter> envelope)
    {
        var body = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(body))
        {
            envelope(w);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
            { Content = new ReadOnlyMemoryContent(body.WrittenMemory) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.Accept.ParseAdd("text/html");
        try
        {
            using var response = await s_views.SendAsync(request, HttpCompletionOption.ResponseContentRead,
                context.RequestAborted);
            if (!response.IsSuccessStatusCode)
                return ViewProblem(context, StatusCodes.Status502BadGateway,
                    $"The provider's view ({url}) answered {(int)response.StatusCode} {response.ReasonPhrase}" +
                    (response.Headers.Location is { } to ? $", to {to} (not followed)." : "."));
            var type = response.Content.Headers.ContentType;
            if (type?.MediaType is not { } media || !(media.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
                                                      media.StartsWith("image/", StringComparison.OrdinalIgnoreCase)))
                return ViewProblem(context, StatusCodes.Status502BadGateway,
                    $"The provider's view ({url}) answered {type?.MediaType ?? "no content type"}: not a page or an image.");
            var bytes = await response.Content.ReadAsByteArrayAsync(context.RequestAborted);
            Sandbox(context);
            return Results.Bytes(bytes, type.ToString());
        }
        catch (TaskCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            return ViewProblem(context, StatusCodes.Status504GatewayTimeout,
                $"The provider's view ({url}) didn't answer within {ViewTimeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException e)
        {
            return ViewProblem(context, StatusCodes.Status502BadGateway,
                $"The provider's view ({url}) couldn't be had: {e.Message}");
        }
    }

    /// <summary>
    ///     A view's answer is the provider's: in an opaque origin (scripts may run; nothing of the viewer is theirs),
    ///     never sniffed, cached or referred.
    /// </summary>
    private static void Sandbox(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = "sandbox allow-scripts";
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.CacheControl = "no-store";
    }

    /// <summary>Why there's no view — a small page, for the frame.</summary>
    private static IResult ViewProblem(HttpContext context, int status, string message)
    {
        Sandbox(context);
        return Results.Content(
            "<!doctype html><meta charset=utf-8><style>body{font:13px ui-monospace,Menlo,Consolas,monospace;margin:8px;color:#5c4400}" +
            "@media(prefers-color-scheme:dark){body{color:#f3d77a;background:#161616}}</style><p>" +
            WebUtility.HtmlEncode(message) + "</p>",
            "text/html; charset=utf-8", statusCode: status);
    }

    private static IResult Gone(long serial)
    {
        return Results.Content(
            $"No client {serial} now: it has ended (clients are numbered once, never reused).",
            "text/plain; charset=utf-8", statusCode: 404);
    }

    /// <summary>
    ///     Where the drains answer (<see cref="TaplineDrainExtensions.MapTaplineDrain" />), if they're mapped: the viewer
    ///     offers them then.
    /// </summary>
    private static string? DrainsAt(HttpRequest request)
    {
        if (request.HttpContext.RequestServices.GetService<EndpointDataSource>() is not { } source) return null;
        foreach (var endpoint in source.Endpoints)
            if (endpoint.Metadata.GetMetadata<DrainEndpoints>() is { } drains)
                return $"{request.PathBase}{drains.Prefix}";
        return null;
    }

    /// <summary>
    ///     A drain's form — posted to the drain (this origin's: the drains refuse a page elsewhere), which goes back to
    ///     <paramref name="back" /> — its fields' defaults given, an explanation before it if any.
    /// </summary>
    private static string DrainForm(string action, string back, string? what, string button,
        params (string Name, string Value)[] fields)
    {
        var form = new StringBuilder("<form class=act method=post action=\"").Append(WebUtility.HtmlEncode(action))
            .Append("\">")
            .Append("<input type=hidden name=back value=\"").Append(WebUtility.HtmlEncode(back)).Append("\">");
        if (what is not null) form.Append(WebUtility.HtmlEncode(what)).Append(' ');
        foreach (var (name, value) in fields)
            form.Append("<label>").Append(name).Append(" <input name=").Append(name).Append(" value=\"")
                .Append(WebUtility.HtmlEncode(value)).Append("\" size=5></label> ");
        return form.Append("<button type=submit>").Append(WebUtility.HtmlEncode(button)).Append("</button></form>")
            .ToString();
    }

    private static bool WantsJson(HttpRequest request)
    {
        return request.Query["format"] == "json" || request.Headers.Accept.ToString().Contains("application/json");
    }

    private static IResult Respond(HttpRequest request, object json, string title, Func<Page, Task> html)
    {
        return WantsJson(request) ? JsonObject(json) : Html(title, html);
    }

    /// <summary>Why the ops endpoints share the client-facing port, if they do (<see cref="OpsOptions.SharedPortReason" />).</summary>
    private static string? SharedPort(HttpContext context)
    {
        return context.RequestServices.GetService<IOptions<TaplineOptions>>()?.Value.Ops is
            { Port: null, SharedPortReason: { } reason }
            ? reason
            : null;
    }

    private static string Ascii(string text)
    {
        return string.Create(text.Length, text, static (span, s) =>
        {
            for (var i = 0; i < s.Length; i++) span[i] = s[i] is >= ' ' and <= '~' ? s[i] : '?';
        });
    }

    /// <summary>A JSON object: <c>sharedPort</c> first if the ops endpoints share the client-facing port, then its fields.</summary>
    private static IResult JsonObject(object value)
    {
        return new Streamed(async context =>
        {
            var element = JsonSerializer.SerializeToElement(value, value.GetType(), Json);
            context.Response.ContentType = "application/json; charset=utf-8";
            await using var json =
                new Utf8JsonWriter(context.Response.BodyWriter, new JsonWriterOptions { Indented = true });
            json.WriteStartObject();
            if (SharedPort(context) is { } reason) json.WriteString("sharedPort", reason);
            foreach (var property in element.EnumerateObject()) property.WriteTo(json);
            json.WriteEndObject();
            json.Flush();
            await context.Response.BodyWriter.FlushAsync(context.RequestAborted);
        });
    }

    /// <summary>A page written to the response as it's made (chunk by chunk), inside the viewer's concurrency gate.</summary>
    private static IResult Html(string title, Func<Page, Task> render)
    {
        return new Streamed(async context =>
        {
            await using var page = new Page(context, title);
            await render(page);
            await page.EndAsync();
        });
    }

    /// <summary>
    ///     A JSON object whose list is written item by item — <paramref name="head" />'s fields, the list, then the next
    ///     page's
    ///     link (null on the last) — flushed as it goes: a list without a limit costs a page's memory, not the list's.
    /// </summary>
    private static IResult JsonList(Action<Utf8JsonWriter> head, string name, IEnumerable<object> items, Paging paging,
        Func<string, string> next)
    {
        return new Streamed(async context =>
        {
            var response = context.Response;
            var aborted = context.RequestAborted;
            response.ContentType = "application/json; charset=utf-8";
            await using var json = new Utf8JsonWriter(response.BodyWriter, new JsonWriterOptions { Indented = true });
            json.WriteStartObject();
            if (SharedPort(context) is { } reason) json.WriteString("sharedPort", reason);
            head(json);
            json.WritePropertyName(name);
            json.WriteStartArray();
            var n = 0;
            foreach (var item in items)
            {
                JsonSerializer.Serialize(json, item, item.GetType(), Json);
                if (++n % JsonFlushEvery != 0) continue;
                json.Flush();
                await response.BodyWriter.FlushAsync(aborted);
            }

            json.WriteEndArray();
            json.WriteString("next", paging.Next is { } cursor ? next(cursor) : null);
            json.WriteEndObject();
            json.Flush();
            await response.BodyWriter.FlushAsync(aborted);
        });
    }

    /// <summary>
    ///     The clients after <paramref name="after" /> (a serial), in the order they came, a page's worth (made as
    ///     they're written).
    /// </summary>
    private static IEnumerable<Outlet> ClientsAfter(OutletRegistry outlets, long after, Paging paging)
    {
        var page = new List<Outlet>();
        foreach (var o in outlets.Outlets)
            if (o is not null && o.Serial > after)
                page.Add(o);
        page.Sort(static (x, y) => x.Serial.CompareTo(y.Serial));
        for (var i = 0; i < page.Count; i++)
        {
            if (paging.Shown == paging.Limit)
            {
                paging.Next = page[i - 1].Serial.ToString(CultureInfo.InvariantCulture);
                yield break;
            }

            paging.Shown++;
            yield return page[i];
        }
    }

    /// <summary>
    ///     Subscriptions that <paramref name="match" />, by client (in the order they came) then subscription, after the
    ///     cursor's.
    /// </summary>
    private static IEnumerable<(Outlet O, Tap T)> TapsAfter(OutletRegistry outlets, (long Serial, uint Tap) after,
        Func<Tap, bool> match, Paging paging)
    {
        var clients = new List<Outlet>();
        foreach (var o in outlets.Outlets)
            if (o is not null && o.Serial >= after.Serial)
                clients.Add(o);
        clients.Sort(static (x, y) => x.Serial.CompareTo(y.Serial));
        (long Serial, uint Tap) last = default;
        foreach (var o in clients)
        foreach (var t in o.Taps.Where(t => match(t) && (o.Serial > after.Serial || t.Id > after.Tap))
                     .OrderBy(t => t.Id))
        {
            if (paging.Shown == paging.Limit)
            {
                paging.Next = $"{last.Serial}.{last.Tap}";
                yield break;
            }

            paging.Shown++;
            last = (o.Serial, t.Id);
            yield return (o, t);
        }
    }

    /// <summary>A subscription cursor: <c>{client serial}.{subscription}</c>.</summary>
    private static string? ParseTapCursor(string s)
    {
        return TryTapCursor(s, out _) ? s : null;
    }

    private static (long Serial, uint Tap) TapCursor(string? s)
    {
        return s is not null && TryTapCursor(s, out var c) ? c : default;
    }

    private static bool TryTapCursor(string s, out (long Serial, uint Tap) cursor)
    {
        cursor = default;
        var dot = s.IndexOf('.');
        if (dot <= 0 || !long.TryParse(s.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture,
                         out var serial)
                     || !uint.TryParse(s.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var tap))
            return false;
        cursor = (serial, tap);
        return true;
    }

    /// <summary>A client in a list: as it is, with its first few subscriptions.</summary>
    private static object ClientItem(Outlet o)
    {
        var view = o.Describe();
        var shown = FirstSubscriptions(o);
        return new
        {
            client = view, subscriptions = shown.Select(t => new { t.Id, t.Topic, t.TopicId, t.State, t.Service }),
            more = Math.Max(0, view.Taps - shown.Count)
        };
    }

    private static List<TapView> FirstSubscriptions(Outlet o)
    {
        var shown = new List<TapView>(RowSubscriptions);
        foreach (var t in o.Taps)
            if (shown.Count < RowSubscriptions && o.DescribeTap(t.Id, false) is { } tap)
                shown.Add(tap);
        return shown;
    }

    // Cells: markup made here — text encoded, links built from encoded parts.
    private static string Text(object? value)
    {
        return WebUtility.HtmlEncode(value switch
        {
            null => "",
            long l => l.ToString("N0", CultureInfo.InvariantCulture),
            int n => n.ToString("N0", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        });
    }

    private static string Link(string href, string text)
    {
        return $"<a href=\"{WebUtility.HtmlEncode(href)}\">{WebUtility.HtmlEncode(text)}</a>";
    }

    private static string Join(IEnumerable<string> cells)
    {
        return string.Join(", ", cells);
    }

    private static string Time(DateTimeOffset? at)
    {
        return at is { } t ? t.UtcDateTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) : "";
    }

    private static string Ago(DateTimeOffset? at)
    {
        return at is { } t ? $"{(DateTimeOffset.UtcNow - t).TotalSeconds:F1} s ago" : "";
    }

    /// <summary>A provider's load as it reported it (0 idle, 1 at capacity), or blank if it hasn't said.</summary>
    private static string LoadOf(double? load)
    {
        return load is { } v ? v.ToString("0.00", CultureInfo.InvariantCulture) : "";
    }

    private static string Ms(double? ms)
    {
        return ms is { } v ? $"{v:F1} ms" : "";
    }

    private static string Size(long bytes)
    {
        return bytes >= 1 << 20 ? $"{bytes / 1048576.0:F1} MiB" :
            bytes >= 1024 ? $"{bytes / 1024.0:F1} KiB" : $"{bytes} B";
    }

    private static string Memory(OutletView c)
    {
        return c.MemoryBudget > 0 ? $"{Size(c.MemoryBytes)} of {Size(c.MemoryBudget)}" : Size(c.MemoryBytes);
    }

    private static string Waiting(OutletView c)
    {
        return c.WaitingMs is { } w ? $"{w / 1000:F1} s" : "";
    }

    private static string Rate(long bytesPerSecond)
    {
        return bytesPerSecond <= 0 ? "" : $"{bytesPerSecond / 1024.0:F1} KiB/s";
    }

    private static string Short(string? agent)
    {
        return agent is null ? "" : agent.Length <= 48 ? agent : agent[..48] + "…";
    }

    private static string Connection(OutletSession? s)
    {
        return s is null ? "never connected"
            : s.DisconnectedAt is { } at ? $"disconnected {Ago(at)}" : $"connected since {Time(s.ConnectedAt)}";
    }

    /// <summary>An out-of-band record's blob, as looked up: still kept (its length, its first bytes) or not.</summary>
    private sealed record BlobView(string Id, string State, int? Length, bool? ContentAddressed, byte[]? Head);

    /// <summary>A response written by the endpoint filter, inside the gate (so a long one still counts against it).</summary>
    private sealed class Streamed(Func<HttpContext, Task> write) : IResult
    {
        public Task ExecuteAsync(HttpContext context)
        {
            return write(context);
        }
    }

    /// <summary>
    ///     A list's page: its limit (<c>?limit=</c> — 100 by default; <c>all</c> or <c>0</c>, none) and where it starts
    ///     (<c>?after=</c>, a cursor); as its rows are made, how many and — if more remain — the cursor for the next page.
    /// </summary>
    private sealed class Paging
    {
        public string? After, Next;
        public int Limit;
        public int Shown;

        public bool Unlimited => Limit == int.MaxValue;

        public string LimitText => Unlimited ? "all" : Limit.ToString(CultureInfo.InvariantCulture);

        public static Paging From(HttpRequest request, Func<string, string?> cursor)
        {
            return new Paging
            {
                Limit = request.Query["limit"].ToString() switch
                {
                    "all" or "0" => int.MaxValue,
                    var s when int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) &&
                               n > 0 => n,
                    _ => DefaultPage
                },
                After = request.Query["after"].ToString() is { Length: > 0 } a ? cursor(a) : null
            };
        }
    }

    /// <summary>The viewer's URLs, from its root (each value escaped).</summary>
    private sealed class Links(HttpRequest request, string root)
    {
        private readonly string _base = $"{request.PathBase}{root}";

        public string Router => _base + "/";

        public string Clients(string? after, Paging? paging)
        {
            return Paged($"{_base}/clients", after, paging);
        }

        public string Client(long serial)
        {
            return $"{_base}/clients/{serial}";
        }

        public string Subscription(long client, uint tap)
        {
            return $"{_base}/clients/{client}/taps/{tap}";
        }

        public string Record(long client, uint tap, long n, bool raw = false)
        {
            return $"{_base}/clients/{client}/taps/{tap}/records/{n}{(raw ? "?format=raw" : "")}";
        }

        public string Topic(long id, string? after = null, Paging? paging = null)
        {
            return Paged($"{_base}/topics/{id}", after, paging);
        }

        public string TopicView(long id, int rows)
        {
            return
                $"{_base}/topics/{id}/view{(rows == DefaultPage ? "" : rows == int.MaxValue ? "?rows=all" : $"?rows={rows}")}";
        }

        public string RecordView(long client, uint tap, long n)
        {
            return $"{_base}/clients/{client}/taps/{tap}/records/{n}/view";
        }

        public string Service(string name)
        {
            return $"{_base}/services/{Uri.EscapeDataString(name)}";
        }

        public string Endpoint(string address, bool check = false)
        {
            return $"{_base}/endpoints?address={Uri.EscapeDataString(address)}{(check ? "&check=now" : "")}";
        }

        public string Instance(Guid id, string? after = null, Paging? paging = null)
        {
            return Paged($"{_base}/instances/{id}", after, paging);
        }

        /// <summary>A list's page: after a cursor, with the same limit (the default left out); with neither, its first page.</summary>
        private static string Paged(string path, string? after, Paging? paging)
        {
            var query = new List<string>(2);
            if (after is not null) query.Add($"after={Uri.EscapeDataString(after)}");
            if (paging is { Limit: not DefaultPage }) query.Add($"limit={paging.LimitText}");
            return query.Count == 0 ? path : $"{path}?{string.Join('&', query)}";
        }

        public string All(Func<string?, Paging?, string> list)
        {
            return list(null, new Paging { Limit = int.MaxValue });
        }
    }

    /// <summary>The pages: plain HTML, values encoded, links everywhere — written as they're made.</summary>
    private static class Pages
    {
        public static async Task Router(Page p, Links to, RouterView r, RouterHealth health,
            IReadOnlyList<InstanceView> dead, int clients, int subscriptions,
            string? drains, RouterDrain? drain)
        {
            p.Heading($"Router {r.Router.Name}");
            p.Para(Text(
                health.State
                    switch // as its clients are told it: the change's id, the needle in their logs and this router's
                    {
                        RouterState.Degraded => $"degraded since {Time(health.At)}: {health.Reason}",
                        RouterState.Draining =>
                            $"draining since {Time(health.At)}, gone by {Time(health.DrainsBy ?? health.At)}",
                        _ => $"serving since {Time(health.At)}"
                    } + $" · correlation {health.Correlation}"));
            if (health.State == RouterState.Draining && drain is not null)
                p.Para(Text(drain.Waiting
                    ? $"its drain: waiting for its clients to move ({drain.Subscribed} still subscribed here)"
                    : drain.Last is { } last
                        ? last.Left > 0
                            ? $"its drain: the grace ran out with {last.Left} of {last.Clients} clients still subscribed here"
                            : $"its drain: {(last.Clients == 0 ? "no client to move" : $"every client moved ({last.Clients}), in {last.Took.TotalMilliseconds:N0} ms")}"
                        : "its drain: told its clients"));
            else if (drains is not null)
                p.Para(DrainForm($"{drains}/drain", to.Router,
                    "Drain this router — one way: its clients told to move to another; it serves on till it stops.",
                    "drain the router", ("grace", "10s")));
            p.Para(Text($"id {r.Router.Id} · started {Time(r.Router.Started)} · ") +
                   Link(to.Clients(null, null), $"{clients:N0} clients") +
                   Text(
                       $" ({subscriptions:N0} subscriptions) · instances deemed dead {r.InstancesDown} · clients a drain moved {r.ClientsMoved} · routes a drain closed {r.RoutesDrained} · subscriptions a move asked to move {r.RoutesMoved}, let go {r.RoutesLetGo}"));
            await p.TableAsync("Services", ["service", "endpoints", "waiting for up", "up policy"],
                r.Services.Select(s => new[]
                {
                    Link(to.Service(s.Name), s.Name), Join(s.Endpoints.Select(e => Link(to.Endpoint(e), e))),
                    Text(s.Waiting),
                    Text($"quorum {s.Quorum}, grace {s.GraceMs:F0} ms, spread {s.SpreadMs:F0} ms")
                }));
            if (r.Realms is { Count: > 0 } realms)
                await p.TableAsync("Sign-in realms", ["realm", "authentication service (its instances)", "passthrough"],
                    realms.Select(x => new[]
                    {
                        Text(x.Realm), Join(x.Endpoints.Select(e => Link(to.Endpoint(e), e))),
                        Text(x.Passthrough ? "yes — any user id taken as given, its identities insecure" : "")
                    }));
            await p.TableAsync("Provider endpoints",
                ["endpoint", "status", "refuses this router", "instance there", "heard", "load"], r.Endpoints.Select(e => new[]
                {
                    Link(to.Endpoint(e.Endpoint), e.Endpoint), Text(e.Status), Text(e.Refused ? "yes" : ""),
                    e.Instance is { } i ? Link(to.Instance(i.Id), i.Name) : "", Text(Ago(e.HeardAt)), Text(LoadOf(e.Load))
                }));
            await p.TableAsync("Provider instances",
                ["instance", "endpoint", "node id (clients')", "routes", "state", "heard"],
                r.Instances.Select(i => InstanceRow(to, i)));
            await p.TableAsync("Deemed dead lately",
                ["instance", "endpoint", "node id (clients')", "routes", "state", "heard"],
                dead.Select(i => InstanceRow(to, i)));
        }

        public static async Task Clients(Page p, Links to, int count, IEnumerable<Outlet> rows, Paging paging)
        {
            p.Heading($"Clients — {count:N0} on this router");
            p.Para(Link(to.Router, "router") + " · " +
                   (paging.After is null ? Text("from the first") : Link(to.Clients(null, paging), "first page")) +
                   Text($" · {(paging.Unlimited ? "every one" : $"{paging.Limit:N0} a page")} · ") +
                   (paging.Unlimited
                       ? Link(to.Clients(null, null), $"{DefaultPage} a page")
                       : Link(to.All(to.Clients), "all on one page")));
            await p.TableAsync(null,
            [
                "client", "user", "transport", "created ▲", "from", "agent", "connection", "subscriptions", "memory",
                "waiting", "frames", "rtt", "min rtt", "delivered"
            ], rows.Select(o =>
            {
                var c = o.Describe();
                return new[]
                {
                    Link(to.Client(c.Serial), c.Serial.ToString(CultureInfo.InvariantCulture)),
                    Text(User(c.Identity)), Text(c.Session?.Transport), Text(Time(c.CreatedAt)), Text(c.Session?.Remote),
                    Text(Short(c.Session?.Agent)),
                    Text(Connection(c.Session)), Subscriptions(to, c, FirstSubscriptions(o)), Text(Memory(c)),
                    Text(Waiting(c)), Text(c.FramesSent),
                    Text(Ms(c.RttMs)), Text(Ms(c.MinRttMs)), Text(Rate(c.DeliveryBytesPerSecond))
                };
            }), paging.After is null ? "none" : "no more");
            if (paging.Next is { } next) p.Para(Link(to.Clients(next, paging), "next page →"));
        }

        /// <summary>A client's user in a list: user@realm, and whether its module says it's insecure; or anonymous.</summary>
        private static string User(ClientIdentity? identity)
        {
            return identity is null ? "anonymous"
                : $"{identity.UserId}@{identity.Realm}{(identity.Insecure ? " (insecure)" : "")}" +
                  (identity.ExpiredAt(DateTimeOffset.UtcNow) ? " (lapsed)" : "");
        }

        /// <summary>
        ///     A client's identity on its page: who, in which realm, by which module — insecure if it says so — its session,
        ///     when proved and when it lapses, and what else the module asserted.
        /// </summary>
        private static string Identity(ClientIdentity? identity, SessionPolicy? policy = null)
        {
            if (identity is null) return "identity: anonymous";
            var now = DateTimeOffset.UtcNow;
            return $"identity: {identity.UserId} in realm {identity.Realm} · module {identity.Module}" +
                   (identity.Insecure ? " · insecure (its module says it proves nothing)" : " · secure") +
                   $" · session {identity.SessionId} · authenticated {Time(identity.AuthenticatedAt)}" +
                   (identity.ExpiresAt is not { } at ? " · doesn't lapse"
                       : at <= now ? $" · lapsed {Time(at)}" : $" · lapses {Time(at)}") +
                   (policy is not null && identity.ExpiresAt is not null
                       ? $" · renewal {policy.Renewal.ToString().ToLowerInvariant()}" +
                         (policy.WarnAt(identity) is { } warnAt ? $", warned {Time(warnAt)}" : "") +
                         (policy.Reason is { } why ? $" ({why})" : "")
                       : "") +
                   (identity.Claims is { Count: > 0 } claims
                       ? " · claims " + string.Join(", ", claims.Select(c => $"{c.Key}={c.Value}"))
                       : "");
        }

        /// <summary>
        ///     A client's row's subscriptions: each its topic, linked to its page — and its service, if through one — then
        ///     the rest, by count, to the client's page.
        /// </summary>
        private static string Subscriptions(Links to, OutletView c, List<TapView> shown)
        {
            if (shown.Count == 0)
                return c.Taps > 0 ? Link(to.Client(c.Serial), $"{c.Taps} (on its page)") : Text("none");
            var cell = new StringBuilder();
            foreach (var t in shown)
            {
                if (cell.Length > 0) cell.Append("<br>");
                cell.Append(Link(to.Subscription(c.Serial, t.Id), t.Topic ?? t.Source ?? $"subscription {t.Id}"));
                if (t.Service is { } service) cell.Append(" (").Append(Link(to.Service(service), service)).Append(')');
                if (t.State != "active")
                    cell.Append(' ').Append(Text(t.State)); // bound, closed, awaiting its initial state
            }

            if (c.Taps > shown.Count)
                cell.Append("<br>").Append(Link(to.Client(c.Serial), $"+{c.Taps - shown.Count} more"));
            return cell.ToString();
        }

        private static string[] InstanceRow(Links to, InstanceView i)
        {
            return
            [
                Link(to.Instance(i.Instance.Id), $"{i.Instance.Name} ({i.Instance.Id})"),
                Link(to.Endpoint(i.Instance.Endpoint.ToString()), i.Instance.Endpoint.ToString()),
                Text(i.NodeId), Text(i.Routes), Text(State(i)), Text(Ago(i.HeardAt))
            ];
        }

        /// <summary>An instance's state: dead, draining, degraded (whole, or service paths there), serving.</summary>
        private static string State(InstanceView i)
        {
            return i.Dead ? "dead"
                : i.Draining ? "draining"
                : i.Degraded ? "degraded"
                : i.DegradedPaths is { Count: > 0 } paths ? $"serving; degraded: {string.Join(", ", paths)}"
                : "serving";
        }

        public static async Task Client(Page p, Links to, OutletView c, IReadOnlyList<TapView> taps,
            IReadOnlyList<FrameView> frames)
        {
            p.Heading($"Client {c.Serial}");
            p.Para(Link(to.Router, "router") + " · " + Link(to.Clients(null, null), "clients") + Text(
                $" · {c.Session?.Transport ?? "no transport yet"} · created {Time(c.CreatedAt)} · from {c.Session?.Remote ?? "?"} · {Connection(c.Session)} · " +
                $"connections {c.Session?.Connections ?? 0} · agent {c.Session?.Agent ?? "?"}"));
            p.Para(Text(Identity(c.Identity, c.SessionPolicy)));
            p.Para(Text(
                $"round trip {Ms(c.RttMs)} (the last heartbeat's) · min {Ms(c.MinRttMs)} · smoothed {Ms(c.SmoothedRttMs)} · delivered {Rate(c.DeliveryBytesPerSecond)} · " +
                $"in flight {c.InflightBytes:N0} B · frames sent {c.FramesSent:N0}, acked to {c.FramesAcked:N0}" +
                (c.StreamingBytesPerSecond > 0 ? $" · capped {Rate(c.StreamingBytesPerSecond)}" : "") +
                (c.MaxFramesPerSecond > 0 ? $" · {c.MaxFramesPerSecond} frames/s at most" : "") +
                (c.StateHolds > 0 || c.HoldingState
                    ? $" · state held for an overdue acknowledgement {c.StateHolds:N0}×{(c.HoldingState ? " (now)" : "")}"
                    : "")));
            p.Para(Text(
                $"memory {Memory(c)} (its subscriptions: taps, history rings, mailboxes, snapshots waiting) · " +
                (c.WaitingMs is { } w ? $"waiting {w / 1000:F1} s without progress" : "nothing waiting") +
                (c.StallTimeoutMs > 0 ? $" (stalled at {c.StallTimeoutMs / 1000:F0} s: its subscriptions ended)" : "") +
                $" · stalls {c.Stalls}"));
            await p.TableAsync("Subscriptions",
                [
                    "subscription", "topic", "state", "class", "version / topic's", "holds", "service",
                    "provider instance", "last sent", "its lag", "acked after"
                ],
                taps.Select(t => new[]
                {
                    Link(to.Subscription(c.Serial, t.Id), t.Id.ToString(CultureInfo.InvariantCulture)),
                    Link(to.Topic(t.TopicId), t.Topic ?? t.Source ?? "?"),
                    Text(t.State + (t.AwaitingFetch ? " (awaiting a fetch)" : "")), Text(t.Class),
                    Text($"{t.Version:N0} / {t.TopicVersion:N0}"), Text(Size(t.Bytes)),
                    t.Service is { } s ? Link(to.Service(s), s) : "",
                    t.Provider is { } i ? Link(to.Instance(i.Id), i.Name) : "",
                    Text(t.Messages.Count > 0 ? Time(t.Messages[0].SentAt) : ""),
                    Text(t.Messages.Count > 0 ? Ms(t.Messages[0].LagMs) : ""),
                    Text(t.Messages.Count > 0 ? Ms(t.Messages[0].AckedAfterMs) : "")
                }));
            await p.TableAsync("Frames (the last 64)",
                ["frame", "sent", "bytes", "subscription records", "acked after"], frames.Select(f => new[]
                {
                    Text(f.Seq), Text(Time(f.SentAt)), Text(f.Bytes), Text(f.Records), Text(Ms(f.AckedAfterMs))
                }));
        }

        public static async Task Subscription(Page p, Links to, long client, TapView t)
        {
            p.Heading($"Subscription {t.Id} — {t.Topic ?? t.Source ?? "?"}");
            p.Para(Link(to.Router, "router") + " · " + Link(to.Client(client), $"client {client}") + " · " +
                   Link(to.Topic(t.TopicId), $"topic {t.Source ?? t.Topic}"));
            await p.TableAsync(null,
                [
                    "state", "class", "initial", "version / topic's", "holds", "bound", "service",
                    "partition (to its client)", "provider instance", "node id (the client's)", "route", "epoch"
                ],
                [
                    [
                        Text(t.State + (t.AwaitingFetch ? " (awaiting a fetch)" : "") +
                             (t.Stale ? ", stale: its provider degraded" : "")),
                        Text(t.Class), Text(t.Initial),
                        Text($"{t.Version:N0} / {t.TopicVersion:N0}"), Text(Size(t.Bytes)), Text(Time(t.BoundAt)),
                        t.Service is { } s ? Link(to.Service(s), s) : "",
                        Text(t.ServicePath is { } path ? $"{path[(path.IndexOf('/') + 1)..]} ({t.PartitionId})" : ""),
                        t.Provider is { } i ? Link(to.Instance(i.Id), $"{i.Name} ({i.Id})") : "", Text(t.NodeId),
                        Text(t.Route), Text(t.Epoch)
                    ]
                ]);
            await p.TableAsync("Its last records",
                [
                    "#", "frame", "kind", "bytes", "version", "upstream", "received (from the provider)", "sent", "lag",
                    "acked after", "kept", "first bytes"
                ],
                t.Messages.Select(m => new[]
                {
                    Link(to.Record(client, t.Id, m.N), m.N.ToString(CultureInfo.InvariantCulture)), Text(m.Frame),
                    Text(m.Blob is null ? m.Kind : $"{m.Kind} ({m.Inner})"), Text(m.Bytes), Text(m.Version),
                    Text(Upstream(m.Upstream, m.UpstreamSince)), Text(Time(m.ReceivedAt)), Text(Time(m.SentAt)),
                    Text(Ms(m.LagMs)), Text(Ms(m.AckedAfterMs)),
                    Text(m.Blob is not null ? "by handle" :
                        m.Whole ? "whole" :
                        m.Preview.Length == 0 ? "none" :
                        m.Preview.Length >= m.Bytes * 2 ? "whole" : "first bytes"),
                    Text(m.Preview)
                }), "no records yet");
        }

        /// <summary>A record's upstream versions, as its provider's publishes gave them: "j → k", "k", or "—".</summary>
        private static string Upstream(long? upstream, long? since)
        {
            return upstream is not { } k ? "—" : since is { } j ? $"{j:N0} → {k:N0}" : $"{k:N0}";
        }

        public static async Task Record(Page p, Links to, long client, TapView t, long n, MessageView? m,
            BlobView? blob, byte[]? inBand, string? rendered, string? view)
        {
            p.Heading($"Record {n} of subscription {t.Id} — {t.Topic ?? t.Source ?? "?"}");
            p.Para(Link(to.Router, "router") + " · " + Link(to.Client(client), $"client {client}") + " · " +
                   Link(to.Subscription(client, t.Id), $"subscription {t.Id}") +
                   " · " + Link(to.Topic(t.TopicId), "its topic, as it is now"));
            if (m is null)
            {
                p.Para(Text(
                    $"No longer in the subscription's history: it keeps its last {Math.Max(t.Messages.Count, 1)} records" +
                    (t.Messages.Count > 0 ? $" (now {t.Messages[^1].N}–{t.Messages[0].N})." : ".")));
                return;
            }

            await p.TableAsync(null,
                [
                    "frame", "kind", "bytes", "version", "upstream (the provider's)", "received (from the provider)",
                    "sent", "lag", "acked", "acked after"
                ],
                [
                    [
                        Text(m.Frame), Text(m.Kind), Text(m.Bytes), Text(m.Version),
                        Text(Upstream(m.Upstream, m.UpstreamSince)), Text(Time(m.ReceivedAt)), Text(Time(m.SentAt)),
                        Text(Ms(m.LagMs)),
                        Text(Time(m.AckedAt)), Text(Ms(m.AckedAfterMs))
                    ]
                ]);
            if (rendered is not null)
            {
                p.Section("Rendered");
                p.Pre(rendered);
            }

            if (view is not null)
            {
                if (rendered is null)
                    p.Para(Text(
                        $"Not kept whole here: its provider's view looks it up by its upstream versions ({Upstream(m.Upstream, m.UpstreamSince)}), if it can."));
                p.View(view);
            }

            if (blob is null && inBand is { Length: > 0 } whole && whole.Length == m.Bytes)
            {
                p.Para(Text($"Sent in-band, {m.Bytes:N0} bytes — kept whole, as sent: ") +
                       Link(to.Record(client, t.Id, n, true), "its bytes") +
                       Text(whole.Length > BlobHead ? $" (the first {BlobHead:N0} below)" : ""));
                p.Hex(whole.AsSpan(0, Math.Min(whole.Length, BlobHead)), whole.Length);
            }
            else if (blob is null && m.Preview.Length > 0)
            {
                p.Para(Text(
                    $"Sent in-band: its first {m.Preview.Length / 2} bytes are kept, not the whole — its subscription's history holds the newest records whole, " +
                    "while they fit, or its topic keeps less (its topic's current state is on the topic's page)."));
                p.Hex(Convert.FromHexString(m.Preview), m.Bytes);
            }
            else if (blob is null)
            {
                p.Para(Text(
                    "Sent in-band: its bytes aren't kept — its topic keeps its records' metadata alone (retention: none)."));
            }
            else if (blob.Head is not { } head)
            {
                p.Para(Text(blob.State == nameof(BlobLookup.Busy)
                    ? $"Out of band (a {m.Inner} record, blob {blob.Id}): the blob store was busy just now — not waited for. Try again."
                    : $"Out of band (a {m.Inner} record, blob {blob.Id}): no longer kept — released after its last fetch, or evicted, between being sent and now."));
            }
            else
            {
                p.Para(
                    Text(
                        $"Out of band: a {m.Inner} record, blob {blob.Id}, {blob.Length:N0} bytes{(blob.ContentAddressed == true ? ", content-addressed (its id is its SHA-256)" : "")} — still kept: ") +
                    Link(to.Record(client, t.Id, n, true), "its bytes") +
                    Text(head.Length < blob.Length ? $" (the first {head.Length:N0} below)" : ""));
                p.Hex(head, blob.Length!.Value);
            }
        }

        public static async Task Topic(Page p, Links to, TopicView topic, string? format, List<string> formatNotes,
            string? state, int stateRows, string? view,
            IEnumerable<(Outlet O, Tap T)> subscribers, Paging paging)
        {
            p.Heading($"Topic {topic.Label ?? "?"}");
            p.Para(Link(to.Router, "router") + Text(
                $" · id {topic.Id} · {topic.Kind} · version {topic.Version:N0}{(topic.Upstream is { } up ? $" (upstream {up:N0})" : "")} · epoch {topic.Epoch}{(topic.EpochClosed ? " (closed: the last publish found nobody)" : "")} · " +
                $"routes {topic.Routes:N0} · changed {Ago(topic.ChangedAt)}{(topic.Fault is { } f ? $" · faulted: {f}" : "")}"));
            p.Section("Format");
            if (formatNotes.Count > 0) p.Para(Text(string.Join(" · ", formatNotes)));
            p.Pre(format ?? "none given: payloads are shown as their bytes");
            p.Section(stateRows == int.MaxValue ? "State" : $"State (at most {stateRows:N0} rows or messages)");
            p.Pre(state ?? "nothing published");
            if (view is not null && state is not null) p.View(view);
            await p.TableAsync("Subscribers" + PageNote(paging),
                ["subscription", "client", "state", "version", "service", "provider instance"],
                subscribers.Select(s => SubscriberRow(to, s)),
                paging.After is null ? "none" : "no more");
            if (paging.Next is { } next) p.Para(Link(to.Topic(topic.Id, next, paging), "next page →"));
        }

        public static async Task Service(Page p, Links to, ServiceView s, List<EndpointView> endpoints,
            List<InstanceView?> nodes, string? drains)
        {
            p.Heading($"Service {s.Name}");
            p.Para(Link(to.Router, "router") +
                   Text(
                       $" · clients waiting for it to be up {s.Waiting} · up policy: quorum {s.Quorum}, grace {s.GraceMs:F0} ms, spread {s.SpreadMs:F0} ms" +
                       $" · access {s.Access ?? "not declared yet (none subscribed)"}"));
            string[] head = drains is null
                ? ["endpoint", "status", "refuses this router", "instance there", "heard", "load"]
                : ["endpoint", "status", "refuses this router", "instance there", "heard", "load", "its node, drained here"];
            await p.TableAsync("Endpoints", head, endpoints.Select((e, n) =>
            {
                string[] row =
                [
                    Link(to.Endpoint(e.Endpoint), e.Endpoint), Text(e.Status), Text(e.Refused ? "yes" : ""),
                    e.Instance is { } i ? Link(to.Instance(i.Id), $"{i.Name} ({i.Id})") : "", Text(Ago(e.HeardAt)),
                    Text(LoadOf(e.Load))
                ];
                return drains is null ? row : [.. row, NodeDrain(drains, to.Service(s.Name), nodes[n])];
            }));
            if (drains is not null)
                p.Para(Text(
                    "Draining a node moves its clients to the service's other nodes, each with the grace to subscribe again before its subscriptions here " +
                    "are let go — as the provider's own drain does, though it isn't asked. One way: no new subscriptions to it from this router until another " +
                    "instance answers at its endpoint (with none other serving, the service is unavailable here till then)."));
        }

        /// <summary>A node's drain, by hand: a form — or why not (draining already, dead, not tracked).</summary>
        private static string NodeDrain(string drains, string back, InstanceView? node)
        {
            return node switch
            {
                null => "",
                { Dead: true } => Text("dead"),
                { Draining: true } => Text("draining"),
                _ => DrainForm($"{drains}/nodes/{node.NodeId}/drain", back, null, "drain", ("over", "0s"),
                    ("grace", "5s"))
            };
        }

        public static async Task Endpoint(Page p, Links to, EndpointView e, List<string> services, bool checkedNow,
            HealthReport? now)
        {
            p.Heading($"Endpoint {e.Endpoint}");
            p.Para(Link(to.Router, "router") + " · services: " + Join(services.Select(s => Link(to.Service(s), s))));
            await p.TableAsync("As last reported", ["status", "refuses this router", "instance there", "heard", "load"],
            [
                [
                    Text(e.Status), Text(e.Refused ? "yes" : "no"),
                    e.Instance is { } i ? Link(to.Instance(i.Id), $"{i.Name} ({i.Id})") : "", Text(Ago(e.HeardAt)),
                    Text(LoadOf(e.Load))
                ]
            ]);
            if (!checkedNow)
                p.Para(Link(to.Endpoint(e.Endpoint, true), "check its health now") +
                       Text(" (a health check of whoever is there; it changes nothing the router knows)"));
            else if (now is not { } answered) p.Para(Text("Checked now: no answer within 2 s."));
            else
                await p.TableAsync("Checked now",
                    ["instance", "status", "ready", "refuses this router", "draining until", "load"],
                    [
                        [
                            Link(to.Instance(answered.Instance.Id), $"{answered.Instance.Name} ({answered.Instance.Id})"),
                            Text(answered.Status), Text(answered.Ready ? "yes" : "no"),
                            Text(answered.RouterRefused ? "yes" : "no"), Text(Time(answered.DrainUntil)),
                            Text(LoadOf(answered.Load))
                        ]
                    ]);
        }

        public static async Task Instance(Page p, Links to, InstanceDetail d, IEnumerable<(Outlet O, Tap T)> routes,
            Paging paging, string? drains)
        {
            var i = d.Instance;
            p.Heading($"Provider instance {i.Instance.Name}");
            p.Para(Link(to.Router, "router") + Text($" · id {i.Instance.Id} · ") +
                   Link(to.Endpoint(i.Instance.Endpoint.ToString()), i.Instance.Endpoint.ToString()) +
                   " · services: " + Join(d.Services.Select(s => Link(to.Service(s), s))));
            await p.TableAsync(null,
                [
                    "state", "node id (clients')", "routes held", "heard", "deemed dead",
                    "endpoint status (its last report)", "check"
                ],
                [
                    [
                        Text(State(i)), Text(i.NodeId), Text(i.Routes), Text(Ago(i.HeardAt)),
                        Text(d.DiedAt is { } at ? Ago(at) : ""),
                        Text(d.Endpoint is { } e
                            ? $"{e.Status}{(e.Refused ? ", refusing this router" : "")}{(e.Instance?.Id == i.Instance.Id ? "" : $" — another instance there now: {e.Instance?.Name} ({e.Instance?.Id})")}"
                            : "withdrawn"),
                        d.Endpoint is not null
                            ? Link(to.Endpoint(i.Instance.Endpoint.ToString(), true), "check now")
                            : ""
                    ]
                ]);
            if (drains is not null && !i.Dead && !i.Draining)
                p.Para(DrainForm($"{drains}/nodes/{i.NodeId}/drain", to.Instance(i.Instance.Id),
                    "Drain this node here — its clients moved to the service's other nodes, one at a time over the time given, each with the grace to subscribe " +
                    "again; one way: no new subscriptions to it from this router until another instance answers at its endpoint.",
                    "drain the node", ("over", "0s"), ("grace", "5s")));
            await p.TableAsync("Routes it holds" + PageNote(paging),
                ["subscription", "client", "topic", "state", "version", "route", "epoch"], routes.Select(r =>
                {
                    var tap = r.O.DescribeTap(r.T.Id, false);
                    return new[]
                    {
                        Link(to.Subscription(r.O.Serial, r.T.Id), r.T.Id.ToString(CultureInfo.InvariantCulture)),
                        Link(to.Client(r.O.Serial), r.O.Serial.ToString(CultureInfo.InvariantCulture)),
                        tap is null ? "" : Link(to.Topic(tap.TopicId), tap.Topic ?? tap.Source ?? "?"),
                        Text(tap?.State ?? "closed"), Text(tap?.Version), Text(tap?.Route), Text(tap?.Epoch)
                    };
                }), paging.After is null ? "none" : "no more");
            if (paging.Next is { } next) p.Para(Link(to.Instance(i.Instance.Id, next, paging), "next page →"));
        }

        private static string[] SubscriberRow(Links to, (Outlet O, Tap T) s)
        {
            var tap = s.O.DescribeTap(s.T.Id, false);
            return
            [
                Link(to.Subscription(s.O.Serial, s.T.Id), s.T.Id.ToString(CultureInfo.InvariantCulture)),
                Link(to.Client(s.O.Serial), s.O.Serial.ToString(CultureInfo.InvariantCulture)),
                Text(tap?.State ?? "closed"), Text(tap?.Version),
                tap?.Service is { } sv ? Link(to.Service(sv), sv) : "",
                tap?.Provider is { } i ? Link(to.Instance(i.Id), i.Name) : ""
            ];
        }

        private static string PageNote(Paging paging)
        {
            return paging.Unlimited ? "" :
                paging.After is null ? $" (the first {paging.Limit:N0})" : $" (the next {paging.Limit:N0})";
        }
    }

    /// <summary>
    ///     A plain HTML page written to the response as it's made: headings, paragraphs and tables of cells made by
    ///     <see cref="Text" /> and <see cref="Link" />, buffered a chunk at a time and written out asynchronously.
    /// </summary>
    private sealed class Page : IAsyncDisposable
    {
        private readonly CancellationToken _aborted;
        private readonly StringBuilder _html = new();
        private readonly StreamWriter _writer;

        public Page(HttpContext context, string title)
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.XFrameOptions =
                "DENY"; // never in another page's frame: its buttons not to be clicked through one
            context.Response.Headers.ContentSecurityPolicy = "frame-ancestors 'none'";
            _writer = new StreamWriter(context.Response.Body, new UTF8Encoding(false), ChunkChars, true);
            _aborted = context.RequestAborted;
            _html.Append(
                    "<!doctype html><html lang=en><head><meta charset=utf-8><meta name=viewport content=\"width=device-width\">")
                .Append("<title>").Append(WebUtility.HtmlEncode(title)).Append("</title>");
            if (int.TryParse(context.Request.Query["refresh"].ToString(), out var seconds) && seconds > 0)
                _html.Append("<meta http-equiv=refresh content=").Append(Math.Max(1, seconds)).Append('>');
            _html.Append(
                    "<style>body{font:13px ui-monospace,Menlo,Consolas,monospace;margin:16px;color:#222;background:#fff}")
                .Append(
                    "@media(prefers-color-scheme:dark){body{color:#ddd;background:#161616}th{background:#262626!important}td,th{border-color:#333!important}a{color:#8ab4f8}}")
                .Append(
                    "table{border-collapse:collapse;margin:4px 0 16px}td,th{border:1px solid #ddd;padding:2px 6px;text-align:left;vertical-align:top}")
                .Append(
                    "th{background:#f3f3f3;font-weight:600}h1{font-size:16px}h2{font-size:14px;margin:12px 0 4px}p{margin:4px 0 12px}")
                .Append(
                    ".shared{border:1px solid #d9a400;background:#fff6d6;color:#5c4400;padding:4px 8px;margin:0 0 12px}")
                .Append(
                    "form.act{margin:0}form.act input,form.act button{font:inherit}form.act label{white-space:nowrap}")
                .Append(
                    ".view{display:block;width:100%;height:420px;border:1px solid #ddd;margin:4px 0 16px}@media(prefers-color-scheme:dark){.view{border-color:#333}}")
                .Append(
                    "@media(prefers-color-scheme:dark){.shared{background:#3a2f0b;color:#f3d77a;border-color:#8a6d12}}</style></head><body>");
            if (SharedPort(context) is { } reason) // stated, not alarmed: why it's so here
                _html.Append("<p class=shared role=note>Ops endpoints share the client-facing port: ")
                    .Append(WebUtility.HtmlEncode(reason)).Append(".</p>");
        }

        public ValueTask DisposeAsync()
        {
            return _writer.DisposeAsync();
        }

        public void Heading(string title)
        {
            _html.Append("<h1>").Append(WebUtility.HtmlEncode(title)).Append("</h1>");
        }

        /// <summary>Bytes as a hex dump: offset, sixteen bytes, their printable characters.</summary>
        public void Hex(ReadOnlySpan<byte> bytes, int total)
        {
            _html.Append("<pre>");
            for (var o = 0; o < bytes.Length; o += 16)
            {
                var row = bytes.Slice(o, Math.Min(16, bytes.Length - o));
                _html.Append(o.ToString("x8", CultureInfo.InvariantCulture)).Append("  ");
                for (var i = 0; i < 16; i++)
                    _html.Append(i < row.Length ? row[i].ToString("x2", CultureInfo.InvariantCulture) : "  ")
                        .Append(i == 7 ? "  " : " ");
                _html.Append(' ');
                foreach (var b in row)
                    _html.Append(WebUtility.HtmlEncode(b is >= 0x20 and < 0x7f ? ((char)b).ToString() : "."));
                _html.Append('\n');
            }

            if (bytes.Length < total) _html.Append(WebUtility.HtmlEncode($"… {total - bytes.Length:N0} more bytes"));
            _html.Append("</pre>");
        }

        /// <summary>A paragraph of markup made here (<see cref="Text" />, <see cref="Link" />).</summary>
        public void Para(string markup)
        {
            _html.Append("<p>").Append(markup).Append("</p>");
        }

        public void Section(string title)
        {
            _html.Append("<h2>").Append(WebUtility.HtmlEncode(title)).Append("</h2>");
        }

        /// <summary>
        ///     The provider's view of it, in a frame — sandboxed (an opaque origin: scripts run, nothing of this page is
        ///     theirs) — and a link to it alone.
        /// </summary>
        public void View(string src)
        {
            Section("View, by its provider");
            Para(Link(src, "open it alone") +
                 Text(" · the router posts it to the provider's view URL when asked, and shows what it answers"));
            _html.Append(
                    "<iframe class=view sandbox=allow-scripts loading=lazy referrerpolicy=no-referrer title=\"the provider's view\" src=\"")
                .Append(WebUtility.HtmlEncode(src)).Append("\"></iframe>");
        }

        /// <summary>Text as it is (encoded), preformatted.</summary>
        public void Pre(string text)
        {
            _html.Append("<pre>").Append(WebUtility.HtmlEncode(text)).Append("</pre>");
        }

        /// <summary>
        ///     A table of cells made here (<see cref="Text" />, <see cref="Link" />), its rows made and written out as they
        ///     come.
        /// </summary>
        public async Task TableAsync(string? title, string[] head, IEnumerable<string[]> rows, string empty = "none")
        {
            if (title is not null) _html.Append("<h2>").Append(WebUtility.HtmlEncode(title)).Append("</h2>");
            var n = 0;
            _html.Append("<table><tr>");
            foreach (var h in head) _html.Append("<th>").Append(WebUtility.HtmlEncode(h)).Append("</th>");
            _html.Append("</tr>");
            foreach (var row in rows)
            {
                n++;
                _html.Append("<tr>");
                foreach (var cell in row) _html.Append("<td>").Append(cell).Append("</td>");
                _html.Append("</tr>");
                if (_html.Length >= ChunkChars) await FlushAsync();
            }

            if (n == 0)
                _html.Append("<tr><td colspan=").Append(head.Length).Append('>').Append(WebUtility.HtmlEncode(empty))
                    .Append("</td></tr>");
            _html.Append("</table>");
        }

        /// <summary>What's made so far, written out.</summary>
        public async Task FlushAsync()
        {
            if (_html.Length == 0) return;
            await _writer.WriteAsync(_html, _aborted);
            _html.Clear();
            await _writer.FlushAsync(_aborted);
        }

        public async Task EndAsync()
        {
            _html.Append("</body></html>");
            await FlushAsync();
        }
    }
}