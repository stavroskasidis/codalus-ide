using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Codalus.Projects.Host.Packages;
using Codalus.Projects.Host.ProjectManagement;
using Codalus.Projects.Protocol;
using Microsoft.VisualStudio.SolutionPersistence.Model;

namespace Codalus.Projects.Host;

/// <summary>Project properties, references, configurations, dependencies, load/unload, filters and SDKs (ADR 0013).</summary>
internal sealed partial class SolutionSession
{
    private readonly HashSet<string> _unloaded = new(s_pathComparer);

    /// <summary>The MSBuild folder of the SDK this host runs (set by the server).</summary>
    public string? SdkPath { get; set; }

    /// <summary>Why the SDK the solution's folder resolves to isn't the one in use (ADR 0025), or null.</summary>
    public string? SdkWarning { get; set; }

    /// <summary>The solution filter the solution was opened through, if any.</summary>
    public string? FilterPath { get; private set; }

    /// <summary>The unloaded projects: per user, in the solution's state folder (ADR 0070).</summary>
    private string StateFile => System.IO.Path.Combine(SolutionStateLayout.Directory(Path, IsFolder), SolutionStateLayout.UnloadedProjectsFile);

    /// <summary>Where the unloaded projects were kept before ADR 0070 (moved on open).</summary>
    private string LegacyStateFile => System.IO.Path.Combine(SolutionDirectory, ".codalus", "solution.json");

    // ------------------------------------------------------------------ properties

    public ProjectPropertiesResult GetProperties(GetPropertiesParams parameters)
    {
        RequireTrusted("Project properties");
        var path = System.IO.Path.GetFullPath(parameters.Project);
        var properties = SolutionProperties();
        (properties["Configuration"], properties["Platform"]) = ProjectConfiguration(path);
        if (parameters.Configuration is { Length: > 0 } configuration)
        {
            properties["Configuration"] = configuration;
        }

        return ProjectManagement.ProjectProperties.Get(path, properties, parameters.Names, parameters.Configuration);
    }

    public async Task<SetPropertyResult> SetPropertyAsync(SetPropertyParams parameters)
    {
        RequireTrusted("Project properties");
        if (string.IsNullOrWhiteSpace(parameters.Name) || !parameters.Name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.'))
        {
            throw new ArgumentException($"'{parameters.Name}' is not a valid property name.");
        }

        var path = System.IO.Path.GetFullPath(parameters.Project);
        var changed = ProjectManagement.ProjectProperties.Set(path, parameters.Name, parameters.Value, string.IsNullOrEmpty(parameters.Configuration) ? null : parameters.Configuration);
        if (changed)
        {
            await ReevaluateAsync([path]).ConfigureAwait(false);
        }

        return new SetPropertyResult(changed);
    }

    // ------------------------------------------------------------------ references

    public void AddReference(ReferenceEditParams parameters)
    {
        var project = System.IO.Path.GetFullPath(parameters.Project);
        var target = System.IO.Path.GetFullPath(parameters.Path);
        if (!File.Exists(target))
        {
            throw new IOException($"{target} does not exist.");
        }

        _ = parameters.Kind switch
        {
            ReferenceEditKinds.Project => _editor.AddProjectReference(project, target),
            ReferenceEditKinds.Assembly or ReferenceEditKinds.Analyzer => _editor.AddFileReference(project, parameters.Kind, target),
            _ => throw new ArgumentException($"Unknown reference kind '{parameters.Kind}'."),
        };
    }

    public void RemoveReference(ReferenceEditParams parameters)
    {
        var project = System.IO.Path.GetFullPath(parameters.Project);
        var target = System.IO.Path.GetFullPath(parameters.Path);
        var removed = parameters.Kind switch
        {
            ReferenceEditKinds.Project => _editor.RemoveProjectReference(project, target),
            ReferenceEditKinds.Assembly or ReferenceEditKinds.Analyzer => _editor.RemoveFileReference(project, parameters.Kind, target),
            _ => throw new ArgumentException($"Unknown reference kind '{parameters.Kind}'."),
        };
        if (!removed)
        {
            throw new InvalidOperationException($"{System.IO.Path.GetFileName(project)} has no such reference: {target}.");
        }
    }

    // ------------------------------------------------------------------ configurations

    public SolutionConfigurationsResult GetConfigurations()
    {
        var model = _model ?? throw new InvalidOperationException("A project opened on its own has no solution configurations.");
        var buildTypes = model.BuildTypes.Count > 0 ? model.BuildTypes.ToArray() : ["Debug", "Release"];
        var platforms = model.Platforms.Count > 0 ? model.Platforms.ToArray() : ["Any CPU"];
        var projects = new List<ProjectConfigurationEntries>();
        foreach (var project in model.SolutionProjects)
        {
            var path = Full(SolutionDirectory, project.FilePath);
            var (declaredConfigurations, declaredPlatforms) = DeclaredConfigurations(path);
            var entries = new List<ProjectConfigurationEntry>();
            foreach (var buildType in buildTypes)
            {
                foreach (var platform in platforms)
                {
                    var mapped = project.GetProjectConfiguration(buildType, platform);
                    entries.Add(new ProjectConfigurationEntry(buildType, platform, mapped.BuildType ?? buildType, mapped.Platform ?? platform.Replace(" ", string.Empty, StringComparison.Ordinal), mapped.Build));
                }
            }

            projects.Add(new ProjectConfigurationEntries(path, project.ActualDisplayName, declaredConfigurations, declaredPlatforms, [.. entries]));
        }

        return new SolutionConfigurationsResult(buildTypes, platforms, [.. projects]);
    }

    /// <summary>What a project declares (<c>Configurations</c>, <c>Platforms</c>), from its last evaluation's defaults.</summary>
    private (string[] Configurations, string[] Platforms) DeclaredConfigurations(string path)
    {
        if (!Trusted || !File.Exists(path))
        {
            return (["Debug", "Release"], ["AnyCPU"]);
        }

        try
        {
            using var collection = new Microsoft.Build.Evaluation.ProjectCollection(SolutionProperties());
            var project = collection.LoadProject(path);
            static string[] Split(string value, string[] fallback) =>
                value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } values ? values : fallback;
            return (Split(project.GetPropertyValue("Configurations"), ["Debug", "Release"]), Split(project.GetPropertyValue("Platforms"), ["AnyCPU"]));
        }
        catch (Microsoft.Build.Exceptions.InvalidProjectFileException)
        {
            return (["Debug", "Release"], ["AnyCPU"]);
        }
    }

    public Task SetProjectConfigurationAsync(SetProjectConfigurationParams parameters, CancellationToken cancellationToken) =>
        EditSolutionAsync(model =>
        {
            var project = FindProject(model, parameters.Project) ?? throw new InvalidOperationException($"{parameters.Project} is not in the solution.");
            if (parameters.Configuration is { Length: > 0 } configuration)
            {
                project.AddProjectConfigurationRule(new ConfigurationRule(BuildDimension.BuildType, parameters.SolutionConfiguration, parameters.SolutionPlatform, configuration));
            }

            if (parameters.Platform is { Length: > 0 } platform)
            {
                project.AddProjectConfigurationRule(new ConfigurationRule(BuildDimension.Platform, parameters.SolutionConfiguration, parameters.SolutionPlatform, platform));
            }

            if (parameters.Build is { } build)
            {
                project.AddProjectConfigurationRule(new ConfigurationRule(BuildDimension.Build, parameters.SolutionConfiguration, parameters.SolutionPlatform, build ? "true" : "false"));
            }
        }, cancellationToken);

    public Task AddConfigurationAsync(SolutionConfigurationEditParams parameters, CancellationToken cancellationToken) =>
        EditSolutionAsync(model =>
        {
            var name = parameters.Name.Trim();
            if (name.Length == 0 || name.Contains('|', StringComparison.Ordinal))
            {
                throw new ArgumentException($"'{parameters.Name}' is not a valid name.");
            }

            var isBuildType = parameters.Dimension == ConfigurationDimensions.BuildType;
            var existing = isBuildType ? model.BuildTypes : model.Platforms;
            if (existing.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"The solution already has '{name}'.");
            }

            // Mappings of the copied configuration, captured before the new one exists.
            var buildTypes = model.BuildTypes.Count > 0 ? model.BuildTypes.ToList() : ["Debug", "Release"];
            var platforms = model.Platforms.Count > 0 ? model.Platforms.ToList() : ["Any CPU"];
            var copies = new List<(SolutionProjectModel Project, string BuildType, string Platform, (string? BuildType, string? Platform, bool Build) Mapped)>();
            if (parameters.CopyFrom is { Length: > 0 } source)
            {
                foreach (var project in model.SolutionProjects)
                {
                    foreach (var other in isBuildType ? platforms : buildTypes)
                    {
                        var (buildType, platform) = isBuildType ? (source, other) : (other, source);
                        var mapped = project.GetProjectConfiguration(buildType, platform);
                        copies.Add((project, isBuildType ? name : other, isBuildType ? other : name, (mapped.BuildType, mapped.Platform, mapped.Build)));
                    }
                }
            }

            if (isBuildType)
            {
                model.AddBuildType(name);
            }
            else if (parameters.Dimension == ConfigurationDimensions.Platform)
            {
                model.AddPlatform(name);
            }
            else
            {
                throw new ArgumentException($"Unknown dimension '{parameters.Dimension}'.");
            }

            foreach (var (project, buildType, platform, mapped) in copies)
            {
                if (mapped.BuildType is { } projectBuildType)
                {
                    project.AddProjectConfigurationRule(new ConfigurationRule(BuildDimension.BuildType, buildType, platform, projectBuildType));
                }

                if (mapped.Platform is { } projectPlatform)
                {
                    project.AddProjectConfigurationRule(new ConfigurationRule(BuildDimension.Platform, buildType, platform, projectPlatform));
                }

                if (!mapped.Build)
                {
                    project.AddProjectConfigurationRule(new ConfigurationRule(BuildDimension.Build, buildType, platform, "false"));
                }
            }
        }, cancellationToken);

    public Task RemoveConfigurationAsync(SolutionConfigurationEditParams parameters, CancellationToken cancellationToken) =>
        EditSolutionAsync(model =>
        {
            var removed = parameters.Dimension switch
            {
                ConfigurationDimensions.BuildType when model.BuildTypes.Count > 1 => model.RemoveBuildType(parameters.Name),
                ConfigurationDimensions.Platform when model.Platforms.Count > 1 => model.RemovePlatform(parameters.Name),
                ConfigurationDimensions.BuildType or ConfigurationDimensions.Platform => throw new InvalidOperationException("A solution needs at least one configuration and one platform."),
                _ => throw new ArgumentException($"Unknown dimension '{parameters.Dimension}'."),
            };
            if (!removed)
            {
                throw new InvalidOperationException($"The solution has no '{parameters.Name}'.");
            }
        }, cancellationToken);

    // ------------------------------------------------------------------ dependencies

    public SolutionDependenciesResult GetDependencies()
    {
        var model = _model ?? throw new InvalidOperationException("A project opened on its own has no solution dependencies.");
        return new SolutionDependenciesResult([.. model.SolutionProjects.Select(p => new ProjectDependencies(
            Full(SolutionDirectory, p.FilePath),
            [.. (p.Dependencies ?? []).Select(d => Full(SolutionDirectory, d.FilePath))]))]);
    }

    public Task SetDependenciesAsync(SetDependenciesParams parameters, CancellationToken cancellationToken) =>
        EditSolutionAsync(model =>
        {
            var project = FindProject(model, parameters.Project) ?? throw new InvalidOperationException($"{parameters.Project} is not in the solution.");
            var wanted = parameters.DependsOn.Select(d => FindProject(model, d) ?? throw new InvalidOperationException($"{d} is not in the solution.")).ToList();
            if (wanted.Contains(project))
            {
                throw new InvalidOperationException("A project can't depend on itself.");
            }

            foreach (var old in (project.Dependencies ?? []).ToList().Where(d => !wanted.Contains(d)))
            {
                project.RemoveDependency(old);
            }

            foreach (var dependency in wanted.Where(d => project.Dependencies?.Contains(d) != true))
            {
                project.AddDependency(dependency);
            }
        }, cancellationToken);

    // ------------------------------------------------------------------ load and unload

    /// <summary>Reads the unloaded projects (from the filter, or from the state folder's <c>solution.json</c>).</summary>
    private void LoadUnloadedState(IReadOnlyList<string>? filterProjects)
    {
        lock (_unloaded)
        {
            LoadUnloadedStateLocked(filterProjects);
        }
    }

    private void LoadUnloadedStateLocked(IReadOnlyList<string>? filterProjects)
    {
        _unloaded.Clear();
        if (filterProjects is not null)
        {
            var kept = filterProjects.Select(p => Full(SolutionDirectory, p)).ToHashSet(s_pathComparer);
            _unloaded.UnionWith(ProjectPaths.Where(p => !kept.Contains(p)));
            return;
        }

        try
        {
            var file = MigrateUnloadedState();
            if (File.Exists(file) && JsonNode.Parse(File.ReadAllText(file))?["unloadedProjects"] is JsonArray unloaded)
            {
                foreach (var item in unloaded.OfType<JsonValue>().Select(v => v.GetValue<string>()))
                {
                    _unloaded.Add(Full(SolutionDirectory, item));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            _ = _events.LogAsync("warning", $"Could not read {StateFile}: {ex.Message}");
        }
    }

    /// <summary>
    /// The file to read the unloaded projects from: the state folder's, after moving <c>.codalus/solution.json</c>
    /// there the first time; the old file while it can't be moved (a read-only folder).
    /// </summary>
    private string MigrateUnloadedState()
    {
        if (IsSingleProject || File.Exists(StateFile) || !File.Exists(LegacyStateFile))
        {
            return StateFile; // a project opened alone has no unloaded projects; the old file belongs to a solution
        }

        try
        {
            SolutionStateLayout.EnsureDirectory(Path, IsFolder);
            SolutionStateLayout.WriteAllTextAtomic(StateFile, File.ReadAllText(LegacyStateFile));
            File.Delete(LegacyStateFile);
            _ = _events.LogAsync("info", $"Moved {LegacyStateFile} to {StateFile}.");
            return StateFile;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = _events.LogAsync("warning", $"Could not move {LegacyStateFile} to {StateFile}: {ex.Message}");
            return File.Exists(StateFile) ? StateFile : LegacyStateFile;
        }
    }

    private void SaveUnloadedState()
    {
        if (FilterPath is not null || IsSingleProject)
        {
            return; // a filter is the user's file; it's saved explicitly with Save As Solution Filter
        }

        try
        {
            var state = File.Exists(StateFile) ? JsonNode.Parse(File.ReadAllText(StateFile)) as JsonObject ?? [] : [];
            state["unloadedProjects"] = new JsonArray([.. UnloadedProjects().Order(StringComparer.Ordinal).Select(p => (JsonNode)System.IO.Path.GetRelativePath(SolutionDirectory, p).Replace('\\', '/'))]);
            SolutionStateLayout.EnsureDirectory(Path, IsFolder);
            SolutionStateLayout.WriteAllTextAtomic(StateFile, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // The project stays unloaded (or loaded) for this session; it isn't remembered.
            _ = _events.LogAsync("warning", $"Could not save the unloaded projects to {StateFile}: {ex.Message}");
        }
    }

    /// <summary>Thread-safe: evaluations run in parallel with unload and reload.</summary>
    public bool IsUnloaded(string project)
    {
        lock (_unloaded)
        {
            return _unloaded.Contains(project);
        }
    }

    private bool SetUnloaded(string project, bool unloaded)
    {
        lock (_unloaded)
        {
            return unloaded ? _unloaded.Add(project) : _unloaded.Remove(project);
        }
    }

    private string[] UnloadedProjects()
    {
        lock (_unloaded)
        {
            return [.. _unloaded];
        }
    }

    public async Task UnloadAsync(string project)
    {
        var path = System.IO.Path.GetFullPath(project);
        if (!ProjectPaths.Contains(path, s_pathComparer))
        {
            throw new InvalidOperationException($"{path} is not in the solution.");
        }

        if (SetUnloaded(path, unloaded: true))
        {
            SaveUnloadedState();
            var info = UnloadedInfo(path);
            _evaluated[path] = new EvaluatedProject(info, []);
            await _events.ProjectEvaluatedAsync(info).ConfigureAwait(false);
        }
    }

    public async Task ReloadAsync(string project)
    {
        var path = System.IO.Path.GetFullPath(project);
        if (SetUnloaded(path, unloaded: false))
        {
            SaveUnloadedState();
            await ReevaluateAsync([path]).ConfigureAwait(false);
        }
    }

    private static ProjectInfo UnloadedInfo(string path) =>
        new(path, System.IO.Path.GetFileNameWithoutExtension(path), ProjectStates.Unloaded, null, [], null, [], [], [], false);

    /// <summary>
    /// The solution (or filter) builds use: with unloaded projects, a generated filter of the loaded ones in
    /// <c>.codalus/</c>, so MSBuild skips the others.
    /// </summary>
    private string BuildEntryPoint()
    {
        // Unloaded projects, compose projects (.dcproj, whose SDK only Visual Studio has: ADR 0042) and projects that
        // import Visual Studio's own targets (M43a, ADR 0075) aren't built.
        if (IsSingleProject || !ProjectPaths.Any(p => IsUnloaded(p) || ComposeProject.Is(p) || CannotBuild(p)))
        {
            return Path;
        }

        var filter = System.IO.Path.Combine(SolutionDirectory, ".codalus", "build.slnf");
        WriteFilter(filter, [.. ProjectPaths.Where(p => !IsUnloaded(p) && !ComposeProject.Is(p) && !CannotBuild(p))]);
        return filter;
    }

    public void SaveFilter(SaveFilterParams parameters)
    {
        if (IsSingleProject || IsFolder)
        {
            throw new InvalidOperationException("A project or folder opened on its own has no solution to filter.");
        }

        var projects = parameters.Projects?.Select(System.IO.Path.GetFullPath).ToList() ?? [.. ProjectPaths.Where(p => !IsUnloaded(p))];
        WriteFilter(System.IO.Path.GetFullPath(parameters.Path), projects);
    }

    /// <summary>A <c>.slnf</c> file: the solution path relative to the filter and the projects relative to the solution.</summary>
    private void WriteFilter(string filter, IReadOnlyList<string> projects)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(filter)!);
        var document = new JsonObject
        {
            ["solution"] = new JsonObject
            {
                ["path"] = System.IO.Path.GetRelativePath(System.IO.Path.GetDirectoryName(filter)!, Path).Replace('/', '\\'),
                ["projects"] = new JsonArray([.. projects.Select(p => (JsonNode)System.IO.Path.GetRelativePath(SolutionDirectory, p).Replace('/', '\\'))]),
            },
        };
        File.WriteAllText(filter, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    /// <summary>Reads a <c>.slnf</c>: the solution it filters (absolute) and its projects (relative to the solution).</summary>
    internal static (string Solution, string[] Projects) ReadFilter(string filter)
    {
        var root = JsonNode.Parse(File.ReadAllText(filter), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?["solution"]
                   ?? throw new InvalidOperationException($"{filter} is not a solution filter.");
        var solution = root["path"]?.GetValue<string>() ?? throw new InvalidOperationException($"{filter} names no solution.");
        var projects = (root["projects"] as JsonArray)?.OfType<JsonValue>().Select(v => v.GetValue<string>().Replace('\\', System.IO.Path.DirectorySeparatorChar)).ToArray() ?? [];
        return (Full(System.IO.Path.GetDirectoryName(filter)!, solution), projects);
    }

    // ------------------------------------------------------------------ SDKs

    public async Task<SdkInfoResult> GetSdkInfoAsync(CancellationToken cancellationToken)
    {
        var (sdksOk, sdksOutput) = await RunDotnetAsync(["--list-sdks"], cancellationToken).ConfigureAwait(false);
        var (workloadsOk, workloadsOutput) = await RunDotnetAsync(["workload", "list"], cancellationToken).ConfigureAwait(false);
        var sdks = sdksOk ? ParseSdks(sdksOutput) : [];
        var workloads = workloadsOk ? ParseWorkloads(workloadsOutput) : [];
        var globalJson = FindGlobalJson(SolutionDirectory);
        string? requested = null, rollForward = null, error = null;
        if (globalJson is not null)
        {
            try
            {
                var sdk = JsonNode.Parse(File.ReadAllText(globalJson), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?["sdk"];
                requested = sdk?["version"]?.GetValue<string>();
                rollForward = sdk?["rollForward"]?.GetValue<string>();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
            {
                error = $"{globalJson}: {ex.Message}";
            }
        }

        var resolved = SdkPath is null ? null : System.IO.Path.TrimEndingDirectorySeparator(SdkPath);
        return new SdkInfoResult(
            resolved,
            resolved is null ? null : System.IO.Path.GetFileName(resolved),
            globalJson,
            requested,
            rollForward,
            sdks,
            workloads,
            error ?? (sdksOk ? null : "dotnet --list-sdks failed: " + sdksOutput),
            SdkWarning);
    }

    public void SetGlobalJson(SetGlobalJsonParams parameters)
    {
        var path = FindGlobalJson(SolutionDirectory) ?? System.IO.Path.Combine(SolutionDirectory, "global.json");
        var root = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? []
            : [];
        if (parameters.Version is null)
        {
            root.Remove("sdk");
        }
        else
        {
            var sdk = root["sdk"] as JsonObject ?? [];
            sdk["version"] = parameters.Version;
            if (parameters.RollForward is { Length: > 0 } rollForward)
            {
                sdk["rollForward"] = rollForward;
            }
            else
            {
                sdk.Remove("rollForward");
            }

            root["sdk"] = sdk;
        }

        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
    }

    internal static string? FindGlobalJson(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "global.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary><c>dotnet --list-sdks</c>: "10.0.401 [C:\Program Files\dotnet\sdk]" lines.</summary>
    internal static DotnetSdk[] ParseSdks(string output) =>
        [.. output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(' ', 2))
            .Where(p => p.Length == 2 && p[1].StartsWith('[') && p[1].EndsWith(']'))
            .Select(p => new DotnetSdk(p[0], System.IO.Path.Combine(p[1][1..^1], p[0])))];

    /// <summary><c>dotnet workload list</c>: a header, a dashed rule, then "id  version  source" rows.</summary>
    internal static DotnetWorkload[] ParseWorkloads(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.TrimEntries);
        var rule = Array.FindIndex(lines, l => l.StartsWith("---", StringComparison.Ordinal));
        if (rule < 0)
        {
            return [];
        }

        return [.. lines.Skip(rule + 1)
            .TakeWhile(l => l.Length > 0)
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length >= 2)
            .Select(p => new DotnetWorkload(p[0], p[1], string.Join(' ', p.Skip(2))))];
    }

    private async Task<(bool Succeeded, string Output)> RunDotnetAsync(IEnumerable<string> args, CancellationToken cancellationToken)
    {
        try
        {
            var psi = DotnetProcess.StartInfo(_dotnet, SolutionDirectory, args);
            using var process = DotnetProcess.Start(psi) ?? throw new InvalidOperationException("dotnet did not start.");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            await DotnetProcess.WaitOrKillAsync(process, linked.Token).ConfigureAwait(false);
            return (process.ExitCode == 0, (await output.ConfigureAwait(false)) + (await error.ConfigureAwait(false)));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (false, ex.Message);
        }
    }
}
