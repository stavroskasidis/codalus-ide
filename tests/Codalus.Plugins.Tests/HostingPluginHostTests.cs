using Codalus.Platform;
using Codalus.Platform.Credentials;
using Codalus.Rpc;
using Codalus.Tests;
using Codalus.Workbench;
using Codalus.Workbench.Git;
using Codalus.Workbench.Hosting;
using Codalus.Workbench.Plugins;
using Codalus.Workbench.Secrets;
using Codalus.Workbench.Settings;

namespace Codalus.Plugins.Tests;

/// <summary>
/// The GitHub plugin in a real plugin host (ADR 0031), driven through the shell's <see cref="HostingService"/> as the
/// shell wires it, against <see cref="MockGitHub"/>, with secrets in an in-memory credential store.
/// </summary>
public sealed class HostingPluginHostTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(90);
    private readonly string _userData = Path.Combine(Path.GetTempPath(), "codalus-hosting-" + Guid.NewGuid().ToString("N"));
    private readonly MockGitHub _github = new("contoso", "app");
    private readonly InMemoryCredentialStore _store = new();
    private WorkbenchServices _workbench = null!;
    private PluginManager _manager = null!;
    private HostingService _hosting = null!;

    internal static string GitHubPluginDir => Path.Combine(PluginHostIntegrationTests.RepoRoot, "src", "Plugins", "Codalus.Plugins.GitHub", "bin", PluginHostIntegrationTests.Configuration, Codalus.Tests.RepoBuild.TargetFramework);

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(s_timeout).Token).Token;

    private IReadOnlyList<GitRemote> Remotes => [new GitRemote("upstream", "https://example.com/other.git", null), new GitRemote("origin", _github.RemoteUrl, null)];

    private PluginDescription Plugin => _manager.Plugins.Single(p => p.Id == "codalus.github");

    public async ValueTask InitializeAsync()
    {
        var paths = AppPaths.Resolve(PlatformInfo.Current, _ => null, _userData);
        paths.EnsureCreated();
        _workbench = new WorkbenchServices(paths, PlatformInfo.Current, watchFiles: false);
        _manager = new PluginManager(_workbench, _ => ProcessLaunchInfo.FromPath(PluginHostIntegrationTests.HostAssembly), "test",
            new SupervisionPolicy { InitialBackoff = TimeSpan.FromMilliseconds(50), MaxRestarts = 2 })
        {
            Secrets = new SecretStorage(_store),
        };
        _hosting = new HostingService(_manager, Path.Combine(paths.State, "hosting-accounts.json"));
        _manager.AddPlugins(PluginDiscovery.Scan([(GitHubPluginDir, true)], PlatformInfo.Current));
        await _workbench.Settings.SetAsync("github.serverUrl", _github.ServerUrl, SettingScope.User, TestContext.Current.CancellationToken);
        await _workbench.Settings.SetAsync("github.oauthClientId", MockGitHub.ClientId, SettingScope.User, TestContext.Current.CancellationToken);
        _github.PullRequests.Add(new MockPullRequest(1, "Add the greeter", "ada", "feature/greeter", "main"));
        _github.Threads[1] = [new MockReviewThread("src/Greeter.cs", 3, [("bo", "Rename this?")])];
        _github.CheckRuns.Add(new MockCheckRun("build", "completed", "success"));
        _github.Issues.Add(new MockIssue(12, "Greeter is rude", "ada"));
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
        _workbench.Dispose();
        await _github.DisposeAsync();
        try
        {
            Directory.Delete(_userData, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task The_plugin_stays_inactive_until_the_user_signs_in()
    {
        var provider = Assert.Single(_hosting.Providers);
        Assert.Equal(("github", "GitHub", false), (provider.Id, provider.Label, provider.IsRegistered));
        Assert.Contains("onHostingProvider:github", Plugin.ActivationEvents);
        Assert.Equal("github", _hosting.SuggestFor([new GitRemote("origin", "git@github.com:contoso/app.git", null)])?.Id);
        Assert.Null(_hosting.SuggestFor([new GitRemote("origin", "https://gitlab.com/contoso/app.git", null)]));

        Assert.Null(await _hosting.MatchAsync(Remotes, Timeout()));
        Assert.Equal(PluginState.Ready, Plugin.State);
        Assert.Empty(_github.Requests);
    }

    [Fact]
    public async Task Signing_in_with_a_device_code_then_working_with_pull_requests_through_the_host()
    {
        var codes = new List<string>();
        _hosting.DeviceCodeReceived += (_, code) =>
        {
            lock (codes)
            {
                codes.Add(code.UserCode);
            }
        };

        var account = await _hosting.SignInAsync("github", null, Timeout());
        Assert.Equal("ada", account.Name);
        Assert.Equal([MockGitHub.UserCode], codes);
        Assert.Equal(PluginState.Active, Plugin.State);
        Assert.True(_hosting.Providers.Single().IsRegistered);
        Assert.Equal(new Uri(_github.ServerUrl).Authority, _hosting.AccountFor("github")?.Host);
        Assert.Contains(MockGitHub.Token, await _store.GetAsync(SecretStorage.Service, "codalus.github/session", TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.DoesNotContain(MockGitHub.Token, await File.ReadAllTextAsync(Path.Combine(_userData, "state", "hosting-accounts.json"), TestContext.Current.CancellationToken), StringComparison.Ordinal);

        // A host of a signed-in account matches (the remote on example.com doesn't).
        var match = await _hosting.MatchAsync(Remotes, Timeout());
        Assert.NotNull(match);
        Assert.Equal(("contoso/app", "origin"), (match!.Repository.Id, match.Repository.Remote));

        var pulls = await _hosting.PullRequestsAsync(match, "all", Timeout());
        Assert.Equal("Add the greeter", Assert.Single(pulls).Title);
        var details = await _hosting.PullRequestAsync(match, "1", Timeout());
        Assert.Equal("Rename this?", Assert.Single(details.Threads).Comments.Single().Body);
        var checkout = await _hosting.CheckoutAsync(match, pulls[0], Timeout());
        Assert.Equal(("origin", "feature/greeter", true), (checkout.Remote, checkout.LocalBranch, checkout.Track));
        Assert.Equal("success", (await _hosting.BranchStatusAsync(match, "main", null, Timeout())).State);
        Assert.Equal(["#12"], (await _hosting.WorkItemsAsync(match, true, null, 20, Timeout())).Select(i => i.Reference));
        Assert.Equal("main", (await _hosting.RepositoryInfoAsync(match, Timeout())).DefaultBranch);
        var created = await _hosting.CreatePullRequestAsync(match, "Second", "More", "feature/two", "main", false, Timeout());
        Assert.Equal("2", created.Number);

        await _hosting.SignOutAsync("github", Timeout());
        Assert.Empty(_hosting.Accounts);
        Assert.Null(await _store.GetAsync(SecretStorage.Service, "codalus.github/session", TestContext.Current.CancellationToken));
        Assert.Null(await _hosting.MatchAsync(Remotes, Timeout()));
    }

    [Fact]
    public async Task A_revoked_session_asks_the_user_to_sign_in_again()
    {
        await _hosting.SignInAsync("github", MockGitHub.Token, Timeout());
        var match = await _hosting.MatchAsync(Remotes, Timeout());
        Assert.NotNull(match);

        // The token is revoked on the host: the next call fails as "sign in", and the account is forgotten. (A change in
        // the provider's settings makes it read the stored session again.)
        var session = (await _store.GetAsync(SecretStorage.Service, "codalus.github/session", TestContext.Current.CancellationToken))!;
        await _store.SetAsync(SecretStorage.Service, "codalus.github/session", session.Replace(MockGitHub.Token, "revoked", StringComparison.Ordinal), TestContext.Current.CancellationToken);
        await _workbench.Settings.SetAsync("github.oauthClientId", "reload", SettingScope.User, TestContext.Current.CancellationToken);

        var asked = new List<string>();
        _hosting.SignInRequired += (_, provider) => asked.Add(provider);
        await WaitForSettingsAsync();
        await Assert.ThrowsAsync<HostingSignInRequiredException>(() => _hosting.PullRequestsAsync(match!, "all", Timeout()));
        Assert.Equal(["github"], asked);
        Assert.Null(_hosting.AccountFor("github"));

        var error = await Assert.ThrowsAsync<HostingRequestException>(() => _hosting.PublishAsync("nope", "x", "y", null, true, Timeout()));
        Assert.Contains("nope", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Settings reach the plugin host as a notification: give it a moment.</summary>
    private static Task WaitForSettingsAsync() => Task.Delay(500, TestContext.Current.CancellationToken);
}
