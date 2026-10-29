namespace Codalus.Plugins.Protocol;

#pragma warning disable CA1819 // Properties should not return arrays: wire DTOs.

// Hosting providers and secrets (docs/plugin-protocol.md#hosting-providers-and-secrets, ADR 0031). Since API 0.2.6.

/// <summary>Method names of the hosting and secrets APIs. Since API 0.2.6 (protocol 0.2).</summary>
public static class HostingMethods
{
    // host → shell (notifications)
    public const string RegisterProvider = "hosting/registerProvider";
    public const string UnregisterProvider = "hosting/unregisterProvider";
    public const string DeviceCode = "hosting/deviceCode";

    // host → shell (requests)
    public const string SecretsGet = "secrets/get";
    public const string SecretsStore = "secrets/store";
    public const string SecretsDelete = "secrets/delete";

    // shell → host (requests)
    public const string Resolve = "hosting/resolve";
    public const string Account = "hosting/account";
    public const string SignIn = "hosting/signIn";
    public const string SignOut = "hosting/signOut";
    public const string RepositoryInfo = "hosting/repositoryInfo";
    public const string PullRequests = "hosting/pullRequests";
    public const string PullRequest = "hosting/pullRequest";
    public const string Checkout = "hosting/checkout";
    public const string CreatePullRequest = "hosting/createPullRequest";
    public const string BranchStatus = "hosting/branchStatus";
    public const string WorkItems = "hosting/workItems";
    public const string Owners = "hosting/owners";
    public const string Publish = "hosting/publish";
}

/// <summary>JSON-RPC error codes of the hosting requests.</summary>
public static class HostingErrorCodes
{
    /// <summary>The user isn't signed in, or the session expired: sign in again.</summary>
    public const int NotSignedIn = -33010;

    /// <summary>The provider doesn't do that.</summary>
    public const int NotSupported = -33011;

    /// <summary>The host refused or failed (the message says why).</summary>
    public const int Failed = -33012;

    /// <summary>No such provider is registered (its plugin isn't active).</summary>
    public const int NoProvider = -33013;
}

/// <summary><c>hosting/registerProvider</c> and <c>hosting/unregisterProvider</c>.</summary>
public sealed record HostingProviderParams(string PluginId, string ProviderId);

/// <summary><c>hosting/deviceCode</c>: a sign-in waits for the user to enter <see cref="UserCode"/> at <see cref="VerificationUri"/>.</summary>
public sealed record HostingDeviceCodeParams(string ProviderId, string UserCode, string VerificationUri, string? VerificationUriComplete, DateTimeOffset ExpiresAt, string? Message);

/// <summary><c>secrets/get</c>, <c>secrets/store</c> (with <see cref="Value"/>) and <c>secrets/delete</c>.</summary>
public sealed record SecretParams(string PluginId, string Key, string? Value);

public sealed record SecretResult(string? Value);

public sealed record HostingRemoteDto(string Remote, string Url);

/// <summary><c>hosting/resolve</c>: the first of the remotes (in order) that is a repository of the provider's host.</summary>
public sealed record HostingResolveParams(string ProviderId, HostingRemoteDto[] Remotes);

public sealed record HostingRepositoryDto(string Id, string Owner, string Name, string WebUrl, string Host, string? Remote);

public sealed record HostingResolveResult(HostingRepositoryDto? Repository);

/// <summary>A request that only names the provider (<c>hosting/account</c>, <c>hosting/signOut</c>, <c>hosting/owners</c>).</summary>
public sealed record HostingIdParams(string ProviderId);

public sealed record HostingAccountDto(string Name, string Host, string? DisplayName, string? Url);

public sealed record HostingAccountResult(HostingAccountDto? Account);

/// <summary>
/// <c>hosting/signIn</c>: the device flow, a personal access token when <see cref="Token"/> is set, or the operating
/// system's account when <see cref="Integrated"/> is (since API 1.2.0).
/// </summary>
public sealed record HostingSignInParams(string ProviderId, string? Token)
{
    public bool Integrated { get; init; }
}

/// <summary><c>hosting/repositoryInfo</c>.</summary>
public sealed record HostingRepositoryParams(string ProviderId, HostingRepositoryDto Repository);

public sealed record HostingRepositoryInfoDto(string DefaultBranch, string WebUrl, bool IsPrivate);

/// <summary><c>hosting/pullRequests</c>; <see cref="Filter"/> is <c>all</c>, <c>mine</c> or <c>reviewRequested</c>.</summary>
public sealed record HostingPullRequestsParams(string ProviderId, HostingRepositoryDto Repository, string Filter);

public sealed record PullRequestDto(
    string Number,
    string Title,
    string Author,
    string SourceBranch,
    string TargetBranch,
    string WebUrl,
    string Reference,
    bool IsDraft,
    string State,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    string? SourceCommit,
    string? SourceRepository,
    string? SourceCloneUrl,
    string[] Reviewers);

public sealed record PullRequestsResult(PullRequestDto[] PullRequests);

/// <summary><c>hosting/pullRequest</c>.</summary>
public sealed record HostingPullRequestParams(string ProviderId, HostingRepositoryDto Repository, string Number);

public sealed record ReviewCommentDto(string Author, string Body, DateTimeOffset CreatedAt, string? Url);

public sealed record ReviewThreadDto(string Id, string Path, int Line, int? StartLine, bool IsResolved, bool IsOutdated, string? Url, ReviewCommentDto[] Comments);

public sealed record PullRequestDetailsDto(PullRequestDto PullRequest, string Description, ReviewThreadDto[] Threads, ReviewCommentDto[] Comments);

/// <summary><c>hosting/checkout</c>.</summary>
public sealed record HostingCheckoutParams(string ProviderId, HostingRepositoryDto Repository, PullRequestDto PullRequest);

public sealed record PullRequestCheckoutDto(string Remote, string SourceRef, string LocalBranch, bool Track);

/// <summary><c>hosting/createPullRequest</c>.</summary>
public sealed record HostingCreatePullRequestParams(string ProviderId, HostingRepositoryDto Repository, string Title, string Description, string SourceBranch, string TargetBranch, bool IsDraft);

/// <summary><c>hosting/branchStatus</c>.</summary>
public sealed record HostingBranchStatusParams(string ProviderId, HostingRepositoryDto Repository, string Branch, string? Commit);

public sealed record StatusCheckDto(string Name, string State, string? Description, string? Url);

public sealed record BranchStatusDto(string State, StatusCheckDto[] Checks, string? Url);

/// <summary><c>hosting/workItems</c>.</summary>
public sealed record HostingWorkItemsParams(string ProviderId, HostingRepositoryDto Repository, bool AssignedToMe, string? Text, int Limit);

public sealed record WorkItemDto(string Number, string Title, string Reference, string WebUrl, string? State, string? Type, string? AssignedTo);

public sealed record WorkItemsResult(WorkItemDto[] WorkItems);

public sealed record HostingOwnerDto(string Id, string Name, string Kind);

public sealed record HostingOwnersResult(HostingOwnerDto[] Owners);

/// <summary><c>hosting/publish</c>.</summary>
public sealed record HostingPublishParams(string ProviderId, string Owner, string Name, string? Description, bool IsPrivate);

public sealed record PublishedRepositoryDto(string CloneUrl, string WebUrl, string? SshUrl);

#pragma warning restore CA1819
