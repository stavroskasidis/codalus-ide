using System.Text.Json;
using Codalus.Plugins.AzureDevOps;
using Codalus.Plugins.Hosting;
using Codalus.Tests;

namespace Codalus.Plugins.Tests;

/// <summary>
/// The Azure DevOps provider on an Azure DevOps Server (ADR 0087), against a mock of a 2020 server under <c>/tfs</c>: it
/// takes REST API 6.0 at most, has no profile service (its connection data says who is signed in), lists project
/// collections, and takes a personal access token or the operating system's account (here, a request with no
/// Authorization header stands for a Negotiate handshake the mock can't do). Nothing reaches a real server.
/// </summary>
public sealed class AzureDevOpsServerProviderTests : IAsyncLifetime
{
    private const string Pat = "server-pat";
    private const string MyId = "33333333-3333-3333-3333-333333333333";

    private MockHttpServer _server = null!;
    private HostingTestContext _context = null!;
    private AzureDevOpsProvider _provider = null!;
    private bool _takesWindowsAccount = true;
    private readonly List<string> _created = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Server => _server.BaseUrl + "/tfs";

    private HostingRepository Repository => _provider.ResolveRepository($"{Server}/DefaultCollection/Web%20Apps/_git/portal")! with { Remote = "origin" };

    public ValueTask InitializeAsync()
    {
        _server = new MockHttpServer(Handle);
        _context = new HostingTestContext(("azureDevOps.serverUrl", Server));
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
        request.Header("Authorization") is { } authorization
            ? authorization == "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(":" + Pat))
            : _takesWindowsAccount;

    private MockHttpResponse Handle(MockHttpRequest request)
    {
        if (!Authorized(request))
        {
            return MockHttpResponse.Json("""{ "message": "TF400813: The user is not authorized to access this resource." }""", 401);
        }

        if (request.QueryValue("api-version") is { } version && version != "6.0")
        {
            return MockHttpResponse.Json($$"""{ "message": "The requested REST API version of {{version}} is out of range for this server. The latest REST API version this server supports is 6.0." }""", 400);
        }

        var repo = "/tfs/DefaultCollection/Web Apps/_apis/git/repositories/portal";
        switch (request.Method, request.Path)
        {
            case ("GET", "/tfs/_apis/connectionData"):
                var account = request.Header("Authorization") is null ? @"CONTOSO\ada" : "ada@contoso.com";
                return MockHttpResponse.Json($$"""
                    { "authenticatedUser": { "id": {{S(MyId)}}, "descriptor": "System.Security.Principal.WindowsIdentity;S-1-5-21-1", "providerDisplayName": "Ada Tester", "isActive": true,
                        "properties": { "Account": { "$type": "System.String", "$value": {{S(account)}} } } },
                      "authorizedUser": { "id": {{S(MyId)}} }, "deploymentType": "onPremises" }
                    """);
            case ("GET", "/tfs/_apis/projectCollections"):
                return MockHttpResponse.Json("""{ "value": [ { "name": "DefaultCollection" } ] }""");
            case ("GET", "/tfs/DefaultCollection/_apis/projects"):
                return MockHttpResponse.Json("""{ "value": [ { "name": "Web Apps" } ] }""");
            case ("POST", "/tfs/DefaultCollection/Web Apps/_apis/git/repositories"):
                _created.Add("repository " + JsonElement.Parse(request.Body).GetProperty("name").GetString());
                return MockHttpResponse.Json($$"""{ "name": "cli", "remoteUrl": {{S(Server + "/DefaultCollection/Web%20Apps/_git/cli")}}, "webUrl": {{S(Server + "/DefaultCollection/Web%20Apps/_git/cli")}} }""", 201);
            case ("GET", var p) when p == repo:
                return MockHttpResponse.Json("""{ "defaultBranch": "refs/heads/main", "project": { "visibility": "private" } }""");
            case ("GET", var p) when p == repo + "/pullrequests":
                return MockHttpResponse.Json($$"""
                    { "value": [
                      { "pullRequestId": 7, "title": "Login page", "status": "active", "createdBy": { "id": {{S(MyId)}}, "displayName": "Ada Tester", "uniqueName": "CONTOSO\\ada" },
                        "sourceRefName": "refs/heads/feature/login", "targetRefName": "refs/heads/main", "reviewers": [] },
                      { "pullRequestId": 8, "title": "Dark mode", "status": "active", "createdBy": { "id": "44", "displayName": "Bo", "uniqueName": "CONTOSO\\bo" },
                        "sourceRefName": "refs/heads/dark", "targetRefName": "refs/heads/main", "reviewers": [ { "id": {{S(MyId)}}, "displayName": "Ada Tester" } ] }
                    ] }
                    """);
            case ("POST", var p) when p == repo + "/pullrequests":
                var body = JsonElement.Parse(request.Body);
                _created.Add($"pull request {body.GetProperty("sourceRefName").GetString()} -> {body.GetProperty("targetRefName").GetString()}");
                return MockHttpResponse.Json($$"""{ "pullRequestId": 9, "title": {{S(body.GetProperty("title").GetString())}}, "status": "active", "createdBy": { "displayName": "Ada Tester" }, "sourceRefName": {{S(body.GetProperty("sourceRefName").GetString())}}, "targetRefName": {{S(body.GetProperty("targetRefName").GetString())}} }""", 201);
        }

        return MockHttpResponse.NotFound();
    }

    [Fact]
    public void Remotes_resolve_under_the_servers_path_in_every_form()
    {
        using var server = new AzureDevOpsProvider(new HostingTestContext(("azureDevOps.serverUrl", "https://tfs.contoso.com:8443/tfs")));
        Assert.Equal("DefaultCollection/Apps/site", server.ResolveRepository("https://tfs.contoso.com:8443/tfs/DefaultCollection/Apps/_git/site")?.Id);
        Assert.Equal("https://tfs.contoso.com:8443/tfs/DefaultCollection/Apps/_git/site", server.ResolveRepository("https://ada@tfs.contoso.com:8443/tfs/DefaultCollection/Apps/_git/site")?.WebUrl);

        // A repository named after its project, and SSH (the server's host on the SSH port).
        Assert.Equal("DefaultCollection/site/site", server.ResolveRepository("https://tfs.contoso.com:8443/tfs/DefaultCollection/_git/site")?.Id);
        Assert.Equal("DefaultCollection/Apps/site", server.ResolveRepository("ssh://tfs.contoso.com:22/tfs/DefaultCollection/Apps/_git/site")?.Id);
        Assert.Null(server.ResolveRepository("https://tfs.contoso.com:8443/other/DefaultCollection/Apps/_git/site"));
        Assert.Null(server.ResolveRepository("https://tfs.contoso.com/tfs/DefaultCollection/Apps/_git/site")); // another port: another server

        using var root = new AzureDevOpsProvider(new HostingTestContext(("azureDevOps.serverUrl", "https://ado.contoso.com")));
        Assert.Equal("Main/Apps/site", root.ResolveRepository("https://ado.contoso.com/Main/Apps/_git/site")?.Id);
    }

    [Fact]
    public async Task A_personal_access_token_signs_in_from_the_connection_data_and_older_api_versions_are_used()
    {
        var account = await _provider.SignInWithTokenAsync(Pat, Ct);
        Assert.Equal(("ada@contoso.com", "Ada Tester"), (account.Name, account.DisplayName));
        Assert.Equal(new Uri(_server.BaseUrl).Authority, account.Host);
        Assert.DoesNotContain(_server.Requests, r => r.Path.Contains("profile", StringComparison.Ordinal));

        // 7.1 is out of range for this server: it says 6.0, which every later call asks for.
        Assert.Equal("main", (await _provider.GetRepositoryInfoAsync(Repository, Ct)).DefaultBranch);
        Assert.Equal(["7.1", "6.0"], _server.Requests.Where(r => r.Path.EndsWith("/portal", StringComparison.Ordinal)).Select(r => r.QueryValue("api-version")));
        Assert.Equal(["7"], (await _provider.GetPullRequestsAsync(Repository, PullRequestFilter.Mine, Ct)).Select(p => p.Number));
        Assert.Equal(["8"], (await _provider.GetPullRequestsAsync(Repository, PullRequestFilter.ReviewRequested, Ct)).Select(p => p.Number));
        Assert.Equal("6.0", _server.Requests.Last().QueryValue("api-version"));
        Assert.StartsWith("Basic ", _server.Requests.Last().Header("Authorization"), StringComparison.Ordinal);

        var created = await _provider.CreatePullRequestAsync(Repository, new NewPullRequest("Sign up", "feature/signup", "main"), Ct);
        Assert.Equal(("9", $"{Server}/DefaultCollection/Web%20Apps/_git/portal/pullrequest/9"), (created.Number, created.WebUrl));
        Assert.Contains("pull request refs/heads/feature/signup -> refs/heads/main", _created);

        await Assert.ThrowsAsync<HostingAuthenticationException>(() => _provider.SignInWithTokenAsync("wrong", Ct));
    }

    [Fact]
    public async Task The_operating_systems_account_signs_in_with_no_token_sent()
    {
        var account = await _provider.SignInIntegratedAsync(Ct);
        Assert.Equal(@"CONTOSO\ada", account.Name);
        Assert.Contains("\"scheme\":\"negotiate\"", _context.StoredSecrets["session"], StringComparison.Ordinal);

        Assert.Equal(2, (await _provider.GetPullRequestsAsync(Repository, PullRequestFilter.All, Ct)).Count);
        Assert.All(_server.Requests, r => Assert.Null(r.Header("Authorization")));

        // A server that refuses it says what to do instead, and nothing is stored.
        await _provider.SignOutAsync(Ct);
        _takesWindowsAccount = false;
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.SignInIntegratedAsync(Ct));
        Assert.Contains("personal access token", refused.Message, StringComparison.Ordinal);
        Assert.False(_context.StoredSecrets.ContainsKey("session"));
    }

    [Fact]
    public async Task Azure_DevOps_Services_doesnt_take_the_operating_systems_account()
    {
        using var cloud = new AzureDevOpsProvider(new HostingTestContext());
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => cloud.SignInIntegratedAsync(Ct));
        Assert.Contains("azureDevOps.serverUrl", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publishing_lists_the_servers_project_collections()
    {
        await _provider.SignInWithTokenAsync(Pat, Ct);
        Assert.Equal(["DefaultCollection/Web Apps"], (await _provider.GetOwnersAsync(Ct)).Select(o => o.Id));
        var published = await _provider.PublishAsync(new NewRepository("DefaultCollection/Web Apps", "cli"), Ct);
        Assert.Equal($"{Server}/DefaultCollection/Web%20Apps/_git/cli", published.CloneUrl);
        Assert.Equal(["repository cli"], _created);
    }

    [Fact]
    public async Task The_systems_account_is_only_sent_to_the_signed_in_server()
    {
        using var http = new HostingHttp("Codalus/test");
        await Assert.ThrowsAsync<HostingAuthenticationException>(() => http.SendAsync(HttpMethod.Get, _server.BaseUrl + "/tfs/_apis/connectionData", null, Ct, integratedHost: "tfs.contoso.com"));
        Assert.Empty(_server.Requests);
    }

    [Theory]
    [InlineData("The requested REST API version of 7.1 is out of range for this server. The latest REST API version this server supports is 5.0.", "5.0")]
    [InlineData("GET /x: 400 TF400898: An internal error occurred.", null)]
    public void The_supported_version_comes_from_the_servers_message(string message, string? version) =>
        Assert.Equal(version, AzureDevOpsProvider.SupportedApiVersion(message));
}
