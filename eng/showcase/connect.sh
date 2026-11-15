# shellcheck shell=bash disable=SC2034,SC2154 # a story: record.sh sources it, sets root and WORK, and reads OPEN, SETTINGS, LOAD
# The "Connect your agent" tab of the home page's AI spotlight (website/screenshots.txt): AI Chat with AI off offers the
# agents, and Connect to Claude shows what it found, the ways to sign in, the exact commands and the consent; Cancel
# leaves everything as it was, so nothing runs. eng/showcase/record.sh plays it.

source "$root/eng/showcase/ai-agent.sh"   # its solution and AI Chat's layout

prepare() {
  _hello_prepare
  OPEN=("$WORK/src/HelloApp/HelloApp.slnx" --open "$WORK/src/HelloApp/src/Greetings/Greeter.cs")
}

teardown() { :; }

# The pointer rests beside the dialog.
rest() { xdotool mousemove 1120 260; }

story() {
  mark start
  sleep 1
  click 917 472; rest; sleep 4.5                # Connect to Claude: Node.js and npx found
  xdotool mousemove --sync 560 487; sleep 2.5    # the command it runs
  click 320 588; rest; sleep 2.5                 # the consent, which enables Connect
  click 938 710; rest; sleep 2                   # Cancel: nothing ran, nothing changed
  mark end
}
