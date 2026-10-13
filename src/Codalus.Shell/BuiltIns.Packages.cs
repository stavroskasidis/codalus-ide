using System.Text.Json;
using Codalus.Shell.Views;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Context;
using Codalus.Workbench.Menus;
using Codalus.Workbench.Packages;
using Codalus.Workbench.Settings;

namespace Codalus.Shell;

/// <summary>NuGet packages, package sources, .NET tools and Pack (ADR 0012).</summary>
internal static partial class BuiltIns
{
    public static IReadOnlyList<SettingDescriptor> PackageSettings() =>
    [
        SettingDescriptor.Create(PackageService.OfflineSetting, "false", SettingType.Boolean, ContributionSource.Shell) with
        {
            Title = "Offline",
            Category = "NuGet",
            Description = "Use only local package sources (folders) and the global packages folder in the NuGet Package Manager. Restore is not affected.",
        },
    ];

    public static void RegisterPackageCommands(ShellHost shell)
    {
        var commands = shell.Workbench.Commands;
        void Add(string id, string title, Func<string?, Task> handler, string? enablement, bool palette = true) =>
            commands.Register(
                new CommandDescriptor(id, title, ContributionSource.Shell)
                {
                    Category = "NuGet",
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

        const string Trusted = "solutionOpen && solutionTrusted";
        Add("packages.manage", "Manage NuGet Packages…", p =>
        {
            shell.OpenPackageManager(p);
            return Task.CompletedTask;
        }, Trusted);
        Add("packages.manageSolution", "Manage NuGet Packages for Solution…", _ =>
        {
            shell.OpenPackageManager(null, solution: true);
            return Task.CompletedTask;
        }, Trusted);
        Add("packages.sources", "Package Sources…", _ =>
        {
            shell.OpenPackageSources();
            return Task.CompletedTask;
        }, Trusted);
        Add("packages.tools", ".NET Tools…", _ =>
        {
            shell.OpenDotnetTools();
            return Task.CompletedTask;
        }, Trusted);
        Add("project.pack", "Pack", shell.PackAsync, Trusted + " && !buildRunning");
    }

    public static void RegisterPackageMenus(MenuRegistry menus)
    {
        var shell = ContributionSource.Shell;
        MenuItemRule Item(string location, string command, string group, int order = 0, string? when = null, string? title = null) =>
            new(location, command, shell) { Group = group, Order = order, When = WhenClause.Parse(when), Title = title };

        const string Context = SolutionExplorerView.ContextMenuLocation;
        const string ProjectLevel = "explorerItemKind == project || explorerItemKind == dependencies || explorerItemKind == dependencyGroup";
        menus.Add(
        [
            Item(Context, "packages.manage", "2-references", 2, ProjectLevel),
            Item(Context, "packages.manageSolution", "2-references", 2, "explorerItemKind == solution"),
            Item(Context, "project.pack", "0-build", 4, "explorerItemKind == project"),
        ]);
    }
}
