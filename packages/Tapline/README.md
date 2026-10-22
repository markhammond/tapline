# Tapline

[Tapline](https://github.com/markhammond/tapline/blob/main/README.md)'s router hosted in ASP.NET Core: real-time, last-mile fan-out of shared state and ordered messages to many clients, prioritised, conflated and ack-clocked, over the transport you already have.

- **Blazor Server:** Tapline over the circuit's own SignalR connection: no second transport, Blazor's own messages never reordered, frames interleaved in priority lanes, each client paced from its acknowledgements.
- **MagicOnion** (gRPC), the router's side: a session's outlet held across its calls, frames written one at a time as the call takes them.
- **Data providers in a process of their own**, reached by routers over MagicOnion: their topics, step 1s, notices, health, heartbeats, drains and mailboxes.
- **Sign-in** by a realm's authentication service of modules (passkeys by WebAuthn, passthrough, tickets, your own), each handing a proven user to your backend, which says who they are.
- **Hosting:** the router's identity, heartbeats and health; the out-of-band fetch and module endpoints; the ops port and the router's viewer; and the browser client, `tapline.js`, served at `_content/Tapline/`.

```csharp
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddBlazorRealtime();          // the circuit transport, and the router it serves
// ...
app.MapBlazorRealtime();                       // the out-of-band and module endpoints
```

The core and the .NET client are [Tapline.Client](https://www.nuget.org/packages/Tapline.Client), which this brings in.
WebAssembly folds and password sign-in are [Tapline.Wasm](https://www.nuget.org/packages/Tapline.Wasm).

## Documentation

- [Architecture](https://github.com/markhammond/tapline/blob/main/docs/architecture.md)
- [Data providers](https://github.com/markhammond/tapline/blob/main/docs/provider.md)
- [Clients](https://github.com/markhammond/tapline/blob/main/docs/client.md)
- [Transports](https://github.com/markhammond/tapline/blob/main/docs/transports.md)
- [Signing users in](https://github.com/markhammond/tapline/blob/main/docs/provider.md#signing-users-in)
- [Observability](https://github.com/markhammond/tapline/blob/main/docs/observability.md)

## License

Elastic License 2.0 ([ELv2](https://www.elastic.co/licensing/elastic-license)), included as `LICENSE`.
The source code is available under the same license at [github.com/markhammond/tapline](https://github.com/markhammond/tapline).
