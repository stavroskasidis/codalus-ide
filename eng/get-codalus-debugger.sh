#!/usr/bin/env bash
# Builds the Codalus debugger's bundle for a platform into .work/codalus-debugger-<rid> (M58b, ADR 0094): the debug
# companion host (codalus-debughost, whose --clr-dap mode is the debugger) published self-contained and trimmed, so a
# remote target needs only the program's own runtime. A published install has the Linux bundles next to the shell
# (remote/<rid>/debugger/, built by Codalus.Shell's publish); this is for a source checkout: remote debugging there, and
# the tests of it (RemoteTests), which skip without the bundle locally and fail without it in CI.
#
# Usage: eng/get-codalus-debugger.sh [rid…]     default: this machine's; any of linux-x64, linux-arm64, osx-arm64, and
#        the two a package offers as components: osx-x64 (x64 programs on Apple silicon) and win-x86 (32-bit .NET
#        Framework programs). A bundle builds on any OS.
# It builds in .work/debugger-build (its own obj and bin: the solution's aren't touched), and does nothing for a bundle
# that is already built from the same sources (bundle.id).
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo"

host_rid() {
  local os arch
  case "$(uname -s)" in Linux) os=linux ;; Darwin) os=osx ;; *) echo "unsupported OS: $(uname -s)" >&2; exit 2 ;; esac
  case "$(uname -m)" in x86_64 | amd64) arch=x64 ;; aarch64 | arm64) arch=arm64 ;; *) echo "unsupported architecture: $(uname -m)" >&2; exit 2 ;; esac
  echo "$os-$arch"
}

[ "$#" -gt 0 ] || set -- "$(host_rid)"
version="$(tr -d '[:space:]' < VERSION)"
# The sources the bundle is built from: another commit or an uncommitted change to them (its files, and what changed in
# them, new files too) is another bundle.
paths=(src/Codalus.Debugging.Host src/Codalus.Debugging src/Codalus.Debugging.StartupHook src/Codalus.Rpc src/ThirdParty Directory.Packages.props)
sources="$(git rev-parse HEAD 2> /dev/null || echo none)$(git status --porcelain -- "${paths[@]}" 2> /dev/null || true)$(git diff HEAD -- "${paths[@]}" 2> /dev/null | sha256sum | cut -c1-16)$(git ls-files -o --exclude-standard -z -- "${paths[@]}" 2> /dev/null | xargs -0 cat 2> /dev/null | sha256sum | cut -c1-16)"
id="$version-$(printf '%s' "$sources" | sha256sum | cut -c1-12)"

for rid in "$@"; do
  out="$repo/.work/codalus-debugger-$rid"
  if { [ -x "$out/codalus-debughost" ] || [ -f "$out/codalus-debughost.exe" ]; } && [ "$(cat "$out/bundle.id" 2> /dev/null)" = "$id" ]; then
    echo "codalus-debugger $id ($rid) -> $out (up to date)"
    continue
  fi

  rm -rf "$out.tmp"
  dotnet publish src/Codalus.Debugging.Host -c Release -r "$rid" -p:CodalusDebuggerBundle=true \
    --artifacts-path "$repo/.work/debugger-build" -o "$out.tmp" -nologo -v quiet
  printf '%s\n' "$id" > "$out.tmp/bundle.id"
  rm -rf "$out"
  mv "$out.tmp" "$out"
  echo "codalus-debugger $id ($rid) -> $out ($(du -sm "$out" | cut -f1) MB)"
done
