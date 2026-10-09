using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Daedalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;
using Diagnostic = Daedalus.LanguageServices.Protocol.Diagnostic;
using DiagnosticSeverity = Daedalus.LanguageServices.Protocol.DiagnosticSeverity;

namespace Daedalus.LanguageServices.Host;

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

    private static readonly TimeSpan s_documentDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan s_backgroundDelay = TimeSpan.FromMilliseconds(1500);

    private readonly WorkspaceManager _workspace;
    private readonly Func<PublishDiagnosticsParams, Task> _publish;
    private readonly Action<string> _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, CancellationTokenSource> _documentPasses = new(Paths.Comparer);
    private readonly Dictionary<string, Diagnostic[]> _background = new(Paths.Comparer);
    private readonly ConditionalWeakTable<Compilation, CompilationWithAnalyzers> _analyzers = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _backgroundPass;

    public DiagnosticsService(WorkspaceManager workspace, Func<PublishDiagnosticsParams, Task> publish, Action<string> log)
    {
        _workspace = workspace;
        _publish = publish;
        _log = log;
    }

    /// <summary><see cref="ScopeOpenDocuments"/> (default) or <see cref="ScopeSolution"/>: where analyzers run.</summary>
    public string AnalysisScope { get; set; } = ScopeOpenDocuments;

    /// <summary>Raised after a background pass over every project completes: (projects, documents).</summary>
    public event Action<int, int>? BackgroundPassCompleted;

    // ------------------------------------------------------------------ open documents

    public void DocumentChanged(string path)
    {
        path = Path.GetFullPath(path);
        CancellationToken token;
        lock (_gate)
        {
            if (_documentPasses.Remove(path, out var previous))
            {
                previous.Cancel();
                previous.Dispose();
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _documentPasses[path] = cts;
            token = cts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(s_documentDelay, token).ConfigureAwait(false);
                await AnalyzeOpenDocumentAsync(path, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log($"Diagnostics failed for {Path.GetFileName(path)}: {ex.Message}");
            }
        }, CancellationToken.None);
        ScheduleBackground();
    }

    /// <summary>A closed document shows what the background pass found for it (or nothing).</summary>
    public void DocumentClosed(string path)
    {
        path = Path.GetFullPath(path);
        lock (_gate)
        {
            if (_documentPasses.Remove(path, out var previous))
            {
                previous.Cancel();
                previous.Dispose();
            }
        }

        Diagnostic[] background;
        lock (_gate)
        {
            background = _background.GetValueOrDefault(path) ?? [];
        }

        _ = _publish(new PublishDiagnosticsParams(DocumentUris.FromPath(path), null, background));
    }

    private async Task AnalyzeOpenDocumentAsync(string path, CancellationToken cancellationToken)
    {
        var solution = _workspace.CurrentSolution;
        var version = _workspace.Version(path);
        if (await WorkspaceManager.FindDocumentAsync(solution, DocumentUris.FromPath(path), cancellationToken).ConfigureAwait(false) is not { } document)
        {
            return;
        }

        var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (tree is null || model is null)
        {
            return;
        }

        var diagnostics = model.GetDiagnostics(cancellationToken: cancellationToken).ToList();
        if (document.Project.Id != _workspace.MiscellaneousProjectId && Analyzers(document.Project, model.Compilation) is { } analyzers)
        {
            diagnostics.AddRange(await analyzers.GetAnalyzerSyntaxDiagnosticsAsync(tree, cancellationToken).ConfigureAwait(false));
            diagnostics.AddRange(await analyzers.GetAnalyzerSemanticDiagnosticsAsync(model, filterSpan: null, cancellationToken).ConfigureAwait(false));
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _publish(new PublishDiagnosticsParams(DocumentUris.FromPath(path), version, Convert(diagnostics, tree))).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ the whole solution

    /// <summary>Restarts the background pass after a quiet period.</summary>
    public void ScheduleBackground()
    {
        CancellationToken token;
        lock (_gate)
        {
            _backgroundPass?.Cancel();
            _backgroundPass?.Dispose();
            _backgroundPass = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            token = _backgroundPass.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(s_backgroundDelay, token).ConfigureAwait(false);
                await BackgroundPassAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log($"Solution diagnostics failed: {ex.Message}");
            }
        }, CancellationToken.None);
    }

    private async Task BackgroundPassAsync(CancellationToken cancellationToken)
    {
        var solution = _workspace.CurrentSolution;
        var found = new Dictionary<string, Diagnostic[]>(Paths.Comparer);
        var projects = 0;
        var documents = 0;
        foreach (var project in solution.Projects.Where(p => p.Id != _workspace.MiscellaneousProjectId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is not { } compilation)
            {
                continue;
            }

            projects++;
            documents += project.DocumentIds.Count;
            var diagnostics = compilation.GetDiagnostics(cancellationToken).ToList();
            if (AnalysisScope == ScopeSolution && Analyzers(project, compilation) is { } analyzers)
            {
                diagnostics.AddRange(await analyzers.GetAnalyzerDiagnosticsAsync(cancellationToken).ConfigureAwait(false));
            }

            foreach (var group in diagnostics.Where(d => d.Location.IsInSource).GroupBy(d => d.Location.SourceTree!))
            {
                if (group.Key.FilePath is { Length: > 0 } path && File.Exists(path))
                {
                    found[path] = [.. (found.GetValueOrDefault(path) ?? []).Concat(Convert(group, group.Key))];
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        List<string> changed;
        lock (_gate)
        {
            changed = [.. _background.Keys.Union(found.Keys, Paths.Comparer)];
            _background.Clear();
            foreach (var (path, diagnostics) in found)
            {
                _background[path] = diagnostics;
            }
        }

        foreach (var path in changed.Where(p => !_workspace.IsOpen(p)))
        {
            await _publish(new PublishDiagnosticsParams(DocumentUris.FromPath(path), null, found.GetValueOrDefault(path) ?? [])).ConfigureAwait(false);
        }

        BackgroundPassCompleted?.Invoke(projects, documents);
    }

    // ------------------------------------------------------------------ helpers

    private CompilationWithAnalyzers? Analyzers(Project project, Compilation compilation)
    {
        var analyzers = project.AnalyzerReferences.SelectMany(r => r.GetAnalyzers(project.Language)).ToImmutableArray();
        if (analyzers.IsEmpty)
        {
            return null;
        }

        return _analyzers.GetValue(compilation, c => c.WithAnalyzers(analyzers, new CompilationWithAnalyzersOptions(
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

    public void Dispose()
    {
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
