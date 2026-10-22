using Codalus.LanguageServices.Host.WinForms;
using Codalus.LanguageServices.Protocol;
using Codalus.Workbench.WinForms;
using Microsoft.CodeAnalysis.CSharp;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// #68 (D41), Windows only: the Properties window's half of the real Windows Forms designer host, on the fixture's own
/// build. Parts come from the type converters in their order, with a real type descriptor context (the font's families,
/// the form's buttons for AcceptButton, "(none)"); editors from the UITypeEditors, before flags and enums; a part's
/// edit is the whole value written once (CreateInstance), or set on a reference type's part; a converter's refusal is
/// the change's error; a localizable form's part goes to its resources. The fake designer host of the shell's tests
/// answers with the same shapes (Font.Name a choice, AcceptButton's "(none)", Text multiline): the facts it encodes are
/// asserted here on the real host. The exact code strings are the classic IDE's; they are confirmed on the first Windows
/// run (CI's windows-latest job or a Windows session), as this file was written where the host can't run. The fixture's
/// build is shared with <see cref="WinFormsDesignerHostTests"/> (read only; each test starts its own host).
/// </summary>
[Collection(nameof(WinFormsHostBuild))]
public sealed class WinFormsDesignerPropertiesHostTests(WinFormsHostFixture fixture)
{
    private const string Skip = "The Windows Forms designer runs on Windows only (D31).";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(IWinFormsDesignerHost Host, DesignerSource Source)> LoadAsync(string form, bool withResources = false)
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
                Localizable = new DesignerResourceSet(target.CodeFile, p => File.Exists(p) ? File.ReadAllText(p) : null).Get(null)?.IsLocalizable ?? false,
            };
        }

        var host = await WinFormsDesignerProcess.StartAsync(target, Ct);
        try
        {
            var render = await host.LoadAsync(parameters, Ct);
            Assert.True(render.Errors.Length == 0, string.Join("\n", render.Errors));
            return (host, source);
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    private static async Task<DesignerPropertyInfo[]> PropertiesAsync(IWinFormsDesignerHost host, string component) =>
        (await host.PropertiesAsync(new DesignerComponentsParams([component]), Ct)).Properties;

    private static DesignerPropertyInfo Property(DesignerPropertyInfo[] properties, string name) => properties.Single(p => p.Name == name && !p.DataBinding && p.Provider is null);

    private static Task<DesignerChangeResult> SetAsync(IWinFormsDesignerHost host, params DesignerPropertyEdit[] edits) =>
        host.SetPropertiesAsync(new DesignerSetPropertiesParams(edits), Ct);

    /// <summary>The designer file after a change, and its resource files that changed, as the designer document writes them.</summary>
    private (string Code, Dictionary<string, string> Resources) Apply(string form, DesignerSource source, DesignerChangeResult result)
    {
        Assert.Null(result.Error);
        string Of(string name) => name == source.Model.ClassName ? string.Empty : name;
        var edits = new List<DesignerEdit>();
        edits.AddRange(result.Changes.Select(c => new DesignerEdit { Kind = DesignerEditKinds.SetProperty, Component = Of(c.Component), Property = c.Property, Value = c.Value }));
        edits.AddRange(result.Localized.Select(l => new DesignerEdit { Kind = DesignerEditKinds.SetLocalized, Component = Of(l.Component), Localized = l.Localized }));
        edits.AddRange(result.Resources.Select(r => new DesignerEdit { Kind = DesignerEditKinds.SetResource, Key = r.Key, Culture = r.Culture, Resource = r.Resource, Metadata = r.Metadata }));
        var resources = new DesignerResourceSet(Path.Combine(fixture.App, form), p => File.Exists(p) ? File.ReadAllText(p) : null);
        var writer = new DesignerWriter(t => DesignerReader.Read(CSharpSyntaxTree.ParseText(t), source.Model.BaseType, DesignerReader.WindowsFormsImplicitUsings), resources);
        return (writer.Apply(source.Text, [.. edits]).ToString(), resources.Changes.ToDictionary(c => Path.GetFileName(c.Path), c => c.After, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Expandable_properties_list_their_parts_in_the_converters_order()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Skip);
        var (host, _) = await LoadAsync("OrderForm.cs");
        await using var _ = host;

        var font = Property(await PropertiesAsync(host, "notesTextBox"), "Font");
        Assert.Equal(["Name", "Size", "Unit", "Bold", "GdiCharSet", "GdiVerticalFont", "Italic", "Strikeout", "Underline"], font.SubProperties!.Select(p => p.Name));
        Assert.Equal("True", font.SubProperties!.Single(p => p.Name == "Italic").Value);
        var name = font.SubProperties![0];
        Assert.True(name is { Editor: DesignerEditors.Enum, ChoicesEditable: false, IsReadOnly: false });
        Assert.Contains("Segoe UI", name.StandardValues!);
        Assert.All(font.SubProperties!, p => Assert.True(p.IsSet, p.Name));

        // GdiCharSet and GdiVerticalFont are settable through FontConverter's CreateInstance, as the approved mockup draws
        // them (a text and a True/False choice); the fake host answers the same.
        Assert.True(font.SubProperties!.Single(p => p.Name == "GdiCharSet") is { IsReadOnly: false, Editor: DesignerEditors.Text, Type: "System.Byte" });
        Assert.True(font.SubProperties!.Single(p => p.Name == "GdiVerticalFont") is { IsReadOnly: false, Editor: DesignerEditors.Boolean });

        var location = Property(await PropertiesAsync(host, "giftCheckBox"), "Location");
        Assert.Equal([("X", "96"), ("Y", "70")], location.SubProperties!.Select(p => (p.Name, p.Value!)));
        Assert.All(location.SubProperties!, p => Assert.False(p.IsReadOnly, p.Name));

        var padding = Property(await PropertiesAsync(host, "shippingGroupBox"), "Padding");
        Assert.Equal(["All", "Left", "Top", "Right", "Bottom"], padding.SubProperties!.Select(p => p.Name));
        Assert.Equal("6", padding.SubProperties![0].Value);

        var button = await PropertiesAsync(host, "placeOrderButton");
        var flat = Property(button, "FlatAppearance");
        Assert.True(flat.IsReadOnly);
        var border = flat.SubProperties!.Single(p => p.Name == "BorderSize");
        Assert.True(border is { IsReadOnly: false, Value: "0" });
        Assert.Null(Property(button, "Text").SubProperties);
    }

    [Fact]
    public async Task Sub_property_edits_are_written_as_the_whole_value()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Skip);
        var (host, source) = await LoadAsync("OrderForm.cs");
        await using var _ = host;

        var bold = await SetAsync(host, new DesignerPropertyEdit("notesTextBox", "Font.Bold") { Text = "True" });
        var change = Assert.Single(bold.Changes);
        Assert.Equal(("notesTextBox", "Font"), (change.Component, change.Property));
        Assert.Contains("            notesTextBox.Font = new Font(\"Segoe UI\", 9F, FontStyle.Bold | FontStyle.Italic, GraphicsUnit.Point, 0);\n", Apply("OrderForm.cs", source, bold).Code, StringComparison.Ordinal);

        var x = await SetAsync(host, new DesignerPropertyEdit("giftCheckBox", "Location.X") { Text = "100" });
        Assert.Contains("            giftCheckBox.Location = new Point(100, 70);\n", Apply("OrderForm.cs", source, x).Code, StringComparison.Ordinal);

        // Padding: All writes one value; a side writes the four.
        var all = await SetAsync(host, new DesignerPropertyEdit("shippingGroupBox", "Padding.All") { Text = "10" });
        Assert.Contains("            shippingGroupBox.Padding = new Padding(10);\n", Apply("OrderForm.cs", source, all).Code, StringComparison.Ordinal);
        var left = await SetAsync(host, new DesignerPropertyEdit("shippingGroupBox", "Padding.Left") { Text = "12" });
        Assert.Contains("            shippingGroupBox.Padding = new Padding(12, 10, 10, 10);\n", Apply("OrderForm.cs", source, left).Code, StringComparison.Ordinal);

        // A reference type's part, and its reset.
        var flat = await SetAsync(host, new DesignerPropertyEdit("placeOrderButton", "FlatAppearance.BorderSize") { Text = "2" });
        Assert.Contains("            placeOrderButton.FlatAppearance.BorderSize = 2;\n", Apply("OrderForm.cs", source, flat).Code, StringComparison.Ordinal);
        var reset = await SetAsync(host, new DesignerPropertyEdit("placeOrderButton", "FlatAppearance.BorderSize") { Reset = true });
        Assert.Contains(reset.Changes, c => c is { Component: "placeOrderButton", Property: "FlatAppearance.BorderSize", Value: null });

        // Two parts of one font in one request: one change.
        var two = await SetAsync(host, new DesignerPropertyEdit("notesTextBox", "Font.Size") { Text = "12" }, new DesignerPropertyEdit("notesTextBox", "Font.Underline") { Text = "True" });
        Assert.Single(two.Changes, c => c.Property == "Font");
        Assert.Equal("Segoe UI, 12pt, style=Bold, Italic, Underline", Property(await PropertiesAsync(host, "notesTextBox"), "Font").Value);

        // A value the converter refuses: the error, nothing changed.
        var invalid = await SetAsync(host, new DesignerPropertyEdit("giftCheckBox", "Location.X") { Text = "abc" });
        Assert.NotNull(invalid.Error);
        Assert.Contains("abc", invalid.Error, StringComparison.Ordinal);
        Assert.Empty(invalid.Changes);
        Assert.Equal("100", Property(await PropertiesAsync(host, "giftCheckBox"), "Location").SubProperties![0].Value);
    }

    [Fact]
    public async Task A_request_with_an_invalid_value_sets_none_of_its_edits()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Skip);
        var (host, source) = await LoadAsync("OrderForm.cs");
        await using var _ = host;
        var before = Property(await PropertiesAsync(host, "notesTextBox"), "Font").Value;

        // The font editor's OK with a family and a size that isn't one: the family isn't kept on the surface either.
        var font = await SetAsync(host, new DesignerPropertyEdit("notesTextBox", "Font.Name") { Text = "Arial" }, new DesignerPropertyEdit("notesTextBox", "Font.Size") { Text = "abc" });
        Assert.NotNull(font.Error);
        Assert.Empty(font.Changes);
        Assert.Equal(before, Property(await PropertiesAsync(host, "notesTextBox"), "Font").Value);

        // The next change doesn't carry the family that was refused.
        var bold = await SetAsync(host, new DesignerPropertyEdit("notesTextBox", "Font.Bold") { Text = "True" });
        Assert.Contains("            notesTextBox.Font = new Font(\"Segoe UI\", 9F, FontStyle.Bold | FontStyle.Italic, GraphicsUnit.Point, 0);\n", Apply("OrderForm.cs", source, bold).Code, StringComparison.Ordinal);

        // Two components: the first one's edit is put back too, and an ambient value stays ambient (not set).
        var customer = Property(await PropertiesAsync(host, "customerTextBox"), "Font");
        var batch = await SetAsync(host, new DesignerPropertyEdit("customerTextBox", "Font.Size") { Text = "14" }, new DesignerPropertyEdit("giftCheckBox", "Location.X") { Text = "abc" });
        Assert.NotNull(batch.Error);
        var after = Property(await PropertiesAsync(host, "customerTextBox"), "Font");
        Assert.Equal((customer.Value, customer.IsSet), (after.Value, after.IsSet));
        var next = await SetAsync(host, new DesignerPropertyEdit("customerTextBox", "Text") { Text = "Ada" });
        Assert.DoesNotContain(next.Changes, c => c.Property == "Font");
    }

    [Fact]
    public async Task Several_components_merge_their_properties_and_parts()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Skip);
        var (host, _) = await LoadAsync("OrderForm.cs");
        await using var _h = host;

        // notesTextBox's font is italic, customerTextBox's isn't: Font and its Italic are mixed, its Name and Size shared.
        var merged = (await host.PropertiesAsync(new DesignerComponentsParams(["notesTextBox", "customerTextBox"]), Ct)).Properties;
        var font = Property(merged, "Font");
        Assert.True(font is { Mixed: true, Value: null });
        Assert.True(font.SubProperties!.Single(p => p.Name == "Italic") is { Mixed: true, Value: null });
        Assert.True(font.SubProperties!.Single(p => p.Name == "Name") is { Mixed: false, Value: "Segoe UI" });
        Assert.True(font.SubProperties!.Single(p => p.Name == "Size") is { Mixed: false, Value: "9" });
        Assert.True(Property(merged, "Location") is { Mixed: true });
        Assert.True(Property(merged, "Multiline") is { Mixed: true, Value: null });
    }

    [Fact]
    public async Task Editors_and_choices_come_from_the_type_editors()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Skip);
        var (host, _) = await LoadAsync("OrderForm.cs");
        await using (host)
        {
            var button = await PropertiesAsync(host, "placeOrderButton");
            Assert.Equal(DesignerEditors.Color, Property(button, "BackColor").Editor);
            Assert.Equal(DesignerEditors.Font, Property(button, "Font").Editor);
            Assert.Equal(DesignerEditors.Multiline, Property(button, "Text").Editor);
            Assert.Equal(DesignerEditors.ContentAlignment, Property(button, "TextAlign").Editor);
            Assert.True(Property(button, "Text").CanReset);
            Assert.False(Property(button, "Name").CanReset);
            var cursor = Property(button, "Cursor");
            Assert.True(cursor is { Editor: DesignerEditors.Enum, ChoicesEditable: true });

            var notes = await PropertiesAsync(host, "notesTextBox");
            Assert.Equal(DesignerEditors.Multiline, Property(notes, "Text").Editor);
            Assert.True(Property(notes, "Lines") is { Editor: DesignerEditors.Strings, IsReadOnly: false });
            Assert.Equal(DesignerEditors.Collection, Property(notes, "AutoCompleteCustomSource").Editor);
            Assert.Equal(DesignerEditors.Date, Property(await PropertiesAsync(host, "orderDatePicker"), "Value").Editor);

            // A component reference: an exclusive list, "(none)" and the form's buttons.
            var accept = Property(await PropertiesAsync(host, string.Empty), "AcceptButton");
            Assert.True(accept is { Editor: DesignerEditors.Enum, ChoicesEditable: false, Value: "placeOrderButton" });
            Assert.Contains("(none)", accept.StandardValues!);
            Assert.Contains("placeOrderButton", accept.StandardValues!);
            Assert.Contains("cancelButton", accept.StandardValues!);

            // An unset reference is the "(none)" of its own list (ReferenceConverter's text for null), not an empty value.
            var strip = Property(button, "ContextMenuStrip");
            Assert.True(strip is { Editor: DesignerEditors.Enum, ChoicesEditable: false, Value: "(none)" });
            Assert.Equal("(none)", strip.StandardValues![0]);
            Assert.Single(strip.StandardValues!, v => v == "(none)");
        }

        var (inventory, _) = await LoadAsync("InventoryForm.cs");
        await using (inventory)
        {
            Assert.Equal(DesignerEditors.ShortcutKeys, Property(await PropertiesAsync(inventory, "newToolStripMenuItem"), "ShortcutKeys").Editor);

            // "(none)", then the form's context menu strips; the unset one is "(none)", the set one names its strip.
            var grid = await PropertiesAsync(inventory, "stockGridView");
            Assert.True(Property(grid, "ContextMenuStrip") is { Value: "(none)", StandardValues: ["(none)", "itemsContextMenuStrip"] });
            Assert.Equal("itemsContextMenuStrip", Property(await PropertiesAsync(inventory, "categoryListBox"), "ContextMenuStrip").Value);

            // A Windows Forms dialog the IDE doesn't show, whose converter takes no text (D41 question 8): modal, read-only.
            Assert.True(Property(grid, "DefaultCellStyle") is { Editor: DesignerEditors.Modal, IsReadOnly: true });
        }

        var (about, _) = await LoadAsync("AboutForm.cs");
        await using (about)
        {
            Assert.Equal(DesignerEditors.ContentAlignment, Property(await PropertiesAsync(about, "versionLabel"), "TextAlign").Editor);
        }
    }

    [Fact]
    public async Task Component_references_and_string_arrays_go_through_the_context()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Skip);
        var (host, source) = await LoadAsync("OrderForm.cs");
        await using var _ = host;

        var cancel = await SetAsync(host, new DesignerPropertyEdit(string.Empty, "AcceptButton") { Text = "cancelButton" });
        Assert.Contains("            AcceptButton = cancelButton;\n", Apply("OrderForm.cs", source, cancel).Code, StringComparison.Ordinal);
        var none = await SetAsync(host, new DesignerPropertyEdit(string.Empty, "AcceptButton") { Text = "(none)" });
        Assert.DoesNotContain("AcceptButton =", Apply("OrderForm.cs", source, none).Code, StringComparison.Ordinal);

        var lines = await SetAsync(host, new DesignerPropertyEdit("notesTextBox", "Lines") { Value = DesignerValue.ArrayOf("System.String", DesignerValue.String("one"), DesignerValue.String("two")) });
        Assert.Contains("notesTextBox.Lines = new string[] { \"one\", \"two\" };", Apply("OrderForm.cs", source, lines).Code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_localizable_forms_sub_property_goes_to_its_resources()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Skip);
        var (host, source) = await LoadAsync("AboutForm.cs", withResources: true);
        await using var _ = host;
        var x = await SetAsync(host, new DesignerPropertyEdit("titleLabel", "Location.X") { Text = "20" });
        var (code, resources) = Apply("AboutForm.cs", source, x);
        Assert.DoesNotContain("titleLabel.Location", code, StringComparison.Ordinal);
        Assert.Contains("<data name=\"titleLabel.Location\" type=\"System.Drawing.Point, System.Drawing\">\n    <value>20, 9</value>", resources["AboutForm.resx"].Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
    }
}
