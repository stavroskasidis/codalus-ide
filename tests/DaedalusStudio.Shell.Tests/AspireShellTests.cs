using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DaedalusStudio.Debugging;
using DaedalusStudio.Shell.Views;
using DaedalusStudio.Workbench.Debugging;

namespace DaedalusStudio.Shell.Tests;

/// <summary>
/// Aspire in the shell (M17b, ADR 0043) on samples/Containers/Aspire: Ctrl+F5 and F5 on the AppHost run and debug its
/// two projects through the IDE execution protocol, the Aspire window shows the resources (and a Redis container that
/// waits for an explicit start, never pulled), their console output and commands, and the dashboard's address.
/// </summary>
public sealed class AspireShellTests
{
    private static async Task<(ShellFixture Fixture, string Folder)> OpenAsync()
    {
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
        }
    }

    [AvaloniaFact]
    public async Task F5_on_the_apphost_debugs_its_projects_in_one_session()
    {
        Assert.SkipWhen(DebugAdapterLocator.Resolve() is null && Environment.GetEnvironmentVariable("CI") != "true", "netcoredbg is not installed (eng/get-netcoredbg.sh).");
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
        }
    }
}
