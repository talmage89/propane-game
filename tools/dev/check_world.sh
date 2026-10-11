#!/usr/bin/env bash
# Build one saved plan in two fresh processes; compare the actual world, including fence mass and collision.
set -euo pipefail
project="$(cd "$(dirname "$0")/../.." && pwd)"
godot="${GODOT:-godot-mono}"
scratch="$(mktemp -d /tmp/wpnet-world-XXXXXX)"
trap 'rm -rf "$scratch"' EXIT
scene=res://scenes/dev/unit_tests.tscn
"$godot" --headless --path "$project" "$scene" -- --write-world-plan="$scratch/plan.bin" >"$scratch/plan.log" 2>&1
for process in 1 2; do
  "$godot" --headless --path "$project" "$scene" -- --hash-world-plan="$scratch/plan.bin" >"$scratch/$process.log" 2>&1
  rg '^\[world\] hash ' "$scratch/$process.log" >"$scratch/$process.hash"
done
diff -u "$scratch/1.hash" "$scratch/2.hash"
cat "$scratch/1.hash"
echo 'Separate-process world builds agree.'
