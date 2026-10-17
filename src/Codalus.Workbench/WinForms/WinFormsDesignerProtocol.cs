using System.Text.Json.Serialization;
using Codalus.LanguageServices.Protocol;

namespace Codalus.Workbench.WinForms;

// The Windows Forms designer host's protocol (M41a, ADR 0073; docs/winforms-designer-protocol.md): JSON-RPC over the
// host's stdio, the IDE asking and the host answering. The designer host (net8.0-windows) links this file and
// WinFormsDesign.cs, so it has no other dependency.

/// <summary>The designer host's methods.</summary>
public static class WinFormsDesignerMethods
{
    /// <summary>Creates the form from a model on a fresh design surface: <see cref="DesignerLoadParams"/> → <see cref="DesignerRenderResult"/>.</summary>
    public const string Load = "designer/load";

    /// <summary>Draws the surface again: → <see cref="DesignerRenderResult"/>.</summary>
    public const string Render = "designer/render";

    /// <summary>Creates a component from the Toolbox: <see cref="DesignerCreateParams"/> → <see cref="DesignerChangeResult"/>.</summary>
    public const string Create = "designer/create";

    /// <summary>Sets properties (a move, a resize, the Properties window): <see cref="DesignerSetPropertiesParams"/> → <see cref="DesignerChangeResult"/>.</summary>
    public const string SetProperties = "designer/setProperties";

    /// <summary>Moves a control into another container: <see cref="DesignerSetParentParams"/> → <see cref="DesignerChangeResult"/>.</summary>
    public const string SetParent = "designer/setParent";

    /// <summary>Removes components (and the controls they contain): <see cref="DesignerComponentsParams"/> → <see cref="DesignerChangeResult"/>.</summary>
    public const string Delete = "designer/delete";

    /// <summary>The selected components' properties, merged: <see cref="DesignerComponentsParams"/> → <see cref="DesignerPropertiesResult"/>.</summary>
    public const string Properties = "designer/properties";

    /// <summary>A component's events: <see cref="DesignerComponentsParams"/> → <see cref="DesignerEventsResult"/>.</summary>
    public const string Events = "designer/events";

    /// <summary>The IDE's selection, for designers that follow it (a tab page shows when selected): <see cref="DesignerComponentsParams"/>.</summary>
    public const string Select = "designer/select";

    /// <summary>Moves a control in its parent's z-order (M41b): <see cref="DesignerChildIndexParams"/> → <see cref="DesignerChangeResult"/>.</summary>
    public const string SetChildIndex = "designer/setChildIndex";

    /// <summary>A collection's items (M41b): <see cref="DesignerCollectionParams"/> → <see cref="DesignerCollectionResult"/>.</summary>
    public const string Collection = "designer/collection";

    /// <summary>Replaces a collection's items (M41b; the collection editors, a menu's Type Here): <see cref="DesignerSetCollectionParams"/> → <see cref="DesignerChangeResult"/>.</summary>
    public const string SetCollection = "designer/setCollection";

    /// <summary>A component's designer actions, its smart tag (M41b): <see cref="DesignerComponentsParams"/> → <see cref="DesignerActionsResult"/>.</summary>
    public const string Actions = "designer/actions";

    /// <summary>Runs a designer action or sets an action's property (M41b): <see cref="DesignerInvokeActionParams"/> → <see cref="DesignerChangeResult"/>.</summary>
    public const string InvokeAction = "designer/invokeAction";
}

/// <summary>
/// What to load: the C# host's model, the project's build output (its types, and its compiled resources for
/// <c>resources</c>), the class's full name, and the folders to find references in.
/// </summary>
public sealed record DesignerLoadParams(DesignerModel Model, string ProjectAssembly, string? ClassFullName, string[] SearchFolders)
{
    /// <summary>
    /// The form's <c>.resx</c> files as the editor has them (M41b; the default one with an empty culture), read instead of
    /// the build's compiled resources, so what the designer wrote shows without a build.
    /// </summary>
    public DesignerResourceText[] Resources { get; init; } = [];

    /// <summary>The language the form is designed in (a localizable form's <c>Language</c>): null or empty for the default.</summary>
    public string? Culture { get; init; }

    /// <summary>The form is localizable: its localizable properties are written to the <c>.resx</c> of <see cref="Culture"/>.</summary>
    public bool Localizable { get; init; }
}

/// <summary>A <c>.resx</c> file's text and its culture (empty: the default resources).</summary>
public sealed record DesignerResourceText(string Culture, string Text);

/// <summary>A drawn surface: a PNG (base64) of the root as it shows at design time, in pixels (96 DPI, 1 pixel per unit of the code).</summary>
public sealed record DesignerFrame(string Png, int Width, int Height);

/// <summary>
/// A component on the surface: its bounds in the frame (<see cref="X"/>…), its parent control's name (null for the
/// root; <c>splitContainer1.Panel1</c> for a SplitContainer's panel), the origin of its client area in the frame (where
/// its children's locations count from), and what it is.
/// </summary>
public sealed record DesignerComponentView(string Name, string Type, string? Parent, double X, double Y, double Width, double Height)
{
    public bool IsControl { get; init; }

    /// <summary>It takes child controls (a panel, a group box, a form…).</summary>
    public bool IsContainer { get; init; }

    public double ClientX { get; init; }

    public double ClientY { get; init; }

    public bool Visible { get; init; } = true;

    /// <summary>The event Visual Studio hooks on a double-click (<c>[DefaultEvent]</c>).</summary>
    public string? DefaultEvent { get; init; }

    /// <summary>Not a component of the class: a sub-control the designer exposes (a SplitContainer's panel).</summary>
    public bool IsPart { get; init; }

    /// <summary>The control's tab index (tab order mode, M41b).</summary>
    public int? TabIndex { get; init; }

    /// <summary>Its <c>Dock</c> and <c>Anchor</c> as their converters write them (the surface's adorners).</summary>
    public string? Dock { get; init; }

    public string? Anchor { get; init; }

    /// <summary>Its margin and (a container's) padding, left, top, right, bottom, for the snap lines; a form's empty padding is the designer's 9.</summary>
    public int[]? Margin { get; init; }

    public int[]? Padding { get; init; }

    /// <summary>Its text's baseline from its top (a label's, a text box's, a button's), for the baseline snap line.</summary>
    public double? Baseline { get; init; }

    /// <summary>Its designer has actions: the surface shows its smart tag glyph.</summary>
    public bool HasActions { get; init; }

    /// <summary>The collections it has editors for (<see cref="DesignerCollectionInfo.Key"/>).</summary>
    public string[]? Collections { get; init; }

    /// <summary>A tool strip's item (M41b): not in the component tray; <see cref="Owner"/> is its strip or menu item, <see cref="ItemIndex"/> its position.</summary>
    public bool IsItem { get; init; }

    public string? Owner { get; init; }

    public int ItemIndex { get; init; }

    public string? Text { get; init; }

    /// <summary>An item that has drop-down items (a menu item, a drop-down button).</summary>
    public bool HasDropDown { get; init; }
}

/// <summary>The surface after a load or a change: its frame, its components (in z-order, front first, parents before children), and what went wrong.</summary>
public sealed record DesignerRenderResult(DesignerFrame? Frame, DesignerComponentView[] Components, string[] Errors);

/// <summary>A component to create from the Toolbox: its type, name, parent (null: the root), where it was dropped (in the frame) and the size it was drawn at, if any.</summary>
public sealed record DesignerCreateParams(string Type, string Name, string? Parent, double X, double Y)
{
    public double? Width { get; init; }

    public double? Height { get; init; }
}

/// <summary>A new component as the code must say it: what the C# host's <c>addComponent</c> edit takes.</summary>
public sealed record DesignerCreated(string Name, string Type, DesignerValue[] CreationArguments, string? Parent, string? ParentPath, int[]? Cell, DesignerPropertyValue[] Properties)
{
    public bool SupportsInitialize { get; init; }

    public bool IsControl { get; init; }

    /// <summary>Its position in its parent's controls (0, the front, unless it's a tab page added at the end).</summary>
    public int Index { get; init; }
}

/// <summary>
/// A property that changed: its new value as code, or null when it's back to its default (the assignment goes). An
/// extender property has its <see cref="Provider"/> (<c>toolTip1.SetToolTip</c>); a data binding (M41b) is
/// <see cref="DataBinding"/>, its value the <c>Binding</c>.
/// </summary>
public sealed record DesignerPropertyChange(string Component, string Property, DesignerValue? Value)
{
    public string? Provider { get; init; }

    public bool DataBinding { get; init; }
}

/// <summary>A control's new parent, as the code must say it.</summary>
public sealed record DesignerReparented(string Component, string? Parent, string? ParentPath, int[]? Cell);

/// <summary>A parent's controls in their new order, front first (<see cref="Parent"/> as <see cref="DesignerSetParentParams.Parent"/> names it; empty for the root).</summary>
public sealed record DesignerChildOrder(string Parent, string[] Children);

/// <summary>A collection as the code must write it (M41b): its statement's method, element type and items, and the locals its items are.</summary>
public sealed record DesignerCollectionChange(string Component, string Property, string Method, string? ElementType, DesignerValue[] Items)
{
    public DesignerLocal[] Locals { get; init; } = [];
}

/// <summary>A local object a collection holds (<c>ListViewItem listViewItem1 = new ListViewItem("Hammer")</c>).</summary>
public sealed record DesignerLocal(string Name, DesignerValue Value);

/// <summary>A component whose properties come from the resources (<c>resources.ApplyResources</c>), or no longer do.</summary>
public sealed record DesignerLocalized(string Component, bool Localized);

/// <summary>
/// What a change did: the properties that changed (as code), the component created, the components removed, the new
/// parent, the surface after it, and <see cref="Error"/> when it couldn't be done.
/// </summary>
public sealed record DesignerChangeResult(DesignerPropertyChange[] Changes, DesignerRenderResult Render)
{
    public DesignerCreated? Created { get; init; }

    public string[] Removed { get; init; } = [];

    public DesignerReparented? Reparented { get; init; }

    public string? Error { get; init; }

    /// <summary>Components something else created (a smart tag's Add Tab, a collection's new items; M41b), in creation order.</summary>
    public DesignerCreated[] Added { get; init; } = [];

    /// <summary>Controls moved to another parent besides <see cref="Reparented"/> (Embed in ToolStripContainer).</summary>
    public DesignerReparented[] Moved { get; init; } = [];

    /// <summary>Parents whose controls' order changed (Bring to Front, Send to Back, a tab page moved).</summary>
    public DesignerChildOrder[] Reordered { get; init; } = [];

    public DesignerCollectionChange[] Collections { get; init; } = [];

    /// <summary>Keys of the form's <c>.resx</c> files to set or remove: a localizable form's properties, images, records of its controls.</summary>
    public DesignerResourceEntry[] Resources { get; init; } = [];

    /// <summary>Components that start or stop getting their properties from the resources.</summary>
    public DesignerLocalized[] Localized { get; init; } = [];
}

/// <summary>A property to set: from the Properties window's text (converted as the property's type converter does) or a value, or reset to its default.</summary>
public sealed record DesignerPropertyEdit(string Component, string Property)
{
    public string? Text { get; init; }

    public DesignerValue? Value { get; init; }

    public bool Reset { get; init; }

    /// <summary>An extender property's provider (<c>toolTip1</c> for <c>ToolTip on toolTip1</c>; M41b).</summary>
    public string? Provider { get; init; }

    /// <summary>The property's data binding (M41b): <see cref="Text"/> is <c>bindingSource1 - Name</c>, or <c>(none)</c>.</summary>
    public bool DataBinding { get; init; }

    /// <summary>An image file's bytes (base64) for an image property (M41b): <c>Image</c>, <c>BackgroundImage</c>, <c>Icon</c>.</summary>
    public string? Bytes { get; init; }
}

public sealed record DesignerSetPropertiesParams(DesignerPropertyEdit[] Edits);

/// <summary>A control moved into <see cref="Parent"/> (null: the root) at a point of the frame.</summary>
public sealed record DesignerSetParentParams(string Component, string? Parent, double X, double Y);

public sealed record DesignerComponentsParams(string[] Components);

/// <summary>A control's new position in its parent's controls (0: the front).</summary>
public sealed record DesignerChildIndexParams(string Component, int Index);

/// <summary>A component's collection (<see cref="DesignerCollectionInfo.Property"/>).</summary>
public sealed record DesignerCollectionParams(string Component, string Property);

/// <summary>
/// An item of a collection: a component item by <see cref="Name"/> (a new one with its <see cref="Type"/>, and
/// <see cref="Text"/> for its <c>Text</c>), a value item by <see cref="Value"/> (a string, a <c>ColumnStyle</c>), a
/// local by <see cref="Name"/> and <see cref="Text"/> (a <c>ListViewItem</c>), an image by <see cref="Name"/> (its key)
/// and, when new, its <see cref="Bytes"/>. <see cref="Display"/> is what the editor lists.
/// </summary>
public sealed record DesignerCollectionItem
{
    public string? Name { get; init; }

    public string? Type { get; init; }

    public DesignerValue? Value { get; init; }

    public string? Text { get; init; }

    public string? Bytes { get; init; }

    public string? Display { get; init; }
}

public sealed record DesignerCollectionResult(string Key, DesignerCollectionItem[] Items);

public sealed record DesignerSetCollectionParams(string Component, string Property, DesignerCollectionItem[] Items);

/// <summary>The kinds of <see cref="DesignerActionItem"/>.</summary>
public static class DesignerActionKinds
{
    public const string Header = "header";
    public const string Text = "text";
    public const string Property = "property";
    public const string Method = "method";
}

/// <summary>
/// An item of a smart tag's panel (a <c>DesignerActionList</c>'s): a header, a text, a property of the action list
/// (with its value and editor) or a method. A method that opens one of Windows Forms' dialogs opens the IDE's own
/// editor instead: <see cref="Collection"/> (a collection editor) or <see cref="ImageProperty"/> (an image import);
/// one with no such editor is <see cref="Unavailable"/>.
/// </summary>
public sealed record DesignerActionItem(string Kind, string Member, string DisplayName)
{
    public string? Category { get; init; }

    public string? Description { get; init; }

    public string? Value { get; init; }

    public string Editor { get; init; } = DesignerEditors.Text;

    public string[]? StandardValues { get; init; }

    public string? Collection { get; init; }

    public string? ImageProperty { get; init; }

    public bool Unavailable { get; init; }
}

public sealed record DesignerActionsResult(DesignerActionItem[] Items);

/// <summary>A smart tag's method to run, or its property to set to <see cref="Text"/> (through the property's converter).</summary>
public sealed record DesignerInvokeActionParams(string Component, string Member)
{
    public string? Text { get; init; }
}

/// <summary>The kinds of editor a property gets in the Properties window.</summary>
public static class DesignerEditors
{
    public const string Text = "text";
    public const string Boolean = "bool";
    public const string Enum = "enum";
    public const string Flags = "flags";
    public const string Color = "color";
    public const string Font = "font";
    public const string Anchor = "anchor";
    public const string Dock = "dock";
    public const string ReadOnly = "readonly";

    /// <summary>An image (M41b): imported from a file into the resources.</summary>
    public const string Image = "image";

    /// <summary>A collection (M41b): its editor (<see cref="DesignerPropertyInfo.Collection"/>).</summary>
    public const string Collection = "collection";
}

/// <summary>
/// A property of the selected components (merged: <see cref="Mixed"/> when their values differ): its value as its type
/// converter writes it, whether the code sets it (<see cref="IsSet"/>, shown bold), its editor and its choices.
/// </summary>
public sealed record DesignerPropertyInfo(string Name, string Category, string Type, string? Value)
{
    public string? Description { get; init; }

    public bool IsReadOnly { get; init; }

    public bool IsSet { get; init; }

    public string Editor { get; init; } = DesignerEditors.Text;

    public string[]? StandardValues { get; init; }

    public bool Mixed { get; init; }

    /// <summary>An extender property's provider (M41b): shown as <c>ToolTip on toolTip1</c>.</summary>
    public string? Provider { get; init; }

    /// <summary>A data binding of the property (M41b), in the <c>(DataBindings)</c> category: its choices are the form's binding sources' members.</summary>
    public bool DataBinding { get; init; }

    /// <summary>The value comes from the resources of a localizable form (M41b).</summary>
    public bool Localizable { get; init; }

    /// <summary>A collection's editor (<see cref="DesignerCollectionInfo.Key"/>).</summary>
    public string? Collection { get; init; }
}

public sealed record DesignerPropertiesResult(DesignerPropertyInfo[] Properties);

/// <summary>An event of a component: its delegate type and the handler's parameters (<c>System.Object sender</c>).</summary>
public sealed record DesignerEventInfo(string Name, string Category, string DelegateType, string[] Parameters)
{
    public string? Description { get; init; }
}

public sealed record DesignerEventsResult(DesignerEventInfo[] Events, string? DefaultEvent);

/// <summary>Source-generated serialization (the shell is Native AOT; the designer host links this file too).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DesignerLoadParams))]
[JsonSerializable(typeof(DesignerRenderResult))]
[JsonSerializable(typeof(DesignerCreateParams))]
[JsonSerializable(typeof(DesignerChangeResult))]
[JsonSerializable(typeof(DesignerSetPropertiesParams))]
[JsonSerializable(typeof(DesignerSetParentParams))]
[JsonSerializable(typeof(DesignerComponentsParams))]
[JsonSerializable(typeof(DesignerPropertiesResult))]
[JsonSerializable(typeof(DesignerEventsResult))]
[JsonSerializable(typeof(DesignerChildIndexParams))]
[JsonSerializable(typeof(DesignerCollectionParams))]
[JsonSerializable(typeof(DesignerCollectionResult))]
[JsonSerializable(typeof(DesignerSetCollectionParams))]
[JsonSerializable(typeof(DesignerActionsResult))]
[JsonSerializable(typeof(DesignerInvokeActionParams))]
[JsonSerializable(typeof(System.Text.Json.JsonElement))]
public sealed partial class WinFormsDesignerJsonContext : JsonSerializerContext;
