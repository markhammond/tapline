// ActivityFeed.Dispatcher — the practice's notifications, handed to a last mile: a server-side Tapline client over
// MagicOnion (as the headless is) holding shards' dispatcher topics (notify:{shard}), bound through the app's
// IPracticeFeed. Each entry it's sent is logged once, with its shard's number; a send is handed to the last mile (a
// stand-in here: it refuses a token it no longer knows, and takes the rest), a no-op recorded. Each is reported through
// the app to the service with a correlation id. Once the rendezvous comes, the entry's removal is in front of the
// dispatcher, and it's done with that entry. A restart loses nothing: its topics' state is every pending entry.
//
//   dotnet run --project samples/ActivityFeed.Dispatcher -- --stream http://localhost:5952   both shards, as dispatcher-1
//   --name NAME          what it reports as (default dispatcher-1)
//   --shards 0,1         the shards it takes
//   --app URL            the app's port, for out-of-band fetches (default: two below the stream's)
//   --Spawned true       spawned: it stops once its standard input ends (the spawner gone), and says once it's bound
//                        (ACTIVITY-DISPATCHER …)

using System.Text;
using ActivityFeed.Contracts;
using ActivityFeed.Dispatcher;
using Grpc.Net.Client;

string stream = "http://127.0.0.1:5952", name = "dispatcher-1";
string? app = null;
int[] shards = [0, 1];
var spawned = false;
for (var i = 0; i < args.Length; i++)
    switch (args[i])
    {
        case "--stream" when i + 1 < args.Length:
            stream = args[++i];
            break;
        case "--app" when i + 1 < args.Length:
            app = args[++i];
            break;
        case "--name" when i + 1 < args.Length:
            name = args[++i];
            break;
        case "--shards" when i + 1 < args.Length:
            shards = [.. args[++i].Split(',').Select(int.Parse)];
            break;
        case "--Spawned" when i + 1 < args.Length && bool.TryParse(args[i + 1], out var told):
            spawned = told;
            i++;
            break;
        default:
            Console.Error.WriteLine($"unknown: {args[i]}\nusage: [--stream URL] [--app URL] [--name NAME] [--shards 0,1] [--Spawned true]");
            return 2;
    }

Console.OutputEncoding = Encoding.UTF8;
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};
if (spawned) Spawning.StopWhenParentGoes(stop.Cancel);
var streamUri = new Uri(stream);
var appUri = app is not null ? new Uri(app) : new UriBuilder(streamUri) { Port = streamUri.Port - 2, Path = "/" }.Uri;
using var channel = GrpcChannel.ForAddress(streamUri);
using var http = new HttpClient();
try
{
    await new Dispatcher(channel, appUri, http, name, shards, spawned).RunAsync(stop.Token);
    return 0;
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
    return 0;
}
