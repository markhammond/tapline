# Tapline.Client

The core of [Tapline](https://github.com/markhammond/tapline/blob/main/README.md), a real-time, last-mile data distribution engine for .NET, and its .NET client: shared state fanned out to many clients, prioritised, conflated and ack-clocked, with ordered messaging beside it. Transport-agnostic and allocation-free in steady state.

- **Sources:** last values, and transactional replicated tables of fixed-size rows, with custom folds and finalizers.
- **Messages:** topics every subscriber reads in order, and each subscription's own mailbox.
- **Outlets and the router** (`Publisher`): one outlet a client, flow-controlled from its acknowledgements, and data providers deciding who may subscribe to what.
- **The client** (`TaplineClient`), whatever carries its frames: replicas of what it subscribed to (tables, sequence and checksum verified; values; watches; message streams), acknowledged frame by frame, resynced, resumed after a reconnect, out-of-band records fetched, and the router's notices.
- **MagicOnion** (gRPC, HTTP/2), the client's side: one duplex call per connection, moved between routers, make before break, when one drains (`TaplineRouters`).

```csharp
var connection = new TaplineStreamConnection(GrpcChannel.ForAddress("http://localhost:5902"));
connection.Connected += (outletId, nonce) => { /* step 1 on your server, then connection.Client.Subscribe(binding, handler) */ };
await connection.RunAsync(stopping);
```

A server takes [Tapline](https://www.nuget.org/packages/Tapline), which brings this in.
A client signing in by password adds [Tapline.Client.Opaque](https://www.nuget.org/packages/Tapline.Client.Opaque).

## Documentation

- [Architecture](https://github.com/markhammond/tapline/blob/main/docs/architecture.md)
- [Usage (.NET client)](https://github.com/markhammond/tapline/blob/main/docs/client.md#usage-net-client)
- [Usage (MagicOnion)](https://github.com/markhammond/tapline/blob/main/docs/transports.md#usage-magiconion)

## License

Elastic License 2.0 ([ELv2](https://www.elastic.co/licensing/elastic-license)), included as `LICENSE`.
The source code is available under the same license at [github.com/markhammond/tapline](https://github.com/markhammond/tapline).
