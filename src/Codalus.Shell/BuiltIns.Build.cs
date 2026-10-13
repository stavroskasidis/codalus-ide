using System.Text.Json;
using Codalus.Projects.Protocol;
using Codalus.Shell.Views;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Context;
using Codalus.Workbench.Menus;
using Codalus.Workbench.Settings;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>Build and run commands, menus, key bindings and settings (ADR 0006).</summary>
internal static partial class BuiltIns
{
    public const string BuildKeybindings = """
        [
          { "key": "primary+shift+b", "command": "build.buildSolution" },
          { "key": "ctrl+pause", "command": "build.cancel", "when": "buildRunning" },
          { "key": "ctrl+f5", "mac": "cmd+f5", "command": "run.startWithoutDebugging" },
          { "key": "alt+f10", "linux": "shift+alt+f10", "command": "run.applyCodeChanges", "when": "hotReloadRunning" },
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
            SettingDescriptor.Create(ShellHost.HotReloadSetting, "false", SettingType.Boolean, shell) with
            {
                Title = "Hot Reload on Start Without Debugging",
                Category = "Build and Run",
                Description = "Run under dotnet watch on Ctrl+F5: saved edits are applied to the running app, which restarts when an edit can't be applied.",
                Order = 3,
            },

            // Build depth (M26b, ADR 0034).
            SettingDescriptor.Create(ShellHost.BuildVerbositySetting, "\"minimal\"", SettingType.String, shell) with
            {
                Title = "Build Output Verbosity",
                Category = "Build and Run",
                Description = "How much MSBuild writes to Output → Build.",
                Enum = [BuildVerbosities.Quiet, BuildVerbosities.Minimal, BuildVerbosities.Normal, BuildVerbosities.Detailed, BuildVerbosities.Diagnostic],
                EnumDescriptions = ["Quiet: errors and warnings", "Minimal: also each project's output", "Normal: also the projects, command lines and copies", "Detailed: also the targets and low-importance messages", "Diagnostic: as detailed"],
                Order = 10,
            },
            SettingDescriptor.Create(ShellHost.BuildLogFileVerbositySetting, "\"off\"", SettingType.String, shell) with
            {
                Title = "Build Log File Verbosity",
                Category = "Build and Run",
                Description = "Also write the build's log to build.log in the logs folder (Build: Open Build Log), at this verbosity.",
                Enum = ["off", BuildVerbosities.Quiet, BuildVerbosities.Minimal, BuildVerbosities.Normal, BuildVerbosities.Detailed, BuildVerbosities.Diagnostic],
                EnumDescriptions = ["No log file", "Quiet", "Minimal", "Normal", "Detailed", "Diagnostic"],
                Order = 11,
            },
            SettingDescriptor.Create(ShellHost.MaxParallelProjectsSetting, "0", SettingType.Integer, shell) with
            {
                Title = "Maximum Parallel Project Builds",
                Category = "Build and Run",
                Description = "How many projects MSBuild builds at once (its nodes). 0: one per processor.",
                Order = 12,
            },
            SettingDescriptor.Create(ShellHost.FastUpToDateCheckSetting, "true", SettingType.Boolean, shell) with
            {
                Title = "Fast Up-to-Date Check",
                Category = "Build and Run",
                Description = "Skip MSBuild when nothing a project builds from changed since its last build here (projects can opt out with DisableFastUpToDateCheck).",
                Order = 13,
            },
            SettingDescriptor.Create(ShellHost.BuildBeforeRunSetting, "\"build\"", SettingType.String, shell) with
            {
                Title = "Before Running",
                Category = "Build and Run",
                Description = "What F5 and Ctrl+F5 do before starting a project.",
                Enum = ["build", "prompt", "never"],
                EnumDescriptions = ["Build it (quick when it is up to date)", "Ask when it is out of date", "Never build"],
                Order = 14,
            },
            SettingDescriptor.Create(ShellHost.BinaryLogSetting, "false", SettingType.Boolean, shell) with
            {
                Title = "Write a Binary Log for Every Build",
                Category = "Build and Run",
                Description = "Capture each build in a .binlog in the logs folder (the newest 20 are kept), for the binary log viewer and Build Timings.",
                Order = 15,
            },
            SettingDescriptor.Create(ShellHost.RunAnalyzersDuringBuildSetting, "true", SettingType.Boolean, shell) with
            {
                Title = "Run Code Analysis on Build",
                Category = "Build and Run",
                Description = "Run Roslyn analyzers during builds (RunAnalyzersDuringBuild). Live analysis in the editor isn't affected.",
                Order = 16,
            },
            SettingDescriptor.Create(ShellHost.RunCustomToolsOnSaveSetting, "true", SettingType.Boolean, shell) with
            {
                Title = "Run Custom Tools on Save",
                Category = "Build and Run",
                Description = "When a file with a custom tool (.resx, a text template…) is saved, generate its output.",
                Order = 17,
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
        // Build depth (M26b, ADR 0034, 0035).
        Add("build.batchBuild", "Batch Build…", "Build", _ =>
        {
            shell.OpenBatchBuild();
            return Task.CompletedTask;
        }, Idle);
        Add("build.checkUpToDate", "Check Whether Projects Are Up to Date", "Build", p => shell.CheckUpToDateAsync(p), Idle);
        Add("build.showTimings", "Build Timings", "Build", _ =>
        {
            shell.OpenBuildTimings();
            return Task.CompletedTask;
        }, "solutionOpen");
        Add("build.buildWithBinaryLog", "Build Solution with Binary Log", "Build", _ => shell.BuildWithBinaryLogAsync(), Idle);
        Add("build.openBinaryLog", "Open Binary Log…", "Build", shell.OpenBinaryLogAsync, "solutionOpen");
        Add("build.openBuildLog", "Open Build Log", "Build", async _ =>
        {
            if (File.Exists(shell.BuildLogFile))
            {
                await shell.OpenFileAsync(shell.BuildLogFile).ConfigureAwait(true);
            }
            else
            {
                shell.StatusBar.ShowMessage(Localizer.Get("msg.builtInsBuild.thereIsNoBuildLogSet", "There is no build log: set Build Log File Verbosity to write one."), TimeSpan.FromSeconds(6));
            }
        });
        Add("project.runCustomTool", "Run Custom Tool", "Project", p => shell.RunCustomToolAsync(p), "solutionOpen");
        Add("run.startWithoutDebugging", "Start Without Debugging", "Debug", p => shell.StartWithoutDebuggingAsync(p), Idle);
        // From .NET 6 (M33): for an older startup project the command is disabled, and the menu says why.
        Add("run.startWithHotReload", "Start with Hot Reload", "Debug", shell.StartWithHotReloadAsync, Idle + " && hotReloadSupported");
        Add("run.applyCodeChanges", "Apply Code Changes (Hot Reload)", "Debug", _ => shell.ApplyCodeChangesAsync(), "hotReloadRunning");
        Add("run.stop", "Stop Running Programs", "Debug", _ =>
        {
            shell.StopRunning();
            return Task.CompletedTask;
        }, "programRunning");
        Add("run.selectStartupProject", "Select Startup Project…", "Debug", _ => shell.SelectStartupProjectAsync(), "solutionOpen");
        Add("run.selectLaunchProfile", "Select Launch Profile…", "Debug", shell.SelectLaunchProfileAsync, "hasStartupProject");
        Add("run.configureStartupProjects", "Configure Startup Projects…", "Debug", _ =>
        {
            shell.OpenStartupProjects();
            return Task.CompletedTask;
        }, "solutionOpen");
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
        var shell = ContributionSource.Shell;
        MenuItemRule Item(string location, string command, string group, int order = 0, string? when = null, string? title = null) =>
            new(location, command, shell) { Group = group, Order = order, When = WhenClause.Parse(when), Title = title };

        const string Context = SolutionExplorerView.ContextMenuLocation;
        menus.Add(
        [
            // The menu bar is BuiltIns.Menus.cs.
            Item(Context, "build.buildSolution", "0-build", 1, "explorerItemKind == solution", "Build Solution"),
            Item(Context, "build.rebuildSolution", "0-build", 2, "explorerItemKind == solution", "Rebuild Solution"),
            Item(Context, "build.cleanSolution", "0-build", 3, "explorerItemKind == solution", "Clean Solution"),
            Item(Context, "build.buildProject", "0-build", 1, "explorerItemKind == project", "Build"),
            Item(Context, "build.rebuildProject", "0-build", 2, "explorerItemKind == project", "Rebuild"),
            Item(Context, "build.cleanProject", "0-build", 3, "explorerItemKind == project", "Clean"),
            Item(Context, "run.setStartupProject", "4-solution", 0, "explorerItemKind == project"),
            Item(Context, "run.configureStartupProjects", "4-solution", 1, "explorerItemKind == solution"),
            Item(Context, "project.runCustomTool", "1-open", 9, "explorerItemInProject && explorerItemName =~ /(?i)\\.(resx|tt)$/"),
        ]);
    }
}
