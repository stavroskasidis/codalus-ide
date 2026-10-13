using System.Text.Json;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Containers;
using Codalus.Workbench.Context;
using Codalus.Workbench.Menus;
using Codalus.Workbench.Settings;
using Codalus.Workbench.ToolWindows;

namespace Codalus.Shell;

/// <summary>
/// Containers (M17a, ADR 0042): the Containers window, Add Docker Support, compose commands and their settings; and
/// Aspire (M17b, ADR 0043): the Aspire window and the dashboard.
/// </summary>
internal static partial class BuiltIns
{
    private const string ComposeFileWhen = "explorerItemName =~ /(?i)^(docker-)?compose.*\\.ya?ml$/";
    private const string ComposeProjectWhen = "explorerItemName =~ /(?i)\\.dcproj$/";

    public static IReadOnlyList<SettingDescriptor> ContainerSettings() =>
    [
        SettingDescriptor.Create(ContainerEngine.ToolSetting, "\"\"", SettingType.String, ContributionSource.Shell) with
        {
            Title = "Container Engine",
            Category = "Containers",
            Description = "The container CLI that builds, runs and lists containers: docker, podman, or a command line such as 'wsl -d Ubuntu --exec podman'. Empty: docker when it is installed, else podman.",
        },
        SettingDescriptor.Create(ContainerLaunch.FastModeSetting, "true", SettingType.Boolean, ContributionSource.Shell) with
        {
            Title = "Fast Mode",
            Category = "Containers",
            Description = "Run and debug a project in a container of its Dockerfile's base stage with the build output mounted, so a rebuild needs no image build. Off: the whole Dockerfile is built every time, and debugging attaches to the app.",
        },
    ];

    public static IEnumerable<ToolWindowDescriptor> ContainerToolWindows()
    {
        yield return new ToolWindowDescriptor(ContainersViewModel.ToolWindowId, "Containers", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, ContributionSource.Shell) { Icon = "container" };
        yield return new ToolWindowDescriptor(AspireViewModel.ToolWindowId, "Aspire", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, ContributionSource.Shell) { Icon = "cloud" };
    }

    public static void RegisterContainerCommands(ShellHost shell)
    {
        var commands = shell.Workbench.Commands;
        void Add(string id, string title, Func<string?, Task> handler, string? enablement = null) =>
            commands.Register(
                new CommandDescriptor(id, title, ContributionSource.Shell) { Category = "Containers", Enablement = WhenClause.Parse(enablement) },
                async (args, _) =>
                {
                    // An optional first argument: a project, compose file or .dcproj path.
                    var argument = args.Count > 0 && args[0].ValueKind == JsonValueKind.String ? args[0].GetString() : null;
                    await Infrastructure.Ui.RunAsync(() => handler(argument)).ConfigureAwait(false);
                    return null;
                });

        Add("containers.show", "Containers", _ => shell.ShowContainersAsync());
        Add("containers.addDockerSupport", "Add Docker Support", async path => await shell.AddDockerSupportAsync(path).ConfigureAwait(false), "solutionOpen && solutionTrusted");
        Add("containers.composeUp", "Compose Up", async path => await shell.ComposeUpAsync(path).ConfigureAwait(false), "solutionOpen && solutionTrusted");
        Add("containers.composeDown", "Compose Down", async path => await shell.ComposeDownAsync(path).ConfigureAwait(false), "solutionOpen");
        Add("containers.composeDebug", "Debug Compose Service…", async path => await shell.ComposeDebugAsync(path).ConfigureAwait(false), "solutionOpen && solutionTrusted && !debugActive");
        commands.Register(new CommandDescriptor("aspire.show", "Aspire Resources", ContributionSource.Shell) { Category = "Aspire" }, async (_, _) =>
        {
            await Infrastructure.Ui.RunAsync(() => { shell.ShowAspire(); return Task.CompletedTask; }).ConfigureAwait(false);
            return null;
        });
        commands.Register(new CommandDescriptor("aspire.openDashboard", "Open the Aspire Dashboard", ContributionSource.Shell) { Category = "Aspire" }, async (_, _) =>
        {
            await Infrastructure.Ui.RunAsync(() => { shell.OpenAspireDashboard(); return Task.CompletedTask; }).ConfigureAwait(false);
            return null;
        });
        Add("project.publish", "Publish…", path =>
        {
            shell.OpenPublish(path);
            return Task.CompletedTask;
        }, "solutionOpen && solutionTrusted");
        Add("containers.attach", "Attach to a Process in a Container…", path => shell.AttachToRemoteProcessAsync("container"), "!debugActive");
    }

    public static void RegisterContainerMenus(MenuRegistry menus)
    {
        var shell = ContributionSource.Shell;
        MenuItemRule Item(string location, string command, string group, int order = 0, string? when = null, string? title = null) =>
            new(location, command, shell) { Group = group, Order = order, When = WhenClause.Parse(when), Title = title };

        menus.Add(
        [
            Item(SolutionExplorerView.AddMenuLocation, "containers.addDockerSupport", "2-references", 3, "explorerItemKind == project && !(" + ComposeProjectWhen + ")", "Docker Support"),
            Item(SolutionExplorerView.ContextMenuLocation, "containers.composeUp", "3-containers", 2, ComposeFileWhen + " || " + ComposeProjectWhen),
            Item(SolutionExplorerView.ContextMenuLocation, "containers.composeDebug", "3-containers", 3, ComposeFileWhen + " || " + ComposeProjectWhen),
            Item(SolutionExplorerView.ContextMenuLocation, "containers.composeDown", "3-containers", 4, ComposeFileWhen + " || " + ComposeProjectWhen),
            Item(OtherWindowsMenu, "containers.show", "windows", AlphabeticalOrder("Containers")),
            Item(SolutionExplorerView.ContextMenuLocation, "project.publish", "3-containers", 0, "explorerItemKind == project && !(" + ComposeProjectWhen + ")"),
            Item(OtherWindowsMenu, "aspire.show", "windows", AlphabeticalOrder("Aspire Resources")),
        ]);
    }
}
