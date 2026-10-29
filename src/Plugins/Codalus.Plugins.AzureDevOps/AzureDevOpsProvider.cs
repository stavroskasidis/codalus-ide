using System.Text.Json;
using System.Text.RegularExpressions;
using Codalus.Plugins.Hosting;
using Codalus.Plugins.Proposed;

namespace Codalus.Plugins.AzureDevOps;

/// <summary>
/// Azure DevOps through its REST API (7.1, or the latest an older Azure DevOps Server takes). Sign-in: Microsoft Entra
/// ID's device flow for the Azure DevOps resource (the app in <c>azureDevOps.oauthClientId</c>), a personal access token
/// (sent as Basic credentials), or on an Azure DevOps Server the operating system's account (Negotiate). Repositories are
/// <c>organization/project/repository</c> (a collection for an organization on a server), from <c>dev.azure.com</c>,
/// <c>*.visualstudio.com</c>, the server in <c>azureDevOps.serverUrl</c> and SSH remotes (ADR 0087).
/// </summary>
internal sealed partial class AzureDevOpsProvider : HostingProviderBase
{
    /// <summary>The Azure DevOps resource in Entra ID, and a refresh token.</summary>
    private const string Scope = "499b84ac-1321-427f-aa17-267ca6975798/.default offline_access";
    private const string LatestApiVersion = "7.1";
    private const string DefaultProfileUrl = "https://app.vssps.visualstudio.com";

    /// <summary>The API version asked for: 7.1, or what an older server said it supports (<see cref="SupportedApiVersion"/>).</summary>
    private volatile string _apiVersion = LatestApiVersion;

    public AzureDevOpsProvider(IPluginContext context)
        : base("azure-devops", context)
    {
        Http.Headers["Accept"] = "application/json";
        context.Settings.Changed += (_, e) =>
        {
            if (e.Affects(SettingsSection))
            {
                _apiVersion = LatestApiVersion; // another server: ask it for the latest again
            }
        };
    }

    protected override string SettingsSection => "azureDevOps";

    private string ServerUrl => Setting("azureDevOps.serverUrl", "https://dev.azure.com");

    private string AuthorityUrl => Setting("azureDevOps.authorityUrl", "https://login.microsoftonline.com/organizations");

    private string ProfileUrl => Setting("azureDevOps.profileUrl", DefaultProfileUrl);

    /// <summary>Codalus IDE's Microsoft Entra ID app for Azure DevOps Services: <c>azureDevOps.oauthClientId</c>'s default (ADR 0088).</summary>
    internal const string ServicesClientId = "743f360d-cef1-4e57-86fe-27c15c10eae0";

    private string ClientId => Settings.GetValue("azureDevOps.oauthClientId", string.Empty).Trim();

    protected override string ServerHost => RemoteUrl.HostOf(ServerUrl);

    /// <summary>
    /// An Azure DevOps Server (on premises): the server isn't Azure DevOps Services', which alone has the profile service,
    /// unless <c>azureDevOps.profileUrl</c> names one (another cloud, the tests' mock).
    /// </summary>
    private bool IsServer => !IsServicesHost(ServerHost) && string.Equals(ProfileUrl, DefaultProfileUrl, StringComparison.OrdinalIgnoreCase);

    internal static bool IsServicesHost(string host) =>
        host == "dev.azure.com" || host.EndsWith(".visualstudio.com", StringComparison.Ordinal);

    private string ApiVersion => "api-version=" + _apiVersion;

    private static string Esc(string value) => Uri.EscapeDataString(value);

    /// <summary>
    /// The version in an Azure DevOps Server's answer to a newer one than it takes: "The requested REST API version of
    /// 7.1 is out of range for this server. The latest REST API version this server supports is 6.0."
    /// </summary>
    internal static string? SupportedApiVersion(string message) =>
        SupportedVersionPattern().Match(message) is { Success: true } match ? match.Groups[1].Value : null;

    [GeneratedRegex(@"REST API version this server supports is (\d+\.\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex SupportedVersionPattern();

    /// <summary>A call to the REST API, asked again once with the version the server says it supports.</summary>
    private async Task<JsonElement> ApiAsync(Func<Task<JsonElement>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (HostingHttpException ex) when (ex.Status == 400 && SupportedApiVersion(ex.Message) is { } supported && supported != _apiVersion)
        {
            _apiVersion = supported;
            return await call().ConfigureAwait(false);
        }
    }

    private Task<JsonElement> GetAsync(Func<string> url, CancellationToken cancellationToken) =>
        ApiAsync(() => Http.GetAsync(url(), cancellationToken));

    private Task<JsonElement> PostAsync(Func<string> url, string json, CancellationToken cancellationToken) =>
        ApiAsync(() => Http.PostAsync(url(), json, cancellationToken));

    public override HostingRepository? ResolveRepository(string remoteUrl)
    {
        if (RemoteUrl.Parse(remoteUrl) is not { } url)
        {
            return null;
        }

        var segments = url.Segments;
        string? organization = null, project = null, repository = null;
        if (url.IsSsh && url.Host is "ssh.dev.azure.com" or "vs-ssh.visualstudio.com" && segments is ["v3", var o, var p, var r])
        {
            (organization, project, repository) = (o, p, r);
        }
        else if (url.Host == ServerHost || (url.IsSsh && url.Host == HostName(ServerHost)))
        {
            // dev.azure.com/{organization}/{project}/_git/{repository}, under the server's own path on an Azure DevOps
            // Server ({collection} for the organization), where {collection}/_git/{repository} is the project's namesake.
            var prefix = Uri.TryCreate(ServerUrl, UriKind.Absolute, out var server) ? server.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries) : [];
            if (segments.Length > prefix.Length && segments.Take(prefix.Length).SequenceEqual(prefix, StringComparer.OrdinalIgnoreCase))
            {
                switch (segments[prefix.Length..])
                {
                    case [var collection, var projectName, "_git", var repositoryName]:
                        (organization, project, repository) = (collection, projectName, repositoryName);
                        break;
                    case [var collection, "_git", var repositoryName]:
                        (organization, project, repository) = (collection, repositoryName, repositoryName);
                        break;
                }
            }
        }
        else if (url.Host.EndsWith(".visualstudio.com", StringComparison.Ordinal) && !url.IsSsh)
        {
            // {organization}.visualstudio.com/[DefaultCollection/]{project}/_git/{repository}
            var rest = segments is ["DefaultCollection", .. var tail] ? tail : segments;
            if (rest is [var projectName, "_git", var repositoryName])
            {
                (organization, project, repository) = (url.Host[..url.Host.IndexOf('.', StringComparison.Ordinal)], projectName, repositoryName);
            }
        }

        return organization is null || project is null || repository is null
            ? null
            : new HostingRepository($"{organization}/{project}/{repository}", $"{organization}/{project}", repository, $"{ServerUrl}/{Esc(organization)}/{Esc(project)}/_git/{Esc(repository)}") { Host = ServerHost };
    }

    /// <summary>A host without its port (an SSH remote's host has the SSH port, not the web server's).</summary>
    private static string HostName(string host) => !host.EndsWith(']') && host.LastIndexOf(':') is var colon and > 0 ? host[..colon] : host;

    private static (string Organization, string Project, string Repository) Split(HostingRepository repository) =>
        repository.Id.Split('/') is [var o, var p, var r] ? (o, p, r) : throw new ArgumentException($"Not an Azure DevOps repository: {repository.Id}");

    private string Project(HostingRepository repository)
    {
        var (o, p, _) = Split(repository);
        return $"{ServerUrl}/{Esc(o)}/{Esc(p)}";
    }

    private string Repo(HostingRepository repository) => $"{Project(repository)}/_apis/git/repositories/{Esc(Split(repository).Repository)}";

    // ------------------------------------------------------------------ account

    public override async Task<HostingAccount> SignInAsync(IDeviceCodePrompt prompt, CancellationToken cancellationToken)
    {
        if (ClientId == ServicesClientId && !IsServicesHost(ServerHost))
        {
            // Codalus IDE's Entra app is for Azure DevOps Services; a server signs in with its own app, the system's account or a token.
            throw new InvalidOperationException($"{ServerUrl} isn't Azure DevOps Services: sign in with your Windows account or a personal access token, or set azureDevOps.oauthClientId to an Entra app of your own.");
        }

        var token = await DeviceFlow.SignInAsync(
            Http,
            new DeviceFlowEndpoints(AuthorityUrl + "/oauth2/v2.0/devicecode", AuthorityUrl + "/oauth2/v2.0/token", ClientId, Scope),
            prompt,
            cancellationToken).ConfigureAwait(false);
        return await StoreAsync("bearer", token, cancellationToken).ConfigureAwait(false);
    }

    public override Task<HostingAccount> SignInWithTokenAsync(string token, CancellationToken cancellationToken) =>
        StoreAsync("basic", new OAuthToken(token.Trim(), null, null), cancellationToken);

    /// <summary>An Azure DevOps Server only: the operating system's account (Windows authentication, or a Kerberos ticket elsewhere).</summary>
    public override async Task<HostingAccount> SignInIntegratedAsync(CancellationToken cancellationToken)
    {
        if (!IsServer)
        {
            throw new InvalidOperationException($"{ServerUrl} is Azure DevOps Services, which doesn't take the operating system's account: sign in with a personal access token, or set azureDevOps.serverUrl to your Azure DevOps Server.");
        }

        JsonElement connection;
        try
        {
            connection = await GetIntegratedAsync($"{ServerUrl}/_apis/connectionData", cancellationToken).ConfigureAwait(false);
        }
        catch (HostingAuthenticationException ex)
        {
            throw new InvalidOperationException(
                OperatingSystem.IsWindows()
                    ? $"{ServerUrl} didn't accept your Windows account ({ex.Message}). Sign in with a personal access token instead."
                    : $"{ServerUrl} didn't accept your Kerberos ticket ({ex.Message}). Run kinit for your domain account, or sign in with a personal access token.",
                ex);
        }

        return await SaveServerSessionAsync(IntegratedScheme, string.Empty, connection, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HostingAccount> StoreAsync(string scheme, OAuthToken token, CancellationToken cancellationToken)
    {
        if (IsServer)
        {
            // An Azure DevOps Server has no profile service: its connection data says who is signed in.
            var connection = await GetWithTokenAsync($"{ServerUrl}/_apis/connectionData", scheme, token.AccessToken, cancellationToken).ConfigureAwait(false);
            return await SaveServerSessionAsync(scheme, token.AccessToken, connection, cancellationToken).ConfigureAwait(false);
        }

        var profile = await GetWithTokenAsync($"{ProfileUrl}/_apis/profile/profiles/me?{ApiVersion}", scheme, token.AccessToken, cancellationToken).ConfigureAwait(false);
        var session = new HostingSession
        {
            Host = ServerHost,
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken,
            ExpiresAt = token.ExpiresAt,
            Scheme = scheme,
            Account = profile.Str("emailAddress") ?? profile.Str("publicAlias") ?? profile.Str("displayName") ?? throw new HostingHttpException(200, "Azure DevOps didn't say who is signed in."),
            DisplayName = profile.Str("displayName"),
            UserId = profile.Str("id"),
        };
        await SaveSessionAsync(session, cancellationToken).ConfigureAwait(false);
        return AccountOf(session);
    }

    /// <summary>The session from an Azure DevOps Server's connection data (<c>authenticatedUser</c>).</summary>
    private async Task<HostingAccount> SaveServerSessionAsync(string scheme, string token, JsonElement connection, CancellationToken cancellationToken)
    {
        var user = connection.Get("authenticatedUser") ?? throw new HostingHttpException(200, "The Azure DevOps Server didn't say who is signed in.");
        if (user.Str("descriptor")?.StartsWith("System:PublicAccess", StringComparison.Ordinal) == true)
        {
            throw new InvalidOperationException("The Azure DevOps Server let you in anonymously: sign in with a personal access token.");
        }

        var session = new HostingSession
        {
            Host = ServerHost,
            AccessToken = token,
            Scheme = scheme,
            Account = user.Str("properties", "Account", "$value") ?? user.Str("providerDisplayName") ?? user.Str("id") ?? throw new HostingHttpException(200, "The Azure DevOps Server didn't say who is signed in."),
            DisplayName = user.Str("providerDisplayName"),
            UserId = user.Str("id"),
        };
        await SaveSessionAsync(session, cancellationToken).ConfigureAwait(false);
        return AccountOf(session);
    }

    protected override async Task<HostingSession> RefreshAsync(HostingSession session, CancellationToken cancellationToken)
    {
        var token = await DeviceFlow.RefreshAsync(
            Http,
            AuthorityUrl + "/oauth2/v2.0/token",
            new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["client_id"] = ClientId, ["refresh_token"] = session.RefreshToken!, ["scope"] = Scope },
            cancellationToken).ConfigureAwait(false);
        return session with { AccessToken = token.AccessToken, RefreshToken = token.RefreshToken ?? session.RefreshToken, ExpiresAt = token.ExpiresAt };
    }

    private bool IsMe(JsonElement identity, HostingSession session) =>
        (session.UserId is { } id && string.Equals(identity.Str("id"), id, StringComparison.OrdinalIgnoreCase))
        || string.Equals(identity.Str("uniqueName"), session.Account, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ repository and pull requests

    public override async Task<HostingRepositoryInfo> GetRepositoryInfoAsync(HostingRepository repository, CancellationToken cancellationToken)
    {
        var info = await GetAsync(() => $"{Repo(repository)}?{ApiVersion}", cancellationToken).ConfigureAwait(false);
        return new HostingRepositoryInfo(Branch(info.Str("defaultBranch")) ?? "main", info.Str("webUrl") ?? repository.WebUrl)
        {
            IsPrivate = info.Str("project", "visibility") != "public",
        };
    }

    private static string? Branch(string? reference) =>
        reference?.StartsWith("refs/heads/", StringComparison.Ordinal) == true ? reference["refs/heads/".Length..] : reference;

    public override async Task<IReadOnlyList<PullRequest>> GetPullRequestsAsync(HostingRepository repository, PullRequestFilter filter, CancellationToken cancellationToken)
    {
        var session = await RequireSessionAsync(cancellationToken).ConfigureAwait(false);
        var result = await GetAsync(() => $"{Repo(repository)}/pullrequests?searchCriteria.status=active&$top=50&{ApiVersion}", cancellationToken).ConfigureAwait(false);
        return [.. result.Items("value")
            .Where(p => filter switch
            {
                PullRequestFilter.Mine => p.Get("createdBy") is { } creator && IsMe(creator, session),
                PullRequestFilter.ReviewRequested => p.Items("reviewers").Any(r => IsMe(r, session)),
                _ => true,
            })
            .Select(p => ToPullRequest(p, repository))];
    }

    private static PullRequest ToPullRequest(JsonElement p, HostingRepository repository)
    {
        var id = p.Str("pullRequestId") ?? string.Empty;
        return new PullRequest(id, p.Str("title") ?? string.Empty, p.Str("createdBy", "displayName") ?? string.Empty, Branch(p.Str("sourceRefName")) ?? string.Empty, Branch(p.Str("targetRefName")) ?? string.Empty, $"{repository.WebUrl}/pullrequest/{id}")
        {
            Reference = "!" + id,
            IsDraft = p.Bool("isDraft"),
            State = p.Str("status") switch { "completed" => "merged", "abandoned" => "closed", _ => "open" },
            CreatedAt = p.Date("creationDate"),
            SourceCommit = p.Str("lastMergeSourceCommit", "commitId"),
            Reviewers = [.. p.Items("reviewers").Select(r => r.Str("displayName")).OfType<string>()],
        };
    }

    public override async Task<PullRequestDetails> GetPullRequestAsync(HostingRepository repository, string number, CancellationToken cancellationToken)
    {
        var pull = await GetAsync(() => $"{Repo(repository)}/pullrequests/{Esc(number)}?{ApiVersion}", cancellationToken).ConfigureAwait(false);
        var threads = await GetAsync(() => $"{Repo(repository)}/pullRequests/{Esc(number)}/threads?{ApiVersion}", cancellationToken).ConfigureAwait(false);
        var reviewThreads = new List<ReviewThread>();
        var comments = new List<ReviewComment>();
        foreach (var thread in threads.Items("value").Where(t => !t.Bool("isDeleted")))
        {
            var notes = thread.Items("comments")
                .Where(c => c.Str("commentType") != "system" && !c.Bool("isDeleted"))
                .Select(c => new ReviewComment(c.Str("author", "displayName") ?? string.Empty, c.Str("content") ?? string.Empty, c.Date("publishedDate") ?? DateTimeOffset.MinValue))
                .ToList();
            if (notes.Count == 0)
            {
                continue;
            }

            if (thread.Get("threadContext") is { } context && context.Str("filePath") is { } path)
            {
                var line = context.Long("rightFileEnd", "line") ?? context.Long("rightFileStart", "line") ?? context.Long("leftFileEnd", "line") ?? 1;
                reviewThreads.Add(new ReviewThread(thread.Str("id") ?? string.Empty, path.TrimStart('/'), (int)line, notes)
                {
                    StartLine = context.Long("rightFileStart", "line") is { } start ? (int)start : null,
                    IsResolved = thread.Str("status") is "fixed" or "closed" or "wontFix" or "byDesign",
                    Url = $"{repository.WebUrl}/pullrequest/{number}?discussionId={thread.Str("id")}",
                });
            }
            else
            {
                comments.AddRange(notes);
            }
        }

        return new PullRequestDetails(ToPullRequest(pull, repository), pull.Str("description") ?? string.Empty, reviewThreads) { Comments = comments };
    }

    public override Task<PullRequestCheckout> GetCheckoutAsync(HostingRepository repository, PullRequest pullRequest, CancellationToken cancellationToken) =>
        Task.FromResult(new PullRequestCheckout(repository.Remote ?? "origin", pullRequest.SourceBranch, pullRequest.SourceBranch, Track: true));

    public override async Task<PullRequest> CreatePullRequestAsync(HostingRepository repository, NewPullRequest request, CancellationToken cancellationToken)
    {
        var created = await PostAsync(
            () => $"{Repo(repository)}/pullrequests?{ApiVersion}",
            Json.Object(("sourceRefName", "refs/heads/" + request.SourceBranch), ("targetRefName", "refs/heads/" + request.TargetBranch), ("title", request.Title), ("description", request.Description), ("isDraft", request.IsDraft)),
            cancellationToken).ConfigureAwait(false);
        return ToPullRequest(created, repository);
    }

    // ------------------------------------------------------------------ pipelines

    public override async Task<BranchStatus> GetBranchStatusAsync(HostingRepository repository, string branch, string? commit, CancellationToken cancellationToken)
    {
        var builds = await GetAsync(() => $"{Project(repository)}/_apis/build/builds?branchName={Esc("refs/heads/" + branch)}&$top=20&queryOrder=queueTimeDescending&{ApiVersion}", cancellationToken).ConfigureAwait(false);
        var latest = builds.Items("value")
            .Where(b => commit is null || b.Str("sourceVersion") is null || string.Equals(b.Str("sourceVersion"), commit, StringComparison.OrdinalIgnoreCase))
            .GroupBy(b => b.Str("definition", "name") ?? "build")
            .Select(g => g.First())
            .Select(b => new StatusCheck(b.Str("definition", "name") ?? "build", b.Str("status") != "completed"
                ? "pending"
                : b.Str("result") switch
                {
                    "succeeded" => "success",
                    "canceled" => "cancelled",
                    _ => "failure",
                })
            {
                Description = b.Str("buildNumber"),
                Url = b.Str("_links", "web", "href"),
            })
            .ToList();
        return new BranchStatus(
            latest.Count == 0 ? "none" : latest.Any(c => c.State == "failure") ? "failure" : latest.Any(c => c.State == "pending") ? "pending" : "success",
            latest)
        {
            Url = $"{Project(repository)}/_build?branchFilter={Esc("refs/heads/" + branch)}",
        };
    }

    // ------------------------------------------------------------------ work items

    public override async Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(HostingRepository repository, WorkItemQuery query, CancellationToken cancellationToken)
    {
        var (organization, project, _) = Split(repository);
        var text = query.Text?.Trim().TrimStart('#');
        IReadOnlyList<string> ids;
        if (!query.AssignedToMe && text is { Length: > 0 } && text.All(char.IsAsciiDigit))
        {
            ids = [text];
        }
        else
        {
            var condition = query.AssignedToMe
                ? "[System.AssignedTo] = @Me AND [System.State] NOT IN ('Closed', 'Done', 'Removed')"
                : text is { Length: > 0 } ? $"[System.Title] CONTAINS '{text.Replace("'", "''", StringComparison.Ordinal)}'" : "[System.State] NOT IN ('Closed', 'Done', 'Removed')";
            var wiql = $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND {condition} ORDER BY [System.ChangedDate] DESC";
            var found = await PostAsync(() => $"{Project(repository)}/_apis/wit/wiql?$top={HostingHttp.Number(query.Limit)}&{ApiVersion}", Json.Object(("query", wiql)), cancellationToken).ConfigureAwait(false);
            ids = [.. found.Items("workItems").Select(w => w.Str("id")).OfType<string>().Take(query.Limit)];
        }

        if (ids.Count == 0)
        {
            return [];
        }

        JsonElement items;
        try
        {
            items = await GetAsync(() => $"{ServerUrl}/{Esc(organization)}/_apis/wit/workitems?ids={string.Join(',', ids)}&fields=System.Id,System.Title,System.State,System.WorkItemType,System.AssignedTo&errorPolicy=omit&{ApiVersion}", cancellationToken).ConfigureAwait(false);
        }
        catch (HostingHttpException ex) when (ex.Status == 404)
        {
            return [];
        }

        return [.. items.Items("value").Where(i => i.ValueKind == JsonValueKind.Object).Select(i =>
        {
            var id = i.Str("id") ?? string.Empty;
            return new WorkItem(id, i.Str("fields", "System.Title") ?? string.Empty, "#" + id, $"{ServerUrl}/{Esc(organization)}/{Esc(project)}/_workitems/edit/{id}")
            {
                State = i.Str("fields", "System.State"),
                Type = i.Str("fields", "System.WorkItemType"),
                AssignedTo = i.Str("fields", "System.AssignedTo", "displayName"),
            };
        })];
    }

    // ------------------------------------------------------------------ publishing

    public override async Task<IReadOnlyList<HostingOwner>> GetOwnersAsync(CancellationToken cancellationToken)
    {
        var session = await RequireSessionAsync(cancellationToken).ConfigureAwait(false);

        // The organizations, or an Azure DevOps Server's project collections.
        var organizations = IsServer
            ? (await GetAsync(() => $"{ServerUrl}/_apis/projectCollections?{ApiVersion}", cancellationToken).ConfigureAwait(false)).Items("value").Select(c => c.Str("name"))
            : (await Http.GetAsync($"{ProfileUrl}/_apis/accounts?memberId={Esc(session.UserId ?? string.Empty)}&{ApiVersion}", cancellationToken).ConfigureAwait(false)).Items("value").Select(a => a.Str("accountName"));
        var owners = new List<HostingOwner>();
        foreach (var organization in organizations.OfType<string>().ToList())
        {
            var projects = await GetAsync(() => $"{ServerUrl}/{Esc(organization)}/_apis/projects?{ApiVersion}", cancellationToken).ConfigureAwait(false);
            owners.AddRange(projects.Items("value").Select(p => p.Str("name")).OfType<string>().Select(project => new HostingOwner($"{organization}/{project}", $"{organization} / {project}", "project")));
        }

        return owners;
    }

    public override async Task<PublishedRepository> PublishAsync(NewRepository request, CancellationToken cancellationToken)
    {
        if (request.Owner.Split('/') is not [var organization, var project])
        {
            throw new ArgumentException("Choose an organization and a project.", nameof(request));
        }

        var created = await PostAsync(() => $"{ServerUrl}/{Esc(organization)}/{Esc(project)}/_apis/git/repositories?{ApiVersion}", Json.Object(("name", request.Name)), cancellationToken).ConfigureAwait(false);
        return new PublishedRepository(created.Str("remoteUrl") ?? throw new HostingHttpException(201, "Azure DevOps didn't return the new repository's URL."), created.Str("webUrl") ?? string.Empty)
        {
            SshUrl = created.Str("sshUrl"),
        };
    }
}
