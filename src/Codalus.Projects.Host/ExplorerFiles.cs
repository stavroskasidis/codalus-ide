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

    /// <summary>Folders the tree and the project discovery never descend into.</summary>
    public static FrozenSet<string> SkippedFolders { get; } = new[] { ".git", ".vs", ".idea", "node_modules" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Everything under a folder, without version control, IDE and package folders, up to a limit; with
    /// <see cref="ListTreeParams.Folders"/>, going only into those folders.
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

        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = OperatingSystem.IsWindows() ? FileAttributes.Hidden | FileAttributes.System : 0 };
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
                if (child is DirectoryInfo sub && (SkippedFolders.Contains(sub.Name) || (sub.Attributes & FileAttributes.ReparsePoint) != 0))
                {
                    continue;
                }

                if (entries.Count >= max)
                {
                    return new ListTreeResult([.. entries], true);
                }

                entries.Add(new DirectoryEntry(child.FullName, child is DirectoryInfo));
                if (child is DirectoryInfo folder && (folders is null || folders.Contains(folder.FullName)))
                {
                    pending.Push(folder);
                }
            }
        }

        return new ListTreeResult([.. entries], false);
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
