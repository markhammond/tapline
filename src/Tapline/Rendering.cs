using System.Text;
using System.Text.Json;

namespace Tapline;

/// <summary>
///     Topics' payloads as JSON, for the router's viewer — never on a publish or a frame's path — by their topic's
///     <see cref="PayloadFormat" />: a fixed-size payload by its layout, read from its bytes (no types, no reflection: as
///     well for a provider in another process, and under Native AOT); a value's or a message's by its content type, JSON
///     as
///     it is, text as a string; anything else as its bytes, hex.
/// </summary>
internal static class Rendering
{
    /// <summary>The most of one payload rendered (beyond: its first bytes, hex).</summary>
    public const int MaxPayload = 1 << 20;

    /// <summary>The most of an unrenderable payload shown, hex.</summary>
    public const int MaxHex = 4096;

    /// <summary>
    ///     A payload by its format: by its layout if it's that size, else by its content type; false if neither applies
    ///     (show its bytes).
    /// </summary>
    public static bool Payload(Utf8JsonWriter w, ReadOnlySpan<byte> payload, PayloadFormat? format)
    {
        if (format?.Layout is { } layout && payload.Length == layout.Size)
        {
            layout.Render(w, payload);
            return true;
        }

        if (format?.ContentType is not { } contentType || payload.Length > MaxPayload) return false;
        var media = contentType.Split(';', 2)[0].Trim();
        if (media.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
            media.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
        {
            var reader = new Utf8JsonReader(payload);
            try
            {
                if (!JsonDocument.TryParseValue(ref reader, out var document)) return false;
                using (document)
                {
                    document.WriteTo(w);
                }

                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        if (media.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            w.WriteStringValue(Encoding.UTF8.GetString(payload));
            return true;
        }

        return false;
    }

    /// <summary>A payload: rendered by its format, or — if it can't be — its length and first bytes, hex.</summary>
    public static void PayloadOrBytes(Utf8JsonWriter w, ReadOnlySpan<byte> payload, PayloadFormat? format)
    {
        if (Payload(w, payload, format)) return;
        w.WriteStartObject();
        w.WriteNumber("bytes", payload.Length);
        w.WriteString("hex", Convert.ToHexString(payload[..Math.Min(payload.Length, MaxHex)]));
        if (payload.Length > MaxHex) w.WriteBoolean("truncated", true);
        w.WriteEndObject();
    }

    /// <summary>A table's key or row: by its layout, or — none — its bytes, hex.</summary>
    public static void Fixed(Utf8JsonWriter w, ReadOnlySpan<byte> bytes, PayloadLayout? layout)
    {
        if (layout is not null) layout.Render(w, bytes);
        else w.WriteStringValue(Convert.ToHexString(bytes));
    }
}