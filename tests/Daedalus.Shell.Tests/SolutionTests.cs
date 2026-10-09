using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Daedalus.Shell.ViewModels;
using Daedalus.Shell.Views;
using Daedalus.Workbench.Settings;

namespace Daedalus.Shell.Tests;

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

    [AvaloniaFact]
    public async Task A_trusted_solution_opens_in_the_solution_explorer()
    {
        await using var f = await OpenTrustedAsync();

        Assert.Equal(["Solution 'ProjectSystem'", "Solution Items", "Directory.Build.props", "README.md", "src", "libs", "Core", "App", "tests", "Core.Tests"],
            f.Shell.Explorer.Rows.Select(r => r.Name));
        Assert.Contains(f.Shell.StatusBar.Left, i => i.Text == "Debug | Any CPU");
        Assert.Equal(f.Shell.Solutions.Solution!.Path, f.Shell.Recent.Items[0]);
        Assert.EndsWith(Path.Combine(".daedalus", "settings.json"), f.Shell.Workbench.Settings.WorkspaceFile, StringComparison.Ordinal);
        Assert.Equal(true, f.Shell.Workbench.Context.GetValue("solutionOpen"));
        Assert.Contains(f.Window.GetVisualDescendants().OfType<SolutionExplorerView>(), v => v.IsEffectivelyVisible);

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

        f.Shell.Run("solution.trust");
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
        f.Shell.Run("explorer.newFile");
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        f.Shell.Dialogs.InputText = "Order.cs";
        f.Shell.Dialogs.Accept();
        await ShellFixture.PumpUntil(() => f.Shell.Layout.ActiveDocument is Daedalus.Shell.Editor.CodeEditorDocument { Title: "Order.cs" });
        Assert.Contains("namespace App;", File.ReadAllText(Path.Combine(appDir, "Order.cs")), StringComparison.Ordinal);

        explorer.Reveal("f:" + Path.Combine(appDir, "appsettings.json"));
        Assert.Equal("appsettings.json", explorer.Selected?.Name);
        f.Shell.Run("explorer.rename");
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        f.Shell.Dialogs.InputText = "Order.cs";
        Assert.True(f.Shell.Dialogs.HasError); // a sibling already has that name
        f.Shell.Dialogs.InputText = "settings.json";
        f.Shell.Dialogs.Accept();
        await ShellFixture.PumpUntil(() => File.Exists(Path.Combine(appDir, "settings.json")));

        // The file moves first; the project file is updated right after.
        await ShellFixture.PumpUntil(() => File.ReadAllText(app).Contains("None Update=\"settings.json\"", StringComparison.Ordinal));

        await ShellFixture.PumpUntil(() => explorer.Rows.Any(r => r.Name == "settings.json"), seconds: 30);
        explorer.Reveal("f:" + Path.Combine(appDir, "settings.json"));
        f.Shell.Run("explorer.delete");
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
        await ShellFixture.PumpUntil(() => f.Shell.Layout.ActiveDocument is Daedalus.Shell.Editor.CodeEditorDocument { Title: "Program.cs" });

        // Opening moves the focus to the editor once its view is attached; take it back after that.
        await ShellFixture.PumpUntil(() => (f.Window.FocusManager?.GetFocusedElement() as Visual)?.FindAncestorOfType<Daedalus.Shell.Editor.CodeEditorView>() is not null);
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

        f.Shell.Run("solution.selectConfiguration");
        await ShellFixture.PumpUntil(() => f.Shell.Palette.IsOpen);
        f.Shell.Palette.Query = "Release|Any";
        f.Shell.Palette.Accept();

        await ShellFixture.PumpUntil(() => f.Shell.StatusBar.Left.Any(i => i.Text == "Release | Any CPU"));
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
        var recentCommand = await f.Shell.Workbench.Commands.ExecuteAsync("workbench.getRecentSolutions", CancellationToken.None);

        Assert.Equal("ProjectSystem.slnx", recentCommand!.Value[0].GetProperty("name").GetString());
        Assert.Equal(f.Shell.Solutions.Solution!.Path, recentCommand.Value[0].GetProperty("path").GetString());
    }
}
