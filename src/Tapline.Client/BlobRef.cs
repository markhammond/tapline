using System.Security.Cryptography;

namespace Tapline.Client;

/// <summary>
///     A record's bytes out of band: fetched over HTTP from the router (Tapline.AspNetCore's <c>MapTapline</c>,
///     <c>GET {blobBase}/{id}/{outletId}/{tag}</c>) — and, when content-addressed, checked against their SHA-256.
/// </summary>
public sealed class BlobRef
{
    private readonly TaplineClient _client;
    private readonly byte[] _id;

    internal BlobRef(TaplineClient client, ReadOnlySpan<byte> id, ReadOnlySpan<byte> tag, int length,
        bool contentAddressed)
    {
        _client = client;
        _id = id.ToArray();
        (Id, Length, ContentAddressed) = (Convert.ToHexStringLower(id), length, contentAddressed);
        Url = client.BlobBase is { } at ? new Uri(at, $"{Id}/{client.OutletId}/{Convert.ToHexStringLower(tag)}") : null;
    }

    /// <summary>The blob's id (hex): its content address (the first 16 bytes of its SHA-256), or a unique id.</summary>
    public string Id { get; }

    /// <summary>Its length in bytes.</summary>
    public int Length { get; }

    /// <summary>Its id is its content address: the bytes fetched are checked against it.</summary>
    public bool ContentAddressed { get; }

    /// <summary>Where it's fetched — null without <see cref="TaplineClientOptions.BlobBase" />.</summary>
    public Uri? Url { get; }

    /// <summary>Fetches the bytes, and checks them if content-addressed.</summary>
    public async Task<byte[]> FetchAsync(CancellationToken cancellationToken = default)
    {
        var url = Url ??
                  throw new InvalidOperationException(
                      "No TaplineClientOptions.BlobBase: out-of-band records can't be fetched.");
        var bytes = await _client.FetchAsync(url, cancellationToken).ConfigureAwait(false);
        if (ContentAddressed && !SHA256.HashData(bytes).AsSpan(0, 16).SequenceEqual(_id))
            throw new InvalidDataException($"Blob {Id} failed verification: its bytes aren't its content address.");
        return bytes;
    }
}