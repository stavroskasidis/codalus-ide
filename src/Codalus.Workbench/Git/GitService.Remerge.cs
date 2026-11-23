using System.Globalization;

namespace Codalus.Workbench.Git;

/// <summary>
/// Whether a conflicted file is still exactly what git's merge wrote (ADR 0020, amended 2026-11-18): only then may the
/// merge editor show <see cref="ThreeWayMerge"/>'s conflicts instead of git's, since rerere, a merge driver or an edit
/// would be lost otherwise.
/// </summary>
public sealed partial class GitService
{
    private static readonly string?[] s_plainStyle = [null];
    private static readonly string?[] s_baseStyles = ["--diff3", "--zdiff3"];

    /// <summary>
    /// Whether <paramref name="workTree"/> is git's own merge of the index's three stages of <paramref name="path"/>:
    /// <c>git merge-file</c> with merge-ort's histogram diff, the file's conflict style and marker labels gives exactly
    /// it (apart from the line endings checkout gave it). False when it isn't, when a stage is missing, or when git is
    /// older than 2.44 (<c>merge-file --diff-algorithm</c>).
    /// </summary>
    public async Task<bool> IsGitsOwnMergeAsync(string path, string workTree, CancellationToken cancellationToken = default)
    {
        if (Root is null)
        {
            return false;
        }

        var (code, output, _) = await RunAsync(Root, ["ls-files", "-s", "-z", "--", Relative(path)], cancellationToken, check: false, readOnly: true).ConfigureAwait(false);
        if (code != 0)
        {
            return false;
        }

        var stages = new string?[4];
        foreach (var entry in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // <mode> <object> <stage>\t<path>
            var fields = entry.Split('\t', 2)[0].Split(' ');
            if (fields.Length == 3 && int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var stage) && stage is >= 1 and <= 3)
            {
                stages[stage] = fields[1];
            }
        }

        return stages is [_, { } @base, { } ours, { } theirs]
            && await IsGitsOwnMergeAsync(Root, objectIds: true, [ours, @base, theirs], workTree, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="IsGitsOwnMergeAsync(string, string, CancellationToken)"/> for <c>codalus merge</c> (git mergetool):
    /// git's LOCAL, BASE and REMOTE files, and MERGED's text.
    /// </summary>
    public async Task<bool> IsGitsOwnMergeOfFilesAsync(string local, string @base, string remote, string workTree, CancellationToken cancellationToken = default) =>
        File.Exists(local) && File.Exists(@base) && File.Exists(remote)
        && await IsGitsOwnMergeAsync(Path.GetDirectoryName(Path.GetFullPath(local))!, objectIds: false, [Path.GetFullPath(local), Path.GetFullPath(@base), Path.GetFullPath(remote)], workTree, cancellationToken).ConfigureAwait(false);

    private async Task<bool> IsGitsOwnMergeAsync(string directory, bool objectIds, IReadOnlyList<string> sources, string workTree, CancellationToken cancellationToken)
    {
        if (ThreeWayMerge.ConflictLabels(workTree) is not var (ours, @base, theirs))
        {
            return false;
        }

        var lf = workTree.Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (var style in @base is null ? s_plainStyle : s_baseStyles)
        {
            if (await MergeFileAsync(directory, objectIds, sources, style, (ours, @base ?? "base", theirs), cancellationToken).ConfigureAwait(false) is not { } git)
            {
                return false;
            }

            if (git == workTree || git == lf)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><c>git merge-file -p --diff-algorithm=histogram</c>: the merged text, or null when git failed or doesn't know an option.</summary>
    private async Task<string?> MergeFileAsync(
        string directory, bool objectIds, IReadOnlyList<string> sources, string? style, (string Ours, string Base, string Theirs) labels, CancellationToken cancellationToken)
    {
        List<string> arguments = ["merge-file", "-p", "--diff-algorithm=histogram"];
        if (style is not null)
        {
            arguments.Add(style);
        }

        if (objectIds)
        {
            arguments.Add("--object-id");
        }

        arguments.AddRange(["-L", labels.Ours, "-L", labels.Base, "-L", labels.Theirs]);
        arguments.AddRange(sources);

        // The exit code is the number of conflicts (at most 127); an error is negative (255 outside Windows), and an
        // unknown option (git older than 2.44) is 129.
        var (code, output, _) = await RunAsync(directory, arguments, cancellationToken, check: false, readOnly: true).ConfigureAwait(false);
        return code is >= 0 and <= 127 ? output : null;
    }
}
