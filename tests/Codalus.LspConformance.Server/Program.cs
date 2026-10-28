using System.Text.Json;
using System.Text.Json.Nodes;
using Codalus.Rpc;

// A scripted LSP server for the bridge tests (ADR 0016). Its results use the shapes VS Code's client accepts and the
// shell's narrowed types don't: options objects, location links, marked strings, numeric diagnostic codes,
// insert/replace edits, snippets, offset parameter labels, `changes` maps and bare commands among code actions.
// Documents are "key = value" lines; a line without '=' is an error with code 42. With --dap it is a scripted debug
// adapter instead (ScriptedDap), as with netcoredbg's --interpreter=vscode (a stand-in for netcoredbg). With the
// arguments of js-debug's DAP server (a port and a host) it stands in for vscode-js-debug (ScriptedJsDebug).
if (args is [var port, var host] && int.TryParse(port, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var portNumber)
    && System.Net.IPAddress.TryParse(host, out var address))
{
    await Codalus.LspConformance.Server.ScriptedJsDebug.RunAsync(address, portNumber);
    return;
}

if (args.Contains("--program"))
{
    // The scripted adapter's "program" in a start that times out: it runs until it's killed, as a program that netcoredbg
    // started and never saw the runtime of does.
    Thread.Sleep(Timeout.Infinite);
    return;
}

if (args.Contains("--dap") || args.Contains("--interpreter=vscode"))
{
    var initializeAfter = Array.IndexOf(args, "--initialize-after") is var at and >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    await Codalus.LspConformance.Server.ScriptedDap.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), initializeAfter);
    return;
}

var connection = new JsonRpcConnection(Console.OpenStandardInput(), Console.OpenStandardOutput());
var documents = new Dictionary<string, string>(StringComparer.Ordinal);
var exit = new TaskCompletionSource();
JsonElement? configuration = null;

static JsonElement Json(string text)
{
    using var document = JsonDocument.Parse(text);
    return document.RootElement.Clone();
}

static JsonElement ToElement(JsonNode node) => Json(node.ToJsonString());

static string Uri(JsonElement parameters) => parameters.GetProperty("textDocument").GetProperty("uri").GetString()!;

static (int Line, int Character) PositionOf(JsonElement parameters) =>
    (parameters.GetProperty("position").GetProperty("line").GetInt32(), parameters.GetProperty("position").GetProperty("character").GetInt32());

static JsonObject Range(int line, int start, int end) => new()
{
    ["start"] = new JsonObject { ["line"] = line, ["character"] = start },
    ["end"] = new JsonObject { ["line"] = line, ["character"] = end },
};

async Task PublishAsync(string uri)
{
    var diagnostics = new JsonArray();
    var lines = documents[uri].Split('\n');
    for (var i = 0; i < lines.Length; i++)
    {
        var line = lines[i].TrimEnd('\r');
        if (line.Trim().Length > 0 && !line.Contains('=', StringComparison.Ordinal) && !line.TrimStart().StartsWith('#'))
        {
            diagnostics.Add((JsonNode)new JsonObject { ["range"] = Range(i, 0, line.Length), ["severity"] = 1, ["code"] = 42, ["source"] = "conformance", ["message"] = "Expected key = value." });
        }
    }

    var parameters = new JsonObject { ["uri"] = uri, ["diagnostics"] = diagnostics };
    await connection.NotifyAsync("textDocument/publishDiagnostics", ToElement(parameters), ConformanceJson.Default.JsonElement, CancellationToken.None).ConfigureAwait(false);
}

// With --initialize-after <file>, initialize is answered only once that file exists: a server still starting, for as
// long as a test needs one.
var lspInitializeAfter = Array.IndexOf(args, "--initialize-after") is var gate and >= 0 && gate + 1 < args.Length ? args[gate + 1] : null;
connection.OnRequest("initialize", async (_, cancellationToken) =>
{
    while (lspInitializeAfter is not null && !File.Exists(lspInitializeAfter))
    {
        await Task.Delay(50, cancellationToken).ConfigureAwait(false);
    }

    return (JsonElement?)InitializeResult();
});

JsonElement InitializeResult() => Json("""
    {
      "capabilities": {
        "textDocumentSync": { "openClose": true, "change": 1, "save": { "includeText": false } },
        "hoverProvider": { "workDoneProgress": false },
        "completionProvider": { "resolveProvider": false },
        "signatureHelpProvider": { "triggerCharacters": ["("] },
        "definitionProvider": { },
        "referencesProvider": true,
        "renameProvider": { "prepareProvider": false },
        "documentFormattingProvider": { },
        "foldingRangeProvider": true,
        "codeActionProvider": { "codeActionKinds": ["quickfix"] },
        "workspaceSymbolProvider": { },
        "semanticTokensProvider": { "legend": { "tokenTypes": ["property", "string", "comment"], "tokenModifiers": [] }, "full": { "delta": false }, "range": false }
      },
      "serverInfo": { "name": "conformance", "version": "1.0" }
    }
    """);
connection.OnNotification("initialized", ConformanceJson.Default.JsonElement, initialized =>
{
    // Ask the client for settings, as many servers do right after initialization.
    _ = Task.Run(async () =>
    {
        var result = await connection.InvokeRawAsync("workspace/configuration", Json("""{ "items": [ { "section": "conformance" } ] }"""), CancellationToken.None).ConfigureAwait(false);
        configuration = result.ValueKind == JsonValueKind.Array && result.GetArrayLength() > 0 ? result[0].Clone() : null;
    });
    return Task.CompletedTask;
});
connection.OnNotification("textDocument/didOpen", ConformanceJson.Default.JsonElement, async p =>
{
    var item = p.GetProperty("textDocument");
    documents[item.GetProperty("uri").GetString()!] = item.GetProperty("text").GetString()!;
    await PublishAsync(item.GetProperty("uri").GetString()!).ConfigureAwait(false);
});
connection.OnNotification("textDocument/didChange", ConformanceJson.Default.JsonElement, async p =>
{
    var uri = Uri(p);
    documents[uri] = p.GetProperty("contentChanges")[0].GetProperty("text").GetString()!; // full sync
    await PublishAsync(uri).ConfigureAwait(false);
});
connection.OnNotification("textDocument/didClose", ConformanceJson.Default.JsonElement, p =>
{
    documents.Remove(Uri(p));
    return Task.CompletedTask;
});
connection.OnNotification("textDocument/didSave", ConformanceJson.Default.JsonElement, _ => Task.CompletedTask);
connection.OnNotification("workspace/didChangeConfiguration", ConformanceJson.Default.JsonElement, _ => Task.CompletedTask);

connection.OnRequest("textDocument/completion", (p, _) =>
{
    var (line, character) = PositionOf(p);
    var items = new JsonArray
    {
        (JsonNode)new JsonObject
        {
            ["label"] = "alpha",
            ["kind"] = 10,
            ["documentation"] = "The first key.",
            ["textEdit"] = new JsonObject { ["insert"] = Range(line, 0, character), ["replace"] = Range(line, 0, character), ["newText"] = "alpha" },
        },
        (JsonNode)new JsonObject { ["label"] = "beta", ["kind"] = 3, ["insertTextFormat"] = 2, ["insertText"] = "beta(${1:value})$0" },
        (JsonNode)new JsonObject { ["label"] = "setting", ["detail"] = configuration?.GetRawText() ?? "none" },
    };
    return Task.FromResult<JsonElement?>(ToElement(items)); // an array, not a CompletionList
});
connection.OnRequest("textDocument/hover", (_, _) => Task.FromResult<JsonElement?>(Json("""
    { "contents": [ { "language": "conf", "value": "alpha = 1" }, "A **marked** string." ] }
    """)));
connection.OnRequest("textDocument/signatureHelp", (_, _) => Task.FromResult<JsonElement?>(Json("""
    { "signatures": [ { "label": "f(first, second)", "documentation": "Two parameters.", "parameters": [ { "label": [2, 7] }, { "label": [9, 15], "documentation": "The second." } ] } ] }
    """)));
connection.OnRequest("textDocument/definition", (p, _) =>
{
    var uri = Uri(p);
    var link = new JsonArray
    {
        (JsonNode)new JsonObject
        {
            ["targetUri"] = uri,
            ["targetRange"] = Range(0, 0, 10),
            ["targetSelectionRange"] = Range(0, 0, 5),
            ["originSelectionRange"] = Range(1, 0, 5),
        },
    };
    return Task.FromResult<JsonElement?>(ToElement(link));
});
connection.OnRequest("textDocument/references", (p, _) =>
    Task.FromResult<JsonElement?>(ToElement(new JsonObject { ["uri"] = Uri(p), ["range"] = Range(0, 0, 5) }))); // one Location, not an array
connection.OnRequest("textDocument/rename", (p, _) =>
{
    var newName = p.GetProperty("newName").GetString();
    var edit = new JsonObject
    {
        ["changes"] = new JsonObject
        {
            [Uri(p)] = new JsonArray { (JsonNode)new JsonObject { ["range"] = Range(0, 0, 5), ["newText"] = newName } },
        },
    };
    return Task.FromResult<JsonElement?>(ToElement(edit));
});
connection.OnRequest("textDocument/formatting", (p, _) =>
{
    var text = documents[Uri(p)];
    var formatted = string.Join('\n', text.Split('\n').Select(l => l.Contains('=', StringComparison.Ordinal)
        ? l.Split('=', 2)[0].Trim() + " = " + l.Split('=', 2)[1].Trim()
        : l));
    var lines = text.Split('\n');
    var edits = new JsonArray { (JsonNode)new JsonObject { ["range"] = new JsonObject { ["start"] = new JsonObject { ["line"] = 0, ["character"] = 0 }, ["end"] = new JsonObject { ["line"] = lines.Length - 1, ["character"] = lines[^1].Length } }, ["newText"] = formatted } };
    return Task.FromResult<JsonElement?>(ToElement(edits));
});
connection.OnRequest("textDocument/foldingRange", (_, _) => Task.FromResult<JsonElement?>(Json("""[ { "startLine": 0, "endLine": 2 } ]""")));
connection.OnRequest("textDocument/codeAction", (p, _) =>
{
    var actions = new JsonArray
    {
        (JsonNode)new JsonObject { ["title"] = "A bare command", ["command"] = "conformance.run" },
        (JsonNode)new JsonObject
        {
            ["title"] = "Add '= value'",
            ["kind"] = "quickfix",
            ["diagnostics"] = new JsonArray { (JsonNode)new JsonObject { ["range"] = Range(1, 0, 5), ["code"] = 42, ["message"] = "Expected key = value." } },
            ["edit"] = new JsonObject { ["changes"] = new JsonObject { [Uri(p)] = new JsonArray { (JsonNode)new JsonObject { ["range"] = Range(1, 5, 5), ["newText"] = " = value" } } } },
        },
    };
    return Task.FromResult<JsonElement?>(ToElement(actions));
});
connection.OnRequest("workspace/symbol", (_, _) =>
{
    var uri = documents.Keys.FirstOrDefault() ?? "file:///none";
    var symbols = new JsonArray { (JsonNode)new JsonObject { ["name"] = "alpha", ["kind"] = 7, ["location"] = new JsonObject { ["uri"] = uri } } };
    return Task.FromResult<JsonElement?>(ToElement(symbols));
});
connection.OnRequest("textDocument/semanticTokens/full", (_, _) => Task.FromResult<JsonElement?>(Json("""{ "data": [0, 0, 5, 0, 0, 0, 8, 1, 1, 0] }""")));
connection.OnRequest("shutdown", (_, _) => Task.FromResult<JsonElement?>(null));
connection.OnNotification("exit", ConformanceJson.Default.JsonElement, _ =>
{
    exit.TrySetResult();
    return Task.CompletedTask;
});

connection.Start();
await Task.WhenAny(connection.Completion, exit.Task).ConfigureAwait(false);

[System.Text.Json.Serialization.JsonSerializable(typeof(JsonElement))]
internal sealed partial class ConformanceJson : System.Text.Json.Serialization.JsonSerializerContext;
