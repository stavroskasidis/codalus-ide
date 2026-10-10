using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DaedalusStudio.LanguageServices.Protocol;
using DaedalusStudio.Rpc;

namespace DaedalusStudio.LanguageServices.Client;

/// <summary>Locates the C# language service host.</summary>
public static class HostLaunchInfo
{
    public const string EnvironmentVariable = "DAEDALUS_LSHOST";

    /// <summary>
    /// Resolution order: <c>DAEDALUS_LSHOST</c> (an apphost or a .dll), then <c>lshost/</c> next to the shell.
    /// </summary>
    public static ProcessLaunchInfo? Resolve()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnv)
        {
            candidates.Add(fromEnv);
        }

        var exe = OperatingSystem.IsWindows() ? "daedalus-lshost.exe" : "daedalus-lshost";
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "lshost", exe));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "lshost", "daedalus-lshost.dll"));
        return candidates.FirstOrDefault(File.Exists) is { } path ? ProcessLaunchInfo.FromPath(path) : null;
    }
}

/// <summary>An open document as the client knows it, for replay after a server restart.</summary>
public sealed record OpenDocumentState(string Uri, string LanguageId, int Version, string Text);

/// <summary>
/// A supervised LSP client (ADR 0005), AOT-safe. It speaks standard LSP plus the <c>daedalus/</c> extensions that a
/// server announces support for by handling them. When the server restarts, the client re-sends the configuration,
/// the projects (<see cref="ProjectsProvider"/>) and the open documents (<see cref="OpenDocumentsProvider"/>).
/// Notifications and requests are written in call order, so a request always follows the edits made before it.
/// </summary>
public sealed class LanguageHostClient : IAsyncDisposable
{
    private readonly SupervisedProcess _process;
    private readonly string _clientName;
    private JsonElement? _settings;

    public LanguageHostClient(string name, ProcessLaunchInfo launch, string clientName = "daedalus", SupervisionPolicy? policy = null)
    {
        _clientName = clientName;
        _process = new SupervisedProcess(name, launch, policy)
        {
            ConfigureConnection = Configure,
            Initialize = InitializeAsync,
        };
        _process.Crashed += (_, e) => Crashed?.Invoke(this, e);
        _process.Restarted += (_, _) => Restarted?.Invoke(this, EventArgs.Empty);
        _process.Log += (_, line) => Log?.Invoke(this, line);
    }

    public ServerCapabilities? Capabilities { get; private set; }

    public SemanticTokensLegend? Legend => Capabilities?.SemanticTokensProvider?.Legend;

    public int? ProcessId => _process.ProcessId;

    public bool IsRunning => _process.IsRunning;

    /// <summary>Open documents with their current text, replayed after a restart.</summary>
    public Func<IReadOnlyList<OpenDocumentState>>? OpenDocumentsProvider { get; set; }

    /// <summary>The current project data, replayed after a restart.</summary>
    public Func<IReadOnlyList<ProjectData>>? ProjectsProvider { get; set; }

    public event EventHandler<PublishDiagnosticsParams>? Diagnostics;

    public event EventHandler<WorkspaceStatusParams>? WorkspaceStatus;

    /// <summary>Server log messages and process output.</summary>
    public event EventHandler<string>? Log;

    public event EventHandler<ProcessCrashedEventArgs>? Crashed;

    /// <summary>Raised after a restart, once projects and documents have been replayed.</summary>
    public event EventHandler? Restarted;

    public Task StartAsync(CancellationToken cancellationToken) => _process.StartAsync(cancellationToken);

    /// <summary>
    /// Sent as <c>initializationOptions</c> instead of the settings (servers contributed by plugins declare their own,
    /// ADR 0016).
    /// </summary>
    public JsonElement? InitializationOptions { get; set; }

    /// <summary>Answers <c>workspace/configuration</c> for a section (null: no value).</summary>
    public Func<string?, JsonElement?>? ConfigurationProvider { get; set; }

    /// <summary>
    /// Answers the markup host's <c>daedalus/msbuild/property</c> (a project's evaluated property, ADR 0016):
    /// <c>{ project, name }</c> → <c>{ value, origin }</c>, or null when unknown.
    /// </summary>
    public Func<string, string, CancellationToken, Task<(string? Value, string? Origin)?>>? PropertyEvaluator { get; set; }

    /// <summary>The workspace folder sent as <c>rootUri</c> and <c>workspaceFolders</c>.</summary>
    public string? RootUri { get; set; }

    /// <summary>What this client supports, so servers built for any LSP client send results it can use.</summary>
    private const string ClientCapabilities = """
        {
          "workspace": { "configuration": true, "workspaceFolders": true, "symbol": {}, "workspaceEdit": { "documentChanges": true } },
          "textDocument": {
            "synchronization": { "didSave": true, "dynamicRegistration": false },
            "completion": { "completionItem": { "snippetSupport": false, "documentationFormat": ["markdown", "plaintext"], "resolveSupport": { "properties": ["documentation", "detail", "textEdit", "additionalTextEdits"] }, "insertReplaceSupport": false }, "contextSupport": true },
            "hover": { "contentFormat": ["markdown", "plaintext"] },
            "signatureHelp": { "signatureInformation": { "documentationFormat": ["markdown", "plaintext"], "parameterInformation": { "labelOffsetSupport": true } }, "contextSupport": true },
            "definition": { "linkSupport": false },
            "implementation": { "linkSupport": false },
            "references": {},
            "documentHighlight": {},
            "foldingRange": { "lineFoldingOnly": false },
            "rename": { "prepareSupport": true },
            "codeAction": { "codeActionLiteralSupport": { "codeActionKind": { "valueSet": ["quickfix", "refactor", "refactor.extract", "refactor.inline", "refactor.rewrite", "source", "source.organizeImports"] } }, "resolveSupport": { "properties": ["edit"] } },
            "formatting": {},
            "rangeFormatting": {},
            "onTypeFormatting": {},
            "publishDiagnostics": { "relatedInformation": false, "tagSupport": { "valueSet": [1, 2] }, "codeDescriptionSupport": true },
            "semanticTokens": {
              "requests": { "full": true, "range": true },
              "tokenTypes": ["namespace", "type", "class", "enum", "interface", "struct", "typeParameter", "parameter", "variable", "property", "enumMember", "event", "function", "method", "macro", "keyword", "modifier", "comment", "string", "number", "regexp", "operator", "decorator"],
              "tokenModifiers": ["declaration", "definition", "readonly", "static", "deprecated", "abstract", "async", "modification", "documentation", "defaultLibrary"],
              "formats": ["relative"]
            }
          },
          "window": { "workDoneProgress": true, "showMessage": {} }
        }
        """;

    private void Configure(JsonRpcConnection connection)
    {
        var json = ProtocolJsonContext.Default;
        connection.OnNotification(Methods.PublishDiagnostics, json.JsonElement, raw =>
        {
            if (LspNormalizer.PublishDiagnostics(raw).Deserialize(json.PublishDiagnosticsParams) is { } p)
            {
                Diagnostics?.Invoke(this, p);
            }
        });

        // Requests and notifications other servers send to any LSP client.
        connection.OnRequest("workspace/configuration", (parameters, _) =>
        {
            var items = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("items", out var i) && i.ValueKind == JsonValueKind.Array ? i : default;
            var values = new List<string>();
            foreach (var item in items.ValueKind == JsonValueKind.Array ? items.EnumerateArray() : default)
            {
                var section = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("section", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                values.Add(ConfigurationProvider?.Invoke(section) is { } value ? value.GetRawText() : "null");
            }

            using var document = JsonDocument.Parse("[" + string.Join(',', values) + "]");
            return Task.FromResult<JsonElement?>(document.RootElement.Clone());
        });
        connection.OnRequest("daedalus/msbuild/property", async (parameters, ct) =>
        {
            if (PropertyEvaluator is not { } evaluate
                || !parameters.TryGetProperty("project", out var project) || project.ValueKind != JsonValueKind.String
                || !parameters.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                || await evaluate(project.GetString()!, name.GetString()!, ct).ConfigureAwait(false) is not { } result)
            {
                return null;
            }

            var node = new System.Text.Json.Nodes.JsonObject { ["value"] = result.Value, ["origin"] = result.Origin };
            using var document = JsonDocument.Parse(node.ToJsonString());
            return document.RootElement.Clone();
        });
        foreach (var accepted in new[] { "client/registerCapability", "client/unregisterCapability", "window/workDoneProgress/create", "workspace/semanticTokens/refresh", "workspace/codeLens/refresh", "workspace/inlayHint/refresh", "workspace/diagnostic/refresh" })
        {
            connection.OnRequest(accepted, (_, _) => Task.FromResult<JsonElement?>(null));
        }

        connection.OnRequest("window/showMessageRequest", (parameters, _) =>
        {
            Log?.Invoke(this, parameters.TryGetProperty("message", out var m) ? m.GetString() ?? string.Empty : string.Empty);
            return Task.FromResult<JsonElement?>(null);
        });
        connection.OnNotification("window/showMessage", json.LogMessageParams, p => Log?.Invoke(this, p.Message));
        connection.OnNotification("$/progress", json.JsonElement, _ => { });
        connection.OnNotification("telemetry/event", json.JsonElement, _ => { });
        connection.OnNotification(Methods.WorkspaceStatus, json.WorkspaceStatusParams, p => WorkspaceStatus?.Invoke(this, p));
        connection.OnNotification(Methods.LogMessage, json.LogMessageParams, p => Log?.Invoke(this, p.Message));
    }

    private async Task InitializeAsync(JsonRpcConnection connection, bool isRestart, CancellationToken cancellationToken)
    {
        var json = ProtocolJsonContext.Default;
        using var capabilities = JsonDocument.Parse(ClientCapabilities);
        var parameters = JsonSerializer.SerializeToElement(
            new InitializeParams(Environment.ProcessId, new ClientInfo(_clientName, null), RootUri, capabilities.RootElement.Clone(), InitializationOptions ?? _settings),
            json.InitializeParams);
        if (RootUri is not null)
        {
            // workspaceFolders isn't in the narrowed InitializeParams; add it to the JSON.
            var node = System.Text.Json.Nodes.JsonNode.Parse(parameters.GetRawText())!;
            node["workspaceFolders"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["uri"] = RootUri, ["name"] = Path.GetFileName(RootUri.TrimEnd('/')) });
            using var withFolders = JsonDocument.Parse(node.ToJsonString());
            parameters = withFolders.RootElement.Clone();
        }

        var raw = await connection.InvokeRawAsync(Methods.Initialize, parameters, cancellationToken).ConfigureAwait(false);
        var result = LspNormalizer.Result(Methods.Initialize, raw).Deserialize(json.InitializeResult)
            ?? throw new InvalidOperationException("The language server returned no capabilities.");
        Capabilities = result.Capabilities;
        await connection.NotifyAsync(Methods.Initialized, new EmptyObject(), json.EmptyObject, cancellationToken).ConfigureAwait(false);
        if (!isRestart)
        {
            return;
        }

        if (ProjectsProvider?.Invoke() is { Count: > 0 } projects)
        {
            await connection.NotifyAsync(Methods.ProjectsUpdate, new ProjectsUpdateParams([.. projects]), json.ProjectsUpdateParams, cancellationToken).ConfigureAwait(false);
        }

        foreach (var document in OpenDocumentsProvider?.Invoke() ?? [])
        {
            await connection.NotifyAsync(
                Methods.DidOpen,
                new DidOpenTextDocumentParams(new TextDocumentItem(document.Uri, document.LanguageId, document.Version, document.Text)),
                json.DidOpenTextDocumentParams,
                cancellationToken).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ notifications

    /// <summary>Server settings (<c>workspace/didChangeConfiguration</c>); also sent as initialization options after a restart.</summary>
    public Task ConfigureAsync(JsonElement settings, CancellationToken cancellationToken)
    {
        _settings = settings;
        return Notify(Methods.DidChangeConfiguration, new DidChangeConfigurationParams(settings), ProtocolJsonContext.Default.DidChangeConfigurationParams, cancellationToken);
    }

    public Task DidOpenAsync(OpenDocumentState document, CancellationToken cancellationToken) =>
        Notify(Methods.DidOpen, new DidOpenTextDocumentParams(new TextDocumentItem(document.Uri, document.LanguageId, document.Version, document.Text)), ProtocolJsonContext.Default.DidOpenTextDocumentParams, cancellationToken);

    public Task DidChangeAsync(string uri, int version, TextDocumentContentChangeEvent[] changes, CancellationToken cancellationToken) =>
        Notify(Methods.DidChange, new DidChangeTextDocumentParams(new VersionedTextDocumentIdentifier(uri, version), changes), ProtocolJsonContext.Default.DidChangeTextDocumentParams, cancellationToken);

    public Task DidCloseAsync(string uri, CancellationToken cancellationToken) =>
        Notify(Methods.DidClose, new DidCloseTextDocumentParams(new TextDocumentIdentifier(uri)), ProtocolJsonContext.Default.DidCloseTextDocumentParams, cancellationToken);

    public Task DidSaveAsync(string uri, CancellationToken cancellationToken) =>
        Notify(Methods.DidSave, new DidSaveTextDocumentParams(new TextDocumentIdentifier(uri)), ProtocolJsonContext.Default.DidSaveTextDocumentParams, cancellationToken);

    public Task UpdateProjectsAsync(IReadOnlyList<ProjectData> projects, CancellationToken cancellationToken) =>
        Notify(Methods.ProjectsUpdate, new ProjectsUpdateParams([.. projects]), ProtocolJsonContext.Default.ProjectsUpdateParams, cancellationToken);

    public Task RemoveProjectsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken) =>
        Notify(Methods.ProjectsRemove, new ProjectsRemoveParams([.. paths]), ProtocolJsonContext.Default.ProjectsRemoveParams, cancellationToken);

    private Task Notify<T>(string method, T parameters, JsonTypeInfo<T> info, CancellationToken cancellationToken) =>
        _process.IsRunning ? _process.Connection.NotifyAsync(method, parameters, info, cancellationToken) : Task.CompletedTask;

    // ------------------------------------------------------------------ requests

    public Task<CompletionList> CompletionAsync(string uri, Position position, CompletionContext? context, CancellationToken cancellationToken) =>
        Invoke(Methods.Completion, new CompletionParams(new TextDocumentIdentifier(uri), position, context), ProtocolJsonContext.Default.CompletionParams, ProtocolJsonContext.Default.CompletionList, cancellationToken);

    public Task<CompletionItem> ResolveCompletionAsync(CompletionItem item, CancellationToken cancellationToken) =>
        Invoke(Methods.CompletionResolve, item, ProtocolJsonContext.Default.CompletionItem, ProtocolJsonContext.Default.CompletionItem, cancellationToken);

    public Task<Hover?> HoverAsync(string uri, Position position, CancellationToken cancellationToken) =>
        InvokeNullable(Methods.Hover, At(uri, position), ProtocolJsonContext.Default.TextDocumentPositionParams, ProtocolJsonContext.Default.Hover, cancellationToken);

    public Task<SignatureHelp?> SignatureHelpAsync(string uri, Position position, SignatureHelpContext? context, CancellationToken cancellationToken) =>
        InvokeNullable(Methods.SignatureHelp, new SignatureHelpParams(new TextDocumentIdentifier(uri), position, context), ProtocolJsonContext.Default.SignatureHelpParams, ProtocolJsonContext.Default.SignatureHelp, cancellationToken);

    public async Task<Location[]> DefinitionAsync(string uri, Position position, CancellationToken cancellationToken) =>
        await InvokeNullable(Methods.Definition, At(uri, position), ProtocolJsonContext.Default.TextDocumentPositionParams, ProtocolJsonContext.Default.LocationArray, cancellationToken).ConfigureAwait(false) ?? [];

    public async Task<Location[]> ImplementationAsync(string uri, Position position, CancellationToken cancellationToken) =>
        await InvokeNullable(Methods.Implementation, At(uri, position), ProtocolJsonContext.Default.TextDocumentPositionParams, ProtocolJsonContext.Default.LocationArray, cancellationToken).ConfigureAwait(false) ?? [];

    public async Task<DocumentHighlight[]> DocumentHighlightsAsync(string uri, Position position, CancellationToken cancellationToken) =>
        await InvokeNullable(Methods.DocumentHighlight, At(uri, position), ProtocolJsonContext.Default.TextDocumentPositionParams, ProtocolJsonContext.Default.DocumentHighlightArray, cancellationToken).ConfigureAwait(false) ?? [];

    public async Task<FoldingRange[]> FoldingRangesAsync(string uri, CancellationToken cancellationToken) =>
        await InvokeNullable(Methods.FoldingRange, new FoldingRangeParams(new TextDocumentIdentifier(uri)), ProtocolJsonContext.Default.FoldingRangeParams, ProtocolJsonContext.Default.FoldingRangeArray, cancellationToken).ConfigureAwait(false) ?? [];

    public Task<SemanticTokens> SemanticTokensAsync(string uri, Protocol.Range? range, CancellationToken cancellationToken) =>
        range is null
            ? Invoke(Methods.SemanticTokensFull, new SemanticTokensParams(new TextDocumentIdentifier(uri)), ProtocolJsonContext.Default.SemanticTokensParams, ProtocolJsonContext.Default.SemanticTokens, cancellationToken)
            : Invoke(Methods.SemanticTokensRange, new SemanticTokensRangeParams(new TextDocumentIdentifier(uri), range), ProtocolJsonContext.Default.SemanticTokensRangeParams, ProtocolJsonContext.Default.SemanticTokens, cancellationToken);

    public Task<DocumentOptions?> DocumentOptionsAsync(string uri, CancellationToken cancellationToken) =>
        InvokeNullable(Methods.DocumentOptions, new DocumentOptionsParams(new TextDocumentIdentifier(uri)), ProtocolJsonContext.Default.DocumentOptionsParams, ProtocolJsonContext.Default.DocumentOptions, cancellationToken);

    public Task<SourceGeneratedText?> SourceGeneratedTextAsync(string uri, CancellationToken cancellationToken) =>
        InvokeNullable(Methods.SourceGeneratedText, new SourceGeneratedTextParams(new TextDocumentIdentifier(uri)), ProtocolJsonContext.Default.SourceGeneratedTextParams, ProtocolJsonContext.Default.SourceGeneratedText, cancellationToken);

    public async Task<Location[]> ReferencesAsync(string uri, Position position, bool includeDeclaration, CancellationToken cancellationToken) =>
        await InvokeNullable(Methods.References, new ReferenceParams(new TextDocumentIdentifier(uri), position, new ReferenceContext(includeDeclaration)), ProtocolJsonContext.Default.ReferenceParams, ProtocolJsonContext.Default.LocationArray, cancellationToken).ConfigureAwait(false) ?? [];

    public Task<PrepareRenameResult?> PrepareRenameAsync(string uri, Position position, CancellationToken cancellationToken) =>
        InvokeNullable(Methods.PrepareRename, At(uri, position), ProtocolJsonContext.Default.TextDocumentPositionParams, ProtocolJsonContext.Default.PrepareRenameResult, cancellationToken);

    public Task<WorkspaceEdit?> RenameAsync(string uri, Position position, string newName, CancellationToken cancellationToken) =>
        InvokeNullable(Methods.Rename, new RenameParams(new TextDocumentIdentifier(uri), position, newName), ProtocolJsonContext.Default.RenameParams, ProtocolJsonContext.Default.WorkspaceEdit, cancellationToken);

    public async Task<CodeAction[]> CodeActionsAsync(string uri, Protocol.Range range, Diagnostic[] diagnostics, CancellationToken cancellationToken) =>
        await InvokeNullable(Methods.CodeAction, new CodeActionParams(new TextDocumentIdentifier(uri), range, new CodeActionContext(diagnostics, null)), ProtocolJsonContext.Default.CodeActionParams, ProtocolJsonContext.Default.CodeActionArray, cancellationToken).ConfigureAwait(false) ?? [];

    public Task<CodeAction> ResolveCodeActionAsync(CodeAction action, CancellationToken cancellationToken) =>
        Invoke(Methods.CodeActionResolve, action, ProtocolJsonContext.Default.CodeAction, ProtocolJsonContext.Default.CodeAction, cancellationToken);

    public async Task<TextEdit[]> FormatAsync(string uri, Protocol.Range? range, FormattingOptions options, CancellationToken cancellationToken) =>
        (range is null
            ? await InvokeNullable(Methods.Formatting, new DocumentFormattingParams(new TextDocumentIdentifier(uri), options), ProtocolJsonContext.Default.DocumentFormattingParams, ProtocolJsonContext.Default.TextEditArray, cancellationToken).ConfigureAwait(false)
            : await InvokeNullable(Methods.RangeFormatting, new DocumentRangeFormattingParams(new TextDocumentIdentifier(uri), range, options), ProtocolJsonContext.Default.DocumentRangeFormattingParams, ProtocolJsonContext.Default.TextEditArray, cancellationToken).ConfigureAwait(false))
        ?? [];

    public async Task<TextEdit[]> FormatOnTypeAsync(string uri, Position position, string ch, FormattingOptions options, CancellationToken cancellationToken) =>
        await InvokeNullable(Methods.OnTypeFormatting, new DocumentOnTypeFormattingParams(new TextDocumentIdentifier(uri), position, ch, options), ProtocolJsonContext.Default.DocumentOnTypeFormattingParams, ProtocolJsonContext.Default.TextEditArray, cancellationToken).ConfigureAwait(false) ?? [];

    public async Task<SymbolInformation[]> WorkspaceSymbolsAsync(string query, CancellationToken cancellationToken) =>
        await InvokeNullable(Methods.WorkspaceSymbol, new WorkspaceSymbolParams(query), ProtocolJsonContext.Default.WorkspaceSymbolParams, ProtocolJsonContext.Default.SymbolInformationArray, cancellationToken).ConfigureAwait(false) ?? [];

    public Task<MetadataText?> MetadataTextAsync(string uri, CancellationToken cancellationToken) =>
        InvokeNullable(Methods.MetadataText, new MetadataTextParams(new TextDocumentIdentifier(uri)), ProtocolJsonContext.Default.MetadataTextParams, ProtocolJsonContext.Default.MetadataText, cancellationToken);

    /// <summary>Test methods and classes in the given documents, from syntax (ADR 0010).</summary>
    public Task<TestCandidatesResult> TestCandidatesAsync(IReadOnlyList<string> uris, CancellationToken cancellationToken) =>
        Invoke(Methods.TestCandidates, new TestCandidatesParams([.. uris.Select(u => new TextDocumentIdentifier(u))]), ProtocolJsonContext.Default.TestCandidatesParams, ProtocolJsonContext.Default.TestCandidatesResult, cancellationToken);

    /// <summary>The type around a position and its methods (Create Unit Tests), or null.</summary>
    public Task<TypeOutline?> TypeOutlineAsync(string uri, Position position, CancellationToken cancellationToken) =>
        InvokeNullable(Methods.TypeOutline, new TypeOutlineParams(new TextDocumentIdentifier(uri), position), ProtocolJsonContext.Default.TypeOutlineParams, ProtocolJsonContext.Default.TypeOutline, cancellationToken);

    // ------------------------------------------------------------------ M12: structure, hierarchies, inline information

    public async Task<DocumentSymbol[]> DocumentSymbolsAsync(string uri, CancellationToken cancellationToken) =>
        await InvokeNullable(Methods.DocumentSymbol, new DocumentSymbolParams(new TextDocumentIdentifier(uri)), ProtocolJsonContext.Default.DocumentSymbolParams, ProtocolJsonContext.Default.DocumentSymbolArray, cancellationToken).ConfigureAwait(false) ?? [];

    public async Task<CallHierarchyItem[]> PrepareCallHierarchyAsync(string uri, Position position, CancellationToken cancellationToken) =>
        await InvokeNullable(Methods.PrepareCallHierarchy, At(uri, position), ProtocolJsonContext.Default.TextDocumentPositionParams, ProtocolJsonContext.Default.CallHierarchyItemArray, cancellationToken).ConfigureAwait(false) ?? [];

    public async Task<CallHierarchyIncomingCall[]> IncomingCallsAsync(CallHierarchyItem item, CancellationToken cancellationToken) =>
        await InvokeNullable(Methods.CallHierarchyIncomingCalls, new CallHierarchyCallsParams(item), ProtocolJsonContext.Default.CallHierarchyCallsParams, ProtocolJsonContext.Default.CallHierarchyIncomingCallArray, cancellationToken).ConfigureAwait(false) ?? [];

    public async Task<CallHierarchyOutgoingCall[]> OutgoingCallsAsync(CallHierarchyItem item, CancellationToken cancellationToken) =>
        await InvokeNullable(Methods.CallHierarchyOutgoingCalls, new CallHierarchyCallsParams(item), ProtocolJsonContext.Default.CallHierarchyCallsParams, ProtocolJsonContext.Default.CallHierarchyOutgoingCallArray, cancellationToken).ConfigureAwait(false) ?? [];

    public async Task<TypeHierarchyItem[]> PrepareTypeHierarchyAsync(string uri, Position position, CancellationToken cancellationToken) =>
        await InvokeNullable(Methods.PrepareTypeHierarchy, At(uri, position), ProtocolJsonContext.Default.TextDocumentPositionParams, ProtocolJsonContext.Default.TypeHierarchyItemArray, cancellationToken).ConfigureAwait(false) ?? [];

    public async Task<TypeHierarchyItem[]> SupertypesAsync(TypeHierarchyItem item, CancellationToken cancellationToken) =>
        await InvokeNullable(Methods.TypeHierarchySupertypes, new TypeHierarchyParams(item), ProtocolJsonContext.Default.TypeHierarchyParams, ProtocolJsonContext.Default.TypeHierarchyItemArray, cancellationToken).ConfigureAwait(false) ?? [];

    public async Task<TypeHierarchyItem[]> SubtypesAsync(TypeHierarchyItem item, CancellationToken cancellationToken) =>
        await InvokeNullable(Methods.TypeHierarchySubtypes, new TypeHierarchyParams(item), ProtocolJsonContext.Default.TypeHierarchyParams, ProtocolJsonContext.Default.TypeHierarchyItemArray, cancellationToken).ConfigureAwait(false) ?? [];

    public async Task<InlayHint[]> InlayHintsAsync(string uri, Protocol.Range range, CancellationToken cancellationToken) =>
        await InvokeNullable(Methods.InlayHint, new InlayHintParams(new TextDocumentIdentifier(uri), range), ProtocolJsonContext.Default.InlayHintParams, ProtocolJsonContext.Default.InlayHintArray, cancellationToken).ConfigureAwait(false) ?? [];

    public async Task<CodeLens[]> CodeLensesAsync(string uri, CancellationToken cancellationToken) =>
        await InvokeNullable(Methods.CodeLens, new CodeLensParams(new TextDocumentIdentifier(uri)), ProtocolJsonContext.Default.CodeLensParams, ProtocolJsonContext.Default.CodeLensArray, cancellationToken).ConfigureAwait(false) ?? [];

    public Task<CodeLens> ResolveCodeLensAsync(CodeLens lens, CancellationToken cancellationToken) =>
        Invoke(Methods.CodeLensResolve, lens, ProtocolJsonContext.Default.CodeLens, ProtocolJsonContext.Default.CodeLens, cancellationToken);

    /// <summary>Find All References with read/write classification (<c>daedalus/references</c>, our hosts only).</summary>
    public Task<ReferenceItemsResult?> ReferenceItemsAsync(string uri, Position position, CancellationToken cancellationToken) =>
        InvokeNullable(Methods.ReferenceItems, At(uri, position), ProtocolJsonContext.Default.TextDocumentPositionParams, ProtocolJsonContext.Default.ReferenceItemsResult, cancellationToken);

    public async Task<Location[]> GoToBaseAsync(string uri, Position position, CancellationToken cancellationToken) =>
        await InvokeNullable(Methods.GoToBase, At(uri, position), ProtocolJsonContext.Default.TextDocumentPositionParams, ProtocolJsonContext.Default.LocationArray, cancellationToken).ConfigureAwait(false) ?? [];

    public async Task<InheritanceMarginItem[]> InheritanceMarginAsync(string uri, Protocol.Range range, CancellationToken cancellationToken) =>
        await InvokeNullable(Methods.InheritanceMargin, new InheritanceMarginParams(new TextDocumentIdentifier(uri), range), ProtocolJsonContext.Default.InheritanceMarginParams, ProtocolJsonContext.Default.InheritanceMarginItemArray, cancellationToken).ConfigureAwait(false) ?? [];

    public Task<SymbolDetails?> SymbolDetailsAsync(string uri, Position position, CancellationToken cancellationToken) =>
        InvokeNullable(Methods.SymbolInfo, At(uri, position), ProtocolJsonContext.Default.TextDocumentPositionParams, ProtocolJsonContext.Default.SymbolDetails, cancellationToken);

    private static TextDocumentPositionParams At(string uri, Position position) => new(new TextDocumentIdentifier(uri), position);

    private async Task<TResult> Invoke<TParams, TResult>(string method, TParams parameters, JsonTypeInfo<TParams> paramsInfo, JsonTypeInfo<TResult> resultInfo, CancellationToken cancellationToken)
    {
        var raw = await _process.Connection.InvokeRawAsync(method, JsonSerializer.SerializeToElement(parameters, paramsInfo), cancellationToken).ConfigureAwait(false);
        return LspNormalizer.Result(method, raw).Deserialize(resultInfo)
            ?? throw new JsonException($"The language server returned no result for {method}.");
    }

    /// <summary>A request whose result may be JSON <c>null</c> (LSP's "nothing here").</summary>
    private async Task<TResult?> InvokeNullable<TParams, TResult>(string method, TParams parameters, JsonTypeInfo<TParams> paramsInfo, JsonTypeInfo<TResult> resultInfo, CancellationToken cancellationToken)
        where TResult : class
    {
        var raw = await _process.Connection.InvokeRawAsync(method, JsonSerializer.SerializeToElement(parameters, paramsInfo), cancellationToken).ConfigureAwait(false);
        var normalized = LspNormalizer.Result(method, raw);
        return normalized.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : normalized.Deserialize(resultInfo);
    }

    public ValueTask DisposeAsync() => _process.DisposeAsync();
}
