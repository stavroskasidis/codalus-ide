using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Codalus.LanguageServices.Protocol;
using Codalus.Rpc;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;

namespace Codalus.LanguageServices.Host;

/// <summary>A Roslyn workspace whose solution is replaced wholesale by <see cref="WorkspaceManager"/>.</summary>
internal sealed class CodalusWorkspace(HostServices host) : Workspace(host, "Codalus")
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

    private static readonly Lazy<MetadataReference[]> s_frameworkReferences = new(() => FrameworkReferences(RuntimeEnvironment.GetRuntimeDirectory()));

    /// <summary>
    /// What Miscellaneous Files compiles against: the reference pack of the .NET in <paramref name="runtimeDirectory"/>
    /// (<see cref="ReferencePack"/>), as a project for it does, with the documentation next to each assembly. Else the
    /// host's own framework assemblies, which have no documentation: on a machine with the runtime only, and when the
    /// pack can't be read (an SDK update replacing it while the host starts, a broken link, a folder the user can't
    /// read), since the host must start anyway.
    /// </summary>
    internal static MetadataReference[] FrameworkReferences(string runtimeDirectory)
    {
        try
        {
            if (ReferencePack(runtimeDirectory) is { } pack
                && Open(Directory.GetFiles(pack, "*.dll").Order(StringComparer.Ordinal)) is var references
                && !references.Contains(null))
            {
                return [.. references.OfType<MetadataReference>()];
            }
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            // Its folders can't be listed: the runtime's assemblies, below.
        }

        // Files the runtime has loaded; one that still can't be opened is left out rather than failing the host.
        return [.. Open(((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll"))
            .OfType<MetadataReference>()];

        // In parallel (the cold start reads a few hundred files); null for a file that can't be read.
        static MetadataReference?[] Open(IEnumerable<string> paths) =>
            [.. paths.AsParallel().AsOrdered().Select(p =>
            {
                try
                {
                    return MetadataReference.CreateFromFile(p, documentation: DocumentationFiles.For(p, CultureInfo.CurrentUICulture));
                }
                catch (Exception ex) when (IsUnreadable(ex))
                {
                    return (MetadataReference?)null;
                }
            })];

        static bool IsUnreadable(Exception ex) => ex is IOException or UnauthorizedAccessException or BadImageFormatException;
    }

    /// <summary>
    /// The reference assemblies (<c>packs/Microsoft.NETCore.App.Ref/&lt;version&gt;/ref/net&lt;major.minor&gt;</c>) of the
    /// runtime in <paramref name="runtimeDirectory"/> (<c>shared/Microsoft.NETCore.App/&lt;version&gt;</c>) in the same
    /// .NET folder: those of its version, else the newest of its major version; null when there are none.
    /// </summary>
    internal static string? ReferencePack(string runtimeDirectory)
    {
        var runtime = new DirectoryInfo(Path.TrimEndingDirectorySeparator(runtimeDirectory));
        if (!FrameworkVersion.TryParse(runtime.Name, out var version)
            || runtime.Parent is not { Name: "Microsoft.NETCore.App", Parent: { Name: "shared", Parent: { } root } })
        {
            return null;
        }

        var packs = Path.Combine(root.FullName, "packs", "Microsoft.NETCore.App.Ref");
        if (!Directory.Exists(packs))
        {
            return null;
        }

        var candidates = Directory.EnumerateDirectories(packs)
            .Select(d => FrameworkVersion.TryParse(Path.GetFileName(d), out var v) ? v : null)
            .OfType<FrameworkVersion>()
            .Where(v => v.Major == version.Major)
            .OrderByDescending(v => v.Equals(version))
            .ThenByDescending(v => v);
        foreach (var candidate in candidates)
        {
            var folder = Path.Combine(packs, candidate.Text, "ref", $"net{candidate.Major}.{candidate.Minor}");
            if (File.Exists(Path.Combine(folder, "System.Runtime.dll")))
            {
                return folder;
            }
        }

        return null;
    }

    private readonly Lock _gate = new();
    private readonly CodalusWorkspace _workspace;
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
        _workspace = new CodalusWorkspace(s_host.Value);
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

    /// <summary>
    /// The solution is still loading (more design-time builds are coming, M34b). Projects whose referenced projects
    /// haven't arrived yet and documents in Miscellaneous Files then get syntax diagnostics only: their semantic errors
    /// would be false (the missing projects' types).
    /// </summary>
    public bool Loading { get; private set; }

    /// <summary>Sets <see cref="Loading"/>; returns whether it changed.</summary>
    public bool SetLoading(bool loading)
    {
        lock (_gate)
        {
            if (Loading == loading)
            {
                return false;
            }

            Loading = loading;
            return true;
        }
    }

    private ImmutableHashSet<ProjectId> _incomplete = [];

    /// <summary>
    /// Whether a project references a project that isn't loaded, directly or through the projects it references: its
    /// compilation lacks types it will have once loading completes.
    /// </summary>
    public bool IsIncomplete(ProjectId project) => _incomplete.Contains(project);

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

    private int _generation;

    /// <summary>Changes whenever projects are added, updated or removed, or closed files change on disk (not for edits of open documents).</summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>A loaded project as its key sees it (M34c): its design-time build and the projects it references.</summary>
    internal sealed record ProjectSnapshot(ProjectId Id, ProjectLoader.Parsed Parsed, IReadOnlyList<ProjectId> References);

    /// <summary>The keys of the loaded projects as they are now (M34c, <see cref="LanguageStateCache"/>).</summary>
    public ProjectKeys Keys()
    {
        lock (_gate)
        {
            return new ProjectKeys(
                _projects.Values.Where(e => e.Parsed is not null).ToDictionary(e => e.Id, e => new ProjectSnapshot(e.Id, e.Parsed!, [.. e.AppliedProjects.Select(r => r.ProjectId)])),
                _open.ToDictionary(o => o.Key, o => o.Value.Text, Paths.Comparer));
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
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        TimeSpan parsing = default, applying = default, referencing = default;
        lock (_gate)
        {
            var solution = _workspace.CurrentSolution;
            foreach (var data in projects)
            {
                var path = Path.GetFullPath(data.Path);
                var step = System.Diagnostics.Stopwatch.GetTimestamp();
                var parsed = _loader.Parse(data with { Path = path });
                parsing += System.Diagnostics.Stopwatch.GetElapsedTime(step);
                if (parsed is null)
                {
                    continue;
                }

                if (!_projects.TryGetValue(path, out var entry))
                {
                    entry = new ProjectEntry(ProjectId.CreateNewId(data.Name));
                    _projects[path] = entry;
                }

                step = System.Diagnostics.Stopwatch.GetTimestamp();
                solution = Apply(solution, entry, parsed);
                entry.Parsed = parsed;
                Watch(Path.GetDirectoryName(path)!);
                applying += System.Diagnostics.Stopwatch.GetElapsedTime(step);
            }

            var references = System.Diagnostics.Stopwatch.GetTimestamp();
            solution = ApplyReferences(solution);
            referencing = System.Diagnostics.Stopwatch.GetElapsedTime(references);
            solution = ReconcileOpenDocuments(solution);
            _workspace.Replace(solution);
            _incomplete = Incomplete();
            Interlocked.Increment(ref _generation);
        }

        if (System.Diagnostics.Stopwatch.GetElapsedTime(started) is { TotalMilliseconds: > 1000 } elapsed)
        {
            Log?.Invoke(this, $"Loading {projects.Count} project(s) took {elapsed.TotalMilliseconds:0} ms (parsing {parsing.TotalMilliseconds:0}, "
                              + $"adding {applying.TotalMilliseconds:0}, references {referencing.TotalMilliseconds:0}).");
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
            _incomplete = Incomplete();
            Interlocked.Increment(ref _generation);
        }

        ProjectsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The projects that reference a project that isn't loaded, directly or through other projects.</summary>
    private ImmutableHashSet<ProjectId> Incomplete()
    {
        var verdicts = new Dictionary<string, bool>(Paths.Comparer);
        bool Missing(string path)
        {
            if (verdicts.TryGetValue(path, out var known))
            {
                return known;
            }

            verdicts[path] = false; // a cycle (never valid in MSBuild) ends here
            var missing = !_projects.TryGetValue(path, out var entry) || entry.Parsed is null
                          || (entry.Parsed.Data.ProjectReferences ?? []).Any(Missing);
            verdicts[path] = missing;
            return missing;
        }

        return [.. _projects.Values.Where(e => e.Parsed is not null && (e.Parsed.Data.ProjectReferences ?? []).Any(Missing)).Select(e => e.Id)];
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
                    ProjectLoader.RoslynLanguage(data.Language) ?? LanguageNames.CSharp,
                    filePath: data.Path,
                    outputFilePath: data.OutputPath,
                    compilationOptions: parsed.CompilationOptions,
                    parseOptions: parsed.ParseOptions,
                    documents: parsed.Sources.Select(s => SourceDocument(id, s.Path, s.Folders)),
                    analyzerReferences: parsed.Analyzers,
                    additionalDocuments: parsed.AdditionalFiles.Select(f => FileDocument(id, f)),
                    outputRefFilePath: data.OutputRefPath)
                .WithAnalyzerConfigDocuments(parsed.AnalyzerConfigFiles.Select(f => FileDocument(id, f)))
                .WithCompilationOutputInfo(default(CompilationOutputInfo).WithAssemblyPath(data.OutputPath)));
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

        if (project.CompilationOutputInfo.AssemblyPath != data.OutputPath)
        {
            // Hot Reload while debugging (M55) reads the build's assembly and PDB from here.
            solution = solution.WithProjectCompilationOutputInfo(id, project.CompilationOutputInfo.WithAssemblyPath(data.OutputPath));
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

        // Projects reference hundreds of the same files: each file's timestamp is read once per call, not once per project
        // (M34b: a 96-project solution, updated a project at a time, spent a minute here).
        var files = new Dictionary<string, MetadataReference?>(Paths.Comparer);

        // Each file's reference is created in parallel first (0.9): reading hundreds of assemblies one at a time took 1.5 s
        // of a 94-project solution's load, the host's messages waiting behind it.
        var distinct = _projects.Values.Where(e => e.Parsed is not null).SelectMany(e => e.Parsed!.References)
            .Where(r => r.Properties == MetadataReferenceProperties.Assembly && !outputs.ContainsKey(r.Path))
            .Select(r => r.Path).Distinct(Paths.Comparer).ToList();
        var created = new System.Collections.Concurrent.ConcurrentDictionary<string, MetadataReference?>(Paths.Comparer);
        Parallel.ForEach(distinct, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) },
            path => created[path] = _loader.Metadata(path, MetadataReferenceProperties.Assembly));
        foreach (var (path, reference) in created)
        {
            files[path] = reference;
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
                else if (properties == MetadataReferenceProperties.Assembly)
                {
                    if (!files.TryGetValue(path, out var reference))
                    {
                        files[path] = reference = _loader.Metadata(path, properties);
                    }

                    if (reference is not null)
                    {
                        metadata.Add(reference);
                    }
                }
                else if (_loader.Metadata(path, properties) is { } aliased)
                {
                    metadata.Add(aliased);
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
            if (IsAdditionalText(path))
            {
                continue; // additional documents, loaded with the open text; never in Miscellaneous Files
            }

            var ids = ProjectDocumentIds(solution, path);
            if (ids.Length > 0 && open.MiscId is { } miscId)
            {
                solution = solution.RemoveDocument(miscId).WithDocumentText(ids, open.Text, PreservationMode.PreserveIdentity);
                _open[path] = open with { MiscId = null };
            }
            else if (ids.Length == 0 && open.MiscId is null && FitsMiscellaneous(path))
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

    /// <summary>
    /// Whether an open file no loaded project has goes into Miscellaneous Files, a C# project: not a Visual Basic file,
    /// which it would parse as C# (no types for the navigation bar, C# errors). Such a file is read on its own
    /// (<see cref="IsVisualBasic"/>).
    /// </summary>
    private static bool FitsMiscellaneous(string path) => !IsVisualBasic(path);

    /// <summary>A Visual Basic source file.</summary>
    public static bool IsVisualBasic(string path) => path.EndsWith(".vb", StringComparison.OrdinalIgnoreCase);

    /// <summary>Razor and XAML files are additional documents: the editor's text drives their source generators (ADR 0023, 0047).</summary>
    public static bool IsAdditionalText(string path) => Razor.RazorFiles.IsRazor(path) || Xaml.XamlContext.IsXaml(path);

    /// <summary>The editor's text of an open file, or null.</summary>
    public SourceText? OpenText(string path)
    {
        lock (_gate)
        {
            return _open.TryGetValue(Path.GetFullPath(path), out var open) ? open.Text : null;
        }
    }

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

    /// <summary>
    /// The encoding an open document is saved in: its file's byte order mark, else UTF-8. Emitting its debug information
    /// needs one (Hot Reload's deltas, M55: CS8055 without), and the PDB's checksums are SHA-256 of those bytes.
    /// </summary>
    private static System.Text.Encoding EncodingOf(string path)
    {
        Span<byte> bom = stackalloc byte[3];
        var read = 0;
        try
        {
            using var file = File.OpenRead(path);
            read = file.ReadAtLeast(bom, 3, throwOnEndOfStream: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF ? new System.Text.UTF8Encoding(true)
            : read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE ? System.Text.Encoding.Unicode
            : read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF ? System.Text.Encoding.BigEndianUnicode
            : new System.Text.UTF8Encoding(false);
    }

    public void Open(string path, string text, int version)
    {
        path = Path.GetFullPath(path);
        var sourceText = SourceText.From(text, EncodingOf(path), SourceHashAlgorithm.Sha256);
        lock (_gate)
        {
            var solution = _workspace.CurrentSolution;
            if (IsAdditionalText(path))
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
            else if (FitsMiscellaneous(path))
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
                    : SourceText.From(change.Text, text.Encoding, text.ChecksumAlgorithm);
            }

            var solution = _workspace.CurrentSolution;
            if (IsAdditionalText(path))
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
            if (IsAdditionalText(path))
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

    private static bool IsMisc(Solution solution, DocumentId id) => solution.GetProject(id.ProjectId) is { } project && IsMiscellaneous(project);

    /// <summary>Whether a project is Miscellaneous Files, the home of open files no loaded project has.</summary>
    public static bool IsMiscellaneous(Project project) => project.Name == "Miscellaneous Files";

    /// <summary>The LSP URI of a document (source-generated documents get a <c>codalus-generated</c> URI).</summary>
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
            if (reloaded.Count > 0)
            {
                Interlocked.Increment(ref _generation);
            }
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
