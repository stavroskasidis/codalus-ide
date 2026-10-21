using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Codalus.Platform;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.WinForms;
using static Codalus.Shell.Tests.WinFormsDesignerTestHost;

namespace Codalus.Shell.Tests;

/// <summary>
/// The Windows Forms designer's menus and tool strips on the surface (#67) in the headless shell with a fake designer
/// host (any OS), on samples/WindowsDesktop's Windows Forms app: which components show a strip's Type Here, and the
/// classic IDE's commands (#67, D40): Edit's on a focused designer, Select All, paste into the nearest container, the
/// context menus of the surface and the tray with their verbs and enablement, the Format menu's enablement, and Lock
/// Controls as the default .resx's Locked metadata.
/// </summary>
public sealed class WinFormsDesignerMenuShellTests
{
    private const string Forms = "System.Windows.Forms.";

    /// <summary>
    /// A list box, combo box, domain up-down or list view has an <c>Items</c> collection too, but its items are a list's,
    /// not a strip's: selecting one showed a menu's Type Here box over it, which took the control's clicks, and typing in
    /// it replaced the list's items with the typed text (#67). Only menu, tool, status and context menu strips take one.
    /// </summary>
    [AvaloniaFact]
    public async Task Type_Here_shows_on_tool_strips_only_not_on_list_and_combo_boxes()
    {
        var (f, app, hosts) = await OpenAsync();
        await using var _ = f;
        var designer = await DesignAsync(f, Path.Combine(app, "OrderForm.cs"));
        var host = Assert.Single(hosts);
        var original = Code(designer);

        // OrderForm's list box (in the split container's first panel) and combo box: no Type Here, no drop-down, and a
        // click where the box was keeps the list selected.
        foreach (var name in new[] { "historyListBox", "productComboBox" })
        {
            designer.SetSelection([name], notify: true);
            var view = designer.Find(name)!;
            Assert.Contains(view.Collections!, c => c.EndsWith(":Items", StringComparison.Ordinal));
            Assert.True(designer.StripTypeHere is null, $"{name} shows Type Here at {designer.StripTypeHere?.Bounds}");
            Assert.Empty(designer.MenuPanels);
            Assert.Equal((null, null), designer.MenuHitTest(view.X + 6, view.Y + 6));
            designer.Click(view.X + 6, view.Y + 6, toggle: false);
            Assert.Equal([name], designer.Selection);
        }

        // A box left over from before can't write a list's items: nothing reaches the host, the items stay.
        await designer.AddItemAsync(new DesignerTypeHere("historyListBox", "Items", ["System.String"], new DesignerRect(0, 0, 96, 16), CanType: true), "fooString");
        await designer.AddItemAsync(new DesignerTypeHere("productComboBox", "Items", ["System.String"], new DesignerRect(0, 0, 96, 16), CanType: true), "fooString");
        Assert.Equal(0, host.SetCollectionCalls);
        Assert.Equal(original, Code(designer));
        Assert.Contains("productComboBox.Items.AddRange(new object[] { \"Coffee\", \"Tea\", \"Hot chocolate\" });", Code(designer), StringComparison.Ordinal);

        // A domain up-down from the Toolbox: no Type Here either; the code only gains the new control.
        var root = designer.Root!;
        await designer.CreateAsync(Forms + "DomainUpDown", root.ClientX + 300, root.ClientY + 340);
        var upDown = designer.Find(Assert.Single(designer.Selection))!;
        Assert.Equal(Forms + "DomainUpDown", upDown.Type);
        Assert.Equal([Forms + "DomainUpDown:Items"], upDown.Collections!);
        Assert.Null(designer.StripTypeHere);
        Assert.Empty(designer.MenuPanels);
        Assert.Equal(0, host.SetCollectionCalls);
        Assert.Contains(upDown.Name + " = new DomainUpDown();", Code(designer), StringComparison.Ordinal);
        Assert.DoesNotContain(upDown.Name + ".Items", Code(designer), StringComparison.Ordinal);

        // InventoryForm: its list box and list view (Columns and Items, as the real host reports them) have none.
        var inventory = await DesignAsync(f, Path.Combine(app, "InventoryForm.cs"));
        var inventoryHost = hosts[^1];
        foreach (var name in new[] { "categoryListBox", "itemsListView" })
        {
            inventory.SetSelection([name], notify: true);
            Assert.True(inventory.StripTypeHere is null, $"{name} shows Type Here at {inventory.StripTypeHere?.Bounds}");
            Assert.Empty(inventory.MenuPanels);
        }

        Assert.Equal([Forms + "ListView:Columns", Forms + "ListView:Items"], inventory.Find("itemsListView")!.Collections!);

        // The strips keep theirs: a menu strip's types the item's text; a tool strip's and a status strip's pick a type.
        inventory.SetSelection(["mainMenuStrip"], notify: true);
        Assert.True(inventory.StripTypeHere is { CanType: true, Property: "Items" });
        Assert.Equal(Forms + "ToolStripMenuItem", inventory.StripTypeHere.Types[0]);
        inventory.SetSelection(["mainToolStrip"], notify: true);
        Assert.True(inventory.StripTypeHere is { CanType: false, Property: "Items" });
        Assert.Contains(Forms + "ToolStripButton", inventory.StripTypeHere.Types);
        inventory.SetSelection(["mainStatusStrip"], notify: true);
        Assert.True(inventory.StripTypeHere is { CanType: false });
        Assert.Equal(Forms + "ToolStripStatusLabel", inventory.StripTypeHere.Types[0]);

        // A context menu (in the tray) opens as a panel with its name and its own Type Here.
        inventory.SetSelection(["itemsContextMenuStrip"], notify: true);
        var panel = Assert.Single(inventory.MenuPanels);
        Assert.Equal(("itemsContextMenuStrip", "itemsContextMenuStrip"), (panel.Owner, panel.Header));
        Assert.Equal(["copyToolStripMenuItem"], panel.Rows.Select(r => r.Item));
        Assert.True(panel.TypeHere.CanType);
        Assert.Equal(0, inventoryHost.SetCollectionCalls);
    }

    /// <summary>
    /// Edit → Copy, Paste, Cut, Undo and Select All act on a focused designer, as in the classic IDE (#67): before, they
    /// looked for a text box or an editor and did nothing. Paste goes into the nearest container of the primary selection
    /// (the classic IDE's nearest drag client): a selected panel itself, a control's group box, a split container's
    /// panel; Select All takes every component but the form and the split container's panels, the tray's too.
    /// </summary>
    [AvaloniaFact]
    public async Task Edit_menu_commands_act_on_a_focused_designer_and_paste_goes_into_the_nearest_container()
    {
        var (f, app, hosts) = await OpenAsync();
        await using var _ = f;
        var designer = await DesignAsync(f, Path.Combine(app, "OrderForm.cs"));
        var host = Assert.Single(hosts);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<Views.WinFormsDesignerView>().Any(v => v.IsEffectivelyVisible));
        var view = f.Window.GetVisualDescendants().OfType<Views.WinFormsDesignerView>().First(v => v.IsEffectivelyVisible);
        view.FocusPrimary();
        await ShellFixture.PumpUntil(() => view.Surface.IsFocused);

        // The split container's first panel holds the list box, as the real host reports it.
        Assert.Equal("notesSplit.Panel1", designer.Find("historyListBox")!.Parent);
        Assert.True(designer.Find("notesSplit.Panel1") is { IsPart: true, IsContainer: true });

        async Task PasteIntoAsync(string selected, string expected)
        {
            designer.SetSelection(["customerTextBox"], notify: true);
            await f.ExecuteAsync("edit.copy");
            designer.SetSelection([selected], notify: true);
            var loads = host.Loads;
            await f.ExecuteAsync("edit.paste");
            await ShellFixture.PumpUntil(() => Code(designer).Contains(expected, StringComparison.Ordinal) && host.Loads > loads, seconds: 30);
        }

        await PasteIntoAsync("buttonPanel", "buttonPanel.Controls.Add(customerTextBox1);");
        await PasteIntoAsync("standardRadioButton", "shippingGroupBox.Controls.Add(customerTextBox2);");
        await PasteIntoAsync("historyListBox", "notesSplit.Panel1.Controls.Add(customerTextBox3);");
        await PasteIntoAsync("orderToolTip", "            Controls.Add(customerTextBox4);");

        // Cut, then Undo, in the designer.
        designer.SetSelection(["giftCheckBox"], notify: true);
        var before = Code(designer);
        await f.ExecuteAsync("edit.cut");
        await ShellFixture.PumpUntil(() => !Code(designer).Contains("giftCheckBox", StringComparison.Ordinal), seconds: 30);
        await f.ExecuteAsync("edit.undo");
        await ShellFixture.PumpUntil(() => Code(designer) == before, seconds: 30);

        // Select All: every component of the form but the form itself and the panels, the tray's and the nested ones too.
        await ShellFixture.PumpUntil(() => designer.Find("giftCheckBox") is not null, seconds: 30);
        await f.ExecuteAsync("edit.selectAll");
        Assert.Contains("orderToolTip", designer.Selection);
        Assert.Contains("standardRadioButton", designer.Selection);
        Assert.Contains("historyListBox", designer.Selection);
        Assert.Contains("customerTextBox3", designer.Selection);
        Assert.DoesNotContain(designer.Root!.Name, designer.Selection);
        Assert.DoesNotContain("notesSplit.Panel1", designer.Selection);
        Assert.Equal(designer.Components.Count(c => c != designer.Root && !c.IsPart), designer.Selection.Count);
    }

    private static Views.WinFormsDesignerView ViewOf(ShellFixture f) =>
        f.Window.GetVisualDescendants().OfType<Views.WinFormsDesignerView>().First(v => v.IsEffectivelyVisible);

    /// <summary>A menu's entries by their names, "-" for a separator.</summary>
    private static List<string> Entries(ContextMenu menu) =>
        [.. menu.Items.Select(i => i is MenuItem item ? AutomationProperties.GetName(item) ?? string.Empty : "-")];

    private static MenuItem EntryOf(ContextMenu menu, string name) => menu.Items.OfType<MenuItem>().Single(i => AutomationProperties.GetName(i) == name);

    private static async Task<ContextMenu> MenuAsync(ShellFixture f, string location = BuiltIns.WinFormsSurfaceMenu)
    {
        var menu = await ViewOf(f).BuildMenuAsync(location);
        Assert.NotNull(menu);
        return menu;
    }

    private static bool Enabled(ShellFixture f, string command) => f.Shell.Workbench.Commands.IsEnabled(command);

    private static List<string> KeysOf(ContextMenu menu, params string[] names) => [.. names.Select(n => AutomationProperties.GetAcceleratorKey(EntryOf(menu, n)) ?? string.Empty)];

    private static void AssertEnabled(ContextMenu menu, bool enabled, params string[] names)
    {
        foreach (var name in names)
        {
            Assert.True(EntryOf(menu, name).IsEnabled == enabled, $"{name} {(enabled ? "isn't" : "is")} enabled");
        }
    }

    private static void AssertIcons(ContextMenu menu, params string[] names)
    {
        foreach (var name in names)
        {
            Assert.True(EntryOf(menu, name).Icon is not null, $"{name} has no icon");
        }
    }

    /// <summary>
    /// The surface's and the tray's context menus are the classic IDE's, as D40 approved them: the component's verbs and
    /// Show Smart Tag first, View Code, Bring to Front, Send to Back, Align to Grid, Lock Controls (a check entry, no
    /// icon), Select 'parent', the clipboard and Properties, grayed as the classic command set grays them; a tray item's
    /// has Rename and Line Up Icons; the tray's background Paste, then Line Up Icons; a tool strip item gets the surface's.
    /// Format's commands are enabled by the selection's shape, and an answer of verbs that comes late is never shown.
    /// </summary>
    [AvaloniaFact]
    public async Task The_surface_and_tray_context_menus_have_the_classic_ides_entries_and_enablement()
    {
        var (f, app, hosts) = await OpenAsync();
        await using var _ = f;
        var designer = await DesignAsync(f, Path.Combine(app, "OrderForm.cs"));
        var host = Assert.Single(hosts);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<Views.WinFormsDesignerView>().Any(v => v.IsEffectivelyVisible));
        var platform = f.Shell.Workbench.Platform;
        string Label(string keys) => KeySequence.Parse(keys, platform).ToLabel(platform);

        // A control on the form, nothing copied: no verbs, no smart tag (a check box has no tasks), Select names the form.
        WinFormsDesignerDocument.ClearClipboard();
        designer.SetSelection(["giftCheckBox"], notify: true);
        Assert.False(f.Shell.Workbench.Menus.GetItems(BuiltIns.WinFormsSurfaceMenu).Single(i => i.Command == "winforms.paste").Enabled);
        var menu = await MenuAsync(f);
        Assert.Equal(["View Code", "-", "Bring to Front", "Send to Back", "-", "Align to Grid", "-", "Lock Controls", "-", "Select 'OrderForm'", "-", "Cut", "Copy", "Paste", "Delete", "-", "Properties"], Entries(menu));
        Assert.Equal(("F7", "F4"), (AutomationProperties.GetAcceleratorKey(EntryOf(menu, "View Code")), AutomationProperties.GetAcceleratorKey(EntryOf(menu, "Properties"))));
        Assert.Equal([Label("primary+x"), Label("primary+c"), Label("primary+v"), Label("delete")], KeysOf(menu, "Cut", "Copy", "Paste", "Delete"));
        if (platform != PlatformKind.MacOS)
        {
            Assert.Equal("Delete", AutomationProperties.GetAcceleratorKey(EntryOf(menu, "Delete"))); // D40 (11): the binding's label, not "Del"
        }

        var lockEntry = EntryOf(menu, "Lock Controls");
        Assert.True(lockEntry is { ToggleType: MenuItemToggleType.CheckBox, IsChecked: false, Icon: null, IsEnabled: true }); // D40 (5)
        AssertIcons(menu, "View Code", "Bring to Front", "Send to Back", "Align to Grid", "Cut", "Copy", "Paste", "Delete", "Properties");
        AssertEnabled(menu, true, "View Code", "Bring to Front", "Send to Back", "Align to Grid", "Select 'OrderForm'", "Cut", "Copy", "Delete", "Properties");
        Assert.False(EntryOf(menu, "Paste").IsEnabled);

        // After Copy, Paste is enabled.
        await f.ExecuteAsync("winforms.copy");
        Assert.True(EntryOf(await MenuAsync(f), "Paste").IsEnabled);

        // A control in a split container's panel: its designer's verb and Show Smart Tag in one group; Select names the panel.
        designer.SetSelection(["historyListBox"], notify: true);
        menu = await MenuAsync(f);
        Assert.Equal(["Edit Items...", "Show Smart Tag", "-", "View Code", "-", "Bring to Front", "Send to Back", "-", "Align to Grid", "-", "Lock Controls", "-", "Select 'notesSplit.Panel1'", "-", "Cut", "Copy", "Paste", "Delete", "-", "Properties"], Entries(menu));
        Assert.Equal(Label("shift+alt+f10"), AutomationProperties.GetAcceleratorKey(EntryOf(menu, "Show Smart Tag")));

        // A control in a group box: Select names the group box, and selects it.
        designer.SetSelection(["standardRadioButton"], notify: true);
        Assert.Contains("Select 'shippingGroupBox'", Entries(await MenuAsync(f)));
        await f.ExecuteAsync("winforms.selectParent");
        Assert.Equal(["shippingGroupBox"], designer.Selection);

        // The form: no verbs, no Select; z-order, Align to Grid and the clipboard grayed (nothing copied); Lock Controls enabled.
        WinFormsDesignerDocument.ClearClipboard();
        designer.SetSelection([designer.Root!.Name], notify: true);
        menu = await MenuAsync(f);
        Assert.Equal(["View Code", "-", "Bring to Front", "Send to Back", "-", "Align to Grid", "-", "Lock Controls", "-", "Cut", "Copy", "Paste", "Delete", "-", "Properties"], Entries(menu));
        AssertEnabled(menu, false, "Bring to Front", "Send to Back", "Align to Grid", "Cut", "Copy", "Paste", "Delete");
        Assert.True(EntryOf(menu, "Lock Controls").IsEnabled);

        // A tray component: View Code, the clipboard, Rename (F2) and Line Up Icons, Properties; no z-order, no Lock Controls.
        designer.SetSelection(["orderToolTip"], notify: true);
        menu = await MenuAsync(f, BuiltIns.WinFormsTrayMenu);
        Assert.Equal(["View Code", "-", "Cut", "Copy", "Paste", "Delete", "-", "Rename", "Line Up Icons", "-", "Properties"], Entries(menu));
        Assert.Equal("F2", AutomationProperties.GetAcceleratorKey(EntryOf(menu, "Rename")));
        AssertEnabled(menu, true, "Cut", "Copy", "Delete");

        // The tray's background: Paste, then Line Up Icons (D40, 10).
        Assert.Equal(["Paste", "-", "Line Up Icons"], Entries(await MenuAsync(f, BuiltIns.WinFormsTrayBackgroundMenu)));

        // Format: one control grays Lefts…Bottoms and spacing; two with one parent enable them; different parents gray
        // spacing; a tray component in the selection grays what is for controls only (to Grid, Lock Controls, Order).
        designer.SetSelection(["customerTextBox"], notify: true);
        Assert.Equal((false, true, true, true, false), (Enabled(f, "winforms.align.lefts"), Enabled(f, "winforms.align.toGrid"), Enabled(f, "winforms.center.horizontally"), Enabled(f, "winforms.lockControls"), Enabled(f, "winforms.hspacing.equal")));
        designer.SetSelection(["customerLabel", "customerTextBox"], notify: true);
        Assert.Equal((true, true, true), (Enabled(f, "winforms.align.lefts"), Enabled(f, "winforms.hspacing.equal"), Enabled(f, "winforms.sameSize.width")));
        designer.SetSelection(["customerTextBox", "standardRadioButton"], notify: true);
        Assert.Equal((true, false), (Enabled(f, "winforms.align.lefts"), Enabled(f, "winforms.hspacing.equal")));
        designer.SetSelection(["customerTextBox", "orderToolTip"], notify: true);
        Assert.Equal((false, false, false, true), (Enabled(f, "winforms.align.toGrid"), Enabled(f, "winforms.lockControls"), Enabled(f, "winforms.order.front"), Enabled(f, "winforms.copy")));

        // The Format menu: Lock Controls at the bottom, a check entry; Align's entries all have their icons (D40, 8).
        var format = f.Shell.Workbench.Menus.GetItems(Codalus.Workbench.Menus.MenuLocations.MenuBar("format"));
        Assert.True(format[^1] is { Command: "winforms.lockControls", IsChecked: false });
        var align = Views.MenuBuilder.Context(f.Shell, "menuBar/format/align")!;
        Assert.Equal(7, align.Items.OfType<MenuItem>().Count(i => i.Icon is not null));

        // InventoryForm: a tab control's designer verbs (Remove Tab enabled with tab pages) before Show Smart Tag; a menu
        // strip's item gets the surface's menu, with what is for controls grayed and Select naming its strip.
        var inventory = await DesignAsync(f, Path.Combine(app, "InventoryForm.cs"));
        var inventoryHost = hosts[^1];
        await ShellFixture.PumpUntil(() => ReferenceEquals(ViewOf(f).Document, inventory));
        inventory.SetSelection(["detailsTabControl"], notify: true);
        menu = await MenuAsync(f);
        Assert.Equal(["Add Tab", "Remove Tab", "Show Smart Tag", "-", "View Code"], Entries(menu).Take(5));
        Assert.True(EntryOf(menu, "Remove Tab").IsEnabled);
        Assert.Contains("Select 'InventoryForm'", Entries(menu));
        inventory.SetSelection(["fileToolStripMenuItem"], notify: true);
        menu = await MenuAsync(f);
        Assert.Equal(["View Code", "-", "Bring to Front", "Send to Back", "-", "Align to Grid", "-", "Lock Controls", "-", "Select 'mainMenuStrip'", "-", "Cut", "Copy", "Paste", "Delete", "-", "Properties"], Entries(menu));
        AssertEnabled(menu, false, "Bring to Front", "Send to Back", "Align to Grid", "Lock Controls");
        AssertEnabled(menu, true, "Select 'mainMenuStrip'", "Cut", "Copy", "Delete");

        // An answer that comes after the selection moved on is never shown; without a delay the verb comes with the menu.
        inventoryHost.ActionsGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inventory.SetSelection(["categoryListBox"], notify: true);
        inventory.PrefetchVerbs();
        inventory.SetSelection(["nameTextBox"], notify: true);
        inventoryHost.ActionsGate.SetResult();
        Assert.DoesNotContain("Edit Items...", Entries(await MenuAsync(f)));
        inventoryHost.ActionsGate = null;
        inventory.SetSelection(["categoryListBox"], notify: true);
        Assert.Equal(["Edit Items...", "Show Smart Tag"], Entries(await MenuAsync(f)).Take(2));
        var calls = inventoryHost.ActionsCalls;
        Assert.Equal("Edit Items...", Entries(await MenuAsync(f))[0]);
        Assert.Equal(calls, inventoryHost.ActionsCalls); // fetched once for the selection and kept
    }

    /// <summary>
    /// The context menus' entries act on the designer's selection: a verb (Edit Items... opens the collection editor, Add
    /// Tab runs the designer's verb), Bring to Front, Align to Grid, the clipboard (paste into a split container's panel),
    /// Cut and Delete, Properties and View Code.
    /// </summary>
    [AvaloniaFact]
    public async Task Context_menu_commands_act_on_the_designer()
    {
        var (f, app, hosts) = await OpenAsync();
        await using var _ = f;
        var designer = await DesignAsync(f, Path.Combine(app, "OrderForm.cs"));
        var host = Assert.Single(hosts);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<Views.WinFormsDesignerView>().Any(v => v.IsEffectivelyVisible));

        // The verb: Edit Items... opens the list box's collection editor.
        designer.SetSelection(["historyListBox"], notify: true);
        EntryOf(await MenuAsync(f), "Edit Items...").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await ShellFixture.PumpUntil(() => designer.CollectionEditor is not null);
        Assert.Contains("Items", designer.CollectionEditor!.Title, StringComparison.Ordinal);
        designer.CloseCollectionEditor();

        // Bring to Front: the control's Controls.Add first among the form's.
        designer.SetSelection(["giftCheckBox"], notify: true);
        await f.ExecuteAsync("winforms.order.front");
        await ShellFixture.PumpUntil(() => Code(designer).Contains("            Controls.Add(giftCheckBox);\n            Controls.Add(counterControl);", StringComparison.Ordinal), seconds: 30);

        // Align to Grid: a control nudged off the grid back onto it (8 px).
        await designer.NudgeAsync(1, 0, resize: false);
        Assert.Contains("giftCheckBox.Location = new Point(97, 70);", Code(designer), StringComparison.Ordinal);
        await f.ExecuteAsync("winforms.align.toGrid");
        await ShellFixture.PumpUntil(() => Code(designer).Contains("giftCheckBox.Location = new Point(96, 72);", StringComparison.Ordinal), seconds: 30);

        // Copy and Paste into the split container's panel the list box is in.
        designer.SetSelection(["customerTextBox"], notify: true);
        await f.ExecuteAsync("winforms.copy");
        designer.SetSelection(["historyListBox"], notify: true);
        var loads = host.Loads;
        await f.ExecuteAsync("winforms.paste");
        await ShellFixture.PumpUntil(() => Code(designer).Contains("notesSplit.Panel1.Controls.Add(customerTextBox1);", StringComparison.Ordinal) && host.Loads > loads, seconds: 30);
        Assert.Equal("notesSplit.Panel1", designer.Find("customerTextBox1")?.Parent);

        // Cut, then Undo; Delete.
        designer.SetSelection(["counterControl"], notify: true);
        var before = Code(designer);
        await f.ExecuteAsync("winforms.cut");
        await ShellFixture.PumpUntil(() => !Code(designer).Contains("counterControl", StringComparison.Ordinal), seconds: 30);
        designer.Undo();
        await ShellFixture.PumpUntil(() => Code(designer) == before && designer.Find("counterControl") is not null, seconds: 30);
        designer.SetSelection(["counterControl"], notify: true);
        await f.ExecuteAsync("winforms.delete");
        await ShellFixture.PumpUntil(() => !Code(designer).Contains("counterControl", StringComparison.Ordinal), seconds: 30);

        // Properties shows the Properties window over the designer; View Code the form's code.
        await f.ExecuteAsync("workbench.view.properties");
        Assert.Same(designer, f.Shell.PropertySource);
        await f.ExecuteAsync("winforms.viewCode");
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == Path.Combine(app, "OrderForm.cs"));

        // InventoryForm: a tab control's Add Tab runs the designer's verb.
        var inventory = await DesignAsync(f, Path.Combine(app, "InventoryForm.cs"));
        await ShellFixture.PumpUntil(() => ReferenceEquals(ViewOf(f).Document, inventory));
        inventory.SetSelection(["detailsTabControl"], notify: true);
        EntryOf(await MenuAsync(f), "Add Tab").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await ShellFixture.PumpUntil(() => hosts[^1].Invoked.Contains("verb:Add Tab"));
    }

    /// <summary>
    /// Lock Controls as the classic IDE's: checked while the form or one of its own controls is locked, and choosing it
    /// sets every lockable component to the opposite, in one change undone in one step; the locks are the default .resx's
    /// name.Locked metadata, never code. A locked control doesn't move, nudge or resize on the surface (no grab handles);
    /// Bring to Front still orders it. A form with a context menu strip (which has no Locked) locks too.
    /// </summary>
    [AvaloniaFact]
    public async Task Lock_Controls_is_the_classic_toggle_and_writes_resx_Locked_metadata()
    {
        var (f, app, hosts) = await OpenAsync();
        await using var _ = f;
        var designer = await DesignAsync(f, Path.Combine(app, "OrderForm.cs"));
        var host = Assert.Single(hosts);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<Views.WinFormsDesignerView>().Any(v => v.IsEffectivelyVisible));
        var resx = designer.ResourceModels.Single();
        var originalResx = resx.Text.Text;
        var originalCode = Code(designer);
        bool Checked() => f.Shell.Workbench.Context.GetValue("winFormsControlsLocked") is true;
        string Metadata(string name) => $"<metadata name=\"{name}.Locked\" type=\"{FakeWinFormsDesignerHost.BooleanType}\">";

        designer.SetSelection(["giftCheckBox"], notify: true);
        Assert.False(Checked());
        await f.ExecuteAsync("winforms.lockControls");
        await ShellFixture.PumpUntil(() => resx.Text.Text.Contains(Metadata("giftCheckBox"), StringComparison.Ordinal), seconds: 30);
        Assert.Contains(Metadata("$this"), resx.Text.Text, StringComparison.Ordinal);
        Assert.Contains(Metadata("historyListBox"), resx.Text.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("orderToolTip.Locked", resx.Text.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("notesSplit.Panel1.Locked", resx.Text.Text, StringComparison.Ordinal);
        Assert.Equal(originalCode, Code(designer));
        Assert.True(designer.AreControlsLocked && Checked());
        Assert.True(designer.Find("giftCheckBox")!.Locked);
        Assert.Null(designer.Find("orderToolTip")!.Locked);
        Assert.True(f.Shell.Workbench.Menus.GetItems(BuiltIns.WinFormsSurfaceMenu).Single(i => i.Command == "winforms.lockControls").IsChecked);
        Assert.True(f.Shell.Workbench.Menus.GetItems(Codalus.Workbench.Menus.MenuLocations.MenuBar("format")).Single(i => i.Command == "winforms.lockControls").IsChecked);
        Assert.True(EntryOf(await MenuAsync(f), "Lock Controls").IsChecked);

        // A locked control: no gestures move or resize it, no grab handles; Bring to Front still orders it.
        var gift = designer.Find("giftCheckBox")!;
        await designer.NudgeAsync(1, 0, resize: false);
        await designer.MoveSelectionAsync(20, 20, gift.X + 30, gift.Y + 30);
        Assert.Equal(originalCode, Code(designer));
        var surface = ViewOf(f).Surface;
        Assert.All(surface.Handles(new Avalonia.Rect(gift.X, gift.Y, gift.Width, gift.Height)), h => Assert.Equal(-1, surface.HandleAt(h.Center)));
        await designer.OrderAsync(front: true);
        Assert.Contains("            Controls.Add(giftCheckBox);\n            Controls.Add(counterControl);", Code(designer), StringComparison.Ordinal);
        var ordered = Code(designer);
        var rows = await RowsAsync(f);
        Assert.Equal("True", rows.Single(r => r.Name == "Locked").Value);

        // Again: every lock goes, and the .resx is as it was.
        await f.ExecuteAsync("winforms.lockControls");
        await ShellFixture.PumpUntil(() => !resx.Text.Text.Contains(".Locked", StringComparison.Ordinal), seconds: 30);
        Assert.Equal(originalResx, resx.Text.Text);
        Assert.False(Checked());

        // One control of the form locked (its Locked property): checked, and Lock Controls unlocks it, so nothing is locked.
        await designer.SetPropertyAsync(["giftCheckBox"], "Locked", "True");
        Assert.Contains(Metadata("giftCheckBox"), resx.Text.Text, StringComparison.Ordinal);
        Assert.True(Checked());
        await f.ExecuteAsync("winforms.lockControls");
        await ShellFixture.PumpUntil(() => !resx.Text.Text.Contains(".Locked", StringComparison.Ordinal), seconds: 30);
        Assert.DoesNotContain(designer.Components, c => c.Locked == true);

        // A nested control locked: not checked (only the form's own controls count), and Lock Controls locks them all.
        await designer.SetPropertyAsync(["standardRadioButton"], "Locked", "True");
        Assert.False(Checked());
        var nested = resx.Text.Text;
        await f.ExecuteAsync("winforms.lockControls");
        await ShellFixture.PumpUntil(() => resx.Text.Text.Contains(Metadata("giftCheckBox"), StringComparison.Ordinal), seconds: 30);
        Assert.True(Checked());

        // Undo takes the whole lock back in one step; the form loads again from the .resx with its one lock.
        var loads = host.Loads;
        designer.Undo();
        await ShellFixture.PumpUntil(() => resx.Text.Text == nested && host.Loads > loads, seconds: 30);
        Assert.Contains("standardRadioButton.Locked", host.LastLoad!.Resources.Single(r => r.Culture.Length == 0).Text, StringComparison.Ordinal);
        await ShellFixture.PumpUntil(() => designer.Find("standardRadioButton")?.Locked == true && designer.Find("giftCheckBox")?.Locked == false, seconds: 30);
        Assert.Equal(ordered, Code(designer)); // the locks never touched the code

        // InventoryForm: its context menu strip has no Locked; Lock Controls leaves it out and locks the rest.
        var inventory = await DesignAsync(f, Path.Combine(app, "InventoryForm.cs"));
        await ShellFixture.PumpUntil(() => ReferenceEquals(ViewOf(f).Document, inventory));
        Assert.Null(inventory.Find("itemsContextMenuStrip")!.Locked);
        inventory.SetSelection(["categoryListBox"], notify: true);
        await f.ExecuteAsync("winforms.lockControls");
        var inventoryResx = inventory.ResourceModels.Single();
        await ShellFixture.PumpUntil(() => inventoryResx.Text.Text.Contains(Metadata("categoryListBox"), StringComparison.Ordinal), seconds: 30);
        Assert.DoesNotContain("itemsContextMenuStrip.Locked", inventoryResx.Text.Text, StringComparison.Ordinal);
        Assert.True(inventory.AreControlsLocked && Checked());
    }

    /// <summary>
    /// Align and Make Same Size keep a locked primary selection as the reference, as the classic IDE does, and no Format
    /// command moves or sizes a locked control.
    /// </summary>
    [AvaloniaFact]
    public async Task Format_commands_keep_a_locked_primary_as_the_reference()
    {
        var (f, app, _) = await OpenAsync();
        await using var fixture = f;
        var designer = await DesignAsync(f, Path.Combine(app, "OrderForm.cs"));
        await designer.SetPropertyAsync(["customerLabel"], "Locked", "True");
        Assert.True(designer.Find("customerLabel")!.Locked);
        string LabelLines() => string.Join('\n', Code(designer).Split('\n').Where(l => l.Contains("customerLabel.Location", StringComparison.Ordinal) || l.Contains("customerLabel.Size", StringComparison.Ordinal)));
        var label = LabelLines();
        designer.SetSelection(["customerLabel", "customerTextBox", "giftCheckBox"], notify: true);

        await designer.AlignAsync(DesignerAlignment.Lefts);
        Assert.Contains("customerTextBox.Location = new Point(12, 12);", Code(designer), StringComparison.Ordinal);
        Assert.Contains("giftCheckBox.Location = new Point(12, 70);", Code(designer), StringComparison.Ordinal);
        await designer.MakeSameSizeAsync(DesignerSizing.Width);
        Assert.Contains("customerTextBox.Size = new Size(62, 23);", Code(designer), StringComparison.Ordinal);
        Assert.Contains("giftCheckBox.Size = new Size(62, 19);", Code(designer), StringComparison.Ordinal);
        await designer.AlignAsync(DesignerAlignment.ToGrid);
        await designer.CenterAsync(horizontal: true);
        await designer.SpacingAsync(horizontal: false, DesignerSpacing.Increase);
        await designer.MakeSameSizeAsync(DesignerSizing.ToGrid);
        Assert.Equal(label, LabelLines());
    }

    /// <summary>
    /// Lock Controls on a form with no default .resx: the file is created (the empty one the C# host makes) and held
    /// before the write, so the lock undoes in one step and leaves the designer code, an earlier change included, alone.
    /// </summary>
    [AvaloniaFact]
    public async Task Lock_Controls_on_a_form_without_a_resx_creates_it_and_undoes_as_one_step()
    {
        var (f, app, _) = await OpenAsync();
        await using var fixture = f;
        var path = Path.Combine(app, "OrderForm.resx");
        File.Delete(path);
        var designer = await DesignAsync(f, Path.Combine(app, "OrderForm.cs"));
        Assert.Empty(designer.ResourceModels);

        // An earlier change of the code, then the lock.
        designer.SetSelection(["giftCheckBox"], notify: true);
        await designer.NudgeAsync(1, 0, resize: false);
        var nudged = Code(designer);
        Assert.Contains("giftCheckBox.Location = new Point(97, 70);", nudged, StringComparison.Ordinal);
        await f.ExecuteAsync("winforms.lockControls");
        await ShellFixture.PumpUntil(() => designer.ResourceModels.Count == 1 && designer.ResourceModels[0].Text.Text.Contains("giftCheckBox.Locked", StringComparison.Ordinal), seconds: 30);
        Assert.True(File.Exists(path));
        var resx = designer.ResourceModels[0];
        Assert.Equal(path, resx.Path);
        Assert.StartsWith(DesignerResxStart, resx.Text.Text, StringComparison.Ordinal);
        Assert.Equal(nudged, Code(designer));

        // Undo: the metadata goes (the file stays, empty), the code keeps the earlier change.
        designer.Undo();
        await ShellFixture.PumpUntil(() => !resx.Text.Text.Contains(".Locked", StringComparison.Ordinal), seconds: 30);
        Assert.Equal(Codalus.LanguageServices.Protocol.DesignerResx.Empty.Replace("\r\n", "\n", StringComparison.Ordinal), resx.Text.Text.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal(nudged, Code(designer));
    }

    private static string DesignerResxStart => Codalus.LanguageServices.Protocol.DesignerResx.Empty.Replace("\r\n", "\n", StringComparison.Ordinal)[..^"</root>\n".Length];
}
