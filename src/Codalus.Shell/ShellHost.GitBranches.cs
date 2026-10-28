using System.Text.Json;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Context;
using Codalus.Workbench.Git;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Menus;

namespace Codalus.Shell;

/// <summary>
/// The Git Changes window's branch drop-down and each branch's menu (#23, D38): <c>gitChanges/branch</c>, built with the
/// branch's keys scoped to that menu; its commands get the branch's full ref, and their titles name the branches, as the
/// classic IDE's do (question 5). With no ref (the palette, Git → Branches) each command asks which branch.
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>The branch drop-down's list, banners and busy rows.</summary>
    public GitBranchPickerViewModel GitBranches { get; private set; } = null!;

    /// <summary>
    /// The context <c>gitChanges/branch</c>'s when-clauses read for a branch: its kind, whether it's the current branch (or
    /// the one a rebase here works on), has an upstream, is checked out in another worktree, has a local branch (a remote
    /// one), whether Update would touch the current branch during a merge or rebase, and the repository's state.
    /// </summary>
    internal IContextKeyReader GitBranchScope(GitBranchDetails branch)
    {
        if (GitBranches.IsCommon(branch))
        {
            return CommonBranchScope(branch);
        }

        var git = GitBranches.IsAllRepositories ? GitBranches.RepositoryOf(branch) : Repo;
        var status = git.Status;
        var current = !branch.IsRemote && (branch.IsCurrent && status?.Branch is not null || branch.IsRebasingHere);
        var menuContext = new MenuContext();
        menuContext.Set("gitBranchKind", branch.IsRemote ? "remote" : "local");
        menuContext.Set("gitBranchIsCurrent", current);
        menuContext.Set("gitBranchHasUpstream", branch is { Upstream: not null, UpstreamGone: false });
        menuContext.Set("gitBranchInOtherWorktree", branch.WorktreePath is not null);
        menuContext.Set("gitBranchHasLocal", GitBranches.LocalTwin(branch) is not null);
        menuContext.Set("gitBranchUpdateBlocked", current && status?.Operation is "merge" or "rebase");
        menuContext.Set("gitHasRemote", GitBranches.HasRemoteFor(branch));
        menuContext.Set("gitBranchIsCommon", false);
        menuContext.Set("gitBranchRepository", git.Root);
        menuContext.Set("gitOperation", status?.Operation);
        menuContext.Set("gitHeadDetached", status is { Branch: null });
        menuContext.Set("gitHeadUnborn", status is { Branch: not null, Head: null });
        return Workbench.Context.With(menuContext.Keys);
    }

    /// <summary>
    /// A branch row's menu (B7): the entries of <c>gitChanges/branch</c> that apply, titled for the branch, with only their
    /// rule's keys (F2, Delete; question 13). Every entry but Update closes the drop-down (question 21).
    /// </summary>
    internal Avalonia.Controls.ContextMenu? GitBranchMenu(GitBranchDetails branch)
    {
        var scope = GitBranchScope(branch);
        var common = GitBranches.IsCommon(branch);
        var git = GitBranches.RepositoryOf(branch);
        JsonElement[] args = [JsonSerializer.SerializeToElement(branch.FullRef, Docking.ShellJsonContext.Default.String)];
        return MenuBuilder.Context(this, MenuLocations.GitChangesBranch, new MenuBuilder.MenuShape(c => new RelayCommand(() =>
        {
            if (c != "git.updateBranch")
            {
                GitBranches.IsOpen = false;
            }

            _ = common ? RunOnCommonBranchAsync(c, branch) : InRepositoryAsync(git, () => RunAsync(c, args, scope));
        }))
        {
            Scope = scope,
            TitleFor = item => GitBranchTitle(item.Command, branch),
            KeyFor = item => item.RuleKeyLabel,
        });
    }

    /// <summary>A branch menu's visible entries with their titles, as built (tests and the smoke test).</summary>
    internal IReadOnlyList<(string Command, string Title, string? Keys)> GitBranchMenuItems(GitBranchDetails branch) =>
        [.. Workbench.Menus.GetItems(MenuLocations.GitChangesBranch, GitBranchScope(branch)).Select(i => (i.Command, GitBranchTitle(i.Command, branch) ?? i.Title, i.RuleKeyLabel))];

    /// <summary>
    /// The titles that name the branches (question 5): "Merge 'x' into 'y'", "Rebase 'y' onto 'x'", HEAD when detached,
    /// "Switch to Local 'x' (1 commit behind)" on a remote branch that has a local one (question 7), "Update from 'origin/x'"
    /// (question 21), "Delete from origin…", "Compare with HEAD". Null keeps the rule's title.
    /// </summary>
    private string? GitBranchTitle(string command, GitBranchDetails branch)
    {
        if (GitBranches.IsCommon(branch))
        {
            return CommonBranchTitle(command, branch);
        }

        var current = (GitBranches.IsAllRepositories ? GitBranches.RepositoryOf(branch) : Repo).Status?.Branch;
        var name = branch.Name;
        switch (command)
        {
            case "git.switchBranch" when branch.IsRemote && GitBranches.LocalTwin(branch) is { } twin:
                var behind = CountBehindSync(twin, branch);
                return behind > 0
                    ? Localizer.Format(behind == 1 ? "msg.gitBranches.switchToLocalBehindOne" : "msg.gitBranches.switchToLocalBehind", behind == 1 ? "Switch to Local '{0}' (1 commit behind)" : "Switch to Local '{0}' ({1} commits behind)", twin.Name, behind)
                    : Localizer.Format("msg.gitBranches.switchToLocal", "Switch to Local '{0}'", twin.Name);
            case "git.merge":
                return current is null
                    ? Localizer.Format("msg.gitBranches.mergeIntoHead", "Merge '{0}' into HEAD", name)
                    : Localizer.Format("msg.gitBranches.mergeInto", "Merge '{0}' into '{1}'", name, current);
            case "git.rebase":
                return current is null
                    ? Localizer.Format("msg.gitBranches.rebaseHeadOnto", "Rebase HEAD onto '{0}'", name)
                    : Localizer.Format("msg.gitBranches.rebaseOnto", "Rebase '{0}' onto '{1}'", current, name);
            case "git.updateBranch" when branch.Upstream is { } upstream:
                return Localizer.Format("msg.gitBranches.updateFrom", "Update from '{0}'", upstream);
            case "git.deleteBranch" when branch.IsRemote:
                return Localizer.Format("msg.gitBranches.deleteFromRemote", "Delete from {0}…", branch.Remote ?? "origin");
            case "git.compareBranch" when current is null:
                return Localizer.Get("msg.gitBranches.compareWithHead", "Compare with HEAD");
            default:
                return null;
        }
    }

    /// <summary>
    /// How far the local branch is behind the remote row (question 7), from the drop-down's last read: the local branch's
    /// incoming count when the row is its upstream; otherwise unknown here (0, and the title says no count).
    /// </summary>
    private static int CountBehindSync(GitBranchDetails local, GitBranchDetails remote) =>
        local.UpstreamRef == remote.FullRef ? local.Behind : 0;

    /// <summary>The ref a branch command was given (its full ref or name), or null.</summary>
    private static string? RefArgument(IReadOnlyList<JsonElement> args) =>
        args.Count > 0 && args[0].ValueKind == JsonValueKind.String && args[0].GetString() is { Length: > 0 } value ? value : null;

    /// <summary>The branch a ref names (a full ref, or a local branch's name), from git; null when there's none.</summary>
    private async Task<GitBranchDetails?> FindBranchAsync(string reference)
    {
        var branches = await Repo.BranchDetailsAsync().ConfigureAwait(true);
        return branches.FirstOrDefault(b => b.FullRef == reference)
            ?? branches.FirstOrDefault(b => !b.IsRemote && b.Name == reference)
            ?? branches.FirstOrDefault(b => b.IsRemote && b.Name == reference);
    }

    /// <summary>
    /// Checkout from the drop-down or a branch's menu (B1, B10, B11, B13): a local branch is switched to; a remote branch
    /// switches to the local branch of its name when there is one, else gets a local branch that tracks it; a branch
    /// checked out in another worktree says where, with Open in New Window, instead.
    /// </summary>
    public Task CheckoutBranchAsync(string reference) => GitActionAsync(async () =>
    {
        if (await FindBranchAsync(reference).ConfigureAwait(true) is not { } branch)
        {
            throw new GitException(Localizer.Format("msg.gitBranches.noBranchNamed", "There is no branch named '{0}'.", ShortRefName(reference)), -1);
        }

        var target = branch.IsRemote && GitBranches.LocalTwin(branch) is { } twin ? twin : branch;
        if (target is { IsRemote: false, IsCurrent: true })
        {
            GitBranches.IsOpen = false;
            return;
        }

        if (target.WorktreePath is { } path)
        {
            ShowWhere(Localizer.Format("msg.gitBranches.checkedOutInCantCheckOut", "{0} is checked out in {1}, so it can't be checked out here too.", target.Name, path), path);
            return;
        }

        GitBranches.IsOpen = false;
        await Repo.SwitchAsync(new GitBranch(target.Name, target.IsRemote, IsCurrent: false, target.Upstream, target.Commit)).ConfigureAwait(true);
    });

    /// <summary>Where a branch is checked out: the drop-down's banner with Open in New Window when it's open (B13), else a notification.</summary>
    private void ShowWhere(string message, string path)
    {
        var open = Localizer.Get("msg.git.openInNewWindow", "Open in New Window");
        if (GitBranches.IsOpen)
        {
            GitBranches.ShowBanner(message, warning: false, open, "window-new", () =>
            {
                StartNewWindow(path);
                return Task.CompletedTask;
            });
            return;
        }

        _ = Workbench.Notifications.Info(message, open).Result.ContinueWith(t => { if (t.Result == open) { Ui.Post(() => StartNewWindow(path)); } }, TaskScheduler.Default);
    }

    /// <summary>git.switchBranch: the ref given (a branch's menu, the drop-down), else the Switch Branch quick pick.</summary>
    public Task SwitchBranchCommandAsync(IReadOnlyList<JsonElement> args)
    {
        if (RefArgument(args) is { } reference)
        {
            return CheckoutBranchAsync(reference);
        }

        // With all repositories shown (ADR 0085), Git Changes' drop-down: Common branches and each repository's.
        if (GitChanges.IsAllRepositories && _repository.Value is null)
        {
            Layout.ShowToolWindow(GitChangesViewModel.ToolWindowId, focus: true);
            GitBranches.IsOpen = true;
            return Task.CompletedTask;
        }

        return SwitchBranchAsync();
    }

    /// <summary>
    /// New Local Branch From… (P4, P5, question 17): the name, what it's based on, and Check out the new branch, checked as
    /// in the classic IDE; during a merge or rebase the branch is still made but not checked out (git can't switch with
    /// unresolved conflicts). With no ref it's based on the current branch (the drop-down's New Branch…, Create Branch…);
    /// a second argument fills in the name (New Branch 'zzz'…, B3).
    /// </summary>
    public Task NewBranchFromAsync(string? reference, string? name = null) => GitActionAsync(async () =>
    {
        var status = Repo.Status;
        var basedOn = reference is null ? status?.Branch ?? "HEAD" : ShortRefName(reference);
        var startPoint = reference ?? "HEAD";
        var branches = await Repo.BranchDetailsAsync().ConfigureAwait(true);
        var locals = branches.Where(b => !b.IsRemote).Select(b => b.Name).ToList();
        var operation = status?.Operation;
        var create = Localizer.Get("msg.gitBranches.create", "Create");
        var result = await Dialogs.ShowAsync(new DialogRequest(
            Localizer.Get("msg.gitBranches.newLocalBranch", "New Local Branch"),
            Localizer.Get("msg.gitBranches.branchName", "Branch name:"),
            [create, Localizer.Get("msg.gitChanges.cancel", "Cancel")])
        {
            Input = name ?? string.Empty,
            Icon = "branch",
            Context = Localizer.Format("msg.gitBranches.basedOn", "Based on: {0}", basedOn),
            Validate = n => BranchNameError(n.Trim(), locals),
            Option = operation is null
                ? new DialogOption("checkout", Localizer.Get("msg.gitBranches.checkOutTheNewBranch", "Check out the new branch"), true)
                : new DialogOption("checkout", Localizer.Get("msg.gitBranches.checkOutTheNewBranch", "Check out the new branch"), false)
                {
                    IsEnabled = false,
                    Note = Localizer.Format("msg.gitBranches.notWhileOperation", "Not while a {0} is in progress: the branch is made at {1}, and {2} stays checked out.", operation, basedOn, status?.Branch ?? "HEAD"),
                },
        }).ConfigureAwait(true);
        if (result is { Button: var button, Input: { } input } && button == create)
        {
            await Repo.CreateBranchAtAsync(input.Trim(), startPoint, switchTo: operation is null && result.Checked.Contains("checkout")).ConfigureAwait(true);
            GitBranches.IsOpen = false;
        }
    });

    /// <summary>git's rules for the name, and the names other branches have (also as a folder of the name, which git refuses).</summary>
    private static string? BranchNameError(string name, IReadOnlyList<string> existing, string? except = null)
    {
        if (GitRefNames.BranchNameError(name) is { } error)
        {
            return error;
        }

        if (existing.Any(e => e != except && e == name))
        {
            return Localizer.Get("msg.gitBranches.branchExists", "A branch with that name exists.");
        }

        return existing.FirstOrDefault(e => e != except && (e.StartsWith(name + "/", StringComparison.Ordinal) || name.StartsWith(e + "/", StringComparison.Ordinal))) is { } clash
            ? Localizer.Format("msg.gitBranches.branchClashes", "The branch '{0}' is in the way: git can't have both.", clash)
            : null;
    }

    /// <summary>Merge 'x' into 'y' (a branch's menu): merges it at once, as the classic IDE does; conflicts stop it.</summary>
    public Task MergeBranchCommandAsync(IReadOnlyList<JsonElement> args) => RefArgument(args) is not { } reference ? MergeBranchAsync() : GitActionAsync(async () =>
    {
        var name = ShortRefName(reference);
        try
        {
            await Repo.MergeAsync(name).ConfigureAwait(true);
            StatusBar.ShowMessage(Localizer.Format("msg.git.merged", "Merged {0}.", name), TimeSpan.FromSeconds(5));
        }
        catch (GitException) when (Repo.Status is { IsMerging: true })
        {
            Workbench.Notifications.Warning(Localizer.Format("msg.git.mergingProducedConflictsResolveThemIn", "Merging {0} produced conflicts. Resolve them in the Git Changes window, stage the files and commit, or abort the merge.", name));
            Layout.ShowToolWindow(GitChangesViewModel.ToolWindowId);
        }
    });

    /// <summary>Rebase 'y' onto 'x' (a branch's menu); conflicts stop it.</summary>
    public Task RebaseBranchCommandAsync(IReadOnlyList<JsonElement> args) => RefArgument(args) is not { } reference ? RebaseAsync() : GitActionAsync(async () =>
    {
        await StopOnConflictAsync(() => Repo.RebaseAsync(ShortRefName(reference)), Localizer.Get("msg.gitBranches.theRebase", "The rebase")).ConfigureAwait(true);
        await RefreshHistoryAsync().ConfigureAwait(true);
    });

    /// <summary>Push (a branch's menu): that branch, checked out or not; with no ref the current one.</summary>
    public Task PushCommandAsync(IReadOnlyList<JsonElement> args) =>
        RefArgument(args) is { } reference ? RemoteActionAsync(token => Repo.PushBranchAsync(reference, token), Localizer.Get("msg.git.pushing", "Pushing…")) : PushAsync();

    /// <summary>
    /// Rename… (P6, F2): the current name to edit, git's rules and the other branches' names as the error under the box;
    /// Rename waits for a valid name. With no ref, the current branch.
    /// </summary>
    public Task RenameBranchCommandAsync(IReadOnlyList<JsonElement> args) => GitActionAsync(async () =>
    {
        var name = RefArgument(args) is { } reference ? ShortRefName(reference) : Repo.Status?.Branch;
        if (name is null)
        {
            return;
        }

        var locals = (await Repo.BranchDetailsAsync().ConfigureAwait(true)).Where(b => !b.IsRemote).Select(b => b.Name).ToList();
        var rename = Localizer.Get("msg.gitBranches.rename", "Rename");
        var result = await Dialogs.ShowAsync(new DialogRequest(
            Localizer.Get("msg.gitBranches.renameBranch", "Rename Branch"),
            Localizer.Format("msg.gitBranches.aNewNameFor", "A new name for {0}:", name),
            [rename, Localizer.Get("msg.gitChanges.cancel", "Cancel")])
        {
            Input = name,
            InputSelection = (name.LastIndexOf('/') + 1, name.Length - name.LastIndexOf('/') - 1),
            Icon = "rename",
            Validate = n => n.Trim() == name ? Localizer.Get("msg.gitBranches.enterANewName", "Enter a new name.") : BranchNameError(n.Trim(), locals, except: name),
        }).ConfigureAwait(true);
        if (result is { Input: { } input } && result.Button == rename)
        {
            GitBranches.IsOpen = false;
            await Repo.RenameBranchAsync(name, input.Trim()).ConfigureAwait(true);
        }
    });

    /// <summary>
    /// Delete (P7-P11, Delete): a local branch's one confirmation, with "Also delete origin/x on the server" unchecked
    /// whenever it has an upstream, listing the commits only the server has (question 8); when it isn't merged by git's
    /// rule (its upstream, else HEAD; HEAD when the upstream goes too) a second dialog names what it was compared with and
    /// lists the commits that would be lost. Then <c>git branch -D</c> and, if asked, <c>git push --delete</c>. A remote
    /// branch has its own confirmation (P11). Both: Delete a plain button, Cancel the default (question 1). With no ref, a
    /// quick pick of the local branches that can be deleted.
    /// </summary>
    public Task DeleteBranchCommandAsync(IReadOnlyList<JsonElement> args) => GitActionAsync(async () =>
    {
        GitBranchDetails? branch;
        if (RefArgument(args) is { } reference)
        {
            branch = await FindBranchAsync(reference).ConfigureAwait(true);
        }
        else
        {
            var candidates = (await Repo.BranchDetailsAsync().ConfigureAwait(true)).Where(b => b is { IsRemote: false, IsCurrent: false, WorktreePath: null }).OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (candidates.Count == 0)
            {
                StatusBar.ShowMessage(Localizer.Get("msg.gitBranches.noBranchToDelete", "No other local branch to delete."), TimeSpan.FromSeconds(5));
                return;
            }

            branch = (await Palette.ShowAsync([.. candidates.Select(b => new QuickPickItem(b.Name) { Tag = b, Detail = b.Upstream })], Localizer.Get("msg.gitBranches.deleteWhichBranch", "Delete which branch?")).ConfigureAwait(true))?.Tag as GitBranchDetails;
        }

        if (branch is null)
        {
            return;
        }

        var delete = Localizer.Get("msg.gitBranches.delete", "Delete");
        var cancel = Localizer.Get("msg.gitChanges.cancel", "Cancel");
        if (branch.IsRemote)
        {
            var remote = branch.Remote ?? "origin";
            var tracker = GitBranches.Branches.FirstOrDefault(b => !b.IsRemote && b.UpstreamRef == branch.FullRef);
            var confirm = await Dialogs.ShowAsync(new DialogRequest(
                Localizer.Format("msg.gitBranches.deleteFromRemoteTitle", "Delete {0} from {1}?", branch.Name, remote),
                Localizer.Get("msg.gitBranches.deletesOnTheServer", "This deletes it on the server for everyone."),
                [delete, cancel])
            {
                DefaultButton = cancel,
                Icon = "trash",
                IsDanger = true,
                Context = tracker is null
                    ? Localizer.Get("msg.gitBranches.remoteBranchNoLocal", "Remote branch · no local branch tracks it")
                    : Localizer.Format("msg.gitBranches.remoteBranchTrackedBy", "Remote branch · tracked by {0}", tracker.Name),
            }).ConfigureAwait(true);
            if (confirm.Button == delete)
            {
                GitBranches.IsOpen = false;
                await Repo.DeleteRemoteBranchAsync(remote, branch.ShortName).ConfigureAwait(true);
            }

            return;
        }

        var hasUpstream = branch is { Upstream: not null, UpstreamGone: false };
        DialogOption? option = null;
        if (hasUpstream)
        {
            var onlyThere = await Repo.UpstreamOnlyCommitsAsync(branch.Name).ConfigureAwait(true);
            option = new DialogOption("upstream", Localizer.Format("msg.gitBranches.alsoDeleteUpstream", "Also delete {0} on the server", branch.Upstream!), false)
            {
                Note = onlyThere.Count == 0
                    ? Localizer.Get("msg.gitBranches.deletesForEveryone", "That deletes it for everyone.")
                    : Localizer.Format(onlyThere.Count == 1 ? "msg.gitBranches.deletesForEveryoneWithOne" : "msg.gitBranches.deletesForEveryoneWithMore", onlyThere.Count == 1 ? "That deletes it for everyone. {0} has 1 commit that isn't in {1}, and it goes too:" : "That deletes it for everyone. {0} has {2} commits that aren't in {1}, and they go too:", branch.Upstream!, branch.Name, onlyThere.Count),
                Lines = onlyThere.Count == 0 ? null : [.. CommitLines(onlyThere)],
            };
        }

        var first = await Dialogs.ShowAsync(new DialogRequest(
            Localizer.Format("msg.gitBranches.deleteBranchTitle", "Delete {0}?", branch.Name),
            Localizer.Get("msg.gitBranches.deletedFromThisRepository", "The branch is deleted from this repository."),
            [delete, cancel])
        {
            DefaultButton = cancel,
            Icon = "trash",
            IsDanger = true,
            Context = hasUpstream ? Localizer.Format("msg.gitBranches.localBranchTracks", "Local branch · tracks {0}", branch.Upstream!) : Localizer.Get("msg.gitBranches.localBranch", "Local branch"),
            Option = option,
        }).ConfigureAwait(true);
        if (first.Button != delete)
        {
            return;
        }

        var withUpstream = first.Checked.Contains("upstream");
        var check = await Repo.IsFullyMergedAsync(branch.Name, withUpstream).ConfigureAwait(true);
        if (!check.Merged)
        {
            var lost = check.Commits.Count + check.UpstreamOnly.Count;
            var second = await Dialogs.ShowAsync(new DialogRequest(
                check.ComparedWithUpstream || check.ComparedWith == "HEAD"
                    ? Localizer.Format("msg.gitBranches.isntMerged", "{0} isn't merged", branch.Name)
                    : Localizer.Format("msg.gitBranches.isntMergedInto", "{0} isn't merged into {1}", branch.Name, check.ComparedWith),
                (check.UpstreamOnly.Count > 0, check.Commits.Count == 1) switch
                {
                    (true, true) => Localizer.Format("msg.gitBranches.unmergedBothOne", "{0} has 1 commit that isn't in {1}, and {2} {3} more. Deleting both loses these {4} unless another branch or a tag has them:", branch.Name, check.ComparedWith, branch.Upstream!, check.UpstreamOnly.Count, lost),
                    (true, false) => Localizer.Format("msg.gitBranches.unmergedBoth", "{0} has {1} commits that aren't in {2}, and {3} {4} more. Deleting both loses these {5} unless another branch or a tag has them:", branch.Name, check.Commits.Count, check.ComparedWith, branch.Upstream!, check.UpstreamOnly.Count, lost),
                    (false, true) => Localizer.Format("msg.gitBranches.unmergedOne", "{0} has 1 commit that isn't in {1}. Deleting it loses it unless another branch or a tag has it:", branch.Name, check.ComparedWith),
                    _ => Localizer.Format("msg.gitBranches.unmerged", "{0} has {1} commits that aren't in {2}. Deleting it loses them unless another branch or a tag has them:", branch.Name, check.Commits.Count, check.ComparedWith),
                },
                [delete, cancel])
            {
                DefaultButton = cancel,
                Icon = "trash",
                IsDanger = true,
                Context = check.ComparedWithUpstream
                    ? Localizer.Format("msg.gitBranches.comparedWithUpstream", "Compared with {0}, the branch it tracks", check.ComparedWith)
                    : withUpstream && hasUpstream
                        ? Localizer.Format("msg.gitBranches.comparedWithHeadUpstreamGoes", "Compared with {0} (HEAD), since {1} goes too", check.ComparedWith, branch.Upstream!)
                        : Localizer.Format("msg.gitBranches.comparedWithHead", "Compared with {0} (HEAD)", check.ComparedWith),
                Details = [new DetailLine(Localizer.Get("msg.gitBranches.commits", "Commits"), "header"), .. CommitLines(check.Commits), .. CommitLines(check.UpstreamOnly, onlyOn: branch.Upstream?.Split('/')[0])],
            }).ConfigureAwait(true);
            if (second.Button != delete)
            {
                return;
            }
        }

        GitBranches.IsOpen = false;
        await Repo.DeleteBranchAsync(branch.Name, force: !check.Merged || withUpstream).ConfigureAwait(true);
        if (withUpstream && branch.UpstreamRef is { } upstreamRef && upstreamRef.StartsWith("refs/remotes/", StringComparison.Ordinal))
        {
            var (remote, remoteBranch) = SplitUpstream(branch.Upstream!, (await Repo.RemotesAsync().ConfigureAwait(true)).Select(r => r.Name).ToList());
            await Repo.DeleteRemoteBranchAsync(remote, remoteBranch).ConfigureAwait(true);
        }

        StatusBar.ShowMessage(Localizer.Format("msg.gitBranches.deletedBranch", "Deleted {0}.", branch.Name), TimeSpan.FromSeconds(5));
    });

    /// <summary>The commits as dialog lines: the short id, the subject, and where only the server has one ("only on origin").</summary>
    private static IEnumerable<DetailLine> CommitLines(IReadOnlyList<GitCommitSummary> commits, string? onlyOn = null)
    {
        foreach (var commit in commits.Take(20))
        {
            var tag = onlyOn is not null ? "  (" + Localizer.Format("msg.gitBranches.onlyOn", "only on {0}", onlyOn) + ")" : string.Empty;
            yield return new DetailLine($"{commit.Hash}  {commit.Subject}{tag}");
        }

        if (commits.Count > 20)
        {
            yield return new DetailLine(Localizer.Format("msg.gitChanges.andMore", "and {0} more", commits.Count - 20));
        }
    }

    /// <summary>"origin/feature/x" into the remote (the longest known remote name it starts with) and the branch there.</summary>
    private static (string Remote, string Branch) SplitUpstream(string upstream, IReadOnlyList<string> remotes)
    {
        var remote = remotes.OrderByDescending(r => r.Length).FirstOrDefault(r => upstream.StartsWith(r + "/", StringComparison.Ordinal)) ?? upstream[..upstream.IndexOf('/', StringComparison.Ordinal)];
        return (remote, upstream[(remote.Length + 1)..]);
    }

    /// <summary>
    /// Compare with Current Branch (C1-C3, question 9): the files the other branch's tip differs in from the current
    /// branch's, in a document named "main → feature" in diff order. With no ref, a quick pick of the branches.
    /// </summary>
    public Task CompareBranchCommandAsync(IReadOnlyList<JsonElement> args) => GitActionAsync(async () =>
    {
        var reference = RefArgument(args);
        if (reference is null)
        {
            var branches = (await Repo.BranchDetailsAsync().ConfigureAwait(true)).Where(b => !(b.IsCurrent && !b.IsRemote)).OrderBy(b => b.IsRemote).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (await Palette.ShowAsync([.. branches.Select(b => new QuickPickItem(b.Name) { Tag = b.FullRef, Detail = b.IsRemote ? "remote" : null })], Localizer.Format("msg.gitBranches.compareWhichBranch", "Compare {0} with", Repo.Status?.Branch ?? "HEAD")).ConfigureAwait(true) is not { Tag: string picked })
            {
                return;
            }

            reference = picked;
        }

        await OpenBranchCompareAsync(reference).ConfigureAwait(true);
    });

    /// <summary>Opens (or brings back) the Compare document from the current branch to <paramref name="reference"/>.</summary>
    public async Task<GitCompareDocument?> OpenBranchCompareAsync(string reference)
    {
        var git = Repo;
        if (git.Root is null)
        {
            return null;
        }

        var from = git.Status?.Branch is { } current ? "refs/heads/" + current : "HEAD";
        var fromName = git.Status?.Branch ?? "HEAD";
        var id = GitCompareDocument.IdFor(from, reference) + (git == Git ? string.Empty : "@" + git.Root);
        if (Layout.FindDocument<GitCompareDocument>(id) is not { } document)
        {
            var viewModel = new GitCompareViewModel(git, from, fromName, reference, ShortRefName(reference))
            {
                OpenFile = (file) => InRepositoryAsync(git, () => OpenCompareFileAsync(from, fromName, reference, ShortRefName(reference), file)),
                ShowHistory = () => InRepositoryAsync(git, () => HistoryCommandAsync([JsonSerializer.SerializeToElement(reference, Docking.ShellJsonContext.Default.String)])),
            };
            document = new GitCompareDocument(viewModel, id);
        }

        Layout.OpenDocument(document, focus: true);
        await GitActionAsync(document.ViewModel.LoadAsync).ConfigureAwait(true);
        return document;
    }

    /// <summary>A file of the Compare document: the current branch's version on the left, the other's on the right (question 9).</summary>
    private Task OpenCompareFileAsync(string from, string fromName, string to, string toName, GitChangedFile file)
    {
        var git = Repo;
        return GitActionAsync(() =>
            OpenRevisionDiffAsync(
                file.Path,
                $"{from}→{to}:{file.Path}",
                $"{Path.GetFileName(file.OldPath ?? file.Path)} ({fromName})",
                async () => await git.ShowAsync(file.OldPath ?? file.Path, from).ConfigureAwait(true) ?? string.Empty,
                $"{Path.GetFileName(file.Path)} ({toName})",
                async () => await git.ShowAsync(file.Path, to).ConfigureAwait(true) ?? string.Empty));
    }

    /// <summary>Copy Branch Name: the branch's name (a remote one with its remote); with no ref the current branch's.</summary>
    public async Task CopyBranchNameCommandAsync(IReadOnlyList<JsonElement> args)
    {
        var name = RefArgument(args) is { } reference ? ShortRefName(reference) : Repo.Status?.Branch;
        if (name is not null)
        {
            await CopyTextAsync(name).ConfigureAwait(true);
        }
    }

    /// <summary>Opens the branch drop-down (the window's branch button; the smoke test).</summary>
    public void ShowBranchPicker() => GitBranches.IsOpen = true;
}
