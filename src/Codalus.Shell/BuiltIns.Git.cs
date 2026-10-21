using Codalus.Shell.ViewModels;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Context;
using Codalus.Workbench.Menus;
using Codalus.Workbench.Settings;
using Codalus.Workbench.ToolWindows;

namespace Codalus.Shell;

/// <summary>Git commands, menus, key bindings and the Changes tool window (ADR 0008).</summary>
internal static partial class BuiltIns
{
    public const string GitKeybindings = """
        [
          { "key": "primary+0 primary+g", "command": "workbench.view.git.changes" },
          { "key": "ctrl+shift+alt+b", "mac": "cmd+alt+b", "command": "git.toggleBlame", "when": "editorTextFocus && gitRepository" },
          { "key": "primary+0 primary+h", "command": "git.history", "when": "gitRepository" },
          { "key": "f8", "command": "git.mergeEditor.nextConflict", "when": "focusedView == mergeEditor" },
          { "key": "shift+f8", "command": "git.mergeEditor.previousConflict", "when": "focusedView == mergeEditor" }
        ]
        """;

    /// <summary>The merge editor's panes: the two versions above the result, or three columns.</summary>
    public const string MergeEditorLayoutSetting = "git.mergeEditor.layout";

    /// <summary>Whether the merge editor shows the incoming version on the left (the default) or the current one.</summary>
    public const string MergeEditorIncomingOnLeftSetting = "git.mergeEditor.incomingOnLeft";

    public static IReadOnlyList<SettingDescriptor> GitSettings()
    {
        var shell = ContributionSource.Shell;
        return
        [
            SettingDescriptor.Create(MergeEditorLayoutSetting, "\"sourcesAbove\"", SettingType.String, shell) with
            {
                Title = "Merge Editor Layout",
                Category = "Source Control",
                Description = "How the merge editor arranges the incoming and current versions and the result. Its gear menu changes it too.",
                Enum = [MergeEditorViewModel.LayoutSourcesAbove, MergeEditorViewModel.LayoutThreeColumns],
                EnumDescriptions = ["The two versions side by side, the result below them", "Three columns, the result in the middle"],
                Scope = SettingScope.User,
                Order = 20,
            },
            SettingDescriptor.Create(MergeEditorIncomingOnLeftSetting, "true", SettingType.Boolean, shell) with
            {
                Title = "Merge Editor: Incoming on the Left",
                Category = "Source Control",
                Description = "Show the incoming version on the left and the current one on the right. Off: the current version on the left.",
                Scope = SettingScope.User,
                Order = 21,
            },
        ];
    }

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

        // A local branch's name or full ref as the argument; none: a quick pick of the branches that have an upstream.
        commands.Register(
            new CommandDescriptor("git.updateBranch", "Update Branch…", ContributionSource.Shell) { Category = "Git", Enablement = WhenClause.Parse("gitRepository") },
            async (args, _) =>
            {
                var branch = args.Count > 0 && args[0].ValueKind == System.Text.Json.JsonValueKind.String ? args[0].GetString() : null;
                await Infrastructure.Ui.RunAsync(() => shell.UpdateBranchAsync(branch)).ConfigureAwait(false);
                return null;
            });
        Add("git.abortMerge", "Abort Merge", shell.AbortMergeAsync, "gitMerging");
        Add("git.fetch", "Fetch", shell.FetchAsync);
        Add("git.pull", "Pull", shell.PullAsync);
        Add("git.push", "Push", shell.PushAsync);
        Add("git.sync", "Sync (Pull then Push)", shell.SyncAsync);
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
        Add("git.dropAllStashes", "Drop All Stashes", shell.DropAllStashesAsync);
        Add("git.rebase", "Rebase Current Branch…", shell.RebaseAsync);
        Add("git.interactiveRebase", "Interactive Rebase…", shell.InteractiveRebaseAsync);
        Add("git.tags", "Tags…", shell.ManageTagsAsync);
        Add("git.remotes", "Remotes…", shell.ManageRemotesAsync);
        Add("git.updateSubmodules", "Update Submodules", shell.UpdateSubmodulesAsync);
        Add("git.clone", "Clone Repository…", shell.CloneRepositoryAsync, string.Empty);
        Add("git.init", "Create Git Repository", shell.CreateRepositoryAsync, "solutionOpen && !gitRepository");
        Add("explorer.addToGitignore", "Add to .gitignore", shell.AddToGitignoreAsync, "gitRepository && explorerItemKind");

        // The merge editor (M13, redesigned 2026-10-19): its toolbar, check boxes and gear menu, from the keyboard.
        const string Merge = "activeDocumentKind == mergeEditor";
        Add("git.mergeEditor.nextConflict", "Next Conflict", () => shell.OnMergeEditorAsync(vm => vm.GoTo(1)), Merge);
        Add("git.mergeEditor.previousConflict", "Previous Conflict", () => shell.OnMergeEditorAsync(vm => vm.GoTo(-1)), Merge);
        Add("git.mergeEditor.takeIncoming", "Take Incoming", () => shell.OnMergeEditorAsync(vm => vm.Accept("incoming")), Merge);
        Add("git.mergeEditor.takeCurrent", "Take Current", () => shell.OnMergeEditorAsync(vm => vm.Accept("current")), Merge);
        Add("git.mergeEditor.takeBoth", "Take Both", () => shell.OnMergeEditorAsync(vm => vm.Accept("both")), Merge);
        Add("git.mergeEditor.takeAllIncoming", "Take All Incoming", () => shell.OnMergeEditorAsync(vm => vm.TakeAll(MergeSide.Incoming, true)), Merge);
        Add("git.mergeEditor.takeAllCurrent", "Take All Current", () => shell.OnMergeEditorAsync(vm => vm.TakeAll(MergeSide.Current, true)), Merge);
        Add("git.mergeEditor.acceptMerge", "Accept Merge", shell.AcceptActiveMergeAsync, Merge);

        // M13b: hosting, worktrees, LFS, several repositories.
        RegisterHostingCommands(shell);
    }
}
