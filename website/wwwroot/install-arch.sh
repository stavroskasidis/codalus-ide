#!/usr/bin/env bash
# Installs Codalus IDE on Arch Linux and the distributions based on it (Manjaro, EndeavourOS, CachyOS…), or updates it:
#
#   curl -fsSL https://codalus.dev/install-arch.sh | bash
#
# It downloads the newest stable release's PKGBUILD (aur-codalus-bin-<version>.zip: the package the AUR will carry, which
# eng/aur.sh writes for each release), builds it with makepkg, which downloads the release's .tar.gz and checks its
# SHA-256, and installs it with pacman as codalus-bin: /opt/codalus, `codalus` on the PATH, and the applications menu's
# entry. Run it again to update; `sudo pacman -R codalus-bin` removes it. Run it as yourself, not as root: makepkg
# refuses root, and pacman asks for your password through sudo.
#
# Options (after `bash -s --`):
#   --version <x.y.z>   that release instead of the newest
#   --noconfirm         don't ask before installing (pacman's and makepkg's own --noconfirm)
#   --releases <url>    where the releases are (https://github.com/stavroskasidis/codalus-ide/releases; tests serve a copy)
set -euo pipefail

# Everything is in a function that runs on the last line: a download cut short runs nothing.
main() {
  local releases="https://github.com/stavroskasidis/codalus-ide/releases" version="" noconfirm=()
  while [ $# -gt 0 ]; do
    case "$1" in
      --version) version="${2:?--version needs a version}"; version="${version#v}"; shift 2 ;;
      --noconfirm) noconfirm=(--noconfirm); shift ;;
      --releases) releases="${2:?--releases needs an address}"; releases="${releases%/}"; shift 2 ;;
      -h|--help) echo "usage: install-arch.sh [--version <x.y.z>] [--noconfirm] [--releases <url>]"; return 0 ;;
      *) fail "unknown option $1 (--version, --noconfirm, --releases)" ;;
    esac
  done

  [ "$(id -u)" -ne 0 ] || fail "run this as yourself, not as root: makepkg won't build as root, and pacman asks for your password"
  { command -v pacman && command -v makepkg; } > /dev/null \
    || fail "this is for Arch Linux and the distributions based on it (pacman and makepkg). Other distributions: https://codalus.dev/download/#linux"
  command -v curl > /dev/null || fail "curl isn't installed: sudo pacman -S curl"

  # Piped into bash, the script is bash's standard input, so pacman's and makepkg's questions read the terminal instead
  # (only theirs: bash goes on reading the script from its own).
  local answers=/dev/stdin
  if [ ! -t 0 ]; then
    if (: < /dev/tty) 2> /dev/null; then
      answers=/dev/tty
    elif [ ${#noconfirm[@]} -gt 0 ]; then
      answers=/dev/null
    else
      fail "there's no terminal to ask on: add --noconfirm (curl … | bash -s -- --noconfirm)"
    fi
  fi

  if [ -z "$version" ]; then
    # /releases/latest redirects to the newest stable release's page (…/tag/v0.10.0): no API, no rate limit.
    local latest
    latest="$(curl -fsSLI -o /dev/null -w '%{url_effective}' "$releases/latest")" || fail "couldn't reach $releases"
    version="${latest##*/tag/v}"
    [ -n "$version" ] && [ "$version" != "$latest" ] || fail "couldn't find the newest release at $releases/latest ($latest)"
  fi

  local installed
  installed="$(pacman -Q codalus-bin 2> /dev/null | awk '{print $2}')" || true
  if [ "${installed%-*}" = "$version" ]; then
    say "Codalus IDE $version is installed and up to date."
    return 0
  fi

  command -v sudo > /dev/null || fail "sudo isn't installed: as root, pacman -S sudo, and add yourself to the sudoers"

  # makepkg's own need: fakeroot (part of base-devel). The package is prebuilt, so nothing compiles or strips.
  # pacman says what it installs and asks first.
  if ! command -v fakeroot > /dev/null; then
    say "makepkg needs fakeroot: installing it with pacman."
    # shellcheck disable=SC2024 # the terminal is yours to read, not root's
    sudo pacman -S --needed "${noconfirm[@]}" fakeroot < "$answers"
  fi

  work="$(mktemp -d "${TMPDIR:-/tmp}/codalus-install.XXXXXX")" # not local: the trap runs after main returns
  trap 'rm -rf "$work"' EXIT

  if [ -n "$installed" ]; then say "Updating Codalus IDE from ${installed%-*} to $version"; else say "Installing Codalus IDE $version"; fi
  curl -fSL --progress-bar -o "$work/pkgbuild.zip" "$releases/download/v$version/aur-codalus-bin-$version.zip" \
    || fail "couldn't download the package of $version ($releases/download/v$version/aur-codalus-bin-$version.zip)"
  mkdir "$work/build"
  bsdtar -xf "$work/pkgbuild.zip" -C "$work/build" # libarchive's bsdtar: pacman needs it, so it's always there
  (cd "$work/build" && makepkg --syncdeps --install --needed --clean "${noconfirm[@]}" < "$answers")

  say "Codalus IDE $version is installed: open it from the applications menu, or run codalus."
  say "To update it, run this command again. To remove it: sudo pacman -R codalus-bin"
}

say() { printf '\033[1m==> %s\033[0m\n' "$*"; }
fail() { printf 'error: %s\n' "$*" >&2; exit 1; }

main "$@"
