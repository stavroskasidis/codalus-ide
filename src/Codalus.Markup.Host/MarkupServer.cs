using System.Collections.Concurrent;
using System.Text.Json;
using Codalus.LanguageServices.Protocol;
using Codalus.Markup.Host.EditorConfig;
using Codalus.Markup.Host.Json;
using Codalus.Markup.Host.Xml;
using Codalus.Markup.Host.Yaml;
using Codalus.Rpc;

namespace Codalus.Markup.Host;

/// <summary>
/// The markup language server (ADR 0016, docs/protocol.md): LSP for JSON, XML and MSBuild, YAML and
/// <c>.editorconfig</c>. Diagnostics are published 250 ms after the last edit. MSBuild hover asks the shell for evaluated
/// values (<c>codalus/msbuild/property</c>), which it gets from the build host.
/// </summary>
internal sealed class MarkupServer
{
    public const string EvaluatePropertyMethod = "codalus/msbuild/property";
    private static readonly TimeSpan s_debounce = TimeSpan.FromMilliseconds(250);

    private readonly JsonRpcConnection _connection;
    private readonly TextDocuments _documents = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SchemaStore _schemas = new(null, false);
    private JsonLanguage _json;
    private YamlLanguage _yaml;
    private readonly XmlLanguage _xml;

    public MarkupServer(JsonRpcConnection connection)
    {
        _connection = connection;
        _json = new JsonLanguage(_schemas);
        _yaml = new YamlLanguage(_schemas);
        _xml = new XmlLanguage(EvaluateAsync);
    }

    public Task ExitRequested => _exit.Task;

    public void Register()
    {
        var json = ProtocolJsonContext.Default;
        _connection.OnRequest(Methods.Initialize, json.InitializeParams, json.InitializeResult, (p, _) =>
        {
            Configure(p.InitializationOptions);
            var version = typeof(MarkupServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            return Task.FromResult(new InitializeResult(
                new ServerCapabilities
                {
                    CompletionProvider = new CompletionOptions(["\"", ":", "<", " ", "=", "/"], null, false),
                    HoverProvider = true,
                },
                new ServerInfo("codalus-markuphost", version)));
        });
        _connection.OnNotification(Methods.Initialized, json.EmptyObject, _ => { });
        _connection.OnRequest(Methods.Shutdown, (_, _) => Task.FromResult<JsonElement?>(null));
        _connection.OnNotification(Methods.Exit, json.EmptyObject, _ => _exit.TrySetResult());
        _connection.OnNotification(Methods.DidChangeConfiguration, json.DidChangeConfigurationParams, p => Configure(p.Settings));
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
            _ = Publish(p.TextDocument.Uri, null, []);
        });
        _connection.OnNotification(Methods.DidSave, json.DidSaveTextDocumentParams, _ => { });
        _connection.OnRequest(Methods.Completion, json.CompletionParams, json.CompletionList, async (p, ct) =>
        {
            if (_documents.Get(p.TextDocument.Uri) is not { } document)
            {
                return new CompletionList(false, []);
            }

            var offset = document.OffsetAt(p.Position);
            return document.LanguageId switch
            {
                _ when JsonLanguage.Handles(document.LanguageId) => await _json.CompletionAsync(document, offset, ct).ConfigureAwait(false),
                _ when XmlLanguage.Handles(document.LanguageId) => _xml.Completion(document, offset),
                "editorconfig" => EditorConfigLanguage.Completion(document, offset),
                _ => new CompletionList(false, []),
            };
        });
        _connection.OnRequest(Methods.Hover, (raw, ct) => HoverAsync(raw, ct));
    }

    /// <summary>
    /// Options: <c>{ "schemaDownload": bool, "schemaCache": path, "xmlSchemas": [{ "pattern": glob, "schema": path }] }</c>
    /// (initialization options or settings). New XML schema associations validate the open XML documents again.
    /// </summary>
    private void Configure(JsonElement? options)
    {
        if (options is not { ValueKind: JsonValueKind.Object } o)
        {
            return;
        }

        if (o.TryGetProperty("xmlSchemas", out var xmlSchemas) && xmlSchemas.ValueKind == JsonValueKind.Array)
        {
            _xml.Schemas.Associations =
            [
                .. xmlSchemas.EnumerateArray()
                    .Where(a => a.ValueKind == JsonValueKind.Object && a.TryGetProperty("pattern", out var p) && p.ValueKind == JsonValueKind.String
                                && a.TryGetProperty("schema", out var s) && s.ValueKind == JsonValueKind.String)
                    .Select(a => (a.GetProperty("pattern").GetString()!, a.GetProperty("schema").GetString()!)),
            ];
            foreach (var document in _documents.All.Where(d => d.LanguageId == "xml"))
            {
                Schedule(document, TimeSpan.Zero);
            }
        }

        var download = o.TryGetProperty("schemaDownload", out var d) ? d.ValueKind == JsonValueKind.True : _schemas.AllowDownloads;
        var cache = o.TryGetProperty("schemaCache", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : _schemas.CacheDirectory;
        if (cache != _schemas.CacheDirectory)
        {
            _schemas = new SchemaStore(cache, download, Log);
            _json = new JsonLanguage(_schemas);
            _yaml = new YamlLanguage(_schemas);
        }
        else
        {
            _schemas.AllowDownloads = download;
        }
    }

    private async Task<JsonElement?> HoverAsync(JsonElement raw, CancellationToken cancellationToken)
    {
        var p = raw.Deserialize(ProtocolJsonContext.Default.TextDocumentPositionParams);
        if (p is null || _documents.Get(p.TextDocument.Uri) is not { } document)
        {
            return null;
        }

        var offset = document.OffsetAt(p.Position);
        var hover = document.LanguageId switch
        {
            _ when JsonLanguage.Handles(document.LanguageId) => await _json.HoverAsync(document, offset, cancellationToken).ConfigureAwait(false),
            _ when XmlLanguage.Handles(document.LanguageId) => await _xml.HoverAsync(document, offset, cancellationToken).ConfigureAwait(false),
            _ when YamlLanguage.Handles(document.LanguageId) => await _yaml.HoverAsync(document, offset, cancellationToken).ConfigureAwait(false),
            "editorconfig" => EditorConfigLanguage.Hover(document, offset),
            _ => null,
        };
        return hover is null ? null : JsonSerializer.SerializeToElement(hover, ProtocolJsonContext.Default.Hover);
    }

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
                var diagnostics = document.LanguageId switch
                {
                    _ when JsonLanguage.Handles(document.LanguageId) => await _json.DiagnosticsAsync(document, cancellation.Token).ConfigureAwait(false),
                    _ when XmlLanguage.Handles(document.LanguageId) => _xml.Diagnostics(document),
                    _ when YamlLanguage.Handles(document.LanguageId) => await _yaml.DiagnosticsAsync(document, cancellation.Token).ConfigureAwait(false),
                    "editorconfig" => EditorConfigLanguage.Diagnostics(document),
                    _ => [],
                };
                if (!cancellation.IsCancellationRequested)
                {
                    await Publish(document.Uri, version, [.. diagnostics]).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log($"{document.Uri}: {e.Message}");
            }
        }, CancellationToken.None);
    }

    private Task Publish(string uri, int? version, Diagnostic[] diagnostics) =>
        _connection.NotifyAsync(Methods.PublishDiagnostics, new PublishDiagnosticsParams(uri, version, diagnostics), ProtocolJsonContext.Default.PublishDiagnosticsParams, CancellationToken.None);

    private async Task<EvaluatedProperty?> EvaluateAsync(string project, string property, CancellationToken cancellationToken)
    {
        try
        {
            using var request = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string> { ["project"] = project, ["name"] = property }, MarkupJsonContext.Default.DictionaryStringString));
            var result = await _connection.InvokeRawAsync(EvaluatePropertyMethod, request.RootElement, cancellationToken).ConfigureAwait(false);
            return result.ValueKind == JsonValueKind.Object
                ? new EvaluatedProperty(
                    result.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null,
                    result.TryGetProperty("origin", out var origin) && origin.ValueKind == JsonValueKind.String ? origin.GetString() : null)
                : null;
        }
        catch (Exception e) when (e is JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            return null; // the shell doesn't know the project (yet): documentation only
        }
    }

    private void Log(string message) =>
        _ = _connection.NotifyAsync(Methods.LogMessage, new LogMessageParams(MessageType.Log, message), ProtocolJsonContext.Default.LogMessageParams, CancellationToken.None);
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class MarkupJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
