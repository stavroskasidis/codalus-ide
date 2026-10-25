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
/// Order, Lock Controls), Tab Order, the smart tag, and the context menus of the surface and the component tray as the
/// classic IDE has them (#67), with its command set's enablement.
/// </summary>
internal static partial class BuiltIns
{
    /// <summary>The designer surface's context menu (M41b; the classic IDE's since #67).</summary>
    public const string WinFormsSurfaceMenu = "winforms/surface";

    /// <summary>A component tray item's context menu (#67).</summary>
    public const string WinFormsTrayMenu = "winforms/tray";

    /// <summary>The component tray's own context menu, on its background (#67).</summary>
    public const string WinFormsTrayBackgroundMenu = "winforms/trayBackground";

    private const string InDesigner = "activeDocumentKind == winformsDesigner";

    /// <summary>The classic IDE's keys: F7 View Code, Shift+F7 View Designer (in a form's code; the XAML Previewer keeps Shift+F7 in XAML), Ctrl+Alt+X the Toolbox.</summary>
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

        // M41b: the Format menu, Tab Order and the smart tag, on the active designer; #67: enabled as the classic IDE's
        // command set enables them (the context keys of WinFormsDesignerDocument.PublishContext).
        void Designer(string id, string title, Func<WinFormsDesignerDocument, Task> handler, string? enablement = null) =>
            Add(id, title, () => shell.Layout.ActiveDocument is WinFormsDesignerDocument designer ? handler(designer) : Task.CompletedTask, enablement is null ? InDesigner : $"{InDesigner} && {enablement}");
        Designer("winforms.tabOrder", "Tab Order", d =>
        {
            d.IsTabOrderMode = !d.IsTabOrderMode;
            return Task.CompletedTask;
        });
        Designer("winforms.smartTag", "Show Smart Tag", d => d.PrimarySelection is { } selected ? d.OpenActionsAsync(selected) : Task.CompletedTask, "winFormsSelectionHasActions");

        // The classic IDE's enablement: Lefts…Bottoms and Make Same Size need two or more controls and a primary one
        // (OnStatusMultiSelectPrimary); to Grid, Size to Grid and Center in Form any control (OnStatusControlsOnlySelection;
        // Align and Size to Grid stay enabled without a SnapToGrid layout mode, which Codalus doesn't have); spacing two or
        // more with one parent; Order controls when the form has two components or more. A selection with the form in it
        // counts as empty, as there.
        foreach (var (id, title, alignment) in s_alignments)
        {
            Designer("winforms.align." + id, "Align " + title, d => d.AlignAsync(alignment), alignment == DesignerAlignment.ToGrid ? "winFormsCanFormat" : "winFormsCanAlign");
        }

        foreach (var (id, title, sizing) in s_sizings)
        {
            Designer("winforms.sameSize." + id, sizing == DesignerSizing.ToGrid ? "Size to Grid" : "Make Same " + title, d => d.MakeSameSizeAsync(sizing), sizing == DesignerSizing.ToGrid ? "winFormsCanFormat" : "winFormsCanAlign");
        }

        foreach (var horizontal in new[] { true, false })
        {
            var axis = horizontal ? "Horizontal" : "Vertical";
            foreach (var (id, title, spacing) in s_spacings)
            {
                Designer($"winforms.{(horizontal ? "h" : "v")}spacing.{id}", $"{axis} Spacing: {title}", d => d.SpacingAsync(horizontal, spacing), "winFormsCanSpace");
            }
        }

        Designer("winforms.center.horizontally", "Center in Form Horizontally", d => d.CenterAsync(horizontal: true), "winFormsCanFormat");
        Designer("winforms.center.vertically", "Center in Form Vertically", d => d.CenterAsync(horizontal: false), "winFormsCanFormat");
        Designer("winforms.order.front", "Bring to Front", d => d.OrderAsync(front: true), "winFormsCanOrder");
        Designer("winforms.order.back", "Send to Back", d => d.OrderAsync(front: false), "winFormsCanOrder");

        // #67: Lock Controls (controls only, OnStatusLockControls), and the context menus' commands, on the designer's
        // selection whatever has the keyboard focus (a tray item's menu, the smoke test). The surface keeps its own keys.
        Designer("winforms.lockControls", "Lock Controls", d => d.LockControlsAsync(), "winFormsControlsOnly");
        Designer("winforms.cut", "Cut", d => d.CutAsync(), "winFormsCanCopy && winFormsCanDelete");
        Designer("winforms.copy", "Copy", d =>
        {
            d.Copy();
            return Task.CompletedTask;
        }, "winFormsCanCopy");
        Designer("winforms.paste", "Paste", d => d.PasteAsync(), "winFormsCanPaste");
        Designer("winforms.delete", "Delete", d => d.DeleteSelectionAsync(), "winFormsCanDelete");
        Designer("winforms.selectParent", "Select Parent", d =>
        {
            d.SelectParent();
            return Task.CompletedTask;
        }, "winFormsSelectionHasParent");
        Designer("winforms.lineUpIcons", "Line Up Icons", d => d.LineUpTrayAsync());
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

        // The Format menu, as the classic IDE's designer has it (shown while a designer is the active document).
        menus.AddMenuBarMenu(new MenuBarMenu("format", "F_ormat", 62));
        var format = MenuLocations.MenuBar("format");
        var order = 0;
        var when = WhenClause.Parse(InDesigner);
        MenuItemRule Sub(string location, string submenu, string title, string group) =>
            MenuItemRule.ForSubmenu(location, submenu, title, shell) with { Group = group, Order = ++order, When = when };
        // A title only where the entry's differs from its command's ("Lefts" in Align): a rule's title wins over the
        // command's localized one, so the others take the command's (#67).
        MenuItemRule Item(string location, string command, string? title = null, string group = "default") =>
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

            // At the bottom, a check entry, as in the classic IDE.
            Item(format, "winforms.lockControls", group: "5-lock") with { Checked = WhenClause.Parse("winFormsControlsLocked") },
            Item(Center, "winforms.center.horizontally", "Horizontally"),
            Item(Center, "winforms.center.vertically", "Vertically"),
            Item(Order, "winforms.order.front"),
            Item(Order, "winforms.order.back"),
        };
        rules.AddRange(s_alignments.Select(a => Item(Align, "winforms.align." + a.Id, a.Title, a.Alignment == DesignerAlignment.ToGrid ? "2-grid" : a.Alignment <= DesignerAlignment.Rights ? "0-x" : "1-y")));
        rules.AddRange(s_sizings.Select(a => Item(Size, "winforms.sameSize." + a.Id, a.Title, a.Sizing == DesignerSizing.ToGrid ? "1-grid" : "0-same")));
        rules.AddRange(s_spacings.Select(a => Item(Horizontal, "winforms.hspacing." + a.Id, a.Title)));
        rules.AddRange(s_spacings.Select(a => Item(Vertical, "winforms.vspacing." + a.Id, a.Title)));

        // The surface's context menu, the classic IDE's (#67, D40): the component's verbs (added as it opens, in the first
        // group with Show Smart Tag, the one entry the classic menu doesn't have), View Code, Bring to Front, Send to Back,
        // Align to Grid, Lock Controls, Select 'parent', the clipboard and Properties, grayed when they don't apply. Align,
        // Make Same Size and Tab Order are in Format and View only, as there. A tool strip item gets it too.
        // TODO(issue): a tool strip item's own classic menu (Convert To, Insert, Set Image…, Edit DropDownItems…).
        MenuItemRule Clipboard(string location, string command, string keys) => Item(location, command, group: "6-clipboard") with { Keys = keys };
        var hasActions = WhenClause.Parse(InDesigner + " && winFormsSelectionHasActions");
        foreach (var location in new[] { WinFormsSurfaceMenu, WinFormsTrayMenu })
        {
            rules.Add(Item(location, "winforms.smartTag", group: "0-verbs") with { When = hasActions });
            rules.Add(Item(location, "winforms.viewCode", group: "1-code"));
            if (location == WinFormsSurfaceMenu)
            {
                rules.Add(Item(location, "winforms.order.front", group: "2-order"));
                rules.Add(Item(location, "winforms.order.back", group: "2-order"));
                rules.Add(Item(location, "winforms.align.toGrid", group: "3-grid"));
                rules.Add(Item(location, "winforms.lockControls", group: "4-lock") with { Checked = WhenClause.Parse("winFormsControlsLocked") });

                // Titled Select '…' as the menu opens (MenuBuilder's titleFor).
                rules.Add(Item(location, "winforms.selectParent", group: "5-select") with { When = WhenClause.Parse(InDesigner + " && winFormsSelectionHasParent") });
            }

            rules.Add(Clipboard(location, "winforms.cut", "primary+x"));
            rules.Add(Clipboard(location, "winforms.copy", "primary+c"));
            rules.Add(Clipboard(location, "winforms.paste", "primary+v"));
            rules.Add(Clipboard(location, "winforms.delete", "delete"));
            if (location == WinFormsTrayMenu)
            {
                // The tray keeps Rename (F2, added as it opens) before Line Up Icons; Lock Controls isn't in its menu.
                rules.Add(Item(location, "winforms.lineUpIcons", group: "7-tray"));
            }

            rules.Add(Item(location, "workbench.view.properties", group: "8-properties"));
        }

        // The tray's background: Paste, then Line Up Icons, in the item menu's group order.
        rules.Add(Clipboard(WinFormsTrayBackgroundMenu, "winforms.paste", "primary+v"));
        rules.Add(Item(WinFormsTrayBackgroundMenu, "winforms.lineUpIcons", group: "7-tray"));
        menus.Add(rules);
    }
}
