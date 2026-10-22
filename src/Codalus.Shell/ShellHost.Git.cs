using System.Globalization;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Git;
using Codalus.Workbench.Output;
using Codalus.Workbench.StatusBar;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>Git (ADR 0008): the repository of the open solution, the Changes view, diffs, branches, remotes and blame.</summary>
public sealed partial class ShellHost
{
    private const string BranchStatusItem = "codalus.git.branch";

    public GitService Git { get; private set; } = null!;

    public GitChangesViewModel GitChanges { get; private set; } = null!;

    GitService IEditorServices.Git => Git;

    public OutputChannel GitLog => Workbench.Output.GetOrCreate("codalus.git", "Git");

    private void InitializeGit()
    {
        Git = new GitService(watch: Options.WatchFiles);
        Git.Log += (_, line) => GitLog.AppendLine(line);
        Git.StatusChanged += (_, _) => Ui.Post(UpdateGitStatus);
        Git.StatusChanged += (_, _) => Ui.Post(ReloadGitDiffs);
        Git.IndexChanged += (_, _) => Ui.Post(ReloadGitDiffs);
        GitChanges = new GitChangesViewModel(Git, (path, staged) => OpenDiffAsync(path, staged), command => RunGitCommandAsync(command));
        GitChanges.OpenFile = path => OpenFileAsync(path);
        GitChanges.RunWithArguments = RunOnGitChangeAsync;
        GitBranches = new GitBranchPickerViewModel(Git)
        {
            Checkout = branch => CheckoutBranchAsync(branch.FullRef),
            NewBranch = name => NewBranchFromAsync(null, name),
            MenuFor = GitBranchMenu,
            RunOnBranch = (command, branch) => GitBranchMenuItems(branch).Any(i => i.Command == command)
                ? RunAsync(command, [System.Text.Json.JsonSerializer.SerializeToElement(branch.FullRef, Docking.ShellJsonContext.Default.String)], GitBranchScope(branch))
                : Task.CompletedTask,
        };
        GitChanges.Branches = GitBranches;
        GitChanges.MenuFor = GitChangesMenu;
        GitChangesView.ReportError = ReportGitError;
        DiffView.ReportError = ReportGitError;
        MergeEditorView.ReportError = ReportGitError;
        UpdateGitStatus();
    }

    private void ReportGitError(Exception ex)
    {
        GitLog.AppendLine("error: " + ex.Message);
        Workbench.Notifications.Error("Git: " + ex.Message);
    }

    /// <summary>Runs a Git action, reporting git's errors as notifications (never throws for them).</summary>
    private async Task GitActionAsync(Func<Task> action, string? progress = null)
    {
        if (Git.Root is null)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.git.theOpenSolutionIsNotIn", "The open solution is not in a git repository."), TimeSpan.FromSeconds(4));
            return;
        }

        try
        {
            if (progress is not null)
            {
                StatusBar.ShowMessage(progress);
            }

            await action().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is GitException or InvalidOperationException or IOException)
        {
            ReportGitError(ex);
        }
        finally
        {
            if (progress is not null)
            {
                StatusBar.ClearMessage();
            }
        }
    }

    private Task RunGitCommandAsync(string command)
    {
        if (command.StartsWith("git.discard:", StringComparison.Ordinal))
        {
            var path = command["git.discard:".Length..];
            return DiscardChangesAsync(GitChangesViewModel.ArgumentsFor(path, "changes", [path]));
        }

        if (command.StartsWith("git.mergeEditor:", StringComparison.Ordinal))
        {
            return OpenMergeEditorAsync(command["git.mergeEditor:".Length..]);
        }

        if (command.StartsWith("git.dropStash:", StringComparison.Ordinal))
        {
            return DropStashAsync(command["git.dropStash:".Length..]);
        }

        Run(command);
        return Task.CompletedTask;
    }

    /// <summary>Opens the repository of the solution in <paramref name="folder"/> (none: Git features stay hidden).</summary>
    private async Task OpenRepositoryAsync(string folder)
    {
        try
        {
            if (!await Git.OpenAsync(folder, CancellationToken.None).ConfigureAwait(true))
            {
                UpdateGitStatus();
            }
        }
        catch (GitException ex)
        {
            GitLog.AppendLine(ex.Message);
            UpdateGitStatus();
        }
    }

    private void UpdateGitStatus()
    {
        Workbench.Context.Set("gitRepository", Git.Status is not null);
        Workbench.Context.Set("gitMerging", Git.Status?.IsMerging ?? false);
        UpdateGitDepthStatus();
        _ = UpdateHasRemoteAsync();
        if (Git.Status is not { } status)
        {
            Workbench.StatusBar.Remove(BranchStatusItem);
            return;
        }

        var branch = status.Branch ?? "(detached)";
        var sync = status.Upstream is null ? string.Empty : $" ↑{status.Ahead.ToString(CultureInfo.InvariantCulture)} ↓{status.Behind.ToString(CultureInfo.InvariantCulture)}";
        var changes = status.Files.Count;
        Workbench.StatusBar.Set(new StatusBarItem(BranchStatusItem, $"$(branch) {branch}{sync}{(changes > 0 ? "  •" + changes.ToString(CultureInfo.InvariantCulture) : string.Empty)}", StatusBarAlignment.Right, 950, ContributionSource.Shell.Id)
        {
            Command = "git.switchBranch",
            Tooltip = $"Branch {branch}{(status.Upstream is { } upstream ? $" (tracking {upstream})" : " (not published)")}; {changes.ToString(CultureInfo.InvariantCulture)} changed file(s). Click to switch branches.",
            AccessibleName = $"Git branch {branch}, {changes.ToString(CultureInfo.InvariantCulture)} changes",
        });
    }

    private string? _remotesReadFor;

    /// <summary>Whether the repository has a remote (B17, B18: the window's Publish to a Git Host… link), read once per repository and after Manage Remotes.</summary>
    private async Task UpdateHasRemoteAsync(bool force = false)
    {
        if (Git.Root is not { } root || (!force && _remotesReadFor == root))
        {
            return;
        }

        _remotesReadFor = root;
        try
        {
            GitChanges.HasRemote = (await Git.RemotesAsync().ConfigureAwait(true)).Count > 0;
        }
        catch (Exception ex) when (ex is GitException or InvalidOperationException or IOException)
        {
            GitLog.AppendLine("git remote failed: " + ex.Message);
        }
    }

    /// <summary>Publish to a Git Host…, then the window's link follows the remote it added (B18).</summary>
    public async Task PublishAndReadRemotesAsync()
    {
        await PublishAsync().ConfigureAwait(true);
        await UpdateHasRemoteAsync(force: true).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ diffs and files

    /// <summary>Compares two files side by side (<c>codalus diff</c>, diff tools such as Verify's).</summary>
    public async Task<DiffDocument> OpenFileDiffAsync(string left, string right)
    {
        if (Layout.FindDocument<DiffDocument>(DiffDocument.IdFor(left, right)) is not { } document)
        {
            document = NewDiffDocument(new DiffViewModel(left, right));
        }

        await document.ViewModel.LoadAsync().ConfigureAwait(true);
        Layout.OpenDocument(document, focus: true);
        return document;
    }

    /// <summary>Opens a file's diff: work tree against the index, or (<paramref name="staged"/>) the index against HEAD.</summary>
    public async Task<DiffDocument?> OpenDiffAsync(string path, bool staged)
    {
        if (Layout.FindDocument<DiffDocument>(DiffDocument.IdFor(path, staged)) is { } open)
        {
            Layout.OpenDocument(open, focus: true);
            await open.ViewModel.LoadAsync().ConfigureAwait(true);
            return open;
        }

        var document = NewDiffDocument(new DiffViewModel(Git, path, staged));
        try
        {
            await document.ViewModel.LoadAsync().ConfigureAwait(true);
        }
        catch (GitException ex)
        {
            ReportGitError(ex);
            return null;
        }

        Layout.OpenDocument(document, focus: true);
        return document;
    }

    /// <summary>The active file's working-tree diff.</summary>
    public Task ShowActiveFileDiffAsync() =>
        ActiveEditor?.Model.Path is { } path ? GitActionAsync(() => OpenDiffAsync(path, staged: false)) : Task.CompletedTask;

    /// <summary>
    /// Discards a file's working-tree changes after asking (Git Changes, the diff's Discard All…). With
    /// <paramref name="unsavedEditsLost"/>, the question says the open document's unsaved edits go too: it is saved, then
    /// restored from the index (D45 question 15). True when it was discarded.
    /// </summary>
    public async Task<bool> DiscardAsync(string path, bool unsavedEditsLost = false)
    {
        var name = Path.GetFileName(path);
        var untracked = Git.Status?.Files.Any(f => f.IsUntracked && Codalus.Workbench.Projects.SolutionService.PathComparer.Equals(f.Path, path)) == true;
        var choice = await Dialogs.ShowAsync(new DialogRequest(
            $"Discard changes to {name}?",
            untracked ? $"{name} is not tracked by git: it will be deleted." : $"The changes to {name} that are not staged will be lost.",
            ["Discard", "Cancel"])
        {
            DefaultButton = "Cancel",
            IsDanger = true,
            Details = unsavedEditsLost ? [new DetailLine(Localizer.Format("msg.git.unsavedEditsLostToo", "{0} also has unsaved edits, and they will be lost too.", name), "note")] : null,
        }).ConfigureAwait(true);
        if (choice.Button != "Discard")
        {
            return false;
        }

        var discarded = false;
        await GitActionAsync(async () =>
        {
            // From the diff, the open document is saved first (its edits are in the diff, and go with the rest).
            var model = unsavedEditsLost ? DiffServices.OpenDocument(path) : null;
            if (model is { IsModified: true } && !await SaveAsync(model).ConfigureAwait(true))
            {
                return;
            }

            await Git.DiscardAsync([path]).ConfigureAwait(true);
            if (model is not null && File.Exists(path))
            {
                await model.ReloadAsync(CancellationToken.None).ConfigureAwait(true);
            }

            discarded = true;
        }).ConfigureAwait(true);
        return discarded;
    }

    public Task ToggleBlameAsync() => ActiveEditor?.Controller is { } controller ? controller.ToggleBlameAsync() : Task.CompletedTask;

    public Task StageActiveFileAsync() =>
        ActiveEditor?.Model.Path is { } path ? GitActionAsync(async () =>
        {
            await SaveActiveDocumentAsync().ConfigureAwait(true);
            await Git.StageAsync([path]).ConfigureAwait(true);
        }) : Task.CompletedTask;

    // ------------------------------------------------------------------ branches and remotes

    public Task SwitchBranchAsync() => GitActionAsync(async () =>
    {
        var branches = await Git.BranchesAsync().ConfigureAwait(true);
        var locals = branches.Where(b => !b.IsRemote).Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        var items = new List<QuickPickItem> { new("+ Create New Branch…") { Tag = "create" } };
        items.AddRange(branches
            .Where(b => !b.IsRemote || !locals.Contains(b.Name[(b.Name.IndexOf('/', StringComparison.Ordinal) + 1)..]))
            .OrderBy(b => b.IsRemote)
            .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .Select(b => new QuickPickItem(b.Name) { Tag = b, Detail = b.IsCurrent ? "current" : b.IsRemote ? "remote" : b.Upstream }));
        switch (await Palette.ShowAsync(items, "Switch to a branch", Math.Max(0, items.FindIndex(i => i.Tag is GitBranch { IsCurrent: true }))).ConfigureAwait(true))
        {
            case { Tag: "create" }:
                await CreateBranchAsync().ConfigureAwait(true);
                break;
            case { Tag: GitBranch { IsCurrent: false } branch }:
                await Git.SwitchAsync(branch).ConfigureAwait(true);
                break;
        }
    });

    public async Task CreateBranchAsync()
    {
        var result = await Dialogs.ShowAsync(new DialogRequest(
            "Create Branch",
            $"New branch from {Git.Status?.Branch ?? "HEAD"} (and switch to it):",
            ["Create", "Cancel"])
        {
            Input = string.Empty,
            Validate = name => string.IsNullOrWhiteSpace(name) ? "Enter a name."
                : name.Any(c => char.IsWhiteSpace(c) || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\') || name.Contains("..", StringComparison.Ordinal) ? "Not a valid branch name."
                : null,
        }).ConfigureAwait(true);
        if (result is { Button: "Create", Input: { } name })
        {
            await GitActionAsync(() => Git.CreateBranchAsync(name.Trim(), switchTo: true)).ConfigureAwait(true);
        }
    }

    public Task MergeBranchAsync() => GitActionAsync(async () =>
    {
        var branches = (await Git.BranchesAsync().ConfigureAwait(true)).Where(b => !b.IsCurrent).ToList();
        var items = branches.Select(b => new QuickPickItem(b.Name) { Tag = b.Name, Detail = b.IsRemote ? "remote" : null }).ToList();
        if (await Palette.ShowAsync(items, $"Merge a branch into {Git.Status?.Branch ?? "HEAD"}").ConfigureAwait(true) is { Tag: string branch })
        {
            try
            {
                await Git.MergeAsync(branch).ConfigureAwait(true);
                StatusBar.ShowMessage(Localizer.Format("msg.git.merged", "Merged {0}.", branch), TimeSpan.FromSeconds(5));
            }
            catch (GitException) when (Git.Status is { IsMerging: true })
            {
                Workbench.Notifications.Warning(Localizer.Format("msg.git.mergingProducedConflictsResolveThemIn", "Merging {0} produced conflicts. Resolve them in the Git Changes window, stage the files and commit, or abort the merge.", branch));
                Layout.ShowToolWindow(GitChangesViewModel.ToolWindowId);
            }
        }
    });

    /// <summary>
    /// Update Branch… (Git → Branches, the Git Changes window's ⋯ menu, the palette): brings a local branch up to date with
    /// its upstream without checking it out and pulling (<see cref="GitService.UpdateBranchAsync"/>, fast-forward only; D38,
    /// question 21). <paramref name="branch"/> is a local branch's name or full ref; with none, a quick pick of the local
    /// branches that have an upstream, the current one first, leaving out (as Pull is hidden there) the current branch
    /// during a merge or rebase and the branch a rebase or bisect here works on. While it runs, the status bar says so
    /// with a cancel button (B23); then it says how many commits came, that the branch was up to date, or that it has
    /// only commits of its own (B24). A notification explains a branch that has diverged, with Checkout (B25), one checked
    /// out in another worktree, with Open in New Window (B26), one with no upstream, and one a merge, rebase or bisect holds.
    /// </summary>
    public Task UpdateBranchAsync(string? branch = null) => GitActionAsync(async () =>
    {
        var branches = await Git.BranchesAsync().ConfigureAwait(true);
        var target = string.IsNullOrWhiteSpace(branch) ? null : branch.Trim();
        if (target is null)
        {
            var held = (await Git.BranchOperationsAsync().ConfigureAwait(true)).Where(o => o.IsThisWorktree).Select(o => o.Branch).ToHashSet(StringComparer.Ordinal);
            var busy = Git.Status?.Operation is "merge" or "rebase";
            var candidates = branches
                .Where(b => b is { IsRemote: false, Upstream: not null } && !held.Contains(b.Name) && !(b.IsCurrent && busy))
                .OrderByDescending(b => b.IsCurrent)
                .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (candidates.Count == 0)
            {
                StatusBar.ShowMessage(Localizer.Get("msg.git.noLocalBranchHasAnUpstream", "No local branch has an upstream branch to update from."), TimeSpan.FromSeconds(5));
                return;
            }

            var items = candidates.Select(b => new QuickPickItem(b.Name)
            {
                Tag = b.Name,
                Detail = b.IsCurrent ? Localizer.Format("msg.git.currentBranchFrom", "current, from {0}", b.Upstream!) : Localizer.Format("msg.git.branchFrom", "from {0}", b.Upstream!),
            }).ToList();
            if (await Palette.ShowAsync(items, Localizer.Get("msg.git.updateWhichBranch", "Update which branch from its upstream?")).ConfigureAwait(true) is not { Tag: string picked })
            {
                return;
            }

            target = picked;
        }

        var name = target.StartsWith("refs/heads/", StringComparison.Ordinal) ? target["refs/heads/".Length..] : target;
        var known = branches.FirstOrDefault(b => !b.IsRemote && b.Name == name);
        using var cancel = new CancellationTokenSource();
        var progress = known?.Upstream is { } upstream
            ? Workbench.Progress.Begin(Localizer.Format("msg.git.updatingBranchFrom", "Updating {0} from {1}", name, upstream), "Git", cancel.Cancel)
            : null;
        GitBranchUpdate update;

        // The drop-down stays open: the row says Updating… where its chips go (B23), then the list reloads (B24).
        GitBranches.SetBusy(name, busy: true);
        try
        {
            update = await Git.UpdateBranchAsync(target, cancel.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            StatusBar.ShowMessage(Localizer.Format("msg.git.updatingBranchWasCancelled", "Updating {0} was cancelled.", name), TimeSpan.FromSeconds(5));
            return;
        }
        finally
        {
            progress?.End();
            GitBranches.SetBusy(name, busy: false);
            if (GitBranches.IsOpen)
            {
                _ = GitBranches.LoadAsync();
            }
        }

        ReportBranchUpdate(update, known);
    });

    /// <summary>What Update Branch did, in the words and with the actions of D38's B24-B26.</summary>
    private void ReportBranchUpdate(GitBranchUpdate update, GitBranch? known)
    {
        var (name, upstream) = (update.Branch, update.Upstream ?? string.Empty);
        switch (update.Outcome)
        {
            case GitBranchUpdateOutcome.Updated:
                StatusBar.ShowMessage(Localizer.Format("msg.git.updatedBranchFromCommits", "Updated {0} from {1}: {2}.", name, upstream, Commits(update.Behind)), TimeSpan.FromSeconds(5));
                break;
            case GitBranchUpdateOutcome.UpToDate:
                StatusBar.ShowMessage(Localizer.Format("msg.git.branchIsAlreadyUpToDateWith", "{0} is already up to date with {1}.", name, upstream), TimeSpan.FromSeconds(5));
                break;
            case GitBranchUpdateOutcome.Ahead:
                StatusBar.ShowMessage(Localizer.Format("msg.git.branchHasNothingToUpdateAhead", "{0} has nothing to update from {1}: it's ahead by {2}, which Push sends.", name, upstream, Commits(update.Ahead)), TimeSpan.FromSeconds(5));
                break;
            case GitBranchUpdateOutcome.Diverged when update.IsCurrent:
                var currentDiverged = Localizer.Format("msg.git.currentBranchHasDiverged", "{0} has diverged from {1}: {2} here, {3} there. Update only fast-forwards; merge {1} into it, or rebase it onto {1}.", name, upstream, Commits(update.Ahead), update.Behind);
                if (GitBranches.IsOpen)
                {
                    GitBranches.ShowBanner(currentDiverged, warning: true);
                    break;
                }

                Workbench.Notifications.Warning(currentDiverged);
                break;
            case GitBranchUpdateOutcome.Diverged:
                var checkout = Localizer.Get("msg.git.checkout", "Checkout");
                var divergedText = Localizer.Format("msg.git.branchHasDiverged", "{0} has diverged from {1}: {2} here, {3} there. Update only fast-forwards; check it out to merge or rebase.", name, upstream, Commits(update.Ahead), update.Behind);
                var branch = known ?? new GitBranch(name, IsRemote: false, IsCurrent: false, update.Upstream, update.Before ?? string.Empty);

                // From the drop-down, its banner with Checkout (B25); from the palette, a notification with the same action.
                if (GitBranches.IsOpen)
                {
                    GitBranches.ShowBanner(divergedText, warning: true, checkout, "branch", () => CheckoutBranchAsync("refs/heads/" + branch.Name));
                    break;
                }

                var diverged = Workbench.Notifications.Warning(divergedText, checkout);
                _ = diverged.Result.ContinueWith(t => { if (t.Result == checkout) { Ui.Post(() => _ = GitActionAsync(() => Git.SwitchAsync(branch))); } }, TaskScheduler.Default);
                break;
            case GitBranchUpdateOutcome.CheckedOutElsewhere:
                var where = update.WorktreePath ?? Localizer.Get("msg.git.anotherWorktree", "another worktree");
                var message = update.Operation switch
                {
                    "rebase" => Localizer.Format("msg.git.branchIsBeingRebasedIn", "{0} is being rebased in {1}, so it can't be updated here.", name, where),
                    "bisect" => Localizer.Format("msg.git.branchIsBeingBisectedIn", "{0} is being bisected in {1}, so it can't be updated here.", name, where),
                    _ => Localizer.Format("msg.git.branchIsCheckedOutInCantBeUpdated", "{0} is checked out in {1}, so it can't be updated here. Pull in that window updates it.", name, where),
                };
                if (update.WorktreePath is not { } path)
                {
                    Workbench.Notifications.Warning(message);
                    break;
                }

                var open = Localizer.Get("msg.git.openInNewWindow", "Open in New Window");
                if (GitBranches.IsOpen)
                {
                    // B26: the drop-down's banner says where, with Open in New Window.
                    GitBranches.ShowBanner(message, warning: false, open, "window-new", () =>
                    {
                        StartNewWindow(path);
                        return Task.CompletedTask;
                    });
                    break;
                }

                _ = Workbench.Notifications.Warning(message, open).Result.ContinueWith(t => { if (t.Result == open) { Ui.Post(() => StartNewWindow(path)); } }, TaskScheduler.Default);
                break;
            case GitBranchUpdateOutcome.OperationInProgress:
                Workbench.Notifications.Info(update.Operation switch
                {
                    "merge" => Localizer.Format("msg.git.branchCantBeUpdatedDuringMerge", "{0} can't be updated while a merge is in progress. Commit or abort the merge first.", name),
                    "bisect" => Localizer.Format("msg.git.branchIsBeingBisected", "{0} is being bisected, so it can't be updated now. End the bisect first.", name),
                    _ => Localizer.Format("msg.git.branchIsBeingRebased", "{0} is being rebased, so it can't be updated now. Continue or abort the rebase first.", name),
                });
                break;
            case GitBranchUpdateOutcome.NoUpstream:
                Workbench.Notifications.Info(Localizer.Format("msg.git.branchHasNoUpstream", "{0} has no upstream branch to update from.", name));
                break;
        }
    }

    private static string Commits(int count) => count == 1
        ? Localizer.Get("msg.git.oneCommit", "1 commit")
        : Localizer.Format("msg.git.commitCount", "{0} commits", count);

    public Task FetchAsync() => GitActionAsync(() => Git.FetchAsync(), "Fetching…");

    public Task PullAsync() => GitActionAsync(() => Git.PullAsync(), "Pulling…");

    public Task PushAsync() => GitActionAsync(() => Git.PushAsync(), "Pushing…");

    /// <summary>Pulls, then pushes the branch's own commits.</summary>
    public Task SyncAsync() => GitActionAsync(async () =>
    {
        if (Git.Status?.Upstream is not null)
        {
            await Git.PullAsync().ConfigureAwait(true);
        }

        await Git.PushAsync().ConfigureAwait(true);
    }, "Syncing…");

    public Task AbortMergeAsync() => GitActionAsync(() => Git.AbortMergeAsync());

    public Task CommitFromCommandAsync()
    {
        Layout.ShowToolWindow(GitChangesViewModel.ToolWindowId, focus: true);
        return Task.CompletedTask;
    }
}
