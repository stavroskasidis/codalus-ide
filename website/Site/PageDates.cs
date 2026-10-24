using System.Diagnostics;

namespace Codalus.Website.Site;

/// <summary>
/// When each page's source last changed, from git: the sitemap's <c>&lt;lastmod&gt;</c>. Search engines use it only
/// while it's accurate, so there are no dates at all unless the repository's whole history is there: a shallow
/// checkout (CI's default) would date every page to its newest commit.
/// </summary>
internal sealed class PageDates(SiteOptions options)
{
    private static readonly string[] s_folders = ["website/Components/Pages", UserGuide.Folder];

    private readonly Lazy<IReadOnlyDictionary<string, string>> _dates = new(() => Read(options.RepoRoot));

    /// <summary>The day (<c>2026-10-24</c>) of the last commit that changed <paramref name="source"/>, a path
    /// relative to the repository root, or null.</summary>
    public string? Of(string source) => _dates.Value.GetValueOrDefault(source);

    private static Dictionary<string, string> Read(string repoRoot)
    {
        var dates = new Dictionary<string, string>(StringComparer.Ordinal);
        // The root of its own repository (no prefix; comparing paths would trip over symlinks such as macOS's /var),
        // with the whole history.
        if (Git(repoRoot, "rev-parse", "--show-prefix", "--is-shallow-repository") is not ["", "false", ..])
        {
            return dates;
        }

        // Newest first: a file's first appearance is its last change.
        var day = "";
        foreach (var line in Git(repoRoot, ["log", "--format=@%cs", "--name-only", "--", .. s_folders]) ?? [])
        {
            if (line.StartsWith('@'))
            {
                day = line[1..];
            }
            else if (line.Length > 0)
            {
                dates.TryAdd(line, day);
            }
        }

        return dates;
    }

    private static string[]? Git(string repoRoot, params string[] args)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(repoRoot);
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        try
        {
            using var git = Process.Start(start)!;
            var stderr = git.StandardError.ReadToEndAsync();
            var output = git.StandardOutput.ReadToEnd();
            git.WaitForExit();
            _ = stderr.Result;
            return git.ExitCode == 0 ? output.Split('\n', StringSplitOptions.TrimEntries) : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // no git: no dates
        }
    }
}
