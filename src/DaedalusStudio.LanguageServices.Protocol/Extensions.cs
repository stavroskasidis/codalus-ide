namespace DaedalusStudio.LanguageServices.Protocol;

// Daedalus extensions to LSP (docs/protocol.md). LSP has no project model: the shell sends the language host what the
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

/// <summary>Adds or replaces projects (keyed by <see cref="ProjectData.Path"/>).</summary>
public sealed record ProjectsUpdateParams(ProjectData[] Projects);

public sealed record ProjectsRemoveParams(string[] Paths);

public sealed record DocumentOptionsParams(TextDocumentIdentifier TextDocument);

/// <summary>Editor options for a document, from <c>.editorconfig</c>. <see cref="NewLine"/> is null when unspecified.</summary>
public sealed record DocumentOptions(bool InsertSpaces, int TabSize, int IndentSize, string? NewLine);

/// <summary>The text of a source-generated document (URIs with the <c>daedalus-generated</c> scheme).</summary>
public sealed record SourceGeneratedTextParams(TextDocumentIdentifier TextDocument);

public sealed record SourceGeneratedText(string Text, string HintName, string GeneratorName);

/// <summary>
/// The source of a type from a referenced assembly (URIs with the <c>daedalus-metadata</c> scheme): the original source
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

/// <summary>The type around a position (<c>daedalus/typeOutline</c>), for Create Unit Tests.</summary>
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
/// A reference with what Find All References shows besides its location (<c>daedalus/references</c>): how the symbol is
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
/// About the symbol at a position (<c>daedalus/symbolInfo</c>): F1 help and the Code Definition window's title.
/// <see cref="HelpUrl"/> is the API reference page for framework symbols, or the package's project URL.
/// </summary>
public sealed record SymbolDetails(string DisplayName, string Kind, string? DocumentationId, string? AssemblyName, bool InSource, string? HelpUrl);

