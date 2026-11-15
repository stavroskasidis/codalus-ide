# shellcheck shell=bash disable=SC2034,SC2154 # a story: record.sh sources it, sets root and WORK, and reads OPEN, SETTINGS, LOAD
# The "An agent that uses the IDE" tab of the home page's AI spotlight (website/screenshots.txt): a question in AI Chat
# that the assistant answers after reading a file with the IDE's tools, its step folded into one line. The model is
# eng/showcase/model-stand-in.py with the answers below: nothing reaches a model. eng/showcase/record.sh plays it.

source "$root/eng/showcase/editor.sh"   # its HelloApp
port=5317

prepare() {
  local app="$WORK/src/HelloApp"
  _hello_prepare
  printf 'using Greetings;\n\nstring[] names = ["Ada", "Grace", "Linus"];\n\nforeach (var name in names)\n{\n    Console.WriteLine(Greeter.Greet(name));\n}\n' > "$app/src/Hello/Program.cs"
  git -C "$app" -c user.name=Demo -c user.email=demo@example.com commit -qam "Greet everyone"
  OPEN=("$app/HelloApp.slnx" --open "$app/src/Greetings/Greeter.cs")
  SETTINGS="  \"ai.enabled\": true,
  \"ai.provider\": \"openai\",
  \"ai.openai.endpoint\": \"http://127.0.0.1:$port/v1\",
  \"ai.model\": \"local-model\""
  answers > "$WORK/answers.json"
  python3 -I "$root/eng/showcase/model-stand-in.py" "$port" "$WORK/answers.json" > "$WORK/model.log" 2>&1 &
  MODEL_PID=$!
}

# The stand-in model's answers (eng/showcase/model-stand-in.py).
answers() {
  cat << 'EOF'
[
  { "when": "empty name", "say": "Let me read Greeter first.", "tool": "read_file", "args": { "path": "src/Greetings/Greeter.cs" } },
  { "when": "tool", "say": "### Empty names\n\n`Greeter.Greet` formats whatever it gets, so an empty name gives `Hello, !`. Check it first:\n\n```csharp\npublic static string Greet(string name) =>\n    string.IsNullOrWhiteSpace(name) ? \"Hello!\" : $\"Hello, {name}!\";\n```\n\n- The callers in `Program.cs` keep working.\n- Add a test for the empty name." }
]
EOF
}

# The pointer rests in the editor's empty part, off the chat.
rest() { xdotool mousemove 520 640; }

teardown() { kill "$MODEL_PID" 2> /dev/null; }

setup() {
  xdotool key ctrl+q; sleep 0.8; typed 30 "View: AI Chat"; sleep 1.5; xdotool key Return; sleep 3
  click 973 589; sleep 0.5; click 973 589        # the Error List and Output close
  xdotool mousemove --sync 993 300; xdotool mousedown 1; sleep 0.2
  xdotool mousemove --sync 900 300; sleep 0.1; xdotool mousemove --sync 760 300; sleep 0.2; xdotool mouseup 1; sleep 1
}

story() {
  sleep 0.5
  mark start
  sleep 1
  click 1000 662; rest
  typed 75 "Does Greeter handle an empty name?"; sleep 0.8
  xdotool key Return
  sleep 9                                        # it reads Greeter.cs, then answers
  mark end
}
