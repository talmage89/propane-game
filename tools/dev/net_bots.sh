#!/usr/bin/env bash
# Runs a local multiplayer test: a headless server and N bot clients (scenes/dev/net_test.tscn) playing one match.
# Logs go to the output directory; compare the clients' views of the tanks with tools/dev/compare_tanks.py.
# Usage: tools/dev/net_bots.sh [-n bots] [-l match seconds] [-o out dir] [-g godot] [-w windowed bots] [-s "server args"] [-S host:port of an existing server] [-- extra bot args]
# Extra bot args (after --) go to every bot, e.g. --net-lag=120 --net-jitter=30 --net-loss=5 --capture-dir=... --shot-every=5
set -euo pipefail

bots=2
length=60
out="${TMPDIR:-/tmp}/propane-net"
godot="${GODOT:-godot-mono}"
windowed=0
port=$((24700 + RANDOM % 200))
server_args=""
remote=""
while getopts "n:l:o:g:w:p:s:S:" opt; do
  case "$opt" in
    n) bots="$OPTARG" ;;
    l) length="$OPTARG" ;;
    o) out="$OPTARG" ;;
    g) godot="$OPTARG" ;;
    w) windowed="$OPTARG" ;;
    p) port="$OPTARG" ;;
    s) server_args="$OPTARG" ;;
    S) remote="$OPTARG" ;;
    *) exit 2 ;;
  esac
done
shift $((OPTIND - 1))
[[ "${1:-}" == "--" ]] && shift
project="$(cd "$(dirname "$0")/../.." && pwd)"
mkdir -p "$out"
rm -f "$out"/*.log
budget=$((length + 75))

address=127.0.0.1
if [[ -n "$remote" ]]; then
  address="${remote%%:*}"
  [[ "$remote" == *:* ]] && port="${remote##*:}" || port=24680
else
  "$godot" --headless --path "$project" -- --server --port="$port" $server_args >"$out/server.log" 2>&1 &
  server=$!
  trap 'kill $server 2>/dev/null || true' EXIT
  sleep 1.5
fi

pids=()
for i in $(seq 1 "$bots"); do
  role=join
  [[ $i == 1 ]] && role=host
  display=(--headless)
  if (( i <= windowed )); then
    display=(--resolution 960x540 --position $(( (i - 1) * 970 )),40)
  fi
  timeout "$budget" "$godot" "${display[@]}" --path "$project" res://scenes/dev/net_test.tscn -- --server="$address" --port="$port" \
    --role="$role" --players="$bots" --length="$length" --name="Bot$i" --color=$((i - 1)) --windowed "$@" >"$out/bot$i.log" 2>&1 &
  pids+=($!)
  sleep 0.4
done
status=0
for pid in "${pids[@]}"; do
  wait "$pid" || status=$?
done
echo "bots finished (status $status); logs in $out"
grep -h "match over" "$out/server.log" 2>/dev/null || true
exit "$status"
