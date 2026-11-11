using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codalus.Workbench.Ai.Cli;

/// <summary>A console as it is kept between runs of the IDE: its agent, and its conversation's id when Codalus gave it one.</summary>
public sealed record AgentConsoleTab(string Agent, string? Session);

/// <summary>
/// The consoles each workspace had open (ADR 0094), in the user's state folder: never in the solution's own
/// <c>.codalus</c> folder, which a cloned repository could ship. Only names and ids: the agent must still be in the
/// user's <c>ai.agents</c>, and an id that isn't a UUID is dropped when read (it becomes an argument of the CLI).
/// </summary>
public sealed class AgentConsoleState(string file)
{
    /// <summary>How many workspaces are remembered; the ones whose folder is gone go first.</summary>
    internal const int MaxWorkspaces = 200;

    private readonly Lock _gate = new();

    /// <summary>The consoles of <paramref name="workspaceRoot"/>, in their order.</summary>
    public IReadOnlyList<AgentConsoleTab> Load(string workspaceRoot)
    {
        lock (_gate)
        {
            return Read().TryGetValue(Key(workspaceRoot), out var tabs)
                ? [.. tabs.Where(t => t.Agent is { Length: > 0 }).Select(t => t with { Session = Guid.TryParse(t.Session, out var id) ? id.ToString("D") : null })]
                : [];
        }
    }

    /// <summary>Keeps <paramref name="tabs"/> for <paramref name="workspaceRoot"/> (none: the workspace is forgotten).</summary>
    public void Save(string workspaceRoot, IReadOnlyList<AgentConsoleTab> tabs)
    {
        lock (_gate)
        {
            var all = Read();
            if (tabs.Count == 0)
            {
                if (!all.Remove(Key(workspaceRoot)))
                {
                    return;
                }
            }
            else
            {
                all[Key(workspaceRoot)] = [.. tabs];
            }

            foreach (var gone in all.Keys.Where(k => !Directory.Exists(k)).Take(Math.Max(0, all.Count - MaxWorkspaces)).ToList())
            {
                all.Remove(gone);
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var temporary = file + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(new AgentConsoleStateFile(all), AgentConsoleJson.Default.AgentConsoleStateFile));
                File.Move(temporary, file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not kept: the next start opens a new console.
            }
        }
    }

    private Dictionary<string, List<AgentConsoleTab>> Read()
    {
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize(File.ReadAllText(file), AgentConsoleJson.Default.AgentConsoleStateFile) is { Workspaces: { } workspaces })
            {
                return new Dictionary<string, List<AgentConsoleTab>>(workspaces.Where(w => w.Value is not null), StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable: as if nothing was kept.
        }

        return new Dictionary<string, List<AgentConsoleTab>>(StringComparer.Ordinal);
    }

    private static string Key(string workspaceRoot) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
}

internal sealed record AgentConsoleStateFile(Dictionary<string, List<AgentConsoleTab>> Workspaces);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentConsoleStateFile))]
internal sealed partial class AgentConsoleJson : JsonSerializerContext;
