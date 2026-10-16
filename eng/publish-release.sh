#!/usr/bin/env bash
# Publishes a GitHub release from the packages the workflows' build jobs uploaded (M31, ADR 0028): every platform's
# installers and Velopack update files, one SHA256SUMS.txt, the winget manifests (stable), and notes generated from the
# commits since the previous release of the same kind. Nightlies are prereleases; a stable release becomes the latest.
# Usage: eng/publish-release.sh stable|nightly <version> <commit> <downloaded artifacts folder>
# Needs: gh (GH_TOKEN), the .NET SDK, zip. Prints tag=<tag> to $GITHUB_OUTPUT when it is set.
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
kind="${1:?usage: eng/publish-release.sh stable|nightly <version> <commit> <artifacts>}"
version="${2:?}" ref="${3:?}" downloaded="${4:?}"
tag="v$version"
work="$(mktemp -d)"
assets="$work/assets"
mkdir -p "$assets"

# Every platform's files in one folder: the names carry the platform (or the channel) and don't collide.
find "$downloaded" -type f ! -path '*/winget-*' ! -name 'SHA256SUMS-*' -exec cp {} "$assets/" \;
cat "$downloaded"/*/SHA256SUMS-*.txt | sort -k2 > "$assets/SHA256SUMS.txt"
if [ "$kind" = stable ] && winget="$(ls -d "$downloaded"/*/winget-* 2> /dev/null | head -n 1)" && [ -n "$winget" ]; then
  (cd "$winget" && zip -q "$assets/winget-manifests-$version.zip" ./*.yaml)
fi
# The AUR package (PKGBUILD and .SRCINFO) of a stable release with both Linux builds: pipeline.yml pushes it to the
# AUR, and Arch users can build it from the release page meanwhile.
if [ "$kind" = stable ] && [ -f "$assets/codalus-$version-linux-x64.tar.gz" ] && [ -f "$assets/codalus-$version-linux-arm64.tar.gz" ]; then
  bash "$repo/eng/aur.sh" "$version" "$assets" "$work/aur"
  (cd "$work/aur" && zip -q "$assets/aur-codalus-bin-$version.zip" PKGBUILD .SRCINFO)
fi

dotnet run --project "$repo/eng/Codalus.Release" -- notes "$kind" "$version" --repo "${GITHUB_REPOSITORY:-stavroskasidis/codalus-ide}" --to "$ref" > "$work/notes.md"
# The workflow tagged the commit when it started (see pipeline.yml): the release goes on that tag.
flags=(--verify-tag --title "Codalus IDE $version" --notes-file "$work/notes.md")
if [ "$kind" = nightly ]; then flags+=(--prerelease); else flags+=(--latest); fi

echo "== Publishing $tag ($kind) from $ref with $(find "$assets" -type f | wc -l) assets"
gh release create "$tag" "${flags[@]}" "$assets"/*
echo "tag=$tag" >> "${GITHUB_OUTPUT:-/dev/null}"
