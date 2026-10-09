using DaedalusStudio.Projects.Protocol;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;

namespace DaedalusStudio.Projects.Host.Packages;

/// <summary>
/// A project's package references: read from its evaluation (each target framework) and its assets file, and edited
/// through MSBuild's construction model with the calls <c>dotnet add package</c> and <c>dotnet remove package</c> make,
/// so the files come out the same (ADR 0012).
/// </summary>
internal static class PackageReferences
{
    private const string PackageReference = "PackageReference";
    private const string PackageVersion = "PackageVersion";

    private static readonly StringComparison s_pathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Loads the project for each target framework with <paramref name="globalProperties"/>.</summary>
    public sealed class Evaluation : IDisposable
    {
        private readonly ProjectCollection _collection;

        public Evaluation(string path, IReadOnlyDictionary<string, string> globalProperties)
        {
            _collection = new ProjectCollection(new Dictionary<string, string>(globalProperties, StringComparer.OrdinalIgnoreCase));
            Outer = _collection.LoadProject(path);
            Frameworks = ProjectEvaluator.TargetFrameworks(Outer);
            Inner = Frameworks.Length <= 1
                ? [(Frameworks.FirstOrDefault() ?? string.Empty, Outer)]
                : [.. Frameworks.Select(f => (f, _collection.LoadProject(path, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["TargetFramework"] = f }, null)))];
        }

        public Project Outer { get; }

        public string[] Frameworks { get; }

        /// <summary>The evaluation of each target framework (the outer one for single-targeted projects).</summary>
        public (string Framework, Project Project)[] Inner { get; }

        public bool CentralPackageManagement => IsTrue(Outer.GetPropertyValue("ManagePackageVersionsCentrally"));

        public string? CentralPackagesFile =>
            Outer.GetPropertyValue("DirectoryPackagesPropsPath") is { Length: > 0 } path && File.Exists(path) ? Path.GetFullPath(path) : null;

        public void Dispose() => _collection.Dispose();
    }

    // ------------------------------------------------------------------ reading

    public static ProjectPackages Read(Evaluation evaluation)
    {
        var path = evaluation.Outer.FullPath;
        var graph = PackageGraph.TryLoad(evaluation.Outer.GetPropertyValue("ProjectAssetsFile"));
        var direct = new Dictionary<string, (InstalledPackage Package, List<string> Frameworks)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (framework, project) in evaluation.Inner)
        {
            var globals = project.GetItems("GlobalPackageReference").Select(i => i.EvaluatedInclude).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var central = project.GetItems(PackageVersion)
                .GroupBy(i => i.EvaluatedInclude, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last().GetMetadataValue("Version"), StringComparer.OrdinalIgnoreCase);
            var resolved = graph?.Framework(framework);
            foreach (var item in project.GetItems(PackageReference))
            {
                var id = item.EvaluatedInclude;
                if (direct.TryGetValue(id, out var existing))
                {
                    existing.Frameworks.Add(framework);
                    continue;
                }

                var versionOverride = NullIfEmpty(item.GetMetadataValue("VersionOverride"));
                var centralVersion = central.GetValueOrDefault(id);
                var kind = globals.Contains(id) ? InstalledPackageKinds.Global
                    : IsTrue(item.GetMetadataValue("IsImplicitlyDefined")) ? InstalledPackageKinds.Implicit
                    : InstalledPackageKinds.Direct;
                var package = new InstalledPackage(
                    id,
                    versionOverride ?? NullIfEmpty(item.GetMetadataValue("Version")) ?? NullIfEmpty(centralVersion),
                    resolved?.Libraries.GetValueOrDefault(id)?.Version,
                    [],
                    kind,
                    versionOverride,
                    NullIfEmpty(centralVersion),
                    item.Xml?.ContainingProject?.FullPath);
                direct[id] = (package, [framework]);
            }
        }

        var transitive = new Dictionary<string, (string Version, List<string> Frameworks)>(StringComparer.OrdinalIgnoreCase);
        foreach (var framework in graph?.Frameworks ?? [])
        {
            foreach (var library in framework.Libraries.Values.Where(l => l.Type == "package" && !direct.ContainsKey(l.Id)))
            {
                if (transitive.TryGetValue(library.Id, out var existing))
                {
                    existing.Frameworks.Add(framework.Name);
                }
                else
                {
                    transitive[library.Id] = (library.Version, [framework.Name]);
                }
            }
        }

        return new ProjectPackages(
            path,
            Path.GetFileNameWithoutExtension(path),
            evaluation.Frameworks,
            evaluation.CentralPackageManagement,
            evaluation.CentralPackageManagement ? evaluation.CentralPackagesFile : null,
            graph is not null,
            [.. direct.Values.OrderBy(d => d.Package.Id, StringComparer.OrdinalIgnoreCase).Select(d => d.Package with { Frameworks = [.. d.Frameworks.Distinct(StringComparer.OrdinalIgnoreCase)] })],
            [.. transitive.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase).Select(t => new TransitivePackage(t.Key, t.Value.Version, [.. t.Value.Frameworks]))]);
    }

    public static PackageWhyResult Why(Evaluation evaluation, string id)
    {
        var graph = PackageGraph.TryLoad(evaluation.Outer.GetPropertyValue("ProjectAssetsFile"))
                    ?? throw new InvalidOperationException($"{Path.GetFileName(evaluation.Outer.FullPath)} has not been restored.");
        var paths = new List<PackageWhyPath>();
        foreach (var framework in graph.Frameworks)
        {
            paths.AddRange(PackageGraph.Paths(framework, id).Select(chain => new PackageWhyPath(framework.Name, chain)));
        }

        return new PackageWhyResult([.. paths]);
    }

    // ------------------------------------------------------------------ editing

    /// <summary>Adds or updates the reference; returns the files that changed.</summary>
    public static IReadOnlyList<string> Install(Evaluation evaluation, string id, string version, string? framework, bool versionOverride)
    {
        var projectPath = evaluation.Outer.FullPath;
        var changed = new List<string>();
        using var files = new Files();
        if (evaluation.CentralPackageManagement)
        {
            var project = files.Open(projectPath);
            var existing = ReferencesTo(project, id).ToList();
            var overriding = versionOverride || existing.Any(e => e.Metadata.Any(m => m.Name == "VersionOverride"));
            if (existing.Count == 0)
            {
                var item = Group(project, PackageReference, framework).AddItem(PackageReference, id);
                if (overriding)
                {
                    item.AddMetadata("VersionOverride", version, expressAsAttribute: true);
                }
            }
            else if (overriding)
            {
                foreach (var item in existing)
                {
                    SetMetadata(item, "VersionOverride", version);
                }
            }

            if (!overriding)
            {
                var propsPath = evaluation.CentralPackagesFile
                                ?? throw new InvalidOperationException($"{Path.GetFileName(projectPath)} manages package versions centrally, but its Directory.Packages.props was not found.");
                var props = files.Open(propsPath);
                var central = props.Items.Where(i => i.ItemType == PackageVersion && i.Include.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
                if (central.Count == 0)
                {
                    Group(props, PackageVersion, null).AddItem(PackageVersion, id).AddMetadata("Version", version, expressAsAttribute: true);
                }
                else
                {
                    foreach (var item in central)
                    {
                        SetMetadata(item, "Version", version);
                    }
                }
            }
        }
        else
        {
            // A reference declared in an imported file of the repository (Directory.Build.props…) is updated there.
            var imported = evaluation.Inner
                .SelectMany(p => p.Project.GetItems(PackageReference))
                .Where(i => i.EvaluatedInclude.Equals(id, StringComparison.OrdinalIgnoreCase) && !IsTrue(i.GetMetadataValue("IsImplicitlyDefined")))
                .Select(i => i.Xml)
                .Where(x => x?.ContainingProject?.FullPath is { } file && !file.Equals(projectPath, s_pathComparison) && IsUserFile(evaluation.Outer, file))
                .Select(x => x.ContainingProject.FullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var target = imported.Count > 0 && framework is null ? imported[0] : projectPath;
            var xml = files.Open(target);
            var existing = ReferencesTo(xml, id).Where(i => framework is null || i.Parent.Condition == FrameworkCondition(framework) || i.Parent.Condition.Length == 0).ToList();
            if (existing.Count == 0)
            {
                Group(xml, PackageReference, framework).AddItem(PackageReference, id).AddMetadata("Version", version, expressAsAttribute: true);
            }
            else
            {
                foreach (var item in existing)
                {
                    SetMetadata(item, item.Metadata.Any(m => m.Name == "VersionOverride") ? "VersionOverride" : "Version", version);
                }
            }
        }

        changed.AddRange(files.SaveAll());
        return changed;
    }

    /// <summary>
    /// Removes the project's references to the package. Like <c>dotnet remove package</c>, a central <c>PackageVersion</c>
    /// stays (other projects may use it).
    /// </summary>
    public static IReadOnlyList<string> Uninstall(Evaluation evaluation, string id)
    {
        var projectPath = evaluation.Outer.FullPath;
        using var files = new Files();
        var project = files.Open(projectPath);
        var items = ReferencesTo(project, id).ToList();
        if (items.Count == 0)
        {
            var declared = evaluation.Inner.SelectMany(p => p.Project.GetItems(PackageReference))
                .FirstOrDefault(i => i.EvaluatedInclude.Equals(id, StringComparison.OrdinalIgnoreCase));
            throw new InvalidOperationException(declared?.Xml?.ContainingProject?.FullPath is { } file && !file.Equals(projectPath, s_pathComparison)
                ? $"{id} is referenced in {Path.GetFileName(file)}, not in {Path.GetFileName(projectPath)}: remove it there ({file})."
                : $"{Path.GetFileName(projectPath)} does not reference {id}.");
        }

        foreach (var item in items)
        {
            var group = item.Parent;
            group.RemoveChild(item);
            if (group.Count == 0)
            {
                group.Parent.RemoveChild(group);
            }
        }

        return files.SaveAll();
    }

    private static IEnumerable<ProjectItemElement> ReferencesTo(ProjectRootElement xml, string id) =>
        xml.Items.Where(i => i.ItemType == PackageReference && i.Include.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The item group a new item goes into, as the CLI picks it: the first (matching-condition) group that already has
    /// items of the type, else a new group after the last one.
    /// </summary>
    private static ProjectItemGroupElement Group(ProjectRootElement xml, string itemType, string? framework)
    {
        var condition = framework is null ? string.Empty : FrameworkCondition(framework);
        var group = xml.ItemGroups.FirstOrDefault(g => g.Condition == condition && g.Items.Any(i => i.ItemType == itemType));
        if (group is null)
        {
            group = xml.AddItemGroup();
            if (condition.Length > 0)
            {
                group.Condition = condition;
            }
        }

        return group;
    }

    private static string FrameworkCondition(string framework) => $"'$(TargetFramework)' == '{framework}'";

    private static void SetMetadata(ProjectItemElement item, string name, string value)
    {
        if (item.Metadata.FirstOrDefault(m => m.Name == name) is { } metadata)
        {
            metadata.Value = value;
        }
        else
        {
            item.AddMetadata(name, value, expressAsAttribute: true);
        }
    }

    /// <summary>Files the user owns: not in the SDK, MSBuild's folders or the NuGet package folders.</summary>
    internal static bool IsUserFile(Project project, string file)
    {
        foreach (var property in new[] { "NuGetPackageRoot", "MSBuildExtensionsPath", "MSBuildBinPath", "NetCoreRoot", "MSBuildSDKsPath" })
        {
            if (project.GetPropertyValue(property) is { Length: > 0 } folder
                && ProjectFileEditor.IsSameOrUnder(file, Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder))))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTrue(string? value) => string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>The project files an edit touches, opened from disk with their formatting, and saved together.</summary>
    private sealed class Files : IDisposable
    {
        private readonly ProjectCollection _collection = new();
        private readonly Dictionary<string, ProjectRootElement> _open = new(StringComparer.OrdinalIgnoreCase);

        public ProjectRootElement Open(string path)
        {
            if (!_open.TryGetValue(path, out var xml))
            {
                // A new collection per edit, so this reads the file as it is on disk now.
                xml = ProjectRootElement.Open(path, _collection, preserveFormatting: true);
                _open[path] = xml;
            }

            return xml;
        }

        public List<string> SaveAll()
        {
            var saved = new List<string>();
            foreach (var (path, xml) in _open)
            {
                if (xml.HasUnsavedChanges)
                {
                    xml.SaveFile();
                    saved.Add(path);
                }
            }

            return saved;
        }

        public void Dispose() => _collection.Dispose();
    }
}
