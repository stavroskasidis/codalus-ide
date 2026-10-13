#!/usr/bin/env bash
# Builds the release packages for this machine's OS and architecture (M31, ADR 0028):
#   Windows: a Velopack Setup.exe (per user, no administrator rights) and the portable .zip, plus winget manifests
#   macOS:   a Velopack .pkg and a zip of Codalus IDE.app
#   Linux:   a Velopack AppImage, a .deb, an .rpm (when rpmbuild is available) and the .tar.gz
# and, for the self-updating installs (M32), the Velopack update feed and packages of the channel `<rid>-<kind>`.
#
# Usage: eng/package.sh [version] [kind]
#   version  what to stamp the build with (default: VERSION with -dev); see `codalus-release version`
#   kind     stable, nightly or dev: the update channel (default: dev, or nightly/stable from the version's form)
# Environment:
#   CODALUS_PREVIOUS_RELEASE  a folder with the previous release of this channel (its releases.<channel>.json and
#                              full .nupkg): Velopack then also builds a delta package
#   CODALUS_RELEASE_NOTES     a Markdown file with the release notes, shown by the update feed
#   CODALUS_FEED_KEY          the feed signing key (PEM; the FEED_SIGNING_KEY secret): signs releases.<channel>.json
# Output: artifacts/packages/ (the Velopack feed and packages in artifacts/packages/velopack/), with SHA256SUMS-<rid>.txt;
#         CODALUS_PACKAGES_OUT names another folder (eng/test-update.sh builds into its own)
# Prerequisites: the .NET SDK (Native AOT toolchain), zip; vswhere on PATH on Windows; mksquashfs on Linux; rpmbuild
# for the .rpm.
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
base="$(tr -d '[:space:]' < "$repo/VERSION")"
version="${1:-$base-dev}"
case "$version" in
  *-nightly.*) default_kind=nightly ;;
  *-*) default_kind=dev ;;
  *) default_kind=stable ;;
esac
kind="${2:-$default_kind}"
rid="$(dotnet --info | awk '/RID:/ {print $2; exit}')"
channel="$rid-$kind"
out="${CODALUS_PACKAGES_OUT:-$repo/artifacts/packages}"
feed="$out/velopack"
stage="$repo/artifacts/package-stage/$rid"
app="$stage/codalus"
name="codalus-$version-$rid"
vpk_version="1.2.161" # the Velopack library's version in Directory.Packages.props

# Paths for the Windows tools (vpk, tar) from Git Bash.
native() { if command -v cygpath > /dev/null; then cygpath -w "$1"; else printf '%s' "$1"; fi; }
# sha256sum escapes a name with backslashes (a Windows path) by starting its line with one: only the hex digits count.
sha256() { (sha256sum "$1" 2> /dev/null || shasum -a 256 "$1") | cut -d' ' -f1 | tr -d '\\'; }

rm -rf "$stage" "$feed"
# This platform's earlier packages go, so versions never mix in the output (other platforms' stay, for CI artifacts).
rm -rf "$out"/codalus-*-"$rid".* "$out"/codalus-*-"$rid"-* "$out"/winget-* "$out/SHA256SUMS-$rid.txt"
mkdir -p "$app" "$out" "$feed"

# Native AOT links with the selected Xcode's clang and ld; use that Xcode's SDK too. Otherwise clang may pick a newer
# Command Line Tools SDK whose .tbd files that ld can't read ("tapi error: … unknown architecture").
if [ "$(uname -s)" = Darwin ] && [ -z "${SDKROOT:-}" ] && command -v xcrun > /dev/null; then
  SDKROOT="$(xcrun --sdk macosx --show-sdk-path 2> /dev/null || true)"
  [ -n "$SDKROOT" ] && export SDKROOT
fi
echo "== Publishing the shell ($rid, $version, channel $channel)"
dotnet publish "$repo/src/Codalus.Shell" -c Release -r "$rid" -o "$app" -p:Version="$version" -p:DebugType=none -p:DebugSymbols=false
# Symbols stay out of packages (they are large); CI keeps them as a separate artifact.
find "$app" -name '*.pdb' -delete

echo "== Debugger (netcoredbg)"
if "$repo/eng/get-netcoredbg.sh" "$stage/netcoredbg"; then
  cp -R "$stage/netcoredbg" "$app/debugger"
else
  echo "netcoredbg is not available for $rid; the package has no debugger." >&2
fi
# The x64 debugger, for the programs of .NET 5 and earlier that run as x64 under Rosetta 2 (M33, ADR 0030).
if [ "$rid" = osx-arm64 ] && [ -d "$app/debugger" ] && "$repo/eng/get-netcoredbg.sh" --x64 "$stage/netcoredbg-x64"; then
  cp -R "$stage/netcoredbg-x64" "$app/debugger/x64"
fi

cp "$repo/LICENSE" "$repo/THIRD-PARTY-NOTICES.md" "$repo/README.md" "$app/"

# Velopack's packaging tool, pinned to the library's version (a mismatch is reported by vpk).
vpk_dir="$repo/.work/vpk-$vpk_version"
if [ ! -x "$vpk_dir/vpk" ] && [ ! -f "$vpk_dir/vpk.exe" ]; then
  dotnet tool install vpk --version "$vpk_version" --tool-path "$vpk_dir" > /dev/null
fi
vpk="$vpk_dir/vpk"
if [ -n "${CODALUS_PREVIOUS_RELEASE:-}" ] && [ -d "$CODALUS_PREVIOUS_RELEASE" ]; then
  echo "== Previous release of $channel (for a delta package)"
  cp "$CODALUS_PREVIOUS_RELEASE"/* "$feed/" 2> /dev/null || true
fi
# Delta packages need zstd on macOS and Linux (vpk brings its own on Windows): without it, full packages only.
delta=BestSpeed
if [[ "$rid" != win-* ]] && ! command -v zstd > /dev/null; then
  echo "warning: no zstd on the PATH: no delta package (sudo apt install zstd, brew install zstd)" >&2
  delta=None
fi
vpk_args=(--packId Codalus --packVersion "$version" --packTitle "Codalus IDE" --packAuthors "Codalus IDE contributors"
  --runtime "$rid" --channel "$channel" --outputDir "$(native "$feed")" --delta "$delta")
if [ -n "${CODALUS_RELEASE_NOTES:-}" ] && [ -f "$CODALUS_RELEASE_NOTES" ]; then
  vpk_args+=(--releaseNotes "$(native "$CODALUS_RELEASE_NOTES")")
fi

case "$rid" in
  win-*)
    echo "== Windows: zip"
    rm -f "$out/$name.zip"
    # Windows PowerShell's Compress-Archive writes backslash separators; zip or Windows' bsdtar write portable archives.
    if command -v zip > /dev/null; then
      (cd "$stage" && zip -qr "$out/$name.zip" codalus)
    else
      (cd "$stage" && "${SYSTEMROOT:-C:/Windows}/System32/tar.exe" -a -cf "$(native "$out/$name.zip")" codalus)
    fi

    echo "== Windows: Setup.exe (Velopack)"
    "$vpk" pack "${vpk_args[@]}" --packDir "$(native "$app")" --mainExe codalus.exe --icon "$(native "$repo/assets/logo/codalus.ico")" \
      --shortcuts StartMenuRoot --skipVeloAppCheck # a Native AOT exe has no IL for vpk to look for VelopackApp.Run in
    mv "$feed/Codalus-$channel-Setup.exe" "$out/$name-setup.exe"
    rm -f "$feed/Codalus-$channel-Portable.zip" # the plain zip above is the portable build

    sha="$(sha256 "$out/$name-setup.exe")"
    manifests="$out/winget-$version"
    mkdir -p "$manifests"
    for template in "$repo/eng/packaging/winget/"*.yaml; do
      sed -e "s/{{VERSION}}/$version/g" -e "s/{{SHA256}}/$sha/g" -e "s/{{FILE}}/$name-setup.exe/g" "$template" > "$manifests/$(basename "$template")"
    done
    ;;
  osx-*)
    echo "== macOS: .pkg and .app (Velopack)"
    iconset="$stage/codalus.iconset"
    mkdir -p "$iconset"
    for size in 16 32 128 256 512; do
      cp "$repo/assets/logo/png/codalus-$size.png" "$iconset/icon_${size}x${size}.png"
    done
    cp "$repo/assets/logo/png/codalus-1024.png" "$iconset/icon_512x512@2x.png"
    iconutil -c icns "$iconset" -o "$stage/codalus.icns"
    # Bundle versions are numeric on macOS: the nightly part stays in the app's own version (`codalus --version`).
    sed -e "s/{{BUNDLE_VERSION}}/${version%%-*}/g" "$repo/eng/packaging/macos/Info.plist" > "$stage/Info.plist"
    sign_args=()
    if [ -n "${CODALUS_CODESIGN_IDENTITY:-}" ]; then
      # D19: unsigned until the repository goes public; the signing step waits behind this variable.
      sign_args=(--signAppIdentity "$CODALUS_CODESIGN_IDENTITY" --signInstallIdentity "${CODALUS_INSTALLER_IDENTITY:-$CODALUS_CODESIGN_IDENTITY}")
      [ -n "${CODALUS_NOTARY_PROFILE:-}" ] && sign_args+=(--notaryProfile "$CODALUS_NOTARY_PROFILE")
    fi
    "$vpk" pack "${vpk_args[@]}" --packDir "$app" --mainExe codalus --icon "$stage/codalus.icns" --plist "$stage/Info.plist" \
      ${sign_args[@]+"${sign_args[@]}"} # macOS bash 3.2 calls an empty array unbound under set -u
    mv "$feed/Codalus-$channel-Setup.pkg" "$out/$name.pkg"
    mv "$feed/Codalus-$channel-Portable.zip" "$out/$name.app.zip"
    ;;
  linux-*)
    echo "== Linux: tar.gz"
    tar -C "$stage" -czf "$out/$name.tar.gz" codalus
    arch="${rid#linux-}"
    [ "$arch" = "x64" ] && debarch=amd64 || debarch="$arch"
    [ "$arch" = "x64" ] && rpmarch=x86_64 || rpmarch=aarch64
    # A prerelease sorts before its release in both formats: 0.2.0~nightly.20261011.1 < 0.2.0.
    pkgversion="${version//-/\~}"
    if command -v dpkg-deb > /dev/null; then
      echo "== Linux: deb"
      deb="$stage/deb"
      mkdir -p "$deb/DEBIAN" "$deb/opt" "$deb/usr/bin" "$deb/usr/share/applications" "$deb/usr/share/icons/hicolor/256x256/apps"
      cp -R "$app" "$deb/opt/codalus"
      ln -s /opt/codalus/codalus "$deb/usr/bin/codalus"
      cp "$repo/eng/packaging/linux/codalus.desktop" "$deb/usr/share/applications/"
      cp "$repo/assets/logo/png/codalus-256.png" "$deb/usr/share/icons/hicolor/256x256/apps/codalus.png"
      sed -e "s/{{VERSION}}/$pkgversion/g" -e "s/{{ARCH}}/$debarch/g" "$repo/eng/packaging/linux/control" > "$deb/DEBIAN/control"
      dpkg-deb --root-owner-group --build "$deb" "$out/$name.deb"
    fi
    if command -v rpmbuild > /dev/null; then
      echo "== Linux: rpm"
      rpmtop="$stage/rpm"
      mkdir -p "$rpmtop/SPECS" "$rpmtop/RPMS"
      sed -e "s|{{VERSION}}|$pkgversion|g" -e "s|{{STAGE}}|$app|g" -e "s|{{REPO}}|$repo|g" \
        "$repo/eng/packaging/linux/codalus.spec" > "$rpmtop/SPECS/codalus.spec"
      rpmbuild --quiet -bb --target "$rpmarch" --define "_topdir $rpmtop" "$rpmtop/SPECS/codalus.spec"
      mv "$rpmtop/RPMS/$rpmarch/"codalus-*.rpm "$out/$name.rpm"
    else
      echo "No rpmbuild on this machine: no .rpm." >&2
    fi

    echo "== Linux: AppImage (Velopack)"
    "$vpk" pack "${vpk_args[@]}" --packDir "$app" --mainExe codalus --icon "$repo/assets/logo/png/codalus-512.png" \
      --categories "Development;IDE"
    mv "$feed/Codalus-$channel.AppImage" "$out/$name.AppImage"
    ;;
esac

# The previous release's files were inputs only: the feed keeps its entries, and their packages stay in their release.
if [ -n "${CODALUS_PREVIOUS_RELEASE:-}" ] && [ -d "$CODALUS_PREVIOUS_RELEASE" ]; then
  for previous in "$CODALUS_PREVIOUS_RELEASE"/*.nupkg; do
    [ -e "$previous" ] && rm -f "$feed/$(basename "$previous")"
  done
fi

# The feed signature (M32, ADR 0029): base64 of an ECDSA P-256 / SHA-256 signature over the feed's bytes, which the app
# checks with the project's public key (eng/packaging/update-feed-key.pub.pem) before it trusts the feed's hashes.
if [ -n "${CODALUS_FEED_KEY:-}" ]; then
  echo "== Signing the update feed"
  key_file="$(mktemp)"
  trap 'rm -f "$key_file"' EXIT
  printf '%s\n' "$CODALUS_FEED_KEY" > "$key_file"
  for file in "$feed"/releases.*.json; do
    openssl dgst -sha256 -sign "$key_file" "$file" | openssl base64 -A > "$file.sig"
  done
  rm -f "$key_file"
else
  echo "CODALUS_FEED_KEY isn't set: the update feed is unsigned, and installed builds will refuse it." >&2
fi

echo "== Checksums"
(cd "$out" && for file in "$name".* "$name"-* velopack/*; do
  [ -f "$file" ] && printf '%s  %s\n' "$(sha256 "$file")" "$(basename "$file")"
done) > "$out/SHA256SUMS-$rid.txt"

echo "== Packages in $out:"
ls -la "$out" "$feed"
