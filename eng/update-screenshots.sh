#!/usr/bin/env bash
# Copies the website's screenshots from the smoke test's pictures (CLAUDE.md, Website): each line of
# website/screenshots.txt names a picture on the site, the CI runner whose smoke job takes it, and the smoke test's
# picture. Then review the pictures (git diff shows which changed) and commit them.
#
# Usage:
#   eng/update-screenshots.sh <run id> [picture ...]   a CI run's smoke-test-<OS> artifacts, downloaded with gh
#   eng/update-screenshots.sh <folder> [picture ...]   a folder of smoke-test-<OS> folders (as gh run download leaves
#       them), or one smoke test's results (eng/smoke-test.sh's, artifacts/smoke-test), used for this OS's pictures
# With pictures named (maui.png wpf.png), only those are copied; otherwise every one the run has. A picture that isn't
# the site's 1280x800 is copied with a warning: the Windows runner's screen was 1024x768 before the smoke job set it to
# 1920x1080 (pipeline.yml).
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
manifest="$repo/website/screenshots.txt"
target="$repo/website/wwwroot/assets/screenshots"
source="${1:?usage: eng/update-screenshots.sh <run id | folder> [picture ...]}"
shift
wanted=("$@")

if [[ "$source" =~ ^[0-9]+$ ]]; then
  command -v gh > /dev/null || { echo "error: downloading run $source needs the GitHub CLI (gh)" >&2; exit 1; }
  download="$(mktemp -d)"
  trap 'rm -rf "$download"' EXIT
  echo "== Downloading the smoke test pictures of run $source"
  (cd "$repo" && gh run download "$source" --pattern 'smoke-test-*' --dir "$download")
  source="$download"
fi

[ -d "$source" ] || { echo "error: $source is neither a run id nor a folder" >&2; exit 1; }
source="$(cd "$source" && pwd)"

# The folder holding one runner's pictures: smoke-test-<OS> in a run's download, else the results of a local run on
# this OS. The pictures are in user-data/logs (eng/smoke-test.sh copies only some of them up a level).
this_os() {
  case "$(uname -s)" in
    Linux) echo Linux ;;
    Darwin) echo macOS ;;
    *) echo Windows ;;
  esac
}
pictures_of() {
  local os="$1"
  if [ -d "$source/smoke-test-$os" ]; then
    echo "$source/smoke-test-$os/user-data/logs"
  elif ! compgen -G "$source/smoke-test-*/" > /dev/null && [ "$os" = "$(this_os)" ]; then
    echo "$source/user-data/logs"
  fi
}

# A PNG's width and height, from its IHDR chunk (bytes 16 to 23, big-endian).
png_size() {
  local b
  read -r -a b <<< "$(od -An -tu1 -j16 -N8 "$1")"
  echo "$(((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]))x$(((b[4] << 24) | (b[5] << 16) | (b[6] << 8) | b[7]))"
}

copied=0 missing=0 skipped=0 found_wanted=()
while read -r picture os from; do
  [[ -z "$picture" || "$picture" == \#* ]] && continue
  if ((${#wanted[@]})); then
    [[ " ${wanted[*]} " == *" $picture "* ]] || continue
    found_wanted+=("$picture")
  fi

  dir="$(pictures_of "$os")"
  if [ -z "$dir" ]; then
    echo "  skipped  $picture: no $os pictures here"
    skipped=$((skipped + 1))
    continue
  fi

  if [ ! -f "$dir/$from" ]; then
    echo "  missing  $picture: $os has no $from (did its step run?)" >&2
    missing=$((missing + 1))
    continue
  fi

  cp "$dir/$from" "$target/$picture"
  size="$(png_size "$target/$picture")"
  if [ "$size" = 1280x800 ]; then
    echo "  copied   $picture from $os: $from"
  else
    echo "  warning  $picture from $os: $from is $size, not the site's 1280x800" >&2
  fi
  copied=$((copied + 1))
done < "$manifest"

for picture in "${wanted[@]}"; do
  [[ " ${found_wanted[*]} " == *" $picture "* ]] || { echo "error: $picture isn't in website/screenshots.txt" >&2; missing=$((missing + 1)); }
done

echo "== $copied copied, $skipped skipped, $missing missing"
((missing == 0))
