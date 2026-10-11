// blazor-realtime.js — BlazorRealtime's browser side: Tapline's client (tapline.js, from Tapline.AspNetCore) carried
// over the Blazor Server circuit's own SignalR connection.
//
//   <script src="_framework/blazor.web.js" autostart="false"></script>
//   <script type="module">
//     import { tapline } from './_content/Tapline.AspNetCore/tapline.js';
//     import { startBlazor } from './_content/BlazorRealtime/blazor-realtime.js';
//     startBlazor(Blazor, tapline);
//   </script>

/**
 * Starts Blazor with the circuit's HubConnection routed through the client (also on every reconnect), and binds the
 * circuit's outlet to it when the server says which it is.
 *
 * A page restored from the browser's back-forward cache (Safari keeps a page with an open WebSocket there; Chrome
 * doesn't) comes back as it was left: its circuit's connection closed as it went, its subscriptions shown their stale
 * view, nothing to bring them back. So it's loaded again — a circuit and subscriptions of its own, as a page visited
 * anew. `options.reloadOnRestore: false` leaves it to the page.
 */
export function startBlazor(Blazor, client, options = {}) {
    if (!client?.attach || !client?.bind) throw new TypeError('startBlazor(Blazor, client): the Tapline client to carry (tapline.js\'s tapline)');
    globalThis.taplineBind = (_token, id, nonce) => client.bind(id, nonce);
    const {reloadOnRestore = true, ...start} = options;
    if (reloadOnRestore)
        globalThis.addEventListener?.('pageshow', event => {
            if (event.persisted) location.reload();
        });
    const circuit = start.circuit ?? {};
    return Blazor.start({
        ...start,
        circuit: {
            ...circuit,
            configureSignalR: builder => {
                circuit.configureSignalR?.(builder);
                const build = builder.build.bind(builder);
                builder.build = () => client.attach(build());
            },
        },
    });
}
