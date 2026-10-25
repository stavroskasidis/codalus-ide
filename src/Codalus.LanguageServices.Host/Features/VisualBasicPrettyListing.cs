using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Codalus.LanguageServices.Host.Features;

/// <summary>
/// Visual Basic's editor behaviour (M35, ADR 0066), the "pretty listing" Roslyn implements only in the classic IDE's
/// editor layers. It's ours, served as on-type formatting:
/// <list type="bullet">
/// <item>Enter after a block's first line (<c>If … Then</c>, <c>For Each …</c>, <c>Sub …</c>, <c>Class …</c>…) whose
/// end is missing adds the end (<c>End If</c>, <c>Next</c>, <c>End Sub</c>…) on the line after the caret, indented
/// like the block.</item>
/// <item>Enter also fixes the casing of the keywords on the line it ends (<c>dim x as integer</c> → <c>Dim x As
/// Integer</c>).</item>
/// <item><c>&gt;</c> that ends an XML literal's start tag adds its end tag (<c>&lt;item&gt;</c> → <c>&lt;item&gt;&lt;/item&gt;</c>).</item>
/// </list>
/// </summary>
internal static class VisualBasicPrettyListing
{
    public static async Task<Protocol.TextEdit[]> OnTypeAsync(Document document, int position, string ch, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || position == 0)
        {
            return [];
        }

        return ch switch
        {
            "\n" => [.. AfterEnter(root, text, position)],
            ">" => [.. AfterGreaterThan(root, text, position)],
            _ => [],
        };
    }

    // ------------------------------------------------------------------ Enter

    private static IEnumerable<Protocol.TextEdit> AfterEnter(SyntaxNode root, SourceText text, int position)
    {
        var caretLine = text.Lines.GetLineFromPosition(position);
        if (caretLine.LineNumber == 0)
        {
            yield break;
        }

        var previous = text.Lines[caretLine.LineNumber - 1];

        // Keywords on the line just ended, in their canonical casing.
        foreach (var token in root.DescendantTokens(previous.Span))
        {
            if (SyntaxFacts.IsKeywordKind(token.Kind()) && SyntaxFacts.GetText(token.Kind()) is { Length: > 0 } canonical
                && !string.Equals(token.Text, canonical, StringComparison.Ordinal) && string.Equals(token.Text, canonical, StringComparison.OrdinalIgnoreCase)
                && token.Span.End <= previous.End)
            {
                yield return new Protocol.TextEdit(Positions.ToRange(text, token.Span), canonical);
            }
        }

        // The end of a block opened on that line.
        if (MissingEnd(root, previous) is not { } end)
        {
            yield break;
        }

        var opener = text.Lines.GetLineFromPosition(end.Block.SpanStart);
        var indent = text.ToString(TextSpan.FromBounds(opener.Start, opener.Start + LeadingWhitespace(text, opener)));
        var newline = Newline(text);
        if (caretLine.LineNumber + 1 < text.Lines.Count)
        {
            var next = text.Lines[caretLine.LineNumber + 1];
            yield return new Protocol.TextEdit(Positions.ToRange(text, new TextSpan(next.Start, 0)), indent + end.Text + newline);
        }
        else
        {
            yield return new Protocol.TextEdit(Positions.ToRange(text, new TextSpan(caretLine.End, 0)), newline + indent + end.Text);
        }
    }

    /// <summary>The innermost block whose first statement ends on <paramref name="line"/> and whose end statement is missing, with the end to add.</summary>
    private static (SyntaxNode Block, string Text)? MissingEnd(SyntaxNode root, TextLine line)
    {
        var token = root.FindToken(Math.Max(line.Start, line.End - 1));
        foreach (var node in token.Parent?.AncestorsAndSelf() ?? [])
        {
            var (begin, end, endText) = node switch
            {
                MultiLineIfBlockSyntax b => (b.IfStatement, (SyntaxNode)b.EndIfStatement, "End If"),
                ForBlockSyntax b => (b.ForStatement, b.NextStatement, "Next"),
                ForEachBlockSyntax b => (b.ForEachStatement, b.NextStatement, "Next"),
                WhileBlockSyntax b => (b.WhileStatement, b.EndWhileStatement, "End While"),
                DoLoopBlockSyntax b => (b.DoStatement, b.LoopStatement, "Loop"),
                SelectBlockSyntax b => (b.SelectStatement, b.EndSelectStatement, "End Select"),
                TryBlockSyntax b => (b.TryStatement, b.EndTryStatement, "End Try"),
                WithBlockSyntax b => (b.WithStatement, b.EndWithStatement, "End With"),
                UsingBlockSyntax b => (b.UsingStatement, b.EndUsingStatement, "End Using"),
                SyncLockBlockSyntax b => (b.SyncLockStatement, b.EndSyncLockStatement, "End SyncLock"),
                MethodBlockSyntax b => (b.SubOrFunctionStatement, b.EndSubOrFunctionStatement, b.SubOrFunctionStatement.DeclarationKeyword.IsKind(SyntaxKind.FunctionKeyword) ? "End Function" : "End Sub"),
                ConstructorBlockSyntax b => (b.SubNewStatement, b.EndSubStatement, "End Sub"),
                OperatorBlockSyntax b => (b.OperatorStatement, b.EndOperatorStatement, "End Operator"),
                PropertyBlockSyntax b => (b.PropertyStatement, b.EndPropertyStatement, "End Property"),
                AccessorBlockSyntax b => (b.AccessorStatement, b.EndAccessorStatement, AccessorEnd(b)),
                EventBlockSyntax b => (b.EventStatement, b.EndEventStatement, "End Event"),
                ClassBlockSyntax b => (b.ClassStatement, b.EndClassStatement, "End Class"),
                ModuleBlockSyntax b => (b.ModuleStatement, b.EndModuleStatement, "End Module"),
                StructureBlockSyntax b => (b.StructureStatement, b.EndStructureStatement, "End Structure"),
                InterfaceBlockSyntax b => (b.InterfaceStatement, b.EndInterfaceStatement, "End Interface"),
                EnumBlockSyntax b => (b.EnumStatement, b.EndEnumStatement, "End Enum"),
                NamespaceBlockSyntax b => (b.NamespaceStatement, b.EndNamespaceStatement, "End Namespace"),
                MultiLineLambdaExpressionSyntax b => (b.SubOrFunctionHeader, b.EndSubOrFunctionStatement, b.SubOrFunctionHeader.DeclarationKeyword.IsKind(SyntaxKind.FunctionKeyword) ? "End Function" : "End Sub"),
                _ => ((SyntaxNode?)null, (SyntaxNode?)null, (string?)null),
            };

            if (begin is null || end is null || endText is null)
            {
                continue;
            }

            // Only the block whose first line is the one just ended; one ended elsewhere already has its end.
            if (begin.Span.End > line.End || begin.Span.End < line.Start)
            {
                return null;
            }

            return end.IsMissing || end.Span.IsEmpty ? (node, endText) : null;
        }

        return null;
    }

    private static string AccessorEnd(AccessorBlockSyntax accessor) => accessor.Kind() switch
    {
        SyntaxKind.SetAccessorBlock => "End Set",
        SyntaxKind.AddHandlerAccessorBlock => "End AddHandler",
        SyntaxKind.RemoveHandlerAccessorBlock => "End RemoveHandler",
        SyntaxKind.RaiseEventAccessorBlock => "End RaiseEvent",
        _ => "End Get",
    };

    private static int LeadingWhitespace(SourceText text, TextLine line)
    {
        var count = 0;
        while (line.Start + count < line.End && text[line.Start + count] is ' ' or '\t')
        {
            count++;
        }

        return count;
    }

    private static string Newline(SourceText text) =>
        text.Lines.Count > 1 && text.Lines[0].EndIncludingLineBreak - text.Lines[0].End == 2 ? "\r\n" : "\n";

    // ------------------------------------------------------------------ XML literals

    private static IEnumerable<Protocol.TextEdit> AfterGreaterThan(SyntaxNode root, SourceText text, int position)
    {
        var token = root.FindToken(position - 1);
        if (token.IsKind(SyntaxKind.GreaterThanToken) && token.Span.End == position
            && token.Parent is XmlElementStartTagSyntax { Name: XmlNameSyntax { LocalName.ValueText: { Length: > 0 } } name } start
            && start.Parent is XmlElementSyntax element && (element.EndTag.IsMissing || element.EndTag.Name is null || element.EndTag.Name.IsMissing || element.EndTag.Span.IsEmpty))
        {
            yield return new Protocol.TextEdit(Positions.ToRange(text, new TextSpan(position, 0)), "</" + name + ">");
        }
    }
}
