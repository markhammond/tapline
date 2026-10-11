# Tapline.AspNetCore

Hosts a [Tapline](https://github.com/markhammond/tapline/blob/main/README.md) router in ASP.NET Core, whatever the transport: its services, identity, heartbeats
and health; the out-of-band fetch and module endpoints; the ops port and the router's viewer; and the browser client,
`tapline.js`, served as a static web asset.

Use it with a transport: [Tapline.BlazorRealtime](https://github.com/markhammond/tapline/blob/main/src/Tapline.BlazorRealtime) or
[Tapline.MagicOnion.Server](https://github.com/markhammond/tapline/blob/main/src/Tapline.MagicOnion.Server), each of which brings it in.

Documentation: [clients](https://github.com/markhammond/tapline/blob/main/docs/client.md), [observability](https://github.com/markhammond/tapline/blob/main/docs/observability.md). License: Elastic License 2.0 ([ELv2](https://www.elastic.co/licensing/elastic-license)), included as `LICENSE`. Its
source code is available under the same license at [github.com/markhammond/tapline](https://github.com/markhammond/tapline).
