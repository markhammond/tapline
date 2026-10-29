# Tapline.Grid.ChalkQL

[Tapline](https://github.com/markhammond/tapline/blob/main/README.md)'s LiveGrid over a database and its change stream, through [ChalkQL](https://www.nuget.org/packages/ChalkQL):

- **The store's pages as SQL:** `ChalkStore` reads an append-only store's tables — each key's newest version, and every version — in any source ChalkQL has (PostgreSQL over ADO.NET, an in-process table). Each view's filter, order and limit go to the database in its own SQL, each statement prepared once a shape, its values bound, and stopped at a time limit.
- **The filter, stated once:** `ChalkFilter` is a view's filter as a SQL condition, its values bound as parameters. The store's pages take it pushed down; `ChalkScopes` keeps the same text as ChalkQL standing views over the rows the stream sent that the database hasn't reached (`ChalkTail`), so a row passes as the database would pass it.
- **One description of the columns:** `ChalkRows` takes a row struct's public fields as the store's columns, its key, version and stamp, and gives each view's order both as SQL and as a .NET comparison.

```csharp
var rows = new ChalkRows<long, Order, DateTime>(nameof(Order.Id), nameof(Order.Version), nameof(Order.Stamp));
var tail = new ChalkTail<long, Order>(rows);
var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions { Sources = [database, tail.Source], Planner = planner });
var source = new StoreAndStream<long, Order, DateTime>(
    new ChalkStore<long, Order, DateTime>(engine, rows, "db.orders", "db.order_versions"), stream, rows,
    new ChalkScopes<long, Order>(engine, tail, rows));
```

It takes ChalkQL 0.6.3. ChalkQL's planner runs in a Java 21 sidecar it starts itself.

## License

Elastic License 2.0 ([ELv2](https://www.elastic.co/licensing/elastic-license)), included as `LICENSE.txt`.

The source code is available at [github.com/markhammond/tapline](https://github.com/markhammond/tapline).
