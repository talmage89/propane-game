#!/usr/bin/env bash
# Exports the Windows (x86_64), macOS (Apple Silicon) and Linux (x86_64) release builds and packages them in build/dist.
# Usage: tools/release/build.sh [path to the Godot .NET editor binary] [--unsigned-macos]
# It owns only build/package (exports, notices, logs and the folders that become the archives) and build/dist (the
# archives, which the release workflow uploads whole), and replaces both on every run. Other output under build/,
# such as screenshots, recordings and test logs, is never touched.
# Needs rcodesign (https://github.com/indygreg/apple-platform-rs) on PATH or in $RCODESIGN to sign the macOS app.
# Godot's exit code does not always reflect export errors, so the logs are checked as well.
set -euo pipefail

godot="${1:-${GODOT:-/Applications/Godot_mono.app/Contents/MacOS/Godot}}"
project="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$project"
version="$(sed -n 's/^config\/version="\(.*\)"$/\1/p' project.godot)"
godot_version="$("$godot" --version | cut -d. -f1-3)"
rcodesign="${RCODESIGN:-$(command -v rcodesign || true)}"
unsigned=0
if [[ "${2:-}" == --unsigned-macos ]]; then unsigned=1; fi
if (( $# > 2 )) || [[ -n "${2:-}" && "$2" != --unsigned-macos ]]; then
  echo "usage: $0 [godot] [--unsigned-macos]" >&2
  exit 2
fi
if [[ -z "$rcodesign" && "$unsigned" == 0 ]]; then
  echo "rcodesign not found; set RCODESIGN or put it on PATH" >&2
  exit 1
fi

stage=build/package
dist=build/dist
rm -rf "$stage" "$dist"
mkdir -p "$stage/macos" "$stage/linux" "$stage/windows" "$stage/notices" "$dist"

# The build id identifies the commit in the menu and multiplayer logs; it does not decide compatibility.
git rev-parse --short HEAD >build_id.txt 2>/dev/null || echo "unknown" >build_id.txt
trap 'rm -f "$project/build_id.txt"' EXIT

run() {
  local log="$stage/$1.log"
  shift
  if ! "$godot" --headless --path . "$@" >"$log" 2>&1 || grep -q '^ERROR' "$log"; then
    echo "Godot step failed ($log):" >&2
    grep -A3 '^ERROR' "$log" >&2 || tail -40 "$log" >&2
    exit 1
  fi
}

python3 -B tools/assets/check.py --report "$stage/asset-inventory.json"
run import --import
run macos --export-release macOS "$stage/macos/Propane.zip"
run linux --export-release Linux "$stage/linux/Propane.x86_64"
run windows --export-release Windows "$stage/windows/Propane.exe"

# Godot is MIT licensed and asks games to ship its notices.
cp CREDITS.md "$stage/notices/CREDITS.md"
# Preserve upstream notices beside the game; non-resource text is not implicitly exported in the PCK.
for kit in cars furniture nature suburban; do
  cp "assets/third_party/kenney/$kit/License.txt" "$stage/notices/KENNEY_${kit}_LICENSE.txt"
done
for notice in LICENSE COPYRIGHT; do
  python3 tools/downloads/verify.py "$godot_version" "$notice.txt" "$stage/notices/GODOT_$notice.txt" \
    "https://raw.githubusercontent.com/godotengine/godot/$godot_version-stable/$notice.txt"
done

# Godot's built-in ad-hoc signature carries entitlement data that current macOS rejects, killing the app at launch,
# so the preset exports unsigned and rcodesign applies a plain ad-hoc signature here. It runs on Linux too.
mac_dir="$stage/Propane-$version-macos-arm64"
mkdir -p "$mac_dir"
unzip -q "$stage/macos/Propane.zip" -d "$mac_dir"
if (( unsigned )); then
  echo "UNSIGNED macOS verification build; do not publish." | tee "$stage/sign.log"
  echo "UNSIGNED verification build; do not publish." >"$mac_dir/UNSIGNED.txt"
else
  "$rcodesign" sign "$mac_dir/Propane.app" >"$stage/sign.log" 2>&1 || { cat "$stage/sign.log" >&2; exit 1; }
fi
cp "$stage"/notices/* "$mac_dir/"
(cd "$mac_dir" && zip -qry "$project/$dist/Propane-$version-macos-arm64.zip" .)

linux_dir="$stage/Propane-$version-linux-x86_64"
mkdir -p "$linux_dir"
cp -R "$stage"/linux/. "$linux_dir/"
cp "$stage"/notices/* "$linux_dir/"
tar -C "$stage" -czf "$dist/Propane-$version-linux-x86_64.tar.gz" "$(basename "$linux_dir")"

windows_dir="$stage/Propane-$version-windows-x86_64"
mkdir -p "$windows_dir"
cp -R "$stage"/windows/. "$windows_dir/"
cp "$stage"/notices/* "$windows_dir/"
(cd "$windows_dir" && zip -qry "$project/$dist/Propane-$version-windows-x86_64.zip" .)

ls -lh "$dist"
