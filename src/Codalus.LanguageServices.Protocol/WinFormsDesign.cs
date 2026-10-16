namespace Codalus.LanguageServices.Protocol;

// The Windows Forms designer's model (M41a, ADR 0073): what the C# host reads from InitializeComponent and writes back,
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
