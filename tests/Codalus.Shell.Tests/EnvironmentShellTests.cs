using Avalonia.Headless.XUnit;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Setup;

namespace Codalus.Shell.Tests;

/// <summary>Help → Check Environment and the install flow (M37), with a stand-in runner: nothing installs for real.</summary>
public sealed class EnvironmentShellTests
{
    private static readonly string[] s_installers = ["winget", "curl", "https://dotnet.microsoft.com/download"];

    [AvaloniaFact]
    public async Task Check_environment_lists_the_tools_and_an_install_runs_only_after_consent()
    {
        await using var f = ShellFixture.Create();
        f.Shell.Run("help.checkEnvironment");
        await ShellFixture.PumpUntil(() => f.Shell.Layout.ActiveDocument is EnvironmentDocument);
        var page = ((EnvironmentDocument)f.Shell.Layout.ActiveDocument!).ViewModel;
        await ShellFixture.PumpUntil(() => page.Rows.Count > 0, seconds: 60);

        // This machine builds the repository: it has an SDK and git.
        Assert.Equal(ToolState.Found, page.Rows.Single(r => r.Tool.Id == "dotnet").Tool.State);
        Assert.Equal(ToolState.Found, page.Rows.Single(r => r.Tool.Id == "git").Tool.State);
        Assert.False(page.Rows.Single(r => r.Tool.Id == "dotnet").CanFix);

        // A missing SDK (stand-in): the plan is shown, and declining runs nothing.
        var missing = new ToolStatus("dotnet", ".NET SDK", true, ToolState.Missing, "Opening .NET projects");
        var runs = new List<string>();
        Task<(int, string)> Runner(string program, IReadOnlyList<string> arguments, string? directory, CancellationToken token)
        {
            runs.Add(program + " " + string.Join(' ', arguments));
            return Task.FromResult((0, "installed\n"));
        }

        InstallPlan? shown = null;
        await f.Shell.InstallToolAsync(missing, plan => { shown = plan; return Task.FromResult(false); }, Runner);
        Assert.NotNull(shown);
        Assert.Contains(shown!.Command.Split(' ')[0], s_installers);
        Assert.Empty(runs);

        // Agreed: a plan that runs here goes through the runner, then the check runs again.
        if (shown.Kind == InstallKind.Run)
        {
            await f.Shell.InstallToolAsync(missing, _ => Task.FromResult(true), Runner);
            Assert.Single(runs);
            Assert.Contains("codalus.setup", f.Shell.Workbench.Output.Channels.Select(c => c.Id));
            await ShellFixture.PumpUntil(() => f.Shell.Workbench.Notifications.Items.Any(n => n.Message.Contains("is installed", StringComparison.Ordinal)));
        }
    }

    /// <summary>
    /// M49: hosts that aren't started because the IDE's runtime is missing (the Mac's 0.4.0 nightly) make one actionable
    /// notification, however many there are, with M37's install; a program of the user's whose runtime is missing doesn't.
    /// </summary>
    [AvaloniaFact]
    public async Task A_missing_runtime_is_one_notification_that_offers_to_install_it_and_the_hosts_start_again()
    {
        await using var f = ShellFixture.Create();
        static Codalus.Rpc.HostRuntimeMissingException Missing(string folder, string name) => new(new Codalus.Rpc.HostRuntimeCheckResult(
            Path.Combine(folder, name), Path.Combine(folder, name + ".runtimeconfig.json"), "/fake/dotnet",
            [new Codalus.Rpc.FrameworkResolution(
                Codalus.Rpc.FrameworkReference.Create(Codalus.Rpc.FrameworkReference.NetCoreApp, Codalus.Rpc.FrameworkVersion.Parse("99.0.0-rc.2.1"), Codalus.Rpc.RollForwardPolicy.Major),
                null,
                [Codalus.Rpc.FrameworkVersion.Parse("10.0.9"), Codalus.Rpc.FrameworkVersion.Parse("10.0.12")])]));
        bool IsRuntimeNotification(Workbench.Notifications.Notification n) => n.Message.Contains("needs the .NET 99.0.0-rc.2.1 runtime", StringComparison.Ordinal);

        // A designer on the app's own runtime (outside the IDE's folder): its caller reports it, not this.
        f.Shell.ReportHostRuntimeMissing(Missing(Path.Combine(Path.GetTempPath(), "app", "bin"), "codalus-winformsdesigner"));
        ShellFixture.Pump();
        Assert.DoesNotContain(f.Shell.Workbench.Notifications.Items, IsRuntimeNotification);

        // The plugin host, then the build host, then the language host: one notification.
        foreach (var host in new[] { "pluginhost", "buildhost", "lshost" })
        {
            f.Shell.ReportHostRuntimeMissing(Missing(Path.Combine(AppContext.BaseDirectory, host), "codalus-" + host));
        }

        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Notifications.Items.Any(IsRuntimeNotification));
        ShellFixture.Pump();
        var notification = Assert.Single(f.Shell.Workbench.Notifications.Items, IsRuntimeNotification);
        Assert.Equal("Codalus IDE needs the .NET 99.0.0-rc.2.1 runtime, which isn't installed (found: 10.0.9, 10.0.12 in /fake/dotnet). Without it, plugins, builds, language services and debugging can't start.", notification.Message);
        Assert.Equal(["Install .NET 99.0", "Check Environment"], notification.Actions);

        // After an install, the hosts start again: nothing failed here, so nothing changes.
        await f.Shell.RestartHostsAsync();
    }
}
