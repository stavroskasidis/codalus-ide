# A story (eng/showcase/record.ps1 plays it): AI Console runs the machine's own, signed-in claude CLI beside the code.
# Add File puts a reference to Greeter.cs in its prompt, a small change is asked for, and the CLI makes the edit with
# its own approval. Needs the claude CLI on PATH, signed in; it calls Claude, so the run uses that account.
#
# The CLI's banner names the account's plan, so the video must never show it: two questions before the recording push
# it out of the console, and the story starts after them.

function Prepare {
    $app = "$Work\src\HelloApp"
    New-Item -ItemType Directory -Force "$app\src\Hello", "$app\src\Greetings" | Out-Null
    $files = @{
        '.gitignore' = "bin/`nobj/`n.codalus/`n"
        'HelloApp.slnx' = @'
<Solution>
  <Project Path="src/Greetings/Greetings.csproj" />
  <Project Path="src/Hello/Hello.csproj" />
</Solution>

'@
        'src/Greetings/Greetings.csproj' = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>

'@
        'src/Greetings/Greeter.cs' = @'
namespace Greetings;

public static class Greeter
{
    public static string Greet(string name) => $"Hello, {name}!";

    public static int Count(string name) => name.Length;
}

'@
        'src/Hello/Hello.csproj' = @'
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

'@
        'src/Hello/Program.cs' = @'
using Greetings;

string[] names = ["Ada", "Grace", "Linus"];

foreach (var name in names)
{
    Console.WriteLine(Greeter.Greet(name));
}

'@
    }
    # The CLI asks before it edits (whatever mode the account's own settings choose), so the video shows its approval.
    $files['.claude/settings.json'] = "{ `"permissions`": { `"defaultMode`": `"default`" } }`n"
    New-Item -ItemType Directory -Force "$app\.claude" | Out-Null
    foreach ($file in $files.GetEnumerator()) { Set-Content -NoNewline -Path (Join-Path $app $file.Key) -Value $file.Value }
    & git -C $app init -q -b main
    & git -C $app -c core.autocrlf=false add .
    & git -C $app -c user.name=Demo -c user.email=demo@example.com commit -q -m Hello
    $script:Open = @("$app\HelloApp.slnx", '--open', "$app\src\Greetings\Greeter.cs")
    $script:Settings = @'
  "ai.enabled": true,
  "ai.agents": { "claude": { "command": "npx", "args": ["-y", "@agentclientprotocol/claude-agent-acp@0.94.0"], "template": "claude@0.94.0" } }
'@
    $script:Load = 40
}

function Setup {
    Click 973 589; Wait 0.5; Click 973 589          # the Error List and Output close
    Key ctrl+q; Wait 0.8; Typed 30 'View: AI Console'; Wait 1.5; Key Return; Wait 2
    Drag 500 582 500 340                             # the console gets the lower half
    Wait 12                                          # the CLI starts
    Click 500 700; Key Down; Wait 0.3; Key Return    # the folder trusted, if the CLI asks (nothing at its prompt)
    Wait 3
    # Two questions, so the banner (the account's plan) scrolls out of the console.
    Click 500 710; Rest
    Typed 10 'List the files of this solution, one per line, each with a few words on what it holds. No other text.'
    Key Return; Wait 40
    Click 500 710; Rest
    Typed 10 'Show Greeter.cs as it is, in a code block. No other text.'
    Key Return; Wait 25
    Click 400 250; Rest                              # Greeter.cs is the active document
}

# Until the CLI shows its diff (a green added line) and asks to make the edit.
function Wait-Diff([double] $Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        for ($y = 440; $y -lt 760; $y += 4) {
            $r, $g, $b = Pixel 30 $y
            if ($g -gt 90 -and $r -lt 40 -and $b -lt 40) { return }
        }
        Start-Sleep -Milliseconds 300
    }
    throw 'the CLI showed no edit'
}

function Story {
    Wait 0.5
    Mark start
    Wait 1
    # Add File types a reference to Greeter.cs into the CLI's prompt.
    Key ctrl+q; Wait 0.6; Typed 45 'Add File to AI Console'; Wait 1; Key Return; Wait 1.2
    Typed 65 'Make Greet say Hello! for an empty name. Only edit the file.'; Wait 0.6
    Key Return
    # Its own approval: the diff, and Yes.
    Wait-Diff 60
    Wait 2.5
    Key Return
    Wait 6
    Mark end
}
