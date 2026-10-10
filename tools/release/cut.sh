#!/usr/bin/env bash
# Cuts a release from dev: picks the next version, checks it may be released, runs the tests, commits the version
# bump on dev, then pushes dev and fast-forwards main to it in one push. The push to main starts the release workflow,
# which builds both games and publishes the GitHub release.
# Usage: tools/release/cut.sh patch|minor|major|X.Y.Z [-y]
#   -y  answer yes to the confirmations (every check still runs)
# The Godot .NET editor comes from $GODOT, else godot-mono or godot on PATH, else the macOS app.
set -euo pipefail

# What players in one match share. A patch should leave these alone, since it must play with every other patch of
# its minor version: messages, the suburb built from the server's plan (body ids follow it), tanks, and the tuners.
shared_paths=(src/Net src/World src/Tank src/Player/PlayerNet.cs src/Core/Tuning.cs)

bump="${1:-}"
yes=0
[[ "${2:-}" == "-y" ]] && yes=1
if [[ -z "$bump" ]]; then
  echo "usage: $0 patch|minor|major|X.Y.Z [-y]" >&2
  exit 2
fi
project="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$project"
die() {
  echo "release: $*" >&2
  exit 1
}
confirm() {
  ((yes)) && return 0
  local answer
  read -r -p "$1 [y/N] " answer
  [[ "$answer" == [yY]* ]]
}

[[ "$(git branch --show-current)" == dev ]] || die "releases are cut from dev"
[[ -z "$(git status --porcelain)" ]] || die "commit or stash your changes first"
git fetch --quiet --tags origin
git merge-base --is-ancestor origin/dev dev || die "dev is behind origin/dev: pull first"
git merge-base --is-ancestor origin/main dev || die "main has commits that dev lacks: merge main into dev first"

latest="$(git tag -l 'v[0-9]*.[0-9]*.[0-9]*' --sort=-v:refname | head -1)"
base="${latest#v}"
[[ -n "$base" ]] || base="$(sed -n 's/^config\/version="\(.*\)"$/\1/p' project.godot)"
IFS=. read -r major minor patch <<<"$base"
case "$bump" in
  patch) version="$major.$minor.$((patch + 1))" ;;
  minor) version="$major.$((minor + 1)).0" ;;
  major) version="$((major + 1)).0.0" ;;
  *) version="${bump#v}" ;;
esac
tools/release/check_version.sh "$version" >/dev/null

if [[ -n "$latest" && "${version%.*}" == "${base%.*}" ]]; then
  shared="$(git diff --name-only "$latest" dev -- "${shared_paths[@]}")"
  if [[ -n "$shared" ]]; then
    echo "A patch plays with every $major.$minor.x, but these files that players in a match share changed since $latest:"
    echo "$shared" | sed 's/^/  /'
    echo "If any change affects other players (messages, the suburb, tanks, tuners), release a minor version instead."
    confirm "Release v$version as a patch anyway?" || die "stopped"
  fi
fi

echo
echo "v$version, changes since ${latest:-the start}:"
git log --oneline --no-decorate "${latest:+$latest..}dev"
echo
confirm "Test and release v$version?" || die "stopped"

godot="${GODOT:-}"
if [[ -z "$godot" ]]; then
  for candidate in godot-mono godot /Applications/Godot_mono.app/Contents/MacOS/Godot; do
    if command -v "$candidate" >/dev/null 2>&1; then
      godot="$candidate"
      break
    fi
  done
fi
[[ -n "$godot" ]] || die "set GODOT to the Godot .NET editor binary"
log="$(mktemp)"
trap 'rm -f "$log"' EXIT
echo "Testing..."
if ! { dotnet build Propane.sln -c Debug -nologo -v quiet &&
  "$godot" --headless --path . res://scenes/dev/generator_tests.tscn -- --seeds=100; } >"$log" 2>&1; then
  tail -40 "$log" >&2
  die "the tests failed"
fi

perl -pi -e "s/^config\\/version=\".*\"\$/config\\/version=\"$version\"/" project.godot
git commit --quiet -m "Release v$version" project.godot
if ! git push --quiet --atomic origin dev dev:main; then
  die "the push failed; the version bump is committed on dev. Push again with: git push --atomic origin dev dev:main"
fi
git update-ref refs/heads/main dev
echo "Pushed v$version to main. The release workflow builds and publishes it:"
echo "  https://github.com/talmage89/propane-game/actions"
