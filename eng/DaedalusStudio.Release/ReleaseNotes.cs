using System.Text;
using System.Text.RegularExpressions;

namespace DaedalusStudio.Release;

internal sealed record Commit(string ShortSha, string Subject);

/// <summary>What the notes are made from: the commits since the previous release of the same kind, and the context.</summary>
internal sealed record ReleaseNotesInput(
    string Version,
    string Repository,
    string Reference,
    string? PreviousTag,
    IReadOnlyList<Commit> Commits,
    IReadOnlyList<string> Checkpoints,
    string? Changelog);

/// <summary>
/// Release notes (M31): a hand-written CHANGELOG.md section first, then the commits since the previous release, grouped by
/// the milestone their subject names and linked to that milestone's checkpoint summaries.
/// </summary>
internal static partial class ReleaseNotes
{
    public static string Build(ReleaseNotesInput input)
    {
        var text = new StringBuilder();
        text.Append("## Daedalus Studio ").AppendLine(input.Version).AppendLine();
        if (ChangelogSection(input.Changelog, BaseVersion(input.Version)) is { } handWritten)
        {
            text.AppendLine(handWritten).AppendLine();
        }

        var groups = new List<(string Key, List<Commit> Commits)>();
        foreach (var commit in input.Commits)
        {
            var key = MilestoneOf(commit.Subject) ?? string.Empty;
            var group = groups.FindIndex(g => g.Key == key);
            if (group < 0)
            {
                groups.Add((key, [commit]));
            }
            else
            {
                groups[group].Commits.Add(commit);
            }
        }

        // Milestones in the order they were built, then everything else.
        foreach (var (key, commits) in groups.OrderBy(g => g.Key.Length == 0))
        {
            text.Append("### ").AppendLine(key.Length == 0 ? "Other changes" : Heading(key, commits)).AppendLine();
            if (key.Length > 0 && CheckpointLinks(key, commits, input) is { Length: > 0 } links)
            {
                text.Append("Checkpoint: ").AppendLine(links).AppendLine();
            }

            foreach (var commit in commits)
            {
                text.Append("- ").Append(commit.Subject).Append(" (").Append(commit.ShortSha).AppendLine(")");
            }

            text.AppendLine();
        }

        if (input.Commits.Count == 0)
        {
            text.AppendLine("No changes since the previous release.").AppendLine();
        }

        if (input.PreviousTag is { } previous)
        {
            text.Append("**All changes:** https://github.com/").Append(input.Repository).Append("/compare/")
                .Append(previous).Append("...").AppendLine(input.Reference);
        }

        // The same bytes on every OS (AppendLine writes CRLF on Windows).
        return text.ToString().ReplaceLineEndings("\n").TrimEnd() + "\n";
    }

    /// <summary>The milestone a commit subject starts with: <c>M16d</c> for "M16d: …" or "M16d checkpoint: …".</summary>
    public static string? MilestoneOf(string subject) => Milestone().Match(subject) is { Success: true } match ? match.Groups["id"].Value : null;

    /// <summary>"M16d: web front-end depth" from the milestone's own commit; just the id when only follow-ups are listed.</summary>
    private static string Heading(string key, List<Commit> commits) =>
        commits.Select(c => MilestoneTitle().Match(c.Subject)).FirstOrDefault(m => m.Success && m.Groups["ids"].Value.StartsWith(key, StringComparison.Ordinal)) is { } title
            ? $"{title.Groups["ids"].Value}: {title.Groups["title"].Value}"
            : key;

    private static string CheckpointLinks(string key, List<Commit> commits, ReleaseNotesInput input)
    {
        // "M9 and M10: …" covers both milestones' checkpoints; M9 has m9a.md and m9b.md.
        var ids = commits.Select(c => MilestoneTitle().Match(c.Subject)).Where(m => m.Success)
            .SelectMany(m => m.Groups["ids"].Value.Split(" and ")).Append(key).Distinct(StringComparer.Ordinal);
        var names = ids.SelectMany(id => input.Checkpoints.Where(name => CheckpointOf(name, id))).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        return string.Join(", ", names.Select(name =>
            $"[{Path.GetFileNameWithoutExtension(name).ToUpperInvariant()}](https://github.com/{input.Repository}/blob/{input.Reference}/docs/checkpoints/{name})"));
    }

    /// <summary>m16d.md for M16d; m9a.md and m9b.md for M9 (a milestone split into parts).</summary>
    private static bool CheckpointOf(string name, string id)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var lower = id.ToLowerInvariant();
        return stem == lower || (char.IsDigit(lower[^1]) && stem.Length == lower.Length + 1 && stem.StartsWith(lower, StringComparison.Ordinal) && char.IsLetter(stem[^1]));
    }

    /// <summary>A CHANGELOG.md section headed "## 0.2.0" or "## [0.2.0]" (anything after the version on the line is kept out).</summary>
    public static string? ChangelogSection(string? changelog, string version)
    {
        if (changelog is null)
        {
            return null;
        }

        var lines = changelog.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = Array.FindIndex(lines, line => ChangelogHeading().Match(line) is { Success: true } m && m.Groups["version"].Value == version);
        if (start < 0)
        {
            return null;
        }

        var end = Array.FindIndex(lines, start + 1, line => line.StartsWith("## ", StringComparison.Ordinal));
        var body = string.Join('\n', lines[(start + 1)..(end < 0 ? lines.Length : end)]).Trim();
        return body.Length == 0 ? null : body;
    }

    /// <summary>0.2.0 for 0.2.0-nightly.20261011.1: a nightly shows the hand-written notes of the release it leads to.</summary>
    private static string BaseVersion(string version) => version.Split('-', 2)[0];

    [GeneratedRegex(@"^(?<id>M\d+[a-z]?)\b")]
    private static partial Regex Milestone();

    [GeneratedRegex(@"^(?<ids>M\d+[a-z]?(?: and M\d+[a-z]?)*): (?<title>.+)$")]
    private static partial Regex MilestoneTitle();

    [GeneratedRegex(@"^## \[?(?<version>\d+\.\d+\.\d+)\]?")]
    private static partial Regex ChangelogHeading();
}

/// <summary>Reads what <see cref="ReleaseNotes"/> needs from the repository.</summary>
internal static class ReleaseNotesSource
{
    /// <summary>The newest earlier release of the same kind reachable from <paramref name="to"/>, or null for the first one.</summary>
    public static string? PreviousTag(Git git, ReleaseKind kind, string to, string? excluding)
    {
        Func<string, bool> sameKind = kind == ReleaseKind.Nightly ? ReleaseVersion.IsNightlyTag : ReleaseVersion.IsStableTag;
        return git.Lines("tag", "--merged", to, "--sort=-v:refname").FirstOrDefault(tag => sameKind(tag) && tag != excluding);
    }

    public static ReleaseNotesInput Read(Git git, ReleaseKind kind, string version, string repository, string to, string? since, string? changelogFile)
    {
        var tag = ReleaseVersion.Tag(version);
        since ??= PreviousTag(git, kind, to, excluding: tag);
        var range = since is null ? to : $"{since}..{to}";
        var commits = git.Lines("log", "--reverse", "--no-merges", "--format=%h%x1f%s", range)
            .Select(line => line.Split('\u001f', 2)).Select(parts => new Commit(parts[0], parts[1])).ToList();
        var checkpoints = git.Lines("ls-tree", "--name-only", to, "docs/checkpoints/").Select(Path.GetFileName).OfType<string>()
            .Where(name => name.EndsWith(".md", StringComparison.Ordinal)).ToList();
        var changelog = changelogFile is not null && File.Exists(Path.Combine(git.Repository, changelogFile))
            ? File.ReadAllText(Path.Combine(git.Repository, changelogFile))
            : null;
        return new ReleaseNotesInput(version, repository, tag, since, commits, checkpoints, changelog);
    }
}
