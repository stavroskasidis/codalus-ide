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

    /// <summary>A type or member declared in a code file (under the file, from its language service's symbols).</summary>
    Member,

    /// <summary>A file a source generator made (under its analyzer); <see cref="ExplorerNode.Path"/> is its <c>codalus-generated</c> URI.</summary>
    GeneratedFile,
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

    /// <summary>For <see cref="ExplorerNodeKind.Member"/>: where it is declared (zero-based line and character).</summary>
    public (int Line, int Character)? Position { get; init; }
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
/// The projects' subtrees of a <see cref="SolutionTree"/> build, for the next one: a project's subtree is reused while its
/// solution entry is equal and its evaluation, compiler inputs and files on disk are the same instances (the solution
/// service replaces a project's only when it changes). A solution's projects evaluate one at a time, each a rebuild of
/// the tree, and building every project's folders, files and dependencies again for each cost a 300-project solution's
/// open 4.2 s of the UI thread. The entries are compared by value: the build host sends the whole solution again for
/// every change of the solution file, each entry a new instance (M56). Not thread-safe: one builder at a time.
/// </summary>
public sealed class SolutionTreeCache
{
    private Dictionary<string, Entry> _projects = new(SolutionService.PathComparer);
    private Dictionary<string, Entry> _built = new(SolutionService.PathComparer);

    private sealed record Entry(SolutionProjectInfo Project, ProjectInfo? Info, bool Trusted, CompilerInputsInfo? Inputs, IReadOnlyList<DirectoryEntry>? Disk, ExplorerNode Node);

    /// <summary>The projects whose subtrees the last build reused (tests).</summary>
    public int Reused { get; private set; }

    internal void Begin()
    {
        _built = new Dictionary<string, Entry>(SolutionService.PathComparer);
        Reused = 0;
    }

    internal ExplorerNode Project(SolutionProjectInfo project, ProjectInfo? info, bool trusted, CompilerInputsInfo? inputs, IReadOnlyList<DirectoryEntry>? disk)
    {
        if (_projects.TryGetValue(project.Path, out var cached) && cached.Project == project && ReferenceEquals(cached.Info, info)
            && cached.Trusted == trusted && ReferenceEquals(cached.Inputs, inputs) && ReferenceEquals(cached.Disk, disk))
        {
            Reused++;
            _built[project.Path] = cached;
            return cached.Node;
        }

        var entry = new Entry(project, info, trusted, inputs, disk, SolutionTree.Project(project, info, trusted, inputs, disk));
        _built[project.Path] = entry;
        return entry.Node;
    }

    /// <summary>Keeps the projects of this build only (a removed project's subtree goes with it).</summary>
    internal void End() => _projects = _built;
}

/// <summary>
/// Derives the Solution Explorer tree from the build host's model (ADR 0004: presentation lives in the shell):
/// solution folders and items, projects with their folders and files, file nesting, linked files, and a
/// Dependencies node grouped per target framework when a project multi-targets.
/// </summary>
public static class SolutionTree
{
    private static readonly StringComparer s_names = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// The order of names in the tree, as the classic IDE's: case-insensitive with letters compared in lower case, so
    /// punctuation such as <c>_</c> comes before them (<c>_Imports.razor</c> before <c>App.razor</c>; an ordinal
    /// case-insensitive comparison upper-cases, which puts it after).
    /// </summary>
    internal static readonly StringComparer Order = new NameOrder();

    private sealed class NameOrder : StringComparer
    {
        public override int Compare(string? x, string? y)
        {
            if (x is null || y is null)
            {
                return x is null ? (y is null ? 0 : -1) : 1;
            }

            for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
            {
                var difference = char.ToLowerInvariant(x[i]).CompareTo(char.ToLowerInvariant(y[i]));
                if (difference != 0)
                {
                    return difference;
                }
            }

            return x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y);
        }

        public override bool Equals(string? x, string? y) => Compare(x, y) == 0;

        public override int GetHashCode(string obj) => obj.GetHashCode(StringComparison.Ordinal);
    }

    /// <summary>A project's folders that the classic IDE lists before the others, in this order.</summary>
    private static readonly string[] s_projectFoldersFirst = ["Properties", "My Project", "wwwroot"];

    /// <param name="cache">The projects' subtrees of the last build, reused for the projects whose inputs didn't change.</param>
    public static ExplorerNode Build(
        SolutionInfo solution,
        IReadOnlyDictionary<string, ProjectInfo> projects,
        IReadOnlyDictionary<string, CompilerInputsInfo>? compilerInputs = null,
        ExplorerOptions? options = null,
        SolutionTreeCache? cache = null)
    {
        if (solution.IsFolder && options?.FolderEntries is { } entries)
        {
            return FolderView(solution, entries);
        }

        cache?.Begin();
        var folders = solution.Folders.ToDictionary(f => f.Id, StringComparer.Ordinal);
        IReadOnlyList<ExplorerNode> ChildrenOf(string? parent)
        {
            var nodes = new List<ExplorerNode>();
            foreach (var folder in solution.Folders.Where(f => f.Parent == parent).OrderBy(f => f.Name, Order))
            {
                nodes.Add(new ExplorerNode("sf:" + folder.Id, ExplorerNodeKind.SolutionFolder, folder.Name)
                {
                    SolutionFolderId = folder.Id,
                    Icon = "folder",
                    Children =
                    [
                        .. ChildrenOf(folder.Id),
                        .. folder.Files.OrderBy(System.IO.Path.GetFileName, Order).Select(file =>
                            new ExplorerNode("si:" + file, ExplorerNodeKind.SolutionItem, System.IO.Path.GetFileName(file)) { Path = file, Icon = "file" }),
                    ],
                });
            }

            foreach (var project in solution.Projects.Where(p => p.Parent == parent && (p.Parent is null || folders.ContainsKey(p.Parent))).OrderBy(p => p.Name, Order))
            {
                var info = projects.GetValueOrDefault(project.Path);
                var inputs = compilerInputs?.GetValueOrDefault(project.Path);
                var disk = options?.DiskEntries?.GetValueOrDefault(project.Path);
                nodes.Add(cache is null ? Project(project, info, solution.Trusted, inputs, disk) : cache.Project(project, info, solution.Trusted, inputs, disk));
            }

            return nodes;
        }

        var count = solution.Projects.Length;
        var root = new ExplorerNode("solution", ExplorerNodeKind.Solution, solution.IsFolder ? $"Folder '{solution.Name}'" : $"Solution '{solution.Name}'")
        {
            Path = solution.Path,
            Icon = solution.IsFolder ? "folder" : "solution",
            Detail = solution.IsFolder ? (count == 1 ? "(1 project loaded)" : $"({count} projects loaded)") : count == 1 ? "(1 project)" : $"({count} projects)",
            Children = ChildrenOf(null),
        };
        cache?.End();
        return root;
    }

    /// <summary>
    /// Folder mode's folder view (ADR 0033): the folder's files and folders as they are on disk, project files marked
    /// (with "(not loaded)" until they are loaded). Nodes inside a loaded project know it.
    /// </summary>
    public static ExplorerNode FolderView(SolutionInfo solution, IReadOnlyList<DirectoryEntry> entries)
    {
        var root = System.IO.Path.TrimEndingDirectorySeparator(solution.Directory);
        var loaded = solution.Projects.Select(p => p.Path).ToHashSet(SolutionService.PathComparer);

        // A node's project is, of the projects whose folder holds it, the one with the longest path (the first of those):
        // ranked once, and carried down the folders. Each node looked for it among every project, allocating for each: a
        // 300-project folder's 20,000 entries made 6 million comparisons, 2 s of the UI thread (M56).
        var byFolder = new Dictionary<string, (string Path, int Rank)>(SolutionService.PathComparer);
        foreach (var (path, rank) in solution.Projects.Select((p, i) => (p.Path, i)).OrderByDescending(p => p.Path.Length).ThenBy(p => p.i).Select((p, rank) => (p.Path, rank)))
        {
            byFolder.TryAdd(System.IO.Path.GetDirectoryName(path)!, (path, rank));
        }

        (string Path, int Rank)? Inside(string folder, (string Path, int Rank)? outer) =>
            byFolder.TryGetValue(folder, out var own) && (outer is null || own.Rank < outer.Value.Rank) ? own : outer;
        (string Path, int Rank)? rootOwner = null;
        for (var folder = root; !string.IsNullOrEmpty(folder); folder = System.IO.Path.GetDirectoryName(folder))
        {
            rootOwner = Inside(folder, rootOwner);
        }

        var children = new Dictionary<string, List<DirectoryEntry>>(SolutionService.PathComparer);
        var below = Below(root);
        foreach (var entry in entries.Where(e => IsBelow(e.Path, below)))
        {
            var parent = System.IO.Path.GetDirectoryName(entry.Path)!;
            if (!children.TryGetValue(parent, out var list))
            {
                children[parent] = list = [];
            }

            list.Add(entry);
        }

        // owner: the project of what is in the folder.
        IReadOnlyList<ExplorerNode> NodesOf(string folder, (string Path, int Rank)? owner) =>
        [
            .. (children.GetValueOrDefault(folder) ?? []).Where(e => e.IsDirectory).OrderBy(e => System.IO.Path.GetFileName(e.Path), Order)
                .Select(e =>
                {
                    var inside = Inside(e.Path, owner);
                    return new ExplorerNode("d:" + e.Path, ExplorerNodeKind.Folder, System.IO.Path.GetFileName(e.Path))
                    {
                        Path = e.Path,
                        ProjectPath = inside?.Path,
                        Icon = "folder",
                        Children = NodesOf(e.Path, inside),
                    };
                }),
            .. (children.GetValueOrDefault(folder) ?? []).Where(e => !e.IsDirectory).OrderBy(e => System.IO.Path.GetFileName(e.Path), Order)
                .Select(e =>
                {
                    var name = System.IO.Path.GetFileName(e.Path);
                    var isProject = IsProjectFile(name);
                    return new ExplorerNode("f:" + e.Path, ExplorerNodeKind.File, name)
                    {
                        Path = e.Path,
                        ProjectPath = isProject && loaded.Contains(e.Path) ? e.Path : owner?.Path,
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
            Children = NodesOf(root, rootOwner),
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

        // A non-SDK-style project (M43a) says so; one that needs the classic IDE's targets says why it doesn't build.
        ExplorerNode[] cannotBuild = info.CannotBuildReason is { } reason
            ? [new ExplorerNode(node.Id + "|cannotBuild", ExplorerNodeKind.Message, reason) { Icon = "warning", IsWarning = true, ProjectPath = entry.Path }]
            : [];
        return node with
        {
            Detail = info.CannotBuildReason is not null ? Localization.Localizer.Get("msg.solutionTree.nonSdkStyleCannotBuild", "(non-SDK-style, can't build here)")
                : info.IsNonSdkStyle ? Localization.Localizer.Get("msg.solutionTree.nonSdkStyle", "(non-SDK-style)")
                : info.TargetFrameworks.Length > 1 ? string.Join(", ", info.TargetFrameworks) : null,
            IsWarning = info.CannotBuildReason is not null,
            Children = [.. cannotBuild, .. dependencies is null ? [] : new[] { dependencies }, .. Contents(entry.Path, info, disk)],
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
    /// The folders of a project's tree that hold its items, with the project's own folder: Show All Files lists what is
    /// on disk in these, and in a folder that isn't in the project only once it's expanded, as the classic IDE does (M56).
    /// </summary>
    public static IReadOnlySet<string> ItemFolders(string projectPath, ProjectInfo info)
    {
        var projectDirectory = System.IO.Path.GetDirectoryName(projectPath)!;
        var below = Below(projectDirectory);
        var folders = new HashSet<string>(SolutionService.PathComparer) { projectDirectory };
        foreach (var item in info.Items)
        {
            if (SolutionService.PathComparer.Equals(item.Path, projectPath))
            {
                continue;
            }

            var segments = DisplaySegments(item, projectDirectory, IsBelow(item.Path, below));
            var folder = projectDirectory;
            for (var i = 0; i < (IsFolderItem(item) ? segments.Length : segments.Length - 1); i++)
            {
                folder = System.IO.Path.Combine(folder, segments[i]);
                folders.Add(folder);
            }
        }

        return folders;
    }

    /// <summary>Where an item shows under its project, folder by folder: its link, its path in the project's folder, or (outside it) its name.</summary>
    private static string[] DisplaySegments(ProjectItemInfo item, string projectDirectory, bool inside) =>
        (item.Link ?? (inside ? System.IO.Path.GetRelativePath(projectDirectory, item.Path) : System.IO.Path.GetFileName(item.Path)))
        .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

    private static bool IsFolderItem(ProjectItemInfo item) => item.Kind.Equals("Folder", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Folders and files of a project, with nesting applied. With <paramref name="disk"/> (Show All Files), files and
    /// folders on disk that aren't items are added, marked <see cref="ExplorerNode.IsExcluded"/>.
    /// </summary>
    public static IReadOnlyList<ExplorerNode> Contents(string projectPath, ProjectInfo info, IReadOnlyList<DirectoryEntry>? disk = null)
    {
        var projectDirectory = System.IO.Path.GetDirectoryName(projectPath)!;
        var below = Below(projectDirectory);
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

            var inside = IsBelow(item.Path, below);
            var segments = DisplaySegments(item, projectDirectory, inside);
            if (segments.Length == 0)
            {
                continue;
            }

            var folder = root;
            var isFolderItem = IsFolderItem(item);
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
            if (SolutionService.PathComparer.Equals(entry.Path, projectPath) || !IsBelow(entry.Path, below) || (!entry.IsDirectory && seen.Contains(entry.Path)))
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
        // At the project's root, Properties (My Project) and wwwroot come first, then the other folders by name.
        var atRoot = folder.Name.Length == 0;
        int First(FolderBuilder f) => atRoot && Array.FindIndex(s_projectFoldersFirst, n => s_names.Equals(n, f.Name)) is >= 0 and var at ? at : s_projectFoldersFirst.Length;
        foreach (var child in folder.Folders.Values.OrderBy(First).ThenBy(f => f.Name, Order))
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

        // Nesting: each file finds its parent among its siblings; files without a parent are roots. The files nested under
        // each are grouped once: each file looked for them among all its siblings, F² lookups for a folder of F files (a
        // folder of 20,000 files took 16 s, M56).
        var names = folder.Files.Select(f => f.Name).ToHashSet(s_names);
        var parentOf = folder.Files.GroupBy(f => f.Name, s_names).ToDictionary(g => g.Key, g => NestParent(g.Key, names, g.First().DependentUpon), s_names);
        var nested = folder.Files.Where(f => parentOf[f.Name] is not null)
            .GroupBy(f => parentOf[f.Name]!, s_names)
            .ToDictionary(g => g.Key, g => g.OrderBy(f => f.Name, Order).ToList(), s_names);
        ExplorerNode File(FileEntry file) =>
            new("f:" + file.FullPath, ExplorerNodeKind.File, file.Name)
            {
                Path = file.FullPath,
                ProjectPath = projectPath,
                Icon = FileIcon(file.Name),
                IsLinked = file.Linked,
                IsExcluded = file.Excluded,
                Children = nested.TryGetValue(file.Name, out var children) ? [.. children.Select(File)] : [],
            };

        nodes.AddRange(folder.Files.Where(f => parentOf[f.Name] is null).OrderBy(f => f.Name, Order).Select(f => (f.Order, File(f))));

        // By name, folders first; in an F# project, compiled files and the folders holding them first, in compile order.
        // (OrderBy is stable, so the rest keep their order by name.)
        return [.. nodes.OrderBy(n => n.Order).ThenBy(n => n.Order == int.MaxValue && n.Node.Kind != ExplorerNodeKind.Folder ? 1 : 0).Select(n => n.Node)];
    }

    /// <summary>
    /// The sibling a file nests under, or null. <c>DependentUpon</c> wins; then the rules the classic IDE applies by
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

        // Windows Forms and resources (M40a): a form's Form1.resx under Form1.cs, and a strongly typed resource class
        // (Resources.Designer.cs) or settings class under the .resx or .settings it comes from.
        if (extension.Equals(".resx", StringComparison.OrdinalIgnoreCase) && !System.IO.Path.HasExtension(withoutLast))
        {
            foreach (var code in new[] { withoutLast + ".cs", withoutLast + ".vb" })
            {
                if (siblings.Contains(code))
                {
                    return code;
                }
            }
        }

        if (System.IO.Path.GetExtension(withoutLast).Equals(".Designer", StringComparison.OrdinalIgnoreCase) && segmentBase.Length > 0)
        {
            foreach (var source in new[] { segmentBase + ".resx", segmentBase + ".settings" })
            {
                if (siblings.Contains(source))
                {
                    return source;
                }
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
        [.. CompilerInputsInfo.AnalyzerPaths(arguments).Distinct(SolutionService.PathComparer)];

    /// <summary>A folder's path with a separator at its end, for <see cref="IsBelow"/> (made once for a loop).</summary>
    private static string Below(string directory) => System.IO.Path.TrimEndingDirectorySeparator(directory) + System.IO.Path.DirectorySeparatorChar;

    /// <summary>Whether <paramref name="path"/> is under the folder of <paramref name="below"/> (<see cref="Below"/>).</summary>
    private static bool IsBelow(string path, string below) =>
        path.StartsWith(below, SolutionService.PathComparer == StringComparer.OrdinalIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // ------------------------------------------------------------------ dependencies

    /// <summary>The most nodes of transitive packages a project's Dependencies node holds (a package graph can be large).</summary>
    public const int TransitiveLimit = 2000;

    /// <summary>
    /// The Dependencies node. Analyzers come from the design-time build of the active target framework. A package shows
    /// the packages it depends on under it, and theirs under them (from the assets file, <see cref="ProjectInfo.Packages"/>),
    /// up to <see cref="TransitiveLimit"/> nodes for the project.
    /// </summary>
    private static ExplorerNode? Dependencies(string projectPath, ProjectInfo info, CompilerInputsInfo? inputs)
    {
        if (info.References.Length == 0 && info.TargetFrameworks.Length == 0)
        {
            return null;
        }

        var id = "deps:" + projectPath;
        var analyzers = inputs is { State: CompilerInputStates.Ok } ? AnalyzerPaths(inputs.Arguments) : [];

        // Which analyzers are missing comes with the inputs from the build host, which owns the disk: a File.Exists for each
        // here cost a 300-project solution 6,000 checks on the UI thread for each build of the tree (M56).
        var missing = new HashSet<string>(inputs?.MissingAnalyzers ?? [], SolutionService.PathComparer);
        var graph = info.Packages
            .GroupBy(p => (p.TargetFramework, p.Name.ToLowerInvariant()))
            .ToDictionary(g => g.Key, g => g.First());
        var budget = TransitiveLimit;
        IReadOnlyList<ExplorerNode> Transitive(string parentId, string framework, string package, PackageChain ancestors)
        {
            if (!graph.TryGetValue((framework, package.ToLowerInvariant()), out var node) || node.Dependencies.Length == 0)
            {
                return [];
            }

            var children = new List<ExplorerNode>();
            foreach (var dependency in node.Dependencies.Order(s_names))
            {
                if (ancestors.Contains(dependency))
                {
                    continue; // a cycle
                }

                if (budget-- <= 0)
                {
                    break;
                }

                var version = graph.TryGetValue((framework, dependency.ToLowerInvariant()), out var resolved) ? resolved.Version : null;
                var childId = parentId + "|" + dependency;
                children.Add(new ExplorerNode(childId, ExplorerNodeKind.Dependency, dependency)
                {
                    ProjectPath = projectPath,
                    Icon = "package",
                    Detail = version is null ? null : $"({version})",
                    Children = Transitive(childId, framework, dependency, ancestors.With(dependency)),
                });
            }

            return children;
        }

        IReadOnlyList<ExplorerNode> Groups(string prefix, IEnumerable<ReferenceInfo> references, bool withAnalyzers)
        {
            var list = references.ToList();
            if (withAnalyzers)
            {
                list.AddRange(analyzers.Select(a => new ReferenceInfo(ReferenceKinds.Analyzer, System.IO.Path.GetFileNameWithoutExtension(a), null, a, inputs!.TargetFramework, !missing.Contains(a))));
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
                        Children = kind == ReferenceKinds.Package ? Transitive($"{prefix}|{kind}|{m.Name}", m.TargetFramework, m.Name, PackageChain.Of(m.Name)) : [],
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

/// <summary>The packages on the way to a transitive package (a cycle guard): small, so a list.</summary>
internal readonly record struct PackageChain(IReadOnlyList<string> Items)
{
    public static PackageChain Of(string item) => new([item]);

    public bool Contains(string item) => Items.Contains(item, StringComparer.OrdinalIgnoreCase);

    public PackageChain With(string item) => new([.. Items, item]);
}
