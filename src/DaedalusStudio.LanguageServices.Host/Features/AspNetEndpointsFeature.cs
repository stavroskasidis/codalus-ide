using DaedalusStudio.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Location = DaedalusStudio.LanguageServices.Protocol.Location;

namespace DaedalusStudio.LanguageServices.Host.Features;

/// <summary>
/// <c>daedalus/aspnet/endpoints</c> (M15a): the ASP.NET Core endpoints a project declares, found with the semantic
/// model. Minimal APIs are <c>Map*</c> calls of the ASP.NET Core extension methods, with the prefixes of the
/// <c>MapGroup</c> calls they are made on (through locals and chained calls). Controller actions come from attribute
/// routes (<c>[Route]</c>, <c>[HttpGet]</c>…, with <c>[controller]</c> and <c>[action]</c> replaced); actions
/// without routes follow the default conventional route, <c>{controller}/{action}</c>.
/// </summary>
internal static class AspNetEndpointsFeature
{
    private const string EndpointRouteBuilder = "Microsoft.AspNetCore.Routing.IEndpointRouteBuilder";
    private const string MvcNamespace = "Microsoft.AspNetCore.Mvc";

    private static readonly Dictionary<string, string> s_mapMethods = new(StringComparer.Ordinal)
    {
        ["MapGet"] = "GET",
        ["MapPost"] = "POST",
        ["MapPut"] = "PUT",
        ["MapDelete"] = "DELETE",
        ["MapPatch"] = "PATCH",
        ["Map"] = "*",
        ["MapMethods"] = "*",
        ["MapFallback"] = "*",
    };

    private static readonly Dictionary<string, string> s_verbAttributes = new(StringComparer.Ordinal)
    {
        ["HttpGetAttribute"] = "GET",
        ["HttpPostAttribute"] = "POST",
        ["HttpPutAttribute"] = "PUT",
        ["HttpDeleteAttribute"] = "DELETE",
        ["HttpPatchAttribute"] = "PATCH",
        ["HttpHeadAttribute"] = "HEAD",
        ["HttpOptionsAttribute"] = "OPTIONS",
    };

    public static async Task<AspNetEndpointsResult> FindAsync(Solution solution, string? projectPath, CancellationToken cancellationToken)
    {
        var comparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var endpoints = new List<AspNetEndpoint>();
        var seen = new HashSet<string>(comparer);

        // A multi-targeted project appears once per framework: its first one is enough.
        foreach (var project in solution.Projects.Where(p => p.Language == LanguageNames.CSharp && p.FilePath is not null
                     && (projectPath is null || comparer.Equals(p.FilePath, projectPath))))
        {
            if (!seen.Add(project.FilePath!)
                || await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is not { } compilation
                || compilation.GetTypeByMetadataName(EndpointRouteBuilder) is null)
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(project.FilePath!);
            foreach (var document in project.Documents)
            {
                if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } root
                    || await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model)
                {
                    continue;
                }

                var uri = WorkspaceManager.UriOf(document);
                Location At(SyntaxNode node) => new(uri, Positions.ToRange(node.GetLocation().GetLineSpan().Span));
                Location LocationOf(ISymbol symbol, SyntaxNode fallback) =>
                    symbol.Locations.FirstOrDefault(l => l.IsInSource) is { SourceTree: { } tree } location && solution.GetDocument(tree) is { } target
                        ? new Location(WorkspaceManager.UriOf(target), Positions.ToRange(location.GetLineSpan().Span))
                        : At(fallback);

                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (MinimalApi(invocation, model, compilation, cancellationToken) is { } endpoint)
                    {
                        var handler = endpoint.Handler;
                        endpoints.Add(new AspNetEndpoint(endpoint.Method, endpoint.Route, handler.Name, "minimal", name, project.FilePath!,
                            handler.Symbol is { } symbol ? LocationOf(symbol, handler.Node) : At(handler.Node)));
                    }
                }

                foreach (var type in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(type, cancellationToken) is not { } symbol || !IsController(symbol))
                    {
                        continue;
                    }

                    foreach (var (method, route, action) in ControllerActions(symbol))
                    {
                        var node = action.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) is MethodDeclarationSyntax declaration
                            ? (SyntaxNode)declaration
                            : type;
                        var location = node is MethodDeclarationSyntax m ? new Location(uri, Positions.ToRange(m.Identifier.GetLocation().GetLineSpan().Span)) : At(node);
                        endpoints.Add(new AspNetEndpoint(method, route, $"{symbol.Name}.{action.Name}", "controller", name, project.FilePath!, location));
                    }
                }
            }
        }

        return new AspNetEndpointsResult([.. endpoints
            .Distinct()
            .OrderBy(e => e.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Route, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.HttpMethod, StringComparer.Ordinal)]);
    }

    private sealed record Handler(string Name, ISymbol? Symbol, SyntaxNode Node);

    private sealed record MinimalEndpoint(string Method, string Route, Handler Handler);

    private static MinimalEndpoint? MinimalApi(InvocationExpressionSyntax invocation, SemanticModel model, Compilation compilation, CancellationToken cancellationToken)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax access || !s_mapMethods.TryGetValue(access.Name.Identifier.Text, out var method)
            || invocation.ArgumentList.Arguments.Count == 0)
        {
            return null;
        }

        var info = model.GetSymbolInfo(invocation, cancellationToken);
        if ((info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) is not IMethodSymbol symbol
            || symbol.ContainingNamespace?.ToDisplayString().StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) != true)
        {
            return null;
        }

        var arguments = invocation.ArgumentList.Arguments;
        string pattern;
        if (access.Name.Identifier.Text == "MapFallback")
        {
            pattern = arguments.Count >= 2 && Constant(arguments[0].Expression, model, cancellationToken) is { } fallback ? fallback : "{*path}";
        }
        else if (Constant(arguments[0].Expression, model, cancellationToken) is { } literal)
        {
            pattern = literal;
        }
        else
        {
            return null;
        }

        if (access.Name.Identifier.Text == "MapMethods" && arguments.Count >= 2)
        {
            var verbs = arguments[1].Expression.DescendantNodesAndSelf().OfType<LiteralExpressionSyntax>()
                .Select(l => l.Token.ValueText.ToUpperInvariant()).Where(v => v.Length > 0).ToList();
            method = verbs.Count == 0 ? "*" : string.Join(",", verbs);
        }

        var prefix = GroupPrefix(access.Expression, model, compilation, depth: 0, cancellationToken);
        return new MinimalEndpoint(method, Combine(prefix, pattern), HandlerOf(arguments[^1].Expression, model, cancellationToken));
    }

    /// <summary>The route prefix of the builder a <c>Map*</c> call is made on: its <c>MapGroup</c> calls, outermost first.</summary>
    private static string GroupPrefix(ExpressionSyntax receiver, SemanticModel model, Compilation compilation, int depth, CancellationToken cancellationToken)
    {
        if (depth > 16)
        {
            return string.Empty;
        }

        switch (receiver)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return GroupPrefix(parenthesized.Expression, model, compilation, depth + 1, cancellationToken);
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax inner } call:
                var outer = GroupPrefix(inner.Expression, model, compilation, depth + 1, cancellationToken);
                return inner.Name.Identifier.Text == "MapGroup" && call.ArgumentList.Arguments.Count > 0
                       && Constant(call.ArgumentList.Arguments[0].Expression, model, cancellationToken) is { } prefix
                    ? Combine(outer, prefix)
                    : outer; // a chained configuration call (WithTags, RequireAuthorization…) returns the same builder
            case IdentifierNameSyntax identifier when model.GetSymbolInfo(identifier, cancellationToken).Symbol is ILocalSymbol or IFieldSymbol:
                var symbol = model.GetSymbolInfo(identifier, cancellationToken).Symbol!;
                foreach (var reference in symbol.DeclaringSyntaxReferences)
                {
                    if (reference.GetSyntax(cancellationToken) is VariableDeclaratorSyntax { Initializer.Value: { } value } declarator)
                    {
                        var declarationModel = declarator.SyntaxTree == model.SyntaxTree ? model : compilation.GetSemanticModel(declarator.SyntaxTree);
                        return GroupPrefix(value, declarationModel, compilation, depth + 1, cancellationToken);
                    }
                }

                return string.Empty;
            default:
                return string.Empty;
        }
    }

    private static Handler HandlerOf(ExpressionSyntax expression, SemanticModel model, CancellationToken cancellationToken)
    {
        if (expression is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax)
        {
            return new Handler("lambda", null, expression);
        }

        var info = model.GetSymbolInfo(expression, cancellationToken);
        return (info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) is IMethodSymbol method
            ? new Handler($"{method.ContainingType?.Name}.{method.Name}".TrimStart('.'), method, expression)
            : new Handler(expression.ToString(), null, expression);
    }

    private static string? Constant(ExpressionSyntax expression, SemanticModel model, CancellationToken cancellationToken) =>
        model.GetConstantValue(expression, cancellationToken) is { HasValue: true, Value: string value } ? value : null;

    // ------------------------------------------------------------------ controllers

    private static bool IsController(INamedTypeSymbol type)
    {
        if (type.IsAbstract || type.IsStatic || type.DeclaredAccessibility != Accessibility.Public || type.IsGenericType)
        {
            return false;
        }

        if (type.GetAttributes().Any(a => a.AttributeClass is { Name: "NonControllerAttribute" }))
        {
            return false;
        }

        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.GetAttributes().Any(a => a.AttributeClass is { Name: "ApiControllerAttribute" or "ControllerAttribute" } attribute && InMvc(attribute))
                || current is { Name: "ControllerBase" } && InMvc(current))
            {
                return true;
            }
        }

        return false;
    }

    private static bool InMvc(ISymbol symbol) => symbol.ContainingNamespace?.ToDisplayString() == MvcNamespace;

    private static IEnumerable<(string Method, string Route, IMethodSymbol Action)> ControllerActions(INamedTypeSymbol controller)
    {
        var controllerName = controller.Name.EndsWith("Controller", StringComparison.Ordinal) ? controller.Name[..^"Controller".Length] : controller.Name;
        var classRoutes = RouteTemplates(controller).ToList();
        var methods = new List<IMethodSymbol>();
        for (var type = controller; type is not null && !(type.Name is "ControllerBase" or "Controller" && InMvc(type)); type = type.BaseType)
        {
            methods.AddRange(type.GetMembers().OfType<IMethodSymbol>().Where(m => m is { MethodKind: MethodKind.Ordinary, IsStatic: false, DeclaredAccessibility: Accessibility.Public }
                && !methods.Any(existing => existing.Name == m.Name && SymbolEqualityComparer.Default.Equals(existing.OverriddenMethod, m))));
        }

        foreach (var action in methods)
        {
            if (action.GetAttributes().Any(a => a.AttributeClass is { Name: "NonActionAttribute" }))
            {
                continue;
            }

            var verbs = action.GetAttributes()
                .Where(a => a.AttributeClass is { } c && s_verbAttributes.ContainsKey(c.Name) && InMvc(c))
                .Select(a => (Method: s_verbAttributes[a.AttributeClass!.Name], Template: a.ConstructorArguments.FirstOrDefault().Value as string))
                .ToList();
            var methodRoutes = RouteTemplates(action).ToList();
            var templates = new List<(string Method, string? Template)>();
            if (verbs.Count > 0)
            {
                foreach (var (verb, template) in verbs)
                {
                    if (template is not null || methodRoutes.Count == 0)
                    {
                        templates.Add((verb, template));
                    }
                    else
                    {
                        templates.AddRange(methodRoutes.Select(r => (verb, (string?)r)));
                    }
                }
            }
            else if (methodRoutes.Count > 0)
            {
                templates.AddRange(methodRoutes.Select(r => ("*", (string?)r)));
            }
            else if (classRoutes.Count > 0)
            {
                templates.Add(("*", null));
            }
            else
            {
                // Conventional routing (the default route of an MVC app).
                yield return ("*", Tokens("/[controller]/[action]", controllerName, action.Name), action);
                continue;
            }

            foreach (var (verb, template) in templates)
            {
                if (template is not null && (template.StartsWith('/') || template.StartsWith("~/", StringComparison.Ordinal)))
                {
                    yield return (verb, Tokens(Combine(string.Empty, template.TrimStart('~')), controllerName, action.Name), action);
                }
                else if (classRoutes.Count == 0)
                {
                    yield return (verb, Tokens(Combine(string.Empty, template ?? string.Empty), controllerName, action.Name), action);
                }
                else
                {
                    foreach (var classRoute in classRoutes)
                    {
                        yield return (verb, Tokens(Combine(classRoute, template ?? string.Empty), controllerName, action.Name), action);
                    }
                }
            }
        }
    }

    private static IEnumerable<string> RouteTemplates(ISymbol symbol) =>
        symbol.GetAttributes()
            .Where(a => a.AttributeClass is { Name: "RouteAttribute" } c && InMvc(c))
            .Select(a => a.ConstructorArguments.FirstOrDefault().Value as string)
            .OfType<string>();

    private static string Tokens(string route, string controller, string action) =>
        route.Replace("[controller]", controller, StringComparison.OrdinalIgnoreCase).Replace("[action]", action, StringComparison.OrdinalIgnoreCase);

    /// <summary>Joins two route templates with one slash, always starting with one.</summary>
    internal static string Combine(string prefix, string pattern)
    {
        var left = prefix.Trim().Trim('/');
        var right = pattern.Trim().Trim('/');
        var joined = left.Length == 0 ? right : right.Length == 0 ? left : left + "/" + right;
        return "/" + joined;
    }
}
