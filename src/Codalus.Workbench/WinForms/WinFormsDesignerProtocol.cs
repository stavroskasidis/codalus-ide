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
}

/// <summary>
/// What to load: the C# host's model, the project's build output (its types, and its compiled resources for
/// <c>resources</c>), the class's full name, and the folders to find references in.
/// </summary>
public sealed record DesignerLoadParams(DesignerModel Model, string ProjectAssembly, string? ClassFullName, string[] SearchFolders);

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
}

/// <summary>A property that changed: its new value as code, or null when it's back to its default (the assignment goes).</summary>
public sealed record DesignerPropertyChange(string Component, string Property, DesignerValue? Value);

/// <summary>A control's new parent, as the code must say it.</summary>
public sealed record DesignerReparented(string Component, string? Parent, string? ParentPath, int[]? Cell);

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
}

/// <summary>A property to set: from the Properties window's text (converted as the property's type converter does) or a value, or reset to its default.</summary>
public sealed record DesignerPropertyEdit(string Component, string Property)
{
    public string? Text { get; init; }

    public DesignerValue? Value { get; init; }

    public bool Reset { get; init; }
}

public sealed record DesignerSetPropertiesParams(DesignerPropertyEdit[] Edits);

/// <summary>A control moved into <see cref="Parent"/> (null: the root) at a point of the frame.</summary>
public sealed record DesignerSetParentParams(string Component, string? Parent, double X, double Y);

public sealed record DesignerComponentsParams(string[] Components);

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
[JsonSerializable(typeof(System.Text.Json.JsonElement))]
public sealed partial class WinFormsDesignerJsonContext : JsonSerializerContext;
