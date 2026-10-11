using System.Net;
using System.Security.Cryptography;
using Microsoft.Net.Http.Headers;

namespace BlazorRealtime.Sample;

/// <summary>
///     The caption's font, when this machine has one for it: <c>Waveform:TaglineFont</c> (or <c>WAVEFORM_TAGLINE_FONT</c>
///     ), the
///     path of a font file of your own (.otf, .ttf, .woff2, .woff). None is bundled — a personal-use font's license
///     forbids
///     sharing it or putting it on a network (NOTICE.md) — and it is served (<c>/tagline-font/{fingerprint}</c>) to this
///     machine alone: a viewer anywhere else, and every viewer without one set, gets the caption in its theme's font,
///     bundled.
///     <c>Waveform:TaglineThemes</c> (or <c>WAVEFORM_TAGLINE_THEMES</c>): the themes it is for, comma-separated (e.g.
///     <c>retro,paper</c>); unset, all four.
/// </summary>
public sealed class TaglineFont
{
    public TaglineFont(IConfiguration config, ILogger<TaglineFont> log)
    {
        var path = config["Waveform:TaglineFont"] ?? Environment.GetEnvironmentVariable("WAVEFORM_TAGLINE_FONT");
        if (string.IsNullOrWhiteSpace(path)) return;
        var themes = config["Waveform:TaglineThemes"] ??
                     Environment.GetEnvironmentVariable("WAVEFORM_TAGLINE_THEMES") ?? "amber,green,retro,paper";
        Themes = themes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 0 && t.All(char.IsAsciiLetterLower))
            .ToArray(); // (theme names: they go into the page's CSS)
        path = Path.GetFullPath(path.Trim());
        (string, string)? type = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".otf" => ("font/otf", "opentype"),
            ".ttf" => ("font/ttf", "truetype"),
            ".woff2" => ("font/woff2", "woff2"),
            ".woff" => ("font/woff", "woff"),
            _ => null
        };
        if (type is null || !File.Exists(path))
        {
            log.LogWarning(
                "Waveform:TaglineFont: {Path} isn't a font file here (.otf, .ttf, .woff2, .woff) — the caption stays in its theme's font",
                path);
            return;
        }

        (FilePath, ContentType, Format) = (path, type.Value.Item1, type.Value.Item2);
        Bytes = File.ReadAllBytes(path); // served as read now: what the fingerprint names, whatever happens to the file
        Fingerprint = Convert.ToHexStringLower(SHA256.HashData(Bytes).AsSpan(0, 8));
        log.LogInformation("The caption's font in {Themes}: {Path}, served to this machine only",
            string.Join(", ", Themes), path);
    }

    /// <summary>The themes whose caption it is.</summary>
    public string[] Themes { get; } = [];

    /// <summary>The font file, if one is set (and is one).</summary>
    public string? FilePath { get; }

    public string ContentType { get; } = "";

    /// <summary>Its CSS <c>format()</c>.</summary>
    public string Format { get; } = "";

    /// <summary>Its bytes, as read at startup.</summary>
    public byte[] Bytes { get; } = [];

    /// <summary>Of its bytes (SHA-256, the first 8 bytes in hex): in its URL — another font, another URL — so cached for good.</summary>
    public string Fingerprint { get; } = "";

    /// <summary>Where the page gets it (relative to the base href).</summary>
    public string Url => $"tagline-font/{Fingerprint}";

    /// <summary>
    ///     <c>GET /tagline-font/{fingerprint}</c>: the font, to a loopback client, if it is the one that fingerprint names —
    ///     cached for good (immutable: its URL changes with its bytes), privately (no shared cache keeps a copy). Anything
    ///     else 404s, as if there were none: another client, or another fingerprint (a page from before a restart with
    ///     another font) — never this font under that one's URL.
    /// </summary>
    public static void Map(WebApplication app)
    {
        app.MapGet("/tagline-font/{fingerprint}", (string fingerprint, HttpContext http, TaglineFont font) =>
            {
                if (font.FilePath is null || fingerprint != font.Fingerprint ||
                    http.Connection.RemoteIpAddress is not { } ip
                    || !IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip))
                    return Results.NotFound();
                http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
                return Results.Bytes(font.Bytes, font.ContentType,
                    entityTag: new EntityTagHeaderValue($"\"{font.Fingerprint}\""));
            })
            .ExcludeFromDescription();
    }
}