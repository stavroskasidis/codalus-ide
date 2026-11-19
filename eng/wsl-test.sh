#!/usr/bin/env bash
# Builds and tests the current working tree (uncommitted changes included) on Linux under WSL.
# Run from Windows:  wsl -d Ubuntu -- bash eng/wsl-test.sh [--smoke] [dotnet test arguments...]
# Several checkouts at once (git worktrees): give each its own mirror, e.g.
#   wsl -d Ubuntu -- env CODALUS_WSL_DIR=/home/<user>/codalus-m33 bash eng/wsl-test.sh --smoke
# It mirrors the repository into the Linux filesystem (~/codalus by default, CODALUS_WSL_DIR overrides) so the
# Linux build never shares bin/ and obj/ with the Windows one, then runs eng/validate.sh there (same arguments).
# A built mirror is about 7.5 GB, and the distro's disk (ext4.vhdx) is a file on a Windows drive that grows with each
# one: on 2026-11-18, 15 mirrors left by worktrees filled C:, and the distro's disk went read-only in the middle of a
# run. So a worktree's own mirror (CODALUS_WSL_DIR) is removed when its run passes (kept when it fails, for the
# investigation), and the run won't start with less than 20 GB free on that drive. Space freed inside the distro goes
# back to Windows only from a sparse disk (`wsl --manage Ubuntu --set-sparse true`).
set -euo pipefail
. "$(dirname "${BASH_SOURCE[0]}")/telemetry-optout.sh" # the tools' telemetry opt-outs (M59)

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

# The free space of the Windows drive that holds this distro's disk (its BasePath in the registry), before the run can
# grow it. Read through Windows' PowerShell and fsutil, so before the Windows PATH is dropped below.
host_disk_check() {
  local min_gb=20 prefix='\\?\' base drive avail sparse
  base="$(powershell.exe -NoProfile -NonInteractive -Command \
    "(Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Lxss' | Get-ItemProperty | Where-Object DistributionName -eq '${WSL_DISTRO_NAME:-}').BasePath" \
    2> /dev/null | tr -d '\r')" || true
  base="${base#"$prefix"}"   # \\?\C:\... -> C:\...
  drive="$(printf '%s' "${base:0:1}" | tr 'A-Z' 'a-z')"
  # Not found (no PowerShell, another WSL layout): nothing to check against.
  { [ "${base:1:1}" = ":" ] && [ -d "/mnt/$drive" ]; } || return 0
  avail="$(df -BG --output=avail "/mnt/$drive" 2> /dev/null | tail -1 | tr -dc '0-9')" || true
  [ -n "$avail" ] || return 0
  sparse="$(fsutil.exe sparse queryflag "$base\\ext4.vhdx" 2> /dev/null | tr -d '\r')" || true
  case "$sparse" in
    *"NOT set"*) echo "warning: $base\\ext4.vhdx isn't sparse, so space freed in the distro stays taken on ${drive^^}:" \
      "(wsl --manage ${WSL_DISTRO_NAME:-Ubuntu} --set-sparse true)" >&2 ;;
  esac
  echo "== ${drive^^}: has $avail GB free (this distro's disk is in $base)"
  if [ "$avail" -lt "$min_gb" ]; then
    echo "error: ${drive^^}: has $avail GB free, under the $min_gb GB a run needs; this distro's disk is in $base." >&2
    echo "The mirrors in $HOME (a worktree's own is removed when its run passes; remove the ones no longer needed):" >&2
    du -sh "$HOME"/codalus* 2> /dev/null | sort -rh >&2 || true
    exit 1
  fi
}
host_disk_check

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

status=0
bash "$dst/eng/validate.sh" "$@" || status=$?
# A worktree's own mirror goes once its run passes; a failed run's stays for the investigation.
if [ "$status" -eq 0 ] && [ "$dst" != "$HOME/codalus" ] && [ "$dst" != "$HOME" ] && [ "$dst" != "/" ]; then
  echo "== passed: removing $dst (this worktree's own mirror, CODALUS_WSL_DIR)"
  rm -rf -- "$dst"
fi
exit "$status"
