# Tapline.Wasm

A [Tapline](https://github.com/markhammond/tapline/blob/main/README.md) data provider's custom functions as WebAssembly modules, run by the router under Wasmtime:
folds (`WasmBatchFold`), finalizers (`WasmBatchFinalizer`) and stale-data functions (`WasmDegradeFunction`), each
sandboxed, fuel- and memory-bounded (`WasmLimits`), and allocation-free once warm.

Documentation: [tables, folds and ingest](https://github.com/markhammond/tapline/blob/main/docs/provider.md#reference-tables-folds-and-ingest),
[making custom functions safe](https://github.com/markhammond/tapline/blob/main/docs/provider.md#reference-making-custom-functions-safe). License: Elastic License 2.0 ([ELv2](https://www.elastic.co/licensing/elastic-license)), included as `LICENSE`. Its
source code is available under the same license at [github.com/markhammond/tapline](https://github.com/markhammond/tapline).
