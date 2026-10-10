#!/usr/bin/env bash
# Builds and tests this checkout the way CI does, on Linux or macOS (eng/wsl-test.sh runs it under WSL; on the Mac it
# runs in a clone, see CLAUDE.md). Installs the SDK pinned by global.json into ~/.dotnet when no matching one is on
# the PATH, and fetches netcoredbg so the debugger tests run instead of skipping.
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
band="${sdk%??}"   # 10.0.401 -> 10.0.4 (global.json rolls forward within the feature band and beyond)
if ! dotnet --list-sdks 2>/dev/null | grep -q "^$band"; then
  if [ ! -x "$HOME/.dotnet/dotnet" ] || ! "$HOME/.dotnet/dotnet" --list-sdks | grep -q "^$band"; then
    echo "== install .NET SDK ${band}xx into ~/.dotnet"
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "${TMPDIR:-/tmp}/dotnet-install.sh"
    bash "${TMPDIR:-/tmp}/dotnet-install.sh" --jsonfile global.json --install-dir "$HOME/.dotnet" --no-path
  fi
  export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
fi
# The multi-targeted test fixtures (samples/Testing) also run on .NET 9.
if ! dotnet --list-runtimes | grep -q '^Microsoft.NETCore.App 9\.'; then
  root="$(dirname "$(readlink -f "$(command -v dotnet)")")"
  echo "== install the .NET 9 runtime into $root"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "${TMPDIR:-/tmp}/dotnet-install.sh"
  bash "${TMPDIR:-/tmp}/dotnet-install.sh" --runtime dotnet --channel 9.0 --install-dir "$root" --no-path
fi
# samples/Net11 needs the .NET 11 SDK (M30). It goes next to the SDK in use, so the build host can roll forward to
# its runtime (ADR 0025).
if ! dotnet --list-sdks | grep -q '^11\.0\.'; then
  root="$(dirname "$(readlink -f "$(command -v dotnet)")")"
  echo "== install the .NET 11 SDK (samples/Net11/global.json) into $root"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "${TMPDIR:-/tmp}/dotnet-install.sh"
  bash "${TMPDIR:-/tmp}/dotnet-install.sh" --jsonfile samples/Net11/global.json --install-dir "$root" --no-path
fi
echo "== $(uname -sm), .NET SDK $(dotnet --version), $(git --version)"

echo "== netcoredbg"
bash eng/get-netcoredbg.sh
export DAEDALUS_NETCOREDBG="$repo/.work/netcoredbg"

echo "== restore / build / test"
dotnet restore DaedalusStudio.slnx
dotnet build DaedalusStudio.slnx -c Release --no-restore
dotnet test --solution DaedalusStudio.slnx -c Release --no-build "$@"

[ "$smoke" = yes ] || exit 0
# Native AOT links with the selected Xcode's clang and ld; use that Xcode's SDK too. Otherwise clang may pick a newer
# Command Line Tools SDK whose .tbd files that ld can't read ("tapi error: … unknown architecture").
if [ "$(uname -s)" = Darwin ] && [ -z "${SDKROOT:-}" ] && command -v xcrun > /dev/null; then
  SDKROOT="$(xcrun --sdk macosx --show-sdk-path 2> /dev/null || true)"
  [ -n "$SDKROOT" ] && export SDKROOT
fi
echo "== Native AOT publish"
rid=$(dotnet --info | awk '/RID:/ {print $2; exit}')
dotnet publish src/DaedalusStudio.Shell -c Release -r "$rid" -o artifacts/shell
echo "== smoke test"
bash eng/smoke-test.sh artifacts/shell/daedalus artifacts/smoke-test
