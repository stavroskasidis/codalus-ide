using System.Text.Json;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Commands;
using DaedalusStudio.Workbench.Context;
using DaedalusStudio.Workbench.Menus;
using DaedalusStudio.Workbench.Settings;
using DaedalusStudio.Workbench.ToolWindows;

namespace DaedalusStudio.Shell;

/// <summary>
/// Profiling and diagnostics (M18a, ADR 0044 and 0045): the Performance Profiler, profiling reports, the Diagnostic
/// Tools and .NET Counters windows, hot lines, their commands, menus and key bindings.
/// </summary>
internal static partial class BuiltIns
{
    public const string ProfilingKeybindings = """
        [
          { "key": "alt+f2", "command": "profiler.show" },
          { "key": "ctrl+alt+f2", "command": "diagnostics.showDiagnosticTools" }
        ]
        """;

    public static IReadOnlyList<SettingDescriptor> ProfilingSettings() =>
    [
        SettingDescriptor.Create(ShellHost.ShowDiagnosticToolsSetting, "false", SettingType.Boolean, ContributionSource.Shell) with
        {
            Title = "Show Diagnostic Tools When Debugging Starts",
            Category = "Debugging",
            Description = "Open the Diagnostic Tools window (CPU, memory, GCs, exceptions and the runtime's counters) when a debug session starts. While it is open, it follows the program being run or debugged either way.",
        },
    ];

    public static IEnumerable<ToolWindowDescriptor> ProfilingToolWindows()
    {
        yield return new ToolWindowDescriptor(DiagnosticToolsViewModel.ToolWindowId, "Diagnostic Tools", ToolWindowLocation.Right, ToolWindowKind.BuiltIn, ContributionSource.Shell) { Icon = "flame" };
        yield return new ToolWindowDescriptor(CountersViewModel.ToolWindowId, ".NET Counters", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, ContributionSource.Shell) { Icon = "flame" };
    }

    public static void RegisterProfilingCommands(ShellHost shell)
    {
        var commands = shell.Workbench.Commands;
        void Add(string id, string title, Func<string?, Task> handler, string? enablement = null) =>
            commands.Register(
                new CommandDescriptor(id, title, ContributionSource.Shell) { Category = "Profiling", Enablement = WhenClause.Parse(enablement) },
                async (args, _) =>
                {
                    var argument = args.Count > 0 && args[0].ValueKind == JsonValueKind.String ? args[0].GetString() : null;
                    await Infrastructure.Ui.RunAsync(() => handler(argument)).ConfigureAwait(false);
                    return null;
                });

        Add("profiler.show", "Performance Profiler…", _ =>
        {
            shell.OpenProfiler();
            return Task.CompletedTask;
        });
        Add("profiler.stop", "Stop Collection", _ => shell.StopProfilingAsync(), "profilerCollecting");
        Add("profiler.open", "Open Profiling Session…", path => shell.OpenProfilingFileAsync(path));
        Add("profiler.toggleHotLines", "Show Hot Lines in the Editor", _ =>
        {
            shell.Profiling.HotLines.IsVisible = !shell.Profiling.HotLines.IsVisible;
            return Task.CompletedTask;
        });
        Add("diagnostics.showDiagnosticTools", "Diagnostic Tools", _ =>
        {
            shell.ShowDiagnosticTools();
            return Task.CompletedTask;
        });
        Add("diagnostics.showCounters", ".NET Counters", _ =>
        {
            shell.ShowCounters();
            return Task.CompletedTask;
        });
    }

    public static void RegisterProfilingMenus(MenuRegistry menus)
    {
        var shell = ContributionSource.Shell;
        MenuItemRule Item(string location, string command, string group, int order = 0, string? when = null) =>
            new(location, command, shell) { Group = group, Order = order, When = WhenClause.Parse(when) };

        menus.Add(
        [
            Item(MenuLocations.MenuBar("debug"), "profiler.show", "7-profiling", 1),
            Item(MenuLocations.MenuBar("debug"), "profiler.stop", "7-profiling", 2, "profilerCollecting"),
            Item(MenuLocations.MenuBar("debug"), "profiler.open", "7-profiling", 3),
            Item(MenuLocations.MenuBar("debug"), "profiler.toggleHotLines", "7-profiling", 4),
            Item(DebugWindowsMenu, "diagnostics.showDiagnosticTools", "windows", AlphabeticalOrder("Diagnostic Tools")),
            Item(OtherWindowsMenu, "diagnostics.showCounters", "windows", AlphabeticalOrder(".NET Counters")),
        ]);
    }
}
