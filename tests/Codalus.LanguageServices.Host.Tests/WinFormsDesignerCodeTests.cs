using Codalus.LanguageServices.Host.WinForms;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// M41a (ADR 0073): the Windows Forms designer's reader and writer in the C# host, on designer files as Visual Studio
/// writes them (samples/WindowsDesktop/WinFormsApp: for .NET and, in AboutForm, for .NET Framework, localizable).
/// Roslyn only, so it runs on every OS. Reading then writing every statement again gives the file back byte for byte;
/// each change is the smallest edit, where Visual Studio's designer puts it; code the designer doesn't write fails with
/// its line.
/// </summary>
public sealed class WinFormsDesignerCodeTests
{
    private static string Sample(params string[] path) => Path.Combine([RepoPaths.Root, "samples", "WindowsDesktop", "WinFormsApp", .. path]);

    private static string OrderForm => File.ReadAllText(Sample("OrderForm.Designer.cs"));

    private static string AboutForm => File.ReadAllText(Sample("AboutForm.Designer.cs"));

    private static DesignerSource Read(string text, string baseType = "System.Windows.Forms.Form") =>
        DesignerReader.Read(CSharpSyntaxTree.ParseText(text, cancellationToken: TestContext.Current.CancellationToken), baseType, DesignerReader.WindowsFormsImplicitUsings);

    private static string Apply(string text, params DesignerEdit[] edits) =>
        new DesignerWriter(t => DesignerReader.Read(CSharpSyntaxTree.ParseText(t), "System.Windows.Forms.Form", DesignerReader.WindowsFormsImplicitUsings))
            .Apply(SourceText.From(text), edits).ToString();

    /// <summary>The changed lines, as a unified diff's body: <c>-</c> removed, <c>+</c> added, hunks in order.</summary>
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

    private static DesignerEdit AddButton(string name, string parent, int x, int y, string? path = null, int[]? cell = null) => new()
    {
        Kind = DesignerEditKinds.AddComponent,
        Component = name,
        Type = "System.Windows.Forms.Button",
        Parent = parent,
        ParentPath = path,
        Cell = cell,
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

    // ------------------------------------------------------------------ reading

    [Fact]
    public void Reads_a_form_visual_studio_wrote_for_dotnet()
    {
        var source = Read(OrderForm);
        var model = source.Model;
        Assert.Equal("OrderForm", model.ClassName);
        Assert.Equal("WinFormsApp", model.Namespace);
        Assert.False(model.UsesThis);
        Assert.False(model.QualifiedNames);
        Assert.True(model.UsesResources);
        Assert.Contains("System.Windows.Forms", model.Imports);

        // The root first, then the fields in creation order, and the resources variable as a local.
        Assert.Equal(("OrderForm", "System.Windows.Forms.Form", true), (model.Components[0].Name, model.Components[0].Type, model.Components[0].IsRoot));
        var fields = model.Components.Where(c => !c.IsRoot && !c.IsLocal).ToList();
        Assert.Equal(20, fields.Count);
        Assert.Equal(("customerLabel", "System.Windows.Forms.Label", "private"), (fields[0].Name, fields[0].Type, fields[0].Modifier));
        Assert.Equal("WinFormsApp.Controls.CounterControl", fields.Single(c => c.Name == "counterControl").Type);
        Assert.Contains(model.Components, c => c is { Name: "resources", IsLocal: true, Type: "System.ComponentModel.ComponentResourceManager" });

        DesignerStatement Assign(string target, string member) => model.Statements.Single(s => s is { Kind: DesignerStatementKinds.Assign } && s.Target == target && s.Member == member);
        Assert.Equal(["Top", "Left", "Right"], Assign("customerTextBox", "Anchor").Value!.Members!);
        Assert.Equal("System.Windows.Forms.AnchorStyles", Assign("customerTextBox", "Anchor").Value!.Type);
        var font = Assign("notesTextBox", "Font").Value!;
        Assert.Equal((DesignerValueKinds.New, "System.Drawing.Font"), (font.Kind, font.Type));
        Assert.Equal(["Segoe UI", "9", "Italic", "Point", "0"], font.Arguments!.Select(a => a.Value ?? a.Members![0]));
        Assert.Equal("float", font.Arguments![1].Type);
        var color = Assign("notesTextBox", "BackColor").Value!;
        Assert.Equal((DesignerValueKinds.Call, "System.Drawing.Color", "FromArgb"), (color.Kind, color.Type, color.Value));
        Assert.Equal((DesignerValueKinds.Member, "System.Drawing.SystemColors", "GrayText"), (Assign("notesTextBox", "ForeColor").Value!.Kind, Assign("notesTextBox", "ForeColor").Value!.Type, Assign("notesTextBox", "ForeColor").Value!.Value));
        Assert.Equal("Notes for the kitchen\r\n(one per line)", Assign("notesTextBox", "Text").Value!.Value);
        Assert.Equal((DesignerValueKinds.Resource, "System.Drawing.Image", "logoPictureBox.Image"), (Assign("logoPictureBox", "Image").Value!.Kind, Assign("logoPictureBox", "Image").Value!.Type, Assign("logoPictureBox", "Image").Value!.Value));
        Assert.Equal(0, int.Parse(Assign("placeOrderButton", "FlatAppearance.BorderSize").Value!.Value!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(DesignerValue.ReferenceTo("placeOrderButton"), Assign(string.Empty, "AcceptButton").Value!);
        var maximum = Assign("quantityUpDown", "Maximum").Value!;
        Assert.Equal((DesignerValueKinds.New, "System.Decimal"), (maximum.Kind, maximum.Type));
        Assert.Equal((DesignerValueKinds.Array, "System.Int32"), (maximum.Arguments![0].Kind, maximum.Arguments![0].Type));

        // Calls: children (a TableLayoutPanel's with their cells, a SplitContainer's panels'), collections, extender properties.
        var calls = model.Statements.Where(s => s.Kind == DesignerStatementKinds.Call).ToList();
        Assert.Contains(calls, c => c.Target == "detailsTable" && c.Member == "Controls.Add" && c.Arguments!.Select(a => a.Value).SequenceEqual(["orderDatePicker", "1", "0"]));
        Assert.Contains(calls, c => c.Target == "notesSplit" && c.Member == "Panel2.Controls.Add" && c.Arguments![0].Value == "notesTextBox");
        Assert.Contains(calls, c => c.Target == "productComboBox" && c.Member == "Items.AddRange" && c.Arguments![0].Arguments!.Select(a => a.Value).SequenceEqual(["Coffee", "Tea", "Hot chocolate"]));
        Assert.Contains(calls, c => c.Target == "orderToolTip" && c.Member == "SetToolTip" && c.Arguments![0].Value == "customerTextBox");
        Assert.Contains(calls, c => c.Target == "detailsTable" && c.Member == "ColumnStyles.Add" && c.Arguments![0].Arguments![0].Members![0] == "Absolute");
        Assert.Equal(11, calls.Count(c => c.Target.Length == 0 && c.Member == "Controls.Add"));

        // Layout calls, events and sections.
        Assert.Contains(model.Statements, s => s is { Kind: DesignerStatementKinds.Layout, Target: "notesSplit", Member: "BeginInit" });
        Assert.Contains(model.Statements, s => s is { Kind: DesignerStatementKinds.Layout, Target: "notesSplit", Member: "Panel2.PerformLayout" });
        Assert.Contains(model.Statements, s => s is { Kind: DesignerStatementKinds.Event, Target: "", Member: "Load", Handler: "OrderForm_Load", Value: null });
        Assert.Contains(model.Statements, s => s is { Kind: DesignerStatementKinds.Event, Target: "placeOrderButton", Member: "Click", Handler: "OnPlaceOrderClicked" });
        Assert.Equal(22, model.Statements.Count(s => s.Kind == DesignerStatementKinds.Section));
        Assert.Contains(model.Statements, s => s is { Kind: DesignerStatementKinds.Section, Target: "notesSplit.Panel1" });
        Assert.Equal("// ", source.SectionRule);
        Assert.Equal("            ", source.Indent);
    }

    [Fact]
    public void Reads_a_localizable_form_visual_studio_wrote_for_dotnet_framework()
    {
        var model = Read(AboutForm).Model;
        Assert.True(model.UsesThis);
        Assert.True(model.QualifiedNames);
        Assert.True(model.UsesResources);
        Assert.Contains(model.Statements, s => s is { Kind: DesignerStatementKinds.ApplyResources, Target: "okButton", Value.Value: "okButton" });
        Assert.Contains(model.Statements, s => s is { Kind: DesignerStatementKinds.ApplyResources, Target: "", Value.Value: "$this" });
        var anchor = model.Statements.Single(s => s.Member == "Anchor").Value!;
        Assert.Equal((DesignerValueKinds.Enum, "System.Windows.Forms.AnchorStyles"), (anchor.Kind, anchor.Type));
        Assert.Equal(["Top", "Left", "Right"], anchor.Members!);
        var color = model.Statements.Single(s => s.Member == "BackColor").Value!;
        Assert.Equal(["240", "240", "255"], color.Arguments!.Select(a => a.Value));
        Assert.Equal("byte", model.Statements.Single(s => s.Member == "Font").Value!.Arguments![4].Type);
        var click = model.Statements.Single(s => s.Kind == DesignerStatementKinds.Event);
        Assert.Equal(("okButton", "Click", "okButton_Click", "System.EventHandler"), (click.Target, click.Member, click.Handler, click.Value!.Type));
        Assert.Equal("System.Drawing.ContentAlignment", model.Statements.Single(s => s.Member == "TextAlign").Value!.Type);
    }

    [Fact]
    public void Reads_a_user_control_and_the_existing_forms()
    {
        var control = Read(File.ReadAllText(Sample("Controls", "CounterControl.Designer.cs")), "System.Windows.Forms.UserControl").Model;
        Assert.Equal(("CounterControl", "WinFormsApp.Controls", "System.Windows.Forms.UserControl"), (control.ClassName, control.Namespace, control.BaseType));
        Assert.Equal(["CounterControl", "countLabel", "incrementButton"], control.Components.Select(c => c.Name));
        Assert.Equal("//", Read(File.ReadAllText(Sample("MainForm.Designer.cs"))).SectionRule);
        Assert.Contains(Read(File.ReadAllText(Sample("MainForm.Designer.cs"))).Model.Components, c => c.Type == "WinFormsApp.Controls.CounterControl");
    }

    // ------------------------------------------------------------------ round trips

    public static TheoryData<string, bool> DesignerFiles => new()
    {
        { "OrderForm.Designer.cs", false },
        { "OrderForm.Designer.cs", true },
        { "AboutForm.Designer.cs", false },
        { "AboutForm.Designer.cs", true },
        { "MainForm.Designer.cs", false },
        { Path.Combine("Controls", "CounterControl.Designer.cs"), true },
        { "InventoryForm.Designer.cs", false },
        { "InventoryForm.Designer.cs", true },
    };

    /// <summary>Every statement written again from the model is the file's own text: the generator writes Visual Studio's style (both of them).</summary>
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
        var generator = new DesignerCodeGenerator(source.Model, source.Indent, source.NewLine);
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
        Assert.Empty(LineDiff.Compute(SourceText.From(text), SourceText.From(Apply(text))));

        // Setting a property to the value it has rewrites nothing either.
        var source = Read(text);
        var assignment = source.Model.Statements.Last(s => s.Kind == DesignerStatementKinds.Assign && s.Value!.Kind != DesignerValueKinds.Array);
        Assert.Equal(text, Apply(text, Set(assignment.Target, assignment.Member!, assignment.Value)));
    }

    // ------------------------------------------------------------------ properties

    [Fact]
    public void A_changed_property_is_its_value_and_nothing_else()
    {
        var after = Apply(OrderForm, Set("placeOrderButton", "Text", DesignerValue.String("&Order")), Set("placeOrderButton", "Location", Point(190, 6)));
        Assert.Equal(
        [
            "-placeOrderButton.Location = new Point(196, 8);",
            "+placeOrderButton.Location = new Point(190, 6);",
            "-placeOrderButton.Text = \"&Place order\";",
            "+placeOrderButton.Text = \"&Order\";",
        ], Diff(OrderForm, after));

        // The .NET Framework style keeps its own.
        var about = Apply(AboutForm, Set("versionLabel", "Location", Point(14, 40)), Set("versionLabel", "Anchor", DesignerValue.EnumOf("System.Windows.Forms.AnchorStyles", "Top", "Left")));
        Assert.Equal(
        [
            "-this.versionLabel.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)",
            "-| System.Windows.Forms.AnchorStyles.Right)));",
            "+this.versionLabel.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)));",
            "-this.versionLabel.Location = new System.Drawing.Point(12, 36);",
            "+this.versionLabel.Location = new System.Drawing.Point(14, 40);",
        ], Diff(AboutForm, about));
    }

    [Fact]
    public void A_new_property_goes_where_visual_studio_sorts_it_and_a_default_one_goes_away()
    {
        var after = Apply(OrderForm,
            Set("customerLabel", "ForeColor", DesignerValue.MemberOf("System.Drawing.Color", "DarkRed")),
            Set("giftCheckBox", "Checked", null),
            Set("giftCheckBox", "CheckState", null),
            Set(string.Empty, "Font", DesignerValue.NewOf("System.Drawing.Font", DesignerValue.String("Segoe UI"), DesignerValue.Float(10F))),
            Set("buttonPanel", "BackColor", DesignerValue.MemberOf("System.Drawing.SystemColors", "ControlLight")),
            Set("notesSplit", "Panel1.BackColor", DesignerValue.MemberOf("System.Drawing.Color", "White")),
            Set("detailsTable", "CellBorderStyle", DesignerValue.EnumOf("System.Windows.Forms.TableLayoutPanelCellBorderStyle", "Single")));
        Assert.Equal(
        [
            "+customerLabel.ForeColor = Color.DarkRed;",
            "-giftCheckBox.Checked = true;",
            "-giftCheckBox.CheckState = CheckState.Checked;",
            "+detailsTable.CellBorderStyle = TableLayoutPanelCellBorderStyle.Single;",
            "+notesSplit.Panel1.BackColor = Color.White;",
            "+buttonPanel.BackColor = SystemColors.ControlLight;",
            "+Font = new Font(\"Segoe UI\", 10F);",
        ], Diff(OrderForm, after));

        // Exactly where: after AutoSize, before Location; in the Panel1 sub-section, before its Controls.Add; the root's after Controls.Add.
        Assert.Contains("customerLabel.AutoSize = true;\n            customerLabel.ForeColor = Color.DarkRed;\n            customerLabel.Location", after, StringComparison.Ordinal);
        Assert.Contains("// notesSplit.Panel1\n            // \n            notesSplit.Panel1.BackColor = Color.White;\n            notesSplit.Panel1.Controls.Add(historyListBox);", after, StringComparison.Ordinal);
        Assert.Contains("detailsTable.CellBorderStyle = TableLayoutPanelCellBorderStyle.Single;\n            detailsTable.ColumnCount = 2;", after, StringComparison.Ordinal);
        Assert.Contains("Controls.Add(customerLabel);\n            Font = new Font(\"Segoe UI\", 10F);\n            FormBorderStyle", after, StringComparison.Ordinal);
        Assert.Contains("// \n            // buttonPanel\n            // \n            buttonPanel.BackColor = SystemColors.ControlLight;\n            buttonPanel.Controls.Add(cancelButton);", after, StringComparison.Ordinal);
    }

    [Fact]
    public void Values_are_written_in_the_files_style()
    {
        var modern = new DesignerCodeGenerator(Read(OrderForm).Model, "            ", "\n");
        var framework = new DesignerCodeGenerator(Read(AboutForm).Model, "            ", "\n");
        var anchor = DesignerValue.EnumOf("System.Windows.Forms.AnchorStyles", "Top", "Bottom", "Left", "Right");
        Assert.Equal("AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right", modern.Value(anchor));
        Assert.Equal(
            "((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) \n            | System.Windows.Forms.AnchorStyles.Left) \n            | System.Windows.Forms.AnchorStyles.Right)))",
            framework.Value(anchor));
        var font = DesignerValue.NewOf("System.Drawing.Font", DesignerValue.String("Segoe UI"), DesignerValue.Float(12F), DesignerValue.EnumOf("System.Drawing.FontStyle", "Bold"), DesignerValue.EnumOf("System.Drawing.GraphicsUnit", "Point"), new DesignerValue { Kind = DesignerValueKinds.Literal, Type = "byte", Value = "0" });
        Assert.Equal("new Font(\"Segoe UI\", 12F, FontStyle.Bold, GraphicsUnit.Point, 0)", modern.Value(font));
        Assert.Equal("new System.Drawing.Font(\"Segoe UI\", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)))", framework.Value(font));
        var color = DesignerValue.CallOf("System.Drawing.Color", "FromArgb", DesignerValue.Int(1), DesignerValue.Int(2), DesignerValue.Int(3));
        Assert.Equal("Color.FromArgb(1, 2, 3)", modern.Value(color));
        Assert.Equal("System.Drawing.Color.FromArgb(((int)(((byte)(1)))), ((int)(((byte)(2)))), ((int)(((byte)(3)))))", framework.Value(color));
        var items = DesignerValue.ArrayOf("System.Object", DesignerValue.String("One"), DesignerValue.String("Two"));
        Assert.Equal("new object[] { \"One\", \"Two\" }", modern.Value(items));
        Assert.Equal("new object[] {\n            \"One\",\n            \"Two\"}", framework.Value(items));
        Assert.Equal("Controls.CounterControl", modern.Type("WinFormsApp.Controls.CounterControl"));
        Assert.Equal("System.ComponentModel.Container", modern.Type("System.ComponentModel.Container"));
        Assert.Equal("WinFormsApp.Controls.CounterControl", framework.Type("WinFormsApp.Controls.CounterControl"));
        Assert.Equal("(Image)resources.GetObject(\"a.Image\")", modern.Value(new DesignerValue { Kind = DesignerValueKinds.Resource, Type = "System.Drawing.Image", Value = "a.Image" }));
        Assert.Equal("((System.Drawing.Image)(resources.GetObject(\"a.Image\")))", framework.Value(new DesignerValue { Kind = DesignerValueKinds.Resource, Type = "System.Drawing.Image", Value = "a.Image" }));
        Assert.Equal("(AnchorStyles)5", modern.Value(new DesignerValue { Kind = DesignerValueKinds.Enum, Type = "System.Windows.Forms.AnchorStyles", Value = "5" }));
        Assert.Equal("0.5D", modern.Value(new DesignerValue { Kind = DesignerValueKinds.Literal, Type = "double", Value = "0.5" }));
        Assert.Equal("'x'", modern.Value(new DesignerValue { Kind = DesignerValueKinds.Literal, Type = "char", Value = "x" }));
        Assert.Equal("\"Tab\\there\"", modern.Value(DesignerValue.String("Tab\there")));
        Assert.Equal("-4", modern.Value(DesignerValue.Int(-4)));
        Assert.Equal("typeof(OrderForm)", modern.Value(new DesignerValue { Kind = DesignerValueKinds.TypeOf, Type = "WinFormsApp.OrderForm" }));
    }

    // ------------------------------------------------------------------ components

    [Fact]
    public void A_new_control_on_the_form_gets_its_field_creation_section_and_goes_to_the_front()
    {
        var after = Apply(OrderForm, AddButton("button1", string.Empty, 290, 300));

        // Only additions (which "// " line the diff pairs with which is a matter of taste).
        List<string> added =
        [
            "+button1 = new Button();",
            "+//",
            "+// button1",
            "+//",
            "+button1.Location = new Point(290, 300);",
            "+button1.Name = \"button1\";",
            "+button1.Size = new Size(75, 23);",
            "+button1.TabIndex = 11;",
            "+button1.Text = \"button1\";",
            "+button1.UseVisualStyleBackColor = true;",
            "+Controls.Add(button1);",
            "+private Button button1;",
        ];
        Assert.Equal(added.Order(StringComparer.Ordinal), Diff(OrderForm, after).Order(StringComparer.Ordinal));
        Assert.Contains("orderToolTip = new ToolTip(components);\n            button1 = new Button();\n", after, StringComparison.Ordinal);
        Assert.Contains("counterControl.TabIndex = 9;\n            // \n            // button1\n            // \n            button1.Location", after, StringComparison.Ordinal);
        Assert.Contains("button1.UseVisualStyleBackColor = true;\n            // \n            // OrderForm\n", after, StringComparison.Ordinal);
        Assert.Contains("ClientSize = new Size(384, 401);\n            Controls.Add(button1);\n            Controls.Add(counterControl);", after, StringComparison.Ordinal);
        Assert.Contains("private ToolTip orderToolTip;\n        private Button button1;\n    }", after, StringComparison.Ordinal);

        // The new text reads back with the button in it.
        Assert.Contains(Read(after).Model.Components, c => c is { Name: "button1", Type: "System.Windows.Forms.Button" });
    }

    [Fact]
    public void A_new_control_in_containers_goes_into_their_controls()
    {
        // A panel: before its first child's Controls.Add.
        var panel = Apply(OrderForm, AddButton("helpButton", "buttonPanel", 12, 8));
        Assert.Contains("buttonPanel.Controls.Add(helpButton);\n            buttonPanel.Controls.Add(cancelButton);", panel, StringComparison.Ordinal);
        Assert.Single(Diff(OrderForm, panel), l => l.Contains("Controls.Add", StringComparison.Ordinal));

        // A SplitContainer's panel and a TableLayoutPanel's cell.
        var nested = Apply(OrderForm, AddButton("clearButton", "notesSplit", 4, 4, path: "Panel2"), AddButton("todayButton", "detailsTable", 0, 0, cell: [1, 0]));
        Assert.Contains("notesSplit.Panel2.Controls.Add(clearButton);\n            notesSplit.Panel2.Controls.Add(notesTextBox);", nested, StringComparison.Ordinal);
        Assert.Contains("detailsTable.Controls.Add(todayButton, 1, 0);\n            detailsTable.Controls.Add(dateLabel, 0, 0);", nested, StringComparison.Ordinal);

        // A new panel, then a button in it: the panel's layout calls appear with its first child, by the root's.
        var fresh = Apply(OrderForm,
            new DesignerEdit
            {
                Kind = DesignerEditKinds.AddComponent, Component = "panel1", Type = "System.Windows.Forms.Panel", Parent = string.Empty,
                Properties = [new("Location", Point(200, 70)), new("Name", DesignerValue.String("panel1")), new("Size", Size(170, 20)), new("TabIndex", DesignerValue.Int(12))],
            },
            AddButton("button1", "panel1", 0, 0));
        Assert.Contains("buttonPanel.SuspendLayout();\n            panel1.SuspendLayout();\n            SuspendLayout();", fresh, StringComparison.Ordinal);
        Assert.Contains("buttonPanel.ResumeLayout(false);\n            panel1.ResumeLayout(false);\n            ResumeLayout(false);", fresh, StringComparison.Ordinal);
        Assert.Contains("// panel1\n            // \n            panel1.Controls.Add(button1);\n            panel1.Location = new Point(200, 70);", fresh, StringComparison.Ordinal);
        var sections = Read(fresh).Model.Statements.Where(s => s.Kind == DesignerStatementKinds.Section).Select(s => s.Target).ToList();
        Assert.True(sections.IndexOf("panel1") < sections.IndexOf("button1") && sections.IndexOf("button1") < sections.IndexOf("OrderForm"), string.Join(", ", sections));
    }

    [Fact]
    public void A_new_control_in_the_dotnet_framework_style_and_a_component_in_the_container()
    {
        var about = Apply(AboutForm, AddButton("button1", string.Empty, 116, 76));
        Assert.Contains("this.versionLabel = new System.Windows.Forms.Label();\n            this.button1 = new System.Windows.Forms.Button();\n", about, StringComparison.Ordinal);
        Assert.Contains("            this.button1.Location = new System.Drawing.Point(116, 76);\n            this.button1.Name = \"button1\";", about, StringComparison.Ordinal);
        Assert.Contains("this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;\n            this.Controls.Add(this.button1);\n            this.Controls.Add(this.versionLabel);", about, StringComparison.Ordinal);
        Assert.Contains("private System.Windows.Forms.Label versionLabel;\n        private System.Windows.Forms.Button button1;", about, StringComparison.Ordinal);
        Assert.Contains("this.ShowInTaskbar = false;\n            this.ResumeLayout(false);\n            this.PerformLayout();\n\n        }", about, StringComparison.Ordinal);

        // A timer in a form without components: the container first, then the timer created in it.
        var timer = Apply(AboutForm, new DesignerEdit
        {
            Kind = DesignerEditKinds.AddComponent, Component = "timer1", Type = "System.Windows.Forms.Timer",
            CreationArguments = [DesignerValue.ReferenceTo("components")],
            Properties = [new("Interval", DesignerValue.Int(1000))],
        });
        Assert.StartsWith("            this.components = new System.ComponentModel.Container();\n            System.ComponentModel.ComponentResourceManager resources",
            timer[(timer.IndexOf("InitializeComponent()", StringComparison.Ordinal) + "InitializeComponent()\n        {\n".Length)..], StringComparison.Ordinal);
        Assert.Contains("this.timer1 = new System.Windows.Forms.Timer(this.components);", timer, StringComparison.Ordinal);
        Assert.Contains("// timer1\n            // \n            this.timer1.Interval = 1000;\n            // \n            // AboutForm", timer, StringComparison.Ordinal);
    }

    [Fact]
    public void The_first_control_of_a_new_form_brings_its_layout_calls_and_field_block()
    {
        const string template = """
            namespace WinFormsApp1
            {
                partial class Form1
                {
                    private System.ComponentModel.IContainer components = null;

                    #region Windows Form Designer generated code

                    private void InitializeComponent()
                    {
                        components = new System.ComponentModel.Container();
                        AutoScaleMode = AutoScaleMode.Font;
                        ClientSize = new Size(800, 450);
                        Text = "Form1";
                    }

                    #endregion
                }
            }

            """;
        var after = Apply(template, AddButton("button1", string.Empty, 12, 12));
        Assert.Equal("""
            namespace WinFormsApp1
            {
                partial class Form1
                {
                    private System.ComponentModel.IContainer components = null;

                    #region Windows Form Designer generated code

                    private void InitializeComponent()
                    {
                        components = new System.ComponentModel.Container();
                        button1 = new Button();
                        SuspendLayout();
                        // 
                        // button1
                        // 
                        button1.Location = new Point(12, 12);
                        button1.Name = "button1";
                        button1.Size = new Size(75, 23);
                        button1.TabIndex = 11;
                        button1.Text = "button1";
                        button1.UseVisualStyleBackColor = true;
                        AutoScaleMode = AutoScaleMode.Font;
                        ClientSize = new Size(800, 450);
                        Controls.Add(button1);
                        Text = "Form1";
                        ResumeLayout(false);
                    }

                    #endregion

                    private Button button1;
                }
            }

            """, after);
    }

    [Fact]
    public void A_removed_container_takes_its_controls_and_every_mention_of_them()
    {
        var after = Apply(OrderForm, new DesignerEdit { Kind = DesignerEditKinds.RemoveComponent, Component = "buttonPanel" });
        foreach (var name in new[] { "buttonPanel", "cancelButton", "placeOrderButton" })
        {
            Assert.DoesNotContain(name, after, StringComparison.Ordinal);
        }

        Assert.All(Diff(OrderForm, after), l => Assert.StartsWith("-", l, StringComparison.Ordinal));
        Assert.Contains("-AcceptButton = placeOrderButton;", Diff(OrderForm, after));
        Assert.Contains("-CancelButton = cancelButton;", Diff(OrderForm, after));
        var model = Read(after).Model;
        Assert.Equal(17, model.Components.Count(c => !c.IsRoot && !c.IsLocal));
        Assert.Equal(19, model.Statements.Count(s => s.Kind == DesignerStatementKinds.Section));

        // A text box: its tooltip goes with it; the tooltip removed: its calls go.
        var textBox = Apply(OrderForm, new DesignerEdit { Kind = DesignerEditKinds.RemoveComponent, Component = "customerTextBox" });
        Assert.DoesNotContain("customerTextBox", textBox, StringComparison.Ordinal);
        Assert.Contains("orderToolTip = new ToolTip(components);", textBox, StringComparison.Ordinal);
        var tooltip = Apply(OrderForm, new DesignerEdit { Kind = DesignerEditKinds.RemoveComponent, Component = "orderToolTip" });
        Assert.DoesNotContain("orderToolTip", tooltip, StringComparison.Ordinal);
        Assert.Equal(["-orderToolTip = new ToolTip(components);", "-orderToolTip.SetToolTip(customerTextBox, \"The customer who places the order.\");", "-private ToolTip orderToolTip;"], Diff(OrderForm, tooltip));
    }

    [Fact]
    public void A_renamed_component_is_renamed_everywhere_in_the_designer_file()
    {
        var after = Apply(OrderForm, new DesignerEdit { Kind = DesignerEditKinds.RenameComponent, Component = "cancelButton", NewName = "closeButton" });
        Assert.DoesNotContain("cancelButton", after, StringComparison.Ordinal);
        Assert.Contains("closeButton.Name = \"closeButton\";", after, StringComparison.Ordinal);
        Assert.Contains("            // closeButton\n", after, StringComparison.Ordinal);
        Assert.Contains("CancelButton = closeButton;", after, StringComparison.Ordinal);
        Assert.Contains("private Button closeButton;", after, StringComparison.Ordinal);
        Assert.Equal(Diff(OrderForm, after).Count(l => l.StartsWith('-')), Diff(OrderForm, after).Count(l => l.StartsWith('+')));
        Assert.Equal(26, Diff(OrderForm, after).Count);

        // The split container's sections follow it; a localizable form's component is refused; a taken name too.
        Assert.Contains("// splitter.Panel2\n", Apply(OrderForm, new DesignerEdit { Kind = DesignerEditKinds.RenameComponent, Component = "notesSplit", NewName = "splitter" }), StringComparison.Ordinal);
        var refused = Assert.Throws<InvalidOperationException>(() => Apply(AboutForm, new DesignerEdit { Kind = DesignerEditKinds.RenameComponent, Component = "okButton", NewName = "closeButton" }));
        Assert.Contains("localizable", refused.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => Apply(OrderForm, new DesignerEdit { Kind = DesignerEditKinds.RenameComponent, Component = "cancelButton", NewName = "dateLabel" }));
        Assert.Throws<InvalidOperationException>(() => Apply(OrderForm, new DesignerEdit { Kind = DesignerEditKinds.RenameComponent, Component = "cancelButton", NewName = "class" }));
    }

    [Fact]
    public void Events_are_hooked_after_the_properties_and_unhooked()
    {
        var after = Apply(OrderForm, new DesignerEdit { Kind = DesignerEditKinds.AddEvent, Component = "giftCheckBox", Event = "CheckedChanged", Handler = "giftCheckBox_CheckedChanged" });
        Assert.Equal(["+giftCheckBox.CheckedChanged += giftCheckBox_CheckedChanged;"], Diff(OrderForm, after));
        Assert.Contains("giftCheckBox.UseVisualStyleBackColor = true;\n            giftCheckBox.CheckedChanged += giftCheckBox_CheckedChanged;\n", after, StringComparison.Ordinal);

        // Alphabetically among the component's events; the root's; a handler changed in place.
        var more = Apply(OrderForm,
            new DesignerEdit { Kind = DesignerEditKinds.AddEvent, Component = "productComboBox", Event = "DropDown", Handler = "OnDropDown" },
            new DesignerEdit { Kind = DesignerEditKinds.AddEvent, Component = string.Empty, Event = "FormClosed", Handler = "OrderForm_FormClosed" },
            new DesignerEdit { Kind = DesignerEditKinds.AddEvent, Component = "placeOrderButton", Event = "Click", Handler = "OnOrder" });
        Assert.Contains("productComboBox.DropDown += OnDropDown;\n            productComboBox.SelectedIndexChanged += OnProductChanged;", more, StringComparison.Ordinal);
        Assert.Contains("FormClosed += OrderForm_FormClosed;\n            Load += OrderForm_Load;", more, StringComparison.Ordinal);
        Assert.Contains("placeOrderButton.Click += OnOrder;", more, StringComparison.Ordinal);
        Assert.Equal(4, Diff(OrderForm, more).Count);

        // .NET Framework's delegate creation.
        var about = Apply(AboutForm, new DesignerEdit { Kind = DesignerEditKinds.AddEvent, Component = "versionLabel", Event = "DoubleClick", Handler = "versionLabel_DoubleClick" });
        Assert.Contains("this.versionLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;\n            this.versionLabel.DoubleClick += new System.EventHandler(this.versionLabel_DoubleClick);", about, StringComparison.Ordinal);

        var removed = Apply(OrderForm, new DesignerEdit { Kind = DesignerEditKinds.RemoveEvent, Component = "customerTextBox", Event = "TextChanged" });
        Assert.Equal(["-customerTextBox.TextChanged += OnCustomerChanged;"], Diff(OrderForm, removed));
    }

    [Fact]
    public void A_control_moved_to_another_parent_moves_its_controls_add()
    {
        var after = Apply(OrderForm,
            new DesignerEdit { Kind = DesignerEditKinds.SetParent, Component = "giftCheckBox", Parent = "shippingGroupBox" },
            Set("giftCheckBox", "Location", Point(200, 22)));
        Assert.Equal(
        [
            "-giftCheckBox.Location = new Point(96, 70);",
            "+giftCheckBox.Location = new Point(200, 22);",
            "+shippingGroupBox.Controls.Add(giftCheckBox);",
            "-Controls.Add(giftCheckBox);",
        ], Diff(OrderForm, after));
    }

    // ------------------------------------------------------------------ what can't be read

    [Fact]
    public void Code_the_designer_doesnt_write_fails_with_its_line()
    {
        var unreadable = File.ReadAllText(Sample("UnreadableForm.Designer.cs"));
        var error = Assert.Throws<DesignerReadException>(() => Read(unreadable));
        Assert.Equal(unreadable.Split('\n').ToList().FindIndex(l => l.Contains("=> Close()", StringComparison.Ordinal)), error.Line);
        Assert.Contains("lambda", error.Message, StringComparison.Ordinal);

        static string Body(string statements) => $$"""
            partial class Form1
            {
                private void InitializeComponent()
                {
                    button1 = new Button();
            {{statements}}
                }

                private Button button1;
                private string greeting;
            }
            """;
        foreach (var (code, reason) in new[]
                 {
                     ("if (DesignMode) { button1.Text = \"x\"; }", "if"),
                     ("for (var i = 0; i < 2; i++) { }", "a loop"),
                     ("ApplyTheme();", "own method ApplyTheme"),
                     ("button1.Text = greeting;", "greeting isn't a component"),
                     ("button1.Text = button1.Name;", "property read of button1"),
                     ("button1.Location = new Point { X = 1 };", "object initializer"),
                     ("button1.Click += Handlers.OnClick;", "isn't a method of the class"),
                     ("button1.Click += delegate { };", "lambda"),
                     ("var margin = 4;", "isn't created with new"),
                     ("button1.Text = $\"{1}\";", "the expression"),
                     ("button1.Tag = button1.ToString();", "button1"),
                 })
        {
            var failure = Assert.Throws<DesignerReadException>(() => Read(Body("        " + code)));
            Assert.Contains(reason, failure.Message, StringComparison.Ordinal);
            Assert.Equal(5, failure.Line);
        }

        Assert.Throws<DesignerReadException>(() => Read("class Form1 { }"));
    }

    [Fact]
    public void The_line_diff_is_the_changed_lines()
    {
        static List<(string Old, string New)> Changes(string a, string b) =>
            [.. LineDiff.Compute(SourceText.From(a), SourceText.From(b)).Select(c => (a.Substring(c.Span.Start, c.Span.Length), c.NewText ?? string.Empty))];
        Assert.Empty(Changes("a\nb\n", "a\nb\n"));
        Assert.Equal([("b\n", "x\n")], Changes("a\nb\nc\n", "a\nx\nc\n"));
        Assert.Equal([(string.Empty, "x\n")], Changes("a\nb\n", "a\nx\nb\n"));
        Assert.Equal([("b\n", string.Empty)], Changes("a\nb\nc\n", "a\nc\n"));
        Assert.Equal([("a\n", string.Empty), (string.Empty, "z\n")], Changes("a\nb\nc\n", "b\nc\nz\n"));
        Assert.Equal([("b\r\n", "y\r\n")], Changes("a\r\nb\r\nc", "a\r\ny\r\nc"));
    }

    // ------------------------------------------------------------------ M41b: menus, collections, extenders, bindings

    private static string InventoryForm => File.ReadAllText(Sample("InventoryForm.Designer.cs"));

    /// <summary>A form's designer file and its resource files (read from the fixture, changed in memory), edited together.</summary>
    private static (string Designer, DesignerResourceSet Resources) ApplyWithResources(string form, params DesignerEdit[] edits)
    {
        var resources = new DesignerResourceSet(Sample(form + ".cs"), p => File.Exists(p) ? File.ReadAllText(p) : null);
        var writer = new DesignerWriter(t => DesignerReader.Read(CSharpSyntaxTree.ParseText(t), "System.Windows.Forms.Form", DesignerReader.WindowsFormsImplicitUsings), resources);
        return (writer.Apply(SourceText.From(File.ReadAllText(Sample(form + ".Designer.cs"))), edits).ToString(), resources);
    }

    private static string Resx(string file) => File.ReadAllText(Sample(file));

    private static DesignerEdit Extender(string component, string provider, string property, DesignerValue? value) =>
        new() { Kind = DesignerEditKinds.SetExtender, Component = component, Provider = provider, Property = property, Value = value };

    private static DesignerEdit Collection(string component, string property, string method, string? elementType, params DesignerValue[] items) =>
        new() { Kind = DesignerEditKinds.SetCollection, Component = component, Property = property, Method = method, Type = elementType, Items = items };

    private static DesignerEdit Resource(string key, DesignerResource? resource, string culture = "", bool metadata = false) =>
        new() { Kind = DesignerEditKinds.SetResource, Key = key, Resource = resource, Culture = culture, Metadata = metadata };

    private static DesignerValue Binding(string property, string source, string member) =>
        DesignerValue.NewOf("System.Windows.Forms.Binding", DesignerValue.String(property), DesignerValue.ReferenceTo(source), DesignerValue.String(member), DesignerValue.Bool(true));

    private static DesignerValue[] Refs(params string[] names) => [.. names.Select(DesignerValue.ReferenceTo)];

    /// <summary>Only additions, in any order (which "// " line the diff pairs with which is a matter of taste).</summary>
    private static void AssertAdded(IEnumerable<string> expected, IEnumerable<string> actual) =>
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Select(l => l.TrimEnd()).Order(StringComparer.Ordinal));

    [Fact]
    public void Reads_menus_collections_extenders_and_bindings_visual_studio_wrote()
    {
        var model = Read(InventoryForm).Model;
        Assert.Equal("System.Windows.Forms.ToolStripMenuItem", model.Components.Single(c => c.Name == "fileToolStripMenuItem").Type);
        Assert.Equal("System.Windows.Forms.BindingSource", model.Components.Single(c => c.Name == "itemBindingSource").Type);
        Assert.Contains(model.Components, c => c is { Name: "listViewItem1", IsLocal: true, Type: "System.Windows.Forms.ListViewItem" });
        DesignerStatement Call(string target, string member) => model.Statements.Single(s => s.Kind == DesignerStatementKinds.Call && s.Target == target && s.Member == member);
        Assert.Equal(["newToolStripMenuItem", "toolStripSeparator1", "exitToolStripMenuItem"], Call("fileToolStripMenuItem", "DropDownItems.AddRange").Arguments![0].Arguments!.Select(a => a.Value));
        Assert.Equal("System.Windows.Forms.ToolStripItem", Call("mainMenuStrip", "Items.AddRange").Arguments![0].Type);
        Assert.Equal(["listViewItem1", "listViewItem2"], Call("itemsListView", "Items.AddRange").Arguments![0].Arguments!.Select(a => a.Value));
        Assert.Equal(["locationColumn", "quantityColumn"], Call("stockGridView", "Columns.AddRange").Arguments![0].Arguments!.Select(a => a.Value));
        var binding = Call("nameTextBox", "DataBindings.Add").Arguments![0];
        Assert.Equal(("System.Windows.Forms.Binding", "Text", "itemBindingSource", "Name"), (binding.Type, binding.Arguments![0].Value, binding.Arguments[1].Value, binding.Arguments[2].Value));
        Assert.Equal("The item's name, as it shows on the shelf.", Call("inventoryHelpProvider", "SetHelpString").Arguments![1].Value);
        Assert.Equal(("WinFormsApp.InventoryItem", DesignerValueKinds.TypeOf), (model.Statements.Single(s => s.Member == "DataSource").Value!.Type, model.Statements.Single(s => s.Member == "DataSource").Value!.Kind));
        Assert.Equal(["Control", "N"], model.Statements.Single(s => s.Member == "ShortcutKeys").Value!.Members!);
        Assert.Equal(DesignerValue.ReferenceTo(string.Empty), model.Statements.Single(s => s.Member == "ContainerControl").Value);
    }

    [Fact]
    public void Bring_to_front_and_send_to_back_move_the_controls_add_as_it_is()
    {
        // The root's controls are written front first: Bring to Front moves a control's Controls.Add first, Send to Back last.
        var front = Apply(OrderForm, new DesignerEdit { Kind = DesignerEditKinds.SetChildIndex, Component = "giftCheckBox", Index = 0 });
        Assert.Equal(["+Controls.Add(giftCheckBox);", "-Controls.Add(giftCheckBox);"], Diff(OrderForm, front));
        Assert.Contains("ClientSize = new Size(384, 401);\n            Controls.Add(giftCheckBox);\n            Controls.Add(counterControl);", front, StringComparison.Ordinal);
        var back = Apply(OrderForm, new DesignerEdit { Kind = DesignerEditKinds.SetChildIndex, Component = "giftCheckBox", Index = int.MaxValue });
        Assert.Contains("Controls.Add(customerLabel);\n            Controls.Add(giftCheckBox);\n            FormBorderStyle", back, StringComparison.Ordinal);
        Assert.Equal(2, Diff(OrderForm, back).Count);

        // Already there: no edit. In a table, the cell stays with it; a tab control's pages are its tabs' order.
        Assert.Equal(OrderForm, Apply(OrderForm, new DesignerEdit { Kind = DesignerEditKinds.SetChildIndex, Component = "counterControl", Index = 0 }));
        Assert.Contains("detailsTable.Controls.Add(orderDatePicker, 1, 0);\n            detailsTable.Controls.Add(dateLabel, 0, 0);",
            Apply(OrderForm, new DesignerEdit { Kind = DesignerEditKinds.SetChildIndex, Component = "orderDatePicker", Index = 0 }), StringComparison.Ordinal);
        Assert.Contains("detailsTabControl.Controls.Add(stockTabPage);\n            detailsTabControl.Controls.Add(generalTabPage);",
            Apply(InventoryForm, new DesignerEdit { Kind = DesignerEditKinds.SetChildIndex, Component = "stockTabPage", Index = 0 }), StringComparison.Ordinal);

        // The .NET Framework style's statement moves with its this.
        var about = Apply(AboutForm, new DesignerEdit { Kind = DesignerEditKinds.SetChildIndex, Component = "titleLabel", Index = 0 });
        Assert.Contains("this.Controls.Add(this.titleLabel);\n            this.Controls.Add(this.versionLabel);\n            this.Controls.Add(this.okButton);", about, StringComparison.Ordinal);
    }

    [Fact]
    public void Tab_order_is_each_controls_tab_index_and_nothing_else()
    {
        // Tab order mode's clicks: each a TabIndex, in place (no sibling moves).
        var after = Apply(OrderForm,
            Set("customerTextBox", "TabIndex", DesignerValue.Int(0)),
            Set("customerLabel", "TabIndex", DesignerValue.Int(1)),
            Set("standardRadioButton", "TabIndex", DesignerValue.Int(1)),
            Set("expressRadioButton", "TabIndex", DesignerValue.Int(0)));
        Assert.Equal(
        [
            "-customerLabel.TabIndex = 0;",
            "+customerLabel.TabIndex = 1;",
            "-customerTextBox.TabIndex = 1;",
            "+customerTextBox.TabIndex = 0;",
            "-expressRadioButton.TabIndex = 1;",
            "+expressRadioButton.TabIndex = 0;",
            "-standardRadioButton.TabIndex = 0;",
            "+standardRadioButton.TabIndex = 1;",
        ], Diff(OrderForm, after));
    }

    [Fact]
    public void Extender_properties_are_the_providers_set_calls_in_the_extended_components_section()
    {
        // A new tool tip: after TabIndex (sorted as ToolTip), before the events.
        var added = Apply(OrderForm, Extender("productComboBox", "orderToolTip", "ToolTip", DesignerValue.String("What to order.")));
        Assert.Equal(["+orderToolTip.SetToolTip(productComboBox, \"What to order.\");"], Diff(OrderForm, added));
        Assert.Contains("productComboBox.TabIndex = 2;\n            orderToolTip.SetToolTip(productComboBox, \"What to order.\");\n            productComboBox.SelectedIndexChanged += OnProductChanged;", added, StringComparison.Ordinal);

        // Changed in place; removed (back to its default).
        Assert.Equal(
            ["-orderToolTip.SetToolTip(customerTextBox, \"The customer who places the order.\");", "+orderToolTip.SetToolTip(customerTextBox, \"Who orders.\");"],
            Diff(OrderForm, Apply(OrderForm, Extender("customerTextBox", "orderToolTip", "ToolTip", DesignerValue.String("Who orders.")))));
        Assert.Equal(["-orderToolTip.SetToolTip(customerTextBox, \"The customer who places the order.\");"],
            Diff(OrderForm, Apply(OrderForm, Extender("customerTextBox", "orderToolTip", "ToolTip", null))));

        // A help provider's two properties sort among the text box's by their names; one on a control without any.
        var help = Apply(InventoryForm,
            Extender("nameTextBox", "inventoryHelpProvider", "ShowHelp", DesignerValue.Bool(false)),
            Extender("categoryListBox", "inventoryHelpProvider", "HelpString", DesignerValue.String("Pick one.")));
        Assert.Equal(
        [
            "+inventoryHelpProvider.SetHelpString(categoryListBox, \"Pick one.\");",
            "-inventoryHelpProvider.SetShowHelp(nameTextBox, true);",
            "+inventoryHelpProvider.SetShowHelp(nameTextBox, false);",
        ], Diff(InventoryForm, help));
        Assert.Contains("categoryListBox.FormattingEnabled = true;\n            inventoryHelpProvider.SetHelpString(categoryListBox, \"Pick one.\");\n            categoryListBox.Items.AddRange", help, StringComparison.Ordinal);

        // .NET Framework's style: this.provider.SetX(this.component, …).
        const string framework = """
            partial class Form1
            {
                private void InitializeComponent()
                {
                    this.components = new System.ComponentModel.Container();
                    this.button1 = new System.Windows.Forms.Button();
                    this.toolTip1 = new System.Windows.Forms.ToolTip(this.components);
                    // 
                    // button1
                    // 
                    this.button1.Location = new System.Drawing.Point(12, 12);
                    this.button1.Name = "button1";
                }

                private System.Windows.Forms.Button button1;
                private System.Windows.Forms.ToolTip toolTip1;
            }
            """;
        Assert.Contains("this.button1.Name = \"button1\";\n        this.toolTip1.SetToolTip(this.button1, \"Saves\");",
            Apply(framework.Replace("\r\n", "\n", StringComparison.Ordinal), Extender("button1", "toolTip1", "ToolTip", DesignerValue.String("Saves"))), StringComparison.Ordinal);
    }

    [Fact]
    public void Collections_are_written_again_in_place_as_visual_studio_writes_them()
    {
        // A list box's items: the AddRange line, in place; emptied, it goes; a list without items gets one where Items sorts.
        var items = Apply(InventoryForm, Collection("categoryListBox", "Items", "AddRange", "System.Object", DesignerValue.String("Tools"), DesignerValue.String("Garden"), DesignerValue.String("Paint"), DesignerValue.String("Plumbing")));
        Assert.Equal(["-categoryListBox.Items.AddRange(new object[] { \"Tools\", \"Garden\", \"Paint\" });", "+categoryListBox.Items.AddRange(new object[] { \"Tools\", \"Garden\", \"Paint\", \"Plumbing\" });"], Diff(InventoryForm, items));
        Assert.Equal(["-categoryListBox.Items.AddRange(new object[] { \"Tools\", \"Garden\", \"Paint\" });"], Diff(InventoryForm, Apply(InventoryForm, Collection("categoryListBox", "Items", "AddRange", "System.Object"))));
        var fresh = Apply(OrderForm, Collection("historyListBox", "Items", "AddRange", "System.Object", DesignerValue.String("Espresso")));
        Assert.Contains("historyListBox.ItemHeight = 15;\n            historyListBox.Items.AddRange(new object[] { \"Espresso\" });\n            historyListBox.Location", fresh, StringComparison.Ordinal);
        Assert.Equal(InventoryForm, Apply(InventoryForm, Collection("categoryListBox", "Items", "AddRange", "System.Object", DesignerValue.String("Tools"), DesignerValue.String("Garden"), DesignerValue.String("Paint"))));

        // A table's column styles: one Add each, where the old ones were.
        DesignerValue Style(string sizeType, float value) => DesignerValue.NewOf("System.Windows.Forms.ColumnStyle", DesignerValue.EnumOf("System.Windows.Forms.SizeType", sizeType), DesignerValue.Float(value));
        var styles = Apply(OrderForm, Collection("detailsTable", "ColumnStyles", "Add", null, Style("Absolute", 84F), Style("Percent", 50F), Style("Percent", 50F)), Set("detailsTable", "ColumnCount", DesignerValue.Int(3)));
        Assert.Equal(
        [
            "-detailsTable.ColumnCount = 2;",
            "+detailsTable.ColumnCount = 3;",
            "-detailsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));",
            "+detailsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));",
            "+detailsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));",
        ], Diff(OrderForm, styles));

        // A list view's columns (components) reordered; a grid's columns with a new one (a field, its creation, its section, the array).
        Assert.Equal(
            ["-itemsListView.Columns.AddRange(new ColumnHeader[] { nameColumnHeader, countColumnHeader });", "+itemsListView.Columns.AddRange(new ColumnHeader[] { countColumnHeader, nameColumnHeader });"],
            Diff(InventoryForm, Apply(InventoryForm, Collection("itemsListView", "Columns", "AddRange", "System.Windows.Forms.ColumnHeader", DesignerValue.ReferenceTo("countColumnHeader"), DesignerValue.ReferenceTo("nameColumnHeader")))));
        var grid = Apply(InventoryForm,
            new DesignerEdit
            {
                Kind = DesignerEditKinds.AddComponent, Component = "inStockColumn", Type = "System.Windows.Forms.DataGridViewCheckBoxColumn",
                Properties = [new("HeaderText", DesignerValue.String("In stock")), new("Name", DesignerValue.String("inStockColumn"))],
            },
            Collection("stockGridView", "Columns", "AddRange", "System.Windows.Forms.DataGridViewColumn", DesignerValue.ReferenceTo("locationColumn"), DesignerValue.ReferenceTo("quantityColumn"), DesignerValue.ReferenceTo("inStockColumn")));
        AssertAdded(
        [
            "+inStockColumn = new DataGridViewCheckBoxColumn();",
            "-stockGridView.Columns.AddRange(new DataGridViewColumn[] { locationColumn, quantityColumn });",
            "+stockGridView.Columns.AddRange(new DataGridViewColumn[] { locationColumn, quantityColumn, inStockColumn });",
            "+//",
            "+// inStockColumn",
            "+//",
            "+inStockColumn.HeaderText = \"In stock\";",
            "+inStockColumn.Name = \"inStockColumn\";",
            "+private DataGridViewCheckBoxColumn inStockColumn;",
        ], Diff(InventoryForm, grid));
        Assert.Contains("            // \n            // inStockColumn\n            // \n            inStockColumn.HeaderText = \"In stock\";\n            inStockColumn.Name = \"inStockColumn\";\n            // \n            // InventoryForm\n", grid, StringComparison.Ordinal);

        // A list view's items are locals, declared before the components are created, as Visual Studio declares them.
        var listItems = Apply(InventoryForm,
            new DesignerEdit { Kind = DesignerEditKinds.AddLocal, Component = "listViewItem3", Value = DesignerValue.NewOf("System.Windows.Forms.ListViewItem", DesignerValue.String("Drill")) },
            Collection("itemsListView", "Items", "AddRange", "System.Windows.Forms.ListViewItem", DesignerValue.ReferenceTo("listViewItem1"), DesignerValue.ReferenceTo("listViewItem2"), DesignerValue.ReferenceTo("listViewItem3")));
        Assert.Equal(
        [
            "+ListViewItem listViewItem3 = new ListViewItem(\"Drill\");",
            "-itemsListView.Items.AddRange(new ListViewItem[] { listViewItem1, listViewItem2 });",
            "+itemsListView.Items.AddRange(new ListViewItem[] { listViewItem1, listViewItem2, listViewItem3 });",
        ], Diff(InventoryForm, listItems));
        Assert.Contains("ListViewItem listViewItem2 = new ListViewItem(\"Saw\");\n            ListViewItem listViewItem3 = new ListViewItem(\"Drill\");\n            mainMenuStrip = new MenuStrip();", listItems, StringComparison.Ordinal);
        var removedItem = Apply(InventoryForm, new DesignerEdit { Kind = DesignerEditKinds.RemoveComponent, Component = "listViewItem1" });
        Assert.Equal(
        [
            "-ListViewItem listViewItem1 = new ListViewItem(\"Hammer\");",
            "-itemsListView.Items.AddRange(new ListViewItem[] { listViewItem1, listViewItem2 });",
            "+itemsListView.Items.AddRange(new ListViewItem[] { listViewItem2 });",
        ], Diff(InventoryForm, removedItem));

        // .NET Framework's arrays, one item a line.
        const string framework = """
            partial class Form1
            {
                private void InitializeComponent()
                {
                    this.listBox1 = new System.Windows.Forms.ListBox();
                    // 
                    // listBox1
                    // 
                    this.listBox1.FormattingEnabled = true;
                    this.listBox1.Location = new System.Drawing.Point(12, 12);
                }

                private System.Windows.Forms.ListBox listBox1;
            }
            """;
        Assert.Contains("this.listBox1.FormattingEnabled = true;\n        this.listBox1.Items.AddRange(new object[] {\n        \"One\",\n        \"Two\"});\n        this.listBox1.Location",
            Apply(framework.Replace("\r\n", "\n", StringComparison.Ordinal), Collection("listBox1", "Items", "AddRange", "System.Object", DesignerValue.String("One"), DesignerValue.String("Two"))), StringComparison.Ordinal);
    }

    [Fact]
    public void Menu_and_tool_strip_items_are_components_in_their_owners_add_range()
    {
        // Type Here on the File menu: a menu item (its field, creation and section) at the end of the drop-down's AddRange.
        var opened = Apply(InventoryForm,
            new DesignerEdit
            {
                Kind = DesignerEditKinds.AddComponent, Component = "openToolStripMenuItem", Type = "System.Windows.Forms.ToolStripMenuItem",
                Properties = [new("Name", DesignerValue.String("openToolStripMenuItem")), new("Size", Size(141, 22)), new("Text", DesignerValue.String("&Open…"))],
            },
            Collection("fileToolStripMenuItem", "DropDownItems", "AddRange", "System.Windows.Forms.ToolStripItem", Refs("newToolStripMenuItem", "openToolStripMenuItem", "toolStripSeparator1", "exitToolStripMenuItem")));
        AssertAdded(
        [
            "+openToolStripMenuItem = new ToolStripMenuItem();",
            "-fileToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { newToolStripMenuItem, toolStripSeparator1, exitToolStripMenuItem });",
            "+fileToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { newToolStripMenuItem, openToolStripMenuItem, toolStripSeparator1, exitToolStripMenuItem });",
            "+//",
            "+// openToolStripMenuItem",
            "+//",
            "+openToolStripMenuItem.Name = \"openToolStripMenuItem\";",
            "+openToolStripMenuItem.Size = new Size(141, 22);",
            "+openToolStripMenuItem.Text = \"&Open…\";",
            "+private ToolStripMenuItem openToolStripMenuItem;",
        ], Diff(InventoryForm, opened));
        Assert.Contains("inventoryErrorProvider = new ErrorProvider(components);\n            openToolStripMenuItem = new ToolStripMenuItem();\n            mainMenuStrip.SuspendLayout();", opened, StringComparison.Ordinal);
        Assert.Contains(Read(opened).Model.Components, c => c.Name == "openToolStripMenuItem");

        // A top-level menu and a tool strip button reordered; an item removed leaves the array without it.
        Assert.Equal(
            ["-mainMenuStrip.Items.AddRange(new ToolStripItem[] { fileToolStripMenuItem, helpToolStripMenuItem });", "+mainMenuStrip.Items.AddRange(new ToolStripItem[] { helpToolStripMenuItem, fileToolStripMenuItem });"],
            Diff(InventoryForm, Apply(InventoryForm, Collection("mainMenuStrip", "Items", "AddRange", "System.Windows.Forms.ToolStripItem", Refs("helpToolStripMenuItem", "fileToolStripMenuItem")))));
        var removed = Apply(InventoryForm, new DesignerEdit { Kind = DesignerEditKinds.RemoveComponent, Component = "toolStripSeparator2" });
        AssertAdded(
        [
            "-toolStripSeparator2 = new ToolStripSeparator();",
            "-mainToolStrip.Items.AddRange(new ToolStripItem[] { newToolStripButton, toolStripSeparator2, searchToolStripLabel });",
            "+mainToolStrip.Items.AddRange(new ToolStripItem[] { newToolStripButton, searchToolStripLabel });",
            "-//",
            "-// toolStripSeparator2",
            "-//",
            "-toolStripSeparator2.Name = \"toolStripSeparator2\";",
            "-toolStripSeparator2.Size = new Size(6, 25);",
            "-private ToolStripSeparator toolStripSeparator2;",
        ], Diff(InventoryForm, removed));

        // A new menu strip's first item: its Items line where Items sorts.
        var strip = Apply(OrderForm,
            new DesignerEdit
            {
                Kind = DesignerEditKinds.AddComponent, Component = "menuStrip1", Type = "System.Windows.Forms.MenuStrip", Parent = string.Empty,
                Properties = [new("Location", Point(0, 0)), new("Name", DesignerValue.String("menuStrip1")), new("Size", Size(384, 24)), new("TabIndex", DesignerValue.Int(11)), new("Text", DesignerValue.String("menuStrip1"))],
            },
            new DesignerEdit
            {
                Kind = DesignerEditKinds.AddComponent, Component = "fileToolStripMenuItem", Type = "System.Windows.Forms.ToolStripMenuItem",
                Properties = [new("Name", DesignerValue.String("fileToolStripMenuItem")), new("Size", Size(37, 20)), new("Text", DesignerValue.String("&File"))],
            },
            Collection("menuStrip1", "Items", "AddRange", "System.Windows.Forms.ToolStripItem", Refs("fileToolStripMenuItem")),
            Set(string.Empty, "MainMenuStrip", DesignerValue.ReferenceTo("menuStrip1")));
        Assert.Contains("            // menuStrip1\n            // \n            menuStrip1.Items.AddRange(new ToolStripItem[] { fileToolStripMenuItem });\n            menuStrip1.Location = new Point(0, 0);", strip, StringComparison.Ordinal);
        Assert.Contains("            Controls.Add(menuStrip1);\n            Controls.Add(counterControl);", strip, StringComparison.Ordinal);
        Assert.Contains("            buttonPanel.SuspendLayout();\n            menuStrip1.SuspendLayout();\n            SuspendLayout();", strip, StringComparison.Ordinal);
        Assert.Contains("            buttonPanel.ResumeLayout(false);\n            menuStrip1.ResumeLayout(false);\n            menuStrip1.PerformLayout();\n            ResumeLayout(false);", strip, StringComparison.Ordinal);
        Assert.Contains("            FormBorderStyle = FormBorderStyle.FixedDialog;\n            MainMenuStrip = menuStrip1;\n            MaximizeBox = false;", strip, StringComparison.Ordinal);
    }

    [Fact]
    public void Data_bindings_are_the_controls_data_bindings_add_calls()
    {
        // The Text binding to another member: in place; a second property's binding: after the first; removed.
        Assert.Equal(
            ["-nameTextBox.DataBindings.Add(new Binding(\"Text\", itemBindingSource, \"Name\", true));", "+nameTextBox.DataBindings.Add(new Binding(\"Text\", itemBindingSource, \"Quantity\", true));"],
            Diff(InventoryForm, Apply(InventoryForm, new DesignerEdit { Kind = DesignerEditKinds.SetDataBinding, Component = "nameTextBox", Property = "Text", Value = Binding("Text", "itemBindingSource", "Quantity") })));
        var tag = Apply(InventoryForm, new DesignerEdit { Kind = DesignerEditKinds.SetDataBinding, Component = "nameTextBox", Property = "Tag", Value = Binding("Tag", "itemBindingSource", "InStock") });
        Assert.Contains("nameTextBox.DataBindings.Add(new Binding(\"Text\", itemBindingSource, \"Name\", true));\n            nameTextBox.DataBindings.Add(new Binding(\"Tag\", itemBindingSource, \"InStock\", true));\n            inventoryHelpProvider.SetHelpString", tag, StringComparison.Ordinal);
        Assert.Equal(["-nameTextBox.DataBindings.Add(new Binding(\"Text\", itemBindingSource, \"Name\", true));"],
            Diff(InventoryForm, Apply(InventoryForm, new DesignerEdit { Kind = DesignerEditKinds.SetDataBinding, Component = "nameTextBox", Property = "Text" })));

        // A control bound for the first time: where DataBindings sorts; the binding source removed takes its bindings.
        var list = Apply(InventoryForm, new DesignerEdit { Kind = DesignerEditKinds.SetDataBinding, Component = "categoryListBox", Property = "Text", Value = Binding("Text", "itemBindingSource", "Name") });
        Assert.Contains("categoryListBox.ContextMenuStrip = itemsContextMenuStrip;\n            categoryListBox.DataBindings.Add(new Binding(\"Text\", itemBindingSource, \"Name\", true));\n            categoryListBox.FormattingEnabled", list, StringComparison.Ordinal);
        var unbound = Apply(InventoryForm, new DesignerEdit { Kind = DesignerEditKinds.RemoveComponent, Component = "itemBindingSource" });
        Assert.DoesNotContain("itemBindingSource", unbound, StringComparison.Ordinal);
        Assert.DoesNotContain("DataBindings", unbound, StringComparison.Ordinal);
    }

    [Fact]
    public void A_localizable_forms_properties_go_to_its_resx_and_per_culture()
    {
        // A localizable property: the default .resx's value, nothing in the code.
        var (code, resources) = ApplyWithResources("AboutForm", Resource("okButton.Text", DesignerResource.String("Close")));
        Assert.Equal(AboutForm, code);
        Assert.Equal(["-<value>OK</value>", "+<value>Close</value>"], Diff(Resx("AboutForm.resx"), resources.Get(null)!.Text));

        // In French: AboutForm.fr.resx; in German, a new AboutForm.de.resx with only that key.
        (_, resources) = ApplyWithResources("AboutForm", Resource("titleLabel.Text", null, "fr"));
        Assert.Equal(
        [
            "-<data name=\"titleLabel.Text\" xml:space=\"preserve\">",
            "-<value>Codalus WinForms (français)</value>",
            "-</data>",
        ], Diff(Resx("AboutForm.fr.resx"), resources.Get("fr")!.Text));
        (_, resources) = ApplyWithResources("AboutForm",
            Resource("okButton.Text", DesignerResource.String("Fermer la fenêtre"), "fr"),
            Resource("okButton.Text", DesignerResource.String("Schließen"), "de"));
        Assert.Equal(["-<value>Fermer</value>", "+<value>Fermer la fenêtre</value>"], Diff(Resx("AboutForm.fr.resx"), resources.Get("fr")!.Text));
        Assert.Equal(Resx("AboutForm.resx"), resources.Get(null)!.Text);
        var german = resources.Get("de")!.Text;
        Assert.Equal(ResxText.Empty.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("</root>", "  <data name=\"okButton.Text\" xml:space=\"preserve\">\n    <value>Schließen</value>\n  </data>\n</root>", StringComparison.Ordinal), german);
        Assert.Contains(resources.Changes, c => c.Path.EndsWith("AboutForm.de.resx", StringComparison.Ordinal) && c.Before is null);
        Assert.Equal(["de", "fr"], resources.Cultures);

        // A property that was in the code moves to the resources: ApplyResources first in the component's section,
        // the assignment gone; its key in a new group before the form's ($this) entries, with Visual Studio's records.
        DesignerResource Point(string text) => new() { Type = "System.Drawing.Point, System.Drawing", Text = text, AssemblyAlias = "System.Drawing", AssemblyName = "System.Drawing, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a" };
        (code, resources) = ApplyWithResources("AboutForm",
            new DesignerEdit { Kind = DesignerEditKinds.SetLocalized, Component = "versionLabel", Localized = true },
            Set("versionLabel", "Text", null),
            Set("versionLabel", "Location", null),
            new DesignerEdit { Kind = DesignerEditKinds.SetResource, Component = "versionLabel", Property = "Text", Resource = DesignerResource.String("Version 1.0") },
            new DesignerEdit { Kind = DesignerEditKinds.SetResource, Component = "versionLabel", Property = "Location", Resource = Point("12, 36") },
            Resource(">>versionLabel.Name", DesignerResource.String("versionLabel")),
            Resource(">>versionLabel.Parent", DesignerResource.String("$this")));
        Assert.Equal(
        [
            "+resources.ApplyResources(this.versionLabel, \"versionLabel\");",
            "-this.versionLabel.Location = new System.Drawing.Point(12, 36);",
            "-this.versionLabel.Text = \"Version 1.0\";",
        ], Diff(AboutForm, code));
        Assert.Contains("            // versionLabel\n            // \n            resources.ApplyResources(this.versionLabel, \"versionLabel\");\n            this.versionLabel.Anchor", code, StringComparison.Ordinal);
        Assert.Equal(
        [
            "+<data name=\"versionLabel.Location\" type=\"System.Drawing.Point, System.Drawing\">",
            "+<value>12, 36</value>",
            "+</data>",
            "+<data name=\"versionLabel.Text\" xml:space=\"preserve\">",
            "+<value>Version 1.0</value>",
            "+</data>",
            "+<data name=\"&gt;&gt;versionLabel.Name\" xml:space=\"preserve\">",
            "+<value>versionLabel</value>",
            "+</data>",
            "+<data name=\"&gt;&gt;versionLabel.Parent\" xml:space=\"preserve\">",
            "+<value>$this</value>",
            "+</data>",
        ], Diff(Resx("AboutForm.resx"), resources.Get(null)!.Text));
        Assert.Contains("    <value>0</value>\n  </data>\n  <data name=\"versionLabel.Location\"", resources.Get(null)!.Text, StringComparison.Ordinal);

        // Back out of the resources: ApplyResources goes when the shell says so.
        (code, _) = ApplyWithResources("AboutForm", new DesignerEdit { Kind = DesignerEditKinds.SetLocalized, Component = "okButton", Localized = false });
        Assert.Equal(["-resources.ApplyResources(this.okButton, \"okButton\");"], Diff(AboutForm, code));
    }

    [Fact]
    public void Renaming_or_removing_a_localized_component_renames_or_removes_its_resource_keys()
    {
        var (code, resources) = ApplyWithResources("AboutForm", new DesignerEdit { Kind = DesignerEditKinds.RenameComponent, Component = "okButton", NewName = "closeButton" });
        Assert.DoesNotContain("okButton", code.Replace("okButton_Click", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("resources.ApplyResources(this.closeButton, \"closeButton\");", code, StringComparison.Ordinal);
        Assert.Contains("this.closeButton.Click += new System.EventHandler(this.okButton_Click);", code, StringComparison.Ordinal);
        var resx = resources.Get(null)!.Text;
        Assert.DoesNotContain("okButton", resx, StringComparison.Ordinal);
        Assert.Equal(
        [
            "-<data name=\"okButton.Anchor\" type=\"System.Windows.Forms.AnchorStyles, System.Windows.Forms\">",
            "+<data name=\"closeButton.Anchor\" type=\"System.Windows.Forms.AnchorStyles, System.Windows.Forms\">",
            "-<data name=\"okButton.Location\" type=\"System.Drawing.Point, System.Drawing\">",
            "+<data name=\"closeButton.Location\" type=\"System.Drawing.Point, System.Drawing\">",
            "-<data name=\"okButton.Size\" type=\"System.Drawing.Size, System.Drawing\">",
            "+<data name=\"closeButton.Size\" type=\"System.Drawing.Size, System.Drawing\">",
            "-<data name=\"okButton.TabIndex\" type=\"System.Int32, mscorlib\">",
            "+<data name=\"closeButton.TabIndex\" type=\"System.Int32, mscorlib\">",
            "-<data name=\"okButton.Text\" xml:space=\"preserve\">",
            "+<data name=\"closeButton.Text\" xml:space=\"preserve\">",
            "-<data name=\"&gt;&gt;okButton.Name\" xml:space=\"preserve\">",
            "-<value>okButton</value>",
            "+<data name=\"&gt;&gt;closeButton.Name\" xml:space=\"preserve\">",
            "+<value>closeButton</value>",
            "-<data name=\"&gt;&gt;okButton.Type\" xml:space=\"preserve\">",
            "+<data name=\"&gt;&gt;closeButton.Type\" xml:space=\"preserve\">",
            "-<data name=\"&gt;&gt;okButton.Parent\" xml:space=\"preserve\">",
            "+<data name=\"&gt;&gt;closeButton.Parent\" xml:space=\"preserve\">",
            "-<data name=\"&gt;&gt;okButton.ZOrder\" xml:space=\"preserve\">",
            "+<data name=\"&gt;&gt;closeButton.ZOrder\" xml:space=\"preserve\">",
        ], Diff(Resx("AboutForm.resx"), resx));
        Assert.Equal(["-<data name=\"okButton.Text\" xml:space=\"preserve\">", "+<data name=\"closeButton.Text\" xml:space=\"preserve\">"], Diff(Resx("AboutForm.fr.resx"), resources.Get("fr")!.Text));

        // Removed: its keys go from every culture's resources.
        (code, resources) = ApplyWithResources("AboutForm", new DesignerEdit { Kind = DesignerEditKinds.RemoveComponent, Component = "titleLabel" });
        Assert.DoesNotContain("titleLabel", code, StringComparison.Ordinal);
        Assert.DoesNotContain("titleLabel", resources.Get(null)!.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("titleLabel", resources.Get("fr")!.Text, StringComparison.Ordinal);
        Assert.Contains("okButton.Text", resources.Get("fr")!.Text, StringComparison.Ordinal);
        Assert.All(Diff(Resx("AboutForm.resx"), resources.Get(null)!.Text), l => Assert.StartsWith("-", l, StringComparison.Ordinal));
    }

    [Fact]
    public void An_image_imported_into_the_forms_resources_is_get_object_and_its_resx_entry()
    {
        // A non-localizable form: the code reads it with GetObject, the .resx holds the bytes (with its group, before the Image).
        var png = new DesignerResource
        {
            Type = "System.Drawing.Bitmap, System.Drawing",
            MimeType = "application/x-microsoft.net.object.bytearray.base64",
            Text = "\r\n        iVBORw0KGgo=\r\n",
            AssemblyAlias = "System.Drawing",
            AssemblyName = "System.Drawing, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
        };
        var resource = new DesignerValue { Kind = DesignerValueKinds.Resource, Type = "System.Drawing.Image", Value = "logoPictureBox.BackgroundImage" };
        var (code, resources) = ApplyWithResources("OrderForm", Resource("logoPictureBox.BackgroundImage", png), Set("logoPictureBox", "BackgroundImage", resource));
        Assert.Equal(["+logoPictureBox.BackgroundImage = (Image)resources.GetObject(\"logoPictureBox.BackgroundImage\");"], Diff(OrderForm, code));
        Assert.Equal(
        [
            "+<data name=\"logoPictureBox.BackgroundImage\" type=\"System.Drawing.Bitmap, System.Drawing\" mimetype=\"application/x-microsoft.net.object.bytearray.base64\">",
            "+<value>",
            "+iVBORw0KGgo=",
            "+</value>",
            "+</data>",
        ], Diff(Resx("OrderForm.resx"), resources.Get(null)!.Text));
        Assert.Contains("  <assembly alias=\"System.Drawing\" name=\"System.Drawing, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a\" />\n  <data name=\"logoPictureBox.BackgroundImage\"", resources.Get(null)!.Text, StringComparison.Ordinal);
        Assert.Contains("  <data name=\"logoPictureBox.BackgroundImage\" type=\"System.Drawing.Bitmap, System.Drawing\" mimetype=\"application/x-microsoft.net.object.bytearray.base64\">\n    <value>\n        iVBORw0KGgo=\n</value>\n  </data>\n  <data name=\"logoPictureBox.Image\"",
            resources.Get(null)!.Text, StringComparison.Ordinal);

        // A form without resources: the resources variable first (after components), and its .resx created.
        var (main, mainResources) = ApplyWithResources("MainForm", Resource("resetButton.Image", png),
            Set("resetButton", "Image", resource with { Value = "resetButton.Image" }));
        Assert.Contains("    {\n        System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MainForm));\n        greetingLabel = new Label();", main, StringComparison.Ordinal);
        Assert.Contains("        resetButton.Image = (Image)resources.GetObject(\"resetButton.Image\");\n        resetButton.Location", main, StringComparison.Ordinal);
        Assert.Contains("<data name=\"resetButton.Image\"", mainResources.Get(null)!.Text, StringComparison.Ordinal);

        // The image's last use removed: so are its key and the resources variable.
        var (noLogo, logoResources) = ApplyWithResources("OrderForm", new DesignerEdit { Kind = DesignerEditKinds.RemoveComponent, Component = "logoPictureBox" });
        Assert.DoesNotContain("ComponentResourceManager", noLogo, StringComparison.Ordinal);
        Assert.DoesNotContain("logoPictureBox", logoResources.Get(null)!.Text, StringComparison.Ordinal);
        Assert.Contains("orderToolTip.TrayLocation", logoResources.Get(null)!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_image_lists_images_and_tray_locations()
    {
        // An image list's images: its stream from the resources, then a SetKeyName each after its properties.
        var stream = new DesignerValue { Kind = DesignerValueKinds.Resource, Type = "System.Windows.Forms.ImageListStreamer", Value = "iconImageList.ImageStream" };
        var images = Apply(InventoryForm, Set("iconImageList", "ImageStream", stream), Collection("iconImageList", "Images", "SetKeyName", null, DesignerValue.String("new.png"), DesignerValue.String("open.png")));
        Assert.Contains(
            "            iconImageList.ImageSize = new Size(24, 24);\n            iconImageList.ImageStream = (ImageListStreamer)resources.GetObject(\"iconImageList.ImageStream\");\n            iconImageList.TransparentColor = Color.Transparent;\n            iconImageList.Images.SetKeyName(0, \"new.png\");\n            iconImageList.Images.SetKeyName(1, \"open.png\");\n",
            images, StringComparison.Ordinal);
        Assert.Contains(Read(images).Model.Statements, s => s is { Kind: DesignerStatementKinds.Call, Member: "Images.SetKeyName" });

        // The tray's arrangement: TrayLocation metadata, in place or with the component's group.
        var point = new DesignerResource { Type = "System.Drawing.Point, System.Drawing, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", Text = "140, 17" };
        var (_, resources) = ApplyWithResources("InventoryForm", Resource("mainToolStrip.TrayLocation", point, metadata: true));
        Assert.Equal(["-<value>132, 17</value>", "+<value>140, 17</value>"], Diff(Resx("InventoryForm.resx"), resources.Get(null)!.Text));
        Assert.Equal(["mainMenuStrip", "mainToolStrip", "mainStatusStrip", "itemsContextMenuStrip", "iconImageList", "itemBindingSource", "inventoryToolTip", "inventoryHelpProvider", "inventoryErrorProvider"],
            new ResxText(Resx("InventoryForm.resx")).TrayLocations().OrderBy(t => t.Value.Y).ThenBy(t => t.Value.X).Select(t => t.Key));
        Assert.True(new ResxText(Resx("AboutForm.resx")).IsLocalizable);
        Assert.False(new ResxText(Resx("OrderForm.resx")).IsLocalizable);
    }

    // ------------------------------------------------------------------ #67: the control designers' Locked

    private static readonly DesignerResource s_lockedTrue = new() { Type = "System.Boolean, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089", Text = "True" };

    /// <summary>
    /// Lock Controls' Locked is design-time metadata in the default .resx, as the classic IDE writes it: with the
    /// component's entries (metadata first), the form's ($this) last, in the file's line endings, never in the code; a
    /// null resource removes it, and removing or renaming the component takes it along.
    /// </summary>
    [Fact]
    public void Locked_metadata_is_written_where_the_classic_ide_writes_it()
    {
        var (code, resources) = ApplyWithResources("OrderForm", Resource("giftCheckBox.Locked", s_lockedTrue, metadata: true), Resource("$this.Locked", s_lockedTrue, metadata: true));
        Assert.Equal(OrderForm, code);
        var original = Resx("OrderForm.resx");
        var resx = resources.Get(null)!.Text;
        var newLine = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string Element(string name) => string.Join(newLine, $"  <metadata name=\"{name}.Locked\" type=\"System.Boolean, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089\">", "    <value>True</value>", "  </metadata>") + newLine;
        Assert.All(Diff(original, resx), l => Assert.StartsWith("+", l, StringComparison.Ordinal));
        Assert.Equal(6, Diff(original, resx).Count);

        // giftCheckBox's sorts before the other components' (logoPictureBox, orderToolTip); the form's goes last.
        Assert.Contains(Element("giftCheckBox") + "  <metadata name=\"orderToolTip.TrayLocation\"", resx, StringComparison.Ordinal);
        Assert.EndsWith(Element("$this") + "</root>" + newLine, resx, StringComparison.Ordinal);

        // Unlocked: the entries go, and the file is as it was.
        var text = new ResxText(resx);
        text.Set("giftCheckBox.Locked", null, metadata: true);
        text.Set("$this.Locked", null, metadata: true);
        Assert.Equal(original, text.Text);

        // The component removed or renamed: its Locked goes, or follows it.
        text = new ResxText(resx);
        text.RemoveComponent("giftCheckBox");
        Assert.DoesNotContain("giftCheckBox", text.Text, StringComparison.Ordinal);
        Assert.Contains("$this.Locked", text.Text, StringComparison.Ordinal);
        text = new ResxText(resx);
        text.RenameComponent("giftCheckBox", "wrapCheckBox");
        Assert.Contains(Element("wrapCheckBox"), text.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("giftCheckBox", text.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The default .resx the IDE writes before Lock Controls' first metadata (DesignerResx.Empty, held by the designer so
    /// the lock undoes with it) takes the same edits as one the C# host creates itself: the same text, the same code.
    /// </summary>
    [Fact]
    public void A_precreated_default_resx_takes_the_same_edits_as_one_the_host_creates()
    {
        DesignerEdit[] edits = [Resource("greetingLabel.Locked", s_lockedTrue, metadata: true), Resource("$this.Locked", s_lockedTrue, metadata: true)];
        (string Code, string Resx) ApplyTo(string? resx)
        {
            var set = new DesignerResourceSet(Sample("MainForm.cs"), p => p.EndsWith("MainForm.resx", StringComparison.OrdinalIgnoreCase) ? resx : File.Exists(p) ? File.ReadAllText(p) : null);
            var writer = new DesignerWriter(t => DesignerReader.Read(CSharpSyntaxTree.ParseText(t), "System.Windows.Forms.Form", DesignerReader.WindowsFormsImplicitUsings), set);
            return (writer.Apply(SourceText.From(File.ReadAllText(Sample("MainForm.Designer.cs"))), edits).ToString(), set.Get(null)!.Text);
        }

        var created = ApplyTo(null);
        var precreated = ApplyTo(DesignerResx.Empty.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal(created, precreated);
        Assert.Equal(File.ReadAllText(Sample("MainForm.Designer.cs")), created.Code);
        Assert.Contains("<metadata name=\"greetingLabel.Locked\"", created.Resx, StringComparison.Ordinal);
        Assert.Equal(ResxText.Empty, DesignerResx.Empty);
    }
}
