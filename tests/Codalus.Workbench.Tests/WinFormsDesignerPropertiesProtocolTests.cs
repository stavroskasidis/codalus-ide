using System.Text.Json;
using Codalus.Workbench.WinForms;

namespace Codalus.Workbench.Tests;

/// <summary>
/// #68 on every OS: the Properties window's additions to the Windows Forms designer's protocol (ADR 0073): a property's
/// parts (sub-properties), Reset's availability and choices that take typing, through the serializer the shell and the
/// host share; the editor a property's UITypeEditor stands for; and a multi-selection's properties merged as the classic
/// IDE's grid merges them.
/// </summary>
public sealed class WinFormsDesignerPropertiesProtocolTests
{
    private static DesignerPropertyInfo Font(string size, bool bold = false) => new("Font", "Appearance", "System.Drawing.Font", $"Segoe UI, {size}pt")
    {
        Editor = DesignerEditors.Font,
        IsSet = true,
        CanReset = true,
        SubProperties =
        [
            new("Name", "Misc", "System.String", "Segoe UI") { Editor = DesignerEditors.Enum, StandardValues = ["Arial", "Segoe UI"], CanReset = true },
            new("Size", "Misc", "System.Single", size) { CanReset = true, ChoicesEditable = true },
            new("Bold", "Misc", "System.Boolean", bold ? "True" : "False") { Editor = DesignerEditors.Boolean, StandardValues = ["True", "False"] },
        ],
    };

    [Fact]
    public void Sub_properties_round_trip_through_the_shared_serializer()
    {
        var json = WinFormsDesignerJsonContext.Default;
        var result = new DesignerPropertiesResult([Font("9"), new("Text", "Appearance", "System.String", "OK") { Editor = DesignerEditors.Multiline, CanReset = true, ChoicesEditable = false }]);
        var text = JsonSerializer.Serialize(result, json.DesignerPropertiesResult);
        Assert.Contains("\"subProperties\":[", text, StringComparison.Ordinal);
        Assert.Contains("\"canReset\":true", text, StringComparison.Ordinal);
        Assert.Contains("\"choicesEditable\":true", text, StringComparison.Ordinal);
        var back = JsonSerializer.Deserialize(text, json.DesignerPropertiesResult)!;
        Assert.Equal(["Name", "Size", "Bold"], back.Properties[0].SubProperties!.Select(s => s.Name));
        Assert.Equal(["Arial", "Segoe UI"], back.Properties[0].SubProperties![0].StandardValues!);
        Assert.True(back.Properties[0].SubProperties![1].ChoicesEditable);
        Assert.True(back.Properties[0].CanReset);
        Assert.Null(back.Properties[1].SubProperties);
        Assert.Equal(DesignerEditors.Multiline, back.Properties[1].Editor);

        // A host older than #68: no parts, no Reset, exclusive choices.
        var old = JsonSerializer.Deserialize("{\"properties\":[{\"name\":\"Text\",\"category\":\"Appearance\",\"type\":\"System.String\",\"value\":\"OK\",\"editor\":\"text\"}]}", json.DesignerPropertiesResult)!;
        Assert.True(old.Properties[0] is { SubProperties: null, CanReset: false, ChoicesEditable: false });
    }

    [Fact]
    public void Editors_come_from_the_type_editors()
    {
        Assert.Equal(DesignerEditors.Color, DesignerEditors.FromEditorType(["System.Drawing.Design.ColorEditor", "System.Drawing.Design.UITypeEditor"], modal: false, hasCollectionEditor: false));
        Assert.Equal(DesignerEditors.Font, DesignerEditors.FromEditorType(["System.Drawing.Design.FontEditor"], modal: true, hasCollectionEditor: false));
        Assert.Equal(DesignerEditors.ContentAlignment, DesignerEditors.FromEditorType(["System.Drawing.Design.ContentAlignmentEditor"], modal: false, hasCollectionEditor: false));
        Assert.Equal(DesignerEditors.Multiline, DesignerEditors.FromEditorType(["System.ComponentModel.Design.MultilineStringEditor"], modal: false, hasCollectionEditor: false));
        Assert.Equal(DesignerEditors.Strings, DesignerEditors.FromEditorType(["System.Windows.Forms.Design.StringArrayEditor", "System.ComponentModel.Design.ArrayEditor", "System.ComponentModel.Design.CollectionEditor"], modal: true, hasCollectionEditor: false));
        Assert.Equal(DesignerEditors.Strings, DesignerEditors.FromEditorType(["System.Windows.Forms.Design.StringCollectionEditor"], modal: true, hasCollectionEditor: false));
        Assert.Equal(DesignerEditors.Strings, DesignerEditors.FromEditorType(["System.Windows.Forms.Design.ListControlStringCollectionEditor"], modal: true, hasCollectionEditor: false));
        Assert.Equal(DesignerEditors.ShortcutKeys, DesignerEditors.FromEditorType(["System.Windows.Forms.Design.ShortcutKeysEditor"], modal: false, hasCollectionEditor: false));
        Assert.Equal(DesignerEditors.Date, DesignerEditors.FromEditorType(["System.ComponentModel.Design.DateTimeEditor"], modal: false, hasCollectionEditor: false));
        Assert.Equal(DesignerEditors.Dock, DesignerEditors.FromEditorType(["System.Windows.Forms.Design.DockEditor"], modal: false, hasCollectionEditor: false));
        Assert.Equal(DesignerEditors.Anchor, DesignerEditors.FromEditorType(["System.Windows.Forms.Design.AnchorEditor"], modal: false, hasCollectionEditor: false));
        foreach (var image in new[] { "System.Drawing.Design.ImageEditor", "System.Drawing.Design.IconEditor", "System.Drawing.Design.BitmapEditor", "System.Drawing.Design.MetafileEditor" })
        {
            Assert.Equal(DesignerEditors.Image, DesignerEditors.FromEditorType([image], modal: true, hasCollectionEditor: false));
        }

        // A collection editor's subclass, through its base types: the IDE's collection editor when it edits the collection.
        string[] columns = ["System.Windows.Forms.Design.DataGridViewColumnCollectionEditor", "System.ComponentModel.Design.CollectionEditor", "System.Drawing.Design.UITypeEditor"];
        Assert.Equal(DesignerEditors.Collection, DesignerEditors.FromEditorType(columns, modal: true, hasCollectionEditor: true));
        Assert.Equal(DesignerEditors.Modal, DesignerEditors.FromEditorType(columns, modal: true, hasCollectionEditor: false));

        // A vendor's subclass of the color editor is a color editor; another dialog is modal; another drop-down keeps its type's editor.
        Assert.Equal(DesignerEditors.Color, DesignerEditors.FromEditorType(["Vendor.FancyColorEditor", "System.Drawing.Design.ColorEditor"], modal: false, hasCollectionEditor: false));
        Assert.Equal(DesignerEditors.Modal, DesignerEditors.FromEditorType(["System.Windows.Forms.Design.MaskPropertyEditor", "System.Drawing.Design.UITypeEditor"], modal: true, hasCollectionEditor: false));
        Assert.Null(DesignerEditors.FromEditorType(["Vendor.SliderEditor", "System.Drawing.Design.UITypeEditor"], modal: false, hasCollectionEditor: false));
        Assert.Null(DesignerEditors.FromEditorType([], modal: false, hasCollectionEditor: false));
    }

    [Fact]
    public void Merging_a_selection_keeps_shared_sub_properties_and_mixes_differing_ones()
    {
        DesignerPropertyInfo[] a =
        [
            new("Text", "Appearance", "System.String", "OK") { CanReset = true, ChoicesEditable = true },
            Font("9"),
            new("Cursor", "Appearance", "System.Windows.Forms.Cursor", "Default") { ChoicesEditable = true, CanReset = true },
            new("FlatStyle", "Appearance", "System.Windows.Forms.FlatStyle", "Flat"),
        ];
        DesignerPropertyInfo[] b =
        [
            new("Cursor", "Appearance", "System.Windows.Forms.Cursor", "Default") { ChoicesEditable = false, CanReset = true },
            Font("12") with { IsSet = false, CanReset = false },
            new("Text", "Appearance", "System.String", "Cancel") { IsSet = true, CanReset = true, ChoicesEditable = true },
            new("Checked", "Appearance", "System.Boolean", "False"),
        ];
        var merged = DesignerPropertyMerge.Merge(a, b);

        // Only what both have, in the first one's order.
        Assert.Equal(["Text", "Font", "Cursor"], merged.Select(p => p.Name));
        Assert.True(merged[0] is { Mixed: true, Value: null, IsSet: true, CanReset: true, ChoicesEditable: true });

        // The fonts differ only in Size: the font is mixed, Size is mixed, Bold is kept.
        var font = merged[1];
        Assert.True(font is { Mixed: true, Value: null, IsSet: true, CanReset: false });
        Assert.True(font.SubProperties!.Single(s => s.Name == "Size") is { Mixed: true, Value: null });
        Assert.True(font.SubProperties!.Single(s => s.Name == "Bold") is { Mixed: false, Value: "False" });
        Assert.Equal(["Name", "Size", "Bold"], font.SubProperties!.Select(s => s.Name));

        // The same value: not mixed; choices take typing only when both allow it.
        Assert.True(merged[2] is { Mixed: false, Value: "Default", ChoicesEditable: false, CanReset: true });
    }
}
