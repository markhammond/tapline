// blazor-realtime.js — BlazorRealtime's browser side: Tapline's client (tapline.js, from Tapline.AspNetCore) carried
// over the Blazor Server circuit's own SignalR connection.
//
//   <script src="_framework/blazor.web.js" autostart="false"></script>
//   <script type="module">
//     import { tapline } from './_content/Tapline/tapline.js';
//     import { startBlazor } from './_content/Tapline/blazor-realtime.js';
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
 *
 * Animations the server's prerendered page is running (a grid's skeleton shimmering, say) go on where they'd got to as
 * Blazor draws the page again for the circuit, not afresh: each new one started when the one of its name began
 * (`options.continueAnimations: false` leaves them).
 *
 * The page pings the server every `options.keepAliveInterval` ms it sends nothing else (3,000; 0: SignalR's own, 15 s).
 * The server drops a circuit it hasn't heard from in 10 s (BlazorRealtimeOptions.CircuitClientTimeout), and a page
 * acknowledges each frame only once it's all there: on a slow link, behind a large message, it can't for longer than
 * that — but its pings go up all the same, whatever is still coming down. The page's own configureSignalR has the last
 * word.
 */
export function startBlazor(Blazor, client, options = {}) {
    if (!client?.attach || !client?.bind) throw new TypeError('startBlazor(Blazor, client): the Tapline client to carry (tapline.js\'s tapline)');
    globalThis.taplineBind = (_token, id, nonce) => client.bind(id, nonce);
    const {reloadOnRestore = true, keepAliveInterval = 3_000, continueAnimations = true, ...start} = options;
    if (continueAnimations) continuing();
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
                if (keepAliveInterval > 0) builder.withKeepAliveInterval(keepAliveInterval);
                circuit.configureSignalR?.(builder);
                const build = builder.build.bind(builder);
                builder.build = () => client.attach(build());
            },
        },
    });
}

/**
 * The prerendered page's running animations, by name and when each began (once it has: pending, at its first frame): as
 * Blazor replaces the page's elements with its own, an animation of a name begun since is set to have begun as the old
 * one did. Looked at as elements are added, till those that ran them are gone (redrawn), or a minute on.
 */
function continuing() {
    if (!globalThis.document?.getAnimations || !globalThis.MutationObserver) return;   // (no page: nothing drawn)
    const began = new Map(), ran = new Set();
    for (const a of document.getAnimations()) {
        if (!a.animationName || !a.effect?.target) continue;
        ran.add(a.effect.target);
        const note = () => a.startTime !== null && !began.has(a.animationName) && began.set(a.animationName, a.startTime);
        if (a.startTime === null) a.ready.then(note, () => {});
        else note();
    }
    if (ran.size === 0) return;
    const observer = new MutationObserver(records => {
        if (!records.some(r => [...r.addedNodes].some(n => n.nodeType === Node.ELEMENT_NODE))) return;
        for (const a of document.getAnimations()) {
            const from = began.get(a.animationName);
            if (from !== undefined && (a.startTime === null || a.startTime > from) && !ran.has(a.effect?.target))
                a.startTime = from;   // (begun since, or pending: just made)
        }
        for (const target of ran) if (!target.isConnected) ran.delete(target);
        if (ran.size === 0) observer.disconnect();   // (all redrawn: those since are the page's own)
    });
    observer.observe(document.body, {childList: true, subtree: true});
    setTimeout(() => observer.disconnect(), 60_000);
}
