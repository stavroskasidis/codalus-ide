using System.Text.Json;
using DaedalusStudio.Plugins.Hosting;
using DaedalusStudio.Plugins.Proposed;

namespace DaedalusStudio.Plugins.GitHub;

/// <summary>
/// GitHub and GitHub Enterprise Server through the REST API (and GraphQL for review threads, which REST doesn't mark
/// resolved). Sign-in: the device flow of the OAuth app in <c>github.oauthClientId</c>, or a personal access token.
/// </summary>
internal sealed class GitHubProvider : HostingProviderBase
{
    private const string Scopes = "repo read:org workflow";

    public GitHubProvider(IPluginContext context)
        : base("github", context)
    {
        Http.Headers["Accept"] = "application/vnd.github+json";
        Http.Headers["X-GitHub-Api-Version"] = "2022-11-28";
    }

    protected override string SettingsSection => "github";

    private string ServerUrl => Setting("github.serverUrl", "https://github.com");

    protected override string ServerHost => RemoteUrl.HostOf(ServerUrl);

    private bool IsDotCom => ServerHost == "github.com";

    /// <summary>The REST API: api.github.com, or <c>/api/v3</c> on a GitHub Enterprise Server.</summary>
    private string Api => IsDotCom ? "https://api.github.com" : ServerUrl + "/api/v3";

    private string GraphQl => IsDotCom ? "https://api.github.com/graphql" : ServerUrl + "/api/graphql";

    public override HostingRepository? ResolveRepository(string remoteUrl)
    {
        if (RemoteUrl.Parse(remoteUrl) is not { } url
            || !(url.Host == ServerHost || (IsDotCom && url.Host == "ssh.github.com"))
            || url.Segments is not [var owner, var name])
        {
            return null;
        }

        return new HostingRepository($"{owner}/{name}", owner, name, $"{ServerUrl}/{owner}/{name}") { Host = ServerHost };
    }

    // ------------------------------------------------------------------ account

    public override async Task<HostingAccount> SignInAsync(IDeviceCodePrompt prompt, CancellationToken cancellationToken)
    {
        var token = await DeviceFlow.SignInAsync(
            Http,
            new DeviceFlowEndpoints(ServerUrl + "/login/device/code", ServerUrl + "/login/oauth/access_token", Settings.GetValue("github.oauthClientId", string.Empty), Scopes),
            prompt,
            cancellationToken).ConfigureAwait(false);
        return await StoreAsync(token.AccessToken, cancellationToken).ConfigureAwait(false);
    }

    public override Task<HostingAccount> SignInWithTokenAsync(string token, CancellationToken cancellationToken) => StoreAsync(token.Trim(), cancellationToken);

    private async Task<HostingAccount> StoreAsync(string token, CancellationToken cancellationToken)
    {
        var user = await GetWithTokenAsync(Api + "/user", "bearer", token, cancellationToken).ConfigureAwait(false);
        var session = new HostingSession
        {
            Host = ServerHost,
            AccessToken = token,
            Account = user.Str("login") ?? throw new HostingHttpException(200, "GitHub didn't say who the token belongs to."),
            DisplayName = user.Str("name"),
            Url = user.Str("html_url"),
            UserId = user.Str("id"),
        };
        await SaveSessionAsync(session, cancellationToken).ConfigureAwait(false);
        return AccountOf(session);
    }

    private async Task<string> MeAsync(CancellationToken cancellationToken) => (await RequireSessionAsync(cancellationToken).ConfigureAwait(false)).Account;

    // ------------------------------------------------------------------ repository and pull requests

    private string Repo(HostingRepository repository) => $"{Api}/repos/{repository.Owner}/{repository.Name}";

    public override async Task<HostingRepositoryInfo> GetRepositoryInfoAsync(HostingRepository repository, CancellationToken cancellationToken)
    {
        var info = await Http.GetAsync(Repo(repository), cancellationToken).ConfigureAwait(false);
        return new HostingRepositoryInfo(info.Str("default_branch") ?? "main", info.Str("html_url") ?? repository.WebUrl) { IsPrivate = info.Bool("private") };
    }

    public override async Task<IReadOnlyList<PullRequest>> GetPullRequestsAsync(HostingRepository repository, PullRequestFilter filter, CancellationToken cancellationToken)
    {
        var me = await MeAsync(cancellationToken).ConfigureAwait(false);
        var pulls = await Http.GetAsync(Repo(repository) + "/pulls?state=open&sort=updated&direction=desc&per_page=50", cancellationToken).ConfigureAwait(false);
        return [.. pulls.Items()
            .Where(p => filter switch
            {
                PullRequestFilter.Mine => string.Equals(p.Str("user", "login"), me, StringComparison.OrdinalIgnoreCase),
                PullRequestFilter.ReviewRequested => p.Items("requested_reviewers").Any(r => string.Equals(r.Str("login"), me, StringComparison.OrdinalIgnoreCase)),
                _ => true,
            })
            .Select(p => ToPullRequest(p, repository))];
    }

    private static PullRequest ToPullRequest(JsonElement p, HostingRepository repository)
    {
        var number = p.Str("number") ?? string.Empty;
        var headRepository = p.Str("head", "repo", "full_name");
        var fork = headRepository is not null && !string.Equals(headRepository, repository.Id, StringComparison.OrdinalIgnoreCase);
        return new PullRequest(number, p.Str("title") ?? string.Empty, p.Str("user", "login") ?? string.Empty, p.Str("head", "ref") ?? string.Empty, p.Str("base", "ref") ?? string.Empty, p.Str("html_url") ?? string.Empty)
        {
            Reference = "#" + number,
            IsDraft = p.Bool("draft"),
            State = p.Bool("merged") || p.Str("merged_at") is not null ? "merged" : p.Str("state") == "closed" ? "closed" : "open",
            CreatedAt = p.Date("created_at"),
            UpdatedAt = p.Date("updated_at"),
            SourceCommit = p.Str("head", "sha"),
            SourceRepository = fork ? headRepository : null,
            SourceCloneUrl = fork ? p.Str("head", "repo", "clone_url") : null,
            Reviewers = [.. p.Items("requested_reviewers").Select(r => r.Str("login")).OfType<string>()],
        };
    }

    public override async Task<PullRequestDetails> GetPullRequestAsync(HostingRepository repository, string number, CancellationToken cancellationToken)
    {
        var pull = await Http.GetAsync($"{Repo(repository)}/pulls/{number}", cancellationToken).ConfigureAwait(false);
        var comments = await Http.GetAsync($"{Repo(repository)}/issues/{number}/comments?per_page=100", cancellationToken).ConfigureAwait(false);
        var threads = await ReviewThreadsAsync(repository, number, cancellationToken).ConfigureAwait(false);
        return new PullRequestDetails(ToPullRequest(pull, repository), pull.Str("body") ?? string.Empty, threads)
        {
            Comments = [.. comments.Items().Select(c => new ReviewComment(c.Str("user", "login") ?? string.Empty, c.Str("body") ?? string.Empty, c.Date("created_at") ?? DateTimeOffset.MinValue) { Url = c.Str("html_url") })],
        };
    }

    /// <summary>Review threads, with whether they're resolved and outdated (GraphQL only).</summary>
    private async Task<IReadOnlyList<ReviewThread>> ReviewThreadsAsync(HostingRepository repository, string number, CancellationToken cancellationToken)
    {
        const string Query = """
            query($owner: String!, $name: String!, $number: Int!) {
              repository(owner: $owner, name: $name) {
                pullRequest(number: $number) {
                  reviewThreads(first: 100) {
                    nodes {
                      id isResolved isOutdated path line startLine originalLine
                      comments(first: 50) { nodes { author { login } body createdAt url } }
                    }
                  }
                }
              }
            }
            """;
        using var variables = JsonDocument.Parse(Json.Object(("owner", repository.Owner), ("name", repository.Name), ("number", int.Parse(number, System.Globalization.CultureInfo.InvariantCulture))));
        var result = await Http.PostAsync(GraphQl, Json.Object(("query", Query), ("variables", variables.RootElement.Clone())), cancellationToken).ConfigureAwait(false);
        if (result.Items("errors").FirstOrDefault() is { ValueKind: JsonValueKind.Object } error)
        {
            throw new HostingHttpException(200, "GitHub GraphQL: " + error.Str("message"));
        }

        return [.. result.Items("data", "repository", "pullRequest", "reviewThreads", "nodes").Select(t =>
        {
            var comments = t.Items("comments", "nodes").Select(c => new ReviewComment(c.Str("author", "login") ?? "ghost", c.Str("body") ?? string.Empty, c.Date("createdAt") ?? DateTimeOffset.MinValue) { Url = c.Str("url") }).ToList();
            return new ReviewThread(t.Str("id") ?? string.Empty, t.Str("path") ?? string.Empty, (int)(t.Long("line") ?? t.Long("originalLine") ?? 1), comments)
            {
                StartLine = t.Long("startLine") is { } start ? (int)start : null,
                IsResolved = t.Bool("isResolved"),
                IsOutdated = t.Bool("isOutdated"),
                Url = comments.FirstOrDefault()?.Url,
            };
        })];
    }

    public override Task<PullRequestCheckout> GetCheckoutAsync(HostingRepository repository, PullRequest pullRequest, CancellationToken cancellationToken) =>
        Task.FromResult(pullRequest.SourceRepository is null
            ? new PullRequestCheckout(repository.Remote ?? "origin", pullRequest.SourceBranch, pullRequest.SourceBranch, Track: true)
            : new PullRequestCheckout(repository.Remote ?? "origin", $"refs/pull/{pullRequest.Number}/head", $"pr/{pullRequest.Number}", Track: false));

    public override async Task<PullRequest> CreatePullRequestAsync(HostingRepository repository, NewPullRequest request, CancellationToken cancellationToken)
    {
        var created = await Http.PostAsync(
            Repo(repository) + "/pulls",
            Json.Object(("title", request.Title), ("body", request.Description), ("head", request.SourceBranch), ("base", request.TargetBranch), ("draft", request.IsDraft)),
            cancellationToken).ConfigureAwait(false);
        return ToPullRequest(created, repository);
    }

    // ------------------------------------------------------------------ checks

    public override async Task<BranchStatus> GetBranchStatusAsync(HostingRepository repository, string branch, string? commit, CancellationToken cancellationToken)
    {
        var reference = HostingHttp.Escape(commit ?? branch);
        JsonElement runs, statuses;
        try
        {
            runs = await Http.GetAsync($"{Repo(repository)}/commits/{reference}/check-runs?per_page=50", cancellationToken).ConfigureAwait(false);
            statuses = await Http.GetAsync($"{Repo(repository)}/commits/{reference}/status", cancellationToken).ConfigureAwait(false);
        }
        catch (HostingHttpException ex) when (ex.Status is 404 or 422)
        {
            return new BranchStatus("none", []); // not pushed
        }

        var checks = new List<StatusCheck>();
        checks.AddRange(runs.Items("check_runs").Select(r => new StatusCheck(r.Str("name") ?? "check", CheckState(r.Str("status"), r.Str("conclusion")))
        {
            Description = r.Str("output", "title"),
            Url = r.Str("html_url"),
        }));
        checks.AddRange(statuses.Items("statuses").Select(s => new StatusCheck(s.Str("context") ?? "status", s.Str("state") switch
        {
            "success" => "success",
            "failure" or "error" => "failure",
            _ => "pending",
        })
        {
            Description = s.Str("description"),
            Url = s.Str("target_url"),
        }));
        return new BranchStatus(Summary(checks), checks) { Url = $"{repository.WebUrl}/commits/{HostingHttp.Escape(branch)}" };
    }

    private static string CheckState(string? status, string? conclusion) => status != "completed"
        ? "pending"
        : conclusion switch
        {
            "success" or "neutral" => "success",
            "skipped" => "skipped",
            "cancelled" => "cancelled",
            _ => "failure",
        };

    /// <summary>Any failure fails; any pending is pending; otherwise success (none when there are no checks).</summary>
    internal static string Summary(IReadOnlyList<StatusCheck> checks) =>
        checks.Count == 0 ? "none"
        : checks.Any(c => c.State == "failure") ? "failure"
        : checks.Any(c => c.State == "pending") ? "pending"
        : "success";

    // ------------------------------------------------------------------ issues

    public override async Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(HostingRepository repository, WorkItemQuery query, CancellationToken cancellationToken)
    {
        var text = query.Text?.Trim().TrimStart('#');
        IEnumerable<JsonElement> issues;
        if (query.AssignedToMe)
        {
            var me = await MeAsync(cancellationToken).ConfigureAwait(false);
            issues = (await Http.GetAsync($"{Repo(repository)}/issues?state=open&assignee={HostingHttp.Escape(me)}&per_page={HostingHttp.Number(query.Limit)}", cancellationToken).ConfigureAwait(false)).Items();
        }
        else if (text is { Length: > 0 } && text.All(char.IsAsciiDigit))
        {
            try
            {
                issues = [await Http.GetAsync($"{Repo(repository)}/issues/{text}", cancellationToken).ConfigureAwait(false)];
            }
            catch (HostingHttpException ex) when (ex.Status == 404)
            {
                issues = [];
            }

            // Also the recent ones whose number starts with what was typed.
            issues = issues.Concat((await Http.GetAsync($"{Repo(repository)}/issues?state=open&sort=updated&per_page=100", cancellationToken).ConfigureAwait(false)).Items()
                .Where(i => i.Str("number") is { } n && n.StartsWith(text, StringComparison.Ordinal) && n != text));
        }
        else
        {
            issues = (await Http.GetAsync($"{Repo(repository)}/issues?state=open&sort=updated&per_page=100", cancellationToken).ConfigureAwait(false)).Items()
                .Where(i => text is not { Length: > 0 } || (i.Str("title") ?? string.Empty).Contains(text, StringComparison.OrdinalIgnoreCase));
        }

        return [.. issues
            .Select(i => new WorkItem(i.Str("number") ?? string.Empty, i.Str("title") ?? string.Empty, "#" + i.Str("number"), i.Str("html_url") ?? string.Empty)
            {
                State = i.Str("state"),
                Type = i.Get("pull_request") is null ? "Issue" : "Pull request",
                AssignedTo = string.Join(", ", i.Items("assignees").Select(a => a.Str("login")).OfType<string>()) is { Length: > 0 } assignees ? assignees : null,
            })
            .Where(i => !query.AssignedToMe || i.Type == "Issue")
            .Take(query.Limit)];
    }

    // ------------------------------------------------------------------ publishing

    public override async Task<IReadOnlyList<HostingOwner>> GetOwnersAsync(CancellationToken cancellationToken)
    {
        var me = await MeAsync(cancellationToken).ConfigureAwait(false);
        var organizations = await Http.GetAsync(Api + "/user/orgs?per_page=100", cancellationToken).ConfigureAwait(false);
        return [new HostingOwner(me, me, "user"), .. organizations.Items().Select(o => o.Str("login")).OfType<string>().Select(login => new HostingOwner(login, login, "organization"))];
    }

    public override async Task<PublishedRepository> PublishAsync(NewRepository request, CancellationToken cancellationToken)
    {
        var me = await MeAsync(cancellationToken).ConfigureAwait(false);
        var url = string.Equals(request.Owner, me, StringComparison.OrdinalIgnoreCase) ? Api + "/user/repos" : $"{Api}/orgs/{request.Owner}/repos";
        var created = await Http.PostAsync(url, Json.Object(("name", request.Name), ("description", request.Description), ("private", request.IsPrivate)), cancellationToken).ConfigureAwait(false);
        return new PublishedRepository(created.Str("clone_url") ?? throw new HostingHttpException(201, "GitHub didn't return the new repository's URL."), created.Str("html_url") ?? string.Empty)
        {
            SshUrl = created.Str("ssh_url"),
        };
    }
}
