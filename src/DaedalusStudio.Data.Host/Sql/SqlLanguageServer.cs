using System.Collections.Concurrent;
using System.Text.Json;
using DaedalusStudio.LanguageServices.Protocol;
using DaedalusStudio.Markup.Host;
using DaedalusStudio.Rpc;

namespace DaedalusStudio.Data.Host.Sql;

/// <summary>
/// The SQL language server (ADR 0038), <c>daedalus-datahost --lsp</c>: completion and hover from the schema the shell
/// sends (<c>daedalus/sql/context</c>), T-SQL diagnostics and formatting through ScriptDom.
/// </summary>
internal sealed class SqlLanguageServer
{
    private static readonly TimeSpan s_debounce = TimeSpan.FromMilliseconds(300);

    private readonly JsonRpcConnection _connection;
    private readonly TextDocuments _documents = new();
    private readonly ConcurrentDictionary<string, SqlContextParams> _contexts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SqlLanguageServer(JsonRpcConnection connection) => _connection = connection;

    public Task ExitRequested => _exit.Task;

    public void Register()
    {
        var json = ProtocolJsonContext.Default;
        _connection.OnRequest(Methods.Initialize, json.InitializeParams, json.InitializeResult, (_, _) => Task.FromResult(new InitializeResult(
            new ServerCapabilities
            {
                TextDocumentSync = TextDocumentSyncKind.Incremental,
                CompletionProvider = new CompletionOptions(["."], null, false),
                HoverProvider = true,
                DocumentFormattingProvider = true,
            },
            new ServerInfo("daedalus-datahost (SQL)", typeof(SqlLanguageServer).Assembly.GetName().Version?.ToString(3)))));
        _connection.OnNotification(Methods.Initialized, json.EmptyObject, _ => { });
        _connection.OnRequest(Methods.Shutdown, (_, _) => Task.FromResult<JsonElement?>(null));
        _connection.OnNotification(Methods.Exit, json.EmptyObject, _ => _exit.TrySetResult());
        _connection.OnNotification(Methods.DidChangeConfiguration, json.DidChangeConfigurationParams, _ => { });
        _connection.OnNotification(Methods.DidOpen, json.DidOpenTextDocumentParams, p => Schedule(_documents.Open(p.TextDocument), TimeSpan.Zero));
        _connection.OnNotification(Methods.DidChange, json.DidChangeTextDocumentParams, p =>
        {
            if (_documents.Change(p.TextDocument.Uri, p.TextDocument.Version, p.ContentChanges) is { } document)
            {
                Schedule(document, s_debounce);
            }
        });
        _connection.OnNotification(Methods.DidClose, json.DidCloseTextDocumentParams, p =>
        {
            _documents.Close(p.TextDocument.Uri);
            _contexts.TryRemove(p.TextDocument.Uri, out _);
            _ = Publish(p.TextDocument.Uri, null, []);
        });
        _connection.OnNotification(Methods.DidSave, json.DidSaveTextDocumentParams, _ => { });
        _connection.OnNotification(Methods.SqlContext, json.SqlContextParams, p =>
        {
            _contexts[p.Uri] = p;
            if (_documents.Get(p.Uri) is { } document)
            {
                Schedule(document, TimeSpan.Zero);
            }
        });
        _connection.OnRequest(Methods.Completion, json.CompletionParams, json.CompletionList, (p, _) =>
            Task.FromResult(_documents.Get(p.TextDocument.Uri) is { } document
                ? SqlCompletion.Complete(document.Text, document.OffsetAt(p.Position), _contexts.GetValueOrDefault(document.Uri))
                : new CompletionList(false, [])));
        _connection.OnRequest(Methods.Hover, (raw, _) =>
        {
            var p = raw.Deserialize(json.TextDocumentPositionParams);
            var hover = p is not null && _documents.Get(p.TextDocument.Uri) is { } document
                ? SqlCompletion.Hover(document.Text, document.OffsetAt(p.Position), _contexts.GetValueOrDefault(document.Uri))
                : null;
            return Task.FromResult<JsonElement?>(hover is null ? null : JsonSerializer.SerializeToElement(hover, json.Hover));
        });
        _connection.OnRequest(Methods.Formatting, json.DocumentFormattingParams, json.TextEditArray, (p, _) =>
        {
            if (_documents.Get(p.TextDocument.Uri) is not { } document || Dialect(document.Uri) != "sqlserver"
                || TSql.Format(document.Text, p.Options.TabSize) is not { } formatted || formatted == document.Text)
            {
                return Task.FromResult<TextEdit[]>([]);
            }

            return Task.FromResult<TextEdit[]>([new TextEdit(document.RangeOf(0, document.Text.Length), formatted)]);
        });
    }

    /// <summary>The document's dialect: its connection's, else T-SQL (a <c>.sqlproj</c>'s scripts and plain .sql files).</summary>
    private string Dialect(string uri) => _contexts.TryGetValue(uri, out var context) ? context.Dialect : "sqlserver";

    private void Schedule(TextDocument document, TimeSpan delay)
    {
        var cancellation = new CancellationTokenSource();
        if (_pending.TryGetValue(document.Uri, out var previous))
        {
            previous.Cancel();
        }

        _pending[document.Uri] = cancellation;
        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellation.Token).ConfigureAwait(false);
                }

                var version = document.Version;
                var diagnostics = Dialect(document.Uri) == "sqlserver" ? TSql.Diagnostics(document.Text) : [];
                if (!cancellation.IsCancellationRequested)
                {
                    await Publish(document.Uri, version, diagnostics).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _ = _connection.NotifyAsync(Methods.LogMessage, new LogMessageParams(MessageType.Log, $"{document.Uri}: {e.Message}"), ProtocolJsonContext.Default.LogMessageParams, CancellationToken.None);
            }
        }, CancellationToken.None);
    }

    private Task Publish(string uri, int? version, Diagnostic[] diagnostics) =>
        _connection.NotifyAsync(Methods.PublishDiagnostics, new PublishDiagnosticsParams(uri, version, diagnostics), ProtocolJsonContext.Default.PublishDiagnosticsParams, CancellationToken.None);
}
