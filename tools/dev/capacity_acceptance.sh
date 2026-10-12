#!/usr/bin/env bash
# Repeat the tested-capacity gate with distinct ENet clients; all scratch lives below the selected output directory.
set -euo pipefail
project="$(cd "$(dirname "$0")/../.." && pwd)"
out="$project/build/a6-capacity"
actors=16
repeats=3
windowed=1
while getopts "n:r:o:w:" opt; do
  case "$opt" in
    n) actors="$OPTARG" ;;
    r) repeats="$OPTARG" ;;
    o) out="$OPTARG" ;;
    w) windowed="$OPTARG" ;;
    *) exit 2 ;;
  esac
done
(( actors >= 3 && actors <= 16 && repeats >= 1 )) || exit 2
mkdir -p "$out/tmp"
export TMPDIR="$out/tmp"
for faults in clean faulted; do
  for run in $(seq 1 "$repeats"); do
    directory="$out/$faults-$run"
    profile=clean
    extra=()
    [[ "$faults" == faulted ]] && profile=faults && extra=(--net-lag=120 --net-jitter=30 --net-loss=3)
    "$project/tools/dev/net_bots.sh" -n "$actors" -t "$profile" -l 120 -w "$windowed" -o "$directory" \
      -s '--start-score=8 --capacity-metrics' -- --scenario=capacity --digest-every=1 --net-seed="$run" "${extra[@]}"
    python3 -B "$project/tools/dev/check_capacity.py" "$directory" "$actors" | tee "$directory/acceptance.txt"
  done
done
