using System.Text.Json;

namespace Codalus.Workbench.Projects;

/// <summary>A file a template names as its result (<c>primaryOutputs</c>), with the condition it's written under.</summary>
public sealed record TemplateOutput(string Path, string? Condition);

/// <summary>A post action of a template: its id, condition and arguments (as text).</summary>
public sealed record TemplatePostAction(string ActionId, string? Condition, IReadOnlyDictionary<string, string> Args);

/// <summary>
/// What New Project reads from a template's <c>template.json</c>, which the template cache leaves out: the text replaced
/// by the project's name (<c>sourceName</c>), its result files (<c>primaryOutputs</c>), its post actions, and the
/// conditions of its computed symbols.
/// </summary>
public sealed record TemplateDefinition
{
    public static TemplateDefinition Empty { get; } = new();

    public string? SourceName { get; init; }

    public IReadOnlyList<TemplateOutput> PrimaryOutputs { get; init; } = [];

    public IReadOnlyList<TemplatePostAction> PostActions { get; init; } = [];

    /// <summary>Computed symbols' conditions by name (<c>UseWebAssembly</c> → <c>(InteractivityPlatform == "WebAssembly" || …)</c>).</summary>
    public IReadOnlyDictionary<string, string> ComputedSymbols { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Symbols' <c>isEnabled</c> conditions by name.</summary>
    public IReadOnlyDictionary<string, string> EnabledConditions { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public static TemplateDefinition Parse(string? text)
    {
        using var document = TemplateJson.Parse(text);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        var root = document.RootElement;
        var computed = new Dictionary<string, string>(StringComparer.Ordinal);
        var enabled = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.Object("symbols") is { } symbols)
        {
            foreach (var symbol in symbols.EnumerateObject().Where(s => s.Value.ValueKind == JsonValueKind.Object))
            {
                var type = symbol.Value.Text("type");
                if (string.Equals(type, "computed", StringComparison.OrdinalIgnoreCase) && symbol.Value.Text("value") is { Length: > 0 } value)
                {
                    computed[symbol.Name] = value;
                }

                if (symbol.Value.Text("isEnabled") is { Length: > 0 } condition)
                {
                    enabled[symbol.Name] = condition;
                }
            }
        }

        return new TemplateDefinition
        {
            SourceName = root.Text("sourceName") is { Length: > 0 } sourceName ? sourceName : null,
            PrimaryOutputs = [.. root.Items("primaryOutputs").Where(o => o.Text("path") is { Length: > 0 })
                .Select(o => new TemplateOutput(o.Text("path")!, o.Text("condition") is { Length: > 0 } c ? c : null))],
            PostActions = [.. root.Items("postActions").Where(a => a.Text("actionId") is { Length: > 0 }).Select(a => new TemplatePostAction(
                a.Text("actionId")!,
                a.Text("condition") is { Length: > 0 } c ? c : null,
                a.Object("args") is { } args
                    ? args.EnumerateObject().Where(p => TemplateJson.AsText(p.Value) is not null).ToDictionary(p => p.Name, p => TemplateJson.AsText(p.Value)!, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)))],
            ComputedSymbols = computed,
            EnabledConditions = enabled,
        };
    }
}

/// <summary>
/// The files to open after Create, as an IDE host reads a template: its result files (<c>primaryOutputs</c>) whose
/// conditions hold for an IDE (<see cref="TemplateCondition.IdeHostIdentifier"/>) and the chosen values, picked by the
/// open-file post action's <c>files</c> indices, with the project's name in place of the template's <c>sourceName</c>.
/// </summary>
public sealed record OpenFileRule(TemplateDefinition Definition)
{
    /// <summary>The post action that opens files in the editor.</summary>
    public const string OpenFileActionId = "84C0DA21-51C8-4541-9940-6CA19AF04EE6";

    /// <summary>The rule of a template, or null when it opens nothing.</summary>
    public static OpenFileRule? From(TemplateDefinition definition) =>
        definition.PostActions.Any(a => string.Equals(a.ActionId, OpenFileActionId, StringComparison.OrdinalIgnoreCase)) ? new OpenFileRule(definition) : null;

    /// <summary>The files to open in <paramref name="projectDirectory"/> that exist, in the template's order.</summary>
    public IReadOnlyList<string> Resolve(IReadOnlyDictionary<string, string?> values, string projectDirectory, string projectName)
    {
        var outputs = Definition.PrimaryOutputs.Where(o => TemplateCondition.Evaluate(o.Condition, values, Definition.ComputedSymbols)).ToList();
        var action = Definition.PostActions.FirstOrDefault(a => string.Equals(a.ActionId, OpenFileActionId, StringComparison.OrdinalIgnoreCase)
            && TemplateCondition.Evaluate(a.Condition, values, Definition.ComputedSymbols));
        if (action is null || !action.Args.TryGetValue("files", out var indices))
        {
            return [];
        }

        var files = new List<string>();
        foreach (var index in indices.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(index, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var i) && i >= 0 && i < outputs.Count)
            {
                var relative = Definition.SourceName is { } source ? outputs[i].Path.Replace(source, projectName, StringComparison.Ordinal) : outputs[i].Path;
                var path = Path.GetFullPath(Path.Combine(projectDirectory, relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
                if (File.Exists(path))
                {
                    files.Add(path);
                }
            }
        }

        return files;
    }
}
