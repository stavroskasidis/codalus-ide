using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Commands;
using DaedalusStudio.Workbench.Context;
using DaedalusStudio.Workbench.Menus;
using DaedalusStudio.Workbench.ToolWindows;

namespace DaedalusStudio.Shell;

/// <summary>Git commands, menus, key bindings and the Changes tool window (ADR 0008).</summary>
internal static partial class BuiltIns
{
    public const string GitKeybindings = """
        [
          { "key": "primary+0 primary+g", "command": "workbench.view.git.changes" },
          { "key": "primary+alt+b", "command": "git.toggleBlame", "when": "editorTextFocus && gitRepository" }
        ]
        """;

    public static ToolWindowDescriptor GitToolWindow() =>
        new(GitChangesViewModel.ToolWindowId, "Git Changes", ToolWindowLocation.Right, ToolWindowKind.BuiltIn, ContributionSource.Shell) { Icon = "branch" };

    public static void RegisterGitCommands(ShellHost shell)
    {
        var commands = shell.Workbench.Commands;
        void Add(string id, string title, Func<Task> handler, string enablement = "gitRepository") =>
            commands.Register(
                new CommandDescriptor(id, title, ContributionSource.Shell) { Category = "Git", Enablement = WhenClause.Parse(enablement) },
                Ui(handler));

        Add("git.commit", "Commit…", shell.CommitFromCommandAsync);
        Add("git.refresh", "Refresh", () => shell.Git.RefreshAsync());
        Add("git.switchBranch", "Switch Branch…", shell.SwitchBranchAsync);
        Add("git.createBranch", "Create Branch…", shell.CreateBranchAsync);
        Add("git.merge", "Merge Branch…", shell.MergeBranchAsync);
        Add("git.abortMerge", "Abort Merge", shell.AbortMergeAsync, "gitMerging");
        Add("git.fetch", "Fetch", shell.FetchAsync);
        Add("git.pull", "Pull", shell.PullAsync);
        Add("git.push", "Push", shell.PushAsync);
        Add("git.showDiff", "Compare with Unmodified", shell.ShowActiveFileDiffAsync, "gitRepository && activeDocumentKind == textFile");
        Add("git.stageFile", "Stage Active File", shell.StageActiveFileAsync, "gitRepository && activeDocumentKind == textFile");
        Add("git.toggleBlame", "Toggle Blame", shell.ToggleBlameAsync, "gitRepository && activeDocumentKind == textFile");
    }

    public static void RegisterGitMenus(MenuRegistry menus)
    {
        menus.AddMenuBarMenu(new MenuBarMenu("git", "_Git", 35));
        var shell = ContributionSource.Shell;
        MenuItemRule Item(string command, string group, int order, string? title = null) =>
            new(MenuLocations.MenuBar("git"), command, shell) { Group = group, Order = order, Title = title };

        menus.Add(
        [
            Item("git.commit", "1-commit", 1),
            Item(ViewCommandPrefix + GitChangesViewModel.ToolWindowId, "1-commit", 2, "Git Changes"),
            Item("git.fetch", "2-remote", 1),
            Item("git.pull", "2-remote", 2),
            Item("git.push", "2-remote", 3),
            Item("git.switchBranch", "3-branch", 1),
            Item("git.createBranch", "3-branch", 2),
            Item("git.merge", "3-branch", 3),
            Item("git.abortMerge", "3-branch", 4),
            Item("git.showDiff", "4-file", 1),
            Item("git.stageFile", "4-file", 2),
            Item("git.toggleBlame", "4-file", 3),
            Item("git.refresh", "5-refresh", 1),
        ]);
    }
}
