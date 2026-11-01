using Avalonia.Headless.XUnit;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Testing;

namespace Codalus.Shell.Tests;

/// <summary>
/// Per-solution state in <c>.codalus/state/</c> next to the solution (ADR 0070): what a solution reopens with, the
/// old layout moved in, two solutions in one folder, and a folder that can't be written.
/// </summary>
public class SolutionStateTests
{
    private static async Task<(ShellFixture F, string Solution)> OpenTrustedAsync()
    {
        var f = ShellFixture.Create();
        var solution = f.CopyFixture();
        f.Shell.Trust.Trust(Path.GetDirectoryName(Path.GetDirectoryName(solution))!);
        await OpenAsync(f, solution);
        return (f, solution);
    }

    private static async Task OpenAsync(ShellFixture f, string solution)
    {
        await f.Shell.OpenSolutionAsync(solution);
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Progress is { Completed: true } && f.Shell.Solutions.Projects.Count == 3, seconds: 90);

        // App's row with its folders: the row is there from the solution file ("loading…", nothing to expand), and the
        // explorer shows the evaluated project on its next refresh, which can come after Projects counts it.
        await ShellFixture.PumpUntil(() => Row(f, "App") is { HasChildren: true });
    }

    private static ExplorerRowViewModel? Row(ShellFixture f, string name) => f.Shell.Explorer.Rows.FirstOrDefault(r => r.Name == name);

    [AvaloniaFact]
    public async Task A_solution_reopens_with_its_expansion_configuration_bookmarks_and_views_kept_in_codalus_state()
    {
        var (f, solution) = await OpenTrustedAsync();
        await using var _ = f;
        var folder = Path.GetDirectoryName(solution)!;
        var state = Path.Combine(folder, ".codalus", "state", "ProjectSystem.slnx");
        Assert.False(Directory.Exists(Path.Combine(folder, ".codalus")));

        // Expand App and its Forms folder; collapse the Solution Items folder (solution folders start expanded).
        f.Shell.Explorer.SetExpanded(Row(f, "App")!, true);
        await ShellFixture.PumpUntil(() => Row(f, "Forms") is not null);
        f.Shell.Explorer.SetExpanded(Row(f, "Forms")!, true);
        await ShellFixture.PumpUntil(() => Row(f, "MainForm.cs") is not null);
        f.Shell.Explorer.SetExpanded(Row(f, "Solution Items")!, false);
        Assert.Null(Row(f, "README.md"));

        await f.Shell.Solutions.SetConfigurationAsync(f.Shell.Solutions.Solution!.Configurations.First(c => c is { Configuration: "Release", Platform: "x64" }), CancellationToken.None);
        var program = Path.Combine(folder, "src", "App", "Program.cs");
        f.Shell.Bookmarks.Toggle(program, 2);
        f.Shell.ErrorList.ShowWarnings = false;
        f.Shell.ErrorList.SourceFilter = 1;
        f.Shell.TestExplorer.Grouping = TestGrouping.Outcome;

        await f.Shell.CloseSolutionAsync();
        await f.Shell.SolutionState.FlushAsync();
        Assert.True(File.Exists(Path.Combine(state, "session.json")));
        Assert.True(File.Exists(Path.Combine(state, "bookmarks.json")));
        Assert.Contains("*", File.ReadAllLines(Path.Combine(folder, ".codalus", "state", ".gitignore")));
        Assert.False(Directory.Exists(Path.Combine(f.Shell.Paths.State, "sessions")));
        Assert.False(Directory.Exists(Path.Combine(f.Shell.Paths.State, "solution-state")));
        Assert.Empty(f.Shell.Bookmarks.All); // back to the user's bookmarks

        // Views back to their defaults, so the reopen shows what was kept.
        f.Shell.ErrorList.ShowWarnings = true;
        f.Shell.ErrorList.SourceFilter = 0;
        f.Shell.TestExplorer.Grouping = TestGrouping.Hierarchy;

        // Reopened: the projects' nodes appear once they're evaluated, expanded as they were.
        await f.Shell.OpenSolutionAsync(solution);
        await ShellFixture.PumpUntil(() => Row(f, "MainForm.cs") is not null, seconds: 90);
        Assert.True(Row(f, "App")!.IsExpanded);
        Assert.True(Row(f, "Forms")!.IsExpanded);
        Assert.False(Row(f, "Solution Items")!.IsExpanded);
        Assert.True(Row(f, "src")!.IsExpanded);
        Assert.Equal(new Codalus.Projects.Protocol.SolutionConfigurationInfo("Release", "x64"), f.Shell.Solutions.Solution!.Active);
        Assert.Equal([(program, 2)], f.Shell.Bookmarks.All.Select(b => (b.Path, b.Line)));
        Assert.False(f.Shell.ErrorList.ShowWarnings);
        Assert.Equal(1, f.Shell.ErrorList.SourceFilter);
        Assert.Equal(TestGrouping.Outcome, f.Shell.TestExplorer.Grouping);
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Progress is { Completed: true } && f.Shell.Solutions.Projects.Count == 3, seconds: 90);

        // Collapse All is kept too: the solution shows its folders, collapsed.
        f.Shell.Explorer.CollapseAll();
        await f.Shell.CloseSolutionAsync();
        await f.Shell.OpenSolutionAsync(solution);
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Progress is { Completed: true } && f.Shell.Solutions.Projects.Count == 3, seconds: 90);
        await ShellFixture.PumpUntil(() => Row(f, "src") is not null);
        Assert.Equal(["Solution 'ProjectSystem'", "Solution Items", "src", "tests"], f.Shell.Explorer.Rows.Select(r => r.Name));
        Assert.All(f.Shell.Explorer.Rows.Skip(1), r => Assert.False(r.IsExpanded));
    }

    [AvaloniaFact]
    public async Task The_old_session_moves_next_to_the_solution_and_two_solutions_in_a_folder_keep_their_own()
    {
        await using var f = ShellFixture.Create();
        var solution = f.CopyFixture();
        var folder = Path.GetDirectoryName(solution)!;
        var readme = Path.Combine(folder, "README.md");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);

        // A session in the old place: <state>/sessions/<hash>.json.
        var legacy = f.Shell.SolutionState.LegacySessionFile(solution);
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        await File.WriteAllTextAsync(legacy, $$"""{ "documents": [ { "path": {{System.Text.Json.JsonSerializer.Serialize(readme)}}, "line": 0, "character": 0 } ], "active": {{System.Text.Json.JsonSerializer.Serialize(readme)}} }""", TestContext.Current.CancellationToken);

        await OpenAsync(f, solution);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Name == "README.md");
        Assert.False(File.Exists(legacy));
        Assert.Contains("README.md", File.ReadAllText(Path.Combine(folder, ".codalus", "state", "ProjectSystem.slnx", "session.json")), StringComparison.Ordinal);
        Assert.Equal(Path.Combine(folder, ".codalus", "state", "ProjectSystem.slnx"), f.Shell.SolutionStateDirectory);

        // The .sln next to it has a session of its own.
        await f.Shell.CloseSolutionAsync();
        await OpenAsync(f, Path.Combine(folder, "ProjectSystem.sln"));
        Assert.Empty(f.Shell.Editors);
        Assert.Equal(Path.Combine(folder, ".codalus", "state", "ProjectSystem.sln"), f.Shell.SolutionStateDirectory);
        await f.Shell.CloseSolutionAsync();
        await f.Shell.SolutionState.FlushAsync();
        Assert.True(File.Exists(Path.Combine(folder, ".codalus", "state", "ProjectSystem.sln", "session.json")));

        await OpenAsync(f, solution);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Name == "README.md");
    }

    [AvaloniaFact]
    public async Task A_sessions_tabs_come_back_behind_the_active_one_and_make_their_editors_when_shown()
    {
        // Each document used to be activated in turn: every tab's editor was made at startup, each asking the language
        // service for its colors, hints and CodeLens, for a session of twenty tabs on a large solution.
        await using var f = ShellFixture.Create();
        var solution = f.CopyFixture();
        var folder = Path.GetDirectoryName(solution)!;
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        string[] paths = [Path.Combine(folder, "README.md"), Path.Combine(folder, "src", "App", "Program.cs"), Path.Combine(folder, "src", "Core", "Greeter.cs")];
        var session = Path.Combine(folder, ".codalus", "state", "ProjectSystem.slnx", "session.json");
        Directory.CreateDirectory(Path.GetDirectoryName(session)!);
        await File.WriteAllTextAsync(session, $$"""
            { "documents": [{{string.Join(", ", paths.Select(p => $$"""{ "path": {{System.Text.Json.JsonSerializer.Serialize(p)}}, "line": 2, "character": 0 }"""))}}], "active": {{System.Text.Json.JsonSerializer.Serialize(paths[1])}} }
            """, TestContext.Current.CancellationToken);

        // The tabs are added in one pass: a layout pass between two of them made the first tab of the empty group active,
        // and built its editor (macOS CI, run 41237010138). A job posted when the first tab lands runs only if the UI
        // thread is let go before the last one does.
        var added = 0;
        var yielded = false;
        var yieldedBeforeLast = false;
        f.Shell.Layout.Factory.DockableAdded += (_, e) =>
        {
            if (e.Dockable is not Editor.CodeEditorDocument)
            {
                return;
            }

            if (++added == 1)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => yielded = true, Avalonia.Threading.DispatcherPriority.Send);
            }
            else if (added == paths.Length)
            {
                yieldedBeforeLast = yielded;
            }
        };

        await OpenAsync(f, solution);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Name == "Program.cs");
        Assert.Equal(paths.Length, added);
        Assert.False(yieldedBeforeLast, "the UI thread was let go between two of the session's tabs");
        Assert.Equal(["README.md", "Program.cs", "Greeter.cs"], f.Shell.Editors.Select(e => e.Model.Name));
        Assert.Null(f.Shell.Editors.Single(e => e.Model.Name == "README.md").Controller);
        Assert.Null(f.Shell.Editors.Single(e => e.Model.Name == "Greeter.cs").Controller);

        // Shown, a tab makes its editor, at the line it was left on.
        var greeter = f.Shell.Editors.Single(e => e.Model.Name == "Greeter.cs");
        f.Shell.Layout.OpenDocument(greeter);
        await ShellFixture.PumpUntil(() => greeter.Controller is not null);
        Assert.Equal(3, greeter.Controller!.Caret.Line);
    }

    [AvaloniaFact]
    public async Task A_solution_folder_that_cant_be_written_keeps_the_state_in_the_user_folder()
    {
        await using var f = ShellFixture.Create();
        var solution = f.CopyFixture();
        var folder = Path.GetDirectoryName(solution)!;
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await File.WriteAllTextAsync(Path.Combine(folder, ".codalus"), string.Empty, TestContext.Current.CancellationToken); // .codalus/state can't be created

        await OpenAsync(f, solution);
        await f.Shell.OpenFileAsync(Path.Combine(folder, "README.md"));
        await f.Shell.CloseSolutionAsync();
        await f.Shell.SolutionState.FlushAsync();
        var fallback = Path.Combine(f.Shell.SolutionState.FallbackDirectory(new SolutionStateKey(solution, IsFolder: false)), "session.json");
        Assert.True(File.Exists(fallback));
        Assert.Contains("Could not write session.json", f.Shell.Workbench.Log.GetText(), StringComparison.Ordinal);

        await OpenAsync(f, solution);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Name == "README.md");
    }
}
