using Codalus.Rpc;
using Codalus.Workbench.Packages;
using Codalus.Workbench.Projects;

namespace Codalus.Projects.Tests;

/// <summary>
/// NuGet's verified-owner badge (D39) through the real build host: <c>PackageSummary.Verified</c> and
/// <c>PackageDetails.Verified</c> come only from nuget.org, for results that can be nobody else's copy (design questions
/// 7, 9, 10 and 11). A mock feed stands in for nuget.org through <c>CODALUS_NUGET_ORG_INDEX</c>; another mock is a feed
/// that passes nuget.org's flag on (a mirror). Nothing reaches nuget.org.
/// </summary>
public sealed class PackageVerifiedOwnerTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(120);
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-verified-" + Guid.NewGuid().ToString("N"));
    private readonly MockVerifiedFeed _nugetOrg = new MockVerifiedFeed().Add("Contoso.Verified", "1.1.0", verified: true).Add("Contoso.Verified", "1.2.0", verified: true).Add("Contoso.Plain", "1.0.0", verified: false);
    private readonly MockVerifiedFeed _mirror = new MockVerifiedFeed().Add("Contoso.Verified", "1.2.0", verified: true).Add("Contoso.Verified", "1.2.1", verified: true).Add("Contoso.Plain", "1.0.0", verified: false);
    private SolutionService _service = null!;
    private PackageService _packages = null!;

    private string Fixture => Path.Combine(_root, "Packages");

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(s_timeout).Token).Token;

    public ValueTask InitializeAsync()
    {
        PackageIntegrationTests.Copy(Path.Combine(BuildHostIntegrationTests.RepoRoot, "samples", "Packages"), Fixture);
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        Codalus.Tests.FixtureSdk.Pin(_root);
        File.WriteAllText(Path.Combine(_root, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        _service = new SolutionService(() => BuildHostIntegrationTests.HostAssemblyPath, new SupervisionPolicy { InitialBackoff = TimeSpan.FromMilliseconds(50) }, async (folder, cancellationToken) =>
            new Dictionary<string, string>(await BuildHostRuntime.EnvironmentAsync(folder, cancellationToken) ?? new Dictionary<string, string>())
            {
                ["CODALUS_NUGET_ORG_INDEX"] = _nugetOrg.IndexUrl,
            });
        _service.Log += (_, line) => Console.Error.WriteLine("[host] " + line);
        _packages = new PackageService(_service);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _service.DisposeAsync();
        await _nugetOrg.DisposeAsync();
        await _mirror.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Writes the solution's <c>nuget.config</c> (read again by every request) and opens the solution once.</summary>
    private async Task ConfigureAsync(string sources, string? mapping = null)
    {
        File.WriteAllText(Path.Combine(Fixture, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                {sources}
              </packageSources>
              {(mapping is null ? string.Empty : $"<packageSourceMapping>{mapping}</packageSourceMapping>")}
            </configuration>
            """);
        if (_service.Solution is null)
        {
            await _service.OpenAsync(Path.Combine(Fixture, "Packages.slnx"), trusted: true, restore: false, Timeout());
        }
    }

    private string NuGetOrg => $"""<add key="nuget.org" value="{_nugetOrg.IndexUrl}" allowInsecureConnections="true" />""";

    private string Mirror => $"""<add key="contoso-mirror" value="{_mirror.IndexUrl}" allowInsecureConnections="true" />""";

    [Fact]
    public async Task Nuget_orgs_verified_flag_reaches_the_rows_and_the_details()
    {
        await ConfigureAsync(NuGetOrg);

        var result = await _packages.SearchAsync("Contoso", prerelease: false, 0, 20, null, Timeout());
        Assert.Empty(result.Errors);
        var verified = Assert.Single(result.Packages, p => p.Id == "Contoso.Verified");
        Assert.True(verified is { Verified: true, Source: "nuget.org", Version: "1.2.0" });
        Assert.False(Assert.Single(result.Packages, p => p.Id == "Contoso.Plain").Verified);

        // The details (from Installed, Updates or Consolidate: no search said) ask nuget.org once, by packageid.
        var details = await _packages.DetailsAsync("Contoso.Verified", prerelease: false, version: null, source: null, Timeout());
        Assert.True(details is { Verified: true, Source: "nuget.org" });
        Assert.Contains("packageid:Contoso.Verified", _nugetOrg.Searches);
        Assert.False((await _packages.DetailsAsync("Contoso.Plain", prerelease: false, version: null, source: null, Timeout())).Verified);

        // From Browse, the row's flag is used and nuget.org isn't asked again.
        var asked = _nugetOrg.Searches.Count();
        Assert.True((await _packages.DetailsAsync("Contoso.Verified", prerelease: false, version: "1.1.0", source: null, Timeout(), verified: true)).Verified);
        Assert.False((await _packages.DetailsAsync("Contoso.Verified", prerelease: false, version: null, source: null, Timeout(), verified: false)).Verified);
        Assert.Equal(asked, _nugetOrg.Searches.Count());

        // Offline: no online source, nothing asked, no badge.
        _packages.Offline = true;
        Assert.DoesNotContain((await _packages.SearchAsync("Contoso", prerelease: false, 0, 20, null, Timeout())).Packages, p => p.Verified);
        Assert.Equal(asked, _nugetOrg.Searches.Count());
    }

    [Fact]
    public async Task Another_feed_that_passes_verified_on_never_shows_it()
    {
        // A mirror (or a feed with nuget.org as its upstream) says verified: true, and is the only source.
        await ConfigureAsync(Mirror);

        var result = await _packages.SearchAsync("Contoso", prerelease: false, 0, 20, null, Timeout());
        Assert.Empty(result.Errors);
        Assert.Equal("1.2.1", Assert.Single(result.Packages, p => p.Id == "Contoso.Verified").Version);
        Assert.All(result.Packages, p => Assert.False(p.Verified));

        var details = await _packages.DetailsAsync("Contoso.Verified", prerelease: false, version: null, source: null, Timeout());
        Assert.True(details is { Verified: false, Source: "contoso-mirror" });
        Assert.DoesNotContain(_mirror.Searches, q => q.StartsWith("packageid:", StringComparison.Ordinal));
        Assert.False((await _packages.DetailsAsync("Contoso.Verified", prerelease: false, version: null, source: null, Timeout(), verified: true)).Verified);
    }

    [Fact]
    public async Task With_several_sources_only_nuget_org_by_itself_or_mapped_alone_shows_it()
    {
        // nuget.org and a mirror, no mapping: All sources could install either copy, so none; nuget.org by itself shows it.
        await ConfigureAsync(NuGetOrg + Mirror);
        var all = await _packages.SearchAsync("Contoso.Verified", prerelease: false, 0, 20, null, Timeout());
        Assert.False(Assert.Single(all.Packages).Verified);
        var alone = await _packages.SearchAsync("Contoso.Verified", prerelease: false, 0, 20, "nuget.org", Timeout());
        Assert.True(Assert.Single(alone.Packages) is { Verified: true, Source: "nuget.org" });
        Assert.False((await _packages.DetailsAsync("Contoso.Verified", prerelease: false, version: "1.2.0", source: null, Timeout())).Verified);
        Assert.True((await _packages.DetailsAsync("Contoso.Verified", prerelease: false, version: null, source: "nuget.org", Timeout())).Verified);

        // Mapping sends Contoso.* to nuget.org alone: All sources shows it, and the mirror's 1.2.1, which restore would
        // refuse, is left out of search and of the Version box (question 10).
        await ConfigureAsync(NuGetOrg + Mirror, """
            <packageSource key="nuget.org"><package pattern="Contoso.*" /></packageSource>
            <packageSource key="contoso-mirror"><package pattern="*" /></packageSource>
            """);
        var mapped = await _packages.SearchAsync("Contoso.Verified", prerelease: false, 0, 20, null, Timeout());
        Assert.True(Assert.Single(mapped.Packages) is { Verified: true, Source: "nuget.org", Version: "1.2.0" });
        var details = await _packages.DetailsAsync("Contoso.Verified", prerelease: false, version: null, source: null, Timeout());
        Assert.True(details is { Verified: true, Source: "nuget.org", Version: "1.2.0" });
        Assert.Equal(["1.2.0", "1.1.0"], details.Versions.Select(v => v.Version));

        // The mirror alone can't provide a Contoso package under that mapping.
        var mirrorOnly = await _packages.SearchAsync("Contoso", prerelease: false, 0, 20, "contoso-mirror", Timeout());
        Assert.Empty(mirrorOnly.Packages);
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => _packages.DetailsAsync("Contoso.Verified", prerelease: false, version: null, source: "contoso-mirror", Timeout()));
        Assert.Contains("mapping", refused.Message, StringComparison.OrdinalIgnoreCase);

        // Mapped to both: either copy could be restored, so none.
        await ConfigureAsync(NuGetOrg + Mirror, """
            <packageSource key="nuget.org"><package pattern="Contoso.*" /></packageSource>
            <packageSource key="contoso-mirror"><package pattern="Contoso.*" /></packageSource>
            """);
        Assert.False(Assert.Single((await _packages.SearchAsync("Contoso.Verified", prerelease: false, 0, 20, null, Timeout())).Packages).Verified);
    }
}
