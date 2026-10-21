using Avalonia.Headless.XUnit;
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
        Assert.True(name is { IsChoice: true, ChoicesEditable: false });
        Assert.Contains(name.Options, o => o.Value == "Segoe UI");
        Assert.True(font.Children.Single(c => c.Name == "GdiCharSet").IsReadOnly);

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
    public async Task Colors_alignment_text_and_reset()
    {
        var (f, designer, _) = await OrderFormAsync("placeOrderButton");
        await using var _f = f;
        var fore = await RowAsync(f, "ForeColor");
        Assert.True(fore is { IsColor: true, IsSet: true, Value: "White" });
        Assert.NotNull(fore.Swatch);
        fore.Text = "Red";
        await ShellFixture.PumpUntil(() => Code(designer).Contains("placeOrderButton.ForeColor = Color.Red;", StringComparison.Ordinal), seconds: 30);
        (await RowAsync(f, "ForeColor", r => r.Value == "Red")).Text = "255, 128, 0";
        await ShellFixture.PumpUntil(() => Code(designer).Contains("placeOrderButton.ForeColor = Color.FromArgb(255, 128, 0);", StringComparison.Ordinal), seconds: 30);
        (await RowAsync(f, "BackColor")).Text = "GrayText";
        await ShellFixture.PumpUntil(() => Code(designer).Contains("placeOrderButton.BackColor = SystemColors.GrayText;", StringComparison.Ordinal), seconds: 30);

        var align = await RowAsync(f, "TextAlign");
        Assert.Equal(PropertyRowKind.ContentAlignment, align.Kind);
        align.Text = "MiddleRight";
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

        // The Events view: a double-click on Click writes and shows the handler.
        designer.SetSelection(["cancelButton"], notify: true);
        properties.ShowEvents = true;
        await ShellFixture.PumpUntil(() => Grid(f).Find("Click") is not null && properties.Title.StartsWith("cancelButton", StringComparison.Ordinal) && !properties.IsLoading);
        Grid(f).Find("Click")!.DoubleClick();
        await ShellFixture.PumpUntil(() => Code(designer).Contains("cancelButton.Click += cancelButton_Click;", StringComparison.Ordinal), seconds: 30);
        properties.ShowEvents = false;
    }

    [AvaloniaFact]
    public async Task Invalid_values_are_reported_and_corrected_or_put_back()
    {
        var (f, designer, _) = await OrderFormAsync("giftCheckBox");
        await using var _f = f;
        var x = await RowAsync(f, "Location.X");
        Grid(f).SelectedRow = x;
        x.Text = "abc";
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        Assert.Equal("Properties Window", f.Shell.Dialogs.Title);
        Assert.StartsWith("Property value is not valid.", f.Shell.Dialogs.Message, StringComparison.Ordinal);
        Assert.Contains("'abc' is not a valid value for Int32.", f.Shell.Dialogs.Message, StringComparison.Ordinal);
        Assert.Equal("error", f.Shell.Dialogs.Request!.Icon);
        Assert.Equal("giftCheckBox · Location.X", f.Shell.Dialogs.Request.Context);
        Assert.Equal(["OK", "Cancel"], f.Shell.Dialogs.Request.Buttons);

        // OK: back to the cell, the typed text kept to correct it; nothing written.
        f.Shell.Dialogs.Accept();
        await ShellFixture.PumpUntil(() => !f.Shell.Dialogs.IsOpen);
        Assert.Equal("abc", Grid(f).Find("Location.X")!.Text);
        Assert.Equal("96", Grid(f).Find("Location.X")!.Value);
        Assert.Equal("Location.X", Grid(f).SelectedRow?.Path);
        Assert.Contains("giftCheckBox.Location = new Point(96, 70);", Code(designer), StringComparison.Ordinal);

        // Again, and Cancel: the old value is back.
        Grid(f).Find("Location.X")!.Text = "-x";
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        f.Shell.Dialogs.Cancel();
        await ShellFixture.PumpUntil(() => !f.Shell.Dialogs.IsOpen && Grid(f).Find("Location.X")!.Text == "96");
        Assert.Equal("Location.X", Grid(f).SelectedRow?.Path);
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
