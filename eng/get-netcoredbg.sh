#!/usr/bin/env bash
# Downloads netcoredbg (Samsung, MIT; ADR 0007), verifies its SHA-256 and unpacks it.
# Usage: eng/get-netcoredbg.sh [--platform linux-x64|linux-arm64|osx-arm64|win-x64] [destination]
#   (default: this machine's platform, into .work/netcoredbg; another platform goes to .work/netcoredbg-<platform>,
#   which remote debugging (M14c) copies to a container, a WSL distribution or an SSH host in a source checkout)
# The debugger ends up in <destination>/netcoredbg[.exe]; point DAEDALUS_NETCOREDBG at it, or copy the folder to
# debugger/ next to the shell. Keep the version and checksums in step with RemoteDebuggers in the workbench.
set -euo pipefail

version="3.2.0-1092"
platform=""
if [ "${1:-}" = "--platform" ]; then
  platform="$2"
  shift 2
fi

if [ -z "$platform" ]; then
  case "$(uname -s)-$(uname -m)" in
    MINGW*-x86_64 | MSYS*-x86_64 | CYGWIN*-x86_64) platform="win-x64" ;;
    Linux-x86_64) platform="linux-x64" ;;
    Linux-aarch64) platform="linux-arm64" ;;
    Darwin-arm64) platform="osx-arm64" ;;
    *)
      echo "netcoredbg $version has no build for $(uname -s) $(uname -m)." >&2
      exit 2 ;;
  esac
  default_destination="$(cd "$(dirname "$0")/.." && pwd)/.work/netcoredbg"
else
  default_destination="$(cd "$(dirname "$0")/.." && pwd)/.work/netcoredbg-$platform"
fi

destination="${1:-$default_destination}"

case "$platform" in
  win-x64)
    asset="netcoredbg-win64.zip"; sha="3c410a45fa502415203a94fcb88654af65bf8e3dac158a5527a722e7a6b9274a" ;;
  linux-x64)
    asset="netcoredbg-linux-amd64.tar.gz"; sha="080eb3b2d2152465f599d3b33d1ee6e747794e11cc0a3773ec689f5e5f2c5afa" ;;
  linux-arm64)
    asset="netcoredbg-linux-arm64.tar.gz"; sha="065ff49badec8a695dbea2de6ab6a330c774a191e426a217ab8cc05250627ccb" ;;
  osx-arm64)
    asset="netcoredbg-osx-arm64.zip"; sha="f4fa33b3ff874910cc184b4bb3b9c56d0abdf5c6521cee0b144d7c6e4a6e59ea" ;;
  *)
    echo "netcoredbg $version has no build for $platform." >&2
    exit 2 ;;
esac

if [ -f "$destination/.version" ] && [ "$(cat "$destination/.version")" = "$version" ]; then
  echo "netcoredbg $version is already in $destination"
  exit 0
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
curl -sSfL -o "$work/$asset" "https://github.com/Samsung/netcoredbg/releases/download/$version/$asset"
actual="$( (sha256sum "$work/$asset" 2>/dev/null || shasum -a 256 "$work/$asset") | cut -d' ' -f1)"
if [ "$actual" != "$sha" ]; then
  echo "SHA-256 mismatch for $asset: expected $sha, got $actual" >&2
  exit 1
fi

mkdir -p "$work/unpacked"
case "$asset" in
  *.zip) unzip -q "$work/$asset" -d "$work/unpacked" ;;
  *) tar -xzf "$work/$asset" -C "$work/unpacked" ;;
esac

rm -rf "$destination"
mkdir -p "$(dirname "$destination")"
mv "$work/unpacked/netcoredbg" "$destination"
echo "$version" > "$destination/.version"
echo "netcoredbg $version -> $destination"
