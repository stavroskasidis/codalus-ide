# A story (eng/showcase/record.ps1 plays it): the ".NET Framework" tab of the home page's app models. A net48 console
# app (samples/NetFramework's, greeting a few names) stops at a breakpoint under the desktop CLR's debugger: a data tip,
# a step over, and Continue to the next name.

function Prepare {
    $sln = "$Work\src\Greetings"
    New-Item -ItemType Directory -Force $sln | Out-Null
    $samples = Join-Path $Root 'samples/NetFramework'
    Copy-Item -Recurse "$samples/ConsoleApp", "$samples/Library" $sln
    Copy-Item "$samples/Directory.Build.props" $sln
    Remove-Item "$sln/ConsoleApp/App.config"
    Set-Content -NoNewline -Path "$sln/Greetings.slnx" -Value @"
<Solution>
  <Project Path="ConsoleApp/ConsoleApp.csproj" />
  <Project Path="Library/Library.csproj" />
</Solution>

"@
    Set-Content -NoNewline -Path "$sln/ConsoleApp/Program.cs" -Value @"
using System;
using Library;

namespace ConsoleApp
{
    internal static class Program
    {
        private static void Main()
        {
            var names = new[] { "Ada", "Grace", "Linus" };
            foreach (var name in names)
            {
                var greeter = new Greeter(name);
                var message = greeter.Greet();
                Console.WriteLine(message);
            }

            Console.WriteLine("Runtime: " + Greeter.RuntimeName);
        }
    }
}

"@
    $script:Open = @("$sln\Greetings.slnx", '--open', "$sln\ConsoleApp\Program.cs")
    $script:Load = 60
}

function Setup {
    Key ctrl+shift+b; Wait 40                        # a first build, so F5 starts straight away
}

function Story {
    Wait 0.5
    Mark start
    Wait 1
    # A breakpoint on the WriteLine, and F5: the desktop CLR stops there, with the values inline and in Locals.
    Click 500 419; Key F9; Wait 1
    Key F5
    Rest
    Debugging 60
    Wait 3
    # A data tip on message, a step over, and Continue to the next name.
    Hover 272 401; Wait 3
    Rest; Wait 0.5
    Key F10; Wait 2
    Key F5; Wait 2.5
    Hover 272 401; Wait 2.5
    Rest; Wait 0.5
    # The breakpoint off, the Debug Console, and the program runs to its end.
    Key F9; Wait 0.8
    Click 160 756; Rest; Wait 0.8
    Key F5
    Wait 4
    Mark end
}
