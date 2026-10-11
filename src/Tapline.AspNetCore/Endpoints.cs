using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Tapline.AspNetCore;

public static class TaplineEndpointRouteBuilderExtensions
{
    /// <summary>
    ///     Maps the out-of-band fetch endpoint, <c>GET {prefix}/blob/{blobId}/{outletId}/{tag}</c> (the client builds the
    ///     URL from the handle). Blobs are immutable and the URL is specific to one outlet, so responses are privately
    ///     cacheable forever; a single Range is supported (resumable downloads). The blob's segments are written as they are
    ///     stored — no joined copy — under a lease, so an eviction meanwhile doesn't recycle them mid-response.
    ///     <para>
    ///         And <c>GET {prefix}/module/{moduleId}</c>: the WebAssembly module of a subscription's stale-data function
    ///         (<see cref="IPortableDegradeFunction" />), while a subscription bound with it is — fetched by its client at
    ///         once, to
    ///         apply while the router is out of its reach. Content-addressed: cacheable by anyone, forever.
    ///     </para>
    ///     Whatever the transport, its clients fetch over plain HTTP (Tapline.BlazorRealtime's <c>MapBlazorRealtime</c> maps these
    ///     too). Returns the group of both endpoints.
    /// </summary>
    public static RouteGroupBuilder MapTapline(this IEndpointRouteBuilder endpoints, string prefix = "/_tapline")
    {
        var group = endpoints.MapGroup(prefix.TrimEnd('/'));
        group.MapGet("/blob/{id}/{outlet:int}/{tag}", static (string id, int outlet, string tag, OutletRegistry outlets)
            => outlets.Blobs.TryLease(id, outlet, tag, outlets, out var lease)
                ? new BlobResult(lease, id)
                : Results.NotFound()).ExcludeFromDescription();
        group.MapGet("/module/{id}", static (string id, HttpContext http, OutletRegistry outlets) =>
        {
            if (!outlets.Modules.TryGet(id, out var module)) return Results.NotFound();
            http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            http.Response.Headers.ETag = $"\"{id}\"";
            return Results.Bytes(module, "application/wasm");
        }).ExcludeFromDescription();
        return group;
    }
}

/// <summary>A blob's bytes (or one range of them), written segment by segment; the lease is released when done or aborted.</summary>
internal sealed class BlobResult(BlobLease lease, string id) : IResult
{
    public async Task ExecuteAsync(HttpContext http)
    {
        using (lease)
        {
            var response = http.Response;
            long length = lease.Length, from = 0, to = length - 1;
            response.Headers.CacheControl = "private, max-age=31536000, immutable";
            response.Headers.ETag = $"\"{id}\"";
            response.Headers.AcceptRanges = "bytes";
            response.ContentType = "application/octet-stream";
            if (http.Request.GetTypedHeaders().Range is { Unit.Value: "bytes", Ranges.Count: 1 } range)
            {
                var r = range.Ranges.First();
                if (r.From is { } f)
                {
                    from = f;
                    to = Math.Min(r.To ?? length - 1, length - 1);
                }
                else if (r.To is { } suffix)
                {
                    from = Math.Max(0, length - suffix); // the last n bytes
                }

                if (from > to || from >= length)
                {
                    response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                    response.Headers.ContentRange = $"bytes */{length}";
                    return;
                }

                response.StatusCode = StatusCodes.Status206PartialContent;
                response.Headers.ContentRange = $"bytes {from}-{to}/{length}";
            }

            var data = length == 0 ? default : lease.Data.Slice(from, to - from + 1);
            response.ContentLength = data.Length;
            foreach (var segment in data) await response.Body.WriteAsync(segment, http.RequestAborted);
        }
    }
}