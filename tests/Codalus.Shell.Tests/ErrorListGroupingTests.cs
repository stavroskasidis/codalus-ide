using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.LanguageServices.Protocol;
using Codalus.Projects.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Settings;
using Range = Codalus.LanguageServices.Protocol.Range;

namespace Codalus.Shell.Tests;

/// <summary>
/// The Error List's grouping (by project, file, severity, code or source, with collapsing headers) and its analysis
/// scope picker, the <c>languageServices.analysisScope</c> setting (deferred from M12b).
/// </summary>
public sealed class ErrorListGroupingTests
{
    private static ErrorListRow Row(string icon, string code, string project, string path, int line, string source = ErrorListViewModel.IntelliSenseSource) =>
        new(icon, code, "message", project, Path.GetFileName(path), line, 1, path, new Position(line - 1, 0)) { Source = source };

    [Fact]
    public void Rows_group_by_each_key_in_order_with_rows_without_one_last()
    {
        var a = Path.Combine("src", "App", "Program.cs");
        var b = Path.Combine("src", "Lib", "Shapes.cs");
        IReadOnlyList<ErrorListRow> rows =
        [
            Row("error", "CS0103", "App", a, 3),
            Row("warning", "CA1822", "Lib", b, 7, ErrorListViewModel.BothSources),
            Row("warning", "", "", "", 1, ErrorListViewModel.BuildSource),
            Row("info", "IDE0005", "App", a, 1),
        ];

        Assert.Equal(["App", "Lib", "(no project)"], ErrorListViewModel.Groups(rows, "project").Select(g => g.Title));
        Assert.Equal([2, 1, 1], ErrorListViewModel.Groups(rows, "project").Select(g => g.Rows.Count));
        Assert.Equal(["Errors", "Warnings", "Messages"], ErrorListViewModel.Groups(rows, "severity").Select(g => g.Title));
        Assert.Equal(["CA1822", "CS0103", "IDE0005", "(no code)"], ErrorListViewModel.Groups(rows, "code").Select(g => g.Title));
        Assert.Equal(["Build", "Build + IntelliSense", "IntelliSense"], ErrorListViewModel.Groups(rows, "source").Select(g => g.Title));
        var files = ErrorListViewModel.Groups(rows, "file");
        Assert.Equal($"Program.cs — {Path.Combine("src", "App")}", files[0].Title);
        Assert.Equal("(no file)", files[^1].Title);
        Assert.All(ErrorListViewModel.Groups(rows, "project"), g => Assert.StartsWith("project:", g.Key, StringComparison.Ordinal));
    }

    [Fact]
    public void Project_names_come_from_one_index_and_match_ProjectOf()
    {
        var root = Path.Combine(Path.GetTempPath(), "codalus-errorlist-projects");
        ProjectInfo Project(string folder, string name, params string[] items) => new(
            Path.Combine(root, folder, name + ".csproj"), name, ProjectStates.Loaded, null, ["net10.0"], "Library",
            [.. items.Select(i => new ProjectItemInfo("Compile", Path.Combine(root, i), null, null))], [], [], Restored: true);
        ProjectInfo[] projects =
        [
            Project("App", "App", Path.Combine("App", "Program.cs"), Path.Combine("Shared", "Linked.cs")),
            Project("Lib", "Lib", Path.Combine("Lib", "Shapes.cs"), Path.Combine("Shared", "Linked.cs")),
            Project(Path.Combine("App", "Tests"), "App.Tests"),
        ];

        var names = ErrorListViewModel.ProjectNames(projects);
        foreach (var file in new[] { "App/Program.cs", "Shared/Linked.cs", "Lib/Shapes.cs", "App/Tests/Unlisted.cs", "App/Unlisted.cs", "Elsewhere/Other.cs" })
        {
            var path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            Assert.Equal(ErrorListViewModel.ProjectOf(projects, path)?.Name ?? string.Empty, names(path));
            Assert.Equal(names(path), names(path)); // the second answer from the cache
        }

        Assert.Equal("App", names(Path.Combine(root, "Shared", "Linked.cs"))); // the first project that lists it
        Assert.Equal("App.Tests", names(Path.Combine(root, "App", "Tests", "Unlisted.cs"))); // the deepest folder
        Assert.Equal(string.Empty, names(Path.Combine(root, "Elsewhere", "Other.cs")));
    }

    [AvaloniaFact]
    public async Task Hundreds_of_thousands_of_diagnostics_fill_the_list_in_one_change()
    {
        // A solution whose packages didn't restore (a company feed off its VPN): every file has errors. The rows were
        // built on the UI thread and added one at a time, which froze the window for minutes.
        await using var f = ShellFixture.Create();
        var root = Path.Combine(Path.GetTempPath(), "codalus-errorlist-" + Guid.NewGuid().ToString("N"));
        Diagnostic[] items = [.. Enumerable.Range(0, 100).Select(line => new Diagnostic(
            new Range(new Position(line, 0), new Position(line, 4)), DiagnosticSeverity.Error, "CS0246", "csharp", "The type or namespace name 'Telerik' could not be found"))];
        var list = f.Shell.ErrorList;
        var changes = 0;
        list.Items.CollectionChanged += (_, _) => changes++;

        for (var file = 0; file < 2000; file++)
        {
            f.Shell.Languages.Diagnostics.Set(Path.Combine(root, $"File{file}.cs"), "csharp", null, items);
        }

        await ShellFixture.PumpUntil(() => list.Rows.Count == 200_000, seconds: 60);
        Assert.Equal(200_000, list.Items.Count);
        Assert.Equal("200000 Errors", list.ErrorsLabel);
        Assert.InRange(changes, 1, 5); // a reset per refresh, not a change per row
    }

    [AvaloniaFact]
    public async Task The_list_groups_collapses_and_sets_the_analysis_scope()
    {
        await using var f = ShellFixture.Create();
        var root = Path.Combine(Path.GetTempPath(), "codalus-errorlist-" + Guid.NewGuid().ToString("N"));
        var one = Path.Combine(root, "One.cs");
        var two = Path.Combine(root, "Two.cs");
        static Diagnostic At(int line, int severity, string code) => new(new Range(new Position(line, 0), new Position(line, 1)), severity, code, "csharp", code + " message");
        f.Shell.Languages.Diagnostics.Set(one, "csharp", null, [At(0, DiagnosticSeverity.Error, "CS0103"), At(4, DiagnosticSeverity.Warning, "CA1822")]);
        f.Shell.Languages.Diagnostics.Set(two, "csharp", null, [At(2, DiagnosticSeverity.Warning, "CA1822")]);
        var list = f.Shell.ErrorList;
        await ShellFixture.PumpUntil(() => list.Rows.Count == 3);
        Assert.Equal(3, list.Items.Count); // no grouping: just the rows

        // By code: a header per code, the rows under it; the setting remembers it.
        list.GroupBy = Array.IndexOf(ErrorListViewModel.GroupByValues, "code");
        await ShellFixture.PumpUntil(() => list.Items.Count == 5);
        Assert.Equal("code", f.Shell.Workbench.Settings.GetString(ErrorListViewModel.GroupBySetting));
        var headers = list.Items.OfType<ErrorListGroup>().ToList();
        Assert.Equal(["CA1822 (2)", "CS0103 (1)"], headers.Select(h => h.Header));
        Assert.IsType<ErrorListRow>(list.Items[1]);

        // A header collapses its rows, and expands them again.
        list.ToggleGroup(headers[0]);
        Assert.Equal(3, list.Items.Count);
        Assert.False(list.Items.OfType<ErrorListGroup>().First().IsExpanded);
        Assert.Contains("collapsed", list.Items.OfType<ErrorListGroup>().First().AccessibleName, StringComparison.Ordinal);
        list.ToggleGroup(list.Items.OfType<ErrorListGroup>().First());
        Assert.Equal(5, list.Items.Count);

        // The view shows the pickers, with accessible names.
        f.Shell.Layout.ShowToolWindow(ErrorListViewModel.ToolWindowId, focus: true);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<ErrorListView>().Any(v => v.IsEffectivelyVisible));
        var view = f.Window.GetVisualDescendants().OfType<ErrorListView>().First();
        var pickers = view.GetVisualDescendants().OfType<ComboBox>().Select(c => Avalonia.Automation.AutomationProperties.GetName(c)).ToList();
        Assert.Contains("Group By", pickers);
        Assert.Contains("Analysis Scope", pickers);

        // The analysis scope picker is the language services' setting; current project follows the active document.
        list.AnalysisScope = Array.IndexOf(ErrorListViewModel.AnalysisScopeValues, "currentProject");
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Settings.GetString(ErrorListViewModel.AnalysisScopeSetting) == "currentProject");
        await f.Shell.Workbench.Settings.SetAsync(ErrorListViewModel.AnalysisScopeSetting, "solution", SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => list.AnalysisScope == 2);
    }
}
