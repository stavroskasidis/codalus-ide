using System.Text.Json;
using Codalus.Plugins.Hosting;
using Codalus.Plugins.Proposed;

namespace Codalus.Plugins.GitLab;

/// <summary>
/// GitLab.com and self-managed GitLab through the REST API (v4). Sign-in: the OAuth device flow (GitLab 17.2 and later)
/// of the application in <c>gitlab.oauthClientId</c>, whose tokens expire after two hours and are refreshed, or a personal
/// access token. Projects are addressed by their path (<c>group/subgroup/project</c>).
/// </summary>
internal sealed class GitLabProvider : HostingProviderBase
{
    private const string Scope = "api";

    public GitLabProvider(IPluginContext context)
        : base("gitlab", context)
    {
        Http.Headers["Accept"] = "application/json";
    }

    protected override string SettingsSection => "gitlab";

    private string ServerUrl => Setting("gitlab.serverUrl", "https://gitlab.com");

    private string Api => ServerUrl + "/api/v4";

    private string ClientId => Settings.GetValue("gitlab.oauthClientId", string.Empty);

    protected override string ServerHost => RemoteUrl.HostOf(ServerUrl);

    public override HostingRepository? ResolveRepository(string remoteUrl)
    {
        if (RemoteUrl.Parse(remoteUrl) is not { } url || url.Host != ServerHost || url.Segments.Length < 2)
        {
            return null;
        }

        var segments = url.Segments;
        var path = string.Join('/', segments);
        return new HostingRepository(path, string.Join('/', segments[..^1]), segments[^1], $"{ServerUrl}/{path}") { Host = ServerHost };
    }

    private string Project(HostingRepository repository) => $"{Api}/projects/{Uri.EscapeDataString(repository.Id)}";

    // ------------------------------------------------------------------ account

    public override async Task<HostingAccount> SignInAsync(IDeviceCodePrompt prompt, CancellationToken cancellationToken)
    {
        var token = await DeviceFlow.SignInAsync(
            Http,
            new DeviceFlowEndpoints(ServerUrl + "/oauth/authorize_device", ServerUrl + "/oauth/token", ClientId, Scope),
            prompt,
            cancellationToken).ConfigureAwait(false);
        return await StoreAsync(token, cancellationToken).ConfigureAwait(false);
    }

    public override Task<HostingAccount> SignInWithTokenAsync(string token, CancellationToken cancellationToken) =>
        StoreAsync(new OAuthToken(token.Trim(), null, null), cancellationToken);

    private async Task<HostingAccount> StoreAsync(OAuthToken token, CancellationToken cancellationToken)
    {
        var user = await GetWithTokenAsync(Api + "/user", "bearer", token.AccessToken, cancellationToken).ConfigureAwait(false);
        var session = new HostingSession
        {
            Host = ServerHost,
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken,
            ExpiresAt = token.ExpiresAt,
            Account = user.Str("username") ?? throw new HostingHttpException(200, "GitLab didn't say who the token belongs to."),
            DisplayName = user.Str("name"),
            Url = user.Str("web_url"),
            UserId = user.Str("id"),
        };
        await SaveSessionAsync(session, cancellationToken).ConfigureAwait(false);
        return AccountOf(session);
    }

    protected override async Task<HostingSession> RefreshAsync(HostingSession session, CancellationToken cancellationToken)
    {
        var token = await DeviceFlow.RefreshAsync(
            Http,
            ServerUrl + "/oauth/token",
            new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = session.RefreshToken!, ["client_id"] = ClientId },
            cancellationToken).ConfigureAwait(false);
        return session with { AccessToken = token.AccessToken, RefreshToken = token.RefreshToken ?? session.RefreshToken, ExpiresAt = token.ExpiresAt };
    }

    private async Task<string> MeAsync(CancellationToken cancellationToken) => (await RequireSessionAsync(cancellationToken).ConfigureAwait(false)).Account;

    // ------------------------------------------------------------------ repository and merge requests

    public override async Task<HostingRepositoryInfo> GetRepositoryInfoAsync(HostingRepository repository, CancellationToken cancellationToken)
    {
        var info = await Http.GetAsync(Project(repository), cancellationToken).ConfigureAwait(false);
        return new HostingRepositoryInfo(info.Str("default_branch") ?? "main", info.Str("web_url") ?? repository.WebUrl) { IsPrivate = info.Str("visibility") != "public" };
    }

    public override async Task<IReadOnlyList<PullRequest>> GetPullRequestsAsync(HostingRepository repository, PullRequestFilter filter, CancellationToken cancellationToken)
    {
        var me = await MeAsync(cancellationToken).ConfigureAwait(false);
        var requests = await Http.GetAsync(Project(repository) + "/merge_requests?state=opened&order_by=updated_at&per_page=50", cancellationToken).ConfigureAwait(false);
        return [.. requests.Items()
            .Where(m => filter switch
            {
                PullRequestFilter.Mine => string.Equals(m.Str("author", "username"), me, StringComparison.OrdinalIgnoreCase),
                PullRequestFilter.ReviewRequested => m.Items("reviewers").Any(r => string.Equals(r.Str("username"), me, StringComparison.OrdinalIgnoreCase)),
                _ => true,
            })
            .Select(ToPullRequest)];
    }

    private static PullRequest ToPullRequest(JsonElement m)
    {
        var iid = m.Str("iid") ?? string.Empty;
        var fork = m.Str("source_project_id") is { } source && m.Str("target_project_id") is { } target && source != target;
        return new PullRequest(iid, m.Str("title") ?? string.Empty, m.Str("author", "username") ?? string.Empty, m.Str("source_branch") ?? string.Empty, m.Str("target_branch") ?? string.Empty, m.Str("web_url") ?? string.Empty)
        {
            Reference = "!" + iid,
            IsDraft = m.Bool("draft") || m.Bool("work_in_progress"),
            State = m.Str("state") switch { "merged" => "merged", "closed" => "closed", _ => "open" },
            CreatedAt = m.Date("created_at"),
            UpdatedAt = m.Date("updated_at"),
            SourceCommit = m.Str("sha"),
            SourceRepository = fork ? m.Str("source_project_id") : null,
            Reviewers = [.. m.Items("reviewers").Select(r => r.Str("username")).OfType<string>()],
        };
    }

    public override async Task<PullRequestDetails> GetPullRequestAsync(HostingRepository repository, string number, CancellationToken cancellationToken)
    {
        var request = await Http.GetAsync($"{Project(repository)}/merge_requests/{number}", cancellationToken).ConfigureAwait(false);
        var discussions = await Http.GetAsync($"{Project(repository)}/merge_requests/{number}/discussions?per_page=100", cancellationToken).ConfigureAwait(false);
        var threads = new List<ReviewThread>();
        var comments = new List<ReviewComment>();
        foreach (var discussion in discussions.Items())
        {
            var notes = discussion.Items("notes").Where(n => !n.Bool("system")).ToList();
            if (notes.Count == 0)
            {
                continue;
            }

            var mapped = notes.Select(n => new ReviewComment(n.Str("author", "username") ?? string.Empty, n.Str("body") ?? string.Empty, n.Date("created_at") ?? DateTimeOffset.MinValue)).ToList();
            if (notes[0].Get("position") is { } position && (position.Str("new_path") ?? position.Str("old_path")) is { } path)
            {
                var resolvable = notes.Where(n => n.Bool("resolvable")).ToList();
                threads.Add(new ReviewThread(discussion.Str("id") ?? string.Empty, path, (int)(position.Long("new_line") ?? position.Long("old_line") ?? 1), mapped)
                {
                    IsResolved = resolvable.Count > 0 && resolvable.All(n => n.Bool("resolved")),
                    Url = $"{request.Str("web_url")}#note_{notes[0].Str("id")}",
                });
            }
            else
            {
                comments.AddRange(mapped);
            }
        }

        return new PullRequestDetails(ToPullRequest(request), request.Str("description") ?? string.Empty, threads) { Comments = comments };
    }

    public override Task<PullRequestCheckout> GetCheckoutAsync(HostingRepository repository, PullRequest pullRequest, CancellationToken cancellationToken) =>
        Task.FromResult(pullRequest.SourceRepository is null
            ? new PullRequestCheckout(repository.Remote ?? "origin", pullRequest.SourceBranch, pullRequest.SourceBranch, Track: true)
            : new PullRequestCheckout(repository.Remote ?? "origin", $"refs/merge-requests/{pullRequest.Number}/head", $"mr/{pullRequest.Number}", Track: false));

    public override async Task<PullRequest> CreatePullRequestAsync(HostingRepository repository, NewPullRequest request, CancellationToken cancellationToken)
    {
        var title = request.IsDraft && !request.Title.StartsWith("Draft:", StringComparison.OrdinalIgnoreCase) ? "Draft: " + request.Title : request.Title;
        var created = await Http.PostAsync(
            Project(repository) + "/merge_requests",
            Json.Object(("source_branch", request.SourceBranch), ("target_branch", request.TargetBranch), ("title", title), ("description", request.Description)),
            cancellationToken).ConfigureAwait(false);
        return ToPullRequest(created);
    }

    // ------------------------------------------------------------------ pipelines

    public override async Task<BranchStatus> GetBranchStatusAsync(HostingRepository repository, string branch, string? commit, CancellationToken cancellationToken)
    {
        var filter = commit is null ? "ref=" + Uri.EscapeDataString(branch) : "sha=" + Uri.EscapeDataString(commit);
        var pipelines = await Http.GetAsync($"{Project(repository)}/pipelines?{filter}&per_page=1&order_by=id&sort=desc", cancellationToken).ConfigureAwait(false);
        if (pipelines.Items().FirstOrDefault() is not { ValueKind: JsonValueKind.Object } pipeline)
        {
            return new BranchStatus("none", []);
        }

        var jobs = await Http.GetAsync($"{Project(repository)}/pipelines/{pipeline.Str("id")}/jobs?per_page=50", cancellationToken).ConfigureAwait(false);
        var checks = jobs.Items().Select(j => new StatusCheck(j.Str("name") ?? "job", State(j.Str("status"))) { Description = j.Str("stage"), Url = j.Str("web_url") }).ToList();
        return new BranchStatus(State(pipeline.Str("status")), checks) { Url = pipeline.Str("web_url") };
    }

    private static string State(string? status) => status switch
    {
        "success" => "success",
        "failed" => "failure",
        "canceled" or "canceling" => "cancelled",
        "skipped" or "manual" => "skipped",
        _ => "pending",
    };

    // ------------------------------------------------------------------ issues

    public override async Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(HostingRepository repository, WorkItemQuery query, CancellationToken cancellationToken)
    {
        var text = query.Text?.Trim().TrimStart('#');
        IEnumerable<JsonElement> issues;
        if (query.AssignedToMe)
        {
            var me = await MeAsync(cancellationToken).ConfigureAwait(false);
            issues = (await Http.GetAsync($"{Project(repository)}/issues?state=opened&assignee_username={Uri.EscapeDataString(me)}&per_page={HostingHttp.Number(query.Limit)}", cancellationToken).ConfigureAwait(false)).Items();
        }
        else if (text is { Length: > 0 } && text.All(char.IsAsciiDigit))
        {
            try
            {
                issues = [await Http.GetAsync($"{Project(repository)}/issues/{text}", cancellationToken).ConfigureAwait(false)];
            }
            catch (HostingHttpException ex) when (ex.Status == 404)
            {
                issues = [];
            }
        }
        else
        {
            var search = text is { Length: > 0 } ? "&search=" + Uri.EscapeDataString(text) : string.Empty;
            issues = (await Http.GetAsync($"{Project(repository)}/issues?state=opened&order_by=updated_at&per_page={HostingHttp.Number(query.Limit)}{search}", cancellationToken).ConfigureAwait(false)).Items();
        }

        return [.. issues.Take(query.Limit).Select(i => new WorkItem(i.Str("iid") ?? string.Empty, i.Str("title") ?? string.Empty, "#" + i.Str("iid"), i.Str("web_url") ?? string.Empty)
        {
            State = i.Str("state"),
            Type = i.Str("issue_type") is { } type ? char.ToUpperInvariant(type[0]) + type[1..] : "Issue",
            AssignedTo = string.Join(", ", i.Items("assignees").Select(a => a.Str("username")).OfType<string>()) is { Length: > 0 } assignees ? assignees : null,
        })];
    }

    // ------------------------------------------------------------------ publishing

    public override async Task<IReadOnlyList<HostingOwner>> GetOwnersAsync(CancellationToken cancellationToken)
    {
        var namespaces = await Http.GetAsync(Api + "/namespaces?per_page=100", cancellationToken).ConfigureAwait(false);
        return [.. namespaces.Items()
            .Where(n => n.Str("id") is not null)
            .Select(n => new HostingOwner(n.Str("id")!, n.Str("full_path") ?? n.Str("path") ?? string.Empty, n.Str("kind") == "user" ? "user" : "group"))];
    }

    public override async Task<PublishedRepository> PublishAsync(NewRepository request, CancellationToken cancellationToken)
    {
        var created = await Http.PostAsync(
            Api + "/projects",
            Json.Object(("name", request.Name), ("namespace_id", long.TryParse(request.Owner, out var id) ? id : null), ("description", request.Description), ("visibility", request.IsPrivate ? "private" : "public")),
            cancellationToken).ConfigureAwait(false);
        return new PublishedRepository(created.Str("http_url_to_repo") ?? throw new HostingHttpException(201, "GitLab didn't return the new project's URL."), created.Str("web_url") ?? string.Empty)
        {
            SshUrl = created.Str("ssh_url_to_repo"),
        };
    }
}
