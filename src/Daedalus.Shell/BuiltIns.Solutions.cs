using System.Text.Json;
using Daedalus.Shell.ViewModels;
using Daedalus.Shell.Views;
using Daedalus.Workbench.Commands;
using Daedalus.Workbench.Context;
using Daedalus.Workbench.Menus;
using Daedalus.Workbench.Settings;

namespace Daedalus.Shell;

/// <summary>Solution and Solution Explorer commands, menus and key bindings.</summary>
internal static partial class BuiltIns
{
    /// <summary>Key bindings that act on the Solution Explorer only apply while it has focus.</summary>
    public const string SolutionKeybindings = """
        [
          { "key": "primary+shift+o", "command": "workbench.openSolution" },
          { "key": "primary+r", "command": "workbench.openRecent" },
          { "key": "f2", "command": "explorer.rename", "when": "focusedView == solutionExplorer" },
          { "key": "delete", "command": "explorer.delete", "when": "focusedView == solutionExplorer" },
          { "mac": "cmd+backspace", "command": "explorer.delete", "when": "focusedView == solutionExplorer" },
          { "key": "primary+x", "command": "explorer.cut", "when": "focusedView == solutionExplorer" },
          { "key": "primary+c", "command": "explorer.copy", "when": "focusedView == solutionExplorer" },
          { "key": "primary+v", "command": "explorer.paste", "when": "focusedView == solutionExplorer" }
        ]
        """;

    public static SettingDescriptor RestoreOnOpenSetting() =>
        SettingDescriptor.Create(ShellHost.RestoreOnOpenSetting, "true", SettingType.Boolean, ContributionSource.Shell) with
        {
            Title = "Restore on Open",
            Category = "Projects",
            Description = "Restore NuGet packages in the background when a trusted solution opens.",
        };

    public static void RegisterSolutionCommands(ShellHost shell)
    {
        var commands = shell.Workbench.Commands;
        var explorer = shell.Explorer;
        void Add(string id, string title, string category, CommandHandler handler, string? enablement = null, bool palette = true) =>
            commands.Register(
                new CommandDescriptor(id, title, ContributionSource.Shell)
                {
                    Category = category,
                    Enablement = WhenClause.Parse(enablement),
                    ShowInPalette = palette,
                },
                handler);

        const string Open = "solutionOpen";
        Add("workbench.openSolution", "Open Solution or Project…", "File", async (args, _) =>
        {
            var path = args.Count > 0 && args[0].ValueKind == JsonValueKind.String ? args[0].GetString() : null;
            await Infrastructure.Ui.RunAsync(() => shell.OpenSolutionAsync(path)).ConfigureAwait(false);
            return null;
        });
        Add("workbench.openRecent", "Open Recent…", "File", Ui(shell.OpenRecentAsync));
        Add("workbench.closeSolution", "Close Solution", "File", Ui(shell.CloseSolutionAsync), Open);
        Add("solution.trust", "Trust Solution Folder", "Solution", Ui(shell.TrustSolutionAsync), Open + " && !solutionTrusted");
        Add("solution.selectConfiguration", "Select Configuration…", "Solution", Ui(shell.SelectConfigurationAsync), Open);
        Add("solution.restore", "Restore Packages", "Solution", Ui(shell.RestoreSolutionAsync), Open);
        Add("solution.addExistingProject", "Add Existing Project…", "Solution", Ui(shell.AddExistingProjectAsync), Open);
        Add("solution.addFolder", "New Solution Folder…", "Solution", Ui(explorer.AddSolutionFolderAsync), Open);
        Add("project.addReference", "Add Project Reference…", "Project", Ui(explorer.AddProjectReferenceAsync), Open);
        Add("project.removeFromSolution", "Remove from Solution", "Project", Ui(() => explorer.Selected?.Node.Path is { } p ? explorer.RemoveProjectAsync(p) : Task.CompletedTask), "explorerItemKind == project");

        const string Selection = "focusedView == solutionExplorer && explorerItemKind";
        Add("explorer.newFile", "New File…", "Explorer", Ui(explorer.NewFileAsync), Open);
        Add("explorer.newFolder", "New Folder…", "Explorer", Ui(explorer.NewFolderAsync), Open);
        Add("explorer.rename", "Rename…", "Explorer", Ui(explorer.RenameAsync), Selection, palette: false);
        Add("explorer.delete", "Delete", "Explorer", Ui(explorer.DeleteAsync), Selection, palette: false);
        Add("explorer.cut", "Cut", "Explorer", Ui(explorer.Cut), Selection, palette: false);
        Add("explorer.copy", "Copy", "Explorer", Ui(explorer.Copy), Selection, palette: false);
        Add("explorer.paste", "Paste", "Explorer", Ui(explorer.PasteAsync), Selection, palette: false);
        Add("explorer.copyPath", "Copy Path", "Explorer", Ui(explorer.CopyPathAsync), Open, palette: false);
        Add("explorer.reveal", OperatingSystem.IsMacOS() ? "Reveal in Finder" : OperatingSystem.IsWindows() ? "Reveal in File Explorer" : "Reveal in File Manager", "Explorer", Ui(explorer.RevealInFileManager), Open, palette: false);
        Add("explorer.collapseAll", "Collapse All", "Explorer", Ui(explorer.CollapseAll), Open);

        // For plugins (the Start page): [{ path, name }], most recent first.
        commands.Register(
            new CommandDescriptor("workbench.getRecentSolutions", "Recent Solutions", ContributionSource.Shell) { ShowInPalette = false },
            async (_, _) => await Infrastructure.Ui.RunAsync(shell.RecentSolutionsJsonForPlugins).ConfigureAwait(false));
    }

    public static void RegisterSolutionMenus(MenuRegistry menus)
    {
        menus.AddMenuBarMenu(new MenuBarMenu("project", "_Project", 4));
        var shell = ContributionSource.Shell;
        MenuItemRule Item(string location, string command, string group, int order = 0, string? when = null, string? title = null) =>
            new(location, command, shell) { Group = group, Order = order, When = WhenClause.Parse(when), Title = title };

        const string Context = SolutionExplorerView.ContextMenuLocation;
        const string Containers = "explorerItemKind == project || explorerItemKind == folder || explorerItemKind == file";
        const string SolutionLevel = "explorerItemKind == solution || explorerItemKind == solutionFolder";
        const string Files = "explorerItemKind == file || explorerItemKind == folder";
        const string HasPath = "explorerItemKind == file || explorerItemKind == folder || explorerItemKind == project || explorerItemKind == solution || explorerItemKind == solutionItem";
        menus.Add(
        [
            Item(MenuLocations.MenuBar("file"), "workbench.openSolution", "solution", 1),
            Item(MenuLocations.MenuBar("file"), "workbench.openRecent", "solution", 2),
            Item(MenuLocations.MenuBar("file"), "workbench.closeSolution", "solution", 3),
            Item(MenuLocations.MenuBar("project"), "solution.addExistingProject", "add", 1),
            Item(MenuLocations.MenuBar("project"), "solution.addFolder", "add", 2),
            Item(MenuLocations.MenuBar("project"), "project.addReference", "add", 3),
            Item(MenuLocations.MenuBar("project"), "solution.selectConfiguration", "solution", 1),
            Item(MenuLocations.MenuBar("project"), "solution.restore", "solution", 2),
            Item(MenuLocations.MenuBar("project"), "solution.trust", "solution", 3),

            Item(Context, "explorer.newFile", "1-new", 1, Containers),
            Item(Context, "explorer.newFolder", "1-new", 2, Containers),
            Item(Context, "solution.addExistingProject", "1-new", 3, SolutionLevel),
            Item(Context, "solution.addFolder", "1-new", 4, SolutionLevel),
            Item(Context, "project.addReference", "2-references", 1, "explorerItemKind == project || explorerItemKind == dependencies"),
            Item(Context, "explorer.cut", "3-edit", 1, Files),
            Item(Context, "explorer.copy", "3-edit", 2, Files),
            Item(Context, "explorer.paste", "3-edit", 3, Containers),
            Item(Context, "explorer.rename", "3-edit", 4, Files + " || explorerItemKind == project || explorerItemKind == solutionFolder"),
            Item(Context, "explorer.delete", "3-edit", 5, Files + " || explorerItemKind == solutionFolder"),
            Item(Context, "explorer.delete", "3-edit", 6, "explorerItemIsProjectReference", "Remove Reference"),
            Item(Context, "project.removeFromSolution", "3-edit", 7, "explorerItemKind == project"),
            Item(Context, "solution.selectConfiguration", "4-solution", 1, "explorerItemKind == solution"),
            Item(Context, "solution.restore", "4-solution", 2, "explorerItemKind == solution || explorerItemKind == project"),
            Item(Context, "explorer.collapseAll", "4-solution", 3, "explorerItemKind == solution"),
            Item(Context, "explorer.copyPath", "5-path", 1, HasPath),
            Item(Context, "explorer.reveal", "5-path", 2, HasPath),
        ]);
    }
}
