#!/usr/bin/env bash
# Installs the .NET runtimes and SDKs of the target framework matrix (M33, ADR 0030) into one dotnet root, through the
# official dotnet-install script. Versions are pinned below: the last patch of each .NET version.
#
# Usage: eng/get-runtimes.sh [destination]      (default: .work/dotnet-compat)
#
# The root holds the SDK the repository builds with (global.json), the runtimes of netcoreapp2.0 to net9.0, the
# ASP.NET Core runtimes of the 2.1 and 3.1 fixtures, and the older SDKs that samples/TargetFrameworks/Sdks pins in
# global.json. The suite in tests/DaedalusStudio.TargetFrameworks.Tests runs against it: point DAEDALUS_DOTNET_COMPAT
# at another root to use that one.
#
# macOS on Apple silicon: .NET 6 and later run natively. Earlier versions have no Arm64 build; their x64 runtimes go
# into <destination>/x64, as the official installers put them into /usr/local/share/dotnet/x64, and run under Rosetta.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
destination="${1:-$repo/.work/dotnet-compat}"

# Microsoft.NETCore.App: net9.0 ... netcoreapp2.0, newest first (net10.0 comes with the SDK).
runtimes=(9.0.20 8.0.31 7.0.20 6.0.36 5.0.17 3.1.32 3.0.3 2.2.8 2.1.30 2.0.9)
# Microsoft.AspNetCore.App: the ASP.NET Core 3.1 and 2.1 fixtures.
aspnetcore=(3.1.32 2.1.30)
# SDKs that samples/TargetFrameworks/Sdks pins in global.json (ADR 0030): the oldest that builds out of process (6.0),
# the newest and oldest MSBuild 16 SDKs (5.0, 3.1), which build in the build host's own node, the 2.1 SDKs down to the
# floor (2.1.202), and the 2.0 SDK below it.
sdks=(6.0.428 5.0.408 3.1.426 2.1.818 2.1.202 2.0.3)
# The SDK the repository builds with: it builds the fixtures.
sdk="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$repo/global.json" | head -1)"

case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*) os=windows ;;
  Darwin) os=macos ;;
  *) os=linux ;;
esac
arch="$(uname -m)"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# dotnet-install.sh on Linux and macOS, dotnet-install.ps1 on Windows. Arguments are the shell script's.
install() {
  local root="$1"; shift
  if [ "$os" = windows ]; then
    [ -f "$work/dotnet-install.ps1" ] || curl -fsSL https://dot.net/v1/dotnet-install.ps1 -o "$work/dotnet-install.ps1"
    local args=() kind="" version="" architecture="" skip=""
    while [ $# -gt 0 ]; do
      case "$1" in
        --runtime) kind="$2"; shift 2 ;;
        --version) version="$2"; shift 2 ;;
        --architecture) architecture="$2"; shift 2 ;;
        --skip-non-versioned-files) skip=yes; shift ;;
        *) shift ;;
      esac
    done
    args=(-Version "$version" -InstallDir "$(cygpath -w "$root")" -NoPath)
    [ -n "$kind" ] && args+=(-Runtime "$kind")
    [ -n "$architecture" ] && args+=(-Architecture "$architecture")
    [ -n "$skip" ] && args+=(-SkipNonVersionedFiles)
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$work/dotnet-install.ps1")" "${args[@]}"
  else
    [ -f "$work/dotnet-install.sh" ] || curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$work/dotnet-install.sh"
    bash "$work/dotnet-install.sh" --install-dir "$root" --no-path "$@"
  fi
}

# Whether <root>/shared/<framework>/<version> (or sdk/<version>) is there already.
have() { [ -d "$1/$2" ]; }

# Where a runtime of a .NET version goes: the x64 folder on Apple silicon below .NET 6.
root_for() {
  local major="${1%%.*}"
  if [ "$os" = macos ] && [ "$arch" = arm64 ] && [ "$major" -lt 6 ]; then
    echo "$destination/x64"
  else
    echo "$destination"
  fi
}

# Keeps the muxer of the main root (the current SDK's); the x64 root gets the muxer of its first install.
skip_for() {
  if [ "$1" = "$destination" ] || [ -x "$1/dotnet" ]; then echo --skip-non-versioned-files; fi
}

architecture_for() {
  if [ "$(root_for "$1")" != "$destination" ]; then echo x64; else echo ""; fi
}

# The current SDK goes first: its muxer (dotnet) is the newest, and everything after it keeps that muxer
# (--skip-non-versioned-files). An old muxer would still load the newest hostfxr, but not everywhere: keep the new one.
mkdir -p "$destination"
if ! have "$destination" "sdk/$sdk"; then
  echo "== .NET SDK $sdk"
  install "$destination" --version "$sdk"
fi

for version in "${runtimes[@]}"; do
  root="$(root_for "$version")"
  if ! have "$root" "shared/Microsoft.NETCore.App/$version"; then
    echo "== .NET runtime $version${root#"$destination"}"
    architecture="$(architecture_for "$version")"
    install "$root" --runtime dotnet --version "$version" ${architecture:+--architecture "$architecture"} $(skip_for "$root")
  fi
done

for version in "${aspnetcore[@]}"; do
  root="$(root_for "$version")"
  if ! have "$root" "shared/Microsoft.AspNetCore.App/$version"; then
    echo "== ASP.NET Core runtime $version${root#"$destination"}"
    architecture="$(architecture_for "$version")"
    install "$root" --runtime aspnetcore --version "$version" ${architecture:+--architecture "$architecture"} $(skip_for "$root")
  fi
done

# The older SDKs go next to the current one, where global.json finds them. On Apple silicon the SDKs before .NET 6
# are x64 programs that the Arm64 dotnet doesn't resolve: they are left out there (ADR 0030).
installed_sdks=()
for version in "${sdks[@]}"; do
  if [ "$(root_for "$version")" != "$destination" ]; then
    echo "== .NET SDK $version: no Arm64 build, left out"
    continue
  fi

  if ! have "$destination" "sdk/$version"; then
    echo "== .NET SDK $version"
    install "$destination" --version "$version" --skip-non-versioned-files
  fi
  installed_sdks+=("$version")
done

# A stamp the tests check, so a half-installed root fails clearly instead of in the middle of a test.
{
  echo "sdk=$sdk"
  echo "runtimes=${runtimes[*]}"
  echo "aspnetcore=${aspnetcore[*]}"
  echo "sdks=${installed_sdks[*]:-}"
} > "$destination/.daedalus-compat"
echo "== $destination"
exe=dotnet; [ "$os" = windows ] && exe=dotnet.exe
"$destination/$exe" --list-sdks
"$destination/$exe" --list-runtimes
if [ -x "$destination/x64/dotnet" ]; then
  echo "== $destination/x64"
  "$destination/x64/dotnet" --list-runtimes
fi
