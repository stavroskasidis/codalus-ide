using Codalus.Shell.ViewModels;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Context;
using Codalus.Workbench.Localization;
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
          { "key": "primary+0 primary+r", "command": "git.manageBranches", "when": "gitRepository" },
          { "key": "f8", "command": "git.mergeEditor.nextConflict", "when": "focusedView == mergeEditor" },
          { "key": "shift+f8", "command": "git.mergeEditor.previousConflict", "when": "focusedView == mergeEditor" },
          { "key": "f8", "command": "git.diff.nextChange", "when": "focusedView == diff" },
          { "key": "shift+f8", "command": "git.diff.previousChange", "when": "focusedView == diff" }
        ]
        """;

    /// <summary>The merge editor's panes: the two versions above the result, or three columns.</summary>
    public const string MergeEditorLayoutSetting = "git.mergeEditor.layout";

    /// <summary>Whether the merge editor shows the incoming version on the left (the default) or the current one.</summary>
    public const string MergeEditorIncomingOnLeftSetting = "git.mergeEditor.incomingOnLeft";

    /// <summary>The diff document's layout (#86, D45): the two versions side by side, or one inline pane.</summary>
    public const string DiffLayoutSetting = "git.diff.layout";

    /// <summary>Whether the diff document shows its staging column (D45 question 5, off by default).</summary>
    public const string DiffStagingControlsSetting = "git.diff.showStagingControls";

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
            SettingDescriptor.Create(DiffLayoutSetting, "\"sideBySide\"", SettingType.String, shell) with
            {
                Title = "Diff Layout",
                Category = "Source Control",
                Description = "How a file's diff shows its two versions. The diff's gear menu changes it too.",
                Enum = [DiffViewModel.LayoutSideBySide, DiffViewModel.LayoutInline],
                EnumDescriptions = ["Side by side, aligned row by row", "Inline: each change's removed lines, then its added lines"],
                Scope = SettingScope.User,
                Order = 22,
            },
            SettingDescriptor.Create(DiffStagingControlsSetting, "false", SettingType.Boolean, shell) with
            {
                Title = "Diff: Show Staging Controls",
                Category = "Source Control",
                Description = "Show each change's Stage and Discard (or Unstage) buttons in a column of the diff, between the line numbers and the signs. Hidden, the commands are still on the code's context menu.",
                Scope = SettingScope.User,
                Order = 23,
            },
        ];
    }

    /// <summary>
    /// The Git Changes window's menus (#23, D38), registered as a plugin's would be: <c>gitChanges/context</c> for a file, a
    /// folder or the repository node (groups open, stage, history, path, tree) and <c>gitChanges/branch</c> for a branch of
    /// the branch drop-down (checkout, integrate, manage, inspect). Entries that don't apply are hidden, never grayed
    /// (question 3); keys read here are set for each menu only. Rename… shows F2 and Delete shows Delete, the keys the
    /// drop-down's list handles (question 13).
    /// </summary>
    public static void RegisterGitChangesMenus(MenuRegistry menus, Codalus.Platform.PlatformKind platform)
    {
        var shell = ContributionSource.Shell;
        MenuItemRule Item(string location, string command, string group, int order, string? when = null, string? title = null, string? keys = null) =>
            new(location, command, shell) { Group = group, Order = order, When = WhenClause.Parse(when), Title = title, Keys = keys };

        const string Change = MenuLocations.GitChangesContext;
        const string Branch = MenuLocations.GitChangesBranch;
        const string Shown = "!gitBranchIsCurrent && !gitBranchInOtherWorktree && !gitOperation && !gitHeadUnborn";

        // A Common branches row (all repositories shown, ADR 0085) offers Checkout, New Local Branch From, Merge, Rebase and
        // Delete, each run in every repository; the rest are one repository's, on its own rows.
        const string One = "!gitBranchIsCommon";

        // TODO(issue): Reset and Cherry-Pick Tip on a branch's menu, as the classic IDE's has them (D38, question 20).
        // TODO(issue): Create Pull Request for any branch on its menu; git.createPullRequest takes no branch (question 20).
        // TODO(issue): Take Current and Take Incoming on a folder of Merge Conflicts (D38's T12).
        menus.Add(
        [
            Item(Change, "git.openMergeEditor", "1-open", 1, "gitChangeKind == file && gitChangeSection == conflicts"),
            Item(Change, "git.showDiff", "1-open", 2, "gitChangeKind == file"),
            Item(Change, "workbench.openFile", "1-open", 3, "gitChangeKind == file && gitChangeExists", Localizer.Get("msg.gitChanges.menuOpen", "Open")),
            Item(Change, "git.stage", "2-stage", 1, "gitChangeSection != staged"),
            Item(Change, "git.unstage", "2-stage", 2, "gitChangeSection == staged"),
            Item(Change, "git.discard", "2-stage", 3, "gitChangeSection == changes"),
            Item(Change, "git.stash", "2-stage", 4, "gitChangeSection != conflicts && !gitOperation"),
            Item(Change, "explorer.addToGitignore", "2-stage", 5, "gitChangeAllUntracked"),
            Item(Change, "git.history", "3-history", 1),
            Item(Change, "explorer.reveal", "4-path", 1, "gitChangeExists"),
            Item(Change, "terminal.openInTerminal", "4-path", 2, "gitChangeExists"),
            Item(Change, "explorer.copyPath", "4-path", 3),
            Item(Change, "explorer.copyRelativePath", "4-path", 4, "gitChangeKind != repository"),
            Item(Change, "gitChanges.expandAll", "5-tree", 1, "gitChangeKind != file"),
            Item(Change, "gitChanges.collapseAll", "5-tree", 2, "gitChangeKind != file"),

            Item(Branch, "git.switchBranch", "1-checkout", 1, Shown, Localizer.Get("msg.gitBranches.menuCheckout", "Checkout")),
            Item(Branch, "git.createBranch", "1-checkout", 2, "!gitHeadUnborn", Localizer.Get("msg.gitBranches.menuNewLocalBranchFrom", "New Local Branch From…")),
            Item(Branch, "git.merge", "2-integrate", 1, Shown.Replace(" && !gitBranchInOtherWorktree", string.Empty, StringComparison.Ordinal)),
            Item(Branch, "git.rebase", "2-integrate", 2, Shown.Replace(" && !gitBranchInOtherWorktree", string.Empty, StringComparison.Ordinal)),
            Item(Branch, "git.updateBranch", "3-manage", 1, $"gitBranchKind == local && gitBranchHasUpstream && !gitBranchUpdateBlocked && {One}"),
            Item(Branch, "git.push", "3-manage", 2, $"gitBranchKind == local && gitHasRemote && !gitHeadUnborn && {One}"),
            Item(Branch, "git.renameBranch", "3-manage", 3, $"gitBranchKind == local && !gitHeadUnborn && {One}", Localizer.Get("msg.gitBranches.menuRename", "Rename…"), "f2"),
            Item(Branch, "git.deleteBranch", "3-manage", 4, Shown, Localizer.Get("msg.gitBranches.menuDelete", "Delete"), platform == Codalus.Platform.PlatformKind.MacOS ? "cmd+backspace" : "delete"),
            Item(Branch, "git.compareBranch", "4-inspect", 1, $"!gitBranchIsCurrent && !gitHeadUnborn && {One}", Localizer.Get("msg.gitBranches.menuCompareWithCurrentBranch", "Compare with Current Branch")),
            Item(Branch, "git.history", "4-inspect", 2, $"!gitHeadUnborn && {One}"),
            Item(Branch, "git.copyBranchName", "4-inspect", 3, One),
        ]);
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

        // Commands the Git Changes window's menus run with arguments (#23, D38): a node's path, section and files shown, or a
        // branch's full ref. Without them (the palette, the menu bar) they do what they did before.
        void AddWithArguments(string id, string title, Func<IReadOnlyList<System.Text.Json.JsonElement>, Task> handler, string enablement = "gitRepository", bool palette = true) =>
            commands.Register(
                new CommandDescriptor(id, title, ContributionSource.Shell) { Category = "Git", Enablement = WhenClause.Parse(enablement), ShowInPalette = palette },
                async (args, _) =>
                {
                    await Infrastructure.Ui.RunAsync(() => handler(args)).ConfigureAwait(false);
                    return null;
                });

        Add("git.commit", "Commit…", shell.CommitFromCommandAsync);
        Add("git.refresh", "Refresh", () => shell.Git.RefreshAsync());
        AddWithArguments("git.switchBranch", "Switch Branch…", shell.SwitchBranchCommandAsync);
        AddWithArguments("git.createBranch", "Create Branch…", args => shell.NewBranchFromAsync(
            args.Count > 0 && args[0].ValueKind == System.Text.Json.JsonValueKind.String ? args[0].GetString() : null,
            args.Count > 1 && args[1].ValueKind == System.Text.Json.JsonValueKind.String ? args[1].GetString() : null));
        AddWithArguments("git.merge", "Merge Branch…", shell.MergeBranchCommandAsync);
        AddWithArguments("git.renameBranch", "Rename Branch…", shell.RenameBranchCommandAsync);
        AddWithArguments("git.deleteBranch", "Delete Branch…", shell.DeleteBranchCommandAsync);
        AddWithArguments("git.compareBranch", "Compare with Branch…", shell.CompareBranchCommandAsync);
        AddWithArguments("git.copyBranchName", "Copy Branch Name", shell.CopyBranchNameCommandAsync);

        // The Git Changes window's menu for a file, a folder or the repository node (gitChanges/context).
        AddWithArguments("git.stage", "Stage", shell.StageChangesAsync, palette: false);
        AddWithArguments("git.unstage", "Unstage", shell.UnstageChangesAsync, palette: false);
        AddWithArguments("git.discard", "Discard Changes…", shell.DiscardChangesAsync, palette: false);
        AddWithArguments("git.openMergeEditor", "Open in Merge Editor", shell.OpenMergeEditorCommandAsync, palette: false);
        AddWithArguments("gitChanges.expandAll", "Expand All", args =>
        {
            shell.ExpandUnderCommand(args, expanded: true);
            return Task.CompletedTask;
        }, palette: false);
        AddWithArguments("gitChanges.collapseAll", "Collapse All", args =>
        {
            shell.ExpandUnderCommand(args, expanded: false);
            return Task.CompletedTask;
        }, palette: false);

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
        AddWithArguments("git.push", "Push", shell.PushCommandAsync);
        Add("git.sync", "Sync (Pull then Push)", shell.SyncAsync);
        AddWithArguments("git.showDiff", "Compare with Unmodified", shell.ShowDiffCommandAsync, "gitRepository && (activeDocumentKind == textFile || gitChangeKind == file)");
        Add("git.stageFile", "Stage Active File", shell.StageActiveFileAsync, "gitRepository && activeDocumentKind == textFile");
        Add("git.toggleBlame", "Toggle Blame", shell.ToggleBlameAsync, "gitRepository && activeDocumentKind == textFile");

        // M13: history, operations in progress, stash, rebase, tags, remotes, repositories.
        const string Editor = "gitRepository && activeDocumentKind == textFile";
        AddWithArguments("git.history", "View History", shell.HistoryCommandAsync);
        Add("git.manageBranches", "Manage Branches", () => shell.ShowGitRepositoryAsync());
        Add("git.fileHistory", "View File History", shell.ShowFileHistoryAsync);
        Add("git.lineHistory", "View Line History", shell.ShowLineHistoryAsync, Editor);
        Add("git.showCommit", "Show Commit for Line", shell.ShowCommitAtCaretAsync, Editor);
        Add("git.compareWith", "Compare with Branch or Commit…", shell.CompareWithRevisionAsync, Editor);
        Add("git.continue", "Continue", shell.ContinueOperationAsync, "gitOperation");
        Add("git.abort", "Abort", shell.AbortOperationAsync, "gitOperation");
        Add("git.skip", "Skip Commit", shell.SkipOperationAsync, "gitOperation");
        AddWithArguments("git.stash", "Stash Changes…", shell.StashChangesAsync);
        Add("git.stashes", "Stashes…", shell.ShowStashesAsync);
        Add("git.dropAllStashes", "Drop All Stashes", shell.DropAllStashesAsync);
        AddWithArguments("git.rebase", "Rebase Current Branch…", shell.RebaseBranchCommandAsync);
        Add("git.interactiveRebase", "Interactive Rebase…", shell.InteractiveRebaseAsync);
        Add("git.tags", "Tags…", shell.ManageTagsAsync);
        Add("git.remotes", "Remotes…", shell.ManageRemotesAsync);
        Add("git.updateSubmodules", "Update Submodules", shell.UpdateSubmodulesAsync);
        Add("git.clone", "Clone Repository…", shell.CloneRepositoryAsync, string.Empty);
        Add("git.init", "Create Git Repository", shell.CreateRepositoryAsync, "solutionOpen && !gitRepository");
        commands.Register(
            new CommandDescriptor("explorer.addToGitignore", "Add to .gitignore", ContributionSource.Shell) { Category = "Git", Enablement = WhenClause.Parse("gitRepository && (explorerItemKind || gitChangeKind)") },
            async (args, _) =>
            {
                await Infrastructure.Ui.RunAsync(() => shell.AddToGitignoreCommandAsync(args)).ConfigureAwait(false);
                return null;
            });

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

        // The diff document (#86, D45): its toolbar, gear menu and context menu, from the keyboard and the palette.
        const string Diff = "activeDocumentKind == diff";
        Add("git.diff.nextChange", "Next Change", () => shell.OnDiff(view => view.GoToChange(1)), Diff);
        Add("git.diff.previousChange", "Previous Change", () => shell.OnDiff(view => view.GoToChange(-1)), Diff);
        Add("git.diff.sideBySide", "Diff: Side by Side", () => shell.OnDiffDocument(vm => vm.SideBySide = true), Diff);
        Add("git.diff.inline", "Diff: Inline", () => shell.OnDiffDocument(vm => vm.SideBySide = false), Diff);
        Add("git.diff.refresh", "Refresh Diff", () => shell.OnDiff(view => view.Refresh()), Diff);
        Add("git.diff.openFile", "Open File from Diff", () => shell.OnDiff(view => view.OpenFile()), Diff);
        const string Stage = Diff + " && diffCanStage";
        const string Unstage = Diff + " && diffCanUnstage";
        Add("git.diff.stageChange", "Stage Change", () => shell.OnDiffAsync(view => view.ApplyToChangeAsync(DiffAction.Stage)), Stage);
        Add("git.diff.stageSelectedLines", "Stage Selected Lines", () => shell.OnDiffAsync(view => view.ApplyToSelectedLinesAsync(DiffAction.Stage)), Stage);
        Add("git.diff.discardChange", "Discard Change…", () => shell.OnDiffAsync(view => view.ApplyToChangeAsync(DiffAction.Discard)), Stage);
        Add("git.diff.discardSelectedLines", "Discard Selected Lines…", () => shell.OnDiffAsync(view => view.ApplyToSelectedLinesAsync(DiffAction.Discard)), Stage);
        Add("git.diff.unstageChange", "Unstage Change", () => shell.OnDiffAsync(view => view.ApplyToChangeAsync(DiffAction.Unstage)), Unstage);
        Add("git.diff.unstageSelectedLines", "Unstage Selected Lines", () => shell.OnDiffAsync(view => view.ApplyToSelectedLinesAsync(DiffAction.Unstage)), Unstage);
        Add("git.diff.stageAll", "Stage All", () => shell.OnDiffAsync(view => view.StageAllAsync()), Stage);
        Add("git.diff.discardAll", "Discard All…", () => shell.OnDiffAsync(view => view.DiscardAllAsync()), Stage);
        Add("git.diff.unstageAll", "Unstage All", () => shell.OnDiffAsync(view => view.UnstageAllAsync()), Unstage);
        Add("git.diff.toggleSummary", "Diff: Summary", () => shell.OnDiffDocument(vm => vm.IsSummary = !vm.IsSummary), Diff);
        Add("git.diff.toggleStagingControls", "Show Staging Controls", () => shell.OnDiffDocument(vm => vm.ShowStagingControls = !vm.ShowStagingControls), Diff + " && (diffCanStage || diffCanUnstage)");

        // M13b: hosting, worktrees, LFS, several repositories.
        RegisterHostingCommands(shell);
    }
}
