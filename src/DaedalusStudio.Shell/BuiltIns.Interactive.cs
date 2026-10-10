using System.Text.Json;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Shell.Views;
using DaedalusStudio.Workbench.Commands;
using DaedalusStudio.Workbench.Context;
using DaedalusStudio.Workbench.Menus;
using DaedalusStudio.Workbench.ToolWindows;

namespace DaedalusStudio.Shell;

/// <summary>C# Interactive's tool window, commands, menus and key bindings (ADR 0015).</summary>
internal static partial class BuiltIns
{
    public const string InteractiveKeybindings = """
        [
          { "key": "primary+e primary+e", "command": "interactive.sendSelection", "when": "activeDocumentKind == textFile" }
        ]
        """;

    public static ToolWindowDescriptor InteractiveToolWindow() =>
        new(InteractiveViewModel.ToolWindowId, "C# Interactive", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, ContributionSource.Shell) { Icon = "output" };

    public static void RegisterInteractiveCommands(ShellHost shell)
    {
        var commands = shell.Workbench.Commands;
        void Add(string id, string title, Func<string?, Task> handler, string? enablement = null, bool palette = true) =>
            commands.Register(
                new CommandDescriptor(id, title, ContributionSource.Shell)
                {
                    Category = "C# Interactive",
                    Enablement = WhenClause.Parse(enablement),
                    ShowInPalette = palette,
                },
                async (args, _) =>
                {
                    var argument = args.Count > 0 && args[0].ValueKind == JsonValueKind.String ? args[0].GetString() : null;
                    await Infrastructure.Ui.RunAsync(() => handler(argument)).ConfigureAwait(false);
                    return null;
                });

        Add("interactive.reset", "Reset C# Interactive", _ => shell.ShowInteractive().ResetAsync(null));
        Add("interactive.clear", "Clear C# Interactive", _ =>
        {
            shell.InteractiveWindow.Transcript.Clear();
            return Task.CompletedTask;
        });
        Add("interactive.sendSelection", "Send Selection to Interactive", _ => shell.SendToInteractiveAsync(), "activeDocumentKind == textFile");
        Add("interactive.initializeWithProject", "Initialize Interactive with Project", project => shell.InitializeInteractiveWithProjectAsync(project), "solutionOpen");
    }

    public static void RegisterInteractiveMenus(MenuRegistry menus)
    {
        var shell = ContributionSource.Shell;
        MenuItemRule Item(string location, string command, string group, int order = 0, string? when = null, string? title = null) =>
            new(location, command, shell) { Group = group, Order = order, When = WhenClause.Parse(when), Title = title };

        menus.Add(
        [
            Item(OtherWindowsMenu, "workbench.view.interactive", "windows", AlphabeticalOrder("C# Interactive")),
            Item(SolutionExplorerView.ContextMenuLocation, "interactive.initializeWithProject", "5-path", 4, "explorerItemKind == project"),
            Item(MenuLocations.EditorContext, "interactive.sendSelection", "5-terminal", 2),
        ]);
    }
}
