#!/usr/bin/env bash
# Scratch branch `stress` only, never merged (M65, 2026-11-19): runs one group of eng/stress-groups.txt in a loop under
# heavy load (half the cores kept busy, the group's test projects at once), for the maintainer's rule for 1.0: a row
# that can't be reproduced or reasoned to a cause closes as "not reproduced" after 50+ passes under heavy load.
# Usage: eng/stress.sh <group> <iterations>
set -u
group="$1"; iterations="$2"
results="$PWD/artifacts/stress"
mkdir -p "$results"
export CODALUS_TEST_WATCHDOG_MINUTES=10
export CODALUS_NETCOREDBG_LOG="$results/netcoredbg" CODALUS_CLR_DEBUGGER_LOG="$results/clr-dap" CODALUS_JSDEBUG_TRACE="$results/js-debug"
export MSBUILDDISABLENODEREUSE=1
msb="$results/msbuild"; mkdir -p "$msb"; MSBUILDDEBUGPATH="$msb"
command -v cygpath > /dev/null && MSBUILDDEBUGPATH="$(cygpath -w "$msb")"
export MSBUILDDEBUGPATH
if [[ "$group" == *codalus* ]]; then
  export CODALUS_DEBUG_ADAPTER=codalus
  export CODALUS_DEBUGHOST="$PWD/src/Codalus.Debugging.Host/bin/Release/net11.0/codalus-debughost.dll"
fi

# The load: half the cores busy the whole time.
cores="$(getconf _NPROCESSORS_ONLN 2> /dev/null || sysctl -n hw.ncpu 2> /dev/null || echo 4)"
burners=()
for ((c = 0; c < (cores + 1) / 2; c++)); do
  (while :; do :; done) &
  burners+=("$!")
done
trap 'kill "${burners[@]}" 2> /dev/null' EXIT
echo "group $group, $iterations iterations, $cores cores, ${#burners[@]} busy loops"

projects="$(awk -v g="$group" '$1 == g { print $2 }' eng/stress-groups.txt | sort -u)"
filters_of() { awk -v g="$group" -v p="$1" '$1 == g && $2 == p { printf " --filter-%s %s", $3, $4 }' eng/stress-groups.txt; }

for ((i = 1; i <= iterations; i++)); do
  pids=()
  for project in $projects; do
    # shellcheck disable=SC2046
    (dotnet test --project "tests/$project" -c Release --no-build --output detailed -- --hangdump --hangdump-timeout 15m \
      --hangdump-type Mini --results-directory "$results/$project-$i" $(filters_of "$project") > "$results/$project-$i.txt" 2>&1) &
    pids+=("$!")
  done
  wait "${pids[@]}"
  for project in $projects; do
    out="$results/$project-$i.txt"
    grep -E '^(passed|failed|skipped) ' "$out" | sed "s/^/$i $(date +%H:%M:%S) /" | tee -a "$results/summary.txt"
    if grep -qE '^failed ' "$out" || ! grep -qE '^(passed|failed|skipped) ' "$out"; then
      mv "$out" "$results/FAILED-$project-$i.txt"
    else
      rm -rf "$out" "$results/$project-$i"
    fi
  done
done

{
  echo "## $group: $iterations iterations under load ($cores cores, ${#burners[@]} busy loops)"
  echo
  echo '| Test | Passed | Failed | Skipped |'
  echo '|---|---|---|---|'
  awk '{ r = $3; t = $4; n[t]; c[t, r]++ } END { for (t in n) printf "| %s | %d | %d | %d |\n", t, c[t, "passed"], c[t, "failed"], c[t, "skipped"] }' "$results/summary.txt" | sort
} >> "${GITHUB_STEP_SUMMARY:-/dev/stdout}"
! ls "$results"/FAILED-* > /dev/null 2>&1
