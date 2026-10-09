#!/usr/bin/env bash
# Downloads appimagetool and the AppImage type2 runtime (both MIT; ADR 0009), verifies their SHA-256 and puts them in
# .work/appimagetool. Prints the directory. eng/package.sh calls it on Linux when appimagetool is not on the PATH.
# Usage: eng/get-appimagetool.sh [destination]
set -euo pipefail

tool_version="1.9.1"
runtime_version="20251108"
destination="${1:-$(cd "$(dirname "$0")/.." && pwd)/.work/appimagetool}"

case "$(uname -s)-$(uname -m)" in
  Linux-x86_64)
    arch="x86_64"
    tool_sha="ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0"
    runtime_sha="2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d" ;;
  *)
    echo "No pinned appimagetool for $(uname -s) $(uname -m)." >&2
    exit 2 ;;
esac

stamp="$tool_version-$runtime_version"
if [ -f "$destination/.version" ] && [ "$(cat "$destination/.version")" = "$stamp" ]; then
  echo "$destination"
  exit 0
fi

fetch() {
  local url="$1" file="$2" sha="$3"
  curl -sSfL -o "$file" "$url"
  local actual
  actual="$(sha256sum "$file" | cut -d' ' -f1)"
  if [ "$actual" != "$sha" ]; then
    echo "SHA-256 mismatch for $url: expected $sha, got $actual" >&2
    exit 1
  fi
}

mkdir -p "$destination"
fetch "https://github.com/AppImage/appimagetool/releases/download/$tool_version/appimagetool-$arch.AppImage" "$destination/appimagetool" "$tool_sha"
fetch "https://github.com/AppImage/type2-runtime/releases/download/$runtime_version/runtime-$arch" "$destination/runtime-$arch" "$runtime_sha"
chmod +x "$destination/appimagetool"
echo "$stamp" > "$destination/.version"
echo "$destination"
