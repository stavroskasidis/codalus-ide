using System.Text.Json;
using Codalus.Workbench.Settings;

namespace Codalus.Workbench.Ai.Acp;

/// <summary>
/// A known ACP agent that AI Chat's "Add agent…" offers (M48, ADR 0079): its <c>ai.agents</c> entry, pinned to the version
/// the ACP registry lists, and the CLI whose presence on the PATH marks it installed.
/// </summary>
/// <param name="Id">Its key in <c>ai.agents</c> (<c>claude</c>).</param>
/// <param name="Name">How the list names it.</param>
/// <param name="Description">What it is, in a line (the registry's).</param>
/// <param name="Version">The npm package's pinned version; for an agent that runs its installed CLI, the registry's version it was tested with (whatever is installed runs).</param>
/// <param name="Command">The command that starts it in ACP mode (<c>npx</c>, or the agent's CLI).</param>
/// <param name="Args">Its arguments: for npx, <c>-y</c> and the pinned package first.</param>
/// <param name="Package">The npm package with its exact version (<c>pi-acp@0.0.34</c>); null when the agent runs its installed CLI.</param>
/// <param name="Cli">The agent's own CLI (<c>claude</c>, <c>goose</c>): on the PATH, the list marks it installed.</param>
public sealed record AcpKnownAgent(string Id, string Name, string Description, string Version, string Command, IReadOnlyList<string> Args, string? Package, string Cli)
{
    /// <summary>The command line it runs, as the list and the dialog show it.</summary>
    public string CommandLine => string.Join(' ', new[] { Command }.Concat(Args.Select(a => a.Contains(' ', StringComparison.Ordinal) ? "\"" + a + "\"" : a)));

    /// <summary>npx downloads the package from npm when the agent first starts; otherwise its own CLI must be installed.</summary>
    public bool UsesNpx => Package is not null;
}

/// <summary>
/// The ACP agents AI Chat can set up in one step (M48), committed here: no list is fetched at runtime. Each entry is pinned
/// to the version in the ACP registry (<see cref="RegistryChecked"/>): an npx package with its exact version, or, for an
/// agent the registry distributes as a binary archive (goose, OpenCode), its installed CLI, whatever version is installed
/// (the entry says which it was tested with; Codalus never downloads one).
/// To update it, read the registry (<c>https://cdn.agentclientprotocol.com/registry/v1/latest/registry.json</c>, shown at
/// agentclientprotocol.com/get-started/registry), check each package and command, change the versions here and in
/// docs/user-guide/ai.md, and set <see cref="RegistryChecked"/>. <c>AcpAgentCatalogTests</c> refuses an unpinned entry.
/// </summary>
public static class AcpAgentCatalog
{
    /// <summary>When the entries were last checked against the ACP registry.</summary>
    public const string RegistryChecked = "2026-10-19";

    /// <summary>The known agents, in the order the list shows them.</summary>
    public static IReadOnlyList<AcpKnownAgent> Agents { get; } =
    [
        Npx("pi", "pi", "ACP adapter for the pi coding agent", "pi-acp", "0.0.34", [], "pi"),
        Npx("claude", "Claude", "ACP wrapper for Anthropic's Claude", "@agentclientprotocol/claude-agent-acp", "0.93.0", [], "claude"),
        Npx("codex", "Codex", "ACP adapter for OpenAI's coding assistant", "@agentclientprotocol/codex-acp", "2.2.2", [], "codex"),
        Npx("gemini", "Gemini CLI", "Google's official CLI for Gemini", "@google/gemini-cli", "0.64.0", ["--acp"], "gemini"),
        Npx("copilot", "GitHub Copilot", "GitHub's AI pair programmer", "@github/copilot", "1.0.98", ["--acp"], "copilot"),
        Installed("opencode", "OpenCode", "The open source coding agent", "1.19.3", "opencode", ["acp"]),
        Installed("goose", "goose", "A local, extensible, open source AI agent", "1.55.0", "goose", ["acp"]),
    ];

    private static AcpKnownAgent Npx(string id, string name, string description, string package, string version, string[] args, string cli) =>
        new(id, name, description, version, "npx", ["-y", package + "@" + version, .. args], package + "@" + version, cli);

    private static AcpKnownAgent Installed(string id, string name, string description, string version, string cli, string[] args) =>
        new(id, name, description, version, cli, args, null, cli);

    /// <summary>
    /// The agents whose own CLI is on <paramref name="path"/> (the PATH's form; null: this process's PATH). It only looks for
    /// the files (with PATHEXT on Windows), off the caller's thread, and gives up after <paramref name="timeout"/> (a slow
    /// network folder on the PATH), answering what it found by then: an agent not found is just not marked.
    /// </summary>
    public static async Task<IReadOnlySet<string>> FindInstalledAsync(IEnumerable<AcpKnownAgent> agents, string? path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var list = agents.ToList();
        var found = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        var search = Task.Run(() =>
        {
            var folders = (path ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var extensions = OperatingSystem.IsWindows()
                ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
                : [""];
            foreach (var folder in folders)
            {
                foreach (var agent in list.Where(a => !found.ContainsKey(a.Id)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (extensions.Any(extension => IsProgram(Path.Combine(folder, agent.Cli + extension))))
                    {
                        found[agent.Id] = true;
                    }
                }
            }
        }, cancellationToken);
        try
        {
            await search.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // What it found by then.
        }

        return found.Keys.ToHashSet(StringComparer.Ordinal);
    }

    private static bool IsProgram(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return false;
            }

            return OperatingSystem.IsWindows() || (File.GetUnixFileMode(file) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Whether the user's <c>ai.agents</c> has an entry named <paramref name="id"/>, usable or not.</summary>
    public static bool HasEntry(SettingsService settings, string id) =>
        settings.GetLayerValue(AcpAgents.AgentsSetting, SettingScope.User) is { ValueKind: JsonValueKind.Object } existing && existing.TryGetProperty(id, out _);

    /// <summary>
    /// Adds <paramref name="agent"/>'s entry to <c>ai.agents</c> in the user's settings (never the workspace's, ADR 0079),
    /// keeping the other entries. False, and nothing written, when an entry of that name is already there, unless
    /// <paramref name="replace"/> (the user chose to replace one Codalus can't use).
    /// </summary>
    public static async Task<bool> AddAsync(SettingsService settings, AcpKnownAgent agent, CancellationToken cancellationToken, bool replace = false)
    {
        var current = settings.GetLayerValue(AcpAgents.AgentsSetting, SettingScope.User);
        if (!replace && HasEntry(settings, agent.Id))
        {
            return false;
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (current is { ValueKind: JsonValueKind.Object } others)
            {
                foreach (var property in others.EnumerateObject().Where(p => p.Name != agent.Id))
                {
                    property.WriteTo(writer);
                }
            }

            writer.WritePropertyName(agent.Id);
            writer.WriteStartObject();
            writer.WriteString("command", agent.Command);
            writer.WriteStartArray("args");
            foreach (var arg in agent.Args)
            {
                writer.WriteStringValue(arg);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        await settings.SetAsync(AcpAgents.AgentsSetting, document.RootElement.Clone(), SettingScope.User, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
