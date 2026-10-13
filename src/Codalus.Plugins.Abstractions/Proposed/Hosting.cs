namespace Codalus.Plugins.Proposed;

/// <summary>
/// Git hosting providers (ADR 0031): GitHub, Azure DevOps, GitLab, or another host. The shell shows the pull requests,
/// the sign-in dialog, review comments, CI status and work items; the provider talks to its host. A provider is declared
/// in the manifest (<c>contributes.hostingProviders</c>) and registered when its plugin activates
/// (<c>onHostingProvider:&lt;id&gt;</c>, implicit). Since API 0.2.6.
/// </summary>
public interface IHosting
{
    /// <summary>Registers the provider whose <see cref="HostingProvider.Id"/> the manifest declares. Dispose to unregister.</summary>
    IDisposable RegisterProvider(HostingProvider provider);
}

/// <summary>
/// The plugin's own secrets (tokens), kept in the OS credential store: Windows Credential Manager, the macOS Keychain or
/// the Secret Service on Linux. Never in a file. A key is the plugin's own; other plugins can't read it. Since API 0.2.6.
/// </summary>
public interface ISecrets
{
    /// <summary>The secret, or null when none is stored under <paramref name="key"/>.</summary>
    Task<string?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>Stores or replaces a secret.</summary>
    Task StoreAsync(string key, string value, CancellationToken cancellationToken);

    /// <summary>Deletes a secret (no error when there is none).</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

/// <summary>
/// A Git host. Override what the host supports: <see cref="ResolveRepository"/> and the account members are required;
/// the others throw <see cref="NotSupportedException"/> by default, and the shell hides what isn't supported. Calls come on
/// any thread and are cancelled when the user moves on. Throw <see cref="HostingAuthenticationException"/> when the
/// stored session is no longer valid (the shell then asks the user to sign in again).
/// </summary>
public abstract class HostingProvider
{
    /// <summary>Creates a provider for the manifest's <c>hostingProviders[].id</c>.</summary>
    protected HostingProvider(string id) => Id = id;

    /// <summary>The id in the manifest, e.g. <c>github</c>.</summary>
    public string Id { get; }

    /// <summary>The repository a remote URL (HTTPS or SSH) points to on this host, or null when it's another host's.</summary>
    public abstract HostingRepository? ResolveRepository(string remoteUrl);

    /// <summary>The signed-in account from the stored session, or null when the user isn't signed in. No network call needed.</summary>
    public abstract Task<HostingAccount?> GetAccountAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Signs in with the OAuth device flow: get a code from the host, show it with <paramref name="prompt"/>, wait for the
    /// user to approve it in the browser, and store the session (<see cref="ISecrets"/>). Cancelled when the user cancels.
    /// </summary>
    public abstract Task<HostingAccount> SignInAsync(IDeviceCodePrompt prompt, CancellationToken cancellationToken);

    /// <summary>Signs in with a personal access token the user pasted: check it, then store it.</summary>
    public virtual Task<HostingAccount> SignInWithTokenAsync(string token, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{Id} doesn't take personal access tokens.");

    /// <summary>Forgets the session (deletes the stored secrets).</summary>
    public abstract Task SignOutAsync(CancellationToken cancellationToken);

    /// <summary>The repository's default branch and web page.</summary>
    public virtual Task<HostingRepositoryInfo> GetRepositoryInfoAsync(HostingRepository repository, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <summary>The repository's open pull requests (merge requests on GitLab), newest first.</summary>
    public virtual Task<IReadOnlyList<PullRequest>> GetPullRequestsAsync(HostingRepository repository, PullRequestFilter filter, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <summary>A pull request with its description and review threads.</summary>
    public virtual Task<PullRequestDetails> GetPullRequestAsync(HostingRepository repository, string number, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <summary>What to fetch to check a pull request out, and the local branch to put it on.</summary>
    public virtual Task<PullRequestCheckout> GetCheckoutAsync(HostingRepository repository, PullRequest pullRequest, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <summary>Opens a pull request.</summary>
    public virtual Task<PullRequest> CreatePullRequestAsync(HostingRepository repository, NewPullRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <summary>The CI status of a branch (its head commit when given).</summary>
    public virtual Task<BranchStatus> GetBranchStatusAsync(HostingRepository repository, string branch, string? commit, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <summary>Issues or work items: the ones assigned to the user, or those matching a number or words (commit message completion).</summary>
    public virtual Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(HostingRepository repository, WorkItemQuery query, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <summary>Where the user can create repositories: their account, organizations, groups, or Azure DevOps projects.</summary>
    public virtual Task<IReadOnlyList<HostingOwner>> GetOwnersAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <summary>Creates a repository (Publish): the shell then adds it as a remote and pushes.</summary>
    public virtual Task<PublishedRepository> PublishAsync(NewRepository request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

/// <summary>The session isn't valid (expired, revoked, never signed in): the user must sign in again.</summary>
public sealed class HostingAuthenticationException : Exception
{
    /// <summary>Creates the exception.</summary>
    public HostingAuthenticationException()
        : base("Sign in again.")
    {
    }

    /// <summary>Creates the exception with a message for the user.</summary>
    public HostingAuthenticationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public HostingAuthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Shows a device code to the user (the shell's sign-in dialog).</summary>
public interface IDeviceCodePrompt
{
    /// <summary>Shows the code and where to enter it. Returns once it is shown; the sign-in call's token cancels it.</summary>
    Task ShowAsync(DeviceCode code, CancellationToken cancellationToken);
}

/// <summary>A device code (RFC 8628) for the user to enter at <see cref="VerificationUri"/>.</summary>
/// <param name="UserCode">The code, such as <c>WDJB-MJHT</c>.</param>
/// <param name="VerificationUri">Where to enter it.</param>
/// <param name="ExpiresAt">When it stops working.</param>
public sealed record DeviceCode(string UserCode, string VerificationUri, DateTimeOffset ExpiresAt)
{
    /// <summary>A URL with the code filled in, when the host gives one.</summary>
    public string? VerificationUriComplete { get; init; }

    /// <summary>The host's own instructions, if any.</summary>
    public string? Message { get; init; }
}

/// <summary>A signed-in account.</summary>
/// <param name="Name">The account name (login, user name or e-mail).</param>
/// <param name="Host">The host it is on, e.g. <c>github.com</c> or a GitHub Enterprise Server host.</param>
public sealed record HostingAccount(string Name, string Host)
{
    /// <summary>The user's display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The account's page.</summary>
    public string? Url { get; init; }
}

/// <summary>
/// A repository on a host, as <see cref="HostingProvider.ResolveRepository"/> found it. <see cref="Id"/> is the
/// provider's own (<c>owner/name</c>, <c>organization/project/repository</c>, a GitLab path).
/// </summary>
/// <param name="Id">The provider's identifier for the repository.</param>
/// <param name="Owner">The owner, organization, group or project.</param>
/// <param name="Name">The repository's name.</param>
/// <param name="WebUrl">Its page.</param>
public sealed record HostingRepository(string Id, string Owner, string Name, string WebUrl)
{
    /// <summary>The host, e.g. <c>github.com</c>.</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>The git remote it was found on (set by the shell), e.g. <c>origin</c>.</summary>
    public string? Remote { get; init; }
}

/// <summary>A repository's default branch and page.</summary>
/// <param name="DefaultBranch">The default branch, e.g. <c>main</c>.</param>
/// <param name="WebUrl">The repository's page.</param>
public sealed record HostingRepositoryInfo(string DefaultBranch, string WebUrl)
{
    /// <summary>Whether it is private.</summary>
    public bool IsPrivate { get; init; }
}

/// <summary>Which pull requests to list.</summary>
public enum PullRequestFilter
{
    /// <summary>Every open pull request.</summary>
    All,

    /// <summary>The user's own.</summary>
    Mine,

    /// <summary>Those that ask for the user's review.</summary>
    ReviewRequested,
}

/// <summary>A pull request (a merge request on GitLab).</summary>
/// <param name="Number">Its number on the host (as text).</param>
/// <param name="Title">Its title.</param>
/// <param name="Author">Who opened it.</param>
/// <param name="SourceBranch">The branch with the changes.</param>
/// <param name="TargetBranch">The branch it merges into.</param>
/// <param name="WebUrl">Its page.</param>
public sealed record PullRequest(string Number, string Title, string Author, string SourceBranch, string TargetBranch, string WebUrl)
{
    /// <summary>How it is referred to: <c>#12</c>, or <c>!12</c> on GitLab.</summary>
    public string Reference { get; init; } = string.Empty;

    /// <summary>Whether it is a draft.</summary>
    public bool IsDraft { get; init; }

    /// <summary><c>open</c>, <c>merged</c> or <c>closed</c>.</summary>
    public string State { get; init; } = "open";

    /// <summary>When it was opened.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>When it last changed.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>The head commit.</summary>
    public string? SourceCommit { get; init; }

    /// <summary>The fork it comes from (<c>owner/name</c>), or null when its branch is in the repository itself.</summary>
    public string? SourceRepository { get; init; }

    /// <summary>The fork's clone URL, when it comes from one.</summary>
    public string? SourceCloneUrl { get; init; }

    /// <summary>Reviewers asked (names).</summary>
    public IReadOnlyList<string> Reviewers { get; init; } = [];
}

/// <summary>A pull request with its description and review threads.</summary>
/// <param name="PullRequest">The pull request.</param>
/// <param name="Description">Its description (Markdown).</param>
/// <param name="Threads">Its review threads on lines of files.</param>
public sealed record PullRequestDetails(PullRequest PullRequest, string Description, IReadOnlyList<ReviewThread> Threads)
{
    /// <summary>General comments (not on a line), oldest first.</summary>
    public IReadOnlyList<ReviewComment> Comments { get; init; } = [];
}

/// <summary>
/// Review comments on a line of a file: <see cref="Path"/> is relative to the repository (forward slashes), and
/// <see cref="Line"/> is one-based in the pull request's version of the file.
/// </summary>
/// <param name="Id">The thread's id on the host.</param>
/// <param name="Path">The file.</param>
/// <param name="Line">The (last) line commented on.</param>
/// <param name="Comments">The comments, oldest first.</param>
public sealed record ReviewThread(string Id, string Path, int Line, IReadOnlyList<ReviewComment> Comments)
{
    /// <summary>The first line, for comments on a range.</summary>
    public int? StartLine { get; init; }

    /// <summary>Whether it was resolved.</summary>
    public bool IsResolved { get; init; }

    /// <summary>Whether the code it is on has changed since.</summary>
    public bool IsOutdated { get; init; }

    /// <summary>The thread's page.</summary>
    public string? Url { get; init; }
}

/// <summary>A review comment.</summary>
/// <param name="Author">Who wrote it.</param>
/// <param name="Body">The text (Markdown).</param>
/// <param name="CreatedAt">When.</param>
public sealed record ReviewComment(string Author, string Body, DateTimeOffset CreatedAt)
{
    /// <summary>The comment's page.</summary>
    public string? Url { get; init; }
}

/// <summary>
/// How to check out a pull request: fetch <see cref="SourceRef"/> from <see cref="Remote"/> (a remote's name or a fork's
/// URL) onto <see cref="LocalBranch"/>. <see cref="Track"/>: <see cref="SourceRef"/> is a branch of that remote, and the
/// local branch tracks it.
/// </summary>
/// <param name="Remote">A remote name or URL.</param>
/// <param name="SourceRef">A branch name, or a ref such as <c>refs/pull/12/head</c>.</param>
/// <param name="LocalBranch">The local branch to create or update.</param>
/// <param name="Track">Whether the local branch tracks the remote branch.</param>
public sealed record PullRequestCheckout(string Remote, string SourceRef, string LocalBranch, bool Track);

/// <summary>A pull request to open.</summary>
/// <param name="Title">Its title.</param>
/// <param name="SourceBranch">The branch with the changes (already pushed).</param>
/// <param name="TargetBranch">The branch to merge into.</param>
public sealed record NewPullRequest(string Title, string SourceBranch, string TargetBranch)
{
    /// <summary>The description (Markdown).</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Open it as a draft.</summary>
    public bool IsDraft { get; init; }
}

/// <summary>
/// The CI status of a branch: <see cref="State"/> is <c>success</c>, <c>failure</c>, <c>pending</c> or <c>none</c>, the
/// summary of its checks.
/// </summary>
/// <param name="State">The overall state.</param>
/// <param name="Checks">The checks, pipelines or builds.</param>
public sealed record BranchStatus(string State, IReadOnlyList<StatusCheck> Checks)
{
    /// <summary>Where to see it all.</summary>
    public string? Url { get; init; }
}

/// <summary>One check, job or build: <see cref="State"/> as in <see cref="BranchStatus"/> (plus <c>skipped</c> and <c>cancelled</c>).</summary>
/// <param name="Name">Its name.</param>
/// <param name="State">Its state.</param>
public sealed record StatusCheck(string Name, string State)
{
    /// <summary>A short description.</summary>
    public string? Description { get; init; }

    /// <summary>Its page.</summary>
    public string? Url { get; init; }
}

/// <summary>Which work items to list.</summary>
public sealed record WorkItemQuery
{
    /// <summary>Only those assigned to the user.</summary>
    public bool AssignedToMe { get; init; }

    /// <summary>A number or words to match (commit message completion); null for any.</summary>
    public string? Text { get; init; }

    /// <summary>At most this many.</summary>
    public int Limit { get; init; } = 50;
}

/// <summary>An issue or work item.</summary>
/// <param name="Number">Its number.</param>
/// <param name="Title">Its title.</param>
/// <param name="Reference">How a commit message mentions it, e.g. <c>#123</c>.</param>
/// <param name="WebUrl">Its page.</param>
public sealed record WorkItem(string Number, string Title, string Reference, string WebUrl)
{
    /// <summary>Its state (open, Active, To Do…).</summary>
    public string? State { get; init; }

    /// <summary>Its type (Issue, Bug, Task, User Story…).</summary>
    public string? Type { get; init; }

    /// <summary>Who it is assigned to.</summary>
    public string? AssignedTo { get; init; }
}

/// <summary>Where a repository can be created.</summary>
/// <param name="Id">The provider's id for it (sent back in <see cref="NewRepository.Owner"/>).</param>
/// <param name="Name">What the user sees.</param>
/// <param name="Kind"><c>user</c>, <c>organization</c>, <c>group</c> or <c>project</c>.</param>
public sealed record HostingOwner(string Id, string Name, string Kind);

/// <summary>A repository to create.</summary>
/// <param name="Owner">A <see cref="HostingOwner.Id"/>.</param>
/// <param name="Name">The repository's name.</param>
public sealed record NewRepository(string Owner, string Name)
{
    /// <summary>A description.</summary>
    public string? Description { get; init; }

    /// <summary>Private (the default) or public.</summary>
    public bool IsPrivate { get; init; } = true;
}

/// <summary>A repository that was created.</summary>
/// <param name="CloneUrl">Its HTTPS clone URL.</param>
/// <param name="WebUrl">Its page.</param>
public sealed record PublishedRepository(string CloneUrl, string WebUrl)
{
    /// <summary>Its SSH clone URL.</summary>
    public string? SshUrl { get; init; }
}
