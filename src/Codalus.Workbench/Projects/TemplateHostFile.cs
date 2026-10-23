using System.Text.Json;

namespace Codalus.Workbench.Projects;

/// <summary>A symbol's entry in a template's IDE host file (<c>ide.host.json</c>'s <c>symbolInfo</c>).</summary>
public sealed record HostSymbol(
    string Id,
    bool IsVisible,
    string? Name,
    string? Description,
    bool InvertBoolean,
    string? DefaultValue,
    string? PersistenceScope,
    string? PersistenceScopeName);

/// <summary>Tags an IDE host file adds to or removes from a template (<c>platform</c> or <c>projectType</c>).</summary>
public sealed record HostTags(string Type, IReadOnlyList<string> Add, IReadOnlyList<string> Remove);

/// <summary>
/// A template's IDE host file (<c>ide.host.json</c>): which symbols an IDE shows and how (their order, labels, inverted
/// check boxes, defaults and where their values are remembered), the template's order and icon, whether it supports
/// containers, the symbol that turns HTTPS off, and tags. Keys are read in any case, booleans as booleans or strings.
/// </summary>
public sealed record TemplateHostFile
{
    public static TemplateHostFile Empty { get; } = new();

    public IReadOnlyList<HostSymbol> Symbols { get; init; } = [];

    public int? Order { get; init; }

    public string? Icon { get; init; }

    public bool SupportsDocker { get; init; }

    public string? DisableHttpsSymbol { get; init; }

    public IReadOnlyList<HostTags> Tags { get; init; } = [];

    /// <summary>Whether the file was there (a template without one shows its CLI-visible parameters instead).</summary>
    public bool Exists { get; init; }

    public static TemplateHostFile Parse(string? text)
    {
        using var document = TemplateJson.Parse(text);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        var root = document.RootElement;
        var symbols = new List<HostSymbol>();
        if (root.TryGet("symbolInfo", out var info))
        {
            // An array of { id, … } (the schema), or an object keyed by id (older files).
            IEnumerable<(string? Id, JsonElement Value)> entries = info.ValueKind switch
            {
                JsonValueKind.Array => info.EnumerateArray().Select(e => (e.Text("id"), e)),
                JsonValueKind.Object => info.EnumerateObject().Select(p => ((string?)p.Name, p.Value)),
                _ => [],
            };
            foreach (var (id, value) in entries)
            {
                if (id is not { Length: > 0 } || value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                symbols.Add(new HostSymbol(
                    id,
                    value.Bool("isVisible") ?? false,
                    Override(value, "name"),
                    Override(value, "description"),
                    value.Bool("invertBoolean") ?? false,
                    value.Text("defaultValue"),
                    value.Text("persistenceScope"),
                    value.Text("persistenceScopeName")));
            }
        }

        return new TemplateHostFile
        {
            Exists = true,
            Symbols = symbols,
            Order = root.Int("order"),
            Icon = root.Text("icon"),
            SupportsDocker = root.Bool("supportsDocker") ?? false,
            DisableHttpsSymbol = root.Text("disableHttpsSymbol") is { Length: > 0 } https ? https : null,
            Tags = [.. root.Items("tags").Where(t => t.ValueKind == JsonValueKind.Object && t.Text("type") is not null)
                .Select(t => new HostTags(t.Text("type")!, t.Strings("add"), t.Strings("remove")))],
        };
    }

    /// <summary>
    /// The key a symbol's last value is remembered under: <c>templateGroup</c> → <c>group:&lt;group&gt;</c>; <c>shared</c>
    /// with a name → <c>shared:&lt;name&gt;</c>; <c>shared</c> without one → <c>shared::&lt;symbol&gt;</c> (every template
    /// with that symbol). No scope: not remembered.
    /// </summary>
    public static string? PersistenceKey(HostSymbol symbol, string group) => symbol.PersistenceScope?.Trim().ToLowerInvariant() switch
    {
        "templategroup" => "group:" + group,
        "shared" when symbol.PersistenceScopeName is { Length: > 0 } name => "shared:" + name,
        "shared" => "shared::" + symbol.Id,
        _ => null,
    };

    /// <summary>A name or description override: <c>{ "text": …, "overrideDefaultText": true }</c>, or a plain string.</summary>
    private static string? Override(JsonElement symbol, string key)
    {
        if (!symbol.TryGet(key, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return value.ValueKind == JsonValueKind.Object && (value.Bool("overrideDefaultText") ?? true) && value.Text("text") is { Length: > 0 } text ? text : null;
    }
}
