using System.Globalization;
using Avalonia.Threading;
using Codalus.Shell.Docking;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Context;
using Codalus.Workbench.Git;
using Codalus.Workbench.Localization;
using Codalus.Workbench.StatusBar;

namespace Codalus.Shell;

/// <summary>
/// Solutions that span several repositories (M13b; All Repositories, ADR 0085). The repositories are those of the
/// solution's folder and its projects' folders, plus the ones the user added, less the ones the user left out (kept with
/// the solution, ADR 0070). Git Changes shows all of them by default, one tree per repository with Commit All Repos,
/// and fetch, pull, push, sync and stash run in each, then report the ones that failed; or the user picks one, as
/// before. A command from a repository's node, branch row, operation banner, stash or history runs in that repository:
/// <see cref="Repo"/>.
/// </summary>
public sealed partial class ShellHost
{
    private readonly AsyncLocal<GitService?> _repository = new();
    private readonly List<string> _addedRepositories = [];
    private readonly List<string> _removedRepositories = [];

    /// <summary>The repository Git Changes shows when it shows one of several; null: all of them (the default).</summary>
    private string? _shownRepository;

    private static StringComparer PathComparer => Codalus.Workbench.Projects.SolutionService.PathComparer;

    /// <summary>
    /// The repository a Git command acts on: the one whose node, branch, operation, stash or history it runs from
    /// (<see cref="InRepositoryAsync"/>), else the open one.
    /// </summary>
    public GitService Repo => _repository.Value is { Root: not null } repository ? repository : Git;

    /// <summary>Runs <paramref name="run"/> with <see cref="Repo"/> set to <paramref name="repository"/> (null: the open one).</summary>
    public async Task InRepositoryAsync(GitService? repository, Func<Task> run)
    {
        _repository.Value = repository;
        await run().ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ the solution's repositories

    private void ScheduleRepositoryDiscovery()
    {
        _repositoryTimer ??= CreateRepositoryTimer();
        StartTimer(_repositoryTimer);
    }

    private DispatcherTimer CreateRepositoryTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _ = DiscoverRepositoriesAsync();
        };
        return timer;
    }

    /// <summary>
    /// Finds the repositories of the solution's folder and its projects' folders (a solution can span several), with the
    /// ones the user added and without the ones the user left out, then shows all of them or the one chosen.
    /// </summary>
    public async Task DiscoverRepositoriesAsync()
    {
        if (Solutions.Solution is not { } solution)
        {
            Git.Repositories = [];
            await Git.SetSiblingsAsync([]).ConfigureAwait(true);
            GitChanges.SetRepositories([], null);
            Workbench.Context.Set("gitRepositories", false);
            Workbench.Context.Set("gitAllRepositories", false);
            return;
        }

        try
        {
            var folders = solution.Projects.Select(p => Path.GetDirectoryName(p.Path)).OfType<string>().Prepend(solution.Directory).Concat(_addedRepositories).ToList();
            var found = await Git.FindRepositoriesAsync(folders).ConfigureAwait(true);
            var roots = found.Where((root, index) => index == 0 || !_removedRepositories.Contains(root, PathComparer)).ToList();
            Git.Repositories = roots;
            await ShowRepositoriesAsync(roots).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is GitException or IOException)
        {
            GitLog.AppendLine("[repositories] " + ex.Message);
        }
    }

    /// <summary>Opens what Git Changes shows of <paramref name="roots"/>: every one (the solution's own as the open one), or the one chosen.</summary>
    private async Task ShowRepositoriesAsync(IReadOnlyList<string> roots)
    {
        if (_shownRepository is { } shown && !roots.Contains(shown, PathComparer))
        {
            _shownRepository = null;
        }

        var all = roots.Count > 1 && _shownRepository is null;
        if (all)
        {
            if (Git.Root is not { } open || !PathComparer.Equals(open, roots[0]))
            {
                await OpenRepositoryAsync(roots[0]).ConfigureAwait(true);
            }

            await Git.SetSiblingsAsync(roots).ConfigureAwait(true);
        }
        else
        {
            await Git.SetSiblingsAsync([]).ConfigureAwait(true);
            if (_shownRepository is { } one && (Git.Root is not { } open || !PathComparer.Equals(open, one)))
            {
                await OpenRepositoryAsync(one).ConfigureAwait(true);
            }
        }

        GitChanges.SetRepositories(roots, Git.Root, all, [.. roots.Skip(1)]);
        Workbench.Context.Set("gitRepositories", roots.Count > 1);
        Workbench.Context.Set("gitAllRepositories", all);
        UpdateGitStatus();
        if (GitBranches.IsOpen)
        {
            _ = GitBranches.LoadAsync();
        }
    }

    /// <summary>Shows one of the solution's repositories in Git Changes, the history and the status bar.</summary>
    public async Task SwitchRepositoryAsync(string root)
    {
        _shownRepository = Path.GetFullPath(root);
        await ShowRepositoriesAsync(Git.Repositories).ConfigureAwait(true);
        SaveEditorSession();
        StatusBar.ShowMessage(Localizer.Format("msg.allRepositories.repository", "Repository: {0}", Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar))), TimeSpan.FromSeconds(4));
    }

    /// <summary>Shows every repository of the solution in Git Changes (All Repositories, ADR 0085).</summary>
    public async Task ShowAllRepositoriesAsync()
    {
        _shownRepository = null;
        await ShowRepositoriesAsync(Git.Repositories).ConfigureAwait(true);
        SaveEditorSession();
    }

    /// <summary>Git → Switch Repository: All Repositories, or one of them.</summary>
    public async Task PickRepositoryAsync()
    {
        if (Git.Repositories.Count < 2)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.hosting.theSolutionIsInASingle", "The solution is in a single repository."), TimeSpan.FromSeconds(4));
            return;
        }

        var items = new List<QuickPickItem> { new(GitRepositoryChoice.All(Git.Repositories.Count).Name) { Tag = string.Empty } };
        items.AddRange(Git.Repositories.Select(r => new QuickPickItem(Path.GetFileName(r.TrimEnd(Path.DirectorySeparatorChar))) { Detail = r, Tag = r }));
        var current = GitChanges.IsAllRepositories ? 0 : Math.Max(0, items.FindIndex(i => i.Tag is string root && Git.Root is { } open && PathComparer.Equals(root, open)));
        switch (await Palette.ShowAsync(items, Localizer.Get("msg.allRepositories.showWhichRepositories", "Show which of the solution's repositories?"), current).ConfigureAwait(true))
        {
            case { Tag: "" }:
                await ShowAllRepositoriesAsync().ConfigureAwait(true);
                break;
            case { Tag: string root }:
                await SwitchRepositoryAsync(root).ConfigureAwait(true);
                break;
        }
    }

    /// <summary>
    /// Add Repository… (the picker's footer, the Git menu): a repository with no project in the solution (docs, build
    /// scripts, a shared library's) joins its repositories, kept with the solution.
    /// </summary>
    public async Task AddRepositoryAsync(string? folder = null)
    {
        if (Solutions.Solution is null)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.allRepositories.openASolutionFirst", "Open a solution first."), TimeSpan.FromSeconds(4));
            return;
        }

        folder ??= Window is { } window ? await window.PickFolderAsync(Localizer.Get("msg.allRepositories.addARepository", "Add a Repository")).ConfigureAwait(true) : null;
        if (folder is null)
        {
            return;
        }

        var found = await Git.FindRepositoriesAsync([folder]).ConfigureAwait(true);
        if (found is not [var root])
        {
            Workbench.Notifications.Warning(Localizer.Format("msg.allRepositories.notInARepository", "{0} is not in a git repository.", folder));
            return;
        }

        if (Git.Repositories.Contains(root, PathComparer))
        {
            StatusBar.ShowMessage(Localizer.Format("msg.allRepositories.alreadyOneOfTheRepositories", "{0} is already one of the solution's repositories.", Path.GetFileName(root)), TimeSpan.FromSeconds(4));
            return;
        }

        _removedRepositories.RemoveAll(r => PathComparer.Equals(r, root));
        _addedRepositories.Add(root);

        await DiscoverRepositoriesAsync().ConfigureAwait(true);
        SaveEditorSession();
    }

    /// <summary>Leaves a repository out of the solution's repositories (not the solution's own); Add Repository… brings it back.</summary>
    public async Task RemoveRepositoryAsync(string root)
    {
        if (Git.Repositories is not [var own, ..] || PathComparer.Equals(own, root))
        {
            return;
        }

        _addedRepositories.RemoveAll(r => PathComparer.Equals(r, root));
        _removedRepositories.Add(root);

        if (_shownRepository is { } shown && PathComparer.Equals(shown, root))
        {
            _shownRepository = null;
        }

        await DiscoverRepositoriesAsync().ConfigureAwait(true);
        SaveEditorSession();
    }

    /// <summary>The repositories' part of the session (ADR 0070): added, left out, and the one shown (none: all).</summary>
    private SessionGitRepositories? CaptureRepositories() =>
        _addedRepositories.Count == 0 && _removedRepositories.Count == 0 && _shownRepository is null
            ? null
            : new SessionGitRepositories
            {
                Added = _addedRepositories.Count == 0 ? null : [.. _addedRepositories],
                Removed = _removedRepositories.Count == 0 ? null : [.. _removedRepositories],
                Shown = _shownRepository,
            };

    private void LoadRepositories(SessionGitRepositories? session)
    {
        _addedRepositories.Clear();
        _removedRepositories.Clear();
        _addedRepositories.AddRange(session?.Added ?? []);
        _removedRepositories.AddRange(session?.Removed ?? []);
        _shownRepository = session?.Shown;
    }

    // ------------------------------------------------------------------ in every repository

    /// <summary>The repositories Git Changes shows: all of the solution's, or the open one.</summary>
    private IReadOnlyList<GitService> ShownRepositories => GitChanges.Services;

    /// <summary>
    /// Runs <paramref name="action"/> in each shown repository <paramref name="applies"/> to, past the ones that fail;
    /// then says how many it ran in (<paramref name="done"/>, with the count), or lists the failures in a notification.
    /// Git Changes shows it running, and its Cancel stops it (<see cref="RemoteActionAsync"/>).
    /// </summary>
    private Task ForEachRepositoryAsync(Func<GitService, bool> applies, Func<GitService, CancellationToken, Task> action, string progress, Func<int, string> done, string none) => RemoteActionAsync(async token =>
    {
        var targets = ShownRepositories.Where(applies).ToList();
        if (targets.Count == 0)
        {
            StatusBar.ShowMessage(none, TimeSpan.FromSeconds(5));
            return;
        }

        var failures = await GitService.ForEachAsync(targets, git => action(git, token), token).ConfigureAwait(true);
        ReportRepositoryFailures(failures, targets.Count, done(targets.Count - failures.Count));
    }, progress);

    /// <summary>What failed in which repository (a notification, each line in the Git output), or <paramref name="done"/> when nothing did.</summary>
    private void ReportRepositoryFailures(IReadOnlyList<GitRepositoryFailure> failures, int total, string? done = null)
    {
        if (failures.Count == 0)
        {
            if (done is not null)
            {
                StatusBar.ShowMessage(done, TimeSpan.FromSeconds(5));
            }

            return;
        }

        foreach (var failure in failures)
        {
            GitLog.AppendLine("error: " + failure);
        }

        Workbench.Notifications.Error("Git: " + GitChangesViewModel.FailureSummary(failures, total));
    }

    private static string Repositories(int count) => count == 1
        ? Localizer.Get("msg.allRepositories.oneRepository", "1 repository")
        : Localizer.Format("msg.allRepositories.repositoryCount", "{0} repositories", count);

    private static bool CanPull(GitService git) => git.Status is { Branch: not null, Upstream: not null };

    /// <summary>A branch with commits to push, or one not published yet (the first push publishes it, as with one repository).</summary>
    private static bool CanPush(GitService git) => git.Status is { Branch: not null, Head: not null } status && (status.Upstream is null || status.Ahead > 0);

    private Task FetchAllAsync() => ForEachRepositoryAsync(
        _ => true, (git, token) => git.FetchAsync(token), Localizer.Get("msg.allRepositories.fetching", "Fetching all repositories…"),
        count => Localizer.Format("msg.allRepositories.fetched", "Fetched {0}.", Repositories(count)), string.Empty);

    private Task PullAllAsync() => ForEachRepositoryAsync(
        CanPull, (git, token) => git.PullAsync(token), Localizer.Get("msg.allRepositories.pulling", "Pulling all repositories…"),
        count => Localizer.Format("msg.allRepositories.pulled", "Pulled {0}.", Repositories(count)),
        Localizer.Get("msg.allRepositories.nothingToPull", "No repository's branch has an upstream to pull from."));

    private Task PushAllAsync() => ForEachRepositoryAsync(
        CanPush, (git, token) => git.PushAsync(token), Localizer.Get("msg.allRepositories.pushing", "Pushing all repositories…"),
        count => Localizer.Format("msg.allRepositories.pushed", "Pushed {0}.", Repositories(count)),
        Localizer.Get("msg.allRepositories.nothingToPush", "Nothing to push in any repository."));

    private Task SyncAllAsync() => ForEachRepositoryAsync(
        git => CanPull(git) || CanPush(git),
        async (git, token) =>
        {
            if (CanPull(git))
            {
                await git.PullAsync(token).ConfigureAwait(true);
            }

            if (CanPush(git))
            {
                await git.PushAsync(token).ConfigureAwait(true);
            }
        },
        Localizer.Get("msg.allRepositories.syncing", "Syncing all repositories…"),
        count => Localizer.Format("msg.allRepositories.synced", "Synced {0}.", Repositories(count)),
        Localizer.Get("msg.allRepositories.nothingToSync", "Nothing to pull or push in any repository."));

    /// <summary>Stash All with every repository shown: one message, a stash in each repository that has changes.</summary>
    private Task StashAllRepositoriesAsync() => GitActionAsync(async () =>
    {
        var targets = ShownRepositories.Where(s => s.Status?.Files.Any(f => !f.IsConflict) == true).ToList();
        if (targets.Count == 0)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.allRepositories.nothingToStash", "No repository has changes to stash."), TimeSpan.FromSeconds(4));
            return;
        }

        var stash = Localizer.Get("msg.gitChanges.stash", "Stash");
        var answer = await Dialogs.ShowAsync(new DialogRequest(
            Localizer.Format("msg.allRepositories.stashChangesIn", "Stash Changes in {0}", Repositories(targets.Count)),
            Localizer.Get("msg.gitChanges.aMessageForTheStash", "A message for the stash (optional):"),
            [stash, Localizer.Get("msg.gitChanges.cancel", "Cancel")])
        {
            Input = string.Empty,
            Icon = "stash",
            Option = new DialogOption("untracked", Localizer.Get("msg.gitChanges.includeUntrackedFiles", "Include untracked files"), false),
            Details = [new DetailLine(Localizer.Get("msg.allRepositories.repositories", "Repositories"), "header"), .. targets.Select(t => new DetailLine(t.Name))],
        }).ConfigureAwait(true);
        if (answer.Button != stash)
        {
            return;
        }

        var untracked = answer.Checked.Contains("untracked");
        var failures = await GitService.ForEachAsync(targets, git => git.StashAsync(answer.Input?.Trim(), untracked)).ConfigureAwait(true);
        ReportRepositoryFailures(failures, targets.Count, Localizer.Get("msg.gitDepth.changesStashed", "Changes stashed."));
    });

    /// <summary>Drop All with every repository shown: every repository's stashes, after asking once.</summary>
    private Task DropAllRepositoriesStashesAsync() => GitActionAsync(async () =>
    {
        var counts = new List<(GitService Git, int Count)>();
        foreach (var git in ShownRepositories)
        {
            counts.Add((git, (await git.StashesAsync().ConfigureAwait(true)).Count));
        }

        var targets = counts.Where(c => c.Count > 0).ToList();
        if (targets.Count == 0)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.gitDepth.noStashes", "No stashes."), TimeSpan.FromSeconds(4));
            return;
        }

        var dropAll = Localizer.Get("msg.allRepositories.dropAll", "Drop All");
        var choice = await Dialogs.ShowAsync(new DialogRequest(
            Localizer.Get("msg.gitDepth.dropAllStashes", "Drop all stashes?"),
            Localizer.Format("msg.allRepositories.allStashesInWillBeDeleted", "{0} stash(es) in {1} will be deleted. This can't be undone.", targets.Sum(t => t.Count).ToString(CultureInfo.CurrentCulture), Repositories(targets.Count)),
            [dropAll, Localizer.Get("msg.gitChanges.cancel", "Cancel")])
        {
            DefaultButton = Localizer.Get("msg.gitChanges.cancel", "Cancel"),
            IsDanger = true,
            Details = [.. targets.Select(t => new DetailLine($"{t.Git.Name}: {t.Count.ToString(CultureInfo.CurrentCulture)}"))],
        }).ConfigureAwait(true);
        if (choice.Button == dropAll)
        {
            var failures = await GitService.ForEachAsync(targets.Select(t => t.Git), git => git.ClearStashesAsync()).ConfigureAwait(true);
            ReportRepositoryFailures(failures, targets.Count);
        }
    });

    // ------------------------------------------------------------------ common branches

    /// <summary>
    /// A Common branches row's scope: a local branch, current when every repository is on it, with an upstream, a remote or
    /// a worktree when any repository's has one; <c>gitBranchIsCommon</c> leaves out the entries that are one repository's.
    /// </summary>
    private IContextKeyReader CommonBranchScope(GitBranchDetails branch)
    {
        var each = GitBranches.CommonBranchIn(branch.Name);
        var menuContext = new MenuContext();
        menuContext.Set("gitBranchKind", "local");
        menuContext.Set("gitBranchIsCurrent", each.Count > 0 && each.All(e => e.Branch.IsCurrent && e.Git.Status?.Branch is not null));
        menuContext.Set("gitBranchHasUpstream", each.Any(e => e.Branch is { Upstream: not null, UpstreamGone: false }));
        menuContext.Set("gitBranchInOtherWorktree", each.Any(e => e.Branch.WorktreePath is not null));
        menuContext.Set("gitBranchHasLocal", false);
        menuContext.Set("gitBranchUpdateBlocked", false);
        menuContext.Set("gitHasRemote", GitBranches.HasRemoteFor(branch));
        menuContext.Set("gitHeadDetached", false);
        menuContext.Set("gitHeadUnborn", each.Any(e => e.Git.Status is { Branch: not null, Head: null }));
        menuContext.Set("gitOperation", each.Select(e => e.Git.Status?.Operation).FirstOrDefault(o => o is not null));
        menuContext.Set("gitBranchIsCommon", true);
        menuContext.Set("gitBranchRepository", null);
        return Workbench.Context.With(menuContext.Keys);
    }

    /// <summary>A Common branches entry's title: "Merge 'x' into 'develop'", or "into the current branches" when they differ.</summary>
    private string? CommonBranchTitle(string command, GitBranchDetails branch)
    {
        var currents = GitChanges.Services.Select(s => s.Status?.Branch).Distinct(StringComparer.Ordinal).ToList();
        var current = currents is [{ } one] ? one : null;
        return command switch
        {
            "git.merge" => current is null
                ? Localizer.Format("msg.allRepositories.mergeIntoCurrentBranches", "Merge '{0}' into the Current Branches", branch.Name)
                : Localizer.Format("msg.gitBranches.mergeInto", "Merge '{0}' into '{1}'", branch.Name, current),
            "git.rebase" => current is null
                ? Localizer.Format("msg.allRepositories.rebaseCurrentBranchesOnto", "Rebase the Current Branches onto '{0}'", branch.Name)
                : Localizer.Format("msg.gitBranches.rebaseOnto", "Rebase '{0}' onto '{1}'", current, branch.Name),
            "git.deleteBranch" => Localizer.Get("msg.allRepositories.deleteInEachRepository", "Delete in Each Repository…"),
            _ => null,
        };
    }

    /// <summary>
    /// A command on a Common branches row: Checkout, New Local Branch From, Merge, Rebase and Delete run in each repository
    /// that has the branch and report the ones that failed; any other entry (a plugin's) runs once in each, with that
    /// repository's ref.
    /// </summary>
    private async Task RunOnCommonBranchAsync(string command, GitBranchDetails branch)
    {
        GitBranches.IsOpen = false;
        var name = branch.Name;
        switch (command)
        {
            case "git.switchBranch":
                await CheckoutCommonBranchAsync(name).ConfigureAwait(true);
                return;
            case "git.createBranch":
                await NewBranchInAllRepositoriesAsync(null, name).ConfigureAwait(true);
                return;
            case "git.merge":
                await IntegrateCommonBranchAsync(name, rebase: false).ConfigureAwait(true);
                return;
            case "git.rebase":
                await IntegrateCommonBranchAsync(name, rebase: true).ConfigureAwait(true);
                return;
            case "git.deleteBranch":
                await DeleteCommonBranchAsync(name).ConfigureAwait(true);
                return;
        }

        foreach (var (git, each) in GitBranches.CommonBranchIn(name))
        {
            await InRepositoryAsync(git, () => RunAsync(command, [System.Text.Json.JsonSerializer.SerializeToElement(each.FullRef, ShellJsonContext.Default.String)], CommonBranchScope(branch))).ConfigureAwait(true);
        }
    }

    /// <summary>Checkout on a Common branches row (a click, Enter): each repository not on it switches to it.</summary>
    public Task CheckoutCommonBranchAsync(string name) => GitActionAsync(async () =>
    {
        GitBranches.IsOpen = false;
        var targets = GitBranches.CommonBranchIn(name).Where(e => !(e.Branch.IsCurrent && e.Git.Status?.Branch is not null)).Select(e => e.Git).ToList();
        if (targets.Count == 0)
        {
            StatusBar.ShowMessage(Localizer.Format("msg.allRepositories.everyRepositoryIsOn", "Every repository is on {0} already.", name), TimeSpan.FromSeconds(4));
            return;
        }

        var failures = await GitService.ForEachAsync(targets, git => git.SwitchAsync(new GitBranch(name, IsRemote: false, IsCurrent: false, null, string.Empty))).ConfigureAwait(true);
        ReportRepositoryFailures(failures, targets.Count, Localizer.Format("msg.allRepositories.checkedOutIn", "Checked out {0} in {1}.", name, Repositories(targets.Count - failures.Count)));
    });

    /// <summary>
    /// New Branch with all repositories shown (the drop-down's footer, a Common branches row's New Local Branch From): one
    /// name, a branch in each repository from its HEAD or from <paramref name="from"/>, checked out unless an operation is
    /// in progress there.
    /// </summary>
    public Task NewBranchInAllRepositoriesAsync(string? name, string? from = null) => GitActionAsync(async () =>
    {
        GitBranches.IsOpen = false;
        var targets = from is null ? GitChanges.Services.Where(s => s.Status?.Head is not null).ToList() : [.. GitBranches.CommonBranchIn(from).Select(e => e.Git)];
        if (targets.Count == 0)
        {
            return;
        }

        var message = from is null
            ? Localizer.Format("msg.allRepositories.newBranchInEach", "A new branch in {0}, from each one's current branch (and switch to it):", Repositories(targets.Count))
            : Localizer.Format("msg.allRepositories.newBranchFromInEach", "A new branch from {0} in {1} (and switch to it):", from, Repositories(targets.Count));
        if (await AskNameAsync(Localizer.Get("msg.allRepositories.newBranch", "New Branch"), message, Localizer.Get("msg.allRepositories.create", "Create"), name ?? string.Empty).ConfigureAwait(true) is not { } branch)
        {
            return;
        }

        var failures = await GitService.ForEachAsync(targets, git => from is null
            ? git.CreateBranchAsync(branch, switchTo: git.Status?.Operation is null)
            : git.CreateBranchAtAsync(branch, "refs/heads/" + from, switchTo: git.Status?.Operation is null)).ConfigureAwait(true);
        ReportRepositoryFailures(failures, targets.Count, Localizer.Format("msg.allRepositories.createdIn", "Created {0} in {1}.", branch, Repositories(targets.Count - failures.Count)));
    });

    /// <summary>
    /// Merge or Rebase on a Common branches row: in each repository whose current branch isn't it. One that stops on
    /// conflicts shows its operation in Git Changes, whose banner continues or aborts it there.
    /// </summary>
    private Task IntegrateCommonBranchAsync(string name, bool rebase) => GitActionAsync(async () =>
    {
        var targets = GitBranches.CommonBranchIn(name).Where(e => e.Git.Status is { Head: not null } status && status.Branch != name).Select(e => e.Git).ToList();
        if (targets.Count == 0)
        {
            StatusBar.ShowMessage(Localizer.Format("msg.allRepositories.everyRepositoryIsOn", "Every repository is on {0} already.", name), TimeSpan.FromSeconds(4));
            return;
        }

        var failures = await GitService.ForEachAsync(targets, git => rebase ? git.RebaseAsync(name) : git.MergeAsync(name)).ConfigureAwait(true);
        ReportRepositoryFailures(failures, targets.Count, rebase
            ? Localizer.Format("msg.allRepositories.rebasedOnto", "Rebased {0} onto {1}.", Repositories(targets.Count), name)
            : Localizer.Format("msg.allRepositories.mergedInto", "Merged {0} in {1}.", name, Repositories(targets.Count)));
        if (targets.Any(t => t.Status?.Operation is not null))
        {
            Layout.ShowToolWindow(GitChangesViewModel.ToolWindowId);
        }

        await RefreshHistoryAsync().ConfigureAwait(true);
    });

    /// <summary>
    /// Delete on a Common branches row: asks once, then deletes the local branch in each repository where it isn't checked
    /// out; git refuses one that isn't fully merged there, and the summary says so.
    /// </summary>
    private Task DeleteCommonBranchAsync(string name) => GitActionAsync(async () =>
    {
        var targets = GitBranches.CommonBranchIn(name).Where(e => !e.Branch.IsCurrent && e.Branch.WorktreePath is null).Select(e => e.Git).ToList();
        if (targets.Count == 0)
        {
            StatusBar.ShowMessage(Localizer.Format("msg.allRepositories.branchIsCheckedOut", "{0} is checked out in every repository.", name), TimeSpan.FromSeconds(4));
            return;
        }

        var delete = Localizer.Get("msg.allRepositories.delete", "Delete");
        var cancel = Localizer.Get("msg.gitChanges.cancel", "Cancel");
        var choice = await Dialogs.ShowAsync(new DialogRequest(
            Localizer.Format("msg.allRepositories.deleteBranchIn", "Delete {0} in {1}?", name, Repositories(targets.Count)),
            Localizer.Get("msg.allRepositories.deleteBranchInDetail", "The local branch is deleted where it is fully merged; git keeps it where it isn't, and says so."),
            [delete, cancel])
        {
            DefaultButton = cancel,
            IsDanger = true,
            Icon = "trash",
            Details = [new DetailLine(Localizer.Get("msg.allRepositories.repositories", "Repositories"), "header"), .. targets.Select(t => new DetailLine(t.Name))],
        }).ConfigureAwait(true);
        if (choice.Button != delete)
        {
            return;
        }

        var failures = await GitService.ForEachAsync(targets, git => git.DeleteBranchAsync(name, force: false)).ConfigureAwait(true);
        ReportRepositoryFailures(failures, targets.Count, Localizer.Format("msg.allRepositories.deletedIn", "Deleted {0} in {1}.", name, Repositories(targets.Count - failures.Count)));
    });

    // ------------------------------------------------------------------ the status bar and the operation in progress

    /// <summary>
    /// The status bar's branch item with every repository shown: their branch (or "3 branches"), outgoing and incoming
    /// commits added up, and the changed files of all; its tooltip has a line for each repository.
    /// </summary>
    private void UpdateAllRepositoriesStatus()
    {
        var services = ShownRepositories.Where(s => s.Status is not null).ToList();
        var branch = GitChanges.BranchName;
        var ahead = services.Sum(s => s.Status!.Ahead);
        var behind = services.Sum(s => s.Status!.Behind);
        var changes = services.Sum(s => s.Status!.Files.Count);
        var lines = services.Select(s => Localizer.Format(
            "msg.allRepositories.statusLine", "{0}: {1}, {2} changed file(s)", s.Name, s.Status!.Branch ?? "(detached)", s.Status.Files.Count.ToString(CultureInfo.InvariantCulture)));
        Workbench.StatusBar.Set(new StatusBarItem(BranchStatusItem, $"$(branch) {branch} ↑{ahead.ToString(CultureInfo.InvariantCulture)} ↓{behind.ToString(CultureInfo.InvariantCulture)}{(changes > 0 ? "  •" + changes.ToString(CultureInfo.InvariantCulture) : string.Empty)}", StatusBarAlignment.Right, 950, ContributionSource.Shell.Id)
        {
            Command = "git.switchBranch",
            Tooltip = Localizer.Format("msg.allRepositories.statusTooltip", "{0}. Click to switch branches.", Repositories(services.Count)) + Environment.NewLine + string.Join(Environment.NewLine, lines),
            AccessibleName = Localizer.Format("msg.allRepositories.statusAccessibleName", "Git, {0}, branch {1}, {2} changes", Repositories(services.Count), branch, changes.ToString(CultureInfo.InvariantCulture)),
        });
    }

    /// <summary>The repository whose operation (merge, rebase…) Git Changes shows: the open one, or with all shown the first that has one.</summary>
    private GitService OperationRepository() =>
        GitChanges.IsAllRepositories ? ShownRepositories.FirstOrDefault(s => s.Status?.Operation is not null) ?? Git : Git;
}
