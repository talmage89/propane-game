#!/usr/bin/env bash
# Adds bot players to a multiplayer server, to play against: they join the first open lobby (create one first) and
# play every match the lobby starts. They run headless from this checkout, in the background, until stopped.
# Usage: tools/dev/add_bots.sh [-n count] [-S host[:port]] [-k skill 0..1] [-b smart|simple]
#        tools/dev/add_bots.sh -x     stops the bots this script started
# Defaults: 4 bots, a server on this computer (127.0.0.1:24680, e.g. Host on this computer), skill 1, the smart brain.
set -euo pipefail

count=4
server="127.0.0.1:24680"
skill=1
brain=smart
stop=0
while getopts "n:S:k:b:x" opt; do
  case "$opt" in
    n) count="$OPTARG" ;;
    S) server="$OPTARG" ;;
    k) skill="$OPTARG" ;;
    b) brain="$OPTARG" ;;
    x) stop=1 ;;
    *) exit 2 ;;
  esac
done
project="$(cd "$(dirname "$0")/../.." && pwd)"
pids="${TMPDIR:-/tmp}/propane-bots.pids"

if (( stop )); then
  [[ -f "$pids" ]] && xargs kill <"$pids" 2>/dev/null || true
  rm -f "$pids"
  echo "bots stopped"
  exit 0
fi

godot="${GODOT:-}"
if [[ -z "$godot" ]]; then
  for candidate in godot-mono godot /Applications/Godot_mono.app/Contents/MacOS/Godot; do
    if command -v "$candidate" >/dev/null 2>&1 || [[ -x "$candidate" ]]; then
      godot="$candidate"
      break
    fi
  done
fi
[[ -n "$godot" ]] || { echo "set GODOT to the Godot .NET editor binary" >&2; exit 1; }

address="${server%%:*}"
port=24680
[[ "$server" == *:* ]] && port="${server##*:}"
names=(Ada Bo Cy Dee Eli Flo Gus)
for i in $(seq 1 "$count"); do
  name="${names[$(( (i - 1) % ${#names[@]} ))]}Bot"
  nohup "$godot" --headless --path "$project" res://scenes/dev/net_test.tscn -- --server="$address" --port="$port" --role=join \
    --name="$name" --color=$(( (i + 2) % 8 )) --brain="$brain" --bot-skill="$skill" --matches=100000 --quit=86400 \
    >"${TMPDIR:-/tmp}/propane-bot-$i.log" 2>&1 &
  echo $! >>"$pids"
  sleep 0.3
done
echo "$count bots joining the first open lobby on $address:$port; stop them with: $0 -x"
