namespace Codalus.LanguageServices.Protocol;

// The Windows Forms designer's model (M41a and M41b, ADR 0073): what the C# host reads from InitializeComponent and writes back,
// and what the designer host evaluates. It is CodeDOM-like: the components the class declares, and InitializeComponent's
// statements in their order, with values as expression trees. This file has no other dependency, so the designer host
// (net8.0-windows) links it.

/// <summary>The kinds of <see cref="DesignerValue"/>.</summary>
public static class DesignerValueKinds
{
    /// <summary>A literal: <see cref="DesignerValue.Type"/> is <c>string</c>, <c>bool</c>, <c>char</c>, <c>int</c>, <c>long</c>, <c>float</c>, <c>double</c>, <c>decimal</c>, <c>byte</c>, <c>short</c> or <c>null</c>; <see cref="DesignerValue.Value"/> its invariant text.</summary>
    public const string Literal = "literal";

    /// <summary>An enum value: its type, and <see cref="DesignerValue.Members"/> (ored together for flags), or its number in <see cref="DesignerValue.Value"/>.</summary>
    public const string Enum = "enum";

    /// <summary>A static field or property: <c>Color.Red</c>, <c>SystemColors.Control</c>, <c>Properties.Resources.Logo</c>; <see cref="DesignerValue.Value"/> is the member.</summary>
    public const string Member = "member";

    /// <summary><c>new Type(arguments)</c>.</summary>
    public const string New = "new";

    /// <summary>A static method's result: <c>Color.FromArgb(255, 128, 0)</c>; <see cref="DesignerValue.Value"/> is the method.</summary>
    public const string Call = "call";

    /// <summary><c>new Type[] { items }</c>: <see cref="DesignerValue.Type"/> is the element type, <see cref="DesignerValue.Arguments"/> the items.</summary>
    public const string Array = "array";

    /// <summary><c>resources.GetObject("key")</c> (with its cast's type in <see cref="DesignerValue.Type"/>).</summary>
    public const string Resource = "resource";

    /// <summary>A component or local of the class (<see cref="DesignerValue.Value"/> is its name; empty for the root, <c>this</c>).</summary>
    public const string Reference = "reference";

    /// <summary><c>typeof(Type)</c>.</summary>
    public const string TypeOf = "typeof";
}

/// <summary>
/// A value in InitializeComponent, as an expression tree. <see cref="Type"/> names types by their full C# name
/// (<c>System.Drawing.Point</c>, <c>Outer.Inner</c>); the writer shortens them to the file's style.
/// </summary>
public sealed record DesignerValue
{
    public required string Kind { get; init; }

    public string? Type { get; init; }

    public string? Value { get; init; }

    public string[]? Members { get; init; }

    public DesignerValue[]? Arguments { get; init; }

    public static DesignerValue String(string value) => new() { Kind = DesignerValueKinds.Literal, Type = "string", Value = value };

    public static DesignerValue Bool(bool value) => new() { Kind = DesignerValueKinds.Literal, Type = "bool", Value = value ? "true" : "false" };

    public static DesignerValue Int(int value) => new() { Kind = DesignerValueKinds.Literal, Type = "int", Value = value.ToString(System.Globalization.CultureInfo.InvariantCulture) };

    public static DesignerValue Float(float value) => new() { Kind = DesignerValueKinds.Literal, Type = "float", Value = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) };

    public static DesignerValue Null { get; } = new() { Kind = DesignerValueKinds.Literal, Type = "null" };

    public static DesignerValue EnumOf(string type, params string[] members) => new() { Kind = DesignerValueKinds.Enum, Type = type, Members = members };

    public static DesignerValue MemberOf(string type, string member) => new() { Kind = DesignerValueKinds.Member, Type = type, Value = member };

    public static DesignerValue NewOf(string type, params DesignerValue[] arguments) => new() { Kind = DesignerValueKinds.New, Type = type, Arguments = arguments };

    public static DesignerValue CallOf(string type, string method, params DesignerValue[] arguments) => new() { Kind = DesignerValueKinds.Call, Type = type, Value = method, Arguments = arguments };

    public static DesignerValue ArrayOf(string elementType, params DesignerValue[] items) => new() { Kind = DesignerValueKinds.Array, Type = elementType, Arguments = items };

    public static DesignerValue ReferenceTo(string name) => new() { Kind = DesignerValueKinds.Reference, Value = name };

    /// <summary>Structural equality (records compare arrays by reference).</summary>
    public bool SameAs(DesignerValue? other) =>
        other is not null && Kind == other.Kind && Type == other.Type && Value == other.Value
        && (Members ?? []).SequenceEqual(other.Members ?? [])
        && (Arguments ?? []).Length == (other.Arguments ?? []).Length
        && (Arguments ?? []).Zip(other.Arguments ?? []).All(p => p.First.SameAs(p.Second));
}

/// <summary>A property and its value (a new component's).</summary>
public sealed record DesignerPropertyValue(string Name, DesignerValue Value);

/// <summary>
/// A value as a <c>.resx</c> file stores it (M41b): <see cref="Type"/> as the file names it (<c>System.Drawing.Point,
/// System.Drawing</c>; null for a string), <see cref="Text"/> the <c>value</c> element's text (base64 when there's a
/// <see cref="MimeType"/>, as the resource writer wraps it), and the <c>assembly</c> alias the type uses, which the file
/// declares once before its first use.
/// </summary>
public sealed record DesignerResource
{
    public string? Type { get; init; }

    public string Text { get; init; } = string.Empty;

    public string? MimeType { get; init; }

    public string? AssemblyAlias { get; init; }

    public string? AssemblyName { get; init; }

    public static DesignerResource String(string text) => new() { Text = text };

    /// <summary>Structural equality.</summary>
    public bool SameAs(DesignerResource? other) =>
        other is not null && Type == other.Type && Text == other.Text && MimeType == other.MimeType;
}

/// <summary>
/// A key of a form's <c>.resx</c> to set or remove (M41b): a localizable property (<c>okButton.Text</c>, <c>$this.Text</c>),
/// an image (<c>pictureBox1.Image</c>), Visual Studio's records of a localizable form's controls (<c>&gt;&gt;okButton.Parent</c>),
/// or design-time metadata (<c>timer1.TrayLocation</c>, <c>$this.Localizable</c>). <see cref="Culture"/> is empty for
/// the default resources (<c>Form1.resx</c>), else the file's culture (<c>fr</c>: <c>Form1.fr.resx</c>);
/// <see cref="Resource"/> null removes the key.
/// </summary>
public sealed record DesignerResourceEntry(string Key, DesignerResource? Resource)
{
    public string Culture { get; init; } = string.Empty;

    /// <summary>A <c>metadata</c> element (design-time only) rather than <c>data</c>.</summary>
    public bool Metadata { get; init; }
}

/// <summary>
/// The collections the designer edits (M41b): the property that holds them, how Visual Studio's serializer writes them,
/// and what can be added. <see cref="Kind"/> is <c>values</c> (strings and styles written inline), <c>components</c>
/// (items that are components of the class, with sections of their own), <c>locals</c> (items created as locals:
/// <c>ListViewItem listViewItem1 = new ListViewItem("…")</c>) or <c>images</c> (an <c>ImageList</c>'s, in the resources).
/// </summary>
public sealed record DesignerCollectionInfo(string OwnerType, string Property, string Kind, string Method, string? ElementType, string[] ItemTypes)
{
    public string Key => OwnerType + ":" + Property;
}

/// <summary>A form's resource file as the designer starts one (M41b, #67).</summary>
public static class DesignerResx
{
    /// <summary>
    /// An empty resource file, as the classic IDE's designer starts a form's or a culture's: the C# host creates a missing
    /// one from it, and the IDE writes it before an edit of a form that has no default .resx (Lock Controls' metadata), so
    /// the edit goes to a file the designer holds and undoes with it.
    /// </summary>
    public const string Empty = """
        <?xml version="1.0" encoding="utf-8"?>
        <root>
          <resheader name="resmimetype">
            <value>text/microsoft-resx</value>
          </resheader>
          <resheader name="version">
            <value>2.0</value>
          </resheader>
          <resheader name="reader">
            <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
          </resheader>
          <resheader name="writer">
            <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
          </resheader>
        </root>

        """;

    /// <summary>
    /// A resource file's design-time metadata (#67: <c>name.Locked</c>), read as XML: each <c>metadata</c> element's value
    /// by its name, a bool for a <c>System.Boolean</c> one, else its text. No <c>data</c> element is read: a
    /// <c>ResXResourceReader</c> deserializes every data value to list the metadata, and one it can't (a binary-formatted
    /// <c>TreeNode</c> on .NET 9 and later, a type it can't find) would fail the form's load. Throws
    /// <see cref="System.Xml.XmlException"/> for a text that isn't XML.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> Metadata(string text)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var element in System.Xml.Linq.XDocument.Parse(text).Root?.Elements("metadata") ?? [])
        {
            if ((string?)element.Attribute("name") is not { Length: > 0 } name)
            {
                continue;
            }

            var value = element.Element("value")?.Value;
            var type = ((string?)element.Attribute("type"))?.Split(',')[0].Trim();
            metadata[name] = type == "System.Boolean" && bool.TryParse(value?.Trim(), out var flag) ? flag : value;
        }

        return metadata;
    }
}

/// <summary>The collections Visual Studio's designer has editors for, by the type that declares them (M41b).</summary>
public static class DesignerCollections
{
    public const string Values = "values";
    public const string Components = "components";
    public const string Locals = "locals";
    public const string Images = "images";

    /// <summary>A new menu item's type: what typed text in a menu's Type Here makes.</summary>
    public const string MenuItemType = Forms + "ToolStripMenuItem";

    private const string Forms = "System.Windows.Forms.";

    private static readonly string[] s_menuItems = [Forms + "ToolStripMenuItem", Forms + "ToolStripComboBox", Forms + "ToolStripTextBox", Forms + "ToolStripSeparator"];

    private static readonly string[] s_toolItems =
    [
        Forms + "ToolStripButton", Forms + "ToolStripLabel", Forms + "ToolStripSplitButton", Forms + "ToolStripDropDownButton", Forms + "ToolStripSeparator",
        Forms + "ToolStripComboBox", Forms + "ToolStripTextBox", Forms + "ToolStripProgressBar",
    ];

    private static readonly string[] s_statusItems = [Forms + "ToolStripStatusLabel", Forms + "ToolStripProgressBar", Forms + "ToolStripDropDownButton", Forms + "ToolStripSplitButton"];

    private static readonly string[] s_columns =
    [
        Forms + "DataGridViewTextBoxColumn", Forms + "DataGridViewCheckBoxColumn", Forms + "DataGridViewComboBoxColumn", Forms + "DataGridViewButtonColumn",
        Forms + "DataGridViewImageColumn", Forms + "DataGridViewLinkColumn",
    ];

    /// <summary>Every collection, most derived owners first (a <c>MenuStrip</c>'s items before a <c>ToolStrip</c>'s).</summary>
    public static IReadOnlyList<DesignerCollectionInfo> All { get; } =
    [
        new(Forms + "ListBox", "Items", Values, "AddRange", "System.Object", ["System.String"]),
        new(Forms + "ComboBox", "Items", Values, "AddRange", "System.Object", ["System.String"]),
        new(Forms + "DomainUpDown", "Items", Values, "AddRange", "System.Object", ["System.String"]),
        new(Forms + "ListView", "Columns", Components, "AddRange", Forms + "ColumnHeader", [Forms + "ColumnHeader"]),
        new(Forms + "ListView", "Items", Locals, "AddRange", Forms + "ListViewItem", [Forms + "ListViewItem"]),
        new(Forms + "TableLayoutPanel", "ColumnStyles", Values, "Add", null, [Forms + "ColumnStyle"]),
        new(Forms + "TableLayoutPanel", "RowStyles", Values, "Add", null, [Forms + "RowStyle"]),
        new(Forms + "TabControl", "TabPages", Components, "Controls.Add", null, [Forms + "TabPage"]),
        new(Forms + "MenuStrip", "Items", Components, "AddRange", Forms + "ToolStripItem", s_menuItems),
        new(Forms + "ContextMenuStrip", "Items", Components, "AddRange", Forms + "ToolStripItem", s_menuItems),
        new(Forms + "StatusStrip", "Items", Components, "AddRange", Forms + "ToolStripItem", s_statusItems),
        new(Forms + "ToolStrip", "Items", Components, "AddRange", Forms + "ToolStripItem", s_toolItems),
        new(Forms + "ToolStripDropDownItem", "DropDownItems", Components, "AddRange", Forms + "ToolStripItem", s_menuItems),
        new(Forms + "DataGridView", "Columns", Components, "AddRange", Forms + "DataGridViewColumn", s_columns),
        new(Forms + "ImageList", "Images", Images, "SetKeyName", null, ["System.Drawing.Image"]),
    ];

    public static DesignerCollectionInfo? Find(string key) => All.FirstOrDefault(c => c.Key == key);

    /// <summary>
    /// Whether a collection key is a tool strip's items (#67): a <c>MenuStrip</c>'s, <c>ToolStrip</c>'s,
    /// <c>StatusStrip</c>'s or <c>ContextMenuStrip</c>'s, or a strip derived from them such as a <c>BindingNavigator</c>
    /// (the host names a derived strip by the type that declares the collection). A list's items (a <c>ListBox</c>'s,
    /// <c>ComboBox</c>'s, <c>DomainUpDown</c>'s or <c>ListView</c>'s) are not: the surface's Type Here is a strip's alone.
    /// </summary>
    public static bool IsStripItems(string key) => Find(key) is { Property: "Items", Kind: Components, ElementType: Forms + "ToolStripItem" };

    /// <summary>A strip's or a menu item's items property (<c>Items</c>, <c>DropDownItems</c>), from a collection key.</summary>
    public static string PropertyOf(string key) => key[(key.IndexOf(':', StringComparison.Ordinal) + 1)..];
}

/// <summary>The kinds of <see cref="DesignerStatement"/>.</summary>
public static class DesignerStatementKinds
{
    /// <summary><c>name = new Type(...)</c> (a field) or <c>Type name = new Type(...)</c> (a local): <see cref="DesignerStatement.Value"/> is the creation.</summary>
    public const string Create = "create";

    /// <summary><c>target.Member = value</c>; <see cref="DesignerStatement.Member"/> may be a path (<c>FlatAppearance.BorderSize</c>).</summary>
    public const string Assign = "assign";

    /// <summary><c>target.Member(arguments)</c>: <c>Controls.Add</c>, <c>Panel1.Controls.Add</c>, <c>Items.AddRange</c>, <c>SetToolTip</c>…</summary>
    public const string Call = "call";

    /// <summary><c>SuspendLayout</c>, <c>ResumeLayout</c>, <c>PerformLayout</c>, and <c>ISupportInitialize</c>'s <c>BeginInit</c> and <c>EndInit</c>.</summary>
    public const string Layout = "layout";

    /// <summary><c>target.Event += handler</c>: <see cref="DesignerStatement.Member"/> is the event, <see cref="DesignerStatement.Handler"/> the method.</summary>
    public const string Event = "event";

    /// <summary><c>resources.ApplyResources(target, "key")</c>: the key is <see cref="DesignerStatement.Value"/>.</summary>
    public const string ApplyResources = "applyResources";

    /// <summary>Visual Studio's section comment (<c>// </c>, <c>// name</c>, <c>// </c>): <see cref="DesignerStatement.Target"/> is the name.</summary>
    public const string Section = "section";
}

/// <summary>
/// A statement of InitializeComponent. <see cref="Target"/> is a component's or local's name, empty for the root
/// (<c>this</c>), or <c>components</c>. <see cref="Line"/> is where it starts in the designer file (zero-based).
/// </summary>
public sealed record DesignerStatement
{
    public required string Kind { get; init; }

    public string Target { get; init; } = string.Empty;

    public string? Member { get; init; }

    public DesignerValue? Value { get; init; }

    public DesignerValue[]? Arguments { get; init; }

    public string? Handler { get; init; }

    public int Line { get; init; }
}

/// <summary>
/// A component the class declares (a field created in InitializeComponent), a local object (<see cref="IsLocal"/>:
/// a <c>ListViewItem</c>, a <c>DataGridViewCellStyle</c>) or the root itself (<see cref="IsRoot"/>: its name is the
/// class's, its type the base type, which is what the designer creates).
/// </summary>
public sealed record DesignerComponent(string Name, string Type)
{
    public bool IsRoot { get; init; }

    public bool IsLocal { get; init; }

    /// <summary>The field's accessibility as written (<c>private</c>, <c>internal</c>…), or null.</summary>
    public string? Modifier { get; init; }
}

/// <summary>
/// A form's or user control's InitializeComponent: the class, the root's base type, the namespaces the file sees, its
/// components (the root first) and statements, and the code style it is written in.
/// </summary>
public sealed record DesignerModel
{
    public required string ClassName { get; init; }

    public string? Namespace { get; init; }

    /// <summary>The root's type: the class's base type (<c>System.Windows.Forms.Form</c>).</summary>
    public required string BaseType { get; init; }

    /// <summary>The namespaces the file imports (its usings and the project's global usings).</summary>
    public string[] Imports { get; init; } = [];

    public DesignerComponent[] Components { get; init; } = [];

    public DesignerStatement[] Statements { get; init; } = [];

    /// <summary>InitializeComponent uses a <c>ComponentResourceManager</c> named <c>resources</c> over the class's resources.</summary>
    public bool UsesResources { get; init; }

    /// <summary>Statements say <c>this.</c> (Visual Studio for .NET Framework) rather than nothing (Visual Studio for .NET).</summary>
    public bool UsesThis { get; init; }

    /// <summary>Types are written with their namespaces (<c>System.Windows.Forms.Button</c>), as Visual Studio for .NET Framework writes them.</summary>
    public bool QualifiedNames { get; init; }
}
