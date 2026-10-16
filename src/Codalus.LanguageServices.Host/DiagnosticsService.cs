using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;
using Diagnostic = Codalus.LanguageServices.Protocol.Diagnostic;
using DiagnosticSeverity = Codalus.LanguageServices.Protocol.DiagnosticSeverity;

namespace Codalus.LanguageServices.Host;

/// <summary>
/// Live diagnostics (ADR 0005). Open documents: compiler and analyzer diagnostics, recomputed shortly after each
/// edit. The whole solution: a low-priority background pass compiles every project (and with the
/// <c>solution</c> analysis scope also runs analyzers) and publishes diagnostics of closed files, so the Error List
/// shows build-breaking errors everywhere. Every pass is cancelled by newer changes.
/// </summary>
internal sealed class DiagnosticsService : IDisposable
{
    public const string ScopeOpenDocuments = "openDocuments";
    public const string ScopeSolution = "solution";
    public const string ScopeCurrentProject = "currentProject";

    private static readonly TimeSpan s_documentDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>A newly opened document waits longer: its first highlighting and folding go first.</summary>
    private static readonly TimeSpan s_openedDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan s_backgroundDelay = TimeSpan.FromMilliseconds(1500);

    private readonly WorkspaceManager _workspace;
    private readonly AnalysisSolution _analysis;
    private readonly Func<PublishDiagnosticsParams, Task> _publish;
    private readonly Action<string> _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, CancellationTokenSource> _documentPasses = new(Paths.Comparer);
    private readonly Dictionary<string, Diagnostic[]> _background = new(Paths.Comparer);
    private readonly ConditionalWeakTable<Compilation, CompilationWithAnalyzers> _analyzers = new();
    private readonly ConditionalWeakTable<Compilation, CompilationWithAnalyzers> _projectAnalyzers = new();
    private readonly ConditionalWeakTable<Compilation, CompilationWithAnalyzers> _ideAnalyzers = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly MemoryTrimmer _trimmer = new();
    private CancellationTokenSource? _backgroundPass;

    public DiagnosticsService(WorkspaceManager workspace, AnalysisSolution analysis, Func<PublishDiagnosticsParams, Task> publish, Action<string> log)
    {
        _workspace = workspace;
        _analysis = analysis;
        _publish = publish;
        _log = log;
    }

    /// <summary>
    /// <see cref="ScopeOpenDocuments"/> (default), <see cref="ScopeCurrentProject"/> (the project at
    /// <see cref="AnalysisProject"/>) or <see cref="ScopeSolution"/>: where analyzers run.
    /// </summary>
    public string AnalysisScope { get; set; } = ScopeOpenDocuments;

    /// <summary>The project file whose analyzers run in every document with the <see cref="ScopeCurrentProject"/> scope.</summary>
    public string? AnalysisProject { get; set; }

    /// <summary>
    /// The scope one project is analyzed with: <see cref="ScopeSolution"/> for every project, or for the current one with
    /// <see cref="ScopeCurrentProject"/>; otherwise <see cref="ScopeOpenDocuments"/>. The background pass keeps a project's
    /// results by it, so switching the current project analyzes only the two projects involved again.
    /// </summary>
    internal static string ScopeOf(string scope, string? analysisProject, string? projectPath) =>
        scope == ScopeSolution || scope == ScopeCurrentProject && analysisProject is not null && projectPath is not null && Paths.Comparer.Equals(analysisProject, projectPath)
            ? ScopeSolution
            : ScopeOpenDocuments;

    /// <summary>
    /// Raised after a background pass over every project completes: (projects, documents, whether the solution had
    /// finished loading, so every project was in it).
    /// </summary>
    public event Action<int, int, bool>? BackgroundPassCompleted;

    // ------------------------------------------------------------------ open documents

    public void DocumentOpened(string path) => Schedule(path, s_openedDelay);

    public void DocumentChanged(string path) => Schedule(path, s_documentDelay);

    private void Schedule(string path, TimeSpan delay)
    {
        path = Path.GetFullPath(path);
        CancellationToken token;
        CancellationTokenSource? previous;
        lock (_gate)
        {
            _documentPasses.Remove(path, out previous);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _documentPasses[path] = cts;
            token = cts.Token;
        }

        Cancel(previous);

        _ = Task.Run(async () =>
        {
            Interlocked.Increment(ref _documentPassesRunning);
            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
                await AnalyzeOpenDocumentAsync(path, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log($"Diagnostics failed for {Path.GetFileName(path)}: {ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref _documentPassesRunning);
            }
        }, CancellationToken.None);
        ScheduleBackground();
    }

    /// <summary>A closed document shows what the background pass found for it (or nothing).</summary>
    public void DocumentClosed(string path)
    {
        path = Path.GetFullPath(path);
        CancellationTokenSource? previous;
        lock (_gate)
        {
            _documentPasses.Remove(path, out previous);
        }

        Cancel(previous);
        Diagnostic[] background;
        lock (_gate)
        {
            background = _background.GetValueOrDefault(path) ?? [];
        }

        _ = _publish(new PublishDiagnosticsParams(DocumentUris.FromPath(path), null, background));
    }

    private async Task AnalyzeOpenDocumentAsync(string path, CancellationToken cancellationToken)
    {
        // The analysis solution while the projects its project references haven't compiled (M34c).
        var version = _workspace.Version(path);
        var solution = await _analysis.ForDocumentAsync(path, cancellationToken).ConfigureAwait(false);
        if (_workspace.Version(path) != version)
        {
            return; // edited meanwhile: that edit scheduled its own pass
        }
        if (Razor.RazorFiles.IsRazor(path))
        {
            await AnalyzeRazorAsync(solution, path, version, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (await WorkspaceManager.FindDocumentAsync(solution, DocumentUris.FromPath(path), cancellationToken).ConfigureAwait(false) is not { } document)
        {
            return;
        }

        var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
        if (tree is not null && IsProvisional(document.Project))
        {
            // Its project hasn't loaded yet (Miscellaneous Files) or misses projects it references: only syntax is sure.
            await _publish(new PublishDiagnosticsParams(DocumentUris.FromPath(path), version, Convert(tree.GetDiagnostics(cancellationToken), tree))).ConfigureAwait(false);
            return;
        }

        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (tree is null || model is null)
        {
            return;
        }

        var diagnostics = await DiagnosticsInSpanAsync(document, null, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await _publish(new PublishDiagnosticsParams(DocumentUris.FromPath(path), version, Convert(diagnostics, tree))).ConfigureAwait(false);
        _trimmer.WorkDone();
    }

    /// <summary>A Razor file's diagnostics (ADR 0023): its generated document's, where they map into the file.</summary>
    private async Task AnalyzeRazorAsync(Solution solution, string path, int? version, CancellationToken cancellationToken)
    {
        if (await Razor.RazorMapping.CreateAsync(solution, path, cancellationToken).ConfigureAwait(false) is not { } mapping)
        {
            return;
        }

        if (IsProvisional(mapping.Generated.Project))
        {
            await _publish(new PublishDiagnosticsParams(DocumentUris.FromPath(path), version, [])).ConfigureAwait(false);
            return;
        }

        var diagnostics = await DiagnosticsInSpanAsync(mapping.Generated, null, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await _publish(new PublishDiagnosticsParams(DocumentUris.FromPath(path), version, ConvertRazor(diagnostics, path))).ConfigureAwait(false);
        _trimmer.WorkDone();
    }

    /// <summary>The diagnostics that map into the Razor file <paramref name="path"/>, with their Razor ranges.</summary>
    private static Diagnostic[] ConvertRazor(IEnumerable<RoslynDiagnostic> diagnostics, string path) =>
        [.. diagnostics
            .Where(d => !d.IsSuppressed && d.Location.IsInSource && d.Severity != RoslynSeverity.Hidden)
            .Select(d => (Diagnostic: d, Mapped: d.Location.GetMappedLineSpan()))
            .Where(d => d.Mapped.HasMappedPath && Paths.Comparer.Equals(Path.GetFullPath(d.Mapped.Path), Path.GetFullPath(path)))
            .DistinctBy(d => (d.Diagnostic.Id, d.Mapped.Span, d.Diagnostic.GetMessage(System.Globalization.CultureInfo.CurrentCulture)))
            .Select(d => Convert(d.Diagnostic) with { Range = Positions.ToRange(d.Mapped.Span) })];

    /// <summary>
    /// Compiler, project analyzer and (for documents up to <see cref="IdeAnalyzerLineLimit"/> lines) IDE analyzer
    /// diagnostics of a document, optionally only those in <paramref name="span"/> (code fixes).
    /// </summary>
    public async Task<IReadOnlyList<RoslynDiagnostic>> DiagnosticsInSpanAsync(Document document, Microsoft.CodeAnalysis.Text.TextSpan? span, CancellationToken cancellationToken)
    {
        var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (tree is null || model is null)
        {
            return [];
        }

        var diagnostics = model.GetDiagnostics(span, cancellationToken).Where(d => !BuildDivergence.IsCompilationReferenceArtifact(d, model.Compilation, cancellationToken)).ToList();
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var withIde = text.Lines.Count <= IdeAnalyzerLineLimit;
        var isMisc = document.Project.Id == _workspace.MiscellaneousProjectId;
        if (Analyzers(document.Project, model.Compilation, includeProject: !isMisc, includeIde: withIde) is { } analyzers)
        {
            diagnostics.AddRange((await analyzers.GetAnalyzerSyntaxDiagnosticsAsync(tree, cancellationToken).ConfigureAwait(false))
                .Where(d => span is null || d.Location.SourceSpan.IntersectsWith(span.Value)));
            diagnostics.AddRange(await analyzers.GetAnalyzerSemanticDiagnosticsAsync(model, span, cancellationToken).ConfigureAwait(false));
        }

        return diagnostics;
    }

    /// <summary>Roslyn's IDE analyzers are skipped for very large files, which they would slow down a lot.</summary>
    public const int IdeAnalyzerLineLimit = 10_000;

    // ------------------------------------------------------------------ the whole solution

    /// <summary>
    /// Restarts the background pass after a quiet period: <paramref name="delay"/>, or 1.5 s, which lets a burst of edits
    /// settle (projects that finished loading need no such wait, M34c).
    /// </summary>
    public void ScheduleBackground(TimeSpan? delay = null)
    {
        CancellationToken token;
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _backgroundPass;
            _backgroundPass = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            token = _backgroundPass.Token;
        }

        Cancel(previous);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay ?? s_backgroundDelay, token).ConfigureAwait(false);
                await BackgroundPassAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log($"Solution diagnostics failed: {ex}"); // with the stack: a failure here is a bug to find
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// <paramref name="included"/> in dependency order (referenced projects first), the open documents' projects and
    /// everything they reference before the rest.
    /// </summary>
    private List<Project> Ordered(Solution solution, HashSet<ProjectId> included)
    {
        var graph = solution.GetProjectDependencyGraph();
        var urgent = new HashSet<ProjectId>();
        foreach (var id in _workspace.OpenPaths.SelectMany(p => solution.GetDocumentIdsWithFilePath(p)).Select(d => d.ProjectId).Distinct())
        {
            urgent.Add(id);
            urgent.UnionWith(graph.GetProjectsThatThisProjectTransitivelyDependsOn(id));
        }

        var sorted = graph.GetTopologicallySortedProjects().Where(included.Contains).ToList();
        return [.. sorted.Where(urgent.Contains).Concat(sorted.Where(id => !urgent.Contains(id))).Select(solution.GetProject).OfType<Project>()];
    }

    /// <summary>Whether a project's semantic diagnostics would be false alarms while the solution loads (M34b).</summary>
    private bool IsProvisional(Project project) =>
        _workspace.Loading && (project.Id == _workspace.MiscellaneousProjectId || _workspace.IsIncomplete(project.Id));

    /// <summary>
    /// What a background pass found in one project, kept while the project and everything it references are unchanged
    /// (its dependent version, references and the analysis scope), so the next pass skips it.
    /// </summary>
    private sealed record ProjectPass(
        VersionStamp Version,
        IReadOnlyList<MetadataReference> References,
        IReadOnlyList<AnalyzerReference> Analyzers,
        string Scope,
        int Documents,
        IReadOnlyList<(string Path, Diagnostic[] Items)> Found);

    private Dictionary<ProjectId, ProjectPass> _passes = [];

    /// <summary>
    /// The background pass, once the solution has loaded (M34b): every project's diagnostics, published for closed files.
    /// A project unchanged since the last pass keeps its diagnostics; so does one whose key matches what the last session
    /// stored (M34c, <see cref="LanguageStateCache"/>), without being compiled. The rest compile in parallel, dependencies
    /// first and the open documents' projects before the others, in the <see cref="AnalysisSolution"/> (against reference
    /// assemblies of unchanged projects) when the projects they reference haven't compiled. Then the projects the open
    /// documents reference compile in the workspace (<see cref="WarmUp"/>), for navigation.
    /// </summary>
    private async Task BackgroundPassAsync(CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var reused = 0;
        var persisted = 0;
        var solution = _workspace.CurrentSolution;
        var generation = _workspace.Generation;
        var loading = _workspace.Loading;
        if (loading)
        {
            // The whole solution waits until it has loaded (M34b): passes over the projects loaded so far would be
            // cancelled by the next update, and they take the processors the design-time builds and open documents need.
            BackgroundPassCompleted?.Invoke(_workspace.ProjectCount, 0, false);
            return;
        }

        var (requested, current) = (AnalysisScope, AnalysisProject);
        string ScopeFor(Project project) => ScopeOf(requested, current, project.FilePath);
        var included = solution.Projects
            .Where(p => p.Id != _workspace.MiscellaneousProjectId)
            .Select(p => p.Id)
            .ToHashSet();
        var projects = Ordered(solution, included);
        Dictionary<ProjectId, ProjectPass> previous;
        bool usePersisted;
        lock (_gate)
        {
            previous = _passes;
            usePersisted = LanguageStateCache.Enabled && _persistedGeneration != generation;
        }

        // Keys only on the first pass of a set of projects: later passes follow edits, and the next session finds a
        // project edited since then changed anyway.
        var keys = usePersisted ? _workspace.Keys() : null;
        var hasDependents = solution.Projects.SelectMany(p => p.ProjectReferences).Select(r => r.ProjectId).ToHashSet();
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2), CancellationToken = cancellationToken };
        var results = new System.Collections.Concurrent.ConcurrentDictionary<ProjectId, ProjectPass>();
        var versions = new System.Collections.Concurrent.ConcurrentDictionary<ProjectId, VersionStamp>();
        await Parallel.ForEachAsync(projects, parallel, async (project, token) =>
        {
            var version = versions[project.Id] = await project.GetDependentVersionAsync(token).ConfigureAwait(false);
            var scope = ScopeFor(project);
            if (previous.TryGetValue(project.Id, out var cached) && cached.Version == version && cached.Scope == scope
                && cached.References.SequenceEqual(project.MetadataReferences) && cached.Analyzers.SequenceEqual(project.AnalyzerReferences))
            {
                results[project.Id] = cached; // unchanged since the last pass, with everything it references
                Interlocked.Increment(ref reused);
                return;
            }

            if (keys?.Key(project.Id) is { } key && LanguageStateCache.Folder(keys.Project(project.Id)?.Parsed) is { } folder
                && LanguageStateCache.Read(folder) is { } entry && entry.Key == key && entry.Scope == scope)
            {
                // Unchanged since the last session (M34c): its diagnostics without compiling it.
                results[project.Id] = new ProjectPass(version, [.. project.MetadataReferences], [.. project.AnalyzerReferences], scope, entry.Documents,
                    [.. entry.Files.Where(f => File.Exists(f.Path)).Select(f => (f.Path, f.Items))]);
                Interlocked.Increment(ref persisted);
            }
        }).ConfigureAwait(false);

        // The rest compile: in the analysis solution when what they reference hasn't compiled in the workspace.
        var compile = projects.Where(p => !results.ContainsKey(p.Id)).Select(p => p.Id).ToList();
        var analysis = solution;
        if (compile.Any(id => !AnalysisSolution.IsLive(solution, id)) && await _analysis.GetAsync(cancellationToken).ConfigureAwait(false) is { } forked)
        {
            analysis = forked;
        }

        var timings = new System.Collections.Concurrent.ConcurrentBag<(string Project, double Milliseconds)>();
        var toCompile = compile.Select(analysis.GetProject).OfType<Project>().ToList();
        await Parallel.ForEachAsync(toCompile, parallel, async (project, token) =>
        {
            var compiling = System.Diagnostics.Stopwatch.GetTimestamp();
            await project.GetCompilationAsync(token).ConfigureAwait(false);
            timings.Add((project.Name, System.Diagnostics.Stopwatch.GetElapsedTime(compiling).TotalMilliseconds));
        }).ConfigureAwait(false);

        await Parallel.ForEachAsync(toCompile, parallel, async (project, token) =>
        {
            var version = versions[project.Id];
            if (await project.GetCompilationAsync(token).ConfigureAwait(false) is not { } compilation)
            {
                return;
            }

            var scope = ScopeFor(project);
            var diagnostics = compilation.GetDiagnostics(token).Where(d => !BuildDivergence.IsCompilationReferenceArtifact(d, compilation, token)).ToList();
            if (scope == ScopeSolution && Analyzers(project, compilation, includeProject: true, includeIde: false) is { } analyzers)
            {
                diagnostics.AddRange(await analyzers.GetAnalyzerDiagnosticsAsync(token).ConfigureAwait(false));
            }

            var found = new List<(string, Diagnostic[])>();
            foreach (var group in diagnostics.Where(d => d.Location.IsInSource).GroupBy(d => d.Location.SourceTree!))
            {
                if (group.Key.FilePath is { Length: > 0 } path && File.Exists(path))
                {
                    found.Add((path, Convert(group, group.Key)));
                }
                else if (Razor.RazorLocations.IsRazorGenerated(group.Key.FilePath) && Razor.RazorLocations.SourceFile(group.Key) is { } razor && File.Exists(razor))
                {
                    found.Add((razor, ConvertRazor(group, razor)));
                }
            }

            results[project.Id] = new ProjectPass(version, [.. project.MetadataReferences], [.. project.AnalyzerReferences], scope, project.DocumentIds.Count, found);
            if (keys?.Key(project.Id) is { } key && LanguageStateCache.Folder(keys.Project(project.Id)?.Parsed) is { } folder)
            {
                // For the next session (M34c): the diagnostics and, for a project others reference, its reference assembly.
                var reference = hasDependents.Contains(project.Id) && LanguageStateCache.WriteReference(folder, compilation, token);
                LanguageStateCache.Write(folder, new LanguageStateCache.Entry(LanguageStateCache.Schema, key, scope, project.DocumentIds.Count,
                    [.. found.Select(f => new LanguageStateCache.FileDiagnostics(f.Item1, f.Item2))]) { ReferenceKey = reference ? key : null });
            }
        }).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        var foundFiles = new Dictionary<string, Diagnostic[]>(Paths.Comparer);
        foreach (var (path, items) in results.Values.SelectMany(r => r.Found))
        {
            foundFiles[path] = [.. (foundFiles.GetValueOrDefault(path) ?? []).Concat(items)];
        }

        var documents = results.Values.Sum(r => r.Documents);
        var compiled = results.Count;
        if (System.Diagnostics.Stopwatch.GetElapsedTime(started) is { TotalSeconds: > 5 } || usePersisted)
        {
            _log($"Solution diagnostics: {compiled} project(s) in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms ({reused} unchanged since the last pass, "
                 + $"{persisted} since the last session, {toCompile.Count} compiled{(ReferenceEquals(analysis, solution) ? string.Empty : $", {_analysis.Images.Count} as reference assemblies")})."
                 + (timings.IsEmpty ? string.Empty : " Slowest: " + string.Join(", ", timings.OrderByDescending(t => t.Milliseconds).Take(5).Select(t => $"{t.Project} {t.Milliseconds:0} ms")) + "."));
        }

        lock (_gate)
        {
            _passes = new Dictionary<ProjectId, ProjectPass>(results);
            if (usePersisted)
            {
                _persistedGeneration = generation;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        List<string> changed;
        lock (_gate)
        {
            // Only files whose diagnostics changed: unchanged projects keep the same diagnostics (the same instances).
            changed = [.. _background.Keys.Union(foundFiles.Keys, Paths.Comparer)
                .Where(p => !(_background.TryGetValue(p, out var before) && foundFiles.TryGetValue(p, out var after) && before.SequenceEqual(after)))];
            _background.Clear();
            foreach (var (path, diagnostics) in foundFiles)
            {
                _background[path] = diagnostics;
            }
        }

        foreach (var path in changed.Where(p => !_workspace.IsOpen(p)))
        {
            await _publish(new PublishDiagnosticsParams(DocumentUris.FromPath(path), null, foundFiles.GetValueOrDefault(path) ?? [])).ConfigureAwait(false);
        }

        BackgroundPassCompleted?.Invoke(compiled, documents, true);
        _trimmer.WorkDone();
        if (GeneratorTimings.Enabled)
        {
            await GeneratorTimings.LogAsync(toCompile, _log, cancellationToken).ConfigureAwait(false);
        }

        WarmUp(generation);
    }

    private int _persistedGeneration = -1;
    private int _documentPassesRunning;
    private int _warmUpGeneration = -1;

    /// <summary>
    /// Compiles, in the workspace, the projects the open documents' projects reference (M34c), so navigation and the
    /// open documents themselves move from the analysis solution to the workspace's sources. Edits don't cancel it: the
    /// projects it compiles don't change with them. A change of projects does.
    /// </summary>
    private void WarmUp(int generation)
    {
        lock (_gate)
        {
            if (_warmUpGeneration == generation)
            {
                return;
            }

            _warmUpGeneration = generation;
        }

        var token = _lifetime.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                // After the open documents' own diagnostics: they need the processors first.
                while (Volatile.Read(ref _documentPassesRunning) > 0 && _workspace.Generation == generation)
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                }

                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var solution = _workspace.CurrentSolution;
                var graph = solution.GetProjectDependencyGraph();
                var open = _workspace.OpenPaths.SelectMany(p => solution.GetDocumentIdsWithFilePath(p)).Select(d => d.ProjectId)
                    .Where(id => id != _workspace.MiscellaneousProjectId).ToHashSet();
                var needed = open.SelectMany(graph.GetProjectsThatThisProjectTransitivelyDependsOn).ToHashSet();
                var ordered = graph.GetTopologicallySortedProjects().Where(needed.Contains).Select(solution.GetProject).OfType<Project>().Where(p => !p.TryGetCompilation(out _)).ToList();
                if (ordered.Count == 0)
                {
                    _analysis.Release();
                    return;
                }

                // Low priority: a quarter of the processors, behind the open documents' own work.
                var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 4), CancellationToken = token };
                await Parallel.ForEachAsync(ordered, parallel, async (project, ct) =>
                {
                    if (_workspace.Generation != generation)
                    {
                        return;
                    }

                    await project.GetCompilationAsync(ct).ConfigureAwait(false);
                }).ConfigureAwait(false);
                if (_workspace.Generation == generation)
                {
                    _analysis.Release();
                    _log($"Workspace compilations of {ordered.Count} project(s) the open documents reference in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms.");
                    _trimmer.WorkDone();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log($"Compiling the open documents' references failed: {ex.Message}");
            }
        }, CancellationToken.None);
    }

    // ------------------------------------------------------------------ helpers

    private CompilationWithAnalyzers? Analyzers(Project project, Compilation compilation, bool includeProject, bool includeIde)
    {
        var analyzers = (includeProject ? project.AnalyzerReferences.SelectMany(r => r.GetAnalyzers(project.Language)) : [])
            .Concat(includeIde ? Features.ProviderCatalog.IdeAnalyzers(project.Language) : [])
            .ToImmutableArray();
        if (analyzers.IsEmpty)
        {
            return null;
        }

        var table = (includeProject, includeIde) switch
        {
            (true, true) => _analyzers,
            (true, false) => _projectAnalyzers,
            _ => _ideAnalyzers,
        };
        return table.GetValue(compilation, c => c.WithAnalyzers(analyzers, new CompilationWithAnalyzersOptions(
            project.AnalyzerOptions,
            onAnalyzerException: (ex, analyzer, _) => _log($"Analyzer {analyzer.GetType().Name} failed: {ex.Message}"),
            concurrentAnalysis: true,
            logAnalyzerExecutionTime: false,
            reportSuppressedDiagnostics: false)));
    }

    private static Diagnostic[] Convert(IEnumerable<RoslynDiagnostic> diagnostics, SyntaxTree tree) =>
        [.. diagnostics
            .Where(d => !d.IsSuppressed && d.Location.IsInSource && d.Location.SourceTree == tree)
            .Where(d => d.Severity != RoslynSeverity.Hidden || d.Descriptor.CustomTags.Contains(WellKnownDiagnosticTags.Unnecessary))
            .DistinctBy(d => (d.Id, d.Location.SourceSpan, d.GetMessage(System.Globalization.CultureInfo.CurrentCulture)))
            .Select(Convert)];

    private static Diagnostic Convert(RoslynDiagnostic diagnostic)
    {
        var tags = new List<int>();
        if (diagnostic.Descriptor.CustomTags.Contains(WellKnownDiagnosticTags.Unnecessary))
        {
            tags.Add(DiagnosticTag.Unnecessary);
        }

        if (diagnostic.Id is "CS0612" or "CS0618" or "CS0619")
        {
            tags.Add(DiagnosticTag.Deprecated);
        }

        var help = diagnostic.Descriptor.HelpLinkUri;
        return new Diagnostic(
            Positions.ToRange(diagnostic.Location.GetLineSpan().Span),
            diagnostic.Severity switch
            {
                RoslynSeverity.Error => DiagnosticSeverity.Error,
                RoslynSeverity.Warning => DiagnosticSeverity.Warning,
                RoslynSeverity.Info => DiagnosticSeverity.Information,
                _ => DiagnosticSeverity.Hint,
            },
            diagnostic.Id,
            diagnostic.Descriptor.Category == "Compiler" ? "csharp" : "analyzer",
            diagnostic.GetMessage(System.Globalization.CultureInfo.CurrentCulture))
        {
            CodeDescription = string.IsNullOrEmpty(help) ? null : new CodeDescription(help),
            Tags = tags.Count == 0 ? null : [.. tags],
        };
    }

    /// <summary>
    /// Cancels and disposes a pass's token source, on the thread pool: Cancel runs the pass's callbacks and
    /// continuations synchronously, which must not hold up the caller (for edits, the connection's dispatch loop) or run
    /// under <see cref="_gate"/>.
    /// </summary>
    private static void Cancel(CancellationTokenSource? source)
    {
        if (source is null)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            source.Dispose();
        });
    }

    public void Dispose()
    {
        _trimmer.Dispose();
        _lifetime.Cancel();
        lock (_gate)
        {
            foreach (var pass in _documentPasses.Values)
            {
                pass.Dispose();
            }

            _documentPasses.Clear();
            _backgroundPass?.Dispose();
        }

        _lifetime.Dispose();
    }
}
