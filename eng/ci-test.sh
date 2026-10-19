#!/usr/bin/env bash
# Runs one shard of the tests (CI runs the shards on separate runners at the same time; pipeline.yml):
#   shell-1  the Shell tests but the classes listed in eng/ci-shell-second-half.txt
#   shell-2  those classes
#   others   every other test project
# Usage: eng/ci-test.sh <shard> [Release|Debug]
# Bash 3.2 (macOS): no mapfile, and an empty array is expanded with ${array[@]+"${array[@]}"} under set -u.
set -euo pipefail
shard="${1:?shard: shell-1, shell-2 or others}"
configuration="${2:-Release}"
cd "$(dirname "$0")/.."

# A hang is reported twice, well inside the job's 50 minutes (pipeline.yml), instead of the job timing out without a
# word: after 10 minutes in one test, the test watchdog (tests/Shared/TestWatchdog.cs) names it and prints the managed
# stacks of the test process and its hosts (dotnet-stack); after 15 minutes without a test finishing, HangDump names
# the tests still running, dumps the process tree into the results folder (which CI uploads) and ends it, so the job
# fails instead of being cancelled. HangDump before 2.1 printed nothing: it turned itself off under dotnet test.
export CODALUS_TEST_WATCHDOG_MINUTES="${CODALUS_TEST_WATCHDOG_MINUTES:-10}"
results="$PWD/artifacts/test-results/$shard"
hang=(--hangdump --hangdump-timeout 15m --hangdump-type Mini --results-directory "$results")
common=(-c "$configuration" --no-build -- "${hang[@]}")
second=()
while IFS= read -r line; do
  line="${line%%#*}"
  line="$(echo "$line" | tr -d '[:space:]')"
  [[ -n "$line" ]] && second+=("$line")
done < eng/ci-shell-second-half.txt

case "$shard" in
  shell-1|shell-2)
    flag=--filter-class
    [[ "$shard" == shell-1 ]] && flag=--filter-not-class
    filters=()
    for class in "${second[@]}"; do filters+=("$flag" "Codalus.Shell.Tests.$class"); done
    dotnet test --project tests/Codalus.Shell.Tests "${common[@]}" "${filters[@]}"
    ;;
  others)
    # The Shell tests' namespace is left out: that module runs no tests, which isn't a failure (exit code 8). The
    # Windows runner has four cores, where all the projects at once timed out at random, and three at a time still
    # starved a test now and then (a plugin host's start, a trace's start, Aspire's orchestrator check): two there.
    # The tests that write full core dumps run on their own afterwards: next to every other test process (a macOS
    # runner has 7 GB) createdump stalled past its timeout or wrote a truncated core.
    extra=()
    [[ "${RUNNER_OS:-}" == "Windows" ]] && extra=(--max-parallel-test-modules 2)
    dumps=(Codalus.Debugging.Tests.CompanionTests Codalus.Debugging.Tests.JitDebuggingTests)
    without=()
    alone=()
    for class in "${dumps[@]}"; do without+=(--filter-not-class "$class"); alone+=(--filter-class "$class"); done
    # Both run whatever the first's outcome, and the shard fails if either did.
    status=0
    dotnet test --solution Codalus.slnx -c "$configuration" --no-build ${extra[@]+"${extra[@]}"} -- \
      "${hang[@]}" --filter-not-namespace Codalus.Shell.Tests "${without[@]}" --ignore-exit-code 8 || status=$?
    dotnet test --project tests/Codalus.Debugging.Tests "${common[@]}" "${alone[@]}" || status=$?
    exit "$status"
    ;;
  *)
    echo "Unknown shard: $shard" >&2
    exit 2
    ;;
esac
