#!/usr/bin/env bash
# Installs the x64 .NET runtime into .work/dotnet-x64 on an Apple silicon Mac, through the official dotnet-install
# script: the same version as the Arm64 runtime the fixtures run on. A program started by that x64 dotnet runs under
# Rosetta 2, as the programs of .NET 5 and earlier do there, and the Codalus debugger's osx-x64 bundle debugs it (M58b,
# ADR 0094). X64DebuggerBundleTests need it with that bundle (eng/get-codalus-debugger.sh osx-x64); they skip without
# either locally and fail without them in CI. On any other machine it does nothing.
#
# Usage: eng/get-dotnet-x64.sh [destination]      (default: .work/dotnet-x64)
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
destination="${1:-$repo/.work/dotnet-x64}"

if [ "$(uname -s)" != Darwin ] || [ "$(uname -m)" != arm64 ]; then
  echo "The x64 .NET runtime beside the Arm64 one is for Apple silicon: nothing to do on $(uname -s) $(uname -m)."
  exit 0
fi

# The runtime of the SDK the repository builds with: the fixtures target its major version.
major="$(sed -n 's/.*"version": *"\([0-9]*\)\..*/\1/p' "$repo/global.json" | head -1)"
version="$(dotnet --list-runtimes | sed -n "s/^Microsoft\.NETCore\.App \($major\.[^ ]*\) .*/\1/p" | sort -t. -k1,1n -k2,2n -k3,3n | tail -1)"
if [ -z "$version" ]; then
  echo "error: no Microsoft.NETCore.App $major runtime is installed (dotnet --list-runtimes)." >&2
  exit 1
fi

if [ ! -d "$destination/shared/Microsoft.NETCore.App/$version" ]; then
  work="$(mktemp -d)"
  trap 'rm -rf "$work"' EXIT
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$work/dotnet-install.sh"
  bash "$work/dotnet-install.sh" --install-dir "$destination" --no-path --runtime dotnet --architecture x64 --version "$version"
fi

# It runs: an x64 process, so this is also where a Mac without Rosetta 2 says so.
if ! "$destination/dotnet" --list-runtimes; then
  echo "error: $destination/dotnet doesn't run. x64 programs need Rosetta 2 (softwareupdate --install-rosetta)." >&2
  exit 1
fi
echo "x64 .NET runtime $version -> $destination"
