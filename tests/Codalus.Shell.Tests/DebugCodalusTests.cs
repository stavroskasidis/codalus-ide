using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// M58 (ADR 0094) in the shell, under the Codalus debugger on samples/Debugging (<c>--features</c>): the commands for
/// what it does that netcoredbg doesn't. Set Next Statement, Step Into Specific, the value a method returned in Locals,
/// and Freeze and Thaw in the Threads window; a logpoint and a hit count that never stop the program on the way.
/// </summary>
public sealed class DebugCodalusTests
{
    private static int Line(string file, string marker) =>
        Array.FindIndex(File.ReadAllLines(file), l => l.Contains($"// [{marker}]", StringComparison.Ordinal)) + 1;

    /// <summary>
    /// What the solution has for its projects and what the hosts wrote, for an expression the language host still
    /// couldn't compile after two minutes: the Visual Basic one once, among the shell's debugger classes run together,
    /// and it passed alone and with its class (Linux, a local run, 2026-11-12; docs/ci-flakes.md).
    /// </summary>
    private static string StillLoading(ShellFixture f, Exception ex)
    {
        var projects = string.Join("; ", f.Shell.Solutions.Projects.Keys.Select(path => Path.GetFileName(path) + ": " + (f.Shell.Solutions.CompilerInputs.TryGetValue(path, out var i)
            ? $"{i.State}{(i.Error is { } error ? $" '{error[..Math.Min(error.Length, 300)]}'" : string.Empty)}, {i.Arguments.Count(a => a.StartsWith("/reference:", StringComparison.OrdinalIgnoreCase))} references, {i.TargetFramework}"
            : "no compiler inputs")));
        var channels = string.Join('\n', f.Shell.Workbench.Output.Channels.Select(c => c.GetText() is var text && text.Length > 3000
            ? $"Output '{c.Name}' (the last 3000 characters):\n{text[^3000..]}"
            : $"Output '{c.Name}':\n{text}"));
        return $"{ex.Message}\nThe solution's projects: {projects}. The restore {f.Shell.Solutions.RestoreState}.\n{channels}";
    }

    private static async Task<string> OpenAsync(ShellFixture f)
    {
        await f.Shell.Workbench.Settings.SetAsync(ShellHost.DebugAdapterSetting, DebugService.CodalusAdapter, SettingScope.User, CancellationToken.None);
        var folder = f.CopySample("Debugging");
        var properties = Path.Combine(folder, "Debuggee", "Properties");
        Directory.CreateDirectory(properties);
        await File.WriteAllTextAsync(Path.Combine(properties, "launchSettings.json"), """
            { "profiles": { "Features": { "commandName": "Project", "commandLineArgs": "--features" } } }
            """, TestContext.Current.CancellationToken);
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Debugging.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 90);
        return Path.Combine(folder, "Debuggee", "Features.cs");
    }

    private static Task StoppedAtAsync(ShellFixture f, string file, string marker, int seconds = 60) =>
        ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Stopped && f.Shell.ActiveEditor is { Controller: { } c } editor
            && string.Equals(editor.Model.Path, file, StringComparison.OrdinalIgnoreCase) && c.CurrentDebugLine() == Line(file, marker), seconds);

    [AvaloniaFact]
    public async Task Set_next_statement_step_into_specific_returned_values_and_frozen_threads_work_from_their_commands()
    {
        await using var f = ShellFixture.Create();
        var features = await OpenAsync(f);
        var breakpoints = f.Shell.Debugger.Breakpoints;

        // A hit count and a logpoint on the way: applied by the debugger, so the first stop is the breakpoint's.
        breakpoints.Set(new BreakpointSpec(features, Line(features, "features-loop"), HitCondition: "== 4"));
        breakpoints.Set(new BreakpointSpec(features, Line(features, "features-start"), LogMessage: "starting in $FUNCTION"));
        await f.RunAsync("debug.start");
        await StoppedAtAsync(f, features, "features-loop", seconds: 180);
        await ShellFixture.PumpUntil(() => f.Shell.Locals.Roots.Any(r => r is { Name: "i", Value: "3" }));
        Assert.Contains("starting in Debuggee.Features.Run", f.Shell.DebugLog.GetText(), StringComparison.Ordinal);
        Assert.Contains("Codalus debugger", f.Shell.DebugLog.GetText(), StringComparison.Ordinal);

        // The commands are offered: the stopped thread's debugger says it can.
        await ShellFixture.PumpUntil(() => Equals(f.Shell.Workbench.Context.GetValue("debugCanSetNextStatement"), true));
        Assert.Equal(true, f.Shell.Workbench.Context.GetValue("debugCanStepIntoSpecific"));
        Assert.Equal(true, f.Shell.Workbench.Context.GetValue("debugCanFreezeThreads"));

        // Set Next Statement, from the caret: on to the line with the nested calls, the rest of the loop not run.
        breakpoints.Clear();
        var editor = f.Shell.ActiveEditor!;
        editor.Controller!.Editor.TextArea.Caret.Line = Line(features, "features-nested");
        await f.RunAsync("debug.setNextStatement");
        await StoppedAtAsync(f, features, "features-nested");
        await ShellFixture.PumpUntil(() => f.Shell.Locals.Roots.Any(r => r is { Name: "count", Value: "3" }));

        // A line of another method: refused, and the status bar says why.
        Assert.False(await f.Shell.SetNextStatementAsync(features, Line(features, "features-measure-body")));
        Assert.Contains("a line of the method the thread is in", f.Shell.StatusBar.Message, StringComparison.Ordinal);
        Assert.Equal(Line(features, "features-nested"), f.Shell.ActiveEditor!.Controller!.CurrentDebugLine());

        // Step Into Specific: the line's three calls; into the outermost, the two inner ones run without a stop.
        var targets = await f.Shell.Debugger.StepInTargetsAsync();
        Assert.Equal(["Debuggee.Features.Twice", "Debuggee.Features.Join", "Debuggee.Features.Shout"], targets.Select(t => t.Label));
        var stepping = f.Shell.StepIntoSpecificAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Palette.IsOpen && f.Shell.Palette.Rows.Count == 3);
        f.Shell.Palette.Accept(f.Shell.Palette.Rows.Single(r => r.Label == "Debuggee.Features.Shout"));
        await stepping;
        await StoppedAtAsync(f, features, "features-shout");
        await ShellFixture.PumpUntil(() => f.Shell.Locals.Roots.Any(r => r is { Name: "value", Value: "\"a-bb\"" }));

        // Step Out: what Shout returned, first in Locals.
        await f.RunAsync("debug.stepOut");
        await StoppedAtAsync(f, features, "features-nested");
        await ShellFixture.PumpUntil(() => f.Shell.Locals.Roots.Count > 0 && f.Shell.Locals.Roots[0] is { Name: "Debuggee.Features.Shout returned", Value: "\"A-BB!\"" });

        // The Threads window: freeze the ticker, and its counter stands still over two steps; thaw it, and it runs.
        await f.Shell.RunToLineAsync(features, Line(features, "features-freeze"));
        await StoppedAtAsync(f, features, "features-freeze");
        f.Shell.Layout.ShowToolWindow(ThreadsViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<ThreadsView>().Any() && f.Shell.ThreadList.Rows.Any(r => r.Name == "Ticker"));
        var ticker = f.Shell.ThreadList.Rows.Single(r => r.Name == "Ticker");
        Assert.True(f.Shell.ThreadList.CanFreeze);
        await f.Shell.ThreadList.FreezeAsync(ticker, frozen: true);
        Assert.Null(f.Shell.ThreadList.FreezeProblem);
        Assert.True(ticker.IsFrozen);
        Assert.Equal("Frozen", ticker.FrozenText);
        Assert.Contains("frozen", ticker.AccessibleName, StringComparison.Ordinal);
        var before = (await f.Shell.Debugger.EvaluateAsync("s_ticks", "watch", TestContext.Current.CancellationToken)).Result;
        await f.RunAsync("debug.stepOver");
        await StoppedAtAsync(f, features, "features-frozen");
        await f.RunAsync("debug.stepOver");
        await StoppedAtAsync(f, features, "features-thawed");
        Assert.Equal(before, (await f.Shell.Debugger.EvaluateAsync("s_ticks", "watch", TestContext.Current.CancellationToken)).Result);

        // Still frozen at the next stop, as the window shows again; then thawed.
        await ShellFixture.PumpUntil(() => f.Shell.ThreadList.Rows.Any(r => r is { Name: "Ticker", IsFrozen: true }));
        ticker = f.Shell.ThreadList.Rows.Single(r => r.Name == "Ticker");
        await f.Shell.ThreadList.FreezeAsync(ticker, frozen: false);
        Assert.False(ticker.IsFrozen);
        await f.RunAsync("debug.stepOver");
        await StoppedAtAsync(f, features, "features-stop");
        Assert.NotEqual(before, (await f.Shell.Debugger.EvaluateAsync("s_ticks", "watch", TestContext.Current.CancellationToken)).Result);

        // To the end: the program's own exit code.
        await f.RunAsync("debug.continue");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle, seconds: 60);
        Assert.Contains("features done 3", f.Shell.DebugLog.GetText(), StringComparison.Ordinal);
        Assert.Contains("exited with code 3", f.Shell.DebugLog.GetText(), StringComparison.Ordinal);
    }

    private static async Task<IReadOnlyList<VariableNode>> ExpandAsync(VariableNode node)
    {
        node.IsExpanded = true;
        await node.LoadChildrenOnceAsync();
        await ShellFixture.PumpUntil(() => node.Children.Count > 0 && node.Children.All(c => !c.IsPlaceholder));
        return node.Children;
    }

    [AvaloniaFact]
    public async Task Type_proxies_results_views_and_object_ids_show_in_the_variable_windows()
    {
        await using var f = ShellFixture.Create();
        var features = await OpenAsync(f);
        f.Shell.Debugger.Breakpoints.Set(new BreakpointSpec(features, Line(features, "features-views")));
        f.Shell.Debugger.Breakpoints.Set(new BreakpointSpec(features, Line(features, "features-ids")));
        await f.RunAsync("debug.start");
        await StoppedAtAsync(f, features, "features-views", seconds: 180);
        await ShellFixture.PumpUntil(() => f.Shell.Locals.Roots.Any(r => r.Name == "basket"));
        VariableNode Local(string name) => f.Shell.Locals.Roots.Single(r => r.Name == name);

        // A type's own proxy: its view, then Raw View with the object's own members, grouped as any object's.
        var basket = await ExpandAsync(Local("basket"));
        Assert.Equal(["Summary", "#0", "#1", "Raw View"], basket.Select(n => n.Name));
        Assert.Equal("apple (5)", basket[1].Value);
        Assert.Equal(MemberKind.AdapterView, basket[3].Kind);
        var raw = await ExpandAsync(basket[3]);
        Assert.Equal(["Items", "Name", "_secret"], raw.Select(n => n.Name).Order(StringComparer.Ordinal));

        // A list stays the shell's own view (its elements, then its Raw View), as under netcoredbg.
        var items = await ExpandAsync(raw.Single(n => n.Name == "Items"));
        Assert.Equal(["[0]", "[1]", "Raw View"], items.Select(n => n.Name));
        Assert.Equal(MemberKind.RawView, items[2].Kind);

        // The framework's proxy for a type the shell has no view for; an iterator's Results View.
        Assert.Equal(["[0]", "[1]", "[2]", "Raw View"], (await ExpandAsync(Local("linked"))).Select(n => n.Name));
        var evens = await ExpandAsync(Local("evens"));
        Assert.Equal("Results View", evens[^1].Name);
        Assert.Equal(MemberKind.AdapterView, evens[^1].Kind);
        Assert.Equal(["0", "2", "4", "6"], (await ExpandAsync(evens[^1])).Select(n => n.Value));

        // Expressions the debugger reads now: new, and null where an object is expected.
        Assert.Equal("2", (await f.Shell.Debugger.EvaluateAsync("new Crate(\"x\", 2).Size", "watch", TestContext.Current.CancellationToken)).Result);
        Assert.Equal("\"nothing!\"", (await f.Shell.Debugger.EvaluateAsync("Features.Label(null, null)", "watch", TestContext.Current.CancellationToken)).Result);

        // Make Object ID: offered, named, watched; the object is still reached when its variable holds another.
        Assert.True(f.Shell.Debugger.CanMakeObjectIds);
        Assert.Equal("$1", await f.Shell.MakeObjectIdAsync("basket"));
        Assert.Contains("$1", f.Shell.Watch.Expressions);
        Assert.Null(await f.Shell.MakeObjectIdAsync("basket.Items.Count"));
        Assert.Contains("only objects can have an ID", f.Shell.StatusBar.Message, StringComparison.Ordinal);
        await f.RunAsync("debug.continue");
        await StoppedAtAsync(f, features, "features-ids");
        await ShellFixture.PumpUntil(() => f.Shell.Watch.Roots.Any(r => r is { Name: "$1", Value: "{Debuggee.Basket}" }));
        Assert.Equal("\"fruit\"", (await f.Shell.Debugger.EvaluateAsync("$1.Name", "watch", TestContext.Current.CancellationToken)).Result);
        Assert.Equal("\"empty\"", (await f.Shell.Debugger.EvaluateAsync("basket.Name", "watch", TestContext.Current.CancellationToken)).Result);
        await f.Shell.DeleteObjectIdAsync("$1");
        Assert.DoesNotContain("$1", f.Shell.Watch.Expressions);

        await f.RunAsync("debug.stop");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle, seconds: 60);
    }

    [AvaloniaFact]
    public async Task Lambdas_queries_and_extension_methods_are_compiled_and_run_in_the_program()
    {
        await using var f = ShellFixture.Create();
        var features = await OpenAsync(f);
        var ct = TestContext.Current.CancellationToken;
        f.Shell.Debugger.Breakpoints.Set(new BreakpointSpec(features, Line(features, "features-views")));

        // A condition the debugger's own evaluator doesn't read (an array creation, a lambda): compiled, and it breaks.
        f.Shell.Debugger.Breakpoints.Set(new BreakpointSpec(features, Line(features, "features-ids"), Condition: "new[] { 1, 2 }.Sum(n => n) == 3 && basket.Items.Count == 0"));
        await f.RunAsync("debug.start");
        await StoppedAtAsync(f, features, "features-views", seconds: 180);
        async Task<string> EvalAsync(string expression) => (await f.Shell.Debugger.EvaluateAsync(expression, "watch", ct)).Result;

        // The language host has the project once the solution has loaded there: the first one waits for it.
        string? first = null;
        for (var attempt = 0; first is null; attempt++)
        {
            try
            {
                first = await EvalAsync("evens.Count()");
            }
            catch (Codalus.Debugging.DapException ex) when (ex.Message.Contains("open the program's solution", StringComparison.Ordinal) || ex.Message.Contains("isn't in a loaded project", StringComparison.Ordinal))
            {
                Assert.True(attempt < 240, StillLoading(f, ex));
                await Task.Delay(500, ct);
            }
        }

        Assert.Equal("4", first);

        // An extension method, lambdas over locals and their members, a query, a generic method, typeof and a pattern.
        Assert.Equal("1", await EvalAsync("basket.Items.Where(i => i.StartsWith(\"a\")).ToList().Count"));
        Assert.Equal("\"a1\"", await EvalAsync("sorted.Select(p => p.Key + p.Value).First()"));
        Assert.Equal("21", await EvalAsync("linked.Sum() + count"));
        Assert.Equal("\"Basket\"", await EvalAsync("typeof(Basket).Name"));
        Assert.Equal("true", await EvalAsync("basket is { Name: \"fruit\" }"));
        Assert.Equal("2", await EvalAsync("(from n in linked where n > 1 select n).Count()"));
        Assert.Equal("\"<x>\"", await EvalAsync("Mark(\"x\")")); // a static method of the frame's type, by its simple name

        // The result is a value of the program like any other: it expands.
        var list = await f.Shell.Debugger.EvaluateAsync("linked.OrderBy(n => n).ToArray()", "watch", ct);
        Assert.Equal("{int[3]}", list.Result);
        Assert.Equal(["1", "2", "3"], (await f.Shell.Debugger.VariablesAsync(list.VariablesReference)).Select(v => v.Value));

        // What doesn't compile says so in the compiler's words; what throws says what it threw.
        Assert.Contains("CS1061", (await Assert.ThrowsAsync<Codalus.Debugging.DapException>(() => EvalAsync("evens.Nope()"))).Message, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException", (await Assert.ThrowsAsync<Codalus.Debugging.DapException>(() => EvalAsync("evens.First(n => n > 100)"))).Message, StringComparison.Ordinal);

        // The compiled expressions' assemblies aren't the program's modules.
        Assert.DoesNotContain(f.Shell.Debugger.Modules, m => m.Name.Contains("CodalusExpression", StringComparison.Ordinal) || m.Name.Contains("dynamic", StringComparison.Ordinal));

        // And the compiled condition breaks.
        await f.RunAsync("debug.continue");
        await StoppedAtAsync(f, features, "features-ids");
        await f.RunAsync("debug.stop");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle, seconds: 60);
    }

    [AvaloniaFact]
    public async Task Visual_basic_expressions_are_read_as_visual_basic()
    {
        await using var f = ShellFixture.Create();
        var ct = TestContext.Current.CancellationToken;
        await f.Shell.Workbench.Settings.SetAsync(ShellHost.DebugAdapterSetting, DebugService.CodalusAdapter, SettingScope.User, CancellationToken.None);
        var folder = f.CopySample("VisualBasic");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "VisualBasic.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 4 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 180);
        f.Shell.Runs.SetStartupProject(Path.Combine(folder, "VbConsole", "VbConsole.vbproj"));
        var program = Path.Combine(folder, "VbConsole", "Program.vb");
        var loop = Array.FindIndex(File.ReadAllLines(program), l => l.Contains("' [vb-loop]", StringComparison.Ordinal)) + 1;
        f.Shell.Debugger.Breakpoints.Set(new BreakpointSpec(program, loop));
        await f.RunAsync("debug.start");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Stopped && f.Shell.ActiveEditor?.Controller?.CurrentDebugLine() == loop, seconds: 240);
        async Task<string> EvalAsync(string expression) => (await f.Shell.Debugger.EvaluateAsync(expression, "watch", ct)).Result;

        // A name and its members mean the same in both languages: read at once.
        Assert.Equal("\"Tea\"", await EvalAsync("product.Name"));

        // The rest is Visual Basic's: = compares, <>, Not, AndAlso, &, a lambda, a decimal literal.
        string? first = null;
        for (var attempt = 0; first is null; attempt++)
        {
            try
            {
                first = await EvalAsync("product.Price = 3D");
            }
            catch (Codalus.Debugging.DapException ex) when (ex.Message.Contains("open the program's solution", StringComparison.Ordinal) || ex.Message.Contains("isn't in a loaded project", StringComparison.Ordinal))
            {
                Assert.True(attempt < 240, StillLoading(f, ex));
                await Task.Delay(500, ct);
            }
        }

        Assert.Equal("true", first);
        Assert.Equal("true", await EvalAsync("Not String.IsNullOrEmpty(product.Name) AndAlso product.Name <> \"Bread\""));
        Assert.Equal("\"Tea!\"", await EvalAsync("product.Name & \"!\""));
        Assert.Equal("1", await EvalAsync("inventory.Products.Where(Function(p) p.Price > 2.6D).Count()"));
        Assert.Equal("\"EUR\"", await EvalAsync("If(formatter Is Nothing, \"none\", \"EUR\")"));
        Assert.Contains("BC30451", (await Assert.ThrowsAsync<Codalus.Debugging.DapException>(() => EvalAsync("nothere + 1"))).Message, StringComparison.Ordinal);

        await f.RunAsync("debug.stop");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle, seconds: 60);
    }

    [AvaloniaFact]
    public async Task The_disassembly_registers_and_memory_windows_show_the_machine_and_step_an_instruction()
    {
        await using var f = ShellFixture.Create();
        var features = await OpenAsync(f);
        var ct = TestContext.Current.CancellationToken;
        f.Shell.Debugger.Breakpoints.Set(new BreakpointSpec(features, Line(features, "features-measure")));
        await f.RunAsync("debug.start");
        await StoppedAtAsync(f, features, "features-measure", seconds: 180);
        Assert.True(f.Shell.Debugger.CanReadMemory);

        // Memory: an expression's object, where its characters are; bytes written there change the string.
        f.Shell.Layout.ShowToolWindow(MemoryViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<MemoryView>().Any());
        var memory = f.Shell.MemoryWindow;
        memory.AddressText = "text";
        await memory.GoAsync();
        Assert.Null(memory.Message);
        Assert.Equal(MemoryViewModel.RowsPerPage, memory.Rows.Count);
        Assert.Contains("A.-.B.B.!.", string.Concat(memory.Rows.Take(3).Select(r => r.Text)), StringComparison.Ordinal);
        var characters = string.Concat(memory.Rows.Take(3).Select(r => r.Text)).IndexOf("A.-.B.B.!.", StringComparison.Ordinal);
        var start = Convert.ToUInt64(memory.Rows[0].Address[2..], 16) + (ulong)characters;
        memory.AddressText = "0x" + start.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        await memory.GoAsync();
        Assert.StartsWith("41 00 2d 00", memory.Rows[0].Hex, StringComparison.Ordinal);
        memory.WriteText = "5a 00";
        await memory.WriteAsync();
        Assert.Null(memory.Message);
        Assert.StartsWith("5a 00 2d 00", memory.Rows[0].Hex, StringComparison.Ordinal);
        Assert.Equal("\"Z-BB!\"", (await f.Shell.Debugger.EvaluateAsync("text", "watch", ct)).Result);

        // What isn't an address says so; so do bytes that aren't hex.
        memory.WriteText = "zz";
        await memory.WriteAsync();
        Assert.Contains("hex digits", memory.Message, StringComparison.Ordinal);
        memory.AddressText = "nothere";
        await memory.GoAsync();
        Assert.Contains("isn't an address", memory.Message, StringComparison.Ordinal);
        Assert.Empty(memory.Rows);

        if (!f.Shell.Debugger.CanDisassemble)
        {
            return; // an architecture whose machine code isn't decoded
        }

        // Disassembly: the method's code under its source lines, the frame's instruction marked and scrolled to.
        f.Shell.Layout.ShowToolWindow(DisassemblyViewModel.ToolWindowId);
        var disassembly = f.Shell.DisassemblyWindow;
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<DisassemblyView>().Any() && disassembly.CurrentIndex >= 0);
        Assert.Equal("Debuggee.Features.Run", disassembly.Rows[0].Heading);
        Assert.Contains(disassembly.Rows, r => r.Heading == $"Features.cs:{Line(features, "features-measure")}");
        var current = Assert.Single(disassembly.Rows, r => r.IsCurrent);
        Assert.Same(current, disassembly.Rows[disassembly.CurrentIndex]);
        Assert.NotEmpty(current.Text);
        Assert.Matches("^[0-9a-f]{2}( [0-9a-f]{2})*$", current.Bytes);
        Assert.True(disassembly.CanStep);

        // Registers: the instruction pointer is the current instruction's address.
        f.Shell.Layout.ShowToolWindow(RegistersViewModel.ToolWindowId);
        var registers = f.Shell.RegistersWindow;
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<RegistersView>().Any() && registers.Rows.Count > 0);
        var ip = registers.Rows.Single(r => r.Name is "rip" or "pc");
        Assert.Equal(Convert.ToUInt64(current.Address[2..], 16), Convert.ToUInt64(ip.Value[2..], 16));
        Assert.False(ip.IsChanged);

        // One instruction: the marker moves to the next one, on the same source line, and the instruction pointer changed.
        var next = disassembly.Rows.Skip(disassembly.CurrentIndex + 1).First(r => r.IsInstruction);
        f.Shell.Layout.ShowToolWindow(DisassemblyViewModel.ToolWindowId);
        await disassembly.StepOverAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Stopped && disassembly.CurrentIndex >= 0 && disassembly.Rows[disassembly.CurrentIndex].Address == next.Address, seconds: 60);
        Assert.Equal(Line(features, "features-measure"), f.Shell.ActiveEditor!.Controller!.CurrentDebugLine());
        f.Shell.Layout.ShowToolWindow(RegistersViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => registers.Rows.Any(r => r.Name is "rip" or "pc" && r.IsChanged));

        await f.RunAsync("debug.stop");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle, seconds: 60);
        Assert.Empty(disassembly.Rows);
        Assert.Empty(registers.Rows);
    }

    [AvaloniaFact]
    public async Task The_symbol_settings_list_no_server_until_one_is_turned_on()
    {
        await using var f = ShellFixture.Create();
        var settings = f.Shell.Workbench.Settings;

        // Out of the box: nowhere to look, so nothing about a program's modules leaves the machine.
        Assert.Null(f.Shell.SymbolOptions());
        Assert.False(settings.GetBoolean(ShellHost.SymbolsMicrosoftServerSetting, true));
        Assert.False(settings.GetBoolean(ShellHost.SymbolsNuGetServerSetting, true));

        // A symbol path alone asks no server; the cache is Codalus IDE's own unless one is named.
        await settings.SetAsync(ShellHost.SymbolsPathsSetting, System.Text.Json.JsonElement.Parse("[\" /symbols/here \"]"), SettingScope.User, CancellationToken.None);
        var paths = f.Shell.SymbolOptions()!;
        Assert.Equal(["/symbols/here"], paths.SearchPaths);
        Assert.Empty(paths.Servers);
        Assert.EndsWith("symbols", paths.CacheFolder, StringComparison.Ordinal);

        // The servers that are checked, in order, then the user's own; the module lists as they're set.
        await settings.SetAsync(ShellHost.SymbolsNuGetServerSetting, true, SettingScope.User, CancellationToken.None);
        await settings.SetAsync(ShellHost.SymbolsMicrosoftServerSetting, true, SettingScope.User, CancellationToken.None);
        await settings.SetAsync(ShellHost.SymbolsServersSetting, System.Text.Json.JsonElement.Parse("[\"https://symbols.example.test/store\"]"), SettingScope.User, CancellationToken.None);
        await settings.SetAsync(ShellHost.SymbolsCacheSetting, "/cache/there", SettingScope.User, CancellationToken.None);
        await settings.SetAsync(ShellHost.SymbolsOnlySpecifiedSetting, true, SettingScope.User, CancellationToken.None);
        await settings.SetAsync(ShellHost.SymbolsIncludeSetting, System.Text.Json.JsonElement.Parse("[\"Contoso.*.dll\"]"), SettingScope.User, CancellationToken.None);
        var all = f.Shell.SymbolOptions()!;
        Assert.Equal([Codalus.Debugging.Symbols.SymbolOptions.MicrosoftServer, Codalus.Debugging.Symbols.SymbolOptions.NuGetServer, "https://symbols.example.test/store"], all.Servers);
        Assert.Equal("/cache/there", all.CacheFolder);
        Assert.Equal(Codalus.Debugging.Symbols.SymbolModuleFilter.OnlySpecified, all.ModuleFilter);
        Assert.True(all.Includes("Contoso.Web.dll"));
        Assert.False(all.Includes("System.Console.dll"));
    }

    [AvaloniaFact]
    public async Task Under_netcoredbg_the_commands_are_off_and_breakpoints_behave_as_before()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("CODALUS_DEBUG_ADAPTER") == DebugService.CodalusAdapter, "The debug-codalus shard runs every session under the Codalus debugger.");
        Assert.SkipWhen(Codalus.Debugging.DebugAdapterLocator.Resolve() is null && Environment.GetEnvironmentVariable("CI") != "true", "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        await using var f = ShellFixture.Create();
        var features = await OpenAsync(f);
        await f.Shell.Workbench.Settings.SetAsync(ShellHost.DebugAdapterSetting, DebugService.NetcoredbgAdapter, SettingScope.User, CancellationToken.None);

        // The IDE applies the hit count and the message, as it always did for netcoredbg.
        var breakpoints = f.Shell.Debugger.Breakpoints;
        breakpoints.Set(new BreakpointSpec(features, Line(features, "features-loop"), HitCondition: "== 4"));
        breakpoints.Set(new BreakpointSpec(features, Line(features, "features-start"), LogMessage: "starting in $FUNCTION"));
        await f.RunAsync("debug.start");
        await StoppedAtAsync(f, features, "features-loop", seconds: 180);
        await ShellFixture.PumpUntil(() => f.Shell.Locals.Roots.Any(r => r is { Name: "i", Value: "3" }));
        Assert.Contains("starting in ", f.Shell.DebugLog.GetText(), StringComparison.Ordinal);

        Assert.Equal(false, f.Shell.Workbench.Context.GetValue("debugCanSetNextStatement"));
        Assert.Equal(false, f.Shell.Workbench.Context.GetValue("debugCanStepIntoSpecific"));
        Assert.Equal(false, f.Shell.Workbench.Context.GetValue("debugCanFreezeThreads"));
        Assert.Empty(await f.Shell.Debugger.StepInTargetsAsync());

        // The machine's windows say which debugger shows them.
        Assert.False(f.Shell.Debugger.CanDisassemble);
        f.Shell.Layout.ShowToolWindow(RegistersViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => f.Shell.RegistersWindow.Message is not null);
        Assert.Contains("Codalus debugger", f.Shell.RegistersWindow.Message, StringComparison.Ordinal);
        f.Shell.Layout.ShowToolWindow(DisassemblyViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => f.Shell.DisassemblyWindow.Message is not null);
        Assert.Contains("Codalus debugger", f.Shell.DisassemblyWindow.Message, StringComparison.Ordinal);
        f.Shell.MemoryWindow.AddressText = "0x1000";
        await f.Shell.MemoryWindow.GoAsync();
        Assert.Contains("Codalus debugger", f.Shell.MemoryWindow.Message, StringComparison.Ordinal);
        Assert.False(await f.Shell.SetNextStatementAsync(features, Line(features, "features-nested")));
        Assert.Contains("can't set the next statement", f.Shell.StatusBar.Message, StringComparison.Ordinal);
        await f.RunAsync("debug.stop");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle, seconds: 60);
    }
}
