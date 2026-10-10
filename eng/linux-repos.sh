#!/usr/bin/env bash
# Builds the apt and dnf repositories that GitHub Pages serves (M31, D18, ADR 0028): the newest .deb and .rpm of each
# channel, signed with the project's package key. Users of these packages update with the system (apt or dnf upgrade).
#
# Usage: eng/linux-repos.sh --out <site> --base-url <url> [--stable <dir>] [--nightly <dir>]
#   <dir>      a folder with that channel's .deb and .rpm packages (x64 and arm64)
#   <site>     the folder to write (what Pages publishes): apt/, rpm/, the .repo files, the public key and index.html
# Environment:
#   PACKAGES_GPG_PRIVATE_KEY  the armored private key (a repository secret); without it, a throwaway key signs (tests)
# Needs: gpg, apt-ftparchive (apt-utils), createrepo_c, rpmsign (rpm).
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
out="" base_url="" stable="" nightly=""
while [ $# -gt 0 ]; do
  case "$1" in
    --out) out="$2"; shift 2 ;;
    --base-url) base_url="${2%/}"; shift 2 ;;
    --stable) stable="$2"; shift 2 ;;
    --nightly) nightly="$2"; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
[ -n "$out" ] && [ -n "$base_url" ] || { echo "usage: eng/linux-repos.sh --out <site> --base-url <url> [--stable <dir>] [--nightly <dir>]" >&2; exit 2; }

GNUPGHOME="$(mktemp -d)"
export GNUPGHOME
trap 'gpgconf --kill gpg-agent 2> /dev/null || true; rm -rf "$GNUPGHOME"' EXIT
if [ -n "${PACKAGES_GPG_PRIVATE_KEY:-}" ]; then
  printf '%s\n' "$PACKAGES_GPG_PRIVATE_KEY" | gpg --batch --quiet --import
else
  echo "warning: PACKAGES_GPG_PRIVATE_KEY isn't set: a throwaway key signs this site (for tests only)" >&2
  gpg --batch --quiet --pinentry-mode loopback --passphrase '' --quick-gen-key "Daedalus Studio test packages" rsa3072 sign 1d
fi
fingerprint="$(gpg --list-secret-keys --with-colons | awk -F: '/^fpr/ {print $10; exit}')"
gpg_sign=(gpg --batch --yes --pinentry-mode loopback --passphrase '' --local-user "$fingerprint")

rm -rf "$out"
mkdir -p "$out"
gpg --armor --export "$fingerprint" > "$out/daedalus-studio-packages.asc"

for channel in stable nightly; do
  packages="$([ "$channel" = stable ] && echo "$stable" || echo "$nightly")"
  [ -n "$packages" ] || continue

  echo "== apt: $channel"
  apt="$out/apt"
  mkdir -p "$apt/pool/$channel"
  # In the pool under Debian's name_version_arch.deb: apt-ftparchive --arch picks packages by that file name.
  for file in "$packages"/*.deb; do
    [ -e "$file" ] || continue
    cp "$file" "$apt/pool/$channel/$(dpkg-deb -f "$file" Package)_$(dpkg-deb -f "$file" Version)_$(dpkg-deb -f "$file" Architecture).deb"
  done
  for arch in amd64 arm64; do
    dir="$apt/dists/$channel/main/binary-$arch"
    mkdir -p "$dir"
    (cd "$apt" && apt-ftparchive --arch "$arch" packages "pool/$channel") > "$dir/Packages"
    gzip -9kn "$dir/Packages"
  done
  (cd "$apt" && apt-ftparchive \
    -o APT::FTPArchive::Release::Origin="Daedalus Studio" -o APT::FTPArchive::Release::Label="Daedalus Studio" \
    -o APT::FTPArchive::Release::Suite="$channel" -o APT::FTPArchive::Release::Codename="$channel" \
    -o APT::FTPArchive::Release::Components=main -o APT::FTPArchive::Release::Architectures="amd64 arm64" \
    release "dists/$channel") > "$apt/dists/$channel/Release.tmp"
  mv "$apt/dists/$channel/Release.tmp" "$apt/dists/$channel/Release"
  "${gpg_sign[@]}" --clearsign -o "$apt/dists/$channel/InRelease" "$apt/dists/$channel/Release"
  "${gpg_sign[@]}" --armor --detach-sign -o "$apt/dists/$channel/Release.gpg" "$apt/dists/$channel/Release"

  echo "== dnf: $channel"
  rpm="$out/rpm/$channel"
  mkdir -p "$rpm"
  cp "$packages"/*.rpm "$rpm/" 2> /dev/null || true
  for file in "$rpm"/*.rpm; do
    [ -e "$file" ] || continue
    # rpm's default gpg is gpg2, which Debian and Ubuntu don't have.
    rpmsign --addsign --define "_gpg_name $fingerprint" --define "_gpg_path $GNUPGHOME" --define "__gpg $(command -v gpg)" \
      --define "_gpg_sign_cmd_extra_args --batch --pinentry-mode loopback --passphrase ''" "$file" > /dev/null
  done
  createrepo_c --quiet "$rpm"
  "${gpg_sign[@]}" --armor --detach-sign -o "$rpm/repodata/repomd.xml.asc" "$rpm/repodata/repomd.xml"
  cat > "$out/rpm/daedalus-studio-$channel.repo" << EOF
[daedalus-studio-$channel]
name=Daedalus Studio ($channel)
baseurl=$base_url/rpm/$channel
enabled=1
gpgcheck=1
repo_gpgcheck=1
gpgkey=$base_url/daedalus-studio-packages.asc
EOF
done

sed -e "s|{{BASE_URL}}|$base_url|g" "$repo/eng/packaging/linux/repos-index.html" > "$out/index.html"
touch "$out/.nojekyll" # Pages serves the files as they are
echo "== Site in $out (signed by $fingerprint)"
find "$out" -maxdepth 3 -type d | sort
