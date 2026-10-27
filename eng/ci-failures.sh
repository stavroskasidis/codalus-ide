#!/usr/bin/env bash
# What failed in CI, so a failure is investigated before anything is re-run (docs/ci-flakes.md):
#   eng/ci-failures.sh <run id>       every failure of a pipeline run, in every attempt (a re-run's earlier attempts too)
#   eng/ci-failures.sh --recent [N]   the failures of the last N pipeline runs (default 60), grouped: each failed test,
#                                     smoke step or lost runner with how often it failed, on which OS, and in which runs
# A failure is a failed test (from the job's annotations, which eng/ci-test.sh writes, or its log), a smoke test step
# that failed, the tests HangDump found still running, a step's timeout, or a runner that was lost. Jobs that only show
# as cancelled because another job stopped the run are left out.
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

# One line per failure of a job: "<kind>\t<what>".
job_failures() {
  local job="$1" log="$cache/$1.log" notes
  notes="$(api "repos/$repo/check-runs/$job/annotations" --jq '.[] | select(.annotation_level == "failure") | .message' 2> /dev/null || true)"
  if grep -q "lost communication with the server\|exceeded the maximum execution time" <<< "$notes"; then
    grep "lost communication with the server\|exceeded the maximum execution time" <<< "$notes" | head -1 | sed 's/^/runner\t/'
  fi
  if [[ ! -s "$log" ]]; then
    api "repos/$repo/actions/jobs/$job/logs" 2> /dev/null | awk '{ sub(/^[0-9T:.-]+Z /, ""); gsub(/\033\[[0-9;]*m/, ""); print }' > "$log" || true
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
    api "repos/$repo/actions/runs/$run/attempts/$attempt/jobs?per_page=100" \
      --jq '.jobs[] | select(.name != "ci") | select(.conclusion == "failure" or ([.steps[]? | select(.conclusion == "failure")] | length > 0)) | "\(.id)\t\(.name)"' |
      while IFS=$'\t' read -r job name; do
        job_failures "$job" | while IFS=$'\t' read -r kind what; do
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
    run_failures "$1" | awk -F'\t' '{ job = "attempt " $2 ": " $3; if (job != last) print job; last = job; printf "  %-7s %s\n", $4, $5 }'
    ;;
esac
