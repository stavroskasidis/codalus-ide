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
            Description = "Start Debugging runs a Blazor WebAssembly app's dev server under dotnet watch, so saving a file (or Apply Code Changes, Alt+F10) applies the edit to the app in the browser while you debug it. The .NET runtime refuses edits to a program the debugger runs, so other apps restart instead.",
            Order = 5,
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
