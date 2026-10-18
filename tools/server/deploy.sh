#!/usr/bin/env bash
# Exports a clean release-tag checkout and runs it as the multiplayer server in Docker on a remote host.
# Usage: tools/server/deploy.sh <ssh host> <remote dir, relative to the remote home> [path to the Godot .NET editor]
# The host needs Docker. Needs export templates and a local main containing the release tag.
set -euo pipefail

if (( $# < 2 )); then
  echo "usage: $0 <ssh host> <remote dir> [godot]" >&2
  exit 2
fi
host="$1"
remote="$2"
godot="${3:-${GODOT:-godot-mono}}"
project="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$project"
version="$(sed -n 's/^config\/version="\(.*\)"$/\1/p' project.godot)"
tag="v$version"
if [[ -n "$(git status --porcelain)" ]] ||
   [[ "$(git rev-parse HEAD)" != "$(git rev-parse --verify "refs/tags/$tag^{commit}" 2>/dev/null || true)" ]] ||
   ! git merge-base --is-ancestor HEAD refs/heads/main; then
  echo "Refusing deployment: use a clean checkout of release tag $tag contained in local main." >&2
  echo "Fetch release history first; this script does not deploy unreleased branches or promise the latest remote release." >&2
  exit 1
fi
mkdir -p build
stage="$(mktemp -d "$project/build/deploy.XXXXXX")"
trap 'rm -rf "$stage" "$project/build_id.txt"' EXIT
echo "Deploying $tag ($(git rev-parse HEAD))"
git rev-parse --short HEAD >build_id.txt
mkdir -p "$stage/game"
log="$stage/export.log"
if ! "$godot" --headless --path . --export-release Linux "$stage/game/Propane.x86_64" >"$log" 2>&1 || grep -q '^ERROR' "$log"; then
  # An earlier export error must not hide a later shutdown backtrace before scratch cleanup.
  grep -A3 '^ERROR' "$log" >&2 || true
  tail -40 "$log" >&2
  exit 1
fi
dotnet build tools/architecture/Architecture.csproj -nologo -v quiet
dotnet build/architecture/bin/Debug/net10.0/Architecture.dll inventory \
  "$stage/game/data_Propane_linuxbsd_x86_64/Propane.dll" "$stage/game/Propane.pck" tools/architecture
printf '%s\n%s\n' "$tag" "$(git rev-parse HEAD)" >"$stage/game/release-id.txt"
cp tools/server/Dockerfile tools/server/docker-compose.yml "$stage/"

# tar over ssh rather than rsync, which a server may not have.
ssh "$host" "mkdir -p $remote && rm -rf $remote/game"
tar -C "$stage" -czf - game Dockerfile docker-compose.yml | ssh "$host" "tar -C $remote -xzf -"
ssh "$host" "cd $remote && docker compose up -d --build && sleep 3 && docker compose logs --tail 5"
