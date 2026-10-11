#!/bin/sh
# Copyright 2026 Mark Hammond. Licensed under the Apache License, Version 2.0 (see ../LICENSE).
#
# Builds opaque.wasm as checked in (Tapline.Authentication.Opaque's: src/Tapline.Authentication.Opaque/wwwroot). Needs Rust 1.94.0 with its wasm32-unknown-unknown target:
#   rustup toolchain install 1.94.0 --profile minimal --target wasm32-unknown-unknown
# (or RUSTC naming an official 1.94.0 rustc whose sysroot has that target). The dependencies are locked (Cargo.lock)
# and local paths remapped, so the same toolchain makes the same bytes: compare the SHA-256 it prints with README.md's.
set -eu
cd "$(dirname "$0")"

if [ -z "${RUSTC:-}" ] && command -v rustup >/dev/null 2>&1; then plus="+1.94.0"; else plus=""; fi
version=$(${RUSTC:-rustc} $plus --version)
case "$version" in
  "rustc 1.94.0 (4a4ef493e 2026-03-02)") ;;
  *) echo "build.sh: needs the official rustc 1.94.0, found: $version" >&2; exit 1 ;;
esac

CARGO_HOME="${CARGO_HOME:-$HOME/.cargo}"
export RUSTFLAGS="--remap-path-prefix=$CARGO_HOME/registry/src/=/crates/ --remap-path-prefix=$PWD=/tapline-opaque"
cargo $plus build --locked --release --target wasm32-unknown-unknown
out=../wwwroot/opaque.wasm
cp target/wasm32-unknown-unknown/release/tapline_opaque.wasm "$out"
shasum -a 256 "$out"
