using System.Collections.Immutable;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Contracts.EditAndContinue;
using Microsoft.CodeAnalysis.EditAndContinue;
using Microsoft.CodeAnalysis.Text;
using Diagnostic = Microsoft.CodeAnalysis.Diagnostic;
using Location = Codalus.LanguageServices.Protocol.Location;

namespace Codalus.LanguageServices.Host.Features;

/// <summary>
/// Hot Reload while debugging (M55, ADR 0086): sessions of Roslyn's Edit and Continue service, the one <c>dotnet
/// watch</c> uses, for programs the Codalus debugger runs. A session starts from the build the program runs (each
/// project's <see cref="CompilationOutputInfo"/>: its assembly and PDB); each update gives the edits since as deltas
/// for the debugger's <c>codalus/applyChanges</c>, or the rude edits that need a restart. An update made while the
/// program is stopped comes with its active statements (M58): Roslyn then checks the edit against them (what a frame
/// can't follow is a rude edit) and says where each one is in the new code, so the debugger moves the frames there.
/// One made while the program runs has none: a frame already running a changed method finishes in its old version.
/// <para>
/// Roslyn's service is internal to <c>Microsoft.CodeAnalysis.Features</c> (dotnet watch reaches it through an internal
/// wrapper, <c>HotReloadService</c>, whose calls this mirrors); the host compiles against it publicized.
/// </para>
/// </summary>
internal sealed class HotReloadFeature(Func<Solution> currentSolution, Func<bool> loading)
{
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private TaskCompletionSource _projectsChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record Session(DebuggingSessionId Id, IEditAndContinueService Service, ImmutableDictionary<ProjectId, RunningProjectOptions> Running, Debugger Debugger)
    {
        /// <summary>Whether Roslyn was last told the program is stopped (its break state).</summary>
        public bool InBreakState { get; set; }

        /// <summary>The solution the next update is made from, as Roslyn commits it: the start's, then each committed update's.</summary>
        public required Solution Baseline { get; set; }

        /// <summary>The last update's solution, until it's committed or discarded.</summary>
        public Solution? Pending { get; set; }
    }

    /// <summary>The debugger's side, as Roslyn asks for it: the runtime's capabilities, and the active statements of the update being made.</summary>
    private sealed class Debugger(ImmutableArray<string> capabilities) : IManagedHotReloadService
    {
        /// <summary>Where the stopped program's frames are, as the update's request gave them; empty while it runs.</summary>
        public ImmutableArray<ManagedActiveStatementDebugInfo> ActiveStatements { get; set; } = [];

        public ValueTask<ImmutableArray<ManagedActiveStatementDebugInfo>> GetActiveStatementsAsync(CancellationToken cancellationToken) => ValueTask.FromResult(ActiveStatements);

        public ValueTask<ManagedHotReloadAvailability> GetAvailabilityAsync(Guid module, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ManagedHotReloadAvailability(ManagedHotReloadAvailabilityStatus.Available));

        public ValueTask<ImmutableArray<string>> GetCapabilitiesAsync(CancellationToken cancellationToken) => ValueTask.FromResult(capabilities);

        public ValueTask PrepareModuleForUpdateAsync(Guid module, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    /// <summary>
    /// The text the build compiled, when Roslyn asks for it (a document's text doesn't match the PDB, or it checks a file
    /// it hadn't read): the session's start had it, before any edit. Without this Roslyn reads the file, and a file
    /// saved with an edit before the first apply no longer matches the build, so its edits were ignored as out of sync.
    /// </summary>
    private sealed class StartTexts(Solution start) : IPdbMatchingSourceTextProvider
    {
        public async ValueTask<string?> TryGetMatchingSourceTextAsync(string filePath, ImmutableArray<byte> requiredChecksum, SourceHashAlgorithm checksumAlgorithm, CancellationToken cancellationToken)
        {
            foreach (var id in start.GetDocumentIdsWithFilePath(filePath))
            {
                if (start.GetDocument(id) is not { } document)
                {
                    continue;
                }

                var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                var content = text.ToString();
                if (SourceText.From(content, text.Encoding ?? new System.Text.UTF8Encoding(false), checksumAlgorithm).GetChecksum().SequenceEqual(requiredChecksum))
                {
                    return content;
                }
            }

            return null;
        }
    }

    /// <summary>The workspace's projects changed, or whether the solution is loading: a start waiting for the load looks again.</summary>
    public void ProjectsChanged() =>
        Interlocked.Exchange(ref _projectsChanged, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    public async Task<bool> StartAsync(HotReloadStartParams parameters, CancellationToken cancellationToken)
    {
        End(parameters.Session);

        // The baseline is the whole solution: started while projects were still arriving (one referencing another's
        // output file, the other not loaded yet), the session took their arrival for changed references, a restart, and
        // Continue waited on that question (arm64 CI, run 40896721482). The debugger cancels the wait when it ends.
        while (true)
        {
            var changed = Volatile.Read(ref _projectsChanged).Task;
            if (!loading())
            {
                break;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        var solution = currentSolution();
        // The projects the debugged programs run; Roslyn counts the projects they reference with them. A rude edit in
        // another project asks no program to restart (it comes back in NotRunning). No projects named (an attach, an
        // AppHost's programs): every project of the solution counts as running.
        var running = solution.Projects
            .Where(p => p.FilePath is { } path && (parameters.Projects.Length == 0 || parameters.Projects.Any(r => Paths.Comparer.Equals(Path.GetFullPath(r), Path.GetFullPath(path)))))
            .ToImmutableDictionary(p => p.Id, _ => new RunningProjectOptions { RestartWhenChangesHaveNoEffect = false });
        if (running.IsEmpty)
        {
            return false;
        }

        var service = solution.Services.GetRequiredService<IEditAndContinueWorkspaceService>().Service;

        // What the build compiled: a document edited since (open and unsaved) is read from disk for the baseline.
        await EditAndContinueService.HydrateDocumentsAsync(solution, cancellationToken).ConfigureAwait(false);
        var debugger = new Debugger([.. parameters.Capabilities]);
        var id = service.StartDebuggingSession(solution, debugger, new StartTexts(solution), reportDiagnostics: false);
        lock (_sessions)
        {
            _sessions[parameters.Session] = new Session(id, service, running, debugger) { Baseline = solution };
        }

        return true;
    }

    public async Task<HotReloadUpdateResult> UpdateAsync(HotReloadSessionParams parameters, CancellationToken cancellationToken)
    {
        if (Find(parameters.Session) is not { } session)
        {
            return new HotReloadUpdateResult(HotReloadUpdateResult.None, [], [], [], []);
        }

        // The program's state for this update: stopped, with where its frames are, or running. Roslyn reads the active
        // statements when its break state changes, so it's told each time they do.
        var statements = parameters.ActiveStatements;
        session.Debugger.ActiveStatements = statements is null ? [] : [.. statements.Select(ActiveStatement)];
        if (statements is not null || session.InBreakState)
        {
            session.InBreakState = statements is not null;
            session.Service.BreakStateOrCapabilitiesChanged(session.Id, inBreakState: session.InBreakState);
        }

        var solution = currentSolution();
        var results = await session.Service.EmitSolutionUpdateAsync(session.Id, solution, session.Running,
            (_, _, _) => ValueTask.FromResult(ImmutableArray<ActiveStatementSpan>.Empty), cancellationToken).ConfigureAwait(false);
        // Roslyn commits an update with no changes that matter at once, and holds the others until they're committed.
        var updated = results.Solution ?? solution;
        var baseline = session.Baseline;
        if (results.ModuleUpdates.Status == ModuleUpdateStatus.None)
        {
            session.Baseline = updated;
        }
        else if (results.ModuleUpdates.Status == ModuleUpdateStatus.Ready)
        {
            session.Pending = updated;
        }

        var status = results.ModuleUpdates.Status switch
        {
            ModuleUpdateStatus.Ready => HotReloadUpdateResult.Ready,
            ModuleUpdateStatus.Blocked => HotReloadUpdateResult.Blocked,
            _ => HotReloadUpdateResult.None,
        };
        var updates = results.ModuleUpdates.Updates.Select(u => new HotReloadModuleUpdate(u.Module, solution.GetProject(u.ProjectId)?.FilePath ?? string.Empty,
            [.. u.MetadataDelta], [.. u.ILDelta], [.. u.PdbDelta], [.. u.UpdatedTypes])
        {
            // Roslyn's spans count from 0, the program's symbols from 1.
            ActiveStatements = [.. u.ActiveStatements.Select(a => new HotReloadActiveStatementUpdate(a.Method.Token, a.Method.Version, a.ILOffset, a.NewSpan.StartLine + 1, a.NewSpan.StartColumn + 1))],
        }).ToArray();
        // A rude edit restarts the running projects that reference its project (Roslyn's ProjectsToRestart, with the
        // projects that caused each); one in a project none references restarts nothing. A program that joined the debug
        // session later was built after its baseline (ADR 0109), so its projects' changes since were taken for rude edits
        // of the session's program, which Continue then asked to restart (macOS CI, run 45872232919).
        var causes = results.ProjectsToRestart.Values.SelectMany(p => p).ToHashSet();
        var rude = new List<HotReloadDiagnostic>();
        var notRunning = new List<HotReloadDiagnostic>();
        foreach (var (project, diagnostics) in results.GetTransientDiagnostics())
        {
            foreach (var diagnostic in diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
            {
                var described = Describe(diagnostic) with { Detail = await GeneratedDetailAsync(baseline, updated, project, diagnostic, cancellationToken).ConfigureAwait(false) };
                (causes.Contains(project) ? rude : notRunning).Add(described);
            }
        }

        var errors = results.GetPersistentDiagnostics().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).Select(Describe).ToArray();
        var restart = results.ProjectsToRestart.Keys.Select(p => solution.GetProject(p)?.FilePath).OfType<string>().ToArray();
        return new HotReloadUpdateResult(status, updates, [.. rude], errors, restart) { NotRunning = [.. notRunning] };
    }

    /// <summary>
    /// For a rude edit in a source generator's document (<see cref="HotReloadDiagnostic.Detail"/>): its lines around the
    /// edit in the baseline and now, and how the project's inputs changed between them. Null for an ordinary document.
    /// TODO(issue): why the Razor generator's output changed with no edit (macOS CI, run 45872232919); this says next time.
    /// </summary>
    private static async Task<string?> GeneratedDetailAsync(Solution baseline, Solution current, ProjectId projectId, Diagnostic diagnostic, CancellationToken cancellationToken)
    {
        if (diagnostic.Location.GetLineSpan() is not { IsValid: true, Path.Length: > 0 } span
            || current.GetProject(projectId) is not { } project
            || !current.GetDocumentIdsWithFilePath(span.Path).IsEmpty)
        {
            return null;
        }

        var now = (await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(d => d.FilePath is { } path && Paths.Comparer.Equals(path, span.Path));
        if (now is null)
        {
            return null;
        }

        var before = baseline.GetProject(projectId);
        var then = before is null ? null : (await before.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(d => d.FilePath is { } path && Paths.Comparer.Equals(path, span.Path));
        var line = span.StartLinePosition.Line;
        var detail = new System.Text.StringBuilder();
        detail.Append("generated; the baseline's lines: ").Append(then is null ? "(no such document)" : Lines(await then.GetTextAsync(cancellationToken).ConfigureAwait(false), line))
            .Append("; now: ").Append(Lines(await now.GetTextAsync(cancellationToken).ConfigureAwait(false), line))
            .Append("; the project since: ").Append(before is null ? "not in the baseline" : await ChangesAsync(project, before, cancellationToken).ConfigureAwait(false));
        return detail.ToString();
    }

    /// <summary>Lines <paramref name="line"/> - 2 to + 2 (from 0) of <paramref name="text"/>, numbered from 1.</summary>
    private static string Lines(SourceText text, int line) =>
        string.Join(" | ", Enumerable.Range(Math.Max(0, line - 2), Math.Max(0, Math.Min(text.Lines.Count, line + 3) - Math.Max(0, line - 2)))
            .Select(i => (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ": " + text.Lines[i].ToString().Trim()));

    /// <summary>What changed in a project's inputs between two solutions, briefly: what a generator's output depends on.</summary>
    private static async Task<string> ChangesAsync(Project project, Project before, CancellationToken cancellationToken)
    {
        static string Names(IEnumerable<string?> names) => string.Join(", ", names.OfType<string>().Select(Path.GetFileName).Take(5));

        var changes = project.GetChanges(before);
        var parts = new List<string>();
        void Add(string what, IEnumerable<string?> names)
        {
            var list = names.ToList();
            if (list.Count > 0)
            {
                parts.Add($"{what} {list.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} ({Names(list)})");
            }
        }

        Add("metadata references added", changes.GetAddedMetadataReferences().Select(r => r.Display));
        Add("metadata references removed", changes.GetRemovedMetadataReferences().Select(r => r.Display));
        Add("analyzer references added", changes.GetAddedAnalyzerReferences().Select(r => r.Display));
        Add("analyzer references removed", changes.GetRemovedAnalyzerReferences().Select(r => r.Display));
        Add("additional files added", changes.GetAddedAdditionalDocuments().Select(d => project.GetAdditionalDocument(d)?.FilePath));
        Add("additional files removed", changes.GetRemovedAdditionalDocuments().Select(d => before.GetAdditionalDocument(d)?.FilePath));
        Add("additional files changed", changes.GetChangedAdditionalDocuments().Select(d => project.GetAdditionalDocument(d)?.FilePath));
        Add("analyzer config files added", changes.GetAddedAnalyzerConfigDocuments().Select(d => project.GetAnalyzerConfigDocument(d)?.FilePath));
        Add("analyzer config files removed", changes.GetRemovedAnalyzerConfigDocuments().Select(d => before.GetAnalyzerConfigDocument(d)?.FilePath));
        Add("documents changed", changes.GetChangedDocuments().Select(d => project.GetDocument(d)?.FilePath));
        if (!Equals(project.ParseOptions, before.ParseOptions))
        {
            parts.Add("parse options changed");
        }

        if (!Equals(project.CompilationOptions, before.CompilationOptions))
        {
            parts.Add("compilation options changed");
        }

        // An analyzer config's lines that changed: a build property the generator reads (build_property.*).
        foreach (var id in changes.GetChangedAnalyzerConfigDocuments())
        {
            if (project.GetAnalyzerConfigDocument(id) is not { } now || before.GetAnalyzerConfigDocument(id) is not { } then)
            {
                continue;
            }

            var newLines = (await now.GetTextAsync(cancellationToken).ConfigureAwait(false)).Lines.Select(l => l.ToString().Trim()).ToHashSet(StringComparer.Ordinal);
            var oldLines = (await then.GetTextAsync(cancellationToken).ConfigureAwait(false)).Lines.Select(l => l.ToString().Trim()).ToHashSet(StringComparer.Ordinal);
            parts.Add($"analyzer config {Path.GetFileName(now.FilePath)} changed: +[{string.Join(", ", newLines.Except(oldLines).Take(5))}] -[{string.Join(", ", oldLines.Except(newLines).Take(5))}]");
        }

        return parts.Count == 0 ? "no input changed" : string.Join("; ", parts);
    }

    /// <summary>A frame's statement as Roslyn takes it: the instruction, its span (from 0) and what kind of frame it is.</summary>
    private static ManagedActiveStatementDebugInfo ActiveStatement(HotReloadActiveStatement statement)
    {
        var flags = (statement.Leaf ? ActiveStatementFlags.LeafFrame : ActiveStatementFlags.NonLeafFrame)
            | (statement.UpToDate ? ActiveStatementFlags.MethodUpToDate : ActiveStatementFlags.None)
            | (statement.PartiallyExecuted ? ActiveStatementFlags.PartiallyExecuted : ActiveStatementFlags.None);
        return new ManagedActiveStatementDebugInfo(
            new ManagedInstructionId(new ManagedMethodId(statement.ModuleId, statement.MethodToken, statement.MethodVersion), statement.IlOffset),
            statement.Document,
            new SourceSpan(Math.Max(0, statement.StartLine - 1), Math.Max(0, statement.StartColumn - 1), Math.Max(0, statement.EndLine - 1), Math.Max(0, statement.EndColumn - 1)),
            flags);
    }

    /// <summary>The debugger applied the last update: the next one is made from it.</summary>
    public bool Commit(HotReloadSessionParams parameters)
    {
        if (Find(parameters.Session) is not { } session)
        {
            return false;
        }

        session.Service.CommitSolutionUpdate(session.Id);
        if (session.Pending is { } pending)
        {
            session.Baseline = pending;
            session.Pending = null;
        }

        return true;
    }

    /// <summary>The last update wasn't applied (refused, a rude edit kept): the next one is made from the one before.</summary>
    public bool Discard(HotReloadSessionParams parameters)
    {
        if (Find(parameters.Session) is not { } session)
        {
            return false;
        }

        session.Service.DiscardSolutionUpdate(session.Id);
        session.Pending = null;
        return true;
    }

    public bool End(string name)
    {
        Session? session;
        lock (_sessions)
        {
            if (!_sessions.Remove(name, out session))
            {
                return false;
            }
        }

        try
        {
            // An update left neither committed nor discarded (the program ended while it was applied) would keep the
            // session from ending.
            session.Service.DiscardSolutionUpdate(session.Id);
        }
        catch (InvalidOperationException)
        {
        }

        session.Service.EndDebuggingSession(session.Id);
        return true;
    }

    private Session? Find(string name)
    {
        lock (_sessions)
        {
            return _sessions.GetValueOrDefault(name);
        }
    }

    private static HotReloadDiagnostic Describe(Diagnostic diagnostic)
    {
        Location? location = null;
        if (diagnostic.Location.GetLineSpan() is { IsValid: true, Path.Length: > 0 } span)
        {
            location = new Location(DocumentUris.FromPath(span.Path), Positions.ToRange(span.Span));
        }

        return new HotReloadDiagnostic(diagnostic.Id, diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), location);
    }
}
