using Avalonia.Headless.XUnit;
using Codalus.Projects.Protocol;
using Codalus.Workbench.Node;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// M16d (ADR 0027): npm in a solution (samples/WebScripts) with the built-in Node.js: the Task Runner Explorer lists
/// the scripts and their bindings, Restore installs the packages, a script runs in a terminal, and a build runs the
/// script bound to Before Build.
/// </summary>
public sealed class NpmShellTests
{
    private static string SharedCache => Path.Combine(Path.GetTempPath(), "codalus-test-node-cache");

    private static string Text(Workbench.Terminal.TerminalSession session) =>
        string.Join("\n", Enumerable.Range(0, session.Terminal.TotalRows).Select(i => session.Terminal.GetRow(i).GetText()));

    [AvaloniaFact]
    public async Task The_task_runner_runs_scripts_restores_packages_and_runs_build_bindings()
    {
        Assert.SkipWhen(NodeRuntime.Platform is null, "No Node.js build for this platform.");
        Environment.SetEnvironmentVariable(ShellHost.NodeCacheVariable, SharedCache);
        await using var f = ShellFixture.Create();
        await f.Shell.Workbench.Settings.SetAsync(Npm.RuntimeSetting, "builtIn", SettingScope.User, TestContext.Current.CancellationToken);
        await f.Shell.Workbench.Settings.SetAsync(Npm.RestoreOnOpenSetting, "never", SettingScope.User, TestContext.Current.CancellationToken);
        var folder = f.CopySample("WebScripts");
        var project = Path.Combine(folder, "WebScripts");
        var packageJson = Path.Combine(project, "package.json");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "WebScripts.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 180);

        // The Task Runner Explorer: the packages (the app's and its local dependency's), the app's scripts, and the
        // one bound to Before Build.
        await f.RunAsync("npm.taskRunner");
        await ShellFixture.PumpUntil(() => f.Shell.TaskRunner.Rows.Count == 5, seconds: 30);
        var rows = f.Shell.TaskRunner.Rows;
        Assert.Equal(["WebScripts/local-greeting/package.json", "WebScripts/package.json"], rows.Where(r => r.IsHeader).Select(r => r.Label));
        var app = rows.Single(r => r.Label == "WebScripts/package.json");
        Assert.Contains("packages not installed", app.Detail, StringComparison.Ordinal);
        Assert.Equal(["hello", "stamp", "greet"], rows.Where(r => !r.IsHeader).Select(r => r.Label));
        Assert.EndsWith("[Before Build]", rows.Single(r => r.Label == "stamp").Detail, StringComparison.Ordinal);

        // Restore: npm install of the local dependency.
        Assert.True(await f.Shell.RestoreNpmAsync(null), f.Shell.NpmLog.GetText());
        Assert.True(Directory.Exists(Path.Combine(project, "node_modules", "local-greeting")), f.Shell.NpmLog.GetText());
        await ShellFixture.PumpUntil(() => !f.Shell.TaskRunner.Rows.Any(r => r.Detail.Contains("not installed", StringComparison.Ordinal)));

        // A script runs in a terminal of its own.
        await f.Shell.RunNpmScriptAsync(packageJson, "greet");
        var session = f.Shell.Terminals.Sessions.Single(s => s.Name == "npm: greet");
        await ShellFixture.PumpUntil(() => Text(session).Contains("Hello, npm!", StringComparison.Ordinal), seconds: 120);

        // Build runs the Before Build binding first.
        var stamp = Path.Combine(project, "stamp.txt");
        Assert.False(File.Exists(stamp));
        Assert.True(await f.Shell.BuildAsync(BuildTargets.Build, null));
        Assert.True(File.Exists(stamp), f.Shell.NpmLog.GetText());
        Assert.Contains("> npm run stamp (Before Build", f.Shell.NpmLog.GetText(), StringComparison.Ordinal);
    }
}
