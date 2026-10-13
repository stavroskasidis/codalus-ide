namespace Codalus.Plugins;

/// <summary>
/// An element of a declarative view (D2, API 1.0, M22a). A plugin describes its view as a tree of these and the IDE
/// renders it with its own controls, in its theme, with no plugin UI code. Use the factory methods; <see cref="Kind"/>
/// is one of <c>stack</c>, <c>text</c>, <c>button</c>, <c>textbox</c>, <c>checkbox</c>, <c>dropdown</c>,
/// <c>table</c>, <c>toolbar</c> and <c>separator</c>. Elements the user acts on need an <see cref="Id"/>.
/// </summary>
/// <param name="Kind">What the element is.</param>
public sealed record ViewNode(string Kind)
{
    /// <summary>The element's id within its view: events name it, and input survives updates by it.</summary>
    public string? Id { get; init; }

    /// <summary>A text's text, a button's or a check box's label.</summary>
    public string? Text { get; init; }

    /// <summary>Text: <c>heading</c>, <c>muted</c> or <c>code</c>; button: <c>primary</c>.</summary>
    public string? Style { get; init; }

    /// <summary>A text box's text, or a dropdown's selected option.</summary>
    public string? Value { get; init; }

    /// <summary>A text box's hint while it's empty.</summary>
    public string? Placeholder { get; init; }

    /// <summary>A check box's state.</summary>
    public bool? Checked { get; init; }

    /// <summary>The element is shown but can't be used.</summary>
    public bool Disabled { get; init; }

    /// <summary>A dropdown's options.</summary>
    public IReadOnlyList<string>? Options { get; init; }

    /// <summary>A table's column headers.</summary>
    public IReadOnlyList<string>? Columns { get; init; }

    /// <summary>A table's rows, one string per column.</summary>
    public IReadOnlyList<IReadOnlyList<string>>? Rows { get; init; }

    /// <summary>A stack's or a toolbar's elements.</summary>
    public IReadOnlyList<ViewNode>? Children { get; init; }

    /// <summary>A stack's direction: <c>vertical</c> (default) or <c>horizontal</c>.</summary>
    public string? Orientation { get; init; }

    /// <summary>Elements one under the other.</summary>
    public static ViewNode Stack(params ViewNode[] children) => new("stack") { Children = children };

    /// <summary>Elements side by side.</summary>
    public static ViewNode Row(params ViewNode[] children) => new("stack") { Children = children, Orientation = "horizontal" };

    /// <summary>A heading.</summary>
    public static ViewNode Heading(string text) => new("text") { Text = text, Style = "heading" };

    /// <summary>A text, plain or <c>muted</c> or <c>code</c>.</summary>
    public static ViewNode Label(string text, string? style = null) => new("text") { Text = text, Style = style };

    /// <summary>A button: a <c>click</c> event.</summary>
    public static ViewNode Button(string id, string text, bool primary = false) => new("button") { Id = id, Text = text, Style = primary ? "primary" : null };

    /// <summary>A text box: a <c>change</c> event with its text when the user presses Enter or leaves it.</summary>
    public static ViewNode TextBox(string id, string? value = null, string? placeholder = null) => new("textbox") { Id = id, Value = value, Placeholder = placeholder };

    /// <summary>A check box: a <c>change</c> event with <c>true</c> or <c>false</c>.</summary>
    public static ViewNode CheckBox(string id, string text, bool isChecked) => new("checkbox") { Id = id, Text = text, Checked = isChecked };

    /// <summary>A dropdown: a <c>change</c> event with the chosen option.</summary>
    public static ViewNode Dropdown(string id, IReadOnlyList<string> options, string? selected = null) => new("dropdown") { Id = id, Options = options, Value = selected };

    /// <summary>A table: <c>select</c> and <c>activate</c> events with the row's index.</summary>
    public static ViewNode Table(string id, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows) => new("table") { Id = id, Columns = columns, Rows = rows };

    /// <summary>A row of buttons.</summary>
    public static ViewNode Toolbar(params ViewNode[] buttons) => new("toolbar") { Children = buttons };

    /// <summary>A line between elements.</summary>
    public static ViewNode Separator() => new("separator");
}

/// <summary>
/// What the user did in a declarative view: <c>click</c> (a button), <c>change</c> (a text box's text when confirmed,
/// a check box, a dropdown: <see cref="Value"/>), <c>select</c> (a table row, <see cref="Row"/>) or <c>activate</c>
/// (a table row double-clicked or Enter).
/// </summary>
/// <param name="ElementId">The element's id.</param>
/// <param name="Kind">What happened.</param>
/// <param name="Value">The new value, for a change.</param>
/// <param name="Row">The table row, for a select or an activate.</param>
public sealed record ViewEvent(string ElementId, string Kind, string? Value = null, int? Row = null);

/// <summary>A file opened in a plugin's custom editor.</summary>
/// <param name="ViewId">The declarative view to fill, and the id the editor's calls use.</param>
/// <param name="Path">The file's path.</param>
/// <param name="Text">The file's text when it opened.</param>
public sealed record CustomEditorDocument(string ViewId, string Path, string Text);

/// <summary>
/// A custom editor (API 1.0, M22a): files matching the manifest's <c>customEditors</c> open in a document whose content
/// is a declarative view the plugin sets with <see cref="IWindow.SetView"/>. To change the file, the plugin gives its
/// new text with <see cref="IWindow.SetCustomEditorText"/>; the user saves it.
/// </summary>
public interface ICustomEditorProvider
{
    /// <summary>A file opened: fill its view (<see cref="CustomEditorDocument.ViewId"/>) and handle its events.</summary>
    Task OpenAsync(CustomEditorDocument document, CancellationToken cancellationToken);

    /// <summary>Its document closed.</summary>
    Task CloseAsync(string viewId, CancellationToken cancellationToken) => Task.CompletedTask;
}
