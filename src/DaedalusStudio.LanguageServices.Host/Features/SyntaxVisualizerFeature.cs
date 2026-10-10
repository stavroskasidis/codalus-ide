using DaedalusStudio.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace DaedalusStudio.LanguageServices.Host.Features;

/// <summary>
/// The Syntax Visualizer (M12b, for analyzer and generator authors): the syntax tree with its tokens and trivia, and the
/// symbol, types, conversion, constant and <c>IOperation</c> tree of the innermost node at the caret.
/// </summary>
internal static class SyntaxVisualizerFeature
{
    /// <summary>Items sent before the tree is cut off away from the position (the path to it is always sent).</summary>
    internal const int MaxItems = 5000;

    private const int MaxText = 80;

    public static async Task<SyntaxTreeResult?> TreeAsync(Document document, int offset, CancellationToken cancellationToken)
    {
        if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } root)
        {
            return null;
        }

        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        offset = Math.Clamp(offset, 0, root.FullSpan.End);
        var token = root.FindToken(Math.Min(offset, Math.Max(0, root.FullSpan.End - 1)), findInsideTrivia: true);
        var path = new HashSet<SyntaxNodeOrToken>();
        for (var current = token; current != default;)
        {
            path.Add(current);
            var next = default(SyntaxToken);
            for (var node = current.Parent; node is not null; node = node.Parent)
            {
                path.Add(node);
                if (node.Parent is null && node.IsStructuredTrivia)
                {
                    next = ((IStructuredTriviaSyntax)node).ParentTrivia.Token; // out of a doc comment or directive
                }
            }

            current = next;
        }

        var budget = MaxItems;
        var tree = Build(root, text, offset, path, ref budget, cancellationToken);

        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var semantics = token.Parent is { } innermost && model is not null ? Semantics(model, innermost, cancellationToken) : null;
        return new SyntaxTreeResult(tree, semantics);
    }

    private static SyntaxNodeItem Build(SyntaxNodeOrToken item, SourceText text, int offset, HashSet<SyntaxNodeOrToken> path, ref int budget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        budget--;
        var onPath = path.Contains(item);
        var children = new List<SyntaxNodeItem>();
        var truncated = false;
        if (item.AsNode() is { } node)
        {
            foreach (var child in node.ChildNodesAndTokens())
            {
                if (budget <= 0 && !path.Contains(child))
                {
                    truncated = true;
                    continue;
                }

                children.Add(Build(child, text, offset, path, ref budget, cancellationToken));
            }
        }
        else
        {
            var token = item.AsToken();
            foreach (var trivia in token.LeadingTrivia.Concat(token.TrailingTrivia))
            {
                if (budget <= 0)
                {
                    truncated = true;
                    break;
                }

                budget--;
                children.Add(Trivia(trivia, text, offset, path, ref budget, cancellationToken));
            }
        }

        return new SyntaxNodeItem(item.Kind().ToString(), item.IsToken ? "token" : "node", Positions.ToRange(text, item.Span), [.. children])
        {
            Text = item.IsToken ? Shorten(item.AsToken().Text) : null,
            Truncated = truncated,
            OnPath = onPath,
        };
    }

    private static SyntaxNodeItem Trivia(SyntaxTrivia trivia, SourceText text, int offset, HashSet<SyntaxNodeOrToken> path, ref int budget, CancellationToken cancellationToken)
    {
        // Structured trivia (documentation comments, directives) has its own nodes.
        var children = trivia.GetStructure() is { } structure ? new[] { Build(structure, text, offset, path, ref budget, cancellationToken) } : [];
        return new SyntaxNodeItem(trivia.Kind().ToString(), "trivia", Positions.ToRange(text, trivia.Span), children)
        {
            Text = Shorten(trivia.ToString()),
            OnPath = trivia.FullSpan.Contains(offset) && children.Any(c => c.OnPath),
        };
    }

    private static SyntaxSemantics Semantics(SemanticModel model, SyntaxNode node, CancellationToken cancellationToken)
    {
        var symbol = model.GetSymbolInfo(node, cancellationToken).Symbol ?? model.GetDeclaredSymbol(node, cancellationToken);
        var type = model.GetTypeInfo(node, cancellationToken);
        var constant = model.GetConstantValue(node, cancellationToken);
        IOperation? operation = null;
        for (var current = node; current is not null && operation is null; current = current.Parent)
        {
            operation = model.GetOperation(current, cancellationToken);
        }

        return new SyntaxSemantics(node.Kind().ToString())
        {
            Symbol = symbol?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            SymbolKind = symbol?.Kind.ToString(),
            Type = type.Type?.ToDisplayString(),
            ConvertedType = type.ConvertedType?.ToDisplayString(),
            Conversion = type.Type is not null && model.GetConversion(node, cancellationToken) is { Exists: true } conversion ? Describe(conversion) : null,
            ConstantValue = constant.HasValue ? constant.Value?.ToString() ?? "null" : null,
            Operation = operation is null ? null : Operation(operation, 0),
        };
    }

    private static string Describe(Conversion conversion) =>
        conversion.IsIdentity ? "Identity"
        : conversion.IsImplicit ? "Implicit" + (conversion.IsNumeric ? " numeric" : conversion.IsReference ? " reference" : conversion.IsBoxing ? " boxing" : conversion.IsUserDefined ? " user-defined" : string.Empty)
        : "Explicit";

    private static OperationItem Operation(IOperation operation, int depth) =>
        new(operation.Kind.ToString(), operation.Type?.ToDisplayString(), Shorten(operation.Syntax.ToString()),
            depth > 30 ? [] : [.. operation.ChildOperations.Select(o => Operation(o, depth + 1))]);

    private static string Shorten(string text)
    {
        text = text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);
        return text.Length > MaxText ? text[..MaxText] + "…" : text;
    }
}
