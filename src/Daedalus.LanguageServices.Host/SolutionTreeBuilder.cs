using Daedalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;

namespace Daedalus.LanguageServices.Host;

/// <summary>
/// Builds the tree shown in the solution explorer from a Roslyn solution.
/// TODO(issue): M2 replaces this with the build host's MSBuild-evaluated project model
/// (solution folders, dependencies node, file nesting, non-compile items, F#/VB projects).
/// </summary>
public static class SolutionTreeBuilder
{
    public static SolutionNode Build(string solutionPath, Solution solution)
    {
        var projects = solution.Projects
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(BuildProject)
            .ToArray();
        return new SolutionNode(SolutionNodeKind.Solution, Path.GetFileNameWithoutExtension(solutionPath), solutionPath, projects);
    }

    private static SolutionNode BuildProject(Project project)
    {
        var root = new FolderBuilder(project.Name);
        var projectDir = Path.GetDirectoryName(project.FilePath) ?? string.Empty;
        foreach (var document in project.Documents)
        {
            // Skip generated files that live under obj/ (global usings, assembly info).
            if (document.FilePath is { } path && IsUnderObj(projectDir, path))
            {
                continue;
            }

            var folder = root;
            foreach (var segment in document.Folders)
            {
                folder = folder.GetOrAdd(segment);
            }

            folder.Documents.Add(new SolutionNode(SolutionNodeKind.Document, document.Name, document.FilePath, []));
        }

        return new SolutionNode(SolutionNodeKind.Project, project.Name, project.FilePath, root.BuildChildren());
    }

    private static bool IsUnderObj(string projectDir, string path)
    {
        var relative = Path.GetRelativePath(projectDir, path);
        return relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || relative.StartsWith("obj" + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FolderBuilder(string name)
    {
        private readonly Dictionary<string, FolderBuilder> _folders = new(StringComparer.OrdinalIgnoreCase);

        public string Name { get; } = name;

        public List<SolutionNode> Documents { get; } = [];

        public FolderBuilder GetOrAdd(string segment)
        {
            if (!_folders.TryGetValue(segment, out var folder))
            {
                folder = new FolderBuilder(segment);
                _folders.Add(segment, folder);
            }

            return folder;
        }

        public SolutionNode[] BuildChildren() =>
        [
            .. _folders.Values
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new SolutionNode(SolutionNodeKind.Folder, f.Name, null, f.BuildChildren())),
            .. Documents.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }
}
