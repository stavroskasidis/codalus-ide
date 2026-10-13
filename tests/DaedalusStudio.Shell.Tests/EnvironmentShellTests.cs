using Avalonia.Headless.XUnit;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Setup;

namespace DaedalusStudio.Shell.Tests;

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
            Assert.Contains("daedalus.setup", f.Shell.Workbench.Output.Channels.Select(c => c.Id));
            await ShellFixture.PumpUntil(() => f.Shell.Workbench.Notifications.Items.Any(n => n.Message.Contains("is installed", StringComparison.Ordinal)));
        }
    }
}
