using System.Xml.Linq;
using Daedalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Daedalus.LanguageServices.Host.Features;

/// <summary>
/// Signature help. Roslyn's own providers are internal (ADR 0005), so this finds the enclosing argument list (calls,
/// object creation, constructor initializers, attributes, indexers), lists the accessible overloads from the
/// semantic model, and picks the active parameter by position or by name.
/// </summary>
internal static class SignatureHelpFeature
{
    private static readonly SymbolDisplayFormat s_parameterFormat = SymbolDisplayFormat.MinimallyQualifiedFormat
        .WithParameterOptions(SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName
                              | SymbolDisplayParameterOptions.IncludeDefaultValue | SymbolDisplayParameterOptions.IncludeParamsRefOut
                              | SymbolDisplayParameterOptions.IncludeExtensionThis);

    private static readonly SymbolDisplayFormat s_typeFormat = SymbolDisplayFormat.MinimallyQualifiedFormat;

    public static SignatureHelpOptions Options => new(TriggerCharacters: ["(", ",", "["], RetriggerCharacters: [")", "]"]);

    public static async Task<SignatureHelp?> SignatureHelpAsync(Document document, int position, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null)
        {
            return null;
        }

        var token = root.FindToken(Math.Max(0, position - 1));
        foreach (var node in token.Parent?.AncestorsAndSelf() ?? [])
        {
            if (Target(node, position) is not { } target)
            {
                continue;
            }

            var candidates = Candidates(model, target.Node, position, cancellationToken);
            if (candidates.Count == 0)
            {
                return null;
            }

            var argumentIndex = target.Separators.Count(s => s.SpanStart < position);
            var name = target.Arguments.ElementAtOrDefault(argumentIndex) is ArgumentSyntax { NameColon.Name.Identifier.ValueText: var n } ? n : null;
            var chosen = model.GetSymbolInfo(target.Node, cancellationToken).Symbol;
            var argumentCount = Math.Max(target.Arguments.Count, argumentIndex + 1);
            var active = chosen is not null ? candidates.FindIndex(c => SymbolEqualityComparer.Default.Equals(c, chosen.OriginalDefinition) || SymbolEqualityComparer.Default.Equals(c, chosen)) : -1;
            if (active < 0)
            {
                active = Math.Max(0, candidates.FindIndex(c => c.Parameters.Length >= argumentCount || c.Parameters.LastOrDefault()?.IsParams == true));
            }

            var signatures = candidates.Select(c => Signature(c, model, position, ParameterIndex(c, argumentIndex, name))).ToArray();
            return new SignatureHelp(signatures, active, signatures[active].ActiveParameter ?? 0);
        }

        return null;
    }

    private sealed record ArgumentTarget(SyntaxNode Node, IReadOnlyList<SyntaxNode> Arguments, IReadOnlyList<SyntaxToken> Separators);

    /// <summary>The call-like node whose argument list contains <paramref name="position"/> (after its opening token).</summary>
    private static ArgumentTarget? Target(SyntaxNode node, int position)
    {
        static bool Inside(SyntaxToken open, SyntaxToken close, int position) =>
            position > open.SpanStart && (close.IsMissing || position <= close.SpanStart);

        return node switch
        {
            InvocationExpressionSyntax { ArgumentList: var list } when Inside(list.OpenParenToken, list.CloseParenToken, position) =>
                new ArgumentTarget(node, list.Arguments, list.Arguments.GetSeparators().ToList()),
            BaseObjectCreationExpressionSyntax { ArgumentList: { } list } when Inside(list.OpenParenToken, list.CloseParenToken, position) =>
                new ArgumentTarget(node, list.Arguments, list.Arguments.GetSeparators().ToList()),
            ConstructorInitializerSyntax { ArgumentList: var list } when Inside(list.OpenParenToken, list.CloseParenToken, position) =>
                new ArgumentTarget(node, list.Arguments, list.Arguments.GetSeparators().ToList()),
            PrimaryConstructorBaseTypeSyntax { ArgumentList: var list } when Inside(list.OpenParenToken, list.CloseParenToken, position) =>
                new ArgumentTarget(node, list.Arguments, list.Arguments.GetSeparators().ToList()),
            ElementAccessExpressionSyntax { ArgumentList: var list } when Inside(list.OpenBracketToken, list.CloseBracketToken, position) =>
                new ArgumentTarget(node, list.Arguments, list.Arguments.GetSeparators().ToList()),
            AttributeSyntax { ArgumentList: { } list } when Inside(list.OpenParenToken, list.CloseParenToken, position) =>
                new ArgumentTarget(node, list.Arguments, list.Arguments.GetSeparators().ToList()),
            _ => null,
        };
    }

    private static List<IMethodSymbol> Candidates(SemanticModel model, SyntaxNode node, int position, CancellationToken cancellationToken)
    {
        IEnumerable<ISymbol> symbols = node switch
        {
            InvocationExpressionSyntax invocation => model.GetMemberGroup(invocation.Expression, cancellationToken),
            BaseObjectCreationExpressionSyntax creation => (model.GetTypeInfo(creation, cancellationToken).Type as INamedTypeSymbol)?.InstanceConstructors ?? [],
            ConstructorInitializerSyntax initializer => model.GetMemberGroup(initializer, cancellationToken),
            PrimaryConstructorBaseTypeSyntax baseType => (model.GetTypeInfo(baseType.Type, cancellationToken).Type as INamedTypeSymbol)?.InstanceConstructors ?? [],
            ElementAccessExpressionSyntax access => model.GetTypeInfo(access.Expression, cancellationToken).Type?.GetMembers()
                .OfType<IPropertySymbol>().Where(p => p.IsIndexer).Select(p => p.GetMethod).OfType<IMethodSymbol>() ?? [],
            AttributeSyntax attribute => (model.GetTypeInfo(attribute, cancellationToken).Type as INamedTypeSymbol)?.InstanceConstructors ?? [],
            _ => [],
        };

        return [.. symbols.OfType<IMethodSymbol>()
            .Where(m => model.IsAccessible(position, m) && !m.GetAttributes().Any(a => a.AttributeClass?.Name == "EditorBrowsableAttribute" && a.ConstructorArguments is [{ Value: 1 }]))
            .Select(m => m.OriginalDefinition)
            .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default)
            .OrderBy(m => m.Parameters.Length)];
    }

    private static int ParameterIndex(IMethodSymbol method, int argumentIndex, string? name)
    {
        if (name is not null)
        {
            var named = method.Parameters.IndexOf(method.Parameters.FirstOrDefault(p => p.Name == name)!);
            if (named >= 0)
            {
                return named;
            }
        }

        if (argumentIndex < method.Parameters.Length)
        {
            return argumentIndex;
        }

        return method.Parameters.LastOrDefault()?.IsParams == true ? method.Parameters.Length - 1 : argumentIndex;
    }

    private static SignatureInformation Signature(IMethodSymbol method, SemanticModel model, int position, int activeParameter)
    {
        var indexer = method.AssociatedSymbol is IPropertySymbol { IsIndexer: true } property ? property : null;
        string prefix;
        if (indexer is not null)
        {
            prefix = indexer.Type.ToMinimalDisplayString(model, position, s_typeFormat) + " this[";
        }
        else if (method.MethodKind == MethodKind.Constructor)
        {
            prefix = method.ContainingType.ToMinimalDisplayString(model, position, s_typeFormat) + "(";
        }
        else
        {
            var name = method.ToMinimalDisplayString(model, position, SymbolDisplayFormat.MinimallyQualifiedFormat
                .WithMemberOptions(SymbolDisplayMemberOptions.IncludeContainingType)
                .WithParameterOptions(SymbolDisplayParameterOptions.None));
            name = name.EndsWith("()", StringComparison.Ordinal) ? name[..^2] : name;
            prefix = method.ReturnType.ToMinimalDisplayString(model, position, s_typeFormat) + " " + name + "(";
        }

        var docs = Documentation(method);
        var parameters = method.Parameters
            .Select(p => new ParameterInformation(p.ToMinimalDisplayString(model, position, s_parameterFormat), docs.Parameters.TryGetValue(p.Name, out var doc) ? MarkupContent.Markdown(Markdown.Escape(doc)) : null))
            .ToArray();
        var label = prefix + string.Join(", ", parameters.Select(p => p.Label)) + (indexer is not null ? "]" : ")");
        return new SignatureInformation(label, docs.Summary is { } summary ? MarkupContent.Markdown(Markdown.Escape(summary)) : null, parameters)
        {
            ActiveParameter = parameters.Length == 0 ? null : Math.Min(activeParameter, parameters.Length - 1),
        };
    }

    private static (string? Summary, Dictionary<string, string> Parameters) Documentation(IMethodSymbol method)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        var xml = (method.AssociatedSymbol ?? method).GetDocumentationCommentXml(expandIncludes: true);
        if (string.IsNullOrWhiteSpace(xml))
        {
            return (null, parameters);
        }

        try
        {
            var element = XElement.Parse(xml);
            foreach (var param in element.Elements("param"))
            {
                if (param.Attribute("name")?.Value is { } name)
                {
                    parameters[name] = Text(param);
                }
            }

            return (element.Element("summary") is { } summary ? Text(summary) : null, parameters);
        }
        catch (System.Xml.XmlException)
        {
            return (null, parameters);
        }
    }

    /// <summary>Documentation text with <c>&lt;see cref&gt;</c> and <c>&lt;paramref&gt;</c> reduced to their names.</summary>
    private static string Text(XElement element)
    {
        var parts = element.Nodes().Select(n => n switch
        {
            XText t => t.Value,
            XElement { Name.LocalName: "see" or "seealso" } e => (e.Attribute("cref")?.Value ?? e.Attribute("langword")?.Value ?? e.Value).Split(':').Last(),
            XElement { Name.LocalName: "paramref" or "typeparamref" } e => e.Attribute("name")?.Value ?? string.Empty,
            XElement e => Text(e),
            _ => string.Empty,
        });
        return string.Join(' ', string.Concat(parts).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
