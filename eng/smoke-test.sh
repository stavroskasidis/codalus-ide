#!/usr/bin/env bash
# End-to-end smoke test of an installed Codalus IDE: `codalus --version`, then `codalus --smoke-test samples`, which
# drives the real app through each MVP milestone (shell, plugins, project system, editor and C# language service, build
# and run, debugging, Git) and exits non-zero when a step fails (src/Codalus.Shell/Infrastructure/SmokeTest.cs).
#
# Usage:
#   eng/smoke-test.sh <codalus executable> [results dir]   test a published app (e.g. artifacts/shell/codalus)
#   eng/smoke-test.sh --packages <dir> [results dir]         install this OS's packages from eng/package.sh as a user
#       would, test them, uninstall them and check that nothing is left (M31): Windows: the Setup.exe (silent, per user;
#       its `codalus` command and uninstall entry) and the zip; macOS: the .pkg (in the user's home) and the .app zip;
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
export CODALUS_NODE_CACHE="${CODALUS_NODE_CACHE:-$node_cache}"
# Browser debugging (M16c) runs Chrome or Edge headless, so the test opens no browser window.
export CODALUS_BROWSER_HEADLESS=1
# The hosting step (M13b) signs in to a mock GitHub. On Linux, a desktop's Secret Service may ask for the keyring's
# password in a window, which an unattended run can't answer: keep the token in memory there (libsecret itself is
# tested against a private keyring in CredentialStoreTests). Windows and macOS use their stores, under the profile's
# own service name, and the step signs out at the end.
if [ "$(uname -s)" = Linux ]; then
  export CODALUS_CREDENTIAL_STORE="${CODALUS_CREDENTIAL_STORE:-memory}"
fi

# Runs "$@" and stops it after $smoke_limit seconds. The app's own watchdog (40 minutes) starts only once it's up: an
# X server that took the connection and never answered (a hung WSLg) held it before its window, for hours. A loop, not
# timeout(1): macOS has none, and Git Bash's is Windows' timeout.exe.
smoke_limit=$((50 * 60))
run_bounded() {
  "$@" &
  local pid=$! waited=0 status=0
  while kill -0 "$pid" 2> /dev/null; do
    if ((waited >= smoke_limit)); then
      echo "error: the smoke test didn't end within $((smoke_limit / 60)) minutes; stopping it" >&2
      kill -TERM "$pid" 2> /dev/null
      sleep 10
      kill -KILL "$pid" 2> /dev/null
      break
    fi

    sleep 5
    waited=$((waited + 5))
  done

  wait "$pid" || status=$?
  return "$status"
}

# Runs one app: --version, then the smoke test. $1: executable; $2: results dir; $3: whether to run the full smoke test.
test_app() {
  local exe="$1" results="$2" full="${3:-yes}"
  mkdir -p "$results"
  echo "== $exe --version"
  local version
  version="$("$exe" --version)"
  echo "$version"
  if [[ "$version" != "Codalus IDE "* ]]; then
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

  # A native crash on CI (a segmentation fault in the start window step, which never happened elsewhere) leaves a core
  # in the results folder, and its threads' stacks are printed below: the runner is the only place to see it.
  local cores=""
  if [[ "$(uname -s)" == Linux && "${CI:-}" == true ]] && sudo -n true 2> /dev/null; then
    cores="$(cd "$results" && pwd)/cores"
    mkdir -p "$cores"
    sudo -n sysctl -q -w kernel.core_pattern="$cores/core.%p" || cores=""
    ulimit -c unlimited || true
  fi

  # vscode-js-debug's logs of the browser steps, kept only when the smoke test fails (a breakpoint in the page that
  # never stopped, once, on the Linux arm64 runner).
  local jsdebug="$results/js-debug"
  rm -rf "$jsdebug"
  [[ -n "${CODALUS_JSDEBUG_TRACE:-}" ]] || cmd=(env "CODALUS_JSDEBUG_TRACE=$jsdebug" "${cmd[@]}")
  # netcoredbg's DAP logs, kept only when the smoke test fails: a Run to Cursor that never stopped (Linux arm64, run
  # 40241261003) left nothing to say whether a request went unanswered or a stop was lost.
  local netcoredbg="$results/netcoredbg"
  rm -rf "$netcoredbg"
  [[ -n "${CODALUS_NETCOREDBG_LOG:-}" ]] || cmd=(env "CODALUS_NETCOREDBG_LOG=$netcoredbg" "${cmd[@]}")

  if [[ "$(uname -s)" == Linux && ( -z "${DISPLAY:-}" || ( "$DISPLAY" == :* && "$x_server" == false ) ) ]]; then
    if ! command -v xvfb-run > /dev/null; then
      echo "error: no X server for DISPLAY='${DISPLAY:-}' ($x_socket is missing) and xvfb-run isn't installed." >&2
      echo "       On WSL, restart WSLg with 'wsl --shutdown' (from Windows), or install xvfb: sudo apt install xvfb" >&2
      return 1
    fi

    run_bounded xvfb-run -a -s "-screen 0 1920x1080x24" "${cmd[@]}" || status=$?
  else
    run_bounded "${cmd[@]}" || status=$?
  fi

  if [[ -n "$cores" && "$status" -gt 128 ]] && compgen -G "$cores/core.*" > /dev/null; then
    command -v gdb > /dev/null || sudo -n apt-get install -y -qq --no-install-recommends gdb > /dev/null 2>&1 || true
    if command -v gdb > /dev/null; then
      for core in "$cores"/core.*; do
        echo "== native stacks at the crash ($core)"
        gdb -batch -nx -ex "set pagination off" -ex "info signal SIGSEGV" -ex "thread apply all bt 40" "$exe" "$core" 2>&1 \
          | tee "$results/crash-stacks.txt" | head -n 400
      done
    fi
    rm -f "$cores"/core.* # hundreds of MB: the stacks are what the results keep
  fi

  if [[ "$status" -eq 0 ]]; then
    rm -rf "$jsdebug" "$netcoredbg"
  fi

  cp "$userdata"/logs/smoke-test* "$userdata"/logs/command-sweep.json "$userdata"/logs/command-sweep-progress.log "$results/" 2> /dev/null || true
  # The UI gallery (M34d): every surface in every theme, with data, empty and in an error state.
  if [ -d "$userdata/logs/gallery" ]; then
    rm -rf "$results/gallery"
    cp -r "$userdata/logs/gallery" "$results/gallery"
  fi
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
  exe="${1:?usage: eng/smoke-test.sh <codalus executable> [results dir] | --packages <dir> [results dir]}"
  [ -f "$exe" ] || [ ! -f "$exe.exe" ] || exe="$exe.exe"
  test_app "$exe" "${2:-$repo/artifacts/smoke-test}"
  exit $?
fi

packages="$(cd "${2:?usage: eng/smoke-test.sh --packages <dir> [results dir]}" && pwd)"
# The command sweep runs once per OS, on the published app (the smoke job): an installed copy is the same build.
export CODALUS_SMOKE_SKIP="${CODALUS_SMOKE_SKIP:-shell.commandSweep=the smoke job sweeps the commands of the same build}"
results="${3:-$repo/artifacts/smoke-test}"
# Installed outside the source checkout, so nothing can fall back to the checkout (netcoredbg in .work/, for one).
install="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/codalus-package-XXXXXX")"

# The one package of a kind in the folder (eng/package.sh names them codalus-<version>-<rid><suffix>).
package() { ls "$packages"/codalus-*"$1" 2> /dev/null | head -n 1; }
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
    test_app "$install/zip/codalus/codalus.exe" "$results/zip" no

    setup="$(package -setup.exe)"
    root="$install/velopack"
    winroot="$(cygpath -w "$root")"
    uninstall_key='HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Codalus'
    # Whether the user's PATH (as the registry holds it) lists the install's bin folder. Compared in PowerShell: the
    # PATH may hold any characters, and Git Bash's grep aborts on some of them.
    on_path() { powershell -NoProfile -Command "if (((Get-Item 'HKCU:\Environment').GetValue('Path', '', 'DoNotExpandEnvironmentNames') -split ';' | ForEach-Object { \$_.TrimEnd('\') }) -contains '$winroot\bin') { exit 0 } else { exit 1 }"; }
    uninstall() { [ -f "$root/Update.exe" ] && "$root/Update.exe" --uninstall --silent > /dev/null 2>&1 || true; }
    trap uninstall EXIT # a failed check still leaves the machine as it was
    echo "== Installing $setup (silent, per user)"
    "$setup" --silent --installto "$winroot"
    # Setup starts the app when it is done: only the smoke test's instance may run.
    powershell -NoProfile -Command "Get-Process codalus -ErrorAction SilentlyContinue | Where-Object { \$_.Path -like '$winroot*' } | Stop-Process -Force" || true
    [ -f "$root/current/codalus.exe" ] && [ -f "$root/Update.exe" ] || fail "the install has no current\\codalus.exe and Update.exe"
    reg query "$uninstall_key" > /dev/null || fail "no uninstall entry ($uninstall_key)"
    [ -f "$root/bin/codalus.cmd" ] || fail "no bin\\codalus.cmd"
    on_path || fail "the user PATH has no $winroot\\bin"
    echo "== codalus --version through the command on the PATH"
    # The app is a GUI program: cmd gives it no handles to capture, so its output goes to the console. Its bench event
    # file shows the command ran the installed app and waited for it (the event is there when cmd returns).
    events="$install/command-events.jsonl"
    CODALUS_BENCH_EVENTS="$(cygpath -w "$events")" cmd //c "$winroot\\bin\\codalus.cmd" --version > /dev/null
    grep -q '"main_entered"' "$events" 2> /dev/null || fail "the codalus command didn't run the installed app"
    test_app "$root/current/codalus.exe" "$results"

    echo "== Uninstalling"
    "$root/Update.exe" --uninstall --silent
    trap - EXIT
    for _ in $(seq 30); do [ ! -e "$root/current" ] && break; sleep 1; done
    [ ! -e "$root/current" ] || fail "uninstalling left $winroot\\current"
    ! reg query "$uninstall_key" > /dev/null 2>&1 || fail "uninstalling left the uninstall entry"
    ! on_path || fail "uninstalling left $winroot\\bin on the PATH"
    [ ! -e "$APPDATA/Microsoft/Windows/Start Menu/Programs/Codalus IDE.lnk" ] || fail "uninstalling left the Start menu entry"
    ;;
  Darwin)
    appzip="$(package .app.zip)"
    echo "== Extracting $appzip"
    ditto -x -k "$appzip" "$install/zip"
    test_app "$install/zip/Codalus IDE.app/Contents/MacOS/codalus" "$results/app-zip" no

    pkg="$(package .pkg)"
    app="$HOME/Applications/Codalus IDE.app"
    [ ! -e "$app" ] || fail "$app exists already: this test installs there"
    remove() { rm -rf "$app"; pkgutil --pkgs --volume "$HOME" 2> /dev/null | grep -i codalus | xargs -I{} pkgutil --forget {} --volume "$HOME" > /dev/null 2>&1 || true; }
    trap remove EXIT
    echo "== Installing $pkg (current user's home)"
    installer -pkg "$pkg" -target CurrentUserHomeDirectory
    pkill -f "$app/Contents/MacOS/codalus" || true # the package starts the app when it is done
    [ -x "$app/Contents/MacOS/codalus" ] || fail "the package didn't install $app"
    test_app "$app/Contents/MacOS/codalus" "$results"

    echo "== Uninstalling (removing the app and the package receipt)"
    remove
    trap - EXIT
    [ ! -e "$app" ] || fail "$app is still there"
    ! pkgutil --pkgs --volume "$HOME" 2> /dev/null | grep -qi codalus || fail "a package receipt is still there"
    ;;
  Linux)
    tarball="$(package .tar.gz)"
    echo "== Extracting $tarball"
    mkdir -p "$install/tar"
    tar -C "$install/tar" -xzf "$tarball"
    test_app "$install/tar/codalus/codalus" "$results/tar.gz" no

    appimage="$(package .AppImage)"
    echo "== AppImage $appimage"
    cp "$appimage" "$install/"
    chmod +x "$install/$(basename "$appimage")"
    export APPIMAGE_EXTRACT_AND_RUN=1 # CI runners and containers have no FUSE
    test_app "$install/$(basename "$appimage")" "$results"

    # The .deb and .rpm: installed with the system's package manager, then removed, and nothing may be left. On the host
    # when sudo needs no password (CI), otherwise in a container of a distribution that uses the format.
    runtime="$(command -v podman || command -v docker || true)"
    check_removed='test ! -e /opt/codalus && test ! -e /usr/bin/codalus && test ! -e /usr/share/applications/codalus.desktop'
    deb="$(package .deb)"
    if [ -n "$deb" ]; then
      if sudo -n true 2> /dev/null; then
        echo "== Installing $deb (apt)"
        sudo apt-get install -y "$deb"
        test_app /usr/bin/codalus "$results/deb" no
        sudo apt-get remove --purge -y codalus
        bash -c "$check_removed" || fail "removing the .deb left files behind"
      elif [ -n "$runtime" ]; then
        echo "== Installing $deb (apt, in an Ubuntu container)"
        "$runtime" run --rm -v "$packages:/packages:ro" docker.io/library/ubuntu:24.04 bash -euc "
          apt-get update -qq && apt-get install -y -qq /packages/$(basename "$deb") > /dev/null
          codalus --version | grep -q '^Codalus IDE '
          apt-get remove --purge -y -qq codalus > /dev/null
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
          codalus --version | grep -q '^Codalus IDE '
          dnf remove -y -q codalus > /tmp/dnf.log 2>&1 || { cat /tmp/dnf.log; exit 1; }
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
