using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Commands;
using DaedalusStudio.Workbench.Context;
using DaedalusStudio.Workbench.Menus;
using DaedalusStudio.Workbench.ToolWindows;

namespace DaedalusStudio.Shell;

/// <summary>Avalonia XAML (M19a, ADR 0046 and 0047): the XAML Previewer's tool window, commands, menus and key binding.</summary>
internal static partial class BuiltIns
{
    public const string XamlKeybindings = """
        [
          { "key": "shift+f7", "command": "xaml.showPreview", "when": "editorLanguageId == axaml" }
        ]
        """;

    public static IEnumerable<ToolWindowDescriptor> XamlToolWindows()
    {
        yield return new ToolWindowDescriptor(XamlPreviewViewModel.ToolWindowId, "XAML Previewer", ToolWindowLocation.Right, ToolWindowKind.BuiltIn, ContributionSource.Shell) { Icon = "layout" };
    }

    public static void RegisterXamlCommands(ShellHost shell)
    {
        var commands = shell.Workbench.Commands;
        void Add(string id, string title, Func<Task> handler, string? enablement = null) =>
            commands.Register(
                new CommandDescriptor(id, title, ContributionSource.Shell) { Category = "XAML", Enablement = WhenClause.Parse(enablement) },
                async (_, _) =>
                {
                    await Infrastructure.Ui.RunAsync(handler).ConfigureAwait(false);
                    return null;
                });

        Add("xaml.showPreview", "XAML Previewer", () =>
        {
            shell.ShowXamlPreview();
            return Task.CompletedTask;
        });
        Add("xaml.refreshPreview", "Refresh the XAML Preview", () => shell.RefreshPreviewAsync(rebuild: true));
    }

    public static void RegisterXamlMenus(MenuRegistry menus)
    {
        var shell = ContributionSource.Shell;
        menus.Add(
        [
            new MenuItemRule(OtherWindowsMenu, "xaml.showPreview", shell) { Group = "windows", Order = AlphabeticalOrder("XAML Previewer") },
            new MenuItemRule(MenuLocations.EditorContext, "xaml.showPreview", shell) { Group = "1-navigation", Order = 20, When = WhenClause.Parse("editorLanguageId == axaml") },
        ]);
    }
}
