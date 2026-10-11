# Tapline.BlazorRealtime

[Tapline](https://github.com/markhammond/tapline/blob/main/README.md) over a Blazor Server circuit's own SignalR connection: no second transport, Blazor's own
messages never reordered, frames interleaved in priority lanes, each client paced from its acknowledgements.

```csharp
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddBlazorRealtime();          // the circuit transport, and the router it serves
// ...
app.MapBlazorRealtime();                       // the out-of-band and module endpoints
```

A component binds a tap on the circuit's outlet (step 1), and the page subscribes to it with `tapline.js` (step 2),
holding a replica kept current. Documentation: [usage (Blazor)](https://github.com/markhammond/tapline/blob/main/docs/client.md#usage-blazor). License: Elastic License 2.0 ([ELv2](https://www.elastic.co/licensing/elastic-license)), included as `LICENSE`. Its
source code is available under the same license at [github.com/markhammond/tapline](https://github.com/markhammond/tapline).
