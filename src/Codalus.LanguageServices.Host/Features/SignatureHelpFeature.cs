using System.Xml.Linq;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using VB = Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Codalus.LanguageServices.Host.Features;

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

    /// <summary>A method's containing type and name (with its type parameters), without its parameters: <c>Type&lt;A&gt;.Name&lt;T&gt;</c>.</summary>
    private static readonly SymbolDisplayFormat s_nameFormat = SymbolDisplayFormat.MinimallyQualifiedFormat
        .WithMemberOptions(SymbolDisplayMemberOptions.IncludeContainingType)
        .WithParameterOptions(SymbolDisplayParameterOptions.None);

    public static SignatureHelpOptions Options => new(TriggerCharacters: ["(", ",", "["], RetriggerCharacters: [")", "]"]);

    public static async Task<SignatureHelp?> SignatureHelpAsync(Document document, int position, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null)
        {
            return null;
        }

        if (FindCall(root, model, position, cancellationToken) is not { } call)
        {
            return null;
        }

        var signatures = call.Candidates.Select(c => Signature(c, model, position, call.ParameterIndex(c))).ToArray();
        return new SignatureHelp(signatures, call.Active, signatures[call.Active].ActiveParameter ?? 0);
    }

    /// <summary>
    /// The call whose argument list holds a position: its overloads, the active one (as signature help picks it: the one
    /// the call binds to, else the first with enough parameters), and the argument's index and name.
    /// </summary>
    internal sealed record ActiveCall(List<IMethodSymbol> Candidates, int Active, int ArgumentIndex, string? Name)
    {
        public IMethodSymbol Method => Candidates[Active];

        /// <summary>The parameter of <paramref name="method"/> the argument goes to (the params array for extra arguments).</summary>
        public int ParameterIndex(IMethodSymbol method) => SignatureHelpFeature.ParameterIndex(method, ArgumentIndex, Name);
    }

    /// <summary>The innermost call whose argument list holds <paramref name="position"/>, or null (none, or no overloads).</summary>
    internal static ActiveCall? FindCall(SyntaxNode root, SemanticModel model, int position, CancellationToken cancellationToken)
    {
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
            var name = target.Arguments.ElementAtOrDefault(argumentIndex) switch
            {
                ArgumentSyntax { NameColon.Name.Identifier.ValueText: var n } => n,
                VB.SimpleArgumentSyntax { NameColonEquals.Name.Identifier.ValueText: var vbName } => vbName,
                _ => null,
            };
            var chosen = model.GetSymbolInfo(target.Node, cancellationToken).Symbol;
            var argumentCount = Math.Max(target.Arguments.Count, argumentIndex + 1);
            var active = chosen is IMethodSymbol picked ? candidates.FindIndex(c => SymbolEqualityComparer.Default.Equals(Definition(c), Definition(picked))) : -1;
            if (active < 0)
            {
                active = Math.Max(0, candidates.FindIndex(c => c.Parameters.Length >= argumentCount || c.Parameters.LastOrDefault()?.IsParams == true));
            }

            return new ActiveCall(candidates, active, argumentIndex, name);
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

            // Visual Basic (M35): calls, New and attributes.
            VB.InvocationExpressionSyntax { ArgumentList: { } list } when Inside(list.OpenParenToken, list.CloseParenToken, position) =>
                new ArgumentTarget(node, list.Arguments, list.Arguments.GetSeparators().ToList()),
            VB.ObjectCreationExpressionSyntax { ArgumentList: { } list } when Inside(list.OpenParenToken, list.CloseParenToken, position) =>
                new ArgumentTarget(node, list.Arguments, list.Arguments.GetSeparators().ToList()),
            VB.AttributeSyntax { ArgumentList: { } list } when Inside(list.OpenParenToken, list.CloseParenToken, position) =>
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
            VB.InvocationExpressionSyntax { Expression: { } expression } => model.GetMemberGroup(expression, cancellationToken),
            VB.ObjectCreationExpressionSyntax creation => (model.GetTypeInfo(creation.Type, cancellationToken).Type as INamedTypeSymbol)?.InstanceConstructors ?? [],
            VB.AttributeSyntax attribute => (model.GetTypeInfo(attribute.Name, cancellationToken).Type as INamedTypeSymbol)?.InstanceConstructors ?? [],
            _ => [],
        };

        return [.. symbols.OfType<IMethodSymbol>()
            .Where(m => model.IsAccessible(position, m) && !m.GetAttributes().Any(a => a.AttributeClass?.Name == "EditorBrowsableAttribute" && a.ConstructorArguments is [{ Value: 1 }]))
            // The members as the receiver sees them (a generic base class's methods with its type arguments substituted,
            // as the classic IDE shows them), one per declaration.
            .GroupBy(Definition, SymbolEqualityComparer.Default)
            .Select(g => g.First())
            .OrderBy(m => m.Parameters.Length)];
    }

    /// <summary>The declaration a (possibly constructed or reduced) method comes from.</summary>
    private static IMethodSymbol Definition(IMethodSymbol method) => (method.ReducedFrom ?? method).OriginalDefinition;

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
        // The signature as classified runs (the codalusParts extension), the parameters' marked with their index; the
        // label is their text. The method is as the receiver sees it, so a generic base class's type arguments are the
        // call site's (wsLayoutBuilderBase<MyPageModel, …>.AddTab, not wsLayoutBuilderBase<TModel, …>.AddTab).
        var indexer = method.AssociatedSymbol is IPropertySymbol { IsIndexer: true } property ? property : null;
        var visualBasic = model.Language == LanguageNames.VisualBasic;
        var runs = new List<ClassifiedRun>();
        void Add(string text, string kind) => runs.Add(new ClassifiedRun(text, kind));
        void AddParts(IEnumerable<SymbolDisplayPart> parts) => runs.AddRange(ClassifiedText.Runs(parts));
        if (indexer is not null)
        {
            AddParts(indexer.Type.ToMinimalDisplayParts(model, position, s_typeFormat));
            Add(" ", ClassifiedRun.PlainText);
            Add("this", "keyword");
            Add("[", "punctuation");
        }
        else if (method.MethodKind == MethodKind.Constructor)
        {
            if (visualBasic)
            {
                Add("Sub", "keyword");
                Add(" ", ClassifiedRun.PlainText);
                Add("New", "keyword");
            }
            else
            {
                AddParts(method.ContainingType.ToMinimalDisplayParts(model, position, s_typeFormat));
            }

            Add("(", "punctuation");
        }
        else if (visualBasic)
        {
            // Sub Name(…) or Function Name(…) As T, as Visual Basic writes it.
            Add(method.ReturnsVoid ? "Sub" : "Function", "keyword");
            Add(" ", ClassifiedRun.PlainText);
            AddParts(method.ContainingType.ToMinimalDisplayParts(model, position, s_typeFormat));
            Add(".", "punctuation");
            Add(method.Name, method.IsExtensionMethod || method.ReducedFrom is not null ? "extensionMethod" : "method");
            Add("(", "punctuation");
        }
        else
        {
            AddParts(method.ReturnType.ToMinimalDisplayParts(model, position, s_typeFormat));
            Add(" ", ClassifiedRun.PlainText);
            AddParts(method.ToMinimalDisplayParts(model, position, s_nameFormat));
            Add("(", "punctuation");
        }

        var docs = Documentation(Definition(method));
        var parameters = new ParameterInformation[method.Parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            if (i > 0)
            {
                Add(",", "punctuation");
                Add(" ", ClassifiedRun.PlainText);
            }

            var parameter = method.Parameters[i];
            var index = i;
            var parameterRuns = ClassifiedText.Runs(parameter.ToMinimalDisplayParts(model, position, s_parameterFormat)).Select(r => r with { Parameter = index }).ToArray();
            runs.AddRange(parameterRuns);
            parameters[i] = new ParameterInformation(
                string.Concat(parameterRuns.Select(r => r.Text)),
                docs.Parameters.TryGetValue(parameter.Name, out var doc) ? MarkupContent.Markdown(Markdown.Escape(doc)) : null);
        }

        Add(indexer is not null ? "]" : ")", "punctuation");
        if (visualBasic && !method.ReturnsVoid && method.MethodKind != MethodKind.Constructor)
        {
            Add(" ", ClassifiedRun.PlainText);
            Add("As", "keyword");
            Add(" ", ClassifiedRun.PlainText);
            AddParts(method.ReturnType.ToMinimalDisplayParts(model, position, s_typeFormat));
        }

        var label = string.Concat(runs.Select(r => r.Text));
        return new SignatureInformation(label, docs.Summary is { } summary ? MarkupContent.Markdown(Markdown.Escape(summary)) : null, parameters)
        {
            ActiveParameter = parameters.Length == 0 ? null : Math.Min(activeParameter, parameters.Length - 1),
            Parts = [.. runs],
        };
    }

    private static (string? Summary, Dictionary<string, string> Parameters) Documentation(IMethodSymbol method)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);

        // TODO(issue): <inheritdoc/> isn't expanded here, as Roslyn expands it for Quick Info and completion, so a member
        // documented only by it (common in NuGet packages' XML files) shows no summary or parameter descriptions.
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
