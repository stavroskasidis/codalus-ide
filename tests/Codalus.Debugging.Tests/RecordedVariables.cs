using System.Text.Json;
using System.Text.Json.Nodes;
using Codalus.Workbench.Debugging;

namespace Codalus.Debugging.Tests;

/// <summary>
/// A debugger's variables recorded from a paused program (<c>Recorded/*.json</c>, D44): a tree of what <c>variables</c>
/// answered, each variable with the members it expanded to. Loaded, every variable with members gets a reference of its
/// own, so code that reads variables runs on it as on the debugger that recorded it.
/// </summary>
internal sealed class RecordedVariables
{
    private readonly Dictionary<int, IReadOnlyList<Variable>> _lists = [];
    private int _next = 1;

    private RecordedVariables(JsonObject recording)
    {
        Runtime = recording["runtime"]?.GetValue<string>() ?? string.Empty;
        Locals = Load(recording["locals"]!.AsArray());
    }

    /// <summary>The runtime the program ran on (<c>net11.0</c>, <c>net48</c>, <c>mono</c>…).</summary>
    public string Runtime { get; }

    /// <summary>The paused frame's locals.</summary>
    public IReadOnlyList<Variable> Locals { get; }

    /// <summary>How many times <see cref="VariablesAsync"/> was asked.</summary>
    public int Requests { get; private set; }

    public static string Folder => Path.Combine(DebuggeeFixture.RepoRoot, "tests", "Codalus.Debugging.Tests", "Recorded");

    public static RecordedVariables Read(string name) => new(JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, name + ".json")))!.AsObject());

    /// <summary>Every recording, by name (<c>datatip-net11.0</c>…).</summary>
    public static IEnumerable<string> Names => Directory.EnumerateFiles(Folder, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.Ordinal);

    public Variable Local(string name) => Locals.Single(v => v.Name == name);

    public Task<IReadOnlyList<Variable>> VariablesAsync(int reference)
    {
        Requests++;
        return Task.FromResult(_lists.TryGetValue(reference, out var list) ? list : (IReadOnlyList<Variable>)[]);
    }

    private List<Variable> Load(JsonArray variables)
    {
        var list = new List<Variable>();
        foreach (var node in variables.Select(v => v!.AsObject()))
        {
            var reference = 0;
            if (node["children"] is JsonArray children)
            {
                reference = _next++;
                _lists[reference] = Load(children);
            }
            else if (node["variablesReference"]?.GetValue<int>() is > 0)
            {
                reference = _next++; // members that weren't recorded
            }

            list.Add(new Variable(node["name"]!.GetValue<string>(), node["value"]?.GetValue<string>() ?? string.Empty, node["type"]?.GetValue<string>(), reference, node["evaluateName"]?.GetValue<string>()));
        }

        return list;
    }

    /// <summary>
    /// Records a paused frame's locals, each expanded <paramref name="depth"/> levels (netcoredbg's Static members
    /// aren't), as <see cref="Read"/> loads them.
    /// </summary>
    public static async Task<JsonObject> RecordAsync(DebugService debug, string runtime, int depth)
    {
        var frame = debug.SelectedFrame ?? throw new InvalidOperationException("The program isn't paused.");
        var scopes = await debug.ScopesAsync(frame);
        return new JsonObject
        {
            ["runtime"] = runtime,
            ["locals"] = await TreeAsync(debug, scopes[0].VariablesReference, depth),
        };
    }

    private static async Task<JsonArray> TreeAsync(DebugService debug, int reference, int depth, bool top = true)
    {
        var result = new JsonArray();
        foreach (var variable in await debug.VariablesAsync(reference))
        {
            var node = new JsonObject
            {
                ["name"] = variable.Name,
                ["value"] = variable.Value,
                ["type"] = variable.Type,
                ["evaluateName"] = variable.EvaluateName,
                ["variablesReference"] = variable.VariablesReference,
            };
            if (variable.VariablesReference > 0 && depth > 0 && !VariableGrouping.IsStaticNode(variable) && !Internal(variable.Type) && (top || Read(variable)))
            {
                // An exception's own members only (its Message): netcoredbg 3.2 dies reading System.Signature's.
                node["children"] = await TreeAsync(debug, variable.VariablesReference, VariableGrouping.IsException(variable.Type) ? 0 : depth - 1, top: false);
            }

            result.Add(node);
        }

        return result;
    }

    /// <summary>
    /// What a collection view reads, under a local: its fields (<c>_items</c>, <c>entries</c>, <c>m_slots</c>), an
    /// entry or a slot of a dictionary or a set, and a getter that threw (for its Message).
    /// </summary>
    private static bool Read(Variable variable) =>
        variable.Name.StartsWith('_') || variable.Name.StartsWith("m_", StringComparison.Ordinal) || (variable.Name.Length > 0 && char.IsLower(variable.Name[0]))
        || (variable.Name.StartsWith('[') && variable.Type is { } type && (type.Contains("Entry", StringComparison.Ordinal) || type.Contains("Slot", StringComparison.Ordinal)))
        || VariableGrouping.IsException(variable.Type);

    /// <summary>The runtime's reflection internals, which nothing here needs.</summary>
    private static bool Internal(string? type) =>
        type is not null && (type.StartsWith("System.Reflection.", StringComparison.Ordinal) || type.StartsWith("System.Runtime", StringComparison.Ordinal) || type is "System.Signature" or "System.Type");

    public static string Serialize(JsonObject recording) => recording.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
}
