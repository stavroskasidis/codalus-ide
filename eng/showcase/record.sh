#!/usr/bin/env bash
# Records one of the website's looping videos (website/screenshots.txt lists each with its story): starts Codalus on a
# virtual 1280x800 display with its own user data, plays the story (eng/showcase/<name>.sh) with xdotool while ffmpeg
# records the screen, then cuts the recording between the story's start and end marks, joins its end to its start with a
# short crossfade so it loops, and encodes it for the web (H.264, no sound, a few hundred KB).
#
# Usage: eng/showcase/record.sh <name> [--codalus <path>] [--out <dir>] [--keep]
#   <name>          a story: eng/showcase/<name>.sh
#   --codalus       the shell to record (default: the Release build, src/Codalus.Shell/bin/Release/net11.0/codalus)
#   --out           where the video goes (default: website/wwwroot/assets/screenshots)
#   --keep          keeps the work folder (the raw recording, the app's log and the story's marks)
#
# Needs Linux with Xvfb, xdotool, ffmpeg and the Inter font (the site's screenshots are Linux's); nothing else runs on
# display :99 meanwhile. The story's fixture is made in a work folder under /tmp, trusted in the user data, and removed
# afterwards; nothing reaches the network but what the fixture restores from NuGet.
#
# A story is a bash file that defines:
#   prepare   makes the fixture in "$WORK/src" and sets OPEN (codalus's arguments) and, optionally, SETTINGS (extra
#             lines for settings.json) and LOAD (seconds the solution takes to load, default 30)
#   setup     optional: gets the window ready before the recording starts (a tool window open, a first build)
#   teardown  optional: ends what prepare or setup started (a stand-in server)
#   story     plays it: mark start ... mark end around what the video shows, with the helpers below
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
name="${1:?usage: eng/showcase/record.sh <name> [--codalus <path>] [--out <dir>] [--keep]}"
shift
codalus="$root/src/Codalus.Shell/bin/Release/net11.0/codalus"
out="$root/website/wwwroot/assets/screenshots"
keep=0
while [ $# -gt 0 ]; do
  case "$1" in
    --codalus) codalus="$2"; shift 2 ;;
    --out) out="$2"; shift 2 ;;
    --keep) keep=1; shift ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done
story_file="$root/eng/showcase/$name.sh"
[ -f "$story_file" ] || { echo "no story $story_file" >&2; exit 2; }
[ -x "$codalus" ] || { echo "no shell at $codalus (dotnet build src/Codalus.Shell -c Release)" >&2; exit 2; }
for tool in Xvfb xdotool ffmpeg; do
  command -v "$tool" > /dev/null || { echo "$tool isn't installed" >&2; exit 2; }
done

WORK="/tmp/codalus-showcase/$name"
rm -rf "$WORK"
mkdir -p "$WORK/src" "$WORK/ud/config" "$WORK/ud/state"
export DISPLAY=:99
APP_PID=""
XVFB_PID=""
FF_PID=""
cleanup() {
  if declare -F teardown > /dev/null; then teardown || true; fi
  [ -n "$FF_PID" ] && kill -INT "$FF_PID" 2> /dev/null || true
  [ -n "$APP_PID" ] && pkill -TERM -s "$APP_PID" 2> /dev/null || true
  sleep 1
  [ -n "$APP_PID" ] && pkill -KILL -s "$APP_PID" 2> /dev/null || true
  [ -n "$XVFB_PID" ] && kill "$XVFB_PID" 2> /dev/null || true
  if [ "$keep" = 0 ]; then rm -rf "$WORK"; else echo "kept $WORK"; fi
}
trap cleanup EXIT

# --- Helpers for the stories ---------------------------------------------------------------------------------------

# mark <label>: the time of a moment in the story (start and end cut the video).
mark() { echo "$(date +%s.%N) $1" >> "$WORK/marks.txt"; }
# rest: the pointer goes where it hides nothing.
rest() { xdotool mousemove 1180 420; }
# click <x> <y>
click() { xdotool mousemove --sync "$1" "$2"; sleep 0.3; xdotool click 1; sleep 0.2; }
# typed <delay ms> <text>: typed like a person, a key at a time.
typed() { xdotool type --delay "$1" "$2"; }
# pixel <x> <y>: the screen's color there, as "r g b".
pixel() {
  ffmpeg -loglevel error -f x11grab -video_size 1280x800 -i :99 -vframes 1 -vf "crop=1:1:$1:$2" -f rawvideo -pix_fmt rgb24 - \
    | od -An -tu1 -N3 | tr -s ' ' | sed 's/^ //'
}
# until_pixel <seconds> <x> <y> <test>: waits until the test (bash arithmetic on r, g and b) holds at that point.
until_pixel() {
  local deadline=$((SECONDS + $1)) r g b
  while [ $SECONDS -lt $deadline ]; do
    # shellcheck disable=SC2034 # the test reads them
    read -r r g b <<< "$(pixel "$2" "$3")"
    if (($4)); then return 0; fi
    sleep 0.3
  done
  echo "timed out waiting for $4 at ($2, $3)" >&2
  return 1
}
# debugging: the status bar is orange (a debug session is paused or running).
debugging() { until_pixel "${1:-60}" 1 796 'r > 150 && b < 60'; }
# shot <name>: a still of the screen in the work folder, to check a story while writing it.
shot() { ffmpeg -y -loglevel error -f x11grab -video_size 1280x800 -i :99 -vframes 1 "$WORK/$1.png"; }

# --- The recording ----------------------------------------------------------------------------------------------------

# shellcheck source=/dev/null
source "$story_file"
OPEN=()
SETTINGS=""
LOAD=30
prepare

trusted=""
for dir in "$WORK"/src/*/; do trusted="$trusted${trusted:+,}\"${dir%/}\""; done
cat > "$WORK/ud/config/settings.json" << EOF
{
  "workbench.colorTheme": "dark",
  "workbench.setupCompleted": true,
  "editor.autoClosingBrackets": false,
  "welcome.showOnStartup": false${SETTINGS:+,
$SETTINGS}
}
EOF
echo "{\"folders\":[$trusted]}" > "$WORK/ud/state/trusted-folders.json"

Xvfb :99 -screen 0 1280x800x24 -nolisten tcp > "$WORK/xvfb.log" 2>&1 &
XVFB_PID=$!
sleep 2
# Sign-ins in memory, and the app (and what it runs, whose environment a video can show) gets none of this machine's
# agent, cloud, token or key variables, nor those that make Connect to an agent treat this as a remote session.
hidden=()
while IFS='=' read -r var _; do
  case "$var" in
    AI_AGENT* | ANTHROPIC_* | AWS_* | AZURE_* | CLAUDE* | GH_* | GITHUB_* | *TOKEN* | *SECRET* | *PASSWORD* | *_KEY | *_KEY_* | \
      NO_BROWSER | SSH_CONNECTION | SSH_CLIENT | SSH_TTY) hidden+=(-u "$var") ;;
  esac
done < <(env)
CODALUS_CREDENTIAL_STORE=memory setsid env "${hidden[@]}" "$codalus" --user-data-dir "$WORK/ud" --skip-setup "${OPEN[@]}" > "$WORK/app.log" 2>&1 &
APP_PID=$!
sleep "$LOAD"
rest
sleep 1
: > "$WORK/marks.txt"
if declare -F setup > /dev/null; then setup; rest; sleep 1; fi

ffmpeg -y -loglevel error -f x11grab -framerate 30 -video_size 1280x800 -i :99 -c:v libx264 -preset ultrafast -qp 0 "$WORK/raw.mkv" &
FF_PID=$!
sleep 1
mark recording
story
sleep 0.5
kill -INT "$FF_PID"
wait "$FF_PID" || true
FF_PID=""

# --- The cut ----------------------------------------------------------------------------------------------------------

at() { awk -v m="$1" '$2 == m { print $1 }' "$WORK/marks.txt"; }
t0="$(at recording)"
start="$(awk -v a="$(at start)" -v b="$t0" 'BEGIN { printf "%.3f", a - b }')"
length="$(awk -v a="$(at end)" -v b="$(at start)" 'BEGIN { printf "%.3f", a - b }')"
fade=0.8

# Xvfb has no compositor: a popup is a black box for a frame before it paints. Those frames (many pure black pixels,
# which no theme draws) are dropped, and fps= repeats the frame before in their place.
flashes="$(ffmpeg -loglevel error -i "$WORK/raw.mkv" -vf "format=gray,lut=y='if(lte(val,1),255,0)',signalstats,metadata=print:key=lavfi.signalstats.YAVG:file=-" -f null - \
  | awk '/^frame:/ { split($2, f, ":"); n = f[2] } /YAVG/ { split($1, v, "="); if (v[2] > 0.3) printf "+eq(n,%d)", n }')"
# shellcheck disable=SC2016 # the quotes are ffmpeg's; the list expands
select="${flashes:+select='not(0${flashes})',}"
[ -n "$flashes" ] && echo "dropped $(grep -o 'eq' <<< "$flashes" | wc -l) flash frames"

# The loop: the video starts $fade into the story and ends crossfading into the story's first $fade, which leads
# straight into the video's first frame.
ffmpeg -y -loglevel error -i "$WORK/raw.mkv" -filter_complex "
  [0:v]${select}fps=30,trim=start=$start:duration=$length,setpts=PTS-STARTPTS,split[a][b];
  [a]trim=start=$fade,setpts=PTS-STARTPTS[body];
  [b]trim=duration=$fade,setpts=PTS-STARTPTS[head];
  [body][head]xfade=transition=fade:duration=$fade:offset=$(awk -v l="$length" -v f="$fade" 'BEGIN { print l - 2 * f }'),format=yuv420p[v]" \
  -map "[v]" -an -c:v libx264 -preset veryslow -crf 30 -tune animation -movflags +faststart "$out/$name.mp4"
echo "$out/$name.mp4: $(du -k "$out/$name.mp4" | cut -f1) KB, $(awk -v l="$length" -v f="$fade" 'BEGIN { print l - f }') s"
