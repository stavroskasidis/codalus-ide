#!/usr/bin/env bash
# Copies the shell's Fluent UI System Icons (Microsoft, MIT; D24, M34a) from the pinned npm package
# @fluentui/svg-icons into src/Codalus.Shell/Icons/Fluent/, after checking the package's SHA-256.
# The list is src/Codalus.Shell/Icons/fluent-icons.txt; the build compiles the copied SVGs into Avalonia
# geometries (eng/icons/CompileIcons.cs). Run it after adding a name to the list, and commit the SVGs.
# Usage: eng/get-fluent-icons.sh
set -euo pipefail

version="1.1.344"
sha="6fe61f489a6d0d065ec2182794fdadf0e15db7d3388997c5ba8039dcfaaa35e8"
repo="$(cd "$(dirname "$0")/.." && pwd)"
icons="$repo/src/Codalus.Shell/Icons"
work="$repo/.work/fluent-icons-$version"

mkdir -p "$work"
archive="$work/svg-icons-$version.tgz"
if [ ! -f "$archive" ]; then
  curl -fsSL -o "$archive.part" "https://registry.npmjs.org/@fluentui/svg-icons/-/svg-icons-$version.tgz"
  mv "$archive.part" "$archive"
fi

if command -v sha256sum >/dev/null; then actual="$(sha256sum "$archive" | cut -d' ' -f1)"; else actual="$(shasum -a 256 "$archive" | cut -d' ' -f1)"; fi
if [ "$actual" != "$sha" ]; then
  echo "SHA-256 mismatch for $archive: $actual" >&2
  rm -f "$archive"
  exit 1
fi

rm -rf "$work/package"
tar -xzf "$archive" -C "$work"
rm -rf "$icons/Fluent"
mkdir -p "$icons/Fluent"
missing=0
while IFS= read -r name; do
  name="${name%%#*}"; name="$(echo "$name" | tr -d '[:space:]')"
  [ -z "$name" ] && continue
  if [ -f "$work/package/icons/$name.svg" ]; then
    cp "$work/package/icons/$name.svg" "$icons/Fluent/$name.svg"
  else
    echo "Not in @fluentui/svg-icons $version: $name" >&2
    missing=1
  fi
done < "$icons/fluent-icons.txt"

echo "Copied $(ls "$icons/Fluent" | wc -l | tr -d ' ') icons from @fluentui/svg-icons $version."
exit $missing
