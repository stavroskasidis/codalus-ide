using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DaedalusStudio.Debugging;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Shell.Views;
using DaedalusStudio.Workbench.Build;
using DaedalusStudio.Workbench.Containers;
using DaedalusStudio.Workbench.Debugging;

namespace DaedalusStudio.Shell.Tests;

/// <summary>
/// Containers in the shell (M17a, ADR 0042) on samples/Containers: Add Docker Support, the .dcproj, and with a container
/// engine running Linux containers, Run and Debug through the "Container (Dockerfile)" profile and the Containers window.
/// </summary>
public sealed class ContainerShellTests
{
    private static async Task<(ShellFixture Fixture, string Folder)> OpenAsync()
    {
        var f = ShellFixture.Create();
        var folder = f.CopySample("Containers");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Containers.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 2 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 120);
        return (f, folder);
    }

    /// <summary>The engine for these tests, or a skip (a failure on CI's Linux runner) when none runs Linux containers.</summary>
    private static async Task<ContainerEngine> RequireEngineAsync()
    {
        var engine = ContainerEngine.Resolve(Environment.GetEnvironmentVariable("DAEDALUS_TEST_CONTAINER_TOOL"));
        var (os, error) = await engine.ServerOsAsync(CancellationToken.None);
        if (os is not "linux")
        {
            var reason = os is null ? $"No container engine answers ({engine.Name}: {error})" : $"{engine.Name} runs {os} containers, not Linux ones";
            if (Environment.GetEnvironmentVariable("CI") == "true" && OperatingSystem.IsLinux())
            {
                Assert.Fail(reason);
            }

            Assert.Skip(reason);
        }

        return engine;
    }

    [AvaloniaFact]
    public async Task Add_docker_support_writes_the_files_and_the_dcproj_loads_as_a_compose_project()
    {
        var (f, folder) = await OpenAsync();
        await using var fixture = f;
        var compose = f.Shell.Solutions.Projects.Values.Single(p => p.IsComposeProject);
        Assert.Equal("loaded", compose.State);
        Assert.Equal(Path.Combine(folder, "docker-compose.yml"), f.Shell.ComposeFileFor(compose.Path));
        Assert.Equal("web", ShellHost.ComposeServiceName(compose.Path));

        // Without its Dockerfile and container profile, Add Docker Support writes them as the fixture has them.
        var web = Path.Combine(folder, "Web", "Web.csproj");
        var expected = await File.ReadAllTextAsync(Path.Combine(folder, "Web", "Dockerfile"));
        File.Delete(Path.Combine(folder, "Web", "Dockerfile"));
        var settings = Path.Combine(folder, "Web", "Properties", "launchSettings.json");
        await File.WriteAllTextAsync(settings, """{ "profiles": { "http": { "commandName": "Project" } } }""");
        var dockerfile = await f.Shell.AddDockerSupportAsync(web);
        Assert.Equal(Path.Combine(folder, "Web", "Dockerfile"), dockerfile);
        Assert.Equal(expected.ReplaceLineEndings("\n"), await File.ReadAllTextAsync(dockerfile!));
        Assert.Contains(LaunchSettings.Read(settings, out _), p => p.CommandName == LaunchProfile.Docker && p.Name == DockerSupport.ContainerProfileName);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == dockerfile);

        // The Containers window, whatever the engine says.
        await f.Shell.ShowContainersAsync();
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<ContainersView>().Any());
    }

    [AvaloniaFact]
    public async Task A_project_runs_and_debugs_in_a_container_through_its_container_profile()
    {
        var engine = await RequireEngineAsync();
        Assert.SkipWhen(DebugAdapterLocator.Resolve() is null && Environment.GetEnvironmentVariable("CI") != "true", "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        var (f, folder) = await OpenAsync();
        await using var fixture = f;
        var web = Path.Combine(folder, "Web", "Web.csproj");
        var name = DockerSupport.ContainerName(web);
        f.Shell.Runs.SetStartupProject(web);
        f.Shell.Runs.SetProfile(web, DockerSupport.ContainerProfileName);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        async Task<int> HostPortAsync()
        {
            int? port = null;
            await ShellFixture.PumpUntil(() =>
            {
                port ??= engine.HostPortAsync(name, "8080/tcp", CancellationToken.None).GetAwaiter().GetResult();
                return port is not null;
            }, seconds: 300);
            return port!.Value;
        }

        try
        {
            // Ctrl+F5: the base stage with the build output, the app through exec, its output in its Run channel.
            await f.Shell.StartWithoutDebuggingAsync();
            var port = await HostPortAsync();
            await ShellFixture.PumpUntil(() => f.Shell.Runs.Running.Any(r => r.Channel?.GetText().Contains("Now listening on", StringComparison.Ordinal) == true), seconds: 120);
            var answer = await http.GetStringAsync($"http://127.0.0.1:{port}/", TestContext.Current.CancellationToken);
            Assert.StartsWith("Hello from ", answer, StringComparison.Ordinal);

            // The Containers window lists it first, with its environment and ports.
            await f.Shell.ShowContainersAsync();
            Assert.Equal(name, f.Shell.ContainersWindow.Containers[0].Name);
            Assert.True(f.Shell.ContainersWindow.Containers[0].IsOurs);
            f.Shell.ContainersWindow.Selected = f.Shell.ContainersWindow.Containers[0];
            await ShellFixture.PumpUntil(() => f.Shell.ContainersWindow.Environment.Any(e => e.Name == "ASPNETCORE_HTTP_PORTS") && f.Shell.ContainersWindow.Ports.Any(p => p.Name == "8080/tcp"));
            f.Shell.ContainersWindow.Tab = 4;
            await ShellFixture.PumpUntil(() => f.Shell.ContainersWindow.Files.Any(e => e.Name == "app"));

            // Stopping the program removes its container.
            f.Shell.StopRunning();
            await ShellFixture.PumpUntil(() => engine.InspectAsync(name, CancellationToken.None).GetAwaiter().GetResult() is null, seconds: 60);

            // F5: netcoredbg copied into the container launches the app; a breakpoint stops a request.
            var program = Path.Combine(folder, "Web", "Program.cs");
            var line = Array.FindIndex(File.ReadAllLines(program), l => l.Contains("var greeting =", StringComparison.Ordinal)) + 1;
            f.Shell.Debugger.Breakpoints.Toggle(program, line);
            await f.Shell.StartDebuggingAsync();
            port = await HostPortAsync();
            await ShellFixture.PumpUntil(() => f.Shell.DebugLog.GetText().Contains("Now listening on", StringComparison.Ordinal), seconds: 180);
            var request = http.GetStringAsync($"http://127.0.0.1:{port}/", TestContext.Current.CancellationToken);
            await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Stopped, seconds: 60);
            await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor is { Controller: { } c } && c.CurrentDebugLine() == line);
            await ShellFixture.PumpUntil(() => f.Shell.Locals.Roots.Any(r => r.Name == "host"));
            f.Shell.Debugger.Breakpoints.Clear();
            f.Shell.Run("debug.continue");
            await ShellFixture.PumpUntil(() => request.IsCompleted, seconds: 60);
            Assert.StartsWith("Hello from ", await request, StringComparison.Ordinal);
            await f.Shell.Debugger.StopAsync();
            await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle, seconds: 60);
            await ShellFixture.PumpUntil(() => engine.InspectAsync(name, CancellationToken.None).GetAwaiter().GetResult() is null, seconds: 60);
        }
        finally
        {
            f.Shell.StopRunning();
            await engine.RemoveAsync(name, CancellationToken.None);
            await engine.RunAsync(["rmi", name + ":dev"], CancellationToken.None);
        }
    }
}
