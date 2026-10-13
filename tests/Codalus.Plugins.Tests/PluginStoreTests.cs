using Codalus.Platform;
using Codalus.Tests;
using Codalus.Workbench;
using Codalus.Workbench.Plugins;

namespace Codalus.Plugins.Tests;

/// <summary>The plugin gallery, installer and store (M22c, ADR 0060) against a mock NuGet feed.</summary>
public sealed class PluginStoreTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("codalus-store-").FullName;
    private readonly MockNuGetFeed _feed = new();
    private readonly HttpClient _http = new();
    private WorkbenchServices _workbench = null!;
    private PluginManager _manager = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string PluginsDir => Path.Combine(_root, "plugins");

    public ValueTask InitializeAsync()
    {
        var paths = AppPaths.Resolve(PlatformInfo.Current, _ => null, Path.Combine(_root, "user-data"));
        paths.EnsureCreated();
        _workbench = new WorkbenchServices(paths, PlatformInfo.Current, watchFiles: false);
        _manager = new PluginManager(_workbench, _ => null, "test");
        return ValueTask.CompletedTask;
    }

    private PluginStore Store(string policy = "none", Func<string, CancellationToken, Task<PackageSignature>>? verify = null) =>
        new(_manager, new PluginGallery(_http), new PluginInstaller(PluginsDir, PlatformInfo.Current) { Verify = verify ?? PluginInstaller.VerifyWithSdkAsync }, PluginsDir, PlatformInfo.Current)
        {
            Sources = () => [_feed.Source],
            SignaturePolicy = () => policy,
        };

    [Fact]
    public async Task Search_finds_plugins_only_and_their_versions()
    {
        _feed.AddPlugin("contoso.greeter", "1.0.0", description: "Greets.");
        _feed.AddPlugin("contoso.greeter", "1.1.0", description: "Greets better.");
        _feed.AddPlugin("contoso.library", "2.0.0", packageType: "Dependency");
        var store = Store();

        var found = await store.SearchAsync(string.Empty, 20, Ct);
        var greeter = Assert.Single(found);
        Assert.Equal("contoso.greeter", greeter.Id);
        Assert.Equal("1.1.0", greeter.Version);
        Assert.Equal("Greets better.", greeter.Description);
        Assert.Equal(["1.0.0", "1.1.0"], greeter.Versions);
        Assert.Equal("Contoso", greeter.Authors);
        Assert.Contains("packageType=CodalusPlugin", _feed.Server.Requests.Single(r => r.Path == "/search").Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Installing_adds_the_plugin_at_once_and_an_update_waits_for_the_next_start()
    {
        _feed.AddPlugin("contoso.greeter", "1.0.0");
        _feed.AddPlugin("contoso.greeter", "1.1.0");
        var store = Store();
        var greeter = Assert.Single(await store.SearchAsync("greeter", 20, Ct));

        var package = await store.DownloadAsync(greeter, "1.0.0", Ct);
        Assert.Equal(PluginState.Ready, package.Plugin.State);
        Assert.Empty(package.Permissions);
        Assert.True(store.Install(package)); // now
        var installed = Assert.Single(_manager.Plugins);
        Assert.Equal("1.0.0", installed.Manifest!.Version);
        Assert.Equal(_feed.Source, File.ReadAllText(Path.Combine(PluginsDir, "contoso.greeter", PluginStore.SourceFile)));

        // The newer version is found, downloaded and staged; the next start applies it.
        var update = Assert.Single(await store.CheckUpdatesAsync(Ct));
        Assert.Equal(("contoso.greeter", "1.0.0", "1.1.0"), (update.Id, update.Installed, update.Available));
        Assert.Single(await store.StageUpdatesAsync([update], Ct));
        Assert.True(store.Pending.ContainsKey("contoso.greeter"));
        Assert.Contains("\"1.0.0\"", File.ReadAllText(Path.Combine(PluginsDir, "contoso.greeter", "codalus-plugin.json")), StringComparison.Ordinal);

        var applied = new PluginInstaller(PluginsDir, PlatformInfo.Current).ApplyPending();
        Assert.Equal(["contoso.greeter"], applied);
        Assert.Contains("\"1.1.0\"", File.ReadAllText(Path.Combine(PluginsDir, "contoso.greeter", "codalus-plugin.json")), StringComparison.Ordinal);
        Assert.Empty(new PluginInstaller(PluginsDir, PlatformInfo.Current).Pending);
        Assert.False(Directory.Exists(Path.Combine(PluginsDir, ".staging")) && Directory.EnumerateDirectories(Path.Combine(PluginsDir, ".staging")).Any());
    }

    [Fact]
    public async Task Uninstalling_waits_for_the_next_start_and_can_be_cancelled()
    {
        _feed.AddPlugin("contoso.greeter", "1.0.0");
        var store = Store();
        store.Install(await store.DownloadAsync(Assert.Single(await store.SearchAsync("greeter", 20, Ct)), "1.0.0", Ct));

        store.Uninstall("contoso.greeter");
        Assert.Null(store.Pending["contoso.greeter"]);
        store.CancelPending("contoso.greeter");
        Assert.Empty(store.Pending);

        store.Uninstall("contoso.greeter");
        new PluginInstaller(PluginsDir, PlatformInfo.Current).ApplyPending();
        Assert.False(Directory.Exists(Path.Combine(PluginsDir, "contoso.greeter")));
    }

    [Fact]
    public async Task Permissions_are_read_and_unknown_ones_warned_about()
    {
        _feed.AddPlugin("contoso.fetcher", "1.0.0", manifestJson: """
            {
              "id": "contoso.fetcher", "version": "1.0.0", "displayName": "Fetcher", "publisher": "contoso",
              "engines": { "codalus": "^1.0.0" },
              "permissions": ["network", "processes", "telepathy"],
              "contributes": { "settings": { "title": "Fetcher", "properties": { "fetcher.on": { "type": "boolean", "default": true } } } }
            }
            """);
        var store = Store();
        var package = await store.DownloadAsync(Assert.Single(await store.SearchAsync("fetcher", 20, Ct)), "1.0.0", Ct);
        Assert.Equal(["network", "processes", "telepathy"], package.Permissions);
        Assert.Contains(package.Plugin.Warnings, w => w.Contains("telepathy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Packages_that_must_be_signed_and_arent_are_refused()
    {
        _feed.AddPlugin("contoso.greeter", "1.0.0");
        var store = Store("all", verify: (_, _) => Task.FromResult(new PackageSignature(false, null, "NU3004: The package is not signed.")));
        var greeter = Assert.Single(await store.SearchAsync("greeter", 20, Ct));
        var refused = await Assert.ThrowsAsync<InvalidDataException>(() => store.DownloadAsync(greeter, "1.0.0", Ct));
        Assert.Contains("NU3004", refused.Message, StringComparison.Ordinal);
        Assert.Empty(_manager.Plugins);

        var signed = Store("all", verify: (_, _) => Task.FromResult(new PackageSignature(true, "CN=Contoso", "Successfully verified package(s).")));
        var package = await signed.DownloadAsync(greeter, "1.0.0", Ct);
        Assert.Equal("CN=Contoso", package.Signature.Publisher);
        Assert.True(signed.RequiresSignature(_feed.Source));
        Assert.False(Store("nugetOrg").RequiresSignature(_feed.Source));
        Assert.True(Store("nugetOrg").RequiresSignature(PluginGallery.NuGetOrg));
    }

    [Fact]
    public async Task A_package_that_isnt_its_plugin_is_refused()
    {
        _feed.AddPlugin("contoso.greeter", "1.0.0", manifestJson: """
            { "id": "contoso.other", "version": "1.0.0", "publisher": "contoso", "engines": { "codalus": "^1.0.0" },
              "contributes": { "settings": { "title": "x", "properties": { "x.on": { "type": "boolean", "default": true } } } } }
            """);
        var store = Store();
        var refused = await Assert.ThrowsAsync<InvalidDataException>(async () => await store.DownloadAsync(Assert.Single(await store.SearchAsync("greeter", 20, Ct)), "1.0.0", Ct));
        Assert.Contains("contoso.other", refused.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(PluginsDir, "contoso.greeter")));
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
        _workbench.Dispose();
        _http.Dispose();
        await _feed.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
