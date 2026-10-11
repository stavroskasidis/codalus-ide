using System.Collections.Immutable;
using System.Reflection;
using DaedalusStudio.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;

namespace DaedalusStudio.LanguageServices.Host;

/// <summary>A Roslyn workspace whose solution is replaced wholesale by <see cref="WorkspaceManager"/>.</summary>
internal sealed class DaedalusWorkspace(HostServices host) : Workspace(host, "Daedalus")
{
    public void Replace(Solution solution) => SetCurrentSolution(solution);
}

/// <summary>
/// Owns the Roslyn solution (ADR 0005). Projects come from design-time builds (<see cref="ProjectData"/>) and are
/// updated as deltas, so unchanged projects keep their compilations. Open documents are authoritative: their text
/// comes from the editor. Files that belong to no loaded project go to a "Miscellaneous Files" project with
/// framework references only. Closed files are reloaded when they change on disk.
/// </summary>
internal sealed class WorkspaceManager : IDisposable
{
    private static readonly Lazy<HostServices> s_host = new(() => MefHostServices.Create(
        MefHostServices.DefaultAssemblies
            .Concat([typeof(Microsoft.CodeAnalysis.Completion.CompletionService).Assembly, Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features")])
            .Distinct()));

    private static readonly Lazy<MetadataReference[]> s_frameworkReferences = new(() =>
        [.. ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
            .AsParallel()
            .AsOrdered()
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))]);

    private readonly Lock _gate = new();
    private readonly DaedalusWorkspace _workspace;
    private readonly ProjectLoader _loader = new();
    private readonly Dictionary<string, ProjectEntry> _projects = new(Paths.Comparer);
    private readonly Dictionary<string, OpenDocument> _open = new(Paths.Comparer);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(Paths.Comparer);
    private readonly HashSet<string> _changedFiles = new(Paths.Comparer);
    private readonly Timer _reloadTimer;
    private readonly ProjectId _miscId = ProjectId.CreateNewId("Miscellaneous Files");

    public WorkspaceManager()
    {
        // Independent and both slow on a cold start: the framework references (Miscellaneous Files) and the MEF
        // composition of Roslyn's services. Load them in parallel.
        var references = Task.Run(() => s_frameworkReferences.Value);
        _workspace = new DaedalusWorkspace(s_host.Value);
        _loader.Log += (_, message) => Log?.Invoke(this, message);
        _reloadTimer = new Timer(_ => ReloadChangedFiles());
        _workspace.Replace(_workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            _miscId,
            VersionStamp.Create(),
            name: "Miscellaneous Files",
            assemblyName: "MiscellaneousFiles",
            language: LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable),
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview),
            metadataReferences: references.Result)));
    }

    public Solution CurrentSolution => _workspace.CurrentSolution;

    public ProjectId MiscellaneousProjectId => _miscId;

    public int ProjectCount
    {
        get
        {
            lock (_gate)
            {
                return _projects.Count;
            }
        }
    }

    /// <summary>The solution changed: projects were added, updated or removed (not raised for document edits).</summary>
    public event EventHandler? ProjectsChanged;

    /// <summary>A closed document changed on disk.</summary>
    public event EventHandler<string>? FileReloaded;

    public event EventHandler<string>? Log;

    private sealed class ProjectEntry(ProjectId id)
    {
        public ProjectId Id { get; } = id;

        public ProjectLoader.Parsed? Parsed { get; set; }

        public IReadOnlyList<MetadataReference> AppliedMetadata { get; set; } = [];

        public IReadOnlyList<ProjectReference> AppliedProjects { get; set; } = [];
    }

    private sealed record OpenDocument(int Version, SourceText Text, DocumentId? MiscId);

    // ------------------------------------------------------------------ projects

    public void UpdateProjects(IReadOnlyList<ProjectData> projects)
    {
        lock (_gate)
        {
            var solution = _workspace.CurrentSolution;
            foreach (var data in projects)
            {
                var path = Path.GetFullPath(data.Path);
                var parsed = _loader.Parse(data with { Path = path });
                if (parsed is null)
                {
                    continue;
                }

                if (!_projects.TryGetValue(path, out var entry))
                {
                    entry = new ProjectEntry(ProjectId.CreateNewId(data.Name));
                    _projects[path] = entry;
                }

                solution = Apply(solution, entry, parsed);
                entry.Parsed = parsed;
                Watch(Path.GetDirectoryName(path)!);
            }

            solution = ApplyReferences(solution);
            solution = ReconcileOpenDocuments(solution);
            _workspace.Replace(solution);
        }

        ProjectsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveProjects(IReadOnlyList<string> paths)
    {
        lock (_gate)
        {
            var solution = _workspace.CurrentSolution;
            foreach (var path in paths.Select(Path.GetFullPath))
            {
                if (_projects.Remove(path, out var entry))
                {
                    // Other projects drop their references to it first.
                    foreach (var dependent in solution.Projects.Where(p => p.ProjectReferences.Any(r => r.ProjectId == entry.Id)).ToList())
                    {
                        solution = solution.WithProjectReferences(dependent.Id, dependent.ProjectReferences.Where(r => r.ProjectId != entry.Id));
                    }

                    solution = solution.RemoveProject(entry.Id);
                }
            }

            foreach (var entry in _projects.Values)
            {
                entry.AppliedProjects = [.. entry.AppliedProjects.Where(r => solution.ContainsProject(r.ProjectId))];
            }

            solution = ApplyReferences(solution);
            solution = ReconcileOpenDocuments(solution);
            _workspace.Replace(solution);
        }

        ProjectsChanged?.Invoke(this, EventArgs.Empty);
    }

    private Solution Apply(Solution solution, ProjectEntry entry, ProjectLoader.Parsed parsed)
    {
        var id = entry.Id;
        var data = parsed.Data;
        if (solution.GetProject(id) is not { } project)
        {
            return solution.AddProject(ProjectInfo.Create(
                    id,
                    VersionStamp.Create(),
                    data.Name,
                    parsed.AssemblyName,
                    LanguageNames.CSharp,
                    filePath: data.Path,
                    outputFilePath: data.OutputPath,
                    compilationOptions: parsed.CompilationOptions,
                    parseOptions: parsed.ParseOptions,
                    documents: parsed.Sources.Select(s => SourceDocument(id, s.Path, s.Folders)),
                    analyzerReferences: parsed.Analyzers,
                    additionalDocuments: parsed.AdditionalFiles.Select(f => FileDocument(id, f)),
                    outputRefFilePath: data.OutputRefPath)
                .WithAnalyzerConfigDocuments(parsed.AnalyzerConfigFiles.Select(f => FileDocument(id, f))));
        }

        if (!Equals(project.CompilationOptions, parsed.CompilationOptions))
        {
            solution = solution.WithProjectCompilationOptions(id, parsed.CompilationOptions);
        }

        if (!Equals(project.ParseOptions, parsed.ParseOptions))
        {
            solution = solution.WithProjectParseOptions(id, parsed.ParseOptions);
        }

        if (project.AssemblyName != parsed.AssemblyName)
        {
            solution = solution.WithProjectAssemblyName(id, parsed.AssemblyName);
        }

        if (project.Name != data.Name)
        {
            solution = solution.WithProjectName(id, data.Name);
        }

        if (project.OutputFilePath != data.OutputPath)
        {
            solution = solution.WithProjectOutputFilePath(id, data.OutputPath);
        }

        if (project.OutputRefFilePath != data.OutputRefPath)
        {
            solution = solution.WithProjectOutputRefFilePath(id, data.OutputRefPath);
        }

        if (!project.AnalyzerReferences.SequenceEqual(parsed.Analyzers))
        {
            solution = solution.WithProjectAnalyzerReferences(id, parsed.Analyzers);
        }

        // Documents, additional files and .editorconfig files: add and remove by path.
        var sources = parsed.Sources.ToDictionary(s => s.Path, Paths.Comparer);
        solution = solution.RemoveDocuments([.. project.Documents.Where(d => d.FilePath is null || !sources.ContainsKey(d.FilePath)).Select(d => d.Id)]);
        var existing = project.Documents.Select(d => d.FilePath).OfType<string>().ToHashSet(Paths.Comparer);
        solution = solution.AddDocuments([.. parsed.Sources.Where(s => !existing.Contains(s.Path)).Select(s => SourceDocument(id, s.Path, s.Folders))]);

        var additional = parsed.AdditionalFiles.ToHashSet(Paths.Comparer);
        solution = solution.RemoveAdditionalDocuments([.. project.AdditionalDocuments.Where(d => d.FilePath is null || !additional.Contains(d.FilePath)).Select(d => d.Id)]);
        var existingAdditional = project.AdditionalDocuments.Select(d => d.FilePath).OfType<string>().ToHashSet(Paths.Comparer);
        solution = solution.AddAdditionalDocuments([.. parsed.AdditionalFiles.Where(f => !existingAdditional.Contains(f)).Select(f => FileDocument(id, f))]);

        var configs = parsed.AnalyzerConfigFiles.ToHashSet(Paths.Comparer);
        solution = solution.RemoveAnalyzerConfigDocuments([.. project.AnalyzerConfigDocuments.Where(d => d.FilePath is null || !configs.Contains(d.FilePath)).Select(d => d.Id)]);
        var existingConfigs = project.AnalyzerConfigDocuments.Select(d => d.FilePath).OfType<string>().ToHashSet(Paths.Comparer);
        return solution.AddAnalyzerConfigDocuments([.. parsed.AnalyzerConfigFiles.Where(f => !existingConfigs.Contains(f)).Select(f => FileDocument(id, f))]);
    }

    /// <summary>
    /// Metadata and project references of every project. A reference to another loaded project's output (or reference
    /// assembly) becomes a project reference; everything else is a file reference. Applied only where it changed.
    /// </summary>
    private Solution ApplyReferences(Solution solution)
    {
        var outputs = new Dictionary<string, ProjectId>(Paths.Comparer);
        foreach (var entry in _projects.Values.Where(e => e.Parsed is not null))
        {
            if (entry.Parsed!.Data.OutputPath is { } output)
            {
                outputs[output] = entry.Id;
            }

            if (entry.Parsed.Data.OutputRefPath is { } refOutput)
            {
                outputs[refOutput] = entry.Id;
            }
        }

        foreach (var entry in _projects.Values.Where(e => e.Parsed is not null))
        {
            var metadata = new List<MetadataReference>();
            var projects = new List<ProjectReference>();

            // Referenced projects by assembly name: the reference may be the output of a target framework other than
            // the one loaded for that project (only the active framework is loaded, ADR 0005).
            var referencedAssemblies = new Dictionary<string, ProjectId>(StringComparer.OrdinalIgnoreCase);
            foreach (var referenced in entry.Parsed!.Data.ProjectReferences ?? [])
            {
                if (_projects.TryGetValue(referenced, out var target) && target.Parsed is { } parsed)
                {
                    referencedAssemblies.TryAdd(parsed.AssemblyName, target.Id);
                }
            }

            foreach (var (path, properties) in entry.Parsed.References)
            {
                if ((outputs.TryGetValue(path, out var target) || referencedAssemblies.TryGetValue(Path.GetFileNameWithoutExtension(path), out target))
                    && target != entry.Id)
                {
                    if (projects.All(p => p.ProjectId != target))
                    {
                        projects.Add(new ProjectReference(target, properties.Aliases, properties.EmbedInteropTypes));
                    }
                }
                else if (_loader.Metadata(path, properties) is { } reference)
                {
                    metadata.Add(reference);
                }
            }

            if (!metadata.SequenceEqual(entry.AppliedMetadata))
            {
                solution = solution.WithProjectMetadataReferences(entry.Id, metadata);
                entry.AppliedMetadata = metadata;
            }

            if (!projects.SequenceEqual(entry.AppliedProjects))
            {
                solution = solution.WithProjectReferences(entry.Id, projects);
                entry.AppliedProjects = projects;
            }
        }

        return solution;
    }

    private DocumentInfo SourceDocument(ProjectId project, string path, IReadOnlyList<string> folders) =>
        DocumentInfo.Create(DocumentId.CreateNewId(project, path), Path.GetFileName(path), folders, SourceCodeKind.Regular, Loader(path), path);

    private DocumentInfo FileDocument(ProjectId project, string path) =>
        DocumentInfo.Create(DocumentId.CreateNewId(project, path), Path.GetFileName(path), loader: Loader(path), filePath: path);

    private TextLoader Loader(string path) =>
        _open.TryGetValue(path, out var open)
            ? TextLoader.From(TextAndVersion.Create(open.Text, VersionStamp.Create(), path))
            : new FileTextLoader(path, defaultEncoding: null);

    /// <summary>Open documents move between their projects and Miscellaneous Files as projects come and go.</summary>
    private Solution ReconcileOpenDocuments(Solution solution)
    {
        foreach (var (path, open) in _open.ToList())
        {
            if (Razor.RazorFiles.IsRazor(path))
            {
                continue; // additional documents, loaded with the open text; never in Miscellaneous Files
            }

            var ids = ProjectDocumentIds(solution, path);
            if (ids.Length > 0 && open.MiscId is { } miscId)
            {
                solution = solution.RemoveDocument(miscId).WithDocumentText(ids, open.Text, PreservationMode.PreserveIdentity);
                _open[path] = open with { MiscId = null };
            }
            else if (ids.Length == 0 && open.MiscId is null)
            {
                var id = DocumentId.CreateNewId(_miscId, path);
                solution = solution.AddDocument(id, Path.GetFileName(path), open.Text, filePath: path);
                _open[path] = open with { MiscId = id };
            }
        }

        return solution;
    }

    private ImmutableArray<DocumentId> ProjectDocumentIds(Solution solution, string path) =>
        [.. solution.GetDocumentIdsWithFilePath(path).Where(id => id.ProjectId != _miscId && solution.ContainsDocument(id))];

    /// <summary>A Razor file's additional documents (ADR 0023): its text drives the Razor source generator.</summary>
    private static Solution WithRazorText(Solution solution, string path, SourceText text, bool fromDisk = false)
    {
        foreach (var id in solution.GetDocumentIdsWithFilePath(path).Where(solution.ContainsAdditionalDocument))
        {
            solution = fromDisk
                ? solution.WithAdditionalDocumentTextLoader(id, new FileTextLoader(path, defaultEncoding: null), PreservationMode.PreserveValue)
                : solution.WithAdditionalDocumentText(id, text, PreservationMode.PreserveIdentity);
        }

        return solution;
    }

    // ------------------------------------------------------------------ open documents

    public void Open(string path, string text, int version)
    {
        path = Path.GetFullPath(path);
        var sourceText = SourceText.From(text);
        lock (_gate)
        {
            var solution = _workspace.CurrentSolution;
            if (Razor.RazorFiles.IsRazor(path))
            {
                _open[path] = new OpenDocument(version, sourceText, null);
                _workspace.Replace(WithRazorText(solution, path, sourceText));
                return;
            }

            var ids = ProjectDocumentIds(solution, path);
            DocumentId? miscId = null;
            if (ids.Length > 0)
            {
                solution = solution.WithDocumentText(ids, sourceText, PreservationMode.PreserveIdentity);
            }
            else
            {
                miscId = DocumentId.CreateNewId(_miscId, path);
                solution = solution.AddDocument(miscId, Path.GetFileName(path), sourceText, filePath: path);
            }

            _open[path] = new OpenDocument(version, sourceText, miscId);
            _workspace.Replace(solution);
        }
    }

    /// <summary>Applies LSP content changes in order. Returns false when the document is not open.</summary>
    public bool Change(string path, int version, IReadOnlyList<TextDocumentContentChangeEvent> changes)
    {
        path = Path.GetFullPath(path);
        lock (_gate)
        {
            if (!_open.TryGetValue(path, out var open))
            {
                return false;
            }

            var text = open.Text;
            foreach (var change in changes)
            {
                text = change.Range is { } range
                    ? text.WithChanges(new TextChange(Positions.ToSpan(text, range), change.Text))
                    : SourceText.From(change.Text);
            }

            var solution = _workspace.CurrentSolution;
            if (Razor.RazorFiles.IsRazor(path))
            {
                _workspace.Replace(WithRazorText(solution, path, text));
            }
            else
            {
                var ids = open.MiscId is { } miscId ? [miscId] : ProjectDocumentIds(solution, path);
                _workspace.Replace(solution.WithDocumentText(ids, text, PreservationMode.PreserveIdentity));
            }

            _open[path] = open with { Version = version, Text = text };
            return true;
        }
    }

    public void Close(string path)
    {
        path = Path.GetFullPath(path);
        lock (_gate)
        {
            if (!_open.Remove(path, out var open))
            {
                return;
            }

            var solution = _workspace.CurrentSolution;
            if (Razor.RazorFiles.IsRazor(path))
            {
                solution = WithRazorText(solution, path, open.Text, fromDisk: true);
            }
            else if (open.MiscId is { } miscId)
            {
                solution = solution.RemoveDocument(miscId);
            }
            else
            {
                // Back to the file on disk (the editor may close without saving).
                foreach (var id in ProjectDocumentIds(solution, path))
                {
                    solution = solution.WithDocumentTextLoader(id, new FileTextLoader(path, defaultEncoding: null), PreservationMode.PreserveValue);
                }
            }

            _workspace.Replace(solution);
        }
    }

    public bool IsOpen(string path)
    {
        lock (_gate)
        {
            return _open.ContainsKey(Path.GetFullPath(path));
        }
    }

    public int? Version(string path)
    {
        lock (_gate)
        {
            return _open.TryGetValue(Path.GetFullPath(path), out var open) ? open.Version : null;
        }
    }

    public IReadOnlyList<string> OpenPaths
    {
        get
        {
            lock (_gate)
            {
                return [.. _open.Keys];
            }
        }
    }

    // ------------------------------------------------------------------ lookup

    /// <summary>The document for a URI in <paramref name="solution"/>: a file (in a project or Miscellaneous Files) or a source-generated document.</summary>
    public static async ValueTask<Document?> FindDocumentAsync(Solution solution, string uri, CancellationToken cancellationToken)
    {
        if (DocumentUris.ToPath(uri) is { } path)
        {
            var ids = solution.GetDocumentIdsWithFilePath(Path.GetFullPath(path));
            return ids.IsEmpty ? null : solution.GetDocument(ids.FirstOrDefault(id => !IsMisc(solution, id)) ?? ids[0]);
        }

        if (DocumentUris.GeneratedPath(uri) is { } generatedPath)
        {
            foreach (var project in solution.Projects)
            {
                foreach (var generated in await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (Paths.Comparer.Equals(generated.FilePath, generatedPath))
                    {
                        return generated;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>The source-generated document of a syntax tree (its virtual path identifies it).</summary>
    public static async ValueTask<Document?> FindGeneratedAsync(Solution solution, SyntaxTree tree, CancellationToken cancellationToken)
    {
        foreach (var project in solution.Projects)
        {
            foreach (var generated in await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (generated.FilePath == tree.FilePath)
                {
                    return generated;
                }
            }
        }

        return null;
    }

    private static bool IsMisc(Solution solution, DocumentId id) => solution.GetProject(id.ProjectId)?.Name == "Miscellaneous Files";

    /// <summary>The LSP URI of a document (source-generated documents get a <c>daedalus-generated</c> URI).</summary>
    public static string UriOf(Document document) =>
        document is SourceGeneratedDocument ? DocumentUris.FromGeneratedPath(document.FilePath ?? document.Name) : DocumentUris.FromPath(document.FilePath!);

    // ------------------------------------------------------------------ files changed on disk

    private void Watch(string directory)
    {
        if (_watchers.ContainsKey(directory) || _watchers.Keys.Any(w => directory.StartsWith(w + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) || !Directory.Exists(directory))
        {
            return;
        }

        // One watcher for the projects of a solution folder (M33): each watcher is an inotify instance on Linux, whose
        // default limit is 128 for all of the user's processes, and a solution of 38 projects used 38 of them.
        if (_watchers.Keys.Select(w => SolutionFolderAbove(w, directory)).FirstOrDefault(s => s is not null) is { } solutionFolder)
        {
            foreach (var covered in _watchers.Keys.Where(w => Paths.Comparer.Equals(w, solutionFolder) || w.StartsWith(solutionFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _watchers[covered].Dispose();
                _watchers.Remove(covered);
            }

            directory = solutionFolder;
        }

        var watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        watcher.Changed += (_, e) => OnFileChanged(e.FullPath);
        watcher.Created += (_, e) => OnFileChanged(e.FullPath);
        watcher.Renamed += (_, e) => OnFileChanged(e.FullPath);
        watcher.EnableRaisingEvents = true;
        _watchers[directory] = watcher;
    }

    /// <summary>
    /// The folder of the solution that holds both <paramref name="a"/> and <paramref name="b"/>: their common ancestor,
    /// or the folder above it, when it has a solution file (never a root or the home folder). Null otherwise (separate
    /// watchers).
    /// </summary>
    internal static string? SolutionFolderAbove(string a, string b)
    {
        var home = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var common = CommonAncestor(a, b);
        for (var level = 0; common is not null && level <= 1; level++, common = Path.GetDirectoryName(common))
        {
            if ((Path.GetPathRoot(common) is { } root && Paths.Comparer.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(common)))
                || Paths.Comparer.Equals(home, Path.TrimEndingDirectorySeparator(common)))
            {
                return null;
            }

            try
            {
                if (Directory.EnumerateFiles(common, "*.sln*").Any(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
                {
                    return common;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    private static string? CommonAncestor(string a, string b)
    {
        for (var candidate = a; candidate is not null; candidate = Path.GetDirectoryName(candidate))
        {
            if (Paths.Comparer.Equals(candidate, b) || b.StartsWith(Path.TrimEndingDirectorySeparator(candidate) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private void OnFileChanged(string path)
    {
        lock (_changedFiles)
        {
            _changedFiles.Add(path);
        }

        _reloadTimer.Change(200, Timeout.Infinite);
    }

    private void ReloadChangedFiles()
    {
        string[] changed;
        lock (_changedFiles)
        {
            changed = [.. _changedFiles];
            _changedFiles.Clear();
        }

        var reloaded = new List<string>();
        lock (_gate)
        {
            var solution = _workspace.CurrentSolution;
            foreach (var path in changed.Where(p => !_open.ContainsKey(p)))
            {
                var ids = solution.GetDocumentIdsWithFilePath(path);
                if (ids.IsEmpty)
                {
                    continue;
                }

                var loader = new FileTextLoader(path, defaultEncoding: null);
                foreach (var id in ids)
                {
                    solution = solution.ContainsDocument(id) ? solution.WithDocumentTextLoader(id, loader, PreservationMode.PreserveValue)
                        : solution.ContainsAdditionalDocument(id) ? solution.WithAdditionalDocumentTextLoader(id, loader, PreservationMode.PreserveValue)
                        : solution.ContainsAnalyzerConfigDocument(id) ? solution.WithAnalyzerConfigDocumentTextLoader(id, loader, PreservationMode.PreserveValue)
                        : solution;
                }

                reloaded.Add(path);
            }

            _workspace.Replace(solution);
        }

        foreach (var path in reloaded)
        {
            FileReloaded?.Invoke(this, path);
        }
    }

    public void Dispose()
    {
        _reloadTimer.Dispose();
        lock (_gate)
        {
            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
        }

        _workspace.Dispose();
    }
}
