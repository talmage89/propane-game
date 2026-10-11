#!/usr/bin/env bash
# Shared by CI and release. GITHUB_ENV makes the editor available to subsequent steps.
set -euo pipefail

version="$1"
cache="$2"
mkdir -p "$cache"
editor="Godot_v${version}-stable_mono_linux_x86_64"
binary="$cache/$editor/Godot_v${version}-stable_mono_linux.x86_64"
if [[ ! -x "$binary" ]]; then
  curl -fsSL --retry 3 -o "$cache/editor.zip" \
    "https://github.com/godotengine/godot/releases/download/${version}-stable/$editor.zip"
  unzip -q "$cache/editor.zip" -d "$cache"
  rm "$cache/editor.zip"
fi
echo "GODOT=$binary" >> "$GITHUB_ENV"
