using System.Text.Json;
using System.Text.Json.Nodes;
using Codalus.LanguageServices.Protocol;

namespace Codalus.LanguageServices.Client;

/// <summary>
/// Turns what any LSP 3.17 server sends into the narrowed shapes of <c>Codalus.LanguageServices.Protocol</c>
/// (ADR 0016): options objects that mean "supported" become <c>true</c>, a single location or location links become a
/// location array, plain-string documentation becomes Markdown, a completion array becomes a list, parameter labels
/// given as offsets become substrings, <c>changes</c> become <c>documentChanges</c>, numeric diagnostic codes become
/// strings, and commands among code actions are dropped. Results already in the narrowed shape pass unchanged.
/// </summary>
public static partial class LspNormalizer
{
    private static readonly string[] BooleanProviders =
    [
        "hoverProvider", "definitionProvider", "implementationProvider", "documentHighlightProvider", "foldingRangeProvider",
        "referencesProvider", "codeActionProvider", "documentFormattingProvider", "documentRangeFormattingProvider",
        "workspaceSymbolProvider", "documentSymbolProvider", "callHierarchyProvider", "typeHierarchyProvider", "inlayHintProvider", "selectionRangeProvider",
        "diagnosticProvider",
    ];

    /// <summary>Normalizes the result of <paramref name="method"/>; returns the input when nothing needs changing.</summary>
    public static JsonElement Result(string method, JsonElement result)
    {
        if (result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return result;
        }

        JsonNode? node = method switch
        {
            Methods.Initialize => InitializeResult(JsonNode.Parse(result.GetRawText())),
            Methods.Completion => CompletionList(JsonNode.Parse(result.GetRawText())),
            Methods.CompletionResolve => CompletionItem(JsonNode.Parse(result.GetRawText())),
            Methods.Hover => Hover(JsonNode.Parse(result.GetRawText())),
            Methods.SignatureHelp => SignatureHelp(JsonNode.Parse(result.GetRawText())),
            Methods.Definition or Methods.Implementation or Methods.References or "textDocument/declaration" or "textDocument/typeDefinition"
                => Locations(JsonNode.Parse(result.GetRawText())),
            Methods.PrepareRename => PrepareRename(JsonNode.Parse(result.GetRawText())),
            Methods.Rename => WorkspaceEdit(JsonNode.Parse(result.GetRawText())),
            Methods.CodeAction => CodeActions(JsonNode.Parse(result.GetRawText())),
            Methods.CodeActionResolve => CodeAction(JsonNode.Parse(result.GetRawText())),
            Methods.WorkspaceSymbol => Symbols(JsonNode.Parse(result.GetRawText())),
            Methods.DocumentSymbol => DocumentSymbols(JsonNode.Parse(result.GetRawText())),
            Methods.InlayHint => InlayHints(JsonNode.Parse(result.GetRawText())),
            _ => null,
        };
        return node is null ? result : Element(node);
    }

    /// <summary>Normalizes <c>textDocument/publishDiagnostics</c> parameters (numeric codes).</summary>
    public static JsonElement PublishDiagnostics(JsonElement parameters)
    {
        var node = JsonNode.Parse(parameters.GetRawText());
        if (node?["diagnostics"] is JsonArray diagnostics)
        {
            foreach (var diagnostic in diagnostics.OfType<JsonObject>())
            {
                Diagnostic(diagnostic);
            }
        }

        return Element(node);
    }

    private static JsonElement Element(JsonNode? node)
    {
        using var document = JsonDocument.Parse(node?.ToJsonString() ?? "null");
        return document.RootElement.Clone();
    }

    // ------------------------------------------------------------------ capabilities

    private static JsonNode? InitializeResult(JsonNode? node)
    {
        if (node?["capabilities"] is not JsonObject capabilities)
        {
            return node;
        }

        if (capabilities["textDocumentSync"] is JsonObject sync)
        {
            capabilities["textDocumentSync"] = sync["change"] is JsonValue change && change.TryGetValue<int>(out var kind) ? kind : TextDocumentSyncKind.Full;
        }

        foreach (var name in BooleanProviders)
        {
            if (capabilities[name] is JsonObject)
            {
                capabilities[name] = true;
            }
        }

        // renameProvider: true (no prepare), or { prepareProvider }.
        switch (capabilities["renameProvider"])
        {
            case JsonObject rename:
                capabilities["prepareRenameProvider"] = rename["prepareProvider"] is JsonValue prepare && prepare.TryGetValue<bool>(out var p) && p;
                capabilities["renameProvider"] = true;
                break;
            case JsonValue value when value.TryGetValue<bool>(out var supported) && supported && capabilities["prepareRenameProvider"] is null:
                capabilities["prepareRenameProvider"] = false;
                break;
        }

        if (capabilities["completionProvider"] is JsonObject completion)
        {
            completion["triggerCharacters"] ??= new JsonArray();
            completion["resolveProvider"] ??= false;
        }

        if (capabilities["codeLensProvider"] is JsonObject lens)
        {
            lens["resolveProvider"] ??= false;
        }

        if (capabilities["signatureHelpProvider"] is JsonObject signature)
        {
            signature["triggerCharacters"] ??= new JsonArray();
            signature["retriggerCharacters"] ??= new JsonArray();
        }

        if (capabilities["semanticTokensProvider"] is JsonObject tokens)
        {
            tokens["full"] = Truthy(tokens["full"]);
            tokens["range"] = Truthy(tokens["range"]);
            if (tokens["legend"] is JsonObject legend)
            {
                legend["tokenModifiers"] ??= new JsonArray();
            }
        }

        return node;
    }

    private static bool Truthy(JsonNode? node) =>
        node is JsonObject || (node is JsonValue value && value.TryGetValue<bool>(out var b) && b);

    // ------------------------------------------------------------------ completion

    private static JsonNode? CompletionList(JsonNode? node)
    {
        var list = node is JsonArray items
            ? new JsonObject { ["isIncomplete"] = false, ["items"] = items.DeepClone() }
            : node as JsonObject;
        if (list?["items"] is JsonArray array)
        {
            foreach (var item in array)
            {
                CompletionItem(item);
            }
        }

        if (list is not null)
        {
            list["isIncomplete"] ??= false;
        }

        return list ?? node;
    }

    private static JsonNode? CompletionItem(JsonNode? node)
    {
        if (node is not JsonObject item)
        {
            return node;
        }

        item["documentation"] = Markup(item["documentation"]);

        // InsertReplaceEdit: use the replace range (the whole word), as accepting with Tab does in VS Code.
        if (item["textEdit"] is JsonObject edit && edit["range"] is null && edit["replace"] is { } replace)
        {
            item["textEdit"] = new JsonObject { ["range"] = replace.DeepClone(), ["newText"] = edit["newText"]?.DeepClone() };
        }

        // Snippets (insertTextFormat 2) keep their tab stops: the editor inserts them with its snippet engine (M12b).

        return item;
    }

    /// <summary><c>${1:name}</c> → <c>name</c>, <c>$0</c> and <c>${0}</c> → nothing.</summary>
    internal static string StripSnippet(string snippet)
    {
        var result = new System.Text.StringBuilder(snippet.Length);
        for (var i = 0; i < snippet.Length; i++)
        {
            var c = snippet[i];
            if (c == '\\' && i + 1 < snippet.Length)
            {
                result.Append(snippet[++i]);
            }
            else if (c == '$' && i + 1 < snippet.Length && char.IsDigit(snippet[i + 1]))
            {
                while (i + 1 < snippet.Length && char.IsDigit(snippet[i + 1]))
                {
                    i++;
                }
            }
            else if (c == '$' && i + 1 < snippet.Length && snippet[i + 1] == '{')
            {
                var close = snippet.IndexOf('}', i);
                var colon = snippet.IndexOf(':', i);
                if (close < 0)
                {
                    result.Append(c);
                    continue;
                }

                if (colon > 0 && colon < close)
                {
                    result.Append(snippet, colon + 1, close - colon - 1);
                }

                i = close;
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    // ------------------------------------------------------------------ hover and signature help

    private static JsonNode? Hover(JsonNode? node)
    {
        if (node is JsonObject hover)
        {
            hover["contents"] = Markup(hover["contents"]);
        }

        return node;
    }

    /// <summary>MarkupContent, a string, a MarkedString <c>{language, value}</c> or an array of them → MarkupContent.</summary>
    private static JsonObject? Markup(JsonNode? node)
    {
        static string Text(JsonNode? part) => part switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonObject marked when marked["language"] is JsonValue language && marked["value"] is JsonValue code =>
                $"```{language.GetValue<string>()}\n{code.GetValue<string>()}\n```",
            JsonObject markup when markup["value"] is JsonValue value => value.GetValue<string>(),
            _ => string.Empty,
        };

        var result = node switch
        {
            null => null,
            JsonObject markup when markup["kind"] is not null => markup,
            JsonArray parts => new JsonObject { ["kind"] = "markdown", ["value"] = string.Join("\n\n", parts.Select(Text).Where(t => t.Length > 0)) },
            _ => new JsonObject { ["kind"] = "markdown", ["value"] = Text(node) },
        };

        // Links that run an editor command (FsAutoComplete's "Open the documentation") lead nowhere here.
        if (result?["value"] is JsonValue markdown && markdown.TryGetValue<string>(out var text) && text.Contains("command:", StringComparison.Ordinal))
        {
            result["value"] = CommandLinks().Replace(text, string.Empty).Trim();
        }

        return result;
    }

    [System.Text.RegularExpressions.GeneratedRegex("<a\\s+href=['\"]command:[^'\"]*['\"]\\s*>.*?</a>|\\[[^\\]]*\\]\\(command:[^)]*\\)", System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex CommandLinks();

    private static JsonNode? SignatureHelp(JsonNode? node)
    {
        if (node is not JsonObject help)
        {
            return node;
        }

        help["activeSignature"] ??= 0;
        help["activeParameter"] ??= 0;
        foreach (var signature in (help["signatures"] as JsonArray ?? []).OfType<JsonObject>())
        {
            signature["documentation"] = Markup(signature["documentation"]);
            var label = signature["label"]?.GetValue<string>() ?? string.Empty;
            if (signature["parameters"] is not JsonArray parameters)
            {
                signature["parameters"] = new JsonArray();
                continue;
            }

            foreach (var parameter in parameters.OfType<JsonObject>())
            {
                parameter["documentation"] = Markup(parameter["documentation"]);
                if (parameter["label"] is JsonArray { Count: 2 } offsets
                    && offsets[0] is JsonValue s && s.TryGetValue<int>(out var start)
                    && offsets[1] is JsonValue e && e.TryGetValue<int>(out var end)
                    && start >= 0 && end <= label.Length && start <= end)
                {
                    parameter["label"] = label[start..end];
                }
            }
        }

        return help;
    }

    // ------------------------------------------------------------------ locations, rename, actions, symbols

    private static JsonArray Locations(JsonNode? node)
    {
        static JsonObject? Location(JsonNode? item) => item switch
        {
            JsonObject link when link["targetUri"] is not null => new JsonObject
            {
                ["uri"] = link["targetUri"]!.DeepClone(),
                ["range"] = (link["targetSelectionRange"] ?? link["targetRange"])?.DeepClone(),
            },
            JsonObject location when location["uri"] is not null => (JsonObject)location.DeepClone(),
            _ => null,
        };

        return node switch
        {
            JsonArray array => new JsonArray([.. array.Select(Location).OfType<JsonObject>()]),
            JsonObject single => Location(single) is { } location ? new JsonArray(location) : new JsonArray(),
            _ => new JsonArray(),
        };
    }

    private static JsonObject? PrepareRename(JsonNode? node)
    {
        // A bare range, { range, placeholder }, or { defaultBehavior: true } (the client decides: no placeholder here).
        if (node is JsonObject range && range["start"] is not null && range["end"] is not null)
        {
            return new JsonObject { ["range"] = range.DeepClone(), ["placeholder"] = string.Empty };
        }

        if (node is JsonObject result && result["range"] is not null)
        {
            result["placeholder"] ??= string.Empty;
            return result;
        }

        return null;
    }

    private static JsonNode? WorkspaceEdit(JsonNode? node)
    {
        if (node is not JsonObject edit)
        {
            return node;
        }

        var changes = new JsonArray();
        var operations = edit["resourceOperations"] is JsonArray ours ? (JsonArray)ours.DeepClone() : new JsonArray();
        if (edit["documentChanges"] is JsonArray documentChanges)
        {
            // TextDocumentEdits, and LSP's file operations: a created file is an edit inserting nothing into a new file;
            // renamed and deleted files are resource operations, applied after the text edits.
            foreach (var change in documentChanges.OfType<JsonObject>())
            {
                switch (change["kind"]?.GetValue<string>())
                {
                    case null when change["textDocument"] is not null:
                        changes.Add((JsonNode?)change.DeepClone());
                        break;
                    case "create" when change["uri"] is JsonValue created:
                        changes.Add((JsonNode)new JsonObject
                        {
                            ["textDocument"] = new JsonObject { ["uri"] = created.DeepClone(), ["version"] = null },
                            ["edits"] = new JsonArray(new JsonObject
                            {
                                ["range"] = new JsonObject { ["start"] = new JsonObject { ["line"] = 0, ["character"] = 0 }, ["end"] = new JsonObject { ["line"] = 0, ["character"] = 0 } },
                                ["newText"] = string.Empty,
                            }),
                        });
                        break;
                    case "rename" when change["oldUri"] is JsonValue from && change["newUri"] is JsonValue to:
                        operations.Add((JsonNode)new JsonObject { ["kind"] = "rename", ["uri"] = from.DeepClone(), ["newUri"] = to.DeepClone() });
                        break;
                    case "delete" when change["uri"] is JsonValue deleted:
                        operations.Add((JsonNode)new JsonObject { ["kind"] = "delete", ["uri"] = deleted.DeepClone() });
                        break;
                }
            }
        }
        else if (edit["changes"] is JsonObject byUri)
        {
            foreach (var (uri, edits) in byUri)
            {
                JsonNode document = new JsonObject
                {
                    ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = null },
                    ["edits"] = edits?.DeepClone() ?? new JsonArray(),
                };
                changes.Add(document);
            }
        }

        return new JsonObject { ["documentChanges"] = changes, ["resourceOperations"] = operations };
    }

    /// <summary>Normalizes a workspace edit a server sends (<c>workspace/applyEdit</c>): <c>changes</c> become <c>documentChanges</c>.</summary>
    public static JsonElement WorkspaceEdit(JsonElement edit) => Element(WorkspaceEdit(JsonNode.Parse(edit.GetRawText())));

    private static JsonArray CodeActions(JsonNode? node) =>
        new JsonArray([.. (node as JsonArray ?? []).OfType<JsonObject>()
            .Where(a => a["command"] is null or JsonObject) // a bare Command has a string "command"
            .Select(a => CodeAction(a.DeepClone()))
            .OfType<JsonNode>()]);

    private static JsonNode? CodeAction(JsonNode? node)
    {
        if (node is JsonObject action)
        {
            if (action["edit"] is JsonObject edit)
            {
                action["edit"] = WorkspaceEdit(edit);
            }

            foreach (var diagnostic in (action["diagnostics"] as JsonArray ?? []).OfType<JsonObject>())
            {
                Diagnostic(diagnostic);
            }
        }

        return node;
    }

    private static JsonArray Symbols(JsonNode? node)
    {
        var symbols = new JsonArray();
        foreach (var symbol in (node as JsonArray ?? []).OfType<JsonObject>())
        {
            var copy = (JsonObject)symbol.DeepClone();
            if (copy["location"] is JsonObject location && location["range"] is null)
            {
                // WorkspaceSymbol with only a URI: point at the start of the file.
                location["range"] = new JsonObject
                {
                    ["start"] = new JsonObject { ["line"] = 0, ["character"] = 0 },
                    ["end"] = new JsonObject { ["line"] = 0, ["character"] = 0 },
                };
            }

            symbols.Add((JsonNode?)copy);
        }

        return symbols;
    }

    // ------------------------------------------------------------------ document symbols, inlay hints (M12)

    /// <summary>
    /// <c>DocumentSymbol[]</c> stays as it is; flat <c>SymbolInformation[]</c> becomes a tree, each symbol a child of
    /// the innermost earlier symbol whose range contains it.
    /// </summary>
    private static JsonNode? DocumentSymbols(JsonNode? node)
    {
        if (node is not JsonArray array || array.OfType<JsonObject>().All(s => s["location"] is null))
        {
            return node;
        }

        var flat = array.OfType<JsonObject>()
            .Where(s => s["location"]?["range"] is JsonObject)
            .Select(s => (Symbol: new JsonObject
            {
                ["name"] = s["name"]?.DeepClone(),
                ["kind"] = s["kind"]?.DeepClone(),
                ["range"] = s["location"]!["range"]!.DeepClone(),
                ["selectionRange"] = s["location"]!["range"]!.DeepClone(),
                ["detail"] = s["containerName"]?.DeepClone(),
            }, Range: RangeOf(s["location"]!["range"]!)))
            .OrderBy(s => s.Range.Start).ThenByDescending(s => s.Range.End)
            .ToList();
        var roots = new JsonArray();
        var stack = new Stack<(JsonObject Symbol, (long Start, long End) Range)>();
        foreach (var (symbol, range) in flat)
        {
            while (stack.Count > 0 && !(stack.Peek().Range.Start <= range.Start && range.End <= stack.Peek().Range.End))
            {
                stack.Pop();
            }

            if (stack.Count == 0)
            {
                roots.Add((JsonNode?)symbol);
            }
            else
            {
                var parent = stack.Peek().Symbol;
                if (parent["children"] is not JsonArray children)
                {
                    parent["children"] = children = new JsonArray();
                }

                children.Add((JsonNode?)symbol);
            }

            stack.Push((symbol, range));
        }

        return roots;
    }

    private static (long Start, long End) RangeOf(JsonNode range)
    {
        static long Key(JsonNode? position) => ((long)(position?["line"]?.GetValue<int>() ?? 0) << 32) + (position?["character"]?.GetValue<int>() ?? 0);
        return (Key(range["start"]), Key(range["end"]));
    }

    /// <summary>Label parts become one string; a string tooltip becomes plain-text markup.</summary>
    private static JsonNode? InlayHints(JsonNode? node)
    {
        foreach (var hint in (node as JsonArray ?? []).OfType<JsonObject>())
        {
            if (hint["label"] is JsonArray parts)
            {
                hint["label"] = string.Concat(parts.Select(p => p is JsonObject part ? part["value"]?.GetValue<string>() : p?.GetValue<string>()));
            }

            if (hint["tooltip"] is JsonValue tooltip && tooltip.GetValueKind() == JsonValueKind.String)
            {
                hint["tooltip"] = new JsonObject { ["kind"] = "plaintext", ["value"] = tooltip.GetValue<string>() };
            }
        }

        return node;
    }

    private static void Diagnostic(JsonObject diagnostic)
    {
        if (diagnostic["code"] is JsonValue code && code.GetValueKind() == JsonValueKind.Number)
        {
            diagnostic["code"] = code.ToJsonString();
        }

        diagnostic["severity"] ??= DiagnosticSeverity.Error;
    }
}
