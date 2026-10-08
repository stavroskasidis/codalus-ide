using Daedalus.Projects.Protocol;

namespace Daedalus.Workbench.Projects;

public enum ExplorerNodeKind
{
    Solution,
    SolutionFolder,
    SolutionItem,
    Project,
    Folder,
    File,
    Dependencies,
    DependencyGroup,
    Dependency,
    Message,
}

/// <summary>
/// A node of the Solution Explorer. <see cref="Id"/> is stable across rebuilds (it keeps expansion and selection);
/// <see cref="Path"/> is the file system path when the node has one.
/// </summary>
public sealed record ExplorerNode(string Id, ExplorerNodeKind Kind, string Name)
{
    public string? Path { get; init; }

    /// <summary>The project the node belongs to (for project, folder, file and dependency nodes).</summary>
    public string? ProjectPath { get; init; }

    /// <summary>The solution folder id, for solution folders.</summary>
    public string? SolutionFolderId { get; init; }

    /// <summary>A built-in icon name (see the shell's icon set).</summary>
    public string Icon { get; init; } = "file";

    /// <summary>Muted text after the name (a version, "loading…").</summary>
    public string? Detail { get; init; }

    public bool IsWarning { get; init; }

    /// <summary>A file outside the project folder, shown through a link.</summary>
    public bool IsLinked { get; init; }

    public IReadOnlyList<ExplorerNode> Children { get; init; } = [];
}

/// <summary>
/// Derives the Solution Explorer tree from the build host's model (ADR 0004: presentation lives in the shell):
/// solution folders and items, projects with their folders and files, file nesting, linked files, and a
/// Dependencies node grouped per target framework when a project multi-targets.
/// </summary>
public static class SolutionTree
{
    private static readonly StringComparer s_names = StringComparer.OrdinalIgnoreCase;

    public static ExplorerNode Build(SolutionInfo solution, IReadOnlyDictionary<string, ProjectInfo> projects)
    {
        var folders = solution.Folders.ToDictionary(f => f.Id, StringComparer.Ordinal);
        IReadOnlyList<ExplorerNode> ChildrenOf(string? parent)
        {
            var nodes = new List<ExplorerNode>();
            foreach (var folder in solution.Folders.Where(f => f.Parent == parent).OrderBy(f => f.Name, s_names))
            {
                nodes.Add(new ExplorerNode("sf:" + folder.Id, ExplorerNodeKind.SolutionFolder, folder.Name)
                {
                    SolutionFolderId = folder.Id,
                    Icon = "folder",
                    Children =
                    [
                        .. ChildrenOf(folder.Id),
                        .. folder.Files.OrderBy(System.IO.Path.GetFileName, s_names).Select(file =>
                            new ExplorerNode("si:" + file, ExplorerNodeKind.SolutionItem, System.IO.Path.GetFileName(file)) { Path = file, Icon = "file" }),
                    ],
                });
            }

            foreach (var project in solution.Projects.Where(p => p.Parent == parent && (p.Parent is null || folders.ContainsKey(p.Parent))).OrderBy(p => p.Name, s_names))
            {
                nodes.Add(Project(project, projects.GetValueOrDefault(project.Path), solution.Trusted));
            }

            return nodes;
        }

        var count = solution.Projects.Length;
        return new ExplorerNode("solution", ExplorerNodeKind.Solution, $"Solution '{System.IO.Path.GetFileNameWithoutExtension(solution.Path)}'")
        {
            Path = solution.Path,
            Icon = "solution",
            Detail = count == 1 ? "(1 project)" : $"({count} projects)",
            Children = ChildrenOf(null),
        };
    }

    public static ExplorerNode Project(SolutionProjectInfo entry, ProjectInfo? info, bool trusted)
    {
        var node = new ExplorerNode("p:" + entry.Path, ExplorerNodeKind.Project, entry.Name)
        {
            Path = entry.Path,
            ProjectPath = entry.Path,
            Icon = "project",
        };

        if (info is null)
        {
            return node with { Detail = trusted ? "(loading…)" : "(not loaded: untrusted folder)" };
        }

        if (info.State != ProjectStates.Loaded)
        {
            var detail = info.State switch
            {
                ProjectStates.Missing => "(missing)",
                ProjectStates.Unsupported => "(unsupported)",
                ProjectStates.Untrusted => "(not loaded: untrusted folder)",
                _ => "(load failed)",
            };
            return node with
            {
                Detail = detail,
                IsWarning = true,
                Children = info.Error is { } error ? [new ExplorerNode(node.Id + "|error", ExplorerNodeKind.Message, error) { Icon = "warning", IsWarning = true, ProjectPath = entry.Path }] : [],
            };
        }

        var dependencies = Dependencies(entry.Path, info);
        return node with
        {
            Detail = info.TargetFrameworks.Length > 1 ? string.Join(", ", info.TargetFrameworks) : null,
            Children = [.. dependencies is null ? [] : new[] { dependencies }, .. Contents(entry.Path, info)],
        };
    }

    // ------------------------------------------------------------------ files

    private sealed class FolderBuilder(string path, string name)
    {
        public string Path { get; } = path;

        public string Name { get; } = name;

        public Dictionary<string, FolderBuilder> Folders { get; } = new(s_names);

        public List<(string Name, string FullPath, string? DependentUpon, bool Linked)> Files { get; } = [];
    }

    /// <summary>Folders and files of a project, with nesting applied.</summary>
    public static IReadOnlyList<ExplorerNode> Contents(string projectPath, ProjectInfo info)
    {
        var projectDirectory = System.IO.Path.GetDirectoryName(projectPath)!;
        var root = new FolderBuilder(projectDirectory, string.Empty);
        var seen = new HashSet<string>(SolutionService.PathComparer);
        foreach (var item in info.Items)
        {
            if (SolutionService.PathComparer.Equals(item.Path, projectPath))
            {
                continue;
            }

            var inside = IsUnder(item.Path, projectDirectory);
            var display = item.Link ?? (inside ? System.IO.Path.GetRelativePath(projectDirectory, item.Path) : System.IO.Path.GetFileName(item.Path));
            var segments = display.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                continue;
            }

            var folder = root;
            var isFolderItem = item.Kind.Equals("Folder", StringComparison.OrdinalIgnoreCase);
            var folderSegments = isFolderItem ? segments.Length : segments.Length - 1;
            for (var i = 0; i < folderSegments; i++)
            {
                if (!folder.Folders.TryGetValue(segments[i], out var child))
                {
                    child = new FolderBuilder(System.IO.Path.Combine(folder.Path, segments[i]), segments[i]);
                    folder.Folders[segments[i]] = child;
                }

                folder = child;
            }

            if (!isFolderItem && seen.Add(item.Path))
            {
                folder.Files.Add((segments[^1], item.Path, item.DependentUpon, !inside || item.Link is not null));
            }
        }

        return Build(root, projectPath);
    }

    private static List<ExplorerNode> Build(FolderBuilder folder, string projectPath)
    {
        var nodes = new List<ExplorerNode>();
        foreach (var child in folder.Folders.Values.OrderBy(f => f.Name, s_names))
        {
            nodes.Add(new ExplorerNode("d:" + child.Path, ExplorerNodeKind.Folder, child.Name)
            {
                Path = child.Path,
                ProjectPath = projectPath,
                Icon = "folder",
                Children = Build(child, projectPath),
            });
        }

        // Nesting: each file finds its parent among its siblings; files without a parent are roots.
        var names = folder.Files.Select(f => f.Name).ToHashSet(s_names);
        var parentOf = folder.Files.ToDictionary(f => f.Name, f => NestParent(f.Name, names, f.DependentUpon), s_names);
        ExplorerNode File((string Name, string FullPath, string? DependentUpon, bool Linked) file) =>
            new("f:" + file.FullPath, ExplorerNodeKind.File, file.Name)
            {
                Path = file.FullPath,
                ProjectPath = projectPath,
                Icon = FileIcon(file.Name),
                IsLinked = file.Linked,
                Children = [.. folder.Files
                    .Where(f => s_names.Equals(parentOf[f.Name], file.Name))
                    .OrderBy(f => f.Name, s_names)
                    .Select(File)],
            };

        nodes.AddRange(folder.Files.Where(f => parentOf[f.Name] is null).OrderBy(f => f.Name, s_names).Select(File));
        return nodes;
    }

    /// <summary>
    /// The sibling a file nests under, or null. <c>DependentUpon</c> wins; then the rules Visual Studio applies by
    /// default: an added extension (<c>View.xaml.cs</c> → <c>View.xaml</c>) and a path segment
    /// (<c>appsettings.Development.json</c> → <c>appsettings.json</c>, <c>Form.Designer.cs</c> → <c>Form.cs</c>).
    /// </summary>
    public static string? NestParent(string name, IReadOnlySet<string> siblings, string? dependentUpon)
    {
        if (dependentUpon is { Length: > 0 })
        {
            var parent = dependentUpon[(dependentUpon.LastIndexOfAny(['\\', '/']) + 1)..];
            return !s_names.Equals(parent, name) && siblings.Contains(parent) ? parent : null;
        }

        // Added extension: strip the last extension.
        var withoutLast = System.IO.Path.GetFileNameWithoutExtension(name);
        if (System.IO.Path.HasExtension(withoutLast) && siblings.Contains(withoutLast))
        {
            return withoutLast;
        }

        // Path segment: remove the second-to-last segment, keeping the extension.
        var extension = System.IO.Path.GetExtension(name);
        var segmentBase = System.IO.Path.GetFileNameWithoutExtension(withoutLast);
        if (System.IO.Path.HasExtension(withoutLast) && segmentBase.Length > 0)
        {
            var candidate = segmentBase + extension;
            if (!s_names.Equals(candidate, name) && siblings.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string FileIcon(string name) => System.IO.Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".cs" or ".fs" or ".vb" or ".razor" or ".xaml" or ".axaml" or ".cshtml" or ".ts" or ".js" => "file-code",
        ".json" or ".xml" or ".resx" or ".config" or ".props" or ".targets" or ".yml" or ".yaml" => "file-data",
        _ => "file",
    };

    private static bool IsUnder(string path, string directory) =>
        path.StartsWith(System.IO.Path.TrimEndingDirectorySeparator(directory) + System.IO.Path.DirectorySeparatorChar,
            SolutionService.PathComparer == StringComparer.OrdinalIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // ------------------------------------------------------------------ dependencies

    /// <summary>
    /// The Dependencies node. TODO(issue): an Analyzers group and transitive packages need design-time build data
    /// (M3, ADR 0004).
    /// </summary>
    private static ExplorerNode? Dependencies(string projectPath, ProjectInfo info)
    {
        if (info.References.Length == 0 && info.TargetFrameworks.Length == 0)
        {
            return null;
        }

        var id = "deps:" + projectPath;
        IReadOnlyList<ExplorerNode> Groups(string prefix, IEnumerable<ReferenceInfo> references)
        {
            var list = references.ToList();
            var groups = new List<ExplorerNode>();
            foreach (var (kind, title, icon) in new[]
                     {
                         (ReferenceKinds.Framework, "Frameworks", "layout"),
                         (ReferenceKinds.Package, "Packages", "package"),
                         (ReferenceKinds.Project, "Projects", "project"),
                         (ReferenceKinds.Assembly, "Assemblies", "link"),
                     })
            {
                var members = list.Where(r => r.Kind == kind).OrderBy(r => r.Name, s_names).ToList();
                if (members.Count == 0)
                {
                    continue;
                }

                var unresolved = members.Any(m => !m.Resolved);
                groups.Add(new ExplorerNode($"{prefix}|{kind}", ExplorerNodeKind.DependencyGroup, title)
                {
                    ProjectPath = projectPath,
                    Icon = icon,
                    IsWarning = unresolved,
                    Children = [.. members.Select(m => new ExplorerNode($"{prefix}|{kind}|{m.Name}", ExplorerNodeKind.Dependency, m.Name)
                    {
                        ProjectPath = projectPath,
                        Path = m.Path,
                        Icon = icon,
                        Detail = m.Version is null ? null : $"({m.Version})",
                        IsWarning = !m.Resolved,
                    })],
                });
            }

            return groups;
        }

        var notRestored = !info.Restored && info.References.Any(r => r.Kind == ReferenceKinds.Package);
        IReadOnlyList<ExplorerNode> children = info.TargetFrameworks.Length > 1
            ? [.. info.TargetFrameworks.Select(tfm => new ExplorerNode($"{id}|{tfm}", ExplorerNodeKind.DependencyGroup, tfm)
            {
                ProjectPath = projectPath,
                Icon = "layout",
                Children = Groups($"{id}|{tfm}", info.References.Where(r => r.TargetFramework == tfm)),
            })]
            : Groups(id, info.References);
        return new ExplorerNode(id, ExplorerNodeKind.Dependencies, "Dependencies")
        {
            ProjectPath = projectPath,
            Icon = "link",
            IsWarning = notRestored || info.References.Any(r => !r.Resolved),
            Detail = notRestored ? "(not restored)" : null,
            Children = children,
        };
    }
}
