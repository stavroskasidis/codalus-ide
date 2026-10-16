using Codalus.Shell.ViewModels;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Context;
using Codalus.Workbench.Menus;
using Codalus.Workbench.ToolWindows;

namespace Codalus.Shell;

/// <summary>Avalonia and WPF XAML (M19a, M40b; ADR 0048, 0072): the XAML Previewer's tool window, commands, menus and key binding.</summary>
internal static partial class BuiltIns
{
    public const string XamlKeybindings = """
        [
          { "key": "shift+f7", "command": "xaml.showPreview", "when": "editorLanguageId == axaml || editorLanguageId == xaml" }
        ]
        """;

    public static IEnumerable<ToolWindowDescriptor> XamlToolWindows()
    {
        yield return new ToolWindowDescriptor(XamlPreviewViewModel.ToolWindowId, "XAML Previewer", ToolWindowLocation.Right, ToolWindowKind.BuiltIn, ContributionSource.Shell) { Icon = "window" };
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
            new MenuItemRule(MenuLocations.EditorContext, "xaml.showPreview", shell) { Group = "2-navigation", Order = 20, When = WhenClause.Parse("editorLanguageId == axaml || editorLanguageId == xaml") },
        ]);
    }
}
