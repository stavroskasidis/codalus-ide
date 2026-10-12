#!/usr/bin/env bash
# Copies the shell's file-type icons from Material Icon Theme (Material Extensions, MIT; D24, M34a), the npm package
# material-icon-theme, into src/DaedalusStudio.Shell/Icons/Material/, after checking the package's SHA-256.
# The list is src/DaedalusStudio.Shell/Icons/material-icons.txt; the build compiles the copied SVGs into Avalonia
# geometries with their colors (eng/icons/CompileIcons.cs). Run it after adding a name to the list, and commit the SVGs.
# Usage: eng/get-material-icons.sh
set -euo pipefail

version="5.39.0"
sha="73bd2c6bfeea79ec1f7122445b7ff808c46e8b30097a11ae779ff683d7ad1ba4"
repo="$(cd "$(dirname "$0")/.." && pwd)"
icons="$repo/src/DaedalusStudio.Shell/Icons"
work="$repo/.work/material-icons-$version"

mkdir -p "$work"
archive="$work/material-icon-theme-$version.tgz"
if [ ! -f "$archive" ]; then
  curl -fsSL -o "$archive.part" "https://registry.npmjs.org/material-icon-theme/-/material-icon-theme-$version.tgz"
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
rm -rf "$icons/Material"
mkdir -p "$icons/Material"
missing=0
while IFS= read -r name; do
  name="${name%%#*}"; name="$(echo "$name" | tr -d '[:space:]')"
  [ -z "$name" ] && continue
  if [ -f "$work/package/icons/$name.svg" ]; then
    cp "$work/package/icons/$name.svg" "$icons/Material/$name.svg"
  else
    echo "Not in material-icon-theme $version: $name" >&2
    missing=1
  fi
done < "$icons/material-icons.txt"

echo "Copied $(ls "$icons/Material" | wc -l | tr -d ' ') icons from material-icon-theme $version."
exit $missing
