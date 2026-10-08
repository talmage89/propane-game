#!/usr/bin/env bash
# Exports the macOS (Apple Silicon) and Linux (x86_64) release builds and packages them in build/dist.
# Usage: tools/release/build.sh [path to the Godot .NET editor binary]
# Needs rcodesign (https://github.com/indygreg/apple-platform-rs) on PATH or in $RCODESIGN to sign the macOS app.
# Godot's exit code does not always reflect export errors, so the logs are checked as well.
set -euo pipefail

godot="${1:-${GODOT:-/Applications/Godot_mono.app/Contents/MacOS/Godot}}"
project="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$project"
version="$(sed -n 's/^config\/version="\(.*\)"$/\1/p' project.godot)"
godot_version="$("$godot" --version | cut -d. -f1-3)"
rcodesign="${RCODESIGN:-$(command -v rcodesign || true)}"
if [[ -z "$rcodesign" ]]; then
  echo "rcodesign not found; set RCODESIGN or put it on PATH" >&2
  exit 1
fi

rm -rf build
mkdir -p build/macos build/linux build/dist build/notices

run() {
  local log="build/$1.log"
  shift
  if ! "$godot" --headless --path . "$@" >"$log" 2>&1 || grep -q '^ERROR' "$log"; then
    echo "Godot step failed ($log):" >&2
    grep -A3 '^ERROR' "$log" >&2 || tail -40 "$log" >&2
    exit 1
  fi
}

run import --import
run macos --export-release macOS build/macos/Propane.zip
run linux --export-release Linux build/linux/Propane.x86_64

# Godot is MIT licensed and asks games to ship its notices.
cp CREDITS.md build/notices/CREDITS.md
curl -fsSL -o build/notices/GODOT_LICENSE.txt "https://raw.githubusercontent.com/godotengine/godot/$godot_version-stable/LICENSE.txt"
curl -fsSL -o build/notices/GODOT_COPYRIGHT.txt "https://raw.githubusercontent.com/godotengine/godot/$godot_version-stable/COPYRIGHT.txt"

# Godot's built-in ad-hoc signature carries entitlement data that current macOS rejects, killing the app at launch,
# so the preset exports unsigned and rcodesign applies a plain ad-hoc signature here. It runs on Linux too.
mac_dir="build/Propane-$version-macos-arm64"
mkdir -p "$mac_dir"
unzip -q build/macos/Propane.zip -d "$mac_dir"
"$rcodesign" sign "$mac_dir/Propane.app" >build/sign.log 2>&1 || { cat build/sign.log >&2; exit 1; }
cp build/notices/* "$mac_dir/"
(cd "$mac_dir" && zip -qry "$project/build/dist/Propane-$version-macos-arm64.zip" .)

linux_dir="build/Propane-$version-linux-x86_64"
mkdir -p "$linux_dir"
cp -R build/linux/. "$linux_dir/"
cp build/notices/* "$linux_dir/"
tar -C build -czf "build/dist/Propane-$version-linux-x86_64.tar.gz" "$(basename "$linux_dir")"

ls -lh build/dist
