using Codalus.LanguageServices.Host.WinForms;
using Codalus.LanguageServices.Protocol;
using Codalus.Workbench.WinForms;
using Microsoft.CodeAnalysis.CSharp;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// M41b (ADR 0073), Windows only: the designer host's answers to M41b's operations on the fixture's own build, through
/// the same process and protocol as the IDE, turned into code by the C# host's writer as the designer document does:
/// snap line data and the tray's and tool strips' views, z-order and tab order, extender properties, data bindings,
/// collections (values, menu items, list view items), smart tags, image import, a localizable form's resources per
/// culture and its Localizable switch, and (#67) Locked as .resx metadata and the context menu's verbs.
/// </summary>
public sealed class WinFormsDesignerDepthHostTests(WinFormsHostFixture fixture) : IClassFixture<WinFormsHostFixture>
{
    private const string Skip = "The Windows Forms designer runs on Windows only (D31).";

    // A 1×1 PNG.
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(IWinFormsDesignerHost Host, DesignerRenderResult Render, DesignerSource Source)> LoadAsync(string form, string? culture = null, bool withResources = false)
    {
        var source = fixture.Read(form[..^3] + ".Designer.cs");
        var target = fixture.Target(form);
        var parameters = WinFormsDesignerService.LoadParams(target, source.Model, source.Model.Namespace + "." + source.Model.ClassName);
        if (withResources)
        {
            var resources = WinFormsDesignerService.FormResources(target.CodeFile);
            parameters = parameters with
            {
                Resources = [.. resources.Select(r => new DesignerResourceText(r.Culture, File.ReadAllText(r.Path)))],
                Culture = culture,
                Localizable = new DesignerResourceSet(target.CodeFile, p => File.Exists(p) ? File.ReadAllText(p) : null).Get(null)?.IsLocalizable ?? false,
            };
        }

        var host = await WinFormsDesignerProcess.StartAsync(target, Ct);
        try
        {
            var render = await host.LoadAsync(parameters, Ct);
            Assert.True(render.Errors.Length == 0, string.Join("\n", render.Errors));
            return (host, render, source);
        }
        catch
        {
            // A load that failed (or ran out of time) ends its host: one left running would take the machine from the tests after it.
            await host.DisposeAsync();
            throw;
        }
    }

    /// <summary>A change as the designer document turns it into the C# host's edits (WinFormsDesignerDocument.EditsFor).</summary>
    private static List<DesignerEdit> Edits(DesignerSource source, DesignerChangeResult result)
    {
        Assert.Null(result.Error);
        var edits = new List<DesignerEdit>();
        var created = new HashSet<string>(StringComparer.Ordinal);
        bool Known(string name) => name.Length == 0 || created.Contains(name) || source.Model.Components.Any(c => c.Name == name);
        string Of(string name) => name == source.Model.ClassName ? string.Empty : name;
        foreach (var component in (result.Created is { } main ? [main] : Array.Empty<DesignerCreated>()).Concat(result.Added))
        {
            if (!Known(component.Name))
            {
                created.Add(component.Name);
                edits.Add(new DesignerEdit
                {
                    Kind = DesignerEditKinds.AddComponent, Component = component.Name, Type = component.Type, CreationArguments = component.CreationArguments,
                    Parent = component.IsControl ? Of(component.Parent ?? string.Empty) : null, ParentPath = component.ParentPath, Cell = component.Cell,
                    Index = component.IsControl ? component.Index : 0, Properties = component.Properties, SupportsInitialize = component.SupportsInitialize,
                });
            }
        }

        foreach (var collection in result.Collections)
        {
            foreach (var local in collection.Locals)
            {
                if (Known(local.Name))
                {
                    edits.Add(new DesignerEdit { Kind = DesignerEditKinds.RemoveComponent, Component = local.Name });
                }

                edits.Add(new DesignerEdit { Kind = DesignerEditKinds.AddLocal, Component = local.Name, Value = local.Value });
                created.Add(local.Name);
            }

            edits.Add(new DesignerEdit { Kind = DesignerEditKinds.SetCollection, Component = Of(collection.Component), Property = collection.Property, Method = collection.Method, Type = collection.ElementType, Items = collection.Items });
        }

        edits.AddRange(result.Removed.Where(n => Known(n) && n.Length > 0 && !created.Contains(n)).Select(n => new DesignerEdit { Kind = DesignerEditKinds.RemoveComponent, Component = n }));
        foreach (var order in result.Reordered)
        {
            edits.AddRange(order.Children.Select((child, i) => (child, i)).Where(c => Known(c.child)).Select(c => new DesignerEdit { Kind = DesignerEditKinds.SetChildIndex, Component = c.child, Index = c.i }));
        }

        foreach (var change in result.Changes.Where(c => Known(Of(c.Component)) && (result.Created?.Name != c.Component || c.Provider is not null || c.DataBinding)))
        {
            edits.Add(change switch
            {
                { Provider: { } provider } => new DesignerEdit { Kind = DesignerEditKinds.SetExtender, Component = Of(change.Component), Provider = provider, Property = change.Property, Value = change.Value },
                { DataBinding: true } => new DesignerEdit { Kind = DesignerEditKinds.SetDataBinding, Component = Of(change.Component), Property = change.Property, Value = change.Value },
                _ => new DesignerEdit { Kind = DesignerEditKinds.SetProperty, Component = Of(change.Component), Property = change.Property, Value = change.Value },
            });
        }

        edits.AddRange(result.Localized.Select(l => new DesignerEdit { Kind = DesignerEditKinds.SetLocalized, Component = Of(l.Component), Localized = l.Localized }));
        edits.AddRange(result.Resources.Select(r => new DesignerEdit { Kind = DesignerEditKinds.SetResource, Key = r.Key, Culture = r.Culture, Resource = r.Resource, Metadata = r.Metadata }));
        return edits;
    }

    /// <summary>The designer file after <paramref name="result"/>, and the form's resource files that changed (path, text).</summary>
    private (string Code, Dictionary<string, string> Resources) Apply(string form, DesignerSource source, DesignerChangeResult result)
    {
        var resources = new DesignerResourceSet(Path.Combine(fixture.App, form), p => File.Exists(p) ? File.ReadAllText(p) : null);
        var writer = new DesignerWriter(t => DesignerReader.Read(CSharpSyntaxTree.ParseText(t), source.Model.BaseType, DesignerReader.WindowsFormsImplicitUsings), resources);
        var code = writer.Apply(source.Text, [.. Edits(source, result)]).ToString();
        return (code, resources.Changes.ToDictionary(c => Path.GetFileName(c.Path), c => c.After, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Snap_data_the_tray_and_tool_strip_items_come_with_the_render()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Skip);
        var (host, render, _) = await LoadAsync("InventoryForm.cs");
        await using var _ = host;
        DesignerComponentView View(string name) => render.Components.Single(c => c.Name == name);

        // Snap lines: margins, a text baseline, tab indexes, Dock and Anchor for the adorners.
        Assert.Equal([3, 3, 3, 3], View("categoryListBox").Margin!);
        Assert.NotNull(View("nameTextBox").Baseline);
        Assert.Equal(0, View("nameTextBox").TabIndex);
        Assert.Equal("Top", View("mainMenuStrip").Dock);
        Assert.NotNull(View("categoryListBox").Anchor);

        // The tray: components that aren't controls, and not a list view's columns; the menu strip shows on the form and in the tray.
        Assert.True(View("itemBindingSource").InTray && View("inventoryToolTip").InTray && View("iconImageList").InTray);
        Assert.DoesNotContain(render.Components, c => c.Name == "nameColumnHeader" && c.InTray);
        Assert.True(View("mainMenuStrip").InTray);

        // Tool strip items: their owner, index and text, a drop-down's items on their own; collections and smart tags flagged.
        var file = View("fileToolStripMenuItem");
        Assert.True(file is { IsItem: true, Owner: "mainMenuStrip", ItemIndex: 0, Text: "&File", HasDropDown: true });
        Assert.Equal(("fileToolStripMenuItem", 2), (View("exitToolStripMenuItem").Owner, View("exitToolStripMenuItem").ItemIndex));
        Assert.Contains("System.Windows.Forms.ListBox:Items", View("categoryListBox").Collections!);
        Assert.True(View("detailsTabControl").HasActions);

        // #67: the real host's keys make the strips' items, and only theirs, a strip's (the surface's Type Here).
        foreach (var strip in new[] { "mainMenuStrip", "mainToolStrip", "mainStatusStrip", "itemsContextMenuStrip" })
        {
            Assert.True(View(strip).Collections?.Any(DesignerCollections.IsStripItems) == true, $"{strip}: {string.Join(", ", View(strip).Collections ?? [])}");
        }

        foreach (var list in new[] { "categoryListBox", "itemsListView" })
        {
            Assert.False(View(list).Collections?.Any(DesignerCollections.IsStripItems) == true, $"{list}: {string.Join(", ", View(list).Collections ?? [])}");
        }

        Assert.Contains("System.Windows.Forms.ListView:Items", View("itemsListView").Collections!);

        // #67: the control designers' Locked, false here; null for what has none (a context menu strip, the tray's
        // components, a tool strip item), so Lock Controls leaves them out.
        Assert.False(render.Components[0].Locked ?? true);
        Assert.False(View("categoryListBox").Locked ?? true);
        Assert.False(View("mainMenuStrip").Locked ?? true);
        Assert.Null(View("itemsContextMenuStrip").Locked);
        Assert.Null(View("inventoryToolTip").Locked);
        Assert.Null(View("fileToolStripMenuItem").Locked);
    }

    /// <summary>
    /// #67: Lock Controls' Locked is the control designers' design-time property, written as the default .resx's
    /// name.Locked metadata ($this.Locked for the form) with ResourceValues' type string, never as code, and read back
    /// from it at load; a split container's panel has none; a batch over a form with a context menu strip (whose designer
    /// has none) skips the strip instead of failing.
    /// </summary>
    [Fact]
    public async Task Locked_is_the_resx_metadata_both_ways()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Skip);
        const string Boolean = "System.Boolean, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";
        string resx;
        var (host, render, source) = await LoadAsync("OrderForm.cs", withResources: true);
        await using (host)
        {
            DesignerComponentView View(string name) => render.Components.Single(c => c.Name == name);
            Assert.Equal("notesSplit.Panel1", View("historyListBox").Parent);
            Assert.True(View("notesSplit.Panel1") is { IsPart: true, Locked: null });
            Assert.Null(View("orderToolTip").Locked);
            Assert.False(View("giftCheckBox").Locked ?? true);

            var locked = await host.SetPropertiesAsync(new DesignerSetPropertiesParams([new DesignerPropertyEdit("giftCheckBox", "Locked") { Text = "True" }, new DesignerPropertyEdit(string.Empty, "Locked") { Text = "True" }]), Ct);
            Assert.Null(locked.Error);
            Assert.Empty(locked.Changes); // design-only: never code
            Assert.Equal(["$this.Locked", "giftCheckBox.Locked"], locked.Resources.Select(r => r.Key).Order(StringComparer.Ordinal));
            Assert.All(locked.Resources, r => Assert.True(r is { Metadata: true, Culture: "", Resource: { Text: "True", Type: Boolean } }, r.Key));
            Assert.True(locked.Render.Components.Single(c => c.Name == "giftCheckBox").Locked);
            var (code, resources) = Apply("OrderForm.cs", source, locked);
            Assert.Equal(source.Text.ToString(), code);
            resx = resources["OrderForm.resx"];
            Assert.Contains($"<metadata name=\"giftCheckBox.Locked\" type=\"{Boolean}\">", resx, StringComparison.Ordinal);
            Assert.Contains($"<metadata name=\"$this.Locked\" type=\"{Boolean}\">", resx, StringComparison.Ordinal);
        }

        // A host loaded with that .resx: the check box and the form are locked, and the Properties window says so; unlocked,
        // the entry goes.
        var target = fixture.Target("OrderForm.cs");
        var parameters = WinFormsDesignerService.LoadParams(target, source.Model, source.Model.Namespace + "." + source.Model.ClassName) with { Resources = [new DesignerResourceText(string.Empty, resx)] };
        var again = await WinFormsDesignerProcess.StartAsync(target, Ct);
        await using (again)
        {
            var reloaded = await again.LoadAsync(parameters, Ct);
            Assert.True(reloaded.Errors.Length == 0, string.Join("\n", reloaded.Errors));
            Assert.True(reloaded.Components.Single(c => c.Name == "giftCheckBox").Locked);
            Assert.True(reloaded.Components[0].Locked);
            Assert.Equal("True", (await again.PropertiesAsync(new DesignerComponentsParams(["giftCheckBox"]), Ct)).Properties.Single(p => p.Name == "Locked").Value);
            var unlocked = await again.SetPropertiesAsync(new DesignerSetPropertiesParams([new DesignerPropertyEdit("giftCheckBox", "Locked") { Text = "False" }]), Ct);
            var entry = Assert.Single(unlocked.Resources);
            Assert.Equal("giftCheckBox.Locked", entry.Key);
            Assert.Null(entry.Resource);
            Assert.Empty(unlocked.Changes);
        }

        // InventoryForm: Lock Controls' batch over every control and the context menu strip: no failure, no entry for the strip.
        var (inventory, inventoryRender, _) = await LoadAsync("InventoryForm.cs", withResources: true);
        await using (inventory)
        {
            var root = inventoryRender.Components[0].Name;
            var edits = inventoryRender.Components.Where(c => c.IsControl && !c.IsPart).Select(c => new DesignerPropertyEdit(c.Name == root ? string.Empty : c.Name, "Locked") { Text = "True" }).ToList();
            Assert.Contains(edits, e => e.Component == "itemsContextMenuStrip");
            var all = await inventory.SetPropertiesAsync(new DesignerSetPropertiesParams([.. edits]), Ct);
            Assert.Null(all.Error);
            Assert.Empty(all.Changes);
            Assert.DoesNotContain(all.Resources, r => r.Key == "itemsContextMenuStrip.Locked");
            Assert.Contains(all.Resources, r => r.Key == "categoryListBox.Locked");
            Assert.Contains(all.Resources, r => r.Key == "$this.Locked");
        }
    }

    /// <summary>
    /// #67: the context menu's verbs as the classic IDE's MenuCommandService lists them, with the action service present:
    /// a tab control's designer verbs (Add Tab runs, Remove Tab stays listed but unavailable with no tab page left), a list
    /// box's Edit Items... (an action method marked IncludeAsDesignerVerb, the collection editor's), no two by the same
    /// text; the smart tag's items don't change.
    /// </summary>
    [Fact]
    public async Task Context_menu_verbs_are_the_classic_ides()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Skip);
        var (host, _, source) = await LoadAsync("InventoryForm.cs");
        await using var _ = host;
        async Task<DesignerActionItem[]> VerbsAsync(string component) => (await host.ActionsAsync(new DesignerComponentsParams([component]), Ct)).Verbs;

        var tabs = await VerbsAsync("detailsTabControl");
        Assert.Contains(tabs, v => v is { Kind: DesignerActionKinds.Method, Member: "verb:Add Tab", DisplayName: "Add Tab", Unavailable: false });
        Assert.Contains(tabs, v => v is { Member: "verb:Remove Tab", Unavailable: false });
        Assert.Equal(tabs.Length, tabs.Select(v => v.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var added = await host.InvokeActionAsync(new DesignerInvokeActionParams("detailsTabControl", "verb:Add Tab"), Ct);
        Assert.Null(added.Error);
        var page = Assert.Single(added.Added.Concat(added.Created is { } c ? [c] : []), c => c.Type == "System.Windows.Forms.TabPage");
        Assert.Contains($"detailsTabControl.Controls.Add({page.Name});", Apply("InventoryForm.cs", source, added).Code, StringComparison.Ordinal);

        for (var i = 0; i < 10 && (await VerbsAsync("detailsTabControl")).Any(v => v is { Member: "verb:Remove Tab", Unavailable: false }); i++)
        {
            Assert.Null((await host.InvokeActionAsync(new DesignerInvokeActionParams("detailsTabControl", "verb:Remove Tab"), Ct)).Error);
        }

        Assert.Contains(await VerbsAsync("detailsTabControl"), v => v is { Member: "verb:Remove Tab", Unavailable: true });

        var listBox = await host.ActionsAsync(new DesignerComponentsParams(["categoryListBox"]), Ct);
        Assert.Contains(listBox.Verbs, v => v is { Kind: DesignerActionKinds.Method, Collection: "System.Windows.Forms.ListBox:Items" } && v.DisplayName.StartsWith("Edit Items", StringComparison.Ordinal));
        Assert.Equal(listBox.Verbs.Length, listBox.Verbs.Select(v => v.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(listBox.Items, i => i is { Kind: DesignerActionKinds.Method, Member: "InvokeItemsDialog" });
    }

    [Fact]
    public async Task Z_order_tab_order_and_extenders_come_back_as_code()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Skip);
        var (host, _, source) = await LoadAsync("OrderForm.cs");
        await using var _ = host;

        // Bring to Front: the label first in the form's controls, so first in Controls.Add's order.
        var front = await host.SetChildIndexAsync(new DesignerChildIndexParams("customerLabel", 0), Ct);
        var order = Assert.Single(front.Reordered);
        Assert.Equal("customerLabel", order.Children[0]);
        var (code, _) = Apply("OrderForm.cs", source, front);
        Assert.Contains("            Controls.Add(customerLabel);\n            Controls.Add(counterControl);", code, StringComparison.Ordinal);
        Assert.DoesNotContain("            Controls.Add(customerTextBox);\n            Controls.Add(customerLabel);", code, StringComparison.Ordinal);

        // Tab order: a TabIndex set is one line.
        var tab = await host.SetPropertiesAsync(new DesignerSetPropertiesParams([new DesignerPropertyEdit("giftCheckBox", "TabIndex") { Text = "0" }]), Ct);
        Assert.Contains("giftCheckBox.TabIndex = 0;", Apply("OrderForm.cs", source, tab).Code, StringComparison.Ordinal);

        // An extender property: "ToolTip on orderToolTip" in the Properties window, provider.SetToolTip in the code.
        var properties = (await host.PropertiesAsync(new DesignerComponentsParams(["customerTextBox"]), Ct)).Properties;
        var tip = properties.Single(p => p is { Name: "ToolTip", Provider: "orderToolTip" });
        Assert.Equal("The customer who places the order.", tip.Value);
        var set = await host.SetPropertiesAsync(new DesignerSetPropertiesParams([new DesignerPropertyEdit("productComboBox", "ToolTip") { Provider = "orderToolTip", Text = "What to order." }]), Ct);
        var change = Assert.Single(set.Changes);
        Assert.Equal(("productComboBox", "orderToolTip", "What to order."), (change.Component, change.Provider, change.Value!.Value));
        Assert.Contains("orderToolTip.SetToolTip(productComboBox, \"What to order.\");", Apply("OrderForm.cs", source, set).Code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Data_bindings_collections_and_menu_items_come_back_as_code()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Skip);
        var (host, _, source) = await LoadAsync("InventoryForm.cs");
        await using var _ = host;

        // (DataBindings): a binding source's members to choose from; a binding is DataBindings.Add.
        var properties = (await host.PropertiesAsync(new DesignerComponentsParams(["nameTextBox"]), Ct)).Properties;
        var text = properties.Single(p => p is { Name: "Text", DataBinding: true });
        Assert.Equal("itemBindingSource - Name", text.Value);
        Assert.Contains("itemBindingSource - Quantity", text.StandardValues!);
        var bound = await host.SetPropertiesAsync(new DesignerSetPropertiesParams([new DesignerPropertyEdit("nameTextBox", "Tag") { DataBinding = true, Text = "itemBindingSource - Quantity" }]), Ct);
        Assert.Contains(bound.Changes, c => c is { Component: "nameTextBox", Property: "Tag", DataBinding: true });
        Assert.Contains("nameTextBox.DataBindings.Add(new Binding(\"Tag\", itemBindingSource, \"Quantity\", true));", Apply("InventoryForm.cs", source, bound).Code, StringComparison.Ordinal);

        // A list box's items: values, AddRange.
        var items = await host.CollectionAsync(new DesignerCollectionParams("categoryListBox", "Items"), Ct);
        Assert.Equal(["Tools", "Garden", "Paint"], items.Items.Select(i => i.Text));
        var values = await host.SetCollectionAsync(new DesignerSetCollectionParams("categoryListBox", "Items", [new DesignerCollectionItem { Text = "Tools" }, new DesignerCollectionItem { Text = "Lumber" }]), Ct);
        Assert.Equal("AddRange", Assert.Single(values.Collections).Method);
        Assert.Contains("categoryListBox.Items.AddRange(new object[] { \"Tools\", \"Lumber\" });", Apply("InventoryForm.cs", source, values).Code, StringComparison.Ordinal);

        // A menu item typed in ("Type Here"): a new component, the menu's AddRange with it.
        var menu = await host.CollectionAsync(new DesignerCollectionParams("mainMenuStrip", "Items"), Ct);
        Assert.Equal(["fileToolStripMenuItem", "helpToolStripMenuItem"], menu.Items.Select(i => i.Name));
        var typed = await host.SetCollectionAsync(new DesignerSetCollectionParams("mainMenuStrip", "Items",
            [.. menu.Items, new DesignerCollectionItem { Name = "editToolStripMenuItem", Type = "System.Windows.Forms.ToolStripMenuItem", Text = "&Edit" }]), Ct);
        Assert.Contains(typed.Added, c => c.Name == "editToolStripMenuItem");
        var menuCode = Apply("InventoryForm.cs", source, typed).Code;
        Assert.Contains("mainMenuStrip.Items.AddRange(new ToolStripItem[] { fileToolStripMenuItem, helpToolStripMenuItem, editToolStripMenuItem });", menuCode, StringComparison.Ordinal);
        Assert.Contains("editToolStripMenuItem.Text = \"&Edit\";", menuCode, StringComparison.Ordinal);
        Assert.Contains(typed.Render.Components, c => c is { Name: "editToolStripMenuItem", Owner: "mainMenuStrip", ItemIndex: 2 });

        // A list view's items: locals.
        var listItems = await host.CollectionAsync(new DesignerCollectionParams("itemsListView", "Items"), Ct);
        Assert.Equal(["Hammer", "Saw"], listItems.Items.Select(i => i.Text));
        var locals = await host.SetCollectionAsync(new DesignerSetCollectionParams("itemsListView", "Items", [.. listItems.Items, new DesignerCollectionItem { Text = "Drill" }]), Ct);
        var listChange = Assert.Single(locals.Collections);
        Assert.Equal(3, listChange.Items.Length);
        Assert.Equal("ListViewItem", Assert.Single(listChange.Locals).Value.Type?.Split('.')[^1]);
    }

    [Fact]
    public async Task Smart_tags_list_the_designers_actions_and_run_them()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Skip);
        var (host, _, source) = await LoadAsync("InventoryForm.cs");
        await using var _ = host;

        // A list box's: Edit Items… opens the collection editor. (Windows Forms' ListBoxDesigner gives a list box the
        // unbound action list on .NET 11, Edit Items… alone: no Use Data Bound Items property.)
        var listBox = (await host.ActionsAsync(new DesignerComponentsParams(["categoryListBox"]), Ct)).Items;
        Assert.Contains(listBox, i => i is { Kind: DesignerActionKinds.Method, Member: "InvokeItemsDialog", Collection: "System.Windows.Forms.ListBox:Items", Unavailable: false });

        // A tab control's Add Tab: a new tab page, written as a component in the tab control's controls.
        var tabs = (await host.ActionsAsync(new DesignerComponentsParams(["detailsTabControl"]), Ct)).Items;
        var addTab = tabs.First(i => i.Kind == DesignerActionKinds.Method && i.DisplayName.Contains("Add Tab", StringComparison.OrdinalIgnoreCase));
        var added = await host.InvokeActionAsync(new DesignerInvokeActionParams("detailsTabControl", addTab.Member), Ct);
        Assert.Null(added.Error);
        var page = Assert.Single(added.Added.Concat(added.Created is { } c ? [c] : []), c => c.Type == "System.Windows.Forms.TabPage");
        Assert.Contains($"detailsTabControl.Controls.Add({page.Name});", Apply("InventoryForm.cs", source, added).Code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_imported_image_goes_to_the_forms_resources()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Skip);
        var (host, _, source) = await LoadAsync("OrderForm.cs", withResources: true);
        await using var _ = host;
        var image = (await host.PropertiesAsync(new DesignerComponentsParams(["placeOrderButton"]), Ct)).Properties.Single(p => p.Name == "Image");
        Assert.Equal(DesignerEditors.Image, image.Editor);
        var imported = await host.SetPropertiesAsync(new DesignerSetPropertiesParams([new DesignerPropertyEdit("placeOrderButton", "Image") { Bytes = Png }]), Ct);
        var change = Assert.Single(imported.Changes, c => c.Property == "Image");
        Assert.Equal((DesignerValueKinds.Resource, "placeOrderButton.Image"), (change.Value!.Kind, change.Value.Value));
        Assert.Contains(imported.Resources, r => r.Key == "placeOrderButton.Image" && r.Resource is not null);
        var (code, resources) = Apply("OrderForm.cs", source, imported);
        Assert.Contains("placeOrderButton.Image = (Image)resources.GetObject(\"placeOrderButton.Image\");", code, StringComparison.Ordinal);
        Assert.Contains("<data name=\"placeOrderButton.Image\" type=\"System.Drawing.Bitmap, System.Drawing\" mimetype=\"application/x-microsoft.net.object.bytearray.base64\">", resources["OrderForm.resx"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_localizable_forms_properties_go_to_each_cultures_resources()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Skip);

        // The default culture: Text goes to AboutForm.resx, not the code.
        var (about, _, source) = await LoadAsync("AboutForm.cs", withResources: true);
        await using (about)
        {
            var text = await about.SetPropertiesAsync(new DesignerSetPropertiesParams([new DesignerPropertyEdit("okButton", "Text") { Text = "Close" }]), Ct);
            Assert.DoesNotContain(text.Changes, c => c.Property == "Text");
            var entry = Assert.Single(text.Resources, r => r.Key == "okButton.Text");
            Assert.Equal(("Close", string.Empty), (entry.Resource!.Text, entry.Culture)); // the default culture's is empty
            var (code, resources) = Apply("AboutForm.cs", source, text);
            Assert.Equal(source.Text.ToString(), code);
            Assert.Contains("<value>Close</value>", resources["AboutForm.resx"], StringComparison.Ordinal);
        }

        // French: the French values shown, an edit goes to AboutForm.fr.resx only.
        var (french, _, frenchSource) = await LoadAsync("AboutForm.cs", culture: "fr", withResources: true);
        await using (french)
        {
            var text = (await french.PropertiesAsync(new DesignerComponentsParams(["okButton"]), Ct)).Properties.Single(p => p is { Name: "Text", DataBinding: false });
            Assert.Equal("Fermer", text.Value);
            var edit = await french.SetPropertiesAsync(new DesignerSetPropertiesParams([new DesignerPropertyEdit("okButton", "Text") { Text = "Fermer la fenêtre" }]), Ct);
            var entry = Assert.Single(edit.Resources);
            Assert.Equal(("okButton.Text", "fr"), (entry.Key, entry.Culture));
            var (_, resources) = Apply("AboutForm.cs", frenchSource, edit);
            Assert.Equal(["AboutForm.fr.resx"], resources.Keys);
            Assert.Contains("<value>Fermer la fenêtre</value>", resources["AboutForm.fr.resx"], StringComparison.Ordinal);
        }

        // Localizable switched on: the localizable values move from the code to the resources, with ApplyResources.
        var (order, _, orderSource) = await LoadAsync("OrderForm.cs", withResources: true);
        await using (order)
        {
            var root = (await order.PropertiesAsync(new DesignerComponentsParams([string.Empty]), Ct)).Properties.Single(p => p.Name == "Localizable");
            Assert.Equal("False", root.Value);
            var localizable = await order.SetPropertiesAsync(new DesignerSetPropertiesParams([new DesignerPropertyEdit(string.Empty, "Localizable") { Text = "True" }]), Ct);
            Assert.Contains(localizable.Resources, r => r.Key == "customerLabel.Text" && r.Resource?.Text == "&Customer:");
            Assert.Contains(localizable.Localized, l => l.Component == "customerLabel");
            var (code, resources) = Apply("OrderForm.cs", orderSource, localizable);
            Assert.Contains("resources.ApplyResources(customerLabel, \"customerLabel\");", code, StringComparison.Ordinal);
            Assert.DoesNotContain("customerLabel.Text = ", code, StringComparison.Ordinal);
            Assert.Contains("<metadata name=\"$this.Localizable\"", resources["OrderForm.resx"], StringComparison.Ordinal);
        }
    }
}
