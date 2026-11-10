using System.Security.Cryptography;

namespace SignIn.Sample;

/// <summary>
///     The page's Content-Security-Policy. The password lives in the page's script until OPAQUE has used it, so the
///     policy decides what can read it: scripts only from this origin, plus the page's one inline module, admitted by a
///     nonce minted for each response; WebAssembly compiled (Argon2id and OPAQUE in opaque.wasm, Tapline's own module);
///     connections only back to this origin, the circuit's WebSocket included; no plugins, no framing, no form
///     submissions anywhere. Styles may be inline: the sample styles its markup in place.
/// </summary>
internal static class ContentSecurity
{
    private const string NonceKey = "SignIn.Sample.CspNonce";

    /// <summary>Sends the policy with every response, a fresh nonce for each.</summary>
    public static void Use(WebApplication app)
    {
        app.Use((context, next) =>
        {
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            context.Items[NonceKey] = nonce;
            var socket = $"{(context.Request.IsHttps ? "wss" : "ws")}://{context.Request.Host}"; // ('self' names ws: only in newer browsers)
            context.Response.Headers.ContentSecurityPolicy =
                "default-src 'self'; " +
                $"script-src 'self' 'nonce-{nonce}' 'wasm-unsafe-eval'; " +
                "style-src 'self' 'unsafe-inline'; " +
                "img-src 'self' data:; " +
                $"connect-src 'self' {socket}; " +
                "object-src 'none'; base-uri 'self'; form-action 'none'; frame-ancestors 'none'";
            return next(context);
        });
    }

    /// <summary>This response's nonce, for the page's inline module script (App.razor).</summary>
    public static string? Nonce(HttpContext? context)
    {
        return context?.Items[NonceKey] as string;
    }
}
