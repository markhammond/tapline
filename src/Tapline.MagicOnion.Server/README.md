# Tapline.MagicOnion.Server

[Tapline](https://github.com/markhammond/tapline/blob/main/README.md) over MagicOnion, the router's side: the stream service, a session's outlet held across its
calls, frames written one at a time as the call takes them.

```csharp
builder.Services.AddMagicOnion();
builder.Services.AddTaplineMagicOnion();        // registers the router too
app.MapTaplineMagicOnion();
app.MapMagicOnionService(typeof(MyStepOne));    // your step 1: sessions.Find(outletId, nonce), then publisher.SubscribeAsync(…)
```

Clients use [Tapline.MagicOnion](https://github.com/markhammond/tapline/blob/main/src/Tapline.MagicOnion). Documentation:
[usage (MagicOnion)](https://github.com/markhammond/tapline/blob/main/docs/transports.md#usage-magiconion). License: Eclipse Public License 2.0 ([EPL-2.0](https://www.eclipse.org/legal/epl-2.0/)), included as `LICENSE`. Its
source code is available under the same license at [github.com/markhammond/tapline](https://github.com/markhammond/tapline).
