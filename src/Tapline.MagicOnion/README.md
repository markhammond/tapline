# Tapline.MagicOnion

[Tapline](https://github.com/markhammond/tapline/blob/main/README.md) over MagicOnion (gRPC, HTTP/2), the client's side: the stream's contract, one duplex call per
connection, carrying [Tapline.Client](https://github.com/markhammond/tapline/blob/main/src/Tapline.Client). Moves between routers, make before break, when one
drains (`TaplineRouters`).

```csharp
var connection = new TaplineStreamConnection(GrpcChannel.ForAddress("http://localhost:5902"));
connection.Connected += (outletId, nonce) => { /* step 1 on your server, then connection.Client.Subscribe(binding, handler) */ };
await connection.RunAsync(stopping);
```

The router's side is [Tapline.MagicOnion.Server](https://github.com/markhammond/tapline/blob/main/src/Tapline.MagicOnion.Server). Documentation:
[usage (MagicOnion)](https://github.com/markhammond/tapline/blob/main/docs/transports.md#usage-magiconion). License: Eclipse Public License 2.0 ([EPL-2.0](https://www.eclipse.org/legal/epl-2.0/)), included as `LICENSE`. Its
source code is available under the same license at [github.com/markhammond/tapline](https://github.com/markhammond/tapline).
