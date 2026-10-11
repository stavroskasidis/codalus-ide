#!/usr/bin/env bash
# End-to-end smoke test of an installed Daedalus Studio: `daedalus --version`, then `daedalus --smoke-test samples`, which
# drives the real app through each MVP milestone (shell, plugins, project system, editor and C# language service, build
# and run, debugging, Git) and exits non-zero when a step fails (src/DaedalusStudio.Shell/Infrastructure/SmokeTest.cs).
#
# Usage:
#   eng/smoke-test.sh <daedalus executable> [results dir]   test a published app (e.g. artifacts/shell/daedalus)
#   eng/smoke-test.sh --packages <dir> [results dir]         install this OS's packages from eng/package.sh as a user
#       would, test them, uninstall them and check that nothing is left (M31): Windows: the Setup.exe (silent, per user;
#       its `daedalus` command and uninstall entry) and the zip; macOS: the .pkg (in the user's home) and the .app zip;
#       Linux: the AppImage, the .deb and the .rpm (apt and dnf, on the host with passwordless sudo, otherwise in
#       containers) and the tar.gz. The full smoke test runs on the Setup.exe, .pkg and AppImage installs.
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
# The hosting step (M13b) signs in to a mock GitHub. On Linux, a desktop's Secret Service may ask for the keyring's
# password in a window, which an unattended run can't answer: keep the token in memory there (libsecret itself is
# tested against a private keyring in CredentialStoreTests). Windows and macOS use their stores, under the profile's
# own service name, and the step signs out at the end.
if [ "$(uname -s)" = Linux ]; then
  export DAEDALUS_CREDENTIAL_STORE="${DAEDALUS_CREDENTIAL_STORE:-memory}"
fi

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
  # An X server listens on that file socket, or on Linux on an abstract one of the same name (Xvfb when /tmp/.X11-unix
  # is read-only, as under WSLg).
  local x_server=false
  if [[ -S "$x_socket" ]] || grep -q " @$x_socket\$" /proc/net/unix 2> /dev/null; then
    x_server=true
  fi

  if [[ "$(uname -s)" == Linux && ( -z "${DISPLAY:-}" || ( "$DISPLAY" == :* && "$x_server" == false ) ) ]]; then
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

# The one package of a kind in the folder (eng/package.sh names them daedalus-studio-<version>-<rid><suffix>).
package() { ls "$packages"/daedalus-studio-*"$1" 2> /dev/null | head -n 1; }
fail() { echo "error: $*" >&2; exit 1; }

case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*)
    zip="$(package .zip)"
    echo "== Extracting $zip"
    if command -v unzip > /dev/null; then
      unzip -q "$zip" -d "$install/zip"
    else
      powershell -NoProfile -Command "Expand-Archive -LiteralPath '$(cygpath -w "$zip")' -DestinationPath '$(cygpath -w "$install/zip")'"
    fi
    test_app "$install/zip/daedalus-studio/daedalus.exe" "$results/zip" no

    setup="$(package -setup.exe)"
    root="$install/velopack"
    winroot="$(cygpath -w "$root")"
    uninstall_key='HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\DaedalusStudio'
    # Whether the user's PATH (as the registry holds it) lists the install's bin folder. Compared in PowerShell: the
    # PATH may hold any characters, and Git Bash's grep aborts on some of them.
    on_path() { powershell -NoProfile -Command "if (((Get-Item 'HKCU:\Environment').GetValue('Path', '', 'DoNotExpandEnvironmentNames') -split ';' | ForEach-Object { \$_.TrimEnd('\') }) -contains '$winroot\bin') { exit 0 } else { exit 1 }"; }
    uninstall() { [ -f "$root/Update.exe" ] && "$root/Update.exe" --uninstall --silent > /dev/null 2>&1 || true; }
    trap uninstall EXIT # a failed check still leaves the machine as it was
    echo "== Installing $setup (silent, per user)"
    "$setup" --silent --installto "$winroot"
    # Setup starts the app when it is done: only the smoke test's instance may run.
    powershell -NoProfile -Command "Get-Process daedalus -ErrorAction SilentlyContinue | Where-Object { \$_.Path -like '$winroot*' } | Stop-Process -Force" || true
    [ -f "$root/current/daedalus.exe" ] && [ -f "$root/Update.exe" ] || fail "the install has no current\\daedalus.exe and Update.exe"
    reg query "$uninstall_key" > /dev/null || fail "no uninstall entry ($uninstall_key)"
    [ -f "$root/bin/daedalus.cmd" ] || fail "no bin\\daedalus.cmd"
    on_path || fail "the user PATH has no $winroot\\bin"
    echo "== daedalus --version through the command on the PATH"
    # The app is a GUI program: cmd gives it no handles to capture, so its output goes to the console. Its bench event
    # file shows the command ran the installed app and waited for it (the event is there when cmd returns).
    events="$install/command-events.jsonl"
    DAEDALUS_BENCH_EVENTS="$(cygpath -w "$events")" cmd //c "$winroot\\bin\\daedalus.cmd" --version > /dev/null
    grep -q '"main_entered"' "$events" 2> /dev/null || fail "the daedalus command didn't run the installed app"
    test_app "$root/current/daedalus.exe" "$results"

    echo "== Uninstalling"
    "$root/Update.exe" --uninstall --silent
    trap - EXIT
    for _ in $(seq 30); do [ ! -e "$root/current" ] && break; sleep 1; done
    [ ! -e "$root/current" ] || fail "uninstalling left $winroot\\current"
    ! reg query "$uninstall_key" > /dev/null 2>&1 || fail "uninstalling left the uninstall entry"
    ! on_path || fail "uninstalling left $winroot\\bin on the PATH"
    [ ! -e "$APPDATA/Microsoft/Windows/Start Menu/Programs/Daedalus Studio.lnk" ] || fail "uninstalling left the Start menu entry"
    ;;
  Darwin)
    appzip="$(package .app.zip)"
    echo "== Extracting $appzip"
    ditto -x -k "$appzip" "$install/zip"
    test_app "$install/zip/Daedalus Studio.app/Contents/MacOS/daedalus" "$results/app-zip" no

    pkg="$(package .pkg)"
    app="$HOME/Applications/Daedalus Studio.app"
    [ ! -e "$app" ] || fail "$app exists already: this test installs there"
    remove() { rm -rf "$app"; pkgutil --pkgs --volume "$HOME" 2> /dev/null | grep -i daedalus | xargs -I{} pkgutil --forget {} --volume "$HOME" > /dev/null 2>&1 || true; }
    trap remove EXIT
    echo "== Installing $pkg (current user's home)"
    installer -pkg "$pkg" -target CurrentUserHomeDirectory
    pkill -f "$app/Contents/MacOS/daedalus" || true # the package starts the app when it is done
    [ -x "$app/Contents/MacOS/daedalus" ] || fail "the package didn't install $app"
    test_app "$app/Contents/MacOS/daedalus" "$results"

    echo "== Uninstalling (removing the app and the package receipt)"
    remove
    trap - EXIT
    [ ! -e "$app" ] || fail "$app is still there"
    ! pkgutil --pkgs --volume "$HOME" 2> /dev/null | grep -qi daedalus || fail "a package receipt is still there"
    ;;
  Linux)
    tarball="$(package .tar.gz)"
    echo "== Extracting $tarball"
    mkdir -p "$install/tar"
    tar -C "$install/tar" -xzf "$tarball"
    test_app "$install/tar/daedalus-studio/daedalus" "$results/tar.gz" no

    appimage="$(package .AppImage)"
    echo "== AppImage $appimage"
    cp "$appimage" "$install/"
    chmod +x "$install/$(basename "$appimage")"
    export APPIMAGE_EXTRACT_AND_RUN=1 # CI runners and containers have no FUSE
    test_app "$install/$(basename "$appimage")" "$results"

    # The .deb and .rpm: installed with the system's package manager, then removed, and nothing may be left. On the host
    # when sudo needs no password (CI), otherwise in a container of a distribution that uses the format.
    runtime="$(command -v podman || command -v docker || true)"
    check_removed='test ! -e /opt/daedalus-studio && test ! -e /usr/bin/daedalus && test ! -e /usr/share/applications/daedalus-studio.desktop'
    deb="$(package .deb)"
    if [ -n "$deb" ]; then
      if sudo -n true 2> /dev/null; then
        echo "== Installing $deb (apt)"
        sudo apt-get install -y "$deb"
        test_app /usr/bin/daedalus "$results/deb" no
        sudo apt-get remove --purge -y daedalus-studio
        bash -c "$check_removed" || fail "removing the .deb left files behind"
      elif [ -n "$runtime" ]; then
        echo "== Installing $deb (apt, in an Ubuntu container)"
        "$runtime" run --rm -v "$packages:/packages:ro" docker.io/library/ubuntu:24.04 bash -euc "
          apt-get update -qq && apt-get install -y -qq /packages/$(basename "$deb") > /dev/null
          daedalus --version | grep -q '^Daedalus Studio '
          apt-get remove --purge -y -qq daedalus-studio > /dev/null
          $check_removed"
      else
        echo "warning: no passwordless sudo and no container runtime: the .deb wasn't installed" >&2
      fi
    fi
    rpm="$(package .rpm)"
    if [ -n "$rpm" ]; then
      if [ -n "$runtime" ]; then
        echo "== Installing $rpm (dnf, in a Fedora container)"
        "$runtime" run --rm -v "$packages:/packages:ro" docker.io/library/fedora:42 bash -euc "
          dnf install -y -q /packages/$(basename "$rpm") > /tmp/dnf.log 2>&1 || { cat /tmp/dnf.log; exit 1; }
          daedalus --version | grep -q '^Daedalus Studio '
          dnf remove -y -q daedalus-studio > /tmp/dnf.log 2>&1 || { cat /tmp/dnf.log; exit 1; }
          $check_removed"
      else
        echo "warning: no container runtime: the .rpm wasn't installed" >&2
      fi
    fi
    ;;
  *)
    echo "error: unsupported OS $(uname -s)" >&2
    exit 1
    ;;
esac
