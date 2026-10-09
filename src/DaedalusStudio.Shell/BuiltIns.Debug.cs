using System.Text.Json;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Commands;
using DaedalusStudio.Workbench.Context;
using DaedalusStudio.Workbench.Menus;
using DaedalusStudio.Workbench.Settings;
using DaedalusStudio.Workbench.ToolWindows;

namespace DaedalusStudio.Shell;

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
          { "key": "ctrl+alt+pause", "command": "debug.pause", "when": "debugRunning" },
          { "key": "f9", "command": "debug.toggleBreakpoint", "when": "editorTextFocus" },
          { "key": "ctrl+f9", "command": "debug.toggleBreakpointEnabled", "when": "editorTextFocus" },
          { "key": "primary+shift+f9", "command": "debug.deleteAllBreakpoints", "when": "hasBreakpoints" },
          { "key": "ctrl+alt+p", "command": "debug.attach", "when": "!debugActive" }
        ]
        """;

    public static IEnumerable<ToolWindowDescriptor> DebugToolWindows()
    {
        var shell = ContributionSource.Shell;
        yield return new ToolWindowDescriptor(VariablesViewModel.LocalsWindowId, "Locals", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "list" };
        yield return new ToolWindowDescriptor(VariablesViewModel.AutosWindowId, "Autos", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "list" };
        yield return new ToolWindowDescriptor(WatchViewModel.ToolWindowId, "Watch", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "search" };
        yield return new ToolWindowDescriptor(CallStackViewModel.ToolWindowId, "Call Stack", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "list" };
        yield return new ToolWindowDescriptor(BreakpointsViewModel.ToolWindowId, "Breakpoints", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "circle" };
        yield return new ToolWindowDescriptor(DebugConsoleViewModel.ToolWindowId, "Debug Console", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "debug" };
    }

    public static IReadOnlyList<SettingDescriptor> DebugSettings() =>
    [
        SettingDescriptor.Create(ShellHost.JustMyCodeSetting, "true", SettingType.Boolean, ContributionSource.Shell) with
        {
            Title = "Just My Code",
            Category = "Debugging",
            Description = "Step through and break in your own code only (optimized and framework code is stepped over).",
            Order = 1,
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
        Add("debug.toggleBreakpoint", "Toggle Breakpoint", Sync(shell.ToggleBreakpoint), "activeDocumentKind == textFile");
        Add("debug.toggleBreakpointEnabled", "Enable or Disable Breakpoint", Sync(shell.ToggleBreakpointEnabled), "activeDocumentKind == textFile");
        Add("debug.editBreakpoint", "Breakpoint Conditions and Actions…", _ => shell.EditBreakpointAsync(), "activeDocumentKind == textFile");
        Add("debug.deleteAllBreakpoints", "Delete All Breakpoints", _ => shell.DeleteAllBreakpointsAsync(), "hasBreakpoints");
        Add("debug.exceptionSettings", "Exception Settings…", _ => shell.SelectExceptionSettingsAsync());
        Add("debug.addWatch", "Add Watch", shell.AddWatchAsync);
    }

    public static void RegisterDebugMenus(MenuRegistry menus)
    {
        var shell = ContributionSource.Shell;
        MenuItemRule Item(string command, string group, int order, string? title = null) =>
            new(MenuLocations.MenuBar("debug"), command, shell) { Group = group, Order = order, Title = title };

        menus.Add(
        [
            Item("debug.start", "0-debug", 1),
            Item("debug.continue", "0-debug", 2),
            Item("debug.stop", "0-debug", 3),
            Item("debug.restart", "0-debug", 4),
            Item("debug.pause", "0-debug", 5),
            Item("debug.attach", "0-debug", 6),
            Item("debug.stepOver", "3-step", 1),
            Item("debug.stepInto", "3-step", 2),
            Item("debug.stepOut", "3-step", 3),
            Item("debug.toggleBreakpoint", "4-breakpoints", 1),
            Item("debug.toggleBreakpointEnabled", "4-breakpoints", 2),
            Item("debug.editBreakpoint", "4-breakpoints", 3),
            Item("debug.deleteAllBreakpoints", "4-breakpoints", 4),
            Item("debug.exceptionSettings", "4-breakpoints", 5),
            Item("debug.addWatch", "5-windows", 1),
            Item(ViewCommandPrefix + VariablesViewModel.LocalsWindowId, "5-windows", 2, "Locals"),
            Item(ViewCommandPrefix + VariablesViewModel.AutosWindowId, "5-windows", 3, "Autos"),
            Item(ViewCommandPrefix + WatchViewModel.ToolWindowId, "5-windows", 4, "Watch"),
            Item(ViewCommandPrefix + CallStackViewModel.ToolWindowId, "5-windows", 5, "Call Stack"),
            Item(ViewCommandPrefix + BreakpointsViewModel.ToolWindowId, "5-windows", 6, "Breakpoints"),
            Item(ViewCommandPrefix + DebugConsoleViewModel.ToolWindowId, "5-windows", 7, "Debug Console"),
        ]);
    }
}
