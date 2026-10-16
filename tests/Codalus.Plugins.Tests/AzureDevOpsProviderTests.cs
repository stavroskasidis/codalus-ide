using System.Text.Json;
using Codalus.Plugins.AzureDevOps;
using Codalus.Plugins.Hosting;
using Codalus.Plugins.Proposed;
using Codalus.Tests;

namespace Codalus.Plugins.Tests;

/// <summary>
/// The Azure DevOps provider (ADR 0031) against a mock of Microsoft Entra ID's device flow, the profile service and the
/// Azure DevOps REST API (7.1): nothing reaches Microsoft.
/// </summary>
public sealed class AzureDevOpsProviderTests : IAsyncLifetime
{
    private const string AccessToken = "ado-access-1";
    private const string RenewedToken = "ado-access-2";
    private const string Pat = "ado-pat";
    private const string MyId = "11111111-1111-1111-1111-111111111111";

    private MockHttpServer _server = null!;
    private HostingTestContext _context = null!;
    private AzureDevOpsProvider _provider = null!;
    private int _polls;
    private int _expiresIn = 3600;
    private readonly List<string> _created = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Server => _server.BaseUrl;

    private HostingRepository Repository => _provider.ResolveRepository($"{Server}/contoso/Web%20Apps/_git/portal")! with { Remote = "origin" };

    public ValueTask InitializeAsync()
    {
        _server = new MockHttpServer(Handle);
        _context = new HostingTestContext(
            ("azureDevOps.serverUrl", Server),
            ("azureDevOps.authorityUrl", Server + "/tenant"),
            ("azureDevOps.profileUrl", Server + "/vssps"),
            ("azureDevOps.oauthClientId", "ado-client"));
        _provider = new AzureDevOpsProvider(_context);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _provider.Dispose();
        await _server.DisposeAsync();
    }

    private static string S(string? value) => value is null ? "null" : JsonSerializer.Serialize(value);

    private bool Authorized(MockHttpRequest request) =>
        request.BearerToken is AccessToken or RenewedToken
        || request.Header("Authorization") == "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(":" + Pat));

    private MockHttpResponse Handle(MockHttpRequest request)
    {
        var path = request.Path;
        if (path == "/tenant/oauth2/v2.0/devicecode")
        {
            Assert.Contains("499b84ac-1321-427f-aa17-267ca6975798/.default", request.Form["scope"], StringComparison.Ordinal);
            return MockHttpResponse.Json($$"""{ "device_code": "dc", "user_code": "ABCD-EFGH", "verification_uri": "https://microsoft.com/devicelogin", "expires_in": 900, "interval": 0, "message": "To sign in, use a web browser." }""");
        }

        if (path == "/tenant/oauth2/v2.0/token")
        {
            if (request.Form["grant_type"] == "refresh_token")
            {
                return request.Form["refresh_token"] == "refresh-1"
                    ? MockHttpResponse.Json($$"""{ "access_token": {{S(RenewedToken)}}, "refresh_token": "refresh-2", "expires_in": 3600 }""")
                    : MockHttpResponse.Json("""{ "error": "invalid_grant", "error_description": "AADSTS70000: expired." }""", 400);
            }

            return ++_polls == 1
                ? MockHttpResponse.Json("""{ "error": "authorization_pending", "error_description": "AADSTS70016: pending." }""", 400)
                : MockHttpResponse.Json($$"""{ "access_token": {{S(AccessToken)}}, "refresh_token": "refresh-1", "expires_in": {{_expiresIn}} }""");
        }

        if (!Authorized(request))
        {
            return MockHttpResponse.Json("""{ "message": "TF400813: not authorized." }""", 401);
        }

        var repo = "/contoso/Web Apps/_apis/git/repositories/portal";
        switch (request.Method, path)
        {
            case ("GET", "/vssps/_apis/profile/profiles/me"):
                return MockHttpResponse.Json($$"""{ "id": {{S(MyId)}}, "displayName": "Ada Tester", "emailAddress": "ada@contoso.com", "publicAlias": {{S(MyId)}} }""");
            case ("GET", "/vssps/_apis/accounts"):
                Assert.Equal(MyId, request.QueryValue("memberId"));
                return MockHttpResponse.Json("""{ "value": [ { "accountName": "contoso" } ] }""");
            case ("GET", "/contoso/_apis/projects"):
                return MockHttpResponse.Json("""{ "value": [ { "name": "Web Apps" }, { "name": "Tools" } ] }""");
            case ("POST", "/contoso/Tools/_apis/git/repositories"):
                _created.Add("repository " + JsonElement.Parse(request.Body).GetProperty("name").GetString());
                return MockHttpResponse.Json($$"""{ "name": "cli", "remoteUrl": {{S(Server + "/contoso/Tools/_git/cli")}}, "webUrl": {{S(Server + "/contoso/Tools/_git/cli")}}, "sshUrl": "git@ssh.dev.azure.com:v3/contoso/Tools/cli" }""", 201);
            case ("GET", var p) when p == repo:
                return MockHttpResponse.Json("""{ "defaultBranch": "refs/heads/develop", "webUrl": "https://dev.azure.com/contoso/Web%20Apps/_git/portal", "project": { "visibility": "private" } }""");
            case ("GET", var p) when p == repo + "/pullrequests":
                Assert.Equal("active", request.QueryValue("searchCriteria.status"));
                return MockHttpResponse.Json($$"""
                    { "value": [
                      { "pullRequestId": 41, "title": "Login page", "status": "active", "isDraft": false, "createdBy": { "id": {{S(MyId)}}, "displayName": "Ada Tester", "uniqueName": "ada@contoso.com" },
                        "sourceRefName": "refs/heads/feature/login", "targetRefName": "refs/heads/develop", "creationDate": "2026-10-09T12:00:00Z", "lastMergeSourceCommit": { "commitId": "abc" }, "reviewers": [ { "id": "22", "displayName": "Bo", "vote": 0 } ] },
                      { "pullRequestId": 42, "title": "Dark mode", "status": "active", "isDraft": true, "createdBy": { "id": "22", "displayName": "Bo", "uniqueName": "bo@contoso.com" },
                        "sourceRefName": "refs/heads/dark", "targetRefName": "refs/heads/develop", "reviewers": [ { "id": {{S(MyId)}}, "displayName": "Ada Tester", "vote": 0 } ] }
                    ] }
                    """);
            case ("GET", var p) when p == repo + "/pullrequests/41":
                return MockHttpResponse.Json($$"""{ "pullRequestId": 41, "title": "Login page", "description": "Adds the login page.", "status": "active", "createdBy": { "displayName": "Ada Tester" }, "sourceRefName": "refs/heads/feature/login", "targetRefName": "refs/heads/develop" }""");
            case ("GET", var p) when p == repo + "/pullRequests/41/threads":
                return MockHttpResponse.Json("""
                    { "value": [
                      { "id": 1, "status": "active", "isDeleted": false, "threadContext": { "filePath": "/src/Login.cs", "rightFileStart": { "line": 10, "offset": 1 }, "rightFileEnd": { "line": 12, "offset": 1 } },
                        "comments": [ { "author": { "displayName": "Bo" }, "content": "Validate the password.", "publishedDate": "2026-10-10T09:00:00Z", "commentType": "text" } ] },
                      { "id": 2, "status": "fixed", "isDeleted": false, "threadContext": { "filePath": "/README.md", "rightFileStart": { "line": 1 }, "rightFileEnd": { "line": 1 } },
                        "comments": [ { "author": { "displayName": "Bo" }, "content": "Typo.", "publishedDate": "2026-10-10T09:00:00Z", "commentType": "text" } ] },
                      { "id": 3, "status": "active", "isDeleted": false,
                        "comments": [ { "author": { "displayName": "Bo" }, "content": "Nice work overall.", "publishedDate": "2026-10-10T09:00:00Z", "commentType": "text" } ] },
                      { "id": 4, "isDeleted": false, "comments": [ { "author": { "displayName": "Azure DevOps" }, "content": "Policy updated", "commentType": "system" } ] }
                    ] }
                    """);
            case ("POST", var p) when p == repo + "/pullrequests":
                var body = JsonElement.Parse(request.Body);
                _created.Add($"pull request {body.GetProperty("sourceRefName").GetString()} -> {body.GetProperty("targetRefName").GetString()} draft={body.GetProperty("isDraft").GetBoolean()}");
                return MockHttpResponse.Json($$"""{ "pullRequestId": 43, "title": {{S(body.GetProperty("title").GetString())}}, "status": "active", "createdBy": { "displayName": "Ada Tester" }, "sourceRefName": {{S(body.GetProperty("sourceRefName").GetString())}}, "targetRefName": {{S(body.GetProperty("targetRefName").GetString())}}, "isDraft": true }""", 201);
            case ("GET", "/contoso/Web Apps/_apis/build/builds"):
                Assert.Equal("refs/heads/feature/login", request.QueryValue("branchName"));
                return MockHttpResponse.Json("""
                    { "value": [
                      { "definition": { "name": "CI" }, "status": "inProgress", "buildNumber": "20261011.3", "sourceVersion": "abc", "_links": { "web": { "href": "https://dev.azure.com/b/3" } } },
                      { "definition": { "name": "CI" }, "status": "completed", "result": "failed", "buildNumber": "20261011.2", "sourceVersion": "old" },
                      { "definition": { "name": "Lint" }, "status": "completed", "result": "succeeded", "buildNumber": "20261011.1", "sourceVersion": "abc" }
                    ] }
                    """);
            case ("POST", "/contoso/Web Apps/_apis/wit/wiql"):
                var query = JsonElement.Parse(request.Body).GetProperty("query").GetString()!;
                return MockHttpResponse.Json(query.Contains("@Me", StringComparison.Ordinal)
                    ? """{ "workItems": [ { "id": 101 } ] }"""
                    : query.Contains("CONTAINS 'login'", StringComparison.Ordinal) ? """{ "workItems": [ { "id": 101 }, { "id": 102 } ] }""" : """{ "workItems": [] }""");
            case ("GET", "/contoso/_apis/wit/workitems"):
                var items = request.QueryValue("ids")!.Split(',').Select(id => id switch
                {
                    "101" => """{ "id": 101, "fields": { "System.Title": "Login fails", "System.State": "Active", "System.WorkItemType": "Bug", "System.AssignedTo": { "displayName": "Ada Tester" } } }""",
                    "102" => """{ "id": 102, "fields": { "System.Title": "Login page design", "System.State": "New", "System.WorkItemType": "User Story" } }""",
                    _ => "null",
                });
                return MockHttpResponse.Json("{ \"value\": [ " + string.Join(", ", items) + " ] }");
        }

        return MockHttpResponse.NotFound();
    }

    [Fact]
    public void Remotes_resolve_from_every_form_of_an_azure_devops_url()
    {
        var cloud = new AzureDevOpsProvider(new HostingTestContext());
        Assert.Equal("contoso/Web Apps/portal", cloud.ResolveRepository("https://contoso@dev.azure.com/contoso/Web%20Apps/_git/portal")?.Id);
        Assert.Equal("contoso/Web Apps", cloud.ResolveRepository("https://dev.azure.com/contoso/Web%20Apps/_git/portal")?.Owner);
        Assert.Equal("https://dev.azure.com/contoso/Web%20Apps/_git/portal", cloud.ResolveRepository("https://dev.azure.com/contoso/Web%20Apps/_git/portal")?.WebUrl);
        Assert.Equal("contoso/Web Apps/portal", cloud.ResolveRepository("git@ssh.dev.azure.com:v3/contoso/Web%20Apps/portal")?.Id);
        Assert.Equal("contoso/Tools/cli", cloud.ResolveRepository("https://contoso.visualstudio.com/DefaultCollection/Tools/_git/cli")?.Id);
        Assert.Equal("contoso/Tools/cli", cloud.ResolveRepository("https://contoso.visualstudio.com/Tools/_git/cli")?.Id);
        Assert.Equal("contoso/Tools/cli", cloud.ResolveRepository("contoso@vs-ssh.visualstudio.com:v3/contoso/Tools/cli")?.Id);
        Assert.Null(cloud.ResolveRepository("https://github.com/contoso/portal.git"));
        Assert.Null(cloud.ResolveRepository("https://dev.azure.com/contoso/Web%20Apps/_wiki/portal"));
        cloud.Dispose();

        using var server = new AzureDevOpsProvider(new HostingTestContext(("azureDevOps.serverUrl", "https://tfs.contoso.com/tfs")));
        Assert.Equal("DefaultCollection/Apps/site", server.ResolveRepository("https://tfs.contoso.com/tfs/DefaultCollection/Apps/_git/site")?.Id);
    }

    [Fact]
    public async Task Entra_device_flow_signs_in_and_an_expiring_token_is_refreshed()
    {
        _expiresIn = 30; // expires within the minute: the next call refreshes it
        var account = await _provider.SignInAsync(_context, Ct);
        Assert.Equal("ada@contoso.com", account.Name);
        Assert.Equal("Ada Tester", account.DisplayName);
        Assert.Equal("ABCD-EFGH", Assert.Single(_context.Codes).UserCode);
        Assert.Contains("refresh-1", _context.StoredSecrets["session"], StringComparison.Ordinal);

        var pulls = await _provider.GetPullRequestsAsync(Repository, PullRequestFilter.All, Ct);
        Assert.Equal(2, pulls.Count);
        Assert.Contains(RenewedToken, _context.StoredSecrets["session"], StringComparison.Ordinal);
        Assert.Contains(_server.Requests, r => r.Form.GetValueOrDefault("grant_type") == "refresh_token");
    }

    [Fact]
    public async Task A_personal_access_token_is_sent_as_basic_credentials()
    {
        var account = await _provider.SignInWithTokenAsync(Pat, Ct);
        Assert.Equal("ada@contoso.com", account.Name);
        Assert.Equal("develop", (await _provider.GetRepositoryInfoAsync(Repository, Ct)).DefaultBranch);
        Assert.StartsWith("Basic ", _server.Requests.Last().Header("Authorization"), StringComparison.Ordinal);
        await Assert.ThrowsAsync<HostingAuthenticationException>(() => _provider.SignInWithTokenAsync("wrong", Ct));
    }

    [Fact]
    public async Task Pull_requests_threads_and_creation()
    {
        await _provider.SignInWithTokenAsync(Pat, Ct);
        var all = await _provider.GetPullRequestsAsync(Repository, PullRequestFilter.All, Ct);
        Assert.Equal(["41", "42"], all.Select(p => p.Number));
        Assert.Equal(("!41", "feature/login", "develop"), (all[0].Reference, all[0].SourceBranch, all[0].TargetBranch));
        Assert.Equal($"{Server}/contoso/Web%20Apps/_git/portal/pullrequest/41", all[0].WebUrl);
        Assert.Equal(["41"], (await _provider.GetPullRequestsAsync(Repository, PullRequestFilter.Mine, Ct)).Select(p => p.Number));
        Assert.Equal(["42"], (await _provider.GetPullRequestsAsync(Repository, PullRequestFilter.ReviewRequested, Ct)).Select(p => p.Number));

        var details = await _provider.GetPullRequestAsync(Repository, "41", Ct);
        Assert.Equal("Adds the login page.", details.Description);
        Assert.Equal([("src/Login.cs", 12, false), ("README.md", 1, true)], details.Threads.Select(t => (t.Path, t.Line, t.IsResolved)));
        Assert.Equal(10, details.Threads[0].StartLine);
        Assert.Equal("Nice work overall.", Assert.Single(details.Comments).Body);
        Assert.Equal(new PullRequestCheckout("origin", "feature/login", "feature/login", true), await _provider.GetCheckoutAsync(Repository, all[0], Ct));

        var created = await _provider.CreatePullRequestAsync(Repository, new NewPullRequest("Sign up", "feature/signup", "develop") { IsDraft = true }, Ct);
        Assert.Equal("43", created.Number);
        Assert.Contains("pull request refs/heads/feature/signup -> refs/heads/develop draft=True", _created);
    }

    [Fact]
    public async Task Builds_of_the_branch_give_its_status()
    {
        await _provider.SignInWithTokenAsync(Pat, Ct);
        var status = await _provider.GetBranchStatusAsync(Repository, "feature/login", "abc", Ct);
        Assert.Equal("pending", status.State);
        Assert.Equal([("CI", "pending"), ("Lint", "success")], status.Checks.Select(c => (c.Name, c.State)));
        var any = await _provider.GetBranchStatusAsync(Repository, "feature/login", null, Ct);
        Assert.Equal("pending", any.State); // the latest CI build counts, not the failed older one
    }

    [Fact]
    public async Task Work_items_come_from_wiql()
    {
        await _provider.SignInWithTokenAsync(Pat, Ct);
        var mine = await _provider.GetWorkItemsAsync(Repository, new WorkItemQuery { AssignedToMe = true }, Ct);
        var bug = Assert.Single(mine);
        Assert.Equal(("101", "#101", "Bug", "Active", "Ada Tester"), (bug.Number, bug.Reference, bug.Type, bug.State, bug.AssignedTo));
        Assert.Equal($"{Server}/contoso/Web%20Apps/_workitems/edit/101", bug.WebUrl);
        Assert.Equal(["101", "102"], (await _provider.GetWorkItemsAsync(Repository, new WorkItemQuery { Text = "login" }, Ct)).Select(i => i.Number));
        Assert.Equal(["102"], (await _provider.GetWorkItemsAsync(Repository, new WorkItemQuery { Text = "#102" }, Ct)).Select(i => i.Number));
    }

    [Fact]
    public async Task Publishing_creates_a_repository_in_a_project()
    {
        await _provider.SignInWithTokenAsync(Pat, Ct);
        var owners = await _provider.GetOwnersAsync(Ct);
        Assert.Equal(["contoso/Web Apps", "contoso/Tools"], owners.Select(o => o.Id));
        var published = await _provider.PublishAsync(new NewRepository("contoso/Tools", "cli"), Ct);
        Assert.Equal($"{Server}/contoso/Tools/_git/cli", published.CloneUrl);
        Assert.Equal(["repository cli"], _created);
    }
}
