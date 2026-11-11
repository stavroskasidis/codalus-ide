using System.Text.Json;
using Codalus.Workbench.Ai.Acp;
using Codalus.Workbench.Ai.Cli;

namespace Codalus.Workbench.Tests;

/// <summary>
/// AI Console's side of the workbench (ADR 0094): which CLI an <c>ai.agents</c> entry has, the command line each CLI
/// starts with (its conversation, <c>codalus mcp</c> in the form it takes for one run, the entry's environment), the
/// program found on the PATH but never inside the workspace, references as each CLI's prompt reads them, and the consoles
/// kept per workspace. Only text and files: nothing starts.
/// </summary>
public sealed class AgentConsoleTests : IDisposable
{
    private static readonly string[] s_withoutFlags = ["pi", "opencode"];
    private static readonly AcpMcpServerStdio s_mcp = new("codalus", "/opt/codalus/codalus", ["mcp", "--agent-session", "abc123"], []);
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-console-" + Guid.NewGuid().ToString("N"));

    public AgentConsoleTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static AgentCli Cli(string id) => AgentClis.Known.Single(c => c.Id == id);

    private static AcpAgentConfig Entry(string json) => AcpAgents.Parse(JsonElement.Parse("{ \"agent\": " + json + " }"))["agent"];

    private static AgentConsoleLaunch Build(string id, AgentConsoleStart start = AgentConsoleStart.Fresh, string? session = "11111111-2222-3333-4444-555555555555", AcpMcpServerStdio? mcp = null, AcpAgentConfig? config = null) =>
        AgentConsoleLauncher.Build(Cli(id), "/usr/bin/" + id, config ?? new AcpAgentConfig("npx", [], new Dictionary<string, string>()), mcp, start, session, "/state/ai-console/run.json", "/usr/bin");

    [Fact]
    public void Every_template_has_a_cli_and_an_entry_names_its_own_with_console()
    {
        // Each agent Connect sets up has a console, by the CLI its catalog entry names.
        Assert.Equal(AcpAgentTemplates.All.Select(t => t.Id).Order(), AgentClis.Known.Select(c => c.Id).Order());
        Assert.All(AcpAgentTemplates.All, t => Assert.Equal(t.Agent.Cli, Cli(t.Id).Program));

        // An entry Connect wrote has its template's CLI; one written by hand has none unless it names it.
        Assert.Equal("claude", AgentClis.For("my-claude", Entry("""{ "command": "npx", "template": "claude@0.94.0" }"""))!.Program);
        Assert.Null(AgentClis.For("mine", Entry("""{ "command": "mine-acp" }""")));
        var named = AgentClis.For("mine", Entry("""{ "command": "mine-acp", "console": "mine" }"""))!;
        Assert.Equal(("mine", "mine", AgentCliMcp.None, false), (named.Id, named.Program, named.Mcp, named.CanResume));
        var withArgs = AgentClis.For("mine", Entry("""{ "command": "mine-acp", "console": { "command": "mine", "args": ["chat", "--tui"] } }"""))!;
        Assert.Equal(["chat", "--tui"], withArgs.Args);

        // console wins over the template's CLI; a console that isn't a name or a command is none.
        Assert.Equal("other", AgentClis.For("x", Entry("""{ "command": "npx", "template": "claude@0.94.0", "console": "other" }"""))!.Program);
        Assert.Null(Entry("""{ "command": "a", "console": 5 }""").Console);
        Assert.Null(Entry("""{ "command": "a", "console": { "args": ["x"] } }""").Console);
        Assert.Null(Entry("""{ "command": "a", "console": { "command": "b", "args": [1] } }""").Console);

        // console doesn't change the agent AI Chat runs.
        Assert.True(Entry("""{ "command": "a" }""").SameAs(Entry("""{ "command": "a", "console": "b" }""")));
    }

    [Fact]
    public void Claude_gets_its_own_conversation_id_and_the_tools_from_a_file()
    {
        var fresh = Build("claude", mcp: s_mcp);
        Assert.Equal(["--session-id", "11111111-2222-3333-4444-555555555555", "--mcp-config", "/state/ai-console/run.json"], fresh.Arguments);
        Assert.True(fresh.IdeTools);
        var (path, text) = Assert.Single(fresh.Files);
        Assert.Equal("/state/ai-console/run.json", path);
        var server = JsonElement.Parse(text).GetProperty("mcpServers").GetProperty("codalus");
        Assert.Equal("/opt/codalus/codalus", server.GetProperty("command").GetString());
        Assert.Equal(["mcp", "--agent-session", "abc123"], server.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("claude --session-id 11111111-2222-3333-4444-555555555555 --mcp-config /state/ai-console/run.json", fresh.CommandLine);

        Assert.Equal(["--resume", "11111111-2222-3333-4444-555555555555"], Build("claude", AgentConsoleStart.ResumeOwn).Arguments);
        Assert.Equal(["--continue"], Build("claude", AgentConsoleStart.ContinueLast, session: null).Arguments);

        // Without a pipe there is nothing to give it, and no file.
        var none = Build("claude");
        Assert.False(none.IdeTools);
        Assert.Empty(none.Files);
    }

    [Fact]
    public void Each_cli_takes_the_tools_and_its_last_conversation_its_own_way()
    {
        // Codex: config overrides read as TOML, in literal strings (a Windows path's backslashes stay, and no double quote
        // that cmd.exe reads is in them); its last conversation is a subcommand, so it comes first.
        var codex = Build("codex", AgentConsoleStart.ContinueLast, mcp: s_mcp);
        Assert.Equal(["resume", "--last", "-c", "mcp_servers.codalus.command='/opt/codalus/codalus'", "-c", "mcp_servers.codalus.args=['mcp','--agent-session','abc123']"], codex.Arguments);
        Assert.Empty(codex.Files);
        Assert.Equal(@"'C:\Program Files\Codalus\codalus.exe'", AgentConsoleLauncher.TomlString(@"C:\Program Files\Codalus\codalus.exe"));
        Assert.Null(AgentConsoleLauncher.TomlString("it's"));
        var quoted = AgentConsoleLauncher.Build(Cli("codex"), "/usr/bin/codex", new AcpAgentConfig("npx", [], new Dictionary<string, string>()), s_mcp with { Command = "/opt/o'brien/codalus" }, AgentConsoleStart.Fresh, null, "", null);
        Assert.False(quoted.IdeTools);
        Assert.Empty(quoted.Arguments);

        // Copilot: a file, named with @, whose server says it is local and which tools it offers.
        var copilot = Build("copilot", AgentConsoleStart.ContinueLast, mcp: s_mcp);
        Assert.Equal(["--continue", "--additional-mcp-config", "@/state/ai-console/run.json"], copilot.Arguments);
        var server = JsonElement.Parse(Assert.Single(copilot.Files).Text).GetProperty("mcpServers").GetProperty("codalus");
        Assert.Equal(("local", "*"), (server.GetProperty("type").GetString(), server.GetProperty("tools")[0].GetString()));

        // goose: its session command, with the extension's command line as one argument it splits itself; one it could
        // misread (a space in a path) isn't given.
        var goose = Build("goose", AgentConsoleStart.ContinueLast, mcp: s_mcp);
        Assert.Equal(["session", "--resume", "--with-extension", "/opt/codalus/codalus mcp --agent-session abc123"], goose.Arguments);
        var spaced = AgentConsoleLauncher.Build(Cli("goose"), "/usr/bin/goose", new AcpAgentConfig("goose", [], new Dictionary<string, string>()), s_mcp with { Command = "/opt/my codalus/codalus" }, AgentConsoleStart.Fresh, null, "", null);
        Assert.Equal((false, "session"), (spaced.IdeTools, Assert.Single(spaced.Arguments)));

        // Gemini CLI, pi and OpenCode take no MCP server for one run: they start without the tools.
        var gemini = Build("gemini", AgentConsoleStart.ContinueLast, mcp: s_mcp);
        Assert.Equal((false, "--resume latest"), (gemini.IdeTools, string.Join(' ', gemini.Arguments)));
        Assert.All(s_withoutFlags, id =>
        {
            var launch = Build(id, AgentConsoleStart.ContinueLast, mcp: s_mcp);
            Assert.Equal((false, 0), (launch.IdeTools, launch.Arguments.Count));
            Assert.False(Cli(id).CanResume);
        });

        // Only Claude gets an id of Codalus's: the others have no flag for one, so none is passed.
        Assert.All(AgentClis.Known.Where(c => c.Id != "claude"), c => Assert.DoesNotContain("11111111-2222-3333-4444-555555555555", Build(c.Id).Arguments));
    }

    [Fact]
    public void The_cli_gets_the_entrys_environment_and_its_keys_and_loses_what_the_entry_unsets()
    {
        var config = new AcpAgentConfig("npx", [], new Dictionary<string, string> { ["FOO"] = "bar" })
        {
            SecretEnv = ["ANTHROPIC_API_KEY"],
            SecretValues = new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "sk-secret" },
            UnsetEnv = ["ANTHROPIC_BASE_URL"],
            NodeFolder = "/codalus/node/bin",
        };
        var launch = Build("claude", config: config);
        Assert.Equal("bar", launch.Environment["FOO"]);
        Assert.Equal("sk-secret", launch.Environment["ANTHROPIC_API_KEY"]);
        Assert.Null(launch.Environment["ANTHROPIC_BASE_URL"]);
        Assert.Equal("/codalus/node/bin" + Path.PathSeparator + "/usr/bin", launch.Environment["PATH"]);

        // The key is in the environment only: never in the command line the log shows.
        Assert.DoesNotContain("sk-secret", launch.CommandLine, StringComparison.Ordinal);
        Assert.Empty(Build("claude").Environment);
    }

    [Fact]
    public void The_program_comes_from_the_path_and_never_from_the_workspace()
    {
        var bin = Path.Combine(_root, "bin");
        var workspace = Path.Combine(_root, "ws");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(Path.Combine(workspace, "node_modules", ".bin"));
        var installed = Program(bin, "claude");
        var planted = Program(Path.Combine(workspace, "node_modules", ".bin"), "claude");

        Assert.Equal(installed, AgentConsoleLauncher.FindProgram(Cli("claude"), bin, workspace, out var problem));
        Assert.Null(problem);

        // A cloned repository's own folder on the PATH: its program isn't the one the console starts.
        Assert.Null(AgentConsoleLauncher.FindProgram(Cli("claude"), Path.GetDirectoryName(planted) + Path.PathSeparator + bin, workspace, out problem));
        Assert.Contains("is inside this folder", problem, StringComparison.Ordinal);

        Assert.Null(AgentConsoleLauncher.FindProgram(Cli("codex"), bin, workspace, out problem));
        Assert.Equal("codex isn't on your PATH.", problem);

        // A console command: a rooted path as written; a relative path with a folder isn't looked for anywhere.
        Assert.Equal(installed, AgentConsoleLauncher.FindProgram(new AgentCli("mine", installed), "", workspace, out _));
        Assert.Null(AgentConsoleLauncher.FindProgram(new AgentCli("mine", Path.Combine("tools", "claude")), bin, workspace, out _));
        Assert.Null(AgentConsoleLauncher.FindProgram(new AgentCli("mine", Path.Combine(_root, "missing")), bin, workspace, out problem));
        Assert.Contains("isn't a program on this computer", problem, StringComparison.Ordinal);
    }

    private static string Program(string folder, string name)
    {
        var file = Path.Combine(folder, OperatingSystem.IsWindows() ? name + ".cmd" : name);
        File.WriteAllText(file, OperatingSystem.IsWindows() ? "@exit /b 0\r\n" : "#!/bin/sh\nexit 0\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return file;
    }

    [Fact]
    public void A_reference_names_the_file_as_each_prompt_reads_it()
    {
        var root = Path.Combine(_root, "ws");
        var file = Path.Combine(root, "src", "App", "Program.cs");
        Assert.Equal("@src/App/Program.cs#L10-24 ", AgentConsoleReference.Format(AgentCliReference.AtPathWithLines, file, root, 10, 24));
        Assert.Equal("@src/App/Program.cs#L7 ", AgentConsoleReference.Format(AgentCliReference.AtPathWithLines, file, root, 7, 7));
        Assert.Equal("@src/App/Program.cs ", AgentConsoleReference.Format(AgentCliReference.AtPathWithLines, file, root));
        Assert.Equal("@src/App/Program.cs (lines 10-24) ", AgentConsoleReference.Format(AgentCliReference.AtPath, file, root, 10, 24));
        Assert.Equal("@src/App/Program.cs (line 7) ", AgentConsoleReference.Format(AgentCliReference.AtPath, file, root, 7));
        Assert.Equal("@src/App/Program.cs ", AgentConsoleReference.Format(AgentCliReference.AtPath, file, root));
        Assert.Equal("src/App/Program.cs:10-24 ", AgentConsoleReference.Format(AgentCliReference.Plain, file, root, 10, 24));
        Assert.Equal("src/App/Program.cs ", AgentConsoleReference.Format(AgentCliReference.Plain, file, root));

        // A space in the path: quoted. A file outside the workspace: its whole path.
        Assert.Equal("@\"src/My App/Program.cs\"#L1 ", AgentConsoleReference.Format(AgentCliReference.AtPathWithLines, Path.Combine(root, "src", "My App", "Program.cs"), root, 1));
        var outside = Path.Combine(_root, "elsewhere", "notes.txt");
        Assert.Equal(outside.Replace('\\', '/') + " ", AgentConsoleReference.Format(AgentCliReference.Plain, outside, root));
    }

    [Fact]
    public void A_workspaces_consoles_are_kept_in_the_users_state_and_a_bad_id_is_dropped()
    {
        var file = Path.Combine(_root, "state", "ai-console.json");
        var state = new AgentConsoleState(file);
        var one = Path.Combine(_root, "one");
        var two = Path.Combine(_root, "two");
        Directory.CreateDirectory(one);
        Directory.CreateDirectory(two);
        Assert.Empty(state.Load(one));

        state.Save(one, [new("claude", "11111111-2222-3333-4444-555555555555"), new("codex", null)]);
        state.Save(two, [new("goose", null)]);
        Assert.Equal([new("claude", "11111111-2222-3333-4444-555555555555"), new("codex", null)], new AgentConsoleState(file).Load(one + Path.DirectorySeparatorChar));
        Assert.Equal([new AgentConsoleTab("goose", null)], state.Load(two));

        // No consoles: the workspace is forgotten, the other stays.
        state.Save(one, []);
        Assert.Empty(state.Load(one));
        Assert.Single(state.Load(two));

        // An id becomes an argument of the CLI: one that isn't a UUID is dropped, and the console starts fresh.
        File.WriteAllText(file, JsonSerializer.Serialize(new Dictionary<string, object> { ["workspaces"] = new Dictionary<string, object> { [one] = new object[] { new { agent = "claude", session = "--dangerously-skip-permissions" }, new { agent = "", session = (string?)null } } } }));
        Assert.Equal([new AgentConsoleTab("claude", null)], state.Load(one));

        // An unreadable file is as if nothing was kept.
        File.WriteAllText(file, "{ not json");
        Assert.Empty(state.Load(one));
        state.Save(one, [new("pi", null)]);
        Assert.Equal([new AgentConsoleTab("pi", null)], state.Load(one));
    }
}
