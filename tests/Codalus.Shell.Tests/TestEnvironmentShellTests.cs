using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Debugging.Remote;
using Codalus.Workbench.Testing;

namespace Codalus.Shell.Tests;

/// <summary>
/// Remote test environments in the Test Explorer (M21b, ADR 0055): samples/Testing with a <c>testenvironments.json</c>
/// naming a WSL distribution (Windows with WSL and .NET in it, <c>CODALUS_TEST_WSL</c>, default <c>Ubuntu</c>; the
/// workbench's tests cover ssh on Linux and macOS).
/// </summary>
public sealed class TestEnvironmentShellTests
{
    private static async Task<string?> WslDistributionAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var distribution = Environment.GetEnvironmentVariable("CODALUS_TEST_WSL") is { Length: > 0 } name ? name : "Ubuntu";
        if (!(await RemoteTargets.WslDistributionsAsync(CancellationToken.None)).Contains(distribution, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var dotnet = await RemoteShell.RunAsync(new WslTarget(distribution), "PATH=\"$HOME/.dotnet:$PATH\" dotnet --list-sdks", CancellationToken.None);
        return dotnet.Succeeded && dotnet.Output.Contains("10.", StringComparison.Ordinal) ? distribution : null;
    }

    [AvaloniaFact]
    public async Task Tests_picked_in_the_explorer_run_in_the_environment_and_show_their_outcomes()
    {
        if (await WslDistributionAsync() is not { } distribution)
        {
            Assert.Skip("No WSL distribution with .NET here (CODALUS_TEST_WSL); the workbench's TestEnvironmentTests cover ssh on Linux and macOS.");
            return;
        }

        await using var f = ShellFixture.Create(builtInPlugins: ShellFixture.PluginBin("Codalus.Plugins.DotNetTest"));
        var folder = f.CopySample("Testing");
        File.WriteAllText(Path.Combine(folder, TestEnvironments.FileName), $$"""
            {
              "version": "1",
              "environments": [
                { "name": "WSL {{distribution}}", "type": "wsl", "wslDistribution": "{{distribution}}" }
              ]
            }
            """);
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Testing.slnx"));
        f.Shell.Layout.ShowToolWindow(TestExplorerViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<TestExplorerView>().Any());

        // The explorer offers the environment next to Local.
        var explorer = f.Shell.TestExplorer;
        await ShellFixture.PumpUntil(() => explorer.HasEnvironments);
        Assert.Equal(["Local", "WSL " + distribution], explorer.EnvironmentNames);
        explorer.EnvironmentIndex = 1;
        Assert.Equal("WSL " + distribution, explorer.Environment?.Name);

        // Two tests of the xUnit v2 project (not the one that hangs) run there.
        await ShellFixture.PumpUntil(() => f.Shell.Tests.Read(roots => roots.Any(r => r.Label == "XunitV2Tests" && r.Leaves().Any(l => l.Label == "Fails"))), seconds: 180);
        var (adds, fails) = f.Shell.Tests.Read(roots =>
        {
            var leaves = roots.Single(r => r.Label == "XunitV2Tests").Leaves().ToList();
            return (leaves.Single(l => l.Label == "Adds"), leaves.Single(l => l.Label == "Fails"));
        });
        await f.Shell.RunTestsAsync(TestRunKind.Run, [adds.Key, fails.Key]);
        var channel = f.Shell.Workbench.Output.Channels.First(c => c.Name == "Tests").GetText();
        Assert.True(adds.Outcome == TestOutcome.Passed, channel);
        Assert.Equal(TestOutcome.Failed, fails.Outcome);
        Assert.Contains("/mnt/", channel, StringComparison.Ordinal); // it ran there
        Assert.Contains(f.Shell.Tests.Runs, r => r.Name == "WSL " + distribution);
        Assert.False(f.Shell.Tests.IsRunning);
    }
}
