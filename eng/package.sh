#!/usr/bin/env bash
# Builds a release package for this machine's OS (ADR 0009):
#   Windows: a portable .zip (and a winget manifest for it)
#   macOS:   Daedalus Studio.app in a .dmg (signed when DAEDALUS_CODESIGN_IDENTITY is set)
#   Linux:   a .tar.gz, a .deb (when dpkg-deb is available) and an AppImage (appimagetool from the PATH, or the pinned
#            one from eng/get-appimagetool.sh on x64)
# Usage: eng/package.sh [version]   (default: 0.1.0-dev)
# Output: artifacts/packages/
# Prerequisites: the .NET SDK (Native AOT toolchain), curl, zip/unzip; vswhere on PATH on Windows.
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
version="${1:-0.1.0-dev}"
rid="$(dotnet --info | awk '/RID:/ {print $2; exit}')"
out="$repo/artifacts/packages"
stage="$repo/artifacts/package-stage/$rid"
app="$stage/daedalus-studio"
name="daedalus-studio-$version-$rid"

rm -rf "$stage"
mkdir -p "$app" "$out"

echo "== Publishing the shell ($rid, $version)"
dotnet publish "$repo/src/DaedalusStudio.Shell" -c Release -r "$rid" -o "$app" -p:Version="$version" -p:DebugType=none -p:DebugSymbols=false
# Symbols stay out of packages (they are large); CI keeps them as a separate artifact.
find "$app" -name '*.pdb' -delete

echo "== Debugger (netcoredbg)"
if "$repo/eng/get-netcoredbg.sh" "$stage/netcoredbg"; then
  cp -R "$stage/netcoredbg" "$app/debugger"
else
  echo "netcoredbg is not available for $rid; the package has no debugger." >&2
fi

cp "$repo/LICENSE" "$repo/THIRD-PARTY-NOTICES.md" "$repo/README.md" "$app/"

case "$rid" in
  win-*)
    echo "== Windows: zip"
    rm -f "$out/$name.zip"
    # Windows PowerShell's Compress-Archive writes backslash separators; zip or Windows' bsdtar write portable archives.
    if command -v zip > /dev/null; then
      (cd "$stage" && zip -qr "$out/$name.zip" daedalus-studio)
    else
      (cd "$stage" && "${SYSTEMROOT:-C:/Windows}/System32/tar.exe" -a -cf "$(cygpath -w "$out/$name.zip")" daedalus-studio)
    fi
    sha="$( (sha256sum "$out/$name.zip" 2>/dev/null || shasum -a 256 "$out/$name.zip") | cut -d' ' -f1)"
    manifests="$out/winget-$version"
    mkdir -p "$manifests"
    for template in "$repo/eng/packaging/winget/"*.yaml; do
      sed -e "s/{{VERSION}}/$version/g" -e "s/{{SHA256}}/$sha/g" -e "s/{{FILE}}/$name.zip/g" "$template" > "$manifests/$(basename "$template")"
    done
    ;;
  osx-*)
    echo "== macOS: app bundle and dmg"
    bundle="$stage/Daedalus Studio.app"
    mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
    cp -R "$app/." "$bundle/Contents/MacOS/"
    sed -e "s/{{VERSION}}/$version/g" "$repo/eng/packaging/macos/Info.plist" > "$bundle/Contents/Info.plist"
    iconset="$stage/daedalus.iconset"
    mkdir -p "$iconset"
    for size in 16 32 128 256 512; do
      cp "$repo/assets/logo/png/daedalus-$size.png" "$iconset/icon_${size}x${size}.png"
    done
    cp "$repo/assets/logo/png/daedalus-1024.png" "$iconset/icon_512x512@2x.png"
    iconutil -c icns "$iconset" -o "$bundle/Contents/Resources/daedalus.icns"
    if [ -n "${DAEDALUS_CODESIGN_IDENTITY:-}" ]; then
      codesign --force --deep --options runtime --timestamp --sign "$DAEDALUS_CODESIGN_IDENTITY" "$bundle"
    else
      echo "DAEDALUS_CODESIGN_IDENTITY is not set: the app is not signed (Gatekeeper will warn)." >&2
    fi
    rm -f "$out/$name.dmg"
    hdiutil create -volname "Daedalus Studio" -srcfolder "$bundle" -ov -format UDZO "$out/$name.dmg"
    ;;
  linux-*)
    echo "== Linux: tar.gz"
    tar -C "$stage" -czf "$out/$name.tar.gz" daedalus-studio
    arch="${rid#linux-}"; [ "$arch" = "x64" ] && debarch=amd64 || debarch="$arch"
    if command -v dpkg-deb > /dev/null; then
      echo "== Linux: deb"
      deb="$stage/deb"
      mkdir -p "$deb/DEBIAN" "$deb/opt" "$deb/usr/bin" "$deb/usr/share/applications" "$deb/usr/share/icons/hicolor/256x256/apps"
      cp -R "$app" "$deb/opt/daedalus-studio"
      ln -s /opt/daedalus-studio/daedalus "$deb/usr/bin/daedalus"
      cp "$repo/eng/packaging/linux/daedalus-studio.desktop" "$deb/usr/share/applications/"
      cp "$repo/assets/logo/png/daedalus-256.png" "$deb/usr/share/icons/hicolor/256x256/apps/daedalus-studio.png"
      sed -e "s/{{VERSION}}/${version//-/\~}/g" -e "s/{{ARCH}}/$debarch/g" "$repo/eng/packaging/linux/control" > "$deb/DEBIAN/control"
      dpkg-deb --root-owner-group --build "$deb" "$out/$name.deb"
    fi
    # appimagetool: the one on the PATH, else the pinned one (with its pinned runtime) from eng/get-appimagetool.sh.
    appimagetool=""
    runtime_args=()
    if command -v appimagetool > /dev/null; then
      appimagetool="appimagetool"
    elif tools="$("$repo/eng/get-appimagetool.sh" "$repo/.work/appimagetool")"; then
      appimagetool="$tools/appimagetool"
      runtime_args=(--runtime-file "$tools/runtime-$(uname -m)")
    fi
    # Runs without FUSE (CI runners and containers don't have it).
    export APPIMAGE_EXTRACT_AND_RUN=1
    if [ -n "$appimagetool" ]; then
      echo "== Linux: AppImage"
      appdir="$stage/DaedalusStudio.AppDir"
      mkdir -p "$appdir/usr"
      cp -R "$app" "$appdir/usr/daedalus-studio"
      cp "$repo/eng/packaging/linux/daedalus-studio.desktop" "$appdir/"
      cp "$repo/assets/logo/png/daedalus-256.png" "$appdir/daedalus-studio.png"
      printf '#!/bin/sh\nexec "$(dirname "$(readlink -f "$0")")/usr/daedalus-studio/daedalus" "$@"\n' > "$appdir/AppRun"
      chmod +x "$appdir/AppRun"
      ARCH="$(uname -m)" "$appimagetool" "${runtime_args[@]}" "$appdir" "$out/$name.AppImage"
    else
      echo "No appimagetool for this machine: no AppImage." >&2
    fi
    ;;
esac

echo "== Packages in $out:"
ls -la "$out"
