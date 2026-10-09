#!/usr/bin/env bash
# Pushes the current working tree (uncommitted and untracked-but-not-ignored files included) to the scratch branch
# `validate` on origin, so another machine (the Mac, see CLAUDE.md) can test work that is not committed yet.
# It leaves HEAD, the index and the working tree untouched: the snapshot commit is built from a temporary index, with
# HEAD as its parent, and force-pushed. CI and the benchmarks only trigger on master, so the push costs no CI minutes.
#
# Usage: eng/push-snapshot.sh [branch]      prints the snapshot commit
# On the other machine: git fetch origin validate && git checkout --detach FETCH_HEAD
set -euo pipefail

branch="${1:-validate}"
repo="$(git rev-parse --show-toplevel)"
cd "$repo"

index="$(mktemp)"
trap 'rm -f "$index"' EXIT
cp "$(git rev-parse --git-path index)" "$index"
GIT_INDEX_FILE="$index" git add -A
tree="$(GIT_INDEX_FILE="$index" git write-tree)"
commit="$(git commit-tree "$tree" -p HEAD -m "Validation snapshot of $(git rev-parse --short HEAD) with local changes")"
git push --quiet --force origin "$commit:refs/heads/$branch"
echo "$commit"
