namespace Daedalus.LanguageServices.Protocol;

public sealed record InitializeParams(int ProcessId, string ClientName);

public sealed record InitializeResult(string ServerName, string ServerVersion, SemanticTokensLegend Legend);

/// <summary>Same shape as LSP's <c>SemanticTokensLegend</c>.</summary>
public sealed record SemanticTokensLegend(string[] TokenTypes, string[] TokenModifiers);

public sealed record OpenSolutionParams(string Path);

public enum SolutionNodeKind
{
    Solution,
    Folder,
    Project,
    Document,
}

public sealed record SolutionNode(SolutionNodeKind Kind, string Name, string? Path, SolutionNode[] Children);

public sealed record OpenSolutionResult(SolutionNode Root, string[] Diagnostics);

public sealed record DidOpenParams(string Path, string Text, int Version);

/// <summary>
/// One edit expressed as an offset into the document <em>before</em> this change is applied.
/// Extension: LSP uses line/character ranges; offsets map directly onto rope and
/// AvaloniaEdit document APIs, so the shell does not have to recompute positions.
/// </summary>
public sealed record TextEdit(int Offset, int Length, string Text);

/// <summary>Edits are applied in order, each against the result of the previous one.</summary>
public sealed record DidChangeParams(string Path, int Version, TextEdit[] Changes);

public sealed record DidCloseParams(string Path);

public sealed record SemanticTokensParams(string Path);

/// <summary>Zero-based, end-exclusive line range.</summary>
public sealed record SemanticTokensRangeParams(string Path, int StartLine, int EndLine);

/// <summary>
/// LSP-encoded tokens: five integers per token
/// (deltaLine, deltaStartChar, length, tokenType, tokenModifiers bitset), relative to the previous token.
/// <see cref="Version"/> is the document version the tokens were computed for.
/// </summary>
public sealed record SemanticTokens(int Version, int[] Data);
