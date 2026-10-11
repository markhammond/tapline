# Tapline.Authentication

A realm's authentication service for [Tapline](https://github.com/markhammond/tapline/blob/main/README.md) routers, composed of modules (passkeys by WebAuthn,
passthrough, tickets, your own), each handing a proven user to your backend, which says who they are. The router
relays each sign-in's messages without reading them.

For passwords, add [Tapline.Authentication.Opaque](https://github.com/markhammond/tapline/blob/main/src/Tapline.Authentication.Opaque). Documentation:
[signing users in](https://github.com/markhammond/tapline/blob/main/docs/provider.md#signing-users-in), [the reference](https://github.com/markhammond/tapline/blob/main/docs/provider.md#reference-signing-in).
License: Elastic License 2.0 ([ELv2](https://www.elastic.co/licensing/elastic-license)), included as `LICENSE`. Its
source code is available under the same license at [github.com/markhammond/tapline](https://github.com/markhammond/tapline).
