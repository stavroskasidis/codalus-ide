#!/usr/bin/env bash
# Installs x64 .NET runtimes into .work/dotnet-x64 on an Apple silicon Mac, through the official dotnet-install script:
# .NET Core 3.1 and .NET 5, which have no Arm64 build there and always run as x64 under Rosetta 2, and the x64 builds of
# .NET 6, 8, 10 and the version the repository builds with. A program started by that x64 dotnet runs under Rosetta 2,
# and the Codalus debugger's osx-x64 bundle debugs it (M58b, ADR 0094). DebuggerBundleTests need them with that bundle
# (eng/get-codalus-debugger.sh osx-x64); they skip without either locally and fail without them in CI. On any other
# machine this does nothing. The versions are pinned, as eng/get-runtimes.sh pins the matrix's.
#
# Usage: eng/get-dotnet-x64.sh [destination]      (default: .work/dotnet-x64)
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
destination="${1:-$repo/.work/dotnet-x64}"

if [ "$(uname -s)" != Darwin ] || [ "$(uname -m)" != arm64 ]; then
  echo "The x64 .NET runtimes beside the Arm64 ones are for Apple silicon: nothing to do on $(uname -s) $(uname -m)."
  exit 0
fi

# The runtime of the SDK the repository builds with (the fixtures' own target), then the older ones.
major="$(sed -n 's/.*"version": *"\([0-9]*\)\..*/\1/p' "$repo/global.json" | head -1)"
current="$(dotnet --list-runtimes | sed -n "s/^Microsoft\.NETCore\.App \($major\.[^ ]*\) .*/\1/p" | sort -t. -k1,1n -k2,2n -k3,3n | tail -1)"
if [ -z "$current" ]; then
  echo "error: no Microsoft.NETCore.App $major runtime is installed (dotnet --list-runtimes)." >&2
  exit 1
fi
versions=("$current" 10.0.13 8.0.31 6.0.36 5.0.17 3.1.32)

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
for version in "${versions[@]}"; do
  if [ ! -d "$destination/shared/Microsoft.NETCore.App/$version" ]; then
    [ -f "$work/dotnet-install.sh" ] || curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$work/dotnet-install.sh"
    # The newest goes first and its muxer (dotnet) stays: the older ones add only their runtime.
    skip=()
    [ -x "$destination/dotnet" ] && skip=(--skip-non-versioned-files)
    bash "$work/dotnet-install.sh" --install-dir "$destination" --no-path --runtime dotnet --architecture x64 --version "$version" ${skip[@]+"${skip[@]}"}
  fi
done

# It runs: an x64 process, so this is also where a Mac without Rosetta 2 says so.
if ! "$destination/dotnet" --list-runtimes; then
  echo "error: $destination/dotnet doesn't run. x64 programs need Rosetta 2 (softwareupdate --install-rosetta)." >&2
  exit 1
fi
echo "x64 .NET runtimes -> $destination"
