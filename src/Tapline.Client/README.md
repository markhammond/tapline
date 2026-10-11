# Tapline.Client

[Tapline](https://github.com/markhammond/tapline/blob/main/README.md)'s .NET client, whatever carries its frames: replicas of what it subscribed to (tables, sequence
and checksum verified; values; watches; message streams), acknowledged frame by frame, resynced, resumed after a
reconnect, out-of-band records fetched, and the router's notices.

For MagicOnion (gRPC), take [Tapline.MagicOnion](https://github.com/markhammond/tapline/blob/main/src/Tapline.MagicOnion), which carries it.

Documentation: [usage (.NET client)](https://github.com/markhammond/tapline/blob/main/docs/client.md#usage-net-client). License: Elastic License 2.0 ([ELv2](https://www.elastic.co/licensing/elastic-license)), included as `LICENSE`. Its
source code is available under the same license at [github.com/markhammond/tapline](https://github.com/markhammond/tapline).
