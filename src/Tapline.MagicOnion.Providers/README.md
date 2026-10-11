# Tapline.MagicOnion.Providers

A [Tapline](https://github.com/markhammond/tapline/blob/main/README.md) data provider in a process of its own, reached by routers over MagicOnion: the provider's
topics on a Tapline stream, and step 1, notices, health, heartbeats, drains and mailboxes on a control stream.

```csharp
// the provider's process
builder.Services.AddMagicOnion();
builder.Services.AddTaplineProviderLink(new ProviderEndpoint(new Uri("https://practice-blue:7443")), sp => myProvider);
app.MapTaplineProviderLink();

// the router's
var provider = publisher.ProvideRemote("practice", new Uri("https://practice-blue:7443"));
```

Documentation: [in-process or out-of-process](https://github.com/markhammond/tapline/blob/main/docs/provider.md#in-process-or-out-of-process). License: Elastic License 2.0 ([ELv2](https://www.elastic.co/licensing/elastic-license)), included as `LICENSE`. Its
source code is available under the same license at [github.com/markhammond/tapline](https://github.com/markhammond/tapline).
