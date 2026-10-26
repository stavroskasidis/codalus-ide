using System.Text.Json;

namespace Codalus.Workbench.Ai.Acp;

/// <summary>How a choice is sent to the agent.</summary>
public enum AcpChoiceKind
{
    /// <summary><c>session/set_config_option</c>.</summary>
    ConfigOption,

    /// <summary><c>session/set_mode</c> (the modes of <c>session/new</c>, for agents without config options).</summary>
    Mode,

    /// <summary><c>session/set_model</c> (the unstable <c>models</c> of <c>session/new</c>).</summary>
    Model,
}

/// <summary>A value of a choice: what is sent, and what the picker shows.</summary>
public sealed record AcpChoiceValue(string Value, string Name, string? Description);

/// <summary>
/// A choice the agent offers for its session (M51): the model, the mode, the effort (thought level) or another select,
/// with its current value. <see cref="Id"/> is the config option's id, or <c>mode</c> and <c>model</c> for the older fields.
/// </summary>
public sealed record AcpSessionChoice(string Id, string Name, string? Category, string? CurrentValue, IReadOnlyList<AcpChoiceValue> Values, AcpChoiceKind Kind)
{
    /// <summary>The current value's name, or the value itself when the agent didn't list it.</summary>
    public string CurrentName => Values.FirstOrDefault(v => v.Value == CurrentValue)?.Name ?? CurrentValue ?? string.Empty;
}

/// <summary>The choices of a session from what <c>session/new</c> (and later updates) gave.</summary>
public static class AcpSessionChoices
{
    public const string ModeCategory = "mode";
    public const string ModelCategory = "model";
    public const string ThoughtLevelCategory = "thought_level";

    /// <summary>
    /// The config options that are selects with values, then the older modes and models when no config option has their
    /// category (an agent that sends both means the same choice twice; the protocol says to prefer config options).
    /// Ordered model, mode, effort, then the others as the agent listed them.
    /// </summary>
    public static IReadOnlyList<AcpSessionChoice> From(IReadOnlyList<AcpSessionConfigOption>? options, AcpSessionModeState? modes, AcpSessionModelState? models)
    {
        var choices = new List<AcpSessionChoice>();
        foreach (var option in options ?? [])
        {
            if (option.Type != "select" || option.Id.Length == 0)
            {
                continue;
            }

            var values = Flatten(option.Options);
            if (values.Count > 0)
            {
                var current = option.CurrentValue is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
                choices.Add(new AcpSessionChoice(option.Id, option.Name.Length > 0 ? option.Name : option.Id, option.Category, current, values, AcpChoiceKind.ConfigOption));
            }
        }

        if (models?.AvailableModels is { Count: > 0 } available && !choices.Any(c => c.Category == ModelCategory))
        {
            choices.Add(new AcpSessionChoice("model", "Model", ModelCategory, models.CurrentModelId, [.. available.Select(m => new AcpChoiceValue(m.ModelId, m.Name.Length > 0 ? m.Name : m.ModelId, m.Description))], AcpChoiceKind.Model));
        }

        if (modes?.AvailableModes is { Count: > 0 } modeList && !choices.Any(c => c.Category == ModeCategory))
        {
            choices.Add(new AcpSessionChoice("mode", "Mode", ModeCategory, modes.CurrentModeId, [.. modeList.Select(m => new AcpChoiceValue(m.Id, m.Name.Length > 0 ? m.Name : m.Id, m.Description))], AcpChoiceKind.Mode));
        }

        return [.. choices.Select((c, i) => (c, i)).OrderBy(x => Rank(x.c.Category)).ThenBy(x => x.i).Select(x => x.c)];
    }

    private static int Rank(string? category) => category switch
    {
        ModelCategory => 0,
        ModeCategory => 1,
        ThoughtLevelCategory => 2,
        _ => 3,
    };

    /// <summary>A select's values: a list of <c>{ value, name, description }</c>, or of groups <c>{ group, name, options: [...] }</c>.</summary>
    public static IReadOnlyList<AcpChoiceValue> Flatten(JsonElement? options)
    {
        var values = new List<AcpChoiceValue>();
        if (options is { ValueKind: JsonValueKind.Array } list)
        {
            Add(list, values);
        }

        return values;

        static void Add(JsonElement list, List<AcpChoiceValue> values)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (item.TryGetProperty("options", out var inner) && inner.ValueKind == JsonValueKind.Array)
                {
                    Add(inner, values);
                }
                else if (Text(item, "value") is { Length: > 0 } value)
                {
                    values.Add(new AcpChoiceValue(value, Text(item, "name") is { Length: > 0 } name ? name : value, Text(item, "description")));
                }
            }
        }

        static string? Text(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
