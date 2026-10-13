namespace Codalus.Release.Tests;

public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("0.2.0", 0, 2, 0)]
    [InlineData(" 1.10.3\n", 1, 10, 3)]
    public void The_version_file_holds_major_minor_and_patch(string text, int major, int minor, int patch)
    {
        Assert.Equal(new ReleaseVersion(major, minor, patch), ReleaseVersion.Parse(text));
    }

    [Theory]
    [InlineData("0.2")]
    [InlineData("v0.2.0")]
    [InlineData("0.2.0-dev")]
    [InlineData("01.2.0")]
    [InlineData("")]
    public void Anything_else_is_refused(string text)
    {
        Assert.Throws<FormatException>(() => ReleaseVersion.Parse(text));
    }

    [Fact]
    public void Each_kind_of_build_has_its_version()
    {
        var version = new ReleaseVersion(0, 2, 0);

        Assert.Equal("0.2.0", version.For(ReleaseKind.Stable));
        Assert.Equal("0.2.0-nightly.20261011.7", version.For(ReleaseKind.Nightly, new DateOnly(2026, 10, 11), 7));
        Assert.Equal("0.2.0-dev", version.For(ReleaseKind.Dev));
        Assert.Throws<ArgumentOutOfRangeException>(() => version.For(ReleaseKind.Nightly, new DateOnly(2026, 10, 11)));
    }

    [Fact]
    public void A_nightly_sorts_before_its_stable_release_and_after_the_previous_one()
    {
        // SemVer 2 precedence, which Velopack and NuGet follow: a prerelease is lower than its release.
        var nightly = NuGetLike("0.2.0-nightly.20261011.1");
        Assert.True(Compare(NuGetLike("0.1.0"), nightly) < 0);
        Assert.True(Compare(nightly, NuGetLike("0.2.0")) < 0);
        Assert.True(Compare(nightly, NuGetLike("0.2.0-nightly.20261012.2")) < 0);
        Assert.True(Compare(NuGetLike("0.2.0-nightly.20261012.2"), NuGetLike("0.2.0-nightly.20261012.10")) < 0);
    }

    [Fact]
    public void After_a_stable_release_develop_builds_the_next_minor_version()
    {
        Assert.Equal(new ReleaseVersion(0, 3, 0), new ReleaseVersion(0, 2, 4).NextMinor());
    }

    [Theory]
    [InlineData("0.2.0", "0.2.0", "0.3.0")] // the usual case: develop was released as it is
    [InlineData("0.3.0", "0.2.1", "0.3.0")] // a hotfix from master: develop has moved on already
    [InlineData("0.2.0", "0.2.1", "0.3.0")] // a hotfix before develop moved on
    [InlineData("1.0.0", "0.9.0", "1.0.0")]
    public void After_a_release_develop_moves_past_it(string develop, string released, string expected)
    {
        Assert.Equal(ReleaseVersion.Parse(expected), ReleaseVersion.Parse(develop).After(ReleaseVersion.Parse(released)));
    }

    [Theory]
    [InlineData("v0.2.0", true, false)]
    [InlineData("v0.2.0-nightly.20261011.1", false, true)]
    [InlineData("v0.2.0-rc.1", false, false)]
    [InlineData("0.2.0", false, false)]
    public void Tags_tell_stable_releases_from_nightlies(string tag, bool stable, bool nightly)
    {
        Assert.Equal(stable, ReleaseVersion.IsStableTag(tag));
        Assert.Equal(nightly, ReleaseVersion.IsNightlyTag(tag));
    }

    private static (int[] Core, string[] Pre) NuGetLike(string version)
    {
        var parts = version.Split('-', 2);
        return (parts[0].Split('.').Select(int.Parse).ToArray(), parts.Length > 1 ? parts[1].Split('.') : []);
    }

    /// <summary>SemVer 2.0 precedence (numeric identifiers compare as numbers).</summary>
    private static int Compare((int[] Core, string[] Pre) a, (int[] Core, string[] Pre) b)
    {
        for (var i = 0; i < 3; i++)
        {
            if (a.Core[i] != b.Core[i])
            {
                return a.Core[i].CompareTo(b.Core[i]);
            }
        }

        if (a.Pre.Length == 0 || b.Pre.Length == 0)
        {
            return b.Pre.Length.CompareTo(a.Pre.Length);
        }

        for (var i = 0; i < Math.Min(a.Pre.Length, b.Pre.Length); i++)
        {
            var result = int.TryParse(a.Pre[i], out var x) && int.TryParse(b.Pre[i], out var y)
                ? x.CompareTo(y)
                : string.CompareOrdinal(a.Pre[i], b.Pre[i]);
            if (result != 0)
            {
                return result;
            }
        }

        return a.Pre.Length.CompareTo(b.Pre.Length);
    }
}
