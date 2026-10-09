using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Daedalus.LanguageServices.Host.Features;
using Daedalus.LanguageServices.Protocol;
using Daedalus.Rpc;
using Microsoft.CodeAnalysis;

namespace Daedalus.LanguageServices.Host;

/// <summary>
/// The language host's side of the protocol (docs/protocol.md): LSP for C# plus the <c>daedalus/</c> project
/// extensions. Notifications (edits, project updates) are applied in arrival order, and each request reads the
/// solution as it is when the request arrives, so it observes every earlier edit.
/// </summary>
internal sealed class LanguageServer : IAsyncDisposable
{
    private readonly JsonRpcConnection _connection;
    private readonly WorkspaceManager _workspace = new();
    private readonly CompletionFeature _completion = new();
    private readonly DiagnosticsService _diagnostics;
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LanguageServer(JsonRpcConnection connection)
    {
        _connection = connection;
        _diagnostics = new DiagnosticsService(_workspace, p => Notify(Methods.PublishDiagnostics, p, ProtocolJsonContext.Default.PublishDiagnosticsParams), message => Log(MessageType.Warning, message));
        _diagnostics.BackgroundPassCompleted += (projects, documents) =>
            _ = Notify(Methods.WorkspaceStatus, new WorkspaceStatusParams(WorkspaceStates.Ready, projects, documents), ProtocolJsonContext.Default.WorkspaceStatusParams);
        _workspace.Log += (_, message) => Log(MessageType.Warning, message);
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
                },
                new ServerInfo("daedalus-lshost", version)));
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
            }
        });
        _connection.OnNotification(Methods.DidChange, json.DidChangeTextDocumentParams, p =>
        {
            if (DocumentUris.ToPath(p.TextDocument.Uri) is { } path && _workspace.Change(path, p.TextDocument.Version, p.ContentChanges))
            {
                _diagnostics.DocumentChanged(path);
            }
        });
        _connection.OnNotification(Methods.DidClose, json.DidCloseTextDocumentParams, p =>
        {
            if (DocumentUris.ToPath(p.TextDocument.Uri) is { } path)
            {
                _workspace.Close(path);
                _diagnostics.DocumentClosed(path);
            }
        });
        _connection.OnNotification(Methods.DidSave, json.DidSaveTextDocumentParams, _ => { });
        _connection.OnNotification(Methods.ProjectsUpdate, json.ProjectsUpdateParams, p =>
        {
            _workspace.UpdateProjects(p.Projects);
            ProjectsChanged();
        });
        _connection.OnNotification(Methods.ProjectsRemove, json.ProjectsRemoveParams, p =>
        {
            _workspace.RemoveProjects(p.Paths);
            ProjectsChanged();
        });

        // ------------------------------------------------------------ features

        OnPosition(Methods.Completion, json.CompletionParams, json.CompletionList, p => (p.TextDocument, p.Position),
            async (document, offset, p, ct) => await _completion.CompleteAsync(document, offset, p.Context, ct).ConfigureAwait(false), new CompletionList(false, []));
        _connection.OnRequest(Methods.CompletionResolve, json.CompletionItem, json.CompletionItem, (item, ct) => _completion.ResolveAsync(item, ct));
        OnPosition(Methods.Hover, json.TextDocumentPositionParams, json.Hover, p => (p.TextDocument, p.Position),
            (document, offset, _, ct) => HoverFeature.HoverAsync(document, offset, ct), null);
        OnPosition(Methods.SignatureHelp, json.SignatureHelpParams, json.SignatureHelp, p => (p.TextDocument, p.Position),
            (document, offset, _, ct) => SignatureHelpFeature.SignatureHelpAsync(document, offset, ct), null);
        OnPosition(Methods.Definition, json.TextDocumentPositionParams, json.LocationArray, p => (p.TextDocument, p.Position),
            async (document, offset, _, ct) => await NavigationFeature.DefinitionAsync(document, offset, ct).ConfigureAwait(false), []);
        OnPosition(Methods.Implementation, json.TextDocumentPositionParams, json.LocationArray, p => (p.TextDocument, p.Position),
            async (document, offset, _, ct) => await NavigationFeature.ImplementationAsync(document, offset, ct).ConfigureAwait(false), []);
        OnPosition(Methods.DocumentHighlight, json.TextDocumentPositionParams, json.DocumentHighlightArray, p => (p.TextDocument, p.Position),
            async (document, offset, _, ct) => await NavigationFeature.HighlightsAsync(document, offset, ct).ConfigureAwait(false), []);
        OnDocument(Methods.FoldingRange, json.FoldingRangeParams, json.FoldingRangeArray, p => p.TextDocument,
            async (document, _, ct) => await FoldingFeature.FoldingRangesAsync(document, ct).ConfigureAwait(false), []);
        OnDocument(Methods.SemanticTokensFull, json.SemanticTokensParams, json.SemanticTokens, p => p.TextDocument,
            async (document, _, ct) => await SemanticTokensFeature.TokensAsync(document, null, ct).ConfigureAwait(false), new SemanticTokens([]));
        OnDocument(Methods.SemanticTokensRange, json.SemanticTokensRangeParams, json.SemanticTokens, p => p.TextDocument,
            async (document, p, ct) => await SemanticTokensFeature.TokensAsync(document, p.Range, ct).ConfigureAwait(false), new SemanticTokens([]));
        OnDocument(Methods.DocumentOptions, json.DocumentOptionsParams, json.DocumentOptions, p => p.TextDocument,
            DocumentOptionsAsync, new DocumentOptions(true, 4, 4, null));
        OnDocument(Methods.SourceGeneratedText, json.SourceGeneratedTextParams, json.SourceGeneratedText, p => p.TextDocument,
            (document, _, ct) => GeneratedTextAsync(document, ct), null);
    }

    private void ApplySettings(JsonElement settings)
    {
        if (settings.TryGetProperty("analysisScope", out var scope) && scope.GetString() is DiagnosticsService.ScopeSolution or DiagnosticsService.ScopeOpenDocuments)
        {
            _diagnostics.AnalysisScope = scope.GetString()!;
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

        _diagnostics.ScheduleBackground();
    }

    /// <summary>A request about a position in a document. The solution is read when the request arrives.</summary>
    private void OnPosition<TParams, TResult>(
        string method,
        JsonTypeInfo<TParams> paramsInfo,
        JsonTypeInfo<TResult> resultInfo,
        Func<TParams, (TextDocumentIdentifier Document, Position Position)> where,
        Func<Document, int, TParams, CancellationToken, Task<TResult?>> handler,
        TResult? empty)
    {
        _connection.OnRequest(method, paramsInfo, resultInfo, async (p, ct) =>
        {
            var solution = _workspace.CurrentSolution;
            var (identifier, position) = where(p);
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
        TResult? empty)
    {
        _connection.OnRequest(method, paramsInfo, resultInfo, async (p, ct) =>
        {
            var solution = _workspace.CurrentSolution;
            return await WorkspaceManager.FindDocumentAsync(solution, where(p).Uri, ct).ConfigureAwait(false) is { } document
                ? await handler(document, p, ct).ConfigureAwait(false) ?? empty!
                : empty!;
        });
    }

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
        _workspace.Dispose();
        return ValueTask.CompletedTask;
    }
}
