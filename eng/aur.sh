#!/usr/bin/env bash
# Writes the AUR package of a stable release (daedalus-studio-bin): PKGBUILD and .SRCINFO from
# eng/packaging/arch/PKGBUILD, with the checksums of the release's Linux .tar.gz files and of the files it takes from
# the tag. The AUR's pkgver can't hold a '-', so prereleases (nightlies) have none.
# Usage: eng/aur.sh <version> <folder with the linux-x64 and linux-arm64 .tar.gz> <out folder>
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
version="${1:?usage: eng/aur.sh <version> <packages> <out>}" packages="${2:?}" out="${3:?}"
case "$version" in *-*) echo "error: $version is a prerelease; the AUR package is for stable releases" >&2; exit 2 ;; esac

sha() { (sha256sum "$1" 2> /dev/null || shasum -a 256 "$1") | cut -d' ' -f1 | tr -d '\\'; }
x64="$packages/daedalus-studio-$version-linux-x64.tar.gz"
arm64="$packages/daedalus-studio-$version-linux-arm64.tar.gz"
for file in "$x64" "$arm64"; do [ -f "$file" ] || { echo "error: $file is missing" >&2; exit 1; }; done

mkdir -p "$out"
sed -e "s/{{VERSION}}/$version/g" \
  -e "s/{{SHA256_X64}}/$(sha "$x64")/" -e "s/{{SHA256_ARM64}}/$(sha "$arm64")/" \
  -e "s/{{SHA256_DESKTOP}}/$(sha "$repo/eng/packaging/linux/daedalus-studio.desktop")/" \
  -e "s/{{SHA256_ICON}}/$(sha "$repo/assets/logo/png/daedalus-256.png")/" \
  -e "s/{{SHA256_LICENSE}}/$(sha "$repo/LICENSE")/" \
  "$repo/eng/packaging/arch/PKGBUILD" > "$out/PKGBUILD"

# .SRCINFO as makepkg --printsrcinfo writes it (the AUR reads it; this way no Arch machine is needed to publish).
field() { sed -n "s/^$1=//p" "$out/PKGBUILD" | head -n 1 | tr -d "'"; }
list() { sed -n "s/^$1=(\(.*\))$/\1/p" "$out/PKGBUILD" | head -n 1 | tr -d "'" | tr ' ' '\n' | sed '/^$/d'; }
raw="https://raw.githubusercontent.com/stavroskasidis/daedalus-studio/v$version"
url="$(field url)"
{
  echo "pkgbase = daedalus-studio-bin"
  printf '\tpkgdesc = %s\n' "$(field pkgdesc)"
  printf '\tpkgver = %s\n\tpkgrel = 1\n\turl = %s\n' "$version" "$url"
  printf '\tarch = x86_64\n\tarch = aarch64\n\tlicense = MIT\n'
  for d in $(list depends); do printf '\tdepends = %s\n' "$d"; done
  printf '\toptdepends = dotnet-sdk: open, build and run solutions (required for any solution)\n'
  printf '\tprovides = daedalus-studio\n\tconflicts = daedalus-studio\n\toptions = !strip\n\toptions = !debug\n'
  printf '\tsource = daedalus-studio.desktop::%s/eng/packaging/linux/daedalus-studio.desktop\n' "$raw"
  printf '\tsource = daedalus-studio.png::%s/assets/logo/png/daedalus-256.png\n' "$raw"
  printf '\tsource = LICENSE::%s/LICENSE\n' "$raw"
  printf '\tsha256sums = %s\n' "$(sha "$repo/eng/packaging/linux/daedalus-studio.desktop")" "$(sha "$repo/assets/logo/png/daedalus-256.png")" "$(sha "$repo/LICENSE")"
  printf '\tsource_x86_64 = %s/releases/download/v%s/daedalus-studio-%s-linux-x64.tar.gz\n' "$url" "$version" "$version"
  printf '\tsha256sums_x86_64 = %s\n' "$(sha "$x64")"
  printf '\tsource_aarch64 = %s/releases/download/v%s/daedalus-studio-%s-linux-arm64.tar.gz\n' "$url" "$version" "$version"
  printf '\tsha256sums_aarch64 = %s\n' "$(sha "$arm64")"
  printf '\npkgname = daedalus-studio-bin\n'
} > "$out/.SRCINFO"
echo "== AUR package daedalus-studio-bin $version in $out"
