using Avalonia.Headless.XUnit;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Tests;

/// <summary>Build, the Error List and Run Without Debugging in the shell (ADR 0006), on samples/BuildAndRun.</summary>
public sealed class BuildTests
{
    [AvaloniaFact]
    public async Task Build_errors_reach_the_error_list_and_ctrl_f5_runs_the_startup_project()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("BuildAndRun");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "BuildAndRun.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 2 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 90);

        var hello = Path.Combine(folder, "src", "Hello", "Hello.csproj");
        Assert.Equal(hello, f.Shell.Runs.StartupProject);
        await ShellFixture.PumpUntil(() => f.Shell.Explorer.Rows.Any(r => r is { Name: "Hello", IsStartup: true }));
        Assert.Equal("Hello", f.Shell.Toolbar.StartupProject?.Name);
        Assert.StartsWith("Hello", f.Shell.Toolbar.StartLabel, StringComparison.Ordinal);

        // A compile error: the build fails and the Error List shows it, with navigation.
        var greeter = Path.Combine(folder, "src", "Greetings", "Greeter.cs");
        var original = File.ReadAllText(greeter);
        File.WriteAllText(greeter, original.Replace("$\"Hello from {name}\"", "missing", StringComparison.Ordinal));
        f.Shell.Run("build.buildSolution");
        await ShellFixture.PumpUntil(() => f.Shell.Builds.LastBuild is not null, seconds: 180);
        Assert.False(f.Shell.Builds.LastBuild!.Result.Succeeded, f.Shell.Builds.Channel.GetText());
        // The live (IntelliSense) diagnostic can show first; wait for the build's.
        await ShellFixture.PumpUntil(() => f.Shell.ErrorList.Rows.Any(r => r.Code == "CS0103" && r.Source.Contains("Build", StringComparison.Ordinal)));
        var row = f.Shell.ErrorList.Rows.First(r => r.Code == "CS0103" && r.Source.Contains("Build", StringComparison.Ordinal));
        Assert.Equal("Greetings", row.Project);
        await ShellFixture.PumpUntil(() => f.Shell.StatusBar.Left.Any(i => i.Item.Id == "codalus.build" && i.Text.Contains("failed", StringComparison.Ordinal)));

        f.Shell.ErrorList.SourceFilter = 2; // IntelliSense only
        Assert.DoesNotContain(f.Shell.ErrorList.Rows, r => r.Source == ErrorListViewModel.BuildSource);
        f.Shell.ErrorList.SourceFilter = 0;

        await f.Shell.ErrorList.OpenAsync(f.Shell.ErrorList.Rows.First(r => r.Code == "CS0103"));
        await ShellFixture.PumpUntil(() => f.Shell.Layout.ActiveDocument is Editor.CodeEditorDocument { Title: "Greeter.cs" });

        // Fixed, then Ctrl+F5: builds the startup project and runs it with its first launch profile.
        File.WriteAllText(greeter, original);
        f.Shell.Run("run.startWithoutDebugging");
        await ShellFixture.PumpUntil(
            () => f.Shell.Workbench.Output.Channels.FirstOrDefault(c => c.Name == "Run: Hello")?.GetText().Contains("has exited with code 0", StringComparison.Ordinal) == true,
            seconds: 180);
        var output = f.Shell.Workbench.Output.Channels.First(c => c.Name == "Run: Hello").GetText();
        Assert.Contains("args: one two words", output, StringComparison.Ordinal);
        Assert.Contains("GREETING: from the launch profile", output, StringComparison.Ordinal);
        Assert.True(f.Shell.Builds.LastBuild!.Result.Succeeded);
        await ShellFixture.PumpUntil(() => !f.Shell.ErrorList.Rows.Any(r => r.Source == ErrorListViewModel.BuildSource));
    }
}
