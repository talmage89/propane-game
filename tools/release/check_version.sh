#!/usr/bin/env bash
# Checks that a version may be released: MAJOR.MINOR.PATCH, newer than every release, and, when it is a patch (same
# major and minor as the latest release), the same network protocol as that release, since patches play together.
# Usage: tools/release/check_version.sh [version]   (default: config/version in project.godot)
# Reads the release tags (v1.2.3), so fetch them first. Run by tools/release/cut.sh and by the release workflow.
set -euo pipefail

project="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$project"
version="${1:-$(sed -n 's/^config\/version="\(.*\)"$/\1/p' project.godot)}"
fail() {
  echo "check_version: $*" >&2
  exit 1
}
protocol_of() { sed -n 's/.*public const int Version = \([0-9][0-9]*\);.*/\1/p'; }

[[ "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] || fail "'$version' is not MAJOR.MINOR.PATCH"
latest="$(git tag -l 'v[0-9]*.[0-9]*.[0-9]*' --sort=-v:refname | head -1)"
if [[ -z "$latest" ]]; then
  echo "v$version: the first release"
  exit 0
fi
previous="${latest#v}"
newest="$(printf '%s\n%s\n' "$previous" "$version" | sort -V | tail -1)"
if [[ "$version" == "$previous" || "$newest" != "$version" ]]; then
  fail "v$version is not newer than the latest release, $latest"
fi

if [[ "${version%.*}" == "${previous%.*}" ]]; then
  now="$(protocol_of <src/Net/Protocol.cs)"
  before="$(git show "$latest:src/Net/Protocol.cs" 2>/dev/null | protocol_of || true)"
  if [[ "$now" != "$before" ]]; then
    IFS=. read -r major minor _ <<<"$version"
    fail "the network protocol changed since $latest (${before:-none} to $now), so it cannot be a patch: release $major.$((minor + 1)).0"
  fi
fi
echo "v$version: after $latest"
