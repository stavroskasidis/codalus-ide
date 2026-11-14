#!/usr/bin/env bash
# Builds one of the Codalus debugger's bundles that a package offers as an on-demand component (M58b, ADR 0094), for
# eng/package.sh: osx-x64 for the macOS package (x64 programs on Apple silicon, which an x64 debugger debugs under
# Rosetta 2) and win-x86 for the Windows one (32-bit .NET Framework programs). The bundle is codalus-debughost with its
# own .NET runtime, published for <rid> (it builds on any OS).
#
# Usage: eng/package-debugger-component.sh <rid> <version> <app folder> <output folder> [stage folder]
# Writes <output folder>/codalus-debugger-<version>-<rid>.tar.gz (one top folder, codalus-debugger/), a release asset
# next to the packages, and adds it to <app folder>/debughost/debugger-components.json with its size and SHA-256: the
# installed app downloads the asset from the release of its own version and refuses one with another checksum
# (DebuggerComponents). Prints the asset's name.
set -euo pipefail

rid="${1:?usage: eng/package-debugger-component.sh <rid> <version> <app folder> <output folder> [stage folder]}"
version="${2:?}" app="${3:?}" out="${4:?}"
repo="$(cd "$(dirname "$0")/.." && pwd)"
stage="${5:-$repo/artifacts/package-stage/debugger-$rid}"
asset="codalus-debugger-$version-$rid.tar.gz"
sha256() { (sha256sum "$1" 2> /dev/null || shasum -a 256 "$1") | cut -d' ' -f1 | tr -d '\\'; }

rm -rf "$stage"
mkdir -p "$stage/bundle" "$out" "$app/debughost"
# Its own obj and bin (--artifacts-path): the shell's publish of the same project for this machine isn't touched.
dotnet publish "$repo/src/Codalus.Debugging.Host" -c Release -r "$rid" -p:CodalusDebuggerBundle=true -p:Version="$version" \
  --artifacts-path "$stage/build" -o "$stage/bundle/codalus-debugger" -nologo -v quiet >&2
find "$stage/bundle" -name '*.pdb' -delete
printf '%s\n' "$version" > "$stage/bundle/codalus-debugger/bundle.id"
# Relative paths: Git Bash's tar and Windows' own both take them.
(cd "$stage/bundle" && tar -czf "$asset" codalus-debugger)
mv "$stage/bundle/$asset" "$out/$asset"

size="$(wc -c < "$out/$asset" | tr -d '[:space:]')"
url="https://github.com/${GITHUB_REPOSITORY:-stavroskasidis/codalus-ide}/releases/download/v$version/$asset"
entry="{\"platform\":\"$rid\",\"asset\":\"$asset\",\"sha256\":\"$(sha256 "$out/$asset")\",\"size\":$size,\"url\":\"$url\"}"
index="$app/debughost/debugger-components.json"
if [ -s "$index" ]; then
  # Another bundle is listed already: this one joins the array.
  sed -e '$ s/]$//' "$index" > "$index.tmp"
  printf ',%s]\n' "$entry" >> "$index.tmp"
  mv "$index.tmp" "$index"
else
  printf '[%s]\n' "$entry" > "$index"
fi
echo "$asset"
