namespace DaedalusStudio.Website.Site;

/// <summary>What the site is built from: the configuration section <c>Site</c> (or <c>--stable</c>, <c>--nightly</c>,
/// <c>--ref</c>, <c>--base-url</c> and <c>--repo</c> on the command line).</summary>
internal sealed record SiteOptions
{
    public const string DefaultRepository = "stavroskasidis/daedalus-studio";
    public const string DefaultBaseUrl = "https://stavroskasidis.com/daedalus-studio/";

    /// <summary>The repository checkout: <c>website/wwwroot</c> and <c>docs/user-guide</c> are read from it.</summary>
    public string RepoRoot { get; init; } = "";

    /// <summary>The GitHub repository, <c>owner/name</c>.</summary>
    public string Repository { get; init; } = DefaultRepository;

    /// <summary>The branch that links to files in the repository point at.</summary>
    public string Ref { get; init; } = "master";

    /// <summary>The address Pages serves the site at, ending in <c>/</c>.</summary>
    public string BaseUrl { get; init; } = DefaultBaseUrl;

    /// <summary>The page's <c>&lt;base href&gt;</c>: <c>/</c> when the app serves the site, the path of
    /// <see cref="BaseUrl"/> in an export. Every link on the site is relative to it.</summary>
    public string BasePath { get; init; } = "/";

    /// <summary>The newest stable release's tag (<c>v0.1.0</c>), if there is one.</summary>
    public string? Stable { get; init; }

    /// <summary>The newest nightly's tag (<c>v0.2.0-nightly.20261013.1</c>), if there is one.</summary>
    public string? Nightly { get; init; }

    public string WebRoot => Path.Combine(RepoRoot, "website", "wwwroot");

    public string GitHub => $"https://github.com/{Repository}";

    /// <summary>A file of the repository on GitHub, at <see cref="Ref"/>.</summary>
    public string Blob(string path) => $"{GitHub}/blob/{Ref}/{path}";

    public string Tree(string path) => $"{GitHub}/tree/{Ref}/{path}";

    /// <summary>Normalizes the addresses and finds the repository when <see cref="RepoRoot"/> isn't set.</summary>
    public SiteOptions Resolved() => this with
    {
        RepoRoot = RepoRoot.Length > 0 ? Path.GetFullPath(RepoRoot) : FindRepoRoot(),
        BaseUrl = BaseUrl.TrimEnd('/') + "/",
        BasePath = "/" + BasePath.Trim('/') + (BasePath.Trim('/').Length > 0 ? "/" : ""),
        Stable = string.IsNullOrWhiteSpace(Stable) ? null : Stable.Trim(),
        Nightly = string.IsNullOrWhiteSpace(Nightly) ? null : Nightly.Trim(),
    };

    private static string FindRepoRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "DaedalusStudio.slnx")))
                {
                    return dir.FullName;
                }
            }
        }

        throw new SiteException("run it inside the repository (no DaedalusStudio.slnx above here), or set Site:RepoRoot");
    }
}

/// <summary>An error in the site's sources: a broken link, a missing file.</summary>
internal sealed class SiteException(string message) : Exception(message);
