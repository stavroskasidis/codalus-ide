using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace DaedalusStudio.LanguageServices.Host;

/// <summary>
/// The solution that diagnostics and a document's own features use while the projects it references haven't compiled
/// (M34c, ADR 0049): the workspace's solution, with every reference to an unchanged project replaced by that project's
/// reference assembly, from <see cref="LanguageStateCache"/> (its key unchanged since the last session) or from the
/// project's last build (newer than every input). A project then compiles with its own sources and source generators
/// only, not the chain of projects below it (on a 96-project web solution, a chain of Razor projects of 8 to 14 s each).
/// Edits to open documents are carried over as they come, so its compilations stay incremental. Navigation and
/// refactoring keep the workspace's solution, whose symbols are the projects' sources.
/// </summary>
internal sealed class AnalysisSolution
{
    private readonly WorkspaceManager _workspace;
    private readonly Action<string> _log;
    private readonly Lock _gate = new();
    private State? _state;
    private Task<State?>? _building;
    private int _buildingGeneration = -1;

    public AnalysisSolution(WorkspaceManager workspace, Action<string> log)
    {
        _workspace = workspace;
        _log = log;
    }

    /// <summary>A reference assembly that stands for a project, and where it came from (<c>cache</c> or <c>build</c>).</summary>
    internal sealed record Image(MetadataReference Reference, string Origin);

    private static readonly IReadOnlyDictionary<ProjectId, Image> s_none = new Dictionary<ProjectId, Image>();

    private sealed record State(int Generation, ProjectKeys Keys, Dictionary<ProjectId, Image> Images, Solution Fork, HashSet<DocumentId> Synced);

    /// <summary>The projects that stand as reference assemblies in the current analysis solution (tests, logs).</summary>
    public IReadOnlyDictionary<ProjectId, Image> Images // the state's, read-only
    {
        get
        {
            lock (_gate)
            {
                return _state?.Images ?? s_none;
            }
        }
    }

    /// <summary>The keys of the snapshot the analysis solution was built from, when there is one.</summary>
    public ProjectKeys? Keys
    {
        get
        {
            lock (_gate)
            {
                return _state?.Keys;
            }
        }
    }

    /// <summary>
    /// The solution for a document's features: the workspace's once the projects its project references have compiled
    /// (or while the solution loads, or with the cache off), otherwise the analysis solution.
    /// </summary>
    public async ValueTask<Solution> ForDocumentAsync(string path, CancellationToken cancellationToken)
    {
        var current = _workspace.CurrentSolution;
        if (!LanguageStateCache.Enabled || _workspace.Loading)
        {
            return current;
        }

        var ids = current.GetDocumentIdsWithFilePath(path);
        if (ids.IsEmpty || ids.All(id => id.ProjectId == _workspace.MiscellaneousProjectId) || ids.Any(id => IsLive(current, id.ProjectId)))
        {
            return current;
        }

        return await GetAsync(cancellationToken).ConfigureAwait(false) ?? current;
    }

    /// <summary>Whether a project's compilation, or those of every project it references, are already there.</summary>
    public static bool IsLive(Solution solution, ProjectId id) =>
        solution.GetProject(id) is not { } project
        || project.TryGetCompilation(out _)
        || project.ProjectReferences.All(r => solution.GetProject(r.ProjectId) is not { } referenced || referenced.TryGetCompilation(out _));

    /// <summary>The analysis solution (built when the projects changed), with the open documents' current text; null when it is the workspace's.</summary>
    public async ValueTask<Solution?> GetAsync(CancellationToken cancellationToken)
    {
        if (!LanguageStateCache.Enabled || _workspace.Loading)
        {
            return null;
        }

        var generation = _workspace.Generation;
        Task<State?> building;
        lock (_gate)
        {
            if (_state is { } state && state.Generation == generation)
            {
                return state.Images.Count == 0 ? null : SyncLocked(state);
            }

            if (_building is null || _buildingGeneration != generation)
            {
                _buildingGeneration = generation;
                _building = Task.Run(() => Build(generation), CancellationToken.None);
            }

            building = _building;
        }

        var built = await building.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (built is not null && built.Generation == _workspace.Generation && (_state is null || _state.Generation != built.Generation))
            {
                _state = built;
            }

            return _state is { Images.Count: > 0 } current && current.Generation == _workspace.Generation ? SyncLocked(current) : null;
        }
    }

    /// <summary>
    /// An open document changed. When its project stands as a reference assembly, that assembly no longer matches it: the
    /// analysis solution is built again (the project then compiles from source).
    /// </summary>
    public void DocumentChanged(string path)
    {
        lock (_gate)
        {
            if (_state is { } state && _workspace.CurrentSolution.GetDocumentIdsWithFilePath(path).Any(id => state.Images.ContainsKey(id.ProjectId)))
            {
                _state = null;
                _building = null;
            }
        }
    }

    /// <summary>Drops the analysis solution and its compilations (the workspace's have caught up).</summary>
    public void Release()
    {
        lock (_gate)
        {
            if (_state is not null)
            {
                _state = _state with { Images = new Dictionary<ProjectId, Image>(), Fork = _workspace.CurrentSolution, Synced = new HashSet<DocumentId>() };
            }
        }
    }

    private State? Build(int generation)
    {
        try
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var solution = _workspace.CurrentSolution;
            var keys = _workspace.Keys();
            var referenced = solution.Projects.SelectMany(p => p.ProjectReferences).Select(r => r.ProjectId).ToHashSet();
            var images = new System.Collections.Concurrent.ConcurrentDictionary<ProjectId, Image>();
            Parallel.ForEach(referenced, id =>
            {
                if (keys.Project(id) is not { } project || LanguageStateCache.Folder(project.Parsed) is not { } folder)
                {
                    return;
                }

                if (keys.Key(id) is { } key && LanguageStateCache.Read(folder) is { } entry && entry.ReferenceKey == key
                    && LanguageStateCache.Reference(Path.Combine(folder, LanguageStateCache.ReferenceFile), Path.Combine(folder, LanguageStateCache.DocumentationFile)) is { } cached)
                {
                    images[id] = new Image(cached, "cache");
                }
                else if (BuiltReference(project.Parsed, keys) is { } built)
                {
                    images[id] = new Image(built, "build");
                }
            });

            var fork = solution;
            foreach (var project in solution.Projects)
            {
                var substituted = project.ProjectReferences.Where(r => images.ContainsKey(r.ProjectId)).ToList();
                if (substituted.Count == 0)
                {
                    continue;
                }

                fork = fork.WithProjectReferences(project.Id, project.ProjectReferences.Where(r => !images.ContainsKey(r.ProjectId)))
                    .AddMetadataReferences(project.Id, substituted.Select(r => images[r.ProjectId].Reference.WithAliases(r.Aliases).WithEmbedInteropTypes(r.EmbedInteropTypes)));
            }

            if (!images.IsEmpty)
            {
                _log($"Analysis solution: {images.Count} of {referenced.Count} referenced project(s) as reference assemblies ({images.Values.Count(i => i.Origin == "cache")} kept from the last session, "
                     + $"{images.Values.Count(i => i.Origin == "build")} from builds) in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms.");
            }

            return new State(generation, keys, new Dictionary<ProjectId, Image>(images), fork, new HashSet<DocumentId>());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log($"Analysis solution failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The project's last build's reference assembly (or assembly), when the build is newer than the project file and
    /// every source, additional and <c>.editorconfig</c> file, and none of them is open with other text.
    /// </summary>
    private static MetadataReference? BuiltReference(ProjectLoader.Parsed parsed, ProjectKeys keys)
    {
        if (parsed.IntermediateAssembly is not { } assembly)
        {
            return null;
        }

        try
        {
            var built = File.GetLastWriteTimeUtc(assembly);
            if (built.Year < 1700)
            {
                return null;
            }

            foreach (var input in parsed.Sources.Select(s => s.Path).Concat(parsed.AdditionalFiles).Concat(parsed.AnalyzerConfigFiles).Append(parsed.Data.Path))
            {
                if (File.GetLastWriteTimeUtc(input) > built || keys.IsOpenWithOtherText(input))
                {
                    return null;
                }
            }

            var reference = parsed.IntermediateRefAssembly is { } refAssembly && File.Exists(refAssembly) && File.GetLastWriteTimeUtc(refAssembly) >= built.AddMinutes(-1) ? refAssembly : assembly;
            return LanguageStateCache.Reference(reference, parsed.DocumentationFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Brings the analysis solution's open documents to the workspace's text (and closed ones back to their files).</summary>
    private Solution SyncLocked(State state)
    {
        var current = _workspace.CurrentSolution;
        var fork = state.Fork;
        var synced = new HashSet<DocumentId>();
        var candidates = _workspace.OpenPaths.SelectMany(p => current.GetDocumentIdsWithFilePath(p)).Concat(state.Synced).Distinct();
        foreach (var id in candidates)
        {
            if (current.GetDocument(id) is { } document && fork.ContainsDocument(id))
            {
                if (document.TryGetText(out var text) && _workspace.IsOpen(document.FilePath ?? string.Empty))
                {
                    if (!fork.GetDocument(id)!.TryGetText(out var forked) || !ReferenceEquals(forked, text))
                    {
                        fork = fork.WithDocumentText(id, text, PreservationMode.PreserveIdentity);
                    }

                    synced.Add(id);
                }
                else if (state.Synced.Contains(id) && document.FilePath is { } path)
                {
                    fork = fork.WithDocumentTextLoader(id, new FileTextLoader(path, defaultEncoding: null), PreservationMode.PreserveValue);
                }
            }
            else if (current.GetAdditionalDocument(id) is { } additional && fork.ContainsAdditionalDocument(id))
            {
                if (additional.TryGetText(out var text) && _workspace.IsOpen(additional.FilePath ?? string.Empty))
                {
                    if (!fork.GetAdditionalDocument(id)!.TryGetText(out var forked) || !ReferenceEquals(forked, text))
                    {
                        fork = fork.WithAdditionalDocumentText(id, text, PreservationMode.PreserveIdentity);
                    }

                    synced.Add(id);
                }
                else if (state.Synced.Contains(id) && additional.FilePath is { } path)
                {
                    fork = fork.WithAdditionalDocumentTextLoader(id, new FileTextLoader(path, defaultEncoding: null), PreservationMode.PreserveValue);
                }
            }
        }

        if (!ReferenceEquals(fork, state.Fork) || !synced.SetEquals(state.Synced))
        {
            _state = state with { Fork = fork, Synced = synced };
        }

        return fork;
    }
}
