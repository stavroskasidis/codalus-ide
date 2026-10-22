namespace Codalus.Projects.Host.Packages;

/// <summary>
/// NuGet's verified-owner badge (D39, docs/design/controls.md "NuGet's verified owner"): which search results may carry
/// nuget.org's <c>verified</c> (its ID prefix is reserved for one of the package's owners). Only nuget.org itself
/// counts, stricter than the classic IDE: a feed with nuget.org as its upstream, a mirror, a private feed or a folder
/// never does, even when it passes <c>verified: true</c> on (question 7). And only when the result can be nobody else's
/// copy: nuget.org is the one source that may serve the ID, because it was searched by itself or package source mapping
/// maps the ID to it alone (question 9). Without NuGet types, so the tests compile it on its own.
/// </summary>
internal static class VerifiedOwner
{
    /// <summary>nuget.org's API host: a source counts only when it is https on exactly this host.</summary>
    public const string NuGetOrgHost = "api.nuget.org";

    /// <summary>
    /// A service index URL the host also counts as nuget.org's, for the package tests' mock feed only
    /// (docs/environment-variables.md).
    /// </summary>
    public const string TestIndexVariable = "CODALUS_NUGET_ORG_INDEX";

    /// <summary>Whether <paramref name="source"/> (a package source's URL or folder) is nuget.org.</summary>
    /// <param name="testIndex">The test feed's index URL that also counts (<see cref="TestIndexVariable"/>), or null.</param>
    public static bool IsNuGetOrg(string? source, string? testIndex = null)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(testIndex) && string.Equals(source.Trim(), testIndex.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Uri.TryCreate(source.Trim(), UriKind.Absolute, out var uri)
               && uri.Scheme == Uri.UriSchemeHttps
               && string.Equals(uri.Host, NuGetOrgHost, StringComparison.OrdinalIgnoreCase)
               && uri.IsDefaultPort
               && uri.UserInfo.Length == 0;
    }

    /// <summary>
    /// Whether a result from <paramref name="source"/> may show the badge: <paramref name="source"/> is nuget.org and the only
    /// source that may serve the ID, <paramref name="candidates"/> being the sources searched for it that package source
    /// mapping lets restore take it from (every searched source without mapping).
    /// </summary>
    public static bool MayBadge(string sourceName, string sourceUrl, IReadOnlyCollection<string> candidates, string? testIndex = null) =>
        IsNuGetOrg(sourceUrl, testIndex)
        && candidates.Count == 1
        && string.Equals(candidates.First(), sourceName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The sources (by name, from <paramref name="searched"/>) that package source mapping lets restore take
    /// <paramref name="id"/> from (question 10): those <paramref name="mapped"/> names, the configured sources of the ID's
    /// most specific pattern; every searched source when mapping is off (<paramref name="mapped"/> null).
    /// <paramref name="keep"/> names sources that stay whatever the mapping (the global packages folder, offline: it holds
    /// what restores already took from allowed sources).
    /// </summary>
    public static IReadOnlyList<string> Allowed(IEnumerable<string> searched, IReadOnlyCollection<string>? mapped, string? keep = null) =>
        mapped is null
            ? [.. searched]
            : [.. searched.Where(s => string.Equals(s, keep, StringComparison.OrdinalIgnoreCase) || mapped.Contains(s, StringComparer.OrdinalIgnoreCase))];
}
