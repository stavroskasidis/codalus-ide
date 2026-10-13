using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codalus.Workbench.Plugins;

/// <summary>A plugin bisect in progress (M22c, ADR 0060), kept in the state folder across restarts.</summary>
/// <param name="Suspects">The plugins one of which causes the problem.</param>
/// <param name="Original">The workspace's turned-off plugins before the bisect, put back at the end.</param>
/// <param name="Step">How many answers so far.</param>
public sealed record PluginBisectState(string[] Suspects, string[] Original, int Step)
{
    /// <summary>The suspects turned off in this round: the first half.</summary>
    [JsonIgnore]
    public string[] TurnedOff => Suspects[..(Suspects.Length / 2)];
}

/// <summary>The next step of a bisect: the workspace's turned-off plugins to apply (then restart), or the culprit.</summary>
/// <param name="Disabled">The workspace's <c>plugins.disabled</c> for the next round, or the original list when done.</param>
/// <param name="Culprit">The plugin found, when there's one suspect left.</param>
/// <param name="State">The state to keep, or null when the bisect is over.</param>
public sealed record PluginBisectStep(string[] Disabled, string? Culprit, PluginBisectState? State);

/// <summary>
/// Finds the plugin behind a problem by halves: each round turns half of the suspects off for the workspace and
/// restarts, and the user says whether the problem is still there. Built-in plugins are never suspects.
/// </summary>
public static class PluginBisect
{
    /// <summary>Starts with the enabled third-party plugins; null when there's nothing to bisect (none, or one).</summary>
    public static PluginBisectStep? Start(IReadOnlyList<string> enabledThirdParty, IReadOnlyList<string> workspaceDisabled)
    {
        if (enabledThirdParty.Count == 0)
        {
            return null;
        }

        var state = new PluginBisectState([.. enabledThirdParty.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)], [.. workspaceDisabled], 0);
        return state.Suspects.Length == 1
            ? new PluginBisectStep([.. workspaceDisabled], state.Suspects[0], null)
            : new PluginBisectStep([.. workspaceDisabled.Union(state.TurnedOff, StringComparer.OrdinalIgnoreCase)], null, state);
    }

    /// <summary>
    /// The user's answer for this round: <paramref name="stillThere"/> means the problem came back with
    /// <see cref="PluginBisectState.TurnedOff"/> off, so the culprit is among the others.
    /// </summary>
    public static PluginBisectStep Answer(PluginBisectState state, bool stillThere)
    {
        ArgumentNullException.ThrowIfNull(state);
        var half = state.Suspects.Length / 2;
        string[] suspects = stillThere ? state.Suspects[half..] : state.Suspects[..half];
        if (suspects.Length <= 1)
        {
            return new PluginBisectStep(state.Original, suspects.FirstOrDefault(), null);
        }

        var next = state with { Suspects = suspects, Step = state.Step + 1 };
        return new PluginBisectStep([.. state.Original.Union(next.TurnedOff, StringComparer.OrdinalIgnoreCase)], null, next);
    }

    /// <summary>Stops: the original list comes back.</summary>
    public static PluginBisectStep Stop(PluginBisectState state) => new(state.Original, null, null);

    public static PluginBisectState? Load(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize(File.ReadAllText(file), PluginBisectJsonContext.Default.PluginBisectState) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    public static void Save(string file, PluginBisectState? state)
    {
        if (state is null)
        {
            File.Delete(file);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(state, PluginBisectJsonContext.Default.PluginBisectState));
    }
}

[JsonSerializable(typeof(PluginBisectState))]
internal sealed partial class PluginBisectJsonContext : JsonSerializerContext;
