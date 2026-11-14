using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Codalus.LanguageServices.Host.Features;

/// <summary>
/// LSP 3.17's <c>textDocument/inlineValue</c> for C# and Visual Basic (M62, ADR 0101): for each line of the stopped
/// method up to the stop, the variables it reads or writes, found through Roslyn's operations, so one code path serves
/// both languages. Locals and parameters are variable lookups (the debugger's scopes already hold them); fields, and
/// auto-properties (whose generated getter only reads their field), are evaluatable expressions. Other properties,
/// calls and other objects' members are left to the data tip, so drawing the values never runs the user's code.
/// </summary>
internal static class InlineValuesFeature
{
    /// <summary>At most this many lines above the stop are read (a long method's top is off screen anyway).</summary>
    public const int MaxLines = 100;

    public static async Task<InlineValue[]> ValuesAsync(Document document, TextSpan range, TextSpan stopped, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null || stopped.Start > text.Length)
        {
            return [];
        }

        var body = Body(root.FindToken(stopped.Start).Parent, model, cancellationToken);
        if (body is null)
        {
            return [];
        }

        // The method's lines from its start (or MaxLines above the stop) to the end of the stop's line, inside the range.
        var stopLine = text.Lines.GetLineFromPosition(stopped.Start);
        var firstLine = Math.Max(text.Lines.GetLineFromPosition(body.SpanStart).LineNumber, stopLine.LineNumber - MaxLines);
        var start = Math.Max(Math.Max(text.Lines[firstLine].Start, body.SpanStart), range.Start);
        var end = Math.Min(Math.Min(stopLine.End, body.Span.End), range.End);
        if (end <= start)
        {
            return [];
        }

        var span = TextSpan.FromBounds(start, end);
        var values = new List<InlineValue>();
        var seen = new HashSet<(int Line, string Name)>();
        foreach (var node in body.DescendantNodes(span))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!span.Contains(node.Span) || Value(model.GetOperation(node, cancellationToken), node, text) is not { } value)
            {
                continue;
            }

            var line = value.Range.Start.Line;
            if (seen.Add((line, value.VariableName ?? value.Expression ?? "")))
            {
                values.Add(value);
            }
        }

        return [.. values.OrderBy(v => v.Range.Start.Line).ThenBy(v => v.Range.Start.Character)];
    }

    /// <summary>The innermost method-like node around the stop: a method, accessor, local function, lambda or top-level code.</summary>
    private static SyntaxNode? Body(SyntaxNode? node, SemanticModel model, CancellationToken cancellationToken)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            if (model.GetDeclaredSymbol(current, cancellationToken) is IMethodSymbol
                || model.GetOperation(current, cancellationToken) is IAnonymousFunctionOperation)
            {
                return current;
            }

            if (current.Parent is null)
            {
                return current; // top-level statements: the compilation unit
            }
        }

        return null;
    }

    private static InlineValue? Value(IOperation? operation, SyntaxNode node, SourceText text)
    {
        switch (operation)
        {
            case ILocalReferenceOperation { Local: { IsImplicitlyDeclared: false } local } when !IsDiscard(local.Name):
                return InlineValue.Lookup(Positions.ToRange(text, node.Span), local.Name, CaseSensitive(node));
            case IParameterReferenceOperation { Parameter: { } parameter } when !IsDiscard(parameter.Name):
                return InlineValue.Lookup(Positions.ToRange(text, node.Span), parameter.Name, CaseSensitive(node));
            case IVariableDeclaratorOperation { Symbol: { IsImplicitlyDeclared: false } declared } when declared.Locations is [{ IsInSource: true } location, ..] && !IsDiscard(declared.Name):
                return InlineValue.Lookup(Positions.ToRange(text, location.SourceSpan), declared.Name, CaseSensitive(node));
            case IFieldReferenceOperation { Field: { IsConst: false, IsImplicitlyDeclared: false } field } reference when OnThisOrStatic(reference.Instance):
                return InlineValue.Evaluate(Positions.ToRange(text, NameSpan(node)), Expression(field, reference.Instance));
            case IPropertyReferenceOperation { Property: { Parameters.Length: 0 } property } reference when OnThisOrStatic(reference.Instance) && IsAutoProperty(property):
                return InlineValue.Evaluate(Positions.ToRange(text, NameSpan(node)), Expression(property, reference.Instance));
            default:
                return null;
        }
    }

    /// <summary>The member's own name in <c>this.x</c> or <c>Type.x</c>, so the value is drawn at the name.</summary>
    private static TextSpan NameSpan(SyntaxNode node) => node.ChildNodes().LastOrDefault() is { } name && !name.ChildNodes().Any() ? name.Span : node.Span;

    /// <summary>A field or auto-property of this object, or a static one: never another object's (its expression may call code).</summary>
    private static bool OnThisOrStatic(IOperation? instance) => instance is null or IInstanceReferenceOperation;

    /// <summary>An instance member by its simple name (the frame's <c>this</c> resolves it), a static one by its type.</summary>
    private static string Expression(ISymbol member, IOperation? instance) =>
        instance is null && member.IsStatic && member.ContainingType is { } type
            ? type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) + "." + member.Name
            : member.Name;

    /// <summary>An auto-property: its type has the compiler's backing field for it, so its getter only reads that field.</summary>
    private static bool IsAutoProperty(IPropertySymbol property) =>
        property.ContainingType?.GetMembers().OfType<IFieldSymbol>().Any(f => f.IsImplicitlyDeclared && SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, property)) == true;

    private static bool IsDiscard(string name) => name.Length == 0 || name == "_";

    /// <summary>Visual Basic's names are case-insensitive.</summary>
    private static bool CaseSensitive(SyntaxNode node) => node.Language != LanguageNames.VisualBasic;
}
