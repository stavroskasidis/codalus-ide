using System.Text.Json;
using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Shell.Views;
using DaedalusStudio.Workbench.Commands;
using DaedalusStudio.Workbench.Context;
using DaedalusStudio.Workbench.Menus;
using DaedalusStudio.Workbench.Settings;

namespace DaedalusStudio.Shell;

/// <summary>Build and run commands, menus, key bindings and settings (ADR 0006).</summary>
internal static partial class BuiltIns
{
    public const string BuildKeybindings = """
        [
          { "key": "primary+shift+b", "command": "build.buildSolution" },
          { "key": "ctrl+pause", "command": "build.cancel", "when": "buildRunning" },
          { "key": "ctrl+f5", "mac": "cmd+f5", "command": "run.startWithoutDebugging" },
          { "key": "shift+f5", "command": "run.stop", "when": "programRunning && !debugActive" }
        ]
        """;

    public static IReadOnlyList<SettingDescriptor> BuildSettings()
    {
        var shell = ContributionSource.Shell;
        return
        [
            SettingDescriptor.Create(ShellHost.SaveAllBeforeBuildSetting, "true", SettingType.Boolean, shell) with
            {
                Title = "Save All Before Build",
                Category = "Build and Run",
                Description = "Save every modified document before a build starts.",
                Order = 1,
            },
            SettingDescriptor.Create(ShellHost.ExternalConsoleSetting, "false", SettingType.Boolean, shell) with
            {
                Title = "Run Console Apps in an External Console",
                Category = "Build and Run",
                Description = "Windows only: start console apps in a console window of their own (so they can read input) instead of showing their output in the Output window.",
                Order = 2,
            },
        ];
    }

    public static void RegisterBuildCommands(ShellHost shell)
    {
        var commands = shell.Workbench.Commands;
        void Add(string id, string title, string category, Func<string?, Task> handler, string? enablement = null, bool palette = true) =>
            commands.Register(
                new CommandDescriptor(id, title, ContributionSource.Shell)
                {
                    Category = category,
                    Enablement = WhenClause.Parse(enablement),
                    ShowInPalette = palette,
                },
                async (args, _) =>
                {
                    // An optional first argument: a project path (for plugins and tests).
                    var project = args.Count > 0 && args[0].ValueKind == JsonValueKind.String ? args[0].GetString() : null;
                    await Infrastructure.Ui.RunAsync(() => handler(project)).ConfigureAwait(false);
                    return null;
                });

        const string Idle = "solutionOpen && !buildRunning";
        Add("build.buildSolution", "Build Solution", "Build", _ => shell.BuildAsync(BuildTargets.Build, null), Idle);
        Add("build.rebuildSolution", "Rebuild Solution", "Build", _ => shell.BuildAsync(BuildTargets.Rebuild, null), Idle);
        Add("build.cleanSolution", "Clean Solution", "Build", _ => shell.BuildAsync(BuildTargets.Clean, null), Idle);
        Add("build.buildProject", "Build Project", "Build", p => shell.BuildProjectAsync(BuildTargets.Build, p), Idle);
        Add("build.rebuildProject", "Rebuild Project", "Build", p => shell.BuildProjectAsync(BuildTargets.Rebuild, p), Idle);
        Add("build.cleanProject", "Clean Project", "Build", p => shell.BuildProjectAsync(BuildTargets.Clean, p), Idle);
        Add("build.cancel", "Cancel Build", "Build", _ =>
        {
            shell.CancelBuild();
            return Task.CompletedTask;
        }, "buildRunning");
        Add("run.startWithoutDebugging", "Start Without Debugging", "Debug", shell.StartWithoutDebuggingAsync, Idle);
        Add("run.stop", "Stop Running Programs", "Debug", _ =>
        {
            shell.StopRunning();
            return Task.CompletedTask;
        }, "programRunning");
        Add("run.selectStartupProject", "Select Startup Project…", "Debug", _ => shell.SelectStartupProjectAsync(), "solutionOpen");
        Add("run.selectLaunchProfile", "Select Launch Profile…", "Debug", shell.SelectLaunchProfileAsync, "hasStartupProject");
        Add("run.setStartupProject", "Set as Startup Project", "Debug", p =>
        {
            if ((p ?? shell.Explorer.Selected?.Node.ProjectPath) is { } project)
            {
                shell.SetStartupProject(Path.GetFullPath(project));
            }

            return Task.CompletedTask;
        }, "solutionOpen", palette: false);
    }

    public static void RegisterBuildMenus(MenuRegistry menus)
    {
        menus.AddMenuBarMenu(new MenuBarMenu("build", "_Build", 50));
        menus.AddMenuBarMenu(new MenuBarMenu("debug", "_Debug", 60));
        var shell = ContributionSource.Shell;
        MenuItemRule Item(string location, string command, string group, int order = 0, string? when = null, string? title = null) =>
            new(location, command, shell) { Group = group, Order = order, When = WhenClause.Parse(when), Title = title };

        const string Context = SolutionExplorerView.ContextMenuLocation;
        menus.Add(
        [
            Item(MenuLocations.MenuBar("build"), "build.buildSolution", "1-solution", 1),
            Item(MenuLocations.MenuBar("build"), "build.rebuildSolution", "1-solution", 2),
            Item(MenuLocations.MenuBar("build"), "build.cleanSolution", "1-solution", 3),
            Item(MenuLocations.MenuBar("build"), "build.buildProject", "2-project", 1),
            Item(MenuLocations.MenuBar("build"), "build.rebuildProject", "2-project", 2),
            Item(MenuLocations.MenuBar("build"), "build.cleanProject", "2-project", 3),
            Item(MenuLocations.MenuBar("build"), "build.cancel", "3-cancel", 1),
            Item(MenuLocations.MenuBar("build"), "solution.selectConfiguration", "4-configuration", 1, title: "Configuration…"),
            Item(MenuLocations.MenuBar("debug"), "run.startWithoutDebugging", "1-run", 1),
            Item(MenuLocations.MenuBar("debug"), "run.stop", "1-run", 2),
            Item(MenuLocations.MenuBar("debug"), "run.selectStartupProject", "2-startup", 1),
            Item(MenuLocations.MenuBar("debug"), "run.selectLaunchProfile", "2-startup", 2),

            Item(Context, "build.buildSolution", "0-build", 1, "explorerItemKind == solution", "Build Solution"),
            Item(Context, "build.rebuildSolution", "0-build", 2, "explorerItemKind == solution", "Rebuild Solution"),
            Item(Context, "build.cleanSolution", "0-build", 3, "explorerItemKind == solution", "Clean Solution"),
            Item(Context, "build.buildProject", "0-build", 1, "explorerItemKind == project", "Build"),
            Item(Context, "build.rebuildProject", "0-build", 2, "explorerItemKind == project", "Rebuild"),
            Item(Context, "build.cleanProject", "0-build", 3, "explorerItemKind == project", "Clean"),
            Item(Context, "run.setStartupProject", "4-solution", 0, "explorerItemKind == project"),
        ]);
    }
}
