#!/usr/bin/env bash
# Which of the review units in docs/upstream/series are already in an upstream commit, and whether the rest still
# apply on top of it.
#
#   scripts/lake/patch-status.sh [upstream-ref]     (default: upstream/master)
#
# The series is replayed in order on a temporary worktree of the ref. For each patch:
#   in upstream   - it applies in reverse: upstream already has this change (merged as is, or identically)
#   applies       - it applies on top of upstream and the earlier patches
#   needs refresh - neither: upstream changed the same code, or merged the unit in another form; regenerate it
# Replay stops at the first "needs refresh", since later patches build on it. Prints a Markdown table.
set -euo pipefail

ref="${1:-upstream/master}"
root="$(git rev-parse --show-toplevel)"
series="$root/docs/upstream/series"
[ -d "$series" ] || { echo "no $series" >&2; exit 1; }

work="$(mktemp -d)"
trap 'git -C "$root" worktree remove --force "$work" >/dev/null 2>&1 || true; rm -rf "$work"' EXIT
git -C "$root" worktree add --detach --quiet "$work" "$ref"

echo "| Patch | Status against \`$(git -C "$root" rev-parse --short "$ref")\` |"
echo "|---|---|"
status=0
for p in "$series"/*.patch; do
  name="$(basename "$p" .patch)"
  if git -C "$work" apply --index "$p" >/dev/null 2>&1; then
    echo "| $name | applies |"
  elif git -C "$work" apply --reverse --check "$p" >/dev/null 2>&1; then
    echo "| $name | in upstream |"
  else
    echo "| $name | needs refresh |"
    status=2
    break
  fi
done
exit $status
