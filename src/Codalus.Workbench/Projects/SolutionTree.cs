using Codalus.Projects.Protocol;

namespace Codalus.Workbench.Projects;

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

    /// <summary>A project the user unloaded (or a solution filter left out): shown dimmed, with Reload Project.</summary>
    public bool IsUnloaded { get; init; }

    /// <summary>The solution folder id, for solution folders.</summary>
    public string? SolutionFolderId { get; init; }

    /// <summary>A built-in icon name (see the shell's icon set).</summary>
    public string Icon { get; init; } = "file";

    /// <summary>Muted text after the name (a version, "loading…").</summary>
    public string? Detail { get; init; }

    public bool IsWarning { get; init; }

    /// <summary>A file outside the project folder, shown through a link.</summary>
    public bool IsLinked { get; init; }

    /// <summary>Show All Files: a file or folder on disk that isn't part of the project (shown dimmed, ADR 0033).</summary>
    public bool IsExcluded { get; init; }

    public IReadOnlyList<ExplorerNode> Children { get; init; } = [];
}

/// <summary>
/// What the Solution Explorer shows besides the projects' items (ADR 0033): with Show All Files, the files on disk of
/// each project (<see cref="DiskEntries"/>, by project path); in folder mode's folder view, the folder's tree
/// (<see cref="FolderEntries"/>).
/// </summary>
public sealed record ExplorerOptions
{
    public IReadOnlyDictionary<string, IReadOnlyList<DirectoryEntry>>? DiskEntries { get; init; }

    /// <summary>The folder view: everything under the workspace folder (null: the solution view).</summary>
    public IReadOnlyList<DirectoryEntry>? FolderEntries { get; init; }
}

/// <summary>
/// Derives the Solution Explorer tree from the build host's model (ADR 0004: presentation lives in the shell):
/// solution folders and items, projects with their folders and files, file nesting, linked files, and a
/// Dependencies node grouped per target framework when a project multi-targets.
/// </summary>
public static class SolutionTree
{
    private static readonly StringComparer s_names = StringComparer.OrdinalIgnoreCase;

    public static ExplorerNode Build(
        SolutionInfo solution,
        IReadOnlyDictionary<string, ProjectInfo> projects,
        IReadOnlyDictionary<string, CompilerInputsInfo>? compilerInputs = null,
        ExplorerOptions? options = null)
    {
        if (solution.IsFolder && options?.FolderEntries is { } entries)
        {
            return FolderView(solution, entries);
        }

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
                nodes.Add(Project(project, projects.GetValueOrDefault(project.Path), solution.Trusted, compilerInputs?.GetValueOrDefault(project.Path), options?.DiskEntries?.GetValueOrDefault(project.Path)));
            }

            return nodes;
        }

        var count = solution.Projects.Length;
        return new ExplorerNode("solution", ExplorerNodeKind.Solution, solution.IsFolder ? $"Folder '{solution.Name}'" : $"Solution '{solution.Name}'")
        {
            Path = solution.Path,
            Icon = solution.IsFolder ? "folder" : "solution",
            Detail = solution.IsFolder ? (count == 1 ? "(1 project loaded)" : $"({count} projects loaded)") : count == 1 ? "(1 project)" : $"({count} projects)",
            Children = ChildrenOf(null),
        };
    }

    /// <summary>
    /// Folder mode's folder view (ADR 0033): the folder's files and folders as they are on disk, project files marked
    /// (with "(not loaded)" until they are loaded). Nodes inside a loaded project know it.
    /// </summary>
    public static ExplorerNode FolderView(SolutionInfo solution, IReadOnlyList<DirectoryEntry> entries)
    {
        var root = System.IO.Path.TrimEndingDirectorySeparator(solution.Directory);
        var loaded = solution.Projects.Select(p => p.Path).ToHashSet(SolutionService.PathComparer);
        string? Owner(string path) => solution.Projects
            .Select(p => p.Path)
            .Where(p => IsUnder(path, System.IO.Path.GetDirectoryName(p)!) || SolutionService.PathComparer.Equals(path, System.IO.Path.GetDirectoryName(p)))
            .OrderByDescending(p => p.Length)
            .FirstOrDefault();

        var children = new Dictionary<string, List<DirectoryEntry>>(SolutionService.PathComparer);
        foreach (var entry in entries.Where(e => IsUnder(e.Path, root)))
        {
            var parent = System.IO.Path.GetDirectoryName(entry.Path)!;
            if (!children.TryGetValue(parent, out var list))
            {
                children[parent] = list = [];
            }

            list.Add(entry);
        }

        IReadOnlyList<ExplorerNode> NodesOf(string folder) =>
        [
            .. (children.GetValueOrDefault(folder) ?? []).Where(e => e.IsDirectory).OrderBy(e => System.IO.Path.GetFileName(e.Path), s_names)
                .Select(e => new ExplorerNode("d:" + e.Path, ExplorerNodeKind.Folder, System.IO.Path.GetFileName(e.Path))
                {
                    Path = e.Path,
                    ProjectPath = Owner(e.Path),
                    Icon = "folder",
                    Children = NodesOf(e.Path),
                }),
            .. (children.GetValueOrDefault(folder) ?? []).Where(e => !e.IsDirectory).OrderBy(e => System.IO.Path.GetFileName(e.Path), s_names)
                .Select(e =>
                {
                    var name = System.IO.Path.GetFileName(e.Path);
                    var isProject = IsProjectFile(name);
                    return new ExplorerNode("f:" + e.Path, ExplorerNodeKind.File, name)
                    {
                        Path = e.Path,
                        ProjectPath = isProject && loaded.Contains(e.Path) ? e.Path : Owner(e.Path),
                        Icon = isProject ? "project" : FileIcon(name),
                        Detail = isProject && !loaded.Contains(e.Path) ? "(not loaded)" : null,
                    };
                }),
        ];

        var count = solution.Projects.Length;
        return new ExplorerNode("solution", ExplorerNodeKind.Solution, $"Folder '{solution.Name}'")
        {
            Path = solution.Path,
            Icon = "folder",
            Detail = count == 1 ? "(1 project loaded)" : $"({count} projects loaded)",
            Children = NodesOf(root),
        };
    }

    /// <summary>A project file (<c>.csproj</c>, <c>.fsproj</c>, <c>.vbproj</c>).</summary>
    public static bool IsProjectFile(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj";

    /// <summary>A file that opens as a solution: a solution (<c>.sln</c>, <c>.slnx</c>, <c>.slnf</c>) or a project.</summary>
    public static bool IsSolutionOrProjectFile(string path) =>
        IsProjectFile(path) || System.IO.Path.GetExtension(path).ToLowerInvariant() is ".sln" or ".slnx" or ".slnf" or ".sqlproj" or ".dcproj";

    public static ExplorerNode Project(SolutionProjectInfo entry, ProjectInfo? info, bool trusted, CompilerInputsInfo? inputs = null, IReadOnlyList<DirectoryEntry>? disk = null)
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

        if (info.State == ProjectStates.Unloaded)
        {
            return node with { Detail = "(unloaded)", IsUnloaded = true };
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

        var dependencies = Dependencies(entry.Path, info, inputs);
        return node with
        {
            Detail = info.TargetFrameworks.Length > 1 ? string.Join(", ", info.TargetFrameworks) : null,
            Children = [.. dependencies is null ? [] : new[] { dependencies }, .. Contents(entry.Path, info, disk)],
        };
    }

    // ------------------------------------------------------------------ files

    private sealed class FolderBuilder(string path, string name, bool excluded = false)
    {
        public string Path { get; } = path;

        public string Name { get; } = name;

        /// <summary>Only on disk (Show All Files): no item is in it.</summary>
        public bool Excluded { get; } = excluded;

        public Dictionary<string, FolderBuilder> Folders { get; } = new(s_names);

        public List<FileEntry> Files { get; } = [];

        /// <summary>F# projects: the compile order of its first compiled file (<see cref="int.MaxValue"/>: none).</summary>
        public int Order { get; set; } = int.MaxValue;
    }

    private sealed record FileEntry(string Name, string FullPath, string? DependentUpon, bool Linked, bool Excluded = false, int Order = int.MaxValue);

    /// <summary>Whether a project's items show in their compile order rather than by name (F#, M36).</summary>
    public static bool IsOrdered(string projectPath) => projectPath.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Folders and files of a project, with nesting applied. With <paramref name="disk"/> (Show All Files), files and
    /// folders on disk that aren't items are added, marked <see cref="ExplorerNode.IsExcluded"/>.
    /// </summary>
    public static IReadOnlyList<ExplorerNode> Contents(string projectPath, ProjectInfo info, IReadOnlyList<DirectoryEntry>? disk = null)
    {
        var projectDirectory = System.IO.Path.GetDirectoryName(projectPath)!;
        var root = new FolderBuilder(projectDirectory, string.Empty);
        var seen = new HashSet<string>(SolutionService.PathComparer);
        var ordered = IsOrdered(projectPath);
        var index = -1;
        foreach (var item in info.Items)
        {
            index++;
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
            // F#: compiled files keep the project's order (the compile order); folders sit where their first one does.
            var order = ordered && item.Kind.Equals("Compile", StringComparison.OrdinalIgnoreCase) ? index : int.MaxValue;
            for (var i = 0; i < folderSegments; i++)
            {
                if (!folder.Folders.TryGetValue(segments[i], out var child))
                {
                    child = new FolderBuilder(System.IO.Path.Combine(folder.Path, segments[i]), segments[i]);
                    folder.Folders[segments[i]] = child;
                }

                folder = child;
                folder.Order = Math.Min(folder.Order, order);
            }

            if (!isFolderItem && seen.Add(item.Path))
            {
                folder.Files.Add(new FileEntry(segments[^1], item.Path, item.DependentUpon, !inside || item.Link is not null, Order: order));
            }
        }

        foreach (var entry in disk ?? [])
        {
            if (SolutionService.PathComparer.Equals(entry.Path, projectPath) || !IsUnder(entry.Path, projectDirectory) || (!entry.IsDirectory && seen.Contains(entry.Path)))
            {
                continue;
            }

            var segments = System.IO.Path.GetRelativePath(projectDirectory, entry.Path).Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            var folder = root;
            for (var i = 0; i < (entry.IsDirectory ? segments.Length : segments.Length - 1); i++)
            {
                if (!folder.Folders.TryGetValue(segments[i], out var child))
                {
                    child = new FolderBuilder(System.IO.Path.Combine(folder.Path, segments[i]), segments[i], excluded: true);
                    folder.Folders[segments[i]] = child;
                }

                folder = child;
            }

            if (!entry.IsDirectory && seen.Add(entry.Path))
            {
                folder.Files.Add(new FileEntry(segments[^1], entry.Path, null, Linked: false, Excluded: true));
            }
        }

        return Build(root, projectPath);
    }

    private static List<ExplorerNode> Build(FolderBuilder folder, string projectPath)
    {
        var nodes = new List<(int Order, ExplorerNode Node)>();
        foreach (var child in folder.Folders.Values.OrderBy(f => f.Name, s_names))
        {
            nodes.Add((child.Order, new ExplorerNode("d:" + child.Path, ExplorerNodeKind.Folder, child.Name)
            {
                Path = child.Path,
                ProjectPath = projectPath,
                Icon = "folder",
                IsExcluded = child.Excluded,
                Children = Build(child, projectPath),
            }));
        }

        // Nesting: each file finds its parent among its siblings; files without a parent are roots.
        var names = folder.Files.Select(f => f.Name).ToHashSet(s_names);
        var parentOf = folder.Files.GroupBy(f => f.Name, s_names).ToDictionary(g => g.Key, g => NestParent(g.Key, names, g.First().DependentUpon), s_names);
        ExplorerNode File(FileEntry file) =>
            new("f:" + file.FullPath, ExplorerNodeKind.File, file.Name)
            {
                Path = file.FullPath,
                ProjectPath = projectPath,
                Icon = FileIcon(file.Name),
                IsLinked = file.Linked,
                IsExcluded = file.Excluded,
                Children = [.. folder.Files
                    .Where(f => s_names.Equals(parentOf[f.Name], file.Name))
                    .OrderBy(f => f.Name, s_names)
                    .Select(File)],
            };

        nodes.AddRange(folder.Files.Where(f => parentOf[f.Name] is null).OrderBy(f => f.Name, s_names).Select(f => (f.Order, File(f))));

        // By name, folders first; in an F# project, compiled files and the folders holding them first, in compile order.
        // (OrderBy is stable, so the rest keep their order by name.)
        return [.. nodes.OrderBy(n => n.Order).ThenBy(n => n.Order == int.MaxValue && n.Node.Kind != ExplorerNodeKind.Folder ? 1 : 0).Select(n => n.Node)];
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

        // An F# signature file nests under its implementation (Shapes.fsi → Shapes.fs).
        var withoutLast = System.IO.Path.GetFileNameWithoutExtension(name);
        if (System.IO.Path.GetExtension(name).Equals(".fsi", StringComparison.OrdinalIgnoreCase) && siblings.Contains(withoutLast + ".fs"))
        {
            return withoutLast + ".fs";
        }

        // Added extension: strip the last extension.
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
        ".cs" or ".fs" or ".fsi" or ".fsx" or ".vb" or ".razor" or ".xaml" or ".axaml" or ".cshtml" or ".ts" or ".js" => "file-code",
        ".json" or ".xml" or ".resx" or ".config" or ".props" or ".targets" or ".yml" or ".yaml" => "file-data",
        _ => "file",
    };

    /// <summary>The <c>/analyzer:</c> arguments of a compiler command line (paths may be quoted, several per argument).</summary>
    public static IReadOnlyList<string> AnalyzerPaths(IEnumerable<string> arguments) =>
        [.. arguments
            .Where(a => a.StartsWith("/analyzer:", StringComparison.OrdinalIgnoreCase) || a.StartsWith("-analyzer:", StringComparison.OrdinalIgnoreCase))
            .SelectMany(a => a["/analyzer:".Length..].Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
            .Select(a => a.Trim('"'))
            .Distinct(SolutionService.PathComparer)];

    private static bool IsUnder(string path, string directory) =>
        path.StartsWith(System.IO.Path.TrimEndingDirectorySeparator(directory) + System.IO.Path.DirectorySeparatorChar,
            SolutionService.PathComparer == StringComparer.OrdinalIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // ------------------------------------------------------------------ dependencies

    /// <summary>
    /// The Dependencies node. Analyzers come from the design-time build of the active target framework.
    /// TODO(issue): transitive packages (from the assets file).
    /// </summary>
    private static ExplorerNode? Dependencies(string projectPath, ProjectInfo info, CompilerInputsInfo? inputs)
    {
        if (info.References.Length == 0 && info.TargetFrameworks.Length == 0)
        {
            return null;
        }

        var id = "deps:" + projectPath;
        var analyzers = inputs is { State: CompilerInputStates.Ok } ? AnalyzerPaths(inputs.Arguments) : [];
        IReadOnlyList<ExplorerNode> Groups(string prefix, IEnumerable<ReferenceInfo> references, bool withAnalyzers)
        {
            var list = references.ToList();
            if (withAnalyzers)
            {
                list.AddRange(analyzers.Select(a => new ReferenceInfo(ReferenceKinds.Analyzer, System.IO.Path.GetFileNameWithoutExtension(a), null, a, inputs!.TargetFramework, File.Exists(a))));
            }

            var groups = new List<ExplorerNode>();
            foreach (var (kind, title, icon) in new[]
                     {
                         (ReferenceKinds.Framework, "Frameworks", "layout"),
                         (ReferenceKinds.Package, "Packages", "package"),
                         (ReferenceKinds.Project, "Projects", "project"),
                         (ReferenceKinds.Assembly, "Assemblies", "link"),
                         (ReferenceKinds.Analyzer, "Analyzers", "file-code"),
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
                Children = Groups($"{id}|{tfm}", info.References.Where(r => r.TargetFramework == tfm), string.Equals(inputs?.TargetFramework, tfm, StringComparison.OrdinalIgnoreCase)),
            })]
            : Groups(id, info.References, withAnalyzers: true);
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
