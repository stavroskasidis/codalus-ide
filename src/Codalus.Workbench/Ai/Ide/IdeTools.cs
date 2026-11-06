using System.Text.Json;

namespace Codalus.Workbench.Ai.Ide;

/// <summary>
/// What an IDE tool does, which decides how it is approved (ADR 0092): reading the IDE's state never asks; an action
/// (building, running tests, running or debugging the program, driving its browser) follows
/// <c>ai.agent.ideActions</c> (allowed by default); a plugin's tool asks every time, as an MCP server's does.
/// </summary>
public enum IdeToolKind
{
    /// <summary>Reads code or the IDE's state: no approval.</summary>
    Read,

    /// <summary>Runs the workspace's code or changes what the IDE is doing: <c>ai.agent.ideActions</c>.</summary>
    Action,

    /// <summary>A plugin's tool: asks every time.</summary>
    Plugin,
}

/// <summary>An IDE tool as an agent sees it: its name, what it does, its JSON schema, and how it is approved.</summary>
/// <param name="Name">The tool's name (snake_case).</param>
/// <param name="Description">What it does, for the model.</param>
/// <param name="Schema">Its input as a JSON schema (an object).</param>
/// <param name="Kind">How it is approved.</param>
/// <param name="Source">Who serves it: <c>codalus</c>, or the plugin's id.</param>
public sealed record IdeToolDefinition(string Name, string Description, string Schema, IdeToolKind Kind, string Source = IdeTools.CodalusSource);

/// <summary>An image an IDE tool returns (a browser screenshot), as base64.</summary>
public sealed record IdeToolImage(string Data, string MimeType);

/// <summary>What an IDE tool returns: text for the model, images, and whether it failed.</summary>
public sealed record IdeToolResult(string Text, bool IsError = false)
{
    public IReadOnlyList<IdeToolImage> Images { get; init; } = [];

    public static IdeToolResult Error(string text) => new(text, IsError: true);
}

/// <summary>A tool call failed in a way the agent should read (no such symbol, no debug session).</summary>
public sealed class IdeToolException(string message) : Exception(message);

/// <summary>
/// The IDE's tools for agents (ADR 0092), one catalog for every way an agent reaches the IDE: <c>codalus mcp</c> (an
/// external agent's session pipe, and the general pipe while <c>ai.mcpServer.enabled</c> is on) and AI Chat's built-in
/// agent. Code intelligence comes from the language services (Roslyn for C#) and Code Search's index (ADR 0078), so an
/// agent can find a symbol, its references and its callers without reading files with grep; the actions build, test,
/// run and debug the program in the IDE, where the user sees them.
/// </summary>
public static class IdeTools
{
    public const string CodalusSource = "codalus";

    /// <summary>The setting that decides how actions are approved: allow (the default), askOncePerSession, ask.</summary>
    public const string ActionsSetting = "ai.agent.ideActions";

    /// <summary>
    /// What <c>codalus mcp</c> says in <c>initialize</c> (MCP's <c>instructions</c>), so an agent prefers the IDE's tools.
    /// Nothing is added to the agent's own prompt.
    /// </summary>
    public const string Instructions =
        "Codalus IDE is running with this workspace open. Its tools answer from the IDE's live state: the C# compiler's " +
        "semantic model (Roslyn) with unsaved edits, Code Search's index, the Error List, Test Explorer and the debugger. " +
        "Prefer them over shell commands: find_symbols and search_code instead of grep or find; go_to_definition, " +
        "find_references, find_implementations, call_hierarchy and type_hierarchy instead of reading files to trace code; " +
        "get_diagnostics after an edit; build, run_tests and get_test_results instead of dotnet build or dotnet test; " +
        "debug_* to reproduce a bug under the debugger (breakpoints, stack, variables, evaluate) instead of adding prints. " +
        "When a web app runs, browser_* drives and reads its page (under the debugger, the very page being debugged, so " +
        "its breakpoints stop). Check your own changes before you say they are done: get_diagnostics and build after an " +
        "edit, run_tests for the tests that cover it, and when the change shows in a running app (a page, a window, its " +
        "output), start it with debug_start or run_project and check it, with browser_* for a web app; skip this only " +
        "when the user says so. The tools wait for what they need: debug_start returns once the program runs and a web " +
        "app listens, the browser tools wait for a web app that is starting, and every result says the state it left the " +
        "program in; read it before the next step instead of retrying. Positions are 1-based lines; give a symbol by name " +
        "(Namespace.Type.Member or Type.Member), or a path, line and the name on that line.";

    private const string Position =
        """
        "symbol":{"type":"string","description":"The symbol's name: Type, Type.Member or Namespace.Type.Member. Use it, or path and line."},
        "path":{"type":"string","description":"A file, relative to the workspace root."},
        "line":{"type":"integer","description":"A 1-based line of that file."},
        "name":{"type":"string","description":"The identifier on that line; its first occurrence is used. Without it, the first identifier."}
        """;

    /// <summary>The built-in tools, in the order agents see them.</summary>
    public static readonly IReadOnlyList<IdeToolDefinition> BuiltIn =
    [
        // ---------------------------------------------------------- the solution and the editor
        Read("get_solution", "The solution open in Codalus IDE: its path and its projects, with their target frameworks, output types, whether they are test projects, and the startup project.", "{}"),
        Read("get_open_files", "The files open in Codalus IDE's editor, the active one, its caret line, and its selected text.", "{}"),
        Read("get_diagnostics", "The compiler's and analyzers' errors and warnings in Codalus IDE's Error List (live, as you edit), of one file or all.", """{"path":{"type":"string","description":"A file relative to the workspace root; empty for all."}}"""),
        Read("get_build_output", "The end of the last build's output in Codalus IDE.", "{}"),

        // ---------------------------------------------------------- code intelligence
        Read("find_symbols", "Find types, members and files by name in the whole solution, from Code Search's index and the language services (fuzzy: camel-case humps match, e.g. 'OrdSvc' finds OrderService). Faster and more precise than grep for declarations. Returns kind, full name, file and line.", """{"query":{"type":"string","description":"A name or part of one."},"kind":{"type":"string","enum":["any","type","member","file"],"description":"Only this kind; any by default."},"limit":{"type":"integer","description":"At most this many results (default 30, at most 200)."}}""", "query"),
        Read("search_code", "Search the text of the solution's files (open documents as edited, ignored files left out), like Find in Files. Returns path:line: text.", """{"query":{"type":"string","description":"The text, or a .NET regular expression when regex is true."},"regex":{"type":"boolean"},"matchCase":{"type":"boolean"},"glob":{"type":"string","description":"Only files whose name matches, e.g. *.cs."},"limit":{"type":"integer","description":"At most this many lines (default 100, at most 1000)."}}""", "query"),
        Read("get_document_outline", "The declarations of a file as a tree (namespaces, types, members) with their lines, from the language service.", """{"path":{"type":"string","description":"A file, relative to the workspace root."}}""", "path"),
        Read("get_symbol_info", "What the compiler knows about a symbol: its signature, kind and documentation (Quick Info).", "{" + Position + "}"),
        Read("go_to_definition", "Where a symbol is declared (source, or the referenced assembly's metadata as text).", "{" + Position + "}"),
        Read("find_references", "Every reference to a symbol in the solution, from the compiler (not text matches). Returns path:line: text.", "{" + Position + ""","limit":{"type":"integer","description":"At most this many (default 200)."}}"""),
        Read("find_implementations", "The implementations of an interface or abstract member, or the types deriving from a type.", "{" + Position + "}"),
        Read("call_hierarchy", "Who calls a method (incoming) or what it calls (outgoing), from the compiler.", "{" + Position + ""","direction":{"type":"string","enum":["incoming","outgoing"],"description":"incoming by default."}}"""),
        Read("type_hierarchy", "A type's base types and interfaces (supertypes), or the types deriving from it (subtypes).", "{" + Position + ""","direction":{"type":"string","enum":["supertypes","subtypes"],"description":"supertypes by default."}}"""),

        // ---------------------------------------------------------- build and tests
        Action("build", "Build the solution or one project in Codalus IDE, and return the result with its errors and warnings.", """{"project":{"type":"string","description":"A project's name; empty for the solution."}}"""),
        Action("run_tests", "Run tests in Codalus IDE's Test Explorer and wait for them: all, a project's, the ones matching a filter, or the ones that failed last. Returns the counts and every failure with its message and stack trace.", """{"project":{"type":"string","description":"A test project's name."},"filter":{"type":"string","description":"Tests whose full name contains this text (e.g. a class or a method)."},"failedOnly":{"type":"boolean","description":"Only the tests that failed in the last run."}}"""),
        Read("get_test_results", "The tests Codalus IDE knows and their last results: counts, and each failure with its message and stack trace.", """{"failedOnly":{"type":"boolean","description":"Only failures (the default)."}}"""),

        // ---------------------------------------------------------- running
        Action("run_project", "Start a project without debugging in Codalus IDE (the startup project by default), and wait until it runs (a web app: until it listens, and its address). Its output goes to the IDE; read it with get_run_output.", """{"project":{"type":"string","description":"A project's name; empty for the startup project."}}"""),
        Read("get_run_output", "The end of the output of the program started last (run or debug), and whether it is still running.", """{"lines":{"type":"integer","description":"At most this many lines from the end (default 200)."}}"""),
        Action("stop_run", "Stop the program started with run_project, or the debug session.", "{}"),

        // ---------------------------------------------------------- debugging
        Action("debug_start", "Start debugging a project (the startup project by default) or one test in Codalus IDE. It builds, then waits until the program runs (a web app: until it listens, with its address) or stops, and says so, with each breakpoint's state (bound or not, and why). Set breakpoints first. Then make the program reach them (a web app: browser_*) and debug_wait_for_stop.", """{"project":{"type":"string","description":"A project's name; empty for the startup project."},"test":{"type":"string","description":"A test's full name, to debug that test instead."}}"""),
        Read("debug_set_breakpoint", "Set a breakpoint in Codalus IDE, optionally with a condition (a C# expression) or a hit count.", """{"path":{"type":"string","description":"A file, relative to the workspace root."},"line":{"type":"integer","description":"A 1-based line."},"condition":{"type":"string"},"hitCount":{"type":"integer"}}""", "path", "line"),
        Read("debug_remove_breakpoint", "Remove a breakpoint, or every breakpoint when no path is given.", """{"path":{"type":"string"},"line":{"type":"integer"}}"""),
        Read("debug_list_breakpoints", "The breakpoints set in Codalus IDE.", "{}"),
        Read("debug_wait_for_stop", "Wait until the debugged program stops (a breakpoint, a step, an exception) or ends, or, while it is starting, until a web app listens. Returns why, where (with the source line), and the exception when there is one; on a timeout, the breakpoints that aren't bound. Nothing stops a web app until something requests its code: use browser_* first.", """{"timeoutSeconds":{"type":"integer","description":"How long to wait (default 30, at most 600)."}}"""),
        Read("debug_get_state", "The debug session's state: running or stopped, why, the threads, and the stopped thread's call stack with files and lines.", "{}"),
        Read("debug_get_variables", "The locals and arguments of a stack frame of the stopped program (the top one by default), with an expanded object's members.", """{"frame":{"type":"integer","description":"The frame's index in debug_get_state's stack, 0 for the top."},"expand":{"type":"string","description":"A variable's path to expand, e.g. order.Lines."}}"""),
        Action("debug_evaluate", "Evaluate a C# expression in a frame of the stopped program, as the Watch window does. It can call methods, which may change the program's state.", """{"expression":{"type":"string"},"frame":{"type":"integer","description":"The frame's index, 0 for the top."}}""", "expression"),
        Action("debug_step", "Step the stopped program over, into or out of the current line, then wait for it to stop.", """{"kind":{"type":"string","enum":["over","into","out"],"description":"over by default."}}"""),
        Action("debug_continue", "Continue the stopped program. Use debug_wait_for_stop to wait for the next stop.", "{}"),

        // ---------------------------------------------------------- the browser of a web app under the debugger
        Read("browser_get_page", "The page of the web app Codalus IDE runs or debugs (under the debugger, the page being debugged): its address, title and visible text, or the HTML of the elements a CSS selector matches. Waits for a web app that is starting.", """{"selector":{"type":"string","description":"A CSS selector; the visible text of the whole page without one."}}"""),
        Read("browser_screenshot", "A PNG screenshot of the browser page Codalus IDE opened for the web app.", "{}"),
        Read("browser_console", "The browser page's console messages and uncaught errors since it opened (the last ones).", """{"lines":{"type":"integer","description":"At most this many (default 100)."}}"""),
        Action("browser_evaluate", "Run JavaScript in the web app's page (to click, fill a form or read state) and return its result as JSON. When the script makes the debugged program stop at a breakpoint, it returns at once with where it stopped.", """{"script":{"type":"string","description":"An expression or the body of an async function; its value is returned."}}""", "script"),
        Action("browser_navigate", "Open an address in the web app's page: a path relative to the app's address (e.g. /counter) or a full localhost address. Waits for a web app that is starting, and says when the page couldn't load.", """{"url":{"type":"string"}}""", "url"),
    ];

    /// <summary>The built-in tool named <paramref name="name"/>, or null.</summary>
    public static IdeToolDefinition? Find(string name) => BuiltIn.FirstOrDefault(t => t.Name == name);

    /// <summary>
    /// How actions are approved: <c>allow</c> (also when unset), <c>askOncePerSession</c> or <c>ask</c>. A value it
    /// doesn't know asks every time: whoever wrote it wanted something other than the default.
    /// </summary>
    public static IdeActionApproval ParseApproval(string? value) => value switch
    {
        null or "" or "allow" => IdeActionApproval.Allow,
        "askOncePerSession" => IdeActionApproval.AskOncePerSession,
        _ => IdeActionApproval.Ask,
    };

    /// <summary>The definitions as MCP's <c>tools/list</c> wants them, as one JSON array.</summary>
    public static string ToJson(IEnumerable<IdeToolDefinition> tools)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartArray();
            foreach (var tool in tools)
            {
                w.WriteStartObject();
                w.WriteString("name", tool.Name);
                w.WriteString("description", tool.Description);
                w.WriteString("kind", tool.Kind.ToString());
                w.WriteString("source", tool.Source);
                w.WritePropertyName("inputSchema");
                using var schema = JsonDocument.Parse(tool.Schema);
                schema.RootElement.WriteTo(w);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Reads <see cref="ToJson"/>'s array back.</summary>
    public static IReadOnlyList<IdeToolDefinition> FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var tools = new List<IdeToolDefinition>();
        foreach (var t in document.RootElement.EnumerateArray())
        {
            var kind = Enum.TryParse<IdeToolKind>(t.TryGetProperty("kind", out var k) ? k.GetString() : null, out var parsed) ? parsed : IdeToolKind.Plugin;
            tools.Add(new IdeToolDefinition(
                t.GetProperty("name").GetString() ?? "",
                t.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                t.TryGetProperty("inputSchema", out var s) ? s.GetRawText() : """{"type":"object","properties":{}}""",
                kind,
                t.TryGetProperty("source", out var src) ? src.GetString() ?? CodalusSource : CodalusSource));
        }

        return tools;
    }

    private static IdeToolDefinition Read(string name, string description, string properties, params string[] required) =>
        new(name, description, Schema(properties, required), IdeToolKind.Read);

    private static IdeToolDefinition Action(string name, string description, string properties, params string[] required) =>
        new(name, description, Schema(properties, required), IdeToolKind.Action);

    private static string Schema(string properties, string[] required)
    {
        var body = properties.Trim();
        if (body.StartsWith('{'))
        {
            body = body[1..^1];
        }

        var schema = "{\"type\":\"object\",\"properties\":{" + body.Trim() + "}" +
            (required.Length > 0 ? ",\"required\":[" + string.Join(',', required.Select(r => "\"" + r + "\"")) + "]" : "") + "}";
        using var _ = JsonDocument.Parse(schema); // a malformed schema fails at startup, in every test
        return schema;
    }
}

/// <summary>How an IDE action is approved (<c>ai.agent.ideActions</c>).</summary>
public enum IdeActionApproval
{
    /// <summary>Every build, test run, run and debug action asks, with "Always Allow in This Workspace".</summary>
    Ask,

    /// <summary>
    /// The first action of a chat session asks, with "Allow for This Chat" and "Always Allow in This Workspace"; the rest
    /// of that session runs.
    /// </summary>
    AskOncePerSession,

    /// <summary>Actions run without asking (the default; still only in trusted workspaces).</summary>
    Allow,
}
