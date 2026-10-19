using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Tags;
using RoslynCompletionItem = Microsoft.CodeAnalysis.Completion.CompletionItem;

namespace Codalus.LanguageServices.Host.Features;

/// <summary>
/// Target-typed completion (a classic IDE's "target type matches"): the completion items whose type converts to the
/// type expected where the item goes. Roslyn keeps its own flag (<c>CompletionItemFlags.MatchesTargetType</c>) and its
/// type inference service internal, so this works from the public semantic model: the expected type of an argument (the
/// active overload's parameter, as signature help picks it), an assignment's or initializer's target, a return value
/// (or an expression body), and an operand of <c>==</c> or <c>!=</c>. Items are found by name where they're completed
/// (after <c>x.</c>, among <c>x</c>'s members). C# only.
/// </summary>
internal static class TargetTyping
{
    /// <summary>
    /// For each item, whether its symbol's type converts to the expected type at <paramref name="spanStart"/> (the start
    /// of the text being completed); null when nothing is expected there or the document isn't C#.
    /// </summary>
    public static async Task<bool[]?> MatchAsync(Document document, int spanStart, IReadOnlyList<RoslynCompletionItem> items, CancellationToken cancellationToken)
    {
        if (document.Project.Language != LanguageNames.CSharp)
        {
            return null;
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null)
        {
            return null;
        }

        // After "x." the expression being completed starts at x, and its members are looked up in x's type.
        var left = LeftToken(root, spanStart);
        INamespaceOrTypeSymbol? container = null;
        var expressionStart = spanStart;
        if (left.IsKind(SyntaxKind.DotToken) && left.Parent is MemberAccessExpressionSyntax access)
        {
            var receiver = model.GetSymbolInfo(access.Expression, cancellationToken).Symbol;
            container = receiver as INamespaceOrTypeSymbol ?? model.GetTypeInfo(access.Expression, cancellationToken).Type;
            expressionStart = access.SpanStart;
            if (container is null)
            {
                return null;
            }
        }

        var (expected, excluded) = Expected(root, model, expressionStart, cancellationToken);
        if (expected.Count == 0)
        {
            return null;
        }

        var compilation = model.Compilation;
        var matches = new bool[items.Count];
        var any = false;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!IsValueKind(item.Tags))
            {
                continue;
            }

            foreach (var symbol in model.LookupSymbols(spanStart, container, item.DisplayText, includeReducedExtensionMethods: true))
            {
                if (SymbolEqualityComparer.Default.Equals(symbol, excluded) || TypeOf(symbol) is not { } type)
                {
                    continue;
                }

                if (expected.Any(target => compilation.ClassifyCommonConversion(type, target).IsImplicit))
                {
                    matches[i] = any = true;
                    break;
                }
            }
        }

        return any ? matches : null;
    }

    /// <summary>
    /// The types expected for an expression starting at <paramref name="position"/>, and the symbol being assigned or
    /// declared there (<c>int x = x</c> isn't a suggestion).
    /// </summary>
    internal static (List<ITypeSymbol> Types, ISymbol? Excluded) Expected(SyntaxNode root, SemanticModel model, int position, CancellationToken cancellationToken)
    {
        var types = new List<ITypeSymbol>();
        ISymbol? excluded = null;
        var left = LeftToken(root, position);
        switch (left.Kind())
        {
            case SyntaxKind.OpenParenToken or SyntaxKind.CommaToken or SyntaxKind.OpenBracketToken or SyntaxKind.ColonToken
                when left.Parent is BaseArgumentListSyntax or AttributeArgumentListSyntax or NameColonSyntax:
                if (SignatureHelpFeature.FindCall(root, model, position, cancellationToken) is { } call)
                {
                    var method = call.Method;
                    var index = call.ParameterIndex(method);
                    if (index < method.Parameters.Length)
                    {
                        var parameter = method.Parameters[index];
                        types.Add(parameter.Type);
                        if (parameter.IsParams && parameter.Type is IArrayTypeSymbol array)
                        {
                            types.Add(array.ElementType);
                        }
                    }
                }

                break;

            case SyntaxKind.EqualsToken when left.Parent is AssignmentExpressionSyntax assignment && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression):
                Add(model.GetTypeInfo(assignment.Left, cancellationToken).Type);
                excluded = model.GetSymbolInfo(assignment.Left, cancellationToken).Symbol;
                break;

            case SyntaxKind.EqualsToken when left.Parent is EqualsValueClauseSyntax clause:
                switch (clause.Parent)
                {
                    case VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } declarator when !declaration.Type.IsVar:
                        Add(model.GetTypeInfo(declaration.Type, cancellationToken).Type);
                        excluded = model.GetDeclaredSymbol(declarator, cancellationToken);
                        break;
                    case PropertyDeclarationSyntax property:
                        Add(model.GetTypeInfo(property.Type, cancellationToken).Type);
                        break;
                    case ParameterSyntax { Type: { } parameterType }:
                        Add(model.GetTypeInfo(parameterType, cancellationToken).Type);
                        break;
                }

                break;

            case SyntaxKind.ReturnKeyword when left.Parent is ReturnStatementSyntax:
            case SyntaxKind.EqualsGreaterThanToken when left.Parent is ArrowExpressionClauseSyntax:
                Add(model.GetEnclosingSymbol(position, cancellationToken) switch
                {
                    IMethodSymbol { ReturnsVoid: false } method => method.IsAsync ? Awaited(method.ReturnType) : method.ReturnType,
                    IPropertySymbol property => property.Type,
                    _ => null,
                });
                break;

            case SyntaxKind.EqualsEqualsToken or SyntaxKind.ExclamationEqualsToken when left.Parent is BinaryExpressionSyntax binary && binary.OperatorToken == left:
                Add(model.GetTypeInfo(binary.Left, cancellationToken).Type);
                break;
        }

        return (types, excluded);

        void Add(ITypeSymbol? type)
        {
            // Anything converts to object: no type to match.
            if (type is { TypeKind: not TypeKind.Error, SpecialType: not (SpecialType.System_Object or SpecialType.System_Void) })
            {
                types.Add(type);
            }
        }
    }

    /// <summary>An async method's result: <c>T</c> of <c>Task&lt;T&gt;</c> or <c>ValueTask&lt;T&gt;</c>; null for <c>Task</c>.</summary>
    private static ITypeSymbol? Awaited(ITypeSymbol type) => type is INamedTypeSymbol { Name: "Task" or "ValueTask", TypeArguments: [var result] } ? result : null;

    /// <summary>The token before <paramref name="position"/> (skipping the whitespace between them).</summary>
    private static SyntaxToken LeftToken(SyntaxNode root, int position)
    {
        var token = root.FindToken(Math.Max(0, position - 1));
        return token.SpanStart >= position ? token.GetPreviousToken() : token;
    }

    /// <summary>Items that stand for a value (not types, namespaces or keywords).</summary>
    private static bool IsValueKind(ImmutableArray<string> tags) => tags.Any(t => t is WellKnownTags.Local or WellKnownTags.Parameter or WellKnownTags.RangeVariable
        or WellKnownTags.Field or WellKnownTags.Property or WellKnownTags.Constant or WellKnownTags.EnumMember or WellKnownTags.Method or WellKnownTags.ExtensionMethod);

    /// <summary>A value's type: a variable's, a member's, or a method's return type.</summary>
    private static ITypeSymbol? TypeOf(ISymbol symbol) => symbol switch
    {
        ILocalSymbol local => local.Type,
        IParameterSymbol parameter => parameter.Type,
        IRangeVariableSymbol => null,
        IFieldSymbol field => field.Type,
        IPropertySymbol property => property.Type,
        IMethodSymbol { ReturnsVoid: false, MethodKind: MethodKind.Ordinary or MethodKind.ReducedExtension } method => method.ReturnType,
        _ => null,
    };
}
