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

/// <summary>The text of a decompiled type from a referenced assembly (URIs with the <c>daedalus-metadata</c> scheme).</summary>
public sealed record MetadataTextParams(TextDocumentIdentifier TextDocument);

public sealed record MetadataText(string Text, string AssemblyName, string TypeName);

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
