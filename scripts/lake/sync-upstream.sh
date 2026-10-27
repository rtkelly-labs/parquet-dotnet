#!/usr/bin/env bash
# Bring an upstream Parquet.Net commit or release into Lake.Parquet.
#
#   scripts/lake/sync-upstream.sh [upstream-ref] [--no-test]
#
# upstream-ref: a tag of aloneguid/parquet-dotnet (a release) or a branch (default: upstream/master).
#
# 1. fetches upstream (adds the `upstream` remote if missing);
# 2. stops if the current branch already contains the ref;
# 3. on a new branch sync/upstream-<short sha>, merges the ref (a merge, never a rebase: published history and
#    submodule pins stay valid);
# 4. on conflict, lists the files and stops (exit 2) for a person to resolve, then re-run with --continue;
# 5. records the new base (commit and version) in docs/lake/upstream-base and commits it; Parquet.SourceGenerator's
#    Lake build names it in the package;
# 6. runs the test suite;
# 7. reports which review units upstream now has (scripts/lake/patch-status.sh).
#
#   scripts/lake/sync-upstream.sh --continue [--no-test]   after resolving conflicts and committing the merge
set -euo pipefail

root="$(git rev-parse --show-toplevel)"
cd "$root"
base_file=docs/lake/upstream-base
upstream_url="${LAKE_UPSTREAM_URL:-https://github.com/aloneguid/parquet-dotnet}"

ref=""; test=1; cont=0
for a in "$@"; do
  case "$a" in
    --no-test) test=0 ;;
    --continue) cont=1 ;;
    *) ref="$a" ;;
  esac
done

record_base() {
  local sha="$1" version
  version="$(git show "$sha:docs/release-notes.md" | sed -n 1p | tr -d ' #\r')"
  echo "$sha $version" > "$base_file"   # commit and Parquet.Net version, read by the Lake.Parquet build
  if ! git diff --quiet -- "$base_file"; then
    git add "$base_file"
    git commit -q -m "Upstream base: Parquet.Net $version (${sha:0:7})"
  fi
  echo "base: Parquet.Net $version ($sha)"
}

if [ $cont -eq 0 ]; then
  git remote get-url upstream >/dev/null 2>&1 || git remote add upstream "$upstream_url.git"
  git fetch --quiet --tags upstream
  ref="${ref:-upstream/master}"
  sha="$(git rev-parse "$ref^{commit}")"
  if git merge-base --is-ancestor "$sha" HEAD; then
    echo "already contains $ref ($sha)"
    exit 0
  fi
  git switch -q -c "sync/upstream-${sha:0:7}"
  if ! git merge --no-ff -q -m "Merge upstream Parquet.Net $ref (${sha:0:7})" "$sha"; then
    echo "conflicts merging $ref:" >&2
    git diff --name-only --diff-filter=U >&2
    echo "resolve them (docs/lake/maintaining.md), commit, then: $0 --continue" >&2
    exit 2
  fi
else
  # the merge commit's second parent is the upstream commit
  sha="$(git rev-parse HEAD^2)"
fi

record_base "$sha"

if [ $test -eq 1 ]; then
  dotnet test src/Parquet.Test
fi

scripts/lake/patch-status.sh "$sha" || true
