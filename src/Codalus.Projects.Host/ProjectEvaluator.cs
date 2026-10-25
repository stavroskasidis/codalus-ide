using Codalus.Projects.Protocol;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Exceptions;

namespace Codalus.Projects.Host;

/// <summary>A project's evaluation result plus what the watcher needs (imported files).</summary>
internal sealed record EvaluatedProject(ProjectInfo Info, IReadOnlyList<string> Imports, string? AssetsFile = null)
{
    /// <summary>The assets file's last write when the project was evaluated (<see cref="DateTime.MinValue"/>: none).</summary>
    public DateTime AssetsStamp { get; init; }

    /// <summary>
    /// A hash of everything evaluation read (M34b): the project file and its imports with their timestamps, every item,
    /// the assets file, the SDK and the global properties. With the referenced projects' fingerprints and the target
    /// framework it keys the design-time build cache (<see cref="DesignTimeCache"/>).
    /// </summary>
    public string? Fingerprint { get; init; }

    /// <summary>The project's <c>obj</c> folder (<c>BaseIntermediateOutputPath</c>).</summary>
    public string? IntermediateDirectory { get; init; }

    /// <summary>What a non-SDK-style .NET Framework project needs restored (M43a), or null for other projects.</summary>
    public NetFrameworkRestore? NetFrameworkRestore { get; init; }
}

/// <summary>
/// What Codalus restores for a non-SDK-style project (M43a, ADR 0075), which <c>dotnet restore</c> doesn't: its
/// <c>packages.config</c> packages into <paramref name="RepositoryPath"/>, and the reference assemblies package of its
/// framework (<paramref name="ReferenceAssemblies"/>, a moniker) into the global packages folder.
/// </summary>
internal sealed record NetFrameworkRestore(
    IReadOnlyList<NetFramework.PackagesConfigEntry> Packages,
    string? RepositoryPath,
    string? ReferenceAssemblies,
    string? GlobalPackagesFolder)
{
    /// <summary>The packages that aren't in the repository path yet.</summary>
    public IEnumerable<NetFramework.PackagesConfigEntry> MissingPackages =>
        RepositoryPath is null ? [] : Packages.Where(p => !Directory.Exists(NetFramework.PackageFolder(RepositoryPath, p)));

    /// <summary>Whether the reference assemblies package is needed and isn't in the global packages folder yet.</summary>
    public bool MissingReferenceAssemblies =>
        ReferenceAssemblies is not null && GlobalPackagesFolder is not null
        && !File.Exists(Path.Combine(NetFramework.ReferenceAssembliesFolder(GlobalPackagesFolder, ReferenceAssemblies), ".NETFramework", NetFramework.VersionFolder(ReferenceAssemblies), "mscorlib.dll"));

    public bool IsComplete => !MissingReferenceAssemblies && !MissingPackages.Any();
}

/// <summary>
/// Evaluates SDK-style projects with MSBuild (no targets run) into the wire model. Multi-targeted projects are
/// evaluated once per target framework; items are the union of those evaluations. Non-SDK-style projects (M43a) are
/// evaluated once, for their .NET Framework.
/// </summary>
internal sealed class ProjectEvaluator(ProjectCollection collection)
{
    /// <summary>Where <c>packages.config</c> packages go (NuGet's <c>repositoryPath</c>, or the solution's <c>packages</c>); set by the session.</summary>
    public string? PackagesRepository { get; set; }

    /// <summary>NuGet's global packages folder (the reference assemblies packages are there); set by the session.</summary>
    public string? GlobalPackagesFolder { get; set; }

    /// <summary>Item types shown in the Solution Explorer (besides those listed in <c>AvailableItemName</c>).</summary>
    private static readonly HashSet<string> s_visibleItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Compile", "None", "Content", "EmbeddedResource", "Folder", "Page", "ApplicationDefinition", "Resource",
        "AdditionalFiles", "AvaloniaResource", "AvaloniaXaml", "Protobuf", "TypeScriptCompile",
        "MauiXaml", "MauiCss", "MauiImage", "MauiIcon", "MauiSplashScreen", "MauiFont", "MauiAsset", // .NET MAUI (M20a)
        "AndroidResource", "AndroidAsset", "BundleResource", "ImageAsset", "InterfaceDefinition",
        "Build", "PreDeploy", "PostDeploy", "RefactorLog", "SqlCmdVariable", // SQL database projects (Microsoft.Build.Sql)
    };

    /// <summary>Item types that <c>AvailableItemName</c> may list but that belong under Dependencies or nowhere.</summary>
    private static readonly HashSet<string> s_hiddenItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "PackageReference", "ProjectReference", "FrameworkReference", "Reference", "PackageVersion", "Using", "Analyzer",
    };

    public EvaluatedProject Evaluate(string path, string configuration, string platform, EvaluationContext context)
    {
        using var gate = StringCacheGate.Enter();
        var name = Path.GetFileNameWithoutExtension(path);
        if (!File.Exists(path))
        {
            return Failed(path, name, ProjectStates.Missing, "The project file does not exist.");
        }

        if (ComposeProject.Is(path))
        {
            return ComposeProject.Read(path, name);
        }

        var global = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Configuration"] = configuration,
            ["Platform"] = platform,
        };

        Project outer;
        try
        {
            outer = Load(path, global, context);
        }
        catch (InvalidProjectFileException ex)
        {
            return Failed(path, name, ProjectStates.Failed, ex.Message);
        }

        try
        {
            if (!string.Equals(outer.GetPropertyValue("UsingMicrosoftNETSdk"), "true", StringComparison.OrdinalIgnoreCase))
            {
                return EvaluateNonSdk(path, name, outer, global);
            }

            var frameworks = TargetFrameworks(outer);
            var diagnostics = new List<string>();
            var assets = AssetsFile.TryLoad(outer.GetPropertyValue("ProjectAssetsFile"), diagnostics);
            var references = new List<ReferenceInfo>();
            var imports = outer.Imports.Select(i => i.ImportedProject.FullPath).ToList();
            var items = new List<ProjectItemInfo>();
            var test = TestProperties.None;
            var fingerprint = new Fingerprint(path, global, outer);
            var missingPacks = new SortedSet<string>(MissingWorkloadPacks(outer), StringComparer.OrdinalIgnoreCase);
            string? windowsPackageType = null;

            if (frameworks.Length <= 1)
            {
                references.AddRange(References(outer, frameworks.FirstOrDefault() ?? string.Empty, assets));
                items.AddRange(Items(outer));
                test = TestProperties.Of(outer);
                windowsPackageType = NullIfEmpty(outer.GetPropertyValue("WindowsPackageType").Trim());
            }
            else
            {
                foreach (var framework in frameworks)
                {
                    var inner = Load(path, new Dictionary<string, string>(global, StringComparer.OrdinalIgnoreCase) { ["TargetFramework"] = framework }, context);
                    try
                    {
                        fingerprint.Add(inner);
                        references.AddRange(References(inner, framework, assets));
                        imports.AddRange(inner.Imports.Select(i => i.ImportedProject.FullPath));

                        // The SDK adds default items (Compile globs…) in the per-framework evaluations only.
                        items.AddRange(Items(inner).Where(i => !items.Any(e => e.Kind == i.Kind && e.Path == i.Path)));

                        // Package props (Microsoft.NET.Test.Sdk, the testing platform) are imported per framework.
                        test = test.Or(TestProperties.Of(inner));
                        missingPacks.UnionWith(MissingWorkloadPacks(inner));

                        // MAUI's Windows target (M44): the Windows App SDK's targets evaluate it in that framework only.
                        if (windowsPackageType is null && framework.Contains("-windows", StringComparison.OrdinalIgnoreCase))
                        {
                            windowsPackageType = NullIfEmpty(inner.GetPropertyValue("WindowsPackageType").Trim());
                        }
                    }
                    finally
                    {
                        collection.UnloadProject(inner);
                    }
                }
            }

            var info = new ProjectInfo(
                path,
                name,
                ProjectStates.Loaded,
                null,
                frameworks,
                NullIfEmpty(outer.GetPropertyValue("OutputType")),
                [.. items],
                [.. references],
                [.. diagnostics],
                assets is not null,
                test.IsTestProject || test.IsTestingPlatformApplication,
                test.Platform)
            {
                IsAspireHost = string.Equals(outer.GetPropertyValue("IsAspireHost").Trim(), "true", StringComparison.OrdinalIgnoreCase),
                UsesMaui = string.Equals(outer.GetPropertyValue("UseMaui").Trim(), "true", StringComparison.OrdinalIgnoreCase),
                UsesWpf = string.Equals(outer.GetPropertyValue("UseWPF").Trim(), "true", StringComparison.OrdinalIgnoreCase),
                UsesWindowsForms = string.Equals(outer.GetPropertyValue("UseWindowsForms").Trim(), "true", StringComparison.OrdinalIgnoreCase),
                UsesWinUI = string.Equals(outer.GetPropertyValue("UseWinUI").Trim(), "true", StringComparison.OrdinalIgnoreCase),
                WindowsPackageType = windowsPackageType,
                MissingWorkloadPacks = [.. missingPacks],
                Packages = assets is null ? [] : [.. frameworks.SelectMany(assets.Graph)],
            };
            var distinctImports = imports.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var assetsFile = NullIfEmpty(outer.GetPropertyValue("ProjectAssetsFile"));
            var assetsStamp = assetsFile is not null && File.Exists(assetsFile) ? File.GetLastWriteTimeUtc(assetsFile) : DateTime.MinValue;
            return new EvaluatedProject(info, distinctImports, assetsFile)
            {
                AssetsStamp = assetsStamp,
                Fingerprint = fingerprint.Finish(distinctImports, assetsFile),
                IntermediateDirectory = NullIfEmpty(outer.GetPropertyValue("BaseIntermediateOutputPath")) is { } obj
                    ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(outer.DirectoryPath, FixSeparators(obj))))
                    : null,
            };
        }
        catch (InvalidProjectFileException ex)
        {
            return Failed(path, name, ProjectStates.Failed, ex.Message);
        }
        finally
        {
            collection.UnloadProject(outer);
        }
    }

    /// <summary>
    /// A non-SDK-style project (M43a, ADR 0075): .NET Framework only, evaluated once. Its frameworks are its
    /// <c>TargetFrameworkVersion</c>'s moniker; its Dependencies are its <c>Reference</c> items (with their
    /// <c>HintPath</c>), its <c>packages.config</c> packages and project references; a project that imports the
    /// classic IDE's own targets loads with <see cref="ProjectInfo.CannotBuildReason"/>.
    /// </summary>
    private EvaluatedProject EvaluateNonSdk(string path, string name, Project outer, Dictionary<string, string> global)
    {
        var identifier = outer.GetPropertyValue("TargetFrameworkIdentifier").Trim();
        var moniker = identifier is "" or ".NETFramework" ? NetFramework.ShortName(outer.GetPropertyValue("TargetFrameworkVersion")) : null;
        if (moniker is null)
        {
            return Failed(path, name, ProjectStates.Unsupported,
                "Non-SDK-style projects load only for .NET Framework (a TargetFrameworkVersion from v2.0 to v4.8.1). Convert it to an SDK-style project to open it here.");
        }

        var directory = outer.DirectoryPath;
        var imports = outer.Imports.Select(i => i.ImportedProject.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var references = References(outer, moniker, null).ToList();

        // The framework: the reference assemblies package (restored by Codalus), or the Developer Pack where it's installed.
        var used = NullIfEmpty(outer.GetPropertyValue(NetFramework.ReferenceAssembliesProperty));
        var installed = NullIfEmpty(outer.GetPropertyValue("_CodalusNetFxInstalled"));
        references.Insert(0, installed is not null && used is null
            ? new ReferenceInfo(ReferenceKinds.Framework, outer.GetPropertyValue("TargetFrameworkMoniker"), null, installed, moniker, true)
            : new ReferenceInfo(ReferenceKinds.Framework, NetFramework.ReferenceAssembliesPackageOf(moniker), NetFramework.ReferenceAssembliesVersion, used, moniker, used is not null));

        // packages.config: its packages under Dependencies, resolved once they're in the repository path.
        var packagesConfig = NetFramework.PackagesConfigOf(path);
        var packages = packagesConfig is null ? [] : NetFramework.ReadPackagesConfig(packagesConfig);
        foreach (var package in packages)
        {
            var folder = PackagesRepository is null ? null : NetFramework.PackageFolder(PackagesRepository, package);
            references.Add(new ReferenceInfo(ReferenceKinds.Package, package.Id, package.Version, folder, moniker, folder is not null && Directory.Exists(folder)));
        }

        // The classic IDE's own targets (a web application's): the project opens, but doesn't build here.
        var reason = NetFramework.VisualStudioTargetsReason(name, outer.Xml.Imports.Select(i =>
            (Path.GetFullPath(Path.Combine(directory, FixSeparators(outer.ExpandString(i.Project)))),
             (string?)(i.Condition.Length > 0 ? i.Condition : i.Parent is Microsoft.Build.Construction.ProjectImportGroupElement group ? group.Condition : null))));

        var fingerprint = new Fingerprint(path, global, outer);
        var hintPaths = outer.GetItems("Reference").Select(i => NullIfEmpty(i.GetMetadataValue("HintPath"))).OfType<string>()
            .Select(h => Path.GetFullPath(Path.Combine(directory, FixSeparators(h)))).ToList();
        var restore = new NetFrameworkRestore(packages, PackagesRepository, installed is null || used is not null ? moniker : null, GlobalPackagesFolder);

        // A test project: IsTestProject (a test SDK's props from packages.config), or the classic IDE's test project type (M43b).
        var test = TestProperties.Of(outer);
        var isTest = test.IsTestProject || NetFramework.IsTestProjectType(outer.GetPropertyValue("ProjectTypeGuids"));
        var info = new ProjectInfo(
            path,
            name,
            ProjectStates.Loaded,
            null,
            [moniker],
            NullIfEmpty(outer.GetPropertyValue("OutputType")),
            [.. Items(outer)],
            [.. references],
            [],
            restore.IsComplete,
            isTest,
            isTest ? TestPlatforms.VSTest : null)
        {
            IsNonSdkStyle = true,
            CannotBuildReason = reason,
            UsesWindowsForms = references.Any(r => r.Kind == ReferenceKinds.Assembly && r.Name.Equals("System.Windows.Forms", StringComparison.OrdinalIgnoreCase)),
            UsesWpf = references.Any(r => r.Kind == ReferenceKinds.Assembly && r.Name.Equals("PresentationFramework", StringComparison.OrdinalIgnoreCase)),
        };

        // What a restore changes (the packages behind the hint paths, the reference assemblies) changes the design-time build.
        IEnumerable<string> restored = [.. hintPaths, .. packagesConfig is null ? [] : new[] { packagesConfig }];
        return new EvaluatedProject(info, imports)
        {
            Fingerprint = fingerprint.Finish(imports, null, restored),
            IntermediateDirectory = NullIfEmpty(outer.GetPropertyValue("BaseIntermediateOutputPath")) is { } obj
                ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(directory, FixSeparators(obj))))
                : null,
            NetFrameworkRestore = restore,
        };
    }

    /// <summary>The workload packs the SDK's workload resolver found missing for this evaluation (M20a).</summary>
    private static IEnumerable<string> MissingWorkloadPacks(Project project) =>
        project.GetItems("MissingWorkloadPack").Select(i => i.EvaluatedInclude).Where(i => i.Length > 0);

    /// <summary>
    /// Whether a project is a test project, and on which platform (ADR 0010). xUnit v3 and MSTest.Sdk projects set only
    /// <c>IsTestingPlatformApplication</c>.
    /// </summary>
    private readonly record struct TestProperties(bool IsTestProject, bool IsTestingPlatformApplication)
    {
        public static TestProperties None => default;

        public string? Platform => IsTestingPlatformApplication ? TestPlatforms.Mtp : IsTestProject ? TestPlatforms.VSTest : null;

        public static TestProperties Of(Project project) => new(
            IsTrue(project.GetPropertyValue("IsTestProject")),
            IsTrue(project.GetPropertyValue("IsTestingPlatformApplication")));

        public TestProperties Or(TestProperties other) => new(IsTestProject || other.IsTestProject, IsTestingPlatformApplication || other.IsTestingPlatformApplication);

        private static bool IsTrue(string value) => string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    private Project Load(string path, Dictionary<string, string> global, EvaluationContext context) =>
        Project.FromFile(path, new Microsoft.Build.Definition.ProjectOptions
        {
            GlobalProperties = global,
            ProjectCollection = collection,
            EvaluationContext = context,
            LoadSettings = ProjectLoadSettings.IgnoreMissingImports | ProjectLoadSettings.IgnoreInvalidImports
                           | ProjectLoadSettings.IgnoreEmptyImports | ProjectLoadSettings.RecordDuplicateButNotCircularImports,
        });

    private static EvaluatedProject Failed(string path, string name, string state, string error) =>
        new(new ProjectInfo(path, name, state, error, [], null, [], [], [], false), []);

    public static string[] TargetFrameworks(Project project)
    {
        var multi = project.GetPropertyValue("TargetFrameworks");
        if (!string.IsNullOrWhiteSpace(multi))
        {
            return [.. multi.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        return project.GetPropertyValue("TargetFramework") is { Length: > 0 } single ? [single] : [];
    }

    private static IEnumerable<ProjectItemInfo> Items(Project project)
    {
        var visibleTypes = new HashSet<string>(s_visibleItemTypes, StringComparer.OrdinalIgnoreCase);
        foreach (var available in project.GetItems("AvailableItemName"))
        {
            visibleTypes.Add(available.EvaluatedInclude);
        }

        visibleTypes.ExceptWith(s_hiddenItemTypes);
        var directory = project.DirectoryPath;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in project.Items)
        {
            if (!visibleTypes.Contains(item.ItemType)
                || string.Equals(item.GetMetadataValue("Visible"), "false", StringComparison.OrdinalIgnoreCase)
                || item.EvaluatedInclude.Length == 0)
            {
                continue;
            }

            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(directory, FixSeparators(item.EvaluatedInclude))));
            if (!seen.Add(item.ItemType + "|" + full))
            {
                continue;
            }

            yield return new ProjectItemInfo(
                item.ItemType,
                full,
                NullIfEmpty(item.GetMetadataValue("Link")),
                NullIfEmpty(item.GetMetadataValue("DependentUpon")));
        }
    }

    private static IEnumerable<ReferenceInfo> References(Project project, string framework, AssetsFile? assets)
    {
        var directory = project.DirectoryPath;
        foreach (var item in project.GetItems("FrameworkReference"))
        {
            yield return new ReferenceInfo(ReferenceKinds.Framework, item.EvaluatedInclude, null, null, framework, true);
        }

        // An SDK-style .NET Framework project (M43a): the SDK adds the reference assemblies package in a target, so it is
        // known from the assets file; without it the framework's own reference assemblies (the Developer Pack) are used.
        if (assets is not null && NetFramework.IsMoniker(framework) && !project.GetItems("PackageReference").Any(i => i.EvaluatedInclude.Equals(NetFramework.ReferenceAssembliesPackage, StringComparison.OrdinalIgnoreCase)))
        {
            yield return assets.ResolvedPackageVersion(framework, NetFramework.ReferenceAssembliesPackage) is { } version
                ? new ReferenceInfo(ReferenceKinds.Framework, NetFramework.ReferenceAssembliesPackage, version, null, framework, true)
                : new ReferenceInfo(ReferenceKinds.Framework, project.GetPropertyValue("TargetFrameworkMoniker"), null, null, framework, true);
        }

        var central = project.GetItems("PackageVersion")
            .GroupBy(i => i.EvaluatedInclude, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().GetMetadataValue("Version"), StringComparer.OrdinalIgnoreCase);
        foreach (var item in project.GetItems("PackageReference"))
        {
            var requested = NullIfEmpty(item.GetMetadataValue("VersionOverride"))
                            ?? NullIfEmpty(item.GetMetadataValue("Version"))
                            ?? central.GetValueOrDefault(item.EvaluatedInclude);
            var resolved = assets?.ResolvedPackageVersion(framework, item.EvaluatedInclude);
            var kind = IsFrameworkPackage(item) ? ReferenceKinds.Framework : ReferenceKinds.Package;
            yield return new ReferenceInfo(kind, item.EvaluatedInclude, resolved ?? requested, null, framework, resolved is not null);
        }

        foreach (var item in project.GetItems("ProjectReference"))
        {
            var full = Path.GetFullPath(Path.Combine(directory, FixSeparators(item.EvaluatedInclude)));
            yield return new ReferenceInfo(ReferenceKinds.Project, Path.GetFileNameWithoutExtension(full), null, full, framework, File.Exists(full));
        }

        foreach (var item in project.GetItems("Reference").Where(i => !string.Equals(i.GetMetadataValue("IsImplicitlyDefined"), "true", StringComparison.OrdinalIgnoreCase) && !IsImportedMscorlib(project, i)))
        {
            var hint = NullIfEmpty(item.GetMetadataValue("HintPath"));
            var full = hint is null ? null : Path.GetFullPath(Path.Combine(directory, hint));
            yield return new ReferenceInfo(ReferenceKinds.Assembly, item.EvaluatedInclude.Split(',')[0], null, full, framework, full is null || File.Exists(full));
        }
    }

    /// <summary>The <c>mscorlib</c> reference the reference assemblies package's targets add (M43a): part of the framework, not the project's.</summary>
    private static bool IsImportedMscorlib(Project project, ProjectItem item) =>
        item.EvaluatedInclude.Equals("mscorlib", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(item.Xml.ContainingProject.FullPath, project.FullPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The framework of .NET Core 2.x and .NET Standard is a package the SDK adds (<c>Microsoft.NETCore.App</c>,
    /// <c>NETStandard.Library</c>), not a framework reference: it goes with the frameworks (M33). So do .NET Framework's
    /// reference assemblies (<c>Microsoft.NETFramework.ReferenceAssemblies</c>, M43a).
    /// </summary>
    private static bool IsFrameworkPackage(ProjectItem item) =>
        string.Equals(item.GetMetadataValue("IsImplicitlyDefined"), "true", StringComparison.OrdinalIgnoreCase)
        && item.EvaluatedInclude is var id
        && (id.Equals("Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase) || id.Equals("NETStandard.Library", StringComparison.OrdinalIgnoreCase)
            || id.Equals("Microsoft.AspNetCore.App", StringComparison.OrdinalIgnoreCase) || id.Equals("Microsoft.AspNetCore.All", StringComparison.OrdinalIgnoreCase)
            || id.Equals(NetFramework.ReferenceAssembliesPackage, StringComparison.OrdinalIgnoreCase)); // net4x (M43a)

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>Project files use <c>\</c>; on macOS and Linux MSBuild keeps it in items that are not existing files (<c>Folder</c>).</summary>
    private static string FixSeparators(string path) => Path.DirectorySeparatorChar == '\\' ? path : path.Replace('\\', '/');
}
