#!/usr/bin/env bash
# Builds and tests this checkout the way CI does, on Linux or macOS (eng/wsl-test.sh runs it under WSL; on the Mac it
# runs in a clone, see CLAUDE.md). Installs the SDK pinned by global.json into ~/.dotnet when no matching one is on
# the PATH (the .NET 11 release candidate since M30b), and the .NET 10 SDK next to it for the fixtures that still
# target net10.0, and fetches netcoredbg so the debugger tests run instead of skipping.
#
# Usage: eng/validate.sh [--smoke] [dotnet test arguments...]
#   --smoke  also publishes the Native AOT shell and runs eng/smoke-test.sh against it (needs clang and, on Linux,
#            zlib1g-dev; needs a display or xvfb-run, and on macOS a logged-in desktop session)
# Results of the smoke test land in artifacts/smoke-test.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo"

smoke=no
if [ "${1:-}" = --smoke ]; then smoke=yes; shift; fi

export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 TESTINGPLATFORM_TELEMETRY_OPTOUT=1

sdk="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' global.json | head -1)"
# `dotnet --version` here applies global.json (its version, roll-forward and prerelease rules) and fails without a
# matching SDK.
if ! dotnet --version > /dev/null 2>&1; then
  if [ ! -x "$HOME/.dotnet/dotnet" ] || ! "$HOME/.dotnet/dotnet" --version > /dev/null 2>&1; then
    echo "== install .NET SDK $sdk into ~/.dotnet"
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "${TMPDIR:-/tmp}/dotnet-install.sh"
    bash "${TMPDIR:-/tmp}/dotnet-install.sh" --jsonfile global.json --install-dir "$HOME/.dotnet" --no-path
  fi
  export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
fi
# The fixtures that still target net10.0 (and the net10.0 half of samples/Testing) run on .NET 10 (M30b). Its SDK
# brings both runtimes, Microsoft.NETCore.App and Microsoft.AspNetCore.App, next to the SDK in use.
if ! dotnet --list-runtimes | grep -q '^Microsoft.AspNetCore.App 10\.'; then
  root="$(dirname "$(readlink -f "$(command -v dotnet)")")"
  echo "== install the .NET 10 SDK into $root"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "${TMPDIR:-/tmp}/dotnet-install.sh"
  bash "${TMPDIR:-/tmp}/dotnet-install.sh" --channel 10.0 --install-dir "$root" --no-path
fi
echo "== $(uname -sm), .NET SDK $(dotnet --version), $(git --version)"

echo "== netcoredbg"
bash eng/get-netcoredbg.sh
export CODALUS_NETCOREDBG="$repo/.work/netcoredbg"
# The x64 debugger on Apple silicon, for .NET 5 and earlier under Rosetta 2 (M33; does nothing elsewhere).
bash eng/get-netcoredbg.sh --x64

# The target framework matrix (M33, ADR 0030): .NET Core 2.0 to .NET 10 and older SDKs, pinned, in .work/dotnet-compat.
# The first run downloads about 1.5 GB; Codalus.TargetFrameworks.Tests runs against it.
echo "== target framework matrix"
bash eng/get-runtimes.sh > /dev/null
export CODALUS_DOTNET_COMPAT="$repo/.work/dotnet-compat"

echo "== restore / build / test"
dotnet restore Codalus.slnx
dotnet build Codalus.slnx -c Release --no-restore
dotnet test --solution Codalus.slnx -c Release --no-build "$@"

[ "$smoke" = yes ] || exit 0
# Native AOT links with the selected Xcode's clang and ld; use that Xcode's SDK too. Otherwise clang may pick a newer
# Command Line Tools SDK whose .tbd files that ld can't read ("tapi error: … unknown architecture").
if [ "$(uname -s)" = Darwin ] && [ -z "${SDKROOT:-}" ] && command -v xcrun > /dev/null; then
  SDKROOT="$(xcrun --sdk macosx --show-sdk-path 2> /dev/null || true)"
  [ -n "$SDKROOT" ] && export SDKROOT
fi
echo "== Native AOT publish"
rid=$(dotnet --info | awk '/RID:/ {print $2; exit}')
dotnet publish src/Codalus.Shell -c Release -r "$rid" -o artifacts/shell
echo "== smoke test"
bash eng/smoke-test.sh artifacts/shell/codalus artifacts/smoke-test
