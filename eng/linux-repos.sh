#!/usr/bin/env bash
# Builds the apt and dnf repositories that GitHub Pages serves (M31, D18, ADR 0028): the newest .deb and .rpm of each
# channel, signed with the project's package key. Users of these packages update with the system (apt or dnf upgrade).
# Pages holds only the signed metadata; the packages are downloaded from their GitHub release (2026-10-18: the two
# channels' packages made the site larger than Pages' 1 GB limit). The dnf metadata names each package's address
# (xml:base); zypper, which ignores that, and apt (apt-transport-mirror, apt 1.6 and later) read through mirror lists
# that send the metadata to the site and the packages to the releases.
#
# Usage: eng/linux-repos.sh --out <site> --base-url <url> --packages <dir> --packages-url <url>
#                           [--stable <tag>] [--nightly <tag>]
#   <site>          the folder to write (what Pages publishes): apt/, rpm/, the .repo files, the public key and
#                   packages.txt (the release each channel serves, which the pipeline keeps from being deleted). The
#                   website (website/, exported with --export) goes into the same folder afterwards; its download page
#                   has the instructions.
#   --base-url      the site's address
#   --packages      a folder with a subfolder per release, <tag>/, holding its .deb and .rpm (x64 and arm64) under
#                   their release asset names
#   --packages-url  where <tag>/<file> is downloaded from: https://github.com/<owner>/<repo>/releases/download, or
#                   file:///packages for eng/test-linux-repos.sh
#   <tag>           the release whose packages a channel serves
# Environment:
#   PACKAGES_GPG_PRIVATE_KEY  the armored private key (a repository secret), which also signed the .rpm files
#                             (eng/package.sh); without it, a throwaway key signs, and signs the .rpm files in place
#                             (tests)
# Needs: gpg, apt-ftparchive (apt-utils), createrepo_c, rpmsign and rpmkeys (rpm).
set -euo pipefail

out="" base_url="" packages="" packages_url="" stable="" nightly=""
while [ $# -gt 0 ]; do
  case "$1" in
    --out) out="$2"; shift 2 ;;
    --base-url) base_url="${2%/}"; shift 2 ;;
    --packages) packages="$2"; shift 2 ;;
    --packages-url) packages_url="${2%/}"; shift 2 ;;
    --stable) stable="$2"; shift 2 ;;
    --nightly) nightly="$2"; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
[ -n "$out" ] && [ -n "$base_url" ] && [ -n "$packages" ] && [ -n "$packages_url" ] || {
  echo "usage: eng/linux-repos.sh --out <site> --base-url <url> --packages <dir> --packages-url <url> [--stable <tag>] [--nightly <tag>]" >&2
  exit 2
}
packages="$(cd "$packages" && pwd)"

GNUPGHOME="$(mktemp -d)"
export GNUPGHOME
trap 'gpgconf --kill gpg-agent 2> /dev/null || true; rm -rf "$GNUPGHOME"' EXIT
throwaway=false
if [ -n "${PACKAGES_GPG_PRIVATE_KEY:-}" ]; then
  printf '%s\n' "$PACKAGES_GPG_PRIVATE_KEY" | gpg --batch --quiet --import
else
  echo "warning: PACKAGES_GPG_PRIVATE_KEY isn't set: a throwaway key signs this site and the .rpm files (for tests only)" >&2
  gpg --batch --quiet --pinentry-mode loopback --passphrase '' --quick-gen-key "Codalus IDE test packages" rsa3072 sign 1d
  throwaway=true
fi
fingerprint="$(gpg --list-secret-keys --with-colons | awk -F: '/^fpr/ {print $10; exit}')"
gpg_sign=(gpg --batch --yes --pinentry-mode loopback --passphrase '' --local-user "$fingerprint")

rm -rf "$out"
mkdir -p "$out"
gpg --armor --export "$fingerprint" > "$out/codalus-packages.asc"
# The packages' own signatures are checked against the key, as dnf does with gpgcheck.
rpmdb="$GNUPGHOME/rpmdb"
mkdir -p "$rpmdb"
rpmkeys --dbpath "$rpmdb" --import "$out/codalus-packages.asc"
: > "$out/packages.txt"

for channel in stable nightly; do
  tag="$([ "$channel" = stable ] && echo "$stable" || echo "$nightly")"
  [ -n "$tag" ] || continue
  [ -d "$packages/$tag" ] || { echo "error: no $packages/$tag for the $channel channel" >&2; exit 1; }
  echo "$channel $tag" >> "$out/packages.txt"

  echo "== apt: $channel ($tag)"
  apt="$out/apt"
  # Filename is <tag>/<file>: a path under the packages' address, which the mirror list below sends to the releases.
  # apt-ftparchive --arch picks packages by Debian's file name (name_version_arch.deb), which release assets don't
  # have, so the architecture is picked from each entry's Architecture field instead.
  all="$(cd "$packages" && apt-ftparchive packages "$tag")"
  for arch in amd64 arm64; do
    dir="$apt/dists/$channel/main/binary-$arch"
    mkdir -p "$dir"
    awk -v arch="$arch" 'BEGIN { RS = ""; ORS = "\n\n" } $0 ~ ("(^|\n)Architecture: (" arch "|all)(\n|$)")' <<< "$all" > "$dir/Packages"
    gzip -9kn "$dir/Packages"
  done
  (cd "$apt" && apt-ftparchive \
    -o APT::FTPArchive::Release::Origin="Codalus IDE" -o APT::FTPArchive::Release::Label="Codalus IDE" \
    -o APT::FTPArchive::Release::Suite="$channel" -o APT::FTPArchive::Release::Codename="$channel" \
    -o APT::FTPArchive::Release::Components=main -o APT::FTPArchive::Release::Architectures="amd64 arm64" \
    release "dists/$channel") > "$apt/dists/$channel/Release.tmp"
  mv "$apt/dists/$channel/Release.tmp" "$apt/dists/$channel/Release"
  "${gpg_sign[@]}" --clearsign -o "$apt/dists/$channel/InRelease" "$apt/dists/$channel/Release"
  "${gpg_sign[@]}" --armor --detach-sign -o "$apt/dists/$channel/Release.gpg" "$apt/dists/$channel/Release"

  echo "== dnf: $channel ($tag)"
  rpm="$out/rpm/$channel"
  mkdir -p "$rpm"
  list="$GNUPGHOME/$channel.rpms"
  : > "$list"
  for file in "$packages/$tag"/*.rpm; do
    [ -e "$file" ] || continue
    if ! rpmkeys --dbpath "$rpmdb" --checksig "$file" | grep -q 'signatures OK'; then
      if ! $throwaway; then
        echo "error: $file isn't signed with the package key (eng/package.sh signs it with CODALUS_PACKAGES_KEY)" >&2
        exit 1
      fi
      # rpm's default gpg is gpg2, which Debian and Ubuntu don't have.
      rpmsign --addsign --define "_gpg_name $fingerprint" --define "_gpg_path $GNUPGHOME" --define "__gpg $(command -v gpg)" \
        --define "_gpg_sign_cmd_extra_args --batch --pinentry-mode loopback --passphrase ''" "$file" > /dev/null
    fi
    echo "$tag/$(basename "$file")" >> "$list"
  done
  # Each package's location is <tag>/<file> under xml:base, the packages' address; only repodata/ goes on the site.
  createrepo_c --quiet --baseurl "$packages_url/" --pkglist "$list" --outputdir "$rpm" "$packages"
  "${gpg_sign[@]}" --armor --detach-sign -o "$rpm/repodata/repomd.xml.asc" "$rpm/repodata/repomd.xml"
  # zypper ignores xml:base (libzypp 17.38), so it reads the repository through this mirror list instead: each file
  # from the first address that has it, the metadata from the site and the packages from the releases. dnf takes the
  # baseurl, so a .repo file from before the mirror list (2026-10-18) keeps working.
  printf '%s/rpm/%s/\n%s/\n' "$base_url" "$channel" "$packages_url" > "$rpm/mirrors.txt"
  cat > "$out/rpm/codalus-$channel.repo" << EOF
[codalus-$channel]
name=Codalus IDE ($channel)
baseurl=$base_url/rpm/$channel
mirrorlist=$base_url/rpm/$channel/mirrors.txt
enabled=1
gpgcheck=1
repo_gpgcheck=1
gpgkey=$base_url/codalus-packages.asc
EOF
done

if [ -d "$out/apt" ]; then
  # apt reads the index files from the site and the .deb files from the releases (apt-transport-mirror's type tag).
  printf '%s/apt/\tpriority:1 type:index\n%s/\ttype:deb\n' "$base_url" "$packages_url" > "$out/apt/mirrors.txt"
fi

touch "$out/.nojekyll" # Pages serves the files as they are
echo "== Site in $out (signed by $fingerprint)"
find "$out" -maxdepth 3 -type d | sort
