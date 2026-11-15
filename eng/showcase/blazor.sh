# shellcheck shell=bash disable=SC2034,SC2154 # a story: record.sh sources it, sets root and WORK, and reads OPEN, SETTINGS, LOAD
# The Blazor tab of the home page's app models (website/screenshots.txt): C# inside a .razor component, typed with
# completion in the markup, Go to Definition from an event handler, and Quick Info. eng/showcase/record.sh plays it.

prepare() {
  local app="$WORK/src/BlazorApp"
  cp -r "$root/samples/Blazor" "$app"
  OPEN=("$app/Blazor.slnx" --open "$app/BlazorApp/Components/Pages/Counter.razor")
  LOAD=50
}

setup() {
  click 973 589; sleep 0.5; click 973 589        # the Error List and Output close
  sleep 2; click 1154 736                        # not now: the web language servers (HTML, CSS) aren't needed here
}

story() {
  mark start
  sleep 1
  # C# in the markup, typed with completion.
  click 700 236; rest; xdotool key End Return; sleep 0.4
  typed 110 "<p>Doubled: @(curr"; sleep 1.2; xdotool key Tab; sleep 0.3; typed 110 " * 2)</p>"; xdotool key Escape
  sleep 1.5
  # Go to Definition from the markup's event handler to its method, and Quick Info on the field it changes.
  xdotool mousemove --sync 470 288; sleep 0.4; xdotool click 1; sleep 0.3; xdotool key F12; rest; sleep 1.5
  xdotool mousemove --sync 190 412; sleep 3; rest; sleep 1
  mark end
}
