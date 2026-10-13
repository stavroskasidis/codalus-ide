#!/usr/bin/env bash
# Tests a site from eng/linux-repos.sh the way users use it, with signatures checked: in an Ubuntu container apt trusts
# the site's key, updates (InRelease) and installs codalus; in Fedora and openSUSE containers dnf and zypper do
# the same with gpgcheck and repo_gpgcheck on. The site is read through file: URLs, so no web server is needed. Then
# each removes it.
# Usage: eng/test-linux-repos.sh <site> [stable|nightly]
set -euo pipefail

site="$(cd "${1:?usage: eng/test-linux-repos.sh <site> [stable|nightly]}" && pwd)"
channel="${2:-stable}"
runtime="$(command -v podman || command -v docker)" || { echo "error: no container runtime (podman or docker)" >&2; exit 1; }
removed='test ! -e /opt/codalus && test ! -e /usr/bin/codalus'

echo "== apt ($channel) in Ubuntu 24.04"
"$runtime" run --rm -v "$site:/site:ro" docker.io/library/ubuntu:24.04 bash -euc "
  apt-get update -qq && apt-get install -y -qq gpg > /dev/null
  gpg --dearmor -o /usr/share/keyrings/codalus.gpg < /site/codalus-packages.asc
  echo 'deb [signed-by=/usr/share/keyrings/codalus.gpg] file:/site/apt $channel main' > /etc/apt/sources.list.d/codalus.list
  apt-get update -qq 2>&1 | grep -v '^W: .*sandbox' || true
  apt-get install -y -qq codalus > /dev/null
  codalus --version
  apt-get remove --purge -y -qq codalus > /dev/null
  $removed"

echo "== dnf ($channel) in Fedora 42"
"$runtime" run --rm -v "$site:/site:ro" docker.io/library/fedora:42 bash -euc "
  sed -e 's|^baseurl=.*|baseurl=file:///site/rpm/$channel|' -e 's|^gpgkey=.*|gpgkey=file:///site/codalus-packages.asc|' \
    /site/rpm/codalus-$channel.repo > /etc/yum.repos.d/codalus.repo
  dnf install -y -q codalus > /tmp/dnf.log 2>&1 || { cat /tmp/dnf.log; exit 1; }
  codalus --version
  dnf remove -y -q codalus > /tmp/dnf.log 2>&1 || { cat /tmp/dnf.log; exit 1; }
  $removed"
echo "== zypper ($channel) in openSUSE Tumbleweed"
# The dnf repository and its .repo file serve zypper as they are; the key is trusted first, as the website's download page says.
"$runtime" run --rm -v "$site:/site:ro" registry.opensuse.org/opensuse/tumbleweed:latest bash -euc "
  rpm --import /site/codalus-packages.asc
  sed -e 's|^baseurl=.*|baseurl=file:///site/rpm/$channel|' -e 's|^gpgkey=.*|gpgkey=file:///site/codalus-packages.asc|' \
    /site/rpm/codalus-$channel.repo > /etc/zypp/repos.d/codalus.repo
  zypper --non-interactive --quiet install codalus > /tmp/zypper.log 2>&1 || { cat /tmp/zypper.log; exit 1; }
  codalus --version
  zypper --non-interactive --quiet remove codalus > /tmp/zypper.log 2>&1 || { cat /tmp/zypper.log; exit 1; }
  $removed"
echo "== The $channel repositories install and remove codalus"
