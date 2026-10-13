using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using DaedalusStudio.Platform;
using DaedalusStudio.Platform.Credentials;
using DaedalusStudio.Plugins.Protocol;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Commands;
using DaedalusStudio.Workbench.Git;
using DaedalusStudio.Workbench.Hosting;
using DaedalusStudio.Workbench.Secrets;
using DaedalusStudio.Workbench.StatusBar;
using DaedalusStudio.Workbench.Localization;

namespace DaedalusStudio.Shell;

/// <summary>
/// Git hosting and the rest of Git depth (M13b, ADR 0031): accounts and sign-in, the open repository's host, pull
/// requests, review comments in editors, CI status, work items and <c>#</c> completion, publishing, the askpass prompt,
/// worktrees, Git LFS, and solutions spanning several repositories.
/// </summary>
public sealed partial class ShellHost : IHostingActions
{
    public const string HostingEnabledSetting = "hosting.enabled";
    public const string HostingStatusSecondsSetting = "hosting.statusRefreshSeconds";
    public const string HostingCiSetting = "hosting.ciStatus";
    public const string HostingReviewCommentsSetting = "hosting.reviewComments";
    public const string HostingResolvedCommentsSetting = "hosting.showResolvedComments";
    private const string CiStatusItem = "daedalus.hosting.ci";

    private HostingMatch? _match;
    private HostingProviderInfo? _suggested;
    private CancellationTokenSource? _hostingCts;
    private string? _hostingKey;
    private string? _hostingFingerprint;
    private DispatcherTimer? _statusTimer;
    private DispatcherTimer? _repositoryTimer;
    private BranchStatusDto? _branchStatus;
    private string? _lfsRoot;
    private bool _lfsUsed;
    private DateTime _locksRead;

    public SecretStorage Secrets { get; private set; } = null!;

    public HostingService Hosting { get; private set; } = null!;

    /// <summary>The askpass server, or null when the <c>daedalus</c> executable wasn't found.</summary>
    public AskpassServer? Askpass { get; private set; }

    /// <summary>The current branch's pull request's review threads, by file.</summary>
    public ReviewThreadIndex ReviewThreads { get; } = new();

    public PullRequestsViewModel PullRequests { get; private set; } = null!;

    public WorkItemsViewModel WorkItems { get; private set; } = null!;

    public WorktreesViewModel Worktrees { get; private set; } = null!;

    public AccountsViewModel Accounts { get; } = new();

    /// <summary>The open repository on a host the user is signed in to.</summary>
    public HostingMatch? Match => _match;

    public HostingProviderInfo? Suggested => _suggested;

    public bool HasRepository => Git.Root is not null;

    public string? CurrentBranch => Git.Status?.Branch;

    /// <summary>The CI status of the current branch, as last read.</summary>
    public BranchStatusDto? BranchStatus => _branchStatus;

    public event EventHandler? ReviewThreadsChanged;

    private void InitializeHosting()
    {
        Workbench.Settings.Register(BuiltIns.HostingSettings());
        // A --user-data-dir profile keeps its sign-ins apart from the default profile's.
        var userData = Options.UserDataDir ?? Environment.GetEnvironmentVariable(AppPaths.UserDataDirVariable);
        Secrets = new SecretStorage(CredentialStore.Create(), SecretStorage.ServiceFor(userData));
        Secrets.FellBack += (_, reason) => Ui.Post(() =>
        {
            Workbench.Notifications.Warning(Localizer.Format("msg.hosting.theCredentialStoreCantBeUsed", "The credential store can't be used ({0}) Sign-ins last until Daedalus Studio closes.", reason));
            UpdateAccounts();
        });
        Plugins.Secrets = Secrets;
        Hosting = new HostingService(Plugins, Path.Combine(Paths.State, "hosting-accounts.json"), () => Workbench.Settings.GetBoolean(HostingEnabledSetting, true))
        {
            ReadSetting = key => Workbench.Settings.GetString(key, string.Empty),
        };
        Hosting.Changed += (_, _) => Ui.Post(() =>
        {
            UpdateAccounts();

            // Any plugin's state change raises it: look at the host again only when a provider or an account changed.
            var fingerprint = string.Join('|', Hosting.Providers.Select(p => $"{p.Id}:{p.IsRegistered}").Concat(Hosting.Accounts.Select(a => $"{a.ProviderId}@{a.Host}/{a.Name}")));
            if (fingerprint != _hostingFingerprint)
            {
                _hostingFingerprint = fingerprint;
                ScheduleHostingRefresh(force: true);
            }
        });
        Hosting.DeviceCodeReceived += (_, code) => Ui.Post(() => Accounts.Row(code.ProviderId)?.ShowCode(code.UserCode, code.VerificationUriComplete ?? code.VerificationUri));
        Hosting.SignInRequired += (_, providerId) => Ui.Post(() =>
        {
            var label = Hosting.Provider(providerId)?.Label ?? providerId;
            var notification = Workbench.Notifications.Warning(Localizer.Format("msg.hosting.yourSessionExpiredOrWasRevoked", "Your {0} session expired or was revoked. Sign in again to see pull requests and checks.", label), "Sign In");
            _ = notification.Result.ContinueWith(t =>
            {
                if (t.Result == "Sign In")
                {
                    Ui.Post(() => ShowAccounts(providerId));
                }
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        });
        foreach (var window in BuiltIns.HostingToolWindows())
        {
            Workbench.ToolWindows.Register(window);
        }

        PullRequests = new PullRequestsViewModel(this);
        WorkItems = new WorkItemsViewModel(this);
        Worktrees = new WorktreesViewModel(Git, WorktreeActionAsync);
        GitChanges.SwitchRepository = SwitchRepositoryAsync;
        GitChanges.CompletionSource = CommitCompletionsAsync;
        ReviewThreads.Changed += (_, _) => ReviewThreadsChanged?.Invoke(this, EventArgs.Empty);
        Git.StatusChanged += (_, _) => Ui.Post(() =>
        {
            ScheduleHostingRefresh(force: false);
            _ = RefreshLfsAsync();
        });
        Solutions.SolutionChanged += (_, _) => Ui.Post(ScheduleRepositoryDiscovery);
        Solutions.ProjectChanged += (_, _) => Ui.Post(ScheduleRepositoryDiscovery);
        Workbench.Settings.Changed += (_, e) => Ui.Post(() =>
        {
            if (e.Affects("hosting") || e.Affects("github") || e.Affects("gitlab") || e.Affects("azureDevOps"))
            {
                ConfigureStatusTimer();
                UpdateAccounts();
                ScheduleHostingRefresh(force: true);
            }
        });
        InitializeAskpass();
        ConfigureStatusTimer();
        UpdateAccounts();
    }

    private void ConfigureStatusTimer()
    {
        _statusTimer ??= new DispatcherTimer();
        _statusTimer.Stop();
        _statusTimer.Interval = TimeSpan.FromSeconds(Math.Max(15, Workbench.Settings.GetInt32(HostingStatusSecondsSetting, 120)));
        _statusTimer.Tick -= OnStatusTimer;
        _statusTimer.Tick += OnStatusTimer;
        _statusTimer.Start();
    }

    private void OnStatusTimer(object? sender, EventArgs e)
    {
        if (_match is not null)
        {
            _ = RefreshBranchStatusAsync(_hostingCts?.Token ?? CancellationToken.None);
        }
    }

    // ------------------------------------------------------------------ the repository's host

    /// <summary>Works out the host again when the repository, branch or commit changed (or always, with <paramref name="force"/>).</summary>
    private void ScheduleHostingRefresh(bool force)
    {
        var status = Git.Status;
        var key = status is null ? null : $"{status.Root}|{status.Branch}|{status.Head}|{status.Upstream}";
        if (!force && key == _hostingKey)
        {
            return;
        }

        _hostingKey = key;
        _ = RefreshHostingAsync();
    }

    /// <summary>
    /// Finds the provider of the repository's remotes (activating its plugin only when the user is signed in to its
    /// host), then refreshes the Pull Requests and Work Items windows, the CI status and the review comments.
    /// </summary>
    public async Task RefreshHostingAsync()
    {
        _hostingCts?.Cancel();
        var cts = _hostingCts = new CancellationTokenSource();
        var token = cts.Token;
        try
        {
            IReadOnlyList<GitRemote> remotes = Git.Root is null ? [] : await Git.RemotesAsync(token).ConfigureAwait(true);
            var match = await Hosting.MatchAsync(remotes, token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return;
            }

            _match = match;
            _suggested = match is null && Hosting.SuggestFor(remotes) is { } suggested && Hosting.AccountFor(suggested.Id) is null ? suggested : null;
            Workbench.Context.Set("hostingRepository", match is not null);
            Workbench.Context.Set("hostingProvider", match?.Provider.Id);
            await PullRequests.ReloadAsync().ConfigureAwait(true);
            if (Layout.IsVisible(WorkItemsViewModel.ToolWindowId))
            {
                await WorkItems.ReloadAsync().ConfigureAwait(true);
            }

            await RefreshBranchStatusAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is HostingRequestException or HostingSignInRequiredException or GitException or InvalidOperationException)
        {
            GitLog.AppendLine("[hosting] " + ex.Message);
        }
    }

    /// <summary>The current branch's CI status, and its open pull request's review threads.</summary>
    public async Task RefreshBranchStatusAsync(CancellationToken cancellationToken)
    {
        if (_match is not { } match || Git.Status is not { Branch: { } branch } status)
        {
            ClearBranchStatus();
            return;
        }

        try
        {
            if (Workbench.Settings.GetBoolean(HostingCiSetting, true) && status.Upstream is not null)
            {
                var ci = await Hosting.BranchStatusAsync(match, branch, status.Head, cancellationToken).ConfigureAwait(true);
                ShowBranchStatus(ci);
            }
            else
            {
                ShowBranchStatus(null);
            }

            var pullRequests = await Hosting.PullRequestsAsync(match, "all", cancellationToken).ConfigureAwait(true);
            var pullRequest = pullRequests.FirstOrDefault(p => p.SourceBranch == branch && p.SourceRepository is null) ?? pullRequests.FirstOrDefault(p => p.SourceBranch == branch);
            GitChanges.PullRequestText = pullRequest is null ? null : $"{pullRequest.Reference} {pullRequest.Title}";
            if (pullRequest is not null && Workbench.Settings.GetBoolean(HostingReviewCommentsSetting, true))
            {
                var details = await Hosting.PullRequestAsync(match, pullRequest.Number, cancellationToken).ConfigureAwait(true);
                ReviewThreads.Set(Git.Root, details, Workbench.Settings.GetBoolean(HostingResolvedCommentsSetting, false));
            }
            else
            {
                ReviewThreads.Set(null, null);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is HostingRequestException or HostingSignInRequiredException)
        {
            GitLog.AppendLine("[hosting] " + ex.Message);
        }
    }

    private void ClearBranchStatus()
    {
        ShowBranchStatus(null);
        GitChanges.PullRequestText = null;
        ReviewThreads.Set(null, null);
    }

    private void ShowBranchStatus(BranchStatusDto? status)
    {
        _branchStatus = status;
        if (status is null or { State: "none" })
        {
            Workbench.StatusBar.Remove(CiStatusItem);
            GitChanges.SetCi(null, string.Empty, null);
            return;
        }

        var failed = status.Checks.Count(c => c.State == "failure");
        var pending = status.Checks.Count(c => c.State == "pending");
        var (icon, text) = status.State switch
        {
            "success" => ("test-passed", "Checks passed"),
            "failure" => ("test-failed", $"{failed.ToString(CultureInfo.InvariantCulture)} check{(failed == 1 ? string.Empty : "s")} failed"),
            _ => ("test-running", $"{pending.ToString(CultureInfo.InvariantCulture)} check{(pending == 1 ? string.Empty : "s")} running"),
        };
        var tooltip = string.Join('\n', status.Checks.Select(c => $"{c.State,-9} {c.Name}{(c.Description is { Length: > 0 } d ? " — " + d : string.Empty)}"));
        Workbench.StatusBar.Set(new StatusBarItem(CiStatusItem, $"$({icon}) {text}", StatusBarAlignment.Right, 949, ContributionSource.Shell.Id)
        {
            Command = "hosting.showChecks",
            Tooltip = tooltip.Length > 0 ? tooltip : text,
            AccessibleName = "CI: " + text,
        });
        GitChanges.SetCi(status.State, text, tooltip);
    }

    /// <summary>The checks of the current branch; picking one opens it.</summary>
    public async Task ShowChecksAsync()
    {
        if (_branchStatus is not { Checks.Length: > 0 } status)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.hosting.noChecksForThisBranch", "No checks for this branch."), TimeSpan.FromSeconds(4));
            return;
        }

        var items = status.Checks.Select(c => new QuickPickItem(c.Name) { Detail = c.Description is { Length: > 0 } d ? $"{c.State} · {d}" : c.State, Tag = c.Url }).ToList();
        if (status.Url is { } all)
        {
            items.Add(new QuickPickItem("Open All Checks in the Browser") { Tag = all });
        }

        if (await Palette.ShowAsync(items, "Checks of " + (Git.Status?.Branch ?? "the branch")).ConfigureAwait(true) is { Tag: string url })
        {
            OpenUrl(url);
        }
    }

    // ------------------------------------------------------------------ accounts

    /// <summary>The Accounts page; with <paramref name="provider"/>, starts signing in to it.</summary>
    public AccountsDocument ShowAccounts(string? provider = null)
    {
        UpdateAccounts();
        var document = Layout.FindDocument<AccountsDocument>(AccountsDocument.DocumentId) ?? new AccountsDocument(Accounts);
        Layout.OpenDocument(document, focus: true);
        if (provider is not null && Accounts.Row(provider) is { CanSignIn: true } row)
        {
            _ = SignInAsync(row, null);
        }

        return document;
    }

    private void UpdateAccounts()
    {
        Accounts.StoreText = Secrets.IsPersistent
            ? $"Tokens are kept in {Secrets.StoreName}, never in a file."
            : "Tokens are kept in memory only, so a sign-in lasts until Daedalus Studio closes.";
        Accounts.IsEnabled = Hosting.IsEnabled;
        var providers = Hosting.Providers;
        foreach (var stale in Accounts.Providers.Where(r => providers.All(p => p.Id != r.Id)).ToList())
        {
            Accounts.Providers.Remove(stale);
        }

        foreach (var provider in providers)
        {
            if (Accounts.Row(provider.Id) is not { } row)
            {
                row = new AccountProviderRow(provider.Id, provider.Label, SignInAsync, SignOutAsync, CancelSignIn, CopyCodeAndOpen);
                Accounts.Providers.Add(row);
            }

            row.ServerHint = ServerHint(provider.Id);
            var account = Hosting.AccountFor(provider.Id);
            row.SetAccount(account?.Name, account?.Host);
        }
    }

    private string? ServerHint(string providerId) => providerId switch
    {
        "github" => $"Server: {Workbench.Settings.GetString("github.serverUrl", "https://github.com")} (the setting github.serverUrl). A token needs the repo, read:org and workflow scopes.",
        "gitlab" => $"Server: {Workbench.Settings.GetString("gitlab.serverUrl", "https://gitlab.com")} (the setting gitlab.serverUrl). A token needs the api scope.",
        "azure-devops" => $"Server: {Workbench.Settings.GetString("azureDevOps.serverUrl", "https://dev.azure.com")} (the setting azureDevOps.serverUrl). A token needs Code, Build and Work Items access.",
        _ => null,
    };

    private async Task SignInAsync(AccountProviderRow row, string? token)
    {
        if (row.IsSigningIn)
        {
            return;
        }

        if (token is not null && token.Trim().Length == 0)
        {
            row.Error = "Paste a personal access token first.";
            return;
        }

        row.Error = null;
        row.IsSigningIn = true;
        using var cts = new CancellationTokenSource();
        row.SignInCancellation = cts;
        try
        {
            var account = await Hosting.SignInAsync(row.Id, token, cts.Token).ConfigureAwait(true);
            row.Token = string.Empty;
            StatusBar.ShowMessage(Localizer.Format("msg.hosting.signedInToAs", "Signed in to {0} as {1}.", row.Label, account.Name), TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is HostingRequestException or HostingSignInRequiredException)
        {
            row.Error = ex.Message;
        }
        finally
        {
            row.SignInCancellation = null;
            row.IsSigningIn = false;
            UpdateAccounts();
        }

        await RefreshHostingAsync().ConfigureAwait(true);
    }

    private async Task SignOutAsync(AccountProviderRow row)
    {
        try
        {
            await Hosting.SignOutAsync(row.Id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is HostingRequestException or HostingSignInRequiredException)
        {
            row.Error = ex.Message;
        }

        UpdateAccounts();
        await RefreshHostingAsync().ConfigureAwait(true);
    }

    private static void CancelSignIn(AccountProviderRow row) => row.SignInCancellation?.Cancel();

    private void CopyCodeAndOpen(AccountProviderRow row)
    {
        if (row.UserCode is { } code && Window?.Clipboard is { } clipboard)
        {
            _ = Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, code);
        }

        if (row.VerificationUri is { } uri)
        {
            OpenUrl(uri);
        }
    }

    // ------------------------------------------------------------------ IHostingActions

    public Task<IReadOnlyList<PullRequestDto>> PullRequestsAsync(string filter, CancellationToken cancellationToken) =>
        Hosting.PullRequestsAsync(_match ?? throw new HostingRequestException("Not on a signed-in host."), filter, cancellationToken);

    public Task<PullRequestDetailsDto> PullRequestAsync(string number, CancellationToken cancellationToken) =>
        Hosting.PullRequestAsync(_match ?? throw new HostingRequestException("Not on a signed-in host."), number, cancellationToken);

    public Task<IReadOnlyList<WorkItemDto>> WorkItemsAsync(bool assignedToMe, string? text, CancellationToken cancellationToken) =>
        Hosting.WorkItemsAsync(_match ?? throw new HostingRequestException("Not on a signed-in host."), assignedToMe, text, 50, cancellationToken);

    /// <summary>Fetches a pull request's branch and switches to it.</summary>
    public Task CheckOutAsync(PullRequestDto pullRequest) => GitActionAsync(async () =>
    {
        if (_match is not { } match)
        {
            return;
        }

        try
        {
            var checkout = await Hosting.CheckoutAsync(match, pullRequest, CancellationToken.None).ConfigureAwait(true);
            await Git.FetchAndSwitchAsync(checkout.Remote, checkout.SourceRef, checkout.LocalBranch, checkout.Track).ConfigureAwait(true);
            StatusBar.ShowMessage(Localizer.Format("msg.hosting.checkedOutOn", "Checked out {0} on {1}.", pullRequest.Reference, checkout.LocalBranch), TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is HostingRequestException or HostingSignInRequiredException)
        {
            ReportGitError(ex);
        }

        await RefreshHostingAsync().ConfigureAwait(true);
    }, $"Checking out {pullRequest.Reference}…");

    void IHostingActions.CreatePullRequest() => _ = CreatePullRequestAsync();

    void IHostingActions.SignIn(string? providerId) => ShowAccounts(providerId);

    public void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            ShellIntegration.OpenUrl(uri);
        }
    }

    Task IHostingActions.OpenFileAsync(string relativePath, int line) =>
        Git.Root is { } root
            ? OpenFileAsync(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)), preview: false, new LanguageServices.Protocol.Position(Math.Max(0, line - 1), 0))
            : Task.CompletedTask;

    void IHostingActions.Mention(string reference)
    {
        var message = GitChanges.Message;
        GitChanges.Message = message.Length == 0 ? reference + " " : message.TrimEnd() + " " + reference;
        Layout.ShowToolWindow(GitChangesViewModel.ToolWindowId, focus: true);
    }

    private PullRequestsViewModel OpenPullRequests()
    {
        _ = PullRequests.ReloadAsync();
        return PullRequests;
    }

    private WorkItemsViewModel OpenWorkItems()
    {
        _ = WorkItems.ReloadAsync();
        return WorkItems;
    }

    private WorktreesViewModel OpenWorktrees()
    {
        _ = Worktrees.ReloadAsync();
        return Worktrees;
    }

    // ------------------------------------------------------------------ creating a pull request

    /// <summary>Create Pull Request for the current branch (a document).</summary>
    public async Task<CreatePullRequestDocument?> CreatePullRequestAsync()
    {
        if (_match is not { } match)
        {
            if (_suggested is { } suggested)
            {
                ShowAccounts(suggested.Id);
            }
            else
            {
                StatusBar.ShowMessage(Localizer.Get("msg.hosting.thisRepositoryIsntOnAHost", "This repository isn't on a host you're signed in to."), TimeSpan.FromSeconds(5));
            }

            return null;
        }

        if (Git.Status is not { Branch: { } branch })
        {
            StatusBar.ShowMessage(Localizer.Get("msg.hosting.switchToABranchFirst", "Switch to a branch first."), TimeSpan.FromSeconds(4));
            return null;
        }

        string? defaultBranch = null;
        try
        {
            defaultBranch = (await Hosting.RepositoryInfoAsync(match, CancellationToken.None).ConfigureAwait(true)).DefaultBranch;
        }
        catch (Exception ex) when (ex is HostingRequestException or HostingSignInRequiredException)
        {
            GitLog.AppendLine("[hosting] " + ex.Message);
        }

        var remote = match.Repository.Remote ?? "origin";
        var targets = (await Git.BranchesAsync().ConfigureAwait(true))
            .Where(b => b.IsRemote && b.Name.StartsWith(remote + "/", StringComparison.Ordinal))
            .Select(b => b.Name[(remote.Length + 1)..])
            .Where(name => name != branch)
            .Prepend(defaultBranch)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (targets.Count == 0)
        {
            targets.Add("main");
        }

        if (Layout.FindDocument<CreatePullRequestDocument>(CreatePullRequestDocument.DocumentId) is { } old)
        {
            Layout.CloseDocument(old);
        }

        var viewModel = new CreatePullRequestViewModel(match.Provider.Label, $"{match.Repository.Owner}/{match.Repository.Name}", branch, targets, defaultBranch, CreatePullRequestAsync);
        var document = new CreatePullRequestDocument(viewModel);
        Layout.OpenDocument(document, focus: true);
        return document;
    }

    private async Task CreatePullRequestAsync(CreatePullRequestViewModel form)
    {
        if (_match is not { } match)
        {
            form.Error = "Sign in to the repository's host first.";
            return;
        }

        var remote = match.Repository.Remote ?? "origin";
        try
        {
            // The branch must be on the host first.
            if (!await Git.IsBranchPublishedAsync(remote, form.SourceBranch).ConfigureAwait(true) || Git.Status is { Ahead: > 0 })
            {
                if (Git.Status?.Upstream is null)
                {
                    await Git.PublishBranchAsync(remote, form.SourceBranch).ConfigureAwait(true);
                }
                else
                {
                    await Git.PushAsync().ConfigureAwait(true);
                }
            }

            var created = await Hosting.CreatePullRequestAsync(match, form.Title.Trim(), form.Description, form.SourceBranch, form.Target!, form.IsDraft, CancellationToken.None).ConfigureAwait(true);
            form.Created = $"Created {created.Reference}: {created.Title}";
            var notification = Workbench.Notifications.Info(Localizer.Format("msg.hosting.createdPullRequest", "Created pull request {0}: {1}", created.Reference, created.Title), "Open in Browser");
            _ = notification.Result.ContinueWith(t =>
            {
                if (t.Result == "Open in Browser")
                {
                    Ui.Post(() => OpenUrl(created.WebUrl));
                }
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            await RefreshHostingAsync().ConfigureAwait(true);
            Layout.ShowToolWindow(PullRequestsViewModel.ToolWindowId, focus: false);
        }
        catch (Exception ex) when (ex is GitException or HostingRequestException or HostingSignInRequiredException or InvalidOperationException)
        {
            form.Error = ex.Message;
        }
    }

    // ------------------------------------------------------------------ # completion

    private async Task<IReadOnlyList<CommitCompletion>> CommitCompletionsAsync(string query, CancellationToken cancellationToken)
    {
        if (_match is not { } match)
        {
            return [];
        }

        var items = await Hosting.WorkItemsAsync(match, assignedToMe: false, query.Length == 0 ? null : query, 20, cancellationToken).ConfigureAwait(true);
        return [.. items.Select(i => new CommitCompletion(i.Reference, i.Title, i.Type))];
    }

    // ------------------------------------------------------------------ review comments

    public IReadOnlyList<EditorReviewThread> ReviewThreadsFor(string path) => ReviewThreads.For(path);

    /// <summary>The threads at a line, in a flyout: who said what, with links to the host.</summary>
    public void ShowReviewThreads(IReadOnlyList<EditorReviewThread> threads, Control anchor)
    {
        if (threads.Count == 0)
        {
            return;
        }

        var panel = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(4), MaxWidth = 520 };
        panel.Children.Add(new TextBlock { Text = $"{threads[0].PullRequestReference} {threads[0].PullRequestTitle}", FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        foreach (var thread in threads)
        {
            foreach (var comment in thread.Comments)
            {
                var item = new StackPanel { Spacing = 2 };
                item.Children.Add(new TextBlock { Text = $"{comment.Author} · {comment.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}", FontWeight = Avalonia.Media.FontWeight.SemiBold });
                item.Children.Add(MarkdownRenderer.Render(comment.Body, OpenUrl));
                panel.Children.Add(item);
            }

            var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (thread.Thread.Url is { } url)
            {
                var open = new Button { Content = "Open in Browser", Classes = { "link" } };
                open.Click += (_, _) => OpenUrl(url);
                links.Children.Add(open);
            }

            var show = new Button { Content = "Show in Pull Requests", Classes = { "link" } };
            show.Click += (_, _) => Layout.ShowToolWindow(PullRequestsViewModel.ToolWindowId, focus: true);
            links.Children.Add(show);
            panel.Children.Add(links);
        }

        var flyout = new Flyout { Content = new ScrollViewer { Content = panel, MaxHeight = 420 }, Placement = PlacementMode.Pointer };
        flyout.ShowAt(anchor, showAtPointer: true);
    }

    // ------------------------------------------------------------------ publishing

    /// <summary>
    /// Publishes the repository (creating it first when the solution has none): the provider creates a repository, the
    /// shell adds it as a remote and pushes the branch.
    /// </summary>
    public async Task PublishAsync()
    {
        var providers = Hosting.Providers;
        if (providers.Count == 0)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.hosting.noHostingPluginIsInstalled", "No hosting plugin is installed."), TimeSpan.FromSeconds(5));
            return;
        }

        var picked = await Palette.ShowAsync(
            [.. providers.Select(p => new QuickPickItem(p.Label)
            {
                Tag = p,
                Detail = Hosting.AccountFor(p.Id) is { } account ? "signed in as " + account.Name : "sign in first",
            })],
            "Publish the repository to").ConfigureAwait(true);
        if (picked?.Tag is not HostingProviderInfo provider)
        {
            return;
        }

        if (Hosting.AccountFor(provider.Id) is null)
        {
            ShowAccounts(provider.Id);
            StatusBar.ShowMessage(Localizer.Format("msg.hosting.signInToThenPublishAgain", "Sign in to {0}, then publish again.", provider.Label), TimeSpan.FromSeconds(6));
            return;
        }

        if (!await EnsureRepositoryWithCommitAsync().ConfigureAwait(true))
        {
            return;
        }

        try
        {
            var owners = await Hosting.OwnersAsync(provider.Id, CancellationToken.None).ConfigureAwait(true);
            if (await Palette.ShowAsync([.. owners.Select(o => new QuickPickItem(o.Name) { Tag = o, Detail = o.Kind })], $"Owner of the new {provider.Label} repository").ConfigureAwait(true) is not { Tag: HostingOwnerDto owner })
            {
                return;
            }

            var suggestedName = Path.GetFileName(Git.Root!.TrimEnd(Path.DirectorySeparatorChar));
            var name = await Dialogs.ShowAsync(new DialogRequest("Publish Repository", $"The new repository's name on {provider.Label}:", ["Next", "Cancel"])
            {
                Input = suggestedName,
                Validate = n => string.IsNullOrWhiteSpace(n) ? "Enter a name." : n.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or ':') ? "No spaces or slashes." : null,
            }).ConfigureAwait(true);
            if (name is not { Button: "Next", Input: { } repositoryName })
            {
                return;
            }

            var visibility = await Palette.ShowAsync([new("Private") { Tag = true, Detail = "only people you add" }, new("Public") { Tag = false, Detail = "anyone" }], "Visibility").ConfigureAwait(true);
            if (visibility?.Tag is not bool isPrivate)
            {
                return;
            }

            var description = await Dialogs.ShowAsync(new DialogRequest("Publish Repository", "A description (optional):", ["Publish", "Cancel"]) { Input = string.Empty }).ConfigureAwait(true);
            if (description.Button != "Publish")
            {
                return;
            }

            StatusBar.ShowMessage(Localizer.Format("msg.hosting.publishingTo", "Publishing to {0}…", provider.Label));
            var published = await Hosting.PublishAsync(provider.Id, owner.Id, repositoryName.Trim(), description.Input is { Length: > 0 } d ? d.Trim() : null, isPrivate, CancellationToken.None).ConfigureAwait(true);
            var remote = await Git.FreeRemoteNameAsync("origin", provider.Id).ConfigureAwait(true);
            await Git.AddRemoteAsync(remote, published.CloneUrl).ConfigureAwait(true);
            await Git.PublishBranchAsync(remote, Git.Status?.Branch ?? "main").ConfigureAwait(true);
            var notification = Workbench.Notifications.Info(Localizer.Format("msg.hosting.publishedToTheRemote", "Published to {0} (the remote '{1}').", published.WebUrl, remote), "Open in Browser");
            _ = notification.Result.ContinueWith(t =>
            {
                if (t.Result == "Open in Browser")
                {
                    Ui.Post(() => OpenUrl(published.WebUrl));
                }
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            await RefreshHostingAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is GitException or HostingRequestException or HostingSignInRequiredException or InvalidOperationException)
        {
            ReportGitError(ex);
        }
        finally
        {
            StatusBar.ClearMessage();
        }
    }

    /// <summary>A repository with a commit to push: creates one for the solution (with a first commit) when needed.</summary>
    private async Task<bool> EnsureRepositoryWithCommitAsync()
    {
        try
        {
            if (Git.Root is null)
            {
                if (Solutions.Solution is not { } solution)
                {
                    StatusBar.ShowMessage(Localizer.Get("msg.hosting.openASolutionFirst", "Open a solution first."), TimeSpan.FromSeconds(4));
                    return false;
                }

                if (!await Dialogs.ConfirmAsync(Localizer.Get("msg.hosting.publishRepository", "Publish Repository"), Localizer.Get("msg.hosting.theSolutionIsntInAGit", "The solution isn't in a git repository. Create one with a first commit of its files, then publish it?"), Localizer.Get("msg.hosting.createAndPublish", "Create and Publish")).ConfigureAwait(true))
                {
                    return false;
                }

                var folder = Path.GetDirectoryName(solution.Path)!;
                await Git.InitAsync(folder, CancellationToken.None).ConfigureAwait(true);
                await OpenRepositoryAsync(folder).ConfigureAwait(true);
            }

            if (Git.Status is { Head: null })
            {
                await Git.StageAsync([Git.Root!]).ConfigureAwait(true);
                await Git.CommitAsync("Initial commit", amend: false, all: false).ConfigureAwait(true);
            }

            return Git.Root is not null;
        }
        catch (GitException ex)
        {
            ReportGitError(ex);
            return false;
        }
    }

    // ------------------------------------------------------------------ askpass

    private void InitializeAskpass()
    {
        if (AskpassProgram() is not { } program)
        {
            Workbench.Log.AppendLine("[askpass] The daedalus executable wasn't found: git asks only the credential helpers.");
            return;
        }

        Askpass = new AskpassServer(program, AnswerAskpassAsync);
        Git.AskpassEnvironment = Askpass.Environment;
    }

    /// <summary>The <c>daedalus</c> executable git and ssh run as askpass: this process, or the one next to the shell's files.</summary>
    internal static string? AskpassProgram()
    {
        var name = OperatingSystem.IsWindows() ? "daedalus.exe" : "daedalus";
        if (Environment.ProcessPath is { } process && string.Equals(Path.GetFileName(process), name, StringComparison.OrdinalIgnoreCase))
        {
            return process;
        }

        var next = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(next) ? next : null;
    }

    /// <summary>Asks the user what git or ssh asked (masked for passwords and passphrases); null cancels. Nothing is kept.</summary>
    private Task<string?> AnswerAskpassAsync(AskpassPrompt prompt, CancellationToken cancellationToken) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            Window?.BringToFront();
            GitLog.AppendLine("[askpass] " + prompt.Text.Trim().ReplaceLineEndings(" "));
            var request = prompt.IsConfirmation
                ? new DialogRequest(prompt.Title, prompt.Text.Trim(), ["Yes", "No"]) { DefaultButton = "No" }
                : new DialogRequest(prompt.Title, prompt.Text.Trim() + "\n\nThis is passed to git once and not stored; a credential helper (such as Git Credential Manager) may store it.", ["OK", "Cancel"])
                {
                    Input = string.Empty,
                    IsPassword = prompt.IsSecret,
                };
            var answer = await Dialogs.ShowAsync(request).ConfigureAwait(true);
            return prompt.IsConfirmation
                ? answer.Button == "Yes" ? "yes" : null
                : answer.Button == "OK" ? answer.Input ?? string.Empty : null;
        });

    // ------------------------------------------------------------------ several repositories

    private void ScheduleRepositoryDiscovery()
    {
        _repositoryTimer ??= CreateRepositoryTimer();
        _repositoryTimer.Stop();
        _repositoryTimer.Start();
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

    /// <summary>Finds the repositories of the solution's folder and its projects' folders (a solution can span several).</summary>
    public async Task DiscoverRepositoriesAsync()
    {
        if (Solutions.Solution is not { } solution)
        {
            Git.Repositories = [];
            GitChanges.SetRepositories([], null);
            Workbench.Context.Set("gitRepositories", false);
            return;
        }

        try
        {
            var folders = solution.Projects.Select(p => Path.GetDirectoryName(p.Path)).OfType<string>().Prepend(Path.GetDirectoryName(solution.Path)!).ToList();
            var roots = await Git.FindRepositoriesAsync(folders).ConfigureAwait(true);
            Git.Repositories = roots;
            GitChanges.SetRepositories(roots, Git.Root);
            Workbench.Context.Set("gitRepositories", roots.Count > 1);
        }
        catch (Exception ex) when (ex is GitException or IOException)
        {
            GitLog.AppendLine("[repositories] " + ex.Message);
        }
    }

    /// <summary>Shows another of the solution's repositories in Git Changes, the history and the status bar.</summary>
    public async Task SwitchRepositoryAsync(string root)
    {
        if (Git.Root is { } current && DaedalusStudio.Workbench.Projects.SolutionService.PathComparer.Equals(current, root))
        {
            return;
        }

        await OpenRepositoryAsync(root).ConfigureAwait(true);
        GitChanges.SetRepositories(Git.Repositories, Git.Root);
        StatusBar.ShowMessage("Repository: " + Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)), TimeSpan.FromSeconds(4));
    }

    public async Task PickRepositoryAsync()
    {
        if (Git.Repositories.Count < 2)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.hosting.theSolutionIsInASingle", "The solution is in a single repository."), TimeSpan.FromSeconds(4));
            return;
        }

        if (await Palette.ShowAsync([.. Git.Repositories.Select(r => new QuickPickItem(Path.GetFileName(r.TrimEnd(Path.DirectorySeparatorChar))) { Detail = r, Tag = r })], "Switch to a repository of the solution").ConfigureAwait(true) is { Tag: string root })
        {
            await SwitchRepositoryAsync(root).ConfigureAwait(true);
        }
    }

    // ------------------------------------------------------------------ worktrees

    private async Task WorktreeActionAsync(string action, WorktreeRow? row)
    {
        if (Git.Root is null)
        {
            return;
        }

        try
        {
            switch (action)
            {
                case "add":
                    await AddWorktreeAsync().ConfigureAwait(true);
                    break;
                case "open" when row is not null:
                    await OpenFolderAsync(row.Worktree.Path).ConfigureAwait(true);
                    break;
                case "openNewWindow" when row is not null:
                    StartNewWindow(row.Worktree.Path);
                    break;
                case "remove" when row is not null:
                    await RemoveWorktreeAsync(row).ConfigureAwait(true);
                    break;
                case "lock" when row is not null:
                    var reason = await Dialogs.ShowAsync(new DialogRequest("Lock Worktree", $"Why {row.Name} is locked (optional): git won't prune or remove it.", ["Lock", "Cancel"]) { Input = string.Empty }).ConfigureAwait(true);
                    if (reason.Button == "Lock")
                    {
                        await Git.LockWorktreeAsync(row.Worktree.Path, reason.Input?.Trim()).ConfigureAwait(true);
                    }

                    break;
                case "unlock" when row is not null:
                    await Git.UnlockWorktreeAsync(row.Worktree.Path).ConfigureAwait(true);
                    break;
                case "prune":
                    await Git.PruneWorktreesAsync().ConfigureAwait(true);
                    break;
            }
        }
        catch (Exception ex) when (ex is GitException or IOException or InvalidOperationException)
        {
            ReportGitError(ex);
        }
    }

    /// <summary>Adds a worktree for a new branch or an existing one, next to the repository's folder.</summary>
    public async Task AddWorktreeAsync()
    {
        var root = Git.Root!;
        var worktrees = await Git.WorktreesAsync().ConfigureAwait(true);
        var checkedOut = worktrees.Select(w => w.Branch).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var items = new List<QuickPickItem> { new("+ New Branch…") { Tag = "new" } };
        items.AddRange((await Git.BranchesAsync().ConfigureAwait(true))
            .Where(b => !b.IsRemote && !checkedOut.Contains(b.Name))
            .Select(b => new QuickPickItem(b.Name) { Tag = b.Name, Detail = "branch" }));
        var picked = await Palette.ShowAsync(items, "A worktree for").ConfigureAwait(true);
        string? newBranch = null, existing = null;
        switch (picked?.Tag)
        {
            case "new":
                newBranch = await AskNameAsync("Add Worktree", $"A new branch from {Git.Status?.Branch ?? "HEAD"} for the worktree:", "Next").ConfigureAwait(true);
                if (newBranch is null)
                {
                    return;
                }

                break;
            case string name:
                existing = name;
                break;
            default:
                return;
        }

        var branch = newBranch ?? existing!;
        var parent = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar))!;
        var suggested = Path.Combine(parent, $"{Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar))}-{branch.Replace('/', '-')}");
        var location = await Dialogs.ShowAsync(new DialogRequest("Add Worktree", $"The folder for {branch}:", ["Add", "Cancel"])
        {
            Input = suggested,
            Validate = path => string.IsNullOrWhiteSpace(path) ? "Enter a folder." : Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any() ? "That folder isn't empty." : null,
        }).ConfigureAwait(true);
        if (location is not { Button: "Add", Input: { } folder })
        {
            return;
        }

        await GitActionAsync(() => Git.AddWorktreeAsync(folder.Trim(), newBranch, existing), "Adding the worktree…").ConfigureAwait(true);
        Layout.ShowToolWindow(WorktreesViewModel.ToolWindowId, focus: false);
        await Worktrees.ReloadAsync().ConfigureAwait(true);
    }

    private async Task RemoveWorktreeAsync(WorktreeRow row)
    {
        if (row.Worktree.IsMain || row.IsCurrent)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.hosting.theMainWorktreeOrTheOne", "The main worktree, or the one open here, can't be removed."), TimeSpan.FromSeconds(5));
            return;
        }

        if (!await Dialogs.ConfirmAsync(Localizer.Get("msg.hosting.removeWorktree", "Remove Worktree"), Localizer.Format("msg.hosting.removeTheWorktreeItsBranchStays", "Remove the worktree {0} ({1})? Its branch stays.", row.Name, row.Location), Localizer.Get("msg.hosting.remove", "Remove"), danger: true).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            await Git.RemoveWorktreeAsync(row.Worktree.Path, force: false).ConfigureAwait(true);
        }
        catch (GitException ex)
        {
            if (await Dialogs.ConfirmAsync(Localizer.Get("msg.hosting.removeWorktree", "Remove Worktree"), Localizer.Format("msg.hosting.nNremoveItAnywayItsUncommitted", "{0}\n\nRemove it anyway? Its uncommitted changes are lost.", ex.Message), Localizer.Get("msg.hosting.removeAnyway", "Remove Anyway"), danger: true).ConfigureAwait(true))
            {
                await Git.RemoveWorktreeAsync(row.Worktree.Path, force: true).ConfigureAwait(true);
            }
        }
    }

    /// <summary>Starts another <c>daedalus</c> on a folder (Open in New Window), with the same user-data folder.</summary>
    private void StartNewWindow(string folder)
    {
        if (Environment.ProcessPath is not { } program)
        {
            return;
        }

        var start = new ProcessStartInfo(program) { UseShellExecute = false };
        start.ArgumentList.Add(folder);
        if (Options.UserDataDir is { } userData)
        {
            start.ArgumentList.Add("--user-data-dir");
            start.ArgumentList.Add(userData);
        }

        try
        {
            using var process = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Workbench.Notifications.Error("Could not start a new window: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ Git LFS

    /// <summary>The repository's LFS files and (at most once a minute) its locks, for Git Changes.</summary>
    private async Task RefreshLfsAsync()
    {
        if (Git.Root is not { } root)
        {
            _lfsRoot = null;
            GitChanges.Lfs = GitLfsStatus.None;
            Workbench.Context.Set("gitLfs", false);
            return;
        }

        if (_lfsRoot == root && !_lfsUsed)
        {
            return; // no LFS in this repository (checked once per repository)
        }

        var includeLocks = _lfsRoot != root || DateTime.UtcNow - _locksRead > TimeSpan.FromMinutes(1);
        try
        {
            var lfs = await Git.LfsStatusAsync(includeLocks).ConfigureAwait(true);
            if (Git.Root != root)
            {
                return;
            }

            if (includeLocks)
            {
                _locksRead = DateTime.UtcNow;
            }
            else
            {
                lfs = lfs with { Locks = GitChanges.Lfs.Locks, LocksError = GitChanges.Lfs.LocksError };
            }

            _lfsRoot = root;
            _lfsUsed = lfs.IsUsed;
            GitChanges.Lfs = lfs;
            Workbench.Context.Set("gitLfs", lfs.IsUsed);
        }
        catch (Exception ex) when (ex is GitException or IOException or InvalidOperationException)
        {
            GitLog.AppendLine("[lfs] " + ex.Message);
        }
    }

    /// <summary>Git LFS locks: unlock one (force for someone else's).</summary>
    public Task ShowLfsLocksAsync() => GitActionAsync(async () =>
    {
        _locksRead = DateTime.MinValue;
        await RefreshLfsAsync().ConfigureAwait(true);
        var lfs = GitChanges.Lfs;
        if (lfs.LocksError is { } error)
        {
            StatusBar.ShowMessage("Git LFS locks: " + error, TimeSpan.FromSeconds(6));
            return;
        }

        if (lfs.Locks.Count == 0)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.hosting.noGitLfsLocks", "No Git LFS locks."), TimeSpan.FromSeconds(4));
            return;
        }

        if (await Palette.ShowAsync([.. lfs.Locks.Select(l => new QuickPickItem(l.RelativePath) { Tag = l, Detail = "locked by " + l.Owner })], "Git LFS locks (pick one to unlock)").ConfigureAwait(true) is { Tag: GitLfsLock picked })
        {
            var force = await Dialogs.ShowAsync(new DialogRequest("Unlock", $"Unlock {picked.RelativePath}, locked by {picked.Owner}?", ["Unlock", "Force Unlock", "Cancel"]) { DefaultButton = "Cancel" }).ConfigureAwait(true);
            if (force.Button is "Unlock" or "Force Unlock")
            {
                await Git.UnlockFileAsync(picked.Path, force.Button == "Force Unlock").ConfigureAwait(true);
                _locksRead = DateTime.MinValue;
                await RefreshLfsAsync().ConfigureAwait(true);
            }
        }
    });

    /// <summary>Locks or unlocks the active file (or the Solution Explorer's selection) in Git LFS.</summary>
    public Task LockActiveFileAsync(bool unlock) =>
        (ActiveEditor?.Model.Path ?? Explorer.Selected?.Node.Path) is { } path
            ? GitActionAsync(async () =>
            {
                if (unlock)
                {
                    await Git.UnlockFileAsync(path, force: false).ConfigureAwait(true);
                }
                else
                {
                    await Git.LockFileAsync(path).ConfigureAwait(true);
                }

                _locksRead = DateTime.MinValue;
                await RefreshLfsAsync().ConfigureAwait(true);
                StatusBar.ShowMessage($"{(unlock ? "Unlocked" : "Locked")} {Path.GetFileName(path)}.", TimeSpan.FromSeconds(4));
            })
            : Task.CompletedTask;

    public Task LfsPullAsync() => GitActionAsync(() => Git.LfsPullAsync(), "Downloading Git LFS files…");
}
