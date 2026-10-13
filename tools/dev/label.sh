#!/usr/bin/env bash
# Runs a command with each output line prefixed by [label], so parallel make jobs stay attributable.
# Exits with the command's status, and names the label and command on failure.
# Usage: tools/dev/label.sh label command [arguments...]
set -uo pipefail
label=$1
shift
"$@" 2>&1 | awk -v prefix="[$label] " '{ print prefix $0; fflush() }'
code=${PIPESTATUS[0]}
if (( code != 0 )); then
  printf '[%s] FAIL (exit %d):' "$label" "$code" >&2
  printf ' %q' "$@" >&2
  printf '\n' >&2
fi
exit "$code"
