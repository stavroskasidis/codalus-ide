#!/usr/bin/env bash
# Self-update, end to end (M32, ADR 0029), on this machine's platform:
#   1. builds the installer of version N and of N+1 (with a delta against N) on a test channel, the feed signed with a
#      throwaway key;
#   2. installs N the way a user does (Windows: the silent Setup.exe; Linux: the AppImage; macOS: the .pkg in the
#      user's home);
#   3. runs N with `--update-now` on a tampered copy of the feed: it must refuse it (exit code 2);
#   4. runs N with `--update-now` on the feed: it downloads N+1 and has it applied once it exits (on macOS with the
#      home folder on another volume than the app, a disk image, as on a Mac whose home is on an external disk);
#   5. checks that the installed app is now N+1, then uninstalls it.
# The app reads the feed from a folder (CODALUS_UPDATE_SOURCE) and trusts the throwaway key for that folder only
# (CODALUS_UPDATE_TEST_KEY).
# Usage: eng/test-update.sh [work folder]   (default: artifacts/update-test). Needs what eng/package.sh needs, and openssl.
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
work="${1:-$repo/artifacts/update-test}"
base="$(tr -d '[:space:]' < "$repo/VERSION")"
old="$base-test.1"
rid="$(dotnet --info | awk '/RID:/ {print $2; exit}')"
new="$base-test.2"
fail() { echo "error: $*" >&2; exit 1; }
native() { if command -v cygpath > /dev/null; then cygpath -w "$1"; else printf '%s' "$1"; fi; }

[ "$(uname -s)" != Darwin ] || hdiutil detach -quiet -force "$work/home-volume" > /dev/null 2>&1 || true # a run that was stopped
rm -rf "$work"
mkdir -p "$work"
echo "== A throwaway feed key"
openssl ecparam -name prime256v1 -genkey -noout -out "$work/key.pem" 2> /dev/null
public_key="$(openssl pkey -in "$work/key.pem" -pubout -outform DER | openssl base64 -A)"
CODALUS_FEED_KEY="$(cat "$work/key.pem")"
export CODALUS_FEED_KEY
# Its own output folder: the release packages in artifacts/packages stay as they are.
export CODALUS_PACKAGES_OUT="$work/out"

echo "== Building $old"
bash "$repo/eng/package.sh" "$old" test > "$work/package-old.log" 2>&1 || { tail -40 "$work/package-old.log"; fail "packaging $old failed"; }
cp -R "$work/out" "$work/old"
echo "== Building $new (with a delta against $old)"
CODALUS_PREVIOUS_RELEASE="$work/old/velopack" bash "$repo/eng/package.sh" "$new" test > "$work/package-new.log" 2>&1 \
  || { tail -40 "$work/package-new.log"; fail "packaging $new failed"; }
cp -R "$work/out" "$work/new"
feed="$work/new/velopack"
ls "$feed"
ls "$feed"/*-delta.nupkg > /dev/null 2>&1 || echo "warning: no delta package was built" >&2

# A copy of the feed whose JSON changed after it was signed.
tampered="$work/tampered"
cp -R "$feed" "$tampered"
for file in "$tampered"/releases.*.json; do
  sed -i.bak -e 's/"Size":/"Size":1/' "$file" && rm -f "$file.bak"
done

echo "== Installing $old"
update_env=()
case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*)
    root="$work/install"
    "$work/old/codalus-$old-$rid-setup.exe" --silent --installto "$(native "$root")"
    powershell -NoProfile -Command "Get-Process codalus -ErrorAction SilentlyContinue | Where-Object { \$_.Path -like '$(native "$root")*' } | Stop-Process -Force" || true
    app="$root/current/codalus.exe"
    uninstall() { "$root/Update.exe" --uninstall --silent > /dev/null 2>&1 || true; }
    ;;
  Darwin)
    bundle="$HOME/Applications/Codalus IDE.app"
    [ ! -e "$bundle" ] || fail "$bundle exists already: this test installs there"
    installer -pkg "$work/old/codalus-$old-$rid.pkg" -target CurrentUserHomeDirectory
    pkill -f "$bundle/Contents/MacOS/codalus" || true
    app="$bundle/Contents/MacOS/codalus"
    # Velopack stages updates under ~/Library/Caches and renames them onto the bundle, which can't cross volumes.
    hdiutil create -quiet -size 1g -type SPARSE -fs APFS -volname CodalusUpdateHome "$work/home"
    mkdir -p "$work/home-volume"
    hdiutil attach -quiet -nobrowse -mountpoint "$work/home-volume" "$work/home.sparseimage"
    update_env=(HOME="$work/home-volume")
    uninstall() {
      rm -rf "$bundle"
      pkgutil --pkgs --volume "$HOME" 2> /dev/null | grep -i codalus | xargs -I{} pkgutil --forget {} --volume "$HOME" > /dev/null 2>&1 || true
      hdiutil detach -quiet -force "$work/home-volume" > /dev/null 2>&1 || true
    }
    ;;
  Linux)
    mkdir -p "$work/install"
    app="$work/install/codalus.AppImage"
    cp "$work/old/codalus-$old-$rid.AppImage" "$app"
    chmod +x "$app"
    export APPIMAGE_EXTRACT_AND_RUN=1 # no FUSE in CI runners and containers
    uninstall() { rm -f "$app"; }
    ;;
esac
trap uninstall EXIT
installed_version() { "$app" --version 2> /dev/null | head -n 1; }
[ "$(installed_version)" = "Codalus IDE $old" ] || fail "the installed app says '$(installed_version)', not $old"

echo "== A tampered feed is refused"
status=0
CODALUS_UPDATE_SOURCE="$(native "$tampered")" CODALUS_UPDATE_TEST_KEY="$public_key" "$app" --update-now || status=$?
[ "$status" -eq 2 ] || fail "the tampered feed wasn't refused (exit code $status)"
[ "$(installed_version)" = "Codalus IDE $old" ] || fail "the tampered feed changed the install"

echo "== Updating $old to $new"
env ${update_env[@]+"${update_env[@]}"} CODALUS_UPDATE_SOURCE="$(native "$feed")" CODALUS_UPDATE_TEST_KEY="$public_key" "$app" --update-now \
  || fail "--update-now failed (exit code $?)"
# Velopack's updater applies the package once the app has exited.
for _ in $(seq 90); do
  [ "$(installed_version)" = "Codalus IDE $new" ] && break
  sleep 2
done
[ "$(installed_version)" = "Codalus IDE $new" ] || fail "after the update the app says '$(installed_version)', not $new"
echo "== The installed app is $new"

echo "== Uninstalling"
uninstall
trap - EXIT
echo "== SELF-UPDATE TEST PASSED ($old -> $new)"
