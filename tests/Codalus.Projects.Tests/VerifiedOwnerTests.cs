using Codalus.Projects.Host.Packages;

namespace Codalus.Projects.Tests;

/// <summary>
/// The verified-owner badge's rules (D39, questions 7, 9 and 10), on their own: which source is nuget.org, when its
/// flag may show, and which sources package source mapping lets serve an id.
/// </summary>
public sealed class VerifiedOwnerTests
{
    [Theory]
    [InlineData("https://api.nuget.org/v3/index.json", true)]
    [InlineData("https://API.NuGet.org/v3/index.json", true)]
    [InlineData(" https://api.nuget.org/v3/index.json ", true)]
    [InlineData("http://api.nuget.org/v3/index.json", false)] // not https
    [InlineData("https://api.nuget.org:8443/v3/index.json", false)] // another port
    [InlineData("https://user:secret@api.nuget.org/v3/index.json", false)]
    [InlineData("https://www.nuget.org/api/v2", false)] // the V2 API's host
    [InlineData("https://api.nuget.org.contoso.com/v3/index.json", false)]
    [InlineData("https://contoso.com/api.nuget.org/v3/index.json", false)]
    [InlineData("https://pkgs.dev.azure.com/contoso/_packaging/upstream/nuget/v3/index.json", false)] // nuget.org as an upstream
    [InlineData("https://nuget-mirror.contoso.com/v3/index.json", false)]
    [InlineData("/home/user/feed", false)]
    [InlineData(@"C:\feed", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_https_on_exactly_api_nuget_org_is_nuget_org(string? source, bool expected) =>
        Assert.Equal(expected, VerifiedOwner.IsNuGetOrg(source));

    [Fact]
    public void The_tests_mock_feed_counts_only_through_its_exact_index()
    {
        const string mock = "http://127.0.0.1:5000/abc/v3/index.json";
        Assert.True(VerifiedOwner.IsNuGetOrg(mock, mock));
        Assert.False(VerifiedOwner.IsNuGetOrg("http://127.0.0.1:5000/other/v3/index.json", mock));
        Assert.False(VerifiedOwner.IsNuGetOrg(mock));
        Assert.True(VerifiedOwner.IsNuGetOrg("https://api.nuget.org/v3/index.json", mock));
    }

    [Fact]
    public void The_badge_needs_nuget_org_as_the_one_source_that_may_serve_the_id()
    {
        const string nugetOrg = "https://api.nuget.org/v3/index.json";
        Assert.True(VerifiedOwner.MayBadge("nuget.org", nugetOrg, ["nuget.org"]));
        Assert.True(VerifiedOwner.MayBadge("nuget.org", nugetOrg, ["NuGet.org"]));
        Assert.False(VerifiedOwner.MayBadge("nuget.org", nugetOrg, ["local", "nuget.org"])); // All sources, no mapping
        Assert.False(VerifiedOwner.MayBadge("nuget.org", nugetOrg, ["local"]));
        Assert.False(VerifiedOwner.MayBadge("nuget.org", nugetOrg, []));
        Assert.False(VerifiedOwner.MayBadge("mirror", "https://nuget-mirror.contoso.com/v3/index.json", ["mirror"])); // a mirror by itself
    }

    [Fact]
    public void Mapping_keeps_only_the_sources_the_id_maps_to()
    {
        string[] searched = ["local", "nuget.org", "Global packages folder"];
        Assert.Equal(searched, VerifiedOwner.Allowed(searched, mapped: null)); // no mapping: every source
        Assert.Equal(["nuget.org"], VerifiedOwner.Allowed(searched, ["NuGet.org"]));
        Assert.Equal(["local", "nuget.org"], VerifiedOwner.Allowed(searched, ["nuget.org", "local"]));
        Assert.Empty(VerifiedOwner.Allowed(searched, [])); // an id no pattern matches: restore takes it from nowhere
        Assert.Equal(["nuget.org", "Global packages folder"], VerifiedOwner.Allowed(searched, ["nuget.org"], keep: "Global packages folder"));
    }
}
