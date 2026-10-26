#!/usr/bin/env bash
# Builds and tests the current working tree (uncommitted changes included) on Linux under WSL.
# Run from Windows:  wsl -d Ubuntu -- bash eng/wsl-test.sh [--smoke] [dotnet test arguments...]
# Several checkouts at once (git worktrees): give each its own mirror, e.g.
#   wsl -d Ubuntu -- env CODALUS_WSL_DIR=/home/<user>/codalus-m33 bash eng/wsl-test.sh --smoke
# It mirrors the repository into the Linux filesystem (~/codalus by default, CODALUS_WSL_DIR overrides) so the
# Linux build never shares bin/ and obj/ with the Windows one, then runs eng/validate.sh there (same arguments).
set -euo pipefail

src="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dst="${CODALUS_WSL_DIR:-$HOME/codalus}"

# A git worktree made on Windows points at its repository with a Windows path, which Linux git can't follow: the files
# are listed with Windows' git then (WSL runs Windows programs). Found before the Windows PATH is dropped below.
windows_git="$(command -v git.exe || true)"
src_files() {
  if git -C "$src" rev-parse --git-dir > /dev/null 2>&1; then
    git -C "$src" ls-files "$@"
  else
    "$windows_git" -C "$(wslpath -w "$src")" ls-files "$@"
  fi
}

# WSL appends the Windows PATH; keep Windows' dotnet.exe and git.exe out of the Linux build.
export PATH="$(printf '%s' "$PATH" | tr ':' '\n' | grep -v '^/mnt/' | paste -sd:)"

echo "== sync $src -> $dst"
mkdir -p "$dst"
# Tracked and untracked-but-not-ignored files, exactly what git would see; deleted files disappear from the mirror.
src_files -z --cached --others --exclude-standard | rsync -a --delete-missing-args --from0 --files-from=- "$src/" "$dst/"
# Drop mirror files that no longer exist in the source tree (keeps bin/, obj/, .work/ and artifacts/).
(cd "$dst" && comm -23 \
  <(find . -type f -not -path './.git/*' -not -path '*/bin/*' -not -path '*/obj/*' -not -path './.work/*' \
      -not -path './artifacts/*' -not -path './samples/Large100k/*' -not -path './samples/LargeMarkup100k/*' -not -path './samples/Solution50/*' -printf '%P\n' | sort) \
  <(src_files --cached --others --exclude-standard | sort) \
  | while IFS= read -r f; do rm -f -- "$f"; done
  # ...and the folders that left empty (git tracks no empty folder; a renamed one would linger, and tests list folders).
  find . -mindepth 1 -type d -empty -not -path './.git/*' -not -path '*/bin/*' -not -path '*/obj/*' -not -path './.work/*' \
    -not -path './artifacts/*' -delete)
[ -d "$dst/.git" ] || git -C "$dst" init -q   # some tests expect to run inside a git work tree
# ...whose index lists the synced files: ProductNameTests scans `git ls-files`, which an empty index leaves empty.
git -C "$dst" add -A > /dev/null 2>&1 || true

# A Chromium browser for the browser tests, and with --smoke a display (WSLg's, or a local Xvfb when it isn't
# running), without root: eng/wsl-display.sh.
display=""
[[ " $* " == *" --smoke "* ]] && display="--display"
# shellcheck source=wsl-display.sh
source "$dst/eng/wsl-display.sh" $display --browser

exec bash "$dst/eng/validate.sh" "$@"
