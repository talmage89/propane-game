#!/usr/bin/env bash
# Runs a local multiplayer test: a headless server and N bot clients (scenes/dev/net_test.tscn) playing one match.
# Logs go to the output directory; tank agreement and final scores are checked automatically.
# Usage: tools/dev/net_bots.sh [-n bots] [-l match seconds] [-o out dir, default build/net] [-g godot] [-w windowed bots] [-s "server args"] [-S host:port of an existing server]
#   [-k skip checks] [-t faults|clean] [-b brain per bot, e.g. smart,simple,simple] [-c hex colors per bot, e.g. 12ABEF,12ABEF,F6F6F6] [-- extra bot args]
# Extra bot args (after --) go to every bot, e.g. --net-lag=120 --net-jitter=30 --net-loss=5 --capture-dir=... --shot-every=5
set -euo pipefail

bots=2
length=60
out=""
godot="${GODOT:-godot-mono}"
windowed=0
port=$((24700 + RANDOM % 200))
server_args=""
remote=""
brains=""
colors=""
checks=1
profile=faults
while getopts "n:l:o:g:w:p:s:S:b:c:kt:" opt; do
  case "$opt" in
    n) bots="$OPTARG" ;;
    l) length="$OPTARG" ;;
    o) out="$OPTARG" ;;
    g) godot="$OPTARG" ;;
    w) windowed="$OPTARG" ;;
    p) port="$OPTARG" ;;
    s) server_args="$OPTARG" ;;
    S) remote="$OPTARG" ;;
    b) brains="$OPTARG" ;;
    c) colors="$OPTARG" ;;
    k) checks=0 ;;
    t) profile="$OPTARG" ;;
    *) exit 2 ;;
  esac
done
shift $((OPTIND - 1))
[[ "${1:-}" == "--" ]] && shift
[[ "$profile" == faults || "$profile" == clean ]] || { echo "invalid check profile: $profile" >&2; exit 2; }
if (( checks )) && [[ -n "$remote" ]]; then
  echo "-S needs -k: the remote server audit log is unavailable; run checks separately with that log" >&2
  exit 2
fi
project="$(cd "$(dirname "$0")/../.." && pwd)"
out="${out:-$project/build/net}"
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
  brain="$(echo "$brains" | cut -d, -f"$i")"
  appearance=()
  if [[ -n "$colors" ]]; then
    color="$(echo "$colors" | cut -d, -f"$i")"
    [[ "$color" =~ ^[0-9A-Fa-f]{6}$ ]] || { echo "missing or invalid hex color for bot $i" >&2; exit 2; }
    appearance=("--appearance=$color" "--expected-appearances=$colors")
  fi
  display=(--headless)
  if (( i <= windowed )); then
    display=(--resolution 960x540 --position $(( (i - 1) * 970 )),40)
  fi
  timeout "$budget" "$godot" "${display[@]}" --path "$project" res://scenes/dev/net_test.tscn -- --server="$address" --port="$port" \
    --tank-check --role="$role" --players="$bots" --length="$length" --name="Bot$i" --color=$((i - 1)) --brain="${brain:-smart}" --windowed "${appearance[@]}" "$@" >"$out/bot$i.log" 2>&1 &
  pids+=($!)
  sleep 0.4
done
status=0
for pid in "${pids[@]}"; do
  wait "$pid" || status=$?
done
echo "bots finished (status $status); logs in $out"
grep -h "match over" "$out/server.log" 2>/dev/null || true
if (( checks )); then
  if (( bots >= 2 )); then
    python3 -B "$project/tools/dev/compare_tanks.py" --profile "$profile" "$out"/bot*.log || status=1
  else
    echo "tank comparison skipped: only one bot"
  fi
  python3 -B "$project/tools/dev/check_scores.py" "$out/server.log" "$out"/bot*.log || status=1
fi
exit "$status"
