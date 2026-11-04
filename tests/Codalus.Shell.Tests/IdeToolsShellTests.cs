using System.Text.Json;
using Avalonia.Headless.XUnit;
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
}
