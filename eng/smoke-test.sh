#!/usr/bin/env bash
# End-to-end smoke test of an installed Daedalus Studio: `daedalus --version`, then `daedalus --smoke-test samples`, which
# drives the real app through each MVP milestone (shell, plugins, project system, editor and C# language service, build
# and run, debugging, Git) and exits non-zero when a step fails (src/DaedalusStudio.Shell/Infrastructure/SmokeTest.cs).
#
# Usage:
#   eng/smoke-test.sh <daedalus executable> [results dir]   test a published app (e.g. artifacts/shell/daedalus)
#   eng/smoke-test.sh --packages <dir> [results dir]         install this OS's packages from eng/package.sh and test them:
#       Windows: the zip; macOS: the .app from the dmg; Linux: the .deb (installed with apt), plus --version of the
#       tar.gz and the AppImage
# Results (report, screenshots, logs): artifacts/smoke-test by default. With GITHUB_STEP_SUMMARY set, the report is added
# to the job summary. Needs the .NET SDK and git on the PATH (the app builds and commits), and a display: on Linux without
# one, the test runs under xvfb-run.
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
# Node.js and the web language servers (M16b) download once into the repository's .work folder, not every run.
node_cache="$repo/.work/node-cache"
command -v cygpath > /dev/null && node_cache="$(cygpath -w "$node_cache")" # Git Bash on Windows: a Windows path for the app
export DAEDALUS_NODE_CACHE="${DAEDALUS_NODE_CACHE:-$node_cache}"
# Browser debugging (M16c) runs Chrome or Edge headless, so the test opens no browser window.
export DAEDALUS_BROWSER_HEADLESS=1

# Runs one app: --version, then the smoke test. $1: executable; $2: results dir; $3: whether to run the full smoke test.
test_app() {
  local exe="$1" results="$2" full="${3:-yes}"
  mkdir -p "$results"
  echo "== $exe --version"
  local version
  version="$("$exe" --version)"
  echo "$version"
  if [[ "$version" != "Daedalus Studio "* ]]; then
    echo "error: unexpected --version output" >&2
    return 1
  fi

  [ "$full" = yes ] || return 0
  echo "== $exe --smoke-test"
  local userdata="$results/user-data"
  rm -rf "$userdata"
  local cmd=("$exe" --user-data-dir "$userdata" --smoke-test "$repo/samples")
  local status=0
  # A DISPLAY without an X server behind it (WSLg not running) aborts the app: use xvfb-run then, as without one.
  local display_number="${DISPLAY:-}"
  display_number="${display_number#:}"
  display_number="${display_number%%.*}"
  local x_socket="/tmp/.X11-unix/X$display_number"
  if [[ "$(uname -s)" == Linux && ( -z "${DISPLAY:-}" || ( "$DISPLAY" == :* && ! -S "$x_socket" ) ) ]]; then
    if ! command -v xvfb-run > /dev/null; then
      echo "error: no X server for DISPLAY='${DISPLAY:-}' ($x_socket is missing) and xvfb-run isn't installed." >&2
      echo "       On WSL, restart WSLg with 'wsl --shutdown' (from Windows), or install xvfb: sudo apt install xvfb" >&2
      return 1
    fi

    xvfb-run -a -s "-screen 0 1920x1080x24" "${cmd[@]}" || status=$?
  else
    "${cmd[@]}" || status=$?
  fi

  cp "$userdata"/logs/smoke-test* "$results/" 2> /dev/null || true
  if [ -f "$results/smoke-test.md" ]; then
    [ -n "${GITHUB_STEP_SUMMARY:-}" ] && cat "$results/smoke-test.md" >> "$GITHUB_STEP_SUMMARY"
  else
    echo "error: the app wrote no smoke test report (exit code $status)" >&2
    [ "$status" -ne 0 ] || status=1
  fi

  echo "== smoke test exit code: $status"
  return "$status"
}

if [ "${1:-}" != "--packages" ]; then
  exe="${1:?usage: eng/smoke-test.sh <daedalus executable> [results dir] | --packages <dir> [results dir]}"
  [ -f "$exe" ] || [ ! -f "$exe.exe" ] || exe="$exe.exe"
  test_app "$exe" "${2:-$repo/artifacts/smoke-test}"
  exit $?
fi

packages="$(cd "${2:?usage: eng/smoke-test.sh --packages <dir> [results dir]}" && pwd)"
results="${3:-$repo/artifacts/smoke-test}"
# Installed outside the source checkout, so nothing can fall back to the checkout (netcoredbg in .work/, for one).
install="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/daedalus-package-XXXXXX")"

case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*)
    zip="$(ls "$packages"/daedalus-studio-*.zip | head -n 1)"
    echo "== Extracting $zip"
    if command -v unzip > /dev/null; then
      unzip -q "$zip" -d "$install"
    else
      powershell -NoProfile -Command "Expand-Archive -LiteralPath '$(cygpath -w "$zip")' -DestinationPath '$(cygpath -w "$install")'"
    fi
    test_app "$install/daedalus-studio/daedalus.exe" "$results"
    ;;
  Darwin)
    dmg="$(ls "$packages"/daedalus-studio-*.dmg | head -n 1)"
    echo "== Mounting $dmg"
    mkdir -p "$install/dmg"
    hdiutil attach -nobrowse -readonly -mountpoint "$install/dmg" "$dmg"
    cp -R "$install/dmg/Daedalus Studio.app" "$install/"
    hdiutil detach "$install/dmg"
    codesign --verify --deep "$install/Daedalus Studio.app"
    test_app "$install/Daedalus Studio.app/Contents/MacOS/daedalus" "$results"
    ;;
  Linux)
    tarball="$(ls "$packages"/daedalus-studio-*.tar.gz | head -n 1)"
    echo "== Extracting $tarball"
    tar -C "$install" -xzf "$tarball"
    test_app "$install/daedalus-studio/daedalus" "$results/tar.gz" no
    if appimage="$(ls "$packages"/daedalus-studio-*.AppImage 2> /dev/null | head -n 1)" && [ -n "$appimage" ]; then
      chmod +x "$appimage"
      export APPIMAGE_EXTRACT_AND_RUN=1 # CI runners have no FUSE
      test_app "$appimage" "$results/appimage" no
    fi
    deb="$(ls "$packages"/daedalus-studio-*.deb | head -n 1)"
    echo "== Installing $deb"
    sudo apt-get install -y "$deb"
    # Through the /usr/bin symbolic link, as a user would start it.
    test_app /usr/bin/daedalus "$results"
    ;;
  *)
    echo "error: unsupported OS $(uname -s)" >&2
    exit 1
    ;;
esac
