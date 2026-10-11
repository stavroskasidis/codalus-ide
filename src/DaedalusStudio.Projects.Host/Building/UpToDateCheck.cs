using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Build.Evaluation;

namespace DaedalusStudio.Projects.Host.Building;

/// <summary>
/// The fast up-to-date check (ADR 0034): a project is up to date when nothing it builds from changed since its last
/// successful build through Daedalus. After such a build, the check records each project's inputs (its files, its MSBuild
/// files and imports, its assets file, the outputs of the projects it references) and outputs (its assembly, reference
/// assembly, deps and runtime config files, and every file it copies to the output folder), with their sizes and write
/// times, in <c>obj/…/daedalus.uptodate.json</c>. The next build skips MSBuild for a project only when the same inputs
/// exist with the same stamps and every output is still as recorded. MSBuild's own incrementality stays the truth: a
/// project the check can't describe (an evaluation error, <c>DisableFastUpToDateCheck</c>) is never skipped. Inputs that
/// change while the build runs (other than what the build itself writes: the intermediate folder, referenced projects'
/// assemblies) are recorded as stale, so the next build runs MSBuild again. One instance serves one build: it caches
/// evaluations.
/// </summary>
internal sealed class UpToDateCheck(Func<string, IReadOnlyDictionary<string, string>> globalProperties) : IDisposable
{
    public const string StateFileName = "daedalus.uptodate.json";

    private static readonly StringComparer s_paths =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Items whose files the build reads.</summary>
    private static readonly string[] s_inputItems =
    [
        "Compile", "EmbeddedResource", "AdditionalFiles", "Analyzer", "EditorConfigFiles", "Page", "Resource", "ApplicationDefinition",
        "AvaloniaXaml", "AvaloniaResource", "Protobuf", "UpToDateCheckInput", "_CompileItemsForUpToDateCheck",
    ];

    /// <summary>Items copied to the output folder when their CopyToOutputDirectory says so.</summary>
    private static readonly string[] s_copiedItems = ["None", "Content", "EmbeddedResource", "Compile"];

    private readonly ProjectCollection _collection = new();
    private readonly Dictionary<string, List<Snapshot>> _snapshots = new(s_paths);
    private readonly Dictionary<string, Project?> _projects = new(s_paths);

    /// <summary>What a project builds from and into, for one target framework.</summary>
    internal sealed record Snapshot(string ProjectPath, string Key, string StateFile, IReadOnlyList<string> Inputs, IReadOnlyList<string> Outputs, string? Reason = null)
    {
        /// <summary>The assembly the project builds (what projects that reference it read).</summary>
        public string? Assembly { get; init; }

        /// <summary>Where the build writes inputs itself (the project's intermediate folder).</summary>
        public string? IntermediateRoot { get; init; }
    }

    /// <summary>A project's verdict: up to date, or why not.</summary>
    public sealed record Verdict(string Project, bool UpToDate, string Reason);

    /// <summary>
    /// Checks <paramref name="project"/> and, through its project references, everything it builds from. Each target
    /// framework of a multi-targeted project must be up to date.
    /// </summary>
    public Verdict Check(string project)
    {
        var visited = new HashSet<string>(s_paths);
        return CheckCore(Path.GetFullPath(project), visited);
    }

    private Verdict CheckCore(string project, HashSet<string> visited)
    {
        if (!visited.Add(project))
        {
            return new Verdict(project, true, "checked");
        }

        var snapshots = Describe(project);
        if (snapshots.Count == 0)
        {
            return new Verdict(project, false, "the project can't be evaluated");
        }

        if (snapshots.FirstOrDefault(s => s.Reason is not null) is { Reason: { } failed })
        {
            return new Verdict(project, false, failed);
        }

        foreach (var reference in References(project))
        {
            var verdict = CheckCore(reference, visited);
            if (!verdict.UpToDate)
            {
                return new Verdict(project, false, $"{Path.GetFileNameWithoutExtension(reference)} is out of date ({verdict.Reason})");
            }
        }

        foreach (var snapshot in snapshots)
        {
            if (Compare(snapshot) is { } reason)
            {
                return new Verdict(project, false, reason);
            }
        }

        return new Verdict(project, true, "up to date");
    }

    /// <summary><paramref name="projects"/> and every project they reference, directly or not.</summary>
    public IReadOnlyList<string> Closure(IEnumerable<string> projects)
    {
        var all = new List<string>();
        var seen = new HashSet<string>(s_paths);
        var queue = new Queue<string>(projects.Select(Path.GetFullPath));
        while (queue.TryDequeue(out var project))
        {
            if (!seen.Add(project))
            {
                continue;
            }

            all.Add(project);
            foreach (var reference in References(project))
            {
                queue.Enqueue(reference);
            }
        }

        return all;
    }

    /// <summary>
    /// Records the state of <paramref name="project"/> (each target framework) after a successful build that started at
    /// <paramref name="buildStarted"/> (UTC). Use an instance created after the build: the build may have changed what
    /// the project imports (its restore's files).
    /// </summary>
    public void Record(string project, DateTime buildStarted)
    {
        var full = Path.GetFullPath(project);
        foreach (var snapshot in Describe(full).Where(s => s.Reason is null))
        {
            var generated = new HashSet<string>(s_paths);
            foreach (var reference in References(full))
            {
                generated.UnionWith(Describe(reference).Select(s => s.Assembly).OfType<string>());
            }

            var inputs = new JsonObject();
            foreach (var file in snapshot.Inputs)
            {
                var stamp = Stamp(file);
                var changedDuringBuild = File.Exists(file) && File.GetLastWriteTimeUtc(file) > buildStarted
                                         && !generated.Contains(file)
                                         && !(snapshot.IntermediateRoot is { } obj && file.StartsWith(obj, s_paths == StringComparer.Ordinal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
                inputs[file] = changedDuringBuild ? "stale" : stamp;
            }

            var state = new JsonObject
            {
                ["key"] = snapshot.Key,
                ["inputs"] = inputs,
                ["outputs"] = Stamps(snapshot.Outputs),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(snapshot.StateFile)!);
            File.WriteAllText(snapshot.StateFile, state.ToJsonString());
        }
    }

    /// <summary>Forgets a project's recorded state (a failed or cancelled build, a clean).</summary>
    public void Forget(string project)
    {
        foreach (var snapshot in Describe(Path.GetFullPath(project)))
        {
            try
            {
                File.Delete(snapshot.StateFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Why the recorded state no longer matches, or null when it does.</summary>
    private static string? Compare(Snapshot snapshot)
    {
        JsonObject? state;
        try
        {
            state = File.Exists(snapshot.StateFile) ? JsonNode.Parse(File.ReadAllText(snapshot.StateFile)) as JsonObject : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            state = null;
        }

        if (state is null)
        {
            return "it wasn't built here yet";
        }

        if (state["key"]?.GetValue<string>() != snapshot.Key)
        {
            return "the configuration or SDK changed";
        }

        if (Differs(state["inputs"] as JsonObject, snapshot.Inputs, "input") is { } input)
        {
            return input;
        }

        return Differs(state["outputs"] as JsonObject, snapshot.Outputs, "output");
    }

    private static string? Differs(JsonObject? recorded, IReadOnlyList<string> files, string kind)
    {
        if (recorded is null)
        {
            return $"no {kind}s were recorded";
        }

        var current = new HashSet<string>(files, s_paths);
        foreach (var (path, _) in recorded)
        {
            if (!current.Contains(path))
            {
                return $"the {kind} {Path.GetFileName(path)} is no longer one";
            }
        }

        foreach (var file in files)
        {
            if (recorded[file]?.GetValue<string>() is not { } stamp)
            {
                return $"{Path.GetFileName(file)} is a new {kind}";
            }

            if (Stamp(file) != stamp)
            {
                return File.Exists(file) ? $"the {kind} {Path.GetFileName(file)} changed" : $"the {kind} {Path.GetFileName(file)} is missing";
            }
        }

        return null;
    }

    private static JsonObject Stamps(IEnumerable<string> files)
    {
        var stamps = new JsonObject();
        foreach (var file in files.Distinct(s_paths))
        {
            stamps[file] = Stamp(file);
        }

        return stamps;
    }

    /// <summary>A file's size and last write time (UTC ticks), or "missing".</summary>
    private static string Stamp(string file)
    {
        var info = new FileInfo(file);
        return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "missing";
    }

    // ------------------------------------------------------------------ evaluation

    private Project? Load(string project)
    {
        if (!_projects.TryGetValue(project, out var loaded))
        {
            try
            {
                loaded = new Project(project, new Dictionary<string, string>(globalProperties(project), StringComparer.OrdinalIgnoreCase), null, _collection);
            }
            catch (Exception ex) when (ex is Microsoft.Build.Exceptions.InvalidProjectFileException or IOException or UnauthorizedAccessException)
            {
                loaded = null;
            }

            _projects[project] = loaded;
        }

        return loaded;
    }

    private List<Snapshot> Describe(string project)
    {
        if (_snapshots.TryGetValue(project, out var cached))
        {
            return cached;
        }

        var snapshots = new List<Snapshot>();
        _snapshots[project] = snapshots;
        if (Load(project) is not { } outer)
        {
            return snapshots;
        }

        var frameworks = ProjectEvaluator.TargetFrameworks(outer);
        if (frameworks.Length <= 1 || outer.GetPropertyValue("TargetFramework").Length > 0)
        {
            snapshots.Add(Capture(outer));
        }
        else
        {
            foreach (var framework in frameworks)
            {
                var inner = new Project(project, new Dictionary<string, string>(globalProperties(project), StringComparer.OrdinalIgnoreCase) { ["TargetFramework"] = framework }, null, _collection);
                snapshots.Add(Capture(inner));
            }
        }

        return snapshots;
    }

    /// <summary>The inputs and outputs of one evaluation (one target framework).</summary>
    private Snapshot Capture(Project project)
    {
        var directory = project.DirectoryPath;
        string Full(string path) => Path.GetFullPath(Path.Combine(directory, path.Replace('\\', Path.DirectorySeparatorChar)));
        var intermediate = Full(project.GetPropertyValue("IntermediateOutputPath"));
        var stateFile = Path.Combine(intermediate, StateFileName);
        if (IsTrue(project.GetPropertyValue("DisableFastUpToDateCheck")))
        {
            return new Snapshot(project.FullPath, string.Empty, stateFile, [], [], "DisableFastUpToDateCheck is set");
        }

        if (project.GetPropertyValue("TargetPath") is not { Length: > 0 } targetPath)
        {
            return new Snapshot(project.FullPath, string.Empty, stateFile, [], [], "the project has no TargetPath");
        }

        var key = Hash(string.Join("|", project.GlobalProperties.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => $"{p.Key}={p.Value}"))
                       + "|" + project.GetPropertyValue("MSBuildExtensionsPath") + "|" + project.GetPropertyValue("TargetFramework"));

        var inputs = new List<string> { project.FullPath };
        inputs.AddRange(project.Imports.Select(i => i.ImportedProject.FullPath));
        foreach (var itemType in s_inputItems)
        {
            inputs.AddRange(project.GetItems(itemType).Select(i => i.EvaluatedInclude).Where(i => i.Length > 0).Select(Full));
        }

        foreach (var reference in project.GetItems("Reference").Select(r => r.GetMetadataValue("HintPath")).Where(h => h.Length > 0))
        {
            inputs.Add(Full(reference));
        }

        foreach (var property in new[] { "ProjectAssetsFile", "ApplicationIcon", "ApplicationManifest", "AssemblyOriginatorKeyFile", "Win32Resource" })
        {
            if (project.GetPropertyValue(property) is { Length: > 0 } file)
            {
                inputs.Add(Full(file));
            }
        }

        var outDir = Full(project.GetPropertyValue("OutDir"));
        var outputs = new List<string> { Full(targetPath) };
        foreach (var property in new[] { "ProjectDepsFilePath", "ProjectRuntimeConfigFilePath", "TargetRefPath" })
        {
            if (project.GetPropertyValue(property) is { Length: > 0 } file)
            {
                outputs.Add(Full(file));
            }
        }

        foreach (var itemType in s_copiedItems)
        {
            foreach (var item in project.GetItems(itemType))
            {
                var copy = item.GetMetadataValue("CopyToOutputDirectory");
                if (copy.Length == 0 || copy.Equals("Never", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var source = Full(item.EvaluatedInclude);
                inputs.Add(source);
                var relative = item.GetMetadataValue("Link") is { Length: > 0 } link ? link
                    : item.GetMetadataValue("TargetPath") is { Length: > 0 } target ? target
                    : Path.GetRelativePath(directory, source);
                outputs.Add(Path.GetFullPath(Path.Combine(outDir, relative.Replace('\\', Path.DirectorySeparatorChar))));
            }
        }

        foreach (var output in project.GetItems("UpToDateCheckBuilt").Select(i => i.EvaluatedInclude).Where(i => i.Length > 0))
        {
            outputs.Add(Full(output));
        }

        // The projects it references build first: their assemblies are inputs here.
        foreach (var reference in References(project))
        {
            inputs.AddRange(Describe(reference).Select(s => s.Assembly).OfType<string>());
        }

        var obj = Full(project.GetPropertyValue("BaseIntermediateOutputPath") is { Length: > 0 } baseObj ? baseObj : "obj");
        return new Snapshot(project.FullPath, key, stateFile, [.. inputs.Distinct(s_paths).Order(StringComparer.Ordinal)], [.. outputs.Distinct(s_paths).Order(StringComparer.Ordinal)])
        {
            Assembly = Full(targetPath),
            IntermediateRoot = obj.EndsWith(Path.DirectorySeparatorChar) ? obj : obj + Path.DirectorySeparatorChar,
        };
    }

    /// <summary>The projects a project references (full paths of those that exist).</summary>
    private List<string> References(string project) => Load(project) is { } loaded ? References(loaded) : [];

    private static List<string> References(Project project) =>
        project.GetItems("ProjectReference")
            .Select(r => Path.GetFullPath(Path.Combine(project.DirectoryPath, r.EvaluatedInclude.Replace('\\', Path.DirectorySeparatorChar))))
            .Where(File.Exists)
            .Distinct(s_paths)
            .ToList();

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    private static bool IsTrue(string value) => value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _collection.Dispose();
}