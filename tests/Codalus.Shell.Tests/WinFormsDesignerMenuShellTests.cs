using Avalonia.Headless.XUnit;
using Codalus.Shell.ViewModels;
using static Codalus.Shell.Tests.WinFormsDesignerTestHost;

namespace Codalus.Shell.Tests;

/// <summary>
/// The Windows Forms designer's menus and tool strips on the surface (#67) in the headless shell with a fake designer
/// host (any OS), on samples/WindowsDesktop's Windows Forms app: which components show a strip's Type Here.
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
}
