using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Context;
using Codalus.Workbench.Menus;
using Codalus.Workbench.Settings;
using Codalus.Workbench.ToolWindows;

namespace Codalus.Shell;

/// <summary>
/// The Windows Forms designer (M41a and M41b, ADR 0073): the Toolbox, View Code and View Designer, their keys and menus,
/// the setting that opens forms in the designer, the Format menu (Align, Make Same Size, spacing, Center in Form,
/// Order), Tab Order, the smart tag, and the surface's context menu.
/// </summary>
internal static partial class BuiltIns
{
    /// <summary>The designer surface's context menu (M41b).</summary>
    public const string WinFormsSurfaceMenu = "winforms/surface";

    private const string InDesigner = "activeDocumentKind == winformsDesigner";

    /// <summary>Visual Studio's keys: F7 View Code, Shift+F7 View Designer (in a form's code; the XAML Previewer keeps Shift+F7 in XAML), Ctrl+Alt+X the Toolbox.</summary>
    public const string WinFormsKeybindings = """
        [
          { "key": "f7", "command": "winforms.viewCode", "when": "activeDocumentKind == winformsDesigner" },
          { "key": "shift+f7", "command": "winforms.viewDesigner", "when": "editorHasWinFormsDesigner || (focusedView == solutionExplorer && explorerItemHasWinFormsDesigner)" },
          { "key": "ctrl+alt+x", "command": "workbench.view.toolbox" },
          { "key": "shift+alt+f10", "command": "winforms.smartTag", "when": "activeDocumentKind == winformsDesigner" }
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

        // M41b: the Format menu, Tab Order and the smart tag, on the active designer.
        void Designer(string id, string title, Func<WinFormsDesignerDocument, Task> handler) =>
            Add(id, title, () => shell.Layout.ActiveDocument is WinFormsDesignerDocument designer ? handler(designer) : Task.CompletedTask, InDesigner);
        Designer("winforms.tabOrder", "Tab Order", d =>
        {
            d.IsTabOrderMode = !d.IsTabOrderMode;
            return Task.CompletedTask;
        });
        Designer("winforms.smartTag", "Show Smart Tag", d => d.PrimarySelection is { } selected ? d.OpenActionsAsync(selected) : Task.CompletedTask);
        foreach (var (id, title, alignment) in s_alignments)
        {
            Designer("winforms.align." + id, "Align " + title, d => d.AlignAsync(alignment));
        }

        foreach (var (id, title, sizing) in s_sizings)
        {
            Designer("winforms.sameSize." + id, sizing == DesignerSizing.ToGrid ? "Size to Grid" : "Make Same " + title, d => d.MakeSameSizeAsync(sizing));
        }

        foreach (var horizontal in new[] { true, false })
        {
            var axis = horizontal ? "Horizontal" : "Vertical";
            foreach (var (id, title, spacing) in s_spacings)
            {
                Designer($"winforms.{(horizontal ? "h" : "v")}spacing.{id}", $"{axis} Spacing: {title}", d => d.SpacingAsync(horizontal, spacing));
            }
        }

        Designer("winforms.center.horizontally", "Center in Form Horizontally", d => d.CenterAsync(horizontal: true));
        Designer("winforms.center.vertically", "Center in Form Vertically", d => d.CenterAsync(horizontal: false));
        Designer("winforms.order.front", "Bring to Front", d => d.OrderAsync(front: true));
        Designer("winforms.order.back", "Send to Back", d => d.OrderAsync(front: false));
    }

    private static readonly (string Id, string Title, DesignerAlignment Alignment)[] s_alignments =
    [
        ("lefts", "Lefts", DesignerAlignment.Lefts), ("centers", "Centers", DesignerAlignment.Centers), ("rights", "Rights", DesignerAlignment.Rights),
        ("tops", "Tops", DesignerAlignment.Tops), ("middles", "Middles", DesignerAlignment.Middles), ("bottoms", "Bottoms", DesignerAlignment.Bottoms),
        ("toGrid", "to Grid", DesignerAlignment.ToGrid),
    ];

    private static readonly (string Id, string Title, DesignerSizing Sizing)[] s_sizings =
    [
        ("width", "Width", DesignerSizing.Width), ("height", "Height", DesignerSizing.Height), ("both", "Size", DesignerSizing.Both), ("toGrid", "Size to Grid", DesignerSizing.ToGrid),
    ];

    private static readonly (string Id, string Title, DesignerSpacing Spacing)[] s_spacings =
    [
        ("equal", "Make Equal", DesignerSpacing.MakeEqual), ("increase", "Increase", DesignerSpacing.Increase), ("decrease", "Decrease", DesignerSpacing.Decrease), ("remove", "Remove", DesignerSpacing.Remove),
    ];

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
            new MenuItemRule(MenuLocations.MenuBar("view"), "winforms.tabOrder", shell) { Group = "0-code", Order = 2, When = WhenClause.Parse(InDesigner) },
        ]);

        // The Format menu, as Visual Studio's designer has it (shown while a designer is the active document).
        menus.AddMenuBarMenu(new MenuBarMenu("format", "F_ormat", 62));
        var format = MenuLocations.MenuBar("format");
        var order = 0;
        var when = WhenClause.Parse(InDesigner);
        MenuItemRule Sub(string location, string submenu, string title, string group) =>
            MenuItemRule.ForSubmenu(location, submenu, title, shell) with { Group = group, Order = ++order, When = when };
        MenuItemRule Item(string location, string command, string title, string group = "default") =>
            new(location, command, shell) { Group = group, Order = ++order, Title = title, When = when };
        string Align = format + "/align", Size = format + "/sameSize", Horizontal = format + "/hspacing", Vertical = format + "/vspacing", Center = format + "/center", Order = format + "/order";
        var rules = new List<MenuItemRule>
        {
            Sub(format, Align, "_Align", "1-layout"),
            Sub(format, Size, "_Make Same Size", "1-layout"),
            Sub(format, Horizontal, "_Horizontal Spacing", "2-spacing"),
            Sub(format, Vertical, "_Vertical Spacing", "2-spacing"),
            Sub(format, Center, "_Center in Form", "3-center"),
            Sub(format, Order, "_Order", "4-order"),
            Item(Center, "winforms.center.horizontally", "Horizontally"),
            Item(Center, "winforms.center.vertically", "Vertically"),
            Item(Order, "winforms.order.front", "Bring to Front"),
            Item(Order, "winforms.order.back", "Send to Back"),
        };
        rules.AddRange(s_alignments.Select(a => Item(Align, "winforms.align." + a.Id, a.Title, a.Alignment == DesignerAlignment.ToGrid ? "2-grid" : a.Alignment <= DesignerAlignment.Rights ? "0-x" : "1-y")));
        rules.AddRange(s_sizings.Select(a => Item(Size, "winforms.sameSize." + a.Id, a.Title, a.Sizing == DesignerSizing.ToGrid ? "1-grid" : "0-same")));
        rules.AddRange(s_spacings.Select(a => Item(Horizontal, "winforms.hspacing." + a.Id, a.Title)));
        rules.AddRange(s_spacings.Select(a => Item(Vertical, "winforms.vspacing." + a.Id, a.Title)));

        // The surface's context menu.
        rules.Add(Item(WinFormsSurfaceMenu, "winforms.smartTag", "Show Smart Tag", "0-tasks"));
        rules.Add(Item(WinFormsSurfaceMenu, "winforms.order.front", "Bring to Front", "1-order"));
        rules.Add(Item(WinFormsSurfaceMenu, "winforms.order.back", "Send to Back", "1-order"));
        rules.Add(Sub(WinFormsSurfaceMenu, Align, "_Align", "2-layout"));
        rules.Add(Sub(WinFormsSurfaceMenu, Size, "_Make Same Size", "2-layout"));
        rules.Add(Item(WinFormsSurfaceMenu, "winforms.tabOrder", "Tab Order", "3-view"));
        rules.Add(Item(WinFormsSurfaceMenu, "winforms.viewCode", "View Code", "3-view"));
        menus.Add(rules);
    }
}
