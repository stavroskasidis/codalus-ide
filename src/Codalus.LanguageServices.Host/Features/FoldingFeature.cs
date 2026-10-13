using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Codalus.LanguageServices.Host.Features;

/// <summary>
/// Folding ranges from the syntax tree (Roslyn's block structure service is internal): namespaces, types, members,
/// accessors, multi-line lambdas and initializers, <c>#region</c> blocks, groups of <c>using</c> directives, and
/// comment blocks. A range starts on the line where its declaration begins and ends at its closing brace.
/// </summary>
internal static class FoldingFeature
{
    public static async Task<FoldingRange[]> FoldingRangesAsync(Document document, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return [];
        }

        var ranges = new List<FoldingRange>();
        void Add(int start, int end, string? kind = null, string? collapsed = null)
        {
            var startLine = text.Lines.GetLineFromPosition(start);
            var endLine = text.Lines.GetLinePosition(end).Line;
            if (endLine > startLine.LineNumber)
            {
                ranges.Add(new FoldingRange(startLine.LineNumber, endLine)
                {
                    StartCharacter = startLine.End - startLine.Start,
                    Kind = kind,
                    CollapsedText = collapsed,
                });
            }
        }

        foreach (var node in root.DescendantNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (node)
            {
                case NamespaceDeclarationSyntax ns:
                    Add(HeaderStart(ns), ns.Span.End);
                    break;
                case BaseTypeDeclarationSyntax type:
                    Add(HeaderStart(type), type.Span.End);
                    break;
                case BaseMethodDeclarationSyntax { Body: not null } or BaseMethodDeclarationSyntax { ExpressionBody: not null }:
                case PropertyDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax:
                case LocalFunctionStatementSyntax:
                    Add(HeaderStart(node), node.Span.End);
                    break;
                case AccessorDeclarationSyntax { Body: not null } accessor:
                    Add(HeaderStart(accessor), accessor.Span.End);
                    break;
                case AnonymousFunctionExpressionSyntax { Block: { } block }:
                    Add(block.OpenBraceToken.SpanStart, block.Span.End);
                    break;
                case InitializerExpressionSyntax initializer:
                    Add(initializer.OpenBraceToken.SpanStart, initializer.Span.End);
                    break;
                case SwitchStatementSyntax or SwitchExpressionSyntax:
                    Add(node.SpanStart, node.Span.End);
                    break;
            }
        }

        // #region … #endregion
        foreach (var region in root.DescendantTrivia(descendIntoTrivia: true).Select(t => t.GetStructure()).OfType<RegionDirectiveTriviaSyntax>())
        {
            if (region.GetRelatedDirectives().OfType<EndRegionDirectiveTriviaSyntax>().FirstOrDefault() is { } end)
            {
                var label = region.EndOfDirectiveToken.LeadingTrivia.ToString().Trim();
                Add(region.SpanStart, end.Span.End, "region", label.Length > 0 ? label : "#region");
            }
        }

        AddUsingGroups(root, Add);
        AddCommentBlocks(root, text, Add);
        return [.. ranges.DistinctBy(r => (r.StartLine, r.EndLine)).OrderBy(r => r.StartLine)];
    }

    /// <summary>Where a declaration's header starts: after attributes and documentation comments.</summary>
    private static int HeaderStart(SyntaxNode node)
    {
        var attributes = node switch
        {
            MemberDeclarationSyntax member => member.AttributeLists,
            AccessorDeclarationSyntax accessor => accessor.AttributeLists,
            LocalFunctionStatementSyntax local => local.AttributeLists,
            _ => default,
        };
        var first = attributes.Count > 0 ? attributes.Last().GetLastToken().GetNextToken() : node.GetFirstToken();
        return first.SpanStart;
    }

    private static void AddUsingGroups(SyntaxNode root, Action<int, int, string?, string?> add)
    {
        foreach (var container in root.DescendantNodesAndSelf().Where(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax))
        {
            var usings = container switch
            {
                CompilationUnitSyntax unit => unit.Usings,
                BaseNamespaceDeclarationSyntax ns => ns.Usings,
                _ => default,
            };
            if (usings.Count > 1)
            {
                add(usings[0].SpanStart, usings[^1].Span.End, "imports", "using …");
            }
        }
    }

    /// <summary>Runs of two or more single-line comments, documentation comments, and multi-line block comments.</summary>
    private static void AddCommentBlocks(SyntaxNode root, SourceText text, Action<int, int, string?, string?> add)
    {
        int? runStart = null;
        int runEnd = 0, runLine = -2, runCount = 0;
        string? runLabel = null;
        void Flush()
        {
            if (runStart is { } start && runCount > 1)
            {
                add(start, runEnd, "comment", runLabel);
            }

            runStart = null;
            runCount = 0;
        }

        foreach (var trivia in root.DescendantTrivia())
        {
            switch (trivia.Kind())
            {
                case SyntaxKind.SingleLineCommentTrivia:
                    var line = text.Lines.GetLineFromPosition(trivia.SpanStart).LineNumber;
                    if (runStart is null || line != runLine + 1)
                    {
                        Flush();
                        runStart = trivia.SpanStart;
                        runLabel = trivia.ToString().Trim() + " …";
                    }

                    runEnd = trivia.Span.End;
                    runLine = line;
                    runCount++;
                    break;
                case SyntaxKind.SingleLineDocumentationCommentTrivia:
                    Flush();
                    var docEnd = trivia.Span.End;
                    while (docEnd > trivia.SpanStart && char.IsWhiteSpace(text[docEnd - 1]))
                    {
                        docEnd--;
                    }

                    var summary = trivia.GetStructure() is DocumentationCommentTriviaSyntax doc
                        ? doc.Content.OfType<XmlElementSyntax>().FirstOrDefault(e => e.StartTag.Name.LocalName.Text == "summary")?.Content.ToString()
                        : null;
                    var collapsed = summary is null ? "/// …" : "/// " + string.Join(' ', summary.Replace("///", string.Empty, StringComparison.Ordinal).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) + " …";
                    add(trivia.SpanStart, docEnd, "comment", collapsed);
                    break;
                case SyntaxKind.MultiLineCommentTrivia:
                    Flush();
                    add(trivia.SpanStart, trivia.Span.End, "comment", "/* … */");
                    break;
                case SyntaxKind.WhitespaceTrivia:
                case SyntaxKind.EndOfLineTrivia:
                    break;
                default:
                    Flush();
                    break;
            }
        }

        Flush();
    }
}
