using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TodoLanguage;

/// <summary>
/// A small LSP server for <c>.todo</c> files, started by Codalus IDE from the plugin's manifest
/// (<c>contributes.languageServers</c>). It speaks JSON-RPC over stdin and stdout with <c>Content-Length</c> headers,
/// like any LSP server, and needs nothing from Codalus. Tasks are <c>- [ ] text</c> (open) or <c>- [x] text</c> (done),
/// with <c>@tags</c>, <c>!high</c> priorities and <c>due:yyyy-mm-dd</c> dates.
/// </summary>
internal static partial class Program
{
    private static readonly Stream Output = Console.OpenStandardOutput();
    private static readonly Dictionary<string, string> Documents = new(StringComparer.Ordinal);
    private static readonly object WriteLock = new();
    private static int s_nextRequest = 1;
    private static string[] s_tags = ["today", "later", "bug"];

    public static async Task Main()
    {
        var input = Console.OpenStandardInput();
        while (await ReadMessageAsync(input) is { } message)
        {
            var method = message["method"]?.GetValue<string>();
            var id = message["id"];
            var parameters = message["params"];
            if (method is null)
            {
                Response(message); // the answer to our workspace/configuration request
                continue;
            }

            try
            {
                var result = Handle(method, parameters);
                if (id is not null)
                {
                    Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result });
                }
            }
            catch (Exception e) when (id is not null)
            {
                Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["error"] = new JsonObject { ["code"] = -32603, ["message"] = e.Message } });
            }

            if (method == "exit")
            {
                return;
            }
        }
    }

    private static JsonNode? Handle(string method, JsonNode? parameters)
    {
        switch (method)
        {
            case "initialize":
                return new JsonObject
                {
                    ["capabilities"] = new JsonObject
                    {
                        ["textDocumentSync"] = 1, // the whole document on every change
                        ["completionProvider"] = new JsonObject { ["triggerCharacters"] = new JsonArray("@") },
                        ["hoverProvider"] = true,
                        ["documentFormattingProvider"] = true,
                    },
                    ["serverInfo"] = new JsonObject { ["name"] = "todo-sample", ["version"] = "1.0.0" },
                };
            case "initialized":
                // Ask for the "todo" settings section (the tags offered after @).
                Write(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = s_nextRequest++,
                    ["method"] = "workspace/configuration",
                    ["params"] = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["section"] = "todo" }) },
                });
                return null;
            case "textDocument/didOpen":
                Update(parameters!["textDocument"]!["uri"]!.GetValue<string>(), parameters["textDocument"]!["text"]!.GetValue<string>());
                return null;
            case "textDocument/didChange":
                Update(parameters!["textDocument"]!["uri"]!.GetValue<string>(), parameters["contentChanges"]![0]!["text"]!.GetValue<string>());
                return null;
            case "textDocument/didClose":
                Documents.Remove(parameters!["textDocument"]!["uri"]!.GetValue<string>());
                return null;
            case "textDocument/completion":
                return Completion(parameters!);
            case "textDocument/hover":
                return Hover(parameters!);
            case "textDocument/formatting":
                return Format(parameters!);
            case "shutdown":
                return null;
            default:
                return null; // other notifications, and requests this server doesn't support
        }
    }

    private static void Response(JsonNode message)
    {
        if (message["result"] is JsonArray { Count: > 0 } items && items[0]?["tags"] is JsonArray tags)
        {
            s_tags = [.. tags.Select(t => t?.GetValue<string>()).OfType<string>()];
        }
    }

    private static void Update(string uri, string text)
    {
        Documents[uri] = text;
        var diagnostics = new JsonArray();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (Regex.IsMatch(line, @"^\s*- \[[^ x]\]"))
            {
                diagnostics.Add(Diagnostic(i, 0, line.Length, 2, "todo.checkbox", "A task is '- [ ]' (open) or '- [x]' (done)."));
            }

            foreach (Match due in DueDate().Matches(line))
            {
                if (!DateOnly.TryParseExact(due.Groups[1].Value, "yyyy-MM-dd", out _))
                {
                    diagnostics.Add(Diagnostic(i, due.Index, due.Index + due.Length, 1, "todo.date", $"'{due.Groups[1].Value}' isn't a date."));
                }
            }
        }

        Write(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "textDocument/publishDiagnostics",
            ["params"] = new JsonObject { ["uri"] = uri, ["diagnostics"] = diagnostics },
        });
    }

    private static JsonObject Diagnostic(int line, int start, int end, int severity, string code, string message) => new()
    {
        ["range"] = Range(line, start, line, end),
        ["severity"] = severity,
        ["code"] = code,
        ["source"] = "todo",
        ["message"] = message,
    };

    private static JsonNode Completion(JsonNode parameters)
    {
        // Every tag already used in open documents, then the configured ones.
        var used = Documents.Values.SelectMany(t => Tag().Matches(t).Select(m => m.Groups[1].Value));
        var items = new JsonArray();
        foreach (var tag in used.Concat(s_tags).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            items.Add(new JsonObject { ["label"] = "@" + tag, ["kind"] = 14, ["insertText"] = tag, ["filterText"] = tag, ["detail"] = "tag" });
        }

        return new JsonObject { ["isIncomplete"] = false, ["items"] = items };
    }

    private static JsonNode? Hover(JsonNode parameters)
    {
        var uri = parameters["textDocument"]!["uri"]!.GetValue<string>();
        var line = parameters["position"]!["line"]!.GetValue<int>();
        if (!Documents.TryGetValue(uri, out var text))
        {
            return null;
        }

        var lines = text.Split('\n');
        var open = lines.Count(l => l.TrimStart().StartsWith("- [ ]", StringComparison.Ordinal));
        var done = lines.Count(l => l.TrimStart().StartsWith("- [x]", StringComparison.Ordinal));
        var current = line < lines.Length ? lines[line].Trim() : string.Empty;
        var tags = Tag().Matches(current).Select(m => "`@" + m.Groups[1].Value + "`").ToList();
        var markdown = new StringBuilder($"**{open} open**, {done} done in this list.");
        if (tags.Count > 0)
        {
            markdown.Append("\n\nTags: ").AppendJoin(", ", tags);
        }

        return new JsonObject { ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = markdown.ToString() } };
    }

    private static JsonNode Format(JsonNode parameters)
    {
        // Normalizes "-[ ]", "- []" and "-[x]" to "- [ ]" and "- [x]".
        var uri = parameters["textDocument"]!["uri"]!.GetValue<string>();
        var text = Documents.GetValueOrDefault(uri) ?? string.Empty;
        var formatted = Checkbox().Replace(text, m => m.Groups[1].Value + "- [" + (m.Groups[2].Value.Trim() == "x" ? "x" : " ") + "] ");
        var lines = text.Split('\n');
        return formatted == text
            ? new JsonArray()
            : new JsonArray(new JsonObject { ["range"] = Range(0, 0, lines.Length - 1, lines[^1].Length), ["newText"] = formatted });
    }

    private static JsonObject Range(int startLine, int startCharacter, int endLine, int endCharacter) => new()
    {
        ["start"] = new JsonObject { ["line"] = startLine, ["character"] = startCharacter },
        ["end"] = new JsonObject { ["line"] = endLine, ["character"] = endCharacter },
    };

    // ------------------------------------------------------------------ LSP base protocol

    private static async Task<JsonNode?> ReadMessageAsync(Stream input)
    {
        var length = -1;
        var header = new StringBuilder();
        var buffer = new byte[1];
        while (true)
        {
            if (await input.ReadAsync(buffer) == 0)
            {
                return null;
            }

            header.Append((char)buffer[0]);
            if (header.Length >= 4 && header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                foreach (var line in header.ToString().Split("\r\n"))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        length = int.Parse(line["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    }
                }

                break;
            }
        }

        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await input.ReadAsync(body.AsMemory(read));
            if (n == 0)
            {
                return null;
            }

            read += n;
        }

        return JsonNode.Parse(body);
    }

    private static void Write(JsonNode message)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        lock (WriteLock)
        {
            Output.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
            Output.Write(body);
            Output.Flush();
        }
    }

    [GeneratedRegex(@"@([\w-]+)")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\bdue:(\S+)")]
    private static partial Regex DueDate();

    [GeneratedRegex(@"(?m)^(\s*)-\s*\[(\s*x?\s*)\]\s*")]
    private static partial Regex Checkbox();
}
