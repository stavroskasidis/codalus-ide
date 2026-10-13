using System.Text.Json;

namespace DaedalusStudio.Workbench.Menus;

/// <summary>The user's changes to one menu or toolbar location (M23b).</summary>
/// <param name="Add">Commands added at the end, in this order.</param>
/// <param name="Remove">Commands (or submenu locations) taken out.</param>
/// <param name="Order">When set, the items in this order (commands or submenu locations); others follow as they were.</param>
public sealed record LocationCustomization(IReadOnlyList<string> Add, IReadOnlyList<string> Remove, IReadOnlyList<string>? Order)
{
    public static LocationCustomization Empty { get; } = new([], [], null);

    public bool IsEmpty => Add.Count == 0 && Remove.Count == 0 && Order is null;
}

/// <summary>
/// The user's customizations of menus and toolbars (M23b), from the <c>workbench.commandBarCustomizations</c> setting:
/// <c>{ "locations": { "menuBar/tools": { "add": [], "remove": [], "order": [] } }, "toolbars": { "Name": ["command", …] } }</c>.
/// The standard toolbar's location is <see cref="StandardToolbar"/>; a user toolbar's is <see cref="ToolbarLocation"/>.
/// </summary>
public sealed record MenuCustomizations(IReadOnlyDictionary<string, LocationCustomization> Locations, IReadOnlyList<(string Name, IReadOnlyList<string> Commands)> Toolbars)
{
    public const string SettingKey = "workbench.commandBarCustomizations";
    public const string StandardToolbar = "toolbar/standard";
    public const string ToolbarPrefix = "toolbar/user/";

    public static MenuCustomizations None { get; } = new(new Dictionary<string, LocationCustomization>(), []);

    public static string ToolbarLocation(string name) => ToolbarPrefix + name;

    public LocationCustomization For(string location) => Locations.GetValueOrDefault(location) ?? LocationCustomization.Empty;

    /// <summary>Reads the setting's value; anything malformed is left out.</summary>
    public static MenuCustomizations Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return None;
        }

        var locations = new Dictionary<string, LocationCustomization>(StringComparer.Ordinal);
        if (value.TryGetProperty("locations", out var all) && all.ValueKind == JsonValueKind.Object)
        {
            foreach (var location in all.EnumerateObject().Where(l => l.Value.ValueKind == JsonValueKind.Object))
            {
                var order = location.Value.TryGetProperty("order", out var o) && o.ValueKind == JsonValueKind.Array ? Strings(o) : null;
                var customization = new LocationCustomization(Strings(location.Value, "add"), Strings(location.Value, "remove"), order);
                if (!customization.IsEmpty)
                {
                    locations[location.Name] = customization;
                }
            }
        }

        var toolbars = new List<(string, IReadOnlyList<string>)>();
        if (value.TryGetProperty("toolbars", out var bars) && bars.ValueKind == JsonValueKind.Object)
        {
            foreach (var bar in bars.EnumerateObject().Where(b => b.Value.ValueKind == JsonValueKind.Array && b.Name.Trim().Length > 0))
            {
                toolbars.Add((bar.Name, Strings(bar.Value)));
            }
        }

        return new(locations, toolbars);
    }

    /// <summary>The setting's value for these customizations.</summary>
    public JsonElement ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("locations");
            foreach (var (location, customization) in Locations.Where(l => !l.Value.IsEmpty).OrderBy(l => l.Key, StringComparer.Ordinal))
            {
                writer.WriteStartObject(location);
                Write(writer, "add", customization.Add);
                Write(writer, "remove", customization.Remove);
                if (customization.Order is { } order)
                {
                    Write(writer, "order", order);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteStartObject("toolbars");
            foreach (var (name, commands) in Toolbars)
            {
                Write(writer, name, commands);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();

        static void Write(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
        {
            writer.WriteStartArray(name);
            foreach (var value in values)
            {
                writer.WriteStringValue(value);
            }

            writer.WriteEndArray();
        }
    }

    public MenuCustomizations With(string location, LocationCustomization customization)
    {
        var locations = new Dictionary<string, LocationCustomization>(Locations, StringComparer.Ordinal) { [location] = customization };
        return this with { Locations = locations };
    }

    private static List<string> Strings(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array ? Strings(array) : [];

    private static List<string> Strings(JsonElement array) =>
        [.. array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Where(s => s.Length > 0)];
}
