#!/usr/bin/env bash
# Release-only installer. Recreate the signer from a verified archive, including on cache hits.
set -euo pipefail
version="$1"
cache="$2"
project="$(cd "$(dirname "$0")/../.." && pwd)"
signer="apple-codesign-${version}-x86_64-unknown-linux-musl"
archive="$cache/$signer.tar.gz"
base="https://github.com/indygreg/apple-platform-rs/releases/download/apple-codesign%2F${version}"
python3 "$project/tools/downloads/verify.py" "$version" "$signer.tar.gz" "$archive" "$base/$signer.tar.gz"
rm -rf "$cache/$signer"
tar -xzf "$archive" -C "$cache"
echo "RCODESIGN=$cache/$signer/rcodesign" >> "$GITHUB_ENV"
