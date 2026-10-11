using DaedalusStudio.Plugins.GitHub;
using DaedalusStudio.Plugins.Hosting;
using DaedalusStudio.Plugins.Proposed;
using DaedalusStudio.Tests;

namespace DaedalusStudio.Plugins.Tests;

/// <summary>The GitHub provider (ADR 0031) against <see cref="MockGitHub"/>: nothing reaches GitHub.</summary>
public sealed class GitHubProviderTests : IAsyncLifetime
{
    private readonly MockGitHub _github = new("contoso", "app");
    private HostingTestContext _context = null!;
    private GitHubProvider _provider = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HostingRepository Repository => _provider.ResolveRepository(_github.RemoteUrl)! with { Remote = "origin" };

    public ValueTask InitializeAsync()
    {
        _context = new HostingTestContext(("github.serverUrl", _github.ServerUrl), ("github.oauthClientId", MockGitHub.ClientId));
        _provider = new GitHubProvider(_context);
        _github.PullRequests.Add(new MockPullRequest(1, "Add the greeter", "ada", "feature/greeter", "main") { Body = "Adds **Greeter**.", Reviewers = ["bo"] });
        _github.PullRequests.Add(new MockPullRequest(2, "Fix the build", "bo", "fix-build", "main") { Reviewers = ["ada"], Draft = true });
        _github.PullRequests.Add(new MockPullRequest(3, "From a fork", "cy", "patch-1", "main") { HeadRepository = "cy/app" });
        _github.Threads[1] = [new MockReviewThread("src/Greeter.cs", 3, [("bo", "Rename this?"), ("ada", "Done.")]), new MockReviewThread("README.md", 1, [("bo", "Typo")]) { Resolved = true }];
        _github.Issues.Add(new MockIssue(12, "Greeter is rude", "ada"));
        _github.Issues.Add(new MockIssue(120, "Docs for the greeter", null));
        _github.Issues.Add(new MockIssue(7, "Crash on start", "bo"));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _provider.Dispose();
        await _github.DisposeAsync();
    }

    private Task<HostingAccount> SignInAsync() => _provider.SignInWithTokenAsync(MockGitHub.Token, Ct);

    [Fact]
    public void Remotes_of_the_server_resolve_to_owner_and_name()
    {
        var host = new Uri(_github.ServerUrl).Authority;
        Assert.Equal("contoso/app", _provider.ResolveRepository(_github.RemoteUrl)?.Id);
        Assert.Equal("contoso/app", _provider.ResolveRepository($"{_github.ServerUrl}/contoso/app")?.Id);
        Assert.Equal($"{_github.ServerUrl}/contoso/app", _provider.ResolveRepository(_github.RemoteUrl)?.WebUrl);
        Assert.Null(_provider.ResolveRepository("https://gitlab.com/contoso/app.git"));
        Assert.Null(_provider.ResolveRepository($"{_github.ServerUrl}/contoso/app/extra"));

        var dotCom = new GitHubProvider(new HostingTestContext());
        Assert.Equal("contoso/app", dotCom.ResolveRepository("https://github.com/contoso/app.git")?.Id);
        Assert.Equal("contoso/app", dotCom.ResolveRepository("git@github.com:contoso/app.git")?.Id);
        Assert.Equal("contoso/app", dotCom.ResolveRepository("ssh://git@ssh.github.com:443/contoso/app.git")?.Id);
        Assert.Equal("github.com", dotCom.ResolveRepository("https://user@github.com/contoso/app")?.Host);
        Assert.Null(dotCom.ResolveRepository($"http://{host}/contoso/app.git"));
        dotCom.Dispose();
    }

    [Fact]
    public async Task The_device_flow_shows_the_code_polls_and_stores_the_session()
    {
        _github.PendingPolls = 2;
        Assert.Null(await _provider.GetAccountAsync(Ct));
        var account = await _provider.SignInAsync(_context, Ct);
        Assert.Equal("ada", account.Name);
        Assert.Equal("Ada Tester", account.DisplayName);
        Assert.Equal(new Uri(_github.ServerUrl).Authority, account.Host);
        var code = Assert.Single(_context.Codes);
        Assert.Equal(MockGitHub.UserCode, code.UserCode);
        Assert.Equal(_github.ServerUrl + "/login/device", code.VerificationUri);
        Assert.Equal(3, _github.Requests.Count(r => r.Path == "/login/oauth/access_token"));
        Assert.Contains(MockGitHub.Token, _context.StoredSecrets["session"], StringComparison.Ordinal);

        // A new provider (the next start) reads the session from the secrets.
        using var again = new GitHubProvider(_context);
        Assert.Equal("ada", (await again.GetAccountAsync(Ct))?.Name);

        await again.SignOutAsync(Ct);
        Assert.Empty(_context.StoredSecrets);
        Assert.Null(await again.GetAccountAsync(Ct));
    }

    [Fact]
    public async Task Sign_in_fails_clearly_without_a_client_id_or_with_a_bad_token()
    {
        _context.Set("github.oauthClientId", string.Empty);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.SignInAsync(_context, Ct));
        Assert.Contains("personal access token", error.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<HostingAuthenticationException>(() => _provider.SignInWithTokenAsync("not-a-token", Ct));
        Assert.Empty(_context.StoredSecrets);
        await Assert.ThrowsAsync<HostingAuthenticationException>(() => _provider.GetPullRequestsAsync(Repository, PullRequestFilter.All, Ct));
    }

    [Fact]
    public async Task Pull_requests_are_listed_filtered_and_detailed_with_review_threads()
    {
        await SignInAsync();
        var all = await _provider.GetPullRequestsAsync(Repository, PullRequestFilter.All, Ct);
        Assert.Equal(["1", "2", "3"], all.Select(p => p.Number));
        Assert.Equal("#1", all[0].Reference);
        Assert.Equal("feature/greeter", all[0].SourceBranch);
        Assert.True(all[1].IsDraft);
        Assert.Null(all[0].SourceRepository);
        Assert.Equal("cy/app", all[2].SourceRepository);
        Assert.Equal(["1"], (await _provider.GetPullRequestsAsync(Repository, PullRequestFilter.Mine, Ct)).Select(p => p.Number));
        Assert.Equal(["2"], (await _provider.GetPullRequestsAsync(Repository, PullRequestFilter.ReviewRequested, Ct)).Select(p => p.Number));

        var details = await _provider.GetPullRequestAsync(Repository, "1", Ct);
        Assert.Equal("Adds **Greeter**.", details.Description);
        Assert.Equal(2, details.Threads.Count);
        var thread = details.Threads[0];
        Assert.Equal(("src/Greeter.cs", 3, false), (thread.Path, thread.Line, thread.IsResolved));
        Assert.Equal(["bo", "ada"], thread.Comments.Select(c => c.Author));
        Assert.True(details.Threads[1].IsResolved);
        Assert.Equal("Looks good overall.", Assert.Single(details.Comments).Body);

        Assert.Equal(new PullRequestCheckout("origin", "feature/greeter", "feature/greeter", true), await _provider.GetCheckoutAsync(Repository, all[0], Ct));
        Assert.Equal(new PullRequestCheckout("origin", "refs/pull/3/head", "pr/3", false), await _provider.GetCheckoutAsync(Repository, all[2], Ct));
        Assert.Equal("main", (await _provider.GetRepositoryInfoAsync(Repository, Ct)).DefaultBranch);
    }

    [Fact]
    public async Task A_pull_request_is_created()
    {
        await SignInAsync();
        var created = await _provider.CreatePullRequestAsync(Repository, new NewPullRequest("Greet in French", "feature/french", "main") { Description = "Bonjour", IsDraft = true }, Ct);
        Assert.Equal("4", created.Number);
        Assert.True(created.IsDraft);
        Assert.Contains("pull request #4 feature/french -> main", _github.Created);
    }

    [Fact]
    public async Task Checks_and_statuses_sum_up_to_the_branch_status()
    {
        await SignInAsync();
        Assert.Equal("none", (await _provider.GetBranchStatusAsync(Repository, "main", null, Ct)).State);

        _github.CheckRuns.Add(new MockCheckRun("build", "completed", "success"));
        _github.CheckRuns.Add(new MockCheckRun("tests", "in_progress", null));
        var pending = await _provider.GetBranchStatusAsync(Repository, "main", new string('b', 40), Ct);
        Assert.Equal("pending", pending.State);
        Assert.Equal(["success", "pending"], pending.Checks.Select(c => c.State));

        _github.CheckRuns[1] = new MockCheckRun("tests", "completed", "failure");
        Assert.Equal("failure", (await _provider.GetBranchStatusAsync(Repository, "main", null, Ct)).State);
        _github.CheckRuns[1] = new MockCheckRun("tests", "completed", "skipped");
        Assert.Equal("success", (await _provider.GetBranchStatusAsync(Repository, "main", null, Ct)).State);
    }

    [Fact]
    public async Task Issues_complete_by_number_and_words_and_list_the_users_own()
    {
        await SignInAsync();
        var assigned = await _provider.GetWorkItemsAsync(Repository, new WorkItemQuery { AssignedToMe = true }, Ct);
        Assert.Equal(["12"], assigned.Select(i => i.Number));
        Assert.Equal("#12", assigned[0].Reference);

        var byNumber = await _provider.GetWorkItemsAsync(Repository, new WorkItemQuery { Text = "#12" }, Ct);
        Assert.Equal(["12", "120"], byNumber.Select(i => i.Number));
        var byWords = await _provider.GetWorkItemsAsync(Repository, new WorkItemQuery { Text = "greeter" }, Ct);
        Assert.Equal(["12", "120"], byWords.Select(i => i.Number).Order());
        Assert.Empty(await _provider.GetWorkItemsAsync(Repository, new WorkItemQuery { Text = "999" }, Ct));
    }

    [Fact]
    public async Task Publishing_lists_owners_and_creates_the_repository()
    {
        await SignInAsync();
        var owners = await _provider.GetOwnersAsync(Ct);
        Assert.Equal([("ada", "user"), ("daedalus-org", "organization")], owners.Select(o => (o.Name, o.Kind)));
        var mine = await _provider.PublishAsync(new NewRepository("ada", "new-app") { Description = "Mine" }, Ct);
        Assert.Equal($"{_github.ServerUrl}/ada/new-app.git", mine.CloneUrl);
        var theirs = await _provider.PublishAsync(new NewRepository("daedalus-org", "shared-app") { IsPrivate = false }, Ct);
        Assert.Equal($"{_github.ServerUrl}/daedalus-org/shared-app", theirs.WebUrl);
        Assert.Equal(["repository ada/new-app", "repository daedalus-org/shared-app"], _github.Created);
    }

    [Fact]
    public void Remote_urls_split_into_host_and_path()
    {
        Assert.Equal(new RemoteUrl("github.com", "a/b", false), RemoteUrl.Parse("https://github.com/a/b.git"));
        Assert.Equal(new RemoteUrl("github.com", "a/b", true), RemoteUrl.Parse("git@github.com:a/b.git"));
        Assert.Equal(new RemoteUrl("example.com:8443", "group/sub/proj", false), RemoteUrl.Parse("https://user@example.com:8443/group/sub/proj"));
        Assert.Equal(new RemoteUrl("ssh.dev.azure.com", "v3/org/My Project/repo", true), RemoteUrl.Parse("git@ssh.dev.azure.com:v3/org/My%20Project/repo".Replace("%20", " ", StringComparison.Ordinal)));
        Assert.Equal("dev.azure.com", RemoteUrl.Parse("https://org@dev.azure.com/org/My%20Project/_git/repo")?.Host);
        Assert.Equal("org/My Project/_git/repo", RemoteUrl.Parse("https://org@dev.azure.com/org/My%20Project/_git/repo")?.Path);
        Assert.Null(RemoteUrl.Parse(@"C:\repos\local"));
        Assert.Null(RemoteUrl.Parse("/srv/git/repo.git"));
    }
}
