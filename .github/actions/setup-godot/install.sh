#!/usr/bin/env bash
# Shared by CI and release. GITHUB_ENV makes the editor available to subsequent steps.
set -euo pipefail

version="$1"
cache="$2"
mkdir -p "$cache"
editor="Godot_v${version}-stable_mono_linux_x86_64"
binary="$cache/$editor/Godot_v${version}-stable_mono_linux.x86_64"
project="$(cd "$(dirname "$0")/../../.." && pwd)"
archive="$cache/$editor.zip"
python3 "$project/tools/downloads/verify.py" "$version" "$editor.zip" "$archive" \
  "https://github.com/godotengine/godot/releases/download/${version}-stable/$editor.zip"
# Never execute an extracted cache entry: recreate it from the verified archive each time.
rm -rf "$cache/$editor"
unzip -q "$archive" -d "$cache"
echo "GODOT=$binary" >> "$GITHUB_ENV"
