using System.Text;
using System.Text.Json;

namespace Codalus.Workbench.Ai.Mcp;

/// <summary>One agent's setup for <c>codalus mcp</c>: what to run or paste, and where.</summary>
/// <param name="Agent">The agent's name.</param>
/// <param name="Where">Where the text goes (a terminal, a file).</param>
/// <param name="Text">The command line or the JSON, with this install's path filled in.</param>
public sealed record McpSetup(string Agent, string Where, string Text);

/// <summary>
/// The setups of the known agents for reaching Codalus IDE from outside it through <c>codalus mcp</c> (M60, ADR 0099),
/// each with this install's path filled in, for Options › AI › MCP Server's Copy buttons and the user guide.
/// </summary>
public static class McpSetups
{
    /// <summary>The server's name in each agent's configuration.</summary>
    public const string ServerName = "codalus";

    /// <summary>
    /// The command line that starts <c>codalus mcp</c> for this install: the running executable by its absolute path
    /// (<c>dotnet</c> with the shell's assembly when it runs that way), and the user-data folder when the IDE has its own.
    /// </summary>
    public static (string Command, IReadOnlyList<string> Arguments) Command(string processPath, string? entryAssembly, string? userDataDir)
    {
        var args = new List<string>();
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase) && entryAssembly is { Length: > 0 })
        {
            args.Add(Path.GetFullPath(entryAssembly));
        }

        args.Add("mcp");
        if (userDataDir is { Length: > 0 })
        {
            args.Add("--user-data-dir");
            args.Add(Path.GetFullPath(userDataDir));
        }

        return (Path.GetFullPath(processPath), args);
    }

    /// <summary>Every known agent's setup, then the JSON most agents read (<c>.mcp.json</c>'s shape).</summary>
    public static IReadOnlyList<McpSetup> For(string processPath, string? entryAssembly, string? userDataDir)
    {
        var (command, arguments) = Command(processPath, entryAssembly, userDataDir);
        var line = string.Join(' ', new[] { command }.Concat(arguments).Select(Quote));
        return
        [
            new("Claude Code", "a terminal", $"claude mcp add {ServerName} -- {line}"),
            new("Codex", "a terminal", $"codex mcp add {ServerName} -- {line}"),
            new("Gemini CLI", "a terminal", $"gemini mcp add {ServerName} {line}"),
            new("GitHub Copilot CLI", "~/.copilot/mcp-config.json", Json(command, arguments, copilot: true)),
            new(".mcp.json", "the folder's .mcp.json, or the agent's MCP settings", Json(command, arguments, copilot: false)),
        ];
    }

    /// <summary><c>{"mcpServers":{"codalus":{"command":…,"args":[…]}}}</c>, indented (Copilot's file wants a type and its tools too).</summary>
    public static string Json(string command, IReadOnlyList<string> arguments, bool copilot = false)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteStartObject("mcpServers");
            w.WriteStartObject(ServerName);
            if (copilot)
            {
                w.WriteString("type", "local");
            }

            w.WriteString("command", command);
            w.WriteStartArray("args");
            foreach (var argument in arguments)
            {
                w.WriteStringValue(argument);
            }

            w.WriteEndArray();
            if (copilot)
            {
                w.WriteStartArray("tools");
                w.WriteStringValue("*");
                w.WriteEndArray();
            }

            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>A word of a command line, quoted when it has to be (a space or a quote).</summary>
    private static string Quote(string word) =>
        word.Length > 0 && word.All(c => !char.IsWhiteSpace(c) && c is not ('"' or '\'')) ? word : "\"" + word.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
