using System.Text;
using Codalus.Projects.Protocol;
using Codalus.Rpc;
using Codalus.Tests;
using Codalus.Workbench.Packages;
using Codalus.Workbench.Projects;

namespace Codalus.Projects.Tests;

/// <summary>
/// Signing in to a package source from the IDE (deferred from M9a): a feed that wants basic authentication, and a fake
/// NuGet credential-provider plugin that gives its credentials out only after an interactive, device-code sign-in.
/// </summary>
public sealed class PackageSignInTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(120);
    private static readonly string s_authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ada:secret"));
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-signin-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _environment = [];
    // NuGet keeps a service index in its machine-wide HTTP cache for 30 minutes, keyed by the source's URL alone, and
    // reads it from there without a request. A loopback port comes back for later mock feeds, so on a plain
    // 127.0.0.1:<port>/v3/index.json this feed would be answered by an earlier feed's index (for example MockNuGetServer's,
    // which has no search NuGet uses), without its 401. A path of its own keeps the URL, and the cache entry, this feed's.
    private readonly string _index = Guid.NewGuid().ToString("N") + "/v3/index.json";
    private MockHttpServer _feed = null!;
    private SolutionService _service = null!;

    private static string Plugin => Path.Combine(BuildHostIntegrationTests.RepoRoot, "tests", "Codalus.NuGetCredentialProvider.Fake", "bin", BuildHostIntegrationTests.Configuration, Codalus.Tests.RepoBuild.TargetFramework, "codalus-fake-credential-provider.dll");

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(s_timeout).Token).Token;

    public ValueTask InitializeAsync()
    {
        Assert.True(File.Exists(Plugin), $"The fake credential provider isn't built: {Plugin}");
        _feed = new MockHttpServer(request =>
        {
            if (request.Header("Authorization") != s_authorization)
            {
                return new MockHttpResponse(401, [], "text/plain") { Headers = new Dictionary<string, string> { ["WWW-Authenticate"] = "Basic realm=\"private\"" } };
            }

            var root = _feed.BaseUrl.TrimEnd('/');
            return request.Path.TrimStart('/') switch
            {
                var path when path == _index => MockHttpResponse.Json($$"""{ "version": "3.0.0", "resources": [ { "@id": "{{root}}/search", "@type": "SearchQueryService/3.0.0-beta" }, { "@id": "{{root}}/search", "@type": "SearchQueryService/3.5.0" } ] }"""),
                "search" => MockHttpResponse.Json("""{ "totalHits": 1, "data": [ { "id": "Contoso.Private", "version": "1.2.0", "description": "Behind a sign-in.", "versions": [ { "version": "1.2.0" } ] } ] }"""),
                _ => MockHttpResponse.NotFound(),
            };
        });

        var folder = Path.Combine(_root, "Private");
        Directory.CreateDirectory(Path.Combine(folder, "App"));
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(_root, "Directory.Packages.props"), "<Project />");
        FixtureSdk.Pin(_root);
        File.WriteAllText(Path.Combine(folder, "App", "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(folder, "Private.slnx"), "<Solution><Project Path=\"App/App.csproj\" /></Solution>");
        File.WriteAllText(Path.Combine(folder, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="private" value="{_feed.BaseUrl.TrimEnd('/')}/{_index}" allowInsecureConnections="true" />
              </packageSources>
            </configuration>
            """);

        // The build host inherits these: NuGet finds the fake plugin, which keeps its sign-in in a file.
        Set("NUGET_NETCORE_PLUGIN_PATHS", Plugin);
        Set("FAKE_NUGET_PROVIDER_STATE", Path.Combine(_root, "signed-in"));
        Set("FAKE_NUGET_PROVIDER_CREDENTIALS", "ada:secret");
        _service = new SolutionService(() => BuildHostIntegrationTests.HostAssemblyPath, new SupervisionPolicy { InitialBackoff = TimeSpan.FromMilliseconds(50) });
        _service.Log += (_, line) => Console.Error.WriteLine("[host] " + line);
        return ValueTask.CompletedTask;
    }

    private void Set(string name, string value)
    {
        _environment.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    public async ValueTask DisposeAsync()
    {
        await _service.DisposeAsync();
        await _feed.DisposeAsync();
        foreach (var (name, value) in _environment)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task A_source_that_needs_a_sign_in_answers_after_one()
    {
        await _service.OpenAsync(Path.Combine(_root, "Private", "Private.slnx"), trusted: true, restore: false, Timeout());
        var packages = new PackageService(_service);

        // Before: the source refuses, and says why.
        var before = await packages.SearchAsync("Contoso", prerelease: false, 0, 10, "private", Timeout());
        Assert.Empty(before.Packages);
        var error = Assert.Single(before.Errors);
        Assert.True(PackageService.NeedsSignIn(error.Message), error.Message);

        // Signing in shows the provider's device code, then the source answers.
        var messages = new List<string>();
        packages.SignInMessage += (_, m) =>
        {
            lock (messages)
            {
                messages.Add($"{m.Source}: {m.Message}");
            }
        };
        var result = await packages.SignInAsync("private", Timeout());
        Assert.True(result.SignedIn, result.Message);
        Assert.Equal("Signed in to private.", result.Message);
        // The message is a notification sent before the answer, but handled on its own: it may land just after it.
        bool Shown()
        {
            lock (messages)
            {
                return messages.Any(m => m.StartsWith("private: ", StringComparison.Ordinal) && m.Contains("FAKE-1234", StringComparison.Ordinal) && m.Contains("https://login.example.invalid/device", StringComparison.Ordinal));
            }
        }

        for (var waited = 0; waited < 100 && !Shown(); waited++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.True(Shown(), "The device code didn't reach the shell: " + string.Join(" | ", messages));

        var after = await packages.SearchAsync("Contoso", prerelease: false, 0, 10, "private", Timeout());
        Assert.Empty(after.Errors);
        Assert.Equal("Contoso.Private", Assert.Single(after.Packages).Id);
    }
}
