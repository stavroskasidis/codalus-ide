#!/usr/bin/env bash
# Runs both spikes and prints a summary. Usage: bash run.sh [rid]   (rid defaults to this machine's: osx-arm64, osx-x64, linux-x64)
set -u
cd "$(dirname "$0")"
# macOS has no timeout(1): fall back to perl's alarm.
command -v timeout >/dev/null || timeout() { local secs=$1; shift; perl -e 'alarm shift; exec @ARGV' "$secs" "$@"; }
rid=${1:-$(dotnet --info | awk '/RID:/ {print $2; exit}')}
dotnet build Spike -nologo -v q >/dev/null && dotnet build HotReload -nologo -v q >/dev/null || { echo "build failed"; exit 1; }
tfms=$(dotnet build Debuggee -nologo -v q 2>&1 | grep -q "error" && echo "" ; ls Debuggee/bin/Debug)
shim=$(find ~/.nuget/packages/microsoft.diagnostics.dbgshim.$rid -name 'libdbgshim.*' | head -1)
echo "rid $rid, shim $shim, debuggee tfms: $tfms"
for tfm in $tfms; do
  for mode in launch --attach --hold; do
    ok=0; codes=""
    for n in 1 2 3 4 5; do
      start=$(date +%s)
      out=$(timeout 60 dotnet Spike/bin/Debug/net10.0/Spike.dll "$shim" Debuggee/bin/Debug/$tfm/Debuggee 6 $mode 2>&1); rc=$?
      if grep -q 'func-eval order.Label = "#42 for Ada"' <<<"$out" && grep -q "step over -> line 8" <<<"$out" && grep -q "total 252" <<<"$out"; then ok=$((ok+1)); else echo "--- FAILED $tfm $mode run $n: status $rc after $(( $(date +%s) - start )) s; leftover debuggees: $(pgrep -f 'Debug/net[0-9.]*/Debuggee' | tr '\n' ' ')"; echo "$out" | tail -15; fi
      codes="$codes $(grep -o 'exit code: -\{0,1\}[0-9]*' <<<"$out" | grep -o -- '-\{0,1\}[0-9]*$')"
    done
    echo "spike $tfm $mode: $ok/5 full sessions; exit codes:$codes"
  done
done
out=$(SPIKE_HIT_ONLY=1 timeout 60 dotnet Spike/bin/Debug/net10.0/Spike.dll "$shim" Debuggee/bin/Debug/net10.0/Debuggee 1 --hold 2>&1); echo "first-line breakpoint under --hold: $(grep -c 'hit on.*at line 1' <<<"$out")"
for flags in "" "--late" "--not-modifiable" "--no-enc-flag --not-modifiable" "--late --not-modifiable"; do
  echo "=== hotreload [${flags:-early, env set, EnC flag}]"
  timeout 60 dotnet HotReload/bin/Debug/net10.0/HotReload.dll "$shim" $flags 2>&1 | grep -E "ApplyChanges|JIT flags|exit code|RESULT|stopped in|Exception|rror"
done
