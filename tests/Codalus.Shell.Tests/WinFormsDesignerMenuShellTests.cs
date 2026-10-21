using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using static Codalus.Shell.Tests.WinFormsDesignerTestHost;

namespace Codalus.Shell.Tests;

/// <summary>
/// The Windows Forms designer's menus and tool strips on the surface (#67) in the headless shell with a fake designer
/// host (any OS), on samples/WindowsDesktop's Windows Forms app: which components show a strip's Type Here, and the
/// classic IDE's commands: Edit's on a focused designer, Select All, and paste into the nearest container.
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
}
