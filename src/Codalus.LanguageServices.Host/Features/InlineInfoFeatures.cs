using System.Text.Json;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Location = Codalus.LanguageServices.Protocol.Location;

namespace Codalus.LanguageServices.Host.Features;

/// <summary>
/// Inlay hints for a range (M12): parameter names before literal, <c>null</c>, <c>default</c> and <c>new</c> arguments,
/// and types after <c>var</c> declarations and before untyped lambda parameters, when the type isn't obvious from the
/// right-hand side. Hints that would repeat what the code says are suppressed (as the classic IDE does).
/// </summary>
internal static class InlayHintsFeature
{
    private static readonly SymbolDisplayFormat s_typeFormat = SymbolDisplayFormat.MinimallyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static async Task<InlayHint[]> HintsAsync(Document document, TextSpan span, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null)
        {
            return [];
        }

        var hints = new List<InlayHint>();
        foreach (var node in root.DescendantNodes(span))
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (node)
            {
                case ArgumentSyntax { NameColon: null, Parent: BaseArgumentListSyntax list } argument when span.Contains(argument.SpanStart) && ShowsParameterName(argument.Expression):
                    if (Parameter(model, argument, list, cancellationToken) is { } parameter && !Suppressed(parameter, argument, list))
                    {
                        hints.Add(new InlayHint(Positions.ToPosition(text, argument.SpanStart), parameter.Name + ":")
                        {
                            Kind = InlayHintKind.Parameter,
                            PaddingRight = true,
                        });
                    }

                    break;
                case VariableDeclarationSyntax { Type.IsVar: true, Variables: [{ Initializer.Value: var value } variable] } when span.Contains(variable.Identifier.SpanStart) && !Apparent(value):
                    if (model.GetDeclaredSymbol(variable, cancellationToken) is ILocalSymbol { Type: { } type } && type is not IErrorTypeSymbol)
                    {
                        hints.Add(TypeHint(text, variable.Identifier.Span.End, type, after: true));
                    }

                    break;
                case ForEachStatementSyntax { Type.IsVar: true } loop when span.Contains(loop.Identifier.SpanStart):
                    if (model.GetDeclaredSymbol(loop, cancellationToken) is ILocalSymbol { Type: { } loopType } && loopType is not IErrorTypeSymbol)
                    {
                        hints.Add(TypeHint(text, loop.Identifier.Span.End, loopType, after: true));
                    }

                    break;
                case SingleVariableDesignationSyntax { Parent: DeclarationExpressionSyntax { Type.IsVar: true } or ParenthesizedVariableDesignationSyntax } designation when span.Contains(designation.SpanStart):
                    if (model.GetDeclaredSymbol(designation, cancellationToken) is ILocalSymbol { Type: { } designationType } && designationType is not IErrorTypeSymbol)
                    {
                        hints.Add(TypeHint(text, designation.Span.End, designationType, after: true));
                    }

                    break;
                case ParameterSyntax { Type: null, Parent: ParameterListSyntax { Parent: ParenthesizedLambdaExpressionSyntax } or null } lambdaParameter when span.Contains(lambdaParameter.SpanStart):
                    if (model.GetDeclaredSymbol(lambdaParameter, cancellationToken) is IParameterSymbol { Type: { } parameterType } && parameterType is not IErrorTypeSymbol)
                    {
                        hints.Add(TypeHint(text, lambdaParameter.SpanStart, parameterType, after: false));
                    }

                    break;
            }
        }

        return [.. hints.OrderBy(h => h.Position.Line).ThenBy(h => h.Position.Character)];
    }

    private static InlayHint TypeHint(SourceText text, int offset, ITypeSymbol type, bool after) =>
        new(Positions.ToPosition(text, offset), after ? ": " + type.ToDisplayString(s_typeFormat) : type.ToDisplayString(s_typeFormat))
        {
            Kind = InlayHintKind.Type,
            PaddingRight = !after,
        };

    private static bool ShowsParameterName(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax or DefaultExpressionSyntax or BaseObjectCreationExpressionSyntax => true,
        PrefixUnaryExpressionSyntax { Operand: LiteralExpressionSyntax } => true, // -1
        CastExpressionSyntax { Expression: LiteralExpressionSyntax } => true,
        _ => false,
    };

    private static IParameterSymbol? Parameter(SemanticModel model, ArgumentSyntax argument, BaseArgumentListSyntax list, CancellationToken cancellationToken)
    {
        if (list.Parent is null || model.GetSymbolInfo(list.Parent, cancellationToken).Symbol is not { } symbol)
        {
            return null;
        }

        var parameters = symbol switch
        {
            IMethodSymbol method => method.Parameters,
            IPropertySymbol property => property.Parameters,
            _ => [],
        };
        var index = list.Arguments.IndexOf(argument);
        if (index < 0 || parameters.Length == 0)
        {
            return null;
        }

        return index < parameters.Length ? parameters[index] : parameters[^1].IsParams ? parameters[^1] : null;
    }

    /// <summary>A single parameter whose name the method name already says (<c>SetName("x")</c>), or <c>arg0</c>-style names.</summary>
    private static bool Suppressed(IParameterSymbol parameter, ArgumentSyntax argument, BaseArgumentListSyntax list)
    {
        if (parameter.Name.Length <= 1 || (parameter.Name.StartsWith("arg", StringComparison.Ordinal) && parameter.Name[3..].All(char.IsDigit)))
        {
            return true;
        }

        if (parameter.ContainingSymbol is IMethodSymbol method && list.Arguments.Count == 1)
        {
            if (method.Name.EndsWith(parameter.Name, StringComparison.OrdinalIgnoreCase)
                || method.MethodKind is MethodKind.PropertySet || parameter.Name is "value" or "obj" or "item")
            {
                return true;
            }
        }

        return argument.Expression is ObjectCreationExpressionSyntax { Type: var type }
            && type.ToString().EndsWith(parameter.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The type is written on the right: <c>new T()</c>, a cast, <c>as T</c>, <c>default(T)</c> or a literal.</summary>
    private static bool Apparent(ExpressionSyntax value) => value switch
    {
        ObjectCreationExpressionSyntax or ArrayCreationExpressionSyntax or CastExpressionSyntax or DefaultExpressionSyntax or LiteralExpressionSyntax => true,
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AsExpression) => true,
        AwaitExpressionSyntax { Expression: var awaited } => Apparent(awaited),
        _ => false,
    };
}

/// <summary>
/// CodeLens (M12): reference counts above types and members and implementation counts for interfaces, abstract and
/// virtual members. <c>textDocument/codeLens</c> only lists the declarations (syntax); <c>codeLens/resolve</c> counts,
/// and the editor resolves only the lenses in view.
/// </summary>
internal static class CodeLensFeature
{
    public const string ReferencesCommand = "editor.action.showReferences";
    public const string ImplementationsCommand = "editor.action.goToImplementation";

    private sealed record LensData(string Uri, int Line, int Character, string Kind);

    public static async Task<CodeLens[]> LensesAsync(Document document, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return [];
        }

        var uri = WorkspaceManager.UriOf(document);
        var lenses = new List<CodeLens>();
        foreach (var node in root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax))
        {
            var identifier = node switch
            {
                BaseTypeDeclarationSyntax t => t.Identifier,
                DelegateDeclarationSyntax d => d.Identifier,
                MethodDeclarationSyntax m => m.Identifier,
                ConstructorDeclarationSyntax c => c.Identifier,
                PropertyDeclarationSyntax p => p.Identifier,
                IndexerDeclarationSyntax i => i.ThisKeyword,
                EventDeclarationSyntax e => e.Identifier,
                _ => default,
            };
            if (identifier == default)
            {
                continue;
            }

            var position = Positions.ToPosition(text, identifier.SpanStart);
            var range = Positions.ToRange(text, identifier.Span);
            lenses.Add(Lens(range, new LensData(uri, position.Line, position.Character, "references")));
            var canBeImplemented = node is InterfaceDeclarationSyntax
                || node.Parent is InterfaceDeclarationSyntax
                || (node is MemberDeclarationSyntax member && member.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword) || m.IsKind(SyntaxKind.VirtualKeyword)))
                || (node is ClassDeclarationSyntax cls && cls.Modifiers.Any(SyntaxKind.AbstractKeyword));
            if (canBeImplemented)
            {
                lenses.Add(Lens(range, new LensData(uri, position.Line, position.Character, "implementations")));
            }
        }

        return [.. lenses];
    }

    private static CodeLens Lens(Protocol.Range range, LensData data) =>
        new(range) { Data = JsonSerializer.SerializeToElement(new[] { data.Uri, data.Line.ToString(System.Globalization.CultureInfo.InvariantCulture), data.Character.ToString(System.Globalization.CultureInfo.InvariantCulture), data.Kind }, ProtocolJsonContext.Default.StringArray) };

    public static async Task<CodeLens> ResolveAsync(Solution solution, CodeLens lens, CancellationToken cancellationToken)
    {
        if (lens.Data is not { ValueKind: JsonValueKind.Array } data || data.GetArrayLength() != 4
            || await WorkspaceManager.FindDocumentAsync(solution, data[0].GetString()!, cancellationToken).ConfigureAwait(false) is not { } document)
        {
            return lens with { Command = new Command("-", string.Empty) };
        }

        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var position = new Position(int.Parse(data[1].GetString()!, System.Globalization.CultureInfo.InvariantCulture), int.Parse(data[2].GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        var offset = Positions.ToOffset(text, position);
        var arguments = new[]
        {
            JsonSerializer.SerializeToElement(data[0].GetString()!, ProtocolJsonContext.Default.String),
            JsonSerializer.SerializeToElement(position, ProtocolJsonContext.Default.Position),
        };
        if (await NavigationFeature.SymbolAtAsync(document, offset, cancellationToken).ConfigureAwait(false) is not { } symbol)
        {
            return lens with { Command = new Command("-", string.Empty) };
        }

        if (data[3].GetString() == "implementations")
        {
            var count = symbol switch
            {
                INamedTypeSymbol { TypeKind: TypeKind.Interface } type => (await SymbolFinder.FindImplementationsAsync(type, solution, transitive: true, cancellationToken: cancellationToken).ConfigureAwait(false)).Count(),
                INamedTypeSymbol type => (await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: true, cancellationToken: cancellationToken).ConfigureAwait(false)).Count(),
                _ when symbol.ContainingType?.TypeKind == TypeKind.Interface => (await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: cancellationToken).ConfigureAwait(false)).Count(),
                _ => (await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: cancellationToken).ConfigureAwait(false)).Count(),
            };
            return lens with { Command = new Command(count == 1 ? "1 implementation" : $"{count} implementations", ImplementationsCommand) { Arguments = arguments } };
        }

        var references = 0;
        foreach (var referenced in await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken).ConfigureAwait(false))
        {
            references += referenced.Locations.Count(l => !l.IsImplicit && !l.IsCandidateLocation);
        }

        return lens with { Command = new Command(references == 1 ? "1 reference" : $"{references} references", ReferencesCommand) { Arguments = arguments } };
    }
}

/// <summary>
/// <c>codalus/references</c>: Find All References with what the results window groups and filters by: read, write,
/// declaration or name-only use, the containing member, the project and the line's text.
/// </summary>
internal static class ReferenceItemsFeature
{
    public static async Task<ReferenceItemsResult?> ReferencesAsync(Document document, int position, CancellationToken cancellationToken)
    {
        if (await NavigationFeature.SymbolAtAsync(document, position, cancellationToken).ConfigureAwait(false) is not { } symbol)
        {
            return null;
        }

        var solution = document.Project.Solution;
        var items = new List<ReferenceItem>();
        foreach (var referenced in await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken).ConfigureAwait(false))
        {
            foreach (var definition in referenced.Definition.Locations.Where(l => l.IsInSource && l.SourceTree is not null))
            {
                if (solution.GetDocument(definition.SourceTree!) is { } definitionDocument)
                {
                    items.Add(await ItemAsync(definitionDocument, definition.SourceSpan, ReferenceItemKinds.Declaration, cancellationToken).ConfigureAwait(false));
                }
            }

            foreach (var reference in referenced.Locations.Where(l => !l.IsImplicit))
            {
                var root = await reference.Location.SourceTree!.GetRootAsync(cancellationToken).ConfigureAwait(false);
                var kind = symbol is INamespaceOrTypeSymbol || IsNameOnly(root, reference.Location.SourceSpan) ? ReferenceItemKinds.Reference
                    : IsWrite(root, reference.Location.SourceSpan) ? ReferenceItemKinds.Write
                    : ReferenceItemKinds.Read;
                items.Add(await ItemAsync(reference.Document, reference.Location.SourceSpan, kind, cancellationToken).ConfigureAwait(false));
            }
        }

        return new ReferenceItemsResult(
            symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor ? constructor.ContainingType.Name : symbol.Name,
            [.. items.DistinctBy(i => i.Location).OrderBy(i => i.Location.Uri, StringComparer.Ordinal).ThenBy(i => i.Location.Range.Start.Line).ThenBy(i => i.Location.Range.Start.Character)]);
    }

    /// <summary>The target of an assignment, <c>++</c>/<c>--</c>, or an <c>out</c>/<c>ref</c> argument.</summary>
    internal static bool IsWrite(SyntaxNode root, TextSpan span)
    {
        SyntaxNode node = root.FindNode(span, getInnermostNodeForTie: true);
        while (node.Parent is MemberAccessExpressionSyntax access && access.Name == node
               || node.Parent is ConditionalAccessExpressionSyntax conditional && conditional.WhenNotNull == node
               || node.Parent is MemberBindingExpressionSyntax binding && binding.Name == node
               || node.Parent is ParenthesizedExpressionSyntax
               || node.Parent is ElementAccessExpressionSyntax element && element.Expression == node)
        {
            node = node.Parent;
        }

        return node.Parent switch
        {
            AssignmentExpressionSyntax assignment => assignment.Left == node || assignment.Left.Span.Contains(node.Span) && assignment.Left is TupleExpressionSyntax,
            PrefixUnaryExpressionSyntax prefix => prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression),
            PostfixUnaryExpressionSyntax postfix => postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression),
            ArgumentSyntax argument => argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) || argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword),
            ArgumentSyntax or TupleExpressionSyntax when node.Parent?.Parent is TupleExpressionSyntax { Parent: AssignmentExpressionSyntax tupleAssignment } tuple && tupleAssignment.Left == tuple => true,
            _ => false,
        };
    }

    private static bool IsNameOnly(SyntaxNode root, TextSpan span) =>
        root.FindNode(span, getInnermostNodeForTie: true).AncestorsAndSelf().Any(n =>
            n is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } } or TypeOfExpressionSyntax or AttributeSyntax or CrefSyntax);

    private static async Task<ReferenceItem> ItemAsync(Document document, TextSpan span, string kind, CancellationToken cancellationToken)
    {
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var container = model?.GetEnclosingSymbol(span.Start, cancellationToken) switch
        {
            null or INamespaceSymbol => null,
            IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction } local => local.ContainingSymbol,
            var enclosing => enclosing,
        };
        return new ReferenceItem(
            new Location(WorkspaceManager.UriOf(document), Positions.ToRange(text, span)),
            kind,
            container?.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
            document.Project.Name,
            text.Lines.GetLineFromPosition(span.Start).ToString().Trim());
    }
}
