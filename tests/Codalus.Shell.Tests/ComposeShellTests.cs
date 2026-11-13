using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Codalus.Debugging;
using Codalus.Tests;
using Codalus.Workbench.Containers;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// Compose in fast mode in the shell (M63, ADR 0102) on samples/Containers/Compose (two .NET services and one that
/// isn't): with a stand-in engine that records its commands, Compose Up writes the IDE's override and a rerun restarts
/// only the service whose build changed; with a real engine running Linux containers (CI's Linux runner), Debug Compose
/// debugs both services in one session, one restarts alone while the other runs, and an edit reaches one.
/// </summary>
public sealed class ComposeShellTests
{
    private static async Task<(ShellFixture Fixture, string Folder)> OpenAsync()
    {
        var f = ShellFixture.Create();
        var folder = Path.Combine(f.CopySample("Containers"), "Compose");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Compose.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 2 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 120);
        return (f, folder);
    }

    /// <summary>
    /// A stand-in for docker: it writes each command line to <paramref name="log"/>, says it runs Linux containers,
    /// has no <c>compose config --format json</c> (as podman-compose, so the files are read), and does the rest.
    /// </summary>
    private static string StandInEngine(string folder, string log)
    {
        var script = Path.Combine(folder, "docker-stand-in");
        StandInScript.Write(script, $$"""
            #!/bin/sh
            printf '%s\n' "$*" >> '{{log}}'
            case "$1" in
              version) echo linux ;;
              compose)
                case "$*" in
                  *" config "*) echo "unknown flag: --format" >&2; exit 1 ;;
                esac ;;
            esac
            exit 0
            """);
        return script;
    }

    private static string[] Commands(string log) => File.Exists(log) ? File.ReadAllLines(log) : [];

    [AvaloniaFact]
    public async Task Compose_up_runs_the_dotnet_services_on_their_build_and_a_rerun_restarts_only_the_changed_one()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in engine is a shell script (Linux and macOS).");
        var (f, folder) = await OpenAsync();
        await using var fixture = f;
        var log = Path.Combine(f.UserData, "engine.log");
        await f.Shell.Workbench.Settings.SetAsync(ContainerEngine.ToolSetting, StandInEngine(f.UserData, log), SettingScope.User, CancellationToken.None);
        var file = Path.Combine(folder, "compose.yaml");

        Assert.True(await f.Shell.ComposeUpAsync(file));
        var ideOverride = f.Shell.ComposeOverridePath()!;
        Assert.Equal(Path.Combine(folder, ".codalus", "state", "Compose.slnx", ComposeFastMode.OverrideFileName), ideOverride);
        Assert.Equal("*", File.ReadAllLines(Path.Combine(folder, ".codalus", "state", ".gitignore"))[^1]); // never committed

        // Each .NET service runs its Debug build on its base stage; the service that isn't .NET is left as written.
        var services = ComposeYaml.Parse(await File.ReadAllTextAsync(ideOverride, TestContext.Current.CancellationToken))!["services"]!.AsObject();
        Assert.Equal(["front", "back"], services.Select(s => s.Key));
        Assert.Equal(["dotnet", "/app/Back.dll"], services["back"]!["entrypoint"]!.AsArray().Select(e => (string?)e));
        Assert.Equal("base", (string?)services["back"]!["build"]!["target"]);
        Assert.Equal(Path.Combine(folder, "Back", "bin", "Debug", "net10.0"), (string?)services["back"]!["volumes"]![0]!["source"]);
        var up = $"compose -f {file} -f {ideOverride} -p compose up -d --build";
        Assert.Equal(1, Commands(log).Count(c => c == up));
        Assert.DoesNotContain(Commands(log), c => c.Contains(" restart", StringComparison.Ordinal));

        // A rerun with nothing changed restarts nothing; after an edit to the back, the back alone.
        Assert.True(await f.Shell.ComposeUpAsync(file));
        Assert.Equal(2, Commands(log).Count(c => c == up));
        Assert.DoesNotContain(Commands(log), c => c.Contains(" restart", StringComparison.Ordinal));
        var back = Path.Combine(folder, "Back", "Program.cs");
        await File.WriteAllTextAsync(back, (await File.ReadAllTextAsync(back, TestContext.Current.CancellationToken)).Replace("back on", "the back on", StringComparison.Ordinal), TestContext.Current.CancellationToken);
        Assert.True(await f.Shell.ComposeUpAsync(file));
        Assert.Equal([$"compose -f {file} -f {ideOverride} -p compose restart back"], Commands(log).Where(c => c.Contains(" restart", StringComparison.Ordinal)));

        // A build of a code change restarts its service too.
        await File.WriteAllTextAsync(back, (await File.ReadAllTextAsync(back, TestContext.Current.CancellationToken)).Replace("the back on", "the back, on", StringComparison.Ordinal), TestContext.Current.CancellationToken);
        Assert.True(await f.Shell.BuildAsync(Codalus.Projects.Protocol.BuildTargets.Build, Path.Combine(folder, "Back", "Back.csproj")));
        await ShellFixture.PumpUntil(() => Commands(log).Count(c => c == $"compose -f {file} -f {ideOverride} -p compose restart back") == 2, seconds: 30, () => string.Join('\n', Commands(log)));
        Assert.DoesNotContain(Commands(log), c => c.EndsWith(" restart front", StringComparison.Ordinal));

        // One service alone: stopped, restarted; one that isn't .NET can't join a debug session, and none is debugged.
        Assert.True(await f.Shell.ComposeServiceAsync(ShellHost.ComposeServiceAction.Stop, "idle"));
        Assert.Contains($"compose -f {file} -f {ideOverride} -p compose stop idle", Commands(log));
        Assert.True(await f.Shell.ComposeServiceAsync(ShellHost.ComposeServiceAction.Restart, "front"));
        Assert.Contains($"compose -f {file} -f {ideOverride} -p compose restart front", Commands(log));
        Assert.False(await f.Shell.ComposeServiceAsync(ShellHost.ComposeServiceAction.Debug, "idle"));
        Assert.False(await f.Shell.ComposeServiceAsync(ShellHost.ComposeServiceAction.Detach, "front"));

        // The override shows as a read-only document.
        await f.Shell.ShowComposeOverrideAsync();
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == ideOverride);
        Assert.True(f.Shell.ActiveEditor!.Model.IsReadOnly);

        // With fast mode off, the compose file runs as written.
        await f.Shell.Workbench.Settings.SetAsync(ContainerLaunch.FastModeSetting, false, SettingScope.User, CancellationToken.None);
        Assert.True(await f.Shell.ComposeUpAsync(file));
        Assert.Contains($"compose -f {file} -p compose up -d --build", Commands(log));
        Assert.True(await f.Shell.ComposeDownAsync(file));
        Assert.Contains($"compose -f {file} -p compose down", Commands(log));
        Assert.False(await f.Shell.ComposeServiceAsync(ShellHost.ComposeServiceAction.Restart, "front")); // the IDE runs no compose app now
    }

    [AvaloniaFact]
    public async Task Debug_compose_debugs_every_dotnet_service_restarts_one_alone_and_takes_an_edit()
    {
        var engine = await ContainerShellTests.RequireEngineAsync();
        var (f, folder) = await OpenAsync();
        await using var fixture = f;
        var file = Path.Combine(folder, "compose.yaml");
        var backSource = Path.Combine(folder, "Back", "Program.cs");
        var frontSource = Path.Combine(folder, "Front", "Program.cs");
        var backLine = Array.FindIndex(File.ReadAllLines(backSource), l => l.Contains("var answer =", StringComparison.Ordinal)) + 1;
        var frontLine = Array.FindIndex(File.ReadAllLines(frontSource), l => l.Contains("var page =", StringComparison.Ordinal)) + 1;
        f.Shell.Debugger.Breakpoints.Toggle(backSource, backLine);
        f.Shell.Debugger.Breakpoints.Toggle(frontSource, frontLine);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };

        string Describe() =>
            $"debugger {f.Shell.Debugger.State}, programs [{string.Join(", ", f.Shell.Debugger.Processes.Select(p => $"{p.Name} {p.Id?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "no id"}{(p.Ended ? " ended" : string.Empty)}"))}]\n"
            + $"Container Tools:\n{Tail(f.Shell.ContainerLog.GetText())}\nDebug Console:\n{Tail(f.Shell.DebugLog.GetText())}";

        // Each program that bound its port: the console logger's event line, once per program. "Now listening on" is in
        // the log twice per program (the console's text and the debug logger's), so counting it let the wait after the
        // back's restart pass before the new back listened, and the front's request found it refused (CI, run 43521735997).
        int Listening() => f.Shell.DebugLog.GetText().Split("Microsoft.Hosting.Lifetime[14]").Length - 1;

        async Task StopsAtAsync(string source, int line)
        {
            await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Stopped, seconds: 120, Describe);
            await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor is { Controller: { } c, Model.Path: var path } && path == source && c.CurrentDebugLine() == line, seconds: 30, Describe);
            await f.RunAsync("debug.continue");
        }

        try
        {
            // Both .NET services start under the debugger in their containers, as programs of one session.
            Assert.True(await f.Shell.ComposeDebugAsync(file), Describe());
            await ShellFixture.PumpUntil(() => f.Shell.Debugger.Processes.Count == 2 && f.Shell.Debugger.Processes.All(p => p.Id is not null), seconds: 300, Describe);
            Assert.Equal(["back", "front"], f.Shell.Debugger.Processes.Select(p => p.Name).Order(StringComparer.Ordinal));
            await ShellFixture.PumpUntil(() => Listening() >= 2, seconds: 120, Describe);
            var front = (await engine.ComposeServiceContainerAsync("compose", "front", null, CancellationToken.None))!;
            var port = await engine.HostPortAsync(front.Name, "8080/tcp", CancellationToken.None);

            // A request stops in the back (the front waits for it), then in the front.
            var request = http.GetStringAsync($"http://127.0.0.1:{port}/", TestContext.Current.CancellationToken);
            await StopsAtAsync(backSource, backLine);
            await StopsAtAsync(frontSource, frontLine);
            await ShellFixture.PumpUntil(() => request.IsCompleted, seconds: 60, Describe);
            Assert.StartsWith("front, then back on ", await request, StringComparison.Ordinal);

            // The back restarts alone, under the debugger again; the front's program goes on.
            var frontProgram = f.Shell.Debugger.Processes.Single(p => p.Name == "front");
            var listened = Listening();
            Assert.True(await f.Shell.ComposeServiceAsync(ShellHost.ComposeServiceAction.Restart, "back"), Describe());
            await ShellFixture.PumpUntil(() => f.Shell.Debugger.Processes.Count == 3 && f.Shell.Debugger.Processes[^1].Id is not null, seconds: 180, Describe);
            await ShellFixture.PumpUntil(() => Listening() > listened, seconds: 120, Describe); // the new back listens
            Assert.True(f.Shell.Debugger.Processes.Single(p => p.Name == "back" && p.Ended) is not null);
            Assert.Contains(f.Shell.Debugger.Processes, p => p == frontProgram && !p.Ended);
            request = http.GetStringAsync($"http://127.0.0.1:{port}/", TestContext.Current.CancellationToken);
            await StopsAtAsync(backSource, backLine);
            await StopsAtAsync(frontSource, frontLine);
            await ShellFixture.PumpUntil(() => request.IsCompleted, seconds: 60, Describe);

            // Under the Codalus debugger, an edit to the back applies to it in its container (its build is this machine's).
            f.Shell.Debugger.Breakpoints.Clear();
            if (f.Shell.Debugger.CodalusAdapters.Count > 0)
            {
                await ShellFixture.PumpUntil(() => f.Shell.DebugEdits.IsActive, seconds: 120, Describe);
                var editor = (await f.Shell.OpenFileAsync(backSource))!;
                var text = editor.Model.Text.Text;
                editor.Model.Text.Replace(text.IndexOf("back on", StringComparison.Ordinal), "back on".Length, "edited back on");
                await f.RunAsync("run.applyCodeChanges");
                await ShellFixture.PumpUntil(() => f.Shell.DebugLog.GetText().Contains("Hot Reload: the code changes were applied", StringComparison.Ordinal), seconds: 120, Describe);
                Assert.StartsWith("front, then edited back on ", await http.GetStringAsync($"http://127.0.0.1:{port}/", TestContext.Current.CancellationToken), StringComparison.Ordinal);
            }
            else
            {
                TestContext.Current.SendDiagnosticMessage("The edit wasn't applied: netcoredbg debugs the services here (no Codalus debugger bundle for the container's platform).");
            }

            await f.Shell.Debugger.StopAsync();
            await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle, seconds: 90, Describe);
        }
        finally
        {
            if (f.Shell.Debugger.IsActive)
            {
                await f.Shell.Debugger.StopAsync();
            }

            await f.Shell.ComposeDownAsync(file);
            await engine.RunAsync(["rmi", ComposeFastMode.ImageName("compose", "front"), ComposeFastMode.ImageName("compose", "back")], CancellationToken.None);
        }
    }

    private static string Tail(string text) => text.Length <= 4000 ? text : text[^4000..];
}
