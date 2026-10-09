#!/usr/bin/env bash
# Exports the Linux build and runs it as the multiplayer server in Docker on a remote host.
# Usage: tools/server/deploy.sh [ssh host] [remote dir] [path to the Godot .NET editor]
# Defaults: host "games", directory ~/games/propane/server-1. Needs the export templates (tools/release/install_templates.sh).
set -euo pipefail

host="${1:-games}"
remote="${2:-games/propane/server-1}"
godot="${3:-${GODOT:-godot-mono}}"
project="$(cd "$(dirname "$0")/../.." && pwd)"
stage="$(mktemp -d)"
trap 'rm -rf "$stage" "$project/build_id.txt"' EXIT

cd "$project"
git rev-parse --short HEAD >build_id.txt
mkdir -p "$stage/game"
log="$stage/export.log"
if ! "$godot" --headless --path . --export-release Linux "$stage/game/Propane.x86_64" >"$log" 2>&1 || grep -q '^ERROR' "$log"; then
  grep -A3 '^ERROR' "$log" >&2 || tail -40 "$log" >&2
  exit 1
fi
cp tools/server/Dockerfile tools/server/docker-compose.yml "$stage/"

# tar over ssh rather than rsync, which a server may not have.
ssh "$host" "mkdir -p $remote && rm -rf $remote/game"
tar -C "$stage" -czf - game Dockerfile docker-compose.yml | ssh "$host" "tar -C $remote -xzf -"
ssh "$host" "cd $remote && docker compose up -d --build && sleep 3 && docker compose logs --tail 5"
