#!/usr/bin/env bash
# Downloads netcoredbg (Samsung, MIT; ADR 0007), verifies its SHA-256 and unpacks it.
# Usage: eng/get-netcoredbg.sh [--x64 | --platform linux-x64|linux-arm64|osx-arm64|win-x64] [destination]
#   (default: this machine's platform, into .work/netcoredbg)
# The debugger ends up in <destination>/netcoredbg[.exe]; point CODALUS_NETCOREDBG at it, or copy the folder to
# debugger/ next to the shell. Keep the version and checksums in step with RemoteDebuggers in the workbench.
#
# --x64 (Apple silicon only, M33, ADR 0030): the x64 build, which debugs the programs of .NET 5 and earlier that run as
# x64 under Rosetta 2. netcoredbg 3.2.0 has no macOS x64 build, so it is the last one (3.1.3-1062). Point
# CODALUS_NETCOREDBG_X64 at it, or copy it to debugger/x64/ next to the shell. On other machines the regular
# debugger already matches the programs' architecture, and --x64 does nothing.
#
# --platform (M14c, ADR 0039): another platform's build, into .work/netcoredbg-<platform>, which remote debugging
# copies to a container, a WSL distribution or an SSH host in a source checkout.
set -euo pipefail

x64=no
platform=""
if [ "${1:-}" = --x64 ]; then x64=yes; shift; fi
if [ "${1:-}" = --platform ]; then platform="$2"; shift 2; fi

version="3.2.0-1092"
repo="$(cd "$(dirname "$0")/.." && pwd)"
destination="${1:-$repo/.work/netcoredbg}"

if [ "$x64" = yes ]; then
  if [ "$(uname -s)-$(uname -m)" != Darwin-arm64 ]; then
    echo "--x64 is for Apple silicon; this machine's netcoredbg already debugs its programs."
    exit 0
  fi

  version="3.1.3-1062"
  destination="${1:-$repo/.work/netcoredbg-x64}"
  asset="netcoredbg-osx-amd64.tar.gz"; sha="49459b066836b6a452f418501d7ecab57bcd7e60d8464faac21ff70b496b8634"
else
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
  else
    destination="${1:-$repo/.work/netcoredbg-$platform}"
  fi

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
fi

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
