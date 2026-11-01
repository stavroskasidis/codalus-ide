using System.Collections.Frozen;
using Codalus.Projects.Protocol;

namespace Codalus.Projects.Host;

/// <summary>
/// What the host reads from the disk for the Solution Explorer, so that the shell's UI thread never waits on it (ADR 0033,
/// M56): Show All Files' and the folder view's listings, and the analyzers missing from a design-time build's command line.
/// No MSBuild types: it is tested on its own.
/// </summary>
internal static class ExplorerFiles
{
    private const int DefaultTreeEntries = 20_000;

    private static readonly StringComparer s_pathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Folders the project discovery never descends into.</summary>
    public static FrozenSet<string> SkippedFolders { get; } = new[] { ".git", ".vs", ".idea", "node_modules" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folders the tree never lists: version control's.</summary>
    private static readonly FrozenSet<string> s_hiddenFolders = new[] { ".git" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Everything under a folder, without version control's folder, up to a limit; with <see cref="ListTreeParams.Folders"/>,
    /// going only into those folders. IDE and package folders (<c>.vs</c>, <c>.idea</c>, <c>node_modules</c>) are listed,
    /// marked <see cref="DirectoryEntry.Skipped"/>, and gone into only when <see cref="ListTreeParams.Folders"/> names them:
    /// the Solution Explorer shows them dimmed, listed once they're expanded. They were left out, so neither Show All Files
    /// nor the folder view showed them at all (M57).
    /// </summary>
    public static ListTreeResult ListTree(ListTreeParams parameters)
    {
        var root = new DirectoryInfo(Path.GetFullPath(parameters.Path));
        var max = parameters.MaxEntries is > 0 and var limit ? limit : DefaultTreeEntries;
        var entries = new List<DirectoryEntry>();
        var folders = parameters.Folders?.Select(f => Path.TrimEndingDirectorySeparator(Path.GetFullPath(f))).ToHashSet(s_pathComparer);
        if (!root.Exists)
        {
            return new ListTreeResult([], false);
        }

        // Windows' hidden and system entries are left out, except the IDE folders, which the classic IDE makes hidden (.vs).
        var hidden = OperatingSystem.IsWindows() ? FileAttributes.Hidden | FileAttributes.System : 0;
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<FileSystemInfo> children;
            try
            {
                children = [.. directory.EnumerateFileSystemInfos("*", options)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                var skipped = child is DirectoryInfo && SkippedFolders.Contains(child.Name);
                if ((child is DirectoryInfo sub && (s_hiddenFolders.Contains(sub.Name) || (sub.Attributes & FileAttributes.ReparsePoint) != 0))
                    || ((child.Attributes & hidden) != 0 && !skipped))
                {
                    continue;
                }

                if (entries.Count >= max)
                {
                    return new ListTreeResult([.. entries], true);
                }

                entries.Add(new DirectoryEntry(child.FullName, child is DirectoryInfo) { Skipped = skipped });
                if (child is DirectoryInfo folder && (folders is null ? !skipped : folders.Contains(folder.FullName)))
                {
                    pending.Push(folder);
                }
            }
        }

        return new ListTreeResult([.. entries], false);
    }

    /// <summary>
    /// Folder mode's project discovery: the project files under <paramref name="root"/>, without bin, obj and
    /// <see cref="SkippedFolders"/> (the tree lists IDE and package folders since M57; this still never goes into them).
    /// </summary>
    public static List<string> DiscoverProjects(string root, IReadOnlyCollection<string> extensions)
    {
        var found = new List<string>();
        void Walk(string directory)
        {
            try
            {
                found.AddRange(Directory.EnumerateFiles(directory).Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)));
                foreach (var sub in Directory.EnumerateDirectories(directory))
                {
                    var name = Path.GetFileName(sub);
                    if (!SkippedFolders.Contains(name) && !name.Equals("bin", StringComparison.OrdinalIgnoreCase) && !name.Equals("obj", StringComparison.OrdinalIgnoreCase))
                    {
                        Walk(sub);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        Walk(root);
        return [.. found.Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The inputs with the analyzers that aren't on disk marked, for the Solution Explorer: the shell checked each analyzer
    /// on its UI thread for every build of its tree (6,000 checks in a 300-project solution), and on its own disk when the
    /// host runs on another machine. Cached inputs needn't be marked: they are taken only while their analyzers are there
    /// (<see cref="DesignTimeCache.TryRead(string?, string, string, string)"/>). A relative path (an <c>Analyzer</c> item's
    /// own) is the project folder's, where the compiler runs, whatever the host's working folder is when this runs.
    /// </summary>
    public static CompilerInputsInfo WithMissingAnalyzers(CompilerInputsInfo inputs) =>
        inputs.State == CompilerInputStates.Ok && CompilerInputsInfo.AnalyzerPaths(inputs.Arguments)
            .Where(a => !File.Exists(Path.GetFullPath(a, Path.GetDirectoryName(inputs.Path)!))).Distinct(s_pathComparer).ToArray() is { Length: > 0 } missing
            ? inputs with { MissingAnalyzers = missing }
            : inputs;
}
