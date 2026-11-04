using System.Text.Json;
using Avalonia.Headless.XUnit;
using Codalus.Debugging;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Ai.Ide;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// The IDE's tools for agents in the shell (ADR 0092): code intelligence from the C# language service and Code Search's
/// index, confined to the workspace and its exclusions; and actions approved as <c>ai.agent.ideActions</c> says, with
/// "Allow for This Chat" lasting until New Chat, and a workspace that can't allow them for itself.
/// </summary>
public sealed class IdeToolsShellTests
{
    private static JsonElement Args(string json) => JsonElement.Parse(json);

    private static async Task<IdeToolResult> CallAsync(ShellFixture f, string tool, string json = "{}", string? caller = "Claude") =>
        await f.Shell.IdeToolAsync(tool, Args(json), caller, CancellationToken.None);

    [AvaloniaFact]
    public async Task Agents_find_and_trace_code_through_the_language_service_instead_of_grep()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("LanguageFeatures");
        File.WriteAllText(Path.Combine(folder, ".codalusignore"), "src/App/Json.cs\n");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "LanguageFeatures.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: "ready", Projects: 2 }, seconds: 180);

        var solution = (await CallAsync(f, "get_solution")).Text;
        Assert.Contains("- App (src/App/App.csproj)", solution, StringComparison.Ordinal);

        // A name, fuzzy, from the index and the language service: kind, full name, file and line.
        await ShellFixture.PumpUntil(() => f.Shell.CodeSearchIndex.IsLoaded, seconds: 120);
        var found = await CallAsync(f, "find_symbols", """{"query":"IShape","kind":"type"}""");
        Assert.False(found.IsError, found.Text);
        Assert.Contains("type (interface) Lib.IShape — src/Lib/Shapes.cs:4", found.Text, StringComparison.Ordinal);

        // The outline, the symbol's documentation, its implementations and its subtypes.
        var outline = (await CallAsync(f, "get_document_outline", """{"path":"src/Lib/Shapes.cs"}""")).Text;
        Assert.Contains("interface IShape (line 4)", outline, StringComparison.Ordinal);
        Assert.Contains("  method Area", outline, StringComparison.Ordinal);
        Assert.Contains("The area in square units.", (await CallAsync(f, "get_symbol_info", """{"symbol":"IShape.Area"}""")).Text, StringComparison.Ordinal);
        var implementations = (await CallAsync(f, "find_implementations", """{"symbol":"Lib.IShape"}""")).Text;
        Assert.Contains("src/Lib/Shapes.cs:11: public abstract class Shape : IShape", implementations, StringComparison.Ordinal);
        var subtypes = (await CallAsync(f, "type_hierarchy", """{"path":"src/Lib/Shapes.cs","line":11,"name":"Shape","direction":"subtypes"}""")).Text;
        Assert.Contains("Circle", subtypes, StringComparison.Ordinal);
        Assert.Contains("Square", subtypes, StringComparison.Ordinal);

        // References from the compiler, with their kind; callers through the call hierarchy.
        var references = (await CallAsync(f, "find_references", """{"symbol":"Geometry.Scale"}""")).Text;
        Assert.StartsWith("References to ", references, StringComparison.Ordinal);
        Assert.Contains("[declaration]", references, StringComparison.Ordinal);
        var callers = (await CallAsync(f, "call_hierarchy", """{"path":"src/Lib/Shapes.cs","line":7,"name":"Area"}""")).Text;
        Assert.StartsWith("Callers of ", callers, StringComparison.Ordinal);
        Assert.Contains("Scale", callers, StringComparison.Ordinal);

        // Text search like Find in Files, without the excluded file.
        var text = (await CallAsync(f, "search_code", """{"query":"namespace","glob":"*.cs"}""")).Text;
        Assert.Contains("src/Lib/Shapes.cs:1: namespace Lib;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Json.cs", text, StringComparison.Ordinal);

        // Confined: outside the workspace, excluded files and unknown symbols are errors the agent can read.
        Assert.True((await CallAsync(f, "get_document_outline", """{"path":"../../etc/passwd"}""")).IsError);
        var excluded = await CallAsync(f, "get_document_outline", """{"path":"src/App/Json.cs"}""");
        Assert.Equal((true, "'src/App/Json.cs' is excluded from AI."), (excluded.IsError, excluded.Text));
        Assert.Contains("No symbol named 'NoSuchThing'", (await CallAsync(f, "go_to_definition", """{"symbol":"NoSuchThing"}""")).Text, StringComparison.Ordinal);

        Assert.Contains("IDE tool (Claude): find_symbols", f.Shell.Workbench.Output.GetOrCreate(AiService.OutputChannelId).GetText(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Actions_ask_once_per_chat_and_a_workspace_cannot_allow_them()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("BuildAndRun");
        Directory.CreateDirectory(Path.Combine(folder, ".codalus"));
        File.WriteAllText(Path.Combine(folder, ".codalus", "settings.json"), """{ "ai.agent.ideActions": "allow" }""");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "BuildAndRun.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 2, seconds: 90);

        // The workspace's "allow" doesn't count: the first build asks, and Reject is the default.
        var building = CallAsync(f, "build");
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen, 30);
        Assert.Equal(["Allow", "Allow for This Chat", "Reject"], f.Shell.Dialogs.Buttons.Select(b => b.Label).Order(StringComparer.Ordinal));
        f.Shell.Dialogs.Accept();
        var refused = await building;
        Assert.True(refused.IsError);
        Assert.Contains("didn't allow", refused.Text, StringComparison.Ordinal);

        // Allow for This Chat: this build runs, and the next one doesn't ask.
        building = CallAsync(f, "build", """{"project":"Greetings"}""");
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen, 30);
        f.Shell.Dialogs.Buttons.Single(b => b.Label == "Allow for This Chat").Command.Execute(null);
        var built = await building;
        Assert.False(built.IsError, built.Text);
        Assert.StartsWith("The build succeeded: 0 error(s)", built.Text, StringComparison.Ordinal);
        built = await CallAsync(f, "build");
        Assert.False(f.Shell.Dialogs.IsOpen);
        Assert.False(built.IsError, built.Text);

        // Another caller asks for itself; New Chat forgets the grant.
        var other = CallAsync(f, "build", caller: null);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen, 30);
        f.Shell.Dialogs.Cancel();
        await other;
        f.Shell.AiChat.NewChat();
        building = CallAsync(f, "build");
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen, 30);
        f.Shell.Dialogs.Cancel();
        await building;

        // allow in the user's settings runs actions without asking.
        await f.Shell.Workbench.Settings.SetAsync(IdeTools.ActionsSetting, "allow", SettingScope.User, CancellationToken.None);
        built = await CallAsync(f, "build");
        Assert.False(built.IsError, built.Text);

        // Reading never asks; a debugger tool without a session says so.
        Assert.Equal("No debug session is running.", (await CallAsync(f, "debug_get_state")).Text);
        Assert.Equal("Breakpoint set at src/Hello/Program.cs:3.", (await CallAsync(f, "debug_set_breakpoint", """{"path":"src/Hello/Program.cs","line":3}""")).Text);
        Assert.Equal("src/Hello/Program.cs:3", (await CallAsync(f, "debug_list_breakpoints")).Text);
        Assert.True((await CallAsync(f, "debug_evaluate", """{"expression":"1+1"}""")).IsError);
        Assert.Contains("No web app is running", (await CallAsync(f, "browser_get_page")).Text, StringComparison.Ordinal);
    }
    [AvaloniaFact]
    public async Task Agents_run_tests_and_read_their_failures()
    {
        await using var f = ShellFixture.Create(builtInPlugins: ShellFixture.PluginBin("Codalus.Plugins.DotNetTest"));
        await f.Shell.Workbench.Settings.SetAsync(IdeTools.ActionsSetting, "allow", SettingScope.User, CancellationToken.None);
        var folder = f.CopySample("Testing");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Testing.slnx"));

        var run = await CallAsync(f, "run_tests", """{"project":"XunitV2Tests","filter":"CalculatorTests"}""");
        Assert.False(run.IsError, run.Text);
        Assert.Contains("this run:", run.Text, StringComparison.Ordinal);
        Assert.Contains("FAILED ", run.Text, StringComparison.Ordinal);
        Assert.Contains("Fails", run.Text, StringComparison.Ordinal);
        Assert.Contains("Assert.Equal() Failure", run.Text, StringComparison.Ordinal);
        Assert.Contains("CalculatorTests.cs", run.Text, StringComparison.Ordinal);

        var results = (await CallAsync(f, "get_test_results")).Text;
        Assert.Contains("FAILED ", results, StringComparison.Ordinal);
        Assert.DoesNotContain("passed Codalus", results, StringComparison.Ordinal); // failures only by default
        Assert.True((await CallAsync(f, "run_tests", """{"filter":"NoSuchTestAnywhere"}""")).IsError);
    }

    [AvaloniaFact]
    public async Task Agents_debug_with_breakpoints_variables_evaluation_and_steps()
    {
        Assert.SkipWhen(DebugAdapterLocator.Resolve() is null && Environment.GetEnvironmentVariable("CI") != "true", "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        await using var f = ShellFixture.Create();
        await f.Shell.Workbench.Settings.SetAsync(IdeTools.ActionsSetting, "allow", SettingScope.User, CancellationToken.None);
        var folder = f.CopySample("Debugging");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Debugging.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 90);
        var program = Path.Combine(folder, "Debuggee", "Program.cs");
        var loop = Array.FindIndex(File.ReadAllLines(program), l => l.Contains("// [loop]", StringComparison.Ordinal)) + 1;

        Assert.False((await CallAsync(f, "debug_set_breakpoint", $$"""{"path":"Debuggee/Program.cs","line":{{loop}},"condition":"i == 1"}""")).IsError);
        var started = await CallAsync(f, "debug_start");
        Assert.False(started.IsError, started.Text);
        var stop = (await CallAsync(f, "debug_wait_for_stop", """{"timeoutSeconds":180}""")).Text;
        Assert.Contains("State: stopped", stop, StringComparison.Ordinal);
        Assert.Contains($"Debuggee/Program.cs:{loop} → total += numbers[i];", stop, StringComparison.Ordinal);

        var variables = (await CallAsync(f, "debug_get_variables")).Text;
        Assert.Contains("i = 1", variables, StringComparison.Ordinal);
        Assert.Contains("total = 1", variables, StringComparison.Ordinal);
        Assert.StartsWith("10", (await CallAsync(f, "debug_evaluate", """{"expression":"total * 10"}""")).Text, StringComparison.Ordinal);
        Assert.Contains("numbers", (await CallAsync(f, "debug_get_variables", """{"expand":"numbers"}""")).Text, StringComparison.Ordinal);

        var stepped = (await CallAsync(f, "debug_step")).Text;
        Assert.Contains("State: stopped", stepped, StringComparison.Ordinal);

        Assert.Equal("Every breakpoint removed.", (await CallAsync(f, "debug_remove_breakpoint")).Text);
        Assert.StartsWith("Continued.", (await CallAsync(f, "debug_continue")).Text, StringComparison.Ordinal);
        Assert.StartsWith("The program ended (exit code", (await CallAsync(f, "debug_wait_for_stop", """{"timeoutSeconds":60}""")).Text, StringComparison.Ordinal);
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle, seconds: 60);
    }
    [AvaloniaFact]
    public async Task A_plugins_tool_is_listed_beside_the_ides_and_asks_before_every_call()
    {
        await using var f = ShellFixture.Create(builtInPlugins: Path.Combine(ShellFixture.RepoRoot, "samples", "plugins", "WordCount", "bin", ShellFixture.Configuration, "net10.0"));
        await f.Shell.Workbench.Settings.SetAsync(IdeTools.ActionsSetting, "allow", SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.IdeToolDefinitions.Any(t => t.Name == "count_words"), seconds: 30);
        var tool = f.Shell.IdeToolDefinitions.Single(t => t.Name == "count_words");
        Assert.Equal((IdeToolKind.Plugin, "codalus-samples.word-count"), (tool.Kind, tool.Source));
        Assert.Contains("count_words", IdeTools.ToJson(f.Shell.IdeToolDefinitions), StringComparison.Ordinal);

        // ideActions doesn't cover a plugin's tool: it asks every time, Reject the default, without Allow for This Chat.
        var refused = CallAsync(f, "count_words", """{"text":"to be or not to be"}""");
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen, 30);
        Assert.Equal(["Allow", "Reject"], f.Shell.Dialogs.Buttons.Select(b => b.Label).Order(StringComparer.Ordinal));
        Assert.Contains(f.Shell.Dialogs.Details, d => d.Text.Contains("codalus-samples.word-count", StringComparison.Ordinal));
        f.Shell.Dialogs.Accept();
        Assert.True((await refused).IsError);

        // Allowed, the plugin activates at its first call and answers; its own error is the call's.
        var counting = CallAsync(f, "count_words", """{"text":"to be or not to be"}""");
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen, 30);
        f.Shell.Dialogs.Buttons.Single(b => b.Label == "Allow").Command.Execute(null);
        var counted = await counting;
        Assert.Equal((false, "6 word(s); the most frequent is 'be' (2)."), (counted.IsError, counted.Text));
        var failing = CallAsync(f, "count_words", "{}");
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen, 30);
        f.Shell.Dialogs.Buttons.Single(b => b.Label == "Allow").Command.Execute(null);
        var failed = await failing;
        Assert.Equal((true, "count_words needs a text."), (failed.IsError, failed.Text));
    }
}
