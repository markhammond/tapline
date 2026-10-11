using Grpc.Core;
using MagicOnion.Client;
using PivotTable.Contracts;

namespace PivotTable.Headless;

/// <summary>
///     The statement — the one the address asks for, as the page makes it (the app's), or a file of your own, its lists
///     bound — and its shape, read from the app before anything streams: the cells' labels and figures, field by field.
/// </summary>
internal sealed record Described(PivotStatement Statement, PivotShape Shape)
{
    /// <summary>
    ///     The statement and its shape — or null, having said why it can't be a pivot. Asked again while the app isn't
    ///     up.
    /// </summary>
    public static async Task<Described?> AskAsync(Options options, ChannelBase channel, CancellationToken token)
    {
        var pivot = MagicOnionClient.Create<IPivotService>(channel);
        // --show: the address's values asked as Show as too (the statement made for them: running totals, the market's);
        // a statement of your own carries them as asked (the provider computes what its totals allow).
        var statement = options.Statement is { } file
            ? new PivotStatement
            {
                Sql = await File.ReadAllTextAsync(file, token), Bindings = options.Bindings,
                Extras = [.. options.Shown.Select(s => s.ToString())]
            }
            : await RetryAsync(() => pivot.StatementAsync(options.AddressShown).ResponseAsync, token);
        var described = await RetryAsync(
            () => pivot.DescribeAsync(statement.Sql, statement.Bindings, statement.Extras).ResponseAsync, token);
        if (described.Shape is not { } json)
        {
            await Console.Error.WriteLineAsync($"not a pivot: {described.Refused}");
            return null;
        }

        return new Described(statement, PivotShape.Parse(json));
    }

    /// <summary>Printed: the statement and its bindings, if asked; the shape — whole, if asked, else a line.</summary>
    public void Print(Options options)
    {
        if (options.Sql)
            Console.Out.WriteLine(Statement.Sql + (Statement.Bindings.Count == 0
                ? ""
                : "\n  bound: " + string.Join("; ",
                    Statement.Bindings.Select(b => $"@{b.Key} = {string.Join(", ", b.Value)}"))));
        Console.Out.WriteLine(options.Describe
            ? Shape.ToJson()
            : $"shape: a {Shape.Key.Bytes}-byte key ({string.Join(", ", Shape.Key.Fields.Select(f => f.Name))}), a {Shape.Row.Bytes}-byte row " +
              $"({string.Join(", ", Shape.Row.Fields.Select(f => f.From is null ? $"{f.Name} (unanswered)" : f.Name))})");
        Console.Out.Flush();
    }

    /// <summary>
    ///     A call to the app, asked again while it can't be reached (it's starting, say): its answer, or cancelled. Reached,
    ///     and
    ///     failing — an app without the call, say (an older build): thrown.
    /// </summary>
    private static async Task<T> RetryAsync<T>(Func<Task<T>> call, CancellationToken token)
    {
        for (var delay = TimeSpan.FromMilliseconds(250);;
             delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(2).Ticks)))
            try
            {
                return await call();
            }
            catch (RpcException e) when (e.StatusCode == StatusCode.Unavailable && !token.IsCancellationRequested)
            {
                await Task.Delay(delay, token);
            }
    }
}