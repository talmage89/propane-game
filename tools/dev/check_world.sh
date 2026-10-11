#!/usr/bin/env bash
# Save every formation once, then compare transforms, meshes, masses and collision in two fresh processes.
set -euo pipefail
project="$(cd "$(dirname "$0")/../.." && pwd)"
godot="${GODOT:-godot-mono}"
scratch="$(mktemp -d "${TMPDIR:-/tmp}/wptest-world-XXXXXX")"
trap 'rm -rf "$scratch"' EXIT
scene=res://scenes/dev/unit_tests.tscn
"$godot" --headless --path "$project" "$scene" -- --write-world-plans="$scratch" >"$scratch/plan.log" 2>&1 || { cat "$scratch/plan.log" >&2; exit 1; }
for process in 1 2; do
  "$godot" --headless --path "$project" "$scene" -- --hash-world-plans="$scratch" >"$scratch/$process.log" 2>&1 || { cat "$scratch/$process.log" >&2; exit 1; }
  grep -E '^\[world\] hash ' "$scratch/$process.log" >"$scratch/$process.hash"
  # Matching partial output must not accidentally pass the comparison.
  test "$(wc -l < "$scratch/$process.hash")" -eq 4
  for formation in Rows Square Triangular Cluster; do
    grep -q " formation $formation$" "$scratch/$process.hash"
  done
done
diff -u "$scratch/1.hash" "$scratch/2.hash"
cat "$scratch/1.hash"
echo 'Separate-process world builds agree for all four formations.'
