using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chalk.Catalog;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.Sources.Ado;
using Npgsql;
using NpgsqlTypes;
using Tapline.Grid;
using Tapline.Grid.Provider;
using Tapline.Grid.ChalkQL;

namespace LiveGrid.Sample.Domain;

/// <summary>
///     The orders' database in PostgreSQL, kept as the ChalkQL blotter keeps its own — its tables, loading, landing and
///     estimates lifted from its <c>PostgresTradeRepository</c>, <c>PostgresBlotterProvider</c> and <c>PostgresExplain</c>
///     (<c>samples/Chalk.Sample.Blotter/Postgres</c>, ChalkQL's repository): <c>UNLOGGED</c> tables, text collated
///     <c>"C"</c>, written with binary <c>COPY</c>; <c>orders_current</c> kept by upserts, a late older version changing
///     nothing. Rows are written from the house's own (<see cref="OrderRow" />, <see cref="Fill" />), their text from its
///     bytes: no records made, no strings. Read through ChalkQL's ADO.NET source, each statement stopped at the time limit
///     by PostgreSQL's own <c>statement_timeout</c>; a view's size estimated from PostgreSQL's plan, the filter's values
///     written in (its statistics see them, as ChalkQL's planner can't). Every session in UTC, as the sample's times are.
/// </summary>
internal sealed class PgOrders(string connectionString, TimeSpan timeLimit) : IAsyncDisposable
{
    /// <summary>The schema, as ChalkQL names its tables too (<c>livegrid.orders_current</c>).</summary>
    public const string Schema = "livegrid";

    private const int Scratch = 32; // (a text field's characters: at most 23)

    private const string OrderColumns = """
                                        order_id    uuid             NOT NULL,
                                        version     integer          NOT NULL,
                                        event_time  timestamp(6)     NOT NULL,
                                        order_time  timestamp(6)     NOT NULL,
                                        cl_ord_id   text COLLATE "C" NOT NULL,
                                        client      text COLLATE "C" NOT NULL,
                                        region      text COLLATE "C" NOT NULL,
                                        channel     text COLLATE "C" NOT NULL,
                                        instrument  text COLLATE "C" NOT NULL,
                                        side        text COLLATE "C" NOT NULL,
                                        qty         double precision NOT NULL,
                                        filled_qty  double precision NOT NULL,
                                        avg_price   double precision NOT NULL,
                                        filled_amt  double precision NOT NULL,
                                        markup_bps  double precision NOT NULL,
                                        status      text COLLATE "C" NOT NULL
                                        """;

    // A newer version replaces the row; an older one, landing late, changes nothing.
    private const string Upsert = """
                                  ON CONFLICT (order_id) DO UPDATE SET
                                      version = EXCLUDED.version, event_time = EXCLUDED.event_time, filled_qty = EXCLUDED.filled_qty,
                                      avg_price = EXCLUDED.avg_price, filled_amt = EXCLUDED.filled_amt, status = EXCLUDED.status
                                  WHERE orders_current.version < EXCLUDED.version
                                  """;

    private static readonly ColumnDescriptor[] OrderDescriptors =
    [
        Column("order_id", ChalkType.Uuid()), Column("version", ChalkType.Int32()), Column("event_time", ChalkType.Timestamp(6)),
        Column("order_time", ChalkType.Timestamp(6)), Column("cl_ord_id", ChalkType.String()), Column("client", ChalkType.String()),
        Column("region", ChalkType.String()), Column("channel", ChalkType.String()), Column("instrument", ChalkType.String()),
        Column("side", ChalkType.String()), Column("qty", ChalkType.Float64()), Column("filled_qty", ChalkType.Float64()),
        Column("avg_price", ChalkType.Float64()), Column("filled_amt", ChalkType.Float64()), Column("markup_bps", ChalkType.Float64()),
        Column("status", ChalkType.String())
    ];

    private static readonly ColumnDescriptor[] FillDescriptors =
    [
        Column("fill_id", ChalkType.Uuid()), Column("order_id", ChalkType.Uuid()), Column("event_time", ChalkType.Timestamp(6)),
        Column("cl_ord_id", ChalkType.String()), Column("client", ChalkType.String()), Column("region", ChalkType.String()),
        Column("channel", ChalkType.String()), Column("instrument", ChalkType.String()), Column("side", ChalkType.String()),
        Column("qty", ChalkType.Float64()), Column("price", ChalkType.Float64()), Column("amt", ChalkType.Float64()),
        Column("mid", ChalkType.Float64()), Column("markup_bps", ChalkType.Float64())
    ];

    private static readonly Regex Parameter = new(@"@(\w+)", RegexOptions.Compiled);

    private readonly char[] _landing = new char[Scratch]; // (the ledger lands one batch at a time)

    private readonly string _writing = new NpgsqlConnectionStringBuilder(connectionString) { Timezone = "UTC" }.ConnectionString;

    // Reads, ChalkQL's and the estimates: the server stops each statement at the time limit.
    private readonly NpgsqlDataSource _reading = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connectionString)
    {
        Timezone = "UTC", Timeout = (int)Math.Ceiling(timeLimit.TotalSeconds), CommandTimeout = (int)Math.Ceiling(timeLimit.TotalSeconds) + 5,
        Options = string.Create(CultureInfo.InvariantCulture, $"-c statement_timeout={(long)timeLimit.TotalMilliseconds}")
    }.ConnectionString);

    public async ValueTask DisposeAsync()
    {
        await _reading.DisposeAsync();
    }

    /// <summary>
    ///     The tables, seeded with the history — each order's newest version (its versions so far that one), and its fills —
    ///     by binary <c>COPY</c>, a table to a connection side by side, each order written as it's made from its number (no
    ///     records held, next to nothing allocated); then the keys and indexes the grid reads by, and the tables analysed
    ///     for the planner. The fills written.
    /// </summary>
    public async Task<long> CreateAsync(History history, CancellationToken ct)
    {
        await ExecuteAsync($"""
                            CREATE UNLOGGED TABLE order_versions ({OrderColumns});
                            CREATE UNLOGGED TABLE orders_current ({OrderColumns});
                            CREATE UNLOGGED TABLE fills (
                                fill_id     uuid             NOT NULL,
                                order_id    uuid             NOT NULL,
                                event_time  timestamp(6)     NOT NULL,
                                cl_ord_id   text COLLATE "C" NOT NULL,
                                client      text COLLATE "C" NOT NULL,
                                region      text COLLATE "C" NOT NULL,
                                channel     text COLLATE "C" NOT NULL,
                                instrument  text COLLATE "C" NOT NULL,
                                side        text COLLATE "C" NOT NULL,
                                qty         double precision NOT NULL,
                                price       double precision NOT NULL,
                                amt         double precision NOT NULL,
                                mid         double precision NOT NULL,
                                markup_bps  double precision NOT NULL
                            );
                            """, ct);
        long fills = 0;
        await Task.WhenAll(Task.Run(() => Seed("order_versions", history, ct), ct), Task.Run(() => Seed("orders_current", history, ct), ct),
            Task.Run(() => fills = SeedFills(history, ct), ct));
        string[] indexes =
        [
            "ALTER TABLE orders_current ADD PRIMARY KEY (order_id)",
            "CREATE INDEX orders_current_newest ON orders_current (order_time DESC, order_id DESC)",
            "ALTER TABLE order_versions ADD PRIMARY KEY (order_id, version)",
            "CREATE INDEX order_versions_recent ON order_versions (event_time)",
            "ALTER TABLE fills ADD PRIMARY KEY (fill_id)",
            "CREATE INDEX fills_by_order ON fills (order_id)",
            "CREATE INDEX fills_newest ON fills (event_time DESC, fill_id DESC)"
        ];
        await Task.WhenAll(indexes.Select(sql => ExecuteAsync(sql, ct)));
        await ExecuteAsync("ANALYZE order_versions; ANALYZE orders_current; ANALYZE fills", ct);
        return fills;
    }

    /// <summary>
    ///     What the ledger lands (<see cref="Ledger.Landing" />), on one connection: every version and fill copied in, each
    ///     order's newest upserted into <c>orders_current</c> from a staged copy. (Copied as the blotter copies, synchronously:
    ///     a few dozen rows, on the ledger's own loop.)
    /// </summary>
    public async Task LandAsync(OrderRow[] versions, OrderRow[] current, (OrderRow Order, Fill Fill)[] fills, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_writing);
        await connection.OpenAsync(ct);
        if (versions.Length > 0) Copy(connection, "order_versions", versions, _landing);
        if (fills.Length > 0)
        {
            using var import = connection.BeginBinaryImport("COPY fills FROM STDIN (FORMAT BINARY)");
            foreach (ref readonly var made in fills.AsSpan()) Write(import, made.Order, made.Fill, _landing);
            import.Complete();
        }

        if (current.Length == 0) return;
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var stage = new NpgsqlCommand("CREATE TEMP TABLE staged (LIKE orders_current INCLUDING ALL) ON COMMIT DROP", connection,
                         transaction))
            await stage.ExecuteNonQueryAsync(ct);
        Copy(connection, "staged", current, _landing);
        await using (var upsert = new NpgsqlCommand($"INSERT INTO orders_current SELECT * FROM staged {Upsert}", connection, transaction))
            await upsert.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    ///     The tables as ChalkQL's ADO.NET source, declared with the keys, indexes and sizes its planner prices by; text
    ///     collated "C", told so, so sorts and ranges on text are pushed down; no statement longer than the time limit.
    /// </summary>
    public ISourceRuntime Source(long orders, long versions, long fills)
    {
        var profile = DialectProfiles.PostgreSql.With(StringCollation.Binary);
        return new AdoSourceBuilder("db", _reading.CreateConnection, Schema)
            .Dialect(profile)
            .Capabilities(AdoCapabilities.For(profile))
            .Options(new SourceOptions { QueryTimeout = timeLimit })
            .AddTable("orders_current", OrderDescriptors, configure: t => t
                .UniqueKey("order_id")
                .Index("orders_current_newest", false, "order_time", "order_id")
                .RowCount(orders))
            .AddTable("order_versions", OrderDescriptors, configure: t => t
                .UniqueKey("order_id", "version")
                .Index("order_versions_recent", false, "event_time")
                .RowCount(versions))
            .AddTable("fills", FillDescriptors, configure: t => t
                .UniqueKey("fill_id")
                .Index("fills_by_order", false, "order_id")
                .Index("fills_newest", false, "event_time", "fill_id")
                .RowCount(fills))
            .Build();
    }

    /// <summary>
    ///     A view's size as PostgreSQL's plan estimates it (the blotter's <c>PostgresExplain</c>): <c>EXPLAIN</c> of the
    ///     view's rows, the filter's values written in so its statistics see them; the root's "Plan Rows". Never run.
    /// </summary>
    public ValueTask<(long Rows, TotalKind Kind)?> EstimateAsync(GridView view, CancellationToken ct)
    {
        return EstimateAsync("orders_current", view, ct);
    }

    /// <summary>A view of the fills' size, as PostgreSQL's plan estimates it (<see cref="EstimateAsync(GridView, CancellationToken)" />).</summary>
    public ValueTask<(long Rows, TotalKind Kind)?> EstimateFillsAsync(GridView view, CancellationToken ct)
    {
        return EstimateAsync("fills", view, ct);
    }

    private async ValueTask<(long Rows, TotalKind Kind)?> EstimateAsync(string table, GridView view, CancellationToken ct)
    {
        var where = view.Filter is ChalkFilter filter ? Literal(filter) : "TRUE";
        await using var connection = await _reading.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand($"EXPLAIN (FORMAT JSON) SELECT 1 FROM {Schema}.{table} WHERE {where}", connection);
        var json = (string)(await command.ExecuteScalarAsync(ct))!;
        using var plan = JsonDocument.Parse(json);
        return ((long)plan.RootElement[0].GetProperty("Plan").GetProperty("Plan Rows").GetDouble(), TotalKind.Estimated);
    }

    /// <summary>A filter's condition, its values written in as PostgreSQL literals: a list as one, for <c>IN</c>.</summary>
    private static string Literal(ChalkFilter filter)
    {
        var values = filter.Parameters ?? new Dictionary<string, object?>();
        return Parameter.Replace(filter.Where, m => values.TryGetValue(m.Groups[1].Value, out var value) ? Write(value) : m.Value);

        static string Write(object? value)
        {
            return value switch
            {
                null => "NULL",
                string text => "'" + text.Replace("'", "''") + "'",
                Guid id => $"'{id:D}'::uuid",
                DateTime time => string.Create(CultureInfo.InvariantCulture, $"TIMESTAMP '{time:yyyy-MM-dd HH:mm:ss.ffffff}'"),
                bool flag => flag ? "TRUE" : "FALSE",
                IEnumerable list => "(" + string.Join(", ", list.Cast<object?>().Select(Write).DefaultIfEmpty("NULL")) + ")",
                IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
                var other => "'" + other.ToString()!.Replace("'", "''") + "'"
            };
        }
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_writing);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>A table of every order's newest version, as the start finds it: each written as it's made.</summary>
    private void Seed(string table, History history, CancellationToken ct)
    {
        using var connection = new NpgsqlConnection(_writing);
        connection.Open();
        using var import = connection.BeginBinaryImport($"COPY {table} FROM STDIN (FORMAT BINARY)");
        var text = new char[Scratch];
        for (var n = 0L; n < history.Orders; n++)
        {
            if ((n & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            Write(import, history.Make(n), text);
        }

        import.Complete();
    }

    /// <summary>Every order's fills as the start finds them, each written as its order is made: how many.</summary>
    private long SeedFills(History history, CancellationToken ct)
    {
        using var connection = new NpgsqlConnection(_writing);
        connection.Open();
        using var import = connection.BeginBinaryImport("COPY fills FROM STDIN (FORMAT BINARY)");
        var (text, made, count) = (new char[Scratch], new List<Fill>(4), 0L);
        for (var n = 0L; n < history.Orders; n++)
        {
            if ((n & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            made.Clear();
            var order = history.Make(n, made);
            foreach (var fill in made) Write(import, order, fill, text);
            count += made.Count;
        }

        import.Complete();
        return count;
    }

    private static void Copy(NpgsqlConnection connection, string table, OrderRow[] rows, char[] text)
    {
        using var import = connection.BeginBinaryImport($"COPY {table} FROM STDIN (FORMAT BINARY)");
        foreach (ref readonly var row in rows.AsSpan()) Write(import, row, text);
        import.Complete();
    }

    /// <summary>An order's version as its tables' row.</summary>
    private static void Write(NpgsqlBinaryImporter import, in OrderRow o, char[] text)
    {
        import.StartRow();
        import.Write(o.OrderId, NpgsqlDbType.Uuid);
        import.Write(o.Version, NpgsqlDbType.Integer);
        import.Write(Plain(o.EventTime), NpgsqlDbType.Timestamp);
        import.Write(Plain(o.OrderTime), NpgsqlDbType.Timestamp);
        Text(import, o.ClOrdId, text);
        Text(import, o.Client, text);
        Text(import, o.Region, text);
        Text(import, o.Channel, text);
        Text(import, o.Instrument, text);
        Text(import, o.Side, text);
        import.Write(o.Qty, NpgsqlDbType.Double);
        import.Write(o.FilledQty, NpgsqlDbType.Double);
        import.Write(o.AvgPrice, NpgsqlDbType.Double);
        import.Write(o.FilledAmt, NpgsqlDbType.Double);
        import.Write(o.MarkupBps, NpgsqlDbType.Double);
        Text(import, o.Status, text);
    }

    /// <summary>A fill as the <c>fills</c> row: its order's names beside it, and the mid it was priced from.</summary>
    private static void Write(NpgsqlBinaryImporter import, in OrderRow order, in Fill fill, char[] text)
    {
        import.StartRow();
        import.Write(fill.Id, NpgsqlDbType.Uuid);
        import.Write(order.OrderId, NpgsqlDbType.Uuid);
        import.Write(Plain(Times.Of(fill.Time)), NpgsqlDbType.Timestamp);
        Text(import, order.ClOrdId, text);
        Text(import, order.Client, text);
        Text(import, order.Region, text);
        Text(import, order.Channel, text);
        Text(import, order.Instrument, text);
        Text(import, order.Side, text);
        import.Write(fill.Qty, NpgsqlDbType.Double);
        import.Write(fill.Price, NpgsqlDbType.Double);
        import.Write(fill.Amount, NpgsqlDbType.Double);
        import.Write(FillRecord.MidOf(order, fill), NpgsqlDbType.Double);
        import.Write(order.MarkupBps, NpgsqlDbType.Double);
    }

    /// <summary>A text field, from the row's own UTF-8: decoded into <paramref name="text" />, never made a string.</summary>
    private static void Text(NpgsqlBinaryImporter import, ReadOnlySpan<byte> field, char[] text)
    {
        var length = Encoding.UTF8.GetChars(InlineText.Bytes(field), text);
        import.Write(new ReadOnlyMemory<char>(text, 0, length), NpgsqlDbType.Text);
    }

    /// <summary>A UTC time as a <c>timestamp</c> holds it: no zone (Npgsql won't write a UTC one to a column without).</summary>
    private static DateTime Plain(DateTime time)
    {
        return DateTime.SpecifyKind(time, DateTimeKind.Unspecified);
    }

    private static ColumnDescriptor Column(string name, ChalkType type)
    {
        return new ColumnDescriptor { Name = name, Type = type };
    }
}
