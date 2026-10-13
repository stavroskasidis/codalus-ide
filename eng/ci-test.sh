#!/usr/bin/env bash
# Runs one shard of the tests (CI runs the shards on separate runners at the same time; ci.yml):
#   shell-1  the Shell tests but the classes listed in eng/ci-shell-second-half.txt
#   shell-2  those classes
#   others   every other test project
# Usage: eng/ci-test.sh <shard> [Release|Debug]
set -euo pipefail
shard="${1:?shard: shell-1, shell-2 or others}"
configuration="${2:-Release}"
cd "$(dirname "$0")/.."

# A test process still running after 25 minutes is dumped, and the tests that were running are named, instead of the
# job timing out without a word.
common=(-c "$configuration" --no-build -- --hangdump --hangdump-timeout 25m --hangdump-type Mini)
mapfile -t second < <(grep -v '^\s*#' eng/ci-shell-second-half.txt | sed '/^\s*$/d')

case "$shard" in
  shell-1)
    filters=()
    for class in "${second[@]}"; do filters+=(--filter-not-class "DaedalusStudio.Shell.Tests.$class"); done
    dotnet test --project tests/DaedalusStudio.Shell.Tests "${common[@]}" "${filters[@]}"
    ;;
  shell-2)
    filters=()
    for class in "${second[@]}"; do filters+=(--filter-class "DaedalusStudio.Shell.Tests.$class"); done
    dotnet test --project tests/DaedalusStudio.Shell.Tests "${common[@]}" "${filters[@]}"
    ;;
  others)
    # The Shell tests' namespace is left out: that module runs no tests, which isn't a failure (exit code 8). The
    # Windows runner has four cores, where all the projects at once timed out at random: three at a time there.
    extra=()
    [[ "${RUNNER_OS:-}" == "Windows" ]] && extra=(--max-parallel-test-modules 3)
    dotnet test --solution DaedalusStudio.slnx -c "$configuration" --no-build "${extra[@]}" -- \
      --hangdump --hangdump-timeout 25m --hangdump-type Mini --filter-not-namespace DaedalusStudio.Shell.Tests --ignore-exit-code 8
    ;;
  *)
    echo "Unknown shard: $shard" >&2
    exit 2
    ;;
esac
