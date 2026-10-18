using Avalonia.Headless.XUnit;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Setup;

namespace Codalus.Shell.Tests;

/// <summary>Help → Check Environment and the install flow (M37), with a stand-in runner: nothing installs for real.</summary>
public sealed class EnvironmentShellTests
{
    private static readonly string[] s_installers = ["winget", "curl", "https://dotnet.microsoft.com/download"];

    /// <summary>The variables an install changes in this process (M49), restored after the test.</summary>
    private static readonly string[] s_dotnetVariables = ["DOTNET_ROOT", "PATH", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64"];

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
    /// notification, however many there are, with M37's install; a program that isn't one of the IDE's hosts doesn't.
    /// Once that notification is answered or closed, a later failure (a solution opened afterwards) shows it again.
    /// </summary>
    [AvaloniaFact]
    public async Task A_missing_runtime_is_one_notification_at_a_time_that_offers_to_install_it()
    {
        await using var f = ShellFixture.Create();
        bool IsRuntimeNotification(Workbench.Notifications.Notification n) => n.Message.Contains("needs the .NET 99.0.0-rc.2.1 runtime", StringComparison.Ordinal);

        // A designer on the app's own runtime: its caller reports it, not this.
        Assert.False(f.Shell.ReportHostRuntimeMissing(Missing(Path.Combine(Path.GetTempPath(), "app", "bin"), "codalus-winformsdesigner")));
        ShellFixture.Pump();
        Assert.DoesNotContain(f.Shell.Workbench.Notifications.Items, IsRuntimeNotification);

        // The plugin host, then the build host, then the language host (a development build's, outside the shell's
        // folder: the IDE's hosts by their names): one notification.
        foreach (var host in new[] { "pluginhost", "buildhost", "lshost" })
        {
            Assert.True(f.Shell.ReportHostRuntimeMissing(Missing(Path.Combine(Path.GetTempPath(), "dev", host), "codalus-" + host)));
        }

        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Notifications.Items.Any(IsRuntimeNotification));
        ShellFixture.Pump();
        var notification = Assert.Single(f.Shell.Workbench.Notifications.Items, IsRuntimeNotification);
        Assert.Equal("Codalus IDE needs the .NET 99.0.0-rc.2.1 runtime, which isn't installed (found: 10.0.9, 10.0.12 in /fake/dotnet). Without it, plugins, builds, language services and debugging can't start.", notification.Message);
        Assert.Equal(["Install .NET 99.0", "Check Environment"], notification.Actions);

        // Closed without installing: the next failure says it again, rather than nothing.
        f.Shell.Workbench.Notifications.Dismiss(notification);
        ShellFixture.Pump();
        Assert.True(f.Shell.ReportHostRuntimeMissing(Missing(Path.Combine(Path.GetTempPath(), "dev", "buildhost"), "codalus-buildhost")));
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Notifications.Items.Count(IsRuntimeNotification) == 1);
        Assert.NotSame(notification, f.Shell.Workbench.Notifications.Items.Single(IsRuntimeNotification));
    }

    [AvaloniaFact]
    public async Task Without_any_sdk_the_runtime_failures_point_at_the_one_sdk_notification()
    {
        await using var f = ShellFixture.Create();
        f.Shell.UseEnvironmentCheck(new EnvironmentCheck(f.Shell.Workbench.Platform, _ => null, (_, _, _, _) => Task.FromResult((1, string.Empty)), _ => false)
        {
            SdkVersions = _ => [],
            InstalledFrameworks = (_, _) => [],
            DirectoryExists = _ => false,
        });
        foreach (var host in new[] { "pluginhost", "buildhost" })
        {
            Assert.True(f.Shell.ReportHostRuntimeMissing(Missing(Path.Combine(AppContext.BaseDirectory, host), "codalus-" + host)));
        }

        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Notifications.Items.Any(n => n.Message.StartsWith("The .NET SDK isn't installed", StringComparison.Ordinal)));
        ShellFixture.Pump();
        Assert.Single(f.Shell.Workbench.Notifications.Items, n => n.Message.StartsWith("The .NET SDK isn't installed", StringComparison.Ordinal));
        Assert.DoesNotContain(f.Shell.Workbench.Notifications.Items, n => n.Message.Contains("runtime, which isn't installed", StringComparison.Ordinal));
    }

    /// <summary>
    /// A solution whose build host lacks its runtime opens once the runtime is installed: the one notification instead
    /// of an error, then RestartHostsAsync opens it again (here a stand-in build host, which then fails as a program,
    /// which shows the open was tried again).
    /// </summary>
    [AvaloniaFact]
    public async Task A_solution_waiting_for_the_runtime_opens_again_when_the_hosts_start_again()
    {
        await using var f = ShellFixture.Create();
        var dir = Directory.CreateTempSubdirectory("codalus-m49-solution-").FullName;
        try
        {
            var folder = Path.Combine(dir, "App");
            Directory.CreateDirectory(folder);
            var solution = Path.Combine(folder, "App.slnx");
            File.WriteAllText(solution, "<Solution />\n");
            var host = Path.Combine(dir, "buildhost", "codalus-buildhost.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(host)!);
            File.WriteAllText(host, "not an assembly");
            var config = Path.ChangeExtension(host, ".runtimeconfig.json");
            File.WriteAllText(config, """{ "runtimeOptions": { "rollForward": "Major", "framework": { "name": "Microsoft.NETCore.App", "version": "99.0.0" } } }""");
            f.Shell.BuildHostOverride = () => host;
            f.Shell.Trust.Trust(dir);
            bool IsError(Workbench.Notifications.Notification n) => n.Severity == Workbench.Notifications.NotificationSeverity.Error && n.Message.StartsWith("Could not open App.slnx", StringComparison.Ordinal);

            await f.Shell.OpenSolutionAsync(solution);
            await ShellFixture.PumpUntil(() => f.Shell.Workbench.Notifications.Items.Any(n => n.Message.Contains("needs the .NET 99.0.0 runtime", StringComparison.Ordinal)));
            Assert.Equal(solution, f.Shell.SolutionWaitingForRuntime);
            Assert.DoesNotContain(f.Shell.Workbench.Notifications.Items, IsError); // the one notification says it

            // "Installed": the build host's runtimeconfig now asks for what's there. The hosts start again.
            File.WriteAllText(config, """{ "runtimeOptions": { "rollForward": "Major", "framework": { "name": "Microsoft.NETCore.App", "version": "1.0.0" } } }""");
            await f.Shell.RestartHostsAsync();
            await ShellFixture.PumpUntil(() => f.Shell.Workbench.Notifications.Items.Any(IsError), seconds: 60);
            Assert.Null(f.Shell.SolutionWaitingForRuntime);
            Assert.DoesNotContain(f.Shell.Workbench.Notifications.Items, n => n.Message.Contains("crashed repeatedly", StringComparison.Ordinal));
        }
        finally
        {
            f.Shell.BuildHostOverride = null;
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Install .NET from an update's warning also starts the IDE's own hosts again when its runtime is now there, and
    /// then checks the update again: both, not one or the other.
    /// </summary>
    [AvaloniaFact]
    public async Task Installing_the_runtime_from_an_update_starts_the_hosts_again_then_checks_the_update()
    {
        await using var f = ShellFixture.Create();
        var saved = s_dotnetVariables.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            // A described machine whose hosts' root has the runtime once "installed".
            var variables = new Dictionary<string, string> { ["DOTNET_ROOT"] = "/fake/root", ["PATH"] = "/fake/bin", ["HOME"] = "/fake/home" };
            static string N(string path) => path.Replace('\\', '/');
            f.Shell.UseEnvironmentCheck(new EnvironmentCheck(f.Shell.Workbench.Platform, n => variables.GetValueOrDefault(n), (_, _, _, _) => Task.FromResult((1, string.Empty)),
                path => N(path) is "/fake/root/dotnet" or "/fake/root/dotnet.exe" or "/fake/bin/winget.exe")
            {
                HostFrameworks = [Codalus.Rpc.FrameworkReference.Create(Codalus.Rpc.FrameworkReference.NetCoreApp, Codalus.Rpc.FrameworkVersion.Parse("99.0.0"), Codalus.Rpc.RollForwardPolicy.Major)],
                SdkVersions = folder => N(folder) == "/fake/root" ? ["99.0.100"] : [],
                InstalledFrameworks = (root, _) => N(root) == "/fake/root" ? [Codalus.Rpc.FrameworkVersion.Parse("99.0.0")] : [],
                DirectoryExists = folder => N(folder) == "/fake/root",
            });
            var runtime = new ToolStatus("dotnet-runtime", ".NET runtime for Codalus IDE", true, ToolState.Missing, "helpers") { WantedVersion = "99.0.0", WantedChannel = "99.0", Found = "none" };
            var then = 0;
            await f.Shell.InstallToolAsync(runtime, _ => Task.FromResult(true), (_, _, _, _) => Task.FromResult((0, "installed\n")), () =>
            {
                then++;
                return Task.CompletedTask;
            });

            Assert.Equal(1, then);
            Assert.Contains("[setup] Starting the helper processes again.", f.Shell.Workbench.Log.GetText(), StringComparison.Ordinal);
        }
        finally
        {
            foreach (var (name, value) in saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    private static Codalus.Rpc.HostRuntimeMissingException Missing(string folder, string name, string version = "99.0.0-rc.2.1") => new(new Codalus.Rpc.HostRuntimeCheckResult(
        Path.Combine(folder, name), Path.Combine(folder, name + ".runtimeconfig.json"), "/fake/dotnet",
        [new Codalus.Rpc.FrameworkResolution(
            Codalus.Rpc.FrameworkReference.Create(Codalus.Rpc.FrameworkReference.NetCoreApp, Codalus.Rpc.FrameworkVersion.Parse(version), Codalus.Rpc.RollForwardPolicy.Major),
            null,
            [Codalus.Rpc.FrameworkVersion.Parse("10.0.9"), Codalus.Rpc.FrameworkVersion.Parse("10.0.12")])]));
}
