#!/usr/bin/env bash
# Save every formation once, then compare transforms, meshes, masses and collision in two fresh processes.
set -euo pipefail
project="$(cd "$(dirname "$0")/../.." && pwd)"
godot="${GODOT:-godot-mono}"
scratch="$(mktemp -d "${TMPDIR:-/tmp}/wptest-world-XXXXXX")"
cleanup() {
  local status=$?
  if (( status == 0 )); then
    rm -rf "$scratch"
  else
    echo "World check failed (status $status); logs kept in $scratch" >&2
    for log in "$scratch"/*.log; do
      [[ -f "$log" ]] || continue
      echo "--- $log (last 80 lines) ---" >&2
      tail -n 80 "$log" >&2
    done
  fi
}
trap cleanup EXIT
scene=res://scenes/dev/unit_tests.tscn
"$godot" --headless --path "$project" "$scene" -- --write-world-plans="$scratch" >"$scratch/plan.log" 2>&1
for process in 1 2; do
  "$godot" --headless --path "$project" "$scene" -- --hash-world-plans="$scratch" >"$scratch/$process.log" 2>&1
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
# Dissolve Run's segments: the same run seeds give the same segments in fresh processes (five runs of twelve).
for process in 1 2; do
  "$godot" --headless --path "$project" res://scenes/dev/generator_tests.tscn -- --hash-segments=5 >"$scratch/segments-$process.log" 2>&1
  grep -E '^\[segments\] hash ' "$scratch/segments-$process.log" >"$scratch/segments-$process.hash"
  test "$(wc -l < "$scratch/segments-$process.hash")" -eq 60
done
diff -u "$scratch/segments-1.hash" "$scratch/segments-2.hash"
echo 'Separate-process segment plans agree for five runs of twelve segments.'
