using Codalus.LanguageServices.Host.WinForms;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// ADR 0073, Visual Basic forms: the Windows Forms designer's reader and writer on Visual Basic designer files as the
/// classic IDE writes them (samples/WindowsDesktop/WinFormsVbApp: OrderForm for .NET, AboutForm for .NET Framework,
/// localizable, with an AddHandler). Roslyn only, so it runs on every OS. As for C#: writing every statement again gives
/// the file back byte for byte, each change is the smallest edit where the classic IDE puts it, and names ignore case as
/// Visual Basic's do. The events a Handles clause hooks are the class's, read and written by <see cref="VisualBasicHandles"/>.
/// </summary>
public sealed class WinFormsDesignerVisualBasicCodeTests
{
    private static string Sample(params string[] path) => Path.Combine([RepoPaths.Root, "samples", "WindowsDesktop", "WinFormsVbApp", .. path]);

    private static string OrderForm => File.ReadAllText(Sample("OrderForm.Designer.vb"));

    private static string OrderCode => File.ReadAllText(Sample("OrderForm.vb"));

    private static string AboutForm => File.ReadAllText(Sample("AboutForm.Designer.vb"));

    private static DesignerSource Read(string text) =>
        DesignerReader.Read(VisualBasicSyntaxTree.ParseText(text, cancellationToken: TestContext.Current.CancellationToken), "System.Windows.Forms.Form", VisualBasicDesignerSyntax.WindowsFormsImplicitImports);

    private static string Apply(string text, params DesignerEdit[] edits) =>
        new DesignerWriter(t => DesignerReader.Read(VisualBasicSyntaxTree.ParseText(t), "System.Windows.Forms.Form", VisualBasicDesignerSyntax.WindowsFormsImplicitImports))
            .Apply(SourceText.From(text), edits).ToString();

    private static List<string> Diff(string before, string after)
    {
        var old = SourceText.From(before);
        var lines = new List<string>();
        foreach (var change in LineDiff.Compute(old, SourceText.From(after)))
        {
            lines.AddRange(old.ToString(change.Span).Split('\n').Where(l => l.Length > 0).Select(l => "-" + l.TrimEnd('\r').Trim()));
            lines.AddRange((change.NewText ?? string.Empty).Split('\n').Where(l => l.Length > 0).Select(l => "+" + l.TrimEnd('\r').Trim()));
        }

        return lines;
    }

    private static DesignerEdit Set(string component, string property, DesignerValue? value) =>
        new() { Kind = DesignerEditKinds.SetProperty, Component = component, Property = property, Value = value };

    private static DesignerValue Point(int x, int y) => DesignerValue.NewOf("System.Drawing.Point", DesignerValue.Int(x), DesignerValue.Int(y));

    private static DesignerValue Size(int width, int height) => DesignerValue.NewOf("System.Drawing.Size", DesignerValue.Int(width), DesignerValue.Int(height));

    private static DesignerEdit AddButton(string name, string parent, int x, int y) => new()
    {
        Kind = DesignerEditKinds.AddComponent,
        Component = name,
        Type = "System.Windows.Forms.Button",
        Parent = parent,
        Properties =
        [
            new("UseVisualStyleBackColor", DesignerValue.Bool(true)),
            new("Text", DesignerValue.String(name)),
            new("TabIndex", DesignerValue.Int(11)),
            new("Size", Size(75, 23)),
            new("Name", DesignerValue.String(name)),
            new("Location", Point(x, y)),
        ],
    };

    private static void Same(DesignerValue expected, DesignerValue? actual) =>
        Assert.True(expected.SameAs(actual), $"Expected {System.Text.Json.JsonSerializer.Serialize(expected)}, got {System.Text.Json.JsonSerializer.Serialize(actual)}");

    private static DesignerStatement Assignment(DesignerModel model, string target, string member) =>
        model.Statements.Single(s => s.Kind == DesignerStatementKinds.Assign && s.Target == target && s.Member == member);

    // ------------------------------------------------------------------ reading

    [Fact]
    public void Reads_a_form_visual_studio_wrote_for_dotnet()
    {
        var model = Read(OrderForm).Model;
        Assert.Equal(DesignerLanguages.VisualBasic, model.Language);
        Assert.Equal("OrderForm", model.ClassName);
        Assert.Null(model.Namespace);
        Assert.False(model.UsesThis);
        Assert.False(model.QualifiedNames);
        Assert.True(model.UsesResources);
        Assert.Equal(16, model.Components.Length);
        Assert.True(model.Components.Single(c => c.Name == "resources").IsLocal);
        Assert.Equal(new DesignerComponent("CustomerTextBox", "System.Windows.Forms.TextBox") { Modifier = "Friend WithEvents" }, model.Components.Single(c => c.Name == "CustomerTextBox"));
        Assert.Equal("System.Windows.Forms.ToolTip", model.Components.Single(c => c.Name == "OrderToolTip").Type);

        Same(DesignerValue.EnumOf("System.Windows.Forms.AnchorStyles", "Top", "Left", "Right"), Assignment(model, "CustomerTextBox", "Anchor").Value);
        Same(DesignerValue.String("Name or \"account\" number"), Assignment(model, "CustomerTextBox", "PlaceholderText").Value);
        Same(DesignerValue.String("Deliver to the back door.\r\nRing twice."), Assignment(model, "NotesTextBox", "Text").Value);
        Same(DesignerValue.NewOf("System.Drawing.SizeF", DesignerValue.Float(7F), DesignerValue.Float(15F)), Assignment(model, string.Empty, "AutoScaleDimensions").Value);
        Same(DesignerValue.NewOf("System.Decimal", DesignerValue.ArrayOf("System.Int32", DesignerValue.Int(50), DesignerValue.Int(0), DesignerValue.Int(0), DesignerValue.Int(0))),
            Assignment(model, "QuantityUpDown", "Maximum").Value);
        var back = Assignment(model, "NotesTextBox", "BackColor").Value!;
        Assert.Equal(("System.Drawing.Color", "FromArgb"), (back.Type, back.Value));
        Assert.All(back.Arguments!, a => Assert.Equal("byte", a.Type));
        Same(new DesignerValue { Kind = DesignerValueKinds.Resource, Type = "System.Drawing.Image", Value = "LogoPictureBox.Image" }, Assignment(model, "LogoPictureBox", "Image").Value);
        Same(DesignerValue.MemberOf("WinFormsVbApp.My.Resources.Resources", "PlaceOrder"), Assignment(model, "PlaceOrderButton", "Text").Value);
        Same(DesignerValue.ReferenceTo("PlaceOrderButton"), Assignment(model, string.Empty, "AcceptButton").Value);
        Same(DesignerValue.Bool(true), Assignment(model, "GiftCheckBox", "Checked").Value);

        var items = model.Statements.Single(s => s.Kind == DesignerStatementKinds.Call && s.Member == "Items.AddRange");
        Same(DesignerValue.ArrayOf("System.Object", DesignerValue.String("Coffee"), DesignerValue.String("Tea"), DesignerValue.String("Hot chocolate")), Assert.Single(items.Arguments!));
        Assert.Contains(model.Statements, s => s is { Kind: DesignerStatementKinds.Call, Target: "OrderToolTip", Member: "SetToolTip" } && s.Arguments![0].Value == "CustomerTextBox");
        Assert.Contains(model.Statements, s => s is { Kind: DesignerStatementKinds.Layout, Target: "QuantityUpDown", Member: "BeginInit" });
        Assert.Contains(model.Statements, s => s is { Kind: DesignerStatementKinds.Section, Target: "CustomerTextBox" });

        // Its events are its Handles clauses', in OrderForm.vb: none in InitializeComponent.
        Assert.DoesNotContain(model.Statements, s => s.Kind == DesignerStatementKinds.Event);
    }

    [Fact]
    public void Reads_a_form_visual_studio_wrote_for_dotnet_framework()
    {
        var source = Read(AboutForm);
        var model = source.Model;
        Assert.True(model.UsesThis);
        Assert.True(model.QualifiedNames);
        Assert.Equal("'", source.SectionRule);
        Assert.Equal("'", source.SectionPrefix);
        Assert.Contains(model.Statements, s => s is { Kind: DesignerStatementKinds.ApplyResources, Target: "" } && s.Value!.Value == "$this");
        Assert.Contains(model.Statements, s => s is { Kind: DesignerStatementKinds.ApplyResources, Target: "TitleLabel" });
        Assert.Equal(new DesignerStatement { Kind = DesignerStatementKinds.Event, Target = "OkButton", Member = "Click", Handler = "OkButton_Click", Line = 44 },
            model.Statements.Single(s => s.Kind == DesignerStatementKinds.Event));
        Same(DesignerValue.EnumOf("System.Windows.Forms.AnchorStyles", "Top", "Left", "Right"), Assignment(model, "VersionLabel", "Anchor").Value);
        Same(DesignerValue.String("Version 1.0\r\nCodalus"), Assignment(model, "VersionLabel", "Text").Value);
        var font = Assignment(model, "TitleLabel", "Font").Value!;
        Same(DesignerValue.Float(9.75F), font.Arguments![1]);
        Same(new DesignerValue { Kind = DesignerValueKinds.Literal, Type = "byte", Value = "0" }, font.Arguments[4]);
        Assert.All(Assignment(model, "VersionLabel", "BackColor").Value!.Arguments!, a => Assert.Equal("int", a.Type));

        // Not WithEvents: the .NET Framework designer's AddHandler event model.
        Assert.False(source.Fields["OkButton"].IsWithEvents);
        Assert.True(source.Fields["TitleLabel"].IsWithEvents);
    }

    [Fact]
    public void Names_ignore_case_and_are_read_as_declared()
    {
        var text = OrderForm.Replace("        GiftCheckBox.AutoSize = True", "        giftcheckbox.AutoSize = True", StringComparison.Ordinal)
            .Replace("Controls.Add(CustomerLabel)", "Controls.Add(CUSTOMERLABEL)", StringComparison.Ordinal);
        var model = Read(text).Model;
        Same(DesignerValue.Bool(true), Assignment(model, "GiftCheckBox", "AutoSize").Value);
        Assert.Contains(model.Statements, s => s is { Kind: DesignerStatementKinds.Call, Target: "", Member: "Controls.Add" } && s.Arguments![0].Value == "CustomerLabel");
    }

    public static TheoryData<string, bool> DesignerFiles => new()
    {
        { "OrderForm.Designer.vb", false },
        { "OrderForm.Designer.vb", true },
        { "AboutForm.Designer.vb", false },
        { "AboutForm.Designer.vb", true },
    };

    [Theory]
    [MemberData(nameof(DesignerFiles))]
    public void Writing_every_statement_again_gives_the_file_back(string file, bool crlf)
    {
        var text = File.ReadAllText(Sample(file));
        if (crlf)
        {
            text = text.Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        var source = Read(text);
        Assert.Equal(crlf ? "\r\n" : "\n", source.NewLine);
        var generator = new VisualBasicDesignerCodeGenerator(source.Model, source.Indent, source.NewLine);
        var written = string.Concat(source.Model.Statements.Select(s => generator.Statement(s, source.SectionRule)));
        var original = source.Text.ToString(TextSpan.FromBounds(source.Statements[0].Lines.Start, source.Statements[^1].Lines.End));
        Assert.Equal(original, written);
    }

    [Theory]
    [MemberData(nameof(DesignerFiles))]
    public void Nothing_changed_is_no_edit(string file, bool crlf)
    {
        var text = File.ReadAllText(Sample(file));
        if (crlf)
        {
            text = text.Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        Assert.Equal(text, Apply(text));
        var assignment = Read(text).Model.Statements.Last(s => s.Kind == DesignerStatementKinds.Assign && s.Value!.Kind != DesignerValueKinds.Array);
        Assert.Equal(text, Apply(text, Set(assignment.Target, assignment.Member!, assignment.Value)));
    }

    [Fact]
    public void Code_the_designer_doesnt_write_says_why_and_where()
    {
        var with = OrderForm.Replace("        GiftCheckBox.AutoSize = True\n", "        With GiftCheckBox\n            .AutoSize = True\n        End With\n", StringComparison.Ordinal);
        var withError = Assert.Throws<DesignerReadException>(() => Read(with));
        Assert.Contains("With", withError.Message, StringComparison.Ordinal);
        Assert.Equal(with.Split('\n').ToList().FindIndex(l => l.Contains("With GiftCheckBox", StringComparison.Ordinal)), withError.Line);

        var lambda = OrderForm.Replace("        GiftCheckBox.AutoSize = True\n", "        AddHandler GiftCheckBox.Click, Sub(s, e) Close()\n", StringComparison.Ordinal);
        Assert.Contains("lambda", Assert.Throws<DesignerReadException>(() => Read(lambda)).Message, StringComparison.Ordinal);

        var own = OrderForm.Replace("        GiftCheckBox.AutoSize = True\n", "        LoadDefaults()\n", StringComparison.Ordinal);
        Assert.Contains("the class's own method LoadDefaults", Assert.Throws<DesignerReadException>(() => Read(own)).Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ writing

    [Fact]
    public void A_changed_property_is_its_value_and_nothing_else()
    {
        var after = Apply(OrderForm, Set("PlaceOrderButton", "Location", Point(210, 250)), Set("NotesTextBox", "Text", DesignerValue.String("Line 1\r\nLine \"2\"")));
        Assert.Equal(
        [
            "-NotesTextBox.Text = \"Deliver to the back door.\" & vbCrLf & \"Ring twice.\"",
            "+NotesTextBox.Text = \"Line 1\" & vbCrLf & \"Line \"\"2\"\"\"",
            "-PlaceOrderButton.Location = New Point(216, 252)",
            "+PlaceOrderButton.Location = New Point(210, 250)",
        ], Diff(OrderForm, after));

        // The .NET Framework style keeps its own.
        var about = Apply(AboutForm, Set("VersionLabel", "Anchor", DesignerValue.EnumOf("System.Windows.Forms.AnchorStyles", "Top", "Left")),
            Set("VersionLabel", "Text", DesignerValue.String("A\tB")));
        Assert.Equal(
        [
            "-Me.VersionLabel.Anchor = CType(((System.Windows.Forms.AnchorStyles.Top Or System.Windows.Forms.AnchorStyles.Left) _",
            "-Or System.Windows.Forms.AnchorStyles.Right), System.Windows.Forms.AnchorStyles)",
            "+Me.VersionLabel.Anchor = CType((System.Windows.Forms.AnchorStyles.Top Or System.Windows.Forms.AnchorStyles.Left), System.Windows.Forms.AnchorStyles)",
            "-Me.VersionLabel.Text = \"Version 1.0\" & Global.Microsoft.VisualBasic.ChrW(13) & Global.Microsoft.VisualBasic.ChrW(10) & \"Codalus\"",
            "+Me.VersionLabel.Text = \"A\" & Global.Microsoft.VisualBasic.ChrW(9) & \"B\"",
        ], Diff(AboutForm, after: about));
    }

    [Fact]
    public void A_new_property_goes_where_visual_studio_sorts_it_and_a_default_one_goes_away()
    {
        var after = Apply(OrderForm,
            Set("CustomerLabel", "ForeColor", DesignerValue.MemberOf("System.Drawing.Color", "DarkRed")),
            Set("GiftCheckBox", "Checked", null),
            Set(string.Empty, "Font", DesignerValue.NewOf("System.Drawing.Font", DesignerValue.String("Segoe UI"), DesignerValue.Float(10F))));
        Assert.Equal(
        [
            "+CustomerLabel.ForeColor = Color.DarkRed",
            "-GiftCheckBox.Checked = True",
            "+Font = New Font(\"Segoe UI\", 10F)",
        ], Diff(OrderForm, after));
        Assert.Contains("CustomerLabel.AutoSize = True\n        CustomerLabel.ForeColor = Color.DarkRed\n        CustomerLabel.Location", after, StringComparison.Ordinal);
    }

    [Fact]
    public void A_new_component_is_a_WithEvents_field_its_creation_and_its_section()
    {
        var after = Apply(OrderForm, AddButton("HelpButton", string.Empty, 12, 252));
        Assert.DoesNotContain(Diff(OrderForm, after), l => l.StartsWith('-'));
        Assert.Contains("        OrderToolTip = New ToolTip(components)\n        HelpButton = New Button()\n", after, StringComparison.Ordinal);
        Assert.Contains(
            "        ' \n        ' HelpButton\n        ' \n        HelpButton.Location = New Point(12, 252)\n        HelpButton.Name = \"HelpButton\"\n        HelpButton.Size = New Size(75, 23)\n"
            + "        HelpButton.TabIndex = 11\n        HelpButton.Text = \"HelpButton\"\n        HelpButton.UseVisualStyleBackColor = True\n        ' \n        ' OrderForm\n",
            after, StringComparison.Ordinal);
        Assert.Contains("    Friend WithEvents OrderToolTip As ToolTip\n    Friend WithEvents HelpButton As Button\nEnd Class", after, StringComparison.Ordinal);
        Assert.Contains("        ' \n        ' HelpButton\n        ' \n        HelpButton.Location", after, StringComparison.Ordinal);
        Assert.Contains("        Controls.Add(HelpButton)\n        Controls.Add(CancelOrderButton)", after, StringComparison.Ordinal);

        // A name that differs only in case is taken: Visual Basic's names ignore it.
        Assert.Throws<InvalidOperationException>(() => Apply(OrderForm, AddButton("giftcheckbox", string.Empty, 0, 0)));

        // .NET Framework's style: Me., full names, CodeDOM's section comments; a form whose first component is added.
        var about = Apply(AboutForm, AddButton("HelpButton", string.Empty, 12, 80));
        Assert.Contains("        Me.VersionLabel = New System.Windows.Forms.Label()\n        Me.HelpButton = New System.Windows.Forms.Button()\n", about, StringComparison.Ordinal);
        Assert.Contains("        '\n        'HelpButton\n        '\n        Me.HelpButton.Location = New System.Drawing.Point(12, 80)\n", about, StringComparison.Ordinal);
        Assert.Contains("    Friend WithEvents VersionLabel As System.Windows.Forms.Label\n    Friend WithEvents HelpButton As System.Windows.Forms.Button\n", about, StringComparison.Ordinal);
        Assert.Contains("        Me.Controls.Add(Me.HelpButton)\n        Me.Controls.Add(Me.VersionLabel)", about, StringComparison.Ordinal);
    }

    [Fact]
    public void The_first_component_of_a_new_form_goes_after_InitializeComponent()
    {
        const string Empty = """
            <Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()>
            Partial Class Form1
                Inherits System.Windows.Forms.Form

                Private components As System.ComponentModel.IContainer

                <System.Diagnostics.DebuggerStepThrough()>
                Private Sub InitializeComponent()
                    components = New System.ComponentModel.Container()
                    AutoScaleMode = AutoScaleMode.Font
                    ClientSize = New Size(800, 450)
                    Text = "Form1"
                End Sub

            End Class

            """;
        var after = Apply(Empty, AddButton("Button1", string.Empty, 12, 12));
        Assert.Contains("    Private Sub InitializeComponent()\n        components = New System.ComponentModel.Container()\n        Button1 = New Button()\n        SuspendLayout()\n", after, StringComparison.Ordinal);
        Assert.Contains("        ' \n        ' Button1\n        ' \n        Button1.Location = New Point(12, 12)\n", after, StringComparison.Ordinal);
        Assert.Contains("        Controls.Add(Button1)\n        Text = \"Form1\"\n        ResumeLayout(False)\n    End Sub\n\n    Friend WithEvents Button1 As Button\n\nEnd Class", after, StringComparison.Ordinal);
    }

    [Fact]
    public void A_removed_component_takes_its_field_section_and_uses()
    {
        var after = Apply(OrderForm, new DesignerEdit { Kind = DesignerEditKinds.RemoveComponent, Component = "ShippingGroupBox" });
        Assert.DoesNotContain("ShippingGroupBox", after, StringComparison.Ordinal);
        Assert.DoesNotContain("RadioButton", after, StringComparison.Ordinal);
        Assert.DoesNotContain("' ExpressRadioButton", after, StringComparison.Ordinal);
        Assert.Contains("    Friend WithEvents GiftCheckBox As CheckBox\n    Friend WithEvents OrderDatePicker As DateTimePicker\n", after, StringComparison.Ordinal);
        _ = Read(after);
    }

    [Fact]
    public void A_rename_is_the_field_its_uses_its_section_and_its_name()
    {
        var after = Apply(OrderForm, new DesignerEdit { Kind = DesignerEditKinds.RenameComponent, Component = "CustomerTextBox", NewName = "NameTextBox" });
        Assert.DoesNotContain("CustomerTextBox", after, StringComparison.Ordinal);
        Assert.Contains("        NameTextBox = New TextBox()\n", after, StringComparison.Ordinal);
        Assert.Contains("        ' \n        ' NameTextBox\n        ' \n        NameTextBox.Anchor", after, StringComparison.Ordinal);
        Assert.Contains("        NameTextBox.Name = \"NameTextBox\"\n", after, StringComparison.Ordinal);
        Assert.Contains("        OrderToolTip.SetToolTip(NameTextBox, ", after, StringComparison.Ordinal);
        Assert.Contains("    Friend WithEvents NameTextBox As TextBox\n", after, StringComparison.Ordinal);

        // Only its case: allowed (it's the same name to Visual Basic, the classic IDE lets it change).
        var cased = Apply(OrderForm, new DesignerEdit { Kind = DesignerEditKinds.RenameComponent, Component = "GiftCheckBox", NewName = "GIFTCheckBox" });
        Assert.Contains("    Friend WithEvents GIFTCheckBox As CheckBox\n", cased, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => Apply(OrderForm, new DesignerEdit { Kind = DesignerEditKinds.RenameComponent, Component = "GiftCheckBox", NewName = "customerLABEL" }));
        Assert.Throws<InvalidOperationException>(() => Apply(OrderForm, new DesignerEdit { Kind = DesignerEditKinds.RenameComponent, Component = "GiftCheckBox", NewName = "End" }));

        // A localizable form's: its ApplyResources key (and its keys in AboutForm.resx) and its Me. uses.
        var resources = new DesignerResourceSet(Sample("AboutForm.vb"), p => File.Exists(p) ? File.ReadAllText(p) : null);
        var about = new DesignerWriter(t => DesignerReader.Read(VisualBasicSyntaxTree.ParseText(t), "System.Windows.Forms.Form", VisualBasicDesignerSyntax.WindowsFormsImplicitImports), resources)
            .Apply(SourceText.From(AboutForm), new DesignerEdit { Kind = DesignerEditKinds.RenameComponent, Component = "TitleLabel", NewName = "HeadingLabel" }).ToString();
        var resx = Assert.Single(resources.Changes);
        Assert.Contains("<data name=\"HeadingLabel.Text\"", resx.After, StringComparison.Ordinal);
        Assert.DoesNotContain("TitleLabel", resx.After, StringComparison.Ordinal);
        Assert.Contains("        resources.ApplyResources(Me.HeadingLabel, \"HeadingLabel\")\n", about, StringComparison.Ordinal);
        Assert.Contains("        '\n        'HeadingLabel\n        '\n", about, StringComparison.Ordinal);
        Assert.Contains("        Me.Controls.Add(Me.HeadingLabel)\n", about, StringComparison.Ordinal);
    }

    [Fact]
    public void An_AddHandler_event_is_kept_in_its_style()
    {
        var after = Apply(AboutForm, new DesignerEdit { Kind = DesignerEditKinds.AddEvent, Component = "OkButton", Event = "Click", Handler = "OnOk" });
        Assert.Equal(["-AddHandler Me.OkButton.Click, AddressOf Me.OkButton_Click", "+AddHandler Me.OkButton.Click, AddressOf Me.OnOk"], Diff(AboutForm, after));
        var removed = Apply(AboutForm, new DesignerEdit { Kind = DesignerEditKinds.RemoveEvent, Component = "OkButton", Event = "Click" });
        Assert.Equal(["-AddHandler Me.OkButton.Click, AddressOf Me.OkButton_Click"], Diff(AboutForm, removed));
    }

    [Fact]
    public void Values_are_written_in_the_files_style()
    {
        var modern = new VisualBasicDesignerCodeGenerator(Read(OrderForm).Model, "        ", "\n");
        var framework = new VisualBasicDesignerCodeGenerator(Read(AboutForm).Model, "        ", "\n");
        var anchor = DesignerValue.EnumOf("System.Windows.Forms.AnchorStyles", "Top", "Bottom", "Left");
        Assert.Equal("AnchorStyles.Top Or AnchorStyles.Bottom Or AnchorStyles.Left", modern.Value(anchor));
        Assert.Equal(
            "CType(((System.Windows.Forms.AnchorStyles.Top Or System.Windows.Forms.AnchorStyles.Bottom) _\n        Or System.Windows.Forms.AnchorStyles.Left), System.Windows.Forms.AnchorStyles)",
            framework.Value(anchor));
        var b = new DesignerValue { Kind = DesignerValueKinds.Literal, Type = "byte", Value = "0" };
        Assert.Equal("New Font(\"Segoe UI\", 12F, FontStyle.Bold, GraphicsUnit.Point, CByte(0))",
            modern.Value(DesignerValue.NewOf("System.Drawing.Font", DesignerValue.String("Segoe UI"), DesignerValue.Float(12F), DesignerValue.EnumOf("System.Drawing.FontStyle", "Bold"), DesignerValue.EnumOf("System.Drawing.GraphicsUnit", "Point"), b)));
        Assert.Equal("New System.Drawing.Font(\"Segoe UI\", 12.0!, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, CType(0, Byte))",
            framework.Value(DesignerValue.NewOf("System.Drawing.Font", DesignerValue.String("Segoe UI"), DesignerValue.Float(12F), DesignerValue.EnumOf("System.Drawing.FontStyle", "Bold"), DesignerValue.EnumOf("System.Drawing.GraphicsUnit", "Point"), b)));
        var color = DesignerValue.CallOf("System.Drawing.Color", "FromArgb", DesignerValue.Int(1), DesignerValue.Int(2), DesignerValue.Int(3));
        Assert.Equal("Color.FromArgb(1, 2, 3)", modern.Value(color));
        Assert.Equal("System.Drawing.Color.FromArgb(CType(CType(1, Byte), Integer), CType(CType(2, Byte), Integer), CType(CType(3, Byte), Integer))", framework.Value(color));
        Assert.Equal("New Object() {\"One\", \"Two\"}", modern.Value(DesignerValue.ArrayOf("System.Object", DesignerValue.String("One"), DesignerValue.String("Two"))));
        Assert.Equal("New Control() {}", modern.Value(DesignerValue.ArrayOf("System.Windows.Forms.Control")));
        Assert.Equal("CType(resources.GetObject(\"a.Image\"), Image)", modern.Value(new DesignerValue { Kind = DesignerValueKinds.Resource, Type = "System.Drawing.Image", Value = "a.Image" }));
        Assert.Equal("resources.GetString(\"a.Text\")", modern.Value(new DesignerValue { Kind = DesignerValueKinds.Resource, Type = "System.String", Value = "a.Text" }));
        Assert.Equal("CType(5, AnchorStyles)", modern.Value(new DesignerValue { Kind = DesignerValueKinds.Enum, Type = "System.Windows.Forms.AnchorStyles", Value = "5" }));
        Assert.Equal("0.5R", modern.Value(new DesignerValue { Kind = DesignerValueKinds.Literal, Type = "double", Value = "0.5" }));
        Assert.Equal("1.5D", modern.Value(new DesignerValue { Kind = DesignerValueKinds.Literal, Type = "decimal", Value = "1.5" }));
        Assert.Equal("\"*\"c", modern.Value(new DesignerValue { Kind = DesignerValueKinds.Literal, Type = "char", Value = "*" }));
        Assert.Equal("Global.Microsoft.VisualBasic.ChrW(42)", framework.Value(new DesignerValue { Kind = DesignerValueKinds.Literal, Type = "char", Value = "*" }));
        Assert.Equal("\"Tab\" & vbTab & \"here\" & vbLf", modern.Value(DesignerValue.String("Tab\there\n")));
        Assert.Equal("\"\"", modern.Value(DesignerValue.String(string.Empty)));
        Assert.Equal("Nothing", modern.Value(DesignerValue.Null));
        Assert.Equal("-4", modern.Value(DesignerValue.Int(-4)));
        Assert.Equal("Me", modern.Value(DesignerValue.ReferenceTo(string.Empty)));
        Assert.Equal("Me.OkButton", framework.Value(DesignerValue.ReferenceTo("OkButton")));
        Assert.Equal("GetType(OrderForm)", modern.Value(new DesignerValue { Kind = DesignerValueKinds.TypeOf, Type = "OrderForm" }));
        Assert.Equal("Global.WinFormsVbApp.My.Resources.Resources.Logo", modern.Value(DesignerValue.MemberOf("WinFormsVbApp.My.Resources.Resources", "Logo")));
        Assert.Equal("Integer", modern.Type("System.Int32"));
        Assert.Equal("Friend WithEvents Button1 As Button", modern.Field("Button1", "System.Windows.Forms.Button"));

        // Read back as written.
        foreach (var value in new[] { DesignerValue.String("a\r\nb\t\"c\"“d"), DesignerValue.String("\r\n") })
        {
            foreach (var generator in new[] { modern, framework })
            {
                var text = OrderForm.Replace("CustomerLabel.Text = \"&Customer:\"", "CustomerLabel.Text = " + generator.Value(value), StringComparison.Ordinal);
                Same(value, Assignment(Read(text).Model, "CustomerLabel", "Text").Value);
            }
        }
    }

    // ------------------------------------------------------------------ Handles clauses

    [Fact]
    public void A_forms_Handles_clauses_are_its_events()
    {
        var model = Read(OrderForm).Model;
        var tree = VisualBasicSyntaxTree.ParseText(OrderCode, cancellationToken: TestContext.Current.CancellationToken);
        var events = VisualBasicHandles.WithHandles(model, null, tree.GetRoot(TestContext.Current.CancellationToken)).Statements.Where(s => s.Kind == DesignerStatementKinds.Event)
            .Select(s => (s.Target, s.Member, s.Handler)).ToList();
        Assert.Equal(
        [
            (string.Empty, "Load", "OrderForm_Load"),
            ("CustomerTextBox", "TextChanged", "CustomerTextBox_TextChanged"),
            ("PlaceOrderButton", "Click", "OnPlaceOrder"),
            ("CancelOrderButton", "Click", "OnPlaceOrder"),
        ], events);
    }

    [Fact]
    public void Handles_items_are_added_and_removed()
    {
        var code = SourceText.From(OrderCode);
        const string Path = "OrderForm.vb";

        var one = VisualBasicHandles.Remove(code, Path, null, "OrderForm", (c, e, _) => c == "CancelOrderButton" && e == "Click").ToString();
        Assert.Equal(["-Private Sub OnPlaceOrder(sender As Object, e As EventArgs) Handles PlaceOrderButton.Click, CancelOrderButton.Click",
            "+Private Sub OnPlaceOrder(sender As Object, e As EventArgs) Handles PlaceOrderButton.Click"], Diff(OrderCode, one));
        var all = VisualBasicHandles.Remove(code, Path, null, "OrderForm", (c, e, _) => c == "CustomerTextBox").ToString();
        Assert.Equal(["-Private Sub CustomerTextBox_TextChanged(sender As Object, e As EventArgs) Handles CustomerTextBox.TextChanged",
            "+Private Sub CustomerTextBox_TextChanged(sender As Object, e As EventArgs)"], Diff(OrderCode, all));

        var added = VisualBasicHandles.AddTo(code, Path, null, "OrderForm", "OrderForm_Load", string.Empty, "Shown")!.ToString();
        Assert.Contains("Handles MyBase.Load, MyBase.Shown\n", added, StringComparison.Ordinal);
        Assert.Same(code, VisualBasicHandles.AddTo(code, Path, null, "OrderForm", "OnPlaceOrder", "placeorderbutton", "click"));
        Assert.Null(VisualBasicHandles.AddTo(code, Path, null, "OrderForm", "NoSuchMethod", "GiftCheckBox", "Click"));

        var method = VisualBasicHandles.AddMethod(code, Path, null, "OrderForm", "GiftCheckBox_CheckedChanged", ["sender As Object", "e As EventArgs"], "GiftCheckBox.CheckedChanged").ToString();
        Assert.EndsWith(
            "    End Sub\n\n    Private Sub GiftCheckBox_CheckedChanged(sender As Object, e As EventArgs) Handles GiftCheckBox.CheckedChanged\n\n    End Sub\nEnd Class\n",
            method, StringComparison.Ordinal);
        var at = VisualBasicHandles.HandlerPosition(SourceText.From(method), Path, null, "OrderForm", "GiftCheckBox_CheckedChanged")!;
        Assert.Equal((method.Split('\n').ToList().FindIndex(l => l.Contains("GiftCheckBox_CheckedChanged", StringComparison.Ordinal)) + 1, 8), (at.Line, at.Character));
    }

    [Fact]
    public void A_new_handler_makes_its_component_WithEvents()
    {
        var source = Read(AboutForm);
        Assert.False(VisualBasicHandles.UsesHandles(source, "OkButton", "Click"));
        Assert.True(VisualBasicHandles.UsesHandles(source, "OkButton", "MouseEnter"));
        Assert.True(VisualBasicHandles.UsesHandles(source, string.Empty, "Load"));
        var after = VisualBasicHandles.WithEvents(source, "OkButton").ToString();
        Assert.Equal(["-Friend OkButton As System.Windows.Forms.Button", "+Friend WithEvents OkButton As System.Windows.Forms.Button"], Diff(AboutForm, after));
        Assert.Same(source.Text, VisualBasicHandles.WithEvents(source, "TitleLabel"));
    }
}
