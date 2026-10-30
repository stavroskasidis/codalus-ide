using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Codalus.Plugins.Protocol;
using Codalus.Shell.Controls;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Git;
using Codalus.Workbench.Hosting;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Menus;

namespace Codalus.Shell;

/// <summary>
/// The Git Repository document (Git → Manage Branches, the classic IDE's Git Repository window): each repository's
/// branches, remotes, tags, open pull requests and submodules in a tree, and the selected ref's history beside it. A
/// branch's menu is the branch drop-down's (<c>gitChanges/branch</c>, so plugins' entries show too); a tag, a pull request,
/// a submodule and a repository have menus of their own. Everything runs in the node's repository. The tree is read
/// again when git changes a repository it shows.
/// </summary>
public sealed partial class ShellHost
{
    private DispatcherTimer? _gitRepositoryTimer;

    /// <summary>
    /// Opens (or brings back) the Git Repository document, reads the repositories, and selects
    /// <paramref name="reference"/> (a branch's or tag's full ref) in <paramref name="repository"/>, or that repository's
    /// current branch (the open one's by default): View History, View all commits, a branch's View History.
    /// </summary>
    public async Task<GitRepositoryDocument?> ShowGitRepositoryAsync(GitService? repository = null, string? reference = null)
    {
        if (Git.Root is null)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.gitRepository.notInARepository", "The open solution is not in a git repository."), TimeSpan.FromSeconds(4));
            return null;
        }

        var document = Layout.FindDocument<GitRepositoryDocument>(GitRepositoryViewModel.DocumentId);
        if (document is null)
        {
            var viewModel = new GitRepositoryViewModel(new GitRepositoryActions
            {
                Repositories = () => GitChanges.Services.Where(s => s.Root is not null).ToList(),
                PullRequests = RepositoryPullRequestsAsync,
                CreateHistory = (git, query, title, canShowAllBranches) => NewHistory(git, query, title, canShowAllBranches),
                Run = RunInRepositoryAsync,
                MenuFor = GitRepositoryMenu,
                Activate = ActivateRepositoryNodeAsync,
            });
            var created = document = new GitRepositoryDocument(viewModel);
            void OnClosed(object? sender, Docking.ShellDocument closed)
            {
                if (closed == created)
                {
                    Layout.DocumentClosed -= OnClosed;
                    viewModel.Dispose();
                }
            }

            Layout.DocumentClosed += OnClosed;
        }

        Layout.OpenDocument(document, focus: true);
        await document.ViewModel.LoadAsync().ConfigureAwait(true);
        var git = repository ?? Git;
        if (reference is not null || repository is not null || document.ViewModel.Selected is null)
        {
            var target = reference ?? (git.Status?.Branch is { } branch ? "refs/heads/" + branch : null);
            if (target is not null)
            {
                document.ViewModel.Select(target, git);
            }
        }

        return document;
    }

    /// <summary>
    /// A history for the Git Repository document or a File History tab: its commit menu and details run in
    /// <paramref name="git"/>, and its toolbar's toggles are the user's settings (Show All Branches only where offered).
    /// </summary>
    private GitHistoryViewModel NewHistory(GitService git, GitLogQuery query, string title, bool canShowAllBranches, (string Path, int Start, int End)? lines = null)
    {
        var history = new GitHistoryViewModel(git, query, title, (action, commit) => InRepositoryAsync(git, () => CommitActionAsync(action, commit)), lines)
        {
            CanShowAllBranches = canShowAllBranches,
        };
        var settings = Workbench.Settings;
        history.InitializeToggles(
            canShowAllBranches && settings.GetBoolean(BuiltIns.HistoryAllBranchesSetting, false),
            settings.GetBoolean(BuiltIns.HistoryRemoteBranchesSetting, true),
            settings.GetBoolean(BuiltIns.HistoryTagsSetting, true));
        history.SaveToggle = (toggle, value) => _ = SaveDiffSettingAsync(
            toggle switch
            {
                "allBranches" => BuiltIns.HistoryAllBranchesSetting,
                "remoteBranches" => BuiltIns.HistoryRemoteBranchesSetting,
                _ => BuiltIns.HistoryTagsSetting,
            },
            value);
        return history;
    }

    /// <summary>After git changed a repository (its status): the open document reads the refs again, once things settle.</summary>
    private void ScheduleGitRepositoryReload()
    {
        if (Layout.FindDocument<GitRepositoryDocument>(GitRepositoryViewModel.DocumentId) is null)
        {
            return;
        }

        if (_gitRepositoryTimer is null)
        {
            _gitRepositoryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _gitRepositoryTimer.Tick += (_, _) =>
            {
                _gitRepositoryTimer.Stop();
                if (Layout.FindDocument<GitRepositoryDocument>(GitRepositoryViewModel.DocumentId) is { } document)
                {
                    _ = document.ViewModel.LoadAsync(pullRequests: false);
                }
            };
        }

        _gitRepositoryTimer.Stop();
        _gitRepositoryTimer.Start();
    }

    /// <summary>
    /// A repository's open pull requests, from the host its remotes are on once the user is signed in to it; before that
    /// nothing is sent (the provider's plugin doesn't even load), and the node offers Sign In.
    /// </summary>
    private async Task<GitRepositoryPullRequests> RepositoryPullRequestsAsync(GitService git, CancellationToken cancellationToken)
    {
        var remotes = await git.RemotesAsync(cancellationToken).ConfigureAwait(true);
        if (remotes.Count == 0)
        {
            return new GitRepositoryPullRequests([], Localizer.Get("msg.gitRepository.noRemote", "No remote: publish the repository to a Git host for pull requests."));
        }

        var match = git == Git && _match is not null ? _match : await Hosting.MatchAsync(remotes, cancellationToken).ConfigureAwait(true);
        if (match is null)
        {
            return Hosting.SuggestFor(remotes) is { } suggested && Hosting.AccountFor(suggested.Id) is null
                ? new GitRepositoryPullRequests([], Localizer.Format("msg.gitRepository.signInToSee", "Sign in to {0} to see the pull requests", suggested.Label), SignIn: () =>
                {
                    ShowAccounts(suggested.Id);
                    return Task.CompletedTask;
                })
                : new GitRepositoryPullRequests([], Localizer.Get("msg.gitRepository.notOnAHost", "The remotes aren't on a Git host Codalus knows."));
        }

        // The Pull Requests window's three filters, asked of the host at once; one it can't answer leaves only its folder empty.
        async Task<IReadOnlyList<PullRequestDto>?> FilterAsync(string filter)
        {
            try
            {
                return await Hosting.PullRequestsAsync(match, filter, cancellationToken).ConfigureAwait(true);
            }
            catch (HostingRequestException ex) when (filter != "all")
            {
                GitLog.AppendLine($"[hosting] {filter}: {ex.Message}");
                return null;
            }
        }

        var all = FilterAsync("all");
        var mine = FilterAsync("mine");
        var reviewRequested = FilterAsync("reviewRequested");
        await Task.WhenAll(all, mine, reviewRequested).ConfigureAwait(true);
        return new GitRepositoryPullRequests(await all.ConfigureAwait(true) ?? [], null, match)
        {
            Mine = await mine.ConfigureAwait(true),
            ReviewRequested = await reviewRequested.ConfigureAwait(true),
        };
    }

    /// <summary>
    /// A command from the document, in <paramref name="git"/>: Fetch, Pull, Push and Sync there (not in every repository,
    /// as Git Changes' buttons do with all repositories shown), Update, and the branch and tag commands with their ref.
    /// </summary>
    internal async Task RunInRepositoryAsync(string command, GitService git, string? reference)
    {
        switch (command)
        {
            case "git.fetch":
                await RemoteActionAsync(token => git.FetchAsync(token), Localizer.Get("msg.git.fetching", "Fetching…")).ConfigureAwait(true);
                break;
            case "git.pull":
                await RemoteActionAsync(token => git.PullAsync(token), Localizer.Get("msg.git.pulling", "Pulling…")).ConfigureAwait(true);
                break;
            case "git.sync":
                await RemoteActionAsync(async token =>
                {
                    if (git.Status?.Upstream is not null)
                    {
                        await git.PullAsync(token).ConfigureAwait(true);
                    }

                    await git.PushAsync(token).ConfigureAwait(true);
                }, Localizer.Get("msg.git.syncing", "Syncing…")).ConfigureAwait(true);
                break;
            case "git.push" when reference is not null:
                await RemoteActionAsync(token => git.PushBranchAsync(reference, token), Localizer.Get("msg.git.pushing", "Pushing…")).ConfigureAwait(true);
                break;
            case "git.updateBranch" when reference is not null:
                await InRepositoryAsync(git, () => UpdateBranchAsync(reference)).ConfigureAwait(true);
                break;
            case "git.deleteTag" when reference is not null:
                await InRepositoryAsync(git, () => DeleteTagAsync(reference.StartsWith("refs/tags/", StringComparison.Ordinal) ? reference["refs/tags/".Length..] : reference, remote: false)).ConfigureAwait(true);
                break;
            default:
                // A branch's own commands (F2, Delete) run only where its menu has them.
                if (reference is not null && await FindRepositoryBranchAsync(git, reference).ConfigureAwait(true) is { } branch)
                {
                    var (scope, _) = await RepositoryBranchScopeAsync(git, branch).ConfigureAwait(true);
                    if (Workbench.Menus.GetItems(MenuLocations.GitChangesBranch, scope).Any(i => i.Command == command))
                    {
                        await InRepositoryAsync(git, () => RunAsync(command, [Serialize(reference)], scope)).ConfigureAwait(true);
                    }
                }

                break;
        }

        await RefreshHistoryAsync().ConfigureAwait(true);
    }

    private static JsonElement Serialize(string value) => JsonSerializer.SerializeToElement(value, Docking.ShellJsonContext.Default.String);

    private static async Task<GitBranchDetails?> FindRepositoryBranchAsync(GitService git, string reference) =>
        (await git.BranchDetailsAsync().ConfigureAwait(true)).FirstOrDefault(b => b.FullRef == reference);

    /// <summary>A branch's menu context in its repository, with that repository's local branch of its name and remotes.</summary>
    private async Task<(Workbench.Context.IContextKeyReader Scope, GitBranchDetails? Twin)> RepositoryBranchScopeAsync(GitService git, GitBranchDetails branch)
    {
        var branches = await git.BranchDetailsAsync().ConfigureAwait(true);
        var twin = branch.IsRemote ? branches.FirstOrDefault(b => !b.IsRemote && b.Name == branch.ShortName) : null;
        var hasRemote = (await git.RemotesAsync().ConfigureAwait(true)).Count > 0;
        return (GitBranchScope(branch, git, twin, hasRemote), twin);
    }

    /// <summary>A double-click or Enter: a branch is checked out, a pull request opens in the browser.</summary>
    private Task ActivateRepositoryNodeAsync(GitRepositoryNode node) => node switch
    {
        { Branch: { } branch, Repository: { } git } => InRepositoryAsync(git, () => CheckoutBranchAsync(branch.FullRef)),
        { PullRequest: { } pullRequest } => ShowPullRequestInWindowAsync(pullRequest),
        _ => Task.CompletedTask,
    };

    private Task ShowPullRequestInWindowAsync(PullRequestDto pullRequest)
    {
        if (Uri.TryCreate(pullRequest.WebUrl, UriKind.Absolute, out _))
        {
            OpenUrl(pullRequest.WebUrl);
        }

        return Task.CompletedTask;
    }

    /// <summary>A node's menu: a branch's is <c>gitChanges/branch</c>; a tag's, a pull request's, a submodule's and a repository's are the document's own.</summary>
    internal ContextMenu? GitRepositoryMenu(GitRepositoryNode node)
    {
        if (node.Repository is not { } git)
        {
            return null;
        }

        switch (node)
        {
            case { Branch: { } branch }:
                // The data the tree was built from: no git here (a menu opens at once). The commands read git again.
                var twin = branch.IsRemote ? node.Siblings?.FirstOrDefault(b => !b.IsRemote && b.Name == branch.ShortName) : null;
                var scope = GitBranchScope(branch, git, twin, node.HasRemote);
                JsonElement[] args = [Serialize(branch.FullRef)];
                return MenuBuilder.Context(this, MenuLocations.GitChangesBranch, new MenuBuilder.MenuShape(c => new RelayCommand(() =>
                    _ = c is "git.push" or "git.updateBranch" ? RunInRepositoryAsync(c, git, branch.FullRef) : InRepositoryAsync(git, () => RunAsync(c, args, scope))))
                {
                    Scope = scope,
                    TitleFor = item => GitBranchTitle(item.Command, branch, git.Status?.Branch, twin),
                    KeyFor = item => item.RuleKeyLabel,
                });
            case { Tag: { } tag }:
                return TagMenu(git, tag, node.HasRemote);
            case { PullRequest: { } pullRequest }:
            {
                var menu = new ContextMenu();
                menu.Items.Add(MenuEntry.Create(Localizer.Get("msg.gitRepository.menuCheckOut", "Check Out"), "branch", click: () => _ = CheckOutPullRequestAsync(git, pullRequest, node.Match)));
                menu.Items.Add(MenuEntry.Create(Localizer.Get("msg.gitRepository.menuOpenInBrowser", "Open in Browser"), "link-external", click: () => _ = ShowPullRequestInWindowAsync(pullRequest)));
                menu.Items.Add(MenuEntry.Create(Localizer.Get("msg.gitRepository.menuShowInPullRequests", "Show in Pull Requests"), "pull-request", click: () => Layout.ShowToolWindow(PullRequestsViewModel.ToolWindowId, focus: true)));
                menu.Items.Add(new Separator());
                menu.Items.Add(MenuEntry.Create(Localizer.Get("msg.gitRepository.menuCopyLink", "Copy Link"), "copy", click: () => _ = CopyTextAsync(pullRequest.WebUrl)));
                return menu;
            }

            case { Submodule: { } submodule }:
            {
                var menu = new ContextMenu();
                menu.Items.Add(MenuEntry.Create(Localizer.Get("msg.gitRepository.menuUpdateSubmodules", "Update Submodules"), "download", click: () => _ = InRepositoryAsync(git, ReloadAfter(UpdateSubmodulesAsync))));
                menu.Items.Add(MenuEntry.Create(Localizer.Get("msg.gitRepository.menuOpenInNewWindow", "Open in New Window"), "window-new", click: () => StartNewWindow(submodule.Path)));
                menu.Items.Add(new Separator());
                menu.Items.Add(MenuEntry.Create(OperatingSystem.IsMacOS() ? Localizer.Get("command.explorer.reveal.mac", "Reveal in Finder") : OperatingSystem.IsWindows() ? Localizer.Get("command.explorer.reveal.windows", "Reveal in File Explorer") : Localizer.Get("command.explorer.reveal.linux", "Reveal in File Manager"), "folder-open", click: () => RevealPath(submodule.Path)));
                menu.Items.Add(MenuEntry.Create(Localizer.Get("msg.gitRepository.menuOpenInTerminal", "Open in Terminal"), "terminal", click: () => OpenInTerminal(submodule.Path)));
                menu.Items.Add(MenuEntry.Create(Localizer.Get("msg.gitRepository.menuCopyPath", "Copy Path"), "copy", click: () => _ = CopyTextAsync(submodule.Path)));
                return menu;
            }

            case { Kind: GitRepositoryNodeKind.Repository }:
            {
                var menu = new ContextMenu();
                menu.Items.Add(MenuEntry.Create(Localizer.Get("msg.gitRepository.menuNewBranch", "New Branch…"), "branch", click: () => _ = InRepositoryAsync(git, () => NewBranchFromAsync(null))));
                menu.Items.Add(MenuEntry.Create(Localizer.Get("msg.gitRepository.menuNewTag", "New Tag…"), "tag", click: () => _ = InRepositoryAsync(git, () => GitActionAsync(() => CreateTagAsync(null)))));
                menu.Items.Add(new Separator());
                menu.Items.Add(MenuEntry.Create(Localizer.Get("msg.gitRepository.menuFetch", "Fetch"), "arrow-down", click: () => _ = RunInRepositoryAsync("git.fetch", git, null)));
                menu.Items.Add(MenuEntry.Create(Localizer.Get("msg.gitRepository.menuManageRemotes", "Manage Remotes…"), "cloud", click: () => _ = InRepositoryAsync(git, ManageRemotesAsync)));
                menu.Items.Add(new Separator());
                menu.Items.Add(MenuEntry.Create(Localizer.Get("msg.gitRepository.menuRefresh", "Refresh"), "refresh", click: () => _ = ReloadGitRepositoryAsync()));
                return menu;
            }

            default:
                return null;
        }
    }

    private Func<Task> ReloadAfter(Func<Task> action) => async () =>
    {
        await action().ConfigureAwait(true);
        await ReloadGitRepositoryAsync().ConfigureAwait(true);
    };

    private async Task ReloadGitRepositoryAsync()
    {
        if (Layout.FindDocument<GitRepositoryDocument>(GitRepositoryViewModel.DocumentId) is { } document)
        {
            await document.ViewModel.LoadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// A tag's menu: Checkout (HEAD detached at it), New Branch From…, Push and Delete from its remote, Delete, View History,
    /// Copy Tag Name.
    /// </summary>
    private ContextMenu TagMenu(GitService git, GitTag tag, bool hasRemote)
    {
        var reference = "refs/tags/" + tag.Name;
        var menu = new ContextMenu();
        menu.Items.Add(MenuEntry.Create(Localizer.Get("msg.gitRepository.menuCheckoutDetached", "Checkout (Detached)"), "go-to", click: () => _ = InRepositoryAsync(git, () => GitActionAsync(() => git.CheckoutAsync(reference)))));
        menu.Items.Add(MenuEntry.Create(Localizer.Get("msg.gitBranches.menuNewLocalBranchFrom", "New Local Branch From…"), "branch", click: () => _ = InRepositoryAsync(git, () => NewBranchFromAsync(reference))));
        menu.Items.Add(new Separator());
        if (hasRemote)
        {
            menu.Items.Add(MenuEntry.Create(Localizer.Get("msg.gitRepository.menuPushTag", "Push"), "upload", click: () => _ = InRepositoryAsync(git, () => PushTagToRemoteAsync(git, tag.Name))));
        }

        menu.Items.Add(MenuEntry.Create(Localizer.Get("msg.gitBranches.menuDelete", "Delete"), "trash", keys: OperatingSystem.IsMacOS() ? "cmd+backspace" : "delete", click: () => _ = InRepositoryAsync(git, () => DeleteTagAsync(tag.Name, remote: false))));
        if (hasRemote)
        {
            menu.Items.Add(MenuEntry.Create(Localizer.Get("msg.gitRepository.menuDeleteTagFromRemote", "Delete from Remote…"), "trash", click: () => _ = InRepositoryAsync(git, () => DeleteTagAsync(tag.Name, remote: true))));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry.Create(Localizer.Get("msg.gitRepository.menuViewHistory", "View History"), "history", click: () => _ = InRepositoryAsync(git, () => HistoryCommandAsync([Serialize(reference)]))));
        menu.Items.Add(MenuEntry.Create(Localizer.Get("msg.gitRepository.menuCopyTagName", "Copy Tag Name"), "copy", click: () => _ = CopyTextAsync(tag.Name)));
        return menu;
    }

    /// <summary>Pushes a tag to the remote the current branch tracks, else origin, else the only remote.</summary>
    private Task PushTagToRemoteAsync(GitService git, string name) => GitActionAsync(async () =>
    {
        var remote = await TagRemoteAsync(git).ConfigureAwait(true);
        await RemoteActionAsync(token => git.PushTagAsync(name, remote, token), Localizer.Get("msg.git.pushing", "Pushing…")).ConfigureAwait(true);
    });

    private static async Task<string> TagRemoteAsync(GitService git)
    {
        var remotes = (await git.RemotesAsync().ConfigureAwait(true)).Select(r => r.Name).ToList();
        var upstream = git.Status?.Upstream;
        return remotes.OrderByDescending(r => r.Length).FirstOrDefault(r => upstream?.StartsWith(r + "/", StringComparison.Ordinal) == true)
            ?? (remotes.Contains("origin") ? "origin" : remotes.FirstOrDefault() ?? "origin");
    }

    /// <summary>Deletes a tag here, or on its remote (for everyone); both ask first, Cancel the default.</summary>
    private Task DeleteTagAsync(string name, bool remote) => GitActionAsync(async () =>
    {
        var git = Repo;
        var where = remote ? await TagRemoteAsync(git).ConfigureAwait(true) : null;
        var delete = Localizer.Get("msg.gitBranches.delete", "Delete");
        var cancel = Localizer.Get("msg.gitChanges.cancel", "Cancel");
        var answer = await Dialogs.ShowAsync(new DialogRequest(
            where is null ? Localizer.Format("msg.gitRepository.deleteTagTitle", "Delete the tag {0}?", name) : Localizer.Format("msg.gitRepository.deleteRemoteTagTitle", "Delete the tag {0} from {1}?", name, where),
            where is null ? Localizer.Get("msg.gitRepository.tagDeletedHere", "The tag is deleted from this repository; its commits stay.") : Localizer.Get("msg.gitBranches.deletesOnTheServer", "This deletes it on the server for everyone."),
            [delete, cancel])
        {
            DefaultButton = cancel,
            Icon = "trash",
            IsDanger = true,
        }).ConfigureAwait(true);
        if (answer.Button != delete)
        {
            return;
        }

        if (where is null)
        {
            await git.DeleteTagAsync(name).ConfigureAwait(true);
        }
        else
        {
            await RemoteActionAsync(token => git.DeleteRemoteTagAsync(name, where, token), Localizer.Get("msg.gitRepository.deletingTag", "Deleting the tag…")).ConfigureAwait(true);
        }

        await ReloadGitRepositoryAsync().ConfigureAwait(true);
    });

    /// <summary>Checks a pull request's branch out in its repository (its host's checkout: a fork's branch too).</summary>
    private Task CheckOutPullRequestAsync(GitService git, PullRequestDto pullRequest, HostingMatch? match) => GitActionAsync(async () =>
    {
        if (match is null)
        {
            return;
        }

        try
        {
            var checkout = await Hosting.CheckoutAsync(match, pullRequest, CancellationToken.None).ConfigureAwait(true);
            await git.FetchAndSwitchAsync(checkout.Remote, checkout.SourceRef, checkout.LocalBranch, checkout.Track).ConfigureAwait(true);
            StatusBar.ShowMessage(Localizer.Format("msg.hosting.checkedOutOn", "Checked out {0} on {1}.", pullRequest.Reference, checkout.LocalBranch), TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is HostingRequestException or HostingSignInRequiredException)
        {
            ReportGitError(ex);
        }
    }, Localizer.Format("msg.gitRepository.checkingOut", "Checking out {0}…", pullRequest.Reference));
}
