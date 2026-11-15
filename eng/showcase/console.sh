# shellcheck shell=bash disable=SC2034,SC2154 # a story: record.sh sources it, sets root and WORK, and reads OPEN, SETTINGS, LOAD
# The "Console apps and libraries" tab of the home page's app models (website/screenshots.txt): the Test Explorer runs a
# library's tests, a failing one shows its message, the code is fixed, and the run goes green.
# eng/showcase/record.sh plays it.

source "$root/eng/showcase/editor.sh"   # its HelloApp

prepare() {
  local app="$WORK/src/HelloApp"
  _hello_prepare
  mkdir -p "$app/tests/Greetings.Tests"
  cat > "$app/tests/Greetings.Tests/Greetings.Tests.csproj" << 'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3.mtp-v2" Version="4.0.1" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Greetings\Greetings.csproj" />
  </ItemGroup>
</Project>
EOF
  cat > "$app/tests/Greetings.Tests/GreeterTests.cs" << 'EOF'
using Xunit;

namespace Greetings.Tests;

public class GreeterTests
{
    [Theory]
    [InlineData("Ada", "Hello, Ada!")]
    [InlineData("Grace", "Hello, Grace!")]
    public void Greet_names_the_person(string name, string expected) =>
        Assert.Equal(expected, Greeter.Greet(name));

    [Fact]
    public void Greet_without_a_name_says_hello() =>
        Assert.Equal("Hello!", Greeter.Greet(""));

    [Fact]
    public void Count_counts_the_letters() =>
        Assert.Equal(3, Greeter.Count("Ada"));
}
EOF
  sed -i 's|  <Project Path="src/Hello/Hello.csproj" />|  <Project Path="src/Hello/Hello.csproj" />\n  <Project Path="tests/Greetings.Tests/Greetings.Tests.csproj" />|' "$app/HelloApp.slnx"
  # Greet's body on a line of its own, so the fix fits beside the Test Explorer.
  printf 'namespace Greetings;\n\npublic static class Greeter\n{\n    public static string Greet(string name) =>\n        $"Hello, {name}!";\n\n    public static int Count(string name) => name.Length;\n}\n' \
    > "$app/src/Greetings/Greeter.cs"
  printf 'using Greetings;\n\nstring[] names = ["Ada", "Grace", "Linus"];\n\nforeach (var name in names)\n{\n    Console.WriteLine(Greeter.Greet(name));\n}\n' > "$app/src/Hello/Program.cs"
  git -C "$app" add .
  git -C "$app" -c user.name=Demo -c user.email=demo@example.com commit -qm "Tests"
  OPEN=("$app/HelloApp.slnx" --open "$app/src/Greetings/Greeter.cs")
  LOAD=60
}

setup() {
  xdotool key ctrl+shift+b; sleep 40
  click 973 589; sleep 0.5; click 973 589        # the Error List and the build's Output close
  xdotool key ctrl+e t; sleep 4                  # the Test Explorer, with the tests it found
  xdotool mousemove --sync 285 400; xdotool mousedown 1; sleep 0.2
  xdotool mousemove --sync 340 400; sleep 0.1; xdotool mousemove --sync 430 400; sleep 0.2; xdotool mouseup 1; sleep 1
  click 45 234; sleep 1                          # GreeterTests, expanded
  click 1257 82; sleep 1                         # the Solution Explorer closes: the editor gets its width
}

story() {
  sleep 0.5
  mark start
  sleep 1
  click 23 111; rest; sleep 7                    # Run All: one test fails
  click 185 282; rest; sleep 3                   # its message

  # The fix, in Greeter.Greet: an empty name says hello.
  click 1000 262; rest; xdotool key End; for _ in $(seq 18); do xdotool key Left; done; sleep 0.5
  typed 95 "name.Length == 0 ? "; xdotool key Escape; typed 95 '"Hello!" : '; xdotool key Escape; sleep 0.5
  xdotool key Escape; rest; xdotool key ctrl+s; sleep 1.2
  xdotool key ctrl+r a; sleep 8                  # Run All again (Ctrl+R, A): green
  mark end
}
