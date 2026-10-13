using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Codalus.LanguageServices.Host.Features;
using Codalus.LanguageServices.Protocol;
using Codalus.Rpc;
using Microsoft.CodeAnalysis;

namespace Codalus.LanguageServices.Host;

/// <summary>
/// The language host's side of the protocol (docs/protocol.md): LSP for C# plus the <c>codalus/</c> project
/// extensions. Notifications (edits, project updates) are applied in arrival order, and each request reads the
/// solution as it is when the request arrives, so it observes every earlier edit.
/// </summary>
internal sealed class LanguageServer : IAsyncDisposable
{
    private readonly JsonRpcConnection _connection;
    private readonly WorkspaceManager _workspace = new();
    private readonly CompletionFeature _completion = new();
    private readonly Razor.RazorFeatures _razor;
    private readonly CodeActionsFeature _codeActions;
    private readonly CodeCleanupFeature _cleanup;
    private readonly DiagnosticsService _diagnostics;
    private readonly AnalysisSolution _analysis;
    private readonly Xaml.XamlDiagnostics _xamlDiagnostics;
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LanguageServer(JsonRpcConnection connection)
    {
        _connection = connection;
        _razor = new Razor.RazorFeatures(_completion, message => Log(MessageType.Log, message));
        _analysis = new AnalysisSolution(_workspace, message => Log(MessageType.Log, message));
        _diagnostics = new DiagnosticsService(_workspace, _analysis, p => Notify(Methods.PublishDiagnostics, p, ProtocolJsonContext.Default.PublishDiagnosticsParams), message => Log(MessageType.Warning, message));
        _xamlDiagnostics = new Xaml.XamlDiagnostics(_workspace, p => Notify(Methods.PublishDiagnostics, p, ProtocolJsonContext.Default.PublishDiagnosticsParams), message => Log(MessageType.Warning, message));
        _codeActions = new CodeActionsFeature(_diagnostics, message => Log(MessageType.Log, message));
        _cleanup = new CodeCleanupFeature(_diagnostics, message => Log(MessageType.Log, message));
        // Ready only once the solution has loaded: a pass over the projects that arrived so far is not the whole solution.
        _diagnostics.BackgroundPassCompleted += (projects, documents, complete) =>
            _ = Notify(Methods.WorkspaceStatus, new WorkspaceStatusParams(complete ? WorkspaceStates.Ready : WorkspaceStates.Loading, projects, documents), ProtocolJsonContext.Default.WorkspaceStatusParams);
        _workspace.Log += (_, message) => Log(MessageType.Warning, message);
        _connection.Slow += (_, message) => Log(MessageType.Warning, message);
        _workspace.FileReloaded += (_, _) => _diagnostics.ScheduleBackground();
    }

    public Task ExitRequested => _exit.Task;

    public void Register()
    {
        var json = ProtocolJsonContext.Default;
        _connection.OnRequest(Methods.Initialize, json.InitializeParams, json.InitializeResult, (p, _) =>
        {
            if (p.InitializationOptions is { ValueKind: JsonValueKind.Object } options)
            {
                ApplySettings(options);
            }

            var version = typeof(LanguageServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

            return Task.FromResult(new InitializeResult(
                new ServerCapabilities
                {
                    CompletionProvider = CompletionFeature.Options,
                    HoverProvider = true,
                    SignatureHelpProvider = SignatureHelpFeature.Options,
                    DefinitionProvider = true,
                    ImplementationProvider = true,
                    DocumentHighlightProvider = true,
                    FoldingRangeProvider = true,
                    SemanticTokensProvider = SemanticTokensFeature.Options,
                    ReferencesProvider = true,
                    RenameProvider = true,
                    CodeActionProvider = true,
                    DocumentFormattingProvider = true,
                    DocumentRangeFormattingProvider = true,
                    DocumentOnTypeFormattingProvider = FormattingFeature.OnTypeOptions,
                    WorkspaceSymbolProvider = true,
                    DocumentSymbolProvider = true,
                    CallHierarchyProvider = true,
                    TypeHierarchyProvider = true,
                    InlayHintProvider = true,
                    CodeLensProvider = new CodeLensOptions(ResolveProvider: true),
                    SelectionRangeProvider = true,
                },
                new ServerInfo("codalus-lshost", version)));
        });
        _connection.OnNotification(Methods.Initialized, json.EmptyObject, _ => { });
        _connection.OnRequest(Methods.Shutdown, (_, _) => Task.FromResult<JsonElement?>(null));
        _connection.OnNotification(Methods.Exit, json.EmptyObject, _ => _exit.TrySetResult());
        _connection.OnNotification(Methods.DidChangeConfiguration, json.DidChangeConfigurationParams, p =>
        {
            if (p.Settings is { ValueKind: JsonValueKind.Object } settings)
            {
                ApplySettings(settings);
                _diagnostics.ScheduleBackground();
            }
        });

        // ------------------------------------------------------------ documents and projects

        _connection.OnNotification(Methods.DidOpen, json.DidOpenTextDocumentParams, p =>
        {
            if (DocumentUris.ToPath(p.TextDocument.Uri) is { } path)
            {
                _workspace.Open(path, p.TextDocument.Text, p.TextDocument.Version);
                _diagnostics.DocumentOpened(path);
                XamlChanged(path);
            }
        });
        _connection.OnNotification(Methods.DidChange, json.DidChangeTextDocumentParams, p =>
        {
            if (DocumentUris.ToPath(p.TextDocument.Uri) is { } path && _workspace.Change(path, p.TextDocument.Version, p.ContentChanges))
            {
                _analysis.DocumentChanged(path);
                _diagnostics.DocumentChanged(path);
                XamlChanged(path);
            }
        });
        _connection.OnNotification(Methods.DidClose, json.DidCloseTextDocumentParams, p =>
        {
            if (DocumentUris.ToPath(p.TextDocument.Uri) is { } path)
            {
                _workspace.Close(path);
                _diagnostics.DocumentClosed(path);
                if (Xaml.XamlContext.IsXaml(path))
                {
                    _xamlDiagnostics.DocumentClosed(path);
                }
            }
        });
        _connection.OnNotification(Methods.DidSave, json.DidSaveTextDocumentParams, _ => { });
        _connection.OnNotification(Methods.ProjectsUpdate, json.ProjectsUpdateParams, p =>
        {
            var loadingChanged = p.Loading is { } loading && _workspace.SetLoading(loading);
            if (p.Projects.Length > 0)
            {
                _workspace.UpdateProjects(p.Projects);
            }

            if (p.Projects.Length > 0 || loadingChanged)
            {
                ProjectsChanged();
            }
        });
        _connection.OnNotification(Methods.ProjectsRemove, json.ProjectsRemoveParams, p =>
        {
            _workspace.RemoveProjects(p.Paths);
            ProjectsChanged();
        });

        // ------------------------------------------------------------ features

        OnPosition(Methods.Completion, json.CompletionParams, json.CompletionList, p => (p.TextDocument, p.Position),
            async (document, offset, p, ct) => await _completion.CompleteAsync(document, offset, p.Context, ct).ConfigureAwait(false), new CompletionList(false, []),
            (mapping, offset, p, ct) => _razor.CompleteAsync(mapping, offset, p.Context, ct),
            async (xaml, offset, _, ct) => await Xaml.XamlCompletion.CompleteAsync(xaml, offset, ct).ConfigureAwait(false), Semantics.Analysis);
        _connection.OnRequest(Methods.CompletionResolve, json.CompletionItem, json.CompletionItem, (item, ct) => _completion.ResolveAsync(item, ct));
        OnPosition(Methods.Hover, json.TextDocumentPositionParams, json.Hover, p => (p.TextDocument, p.Position),
            (document, offset, _, ct) => HoverFeature.HoverAsync(document, offset, ct), null,
            (mapping, offset, _, ct) => _razor.HoverAsync(mapping, offset, ct),
            (xaml, offset, _, ct) => Xaml.XamlNavigation.HoverAsync(xaml, offset, ct), Semantics.Analysis);
        OnPosition(Methods.SignatureHelp, json.SignatureHelpParams, json.SignatureHelp, p => (p.TextDocument, p.Position),
            (document, offset, _, ct) => SignatureHelpFeature.SignatureHelpAsync(document, offset, ct), null,
            (mapping, offset, _, ct) => mapping.ToGenerated(offset) is { } generated ? SignatureHelpFeature.SignatureHelpAsync(mapping.Generated, generated, ct) : Task.FromResult<SignatureHelp?>(null),
            semantics: Semantics.Analysis);
        OnPosition(Methods.Definition, json.TextDocumentPositionParams, json.LocationArray, p => (p.TextDocument, p.Position),
            async (document, offset, _, ct) => await NavigationFeature.DefinitionAsync(document, offset, ct).ConfigureAwait(false), [],
            async (mapping, offset, _, ct) => await _razor.DefinitionAsync(mapping, offset, ct).ConfigureAwait(false),
            async (xaml, offset, _, ct) => await Xaml.XamlNavigation.DefinitionAsync(xaml, offset, ct).ConfigureAwait(false));
        OnPosition(Methods.Implementation, json.TextDocumentPositionParams, json.LocationArray, p => (p.TextDocument, p.Position),
            async (document, offset, _, ct) => await NavigationFeature.ImplementationAsync(document, offset, ct).ConfigureAwait(false), []);
        OnPosition(Methods.DocumentHighlight, json.TextDocumentPositionParams, json.DocumentHighlightArray, p => (p.TextDocument, p.Position),
            async (document, offset, _, ct) => await NavigationFeature.HighlightsAsync(document, offset, ct).ConfigureAwait(false), [],
            async (mapping, offset, _, ct) => await _razor.HighlightsAsync(mapping, offset, ct).ConfigureAwait(false), semantics: Semantics.Analysis);
        OnDocument(Methods.FoldingRange, json.FoldingRangeParams, json.FoldingRangeArray, p => p.TextDocument,
            async (document, _, ct) => await FoldingFeature.FoldingRangesAsync(document, ct).ConfigureAwait(false), [],
            (xaml, _, _) => Task.FromResult<FoldingRange[]?>(Xaml.XamlAnalysis.Folding(xaml)));
        OnDocument(Methods.SemanticTokensFull, json.SemanticTokensParams, json.SemanticTokens, p => p.TextDocument,
            async (document, _, ct) => await SemanticTokensFeature.TokensAsync(document, null, ct).ConfigureAwait(false), new SemanticTokens([]), semantics: Semantics.Analysis);
        OnDocument(Methods.SemanticTokensRange, json.SemanticTokensRangeParams, json.SemanticTokens, p => p.TextDocument,
            async (document, p, ct) => await SemanticTokensFeature.TokensAsync(document, p.Range, ct).ConfigureAwait(false), new SemanticTokens([]), semantics: Semantics.Analysis);
        OnDocument(Methods.DocumentOptions, json.DocumentOptionsParams, json.DocumentOptions, p => p.TextDocument,
            DocumentOptionsAsync, new DocumentOptions(true, 4, 4, null));
        OnPosition(Methods.References, json.ReferenceParams, json.LocationArray, p => (p.TextDocument, p.Position),
            async (document, offset, p, ct) => await ReferencesFeature.ReferencesAsync(document, offset, p.Context.IncludeDeclaration, ct).ConfigureAwait(false), [],
            async (mapping, offset, p, ct) => mapping.ToGenerated(offset) is { } generated
                ? await ReferencesFeature.ReferencesAsync(mapping.Generated, generated, p.Context.IncludeDeclaration, ct).ConfigureAwait(false)
                : []);
        OnPosition(Methods.PrepareRename, json.TextDocumentPositionParams, json.PrepareRenameResult, p => (p.TextDocument, p.Position),
            (document, offset, _, ct) => RenameFeature.PrepareAsync(document, offset, ct), null,
            xaml: (xaml, offset, _, ct) => Xaml.XamlNavigation.PrepareRenameAsync(xaml, offset, ct));
        OnPosition(Methods.Rename, json.RenameParams, json.WorkspaceEdit, p => (p.TextDocument, p.Position),
            (document, offset, p, ct) => RenameFeature.RenameAsync(document, offset, p.NewName, _workspace.Version, ct), null,
            xaml: (xaml, offset, p, ct) => Xaml.XamlNavigation.RenameAsync(xaml, offset, p.NewName, _workspace.Version, ct));
        OnDocument(Methods.CodeAction, json.CodeActionParams, json.CodeActionArray, p => p.TextDocument, async (document, p, ct) =>
        {
            var text = await document.GetTextAsync(ct).ConfigureAwait(false);
            return await _codeActions.ActionsAsync(document, Positions.ToSpan(text, p.Range), ct).ConfigureAwait(false);
        }, []);
        _connection.OnRequest(Methods.CodeActionResolve, json.CodeAction, json.CodeAction, (action, ct) => _codeActions.ResolveAsync(action, _workspace.Version, ct));
        _connection.OnRequest(Methods.CodeActionOptions, json.CodeActionOptionsParams, json.CodeActionOptions, (p, _) => Task.FromResult(_codeActions.Options(p.Action)!));
        OnDocument(Methods.Formatting, json.DocumentFormattingParams, json.TextEditArray, p => p.TextDocument,
            async (document, _, ct) => await FormattingFeature.FormatAsync(document, null, ct).ConfigureAwait(false), [],
            (xaml, p, _) => Task.FromResult<TextEdit[]?>(Xaml.XamlAnalysis.Format(xaml, null, p.Options)));
        OnDocument(Methods.RangeFormatting, json.DocumentRangeFormattingParams, json.TextEditArray, p => p.TextDocument, async (document, p, ct) =>
        {
            var text = await document.GetTextAsync(ct).ConfigureAwait(false);
            return await FormattingFeature.FormatAsync(document, Positions.ToSpan(text, p.Range), ct).ConfigureAwait(false);
        }, [], (xaml, p, _) => Task.FromResult<TextEdit[]?>(Xaml.XamlAnalysis.Format(xaml, p.Range, p.Options)));
        OnPosition(Methods.OnTypeFormatting, json.DocumentOnTypeFormattingParams, json.TextEditArray, p => (p.TextDocument, p.Position),
            async (document, offset, p, ct) => await FormattingFeature.OnTypeAsync(document, offset, p.Ch, ct).ConfigureAwait(false), []);
        _connection.OnRequest(Methods.WorkspaceSymbol, json.WorkspaceSymbolParams, json.SymbolInformationArray, (p, ct) =>
            WorkspaceSymbolsFeature.SearchAsync(_workspace.CurrentSolution, _workspace.MiscellaneousProjectId, p.Query, ct));
        _connection.OnRequest(Methods.MetadataText, json.MetadataTextParams, json.MetadataText, (p, _) => Task.FromResult(MetadataFeature.Text(p.TextDocument.Uri)!));
        OnDocument(Methods.SourceGeneratedText, json.SourceGeneratedTextParams, json.SourceGeneratedText, p => p.TextDocument,
            (document, _, ct) => GeneratedTextAsync(document, ct), null);
        _connection.OnRequest(Methods.TestCandidates, json.TestCandidatesParams, json.TestCandidatesResult, (p, ct) =>
            TestCandidatesFeature.FindAsync(_workspace.CurrentSolution, p.TextDocuments, ct));
        _connection.OnRequest(Methods.TypeOutline, json.TypeOutlineParams, json.TypeOutline, async (p, ct) =>
            (await TypeOutlineFeature.OutlineAsync(_workspace.CurrentSolution, p.TextDocument.Uri, p.Position, ct).ConfigureAwait(false))!);

        // ------------------------------------------------------------ M12: structure, hierarchies, inline information

        OnDocument(Methods.DocumentSymbol, json.DocumentSymbolParams, json.DocumentSymbolArray, p => p.TextDocument,
            async (document, _, ct) => await DocumentSymbolsFeature.SymbolsAsync(document, ct).ConfigureAwait(false), [],
            (xaml, _, _) => Task.FromResult<DocumentSymbol[]?>(Xaml.XamlAnalysis.Symbols(xaml)));
        OnPosition(Methods.PrepareCallHierarchy, json.TextDocumentPositionParams, json.CallHierarchyItemArray, p => (p.TextDocument, p.Position),
            async (document, offset, _, ct) => await HierarchyFeature.PrepareCallsAsync(document, offset, ct).ConfigureAwait(false), []);
        _connection.OnRequest(Methods.CallHierarchyIncomingCalls, json.CallHierarchyCallsParams, json.CallHierarchyIncomingCallArray, (p, ct) =>
            HierarchyFeature.IncomingAsync(_workspace.CurrentSolution, p.Item, ct));
        _connection.OnRequest(Methods.CallHierarchyOutgoingCalls, json.CallHierarchyCallsParams, json.CallHierarchyOutgoingCallArray, (p, ct) =>
            HierarchyFeature.OutgoingAsync(_workspace.CurrentSolution, p.Item, ct));
        OnPosition(Methods.PrepareTypeHierarchy, json.TextDocumentPositionParams, json.TypeHierarchyItemArray, p => (p.TextDocument, p.Position),
            async (document, offset, _, ct) => await HierarchyFeature.PrepareTypesAsync(document, offset, ct).ConfigureAwait(false), []);
        _connection.OnRequest(Methods.TypeHierarchySupertypes, json.TypeHierarchyParams, json.TypeHierarchyItemArray, (p, ct) =>
            HierarchyFeature.SupertypesAsync(_workspace.CurrentSolution, p.Item, ct));
        _connection.OnRequest(Methods.TypeHierarchySubtypes, json.TypeHierarchyParams, json.TypeHierarchyItemArray, (p, ct) =>
            HierarchyFeature.SubtypesAsync(_workspace.CurrentSolution, p.Item, ct));
        OnPosition(Methods.GoToBase, json.TextDocumentPositionParams, json.LocationArray, p => (p.TextDocument, p.Position),
            async (document, offset, _, ct) => await HierarchyFeature.BaseAsync(document, offset, ct).ConfigureAwait(false), []);
        OnDocument(Methods.InheritanceMargin, json.InheritanceMarginParams, json.InheritanceMarginItemArray, p => p.TextDocument, async (document, p, ct) =>
        {
            var text = await document.GetTextAsync(ct).ConfigureAwait(false);
            return await HierarchyFeature.MarginAsync(document, Positions.ToSpan(text, p.Range), ct).ConfigureAwait(false);
        }, []);
        OnPosition(Methods.SymbolInfo, json.TextDocumentPositionParams, json.SymbolDetails, p => (p.TextDocument, p.Position),
            (document, offset, _, ct) => HierarchyFeature.InfoAsync(document, offset, ct), null);
        OnDocument(Methods.InlayHint, json.InlayHintParams, json.InlayHintArray, p => p.TextDocument, async (document, p, ct) =>
        {
            var text = await document.GetTextAsync(ct).ConfigureAwait(false);
            return await InlayHintsFeature.HintsAsync(document, Positions.ToSpan(text, p.Range), ct).ConfigureAwait(false);
        }, [], semantics: Semantics.Analysis);
        OnDocument(Methods.CodeLens, json.CodeLensParams, json.CodeLensArray, p => p.TextDocument,
            async (document, _, ct) => await CodeLensFeature.LensesAsync(document, ct).ConfigureAwait(false), []);
        _connection.OnRequest(Methods.CodeLensResolve, json.CodeLens, json.CodeLens, (lens, ct) => CodeLensFeature.ResolveAsync(_workspace.CurrentSolution, lens, ct));
        OnDocument(Methods.SelectionRange, json.SelectionRangeParams, json.SelectionRangeArray, p => p.TextDocument,
            async (document, p, ct) => await EditingFeatures.SelectionRangesAsync(document, p.Positions, ct).ConfigureAwait(false), []);
        OnDocument(Methods.CodeCleanup, json.CodeCleanupParams, json.WorkspaceEdit, p => p.TextDocument,
            (document, p, ct) => _cleanup.CleanUpAsync(document, p, _workspace.Version, ct), null);
        OnPosition(Methods.DocCommentTemplate, json.TextDocumentPositionParams, json.DocCommentTemplate, p => (p.TextDocument, p.Position),
            async (document, _, p, ct) => await EditingFeatures.TemplateAsync(document, p.Position, ct).ConfigureAwait(false), null);
        OnPosition(Methods.ReferenceItems, json.TextDocumentPositionParams, json.ReferenceItemsResult, p => (p.TextDocument, p.Position),
            (document, offset, _, ct) => ReferenceItemsFeature.ReferencesAsync(document, offset, ct), null);
        OnPosition(Methods.SyntaxTree, json.SyntaxTreeParams, json.SyntaxTreeResult, p => (p.TextDocument, p.Position),
            (document, offset, _, ct) => SyntaxVisualizerFeature.TreeAsync(document, offset, ct), null);
        _connection.OnRequest(Methods.CodeMetrics, json.CodeMetricsParams, json.CodeMetricsResult, async (p, ct) =>
        {
            var solution = _workspace.CurrentSolution;
            ProjectId? project = null;
            if (p.TextDocument is { } identifier)
            {
                if (await WorkspaceManager.FindDocumentAsync(solution, identifier.Uri, ct).ConfigureAwait(false) is not { } document)
                {
                    return new CodeMetricsResult([]);
                }

                project = document.Project.Id;
            }

            return await CodeMetricsFeature.MetricsAsync(solution, project, _workspace.MiscellaneousProjectId, ct).ConfigureAwait(false);
        });
        _connection.OnRequest(Methods.AspNetEndpoints, json.AspNetEndpointsParams, json.AspNetEndpointsResult,
            (p, ct) => AspNetEndpointsFeature.FindAsync(_workspace.CurrentSolution, p.Project, ct));
        _connection.OnRequest(Methods.EfContexts, json.EfContextsParams, json.EfDiscoveredContextsResult,
            (p, ct) => EntityFrameworkFeature.FindAsync(_workspace.CurrentSolution, p.Project, ct));
        OnDocument(Methods.XamlLogicalTree, json.XamlLogicalTreeParams, json.XamlLogicalTreeResult, p => p.TextDocument,
            (_, _, _) => Task.FromResult<XamlLogicalTreeResult?>(null), new XamlLogicalTreeResult([]),
            (xaml, _, _) => Task.FromResult<XamlLogicalTreeResult?>(new XamlLogicalTreeResult(Xaml.XamlAnalysis.LogicalTree(xaml))));
    }

    private void ApplySettings(JsonElement settings)
    {
        if (settings.TryGetProperty("analysisScope", out var scope) && scope.GetString() is DiagnosticsService.ScopeSolution or DiagnosticsService.ScopeOpenDocuments)
        {
            _diagnostics.AnalysisScope = scope.GetString()!;
        }

        if (settings.TryGetProperty("sourceLink", out var sourceLink))
        {
            PdbSources.AllowSourceLink = sourceLink.ValueKind == JsonValueKind.True || sourceLink.GetString() == "true";
        }
    }

    private void ProjectsChanged()
    {
        _ = Notify(Methods.WorkspaceStatus, new WorkspaceStatusParams(WorkspaceStates.Loading, _workspace.ProjectCount, 0), ProtocolJsonContext.Default.WorkspaceStatusParams);

        // Open documents may have moved between projects, or their references changed.
        foreach (var path in _workspace.OpenPaths)
        {
            _diagnostics.DocumentChanged(path);
        }

        _diagnostics.ScheduleBackground(_workspace.Loading ? null : TimeSpan.FromMilliseconds(200));
        _xamlDiagnostics.OthersChanged();
    }

    private readonly HashSet<string> _xamlExplained = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Says once per file and state why a XAML file has no type information (not an additional file of a project, or no Avalonia in its references).</summary>
    private void ExplainXaml(Xaml.XamlContext context, Solution solution)
    {
        string? reason = context.Project is null
            ? $"isn't an additional file of a loaded project ({solution.Projects.Count()} projects, {solution.Projects.Sum(p => p.AdditionalDocuments.Count(d => d.FilePath?.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) == true))} .axaml additional files)"
            : context.Types is { IsAvalonia: false }
                ? $"is in {context.Project.Name}, whose compilation has no Avalonia ({context.Project.MetadataReferences.Count} references, {context.Project.MetadataReferences.Count(r => r.Display?.Contains("Avalonia", StringComparison.OrdinalIgnoreCase) == true)} of Avalonia)"
                : null;
        lock (_xamlExplained)
        {
            if (reason is null ? !_xamlExplained.Remove(context.Path) : !_xamlExplained.Add(context.Path))
            {
                return;
            }
        }

        Log(MessageType.Log, reason is null ? $"XAML: {Path.GetFileName(context.Path)} has its project's types now." : $"XAML: {Path.GetFileName(context.Path)} {reason}.");
    }

    /// <summary>An open XAML file changed (its own diagnostics), or another file did (theirs follow C#).</summary>
    private void XamlChanged(string path)
    {
        if (Xaml.XamlContext.IsXaml(path))
        {
            _xamlDiagnostics.DocumentChanged(path);
        }
        else
        {
            _xamlDiagnostics.OthersChanged();
        }
    }

    /// <summary>
    /// A request about a position in a document. The solution is read when the request arrives. In a Razor file, the
    /// request goes to <paramref name="razor"/> (ADR 0023) with the file's mapping to its generated C#.
    /// </summary>
    private void OnPosition<TParams, TResult>(
        string method,
        JsonTypeInfo<TParams> paramsInfo,
        JsonTypeInfo<TResult> resultInfo,
        Func<TParams, (TextDocumentIdentifier Document, Position Position)> where,
        Func<Document, int, TParams, CancellationToken, Task<TResult?>> handler,
        TResult? empty,
        Func<Razor.RazorMapping, int, TParams, CancellationToken, Task<TResult?>>? razor = null,
        Func<Xaml.XamlContext, int, TParams, CancellationToken, Task<TResult?>>? xaml = null,
        Semantics semantics = Semantics.Workspace)
    {
        _connection.OnRequest(method, paramsInfo, resultInfo, async (p, ct) =>
        {
            var (identifier, position) = where(p);
            var solution = await SolutionForAsync(identifier, semantics, ct).ConfigureAwait(false);
            if (DocumentUris.ToPath(identifier.Uri) is { } xamlPath && Xaml.XamlContext.IsXaml(xamlPath))
            {
                // Avalonia XAML (ADR 0047): its own features, never C#'s on a Miscellaneous File.
                var context = xaml is null ? null : await Xaml.XamlContext.CreateAsync(solution, xamlPath, _workspace.OpenText(xamlPath), ct).ConfigureAwait(false);
                if (context is not null)
                {
                    ExplainXaml(context, solution);
                }

                return context is not null ? await xaml!(context, context.OffsetOf(position), p, ct).ConfigureAwait(false) ?? empty! : empty!;
            }

            if (razor is not null && DocumentUris.ToPath(identifier.Uri) is { } path && Razor.RazorFiles.IsRazor(path))
            {
                return await Razor.RazorMapping.CreateAsync(solution, path, ct).ConfigureAwait(false) is { } mapping
                    ? await razor(mapping, Positions.ToOffset(mapping.RazorText, position), p, ct).ConfigureAwait(false) ?? empty!
                    : empty!;
            }

            if (await WorkspaceManager.FindDocumentAsync(solution, identifier.Uri, ct).ConfigureAwait(false) is not { } document)
            {
                return empty!;
            }


            var text = await document.GetTextAsync(ct).ConfigureAwait(false);
            return await handler(document, Positions.ToOffset(text, position), p, ct).ConfigureAwait(false) ?? empty!;
        });
    }

    private void OnDocument<TParams, TResult>(
        string method,
        JsonTypeInfo<TParams> paramsInfo,
        JsonTypeInfo<TResult> resultInfo,
        Func<TParams, TextDocumentIdentifier> where,
        Func<Document, TParams, CancellationToken, Task<TResult?>> handler,
        TResult? empty,
        Func<Xaml.XamlContext, TParams, CancellationToken, Task<TResult?>>? xaml = null,
        Semantics semantics = Semantics.Workspace)
    {
        _connection.OnRequest(method, paramsInfo, resultInfo, async (p, ct) =>
        {
            var solution = await SolutionForAsync(where(p), semantics, ct).ConfigureAwait(false);
            if (DocumentUris.ToPath(where(p).Uri) is { } xamlPath && Xaml.XamlContext.IsXaml(xamlPath))
            {
                return xaml is not null && await Xaml.XamlContext.CreateAsync(solution, xamlPath, _workspace.OpenText(xamlPath), ct).ConfigureAwait(false) is { } context
                    ? await xaml(context, p, ct).ConfigureAwait(false) ?? empty!
                    : empty!;
            }

            return await WorkspaceManager.FindDocumentAsync(solution, where(p).Uri, ct).ConfigureAwait(false) is { } document
                ? await handler(document, p, ct).ConfigureAwait(false) ?? empty!
                : empty!;
        });
    }

    /// <summary>Which solution a request reads (M34c).</summary>
    private enum Semantics
    {
        /// <summary>The workspace's: navigation and refactoring, whose symbols must be the projects' sources.</summary>
        Workspace,

        /// <summary>The <see cref="AnalysisSolution"/> while the document's references haven't compiled: the document's own semantics.</summary>
        Analysis,

    }

    private async ValueTask<Solution> SolutionForAsync(TextDocumentIdentifier identifier, Semantics semantics, CancellationToken cancellationToken) =>
        semantics != Semantics.Workspace && DocumentUris.ToPath(identifier.Uri) is { } path && !Xaml.XamlContext.IsXaml(path)
            ? await _analysis.ForDocumentAsync(Path.GetFullPath(path), cancellationToken).ConfigureAwait(false)
            : _workspace.CurrentSolution;

    private static async Task<DocumentOptions?> DocumentOptionsAsync(Document document, DocumentOptionsParams parameters, CancellationToken cancellationToken)
    {
        var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
        if (tree is null)
        {
            return null;
        }

        var options = document.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GetOptions(tree);
        var insertSpaces = !(options.TryGetValue("indent_style", out var style) && style.Equals("tab", StringComparison.OrdinalIgnoreCase));
        var tabSize = options.TryGetValue("tab_width", out var tabWidth) && int.TryParse(tabWidth, out var width) && width > 0 ? width : 4;
        var indentSize = options.TryGetValue("indent_size", out var indent) && int.TryParse(indent, out var size) && size > 0 ? size : tabSize;
        string? newLine = options.TryGetValue("end_of_line", out var eol) ? eol.ToLowerInvariant() switch
        {
            "lf" => "\n",
            "crlf" => "\r\n",
            "cr" => "\r",
            _ => null,
        } : null;
        return new DocumentOptions(insertSpaces, tabSize, indentSize, newLine);
    }

    private static async Task<SourceGeneratedText?> GeneratedTextAsync(Document document, CancellationToken cancellationToken)
    {
        if (document is not SourceGeneratedDocument generated)
        {
            return null;
        }

        // The virtual path is <base>/<generator assembly>/<generator type>/<hint name>.
        var text = await generated.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var generator = Path.GetFileName(Path.GetDirectoryName(generated.FilePath)) ?? string.Empty;
        return new SourceGeneratedText(text.ToString(), generated.HintName, generator);
    }

    private Task Notify<T>(string method, T parameters, JsonTypeInfo<T> info)
    {
        try
        {
            return _connection.NotifyAsync(method, parameters, info, CancellationToken.None);
        }
        catch (Exception ex) when (ex is JsonRpcConnectionLostException or ObjectDisposedException)
        {
            return Task.CompletedTask;
        }
    }

    private void Log(int type, string message)
    {
        Console.Error.WriteLine(message);
        _ = Notify(Methods.LogMessage, new LogMessageParams(type, message), ProtocolJsonContext.Default.LogMessageParams);
    }

    public ValueTask DisposeAsync()
    {
        _diagnostics.Dispose();
        _xamlDiagnostics.Dispose();
        _workspace.Dispose();
        return ValueTask.CompletedTask;
    }
}
