using System.Text;
using System.Text.Json;
using Codalus.Workbench.Ai.Acp;

namespace Codalus.Workbench.Ai.Cli;

/// <summary>How a console's CLI starts.</summary>
/// <param name="Program">The program, by its full path.</param>
/// <param name="Arguments">Its arguments.</param>
/// <param name="Environment">Variables set for it, and removed (null) from what it would inherit.</param>
/// <param name="Files">Files written before it starts and deleted when it ends (an MCP config): path and text.</param>
/// <param name="IdeTools">Whether the arguments give it the IDE's tools.</param>
public sealed record AgentConsoleLaunch(string Program, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string?> Environment, IReadOnlyList<(string Path, string Text)> Files, bool IdeTools)
{
    /// <summary>The command line as the log shows it: the program's name and arguments, never the environment.</summary>
    public string CommandLine => string.Join(' ', new[] { Path.GetFileName(Program) }.Concat(Arguments.Select(a => a.Any(char.IsWhiteSpace) ? "\"" + a + "\"" : a)));
}

/// <summary>
/// Builds the command line of an agent's CLI for AI Console (ADR 0094): its conversation (new with an id of Codalus's,
/// resumed by that id, or the CLI's last one), <c>codalus mcp</c> for this console's pipe in the form the CLI takes for one
/// run, and the entry's environment (<c>env</c>, <c>secretEnv</c>, <c>unsetEnv</c>, Codalus's Node.js). Only text and
/// paths: it starts nothing, so it is tested on every OS.
/// </summary>
public static class AgentConsoleLauncher
{
    /// <summary>
    /// The launch of <paramref name="cli"/> found at <paramref name="program"/>. <paramref name="mcp"/> is the console's
    /// <c>codalus mcp</c> (null: no tools); <paramref name="configFile"/> is where a CLI that reads its MCP servers from a
    /// file gets this run's. A CLI that can't take the server as given (goose and a path with a space) starts without it.
    /// </summary>
    public static AgentConsoleLaunch Build(AgentCli cli, string program, AcpAgentConfig config, AcpMcpServerStdio? mcp, AgentConsoleStart start, string? sessionId, string configFile, string? inheritedPath)
    {
        var arguments = new List<string>(cli.Args);
        switch (start)
        {
            case AgentConsoleStart.Fresh when cli.NewSessionFlag is { } flag && sessionId is not null:
                arguments.Add(flag);
                arguments.Add(sessionId);
                break;
            case AgentConsoleStart.ResumeOwn when cli.ResumeSessionFlag is { } flag && sessionId is not null:
                arguments.Add(flag);
                arguments.Add(sessionId);
                break;
            case AgentConsoleStart.ContinueLast:
                arguments.AddRange(cli.ContinueArgs);
                break;
        }

        var files = new List<(string, string)>();
        var tools = false;
        if (mcp is not null)
        {
            switch (cli.Mcp)
            {
                case AgentCliMcp.ClaudeConfigFile:
                    files.Add((configFile, McpJson(mcp, copilot: false)));
                    arguments.Add("--mcp-config");
                    arguments.Add(configFile);
                    tools = true;
                    break;
                case AgentCliMcp.CopilotConfigFile:
                    files.Add((configFile, McpJson(mcp, copilot: true)));
                    arguments.Add("--additional-mcp-config");
                    arguments.Add("@" + configFile);
                    tools = true;
                    break;
                case AgentCliMcp.CodexConfigOverride when TomlString(mcp.Command) is { } command && mcp.Args.Select(TomlString).ToList() is var args && args.All(a => a is not null):
                    arguments.Add("-c");
                    arguments.Add($"mcp_servers.{AgentClis.McpServerName}.command={command}");
                    arguments.Add("-c");
                    arguments.Add($"mcp_servers.{AgentClis.McpServerName}.args=[{string.Join(',', args)}]");
                    tools = true;
                    break;
                case AgentCliMcp.GooseExtension when new[] { mcp.Command }.Concat(mcp.Args).All(a => a.Length > 0 && !a.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '\\' or '=')):
                    // goose splits the extension's command line itself: only one it can't misread is given.
                    arguments.Add("--with-extension");
                    arguments.Add(string.Join(' ', new[] { mcp.Command }.Concat(mcp.Args)));
                    tools = true;
                    break;
            }
        }

        var environment = new Dictionary<string, string?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var (key, value) in config.Env)
        {
            environment[key] = value;
        }

        foreach (var (key, value) in config.SecretValues)
        {
            environment[key] = value;
        }

        foreach (var name in config.UnsetEnv)
        {
            environment[name] = null;
        }

        if (config.NodeFolder is { } node)
        {
            environment["PATH"] = node + Path.PathSeparator + inheritedPath;
        }

        return new AgentConsoleLaunch(program, arguments, environment, files, tools);
    }

    /// <summary>The same launch without the IDE's tools (a Windows script that <c>cmd.exe</c> can't be given the arguments of).</summary>
    public static AgentConsoleLaunch WithoutTools(AgentCli cli, string program, AcpAgentConfig config, AgentConsoleStart start, string? sessionId, string? inheritedPath) =>
        Build(cli, program, config, null, start, sessionId, "", inheritedPath);

    /// <summary>
    /// <paramref name="value"/> as a TOML string an argument can carry: a literal string (<c>'…'</c>, nothing escaped, so
    /// a Windows path stays as it is and no quote <c>cmd.exe</c> reads is in it); null when it has a quote or a control
    /// character, which a literal string can't hold.
    /// </summary>
    internal static string? TomlString(string value) =>
        value.Any(c => c == '\'' || char.IsControl(c)) ? null : "'" + value + "'";

    /// <summary>The MCP config file of one run: <c>{ "mcpServers": { "codalus": { "command": …, "args": […] } } }</c>.</summary>
    internal static string McpJson(AcpMcpServerStdio mcp, bool copilot)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("mcpServers");
            writer.WriteStartObject(AgentClis.McpServerName);
            if (copilot)
            {
                writer.WriteString("type", "local");
            }

            writer.WriteString("command", mcp.Command);
            writer.WriteStartArray("args");
            foreach (var argument in mcp.Args)
            {
                writer.WriteStringValue(argument);
            }

            writer.WriteEndArray();
            if (copilot)
            {
                writer.WriteStartArray("tools");
                writer.WriteStringValue("*");
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// The program of <paramref name="cli"/>: a rooted path as written, else found on <paramref name="path"/> (the PATH's
    /// form). Never one inside <paramref name="workspaceRoot"/>: a cloned repository can't put the program the console
    /// starts on the PATH (a relative PATH entry, <c>node_modules/.bin</c>). Null with why in <paramref name="problem"/>.
    /// </summary>
    public static string? FindProgram(AgentCli cli, string? path, string? workspaceRoot, out string? problem)
    {
        problem = null;
        var found = Path.IsPathRooted(cli.Program)
            ? (AcpAgentCatalog.IsProgram(cli.Program) ? cli.Program : null)
            : cli.Program.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 ? null : AcpAgentConnector.FindOnPath(cli.Program, path);
        if (found is null)
        {
            problem = Path.IsPathRooted(cli.Program) ? $"{cli.Program} isn't a program on this computer." : $"{cli.Program} isn't on your PATH.";
            return null;
        }

        found = Path.GetFullPath(found);
        if (workspaceRoot is not null && IsUnder(found, workspaceRoot))
        {
            problem = $"{found} is inside this folder. AI Console starts only a program installed outside the folder it works in.";
            return null;
        }

        return found;
    }

    private static bool IsUnder(string file, string folder)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return file.StartsWith(root, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }
}
