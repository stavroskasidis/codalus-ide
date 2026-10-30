using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codalus.LspConformance.Server;

/// <summary>
/// A paused program for the data tip and variable window tests (<c>--dap</c> with a launch configuration's
/// <c>inspect</c>): one thread, stopped after <c>configurationDone</c> at <c>path</c>:<c>line</c>, answering as
/// netcoredbg does from its script, recorded from netcoredbg where it matters:
/// <code>
/// "inspect": { "path": "/x/Program.cs", "line": 12, "stops": [ {
///     "line": 12,                                         (optional: another line for this stop)
///     "scopes": [ { "name": "Locals", "variablesReference": 1 } ],   (optional: this one is the default)
///     "variables": { "1": [ { "name", "value", "type", "variablesReference", "evaluateName", "namedVariables" } ],
///                    "5": { "generate": 100000, "name": "[{0}]", "value": "{0}", "type": "int", "evaluateName": "a[{0}]" } },
///                                                         (a request's start and count page them, as netcoredbg's do)
///     "evaluate": { "app": { "result", "type", "variablesReference", "namedVariables" } },
///     "errors": { "app.Name": "error: ..." }              (setVariable or setExpression by evaluateName fail with it;
///                                                          "app.Name=Production": only for that value)
/// } ] }
/// </code>
/// An expression <c>evaluate</c> doesn't list is answered from the variable with that <c>evaluateName</c>, else with
/// netcoredbg's error for an unknown name. A step (<c>next</c>, <c>stepIn</c>, <c>stepOut</c>) or <c>continue</c> goes
/// to the next stop; after the last, the program exits. An edit changes the stop's value, as the program's would.
/// </summary>
internal sealed class ScriptedInspection
{
    private readonly string _path;
    private readonly int _line;
    private readonly JsonArray _stops;
    private readonly int? _processId;
    private int _stop;

    private ScriptedInspection(string path, int line, JsonArray stops, int? processId)
    {
        _path = path;
        _line = line;
        _stops = stops;
        _processId = processId;
    }

    /// <summary>The script's <c>inspect</c>; its <c>processId</c> (optional) is reported as the program's, as netcoredbg's process event does.</summary>
    public static ScriptedInspection? From(JsonElement? launch) =>
        launch is { ValueKind: JsonValueKind.Object } arguments && arguments.TryGetProperty("inspect", out var inspect)
            ? new ScriptedInspection(inspect.GetProperty("path").GetString()!, inspect.GetProperty("line").GetInt32(), JsonNode.Parse(inspect.GetProperty("stops").GetRawText())!.AsArray(),
                inspect.TryGetProperty("processId", out var processId) ? processId.GetInt32() : null)
            : null;

    private JsonObject Stop => _stops[_stop]!.AsObject();

    /// <summary>Answers <paramref name="command"/> when the script covers it.</summary>
    public async Task<bool> HandleAsync(Stream output, JsonElement message, string command, int requestSeq)
    {
        var arguments = message.TryGetProperty("arguments", out var a) ? a : default;
        switch (command)
        {
            case "configurationDone":
                await ScriptedDap.RespondAsync(output, requestSeq, command, null);
                if (_processId is { } processId)
                {
                    await ScriptedDap.EventAsync(output, "process", new JsonObject { ["name"] = "program", ["systemProcessId"] = processId, ["isLocalProcess"] = true, ["startMethod"] = "launch" });
                }

                await ScriptedDap.EventAsync(output, "thread", new JsonObject { ["reason"] = "started", ["threadId"] = 1 });
                await ScriptedDap.EventAsync(output, "stopped", new JsonObject { ["reason"] = "breakpoint", ["threadId"] = 1, ["allThreadsStopped"] = true });
                return true;
            case "threads":
                await ScriptedDap.RespondAsync(output, requestSeq, command, new JsonObject { ["threads"] = new JsonArray(new JsonObject { ["id"] = 1, ["name"] = "Main Thread" }) });
                return true;
            case "stackTrace":
                var line = Stop["line"]?.GetValue<int>() ?? _line;
                await ScriptedDap.RespondAsync(output, requestSeq, command, new JsonObject
                {
                    ["stackFrames"] = new JsonArray(new JsonObject { ["id"] = 1, ["name"] = "Program.Main()", ["source"] = new JsonObject { ["path"] = _path }, ["line"] = line, ["column"] = 1 }),
                    ["totalFrames"] = 1,
                });
                return true;
            case "scopes":
                await ScriptedDap.RespondAsync(output, requestSeq, command, new JsonObject
                {
                    ["scopes"] = Stop["scopes"]?.DeepClone() ?? new JsonArray(new JsonObject { ["name"] = "Locals", ["variablesReference"] = 1, ["expensive"] = false }),
                });
                return true;
            case "variables":
                {
                    var reference = arguments.GetProperty("variablesReference").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var start = arguments.TryGetProperty("start", out var s) ? s.GetInt32() : 0;
                    int? count = arguments.TryGetProperty("count", out var c) && c.GetInt32() > 0 ? c.GetInt32() : null;
                    await ScriptedDap.RespondAsync(output, requestSeq, command, new JsonObject { ["variables"] = Variables(Stop["variables"]?[reference], start, count) });
                    return true;
                }
            case "evaluate":
                {
                    var expression = arguments.GetProperty("expression").GetString()!;
                    if (Evaluate(expression) is { } result)
                    {
                        await ScriptedDap.RespondAsync(output, requestSeq, command, result);
                    }
                    else
                    {
                        await ScriptedDap.FailAsync(output, requestSeq, command, $"error: The name '{expression}' does not exist in the current context");
                    }

                    return true;
                }

            case "setVariable":
                {
                    var name = arguments.GetProperty("name").GetString()!;
                    var value = arguments.GetProperty("value").GetString()!;
                    var list = Stop["variables"]?[arguments.GetProperty("variablesReference").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonArray;
                    var variable = list?.Select(v => v!.AsObject()).FirstOrDefault(v => v["name"]?.GetValue<string>() == name);
                    await SetAsync(output, requestSeq, command, variable?["evaluateName"]?.GetValue<string>() ?? name, value, variable);
                    return true;
                }

            case "setExpression":
                {
                    var expression = arguments.GetProperty("expression").GetString()!;
                    await SetAsync(output, requestSeq, command, expression, arguments.GetProperty("value").GetString()!, Variable(expression));
                    return true;
                }

            case "next" or "stepIn" or "stepOut" or "continue":
                await ScriptedDap.RespondAsync(output, requestSeq, command, command == "continue" ? new JsonObject { ["allThreadsContinued"] = true } : null);
                await ScriptedDap.EventAsync(output, "continued", new JsonObject { ["threadId"] = 1, ["allThreadsContinued"] = true });
                if (_stop + 1 < _stops.Count)
                {
                    _stop++;
                    await ScriptedDap.EventAsync(output, "stopped", new JsonObject { ["reason"] = command == "continue" ? "breakpoint" : "step", ["threadId"] = 1, ["allThreadsStopped"] = true });
                }
                else
                {
                    await ScriptedDap.EventAsync(output, "exited", new JsonObject { ["exitCode"] = 0 });
                    await ScriptedDap.EventAsync(output, "terminated", new JsonObject());
                }

                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// A reference's children from <paramref name="start"/>, <paramref name="count"/> of them (all without one), as
    /// netcoredbg pages them: listed, or generated (<c>{ "generate": 100000, "name": "[{0}]", "value": "{0}", "type":
    /// "int", "evaluateName": "numbers[{0}]" }</c>, <c>{0}</c> the index) for an array too long to list.
    /// </summary>
    private static JsonArray Variables(JsonNode? listed, int start, int? count)
    {
        if (listed is JsonObject { } generator && generator["generate"]?.GetValue<int>() is { } length)
        {
            string Text(string key, int index) => (generator[key]?.GetValue<string>() ?? string.Empty).Replace("{0}", index.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
            var end = count is { } n ? Math.Min(length, start + n) : length;
            var page = new JsonArray();
            for (var i = start; i < end; i++)
            {
                page.Add(new JsonObject { ["name"] = Text("name", i), ["value"] = Text("value", i), ["type"] = generator["type"]?.DeepClone(), ["variablesReference"] = 0, ["evaluateName"] = Text("evaluateName", i) });
            }

            return page;
        }

        var all = listed as JsonArray ?? [];
        return [.. all.Skip(start).Take(count ?? int.MaxValue).Select(v => v?.DeepClone())];
    }

    private JsonObject? Evaluate(string expression)
    {
        if (Stop["evaluate"]?[expression] is JsonObject listed)
        {
            return (JsonObject)listed.DeepClone();
        }

        return Variable(expression) is { } variable
            ? new JsonObject { ["result"] = variable["value"]?.DeepClone(), ["type"] = variable["type"]?.DeepClone(), ["variablesReference"] = variable["variablesReference"]?.DeepClone() ?? 0 }
            : null;
    }

    private JsonObject? Variable(string evaluateName) =>
        (Stop["variables"] as JsonObject)?.SelectMany(list => list.Value as JsonArray ?? [])
            .Select(v => v!.AsObject())
            .FirstOrDefault(v => v["evaluateName"]?.GetValue<string>() == evaluateName);

    private async Task SetAsync(Stream output, int requestSeq, string command, string evaluateName, string value, JsonObject? variable)
    {
        if ((Stop["errors"]?[evaluateName + "=" + value] ?? Stop["errors"]?[evaluateName])?.GetValue<string>() is { } error)
        {
            await ScriptedDap.FailAsync(output, requestSeq, command, error);
            return;
        }

        if (variable is not null)
        {
            variable["value"] = value;
        }

        if (Stop["evaluate"]?[evaluateName] is JsonObject listed)
        {
            listed["result"] = value;
        }

        await ScriptedDap.RespondAsync(output, requestSeq, command, new JsonObject
        {
            ["value"] = value,
            ["type"] = variable?["type"]?.DeepClone(),
            ["variablesReference"] = variable?["variablesReference"]?.DeepClone() ?? 0,
        });
    }
}
