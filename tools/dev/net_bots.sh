#!/usr/bin/env bash
# Runs a local multiplayer test: a headless server and N bot clients (scenes/dev/net_test.tscn) playing one match.
# Logs go to the output directory; tank agreement and final scores are checked automatically.
# Usage: tools/dev/net_bots.sh [-n bots] [-l match seconds] [-o out dir, default build/net] [-g godot] [-w windowed bots] [-s "server args"] [-S host:port of an existing server]
#   [-k skip checks] [-t faults|clean] [-b brain per bot, e.g. smart,simple,simple] [-c hex colors per bot, e.g. 12ABEF,12ABEF,F6F6F6] [-- extra bot args]
#   --tank-supply=1..8 sets the lobby supply in either mode; defaults to the local TankCountMultiplier tuner.
#   King: -- --mode=king [--mint-seconds=2]; -n 3 -l 110 -k with --scenario=king-late --tune.PlayerSendRate=20 --delay-who=Bot3 --match-delay=70 tests a late builder; check scores separately.
# Extra bot args (after --) go to every bot, e.g. --net-lag=120 --net-jitter=30 --net-loss=5 --capture-dir=... --shot-every=5
#   --lobby-bots=2 (or 2,1 per bot) gives each client lobby bots it runs itself; --leave-at=30 --leave-who=Bot2 makes one leave
set -euo pipefail

bots=2
length=60
out=""
godot="${GODOT:-godot-mono}"
windowed=0
port=""
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
# Each match adds its length plus loading, countdown and results; --matches=N plays N on one connection.
matches=1
for arg in "$@"; do [[ "$arg" == --matches=* ]] && matches="${arg#*=}"; done
budget=$((matches * (length + 40) + 35))

address=127.0.0.1
if [[ -n "$remote" ]]; then
  address="${remote%%:*}"
  [[ "$remote" == *:* ]] && port="${remote##*:}" || port=24680
else
  # Without -p, pick a port no other run is listening on, and pick again if a concurrent run takes it first.
  pick=0
  [[ -z "$port" ]] && pick=1
  for attempt in 1 2 3 4 5; do
    if (( pick )); then
      port=$(python3 -c 'import random, socket
for _ in range(200):
    port = random.randrange(24700, 24900)
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
        try:
            probe.bind(("", port))
        except OSError:
            continue
    print(port)
    break')
    fi
    "$godot" --headless --path "$project" -- --server --port="$port" $server_args >"$out/server.log" 2>&1 &
    server=$!
    trap 'kill $server 2>/dev/null || true' EXIT
    sleep 1.5
    grep -q "Couldn't create an ENet host" "$out/server.log" 2>/dev/null || break
    kill $server 2>/dev/null || true
    wait $server 2>/dev/null || true
    if (( !pick || attempt == 5 )); then
      echo "FAIL: the server could not listen on port $port" >&2
      exit 1
    fi
  done
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
    display=(--disable-vsync --resolution 960x540 --position $(( (i - 1) * 970 )),40)
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
  if grep -nE '(^ERROR:|bad [A-Za-z]+ message|\[net\] bad message:)' "$out"/*.log; then
    echo "FAIL engine or protocol errors in network logs" >&2
    status=1
  fi
  # A client told to leave mid-match (--leave-at) has no completion to compare. The survivors' tank agreement, final
  # scores and the server's audit still cover its departure.
  client_logs=()
  for log in "$out"/bot*.log; do
    grep -q "leaving the match on purpose" "$log" || client_logs+=("$log")
  done
  # The capacity fixture intentionally retires its creator before completion. Its dedicated
  # checker proves that departure; all survivors still use the ordinary continuous tank gate.
  if [[ " $* " == *" --scenario=capacity "* ]] && grep -q "capacity creator departure" "$out/bot1.log"; then
    client_logs=()
    for i in $(seq 2 "$bots"); do client_logs+=("$out/bot$i.log"); done
  fi
  if (( ${#client_logs[@]} >= 2 )); then
    python3 -B "$project/tools/dev/compare_tanks.py" --profile "$profile" "${client_logs[@]}" || status=1
  else
    echo "tank comparison skipped: only one bot"
  fi
  python3 -B "$project/tools/dev/check_scores.py" "$out/server.log" "${client_logs[@]}" || status=1
fi
exit "$status"
