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
}
