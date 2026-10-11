using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DaedalusStudio.Tests;

/// <summary>A pull request on <see cref="MockGitHub"/>.</summary>
internal sealed record MockPullRequest(int Number, string Title, string Author, string Head, string Base)
{
    public string Body { get; init; } = string.Empty;

    public bool Draft { get; init; }

    /// <summary>The head commit (its checks are the mock's checks).</summary>
    public string Sha { get; init; } = new('a', 40);

    /// <summary>A fork (<c>owner/name</c>), or null for a branch of the repository.</summary>
    public string? HeadRepository { get; init; }

    public IReadOnlyList<string> Reviewers { get; init; } = [];
}

/// <summary>A review thread on <see cref="MockGitHub"/>: on <see cref="Path"/> at <see cref="Line"/>, with comments (author, body).</summary>
internal sealed record MockReviewThread(string Path, int Line, IReadOnlyList<(string Author, string Body)> Comments)
{
    public bool Resolved { get; init; }
}

/// <summary>An issue on <see cref="MockGitHub"/>.</summary>
internal sealed record MockIssue(int Number, string Title, string? Assignee);

/// <summary>A check run on <see cref="MockGitHub"/>: status <c>queued</c>, <c>in_progress</c> or <c>completed</c>, with a conclusion.</summary>
internal sealed record MockCheckRun(string Name, string Status, string? Conclusion);

/// <summary>
/// GitHub's REST and GraphQL APIs and the OAuth device flow as a GitHub Enterprise Server answers them (the API under
/// <c>/api/v3</c>, GraphQL at <c>/api/graphql</c>), for one repository; set <c>github.serverUrl</c> to
/// <see cref="ServerUrl"/>. With <see cref="GitDirectory"/> it also serves that bare repository over git's dumb HTTP
/// protocol at <c>/&lt;owner&gt;/&lt;name&gt;.git</c>, so clones and fetches work. Nothing here talks to GitHub.
/// </summary>
internal sealed class MockGitHub : IAsyncDisposable
{
    public const string ClientId = "mock-client-id";
    public const string Token = "gho_mock_token_0123456789";
    public const string UserCode = "WDJB-MJHT";

    private readonly MockHttpServer _server;
    private int _polls;

    public MockGitHub(string owner, string name)
    {
        Owner = owner;
        Name = name;
        _server = new MockHttpServer(Handle);
    }

    public string Owner { get; }

    public string Name { get; }

    public string Login { get; set; } = "ada";

    /// <summary>The value for <c>github.serverUrl</c>.</summary>
    public string ServerUrl => _server.BaseUrl;

    /// <summary>The repository's HTTP remote URL on the mock.</summary>
    public string RemoteUrl => $"{ServerUrl}/{Owner}/{Name}.git";

    /// <summary>A bare repository served over git's dumb HTTP protocol (run <c>git update-server-info</c> in it first).</summary>
    public string? GitDirectory { get; set; }

    /// <summary>How many times the device flow answers <c>authorization_pending</c> before the token.</summary>
    public int PendingPolls { get; set; } = 1;

    public List<MockPullRequest> PullRequests { get; } = [];

    public Dictionary<int, List<MockReviewThread>> Threads { get; } = [];

    public List<MockIssue> Issues { get; } = [];

    public List<MockCheckRun> CheckRuns { get; } = [];

    /// <summary>The pull requests and repositories created through the API.</summary>
    public List<string> Created { get; } = [];

    public IEnumerable<MockHttpRequest> Requests => _server.Requests;

    private static string S(string? value) => value is null ? "null" : "\"" + JsonEncodedText.Encode(value).ToString() + "\"";

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    private string Web => $"{ServerUrl}/{Owner}/{Name}";

    private MockHttpResponse Handle(MockHttpRequest request)
    {
        var path = request.Path;
        if (GitDirectory is { } git && path.StartsWith($"/{Owner}/{Name}.git/", StringComparison.Ordinal))
        {
            var relative = path[$"/{Owner}/{Name}.git/".Length..];
            return relative == "info/refs" && request.QueryValue("service") is not null
                ? MockHttpResponse.Text(File.ReadAllText(Path.Combine(git, "info", "refs"))) // a dumb server: no smart content type
                : MockHttpResponse.File(Path.Combine(git, relative.Replace('/', Path.DirectorySeparatorChar)));
        }

        switch (request.Method, path)
        {
            case ("POST", "/login/device/code"):
                return request.Form.GetValueOrDefault("client_id") == ClientId
                    ? MockHttpResponse.Json($$"""{ "device_code": "device-1", "user_code": {{S(UserCode)}}, "verification_uri": {{S(ServerUrl + "/login/device")}}, "expires_in": 900, "interval": 0 }""")
                    : MockHttpResponse.Json("""{ "error": "unauthorized_client", "error_description": "Unknown client." }""", 401);
            case ("POST", "/login/oauth/access_token"):
                return Interlocked.Increment(ref _polls) <= PendingPolls
                    ? MockHttpResponse.Json("""{ "error": "authorization_pending" }""")
                    : MockHttpResponse.Json($$"""{ "access_token": {{S(Token)}}, "token_type": "bearer", "scope": "repo,read:org,workflow" }""");
        }

        if (!path.StartsWith("/api/", StringComparison.Ordinal))
        {
            return MockHttpResponse.NotFound();
        }

        if (request.BearerToken != Token)
        {
            return MockHttpResponse.Json("""{ "message": "Bad credentials" }""", 401);
        }

        var repo = $"/api/v3/repos/{Owner}/{Name}";
        if (path == "/api/v3/user")
        {
            return MockHttpResponse.Json($$"""{ "login": {{S(Login)}}, "id": 1, "name": "Ada Tester", "html_url": {{S(ServerUrl + "/" + Login)}} }""");
        }

        if (path == "/api/v3/user/orgs")
        {
            return MockHttpResponse.Json("""[ { "login": "daedalus-org" } ]""");
        }

        if (request.Method == "POST" && (path == "/api/v3/user/repos" || path.StartsWith("/api/v3/orgs/", StringComparison.Ordinal)))
        {
            using var body = JsonDocument.Parse(request.Body);
            var repositoryName = body.RootElement.GetProperty("name").GetString()!;
            var owner = path == "/api/v3/user/repos" ? Login : path.Split('/')[4];
            Created.Add($"repository {owner}/{repositoryName}");
            return MockHttpResponse.Json($$"""{ "clone_url": {{S($"{ServerUrl}/{owner}/{repositoryName}.git")}}, "html_url": {{S($"{ServerUrl}/{owner}/{repositoryName}")}}, "ssh_url": {{S($"git@mock:{owner}/{repositoryName}.git")}} }""", 201);
        }

        if (path == "/api/graphql" && request.Method == "POST")
        {
            using var body = JsonDocument.Parse(request.Body);
            var number = body.RootElement.GetProperty("variables").GetProperty("number").GetInt32();
            return MockHttpResponse.Json($$"""{ "data": { "repository": { "pullRequest": { "reviewThreads": { "nodes": [ {{string.Join(", ", (Threads.GetValueOrDefault(number) ?? []).Select((t, i) => ThreadJson(t, i)))}} ] } } } } }""");
        }

        if (path == repo)
        {
            return MockHttpResponse.Json($$"""{ "default_branch": "main", "html_url": {{S(Web)}}, "private": true }""");
        }

        if (path == repo + "/pulls" && request.Method == "GET")
        {
            return MockHttpResponse.Json("[ " + string.Join(", ", PullRequests.Select(PullJson)) + " ]");
        }

        if (path == repo + "/pulls" && request.Method == "POST")
        {
            using var body = JsonDocument.Parse(request.Body);
            var root = body.RootElement;
            var pull = new MockPullRequest(PullRequests.Count == 0 ? 1 : PullRequests.Max(p => p.Number) + 1, root.GetProperty("title").GetString()!, Login, root.GetProperty("head").GetString()!, root.GetProperty("base").GetString()!)
            {
                Body = root.TryGetProperty("body", out var text) ? text.GetString() ?? string.Empty : string.Empty,
                Draft = root.TryGetProperty("draft", out var draft) && draft.GetBoolean(),
            };
            PullRequests.Add(pull);
            Created.Add($"pull request #{pull.Number} {pull.Head} -> {pull.Base}");
            return MockHttpResponse.Json(PullJson(pull), 201);
        }

        if (path.StartsWith(repo + "/pulls/", StringComparison.Ordinal) && int.TryParse(path[(repo.Length + 7)..], out var pullNumber))
        {
            return PullRequests.FirstOrDefault(p => p.Number == pullNumber) is { } found ? MockHttpResponse.Json(PullJson(found)) : MockHttpResponse.NotFound();
        }

        if (path.StartsWith(repo + "/issues/", StringComparison.Ordinal) && path.EndsWith("/comments", StringComparison.Ordinal))
        {
            return MockHttpResponse.Json($$"""[ { "user": { "login": "bo" }, "body": "Looks good overall.", "created_at": "2026-10-10T09:00:00Z", "html_url": {{S(Web + "/pull/1#issuecomment-1")}} } ]""");
        }

        if (path.StartsWith(repo + "/issues/", StringComparison.Ordinal) && int.TryParse(path[(repo.Length + 8)..], out var issueNumber))
        {
            return Issues.FirstOrDefault(i => i.Number == issueNumber) is { } issue ? MockHttpResponse.Json(IssueJson(issue)) : MockHttpResponse.NotFound();
        }

        if (path == repo + "/issues")
        {
            var assignee = request.QueryValue("assignee");
            return MockHttpResponse.Json("[ " + string.Join(", ", Issues.Where(i => assignee is null || i.Assignee == assignee).Select(IssueJson)) + " ]");
        }

        if (path.StartsWith(repo + "/commits/", StringComparison.Ordinal) && path.EndsWith("/check-runs", StringComparison.Ordinal))
        {
            return MockHttpResponse.Json($$"""{ "total_count": {{N(CheckRuns.Count)}}, "check_runs": [ {{string.Join(", ", CheckRuns.Select(c => $$"""{ "name": {{S(c.Name)}}, "status": {{S(c.Status)}}, "conclusion": {{S(c.Conclusion)}}, "html_url": {{S(Web + "/actions")}}, "output": { "title": {{S(c.Name + " " + (c.Conclusion ?? c.Status))}} } }"""))}} ] }""");
        }

        if (path.StartsWith(repo + "/commits/", StringComparison.Ordinal) && path.EndsWith("/status", StringComparison.Ordinal))
        {
            return MockHttpResponse.Json("""{ "state": "success", "statuses": [] }""");
        }

        return MockHttpResponse.NotFound();
    }

    private string PullJson(MockPullRequest p)
    {
        var headRepository = p.HeadRepository ?? $"{Owner}/{Name}";
        return $$"""
            {
              "number": {{N(p.Number)}}, "title": {{S(p.Title)}}, "body": {{S(p.Body)}}, "state": "open", "draft": {{(p.Draft ? "true" : "false")}},
              "html_url": {{S($"{Web}/pull/{p.Number}")}}, "user": { "login": {{S(p.Author)}} },
              "created_at": "2026-10-09T12:00:00Z", "updated_at": "2026-10-10T12:00:00Z",
              "head": { "ref": {{S(p.Head)}}, "sha": {{S(p.Sha)}}, "repo": { "full_name": {{S(headRepository)}}, "clone_url": {{S($"{ServerUrl}/{headRepository}.git")}} } },
              "base": { "ref": {{S(p.Base)}} },
              "requested_reviewers": [ {{string.Join(", ", p.Reviewers.Select(r => $$"""{ "login": {{S(r)}} }"""))}} ]
            }
            """;
    }

    private string ThreadJson(MockReviewThread t, int index) => $$"""
        {
          "id": {{S("thread-" + N(index))}}, "isResolved": {{(t.Resolved ? "true" : "false")}}, "isOutdated": false, "path": {{S(t.Path)}}, "line": {{N(t.Line)}}, "startLine": null, "originalLine": {{N(t.Line)}},
          "comments": { "nodes": [ {{string.Join(", ", t.Comments.Select((c, i) => $$"""{ "author": { "login": {{S(c.Author)}} }, "body": {{S(c.Body)}}, "createdAt": "2026-10-10T10:0{{N(i)}}:00Z", "url": {{S($"{Web}/pull/1#discussion_r{index}{i}")}} }"""))}} ] }
        }
        """;

    private string IssueJson(MockIssue i) => $$"""
        { "number": {{N(i.Number)}}, "title": {{S(i.Title)}}, "state": "open", "html_url": {{S($"{Web}/issues/{i.Number}")}}, "assignees": [ {{(i.Assignee is null ? string.Empty : $$"""{ "login": {{S(i.Assignee)}} }""")}} ] }
        """;

    /// <summary>A short report of the requests, for assertion messages.</summary>
    public string Describe() => new StringBuilder().AppendJoin('\n', _server.Requests.Select(r => $"{r.Method} {r.Path}{(r.Query.Length > 0 ? "?" + r.Query : string.Empty)}")).ToString();

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}
