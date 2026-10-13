using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Projects.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

namespace Codalus.Shell.Tests;

/// <summary>
/// Build depth (M26b, ADR 0034 and 0035) in the headless shell, against the real build host: Batch Build, the fast
/// up-to-date check, Build Timings, the binary log viewer, the Build Events page, and custom tools (Run Custom Tool, and
/// a text template regenerated on save).
/// </summary>
public sealed class BuildDepthTests
{
    private static ShellFixture Create()
    {
        var f = ShellFixture.Create();
        f.Window.Width = 1400;
        f.Window.Height = 900;
        return f;
    }

    private static async Task<(ShellFixture Fixture, string Folder)> OpenBuildAndRunAsync()
    {
        var f = Create();
        var folder = f.CopySample("BuildAndRun");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "BuildAndRun.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 2 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 90);
        return (f, folder);
    }

    private static T Page<T>(ShellFixture f)
        where T : class =>
        f.Shell.Layout.Documents.OfType<PageDocument>().Select(d => d.ViewModel).OfType<T>().Single();

    [AvaloniaFact]
    public async Task Batch_build_the_up_to_date_check_timings_and_the_binary_log_viewer()
    {
        var (f, folder) = await OpenBuildAndRunAsync();
        await using var _ = f;
        var hello = Path.Combine(folder, "src", "Hello", "Hello.csproj");

        // Batch Build: every project × configuration; the checked ones build in turn.
        f.Shell.Run("build.batchBuild");
        await ShellFixture.PumpUntil(() => f.Shell.Layout.Documents.OfType<PageDocument>().Any(d => d.ViewModel is BatchBuildViewModel { Rows.Count: >= 4 }), seconds: 60);
        var batch = Page<BatchBuildViewModel>(f);
        Assert.Contains(batch.Rows, r => r is { Name: "Greetings", Configuration: "Release", Platform: "AnyCPU" });
        foreach (var row in batch.Rows.Where(r => r.Name == "Hello"))
        {
            row.IsChecked = true;
        }

        await batch.RunAsync(BuildTargets.Build);
        Assert.All(batch.Rows.Where(r => r.Name == "Hello"), r => Assert.StartsWith("succeeded", r.Status, StringComparison.Ordinal));
        Assert.Null(batch.Rows.First(r => r.Name == "Greetings").Status);
        Assert.True(File.Exists(Path.Combine(folder, "src", "Hello", "bin", "Release", "net10.0", "Hello.dll")));
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<BatchBuildView>().Any());
        NavigationDepthTests.Screenshot(f, "m26b-batch-build");

        // The fast up-to-date check: Hello (Debug) was just built, so building it again skips MSBuild.
        Assert.True(await f.Shell.BuildAsync(BuildTargets.Build, hello));
        Assert.True(f.Shell.Builds.LastBuild!.Result.UpToDate, f.Shell.Builds.Channel.GetText());
        var verdict = await f.Shell.CheckUpToDateAsync();
        Assert.NotNull(verdict);
        Assert.True(verdict!.UpToDate, string.Join("; ", verdict.Projects.Select(p => p.Reason)));

        // Build with Binary Log runs MSBuild anyway and opens the log; search reveals a node.
        f.Shell.Run("build.buildWithBinaryLog");
        await ShellFixture.PumpUntil(() => f.Shell.Layout.Documents.OfType<PageDocument>().Any(d => d.ViewModel is BinaryLogViewModel { Roots.Count: 1 } log && log.Roots[0].Children.Count > 1), seconds: 180);
        var binlog = Page<BinaryLogViewModel>(f);
        Assert.StartsWith(f.Shell.BinaryLogsFolder, binlog.Path, StringComparison.Ordinal);
        Assert.Contains("Build succeeded", binlog.Summary, StringComparison.Ordinal);
        binlog.Query = "CoreCompile";
        await binlog.SearchAsync();
        Assert.True(binlog.HasResults);
        var target = binlog.Results.First(r => r.Kind == "Target");
        await binlog.RevealAsync(target.Result);
        Assert.Equal(target.Result.Node.Id, binlog.Selected?.Node.Id);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<BinaryLogView>().Any());
        NavigationDepthTests.Screenshot(f, "m26b-binary-log");

        // Build Timings: the projects of that build, on the critical path.
        f.Shell.Run("build.showTimings");
        await ShellFixture.PumpUntil(() => f.Shell.Layout.Documents.OfType<PageDocument>().Any(d => d.ViewModel is BuildTimingsViewModel { Projects.Count: > 0 }), seconds: 60);
        var timings = Page<BuildTimingsViewModel>(f);
        Assert.Contains(timings.Projects, p => p.Name == "Hello");
        Assert.Contains(timings.CriticalPath, p => p.StartsWith("Hello", StringComparison.Ordinal));
        Assert.True(timings.HasBinaryLog);
        Assert.NotEmpty(timings.Targets);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<BuildTimingsView>().Any());
        NavigationDepthTests.Screenshot(f, "m26b-build-timings");
    }

    [AvaloniaFact]
    public async Task Build_events_page_writes_the_events_and_the_build_runs_them()
    {
        var (f, folder) = await OpenBuildAndRunAsync();
        await using var _ = f;
        var hello = Path.Combine(folder, "src", "Hello", "Hello.csproj");
        var properties = f.Shell.OpenProjectProperties(hello)!;
        properties.SelectedPage = properties.Pages.Single(p => p.IsBuildEvents);
        var events = properties.BuildEvents!;
        await ShellFixture.PumpUntil(() => events.IsLoaded, seconds: 60);
        Assert.Equal("OnBuildSuccess", events.RunPostBuildEvent.Value);
        Assert.Equal(string.Empty, events.PreBuild);
        events.PreBuild = "echo pre-build from the page";
        events.RunPostBuildEvent = BuildEventsViewModel.RunOptions.Single(o => o.Value == "Always");
        Assert.True(events.IsDirty);
        await events.SaveAsync();
        Assert.False(events.IsDirty, events.Error);
        var text = File.ReadAllText(hello);
        Assert.Contains("<Exec Command=\"echo pre-build from the page\" />", text, StringComparison.Ordinal);
        Assert.Contains("<RunPostBuildEvent>Always</RunPostBuildEvent>", text, StringComparison.Ordinal);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<ProjectPropertiesView>().Any());
        NavigationDepthTests.Screenshot(f, "m26b-build-events");

        await f.Shell.Workbench.Settings.SetAsync(ShellHost.BuildVerbositySetting, "normal", Codalus.Workbench.Settings.SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Settings.GetString(ShellHost.BuildVerbositySetting) == "normal");
        Assert.True(await f.Shell.BuildAsync(BuildTargets.Build, hello), f.Shell.Builds.Channel.GetText());
        Assert.Contains("pre-build from the page", f.Shell.Builds.Channel.GetText(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Custom_tools_run_on_command_and_on_save()
    {
        var f = Create();
        await using var _ = f;
        var folder = f.CopySample("ProjectSystem");
        var items = Path.Combine(folder, "src", "Items");
        var project = Path.Combine(items, "Items.csproj");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(project);
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 90);

        // Run Custom Tool on the .resx.
        var resx = Path.Combine(items, "Resources.resx");
        File.WriteAllText(resx, File.ReadAllText(resx).Replace("</root>", "  <data name=\"FromTheShell\" xml:space=\"preserve\">\n    <value>Shell</value>\n  </data>\n</root>", StringComparison.Ordinal));
        var result = await f.Shell.RunCustomToolAsync(resx);
        Assert.True(result?.Succeeded, string.Join("; ", result?.Diagnostics.Select(d => d.Message) ?? []));
        Assert.Contains("internal static string FromTheShell {", File.ReadAllText(Path.Combine(items, "Resources.Designer.cs")), StringComparison.Ordinal);

        // A text template edited in the editor: saving it regenerates its output.
        var template = Path.Combine(items, "Greeting.tt");
        var editor = await f.Shell.OpenFileAsync(template);
        Assert.Equal("t4", editor!.Model.LanguageId);
        await ShellFixture.PumpUntil(() => editor.Controller is not null);
        editor.Controller!.Editor.Document.Insert(0, "<#@ assembly name=\"System.Core\" #>\n");
        var source = editor.Controller.Editor.Document.Text;
        var line = source.IndexOf("namespace Fixture.Items;", StringComparison.Ordinal);
        editor.Controller.Editor.Document.Insert(line, "// regenerated on save\n");
        f.Shell.Run("file.save");
        await ShellFixture.PumpUntil(() => ReadWhileWritten(Path.Combine(items, "Greeting.cs")).Contains("// regenerated on save", StringComparison.Ordinal), seconds: 120);
    }

    /// <summary>A file the generator may be writing right now: locked means not ready yet (the Windows runner).</summary>
    private static string ReadWhileWritten(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }
}
