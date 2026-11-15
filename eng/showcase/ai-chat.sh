# shellcheck shell=bash disable=SC2034,SC2154 # a story: record.sh sources it, sets root and WORK, and reads OPEN, SETTINGS, LOAD
# The AI Chat tab of the home page's AI spotlight (website/screenshots.txt): a method selected in the editor, a question
# that @-mentions it, and the answer with its code block. The model is eng/showcase/model-stand-in.py with the answers
# below: nothing reaches a model. eng/showcase/record.sh plays it.

source "$root/eng/showcase/ai-agent.sh"   # its solution, model settings and layout

answers() {
  cat << 'EOF'
[
  { "when": "test", "say": "An xUnit test for each case `Greet` has:\n\n```csharp\npublic class GreeterTests\n{\n    [Theory]\n    [InlineData(\"Ada\", \"Hello, Ada!\")]\n    [InlineData(\"Grace\", \"Hello, Grace!\")]\n    public void Greet_names_the_person(string name, string expected) =>\n        Assert.Equal(expected, Greeter.Greet(name));\n}\n```\n\nPut it in a test project that references `Greetings`." }
]
EOF
}

story() {
  sleep 0.5
  mark start
  sleep 1
  # The method, selected in the editor.
  click 116 244; xdotool key Home shift+End; sleep 1; rest; sleep 0.6
  click 1000 662; rest
  typed 75 "Write a test for "; typed 120 "@sel"; sleep 1.2; xdotool key Tab; sleep 1
  xdotool key Return
  sleep 8
  mark end
}
