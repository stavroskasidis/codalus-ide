using Avalonia.Headless.XUnit;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.WinForms;
using static Codalus.Shell.Tests.WinFormsDesignerTestHost;

namespace Codalus.Shell.Tests;

/// <summary>
/// M41b (ADR 0073) in the headless shell with a fake designer host (any OS), on samples/WindowsDesktop's Windows Forms
/// app: the Format menu's commands, tab order mode, snap lines, the Dock and Anchor pickers, extender properties,
/// images imported into the resources (undone with the designer file), smart tags, collection editors, paste with
/// event hookups, menus edited in place, data bindings, the component tray's arrangement, and a localizable form's
/// properties written to its resources per language: each becomes the code (and resources) the C# host writes.
/// </summary>
public sealed class WinFormsDesignerDepthShellTests
{
    [AvaloniaFact]
    public async Task Format_commands_tab_order_snap_lines_pickers_extenders_images_and_smart_tags_become_code()
    {
        var (f, app, hosts) = await OpenAsync();
        await using var _ = f;
        var designer = await DesignAsync(f, Path.Combine(app, "OrderForm.cs"));
        var host = Assert.Single(hosts);
        Assert.Single(designer.ResourceModels);
        Assert.Contains("orderToolTip.TrayLocation", host.LastLoad!.Resources.Single(r => r.Culture.Length == 0).Text, StringComparison.Ordinal);
        Assert.False(host.LastLoad.Localizable);

        // Format → Align Lefts and Tops to the primary selection (customerLabel at 12, 15), Make Same Size, spacing, Center in Form.
        designer.SetSelection(["customerLabel", "customerTextBox"], notify: true);
        await designer.AlignAsync(DesignerAlignment.Lefts);
        Assert.Contains("customerTextBox.Location = new Point(12, 12);", Code(designer), StringComparison.Ordinal);
        await designer.AlignAsync(DesignerAlignment.Tops);
        Assert.Contains("customerTextBox.Location = new Point(12, 15);", Code(designer), StringComparison.Ordinal);
        designer.SetSelection(["customerTextBox", "customerLabel"], notify: true);
        await designer.MakeSameSizeAsync(DesignerSizing.Width);
        Assert.Contains("customerLabel.Size = new Size(276, 15);", Code(designer), StringComparison.Ordinal);
        designer.SetSelection(["customerLabel", "quantityUpDown"], notify: true);
        await designer.MakeSameSizeAsync(DesignerSizing.ToGrid);
        Assert.Contains("customerLabel.Size = new Size(280, 16);", Code(designer), StringComparison.Ordinal);
        Assert.Contains("quantityUpDown.Size = new Size(112, 24);", Code(designer), StringComparison.Ordinal);
        designer.SetSelection(["productComboBox", "quantityUpDown"], notify: true);
        await designer.SpacingAsync(horizontal: true, DesignerSpacing.Remove);
        Assert.Contains("quantityUpDown.Location = new Point(256, 41);", Code(designer), StringComparison.Ordinal);
        await designer.SpacingAsync(horizontal: true, DesignerSpacing.Increase);
        Assert.Contains("quantityUpDown.Location = new Point(264, 41);", Code(designer), StringComparison.Ordinal);
        designer.SetSelection(["giftCheckBox"], notify: true);
        await designer.CenterAsync(horizontal: true);
        Assert.Contains("giftCheckBox.Location = new Point(150, 70);", Code(designer), StringComparison.Ordinal);

        // Order → Bring to Front: its Controls.Add first among the form's.
        await designer.OrderAsync(front: true);
        Assert.Contains("            Controls.Add(giftCheckBox);\n            Controls.Add(counterControl);", Code(designer), StringComparison.Ordinal);
        await designer.OrderAsync(front: false);
        Assert.Contains("            Controls.Add(customerLabel);\n            Controls.Add(giftCheckBox);\n            FormBorderStyle", Code(designer), StringComparison.Ordinal);

        // Tab order mode: the labels (a group box's controls after its own index), clicks number each container from 0.
        f.Shell.Layout.OpenDocument(designer, focus: true);
        await f.RunAsync("winforms.tabOrder");
        await ShellFixture.PumpUntil(() => designer.IsTabOrderMode);
        Assert.Equal("1", designer.TabOrderLabel(designer.Find("customerTextBox")!));
        Assert.Equal("5.0", designer.TabOrderLabel(designer.Find("standardRadioButton")!));
        Assert.Null(designer.TabOrderLabel(designer.Root!));
        var upDown = designer.Find("quantityUpDown")!;
        await designer.TabOrderClickAsync(upDown.X + 4, upDown.Y + 4);
        var combo = designer.Find("productComboBox")!;
        await designer.TabOrderClickAsync(combo.X + 4, combo.Y + 4);
        Assert.Contains("quantityUpDown.TabIndex = 0;", Code(designer), StringComparison.Ordinal);
        Assert.Contains("productComboBox.TabIndex = 1;", Code(designer), StringComparison.Ordinal);
        Assert.Equal("0", designer.TabOrderLabel(designer.Find("quantityUpDown")!));
        designer.IsTabOrderMode = false;

        // Snap lines: an edge to a sibling's left, a baseline, the form's padding with the control's margin (12 from the edge).
        var textBox = designer.Find("customerTextBox")!;
        var label = designer.Find("customerLabel")!;
        var gift = designer.Find("giftCheckBox")!;
        var bounds = WinFormsDesignerDocument.Bounds(gift);
        var edge = DesignerSnapping.Move(designer, gift, bounds with { X = textBox.X + 3, Y = bounds.Y + 40 }, new HashSet<string> { "giftCheckBox" });
        Assert.Equal(textBox.X, edge.Bounds.X);
        Assert.Contains(edge.Lines, l => l.Kind == DesignerSnapLine.Edge && l.X1 == textBox.X && l.X2 == textBox.X);
        var quantity = designer.Find("quantityUpDown")!;
        var baseline = DesignerSnapping.Move(designer, gift, bounds with { X = 300, Y = label.Y - 1 }, new HashSet<string> { "giftCheckBox" });
        Assert.Equal(label.Y, baseline.Bounds.Y);
        Assert.Contains(baseline.Lines, l => l.Kind == DesignerSnapLine.Baseline && l.Y1 == label.Y + 15);
        var padding = DesignerSnapping.Move(designer, gift, bounds with { X = 8 + 14, Y = 31 + 330 }, new HashSet<string> { "giftCheckBox" });
        Assert.Equal(8 + 9 + 3, padding.Bounds.X);
        Assert.Contains(padding.Lines, l => l.Kind == DesignerSnapLine.Margin && l.X1 == 17 && l.X2 == 20);
        var far = DesignerSnapping.Move(designer, gift, bounds with { X = 330, Y = 31 + 250 }, new HashSet<string> { "giftCheckBox" });
        Assert.Equal((330d, 281d), (far.Bounds.X, far.Bounds.Y));
        Assert.Empty(far.Lines);
        var resized = DesignerSnapping.Resize(designer, quantity, WinFormsDesignerDocument.Bounds(quantity) with { Width = quantity.Width + (textBox.X + textBox.Width - quantity.X - quantity.Width) - 5 }, handle: 3);
        Assert.Equal(textBox.X + textBox.Width, resized.Bounds.Right);

        // The Dock and Anchor pickers, and an extender property: "ToolTip on orderToolTip", written as SetToolTip.
        designer.SetSelection(["giftCheckBox"], notify: true);
        var rows = await RowsAsync(f);
        var dock = rows.Single(r => r.Name == "Dock");
        Assert.True(dock.IsDock);
        Assert.True(dock.DockNone);
        dock.DockCommand.Execute("Bottom");
        await ShellFixture.PumpUntil(() => Code(designer).Contains("giftCheckBox.Dock = DockStyle.Bottom;", StringComparison.Ordinal));
        rows = await RowsAsync(f);
        var anchor = rows.Single(r => r.Name == "Anchor");
        Assert.True(anchor is { IsAnchor: true, IsPicker: true });
        Assert.True(anchor.AnchorTop!.IsChecked && anchor.AnchorLeft!.IsChecked && !anchor.AnchorRight!.IsChecked && !anchor.AnchorBottom!.IsChecked);
        anchor.AnchorRight.IsChecked = true;
        await ShellFixture.PumpUntil(() => Code(designer).Contains("giftCheckBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;", StringComparison.Ordinal));
        rows = await RowsAsync(f);
        var tip = rows.Single(r => r.Name == "ToolTip on orderToolTip");
        tip.Text = "Wrap it as a present.";
        await ShellFixture.PumpUntil(() => Code(designer).Contains("orderToolTip.SetToolTip(giftCheckBox, \"Wrap it as a present.\");", StringComparison.Ordinal));
        Assert.Contains("giftCheckBox.Text = \"Gift wrap it\";\n            orderToolTip.SetToolTip(giftCheckBox, \"Wrap it as a present.\");", Code(designer), StringComparison.Ordinal);

        // An image imported into the form's resources: GetObject in the code, the bytes in OrderForm.resx; undone in both.
        designer.SetSelection(["placeOrderButton"], notify: true);
        rows = await RowsAsync(f);
        var image = rows.Single(r => r.Name == "Image");
        Assert.True(image is { HasModalEditor: true, IsReadOnly: true });
        var png = Path.Combine(app, "logo.png");
        await File.WriteAllBytesAsync(png, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="), TestContext.Current.CancellationToken);
        await designer.ImportImageAsync("placeOrderButton", "Image", png, toProject: false);
        Assert.Contains("placeOrderButton.Image = (Image)resources.GetObject(\"placeOrderButton.Image\");", Code(designer), StringComparison.Ordinal);
        var resx = designer.ResourceModels.Single();
        Assert.Contains("<data name=\"placeOrderButton.Image\" type=\"System.Drawing.Bitmap, System.Drawing\" mimetype=\"application/x-microsoft.net.object.bytearray.base64\">", resx.Text.Text, StringComparison.Ordinal);
        Assert.True(designer.IsModified);
        var loads = host.Loads;
        designer.Undo();
        await ShellFixture.PumpUntil(() => !Code(designer).Contains("placeOrderButton.Image", StringComparison.Ordinal) && !resx.Text.Text.Contains("placeOrderButton.Image", StringComparison.Ordinal) && host.Loads > loads, seconds: 30);
        designer.Redo();
        await ShellFixture.PumpUntil(() => Code(designer).Contains("placeOrderButton.Image", StringComparison.Ordinal) && resx.Text.Text.Contains("placeOrderButton.Image", StringComparison.Ordinal), seconds: 30);

        // A smart tag: its panel from the host's actions; a method runs and its change is code; "Edit Items…" opens the collection editor.
        await designer.OpenActionsAsync("historyListBox");
        var panel = designer.ActionPanel!;
        Assert.Equal("ListBox Tasks", panel.Title);
        Assert.True(panel.Rows[0].IsHeader);
        Assert.True(panel.Row("Use Data Bound Items")!.IsBoolean);
        Assert.False(panel.Row("Choose Data Source...")!.IsAvailable);
        await panel.Row("Undock in Parent Container")!.Run();
        Assert.Contains("DockInParent", host.Invoked);
        Assert.DoesNotContain("historyListBox.Dock = DockStyle.Fill;", Code(designer), StringComparison.Ordinal);
        Assert.NotNull(designer.ActionPanel?.Row("Dock in Parent Container"));
        await designer.ActionPanel!.Row("Use Data Bound Items")!.SetValueAsync("True");
        Assert.Contains("UseDataBoundItems=True", host.Invoked);
        await designer.ActionPanel!.Row("Edit Items...")!.Run();
        Assert.Null(designer.ActionPanel);
        var editor = designer.CollectionEditor!;
        Assert.True(editor is { IsText: true, IsLive: false });
        editor.Text = "Espresso\nLatte";
        await editor.OkAsync();
        Assert.Null(designer.CollectionEditor);
        Assert.Contains("historyListBox.Items.AddRange(new object[] { \"Espresso\", \"Latte\" });", Code(designer), StringComparison.Ordinal);

        // Paste within the form keeps the event hookups and the extender properties (Visual Studio does); the handler isn't shown.
        designer.SetSelection(["customerTextBox"], notify: true);
        designer.Copy();
        designer.SetSelection(["buttonPanel"], notify: true);
        var active = f.Shell.Layout.ActiveDocument;
        await designer.PasteAsync();
        await ShellFixture.PumpUntil(() => Code(designer).Contains("customerTextBox1 = new TextBox();", StringComparison.Ordinal));
        Assert.Contains("customerTextBox1.TextChanged += OnCustomerChanged;", Code(designer), StringComparison.Ordinal);
        Assert.Contains("orderToolTip.SetToolTip(customerTextBox1, \"The customer who places the order.\");", Code(designer), StringComparison.Ordinal);
        Assert.Same(active, f.Shell.Layout.ActiveDocument);

        // The surface's context menu and the Format menu are there in the designer.
        Assert.Contains(f.Shell.Workbench.Menus.GetItems(BuiltIns.WinFormsSurfaceMenu), i => i.Command == "winforms.order.front");
        Assert.Contains(f.Shell.Workbench.Menus.GetItems(Codalus.Workbench.Menus.MenuLocations.MenuBar("format")), i => i.Submenu == "menuBar/format/align");
        Assert.Contains(f.Shell.Workbench.Menus.GetItems("menuBar/format/align"), i => i.Command == "winforms.align.lefts");

        await f.Shell.SaveAllAsync();
        Assert.Contains("placeOrderButton.Image", await File.ReadAllTextAsync(Path.Combine(app, "OrderForm.resx"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.False(designer.IsModified);

        // The menu bar (macOS's global one too) shows the Format menu while a designer is the active document, only.
        await ShellFixture.PumpUntil(() => MenuBar(f).Contains("Format"));
        await f.Shell.OpenFileAsync(Path.Combine(app, "Program.cs"));
        await ShellFixture.PumpUntil(() => !MenuBar(f).Contains("Format"));
        Assert.Contains("Debug", MenuBar(f));
        f.Shell.Layout.OpenDocument(designer, focus: true);
        await ShellFixture.PumpUntil(() => MenuBar(f).Contains("Format"));
    }

    /// <summary>The menu bar's menus: macOS's global menu bar, elsewhere the window's.</summary>
    private static List<string?> MenuBar(ShellFixture f) => f.Shell.Workbench.Platform == Codalus.Platform.PlatformKind.MacOS
        ? [.. Avalonia.Controls.NativeMenu.GetMenu(f.Window)!.Items.OfType<Avalonia.Controls.NativeMenuItem>().Select(m => m.Header)]
        : [.. f.Window.MenuBar.Items.OfType<Avalonia.Controls.MenuItem>().Select(m => Avalonia.Automation.AutomationProperties.GetName(m))];

    [AvaloniaFact]
    public async Task Menus_collections_bindings_the_tray_and_a_localizable_forms_languages_become_code_and_resources()
    {
        var (f, app, hosts) = await OpenAsync();
        await using var _ = f;
        var designer = await DesignAsync(f, Path.Combine(app, "InventoryForm.cs"));
        Assert.Equal(["mainMenuStrip", "mainToolStrip", "mainStatusStrip", "itemsContextMenuStrip", "iconImageList", "itemBindingSource", "inventoryToolTip", "inventoryHelpProvider", "inventoryErrorProvider"],
            designer.Tray.Select(t => t.Name));

        // A menu strip's Type Here: "&Edit" is editToolStripMenuItem, at the end of its items.
        designer.SetSelection(["mainMenuStrip"], notify: true);
        var stripTypeHere = designer.StripTypeHere!;
        Assert.True(stripTypeHere.CanType);
        Assert.Equal("Items", stripTypeHere.Property);
        await designer.AddItemAsync(stripTypeHere, "&Edit");
        Assert.Contains("editToolStripMenuItem = new ToolStripMenuItem();", Code(designer), StringComparison.Ordinal);
        Assert.Contains("mainMenuStrip.Items.AddRange(new ToolStripItem[] { fileToolStripMenuItem, helpToolStripMenuItem, editToolStripMenuItem });", Code(designer), StringComparison.Ordinal);
        Assert.Contains("editToolStripMenuItem.Text = \"&Edit\";", Code(designer), StringComparison.Ordinal);
        Assert.Equal(["editToolStripMenuItem"], designer.Selection);

        // A menu item's drop-down: its rows and Type Here; a new item, a separator ("-"), one moved up, one deleted.
        designer.SetSelection(["fileToolStripMenuItem"], notify: true);
        var dropDown = Assert.Single(designer.MenuPanels);
        Assert.Equal(["newToolStripMenuItem", "toolStripSeparator1", "exitToolStripMenuItem"], dropDown.Rows.Select(r => r.Item));
        Assert.True(dropDown.Rows[1].IsSeparator);
        Assert.Equal(("newToolStripMenuItem", (DesignerTypeHere?)null), designer.MenuHitTest(dropDown.Rows[0].Bounds.X + 4, dropDown.Rows[0].Bounds.Y + 4));
        Assert.Equal(dropDown.TypeHere, designer.MenuHitTest(dropDown.TypeHere.Bounds.X + 4, dropDown.TypeHere.Bounds.Y + 4).TypeHere);
        await designer.AddItemAsync(dropDown.TypeHere, "&Save");
        Assert.Contains("fileToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { newToolStripMenuItem, toolStripSeparator1, exitToolStripMenuItem, saveToolStripMenuItem });", Code(designer), StringComparison.Ordinal);
        designer.SetSelection(["saveToolStripMenuItem"], notify: true);
        Assert.Equal(["fileToolStripMenuItem", "saveToolStripMenuItem"], designer.MenuPanels.Select(p => p.Owner));
        await designer.MoveItemAsync("saveToolStripMenuItem", -1);
        Assert.Contains("{ newToolStripMenuItem, toolStripSeparator1, saveToolStripMenuItem, exitToolStripMenuItem }", Code(designer), StringComparison.Ordinal);
        await designer.AddItemAsync(designer.MenuPanels.Single(p => p.Owner == "fileToolStripMenuItem").TypeHere, "-");
        Assert.Contains("toolStripSeparator3 = new ToolStripSeparator();", Code(designer), StringComparison.Ordinal);
        designer.SetSelection(["toolStripSeparator1"], notify: true);
        await designer.DeleteSelectionAsync();
        Assert.Contains("{ newToolStripMenuItem, saveToolStripMenuItem, exitToolStripMenuItem, toolStripSeparator3 }", Code(designer), StringComparison.Ordinal);
        Assert.DoesNotContain("toolStripSeparator1", Code(designer), StringComparison.Ordinal);

        // The tool strip's items in the collection editor: added of a chosen type (live), reordered, their properties.
        await designer.OpenCollectionEditorAsync("mainToolStrip", "System.Windows.Forms.ToolStrip:Items");
        var editor = designer.CollectionEditor!;
        Assert.True(editor is { IsLive: true, HasItemTypes: true, HasProperties: true });
        Assert.Equal(["newToolStripButton", "toolStripSeparator2", "searchToolStripLabel"], editor.Items.Select(i => i.Name));
        Assert.Contains("ToolStripProgressBar", editor.ItemTypes);
        editor.ItemType = "ToolStripButton";
        await editor.AddAsync();
        Assert.Contains("toolStripButton1 = new ToolStripButton();", Code(designer), StringComparison.Ordinal);
        Assert.Equal("toolStripButton1", editor.Selected!.Name);
        await ShellFixture.PumpUntil(() => editor.Properties.Any(p => p.Name == "Text"));
        await editor.MoveAsync(-1);
        Assert.Contains("mainToolStrip.Items.AddRange(new ToolStripItem[] { newToolStripButton, toolStripSeparator2, toolStripButton1, searchToolStripLabel });", Code(designer), StringComparison.Ordinal);
        await editor.RemoveAsync();
        Assert.DoesNotContain("toolStripButton1", Code(designer), StringComparison.Ordinal);
        designer.CloseCollectionEditor();

        // A list's items as text, with OK.
        await designer.OpenCollectionEditorAsync("categoryListBox", "System.Windows.Forms.ListBox:Items");
        editor = designer.CollectionEditor!;
        Assert.Equal("Tools\nGarden\nPaint", editor.Text);
        editor.Text = "Tools\nGarden";
        await editor.OkAsync();
        Assert.Contains("categoryListBox.Items.AddRange(new object[] { \"Tools\", \"Garden\" });", Code(designer), StringComparison.Ordinal);

        // A data binding: (DataBindings) Text to the binding source's other member.
        designer.SetSelection(["nameTextBox"], notify: true);
        var rows = await RowsAsync(f);
        // #68: the bindings are the (DataBindings) row's parts, in Data, not a category of their own.
        Assert.DoesNotContain(f.Shell.PropertiesWindow.Categories, c => c.Title == "(DataBindings)");
        var bindings = Assert.Single(rows, r => r.Name == "(DataBindings)");
        Assert.True(bindings.IsReadOnly);
        var binding = Assert.Single(bindings.Children, r => r.Name == "Text" && r.IsChoice);
        Assert.Contains(binding.Options, o => o.Value == "itemBindingSource - Quantity");
        binding.SelectedOption = binding.Options.Single(o => o.Value == "itemBindingSource - Quantity");
        await ShellFixture.PumpUntil(() => Code(designer).Contains("nameTextBox.DataBindings.Add(new Binding(\"Text\", itemBindingSource, \"Quantity\", true));", StringComparison.Ordinal));
        Assert.DoesNotContain("\"Name\", true", Code(designer), StringComparison.Ordinal);

        // The tray: arranged, as Visual Studio records it, in the .resx's TrayLocation metadata.
        await designer.MoveTrayItemAsync("inventoryErrorProvider", "mainMenuStrip");
        var resx = designer.ResourceModels.Single().Text.Text;
        Assert.Contains("<metadata name=\"inventoryErrorProvider.TrayLocation\" type=\"System.Drawing.Point, System.Drawing, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a\">\n    <value>17, 17</value>", resx, StringComparison.Ordinal);
        await ShellFixture.PumpUntil(() => designer.Tray.Count > 0 && designer.Tray[0].Name == "inventoryErrorProvider", seconds: 30);

        // Localizable on: the host's metadata in the .resx.
        designer.SetSelection([designer.Root!.Name], notify: true);
        rows = await RowsAsync(f);
        var localizable = rows.Single(r => r.Name == "Localizable");
        localizable.SelectedOption = localizable.Options.Single(o => o.Value == "True");
        await ShellFixture.PumpUntil(() => designer.ResourceModels.Single().Text.Text.Contains("$this.Localizable", StringComparison.Ordinal));

        // A localizable form: its resources (and its French ones) to the host; its texts to the language's .resx.
        var about = await DesignAsync(f, Path.Combine(app, "AboutForm.cs"));
        var host = hosts[^1];
        Assert.True(about.IsLocalizable);
        Assert.True(host.LastLoad!.Localizable);
        Assert.Equal(["", "fr"], host.LastLoad.Resources.Select(r => r.Culture).Order(StringComparer.Ordinal));
        about.SetSelection(["versionLabel"], notify: true);
        rows = await RowsAsync(f);
        var text = rows.Single(r => r.Name == "Text");
        Assert.Contains("Localizable", text.Description, StringComparison.Ordinal);
        text.Text = "Version 2.0";
        var aboutResx = about.ResourceModels.Single(m => m.Path.EndsWith("AboutForm.resx", StringComparison.Ordinal));
        await ShellFixture.PumpUntil(() => aboutResx.Text.Text.Contains("<value>Version 2.0</value>", StringComparison.Ordinal));
        Assert.Contains("resources.ApplyResources(this.versionLabel, \"versionLabel\");", Code(about), StringComparison.Ordinal);

        // Language: French loads the French resources; a text goes to AboutForm.fr.resx; German makes AboutForm.de.resx.
        about.SetSelection([about.Root!.Name], notify: true);
        rows = await RowsAsync(f);
        var language = rows.Single(r => r.Name == "Language");
        Assert.Contains(language.Options, o => o.Value == "fr");
        language.SelectedOption = language.Options.Single(o => o.Value == "fr");
        await ShellFixture.PumpUntil(() => host.LastLoad.Culture == "fr" && about.Culture == "fr", seconds: 30);
        await about.SetPropertyAsync(["okButton"], "Text", "Fermer!");
        var french = about.ResourceModels.Single(m => m.Path.EndsWith("AboutForm.fr.resx", StringComparison.Ordinal));
        Assert.Contains("<value>Fermer!</value>", french.Text.Text, StringComparison.Ordinal);
        await about.SetCultureAsync("de");
        await about.SetPropertyAsync(["okButton"], "Text", "Schließen");
        Assert.True(File.Exists(Path.Combine(app, "AboutForm.de.resx")));
        Assert.Contains("<value>Schließen</value>", await File.ReadAllTextAsync(Path.Combine(app, "AboutForm.de.resx"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
        await ShellFixture.PumpUntil(() => about.ResourceModels.Any(m => m.Path.EndsWith("AboutForm.de.resx", StringComparison.Ordinal)));
        await about.SetCultureAsync("(Default)");
        Assert.Equal(string.Empty, about.Culture);
    }
}
