using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Codalus.Plugins.Protocol;
using Codalus.Shell.Docking;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Git;
using Codalus.Workbench.Hosting;

namespace Codalus.Shell.ViewModels;

/// <summary>What the hosting windows ask of the shell (ADR 0031).</summary>
public interface IHostingActions
{
    /// <summary>The open repository on a host the user is signed in to.</summary>
    HostingMatch? Match { get; }

    /// <summary>A provider for the repository's remotes that the user isn't signed in to (Sign In offers it).</summary>
    HostingProviderInfo? Suggested { get; }

    bool HasRepository { get; }

    string? CurrentBranch { get; }

    Task<IReadOnlyList<PullRequestDto>> PullRequestsAsync(string filter, CancellationToken cancellationToken);

    Task<PullRequestDetailsDto> PullRequestAsync(string number, CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkItemDto>> WorkItemsAsync(bool assignedToMe, string? text, CancellationToken cancellationToken);

    Task CheckOutAsync(PullRequestDto pullRequest);

    void CreatePullRequest();

    /// <summary>Opens the Accounts page and starts signing in to a provider (null: the suggested one).</summary>
    void SignIn(string? providerId);

    void OpenUrl(string url);

    /// <summary>Opens a repository file at a one-based line (a review thread).</summary>
    Task OpenFileAsync(string relativePath, int line);

    /// <summary>Adds a reference (<c>#12</c>) to the commit message.</summary>
    void Mention(string reference);
}

// ------------------------------------------------------------------ accounts

/// <summary>A provider on the Accounts page: signed in or not, and the sign-in in progress (its device code).</summary>
public sealed class AccountProviderRow : ObservableObject
{
    private string? _account;
    private string? _host;
    private bool _isSigningIn;
    private string? _userCode;
    private string? _verificationUri;
    private string _token = string.Empty;
    private string? _error;

    public AccountProviderRow(string id, string label, Func<AccountProviderRow, string?, Task> signIn, Func<AccountProviderRow, Task> signOut, Action<AccountProviderRow> cancel, Action<AccountProviderRow> copyAndOpen)
    {
        Id = id;
        Label = label;
        SignInCommand = new RelayCommand(() => _ = signIn(this, null));
        SignInWithTokenCommand = new RelayCommand(() => _ = signIn(this, Token));
        SignOutCommand = new RelayCommand(() => _ = signOut(this));
        CancelCommand = new RelayCommand(() => cancel(this));
        CopyAndOpenCommand = new RelayCommand(() => copyAndOpen(this));
    }

    public string Id { get; }

    public string Label { get; }

    public string? ServerHint { get; set; }

    public bool HasServerHint => ServerHint is { Length: > 0 };

    public bool IsSignedIn => _account is not null;

    public bool CanSignIn => !IsSignedIn && !IsSigningIn;

    public string StatusText => IsSignedIn ? $"Signed in as {_account} on {_host}" : IsSigningIn ? "Signing in…" : "Not signed in";

    public string? Account => _account;

    public void SetAccount(string? name, string? host)
    {
        _account = name;
        _host = host;
        Notify();
    }

    public bool IsSigningIn
    {
        get => _isSigningIn;
        set
        {
            if (SetField(ref _isSigningIn, value))
            {
                if (!value)
                {
                    _userCode = null;
                    _verificationUri = null;
                }

                Notify();
            }
        }
    }

    public string? UserCode => _userCode;

    public string? VerificationUri => _verificationUri;

    public bool HasCode => _userCode is not null;

    public void ShowCode(string code, string uri)
    {
        _userCode = code;
        _verificationUri = uri;
        Notify();
    }

    /// <summary>A personal access token the user pasted (never stored here: the provider keeps it in the credential store).</summary>
    public string Token
    {
        get => _token;
        set => SetField(ref _token, value ?? string.Empty);
    }

    public string? Error
    {
        get => _error;
        set
        {
            if (SetField(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => _error is not null;

    public string AccessibleName => $"{Label}: {StatusText}";

    public ICommand SignInCommand { get; }

    public ICommand SignInWithTokenCommand { get; }

    public ICommand SignOutCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand CopyAndOpenCommand { get; }

    /// <summary>The cancellation of the sign-in in progress.</summary>
    internal CancellationTokenSource? SignInCancellation { get; set; }

    private void Notify()
    {
        foreach (var name in new[] { nameof(IsSignedIn), nameof(CanSignIn), nameof(StatusText), nameof(Account), nameof(UserCode), nameof(VerificationUri), nameof(HasCode), nameof(AccessibleName) })
        {
            OnPropertyChanged(name);
        }
    }
}

/// <summary>The Accounts page: sign in to GitHub, Azure DevOps and GitLab (ADR 0031).</summary>
public sealed class AccountsViewModel : ObservableObject
{
    private string _storeText = string.Empty;
    private bool _isEnabled = true;

    public ObservableCollection<AccountProviderRow> Providers { get; } = [];

    /// <summary>Where tokens are kept.</summary>
    public string StoreText
    {
        get => _storeText;
        set => SetField(ref _storeText, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetField(ref _isEnabled, value))
            {
                OnPropertyChanged(nameof(IsDisabled));
            }
        }
    }

    public bool IsDisabled => !_isEnabled;

    public AccountProviderRow? Row(string providerId) => Providers.FirstOrDefault(p => p.Id == providerId);

    /// <summary>Plugins' authentication providers' sessions (plugin API 1.0).</summary>
    public ObservableCollection<AuthenticationSessionRow> Sessions { get; } = [];

    public bool HasSessions => Sessions.Count > 0;

    public void SetSessions(IEnumerable<AuthenticationSessionRow> sessions)
    {
        Sessions.Clear();
        foreach (var session in sessions)
        {
            Sessions.Add(session);
        }

        OnPropertyChanged(nameof(HasSessions));
    }
}

/// <summary>A session of a plugin's authentication provider, with Sign Out.</summary>
public sealed class AuthenticationSessionRow(string provider, string account, IReadOnlyList<string> scopes, Action signOut)
{
    public string Provider { get; } = provider;

    public string Account { get; } = account;

    public string Detail => scopes.Count == 0 ? Provider : $"{Provider} · {string.Join(", ", scopes)}";

    public RelayCommand SignOutCommand { get; } = new(signOut);

    public string AccessibleName => $"{Account}, {Provider}";
}

public sealed class AccountsDocument : ShellDocument
{
    public const string DocumentId = "hosting.accounts";

    public AccountsDocument(AccountsViewModel viewModel)
    {
        ViewModel = viewModel;
        Id = DocumentId;
        Title = "Git Hosting Accounts";
    }

    public override string Kind => "hostingAccounts";

    public AccountsViewModel ViewModel { get; }
}

// ------------------------------------------------------------------ pull requests

public sealed class PullRequestRow(PullRequestDto pullRequest, bool isCurrent)
{
    public PullRequestDto PullRequest { get; } = pullRequest;

    public string Reference => PullRequest.Reference;

    public string Title => PullRequest.Title;

    public string Detail => $"{PullRequest.Author} · {PullRequest.SourceBranch} → {PullRequest.TargetBranch}{(PullRequest.IsDraft ? " · draft" : string.Empty)}";

    public bool IsCurrent { get; } = isCurrent;

    public string AccessibleName => $"{Reference} {Title}, by {PullRequest.Author}{(IsCurrent ? ", the current branch" : string.Empty)}";
}

public sealed class ReviewCommentRow(ReviewCommentDto comment)
{
    public string Author { get; } = comment.Author;

    public string Body { get; } = comment.Body;

    public string When { get; } = comment.CreatedAt == DateTimeOffset.MinValue ? string.Empty : comment.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}

public sealed class ReviewThreadRow(ReviewThreadDto thread)
{
    public ReviewThreadDto Thread { get; } = thread;

    public string Location => $"{Thread.Path}:{Thread.Line.ToString(CultureInfo.InvariantCulture)}";

    public string State => Thread.IsResolved ? "resolved" : Thread.IsOutdated ? "outdated" : string.Empty;

    public bool HasState => State.Length > 0;

    public IReadOnlyList<ReviewCommentRow> Comments { get; } = [.. thread.Comments.Select(c => new ReviewCommentRow(c))];

    public string AccessibleName => $"Review thread on {Location}{(State.Length > 0 ? ", " + State : string.Empty)}, {Comments.Count} comment(s)";
}

/// <summary>The Pull Requests window (ADR 0031): the repository's open pull requests, and one's details.</summary>
public sealed class PullRequestsViewModel : ObservableObject, IDisposable
{
    public const string ToolWindowId = "git.pullRequests";

    private readonly IHostingActions _actions;
    private CancellationTokenSource? _listCts;
    private CancellationTokenSource? _detailsCts;
    private int _filterIndex;
    private PullRequestRow? _selected;
    private string? _message;
    private bool _isLoading;
    private string _repositoryText = string.Empty;
    private string? _signInLabel;
    private PullRequestDetailsDto? _details;

    public PullRequestsViewModel(IHostingActions actions)
    {
        _actions = actions;
        RefreshCommand = new RelayCommand(() => _ = ReloadAsync());
        CheckOutCommand = new RelayCommand(() =>
        {
            if (_selected is { } row)
            {
                _ = _actions.CheckOutAsync(row.PullRequest);
            }
        });
        OpenInBrowserCommand = new RelayCommand(() =>
        {
            if (_selected is { } row)
            {
                _actions.OpenUrl(row.PullRequest.WebUrl);
            }
        });
        CreateCommand = new RelayCommand(() => _actions.CreatePullRequest());
        SignInCommand = new RelayCommand(() => _actions.SignIn(_actions.Suggested?.Id));
    }

    public static IReadOnlyList<string> Filters { get; } = ["All open", "Mine", "Review requested"];

    public int FilterIndex
    {
        get => _filterIndex;
        set
        {
            if (SetField(ref _filterIndex, value))
            {
                _ = ReloadAsync();
            }
        }
    }

    public ObservableCollection<PullRequestRow> Rows { get; } = [];

    public PullRequestRow? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                _ = LoadDetailsAsync();
            }
        }
    }

    public bool HasSelection => _selected is not null;

    public string RepositoryText
    {
        get => _repositoryText;
        private set => SetField(ref _repositoryText, value);
    }

    public string? Message
    {
        get => _message;
        private set
        {
            if (SetField(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => _message is not null;

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetField(ref _isLoading, value);
    }

    /// <summary>"Sign in to GitHub" when the repository's host has a provider the user isn't signed in to.</summary>
    public string? SignInLabel
    {
        get => _signInLabel;
        private set
        {
            if (SetField(ref _signInLabel, value))
            {
                OnPropertyChanged(nameof(CanSignIn));
            }
        }
    }

    public bool CanSignIn => _signInLabel is not null;

    public bool CanCreate => _actions.Match is not null;

    // ------------------------------------------------------------------ details

    public PullRequestDetailsDto? Details
    {
        get => _details;
        private set
        {
            if (SetField(ref _details, value))
            {
                OnPropertyChanged(nameof(HasDetails));
                OnPropertyChanged(nameof(DetailsTitle));
                OnPropertyChanged(nameof(DetailsMeta));
                OnPropertyChanged(nameof(Description));
                DescriptionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool HasDetails => _details is not null;

    public string DetailsTitle => _details is { } d ? $"{d.PullRequest.Reference} {d.PullRequest.Title}" : string.Empty;

    public string DetailsMeta => _details is { PullRequest: var p }
        ? $"{p.Author} wants to merge {p.SourceBranch} into {p.TargetBranch}{(p.IsDraft ? " (draft)" : string.Empty)}{(p.Reviewers.Length > 0 ? " · reviewers: " + string.Join(", ", p.Reviewers) : string.Empty)}"
        : string.Empty;

    public string Description => _details?.Description is { Length: > 0 } text ? text : "*No description.*";

    public ObservableCollection<ReviewThreadRow> Threads { get; } = [];

    public ObservableCollection<ReviewCommentRow> Comments { get; } = [];

    public bool HasThreads => Threads.Count > 0;

    public bool HasComments => Comments.Count > 0;

    /// <summary>The description changed (the view renders its Markdown).</summary>
    public event EventHandler? DescriptionChanged;

    public ICommand RefreshCommand { get; }

    public ICommand CheckOutCommand { get; }

    public ICommand OpenInBrowserCommand { get; }

    public ICommand CreateCommand { get; }

    public ICommand SignInCommand { get; }

    public void OpenThread(ReviewThreadRow row) => _ = _actions.OpenFileAsync(row.Thread.Path, row.Thread.Line);

    public void OpenLink(string url) => _actions.OpenUrl(url);

    /// <summary>Reads the pull requests again (the repository, the account or the filter changed).</summary>
    public async Task ReloadAsync()
    {
        _listCts?.Cancel();
        var cts = _listCts = new CancellationTokenSource();
        OnPropertyChanged(nameof(CanCreate));
        if (_actions.Match is not { } match)
        {
            Rows.Clear();
            Selected = null;
            RepositoryText = string.Empty;
            SignInLabel = _actions.Suggested is { } suggested ? "Sign in to " + suggested.Label : null;
            Message = !_actions.HasRepository ? "The open solution is not in a git repository."
                : _actions.Suggested is { } provider ? $"Sign in to {provider.Label} to see this repository's pull requests."
                : "This repository's remotes aren't on GitHub, Azure DevOps or GitLab, or you aren't signed in to their server (Git → Git Hosting Accounts).";
            return;
        }

        SignInLabel = null;
        RepositoryText = $"{match.Repository.Owner}/{match.Repository.Name} on {match.Provider.Label}";
        IsLoading = true;
        try
        {
            var pullRequests = await _actions.PullRequestsAsync(FilterIndex switch { 1 => "mine", 2 => "reviewRequested", _ => "all" }, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            var selected = _selected?.PullRequest.Number;
            Rows.Clear();
            var branch = _actions.CurrentBranch;
            foreach (var pullRequest in pullRequests)
            {
                Rows.Add(new PullRequestRow(pullRequest, pullRequest.SourceBranch == branch));
            }

            Message = Rows.Count == 0 ? (FilterIndex == 0 ? "No open pull requests." : "None match the filter.") : null;
            Selected = Rows.FirstOrDefault(r => r.PullRequest.Number == selected);
        }
        catch (OperationCanceledException)
        {
        }
        catch (HostingSignInRequiredException ex)
        {
            Rows.Clear();
            Message = ex.Message + " Sign in again.";
            SignInLabel = "Sign in to " + match.Provider.Label;
        }
        catch (HostingRequestException ex)
        {
            Rows.Clear();
            Message = ex.Message;
        }
        finally
        {
            if (_listCts == cts)
            {
                IsLoading = false;
            }
        }
    }

    private async Task LoadDetailsAsync()
    {
        _detailsCts?.Cancel();
        var cts = _detailsCts = new CancellationTokenSource();
        Threads.Clear();
        Comments.Clear();
        if (_selected is not { } row)
        {
            Details = null;
            NotifyCollections();
            return;
        }

        try
        {
            var details = await _actions.PullRequestAsync(row.PullRequest.Number, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            Details = details;
            foreach (var thread in details.Threads)
            {
                Threads.Add(new ReviewThreadRow(thread));
            }

            foreach (var comment in details.Comments)
            {
                Comments.Add(new ReviewCommentRow(comment));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is HostingRequestException or HostingSignInRequiredException)
        {
            Message = ex.Message;
        }

        NotifyCollections();
    }

    private void NotifyCollections()
    {
        OnPropertyChanged(nameof(HasThreads));
        OnPropertyChanged(nameof(HasComments));
    }

    public void Dispose()
    {
        _listCts?.Cancel();
        _listCts?.Dispose();
        _detailsCts?.Cancel();
        _detailsCts?.Dispose();
    }
}

/// <summary>Create Pull Request (a document): title, description, the base branch and draft.</summary>
public sealed class CreatePullRequestViewModel : ObservableObject
{
    private readonly Func<CreatePullRequestViewModel, Task> _create;
    private string _title = string.Empty;
    private string _description = string.Empty;
    private string? _target;
    private bool _isDraft;
    private bool _isBusy;
    private string? _error;
    private string? _created;

    public CreatePullRequestViewModel(string providerLabel, string repository, string sourceBranch, IReadOnlyList<string> targets, string? defaultTarget, Func<CreatePullRequestViewModel, Task> create)
    {
        ProviderLabel = providerLabel;
        Repository = repository;
        SourceBranch = sourceBranch;
        Targets = targets;
        _target = defaultTarget ?? (targets.Count > 0 ? targets[0] : null);
        _create = create;
        _title = TitleFromBranch(sourceBranch);
        CreateCommand = new RelayCommand(() => _ = CreateAsync());
        WriteWithAiCommand = new RelayCommand(() => _ = WriteWithAi?.Invoke(this));
    }

    public string ProviderLabel { get; }

    public string Repository { get; }

    public string SourceBranch { get; }

    public IReadOnlyList<string> Targets { get; }

    public string Header => $"Create a pull request on {ProviderLabel}: {Repository}";

    /// <summary>Writes the title and description with AI (M28b), set by the shell.</summary>
    public Func<CreatePullRequestViewModel, Task>? WriteWithAi { get; set; }

    public ICommand WriteWithAiCommand { get; }

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value ?? string.Empty);
    }

    public string Description
    {
        get => _description;
        set => SetField(ref _description, value ?? string.Empty);
    }

    public string? Target
    {
        get => _target;
        set => SetField(ref _target, value);
    }

    public bool IsDraft
    {
        get => _isDraft;
        set => SetField(ref _isDraft, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetField(ref _isBusy, value);
    }

    public string? Error
    {
        get => _error;
        set
        {
            if (SetField(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => _error is not null;

    /// <summary>"Created #4" once done.</summary>
    public string? Created
    {
        get => _created;
        set
        {
            if (SetField(ref _created, value))
            {
                OnPropertyChanged(nameof(HasCreated));
            }
        }
    }

    public bool HasCreated => _created is not null;

    public ICommand CreateCommand { get; }

    /// <summary>A title from a branch name: <c>feature/add-login</c> → "Add login".</summary>
    public static string TitleFromBranch(string branch)
    {
        var name = branch[(branch.LastIndexOf('/') + 1)..].Replace('-', ' ').Replace('_', ' ').Trim();
        return name.Length == 0 ? branch : char.ToUpperInvariant(name[0]) + name[1..];
    }

    public async Task CreateAsync()
    {
        if (string.IsNullOrWhiteSpace(Title) || Target is null)
        {
            Error = "Enter a title and choose the branch to merge into.";
            return;
        }

        Error = null;
        IsBusy = true;
        try
        {
            await _create(this).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed class CreatePullRequestDocument : ShellDocument
{
    public const string DocumentId = "git.createPullRequest";

    public CreatePullRequestDocument(CreatePullRequestViewModel viewModel)
    {
        ViewModel = viewModel;
        Id = DocumentId;
        Title = "Create Pull Request";
    }

    public override string Kind => "createPullRequest";

    public CreatePullRequestViewModel ViewModel { get; }
}

// ------------------------------------------------------------------ work items

public sealed class WorkItemRow(WorkItemDto item)
{
    public WorkItemDto Item { get; } = item;

    public string Reference => Item.Reference;

    public string Title => Item.Title;

    public string Detail => string.Join(" · ", new[] { Item.Type, Item.State, Item.AssignedTo }.Where(s => s is { Length: > 0 }));

    public string AccessibleName => $"{Reference} {Title}, {Detail}";
}

/// <summary>The Work Items window (ADR 0031): issues or work items assigned to the user, or found by words.</summary>
public sealed class WorkItemsViewModel : ObservableObject, IDisposable
{
    public const string ToolWindowId = "git.workItems";

    private readonly IHostingActions _actions;
    private CancellationTokenSource? _cts;
    private string _search = string.Empty;
    private string? _message;
    private string? _signInLabel;
    private WorkItemRow? _selected;
    private readonly Avalonia.Threading.DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(350) };

    public WorkItemsViewModel(IHostingActions actions)
    {
        _actions = actions;
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = ReloadAsync();
        };
        RefreshCommand = new RelayCommand(() => _ = ReloadAsync());
        OpenCommand = new RelayCommand(() =>
        {
            if (_selected is { } row)
            {
                _actions.OpenUrl(row.Item.WebUrl);
            }
        });
        MentionCommand = new RelayCommand(() =>
        {
            if (_selected is { } row)
            {
                _actions.Mention(row.Reference);
            }
        });
        SignInCommand = new RelayCommand(() => _actions.SignIn(_actions.Suggested?.Id));
    }

    public ObservableCollection<WorkItemRow> Rows { get; } = [];

    public WorkItemRow? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
            }
        }
    }

    public bool HasSelection => _selected is not null;

    /// <summary>Empty: the ones assigned to the user; otherwise a number or words.</summary>
    public string Search
    {
        get => _search;
        set
        {
            if (SetField(ref _search, value ?? string.Empty))
            {
                _debounce.Stop();
                _debounce.Start();
            }
        }
    }

    public string Heading => _search.Length == 0 ? "Assigned to me" : "Search results";

    public string? Message
    {
        get => _message;
        private set
        {
            if (SetField(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => _message is not null;

    public string? SignInLabel
    {
        get => _signInLabel;
        private set
        {
            if (SetField(ref _signInLabel, value))
            {
                OnPropertyChanged(nameof(CanSignIn));
            }
        }
    }

    public bool CanSignIn => _signInLabel is not null;

    public ICommand RefreshCommand { get; }

    public ICommand OpenCommand { get; }

    public ICommand MentionCommand { get; }

    public ICommand SignInCommand { get; }

    public async Task ReloadAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        OnPropertyChanged(nameof(Heading));
        if (_actions.Match is null)
        {
            Rows.Clear();
            SignInLabel = _actions.Suggested is { } suggested ? "Sign in to " + suggested.Label : null;
            Message = _actions.Suggested is { } provider ? $"Sign in to {provider.Label} to see your issues and work items." : "This repository isn't on a host you're signed in to.";
            return;
        }

        SignInLabel = null;
        try
        {
            var items = await _actions.WorkItemsAsync(_search.Length == 0, _search.Length == 0 ? null : _search, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            Rows.Clear();
            foreach (var item in items)
            {
                Rows.Add(new WorkItemRow(item));
            }

            Message = Rows.Count == 0 ? (_search.Length == 0 ? "Nothing is assigned to you." : "Nothing matches.") : null;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is HostingRequestException or HostingSignInRequiredException)
        {
            Rows.Clear();
            Message = ex.Message;
        }
    }

    public void Dispose()
    {
        _debounce.Stop();
        _cts?.Cancel();
        _cts?.Dispose();
    }
}

// ------------------------------------------------------------------ worktrees

public sealed class WorktreeRow(GitWorktree worktree, bool isCurrent)
{
    public GitWorktree Worktree { get; } = worktree;

    public string Name => Path.GetFileName(Worktree.Path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : Worktree.Path;

    public string Location => Worktree.Path;

    public string Branch => Worktree.Branch ?? (Worktree.IsBare ? "(bare)" : $"(detached at {Worktree.Head?[..Math.Min(8, Worktree.Head.Length)]})");

    public string Flags => string.Join(" · ", new[]
    {
        IsCurrent ? "current" : null,
        Worktree.IsMain ? "main worktree" : null,
        Worktree.IsLocked ? "locked" + (Worktree.LockReason is { Length: > 0 } reason ? ": " + reason : string.Empty) : null,
        Worktree.IsPrunable ? "missing (prunable)" : null,
    }.OfType<string>());

    public bool IsCurrent { get; } = isCurrent;

    public string AccessibleName => $"Worktree {Name} on {Branch}, {Flags}";
}

/// <summary>The Worktrees window (ADR 0031): list, add, open, remove, lock and prune worktrees.</summary>
public sealed class WorktreesViewModel : ObservableObject
{
    public const string ToolWindowId = "git.worktrees";

    private readonly GitService _git;
    private readonly Func<string, WorktreeRow?, Task> _act;
    private WorktreeRow? _selected;
    private string? _message;

    /// <param name="act">Runs an action that may ask the user (<c>add</c>, <c>open</c>, <c>openNewWindow</c>, <c>remove</c>, <c>lock</c>, <c>unlock</c>, <c>prune</c>).</param>
    public WorktreesViewModel(GitService git, Func<string, WorktreeRow?, Task> act)
    {
        _git = git;
        _act = act;
        RefreshCommand = new RelayCommand(() => _ = ReloadAsync());
        AddCommand = new RelayCommand(() => _ = Act("add"));
        OpenCommand = new RelayCommand(() => _ = Act("open"));
        OpenNewWindowCommand = new RelayCommand(() => _ = Act("openNewWindow"));
        RemoveCommand = new RelayCommand(() => _ = Act("remove"));
        LockCommand = new RelayCommand(() => _ = Act(_selected?.Worktree.IsLocked == true ? "unlock" : "lock"));
        PruneCommand = new RelayCommand(() => _ = Act("prune"));
    }

    public ObservableCollection<WorktreeRow> Rows { get; } = [];

    public WorktreeRow? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(LockText));
            }
        }
    }

    public bool HasSelection => _selected is not null;

    public string LockText => _selected?.Worktree.IsLocked == true ? "Unlock" : "Lock";

    public string? Message
    {
        get => _message;
        private set
        {
            if (SetField(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => _message is not null;

    public ICommand RefreshCommand { get; }

    public ICommand AddCommand { get; }

    public ICommand OpenCommand { get; }

    public ICommand OpenNewWindowCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand LockCommand { get; }

    public ICommand PruneCommand { get; }

    private async Task Act(string action)
    {
        await _act(action, _selected).ConfigureAwait(true);
        await ReloadAsync().ConfigureAwait(true);
    }

    public async Task ReloadAsync()
    {
        if (_git.Root is not { } root)
        {
            Rows.Clear();
            Message = "The open solution is not in a git repository.";
            return;
        }

        try
        {
            var worktrees = await _git.WorktreesAsync().ConfigureAwait(true);
            var selected = _selected?.Worktree.Path;
            Rows.Clear();
            foreach (var worktree in worktrees)
            {
                Rows.Add(new WorktreeRow(worktree, Workbench.Projects.SolutionService.PathComparer.Equals(worktree.Path.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar))));
            }

            Selected = Rows.FirstOrDefault(r => r.Worktree.Path == selected);
            Message = null;
        }
        catch (Exception ex) when (ex is GitException or InvalidOperationException)
        {
            Rows.Clear();
            Message = ex.Message;
        }
    }
}
