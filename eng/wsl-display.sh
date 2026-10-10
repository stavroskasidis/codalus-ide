#!/usr/bin/env bash
# Gives a WSL distro what the smoke test and the browser tests need, without root (source it: it exports variables).
#   source eng/wsl-display.sh [--display] [--browser]
# - With --display, a display: WSLg's X server when it runs, or xvfb-run when it's installed. Otherwise Xvfb, unpacked from the distro's own packages (apt-get download,
#   dpkg -x) into ~/.local/xvfb and started on :99. It listens on the abstract socket, because WSLg mounts
#   /tmp/.X11-unix read-only. Its keymap compiler path is compiled in as /usr/bin; a copy of the binary looks in /tmp/xkb
#   instead, where the unpacked xkbcomp is linked.
# - With --browser: a Chromium browser on PATH. An installed one is used; otherwise Google's Chrome for Testing (pinned
#   below) is unpacked into ~/.local/chrome-for-testing once.
# The alternative, with root: sudo apt install xvfb (eng/smoke-test.sh then uses xvfb-run), and install Chrome.

daedalus_x_server() {
  local socket="/tmp/.X11-unix/X$1"
  [[ -S "$socket" ]] || grep -q " @$socket\$" /proc/net/unix 2> /dev/null
}

daedalus_wsl_display() {
  local display="${DISPLAY:-:0}"
  display="${display#:}"
  display="${display%%.*}"
  if daedalus_x_server "$display" || command -v xvfb-run > /dev/null; then
    return 0
  fi

  local root="$HOME/.local/xvfb"
  if [[ ! -x "$root/root/usr/bin/Xvfb-local" ]]; then
    echo "== Xvfb into $root (WSLg's X server isn't running)"
    mkdir -p "$root/debs" "$root/root"
    local missing=()
    local package
    for package in xvfb xserver-common libxfont2 x11-xkb-utils; do
      dpkg-query -W -f='${Status}' "$package" 2> /dev/null | grep -q "install ok installed" || missing+=("$package")
    done
    (cd "$root/debs" && apt-get download "${missing[@]}" > /dev/null) || return 1
    local deb
    for deb in "$root"/debs/*.deb; do
      dpkg -x "$deb" "$root/root"
    done

    local xvfb="$root/root/usr/bin/Xvfb"
    [[ -x "$xvfb" ]] || xvfb=/usr/bin/Xvfb
    python3 - "$xvfb" "$root/root/usr/bin/Xvfb-local" << 'EOF' || return 1
import sys
data = open(sys.argv[1], 'rb').read()
old, new = b'/usr/bin\x00', b'/tmp/xkb\x00'
assert data.count(old) == 1, f'{sys.argv[1]}: {data.count(old)} occurrences of /usr/bin'
open(sys.argv[2], 'wb').write(data.replace(old, new))
EOF
    chmod +x "$root/root/usr/bin/Xvfb-local"
  fi

  mkdir -p /tmp/xkb
  ln -sf "$( [[ -x "$root/root/usr/bin/xkbcomp" ]] && echo "$root/root/usr/bin/xkbcomp" || echo /usr/bin/xkbcomp)" /tmp/xkb/xkbcomp
  export DISPLAY=:99
  if daedalus_x_server 99; then
    return 0
  fi

  rm -f /tmp/.X99-lock
  LD_LIBRARY_PATH="$root/root/usr/lib/x86_64-linux-gnu${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" \
    nohup "$root/root/usr/bin/Xvfb-local" :99 -screen 0 1920x1080x24 -nolisten tcp > /tmp/xvfb-99.log 2>&1 &
  local i
  for i in $(seq 1 50); do
    daedalus_x_server 99 && { echo "== Xvfb on :99"; return 0; }
    sleep 0.2
  done

  echo "error: Xvfb didn't start (/tmp/xvfb-99.log):" >&2
  tail -5 /tmp/xvfb-99.log >&2
  return 1
}

daedalus_wsl_browser() {
  local name
  for name in google-chrome google-chrome-stable microsoft-edge microsoft-edge-stable chromium chromium-browser; do
    command -v "$name" > /dev/null && return 0
  done

  local version=155.0.8059.39
  local root="$HOME/.local/chrome-for-testing"
  if [[ ! -x "$root/$version/chrome-linux64/chrome" ]]; then
    echo "== Chrome for Testing $version into $root"
    mkdir -p "$root/$version"
    curl -sSfL -o "$root/$version/chrome-linux64.zip" "https://storage.googleapis.com/chrome-for-testing-public/$version/linux64/chrome-linux64.zip" || return 1
    python3 -c "import zipfile, sys; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])" "$root/$version/chrome-linux64.zip" "$root/$version" || return 1
    rm -f "$root/$version/chrome-linux64.zip"
    chmod +x "$root/$version/chrome-linux64/chrome" "$root/$version/chrome-linux64/chrome_crashpad_handler"
  fi

  mkdir -p "$root/bin"
  ln -sf "$root/$version/chrome-linux64/chrome" "$root/bin/google-chrome"
  export PATH="$root/bin:$PATH"
}

if [[ " $* " == *" --display "* ]]; then
  daedalus_wsl_display || return 1 2> /dev/null || exit 1
fi

if [[ " $* " == *" --browser "* ]]; then
  daedalus_wsl_browser || return 1 2> /dev/null || exit 1
fi
