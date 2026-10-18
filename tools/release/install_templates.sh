#!/usr/bin/env bash
# Installs the Godot .NET export templates this project ships with: macOS (with an Apple Silicon only binary carved
# out of the universal one), Linux x86_64 and Windows x86_64. Usage: tools/release/install_templates.sh <godot version, e.g. 4.7.2>
# Set TEMPLATE_CACHE to a directory to keep the downloaded archive between runs.
set -euo pipefail

version="$1"
cache="${TEMPLATE_CACHE:-${TMPDIR:-/tmp}/godot-templates}"
case "$(uname -s)" in
  Darwin) root="$HOME/Library/Application Support/Godot/export_templates" ;;
  *) root="${XDG_DATA_HOME:-$HOME/.local/share}/godot/export_templates" ;;
esac
target="$root/$version.stable.mono"

project="$(cd "$(dirname "$0")/../.." && pwd)"
filename="Godot_v$version-stable_mono_export_templates.tpz"
archive="$cache/$filename"
python3 "$project/tools/downloads/verify.py" "$version" "$filename" "$archive" \
  "https://github.com/godotengine/godot/releases/download/$version-stable/$filename"
# Installed files (including thinned macOS binaries) are derived, never trusted as a cache.
rm -rf "$target"
mkdir -p "$target"

work="$(mktemp -d "${TMPDIR:-/tmp}/propane-templates.XXXXXX")"
trap 'rm -rf "$work"' EXIT
unzip -q -o "$archive" templates/version.txt templates/icudt_godot.dat templates/macos.zip \
  templates/linux_release.x86_64 templates/linux_debug.x86_64 \
  'templates/windows_*_x86_64*.exe' -d "$work"
cp "$work"/templates/* "$target/"

# The official macOS template is universal only. Add arm64 slices so an arm64 preset finds its binary.
lipo_tool="$(command -v lipo || command -v llvm-lipo || ls /usr/bin/llvm-lipo-* 2>/dev/null | sort -V | tail -1 || true)"
if [[ -z "$lipo_tool" ]]; then
  echo "No lipo or llvm-lipo found; cannot thin the macOS template" >&2
  exit 1
fi
mac="$work/mac"
mkdir -p "$mac"
(cd "$mac" && unzip -q "$target/macos.zip")
binaries="$mac/macos_template.app/Contents/MacOS"
for kind in release debug; do
  "$lipo_tool" "$binaries/godot_macos_$kind.universal" -thin arm64 -output "$binaries/godot_macos_$kind.arm64"
  chmod +x "$binaries/godot_macos_$kind.arm64"
done
(cd "$mac" && zip -q "$target/macos.zip" macos_template.app/Contents/MacOS/godot_macos_release.arm64 \
  macos_template.app/Contents/MacOS/godot_macos_debug.arm64)
echo "Installed templates in $target"
