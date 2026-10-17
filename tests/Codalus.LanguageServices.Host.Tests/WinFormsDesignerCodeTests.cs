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
}
