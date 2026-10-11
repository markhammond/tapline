namespace PivotTable.Headless;

/// <summary>
///     Where it streams from: each router's stream and out-of-band fetches, in order, and the first router's ops
///     endpoints.
/// </summary>
internal sealed record Endpoints(Uri[] Streams, Uri[] Blobs, Uri Ops)
{
    /// <summary>
    ///     The routers named (<c>--stream</c>): their fetches and the first's ops endpoints as given, else as the sample lays
    ///     its ports out — the app two below the stream, the ops one below.
    /// </summary>
    public static Endpoints Of(Options options)
    {
        Uri[] streams = [.. options.Stream!.Select(s => new Uri(s))];

        static Uri Below(Uri stream, int ports, string path)
        {
            return new UriBuilder(stream) { Port = stream.Port - ports, Path = path }.Uri;
        }

        Uri[] blobs =
        [
            .. streams.Select((s, i) =>
                options.Blobs is { } given && i < given.Length
                    ? new Uri(Slashed(given[i]))
                    : Below(s, 2, "/_tapline/blob/"))
        ];
        return new Endpoints(streams, blobs,
            options.Ops is { } ops ? new Uri(Slashed(ops)) : Below(streams[0], 1, "/"));
    }

    /// <summary>A spawned backend's: its stream, its app's fetches, and its ops endpoints — on its app's port (shared).</summary>
    public static Endpoints Of(Backend backend)
    {
        return new Endpoints([backend.Stream], [new Uri(backend.App, "_tapline/blob/")], backend.App);
    }

    private static string Slashed(string url)
    {
        return url.EndsWith('/') ? url : url + "/";
    }
}