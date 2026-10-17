using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Context;
using Codalus.Workbench.Menus;
using Codalus.Workbench.Settings;
using Codalus.Workbench.ToolWindows;

namespace Codalus.Shell;

/// <summary>The Windows Forms designer (M41a, ADR 0073): the Toolbox, View Code and View Designer, their keys and menus, and the setting that opens forms in the designer.</summary>
internal static partial class BuiltIns
{
    /// <summary>Visual Studio's keys: F7 View Code, Shift+F7 View Designer (in a form's code; the XAML Previewer keeps Shift+F7 in XAML), Ctrl+Alt+X the Toolbox.</summary>
    public const string WinFormsKeybindings = """
        [
          { "key": "f7", "command": "winforms.viewCode", "when": "activeDocumentKind == winformsDesigner" },
          { "key": "shift+f7", "command": "winforms.viewDesigner", "when": "editorHasWinFormsDesigner || (focusedView == solutionExplorer && explorerItemHasWinFormsDesigner)" },
          { "key": "ctrl+alt+x", "command": "workbench.view.toolbox" }
        ]
        """;

    public static IReadOnlyList<SettingDescriptor> WinFormsSettings() =>
    [
        SettingDescriptor.Create(ShellHost.OpenFormsInDesignerSetting, "true", SettingType.Boolean, ContributionSource.Shell) with
        {
            Title = "Open Forms in the Designer",
            Category = "Windows Forms",
            Description = "A form's or user control's code file opens in the Windows Forms designer (on Windows); View Code (F7) shows the code. Off, it opens as code and View Designer (Shift+F7) shows the designer.",
        },
    ];

    public static IEnumerable<ToolWindowDescriptor> WinFormsToolWindows()
    {
        yield return new ToolWindowDescriptor(ToolboxViewModel.ToolWindowId, "Toolbox", ToolWindowLocation.Left, ToolWindowKind.BuiltIn, ContributionSource.Shell) { Icon = "toolbox" };
    }

    public static void RegisterWinFormsCommands(ShellHost shell)
    {
        var commands = shell.Workbench.Commands;
        void Add(string id, string title, Func<Task> handler, string? enablement = null) =>
            commands.Register(
                new CommandDescriptor(id, title, ContributionSource.Shell) { Category = "Windows Forms", Enablement = WhenClause.Parse(enablement) },
                async (_, _) =>
                {
                    await Infrastructure.Ui.RunAsync(handler).ConfigureAwait(false);
                    return null;
                });

        Add("winforms.viewCode", "View Code", () => shell.ViewWinFormsCodeAsync(), "activeDocumentKind == winformsDesigner");
        Add("winforms.viewDesigner", "View Designer", shell.ViewWinFormsDesignerAsync);
    }

    public static void RegisterWinFormsMenus(MenuRegistry menus)
    {
        var shell = ContributionSource.Shell;
        menus.Add(
        [
            new MenuItemRule(MenuLocations.MenuBar("view"), "winforms.viewCode", shell) { Group = "0-code", Order = 0, When = WhenClause.Parse("activeDocumentKind == winformsDesigner") },
            new MenuItemRule(MenuLocations.MenuBar("view"), "winforms.viewDesigner", shell) { Group = "0-code", Order = 1, When = WhenClause.Parse("editorHasWinFormsDesigner") },
            new MenuItemRule(MenuLocations.EditorContext, "winforms.viewDesigner", shell) { Group = "2-navigation", Order = 21, When = WhenClause.Parse("editorHasWinFormsDesigner") },
            new MenuItemRule(SolutionExplorerView.ContextMenuLocation, "winforms.viewDesigner", shell) { Group = "0-open", Order = 5, When = WhenClause.Parse("explorerItemHasWinFormsDesigner") },
            new MenuItemRule(MenuLocations.DocumentTabContext, "winforms.viewCode", shell) { Group = "3-path", Order = 0, When = WhenClause.Parse("activeDocumentKind == winformsDesigner") },
        ]);
    }
}
