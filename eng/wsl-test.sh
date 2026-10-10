#!/usr/bin/env bash
# Builds and tests the current working tree (uncommitted changes included) on Linux under WSL.
# Run from Windows:  wsl -d Ubuntu -- bash eng/wsl-test.sh [--smoke] [dotnet test arguments...]
# It mirrors the repository into the Linux filesystem (~/daedalus-studio by default, DAEDALUS_WSL_DIR overrides) so the
# Linux build never shares bin/ and obj/ with the Windows one, then runs eng/validate.sh there (same arguments).
set -euo pipefail

src="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dst="${DAEDALUS_WSL_DIR:-$HOME/daedalus-studio}"

# WSL appends the Windows PATH; keep Windows' dotnet.exe and git.exe out of the Linux build.
export PATH="$(printf '%s' "$PATH" | tr ':' '\n' | grep -v '^/mnt/' | paste -sd:)"

echo "== sync $src -> $dst"
mkdir -p "$dst"
# A git worktree made on Windows names its git directory with a Windows path (gitdir: D:/…): translate it for git here.
src_git=(git -C "$src")
if [ -f "$src/.git" ]; then
  gitdir="$(sed -n 's/^gitdir: //p' "$src/.git" | tr -d '\r')"
  if [[ "$gitdir" =~ ^[A-Za-z]: ]]; then
    src_git=(git --git-dir="$(wslpath -u "$gitdir")" --work-tree="$src" -C "$src")
  fi
fi
# Tracked and untracked-but-not-ignored files, exactly what git would see; deleted files disappear from the mirror.
"${src_git[@]}" ls-files -z --cached --others --exclude-standard \
  | rsync -a --delete-missing-args --from0 --files-from=- "$src/" "$dst/"
# Drop mirror files that no longer exist in the source tree (keeps bin/, obj/, .work/ and artifacts/).
(cd "$dst" && comm -23 \
  <(find . -type f -not -path './.git/*' -not -path '*/bin/*' -not -path '*/obj/*' -not -path './.work/*' \
      -not -path './artifacts/*' -not -path './samples/Large100k/*' -not -path './samples/LargeMarkup100k/*' -not -path './samples/Solution50/*' -printf '%P\n' | sort) \
  <("${src_git[@]}" ls-files --cached --others --exclude-standard | sort) \
  | while IFS= read -r f; do rm -f -- "$f"; done)
[ -d "$dst/.git" ] || git -C "$dst" init -q   # some tests expect to run inside a git work tree

# A Chromium browser for the browser tests, and with --smoke a display (WSLg's, or a local Xvfb when it isn't
# running), without root: eng/wsl-display.sh.
display=""
[[ " $* " == *" --smoke "* ]] && display="--display"
# shellcheck source=wsl-display.sh
source "$dst/eng/wsl-display.sh" $display --browser

exec bash "$dst/eng/validate.sh" "$@"
