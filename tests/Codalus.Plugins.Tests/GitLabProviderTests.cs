using System.Text.Json;
using Codalus.Plugins.GitLab;
using Codalus.Plugins.Hosting;
using Codalus.Plugins.Proposed;
using Codalus.Tests;

namespace Codalus.Plugins.Tests;

/// <summary>The GitLab provider (ADR 0031) against a mock of GitLab's device flow and REST API (v4): nothing reaches GitLab.</summary>
public sealed class GitLabProviderTests : IAsyncLifetime
{
    private const string AccessToken = "glo-access-1";
    private const string RenewedToken = "glo-access-2";
    private const string ProjectPath = "/api/v4/projects/platform/web/storefront";

    private MockHttpServer _server = null!;
    private HostingTestContext _context = null!;
    private GitLabProvider _provider = null!;
    private int _polls;
    private int _expiresIn = 7200;
    private readonly List<string> _created = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Server => _server.BaseUrl;

    private HostingRepository Repository => _provider.ResolveRepository($"{Server}/platform/web/storefront.git")! with { Remote = "origin" };

    public ValueTask InitializeAsync()
    {
        _server = new MockHttpServer(Handle);
        _context = new HostingTestContext(("gitlab.serverUrl", Server), ("gitlab.oauthClientId", "gl-app"));
        _provider = new GitLabProvider(_context);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _provider.Dispose();
        await _server.DisposeAsync();
    }

    private static string S(string? value) => value is null ? "null" : JsonSerializer.Serialize(value);

    private MockHttpResponse Handle(MockHttpRequest request)
    {
        var path = request.Path;
        switch (request.Method, path)
        {
            case ("POST", "/oauth/authorize_device"):
                Assert.Equal("api", request.Form["scope"]);
                return MockHttpResponse.Json($$"""{ "device_code": "dc", "user_code": "0A44L90H", "verification_uri": {{S(Server + "/oauth/device")}}, "verification_uri_complete": {{S(Server + "/oauth/device?user_code=0A44L90H")}}, "expires_in": 300, "interval": 0 }""");
            case ("POST", "/oauth/token"):
                if (request.Form["grant_type"] == "refresh_token")
                {
                    return MockHttpResponse.Json($$"""{ "access_token": {{S(RenewedToken)}}, "refresh_token": "gl-refresh-2", "expires_in": 7200, "created_at": 1760000000 }""");
                }

                return ++_polls == 1
                    ? MockHttpResponse.Json("""{ "error": "authorization_pending", "error_description": "The authorization request is still pending." }""", 400)
                    : MockHttpResponse.Json($$"""{ "access_token": {{S(AccessToken)}}, "refresh_token": "gl-refresh-1", "expires_in": {{_expiresIn}}, "token_type": "Bearer" }""");
        }

        if (request.BearerToken is not (AccessToken or RenewedToken or "glpat-mock"))
        {
            return MockHttpResponse.Json("""{ "message": "401 Unauthorized" }""", 401);
        }

        switch (request.Method, path)
        {
            case ("GET", "/api/v4/user"):
                return MockHttpResponse.Json($$"""{ "id": 7, "username": "ada", "name": "Ada Tester", "web_url": {{S(Server + "/ada")}} }""");
            case ("GET", ProjectPath):
                return MockHttpResponse.Json("""{ "id": 99, "default_branch": "main", "web_url": "https://gitlab.com/platform/web/storefront", "visibility": "internal" }""");
            case ("GET", ProjectPath + "/merge_requests"):
                return MockHttpResponse.Json($$"""
                    [
                      { "iid": 5, "title": "Checkout flow", "state": "opened", "draft": false, "author": { "username": "ada" }, "source_branch": "checkout", "target_branch": "main",
                        "web_url": {{S(Server + "/platform/web/storefront/-/merge_requests/5")}}, "sha": "c5", "source_project_id": 99, "target_project_id": 99, "reviewers": [ { "username": "bo" } ], "created_at": "2026-10-09T12:00:00Z" },
                      { "iid": 6, "title": "Draft: Cart", "state": "opened", "draft": true, "author": { "username": "cy" }, "source_branch": "cart", "target_branch": "main",
                        "web_url": {{S(Server + "/platform/web/storefront/-/merge_requests/6")}}, "sha": "c6", "source_project_id": 123, "target_project_id": 99, "reviewers": [ { "username": "ada" } ] }
                    ]
                    """);
            case ("GET", ProjectPath + "/merge_requests/5"):
                return MockHttpResponse.Json($$"""{ "iid": 5, "title": "Checkout flow", "description": "Adds checkout.", "state": "opened", "author": { "username": "ada" }, "source_branch": "checkout", "target_branch": "main", "web_url": {{S(Server + "/platform/web/storefront/-/merge_requests/5")}}, "source_project_id": 99, "target_project_id": 99 }""");
            case ("GET", ProjectPath + "/merge_requests/5/discussions"):
                return MockHttpResponse.Json("""
                    [
                      { "id": "d1", "notes": [
                        { "id": 1, "body": "Handle an empty cart.", "author": { "username": "bo" }, "created_at": "2026-10-10T09:00:00Z", "system": false, "resolvable": true, "resolved": false,
                          "position": { "position_type": "text", "new_path": "src/Checkout.cs", "new_line": 21, "old_path": "src/Checkout.cs", "old_line": null } },
                        { "id": 2, "body": "Will do.", "author": { "username": "ada" }, "created_at": "2026-10-10T09:05:00Z", "system": false, "resolvable": true, "resolved": false } ] },
                      { "id": "d2", "notes": [
                        { "id": 3, "body": "Typo here.", "author": { "username": "bo" }, "created_at": "2026-10-10T09:00:00Z", "system": false, "resolvable": true, "resolved": true,
                          "position": { "position_type": "text", "new_path": "README.md", "new_line": 2 } } ] },
                      { "id": "d3", "notes": [ { "id": 4, "body": "Ship it!", "author": { "username": "bo" }, "created_at": "2026-10-10T10:00:00Z", "system": false } ] },
                      { "id": "d4", "notes": [ { "id": 5, "body": "added 2 commits", "author": { "username": "ada" }, "created_at": "2026-10-10T10:00:00Z", "system": true } ] }
                    ]
                    """);
            case ("POST", ProjectPath + "/merge_requests"):
                var body = JsonElement.Parse(request.Body);
                _created.Add($"merge request {body.GetProperty("source_branch").GetString()} -> {body.GetProperty("target_branch").GetString()}: {body.GetProperty("title").GetString()}");
                return MockHttpResponse.Json($$"""{ "iid": 7, "title": {{S(body.GetProperty("title").GetString())}}, "state": "opened", "draft": true, "author": { "username": "ada" }, "source_branch": {{S(body.GetProperty("source_branch").GetString())}}, "target_branch": "main", "web_url": "x" }""", 201);
            case ("GET", ProjectPath + "/pipelines"):
                return request.QueryValue("ref") == "nothing"
                    ? MockHttpResponse.Json("[]")
                    : MockHttpResponse.Json("""[ { "id": 501, "status": "failed", "web_url": "https://gitlab.com/p/501", "sha": "c5" } ]""");
            case ("GET", ProjectPath + "/pipelines/501/jobs"):
                return MockHttpResponse.Json("""[ { "name": "build", "stage": "build", "status": "success", "web_url": "j1" }, { "name": "test", "stage": "test", "status": "failed", "web_url": "j2" }, { "name": "deploy", "stage": "deploy", "status": "manual" } ]""");
            case ("GET", ProjectPath + "/issues"):
                return request.QueryValue("assignee_username") == "ada"
                    ? MockHttpResponse.Json("""[ { "iid": 31, "title": "Prices round wrong", "state": "opened", "web_url": "i31", "issue_type": "issue", "assignees": [ { "username": "ada" } ] } ]""")
                    : MockHttpResponse.Json(request.QueryValue("search") == "cart"
                        ? """[ { "iid": 32, "title": "Cart empties itself", "state": "opened", "web_url": "i32", "issue_type": "incident" } ]"""
                        : "[]");
            case ("GET", ProjectPath + "/issues/31"):
                return MockHttpResponse.Json("""{ "iid": 31, "title": "Prices round wrong", "state": "opened", "web_url": "i31" }""");
            case ("GET", "/api/v4/namespaces"):
                return MockHttpResponse.Json("""[ { "id": 7, "full_path": "ada", "kind": "user" }, { "id": 40, "full_path": "platform/web", "kind": "group" } ]""");
            case ("POST", "/api/v4/projects"):
                var project = JsonElement.Parse(request.Body);
                _created.Add($"project {project.GetProperty("name").GetString()} in {(project.TryGetProperty("namespace_id", out var ns) ? ns.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) : "mine")} {project.GetProperty("visibility").GetString()}");
                return MockHttpResponse.Json($$"""{ "http_url_to_repo": {{S(Server + "/platform/web/catalog.git")}}, "web_url": {{S(Server + "/platform/web/catalog")}}, "ssh_url_to_repo": "git@gitlab.com:platform/web/catalog.git" }""", 201);
        }

        return MockHttpResponse.NotFound();
    }

    [Fact]
    public void Remotes_with_nested_groups_resolve()
    {
        using var cloud = new GitLabProvider(new HostingTestContext());
        var repository = cloud.ResolveRepository("git@gitlab.com:platform/web/storefront.git");
        Assert.Equal(("platform/web/storefront", "platform/web", "storefront"), (repository?.Id, repository?.Owner, repository?.Name));
        Assert.Equal("https://gitlab.com/platform/web/storefront", repository?.WebUrl);
        Assert.Equal("platform/web/storefront", cloud.ResolveRepository("https://gitlab.com/platform/web/storefront")?.Id);
        Assert.Null(cloud.ResolveRepository("https://github.com/platform/storefront.git"));
        Assert.Null(cloud.ResolveRepository("https://gitlab.com/lonely"));
        Assert.Equal("platform/web/storefront", Repository.Id);
    }

    [Fact]
    public async Task Gitlab_coms_application_isnt_used_on_a_self_managed_server()
    {
        // The mock stands for a self-managed GitLab: gitlab.com's application id (the default) is refused, and nothing is asked.
        _context.Set("gitlab.oauthClientId", GitLabProvider.DotComClientId);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.SignInAsync(_context, Ct));
        Assert.Contains("needs its own OAuth application", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_server.Requests, r => r.Path == "/oauth/authorize_device");
    }

    [Fact]
    public async Task The_device_flow_signs_in_and_the_two_hour_token_is_refreshed()
    {
        _expiresIn = 10;
        var account = await _provider.SignInAsync(_context, Ct);
        Assert.Equal(("ada", "Ada Tester"), (account.Name, account.DisplayName));
        var code = Assert.Single(_context.Codes);
        Assert.Equal("0A44L90H", code.UserCode);
        Assert.Equal(Server + "/oauth/device?user_code=0A44L90H", code.VerificationUriComplete);
        Assert.Equal("main", (await _provider.GetRepositoryInfoAsync(Repository, Ct)).DefaultBranch);
        Assert.Contains(RenewedToken, _context.StoredSecrets["session"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Merge_requests_discussions_and_creation()
    {
        await _provider.SignInWithTokenAsync("glpat-mock", Ct);
        var all = await _provider.GetPullRequestsAsync(Repository, PullRequestFilter.All, Ct);
        Assert.Equal(["5", "6"], all.Select(m => m.Number));
        Assert.Equal("!5", all[0].Reference);
        Assert.True(all[1].IsDraft);
        Assert.Null(all[0].SourceRepository);
        Assert.NotNull(all[1].SourceRepository);
        Assert.Equal(["5"], (await _provider.GetPullRequestsAsync(Repository, PullRequestFilter.Mine, Ct)).Select(m => m.Number));
        Assert.Equal(["6"], (await _provider.GetPullRequestsAsync(Repository, PullRequestFilter.ReviewRequested, Ct)).Select(m => m.Number));

        var details = await _provider.GetPullRequestAsync(Repository, "5", Ct);
        Assert.Equal("Adds checkout.", details.Description);
        Assert.Equal([("src/Checkout.cs", 21, false, 2), ("README.md", 2, true, 1)], details.Threads.Select(t => (t.Path, t.Line, t.IsResolved, t.Comments.Count)));
        Assert.Equal("Ship it!", Assert.Single(details.Comments).Body);

        Assert.Equal(new PullRequestCheckout("origin", "checkout", "checkout", true), await _provider.GetCheckoutAsync(Repository, all[0], Ct));
        Assert.Equal(new PullRequestCheckout("origin", "refs/merge-requests/6/head", "mr/6", false), await _provider.GetCheckoutAsync(Repository, all[1], Ct));

        await _provider.CreatePullRequestAsync(Repository, new NewPullRequest("Wishlist", "wishlist", "main") { IsDraft = true }, Ct);
        Assert.Equal(["merge request wishlist -> main: Draft: Wishlist"], _created);
    }

    [Fact]
    public async Task The_latest_pipeline_and_its_jobs_give_the_status()
    {
        await _provider.SignInWithTokenAsync("glpat-mock", Ct);
        var status = await _provider.GetBranchStatusAsync(Repository, "checkout", null, Ct);
        Assert.Equal("failure", status.State);
        Assert.Equal([("build", "success"), ("test", "failure"), ("deploy", "skipped")], status.Checks.Select(c => (c.Name, c.State)));
        Assert.Equal("none", (await _provider.GetBranchStatusAsync(Repository, "nothing", null, Ct)).State);
    }

    [Fact]
    public async Task Issues_and_publishing()
    {
        await _provider.SignInWithTokenAsync("glpat-mock", Ct);
        Assert.Equal(["#31"], (await _provider.GetWorkItemsAsync(Repository, new WorkItemQuery { AssignedToMe = true }, Ct)).Select(i => i.Reference));
        Assert.Equal(["31"], (await _provider.GetWorkItemsAsync(Repository, new WorkItemQuery { Text = "31" }, Ct)).Select(i => i.Number));
        var incident = Assert.Single(await _provider.GetWorkItemsAsync(Repository, new WorkItemQuery { Text = "cart" }, Ct));
        Assert.Equal("Incident", incident.Type);

        Assert.Equal([("ada", "user"), ("platform/web", "group")], (await _provider.GetOwnersAsync(Ct)).Select(o => (o.Name, o.Kind)));
        var published = await _provider.PublishAsync(new NewRepository("40", "catalog"), Ct);
        Assert.Equal(Server + "/platform/web/catalog.git", published.CloneUrl);
        Assert.Equal(["project catalog in 40 private"], _created);
    }
}
