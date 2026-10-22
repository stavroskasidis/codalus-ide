namespace Codalus.LanguageServices.Protocol;

// Codalus extensions to LSP (docs/protocol.md). LSP has no project model: the shell sends the language host what the
// build host's design-time builds produced (ADR 0005).

/// <summary>
/// One project as the compiler sees it. <see cref="Arguments"/> is the compiler command line from the design-time
/// build (sources relative to the project directory). A reference to another project's <see cref="OutputPath"/> or
/// <see cref="OutputRefPath"/>, or to the assembly of a project in <see cref="ProjectReferences"/> (built for another
/// target framework than the one loaded), becomes a project reference.
/// </summary>
public sealed record ProjectData(
    string Path,
    string Name,
    string Language,
    string TargetFramework,
    string[] Arguments,
    string? OutputPath,
    string? OutputRefPath,
    string[]? ProjectReferences = null);

/// <summary>
/// Adds or replaces projects (keyed by <see cref="ProjectData.Path"/>). <see cref="Loading"/>, when set, says whether
/// the solution is still loading (design-time builds still to come): the host then holds back semantic diagnostics that
/// missing projects would make false (M34b). An update may carry no projects, only that state.
/// </summary>
public sealed record ProjectsUpdateParams(ProjectData[] Projects)
{
    public bool? Loading { get; init; }
}

public sealed record ProjectsRemoveParams(string[] Paths);

public sealed record DocumentOptionsParams(TextDocumentIdentifier TextDocument);

/// <summary>Editor options for a document, from <c>.editorconfig</c>. <see cref="NewLine"/> is null when unspecified.</summary>
public sealed record DocumentOptions(bool InsertSpaces, int TabSize, int IndentSize, string? NewLine);

/// <summary>The text of a source-generated document (URIs with the <c>codalus-generated</c> scheme).</summary>
public sealed record SourceGeneratedTextParams(TextDocumentIdentifier TextDocument);

public sealed record SourceGeneratedText(string Text, string HintName, string GeneratorName);

/// <summary><c>codalus/generatedDocuments</c>: the source-generated files of a project (by its project file).</summary>
public sealed record GeneratedDocumentsParams(string Project);

/// <summary>
/// One generated file: its <c>codalus-generated</c> URI (its text through <c>codalus/sourceGeneratedText</c>), hint
/// name, and the generator's assembly (its file name without the extension, as the Solution Explorer lists analyzers)
/// and type.
/// </summary>
public sealed record GeneratedDocumentInfo(string Uri, string HintName, string Assembly, string Generator);

public sealed record GeneratedDocumentsResult(GeneratedDocumentInfo[] Documents);

/// <summary>
/// <c>codalus/browse</c> (Class View and the Object Browser): the children of <see cref="Parent"/> in a project (its
/// project file), or the roots: the namespaces of its source, or with <see cref="References"/> the assemblies it compiles
/// against. With <see cref="Query"/>, the types and members whose names contain it instead, as a flat list.
/// </summary>
public sealed record BrowseParams(string Project, string? Parent, bool References)
{
    public string? Query { get; init; }
}

/// <summary>
/// An item: <see cref="Id"/> names it in later requests (a documentation comment id, <c>A:assembly</c>, or
/// <c>A:assembly|N:namespace</c>), <see cref="Kind"/> is an LSP symbol kind (an assembly is a package).
/// </summary>
public sealed record BrowseItem(string Id, string Name, int Kind, bool HasChildren)
{
    /// <summary>A member's signature; in search results, the containing symbol.</summary>
    public string? Detail { get; init; }

    /// <summary>Declared in the project's source (else in a referenced assembly).</summary>
    public bool IsSource { get; init; }
}

public sealed record BrowseResult(BrowseItem[] Items);

public sealed record BrowseDescribeParams(string Project, string Id);

/// <summary>An item's declaration, where it is (in source, or a read-only <c>codalus-metadata</c> document) and its summary.</summary>
public sealed record BrowseDescription(string Declaration, Location? Location)
{
    public string? Summary { get; init; }

    /// <summary>"Member of Namespace.Type".</summary>
    public string? Container { get; init; }

    public string? Assembly { get; init; }
}

/// <summary>
/// The source of a type from a referenced assembly (URIs with the <c>codalus-metadata</c> scheme): the original source
/// when the assembly's PDB embeds it or Source Link can fetch it, otherwise decompiled C#.
/// </summary>
public sealed record MetadataTextParams(TextDocumentIdentifier TextDocument);

/// <summary><see cref="Origin"/> is one of <see cref="MetadataOrigins"/>; <see cref="SourcePath"/> is the original file's path when known.</summary>
public sealed record MetadataText(string Text, string AssemblyName, string TypeName)
{
    public string? Origin { get; init; }

    public string? SourcePath { get; init; }
}

public static class MetadataOrigins
{
    public const string Decompiled = "decompiled";

    /// <summary>Embedded in the assembly's portable PDB.</summary>
    public const string Embedded = "embedded";

    /// <summary>Downloaded through the PDB's Source Link map (setting <c>navigation.sourceLink</c>).</summary>
    public const string SourceLink = "sourceLink";
}

public static class WorkspaceStates
{
    /// <summary>Projects are being loaded or the first compilation pass is running.</summary>
    public const string Loading = "loading";

    /// <summary>Every known project has been compiled once and its diagnostics published.</summary>
    public const string Ready = "ready";
}

/// <summary>Server → client: progress of loading projects (status bar, benchmarks).</summary>
public sealed record WorkspaceStatusParams(string State, int Projects, int Documents);

/// <summary>
/// Finds tests in source (ADR 0010): methods and classes with known test attributes, from syntax only, so it works
/// before design-time builds. Documents the host knows use their current text (unsaved edits); others are read from disk.
/// </summary>
public sealed record TestCandidatesParams(TextDocumentIdentifier[] TextDocuments);

public static class TestCandidateKinds
{
    public const string Class = "class";
    public const string Method = "method";
}

/// <summary>
/// A test class or method found in source. <see cref="Type"/> is the managed type name the runners report
/// (<c>Namespace.Outer+Inner</c>, with <c>`1</c> for generic types); <see cref="Name"/> is the method name (for
/// classes, the type's simple name). <see cref="Range"/> covers the declaration, <see cref="SelectionRange"/> its name.
/// <see cref="Parameterized"/> marks theories and data-driven tests, whose cases only a runner can list.
/// </summary>
public sealed record TestCandidate(
    string Kind,
    string Name,
    string Type,
    string? Namespace,
    Range Range,
    Range SelectionRange,
    bool Parameterized);

public sealed record DocumentTestCandidates(string Uri, TestCandidate[] Candidates);

public sealed record TestCandidatesResult(DocumentTestCandidates[] Documents);

/// <summary>The type around a position (<c>codalus/typeOutline</c>), for Create Unit Tests.</summary>
public sealed record TypeOutlineParams(TextDocumentIdentifier TextDocument, Position Position);

public sealed record ParameterOutline(string Name, string Type);

/// <summary>A public or internal method; <see cref="Line"/> is its name's zero-based line.</summary>
public sealed record MethodOutline(string Name, string ReturnType, ParameterOutline[] Parameters, bool IsStatic, bool IsAsync, int Line);

/// <summary>
/// A class, record or struct: <see cref="NestedName"/> is <c>Outer.Inner</c> within <see cref="Namespace"/>;
/// <see cref="SelectedMethod"/> is the method the position is in.
/// </summary>
public sealed record TypeOutline(
    string Name,
    string? Namespace,
    string NestedName,
    bool IsStatic,
    bool HasParameterlessConstructor,
    MethodOutline[] Methods,
    string? SelectedMethod);

// ------------------------------------------------------------------ M47 Code Search

/// <summary>
/// Code Search's index (<c>codalus/symbolIndex</c>, ADR 0078): the declarations of the documents in <see cref="Uris"/>,
/// or of every document of the solution when it is null.
/// </summary>
public sealed record SymbolIndexParams(string[]? Uris = null);

/// <summary>
/// A type or member declared in a document: <see cref="Kind"/> is an LSP symbol kind; <see cref="Container"/> is the
/// containing type for a member and the namespace for a type; the position is the name's (zero-based).
/// </summary>
public sealed record IndexedSymbol(string Name, int Kind, int Line, int Character)
{
    public string? Container { get; init; }
}

/// <summary>A document's declarations; <see cref="Project"/> is the project's name.</summary>
public sealed record IndexedDocument(string Uri, string Project, IndexedSymbol[] Symbols);

public sealed record SymbolIndexResult(IndexedDocument[] Documents);

/// <summary>
/// Types of the assemblies the solution references that match <see cref="Query"/> (<c>codalus/externalSymbols</c>,
/// Code Search's "include external items"), at most <see cref="Limit"/>.
/// </summary>
public sealed record ExternalSymbolsParams(string Query, int Limit = 50);

// ------------------------------------------------------------------ M12 navigation extensions

public static class ReferenceItemKinds
{
    public const string Declaration = "declaration";
    public const string Read = "read";
    public const string Write = "write";

    /// <summary>A use that is neither a read nor a write: <c>nameof</c>, a type name, an attribute.</summary>
    public const string Reference = "reference";
}

/// <summary>
/// A reference with what Find All References shows besides its location (<c>codalus/references</c>): how the symbol is
/// used (<see cref="ReferenceItemKinds"/>), the member and project containing it, and its line's text.
/// </summary>
public sealed record ReferenceItem(Location Location, string Kind, string? ContainingMember, string? Project, string LineText);

public sealed record ReferenceItemsResult(string SymbolName, ReferenceItem[] Items);

public static class InheritanceRelations
{
    public const string Implements = "implements";
    public const string ImplementedBy = "implementedBy";
    public const string Overrides = "overrides";
    public const string OverriddenBy = "overriddenBy";
    public const string Inherits = "inherits";
    public const string InheritedBy = "inheritedBy";
}

public sealed record InheritanceMarginParams(TextDocumentIdentifier TextDocument, Range Range);

/// <summary>A base or derived symbol of a margin entry; <see cref="Location"/> is null for symbols without source.</summary>
public sealed record InheritanceTarget(string Relation, string DisplayName, Location? Location);

/// <summary>A type or member declared on <see cref="Line"/> (zero-based) that has bases or derived symbols.</summary>
public sealed record InheritanceMarginItem(int Line, string DisplayName, string[] Relations, InheritanceTarget[] Targets);

/// <summary>
/// About the symbol at a position (<c>codalus/symbolInfo</c>): F1 help and the Code Definition window's title.
/// <see cref="HelpUrl"/> is the API reference page for framework symbols, or the package's project URL.
/// </summary>
public sealed record SymbolDetails(string DisplayName, string Kind, string? DocumentationId, string? AssemblyName, bool InSource, string? HelpUrl);

// ------------------------------------------------------------------ M12b editing extensions

/// <summary><c>codalus/docCommentTemplate</c>: typing <c>///</c> above a member; the edit completes the comment.</summary>
public sealed record DocCommentTemplate(TextEdit Edit, Position Caret);

/// <summary>
/// <c>codalus/codeCleanup</c> (M12b): the fixes to apply to the whole document (diagnostic ids; null: the default
/// profile), then sorting usings and formatting (both on unless false). The result is a <see cref="WorkspaceEdit"/>.
/// </summary>
public sealed record CodeCleanupParams(TextDocumentIdentifier TextDocument, string[]? Fixes, bool? SortUsings, bool? Format);

/// <summary>
/// <c>codalus/pasteImports</c>: the usings (VB: Imports) that the code just pasted at <see cref="Range"/> needs, as a
/// workspace edit, or null. <see cref="Namespaces"/> are the ones the code was copied under, preferred when a name has
/// several candidates.
/// </summary>
public sealed record PasteImportsParams(TextDocumentIdentifier TextDocument, Range Range, string[]? Namespaces);

/// <summary>A choice of a refactoring's options dialog: a member, a parameter or a flag.</summary>
public sealed record OptionItem(string Id, string Label, bool Checked)
{
    public string? Detail { get; init; }
}

/// <summary>
/// <c>codalus/codeActionOptions</c> (M12b, ADR 0018): what a refactoring needs to ask before it runs (Generate
/// Constructor's members, Extract Interface's name and members, Change Signature's parameters). The client shows a
/// dialog and resolves the action with the answers in its data (<see cref="CodeActionChoices"/>).
/// </summary>
public sealed record CodeActionOptions(string Title, OptionItem[] Items)
{
    /// <summary>A name to enter (the interface name…), with its label; null: none.</summary>
    public string? NameLabel { get; init; }

    public string? Name { get; init; }

    /// <summary>The items can be reordered (Change Signature).</summary>
    public bool Reorderable { get; init; }

    /// <summary>A short text above the items.</summary>
    public string? Message { get; init; }
}

public sealed record CodeActionOptionsParams(CodeAction Action);

/// <summary>The answers: the entered name and the checked items' ids in their order.</summary>
public sealed record CodeActionChoices(string? Name, string[] Selected)
{
    /// <summary>The action with these answers added to its data (<c>choices</c>), ready for <c>codeAction/resolve</c>.</summary>
    public CodeAction AttachTo(CodeAction action)
    {
        var data = action.Data is { ValueKind: System.Text.Json.JsonValueKind.Object } existing
            ? System.Text.Json.Nodes.JsonNode.Parse(existing.GetRawText())!.AsObject()
            : new System.Text.Json.Nodes.JsonObject();
        data["choices"] = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(this, ProtocolJsonContext.Default.CodeActionChoices));
        using var document = System.Text.Json.JsonDocument.Parse(data.ToJsonString());
        return action with { Data = document.RootElement.Clone() };
    }
}


// ------------------------------------------------------------------ M12b analysis extensions

/// <summary>
/// <c>codalus/syntaxTree</c> (M12b, the Syntax Visualizer): the document's syntax tree around a position, and what the
/// compiler knows about the node there.
/// </summary>
public sealed record SyntaxTreeParams(TextDocumentIdentifier TextDocument, Position Position);

/// <summary>
/// A node, token or trivia (<see cref="Category"/>: <c>node</c>, <c>token</c>, <c>trivia</c>). Large trees are cut off
/// away from the position: <see cref="Truncated"/> nodes have children that were not sent.
/// </summary>
public sealed record SyntaxNodeItem(string Kind, string Category, Range Range, SyntaxNodeItem[] Children)
{
    /// <summary>The token or trivia text (shortened).</summary>
    public string? Text { get; init; }

    public bool Truncated { get; init; }

    /// <summary>On the path from the root to the innermost item at the position.</summary>
    public bool OnPath { get; init; }
}

/// <summary>An operation of the <c>IOperation</c> tree.</summary>
public sealed record OperationItem(string Kind, string? Type, string Syntax, OperationItem[] Children);

/// <summary>The semantic information about the innermost node at the position.</summary>
public sealed record SyntaxSemantics(string NodeKind)
{
    public string? Symbol { get; init; }

    public string? SymbolKind { get; init; }

    public string? Type { get; init; }

    public string? ConvertedType { get; init; }

    public string? Conversion { get; init; }

    public string? ConstantValue { get; init; }

    public OperationItem? Operation { get; init; }
}

public sealed record SyntaxTreeResult(SyntaxNodeItem Root, SyntaxSemantics? Semantics);

/// <summary>
/// <c>codalus/codeMetrics</c> (M12b): metrics for the project of a document, or for every project when it is null.
/// </summary>
public sealed record CodeMetricsParams(TextDocumentIdentifier? TextDocument);

/// <summary>
/// The metrics of a project, namespace, type or member (<see cref="Kind"/>), as Visual Studio's Code Metrics reports them:
/// the maintainability index (0–100), the cyclomatic complexity, the depth of inheritance, the class coupling, and the
/// lines of source and executable code.
/// </summary>
public sealed record CodeMetricsItem(string Name, string Kind, int MaintainabilityIndex, int CyclomaticComplexity, int DepthOfInheritance, int ClassCoupling, int SourceLines, int ExecutableLines, CodeMetricsItem[] Children)
{
    public Location? Location { get; init; }
}

public sealed record CodeMetricsResult(CodeMetricsItem[] Projects);

/// <summary><c>codalus/aspnet/endpoints</c>: every project's endpoints, or one project's (by the path of its project file).</summary>
public sealed record AspNetEndpointsParams(string? Project = null);

/// <summary>
/// An endpoint. <see cref="HttpMethod"/> is <c>GET</c>, <c>POST</c>… or <c>*</c> (any). <see cref="Route"/> is the
/// full route template with group and controller prefixes; <see cref="Kind"/> is <c>minimal</c> or <c>controller</c>.
/// <see cref="Location"/> is the handler (a method, or the lambda).
/// </summary>
public sealed record AspNetEndpoint(string HttpMethod, string Route, string Handler, string Kind, string Project, string ProjectPath, Location Location);

public sealed record AspNetEndpointsResult(AspNetEndpoint[] Endpoints);

/// <summary><c>codalus/ef/contexts</c> (M27a, ADR 0036): the DbContext types of every project, or of one.</summary>
public sealed record EfContextsParams(string? Project = null);

/// <summary>
/// A class deriving from EF Core's <c>DbContext</c>, found with the semantic model without building or running anything.
/// <see cref="Provider"/> is the EF Core provider package its configuration uses (from <c>UseSqlite</c>,
/// <c>UseSqlServer</c>, <c>UseNpgsql</c>… in <c>OnConfiguring</c> or an <c>AddDbContext&lt;T&gt;</c> call anywhere in the
/// solution, or Aspire's <c>Add…DbContext&lt;T&gt;</c>); <see cref="ConnectionName"/> is the configuration name given to
/// <c>GetConnectionString</c> (or Aspire's connection name). <see cref="Entities"/> are the types of its <c>DbSet</c>s.
/// </summary>
public sealed record EfDiscoveredContext(
    string Project,
    string Name,
    string FullName,
    Location Location,
    string? Provider,
    string? ConnectionName,
    string[] Entities,
    bool HasDesignTimeFactory);

public sealed record EfDiscoveredContextsResult(EfDiscoveredContext[] Contexts);

/// <summary>
/// <c>codalus/sql/context</c> (M27b, ADR 0038), client → SQL server notification: a SQL document's dialect
/// (<c>sqlserver</c>, <c>postgresql</c>, <c>mysql</c>, <c>sqlite</c> or <c>sql</c> when it has no connection) and the
/// connection's schema, for completion and hover. Sent when the editor's connection or its schema changes.
/// </summary>
public sealed record SqlContextParams(string Uri, string Dialect, string? DefaultSchema, SqlSchemaObject[] Objects);

/// <summary>A table, view, procedure or function (<see cref="Kind"/>) of the connection's database.</summary>
public sealed record SqlSchemaObject(string? Schema, string Name, string Kind, SqlSchemaColumn[] Columns);

public sealed record SqlSchemaColumn(string Name, string Type, bool IsKey, bool Nullable);

/// <summary><c>codalus/xaml/logicalTree</c> (M19a, ADR 0047): an <c>.axaml</c> file's controls as the previewer's logical tree.</summary>
public sealed record XamlLogicalTreeParams(TextDocumentIdentifier TextDocument);

/// <summary>
/// A control the XAML declares: <see cref="Type"/> is the type's name (the root's <c>x:Class</c> name, which is what
/// runs), <see cref="Name"/> its <c>x:Name</c>, <see cref="Range"/> the element from its start tag to its end tag,
/// <see cref="Tag"/> its start tag, and <see cref="Children"/> its logical children (through content properties, not templates, styles or resources).
/// </summary>
public sealed record XamlLogicalNode(string Type, string? Name, Range Range, Range Tag, XamlLogicalNode[] Children);

public sealed record XamlLogicalTreeResult(XamlLogicalNode[] Nodes);

/// <summary>
/// <c>codalus/winforms/read</c> (M41a, ADR 0073): a form's or user control's designer model, from its code file
/// (<c>MainForm.cs</c>) or its designer file (<c>MainForm.Designer.cs</c>).
/// </summary>
public sealed record WinFormsReadParams(TextDocumentIdentifier TextDocument)
{
    /// <summary>The form's <c>.resx</c> files as the designer has them (M41b), which win over the files on disk.</summary>
    public DesignerFileText[]? ResourceFiles { get; init; }
}

/// <summary>A file's text as the client has it, unsaved (M41b): a form's <c>.resx</c> file the designer edits.</summary>
public sealed record DesignerFileText(string Path, string Text);

/// <summary>
/// The designer files of a class (<see cref="CodeFile"/> and <see cref="DesignerFile"/>) and, when InitializeComponent
/// can be read, its <see cref="Model"/>. Otherwise <see cref="Problem"/> says why, at <see cref="ProblemLine"/>
/// (zero-based) of the designer file when it's a statement: the file opens as code, as Visual Studio does.
/// <see cref="IsDesignable"/> is false when the class isn't a form, a user control or another control.
/// </summary>
public sealed record WinFormsReadResult(string? CodeFile, string? DesignerFile, bool IsDesignable, DesignerModel? Model, string? Problem, int? ProblemLine)
{
    /// <summary>The <c>.resx</c> next to the code file, if any.</summary>
    public string? ResourceFile { get; init; }

    /// <summary>The project the file belongs to.</summary>
    public string? Project { get; init; }

    /// <summary>The class's full name (<c>WinFormsApp.MainForm</c>), whose compiled resources <c>resources</c> reads.</summary>
    public string? ClassFullName { get; init; }

    /// <summary>The form is localizable (M41b): its default <c>.resx</c> says <c>$this.Localizable</c>, so its localizable properties are written there.</summary>
    public bool IsLocalizable { get; init; }

    /// <summary>The cultures the form has resources for besides the default (<c>fr</c> for <c>MainForm.fr.resx</c>), sorted.</summary>
    public string[] Cultures { get; init; } = [];

    /// <summary>The non-visual components in the order of their tray locations (the <c>.resx</c>'s <c>TrayLocation</c> metadata), for the component tray.</summary>
    public string[] TrayOrder { get; init; } = [];

    /// <summary>
    /// Read without all of the project's types (#76): from Miscellaneous Files (the C# host doesn't have the project
    /// yet), from a compilation without Windows Forms (before the restore), or with types named by their syntax.
    /// </summary>
    public bool IsPartial { get; init; }
}

/// <summary>The kinds of <see cref="DesignerEdit"/>.</summary>
public static class DesignerEditKinds
{
    /// <summary>Sets <see cref="DesignerEdit.Property"/> of <see cref="DesignerEdit.Component"/> to <see cref="DesignerEdit.Value"/>, or removes its assignment when the value is null (the default).</summary>
    public const string SetProperty = "setProperty";

    /// <summary>Adds a component: its field, creation, section with <see cref="DesignerEdit.Properties"/>, and its <c>Controls.Add</c> in <see cref="DesignerEdit.Parent"/>.</summary>
    public const string AddComponent = "addComponent";

    /// <summary>Removes a component and every statement about it.</summary>
    public const string RemoveComponent = "removeComponent";

    /// <summary>Renames a component (<see cref="DesignerEdit.NewName"/>): its field, statements, section, <c>Name</c>, and its uses in the class.</summary>
    public const string RenameComponent = "renameComponent";

    /// <summary>Moves a control to another parent (<see cref="DesignerEdit.Parent"/>, <see cref="DesignerEdit.ParentPath"/>, <see cref="DesignerEdit.Cell"/>, <see cref="DesignerEdit.Index"/>).</summary>
    public const string SetParent = "setParent";

    /// <summary>Hooks <see cref="DesignerEdit.Event"/> to <see cref="DesignerEdit.Handler"/>, generating the handler in the code file when it doesn't exist.</summary>
    public const string AddEvent = "addEvent";

    /// <summary>Unhooks <see cref="DesignerEdit.Event"/> (the handler method stays, as in Visual Studio).</summary>
    public const string RemoveEvent = "removeEvent";

    /// <summary>
    /// Moves a control's <c>Controls.Add</c> to <see cref="DesignerEdit.Index"/> among its parent's (M41b): the z-order,
    /// front first, and a tab page's position, as Visual Studio writes them. The statement's text moves as it is.
    /// </summary>
    public const string SetChildIndex = "setChildIndex";

    /// <summary>
    /// Sets an extender property (M41b): <c>provider.SetProperty(component, value)</c> (<see cref="DesignerEdit.Provider"/>,
    /// <see cref="DesignerEdit.Property"/>) in the extended component's section, or removes it when the value is null.
    /// </summary>
    public const string SetExtender = "setExtender";

    /// <summary>
    /// Writes a collection (M41b): <see cref="DesignerEdit.Items"/> as <c>component.Property.AddRange(new Type[] { … })</c>
    /// (<see cref="DesignerEdit.Method"/> <c>AddRange</c>, <see cref="DesignerEdit.Type"/> the element type), one
    /// <c>Add(item)</c> each (<c>Add</c>), or <c>Images.SetKeyName(i, key)</c> each (<c>SetKeyName</c>), in place of the
    /// statements that filled it; no items, no statement.
    /// </summary>
    public const string SetCollection = "setCollection";

    /// <summary>Declares a local object (M41b): <c>Type name = value;</c> before the components are created (a <c>ListViewItem</c>).</summary>
    public const string AddLocal = "addLocal";

    /// <summary>
    /// Binds <see cref="DesignerEdit.Property"/> (M41b): <c>component.DataBindings.Add(value)</c> with the binding as
    /// <see cref="DesignerEdit.Value"/> (<c>new Binding("Text", bindingSource1, "Name", true)</c>), replacing the
    /// property's binding, or removing it when the value is null.
    /// </summary>
    public const string SetDataBinding = "setDataBinding";

    /// <summary>
    /// Sets or removes a key of the form's <c>.resx</c> for <see cref="DesignerEdit.Culture"/> (M41b):
    /// <see cref="DesignerEdit.Key"/>, <see cref="DesignerEdit.Resource"/>, <see cref="DesignerEdit.Metadata"/>.
    /// </summary>
    public const string SetResource = "setResource";

    /// <summary>
    /// Adds (<see cref="DesignerEdit.Localized"/> true) or removes <c>resources.ApplyResources(component, "name")</c>,
    /// the first statement of a localizable form's component's section (M41b).
    /// </summary>
    public const string SetLocalized = "setLocalized";
}

/// <summary>
/// A change the designer makes. <see cref="Component"/> is a name, empty for the root. <see cref="Parent"/> is the
/// parent control's name (empty for the root), <see cref="ParentPath"/> a sub-control holding its children
/// (<c>Panel1</c> of a <c>SplitContainer</c>), <see cref="Cell"/> a <c>TableLayoutPanel</c>'s column and row, and
/// <see cref="Index"/> the position in the parent's <c>Controls</c> (0, the front, is where new controls go).
/// </summary>
public sealed record DesignerEdit
{
    public required string Kind { get; init; }

    public string Component { get; init; } = string.Empty;

    public string? Property { get; init; }

    public DesignerValue? Value { get; init; }

    public string? Type { get; init; }

    public DesignerValue[]? CreationArguments { get; init; }

    public string? Parent { get; init; }

    public string? ParentPath { get; init; }

    public int[]? Cell { get; init; }

    public int? Index { get; init; }

    /// <summary>The new component's properties in the order they are written (the writer sorts them as Visual Studio does).</summary>
    public DesignerPropertyValue[]? Properties { get; init; }

    /// <summary>The new component implements <c>ISupportInitialize</c> (BeginInit and EndInit around the statements).</summary>
    public bool SupportsInitialize { get; init; }

    public string? NewName { get; init; }

    public string? Event { get; init; }

    public string? Handler { get; init; }

    /// <summary>The event's delegate type (<c>System.EventHandler</c>), when the caller knows it; else the C# host finds it.</summary>
    public string? DelegateType { get; init; }

    /// <summary>The handler's parameters as <c>Type name</c> pairs (<c>System.Object sender</c>), when the caller knows them.</summary>
    public string[]? HandlerParameters { get; init; }

    /// <summary>An extender property's provider (<c>toolTip1</c>).</summary>
    public string? Provider { get; init; }

    /// <summary>How a collection is written: <c>AddRange</c>, <c>Add</c> or <c>SetKeyName</c>.</summary>
    public string? Method { get; init; }

    public DesignerValue[]? Items { get; init; }

    /// <summary>A <c>.resx</c> key.</summary>
    public string? Key { get; init; }

    /// <summary>The <c>.resx</c>'s culture: empty or null for the default one.</summary>
    public string? Culture { get; init; }

    public DesignerResource? Resource { get; init; }

    public bool Metadata { get; init; }

    public bool? Localized { get; init; }
}

/// <summary><c>codalus/winforms/edit</c>: <see cref="Edits"/> applied in order to the designer file at <see cref="Version"/>.</summary>
public sealed record WinFormsEditParams(TextDocumentIdentifier TextDocument, DesignerEdit[] Edits)
{
    public int? Version { get; init; }

    /// <summary>The form's <c>.resx</c> files as the designer has them (M41b), which win over the files on disk.</summary>
    public DesignerFileText[]? ResourceFiles { get; init; }
}

/// <summary>
/// The edit to apply (minimal text edits of the designer file, and of the code file for a new event handler; a rename
/// also reaches the class's other uses), the model after it, and where a new or existing event handler is.
/// <see cref="Error"/> says why nothing could be done.
/// </summary>
public sealed record WinFormsEditResult(WorkspaceEdit Edit, DesignerModel? Model, string? Error)
{
    public Location? Handler { get; init; }
}

/// <summary><c>codalus/winforms/toolbox</c>: the components a project and the projects it references declare, for the Toolbox.</summary>
public sealed record WinFormsToolboxParams(string Project);

/// <summary>A component for the Toolbox: its full type name, its display name, and the project that declares it.</summary>
public sealed record WinFormsToolboxItem(string Type, string Name, string Project, bool IsControl)
{
    public string? Description { get; init; }
}

public sealed record WinFormsToolboxResult(WinFormsToolboxItem[] Items);
