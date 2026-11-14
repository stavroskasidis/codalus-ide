using System.Text.Json;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Context;
using Codalus.Workbench.Menus;
using Codalus.Workbench.Settings;
using Codalus.Workbench.ToolWindows;

namespace Codalus.Shell;

/// <summary>Debugger commands, key bindings, menus, settings and tool windows (ADR 0007).</summary>
internal static partial class BuiltIns
{
    public const string DebugKeybindings = """
        [
          { "key": "f5", "command": "debug.start", "when": "!debugRunning" },
          { "key": "shift+f5", "command": "debug.stop", "when": "debugActive" },
          { "key": "primary+shift+f5", "command": "debug.restart", "when": "debugActive" },
          { "key": "f10", "command": "debug.stepOver", "when": "debugStopped" },
          { "key": "f11", "command": "debug.stepInto", "when": "debugStopped" },
          { "key": "shift+f11", "command": "debug.stepOut", "when": "debugStopped" },
          { "key": "ctrl+alt+pause", "mac": "cmd+alt+p", "command": "debug.pause", "when": "debugRunning" },
          { "key": "f9", "command": "debug.toggleBreakpoint", "when": "editorTextFocus" },
          { "key": "ctrl+f9", "command": "debug.toggleBreakpointEnabled", "when": "editorTextFocus" },
          { "key": "primary+shift+f9", "command": "debug.deleteAllBreakpoints", "when": "hasBreakpoints" },
          { "key": "ctrl+alt+p", "command": "debug.attach", "when": "!debugActive" },
          { "key": "shift+alt+p", "command": "debug.reattach", "when": "!debugActive" },
          { "key": "shift+f9", "command": "debug.quickWatch", "when": "debugStopped" },
          { "key": "primary+f10", "command": "debug.runToCursor", "when": "editorTextFocus && !debugRunning" },
          { "key": "primary+shift+f10", "command": "debug.setNextStatement", "when": "editorTextFocus && debugCanSetNextStatement" },
          { "key": "shift+alt+f11", "command": "debug.stepIntoSpecific", "when": "debugCanStepIntoSpecific" },
          { "key": "ctrl+alt+e", "command": "debug.exceptionSettings" },
          { "key": "shift+alt+f9 t", "command": "debug.insertTemporaryBreakpoint", "when": "editorTextFocus" },
          { "key": "ctrl+alt+b", "command": "workbench.view.debug.breakpoints", "when": "keyboardScheme != vscode" },
          { "key": "ctrl+alt+h", "command": "workbench.view.debug.threads", "when": "keyboardScheme != vscode" },
          { "key": "ctrl+alt+u", "command": "workbench.view.debug.modules", "when": "keyboardScheme != vscode" },
          { "key": "ctrl+alt+c", "command": "workbench.view.debug.callStack", "when": "keyboardScheme != vscode" }
        ]
        """;

    public static IEnumerable<ToolWindowDescriptor> DebugToolWindows()
    {
        var shell = ContributionSource.Shell;
        yield return new ToolWindowDescriptor(VariablesViewModel.LocalsWindowId, "Locals", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "table" };
        yield return new ToolWindowDescriptor(VariablesViewModel.AutosWindowId, "Autos", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "sparkle" };
        yield return new ToolWindowDescriptor(WatchViewModel.ToolWindowId, "Watch", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "search" };
        yield return new ToolWindowDescriptor(CallStackViewModel.ToolWindowId, "Call Stack", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "call-hierarchy" };
        yield return new ToolWindowDescriptor(BreakpointsViewModel.ToolWindowId, "Breakpoints", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "circle" };
        yield return new ToolWindowDescriptor(DebugConsoleViewModel.ToolWindowId, "Debug Console", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "debug" };
        yield return new ToolWindowDescriptor(QuickWatchViewModel.ToolWindowId, "QuickWatch", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "search" };
        yield return new ToolWindowDescriptor(ThreadsViewModel.ToolWindowId, "Threads", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "timer" };
        yield return new ToolWindowDescriptor(ModulesViewModel.ToolWindowId, "Modules", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "package" };
        yield return new ToolWindowDescriptor(DisassemblyViewModel.ToolWindowId, "Disassembly", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "code" };
        yield return new ToolWindowDescriptor(RegistersViewModel.ToolWindowId, "Registers", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "braces" };
        yield return new ToolWindowDescriptor(MemoryViewModel.ToolWindowId, "Memory", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "table" };
        yield return new ToolWindowDescriptor(LiveTreeViewModel.ToolWindowId, "Live Visual Tree", ToolWindowLocation.Left, ToolWindowKind.BuiltIn, shell) { Icon = "type-hierarchy" };
        yield return new ToolWindowDescriptor(LivePropertiesViewModel.ToolWindowId, "Live Property Explorer", ToolWindowLocation.Right, ToolWindowKind.BuiltIn, shell) { Icon = "properties" };
        yield return new ToolWindowDescriptor(ParallelStacksViewModel.ToolWindowId, "Parallel Stacks", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "layers" };
        yield return new ToolWindowDescriptor(ParallelWatchViewModel.ToolWindowId, "Parallel Watch", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "search" };
        yield return new ToolWindowDescriptor(ProcessesViewModel.ToolWindowId, "Processes", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "gauge" };
        yield return new ToolWindowDescriptor(ExceptionSettingsViewModel.ToolWindowId, "Exception Settings", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "circle" };
    }

    public static IReadOnlyList<SettingDescriptor> DebugSettings() =>
    [
        SettingDescriptor.Create(ShellHost.LiveVisualTreeSetting, "true", SettingType.Boolean, ContributionSource.Shell) with
        {
            Title = "Live Visual Tree for Avalonia and WPF apps",
            Category = "Debugging",
            Description = "Running or debugging an Avalonia app, or a WPF app on Windows, loads Codalus IDE's agent into it (a .NET startup hook), for the Live Visual Tree and Live Property Explorer.",
            Order = 30,
        },
        SettingDescriptor.Create(ShellHost.XamlHotReloadSetting, "true", SettingType.Boolean, ContributionSource.Shell) with
        {
            Title = "XAML Hot Reload",
            Category = "Debugging",
            Description = "A saved .axaml or .xaml file is loaded again into the running Avalonia app (or WPF app on Windows) that has the live agent: each window or user control of its x:Class on screen changes in place. An edit it can't apply is an Error List row with an offer to restart.",
            Order = 31,
        },
        SettingDescriptor.Create(ShellHost.BreakOnFailedBindingSetting, "false", SettingType.Boolean, ContributionSource.Shell) with
        {
            Title = "Break when a binding fails",
            Category = "Debugging",
            Description = "A binding that fails in the Avalonia app (or WPF app on Windows) being debugged stops it, as a thrown exception does, at the binding's XAML.",
            Order = 32,
        },
        SettingDescriptor.Create(ShellHost.JustMyCodeSetting, "true", SettingType.Boolean, ContributionSource.Shell) with
        {
            Title = "Just My Code",
            Category = "Debugging",
            Description = "Step through and break in your own code only (optimized and framework code is stepped over).",
            Order = 1,
        },
        SettingDescriptor.Create(ShellHost.StepFilteringSetting, "true", SettingType.Boolean, ContributionSource.Shell) with
        {
            Title = "Step over properties and operators",
            Category = "Debugging",
            Description = "Step Into steps over property getters and setters and operators (applies from the next session).",
            Order = 1,
        },
        SettingDescriptor.Create(ShellHost.BreakAllProcessesSetting, "true", SettingType.Boolean, ContributionSource.Shell) with
        {
            Title = "Break all processes when one process breaks",
            Category = "Debugging",
            Description = "With several startup projects under the Codalus debugger, a breakpoint, a step's end or an exception in one program pauses the others, and they go on with it (applies from the next session).",
            Order = 1,
        },
        SettingDescriptor.Create(Workbench.Debugging.Browsers.SettingKey, "\"auto\"", SettingType.String, ContributionSource.Shell) with
        {
            Title = "Browser",
            Category = "Debugging",
            Description = "The browser for debugging in the browser (Blazor WebAssembly, JavaScript): auto (Chrome, then Edge, then Chromium), chrome, edge, chromium, or the path of a Chromium browser.",
            Order = 3,
        },
        SettingDescriptor.Create(ShellHost.JavaScriptDebugSetting, "true", SettingType.Boolean, ContributionSource.Shell) with
        {
            Title = "JavaScript debugging for ASP.NET Core",
            Category = "Debugging",
            Description = "Start Debugging opens an ASP.NET Core app's page in Chrome or Edge under the debugger, when its launch profile opens a browser, so breakpoints in its scripts bind too. Blazor WebAssembly components always debug in the browser.",
            Order = 4,
        },
        SettingDescriptor.Create(ShellHost.DebugHotReloadSetting, "true", SettingType.Boolean, ContributionSource.Shell) with
        {
            Title = "Hot Reload while debugging",
            Category = "Debugging",
            Description = "Saving a file applies the edit to the program being debugged. Under the Codalus debugger (Debugger for .NET programs) every program takes C#, Visual Basic and Razor edits while debugged: Apply Code Changes (Alt+F10) applies them any time, and Continue applies the ones made while stopped. Start Debugging also runs a Blazor WebAssembly app's dev server, and a Blazor Web App's server, under dotnet watch, so WebAssembly components take edits in the browser; in a Blazor Web App the debugger attaches to the server once it starts (breakpoints in its Program.cs debug it from the start, without Hot Reload), and edits that need a restart restart the app. Under netcoredbg the .NET runtime doesn't apply edits to a program the debugger runs: the server keeps the code it started with until you restart, and other apps need Start with Hot Reload.",
            Order = 5,
        },
        SettingDescriptor.Create(Codalus.Shell.Editor.EditorOptions.InlineValuesKey, "true", SettingType.Boolean, ContributionSource.Shell) with
        {
            Title = "Inline values",
            Category = "Debugging",
            Description = "While the debugger is stopped, draw each line's values after it (name = value), from the method's start to the current statement, for the frame selected in the Call Stack. Locals, parameters, fields and auto-properties are shown; other properties and calls are left to the data tip, so no code of yours runs. A value that changed since the last step is highlighted. Turn it off for one language in that language's settings (for example \"[csharp]\": { \"debug.inlineValues\": false }).",
            Order = 3,
        },
        SettingDescriptor.Create(Codalus.Shell.Editor.EditorOptions.PerfTipsKey, "true", SettingType.Boolean, ContributionSource.Shell) with
        {
            Title = "PerfTips",
            Category = "Debugging",
            Description = "Show the time elapsed since the program was resumed at the end of the current statement's line.",
            Order = 2,
        },
        SettingDescriptor.Create(ShellHost.DebugConsoleSetting, $"\"{ShellHost.DefaultDebugConsole}\"", SettingType.String, ContributionSource.Shell) with
        {
            Title = "Console of debugged programs",
            Category = "Debugging",
            Description = "Where a console or web app debugged with Start Debugging gets its console: the Debug Console (output only), the integrated terminal, or a console window of its own (Windows, the default there). The terminal and the console window take input, and Detach All leaves the program running.",
            Enum = ["internalConsole", "integratedTerminal", "externalConsole"],
            EnumDescriptions = ["Debug Console", "Integrated terminal", "Console window (Windows)"],
            Order = 5,
        },
        SettingDescriptor.Create(ShellHost.DebugAdapterSetting, $"\"{Workbench.Debugging.DebugService.CodalusAdapter}\"", SettingType.String, ContributionSource.Shell) with
        {
            Title = "Debugger for .NET programs",
            Category = "Debugging",
            Description = "The debugger that debugs .NET programs from the next session: the Codalus debugger, Codalus IDE's own (the default), or netcoredbg, kept for one release as a fallback when something goes wrong under the Codalus debugger. Hot Reload and Edit and Continue while debugging, Set Next Statement, lambdas in expressions, the Disassembly, Registers and Memory windows and symbol servers need the Codalus debugger. It doesn't yet debug remote targets or x64 programs on Apple silicon, which netcoredbg debugs then (the Debug Console says so). .NET Framework programs have their own debugger.",
            Enum = [Workbench.Debugging.DebugService.CodalusAdapter, Workbench.Debugging.DebugService.NetcoredbgAdapter],
            EnumDescriptions = ["Codalus debugger", "netcoredbg (fallback)"],
            Order = 8,
        },
        SettingDescriptor.Create(ShellHost.DebugEditsOfferSetting, "true", SettingType.Boolean, ContributionSource.Shell) with
        {
            Title = "Offer the Codalus debugger for edits",
            Category = "Debugging",
            Description = "When you save an edit while netcoredbg debugs a program, offer once to debug with the Codalus debugger, which applies edits to the program while it's debugged.",
            Order = 9,
        },
        SettingDescriptor.Create(Workbench.Debugging.Remote.RemoteTargets.ContainerToolSetting, "\"\"", SettingType.String, ContributionSource.Shell) with
        {
            Title = "Container tool",
            Category = "Debugging",
            Description = "The container command line for attaching in containers: docker, podman, or one inside WSL such as \"wsl -d Ubuntu --exec podman\". Empty: the Containers window's engine (containers.tool, else docker when it is installed, else podman).",
            Order = 6,
        },
        SettingDescriptor.Create(Workbench.Debugging.Remote.RemoteTargets.SourceFileMapSetting, "[]", SettingType.StringArray, ContributionSource.Shell) with
        {
            Title = "Source file map",
            Category = "Debugging",
            Description = "Source folders of programs debugged on another machine, as \"/folder/there=C:\\folder\\here\". Without one, a file is found in the solution by its folder and name.",
            Order = 7,
        },
        SettingDescriptor.Create(ShellHost.SymbolsMicrosoftServerSetting, "false", SettingType.Boolean, ContributionSource.Shell) with
        {
            Title = "Symbols: Microsoft Symbol Server",
            Category = "Debugging",
            Description = "Look for the PDBs of .NET's own modules on Microsoft's symbol server (msdl.microsoft.com), under the Codalus debugger. The names and build IDs of the modules looked up are sent to it. Off by default.",
            Order = 20,
        },
        SettingDescriptor.Create(ShellHost.SymbolsNuGetServerSetting, "false", SettingType.Boolean, ContributionSource.Shell) with
        {
            Title = "Symbols: NuGet.org Symbol Server",
            Category = "Debugging",
            Description = "Look for the PDBs of NuGet packages on NuGet.org's symbol server (symbols.nuget.org), under the Codalus debugger. The names and build IDs of the modules looked up are sent to it. Off by default.",
            Order = 21,
        },
        SettingDescriptor.Create(ShellHost.SymbolsServersSetting, "[]", SettingType.StringArray, ContributionSource.Shell) with
        {
            Title = "Symbols: other symbol servers",
            Category = "Debugging",
            Description = "The URLs of other symbol servers (https://), asked in order after the two above.",
            Order = 22,
        },
        SettingDescriptor.Create(ShellHost.SymbolsPathsSetting, "[]", SettingType.StringArray, ContributionSource.Shell) with
        {
            Title = "Symbols: symbol paths",
            Category = "Debugging",
            Description = "Folders searched for a module's PDB when there's none next to it: the PDB itself, or a symbol store's layout.",
            Order = 23,
        },
        SettingDescriptor.Create(ShellHost.SymbolsCacheSetting, "\"\"", SettingType.String, ContributionSource.Shell) with
        {
            Title = "Symbols: cache folder",
            Category = "Debugging",
            Description = "Where PDBs downloaded from symbol servers are kept. Empty: Codalus IDE's cache folder.",
            Order = 24,
        },
        SettingDescriptor.Create(ShellHost.SymbolsOnlySpecifiedSetting, "false", SettingType.Boolean, ContributionSource.Shell) with
        {
            Title = "Symbols: load only specified modules",
            Category = "Debugging",
            Description = "Look up only the modules listed in \"Symbols: modules to load\". Off, every module is looked up but those in \"Symbols: modules to leave out\". With Just My Code on, modules are looked up by themselves only when this is on; Load Symbols in the Modules window asks for any module.",
            Order = 25,
        },
        SettingDescriptor.Create(ShellHost.SymbolsIncludeSetting, "[]", SettingType.StringArray, ContributionSource.Shell) with
        {
            Title = "Symbols: modules to load",
            Category = "Debugging",
            Description = "Module file names to look up when only specified modules are loaded; * and ? match any characters (Newtonsoft.*.dll).",
            Order = 26,
        },
        SettingDescriptor.Create(ShellHost.SymbolsExcludeSetting, "[]", SettingType.StringArray, ContributionSource.Shell) with
        {
            Title = "Symbols: modules to leave out",
            Category = "Debugging",
            Description = "Module file names that are never looked up by themselves; * and ? match any characters.",
            Order = 27,
        },
    ];

    public static void RegisterDebugCommands(ShellHost shell)
    {
        var commands = shell.Workbench.Commands;
        void Add(string id, string title, Func<string?, Task> handler, string? enablement = null, bool palette = true) =>
            commands.Register(
                new CommandDescriptor(id, title, ContributionSource.Shell)
                {
                    Category = "Debug",
                    Enablement = WhenClause.Parse(enablement),
                    ShowInPalette = palette,
                },
                async (args, _) =>
                {
                    var argument = args.Count > 0 && args[0].ValueKind == JsonValueKind.String ? args[0].GetString()
                        : args.Count > 0 && args[0].ValueKind == JsonValueKind.Number ? args[0].GetRawText() : null;
                    await Infrastructure.Ui.RunAsync(() => handler(argument)).ConfigureAwait(false);
                    return null;
                });
        static Func<string?, Task> Sync(Action action) => _ =>
        {
            action();
            return Task.CompletedTask;
        };

        Add("debug.start", "Start Debugging", shell.StartDebuggingAsync, "!debugRunning && !buildRunning");
        Add("debug.continue", "Continue", _ => shell.Debugger.ContinueAsync(), "debugStopped");
        Add("debug.stop", "Stop Debugging", _ => shell.Debugger.StopAsync(), "debugActive");
        Add("debug.restart", "Restart", _ => shell.RestartDebuggingAsync(), "debugActive");
        Add("debug.stepOver", "Step Over", _ => shell.Debugger.StepOverAsync(), "debugStopped");
        Add("debug.stepInto", "Step Into", _ => shell.Debugger.StepIntoAsync(), "debugStopped");
        Add("debug.stepOut", "Step Out", _ => shell.Debugger.StepOutAsync(), "debugStopped");
        Add("debug.pause", "Break All", _ => shell.Debugger.PauseAsync(), "debugRunning");
        Add("debug.attach", "Attach to Process…", shell.AttachToProcessAsync, "!debugActive");
        Add("debug.webPage", "Debug a Web Page…", shell.DebugWebPageAsync, "!debugActive");
        Add("debug.toggleBreakpoint", "Toggle Breakpoint", Sync(shell.ToggleBreakpoint), "activeDocumentKind == textFile");
        Add("debug.toggleBreakpointEnabled", "Enable or Disable Breakpoint", Sync(shell.ToggleBreakpointEnabled), "activeDocumentKind == textFile");
        Add("debug.editBreakpoint", "Breakpoint Conditions and Actions…", _ => shell.EditBreakpointAsync(), "activeDocumentKind == textFile");
        Add("debug.deleteAllBreakpoints", "Delete All Breakpoints", _ => shell.DeleteAllBreakpointsAsync(), "hasBreakpoints");
        Add("debug.exceptionSettings", "Exception Settings", Sync(shell.ShowExceptionSettings));
        Add("debug.addWatch", "Add Watch", shell.AddWatchAsync);
        Add("debug.quickWatch", "QuickWatch…", shell.ShowQuickWatchAsync, "debugStopped");
        Add("debug.runToCursor", "Run to Cursor", _ => shell.RunToCursorAsync(), "activeDocumentKind == textFile && !debugRunning");
        Add("debug.forceRunToCursor", "Force Run to Cursor", _ => shell.RunToCursorAsync(force: true), "activeDocumentKind == textFile && debugStopped");
        Add("debug.setNextStatement", "Set Next Statement", _ => shell.SetNextStatementAsync(), "activeDocumentKind == textFile && debugCanSetNextStatement");
        Add("debug.stepIntoSpecific", "Step Into Specific…", _ => shell.StepIntoSpecificAsync(), "debugCanStepIntoSpecific");
        Add("debug.reattach", "Reattach to Process", _ => shell.ReattachAsync(), "!debugActive");
        Add("debug.newFunctionBreakpoint", "New Function Breakpoint…", _ => shell.NewFunctionBreakpointAsync());
        Add("debug.insertTemporaryBreakpoint", "Insert Temporary Breakpoint", Sync(shell.InsertTemporaryBreakpoint), "activeDocumentKind == textFile");
        Add("debug.exportBreakpoints", "Export Breakpoints…", _ => shell.ExportBreakpointsAsync(), "hasBreakpoints");
        Add("debug.importBreakpoints", "Import Breakpoints…", _ => shell.ImportBreakpointsAsync());
        Add("debug.openDump", "Open Dump File…", shell.OpenDumpAsync);
        Add("debug.createDump", "Create Dump", shell.CreateDumpAsync, "debugActive");
        Add("debug.clearDataTips", "Clear All DataTips", Sync(shell.Debugger.PinnedTips.Clear));
        Add("debug.detachAll", "Detach All", _ => shell.Debugger.DetachAsync(), "debugCanDetach");
        Add("debug.attachRemote", "Attach to Process on a Remote Machine…", shell.AttachToRemoteProcessAsync, "!debugActive");
        Add("debug.justInTime", "Just-In-Time Debugging…", shell.ConfigureJitDebuggingAsync);
    }
}
