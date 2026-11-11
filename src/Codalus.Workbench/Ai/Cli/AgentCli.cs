using Codalus.Workbench.Ai.Acp;
using Codalus.Workbench.Setup;

namespace Codalus.Workbench.Ai.Cli;

/// <summary>How an agent's CLI takes an MCP server for one run, without anything written to its own config (ADR 0094).</summary>
public enum AgentCliMcp
{
    /// <summary>It takes none on its command line: the console says the IDE's tools aren't connected.</summary>
    None,

    /// <summary>Claude: <c>--mcp-config &lt;file&gt;</c>, a JSON file with <c>mcpServers</c>.</summary>
    ClaudeConfigFile,

    /// <summary>Codex: <c>-c mcp_servers.codalus.command=…</c> and <c>-c mcp_servers.codalus.args=…</c>, read as TOML.</summary>
    CodexConfigOverride,

    /// <summary>GitHub Copilot: <c>--additional-mcp-config @&lt;file&gt;</c>, a JSON file with <c>mcpServers</c>.</summary>
    CopilotConfigFile,

    /// <summary>goose: <c>--with-extension "&lt;command line&gt;"</c>.</summary>
    GooseExtension,
}

/// <summary>How a CLI's prompt names a file of the workspace.</summary>
public enum AgentCliReference
{
    /// <summary><c>src/App/Program.cs:10-24</c>: a path the agent reads as text.</summary>
    Plain,

    /// <summary><c>@src/App/Program.cs</c>, with the lines after it in words.</summary>
    AtPath,

    /// <summary><c>@src/App/Program.cs#L10-24</c> (Claude).</summary>
    AtPathWithLines,
}

/// <summary>How a console starts its conversation.</summary>
public enum AgentConsoleStart
{
    /// <summary>A new conversation.</summary>
    Fresh,

    /// <summary>This console's own conversation, by the id Codalus gave it when it started.</summary>
    ResumeOwn,

    /// <summary>The CLI's last conversation in this folder.</summary>
    ContinueLast,
}

/// <summary>
/// An agent's own CLI as AI Console runs it (ADR 0094): the program found on the PATH, how it takes the IDE's tools for
/// one run, how a conversation is resumed, and how its prompt names a file. Written from each CLI's documentation.
/// </summary>
/// <param name="Id">The agent's id (a template's, or the entry's name for a <c>console</c> command).</param>
/// <param name="Program">The program's name on the PATH, or a rooted path from a <c>console</c> command.</param>
public sealed record AgentCli(string Id, string Program)
{
    /// <summary>Arguments every start has (goose's <c>session</c>, a <c>console</c> command's own).</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    public AgentCliMcp Mcp { get; init; }

    /// <summary>The flag that gives a new conversation an id Codalus makes (a UUID), or null.</summary>
    public string? NewSessionFlag { get; init; }

    /// <summary>The flag that resumes the conversation of an id, or null.</summary>
    public string? ResumeSessionFlag { get; init; }

    /// <summary>The arguments that continue the last conversation in the folder; empty when it has none.</summary>
    public IReadOnlyList<string> ContinueArgs { get; init; } = [];

    public AgentCliReference Reference { get; init; }

    /// <summary>Where the CLI's own install instructions are, for a CLI Codalus has no pinned install for.</summary>
    public string? InstallPage { get; init; }

    /// <summary>Whether a restarted console can bring a conversation back.</summary>
    public bool CanResume => ResumeSessionFlag is not null || ContinueArgs.Count > 0;
}

/// <summary>The CLIs AI Console knows (ADR 0094), by the agent template's id, and the CLI of an <c>ai.agents</c> entry.</summary>
public static class AgentClis
{
    /// <summary>The agent the last console was started with (the user's settings), for the next one.</summary>
    public const string ConsoleAgentSetting = "ai.console.agent";

    /// <summary>The name of the MCP server the console gives a CLI.</summary>
    public const string McpServerName = "codalus";

    public static IReadOnlyList<AgentCli> Known { get; } =
    [
        new("claude", "claude")
        {
            Mcp = AgentCliMcp.ClaudeConfigFile,
            NewSessionFlag = "--session-id",
            ResumeSessionFlag = "--resume",
            ContinueArgs = ["--continue"],
            Reference = AgentCliReference.AtPathWithLines,
            InstallPage = "https://code.claude.com/docs/en/setup",
        },
        new("codex", "codex")
        {
            Mcp = AgentCliMcp.CodexConfigOverride,
            ContinueArgs = ["resume", "--last"],
            Reference = AgentCliReference.AtPath,
            InstallPage = "https://developers.openai.com/codex/cli",
        },
        new("gemini", "gemini")
        {
            ContinueArgs = ["--resume", "latest"],
            Reference = AgentCliReference.AtPath,
        },
        new("copilot", "copilot")
        {
            Mcp = AgentCliMcp.CopilotConfigFile,
            ContinueArgs = ["--continue"],
            Reference = AgentCliReference.AtPath,
        },
        // TODO(issue): the IDE's tools and resuming for pi and OpenCode, once their flags are confirmed from their
        // documentation (ADR 0094): they start a new conversation without the tools.
        new("pi", "pi"),
        new("opencode", "opencode"),
        new("goose", "goose")
        {
            Args = ["session"],
            Mcp = AgentCliMcp.GooseExtension,
            ContinueArgs = ["--resume"],
        },
    ];

    /// <summary>
    /// The CLI of the entry <paramref name="name"/>: its <c>console</c> command when it has one (run as written: no IDE
    /// tools, nothing resumed), else the CLI of the template it was connected from; null when it has neither.
    /// </summary>
    public static AgentCli? For(string name, AcpAgentConfig config) =>
        config.Console is { } console
            ? new AgentCli(name, console.Command) { Args = console.Args }
            : AcpAgentTemplates.FromTag(config.Template) is { } template ? Known.FirstOrDefault(c => c.Id == template.Id) : null;

    /// <summary>
    /// How <paramref name="cli"/> is installed when it isn't on the PATH: the template's own pinned install (pi, OpenCode,
    /// goose), npm pinned to the catalog's version where the agent's package is its CLI (Gemini CLI, GitHub Copilot), or
    /// the CLI's own page.
    /// </summary>
    public static IReadOnlyList<AcpInstallOption> Install(AgentCli cli, Platform.PlatformKind platform)
    {
        if (AcpAgentTemplates.Find(cli.Id) is not { } template)
        {
            return [];
        }

        if (template.CliInstall(platform) is { Count: > 0 } own)
        {
            return own;
        }

        // TODO(issue): pinned installs for Claude's and Codex's CLIs (other packages than their ACP adapters, ADR 0094).
        if (cli.InstallPage is { } page)
        {
            return [new AcpInstallOption("page", template.Name, $"{template.Name}'s install page.", InstallKind.OpenPage, page)];
        }

        return template.Package is { } package && template.Agent.Cli == cli.Program
            ? [new AcpInstallOption("npm", "npm", $"{cli.Program} from npm, pinned to {template.Version}.", InstallKind.Run, "npm install -g " + package) { Program = "npm", Arguments = ["install", "-g", package] }]
            : [];
    }
}

/// <summary>A file of the workspace as a CLI's prompt names it: a reference the agent reads itself, never the content.</summary>
public static class AgentConsoleReference
{
    /// <summary>
    /// The reference to <paramref name="path"/> (relative to the workspace, with forward slashes; absolute when it is
    /// outside) and its lines (1-based, inclusive; null for the whole file), ending with a space so typing goes on.
    /// </summary>
    public static string Format(AgentCliReference style, string path, string? workspaceRoot, int? firstLine = null, int? lastLine = null)
    {
        var shown = path;
        if (workspaceRoot is not null)
        {
            var relative = Path.GetRelativePath(workspaceRoot, path);
            if (!Path.IsPathRooted(relative) && !relative.StartsWith("..", StringComparison.Ordinal))
            {
                shown = relative;
            }
        }

        shown = shown.Replace('\\', '/');
        var quoted = shown.Any(char.IsWhiteSpace) ? "\"" + shown + "\"" : shown;
        var lines = firstLine is { } first ? (lastLine is { } last && last != first ? $"{first}-{last}" : $"{first}") : null;
        return style switch
        {
            AgentCliReference.AtPathWithLines => "@" + quoted + (lines is null ? "" : "#L" + lines) + " ",
            AgentCliReference.AtPath => "@" + quoted + (lines is null ? " " : lines.Contains('-', StringComparison.Ordinal) ? $" (lines {lines}) " : $" (line {lines}) "),
            _ => quoted + (lines is null ? "" : ":" + lines) + " ",
        };
    }
}
