#!/usr/bin/env bash
# Runs both spikes and prints a summary. Usage: bash run.sh [rid] [runs] [design runs] [sections]
#   rid defaults to this machine's (osx-arm64, osx-x64, linux-x64, linux-arm64, win-x64; on Windows, run it in Git Bash);
#   DEBUGGEE_TFMS="net9.0;net10.0" builds the debuggee for those only (what the installed SDK can build).
#   runs (5) per mode of the debugging spike; design runs (25) per launch candidate and attach variant;
#   sections (default "spike design attach hotreload").
set -u
cd "$(dirname "$0")"
# macOS has no timeout(1): fall back to perl's alarm.
command -v timeout >/dev/null || timeout() { local secs=$1; shift; perl -e 'alarm shift; exec @ARGV' "$secs" "$@"; }
rid=${1:-$(dotnet --info | awk '/RID:/ {print $2; exit}')}
runs=${2:-5}
design_runs=${3:-25}
sections=${4:-spike design attach hotreload}
for p in Spike HotReload Hook; do dotnet build $p -nologo -v q >/dev/null || { echo "build of $p failed"; dotnet build $p -nologo -v q | grep -E "error" | head; exit 1; }; done
DebuggeeTfms=${DEBUGGEE_TFMS:-} dotnet build Debuggee -nologo -v q 2>&1 | grep -E " error " | sort -u | head -3
tfms=$(for t in Debuggee/bin/Debug/*/; do t=${t%/}; t=${t##*/}; ls Debuggee/bin/Debug/$t/Debuggee.dll >/dev/null 2>&1 && echo $t; done)
shim=$(find ~/.nuget/packages/microsoft.diagnostics.dbgshim.$rid \( -name 'libdbgshim.*' -o -name 'dbgshim.dll' \) | head -1)
echo "rid $rid, shim $shim, debuggee tfms: $(echo $tfms), $(uname -s 2>/dev/null) $(uname -m 2>/dev/null)"
spike() { timeout 90 dotnet Spike/bin/Debug/net10.0/Spike.dll "$shim" "$@" 2>&1; }
full() { grep -q 'func-eval order.Label = "#42 for Ada"' <<<"$1" && grep -q "step over -> line 8" <<<"$1" && grep -q "total 252" <<<"$1"; }
code() { grep -o 'exit code: -\{0,1\}[0-9]*' <<<"$1" | grep -o -- '-\{0,1\}[0-9]*$'; }
leftovers() { if command -v pgrep >/dev/null; then pgrep -f 'Debug/net[0-9.]*/Debuggee' | tr '\n' ' '; else tasklist 2>/dev/null | grep -i debuggee | awk '{print $2}' | tr '\n' ' '; fi; }
median() { sort -n | awk '{a[NR]=$1} END {print (NR ? a[int((NR+1)/2)] : "-")}'; }

# 1. The debugging spike: launch, attach and held launch, a full session each.
if [[ " $sections " == *" spike "* ]]; then
for tfm in $tfms; do
  for mode in launch --attach --hold; do
    ok=0; codes=""
    for n in $(seq $runs); do
      start=$(date +%s)
      out=$(spike Debuggee/bin/Debug/$tfm/Debuggee 6 $mode); rc=$?
      if full "$out"; then ok=$((ok+1)); else echo "--- FAILED $tfm $mode run $n: status $rc after $(( $(date +%s) - start )) s; leftover debuggees: $(leftovers)"; echo "$out" | tail -20; fi
      codes="$codes $(code "$out")"
    done
    echo "spike $tfm $mode: $ok/$runs full sessions; exit codes:$codes"
  done
  out=$(SPIKE_HIT_ONLY=1 spike Debuggee/bin/Debug/$tfm/Debuggee 1 --hold); echo "spike $tfm first-line breakpoint under --hold: $(grep -c 'hit on.*at line 1' <<<"$out")"
done
fi

# 2. The launch design: candidate (a) --launcher and (b) --hook, with the debuggee starting a .NET child of its own
#    (through Process.Start and natively); each run must be a full session with exit code 7, both children exit 3
#    (not held, the variable not inherited), and no "configured to pause" notice in the output.
if [[ " $sections " == *" design "* ]]; then
for tfm in $tfms; do
  for mode in --launcher --hook; do
    ok=0; codes=""; hits=""; notices=0; managed=""; native=""; first=0; firsthits=""
    for n in $(seq $design_runs); do
      start=$(date +%s)
      out=$(SPIKE_CHILD=1 spike Debuggee/bin/Debug/$tfm/Debuggee 6 $mode); rc=$?
      c=$(code "$out"); m=$(grep -o 'managed child: [a-z]* [0-9]*' <<<"$out" | head -1); v=$(grep -o 'native child: [a-z]* [0-9]*' <<<"$out" | head -1)
      grep -q "configured to pause" <<<"$out" && notices=$((notices+1))
      if full "$out" && [ "$c" = 7 ] && [ "$m" = "managed child: exit 3" ] && [ "$v" = "native child: exit 3" ] && ! grep -q "configured to pause" <<<"$out"; then ok=$((ok+1)); else echo "--- FAILED $tfm $mode run $n: status $rc after $(( $(date +%s) - start )) s; leftover debuggees: $(leftovers)"; echo "$out" | tail -25; fi
      codes="$codes $c"; managed="$managed|${m#managed child: }"; native="$native|${v#native child: }"
      hits="$hits $(grep -o 'breakpoint hit on thread [0-9]* at line 6 after [0-9]*' <<<"$out" | grep -o '[0-9]*$')"
      out=$(SPIKE_HIT_ONLY=1 spike Debuggee/bin/Debug/$tfm/Debuggee 1 $mode)
      if grep -q 'hit on.*at line 1' <<<"$out" && [ "$(code "$out")" = 7 ]; then first=$((first+1)); else echo "--- FIRST LINE FAILED $tfm $mode run $n"; echo "$out" | tail -15; fi
      firsthits="$firsthits $(grep -o 'at line 1 after [0-9]*' <<<"$out" | grep -o '[0-9]*$')"
    done
    echo "design $tfm $mode: $ok/$design_runs runs with every check passing; first-line breakpoint $first/$design_runs; notices $notices"
    echo "  exit codes:$codes"
    echo "  managed child: $(tr '|' '\n' <<<"${managed#|}" | sort | uniq -c | tr -s ' ' | tr '\n' ';')  native child: $(tr '|' '\n' <<<"${native#|}" | sort | uniq -c | tr -s ' ' | tr '\n' ';')"
    echo "  ms to the line-6 breakpoint, median $(tr ' ' '\n' <<<"$hits" | grep . | median); to the line-1 breakpoint, median $(tr ' ' '\n' <<<"$firsthits" | grep . | median)"
  done
  # What each candidate fixes: the plain held launch's children and notice, and (b) clearing only .NET's copy of the environment.
  out=$(SPIKE_CHILD=1 spike Debuggee/bin/Debug/$tfm/Debuggee 6 --hold); echo "baseline $tfm --hold: exit $(code "$out"); $(grep -o 'managed child: .*' <<<"$out" | cut -c1-60); $(grep -o 'native child: .*' <<<"$out"); notice lines: $(grep -c 'configured to pause' <<<"$out")"
  # The runtime prints its "configured to pause" notice only once it has been held 5 s.
  for d in 4000 6000; do out=$(SPIKE_RESUME_DELAY_MS=$d SPIKE_HIT_ONLY=1 spike Debuggee/bin/Debug/$tfm/Debuggee 1 --hook); echo "baseline $tfm --hook resumed after $d ms: notice lines $(grep -c 'configured to pause' <<<"$out"), first line hit $(grep -c 'at line 1' <<<"$out"), exit $(code "$out")"; done
  out=$(SPIKE_CHILD=1 SPIKE_HOOK_MANAGED_ONLY=1 spike Debuggee/bin/Debug/$tfm/Debuggee 6 --hook); echo "baseline $tfm --hook, Environment.SetEnvironmentVariable only: $(grep -o 'managed child: .*' <<<"$out" | cut -c1-60); $(grep -o 'native child: .*' <<<"$out")"
done
fi

# 2b. Attach, repeated: the program goes on to the breakpoint's line once the breakpoint is bound, and (the first
#     spike's way) at once after DebugActiveProcess returns, before the attach's LoadModule events.
if [[ " $sections " == *" attach "* ]]; then
for tfm in $tfms; do
  for wait in bound none; do
    ok=0; codes=""
    for n in $(seq $design_runs); do
      if [ $wait = none ]; then out=$(SPIKE_ATTACH_NO_WAIT=1 spike Debuggee/bin/Debug/$tfm/Debuggee 6 --attach); else out=$(spike Debuggee/bin/Debug/$tfm/Debuggee 6 --attach); fi
      if full "$out"; then ok=$((ok+1)); else echo "--- FAILED $tfm --attach (released: $wait) run $n"; echo "$out" | grep -E "Exception|exit code|bound|hit" | head -5; fi
      codes="$codes $(code "$out")"
    done
    echo "attach $tfm, program released when $([ $wait = bound ] && echo "the breakpoint is bound" || echo "DebugActiveProcess returns"): $ok/$design_runs full sessions; exit codes:$codes"
  done
done
fi

# 3. Hot Reload through ICorDebugModule2::ApplyChanges.
if [[ " $sections " == *" hotreload "* ]]; then
for flags in "" "--late" "--not-modifiable" "--no-enc-flag --not-modifiable" "--late --not-modifiable"; do
  echo "=== hotreload [${flags:-early, env set, EnC flag}]"
  timeout 60 dotnet HotReload/bin/Debug/net10.0/HotReload.dll "$shim" $flags 2>&1 | grep -E "ApplyChanges|JIT flags|exit code|RESULT|stopped in|Exception|rror"
done
fi
