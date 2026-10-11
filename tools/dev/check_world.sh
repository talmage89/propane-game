#!/usr/bin/env bash
# Build one plan per formation in two fresh processes; compare transforms, meshes, masses and collision.
set -euo pipefail
project="$(cd "$(dirname "$0")/../.." && pwd)"
godot="${GODOT:-godot-mono}"
scratch="$(mktemp -d "${TMPDIR:-/tmp}/propane-world-XXXXXX")"
trap 'rm -rf "$scratch"' EXIT
scene=res://scenes/dev/unit_tests.tscn
for formation in Rows Square Triangular Cluster; do
  "$godot" --headless --path "$project" "$scene" -- --formation="$formation" --write-world-plan="$scratch/plan.bin" >"$scratch/plan.log" 2>&1
  for process in 1 2; do
    "$godot" --headless --path "$project" "$scene" -- --hash-world-plan="$scratch/plan.bin" >"$scratch/$process.log" 2>&1
    rg '^\[world\] hash ' "$scratch/$process.log" >"$scratch/$process.hash"
  done
  diff -u "$scratch/1.hash" "$scratch/2.hash"
  cat "$scratch/1.hash"
done
echo 'Separate-process world builds agree for all four formations.'
