#!/usr/bin/env bash
# Tests a site from eng/linux-repos.sh the way users use it, with signatures checked: in an Ubuntu container apt trusts
# the site's key, updates (InRelease) and installs codalus through the site's mirror list; in Fedora and openSUSE
# containers dnf and zypper do the same with gpgcheck and repo_gpgcheck on. The site holds only metadata, so it must be
# built with file: addresses for the site and the packages, which this mounts read-only at /site and /packages:
#   eng/linux-repos.sh --out <site> --base-url file:///site --packages <packages> --packages-url file:///packages ...
# No web server is needed. Then each removes it.
# Usage: eng/test-linux-repos.sh <site> <packages> [stable|nightly]
set -euo pipefail

usage="usage: eng/test-linux-repos.sh <site> <packages> [stable|nightly]"
site="$(cd "${1:?$usage}" && pwd)"
packages="$(cd "${2:?$usage}" && pwd)"
channel="${3:-stable}"
runtime="$(command -v podman || command -v docker)" || { echo "error: no container runtime (podman or docker)" >&2; exit 1; }
mounts=(-v "$site:/site:ro" -v "$packages:/packages:ro")
removed='test ! -e /opt/codalus && test ! -e /usr/bin/codalus'

echo "== apt ($channel) in Ubuntu 24.04"
"$runtime" run --rm "${mounts[@]}" docker.io/library/ubuntu:24.04 bash -euc "
  apt-get update -qq && apt-get install -y -qq gpg > /dev/null
  gpg --dearmor -o /usr/share/keyrings/codalus.gpg < /site/codalus-packages.asc
  echo 'deb [signed-by=/usr/share/keyrings/codalus.gpg] mirror+file:/site/apt/mirrors.txt $channel main' > /etc/apt/sources.list.d/codalus.list
  apt-get update -qq 2>&1 | grep -v '^W: .*sandbox' || true
  apt-get install -y -qq codalus > /dev/null
  codalus --version
  apt-get remove --purge -y -qq codalus > /dev/null
  $removed"

echo "== dnf ($channel) in Fedora 42"
"$runtime" run --rm "${mounts[@]}" docker.io/library/fedora:42 bash -euc "
  cp /site/rpm/codalus-$channel.repo /etc/yum.repos.d/codalus.repo
  dnf install -y -q codalus > /tmp/dnf.log 2>&1 || { cat /tmp/dnf.log; exit 1; }
  codalus --version
  dnf remove -y -q codalus > /tmp/dnf.log 2>&1 || { cat /tmp/dnf.log; exit 1; }
  $removed"
echo "== zypper ($channel) in openSUSE Tumbleweed"
# The dnf repository and its .repo file serve zypper too, through the .repo file's mirror list (zypper ignores the
# packages' xml:base); the key is trusted first, as the website's download page says.
"$runtime" run --rm "${mounts[@]}" registry.opensuse.org/opensuse/tumbleweed:latest bash -euc "
  rpm --import /site/codalus-packages.asc
  cp /site/rpm/codalus-$channel.repo /etc/zypp/repos.d/codalus.repo
  zypper --non-interactive --quiet install codalus > /tmp/zypper.log 2>&1 || { cat /tmp/zypper.log; exit 1; }
  codalus --version
  zypper --non-interactive --quiet remove codalus > /tmp/zypper.log 2>&1 || { cat /tmp/zypper.log; exit 1; }
  $removed"
echo "== The $channel repositories install and remove codalus"
