using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Codalus.LanguageServices.Host.Features;

/// <summary>
/// Editing support from syntax (M12b): selection ranges for Expand and Shrink Selection, and the XML documentation
/// comment template typed with <c>///</c>.
/// </summary>
internal static class EditingFeatures
{
    /// <summary>For each position, the nested ranges from the token outwards: token, its nodes, and the inside of braces and strings.</summary>
    public static async Task<SelectionRange[]> SelectionRangesAsync(Document document, Position[] positions, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return [];
        }

        var result = new List<SelectionRange>();
        foreach (var position in positions)
        {
            var offset = Positions.ToOffset(text, position);
            var spans = new List<TextSpan>();
            var token = root.FindToken(Math.Clamp(offset, 0, Math.Max(0, root.FullSpan.End - 1)));
            if (token.Span.Contains(offset) || token.Span.End == offset)
            {
                if (token.IsKind(SyntaxKind.StringLiteralToken) && token.Span.Length > 2)
                {
                    spans.Add(TextSpan.FromBounds(token.Span.Start + 1, token.Span.End - 1)); // the string's content
                }

                spans.Add(token.Span);
            }

            for (var node = token.Parent; node is not null; node = node.Parent)
            {
                // The inside of a block, an argument list or a type body before the whole of it.
                var inner = node switch
                {
                    BlockSyntax block when block.Statements.Count > 0 => TextSpan.FromBounds(block.Statements[0].SpanStart, block.Statements[^1].Span.End),
                    BaseArgumentListSyntax args when args.Arguments.Count > 0 => TextSpan.FromBounds(args.Arguments[0].SpanStart, args.Arguments[^1].Span.End),
                    BaseParameterListSyntax parameters when parameters.Parameters.Count > 0 => TextSpan.FromBounds(parameters.Parameters[0].SpanStart, parameters.Parameters[^1].Span.End),
                    TypeDeclarationSyntax type when type.Members.Count > 0 => TextSpan.FromBounds(type.Members[0].SpanStart, type.Members[^1].Span.End),
                    _ => default,
                };
                if (!inner.IsEmpty)
                {
                    spans.Add(inner);
                }

                spans.Add(node.Span);
            }

            spans.Add(new TextSpan(0, text.Length));
            SelectionRange? range = null;
            foreach (var span in spans.Where(s => s.Contains(offset) || s.End == offset).Distinct().OrderByDescending(s => s.Length))
            {
                range = new SelectionRange(Positions.ToRange(text, span)) { Parent = range };
            }

            result.Add(range ?? new SelectionRange(new Protocol.Range(position, position)));
        }

        return [.. result];
    }

    /// <summary>
    /// The documentation comment for the member below a line that is just <c>///</c> (Visual Studio's template): summary,
    /// a <c>param</c> per parameter, <c>typeparam</c>s and <c>returns</c> for non-void methods. Null elsewhere.
    /// </summary>
    public static async Task<DocCommentTemplate?> TemplateAsync(Document document, Position position, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || position.Line >= text.Lines.Count)
        {
            return null;
        }

        var line = text.Lines[position.Line];
        var lineText = line.ToString();
        var indent = lineText[..(lineText.Length - lineText.TrimStart().Length)];
        if (lineText.Trim() != "///" || position.Line + 1 >= text.Lines.Count)
        {
            return null;
        }

        // The member starts on a following line (attributes included).
        var next = text.Lines[position.Line + 1];
        var token = root.FindToken(next.Start + (next.ToString().Length - next.ToString().TrimStart().Length));
        var member = token.Parent?.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault(m => text.Lines.GetLineFromPosition(m.SpanStart).LineNumber == position.Line + 1);
        if (member is null || member.GetLeadingTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) && t.Span.Start > line.End))
        {
            return null;
        }

        var lines = new List<string> { " <summary>", "/// ", "/// </summary>" };
        var typeParameters = member switch
        {
            TypeDeclarationSyntax t => t.TypeParameterList?.Parameters.Select(p => p.Identifier.ValueText),
            MethodDeclarationSyntax m => m.TypeParameterList?.Parameters.Select(p => p.Identifier.ValueText),
            DelegateDeclarationSyntax d => d.TypeParameterList?.Parameters.Select(p => p.Identifier.ValueText),
            _ => null,
        } ?? [];
        lines.AddRange(typeParameters.Select(name => $"/// <typeparam name=\"{name}\"></typeparam>"));
        var parameters = member switch
        {
            BaseMethodDeclarationSyntax m => m.ParameterList.Parameters,
            DelegateDeclarationSyntax d => d.ParameterList.Parameters,
            IndexerDeclarationSyntax i => i.ParameterList.Parameters,
            RecordDeclarationSyntax { ParameterList: { } p } => p.Parameters,
            _ => default,
        };
        lines.AddRange(parameters.Select(p => $"/// <param name=\"{p.Identifier.ValueText}\"></param>"));
        var returns = member switch
        {
            MethodDeclarationSyntax { ReturnType: PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword } } => false,
            MethodDeclarationSyntax => true,
            DelegateDeclarationSyntax { ReturnType: not PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword } } => true,
            _ => false,
        };
        if (returns)
        {
            lines.Add("/// <returns></returns>");
        }

        var newLine = text.Lines.Count > 1 && text.ToString(TextSpan.FromBounds(text.Lines[0].End, text.Lines[0].EndIncludingLineBreak)) is { Length: > 0 } eol ? eol : "\n";
        var inserted = string.Join(newLine + indent, lines);

        // The caret goes on the summary's empty line.
        var caretLine = position.Line + 1;
        var caretCharacter = indent.Length + "/// ".Length;
        return new DocCommentTemplate(new TextEdit(new Protocol.Range(Positions.ToPosition(text, line.End), Positions.ToPosition(text, line.End)), inserted), new Position(caretLine, caretCharacter));
    }
}
