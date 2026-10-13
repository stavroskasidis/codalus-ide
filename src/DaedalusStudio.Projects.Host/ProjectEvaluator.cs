using DaedalusStudio.Projects.Protocol;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Exceptions;

namespace DaedalusStudio.Projects.Host;

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
}

/// <summary>
/// Evaluates SDK-style projects with MSBuild (no targets run) into the wire model. Multi-targeted projects are
/// evaluated once per target framework; items are the union of those evaluations.
/// </summary>
internal sealed class ProjectEvaluator(ProjectCollection collection)
{
    /// <summary>Item types shown in the Solution Explorer (besides those listed in <c>AvailableItemName</c>).</summary>
    private static readonly HashSet<string> s_visibleItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Compile", "None", "Content", "EmbeddedResource", "Folder", "Page", "ApplicationDefinition", "Resource",
        "AdditionalFiles", "AvaloniaResource", "AvaloniaXaml", "Protobuf", "TypeScriptCompile",
        "Build", "PreDeploy", "PostDeploy", "RefactorLog", "SqlCmdVariable", // SQL database projects (Microsoft.Build.Sql)
    };

    /// <summary>Item types that <c>AvailableItemName</c> may list but that belong under Dependencies or nowhere.</summary>
    private static readonly HashSet<string> s_hiddenItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "PackageReference", "ProjectReference", "FrameworkReference", "Reference", "PackageVersion", "Using", "Analyzer",
    };

    public EvaluatedProject Evaluate(string path, string configuration, string platform, EvaluationContext context)
    {
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
                return Failed(path, name, ProjectStates.Unsupported, "Only SDK-style projects are supported.");
            }

            var frameworks = TargetFrameworks(outer);
            var diagnostics = new List<string>();
            var assets = AssetsFile.TryLoad(outer.GetPropertyValue("ProjectAssetsFile"), diagnostics);
            var references = new List<ReferenceInfo>();
            var imports = outer.Imports.Select(i => i.ImportedProject.FullPath).ToList();
            var items = new List<ProjectItemInfo>();
            var test = TestProperties.None;
            var fingerprint = new Fingerprint(path, global, outer);

            if (frameworks.Length <= 1)
            {
                references.AddRange(References(outer, frameworks.FirstOrDefault() ?? string.Empty, assets));
                items.AddRange(Items(outer));
                test = TestProperties.Of(outer);
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

        foreach (var item in project.GetItems("Reference").Where(i => !string.Equals(i.GetMetadataValue("IsImplicitlyDefined"), "true", StringComparison.OrdinalIgnoreCase)))
        {
            var hint = NullIfEmpty(item.GetMetadataValue("HintPath"));
            var full = hint is null ? null : Path.GetFullPath(Path.Combine(directory, hint));
            yield return new ReferenceInfo(ReferenceKinds.Assembly, item.EvaluatedInclude.Split(',')[0], null, full, framework, full is null || File.Exists(full));
        }
    }

    /// <summary>
    /// The framework of .NET Core 2.x and .NET Standard is a package the SDK adds (<c>Microsoft.NETCore.App</c>,
    /// <c>NETStandard.Library</c>), not a framework reference: it goes with the frameworks (M33).
    /// </summary>
    private static bool IsFrameworkPackage(ProjectItem item) =>
        string.Equals(item.GetMetadataValue("IsImplicitlyDefined"), "true", StringComparison.OrdinalIgnoreCase)
        && item.EvaluatedInclude is var id
        && (id.Equals("Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase) || id.Equals("NETStandard.Library", StringComparison.OrdinalIgnoreCase)
            || id.Equals("Microsoft.AspNetCore.App", StringComparison.OrdinalIgnoreCase) || id.Equals("Microsoft.AspNetCore.All", StringComparison.OrdinalIgnoreCase));

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>Project files use <c>\</c>; on macOS and Linux MSBuild keeps it in items that are not existing files (<c>Folder</c>).</summary>
    private static string FixSeparators(string path) => Path.DirectorySeparatorChar == '\\' ? path : path.Replace('\\', '/');
}
