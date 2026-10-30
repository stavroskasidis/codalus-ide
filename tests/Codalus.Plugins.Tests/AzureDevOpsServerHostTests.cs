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
/// An Azure DevOps Server found from a repository's remote (ADR 0087): the shell's <see cref="HostingService"/> suggests
/// the server, the setting is written, and the Azure DevOps plugin, in a real plugin host, signs in there as the
/// operating system's user. A mock server stands in for it; a request with no Authorization header stands for Negotiate.
/// </summary>
public sealed class AzureDevOpsServerHostTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(90);
    private readonly string _userData = Path.Combine(Path.GetTempPath(), "codalus-ados-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryCredentialStore _store = new();
    private MockHttpServer _server = null!;
    private WorkbenchServices _workbench = null!;
    private PluginManager _manager = null!;
    private HostingService _hosting = null!;

    private static string PluginDir => Path.Combine(PluginHostIntegrationTests.RepoRoot, "src", "Plugins", "Codalus.Plugins.AzureDevOps", "bin", PluginHostIntegrationTests.Configuration, RepoBuild.TargetFramework);

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(s_timeout).Token).Token;

    private IReadOnlyList<GitRemote> Remotes => [new GitRemote("origin", _server.BaseUrl + "/tfs/DefaultCollection/Apps/_git/site", null)];

    public ValueTask InitializeAsync()
    {
        _server = new MockHttpServer(request => request.Header("Authorization") is null && request.Path == "/tfs/_apis/connectionData"
            ? MockHttpResponse.Json("""{ "authenticatedUser": { "id": "33", "providerDisplayName": "Ada Tester", "properties": { "Account": { "$value": "CONTOSO\\ada" } } } }""")
            : MockHttpResponse.Json("""{ "message": "TF400813: not authorized." }""", 401));
        var paths = AppPaths.Resolve(PlatformInfo.Current, _ => null, _userData);
        paths.EnsureCreated();
        _workbench = new WorkbenchServices(paths, PlatformInfo.Current, watchFiles: false);
        _manager = new PluginManager(_workbench, _ => ProcessLaunchInfo.FromPath(PluginHostIntegrationTests.HostAssembly), "test",
            new SupervisionPolicy { InitialBackoff = TimeSpan.FromMilliseconds(50), MaxRestarts = 2 })
        {
            Secrets = new SecretStorage(_store),
        };
        _hosting = new HostingService(_manager, Path.Combine(paths.State, "hosting-accounts.json"))
        {
            ReadSetting = key => _workbench.Settings.GetString(key, string.Empty),
        };
        _manager.AddPlugins(PluginDiscovery.Scan([(PluginDir, true)], PlatformInfo.Current));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
        _workbench.Dispose();
        await _server.DisposeAsync();
        try
        {
            Directory.Delete(_userData, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task The_server_comes_from_the_remote_and_the_systems_account_signs_in_there()
    {
        var provider = Assert.Single(_hosting.Providers);
        Assert.Equal(("azure-devops", true, false), (provider.Id, provider.IntegratedSignIn, provider.IsSelfHosted));
        Assert.Null(_hosting.SuggestFor(Remotes));
        var suggestion = _hosting.SuggestServerFor(Remotes);
        Assert.Equal(("azure-devops", _server.BaseUrl + "/tfs"), (suggestion?.Provider.Id, suggestion?.ServerUrl));

        // The shell writes the server, then signs in: the plugin host hears of the setting before the request.
        await _workbench.Settings.SetAsync(provider.ServerSetting!, suggestion!.ServerUrl, SettingScope.User, Timeout());
        Assert.True(Assert.Single(_hosting.Providers).IsSelfHosted);
        Assert.Null(_hosting.SuggestServerFor(Remotes));
        var account = await _hosting.SignInAsync("azure-devops", null, Timeout(), integrated: true);
        Assert.Equal((@"CONTOSO\ada", new Uri(_server.BaseUrl).Authority), (account.Name, account.Host));

        var match = await _hosting.MatchAsync(Remotes, Timeout());
        Assert.Equal("DefaultCollection/Apps/site", match?.Repository.Id);
    }

    [Theory]
    [InlineData("https://tfs.contoso.com/tfs/DefaultCollection/Apps/_git/site", "https://tfs.contoso.com/tfs")]
    [InlineData("http://tfs.contoso.com:8080/tfs/DefaultCollection/Apps/_git/site", "http://tfs.contoso.com:8080/tfs")]
    [InlineData("https://tfs.contoso.com/tfs/DefaultCollection/_git/site", "https://tfs.contoso.com/tfs")]
    [InlineData("https://ado.contoso.com/Main/Apps/_git/site", "https://ado.contoso.com")]
    [InlineData("https://ado.contoso.com/Main/_git/site", "https://ado.contoso.com")]
    [InlineData("https://ado.contoso.com/devops/tfs/Main/Apps/_git/site", "https://ado.contoso.com/devops/tfs")]
    [InlineData("ssh://tfs.contoso.com:22/tfs/DefaultCollection/Apps/_git/site", "https://tfs.contoso.com/tfs")]
    [InlineData("https://github.com/contoso/site.git", null)]
    [InlineData("https://tfs.contoso.com/tfs/DefaultCollection/Apps/_git/site/extra", null)]
    [InlineData("/home/ada/remote.git", null)]
    public void An_azure_devops_servers_remote_says_where_the_server_is(string remote, string? server) =>
        Assert.Equal(server, HostingService.AzureDevOpsServerOf(remote));

    [Theory]
    [InlineData("github", "https://github.com", "https://github.com/contoso/site.git", "https://github.com/settings/tokens/new?scopes=repo,read:org,workflow&description=Codalus%20IDE")]
    [InlineData("gitlab", "https://gitlab.contoso.com/", null, "https://gitlab.contoso.com/-/user_settings/personal_access_tokens?name=Codalus%20IDE&scopes=api")]
    [InlineData("azure-devops", "https://dev.azure.com", "https://contoso@dev.azure.com/contoso/Web%20Apps/_git/portal", "https://dev.azure.com/contoso/_usersSettings/tokens")]
    [InlineData("azure-devops", "https://dev.azure.com", "git@ssh.dev.azure.com:v3/contoso/Web%20Apps/portal", "https://dev.azure.com/contoso/_usersSettings/tokens")]
    [InlineData("azure-devops", "https://dev.azure.com", "https://contoso.visualstudio.com/DefaultCollection/Tools/_git/cli", "https://contoso.visualstudio.com/_usersSettings/tokens")]
    [InlineData("azure-devops", "https://tfs.contoso.com/tfs", "https://tfs.contoso.com/tfs/DefaultCollection/Apps/_git/site", "https://tfs.contoso.com/tfs/DefaultCollection/_usersSettings/tokens")]
    [InlineData("azure-devops", "https://dev.azure.com", null, null)]
    [InlineData("other", "https://example.com", null, null)]
    public void The_token_page_is_the_hosts_own(string provider, string server, string? remote, string? page) =>
        Assert.Equal(page, HostingService.TokenPageUrl(provider, server, remote));
}
