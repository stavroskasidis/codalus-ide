using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>Opening solutions and working in the Solution Explorer, against the real build host.</summary>
public class SolutionTests
{
    private static async Task<ShellFixture> OpenTrustedAsync()
    {
        var f = ShellFixture.Create();
        var solution = f.CopyFixture();
        f.Shell.Trust.Trust(Path.GetDirectoryName(Path.GetDirectoryName(solution))!);
        await f.Shell.OpenSolutionAsync(solution);
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Progress is { Completed: true } && f.Shell.Solutions.Projects.Count == 3, seconds: 90);
        await ShellFixture.PumpUntil(() => f.Shell.Explorer.Rows.Any(r => r.Name == "App"));
        return f;
    }

    private static ExplorerRowViewModel Row(ShellFixture f, string name) => f.Shell.Explorer.Rows.Single(r => r.Name == name);

    /// <summary>
    /// A solution reached through a symbolic link (as macOS's <c>/var</c> → <c>/private/var</c>) is opened under its
    /// real path, so MSBuild nodes and the file watcher, which report real paths, see each project under one spelling:
    /// otherwise restores of the same project race and re-evaluations load it twice.
    /// </summary>
    [AvaloniaFact]
    public async Task A_solution_opened_through_a_symbolic_link_uses_its_real_path_and_builds()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Symbolic links need extra privileges on Windows.");
        await using var f = ShellFixture.Create();
        var solution = f.CopyFixture();
        var real = Path.GetDirectoryName(Path.GetDirectoryName(solution))!;
        var link = Path.Combine(f.UserData, "linked");
        Directory.CreateSymbolicLink(link, real);
        f.Shell.Trust.Trust(real);

        await f.Shell.OpenSolutionAsync(Path.Combine(link, "ProjectSystem", "ProjectSystem.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Progress is { Completed: true } && f.Shell.Solutions.Projects.Count == 3, seconds: 90);
        Assert.Equal(solution, f.Shell.Solutions.Solution!.Path);
        Assert.All(f.Shell.Solutions.Projects.Keys, p => Assert.StartsWith(real, p, StringComparison.Ordinal));

        // Touching a project file re-evaluates it under the same spelling; then the solution builds.
        var app = f.Shell.Solutions.Projects.Keys.Single(p => p.EndsWith("App.csproj", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(app, DateTime.UtcNow);
        await Task.Delay(1000);
        var before = f.Shell.Builds.LastBuild;
        await f.RunAsync("build.buildSolution");
        await ShellFixture.PumpUntil(() => f.Shell.Builds.LastBuild is { } b && b != before, seconds: 300);
        Assert.True(f.Shell.Builds.LastBuild!.Result.Succeeded, f.Shell.Builds.Channel.GetText());
        Assert.Equal(3, f.Shell.Solutions.Projects.Count);
    }

    [AvaloniaFact]
    public async Task A_trusted_solution_opens_in_the_solution_explorer()
    {
        await using var f = await OpenTrustedAsync();

        Assert.Equal(["Solution 'ProjectSystem'", "Solution Items", "Directory.Build.props", "README.md", "src", "libs", "Core", "App", "tests", "Core.Tests"],
            f.Shell.Explorer.Rows.Select(r => r.Name));
        Assert.Equal(("Debug", "Any CPU"), (f.Shell.Toolbar.Configuration, f.Shell.Toolbar.Platform));
        Assert.Equal("ProjectSystem", f.Shell.Toolbar.SolutionName);
        Assert.Equal(f.Shell.Solutions.Solution!.Path, f.Shell.Recent.Items[0]);
        Assert.EndsWith(Path.Combine(".codalus", "settings.json"), f.Shell.Workbench.Settings.WorkspaceFile, StringComparison.Ordinal);
        Assert.Equal(true, f.Shell.Workbench.Context.GetValue("solutionOpen"));
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<SolutionExplorerView>().Any(v => v.IsEffectivelyVisible));

        await f.Shell.CloseSolutionAsync();
        await ShellFixture.PumpUntil(() => !f.Shell.Explorer.HasSolution);
        Assert.Null(f.Shell.Workbench.Settings.WorkspaceFile);
    }

    [AvaloniaFact]
    public async Task An_untrusted_folder_asks_first_and_can_be_trusted_later()
    {
        await using var f = ShellFixture.Create();
        var solution = f.CopyFixture();

        var opening = f.Shell.OpenSolutionAsync(solution);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        Assert.Contains("trust", f.Shell.Dialogs.Title, StringComparison.OrdinalIgnoreCase);
        f.Shell.Dialogs.Buttons.Single(b => b.Label == "Open Without Trusting").Command.Execute(null);
        await opening;

        await ShellFixture.PumpUntil(() => f.Shell.StatusBar.Left.Any(i => i.Text == "Restricted mode"));
        Assert.Empty(f.Shell.Solutions.Projects);

        await f.RunAsync("solution.trust");
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        f.Shell.Dialogs.Accept();
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 3, seconds: 90);
        Assert.True(f.Shell.Trust.IsTrusted(solution));
    }

    [AvaloniaFact]
    public async Task Files_can_be_created_renamed_and_deleted_from_the_explorer()
    {
        await using var f = await OpenTrustedAsync();
        var explorer = f.Shell.Explorer;
        var app = f.Shell.Solutions.Solution!.Projects.Single(p => p.Name == "App").Path;
        var appDir = Path.GetDirectoryName(app)!;

        explorer.Reveal("p:" + app);
        f.Shell.Workbench.Context.Set("focusedView", SolutionExplorerViewModel.ToolWindowId);
        await f.RunAsync("explorer.newFile");
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        f.Shell.Dialogs.InputText = "Order.cs";
        f.Shell.Dialogs.Accept();
        await ShellFixture.PumpUntil(() => f.Shell.Layout.ActiveDocument is Codalus.Shell.Editor.CodeEditorDocument { Title: "Order.cs" });
        Assert.Contains("namespace App;", File.ReadAllText(Path.Combine(appDir, "Order.cs")), StringComparison.Ordinal);

        // The new file's editor took the focus (M34a: editors now report it); back to the Solution Explorer, as a user would.
        explorer.Reveal("f:" + Path.Combine(appDir, "appsettings.json"));
        Assert.Equal("appsettings.json", explorer.Selected?.Name);
        f.Shell.Workbench.Context.Set("focusedView", SolutionExplorerViewModel.ToolWindowId);
        await f.RunAsync("explorer.rename");
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        f.Shell.Dialogs.InputText = "Order.cs";
        Assert.True(f.Shell.Dialogs.HasError); // a sibling already has that name
        f.Shell.Dialogs.InputText = "settings.json";
        f.Shell.Dialogs.Accept();
        await ShellFixture.PumpUntil(() => File.Exists(Path.Combine(appDir, "settings.json")));

        // The file moves first; the project file is updated right after.
        await ShellFixture.PumpUntil(() => ShellFixture.ReadText(app).Contains("None Update=\"settings.json\"", StringComparison.Ordinal));

        await ShellFixture.PumpUntil(() => explorer.Rows.Any(r => r.Name == "settings.json"), seconds: 30);
        explorer.Reveal("f:" + Path.Combine(appDir, "settings.json"));
        f.Shell.Workbench.Context.Set("focusedView", SolutionExplorerViewModel.ToolWindowId);
        await f.RunAsync("explorer.delete");
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        f.Shell.Dialogs.Accept();
        await ShellFixture.PumpUntil(() => !File.Exists(Path.Combine(appDir, "settings.json")));
        Assert.True(File.Exists(Path.Combine(f.UserData, "trash", "settings.json")));
    }

    [AvaloniaFact]
    public async Task The_keyboard_expands_collapses_and_opens()
    {
        await using var f = await OpenTrustedAsync();
        var explorer = f.Shell.Explorer;
        f.Shell.Layout.ShowToolWindow(SolutionExplorerViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<SolutionExplorerView>().Any());
        explorer.Selected = Row(f, "App");
        f.Window.GetVisualDescendants().OfType<SolutionExplorerView>().Single().FocusPrimary();
        ShellFixture.Pump();

        f.Press(Key.Right);
        Assert.True(Row(f, "App").IsExpanded);
        Assert.Contains(explorer.Rows, r => r.Name == "Program.cs");

        explorer.Selected = Row(f, "Program.cs");
        f.Press(Key.Enter);
        await ShellFixture.PumpUntil(() => f.Shell.Layout.ActiveDocument is Codalus.Shell.Editor.CodeEditorDocument { Title: "Program.cs" });

        // Opening moves the focus to the editor once its view is attached; take it back after that.
        await ShellFixture.PumpUntil(() => (f.Window.FocusManager?.GetFocusedElement() as Visual)?.FindAncestorOfType<Codalus.Shell.Editor.CodeEditorView>() is not null);
        explorer.Selected = Row(f, "Program.cs");
        var view = f.Window.GetVisualDescendants().OfType<SolutionExplorerView>().Single();
        view.FocusPrimary();
        await ShellFixture.PumpUntil(() => view.IsKeyboardFocusWithin);
        f.Press(Key.Left);
        Assert.Equal("App", explorer.Selected?.Name);
        f.Press(Key.Left);
        Assert.False(Row(f, "App").IsExpanded);
    }

    [AvaloniaFact]
    public async Task The_configuration_picker_switches_the_active_configuration()
    {
        await using var f = await OpenTrustedAsync();

        await f.RunAsync("solution.selectConfiguration");
        await ShellFixture.PumpUntil(() => f.Shell.Palette.IsOpen);
        f.Shell.Palette.Query = "Release|Any";
        f.Shell.Palette.Accept();

        await ShellFixture.PumpUntil(() => f.Shell.Toolbar.Configuration == "Release" && f.Shell.Toolbar.Platform == "Any CPU");
        var app = f.Shell.Solutions.Solution!.Projects.Single(p => p.Name == "App").Path;
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects[app].Items.Any(i => i.Path.EndsWith("ReleaseOnly.cs", StringComparison.Ordinal)), seconds: 60);
    }

    [AvaloniaFact]
    public async Task Workspace_settings_are_edited_on_their_own_tab()
    {
        await using var f = await OpenTrustedAsync();
        f.Shell.OpenSettings();
        var settings = ((SettingsDocument)f.Shell.Layout.ActiveDocument!).ViewModel;

        Assert.True(settings.HasWorkspace);
        settings.IsWorkspaceScope = true;
        settings.Search = "workbench.statusBar.visible";
        Assert.False(Assert.Single(settings.Items).CanEdit); // user-only

        settings.Search = "editor.fontSize";
        var size = Assert.IsType<TextSettingViewModel>(Assert.Single(settings.Items));
        size.Text = "16";
        size.Commit();

        var file = f.Shell.Workbench.Settings.WorkspaceFile!;
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Settings.GetDouble("editor.fontSize") == 16);
        Assert.Contains("\"editor.fontSize\": 16", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.Null(f.Shell.Workbench.Settings.GetLayerValue("editor.fontSize", SettingScope.User));
    }

    [AvaloniaFact]
    public async Task The_start_page_lists_recent_solutions()
    {
        await using var f = await OpenTrustedAsync();
        var recentCommand = await f.ExecuteAsync("workbench.getRecentSolutions");

        Assert.Equal("ProjectSystem.slnx", recentCommand!.Value[0].GetProperty("name").GetString());
        Assert.Equal(f.Shell.Solutions.Solution!.Path, recentCommand.Value[0].GetProperty("path").GetString());
    }
}
