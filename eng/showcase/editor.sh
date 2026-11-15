# shellcheck shell=bash disable=SC2034,SC2154 # a story: record.sh sources it, sets root and WORK, and reads OPEN, SETTINGS, LOAD
# The home page's first video (website/screenshots.txt): a line typed with IntelliSense, a breakpoint, F5, a data tip,
# Continue, and the program run to its end. eng/showcase/record.sh plays it.

_hello_prepare() {
  local app="$WORK/src/HelloApp"
  mkdir -p "$app/src/Hello" "$app/src/Greetings"
  printf 'bin/\nobj/\n.codalus/\n' > "$app/.gitignore"
  cat > "$app/HelloApp.slnx" << 'EOF'
<Solution>
  <Project Path="src/Greetings/Greetings.csproj" />
  <Project Path="src/Hello/Hello.csproj" />
</Solution>
EOF
  cat > "$app/src/Greetings/Greetings.csproj" << 'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
EOF
  cat > "$app/src/Greetings/Greeter.cs" << 'EOF'
namespace Greetings;

public static class Greeter
{
    public static string Greet(string name) => $"Hello, {name}!";

    public static int Count(string name) => name.Length;
}
EOF
  cat > "$app/src/Hello/Hello.csproj" << 'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net11.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Greetings\Greetings.csproj" />
  </ItemGroup>
</Project>
EOF
  printf 'using Greetings;\n\nstring[] names = ["Ada", "Grace", "Linus"];\n\nforeach (var name in names)\n{\n    \n}\n\nConsole.WriteLine("Done.");\n' > "$app/src/Hello/Program.cs"
  git -C "$app" init -q -b main
  git -C "$app" add .
  git -C "$app" -c user.name=Demo -c user.email=demo@example.com commit -q -m "Hello"
}

prepare() {
  _hello_prepare
  OPEN=("$WORK/src/HelloApp/HelloApp.slnx" --open "$WORK/src/HelloApp/src/Hello/Program.cs")
}

story() {
  sleep 1
  mark start
  # A line typed with IntelliSense (line 7, its indentation already there).
  click 700 218; xdotool key End; sleep 0.6
  typed 110 "var message = Greeter."; sleep 1.8   # completion, with Quick Info
  typed 160 "Gr"; sleep 0.7; xdotool key Tab; sleep 0.4
  typed 110 "(name"; sleep 0.3; xdotool key Escape; typed 110 ");"; sleep 0.5
  xdotool key Return
  typed 85 "Console.WriteLine(message"; sleep 0.4; xdotool key Escape; typed 85 ");"; sleep 0.6; xdotool key Escape; sleep 0.4; xdotool key Escape
  rest; sleep 1.5

  # A breakpoint, save, F5: it stops there with a PerfTip and the Locals.
  xdotool key F9; sleep 1.2
  xdotool key ctrl+s; sleep 0.8
  xdotool key F5
  debugging 60
  sleep 3.5

  # A data tip on message, then Continue to the next name.
  xdotool mousemove --sync 168 219; sleep 3
  rest; sleep 0.6
  xdotool key F5; sleep 3.2

  # The breakpoint off, the Debug Console, and the program runs to its end.
  xdotool key F9; sleep 0.8
  xdotool key ctrl+q; sleep 0.6; typed 70 "Debug Console"; sleep 1; xdotool key Return; sleep 1.2
  xdotool key F5
  sleep 4
  mark end
}
