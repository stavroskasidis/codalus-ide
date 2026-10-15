using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Debugging;
using Codalus.Shell.Views;
using Codalus.Workbench.Debugging;

namespace Codalus.Shell.Tests;

/// <summary>
/// Aspire in the shell (M17b, ADR 0043) on samples/Containers/Aspire: Ctrl+F5 and F5 on the AppHost run and debug its
/// two projects through the IDE execution protocol, the Aspire window shows the resources (and a Redis container that
/// waits for an explicit start, never pulled), their console output and commands, and the dashboard's address.
/// </summary>
public sealed class AspireShellTests
{
    private static async Task<(ShellFixture Fixture, string Folder)> OpenAsync()
    {
        await AspireRuntime.RequireContainerEngineAsync();
        var f = ShellFixture.Create();
        var folder = f.CopySample("Containers");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Aspire.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 3 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 300);
        var appHost = Path.Combine(folder, "Aspire", "AppHost", "AppHost.csproj");
        Assert.True(f.Shell.IsAspireHost(appHost));
        f.Shell.Runs.SetStartupProject(appHost);
        return (f, folder);
    }

    private static string? Endpoint(ShellFixture f, string resource) =>
        f.Shell.AspireWindow.Resources.FirstOrDefault(r => r.Name == resource)?.Resource.Urls.FirstOrDefault(u => !u.IsInternal)?.Url;

    private static Task RunningAsync(ShellFixture f) =>
        ShellFixture.PumpUntil(
            () => f.Shell.AspireWindow.Resources.Count(r => r.Resource.Type == "Project" && r.Resource.State == "Running") == 2
                && f.Shell.AspireWindow.Resources.Any(r => r.Name == "cache"),
            seconds: 300);

    [AvaloniaFact]
    public async Task Ctrl_F5_on_the_apphost_runs_its_projects_and_the_aspire_window_shows_them()
    {
        var started = DateTime.Now;
        var (f, _) = await OpenAsync();
        await using var fixture = f;
        try
        {
            await f.Shell.StartWithoutDebuggingAsync();
            await RunningAsync(f);
            await ShellFixture.PumpUntil(() => f.Shell.AspireWindow.HasDashboard, seconds: 60);
            Assert.StartsWith("http://localhost:", f.Shell.AspireWindow.DashboardUrl, StringComparison.Ordinal);
            Assert.Contains("/login?t=", f.Shell.AspireWindow.DashboardUrl, StringComparison.Ordinal);
            var cache = f.Shell.AspireWindow.Resources.Single(r => r.Name == "cache");
            Assert.Equal("Container", cache.Resource.Type);
            Assert.NotEqual("Running", cache.Resource.State);

            // The projects run as the IDE's programs: their Run channels, and their logs in the window.
            await ShellFixture.PumpUntil(() => f.Shell.Runs.Running.Any(r => r.Name == "Api") && f.Shell.Runs.Running.Any(r => r.Name == "Frontend"));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            Assert.StartsWith("Frontend: Sunny, from the api", await http.GetStringAsync(Endpoint(f, "frontend"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
            f.Shell.AspireWindow.Selected = f.Shell.AspireWindow.Resources.Single(r => r.Name == "api");
            await ShellFixture.PumpUntil(() => f.Shell.AspireWindow.Logs.Contains("Now listening on", StringComparison.Ordinal), seconds: 60);
            Assert.DoesNotContain('\u001b', f.Shell.AspireWindow.Logs); // the console's colors are left out
            Assert.Contains(f.Shell.AspireWindow.Environment, e => e.Name == "OTEL_SERVICE_NAME");

            // Stop the api through its command: the IDE ends its program.
            f.Shell.ShowAspire();
            await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<AspireView>().Any());
            Assert.Null(await f.Shell.AspireWindow.StopAsync());
            await ShellFixture.PumpUntil(() => !f.Shell.Runs.Running.Any(r => r.Name == "Api"), seconds: 60);
            await ShellFixture.PumpUntil(() => f.Shell.AspireWindow.Resources.Single(r => r.Name == "api").Resource.State != "Running", seconds: 60);
        }
        finally
        {
            f.Shell.StopRunning();
            await ShellFixture.PumpUntil(() => !f.Shell.AspireWindow.IsRunning && f.Shell.Runs.Running.Count == 0, seconds: 60);
            await AspireRuntime.StopOrchestratorsAsync(started);
        }
    }

    [AvaloniaFact]
    public async Task F5_on_the_apphost_debugs_its_projects_in_one_session()
    {
        Assert.SkipWhen(DebugAdapterLocator.Resolve() is null && Environment.GetEnvironmentVariable("CI") != "true", "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        var started = DateTime.Now;
        var (f, folder) = await OpenAsync();
        await using var fixture = f;
        var api = Path.Combine(folder, "Aspire", "Api", "Program.cs");
        var line = Array.FindIndex(File.ReadAllLines(api), l => l.Contains("var forecast =", StringComparison.Ordinal)) + 1;
        f.Shell.Debugger.Breakpoints.Toggle(api, line);
        try
        {
            await f.Shell.StartDebuggingAsync();
            await RunningAsync(f);
            await ShellFixture.PumpUntil(() => f.Shell.Debugger.Processes.Count == 3 && f.Shell.Debugger.Processes.All(p => p.Id is not null), seconds: 120);
            Assert.Equal(["Api", "AppHost", "Frontend"], f.Shell.Debugger.Processes.Select(p => p.Name).Order(StringComparer.Ordinal));

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            var request = http.GetStringAsync(Endpoint(f, "frontend"), TestContext.Current.CancellationToken);
            await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Stopped, seconds: 90);
            await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor is { Controller: { } c } && c.CurrentDebugLine() == line);
            f.Shell.Debugger.Breakpoints.Clear();
            f.Shell.Run("debug.continue");
            await ShellFixture.PumpUntil(() => request.IsCompleted, seconds: 60);
            Assert.StartsWith("Frontend: Sunny, from the api", await request, StringComparison.Ordinal);

            await f.Shell.Debugger.StopAsync();
            await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle && !f.Shell.AspireWindow.IsRunning, seconds: 90);
        }
        finally
        {
            if (f.Shell.Debugger.IsActive)
            {
                await f.Shell.Debugger.StopAsync();
            }

            await AspireRuntime.StopOrchestratorsAsync(started);
        }
    }
}

/// <summary>
/// Aspire 13.1's orchestrator wants a container runtime: without one (GitHub's macOS runner, a Mac without Docker or
/// Podman) the Aspire tests skip with the reason, and only CI's Linux runner, which has Docker, fails instead.
/// </summary>
internal static class AspireRuntime
{
    public static async Task RequireContainerEngineAsync()
    {
        // Aspire's orchestrator checks its container runtime within a short limit: on a loaded CI runner (Windows,
        // three test projects at once) that check timed out. The AppHosts these tests start inherit a longer limit.
        Environment.SetEnvironmentVariable("ASPIRE_DEPENDENCY_CHECK_TIMEOUT", "120");
        var engine = Codalus.Workbench.Containers.ContainerEngine.Resolve(Environment.GetEnvironmentVariable("CODALUS_TEST_CONTAINER_TOOL"));
        var (os, error) = await engine.ServerOsAsync(CancellationToken.None);
        if (os is null)
        {
            var reason = $"Aspire's orchestrator needs a container runtime, and none answers ({engine.Name}: {error})";
            if (Environment.GetEnvironmentVariable("CI") == "true" && OperatingSystem.IsLinux())
            {
                Assert.Fail(reason);
            }

            Assert.Skip(reason);
        }
    }

    /// <summary>
    /// Ends the orchestrator processes started since <paramref name="since"/> that outlived their AppHost (Aspire's
    /// DCP runs detached; a killed AppHost can leave it holding the app's ports).
    /// </summary>
    public static async Task StopOrchestratorsAsync(DateTime since)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var left = System.Diagnostics.Process.GetProcesses().Where(p => p.ProcessName is "dcp" or "dcpctrl" or "dcpproc" && Started(p) >= since).ToList();
            if (left.Count == 0)
            {
                return;
            }

            if (attempt == 19)
            {
                foreach (var process in left)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                    }
                }
            }

            await Task.Delay(500);
        }

        static DateTime Started(System.Diagnostics.Process process)
        {
            try
            {
                return process.StartTime;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                return DateTime.MinValue;
            }
        }
    }
}