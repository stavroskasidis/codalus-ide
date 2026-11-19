using Avalonia.Headless.XUnit;
using Codalus.Projects.Protocol;
using Codalus.Workbench.Build;
using Codalus.Workbench.Debugging.Remote;
using Codalus.Workbench.Setup;

namespace Codalus.Shell.Tests;

/// <summary>
/// The WSL launch profile's .NET (M14c): a start in a distribution without the .NET the app needs offers to install the
/// SDK there, the install runs through <c>wsl.exe</c> and the start runs again; the <c>dotnet</c> in <c>~/.dotnet</c> is
/// found off the PATH; and the app's static web assets get paths WSL reads. A stand-in <c>wsl.exe</c> runs the
/// distribution's commands here, in a folder that plays its home, with a stand-in <c>dotnet</c>; nothing installs.
/// </summary>
public sealed class WslDotnetShellTests
{
    private static readonly string[] s_toolFolders = ["/bin/", "/usr/bin/"];

    [AvaloniaFact]
    public async Task A_distribution_without_the_apps_dotnet_offers_the_sdk_and_runs_it_once_installed()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The stand-in wsl.exe is a shell script: the real one is tested by the smoke test's WSL steps.");
            return;
        }

        await using var f = ShellFixture.Create();
        var root = Directory.CreateTempSubdirectory("codalus-wsl-").FullName;
        try
        {
            var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
            var wsl = StandIns(root, home);
            f.Shell.WslProgram = wsl;

            // An ASP.NET Core 8.0 app, built on Windows: its static web assets' content root is a Windows folder.
            var output = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
            await File.WriteAllTextAsync(Path.Combine(output, "App.dll"), string.Empty, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(output, "App.runtimeconfig.json"), """
                { "runtimeOptions": { "tfm": "net8.0", "frameworks": [
                  { "name": "Microsoft.NETCore.App", "version": "8.0.0" },
                  { "name": "Microsoft.AspNetCore.App", "version": "8.0.0" } ] } }
                """, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(output, "App.staticwebassets.runtime.json"), """{"ContentRoots":["C:\\src\\App\\wwwroot\\"],"Root":{"Children":null,"Asset":null,"Patterns":[{"ContentRootIndex":0,"Pattern":"**","Depth":0}]}}""", TestContext.Current.CancellationToken);
            var info = new RunInfo(Path.Combine(root, "App.csproj"), "net8.0", "Exe", Path.Combine(output, "App.dll"), null, null, null, null);
            var plan = WslLaunch.Apply(new LaunchPlan("wsl.exe", string.Empty, root, new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Development" }, null)
            {
                Wsl = new WslLaunch("Ubuntu", "dotnet", ["exec", "/mnt/c/src/App/bin/App.dll"], "/mnt/c/src/App"),
            });

            // No dotnet at all: the start stops, saying so, with Install .NET SDK 8.0.
            var none = await Assert.ThrowsAsync<RunNotStartedException>(() => f.Shell.PrepareWslAsync(info, plan, TestContext.Current.CancellationToken));
            Assert.Equal(".NET isn't installed in WSL: Ubuntu, so App can't run there.", none.Message);
            var offer = Assert.Single(f.Shell.Workbench.Notifications.Items, n => n.Message == none.Message);
            Assert.Equal(["Install .NET SDK 8.0…"], offer.Actions);

            // The distribution's own dotnet on its PATH, with 6.0 only: what the app needs, and what it has. The second start's
            // notification replaces the first's.
            Dotnet(Path.Combine(root, "usr", "lib", "dotnet", "dotnet"), "6.0.36");
            var old = await Assert.ThrowsAsync<RunNotStartedException>(() => f.Shell.PrepareWslAsync(info, plan, TestContext.Current.CancellationToken));
            Assert.Equal("App needs Microsoft.NETCore.App 8.0.0, Microsoft.AspNetCore.App 8.0.0, which the .NET in WSL: Ubuntu doesn't have (it has 6.0.36), so it can't run there.", old.Message);
            Assert.DoesNotContain(f.Shell.Workbench.Notifications.Items, n => n.Message == none.Message);

            // Install: Linux's choices, dotnet-install picked; it runs through wsl.exe into ~/.dotnet (the stand-in runner
            // puts a stand-in dotnet there), and the start runs again.
            IReadOnlyList<InstallPlan>? offered = null;
            var runs = new List<string>();
            var again = 0;
            await Within(f, f.Shell.InstallDotnetInWslAsync(new WslTarget("Ubuntu", wsl), info, plan, "8.0", () =>
                {
                    again++;
                    return Task.CompletedTask;
                },
                choose: plans =>
                {
                    offered = plans;
                    return Task.FromResult<InstallPlan?>(plans.Single(p => p.Option == "dotnet-install"));
                },
                run: (program, arguments, _, _) =>
                {
                    runs.Add(program + " " + string.Join(' ', arguments));
                    Dotnet(Path.Combine(home, ".dotnet", "dotnet"), "8.0.11");
                    return Task.FromResult((0, "installed\n"));
                }), 60, "the install in WSL");

            Assert.Equal(1, again);
            Assert.Contains(offered!, p => p is { Option: "page" });
            Assert.All(offered!, p => Assert.Null(p.AddsToPath)); // the IDE's own .NET stays as it is
            var run = Assert.Single(runs);
            Assert.StartsWith($"{wsl} -d Ubuntu --exec bash -c curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir \"{home}/.dotnet\"", run, StringComparison.Ordinal);
            Assert.Empty(f.TerminalInstalls);

            // The start now runs on ~/.dotnet's dotnet, which isn't on the PATH, with the static web assets WSL reads.
            var ready = await f.Shell.PrepareWslAsync(info, plan, TestContext.Current.CancellationToken);
            Assert.Equal(Path.Combine(home, ".dotnet", "dotnet"), ready.Wsl!.Program);
            Assert.Contains(WslLaunch.ShellQuote(ready.Wsl.Program) + " exec /mnt/c/src/App/bin/App.dll", ready.ArgumentList![^1], StringComparison.Ordinal);
            var manifest = ready.Environment[WslDotnet.StaticWebAssetsVariable];
            Assert.StartsWith(f.Shell.Paths.Cache, manifest, StringComparison.Ordinal);
            Assert.Contains("\"ContentRoots\":[\"/mnt/c/src/App/wwwroot/\"]", await File.ReadAllTextAsync(manifest, TestContext.Current.CancellationToken), StringComparison.Ordinal);
            Assert.Contains(WslLaunch.ShellQuote($"{WslDotnet.StaticWebAssetsVariable}={manifest}"), ready.ArgumentList[^1], StringComparison.Ordinal);

            // A manifest the launch profile names itself stays.
            var own = plan with { Environment = new Dictionary<string, string>(plan.Environment) { [WslDotnet.StaticWebAssetsVariable] = "/mine.json" } };
            Assert.Equal("/mine.json", (await f.Shell.PrepareWslAsync(info, own, TestContext.Current.CancellationToken)).Environment[WslDotnet.StaticWebAssetsVariable]);

            // A distribution that isn't there: wsl.exe's message.
            var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Shell.PrepareWslAsync(info, plan with { Wsl = plan.Wsl! with { Distribution = "Gone" } }, TestContext.Current.CancellationToken));
            Assert.Equal("WSL: Gone didn't answer (exit code 1): There is no distribution with the supplied name.", missing.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A stand-in <c>wsl.exe</c>: <c>-d Gone</c> isn't there; otherwise it skips <c>-d</c> and <c>--cd</c> and runs the
    /// command after <c>--exec</c> here, with <paramref name="home"/> as the home folder and a <c>PATH</c> of the
    /// distribution's .NET folder (<c>usr/lib/dotnet</c>) and the few tools the probe uses (none of this machine's own dotnet). A login shell (<c>sh -lc</c>) is a plain one
    /// there, so this machine's profile files don't change the PATH.
    /// </summary>
    private static string StandIns(string root, string home)
    {
        var tools = Directory.CreateDirectory(Path.Combine(root, "tools")).FullName;
        static string Real(string tool) => s_toolFolders.Select(folder => folder + tool).First(File.Exists);
        foreach (var tool in new[] { "readlink", "dirname" })
        {
            File.CreateSymbolicLink(Path.Combine(tools, tool), Real(tool));
        }

        // sh -lc is sh -c: a login shell reads /etc/profile, which sets a PATH of its own.
        Executable(Path.Combine(tools, "sh"), $"#!{Real("sh")}\nif [ \"$1\" = -lc ]; then shift; set -- -c \"$@\"; fi\nexec {Real("sh")} \"$@\"\n");
        var wsl = Path.Combine(root, "wsl");
        Executable(wsl, $"""
            #!/bin/sh
            while [ $# -gt 0 ]; do
              case "$1" in
                -d) if [ "$2" = Gone ]; then echo "There is no distribution with the supplied name." >&2; exit 1; fi; shift 2 ;;
                --cd) shift 2 ;;
                --exec) shift; break ;;
                *) break ;;
              esac
            done
            exec /usr/bin/env -i HOME='{home}' PATH='{root}/usr/lib/dotnet:{tools}' "$@"
            """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
        return wsl;
    }

    /// <summary>A stand-in <c>dotnet</c> that has the .NET and ASP.NET Core runtimes of <paramref name="version"/>.</summary>
    private static void Dotnet(string path, string version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var shared = Path.Combine(Path.GetDirectoryName(path)!, "shared");
        Executable(path, $"#!/bin/sh\n[ \"$1\" = --list-runtimes ] || exit 1\necho 'Microsoft.AspNetCore.App {version} [{shared}/Microsoft.AspNetCore.App]'\necho 'Microsoft.NETCore.App {version} [{shared}/Microsoft.NETCore.App]'\n");
    }

    private static void Executable(string path, string script)
    {
        File.WriteAllText(path, script);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static async Task Within(ShellFixture f, Task operation, int seconds, string what)
    {
        var timeout = Task.Delay(TimeSpan.FromSeconds(seconds), TestContext.Current.CancellationToken);
        while (!operation.IsCompleted && !timeout.IsCompleted)
        {
            ShellFixture.Pump();
            await Task.WhenAny(operation, timeout, Task.Delay(15, TestContext.Current.CancellationToken));
        }

        if (!operation.IsCompleted)
        {
            Assert.Fail($"Timed out after {seconds} s: {what}." + (f.Shell.Dialogs.Request is { } dialog ? $" A dialog waits for an answer: \"{dialog.Title}\"." : string.Empty));
        }

        await operation;
    }
}
