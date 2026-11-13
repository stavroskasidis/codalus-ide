#!/usr/bin/env bash
# What a pull request's run tests, from the files it changes (pipeline.yml's gate; ADR 0028, update 2026-11-13). Reads
# the changed files, one per line, on the standard input and prints `scope=<scope>` and `projects=<test projects>`:
#   full      every OS, as develop's and master's runs: with --full (a push, a dispatch, the ci:full label), when the
#             file list is empty (the listing failed), or when a file is under a path in eng/ci-platform-paths.txt
#   linux     the Linux x64 test shards and the Linux smoke test: any other change
#   isolated  only the test projects of the areas in eng/ci-isolated-paths.txt that change, and the tests that scan
#             every tracked file (eng/ci-repo-scan.txt), in one Linux job without smoke: when every file is in such
#             an area or is documentation
#   docs      nothing (test and smoke skipped): only docs/** and *.md files
# Usage: git diff --name-only origin/develop... | eng/ci-scope.sh [--full]
# Bash 3.2 (macOS): no mapfile, no associative arrays.
set -euo pipefail
cd "$(dirname "$0")/.."

full=false
[[ "${1:-}" == "--full" ]] && full=true

# A file of `prefix [value]` lines, comments after #; a prefix starting with ! excludes what it starts.
read_list() {
  local line
  while IFS= read -r line || [[ -n "$line" ]]; do
    line="${line%%#*}"
    line="$(echo "$line" | sed -E 's/^[[:space:]]+//; s/[[:space:]]+$//')"
    [[ -n "$line" ]] && echo "$line"
  done < "$1"
}

platform=()
while IFS= read -r line; do platform+=("$line"); done < <(read_list eng/ci-platform-paths.txt)
isolated=()
while IFS= read -r line; do isolated+=("$line"); done < <(read_list eng/ci-isolated-paths.txt)

# Whether $1 is under one of the prefixes that follow and under none of the !excluded ones.
under() {
  local file="$1" prefix matched=false
  shift
  for prefix in "$@"; do
    if [[ "$prefix" == "!"* ]]; then
      [[ "$file" == "${prefix#!}"* ]] && return 1
    elif [[ "$file" == "$prefix"* ]]; then
      matched=true
    fi
  done
  [[ "$matched" == true ]]
}

files=()
while IFS= read -r line; do [[ -n "$line" ]] && files+=("$line"); done

scope=docs
projects=()
if [[ "$full" == true || ${#files[@]} -eq 0 ]]; then
  scope=full
else
  for file in "${files[@]}"; do
    if under "$file" ${platform[@]+"${platform[@]}"}; then
      scope=full
      break
    fi
    [[ "$file" == docs/* || "$file" == *.md ]] && continue
    project=
    for entry in ${isolated[@]+"${isolated[@]}"}; do
      prefix="${entry%% *}"
      if [[ "$file" == "$prefix"* ]]; then
        project="${entry#* }"
        break
      fi
    done
    if [[ -z "$project" ]]; then
      scope=linux
    else
      [[ "$scope" == docs ]] && scope=isolated
      [[ " ${projects[*]-} " == *" $project "* ]] || projects+=("$project")
    fi
  done
fi
[[ "$scope" == isolated ]] || projects=()

echo "scope=$scope"
echo "projects=${projects[*]-}"
