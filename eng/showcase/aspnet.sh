# shellcheck shell=bash disable=SC2034,SC2154 # a story: record.sh sources it, sets root and WORK, and reads OPEN, SETTINGS, LOAD
# The ASP.NET Core tab of the home page's app models (website/screenshots.txt): the web app running beside its
# Endpoints Explorer, and requests sent from a .http file with their responses. eng/showcase/record.sh plays it.

prepare() {
  local app="$WORK/src/TodoApi"
  cp -r "$root/samples/Web" "$app"
  # The sample's test markers and its greeting go; the requests are the sample's.
  find "$app" -name '*.cs' -exec sed -i 's| // \[[a-z-]*\]$||' {} +
  sed -i 's/Hello from the Web fixture/Hello, World!/; s/"Write the fixture"/"Plan the API"/; s/"Test the endpoints"/"Map the endpoints"/' \
    "$app/WebApi/Program.cs"
  OPEN=("$app/Web.slnx" --open "$app/WebApi/WebApi.http")
  LOAD=45
}

setup() {
  xdotool key ctrl+q; sleep 0.8; typed 30 "Endpoints Explorer"; sleep 1.5; xdotool key Return; sleep 8
  xdotool key ctrl+F5; sleep 40                    # the web app builds and starts
  click 1254 115; sleep 8                        # the Endpoints Explorer reads the routes
  click 973 589; sleep 0.5; click 973 589        # the Error List and the app's Output close
  click 50 148; sleep 3                          # GET /: the HTTP Response window opens beside the file
  xdotool mousemove --sync 498 300; xdotool mousedown 1; sleep 0.2
  xdotool mousemove --sync 540 300; sleep 0.1; xdotool mousemove --sync 590 300; sleep 0.2; xdotool mouseup 1; sleep 1
}

story() {
  sleep 0.5
  mark start
  sleep 1.5
  click 50 236; rest; sleep 3.5                  # GET /api/todos/1: JSON
  click 50 341; rest; sleep 3.5                  # POST: 201 Created

  # A request of its own at the end of the file, sent from its glyph: the list, with the new to-do.
  click 300 481; xdotool key ctrl+End; sleep 0.4
  xdotool key Return; typed 120 "###"; xdotool key Return Return; sleep 0.3
  typed 110 "GET {{"; sleep 1.2; typed 110 "WebApi"; sleep 0.5; xdotool key Escape; typed 110 "_HostAddress}}/api/todos"
  sleep 0.3; xdotool key Escape; rest; sleep 1.2
  click 50 516; rest; sleep 4
  mark end
}
