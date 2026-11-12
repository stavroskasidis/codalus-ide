#!/usr/bin/env bash
# What failed in CI, so a failure is investigated before anything is re-run (docs/ci-flakes.md):
#   eng/ci-failures.sh <run id>       every failure of a pipeline run, in every attempt (a re-run's earlier attempts too)
#   eng/ci-failures.sh --recent [N]   the failures of the last N pipeline runs (default 60), grouped: each failed test,
#                                     smoke step or lost runner with how often it failed, on which OS, and in which runs
# A failure is a failed test (from the job's annotations, which eng/ci-test.sh writes, or its log), a smoke test step
# that failed, the tests HangDump found still running, a step's timeout, or a runner that was lost. Jobs that only show
# as cancelled because another job stopped the run are left out.
# A runner that went silent leaves no log: its job ends with a step that never finished. For one run, what the job's
# live output last sent off the runner is printed with it (.github/actions/live-output: the tests that had just ended,
# the last memory, load and disk samples, and how to get the whole upload).
# Needs gh (authenticated for a private repository). Logs are kept in ${XDG_CACHE_HOME:-~/.cache}/codalus-ci.
set -euo pipefail
repo="${CODALUS_CI_REPO:-stavroskasidis/codalus-ide}"
cache="${XDG_CACHE_HOME:-$HOME/.cache}/codalus-ci"
mkdir -p "$cache"

usage() {
  sed -n '2,4p' "$0" | sed 's/^# \{0,1\}//'
  exit 2
}

# gh api, tried three times: GitHub answers 502 now and then, and a run read halfway would be counted short.
api() {
  local attempt
  for attempt in 1 2 3; do
    gh api "$@" && return 0
    sleep $((attempt * 3))
  done
  echo "gh api $1 failed three times" >&2
  return 1
}

# gh 2.102 prints nothing (an error on stderr) for a response with terminal escape sequences, which every job's log has
# (the test runner's colors), unless allowed; an older gh doesn't know the option.
log_options=()
gh api --help 2>&1 | grep -q -- --allow-escape-sequences && log_options=(--allow-escape-sequences)

# What a test job's live output last uploaded, for a job whose runner went silent: "live\t<what>" lines. The artifact is
# live-<os>-<shard>-<attempt>-<n>, from the job's name "test (<os>, <shard>)".
live_output() {
  local run="$1" attempt="$2" name="$3" prefix artifact id folder file sample shard
  [[ "$name" =~ ^test\ \(([^,]+),\ ([^\)]+)\)$ ]] || return 0
  prefix="live-${BASH_REMATCH[1]}-${BASH_REMATCH[2]}-$attempt-"
  artifact="$(api "repos/$repo/actions/runs/$run/artifacts?per_page=100" --jq ".artifacts[] | select(.expired | not) | select(.name | startswith(\"$prefix\")) | \"\(.id)\t\(.name)\"" 2> /dev/null | sort -t$'\t' -k2,2 | tail -1)"
  if [[ -z "$artifact" ]]; then
    printf 'live\tno upload named %s<n> is left on the run\n' "$prefix"
    return 0
  fi

  id="${artifact%%$'\t'*}"
  folder="$cache/live-$id"
  if [[ ! -d "$folder" ]]; then
    mkdir -p "$folder"
    api "repos/$repo/actions/artifacts/$id/zip" > "$folder.zip" 2> /dev/null && unzip -q -o "$folder.zip" -d "$folder" 2> /dev/null || true
    rm -f "$folder.zip"
  fi

  printf 'live\tthe last upload: %s (gh run download %s -R %s -n %s)\n' "${artifact#*$'\t'}" "$run" "$repo" "${artifact#*$'\t'}"
  for file in "$folder"/*/console.txt; do
    [[ -f "$file" ]] || continue
    shard="$(basename "$(dirname "$file")")"
    # The tests that had just ended (the one running then comes after them in the class), and the watchdog's word.
    awk '{ gsub(/\033\[[0-9;]*m/, ""); print }' "$file" | grep -E '^(passed|failed|skipped) [A-Za-z_]|^\[[0-9:]+\] [A-Za-z_]|still running' | tail -4 |
      while IFS= read -r line; do printf 'live\t%s console: %s\n' "$shard" "${line:0:200}"; done
    for sample in "$folder/$shard"/memory.txt "$folder/$shard"/load.txt "$folder/$shard"/disk.txt; do
      [[ -s "$sample" ]] && printf 'live\t%s %s: %s\n' "$shard" "$(basename "$sample" .txt)" "$(grep -E '^[0-9]{2}:[0-9]{2}:[0-9]{2} |^\[disk\]' "$sample" | tail -1 | cut -c1-200)"
    done
  done
}

# One line per failure of a job: "<kind>\t<what>".
job_failures() {
  local job="$1" log="$cache/$1.log" notes
  notes="$(api "repos/$repo/check-runs/$job/annotations" --jq '.[] | select(.annotation_level == "failure") | .message' 2> /dev/null || true)"
  if grep -q "lost communication with the server\|exceeded the maximum execution time" <<< "$notes"; then
    grep "lost communication with the server\|exceeded the maximum execution time" <<< "$notes" | head -1 | sed 's/^/runner\t/'
  fi
  if [[ ! -s "$log" ]]; then
    api "repos/$repo/actions/jobs/$job/logs" "${log_options[@]}" 2> /dev/null | awk '{ sub(/^[0-9T:.-]+Z /, ""); gsub(/\033\[[0-9;]*m/, ""); print }' > "$log" || true
    grep -q "BlobNotFound" "$log" && : > "$log"
  fi
  [[ -s "$log" ]] || return 0
  awk '
    /^failed [A-Za-z_]/ { name = $2; sub(/\(.*/, "", name); print "test\t" name; next }
    /^\[[0-9:]+\] [A-Za-z_][A-Za-z0-9_.]+$/ && hang { print "hang\t" $2; next }
    /following tests were still running/ { hang = 1; next }
    /^\[[0-9:]+\] / { step = $2 " " $3; sub(/:$/, "", step); next }
    /^  FAILED \(/ { message = $0; sub(/^  FAILED \([^)]*\): */, "", message); print "smoke\t" step ": " substr(message, 1, 140); next }
    /^##\[error\]The action .* has timed out/ { print "timeout\t" substr($0, 10) }
  ' "$log" | sort -u
}

# Every failure of a run: "<run>\t<attempt>\t<job>\t<kind>\t<what>".
run_failures() {
  local run="$1" attempts attempt
  attempts="$(api "repos/$repo/actions/runs/$run" --jq '.run_attempt')"
  for ((attempt = 1; attempt <= attempts; attempt++)); do
    # A job whose runner went silent is over (failed, or cancelled at its limit) with a step that never finished.
    api "repos/$repo/actions/runs/$run/attempts/$attempt/jobs?per_page=100" \
      --jq '.jobs[] | select(.name != "ci") | ([.steps[]? | select(.status == "in_progress") | .name] | first // "") as $silent
        | select(.conclusion == "failure" or ([.steps[]? | select(.conclusion == "failure")] | length > 0) or (.status == "completed" and $silent != ""))
        | "\(.id)\t\(.name)\t\($silent)"' |
      while IFS=$'\t' read -r job name silent; do
        {
          job_failures "$job"
          if [[ -n "$silent" ]]; then
            printf 'runner\tThe runner went silent: "%s" never finished\n' "$silent"
            [[ -n "${live:-}" ]] && live_output "$run" "$attempt" "$name"
          fi
        } | while IFS=$'\t' read -r kind what; do
          printf '%s\t%s\t%s\t%s\t%s\n' "$run" "$attempt" "$name" "$kind" "$what"
        done
      done
  done
}

case "${1:-}" in
  --recent)
    count="${2:-60}"
    runs="$(api "repos/$repo/actions/workflows/pipeline.yml/runs?per_page=$count" --jq '.workflow_runs[] | select(.status == "completed") | .id')"
    total="$(wc -w <<< "$runs" | tr -d ' ')"
    echo "Failures in the last $total completed pipeline runs, most frequent first (count, what, the jobs, the runs):"
    # A run that can't be read is named and skipped, not the end of the list.
    for run in $runs; do run_failures "$run" || echo "Run $run couldn't be read: its failures aren't counted." >&2; done |
      awk -F'\t' '{
        key = $4 "\t" $5; n[key]++
        if (index(jobs[key], $3) == 0) jobs[key] = jobs[key] (jobs[key] ? "; " : "") $3
        if (index(runs[key], $1) == 0) runs[key] = runs[key] (runs[key] ? " " : "") $1
      } END { for (k in n) print n[k] "\t" k "\t" jobs[k] "\t" runs[k] }' |
      sort -t$'\t' -k1,1nr |
      awk -F'\t' '{ printf "%3d  %-6s %s\n       %s\n       runs: %s\n", $1, $2, $3, $4, $5 }'
    ;;
  "" | -h | --help) usage ;;
  *)
    live=1 # one run: with what a silent runner's job last sent off it
    run_failures "$1" | awk -F'\t' '{ job = "attempt " $2 ": " $3; if (job != last) print job; last = job; printf "  %-7s %s\n", $4, $5 }'
    ;;
esac
