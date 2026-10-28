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
# On the macOS runner the Shell shards' hang ends the tests without a dump: twice the runner was lost 46 minutes into
# shell-2 (runs 40255384457 and 40453091722, and the job's timeout in 40241261003), where a hang's 15 minutes after the
# shard's usual 30 put HangDump dumping the Shell tests' tree (the test process, its hosts and their build nodes) on a
# 7 GB runner; a lost runner keeps neither its log nor its artifacts, so nothing said which test. The watchdog's stacks
# (dotnet-stack, at 10 minutes) are in the log instead, and HangDump still names the tests still running and ends them.
dump=Mini
[[ "${RUNNER_OS:-}" == "macOS" && "$shard" == shell-* ]] && dump=None
hang=(--hangdump --hangdump-timeout 15m --hangdump-type "$dump" --results-directory "$results")
common=(-c "$configuration" --no-build -- "${hang[@]}")
mkdir -p "$results"
# netcoredbg's DAP log of every debug session (--engineLogging, a few KB each), in the results folder that CI uploads
# when the shard fails: a func-eval netcoredbg refused once on Windows (0x80131313, CORDBG_E_FUNC_EVAL_BAD_START_POINT,
# which the runtime returns while an aborted func-eval is pending; netcoredbg aborts one after 5 s) left no trace of
# the evaluation before it (DataTipDebuggerTests, run 40375629247).
export CODALUS_NETCOREDBG_LOG="${CODALUS_NETCOREDBG_LOG:-$results/netcoredbg}"

# Why an MSBuild worker node dies mid-build. The build host's builds failed now and then on the macOS runner with
# "MSB4166: Child node "2" exited prematurely" and nothing else (DotNetTestRunControlTests, BuildIntegrationTests;
# runs 39172828497 and 39782528364), which is what a node killed with SIGKILL prints: a node that crashes in managed
# code writes MSBuild_*.failure.txt, by default to a temp folder the runner throws away, so it goes to the results
# folder (uploaded when the job fails). A node killed by the kernel for memory (the macOS runner has 7 GB, and runs
# every test module at once) or one that crashed natively leaves no trace there, so on CI this also samples the free
# memory while the tests run and, at the end, prints the kernel's memory kills and the crash reports of the run.
msbuild_debug="$results/msbuild"
mkdir -p "$msbuild_debug"
MSBUILDDEBUGPATH="$msbuild_debug"
command -v cygpath > /dev/null && MSBUILDDEBUGPATH="$(cygpath -w "$msbuild_debug")" # Git Bash: MSBuild wants a Windows path
export MSBUILDDEBUGPATH
started_at="$(date '+%Y-%m-%d %H:%M:%S')"
touch "$results/.started"
# Each sample also goes to the log as one line (the free memory, the swap in use, the number of processes and the
# largest three), so it shows as the shard runs: a runner that is lost (macOS shell-2, runs 40241261003 and 40255384457,
# M51) uploads neither the results folder nor its log, and only the live log was there to read.
sampler=
if [[ "${CI:-}" == "true" && "$(uname)" == "Darwin" ]]; then
  (
    while :; do
      {
        now="$(date -u +%H:%M:%S)"
        free="$(sysctl -n kern.memorystatus_level)"
        swap="$(sysctl -n vm.swapusage)"
        top="$(ps -axo rss=,pid=,ppid=,args= | sort -nr)"
        echo "$now free=$free% swap: $swap" >> "$results/memory.txt"
        echo "$top" | head -6 | cut -c1-200 >> "$results/memory.txt"
        used="$(echo "$swap" | awk '{ for (i = 1; i < NF; i++) if ($i == "used") print $(i + 2) }')"
        largest="$(echo "$top" | head -3 | awk '{ n = $4; for (i = 5; i <= NF; i++) if ($i ~ /\.dll$/) { n = $i; break }; sub(".*/", "", n); printf " %dM %s", $1 / 1024, n }')"
        echo "[memory] $now free=$free% swap=$used processes=$(echo "$top" | wc -l | tr -d ' ') largest:$largest"
      } 2>/dev/null || true
      sleep 15
    done
  ) &
  sampler=$!
fi

# Every test command's output goes to console.txt, so the report can name the failed tests: as annotations on the run's
# page and in the job's summary, where a failure is read first (a job that stops the run shows as cancelled, and its log
# is thousands of lines). eng/ci-failures.sh reads them from there to tell a new failure from a known flake. A tail shows
# the file in the log as it grows. Not a pipe through tee: a process the tests leave running (a build server, an MSBuild
# node) inherits the pipe and holds it open, and tee waited for it after the tests had ended until the step timed out
# (Windows shell-1, run 40571698799: the tests took 14 minutes, the step 40). A file doesn't wait for anyone.
console="$results/console.txt"
: > "$console"
tail -n +1 -f "$console" &
tailer=$!

# What the report reads may be missing or fail: that never changes the shard's outcome.
report() {
  set +eo pipefail
  if [[ -n "$sampler" ]]; then
    pkill -P "$sampler" 2>/dev/null
    kill "$sampler" 2>/dev/null
  fi
  sleep 2 # the tail reads the file about once a second: its last lines reach the log first
  kill "$tailer" 2>/dev/null
  local failed
  failed="$(awk '{ gsub(/\033\[[0-9;]*m/, ""); print }' "$console" 2>/dev/null | grep -E '^(failed [A-Za-z_]|\[[0-9:]+\] [A-Za-z_][A-Za-z0-9_.]+)' | sort -u | head -30)"
  if [[ -n "$failed" ]]; then
    echo "Failed tests (and tests still running at a hang):"
    echo "$failed"
    if [[ "${GITHUB_ACTIONS:-}" == "true" ]]; then
      echo "$failed" | while IFS= read -r line; do echo "::error title=$shard on ${RUNNER_OS:-?}::$line"; done
      [[ -n "${GITHUB_STEP_SUMMARY:-}" ]] && { echo "### Failed tests ($shard, ${RUNNER_OS:-?})"; echo; echo "$failed" | sed 's/^/- /'; echo; echo "Investigate before re-running: docs/ci-flakes.md."; } >> "$GITHUB_STEP_SUMMARY"
    fi
  fi
  local failures
  failures="$(find "$msbuild_debug" -name 'MSBuild_*.failure.txt' 2>/dev/null)"
  if [[ -n "$failures" ]]; then
    echo "MSBuild nodes that failed (MSBUILDDEBUGPATH):"
    echo "$failures" | while IFS= read -r file; do echo "== $file"; head -40 "$file"; done
  fi
  [[ "${CI:-}" == "true" ]] || return 0
  if [[ "$(uname)" == "Darwin" ]]; then
    local predicate='process == "kernel" AND (eventMessage CONTAINS[c] "memorystatus" OR eventMessage CONTAINS[c] "jetsam" OR eventMessage CONTAINS[c] "low swap" OR eventMessage CONTAINS[c] "exceeded mem limit")'
    { sudo -n log show --style compact --start "$started_at" --predicate "$predicate" 2>/dev/null \
      || log show --style compact --start "$started_at" --predicate "$predicate" 2>/dev/null; } | tail -n 3000 > "$results/kernel-memory.txt"
    echo "Processes the kernel killed for memory during the tests (all its memory lines: kernel-memory.txt in the results):"
    local kills
    kills="$(grep -i "kill" "$results/kernel-memory.txt")"
    if [[ -n "$kills" ]]; then echo "$kills" | head -50; else echo "  none"; fi
    if [[ -s "$results/memory.txt" ]]; then
      echo "Least free memory while the tests ran (every 15 seconds: memory.txt in the results):"
      awk '/ free=/ { v = $2; sub("free=", "", v); sub("%", "", v); print v " " $0 }' "$results/memory.txt" | sort -n | head -3 | cut -d' ' -f2-
    fi
    mkdir -p "$results/crash-reports"
    find "$HOME/Library/Logs/DiagnosticReports" /Library/Logs/DiagnosticReports -maxdepth 1 -type f -newer "$results/.started" \
      \( -name '*.ips' -o -name '*.crash' \) -exec cp {} "$results/crash-reports/" \; 2>/dev/null
    echo "Crash reports written during the tests (crash-reports in the results):"
    local any= crash
    for crash in "$results"/crash-reports/*; do
      [[ -f "$crash" ]] || continue
      any=1
      echo "== $(basename "$crash"): $(grep -o '"procPath" *: *"[^"]*"\|"signal" *: *"[^"]*"\|"indicator" *: *"[^"]*"' "$crash" | head -3 | tr '\n' ' ')"
    done
    [[ -n "$any" ]] || echo "  none"
  elif [[ "$(uname)" == "Linux" ]]; then
    echo "Processes the kernel killed for memory during the tests:"
    local oom
    oom="$(sudo -n dmesg 2>/dev/null | grep -i "out of memory\|killed process\|oom-kill" | tail -20)"
    if [[ -n "$oom" ]]; then echo "$oom"; else echo "  none"; fi
  fi
}
trap report EXIT
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
    # Every test is listed as it ends, so a shard that stalls shows how far it got next to the memory lines above.
    dotnet test --project tests/Codalus.Shell.Tests --output detailed "${common[@]}" "${filters[@]}" >> "$console" 2>&1
    ;;
  others)
    # The Shell tests' namespace is left out: that module runs no tests, which isn't a failure (exit code 8). The
    # Windows runner has four cores, where all the projects at once timed out at random, and three at a time still
    # starved a test now and then (a plugin host's start, a trace's start, Aspire's orchestrator check): two there.
    # The macOS runner (three cores, 7 GB) too: with every project at once, the fixtures' builds ran out of the
    # kernel's socket buffers (an MSBuild node's pipe write failed with ENOBUFS, "No buffer space available": MSB4166,
    # run 39893166475), and three unrelated tests stalled together until HangDump ended them (its second attempt).
    # The tests that write full core dumps run on their own afterwards: next to every other test process (a macOS
    # runner has 7 GB) createdump stalled past its timeout or wrote a truncated core.
    extra=()
    [[ "${RUNNER_OS:-}" == "Windows" || "${RUNNER_OS:-}" == "macOS" ]] && extra=(--max-parallel-test-modules 2)
    dumps=(Codalus.Debugging.Tests.CompanionTests Codalus.Debugging.Tests.JitDebuggingTests)
    without=()
    alone=()
    for class in "${dumps[@]}"; do without+=(--filter-not-class "$class"); alone+=(--filter-class "$class"); done
    # Both run whatever the first's outcome, and the shard fails if either did.
    status=0
    dotnet test --solution Codalus.slnx -c "$configuration" --no-build ${extra[@]+"${extra[@]}"} -- \
      "${hang[@]}" --filter-not-namespace Codalus.Shell.Tests "${without[@]}" --ignore-exit-code 8 >> "$console" 2>&1 || status=$?
    dotnet test --project tests/Codalus.Debugging.Tests "${common[@]}" "${alone[@]}" >> "$console" 2>&1 || status=$?
    exit "$status"
    ;;
  *)
    echo "Unknown shard: $shard" >&2
    exit 2
    ;;
esac
