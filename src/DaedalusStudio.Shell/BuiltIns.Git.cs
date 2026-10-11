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
          { "key": "primary+alt+b", "command": "git.toggleBlame", "when": "editorTextFocus && gitRepository" },
          { "key": "primary+0 primary+h", "command": "git.history", "when": "gitRepository" }
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

        // M13: history, operations in progress, stash, rebase, tags, remotes, repositories.
        const string Editor = "gitRepository && activeDocumentKind == textFile";
        Add("git.history", "View History", () => shell.ShowHistoryAsync());
        Add("git.fileHistory", "View File History", shell.ShowFileHistoryAsync);
        Add("git.lineHistory", "View Line History", shell.ShowLineHistoryAsync, Editor);
        Add("git.showCommit", "Show Commit for Line", shell.ShowCommitAtCaretAsync, Editor);
        Add("git.compareWith", "Compare with Branch or Commit…", shell.CompareWithRevisionAsync, Editor);
        Add("git.continue", "Continue", shell.ContinueOperationAsync, "gitOperation");
        Add("git.abort", "Abort", shell.AbortOperationAsync, "gitOperation");
        Add("git.skip", "Skip Commit", shell.SkipOperationAsync, "gitOperation");
        Add("git.stash", "Stash Changes…", shell.StashAsync);
        Add("git.stashes", "Stashes…", shell.ShowStashesAsync);
        Add("git.rebase", "Rebase Current Branch…", shell.RebaseAsync);
        Add("git.interactiveRebase", "Interactive Rebase…", shell.InteractiveRebaseAsync);
        Add("git.tags", "Tags…", shell.ManageTagsAsync);
        Add("git.remotes", "Remotes…", shell.ManageRemotesAsync);
        Add("git.updateSubmodules", "Update Submodules", shell.UpdateSubmodulesAsync);
        Add("git.clone", "Clone Repository…", shell.CloneRepositoryAsync, string.Empty);
        Add("git.init", "Create Git Repository", shell.CreateRepositoryAsync, "solutionOpen && !gitRepository");
        Add("explorer.addToGitignore", "Add to .gitignore", shell.AddToGitignoreAsync, "gitRepository && explorerItemKind");

        // M13b: hosting, worktrees, LFS, several repositories.
        RegisterHostingCommands(shell);
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
            Item("git.history", "1-commit", 3),
            Item("git.continue", "3-operation", 1),
            Item("git.skip", "3-operation", 2),
            Item("git.abort", "3-operation", 3),
            Item("git.stash", "3-branch", 5),
            Item("git.stashes", "3-branch", 6),
            Item("git.rebase", "3-branch", 7),
            Item("git.interactiveRebase", "3-branch", 8),
            Item("git.tags", "3-branch", 9),
            Item("git.remotes", "2-remote", 4),
            Item("git.fileHistory", "4-file", 4),
            Item("git.lineHistory", "4-file", 5),
            Item("git.showCommit", "4-file", 6),
            Item("git.compareWith", "4-file", 7),
            Item("git.clone", "0-repository", 1),
            Item("git.init", "0-repository", 2),
            Item("git.updateSubmodules", "5-refresh", 2),
            new(MenuLocations.EditorContext, "git.lineHistory", shell) { Group = "5-git", Order = 1, When = WhenClause.Parse("gitRepository") },
            new(MenuLocations.EditorContext, "git.showCommit", shell) { Group = "5-git", Order = 2, When = WhenClause.Parse("gitRepository") },
            new(MenuLocations.EditorContext, "git.compareWith", shell) { Group = "5-git", Order = 3, When = WhenClause.Parse("gitRepository") },
            new(Views.SolutionExplorerView.ContextMenuLocation, "git.fileHistory", shell) { Group = "6-git", Order = 1, When = WhenClause.Parse("gitRepository && explorerItemKind != solution") },
            new(Views.SolutionExplorerView.ContextMenuLocation, "explorer.addToGitignore", shell) { Group = "6-git", Order = 2, When = WhenClause.Parse("gitRepository && explorerItemKind != solution") },
        ]);
        RegisterHostingMenus(menus);
    }
}
