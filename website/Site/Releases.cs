namespace DaedalusStudio.Website.Site;

/// <summary>
/// The release the download page offers: the newest stable one, else the newest nightly, else none (the links then lead
/// to the releases page). Release files are named <c>daedalus-studio-&lt;version&gt;-&lt;rid&gt;&lt;suffix&gt;</c>
/// (<c>eng/package.sh</c>), and the tag is <c>v&lt;version&gt;</c> (<c>eng/publish-release.sh</c>).
/// </summary>
internal sealed class Releases(SiteOptions options)
{
    public string? Stable { get; } = Version(options.Stable);

    public string? Nightly { get; } = Version(options.Nightly);

    /// <summary>The version the download page offers.</summary>
    public string? Offered => Stable ?? Nightly;

    /// <summary>Only nightlies so far: the page offers the newest one, marked as a prerelease.</summary>
    public bool OnlyNightly => Stable is null && Nightly is not null;

    public string ReleasesUrl => $"{options.GitHub}/releases";

    public string ReleaseUrl(string? version) => version is null ? ReleasesUrl : $"{ReleasesUrl}/tag/v{version}";

    /// <summary>The file name of one package of the offered release, <c>&lt;version&gt;</c> standing in without one.</summary>
    public string FileName(string suffix) => $"daedalus-studio-{Offered ?? "<version>"}-{suffix}";

    public string AssetUrl(string suffix) =>
        Offered is { } version ? $"{ReleasesUrl}/download/v{version}/{FileName(suffix)}" : ReleasesUrl;

    private static string? Version(string? tag) =>
        tag is null ? null : tag.StartsWith('v') ? tag[1..] : throw new SiteException($"a release tag starts with v: {tag}");
}
