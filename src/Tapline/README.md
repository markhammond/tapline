# Tapline

The core of [Tapline](https://github.com/markhammond/tapline/blob/main/README.md), a real-time, last-mile data distribution engine for .NET: shared state fanned
out to many clients, prioritised, conflated and ack-clocked, with ordered messaging beside it. Transport-agnostic and
allocation-free in steady state.

- **Sources:** last values, and transactional replicated tables of fixed-size rows, with custom folds and finalizers.
- **Messages:** topics every subscriber reads in order, and each subscription's own mailbox.
- **Outlets and the router** (`Publisher`): one outlet a client, flow-controlled from its acknowledgements, and data
  providers deciding who may subscribe to what.

A transport carries its frames: [Tapline.BlazorRealtime](https://github.com/markhammond/tapline/blob/main/src/Tapline.BlazorRealtime) (a Blazor Server circuit) or
[Tapline.MagicOnion](https://github.com/markhammond/tapline/blob/main/src/Tapline.MagicOnion) (gRPC), hosted by [Tapline.AspNetCore](https://github.com/markhammond/tapline/blob/main/src/Tapline.AspNetCore).

```csharp
var book = publisher.Table<Level, long>("book:BTC-PERP");
using (var u = book.BeginUpdate())
{
    u.Set(new Level(0, 6_499_950), 120);
    u.Delete(new Level(1, 6_500_050));
    u.Complete();
}
```

Documentation: [architecture](https://github.com/markhammond/tapline/blob/main/docs/architecture.md), [data providers](https://github.com/markhammond/tapline/blob/main/docs/provider.md),
[clients](https://github.com/markhammond/tapline/blob/main/docs/client.md). License: Elastic License 2.0 ([ELv2](https://www.elastic.co/licensing/elastic-license)), included as `LICENSE`. Its
source code is available under the same license at [github.com/markhammond/tapline](https://github.com/markhammond/tapline).
