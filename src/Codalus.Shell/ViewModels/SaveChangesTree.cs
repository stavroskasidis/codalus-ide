using Codalus.Projects.Protocol;
using Codalus.Shell.Controls;
using Codalus.Workbench.Projects;

namespace Codalus.Shell.ViewModels;

/// <summary>
/// The rows of the save-changes dialog (closing a tab, Close All, closing the solution, exiting): the unsaved files under
/// what holds them, as the classic IDE lists them: the solution, its solution folders, the project, then the file. A file
/// outside the solution is a row of its own, after it. Only the files are rows with a check box, and only when there are
/// several: for one, Save and Don't Save are the choice.
/// </summary>
public static class SaveChangesTree
{
    /// <summary>The id of the file at <paramref name="index"/> of the list given to <see cref="Rows"/>.</summary>
    public static string FileId(int index) => "file:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The rows for <paramref name="files"/> (each one's name and path, the path null for a document that has no file):
    /// <see cref="FileId"/> for a file, checked; a group's id starts with <c>group:</c>.
    /// </summary>
    public static IReadOnlyList<DialogChoice> Rows(SolutionInfo? solution, IReadOnlyCollection<ProjectInfo> projects, IReadOnlyList<(string Name, string? Path)> files)
    {
        var checkable = files.Count > 1;
        var root = new Node("solution", solution is null ? string.Empty : solution.IsFolder ? solution.Name : Path.GetFileName(solution.Path),
            solution is null ? string.Empty : solution.IsFolder ? FileIcons.ForFolder(solution.Name, open: false) : FileIcons.Solution, Rank: 0);
        var outside = new Node("outside", string.Empty, string.Empty, Rank: 0);
        var groups = new Dictionary<string, Node>(StringComparer.Ordinal);

        for (var i = 0; i < files.Count; i++)
        {
            var (name, path) = files[i];
            var (parent, folder) = Place(path);
            parent.Children.Add(new Node(FileId(i), name, FileIcons.ForFile(name), Rank: 2)
            {
                IsFile = true,
                Detail = path is null || folder is null ? null : Folder(path, folder),
            });
        }

        var rows = new List<DialogChoice>();
        if (root.Children.Count > 0)
        {
            Emit(root, 0);
        }

        foreach (var child in Sorted(outside.Children))
        {
            Emit(child, 0);
        }

        return rows;

        // Where a file goes: under its project, its solution folder (a solution item), the solution (a file of the folder
        // a folder workspace opened that no project holds), or outside; and the folder its detail is relative to.
        (Node Parent, string? Folder) Place(string? path)
        {
            if (path is null || solution is null)
            {
                return (outside, path is null ? null : string.Empty);
            }

            if (solution.Folders.FirstOrDefault(f => f.Files.Any(file => SolutionService.PathComparer.Equals(file, path))) is { } solutionFolder)
            {
                return (FolderNode(solutionFolder.Id), null);
            }

            var info = ErrorListViewModel.ProjectOf(projects, path);
            var projectPath = info?.Path ?? solution.Projects
                .Where(p => Path.GetDirectoryName(p.Path) is { } directory && IsUnder(path, directory))
                .MaxBy(p => p.Path.Length)?.Path;
            if (projectPath is not null)
            {
                var entry = solution.Projects.FirstOrDefault(p => SolutionService.PathComparer.Equals(p.Path, projectPath));
                var key = "project:" + projectPath;
                if (!groups.TryGetValue(key, out var node))
                {
                    var parent = entry?.Parent is { } folderId ? FolderNode(folderId) : root;
                    groups[key] = node = new Node(key, entry?.Name ?? info?.Name ?? Path.GetFileNameWithoutExtension(projectPath), FileIcons.ForProject(projectPath, info), Rank: 1);
                    parent.Children.Add(node);
                }

                return (node, Path.GetDirectoryName(projectPath));
            }

            return IsUnder(path, solution.Directory) ? (root, solution.Directory) : (outside, string.Empty);
        }

        Node FolderNode(string id)
        {
            var key = "folder:" + id;
            if (groups.TryGetValue(key, out var node))
            {
                return node;
            }

            var folder = solution!.Folders.FirstOrDefault(f => f.Id == id);
            var parent = folder?.Parent is { } parentId && parentId != id ? FolderNode(parentId) : root;
            groups[key] = node = new Node(key, folder?.Name ?? id.Trim('/'), FileIcons.ForFolder(folder?.Name ?? id, open: false), Rank: 0);
            parent.Children.Add(node);
            return node;
        }

        void Emit(Node node, int level)
        {
            rows.Add(new DialogChoice(node.IsFile ? node.Key : "group:" + node.Key, node.Label, node.IsFile && checkable, node.Detail)
            {
                Icon = node.Icon,
                Level = level,
                Checkable = node.IsFile && checkable,
                IsCode = false,
            });
            foreach (var child in Sorted(node.Children))
            {
                Emit(child, level + 1);
            }
        }
    }

    /// <summary>The folder a file is in, relative to <paramref name="folder"/> (its project's); null at its root; the full folder for a file outside the solution.</summary>
    private static string? Folder(string path, string folder)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is null || SolutionService.PathComparer.Equals(directory, folder))
        {
            return null;
        }

        return folder.Length == 0 ? directory : Path.GetRelativePath(folder, directory);
    }

    private static bool IsUnder(string path, string folder) =>
        path.Length > folder.Length && path.StartsWith(folder, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
        && path[folder.Length] is '/' or '\\';

    /// <summary>Solution folders, then projects, then files, each by name.</summary>
    private static IEnumerable<Node> Sorted(List<Node> nodes) =>
        nodes.OrderBy(n => n.Rank).ThenBy(n => n.Label, StringComparer.OrdinalIgnoreCase).ThenBy(n => n.Key, StringComparer.Ordinal);

    private sealed record Node(string Key, string Label, string Icon, int Rank)
    {
        public List<Node> Children { get; } = [];

        public bool IsFile { get; init; }

        public string? Detail { get; init; }
    }
}
