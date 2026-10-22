using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using static Codalus.Shell.Tests.WinFormsDesignerTestHost;

namespace Codalus.Shell.Tests;

/// <summary>
/// #68 (D41) in the headless shell on samples/WindowsDesktop's Windows Forms app, with the fake designer host (any OS)
/// that answers with the real host's shapes, through the real build and C# hosts, so the code is the real writer's:
/// categories collapse and stay so; a part (Location.X, Font.Bold) is written as one assignment of the whole value;
/// standard values are choices (AcceptButton's "(none)" and buttons, Font.Name's families, Cursor's that take typing);
/// colors, alignment, multi-line text and Reset; the component selector, Property Pages and double-click; an invalid
/// value's dialog (OK goes back to the text, Cancel puts the old value back); data bindings as (DataBindings)'s parts.
/// </summary>
public sealed class WinFormsPropertiesShellTests
{
    private static async Task<(ShellFixture Fixture, WinFormsDesignerDocument Designer, FakeWinFormsDesignerHost Host)> OrderFormAsync(string selected)
    {
        var (f, app, hosts) = await OpenAsync();
        var designer = await DesignAsync(f, Path.Combine(app, "OrderForm.cs"));
        designer.SetSelection([selected], notify: true);
        await RowsAsync(f);
        return (f, designer, hosts[^1]);
    }

    private static PropertyGridViewModel Grid(ShellFixture f) => f.Shell.PropertiesWindow.Grid;

    private static async Task<PropertyRowViewModel> RowAsync(ShellFixture f, string path, Func<PropertyRowViewModel, bool>? until = null)
    {
        await ShellFixture.PumpUntil(() => Grid(f).Find(path) is { } row && (until?.Invoke(row) ?? true) && !f.Shell.PropertiesWindow.IsLoading, seconds: 30,
            () => string.Join(", ", Grid(f).AllRows.Select(r => $"{r.Path}={r.Value}")));
        return Grid(f).Find(path)!;
    }

    [AvaloniaFact]
    public async Task Categories_collapse_and_expandable_properties_write_one_assignment()
    {
        var (f, designer, host) = await OrderFormAsync("giftCheckBox");
        await using var _ = f;
        var location = await RowAsync(f, "Location");
        Assert.Equal(["X", "Y"], location.Children.Select(c => c.Name));
        Assert.Equal("96", location.Children[0].Value);
        Assert.True(location.Children[0].IsSet);

        (await RowAsync(f, "Location.X")).Text = "100";
        await ShellFixture.PumpUntil(() => Code(designer).Contains("giftCheckBox.Location = new Point(100, 70);", StringComparison.Ordinal), seconds: 30);
        Assert.Equal(["Location.X"], host.LastEdits.Select(e => e.Property));
        Assert.Equal("100", (await RowAsync(f, "Location.X", r => r.Value == "100")).Value);

        // Layout collapsed and Location expanded stay so for another control and back.
        var grid = Grid(f);
        grid.Toggle(grid.Find("Location")!);
        grid.Toggle(grid.Categories.Single(c => c.Title == "Layout"));
        Assert.DoesNotContain(grid.Items, i => i is PropertyRowViewModel { Name: "Dock" });
        designer.SetSelection(["customerTextBox"], notify: true);
        await ShellFixture.PumpUntil(() => f.Shell.PropertiesWindow.Title.StartsWith("customerTextBox", StringComparison.Ordinal) && !f.Shell.PropertiesWindow.IsLoading);
        designer.SetSelection(["giftCheckBox"], notify: true);
        await ShellFixture.PumpUntil(() => f.Shell.PropertiesWindow.Title.StartsWith("giftCheckBox", StringComparison.Ordinal) && !f.Shell.PropertiesWindow.IsLoading);
        Assert.False(grid.Categories.Single(c => c.Title == "Layout").IsExpanded);
        Assert.True(grid.Find("Location")!.IsExpanded);
        Assert.DoesNotContain(grid.Items, i => i is PropertyRowViewModel { Name: "Dock" });

        // Design shows (Name) only (D41 question 9): Locked isn't in the grid.
        Assert.Equal(["(Name)"], grid.Categories.Single(c => c.Title == "Design").Rows.Select(r => r.Name));
    }

    [AvaloniaFact]
    public async Task Font_parts_are_choices_and_change_the_font_in_one_assignment()
    {
        var (f, designer, host) = await OrderFormAsync("notesTextBox");
        await using var _ = f;
        var font = await RowAsync(f, "Font");
        Assert.Equal(PropertyRowKind.Font, font.Kind);
        Assert.True(font.IsSet);
        Assert.Equal("Segoe UI, 9pt, style=Italic", font.Value);
        Assert.Equal(["Name", "Size", "Unit", "Bold", "GdiCharSet", "GdiVerticalFont", "Italic", "Strikeout", "Underline"], font.Children.Select(c => c.Name));
        var name = font.Children[0];
        Assert.True(name is { IsChoice: true, ChoicesEditable: true }); // FontNameConverter's list isn't exclusive: any name can be typed
        Assert.Contains(name.Options, o => o.Value == "Segoe UI");
        // GdiCharSet is a settable byte and GdiVerticalFont a True/False choice, as the real host reports them and the mockup draws them.
        Assert.True(font.Children.Single(c => c.Name == "GdiCharSet") is { IsReadOnly: false, IsText: true });
        Assert.True(font.Children.Single(c => c.Name == "GdiVerticalFont") is { IsReadOnly: false, IsChoice: true });

        var bold = font.Children.Single(c => c.Name == "Bold");
        bold.SelectedOption = bold.Options.Single(o => o.Value == "True");
        await ShellFixture.PumpUntil(() => Code(designer).Contains("notesTextBox.Font = new Font(\"Segoe UI\", 9F, FontStyle.Bold | FontStyle.Italic, GraphicsUnit.Point, 0);", StringComparison.Ordinal), seconds: 30);
        Assert.Equal(["Font.Bold"], host.LastEdits.Select(e => e.Property));
        Assert.Same(font, await RowAsync(f, "Font", r => r.Value.Contains("Bold", StringComparison.Ordinal)));

        // One undo puts the old line back.
        designer.Undo();
        await ShellFixture.PumpUntil(() => Code(designer).Contains("notesTextBox.Font = new Font(\"Segoe UI\", 9F, FontStyle.Italic, GraphicsUnit.Point, 0);", StringComparison.Ordinal), seconds: 30);
    }

    [AvaloniaFact]
    public async Task The_font_editor_sends_the_changed_parts_in_one_request()
    {
        var (f, designer, host) = await OrderFormAsync("notesTextBox");
        await using var _ = f;
        var font = await RowAsync(f, "Font");
        Assert.True(font is { HasModalEditor: true, ShowsModalButton: false });
        Grid(f).SelectedRow = font;
        Assert.True(font.ShowsModalButton);
        font.ModalEditor!.Execute(null);

        // A panel over the surface in the in-window dialog's frame (D41 question 2); the Properties window takes no input meanwhile.
        var editor = designer.FontEditor!;
        Assert.NotNull(editor);
        Assert.Equal("notesTextBox · Font", editor.Context);
        Assert.Equal(FakeWinFormsDesignerHost.FontFamilies, editor.Families);
        Assert.True(editor is { Family: "Segoe UI", Size: "9", IsItalic: true, IsBold: false, Strikeout: false, Underline: false });
        await ShellFixture.PumpUntil(() => !f.Shell.PropertiesWindow.IsInputEnabled);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<Avalonia.Controls.Border>()
            .Any(b => Avalonia.Automation.AutomationProperties.GetName(b) == editor.Title && b.IsEffectivelyVisible));

        editor.Size = "12";
        editor.Style = editor.Styles[3];
        Assert.Equal(["Font.Size", "Font.Bold"], editor.Edits().Select(e => e.Property));
        await editor.OkAsync();
        Assert.Null(designer.FontEditor);
        await ShellFixture.PumpUntil(() => Code(designer).Contains("notesTextBox.Font = new Font(\"Segoe UI\", 12F, FontStyle.Bold | FontStyle.Italic, GraphicsUnit.Point, 0);", StringComparison.Ordinal), seconds: 30);
        Assert.Equal(["Font.Size", "Font.Bold"], host.LastEdits.Select(e => e.Property));
        await ShellFixture.PumpUntil(() => f.Shell.PropertiesWindow.IsInputEnabled);
        Assert.Equal("12", (await RowAsync(f, "Font.Size", r => r.Value == "12")).Value);

        // One request, one undo step.
        designer.Undo();
        await ShellFixture.PumpUntil(() => Code(designer).Contains("notesTextBox.Font = new Font(\"Segoe UI\", 9F, FontStyle.Italic, GraphicsUnit.Point, 0);", StringComparison.Ordinal), seconds: 30);

        // Cancel sends nothing: no request reaches the host, and after the dispatcher has run the code and the grid are unchanged.
        (await RowAsync(f, "Font", r => r.Value.StartsWith("Segoe UI, 9pt", StringComparison.Ordinal))).ModalEditor!.Execute(null);
        var calls = host.SetPropertiesCalls;
        designer.FontEditor!.Size = "20";
        designer.FontEditor.CancelCommand.Execute(null);
        Assert.Null(designer.FontEditor);
        await DrainAsync();
        Assert.Equal(calls, host.SetPropertiesCalls);
        Assert.Equal(["Font.Size", "Font.Bold"], host.LastEdits.Select(e => e.Property));
        Assert.Contains("notesTextBox.Font = new Font(\"Segoe UI\", 9F, FontStyle.Italic, GraphicsUnit.Point, 0);", Code(designer), StringComparison.Ordinal);
        Assert.Equal("Segoe UI, 9pt, style=Italic", Grid(f).Find("Font")!.Value);
    }

    /// <summary>Runs the dispatcher for a while (an edit sent by mistake would have reached the code by then).</summary>
    private static async Task DrainAsync()
    {
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            ShellFixture.Pump();
        }
    }

    [AvaloniaFact]
    public async Task A_font_editor_ok_with_an_invalid_size_changes_nothing()
    {
        var (f, designer, host) = await OrderFormAsync("notesTextBox");
        await using var _ = f;
        var before = Code(designer);
        (await RowAsync(f, "Font")).ModalEditor!.Execute(null);
        var editor = designer.FontEditor!;
        editor.Family = "Arial";
        editor.Size = "abc";
        await editor.OkAsync();
        Assert.Equal(["Font.Name", "Font.Size"], host.LastEdits.Select(e => e.Property));
        await ShellFixture.PumpUntil(() => designer.Status.Contains("not a valid value", StringComparison.Ordinal));
        await DrainAsync();
        Assert.Equal(before, Code(designer));

        // The host kept none of it (all or none, as the real host): the next change doesn't bring Arial along.
        var bold = (await RowAsync(f, "Font.Bold"));
        bold.SelectedOption = bold.Options.Single(o => o.Value == "True");
        await ShellFixture.PumpUntil(() => Code(designer).Contains("notesTextBox.Font = new Font(\"Segoe UI\", 9F, FontStyle.Bold | FontStyle.Italic, GraphicsUnit.Point, 0);", StringComparison.Ordinal), seconds: 30, () => Code(designer));
    }

    [AvaloniaFact]
    public async Task Several_components_merge_their_values_and_the_font_editor_sends_the_changed_parts_to_each()
    {
        var (f, designer, host) = await OrderFormAsync("customerTextBox");
        await using var _ = f;
        (await RowAsync(f, "Font.Size")).Text = "12";
        await ShellFixture.PumpUntil(() => Code(designer).Contains("customerTextBox.Font = new Font(\"Segoe UI\", 12F", StringComparison.Ordinal), seconds: 30);

        // notesTextBox (9pt, italic) and customerTextBox (12pt): the font and its size and style blank, its name shared.
        designer.SetSelection(["notesTextBox", "customerTextBox"], notify: true);
        var font = await RowAsync(f, "Font", r => r.IsMixed);
        Assert.Equal(string.Empty, font.DisplayValue);
        Assert.True(Grid(f).Find("Font.Size") is { IsMixed: true, DisplayValue: "", Text: "" });
        Assert.True(Grid(f).Find("Font.Italic") is { IsMixed: true });
        Assert.True(Grid(f).Find("Font.Name") is { IsMixed: false, Value: "Segoe UI" });
        Assert.Null(f.Shell.PropertiesWindow.SelectedComponent);

        // The font editor opens with what they share; OK sends only the changed part, for both.
        font.ModalEditor!.Execute(null);
        var editor = designer.FontEditor!;
        Assert.True(editor is { Family: "Segoe UI", Size: "", IsItalic: false, IsBold: false });
        editor.Style = editor.Styles.First(st => st.Contains("Bold", StringComparison.Ordinal) && !st.Contains("Italic", StringComparison.Ordinal));
        Assert.Equal([("notesTextBox", "Font.Bold"), ("customerTextBox", "Font.Bold")], editor.Edits().Select(e => (e.Component, e.Property)));
        await editor.OkAsync();
        await ShellFixture.PumpUntil(() => Code(designer).Contains("customerTextBox.Font = new Font(\"Segoe UI\", 12F, FontStyle.Bold, GraphicsUnit.Point, 0);", StringComparison.Ordinal)
            && Code(designer).Contains("notesTextBox.Font = new Font(\"Segoe UI\", 9F, FontStyle.Bold | FontStyle.Italic, GraphicsUnit.Point, 0);", StringComparison.Ordinal), seconds: 30, () => Code(designer));
        Assert.Equal([("notesTextBox", "Font.Bold"), ("customerTextBox", "Font.Bold")], host.LastEdits.Select(e => (e.Component, e.Property)));
    }

    [AvaloniaFact]
    public async Task Windows_forms_dialog_editors_are_typed_or_read_only_with_a_note()
    {
        var (f, designer, _) = await OrderFormAsync("quantityUpDown");
        await using var _f = f;

        // A numeric up-down's Accelerations: a dialog whose converter takes no text, so read-only, no "…", and a note.
        var accelerations = await RowAsync(f, "Accelerations");
        Assert.True(accelerations is { IsReadOnly: true, HasModalEditor: false, ShowsTextBox: false });
        Assert.Contains("set it in the code", accelerations.Description, StringComparison.Ordinal);

        // A masked text box's Mask: typed in the cell, no "…", and a note.
        var root = designer.Root!;
        await designer.CreateAsync("System.Windows.Forms.MaskedTextBox", root.ClientX + 300, root.ClientY + 300);
        var masked = Assert.Single(designer.Selection);
        Assert.Equal("System.Windows.Forms.MaskedTextBox", designer.Find(masked)!.Type);
        designer.SetSelection([masked], notify: true);
        var mask = await RowAsync(f, "Mask", r => f.Shell.PropertiesWindow.Title.StartsWith(masked, StringComparison.Ordinal));
        Assert.True(mask is { IsReadOnly: false, IsText: true, HasModalEditor: false, ShowsTextBox: true });
        Assert.Contains("type the value here", mask.Description, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_string_array_is_edited_in_the_string_collection_editor()
    {
        var (f, designer, _) = await OrderFormAsync("notesTextBox");
        await using var _f = f;
        var lines = await RowAsync(f, "Lines");
        Assert.True(lines is { IsReadOnly: true, HasModalEditor: true });
        lines.ModalEditor!.Execute(null);
        await ShellFixture.PumpUntil(() => designer.StringsEditor is not null);
        var editor = designer.StringsEditor!;
        Assert.Equal("String Collection Editor", editor.Title);
        Assert.Equal("notesTextBox · Lines", editor.Context);
        editor.Text = "one\ntwo\n";
        await editor.OkAsync();
        Assert.Null(designer.StringsEditor);
        // Lines is hidden from serialization: the lines are written as the box's Text, as the classic IDE writes them.
        await ShellFixture.PumpUntil(() => Code(designer).Contains("notesTextBox.Text = \"one\\r\\ntwo\";", StringComparison.Ordinal), seconds: 30);
        Assert.DoesNotContain("notesTextBox.Lines", Code(designer), StringComparison.Ordinal);

        // Opened again, it shows the array's items a line each, with CRLF.
        (await RowAsync(f, "Lines", r => r.IsSet)).ModalEditor!.Execute(null);
        await ShellFixture.PumpUntil(() => designer.StringsEditor is not null);
        Assert.Equal("one\r\ntwo", designer.StringsEditor!.Text);
        designer.CloseStringsEditor();
    }

    [AvaloniaFact]
    public async Task Select_resource_sets_a_local_or_a_project_image_or_clears_it()
    {
        var (f, app, _) = await OpenAsync();
        await using var _f = f;
        var designer = await DesignAsync(f, Path.Combine(app, "OrderForm.cs"));
        designer.SetSelection(["placeOrderButton"], notify: true);
        await RowsAsync(f);
        var png = Path.Combine(app, "logo.png");
        await File.WriteAllBytesAsync(png, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="), TestContext.Current.CancellationToken);

        // Local resource: Import… shows the image; OK puts it in the form's resources.
        (await RowAsync(f, "Image")).ModalEditor!.Execute(null);
        await ShellFixture.PumpUntil(() => designer.ResourcePicker is not null);
        var picker = designer.ResourcePicker!;
        Assert.True(picker is { Title: "Select Resource", Context: "placeOrderButton · Image", IsLocal: true, HasResourceFile: true });
        Assert.Empty(picker.ProjectResources); // Properties/Resources.resx has a string only
        Assert.Equal(["Properties\\Resources.resx"], picker.ResourceFiles);
        Assert.True(designer.HasOpenEditor);

        // As panel (k) draws it: the project side grayed while Local resource is chosen, its list "(none)" first.
        var panel = await PanelAsync(f, "Select Resource");
        Button Named(string name) => panel.GetVisualDescendants().OfType<Button>().First(b => Avalonia.Automation.AutomationProperties.GetName(b) == name);
        var list = panel.GetVisualDescendants().OfType<ListBox>().Single();
        Assert.True(Named("Clear").IsEffectivelyEnabled);
        Assert.False(list.IsEffectivelyEnabled);
        Assert.Same(DesignerResourcePickerViewModel.NoResource, list.Items[0]);
        Assert.Equal(2, panel.GetVisualDescendants().OfType<Button>().Count(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Import…" && Controls.ButtonIcon.GetKind(b) == "import"));
        await picker.ImportLocalAsync(png);
        Assert.Equal("logo.png", picker.LocalText);
        Assert.NotNull(picker.Preview);
        await ShellFixture.PumpUntil(() => panel.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "logo.png · 1 × 1" && t.IsEffectivelyVisible));
        await picker.OkAsync();
        Assert.Null(designer.ResourcePicker);
        await ShellFixture.PumpUntil(() => Code(designer).Contains("placeOrderButton.Image = (Image)resources.GetObject(\"placeOrderButton.Image\");", StringComparison.Ordinal), seconds: 30);

        // Project resource file: Import… adds the file to the list; OK copies it into the project and the code reads Properties.Resources.
        (await RowAsync(f, "Image")).ModalEditor!.Execute(null);
        await ShellFixture.PumpUntil(() => designer.ResourcePicker is not null);
        picker = designer.ResourcePicker!;
        await picker.ImportProjectAsync(png);
        Assert.True(picker is { IsProject: true, SelectedResource: { Name: "logo", IsNew: true } });
        panel = await PanelAsync(f, "Select Resource");
        Assert.False(panel.GetVisualDescendants().OfType<Button>().First(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Clear").IsEffectivelyEnabled);
        Assert.True(panel.GetVisualDescendants().OfType<ListBox>().Single().IsEffectivelyEnabled);
        await ShellFixture.PumpUntil(() => picker.Preview is not null);
        NavigationDepthTests.Screenshot(f, "m68-select-resource");
        await picker.OkAsync();
        await ShellFixture.PumpUntil(() => Code(designer).Contains("placeOrderButton.Image = Properties.Resources.logo;", StringComparison.Ordinal)
            || Code(designer).Contains("placeOrderButton.Image = WinFormsApp.Properties.Resources.logo;", StringComparison.Ordinal), seconds: 30, () => Code(designer));
        var resx = Path.Combine(app, "Properties", "Resources.resx");
        Assert.Contains("<data name=\"logo\" type=\"System.Resources.ResXFileRef, System.Windows.Forms\">", await File.ReadAllTextAsync(resx, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(app, "Resources", "logo.png")));

        // Opened again: the project's image is listed and chosen, with its preview; "(none)" in the list and OK remove the image.
        (await RowAsync(f, "Image")).ModalEditor!.Execute(null);
        await ShellFixture.PumpUntil(() => designer.ResourcePicker is { IsProject: true, Preview: not null });
        picker = designer.ResourcePicker!;
        Assert.Equal(["logo"], picker.ProjectResources.Select(r => r.Name));
        Assert.Equal("logo", picker.SelectedResource?.Name);
        panel = await PanelAsync(f, "Select Resource");
        list = panel.GetVisualDescendants().OfType<ListBox>().Single();
        Assert.Equal("logo", (list.SelectedItem as DesignerProjectResource)?.Name);
        list.SelectedIndex = 0;
        Assert.Null(picker.SelectedResource);
        await picker.OkAsync();
        await ShellFixture.PumpUntil(() => !Code(designer).Contains("placeOrderButton.Image =", StringComparison.Ordinal), seconds: 30);
        Assert.False(designer.HasOpenEditor);

        // Clear on Local resource removes it too.
        (await RowAsync(f, "Image")).ModalEditor!.Execute(null);
        await ShellFixture.PumpUntil(() => designer.ResourcePicker is not null);
        await designer.ResourcePicker!.ImportLocalAsync(png);
        await designer.ResourcePicker!.OkAsync();
        await ShellFixture.PumpUntil(() => Code(designer).Contains("placeOrderButton.Image = (Image)resources.GetObject(\"placeOrderButton.Image\");", StringComparison.Ordinal), seconds: 30);
        (await RowAsync(f, "Image")).ModalEditor!.Execute(null);
        await ShellFixture.PumpUntil(() => designer.ResourcePicker is not null);
        designer.ResourcePicker!.Clear();
        Assert.True(designer.ResourcePicker.IsLocal);
        await designer.ResourcePicker.OkAsync();
        await ShellFixture.PumpUntil(() => !Code(designer).Contains("placeOrderButton.Image =", StringComparison.Ordinal), seconds: 30);
    }

    /// <summary>An editor panel over the designer by its title (the in-window dialog's frame).</summary>
    private static async Task<Border> PanelAsync(ShellFixture f, string title)
    {
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<Border>().Any(b => Avalonia.Automation.AutomationProperties.GetName(b) == title && b.IsEffectivelyVisible));
        return f.Window.GetVisualDescendants().OfType<Border>().First(b => Avalonia.Automation.AutomationProperties.GetName(b) == title && b.IsEffectivelyVisible);
    }

    [Fact]
    public void A_resources_files_images_are_its_file_references_and_embedded_images()
    {
        var resx = Path.Combine(Path.GetTempPath(), "p", "Properties", "Resources.resx");
        var images = DesignerProjectResource.ImagesOf(resx, """
            <root>
              <data name="Greeting" xml:space="preserve"><value>Hi</value></data>
              <data name="logo" type="System.Resources.ResXFileRef, System.Windows.Forms"><value>..\Resources\logo.png;System.Drawing.Bitmap, System.Drawing, Version=4.0.0.0</value></data>
              <data name="readme" type="System.Resources.ResXFileRef, System.Windows.Forms"><value>..\Resources\readme.txt;System.String, mscorlib</value></data>
              <data name="app" type="System.Drawing.Icon, System.Drawing" mimetype="application/x-microsoft.net.object.bytearray.base64"><value>AAAB</value></data>
            </root>
            """);
        Assert.Equal(["logo", "app"], images.Select(i => i.Name));
        Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "p", "Resources", "logo.png")), images[0].FilePath);
        Assert.Equal("AAAB", images[1].Base64);
        Assert.Equal("_2x_logo", WinFormsDesignerDocument.ResourceNameOf("/x/2x-logo.png"));
    }

    [AvaloniaFact]
    public async Task The_collection_editor_shares_the_grid_and_reads_its_item_again()
    {
        var (f, app, _) = await OpenAsync();
        await using var _f = f;
        var designer = await DesignAsync(f, Path.Combine(app, "InventoryForm.cs"));
        await designer.OpenCollectionEditorAsync("mainMenuStrip", "System.Windows.Forms.MenuStrip:Items");
        var editor = designer.CollectionEditor!;
        Assert.True(editor.HasProperties);
        editor.Selected = editor.Items.Single(i => i.Name == "fileToolStripMenuItem");
        await ShellFixture.PumpUntil(() => editor.Grid.Find("Font") is not null, seconds: 30);
        Assert.Equal(["(Name)", "Font", "ShortcutKeys", "Text"], editor.Grid.AllRows.Where(r => r.Depth == 0).Select(r => r.Name).Order(StringComparer.OrdinalIgnoreCase));
        var font = editor.Grid.Find("Font")!;
        editor.Grid.Toggle(font);
        var bold = editor.Grid.Find("Font.Bold")!;
        bold.SelectedOption = bold.Options.Single(o => o.Value == "True");
        await ShellFixture.PumpUntil(() => Code(designer).Contains("fileToolStripMenuItem.Font = new Font(\"Segoe UI\", 9F, FontStyle.Bold", StringComparison.Ordinal), seconds: 30);

        // Read again in place: the same rows, the part True, Font still expanded.
        await ShellFixture.PumpUntil(() => editor.Grid.Find("Font.Bold")?.Value == "True", seconds: 30);
        Assert.Same(font, editor.Grid.Find("Font"));
        Assert.True(font.IsExpanded);

        // An undo reloads the code, and the item grid reads its item again, in place.
        designer.Undo();
        await ShellFixture.PumpUntil(() => editor.Grid.Find("Font.Bold")?.Value == "False", seconds: 30, () => Code(designer));
        Assert.DoesNotContain("fileToolStripMenuItem.Font =", Code(designer), StringComparison.Ordinal);
        Assert.Same(font, editor.Grid.Find("Font"));
        Assert.True(font.IsExpanded);

        // F4 in the item grid opens the selected row's drop-down there (the global F4 shows the Properties window elsewhere).
        f.Shell.Layout.CloseToolWindow(PropertiesWindowViewModel.ToolWindowId);
        var itemGrid = await GridViewOfAsync(f, editor.Grid);
        var keys = editor.Grid.Find("ShortcutKeys")!;
        editor.Grid.SelectedRow = keys;
        itemGrid.FocusPrimary();
        await ShellFixture.PumpUntil(() => Equals(f.Shell.Workbench.Context.GetValue("propertyGridFocus"), true));
        f.Press(Key.F4);
        await ShellFixture.PumpUntil(() => itemGrid.OpenDropDownControl is Views.PropertyEditors.ShortcutKeysDropDown);
        Assert.False(f.Shell.Layout.IsVisible(PropertiesWindowViewModel.ToolWindowId));
        f.Press(Key.Escape);
        await ShellFixture.PumpUntil(() => itemGrid.OpenDropDownControl is null);

        // Another item: the same grid view, listening still (a view per item stayed subscribed to the grid before).
        var other = editor.Items.First(i => i.Name is { } itemName && itemName != "fileToolStripMenuItem" && itemName.EndsWith("ToolStripMenuItem", StringComparison.Ordinal));
        editor.Selected = other;
        await ShellFixture.PumpUntil(() => editor.Grid.Find("(Name)")?.Value == other.Name, seconds: 30);
        Assert.Same(itemGrid, await GridViewOfAsync(f, editor.Grid));
        Assert.Single(f.Window.GetVisualDescendants().OfType<Views.PropertyGridView>(), v => ReferenceEquals(v.DataContext, editor.Grid));
        editor.Selected = editor.Items.Single(i => i.Name == "fileToolStripMenuItem");
        await ShellFixture.PumpUntil(() => editor.Grid.Find("(Name)")?.Value == "fileToolStripMenuItem", seconds: 30);
        font = editor.Grid.Find("Font")!;

        // The font editor opens over the collection editor; the item grid takes no input meanwhile.
        font.ModalEditor!.Execute(null);
        Assert.Equal("fileToolStripMenuItem · Font", designer.FontEditor!.Context);
        Assert.False(editor.Grid.IsEnabled);
        Assert.True(designer.HasOpenEditor);
        designer.FontEditor.CancelCommand.Execute(null);
        Assert.True(editor.Grid.IsEnabled);
        Assert.Same(editor, designer.CollectionEditor);
        designer.CloseCollectionEditor();
        Assert.False(designer.HasOpenEditor);
    }

    private static async Task<Views.PropertyGridView> GridViewOfAsync(ShellFixture f, PropertyGridViewModel grid)
    {
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<Views.PropertyGridView>().Any(v => ReferenceEquals(v.DataContext, grid) && v.IsEffectivelyVisible));
        return f.Window.GetVisualDescendants().OfType<Views.PropertyGridView>().First(v => ReferenceEquals(v.DataContext, grid) && v.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Colors_alignment_text_and_reset()
    {
        var (f, designer, _) = await OrderFormAsync("placeOrderButton");
        await using var _f = f;
        var fore = await RowAsync(f, "ForeColor");
        Assert.True(fore is { IsColor: true, IsSet: true, Value: "White" });
        Assert.NotNull(fore.Swatch);
        // The color drop-down (D41 question 18): the System tab has GrayText; a Web color is written as Color's.
        f.Shell.Layout.ShowToolWindow(PropertiesWindowViewModel.ToolWindowId, focus: false);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<Views.PropertiesWindowView>().Any(v => v.IsEffectivelyVisible));
        var view = f.Window.GetVisualDescendants().OfType<Views.PropertiesWindowView>().First(v => v.IsEffectivelyVisible);
        view.Grid.OpenDropDown(fore);
        var color = Assert.IsType<Views.PropertyEditors.ColorDropDown>(view.Grid.OpenDropDownControl);
        Assert.Equal(PropertyColors.WebTab, color.Tab);
        color.SelectTab(PropertyColors.SystemTab);
        Assert.Contains(color.SystemColors, c => c.Name == "GrayText");
        color.SelectTab(PropertyColors.WebTab);
        color.PickNamed("Red");
        await ShellFixture.PumpUntil(() => Code(designer).Contains("placeOrderButton.ForeColor = Color.Red;", StringComparison.Ordinal), seconds: 30);
        (await RowAsync(f, "ForeColor", r => r.Value == "Red")).Text = "255, 128, 0";
        await ShellFixture.PumpUntil(() => Code(designer).Contains("placeOrderButton.ForeColor = Color.FromArgb(255, 128, 0);", StringComparison.Ordinal), seconds: 30);
        (await RowAsync(f, "BackColor")).Text = "GrayText";
        await ShellFixture.PumpUntil(() => Code(designer).Contains("placeOrderButton.BackColor = SystemColors.GrayText;", StringComparison.Ordinal), seconds: 30);

        var align = await RowAsync(f, "TextAlign");
        Assert.Equal(PropertyRowKind.ContentAlignment, align.Kind);
        view.Grid.OpenDropDown(align);
        Assert.IsType<Views.PropertyEditors.ContentAlignmentDropDown>(view.Grid.OpenDropDownControl).Pick("MiddleRight");
        await ShellFixture.PumpUntil(() => Code(designer).Contains("placeOrderButton.TextAlign = ContentAlignment.MiddleRight;", StringComparison.Ordinal), seconds: 30);

        // The multi-line Text row is typed through its text box; a value with a line break isn't (D41 question 16).
        var text = await RowAsync(f, "Text");
        Assert.True(text is { Kind: PropertyRowKind.Multiline, IsTextEditor: true, ShowsTextBox: true });
        text.Text = "a\r\nb";
        await ShellFixture.PumpUntil(() => Code(designer).Contains("placeOrderButton.Text = \"a\\r\\nb\";", StringComparison.Ordinal), seconds: 30);
        text = await RowAsync(f, "Text", r => r.HasLineBreaks);
        Assert.False(text.ShowsTextBox);
        Assert.Equal("a ⏎ b", text.DisplayValue);

        // Reset removes the assignment; (Name) can't be reset.
        Assert.True(text.ResetCommand.CanExecute(null));
        text.ResetCommand.Execute(null);
        await ShellFixture.PumpUntil(() => !Code(designer).Contains("placeOrderButton.Text =", StringComparison.Ordinal), seconds: 30);
        Assert.False((await RowAsync(f, "(Name)")).ResetCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Choices_selector_property_pages_and_double_click()
    {
        var (f, designer, _) = await OrderFormAsync("placeOrderButton");
        await using var _f = f;
        var properties = f.Shell.PropertiesWindow;

        // The component selector: the form first, then the components by name; picking one selects it.
        Assert.Equal("OrderForm", properties.ComponentChoices[0].Name);
        Assert.Contains(properties.ComponentChoices, c => c is { Name: "placeOrderButton", Type: "System.Windows.Forms.Button" });
        Assert.DoesNotContain(properties.ComponentChoices, c => c.Name.Contains('.', StringComparison.Ordinal));
        Assert.Equal("placeOrderButton", properties.SelectedComponent?.Name);
        Assert.False(properties.PropertyPagesCommand.CanExecute(null));

        // Cursor is a choice that takes typing (D41 question 13), in Appearance.
        var cursor = await RowAsync(f, "Cursor");
        Assert.True(cursor is { IsChoice: true, ChoicesEditable: true, IsTextEditor: true, IsCursor: true });
        Assert.Contains(Grid(f).Categories.Single(c => c.Title == "Appearance").Rows, r => r.Name == "Cursor");

        // A double-click on Enabled's value cycles it.
        (await RowAsync(f, "Enabled")).DoubleClick();
        await ShellFixture.PumpUntil(() => Code(designer).Contains("placeOrderButton.Enabled = false;", StringComparison.Ordinal), seconds: 30);

        // The form's AcceptButton: "(none)", then its buttons; choosing cancelButton writes it.
        properties.SelectedComponent = properties.ComponentChoices[0];
        await ShellFixture.PumpUntil(() => designer.Selection is ["OrderForm"] && properties.Title.StartsWith("OrderForm", StringComparison.Ordinal));
        var accept = await RowAsync(f, "AcceptButton");
        Assert.True(accept is { IsChoice: true, ChoicesEditable: false, Value: "placeOrderButton" });
        Assert.Equal(["(none)", "cancelButton", "placeOrderButton"], accept.Options.Select(o => o.Value));
        accept.SelectedOption = accept.Options.Single(o => o.Value == "cancelButton");
        await ShellFixture.PumpUntil(() => Code(designer).Contains("            AcceptButton = cancelButton;", StringComparison.Ordinal), seconds: 30);

        // CancelButton reset: an unset reference is "(none)", the first of its list and listed once (D41 question 17).
        var cancelReference = await RowAsync(f, "CancelButton");
        cancelReference.ResetCommand.Execute(null);
        await ShellFixture.PumpUntil(() => !Code(designer).Contains("CancelButton = cancelButton;", StringComparison.Ordinal), seconds: 30);
        cancelReference = await RowAsync(f, "CancelButton", r => r.Value == "(none)");
        Assert.Equal(["(none)", "cancelButton", "placeOrderButton"], cancelReference.Options.Select(o => o.Value));
        Assert.Same(cancelReference.Options[0], cancelReference.SelectedOption);

        // The Events view: a double-click on Click writes and shows the handler.
        designer.SetSelection(["cancelButton"], notify: true);
        properties.ShowEvents = true;
        await ShellFixture.PumpUntil(() => Grid(f).Find("Click") is not null && properties.Title.StartsWith("cancelButton", StringComparison.Ordinal) && !properties.IsLoading);
        Grid(f).Find("Click")!.DoubleClick();
        await ShellFixture.PumpUntil(() => Code(designer).Contains("cancelButton.Click += cancelButton_Click;", StringComparison.Ordinal), seconds: 30);
        properties.ShowEvents = false;
    }

    [AvaloniaFact]
    public async Task The_events_view_follows_the_selection()
    {
        var (f, designer, _) = await OrderFormAsync("placeOrderButton");
        await using var _f = f;
        var properties = f.Shell.PropertiesWindow;

        // Events with placeOrderButton (its Click is hooked to OnPlaceOrderClicked), then cancelButton chosen on the surface.
        properties.ShowEvents = true;
        var click = await RowAsync(f, "Click", r => r.Value == "OnPlaceOrderClicked");
        Assert.True(properties.IsShowingEvents);
        designer.SetSelection(["cancelButton"], notify: true);
        await RowAsync(f, "Click", r => r.Value.Length == 0 && properties.Title.StartsWith("cancelButton", StringComparison.Ordinal));
        Assert.Same(click, Grid(f).Find("Click")); // the row is refreshed in place, its action with it

        // The double-click acts on cancelButton, the component shown, not on placeOrderButton.
        Grid(f).Find("Click")!.DoubleClick();
        await ShellFixture.PumpUntil(() => Code(designer).Contains("cancelButton.Click += cancelButton_Click;", StringComparison.Ordinal), seconds: 30, () => Code(designer));
        Assert.DoesNotContain("placeOrderButton_Click", Code(designer), StringComparison.Ordinal);

        // Two components: their properties, Properties checked and Events grayed (D41 panel r); one again: its events.
        designer.SetSelection(["placeOrderButton", "cancelButton"], notify: true);
        await RowAsync(f, "Text", r => !properties.IsShowingEvents);
        Assert.True(properties.ShowEvents);
        Assert.False(properties.EventsCommand.CanExecute(null));
        designer.SetSelection(["cancelButton"], notify: true);
        await RowAsync(f, "Click", r => properties.IsShowingEvents);
        properties.ShowEvents = false;
    }

    [AvaloniaFact]
    public async Task Invalid_values_are_reported_and_corrected_or_put_back()
    {
        var (f, designer, _) = await OrderFormAsync("giftCheckBox");
        await using var _f = f;
        f.Shell.Layout.ShowToolWindow(PropertiesWindowViewModel.ToolWindowId, focus: false);
        var view = await PropertiesViewAsync(f);
        var x = await RowAsync(f, "Location.X");
        Grid(f).SelectedRow = Grid(f).Find("Text");
        x.Text = "abc";
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        Assert.Equal("Properties Window", f.Shell.Dialogs.Title);
        Assert.StartsWith("Property value is not valid.", f.Shell.Dialogs.Message, StringComparison.Ordinal);
        Assert.Contains("'abc' is not a valid value for Int32.", f.Shell.Dialogs.Message, StringComparison.Ordinal);
        Assert.Equal("error", f.Shell.Dialogs.Request!.Icon);
        Assert.Equal("giftCheckBox · Location.X", f.Shell.Dialogs.Request.Context);
        Assert.Equal(["OK", "Cancel"], f.Shell.Dialogs.Request.Buttons);

        // OK: back to the cell (selected, its text box focused with what was typed selected), to correct it; nothing written.
        f.Shell.Dialogs.Accept();
        await ShellFixture.PumpUntil(() => !f.Shell.Dialogs.IsOpen);
        Assert.Equal("abc", Grid(f).Find("Location.X")!.Text);
        Assert.Equal("96", Grid(f).Find("Location.X")!.Value);
        Assert.Equal("Location.X", Grid(f).SelectedRow?.Path);
        await ShellFixture.PumpUntil(() => PropertyGridViewTests.Editor(view, Grid(f).Find("Location.X")!) is { IsFocused: true } box && box.SelectedText == "abc");
        Assert.Contains("giftCheckBox.Location = new Point(96, 70);", Code(designer), StringComparison.Ordinal);

        // Again, and Cancel: the old value is back.
        Grid(f).SelectedRow = Grid(f).Find("Text");
        Grid(f).Find("Location.X")!.Text = "-x";
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        f.Shell.Dialogs.Cancel();
        await ShellFixture.PumpUntil(() => !f.Shell.Dialogs.IsOpen && Grid(f).Find("Location.X")!.Text == "96");
        Assert.Equal("Location.X", Grid(f).SelectedRow?.Path);
    }

    private static async Task<Views.PropertiesWindowView> PropertiesViewAsync(ShellFixture f)
    {
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<Views.PropertiesWindowView>().Any(v => v.IsEffectivelyVisible));
        return f.Window.GetVisualDescendants().OfType<Views.PropertiesWindowView>().First(v => v.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task A_multi_line_tool_tip_opens_the_multi_line_editor_and_resets()
    {
        var (f, designer, _) = await OrderFormAsync("customerTextBox");
        await using var _f = f;
        f.Shell.Layout.ShowToolWindow(PropertiesWindowViewModel.ToolWindowId, focus: false);
        var view = await PropertiesViewAsync(f);
        var tip = await RowAsync(f, "ToolTip on orderToolTip");
        tip.Text = "Line 1\r\nLine 2";
        await ShellFixture.PumpUntil(() => Code(designer).Contains("orderToolTip.SetToolTip(customerTextBox, \"Line 1\\r\\nLine 2\");", StringComparison.Ordinal), seconds: 30, () => Code(designer));

        // No text box (its lines would be joined): the arrow, F4 and a double-click open the multi-line editor.
        tip = await RowAsync(f, "ToolTip on orderToolTip", r => r.HasLineBreaks);
        Assert.True(tip is { ShowsTextBox: false, HasDropDown: true, DisplayValue: "Line 1 ⏎ Line 2" });
        tip.DoubleClick();
        await ShellFixture.PumpUntil(() => view.Grid.OpenDropDownControl is Views.PropertyEditors.MultilineTextDropDown);
        var multiline = (Views.PropertyEditors.MultilineTextDropDown)view.Grid.OpenDropDownControl!;
        Assert.Equal("Line 1\r\nLine 2", multiline.Box.Text);
        multiline.Box.Text = "Line 1\nLine 3";
        view.Grid.DropDownPopup.IsOpen = false;
        await ShellFixture.PumpUntil(() => Code(designer).Contains("orderToolTip.SetToolTip(customerTextBox, \"Line 1\\r\\nLine 3\");", StringComparison.Ordinal), seconds: 30, () => Code(designer));

        // Reset clears it.
        tip = await RowAsync(f, "ToolTip on orderToolTip", r => r.Value.Contains("Line 3", StringComparison.Ordinal));
        Assert.True(tip.ResetCommand.CanExecute(null));
        tip.ResetCommand.Execute(null);
        await ShellFixture.PumpUntil(() => !Code(designer).Contains("orderToolTip.SetToolTip(customerTextBox", StringComparison.Ordinal), seconds: 30, () => Code(designer));
    }

    [AvaloniaFact]
    public async Task Data_bindings_are_an_expandable_property()
    {
        var (f, app, _) = await OpenAsync();
        await using var _f = f;
        var designer = await DesignAsync(f, Path.Combine(app, "InventoryForm.cs"));
        designer.SetSelection(["nameTextBox"], notify: true);
        await RowsAsync(f);
        var bindings = await RowAsync(f, "(DataBindings)");
        Assert.True(bindings.IsReadOnly);
        Assert.Contains(Grid(f).Categories.Single(c => c.Title == "Data").Rows, r => r.Name == "(DataBindings)");
        Assert.DoesNotContain(Grid(f).Categories, c => c.Title == "(DataBindings)");
        var text = Assert.Single(bindings.Children, c => c.Name == "Text");
        Assert.Equal("(DataBindings).Text", text.Path);
        Assert.Contains(text.Options, o => o.Value == "itemBindingSource - Quantity");
        text.SelectedOption = text.Options.Single(o => o.Value == "itemBindingSource - Quantity");
        await ShellFixture.PumpUntil(() => Code(designer).Contains("nameTextBox.DataBindings.Add(new Binding(\"Text\", itemBindingSource, \"Quantity\", true));", StringComparison.Ordinal), seconds: 30);

        // Alphabetical: (DataBindings) and (Name) lead (D41 question 4).
        f.Shell.PropertiesWindow.Alphabetical = true;
        await ShellFixture.PumpUntil(() => Grid(f).Alphabetical && !f.Shell.PropertiesWindow.IsLoading);
        Assert.Equal(["(DataBindings)", "(Name)"], Grid(f).Items.OfType<PropertyRowViewModel>().Take(2).Select(r => r.Name));
        f.Shell.PropertiesWindow.Alphabetical = false;
    }
}
