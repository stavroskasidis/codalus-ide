#!/usr/bin/env bash
# Tests the AUR package from eng/aur.sh as Arch users install it, in an Arch Linux container (x86_64): makepkg builds
# it from the PKGBUILD (its sources from the local release files instead of the download URLs, checksums verified),
# pacman installs it, daedalus runs, pacman removes it and nothing is left. It also checks that the .SRCINFO we write
# matches makepkg --printsrcinfo.
# Usage: eng/test-aur.sh <the aur.sh output folder> <folder with the release's linux-x64 .tar.gz>
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
aur="$(cd "${1:?usage: eng/test-aur.sh <aur folder> <packages>}" && pwd)"
packages="$(cd "${2:?}" && pwd)"
runtime="$(command -v podman || command -v docker)" || { echo "error: no container runtime (podman or docker)" >&2; exit 1; }
version="$(sed -n 's/^pkgver=//p' "$aur/PKGBUILD")"

echo "== makepkg and pacman (daedalus-studio-bin $version) in Arch Linux"
"$runtime" run --rm -v "$aur:/aur:ro" -v "$packages:/packages:ro" -v "$repo:/repo:ro" docker.io/library/archlinux:latest bash -euc "
  pacman -Syu --noconfirm --needed --quiet base-devel > /dev/null
  useradd -m builder
  install -d -o builder /build
  cp /aur/PKGBUILD /aur/.SRCINFO /build/
  # The sources under the names the PKGBUILD gives them: makepkg uses them instead of downloading, and checks them.
  cp /packages/daedalus-studio-$version-linux-x64.tar.gz /build/
  cp /repo/eng/packaging/linux/daedalus-studio.desktop /build/daedalus-studio.desktop
  cp /repo/assets/logo/png/daedalus-256.png /build/daedalus-studio.png
  cp /repo/LICENSE /build/LICENSE
  chown -R builder /build
  cd /build
  su builder -c 'makepkg --printsrcinfo' > /tmp/srcinfo
  diff -u /build/.SRCINFO /tmp/srcinfo || { echo 'error: .SRCINFO differs from makepkg --printsrcinfo' >&2; exit 1; }
  su builder -c 'makepkg --noconfirm --nodeps' > /tmp/makepkg.log 2>&1 || { cat /tmp/makepkg.log; exit 1; }
  pacman -U --noconfirm --quiet /build/daedalus-studio-bin-$version-1-x86_64.pkg.tar.zst > /tmp/pacman.log 2>&1 || { cat /tmp/pacman.log; exit 1; }
  daedalus --version
  test -f /usr/share/applications/daedalus-studio.desktop && test -f /usr/share/licenses/daedalus-studio-bin/LICENSE
  pacman -R --noconfirm --quiet daedalus-studio-bin > /dev/null
  test ! -e /opt/daedalus-studio && test ! -e /usr/bin/daedalus"
echo "== daedalus-studio-bin builds, installs, runs and uninstalls"
