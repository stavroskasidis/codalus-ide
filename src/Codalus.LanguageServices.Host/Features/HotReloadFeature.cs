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
        // No projects named (an attach): every project of the solution counts as running.
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
            _sessions[parameters.Session] = new Session(id, service, running, debugger);
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
        var rude = results.GetTransientDiagnostics().SelectMany(p => p.diagnostics).Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).Select(Describe).ToArray();
        var errors = results.GetPersistentDiagnostics().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).Select(Describe).ToArray();
        var restart = results.ProjectsToRestart.Keys.Select(p => solution.GetProject(p)?.FilePath).OfType<string>().ToArray();
        return new HotReloadUpdateResult(status, updates, rude, errors, restart);
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
