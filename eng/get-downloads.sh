#!/usr/bin/env bash
# Fetches the archives the IDE downloads on demand (Node.js, vscode-js-debug) for this machine into a folder, checked
# against the checksums pinned in the sources (read from them here, so they stay in one place), for CI's downloads cache
# (CODALUS_DOWNLOAD_CACHE, .github/actions/setup): the tests and the smoke test then don't download them, and a slow
# nodejs.org doesn't fail them (the macOS smoke test, run 40632085595, and the npm tests, run 40637907916).
# Usage: eng/get-downloads.sh [folder]   (default: $CODALUS_DOWNLOAD_CACHE)
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
folder="${1:-${CODALUS_DOWNLOAD_CACHE:?a folder, or CODALUS_DOWNLOAD_CACHE}}"
mkdir -p "$folder"
node_source="$repo/src/Codalus.Workbench/Node/NodeRuntime.cs"
jsdebug_source="$repo/src/Codalus.Workbench/Node/JsDebug.cs"

# NodeRuntime.Platform's names.
case "$(uname -s)-$(uname -m)" in
  MINGW*-x86_64 | MSYS*-x86_64 | CYGWIN*-x86_64) platform="win-x64.zip" ;;
  MINGW*-aarch64 | MSYS*-aarch64 | MINGW*-arm64 | MSYS*-arm64) platform="win-arm64.zip" ;;
  Linux-x86_64) platform="linux-x64.tar.gz" ;;
  Linux-aarch64) platform="linux-arm64.tar.gz" ;;
  Darwin-x86_64) platform="darwin-x64.tar.gz" ;;
  Darwin-arm64) platform="darwin-arm64.tar.gz" ;;
  *) echo "No Node.js build for $(uname -s) $(uname -m)."; exit 0 ;;
esac

const() { sed -n "s/.*const string $2 = \"\([^\"]*\)\";.*/\1/p" "$1" | head -n 1; }
node_version="$(const "$node_source" Version)"
node_sha="$(sed -n "s/.*\[\"$platform\"\] = \"\([0-9a-f]*\)\".*/\1/p" "$node_source" | head -n 1)"
jsdebug_version="$(const "$jsdebug_source" Version)"
jsdebug_sha="$(const "$jsdebug_source" Sha256)"
for value in node_version node_sha jsdebug_version jsdebug_sha; do
  [ -n "${!value}" ] || { echo "::error::$value wasn't found in the sources (eng/get-downloads.sh reads them)."; exit 1; }
done

sha256() { if command -v sha256sum > /dev/null; then sha256sum "$1"; else shasum -a 256 "$1"; fi | cut -c1-64; }

# A file already there with its checksum stays; otherwise it's fetched, resumed when it stalls (no 10 KB/s for a
# minute), drops or takes over 10 minutes, and checked.
fetch() {
  local url="$1" sha="$2" file="$folder/${1##*/}"
  if [ -f "$file" ] && [ "$(sha256 "$file")" = "$sha" ]; then
    echo "$file: already there."
    return
  fi
  rm -f "$file"
  local attempt
  for attempt in 1 2 3 4 5; do
    if curl -fL --silent --show-error --speed-limit 10000 --speed-time 60 --max-time 600 -C - -o "$file.partial" "$url"; then
      break
    fi
    echo "$url: attempt $attempt stopped at $(wc -c < "$file.partial" 2> /dev/null || echo 0) bytes."
  done
  if [ "$(sha256 "$file.partial")" != "$sha" ]; then
    rm -f "$file.partial"
    echo "::error::$url doesn't match its pinned checksum, or didn't finish."
    exit 1
  fi
  mv "$file.partial" "$file"
  echo "$file: fetched."
}

fetch "https://nodejs.org/dist/v$node_version/node-v$node_version-$platform" "$node_sha"
fetch "https://github.com/microsoft/vscode-js-debug/releases/download/v$jsdebug_version/js-debug-dap-v$jsdebug_version.tar.gz" "$jsdebug_sha"
