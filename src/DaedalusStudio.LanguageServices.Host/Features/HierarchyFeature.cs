using System.Text.Json;
using DaedalusStudio.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Location = DaedalusStudio.LanguageServices.Protocol.Location;
using SymbolKind = DaedalusStudio.LanguageServices.Protocol.SymbolKind;

namespace DaedalusStudio.LanguageServices.Host.Features;

/// <summary>
/// Call hierarchy, type hierarchy, Go to Base and the inheritance margin (M12), with <see cref="SymbolFinder"/>.
/// Hierarchy items carry the symbol's documentation id in <c>data</c>, so items without source (framework methods,
/// base types from packages) can be expanded too.
/// </summary>
internal static class HierarchyFeature
{
    // ------------------------------------------------------------------ call hierarchy

    public static async Task<CallHierarchyItem[]> PrepareCallsAsync(Document document, int position, CancellationToken cancellationToken)
    {
        var symbol = await NavigationFeature.SymbolAtAsync(document, position, cancellationToken).ConfigureAwait(false);
        if (symbol is not (IMethodSymbol or IPropertySymbol or IEventSymbol or IFieldSymbol))
        {
            return [];
        }

        return [await CallItemAsync(document.Project.Solution, Normalize(symbol), cancellationToken).ConfigureAwait(false)];
    }

    public static async Task<CallHierarchyIncomingCall[]> IncomingAsync(Solution solution, CallHierarchyItem item, CancellationToken cancellationToken)
    {
        if (await ResolveAsync(solution, item.Data, cancellationToken).ConfigureAwait(false) is not { } symbol)
        {
            return [];
        }

        var result = new List<CallHierarchyIncomingCall>();
        var callers = await SymbolFinder.FindCallersAsync(symbol, solution, cancellationToken).ConfigureAwait(false);
        foreach (var group in callers.Where(c => c.Locations.Any()).GroupBy(c => c.CallingSymbol, SymbolEqualityComparer.Default))
        {
            var caller = Normalize(group.Key!);
            var ranges = new List<Protocol.Range>();
            foreach (var location in group.SelectMany(c => c.Locations).Where(l => l.IsInSource))
            {
                ranges.Add(Positions.ToRange(location.GetLineSpan().Span));
            }

            result.Add(new CallHierarchyIncomingCall(await CallItemAsync(solution, caller, cancellationToken).ConfigureAwait(false), [.. ranges.Distinct()]));
        }

        return [.. result.OrderBy(c => c.From.Name, StringComparer.Ordinal)];
    }

    public static async Task<CallHierarchyOutgoingCall[]> OutgoingAsync(Solution solution, CallHierarchyItem item, CancellationToken cancellationToken)
    {
        if (await ResolveAsync(solution, item.Data, cancellationToken).ConfigureAwait(false) is not { } symbol)
        {
            return [];
        }

        var calls = new Dictionary<ISymbol, List<Protocol.Range>>(SymbolEqualityComparer.Default);
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            var node = await reference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
            if (solution.GetDocument(node.SyntaxTree) is not { } document
                || await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model)
            {
                continue;
            }

            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            foreach (var candidate in node.DescendantNodes())
            {
                var (target, span) = candidate switch
                {
                    InvocationExpressionSyntax invocation => (model.GetSymbolInfo(invocation, cancellationToken).Symbol, NameSpan(invocation.Expression)),
                    BaseObjectCreationExpressionSyntax creation => (model.GetSymbolInfo(creation, cancellationToken).Symbol, creation.Span),
                    IdentifierNameSyntax name when name.Parent is not InvocationExpressionSyntax && (name.Parent is not MemberAccessExpressionSyntax access || access.Name != name || access.Parent is not InvocationExpressionSyntax)
                        => (model.GetSymbolInfo(name, cancellationToken).Symbol is IPropertySymbol or IEventSymbol ? model.GetSymbolInfo(name, cancellationToken).Symbol : null, name.Span),
                    _ => (null, default),
                };
                if (target is null || target is IMethodSymbol { MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction })
                {
                    continue;
                }

                target = Normalize(target);
                if (!calls.TryGetValue(target, out var ranges))
                {
                    calls[target] = ranges = [];
                }

                ranges.Add(Positions.ToRange(text, span));
            }
        }

        var result = new List<CallHierarchyOutgoingCall>();
        foreach (var (target, ranges) in calls)
        {
            result.Add(new CallHierarchyOutgoingCall(await CallItemAsync(solution, target, cancellationToken).ConfigureAwait(false), [.. ranges]));
        }

        return [.. result.OrderBy(c => c.FromRanges[0].Start.Line).ThenBy(c => c.FromRanges[0].Start.Character)];
    }

    private static Microsoft.CodeAnalysis.Text.TextSpan NameSpan(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax access => access.Name.Span,
        MemberBindingExpressionSyntax binding => binding.Name.Span,
        _ => expression.Span,
    };

    private static async Task<CallHierarchyItem> CallItemAsync(Solution solution, ISymbol symbol, CancellationToken cancellationToken)
    {
        var (uri, range, selection) = await PlaceAsync(solution, symbol, cancellationToken).ConfigureAwait(false);
        return new CallHierarchyItem(symbol.Name, Kind(symbol), uri, range, selection)
        {
            Detail = Detail(symbol),
            Data = Data(symbol),
        };
    }

    // ------------------------------------------------------------------ type hierarchy

    public static async Task<TypeHierarchyItem[]> PrepareTypesAsync(Document document, int position, CancellationToken cancellationToken)
    {
        var symbol = await NavigationFeature.SymbolAtAsync(document, position, cancellationToken).ConfigureAwait(false);
        var type = symbol as INamedTypeSymbol ?? (symbol as ILocalSymbol)?.Type as INamedTypeSymbol ?? symbol?.ContainingType;
        return type is null ? [] : [await TypeItemAsync(document.Project.Solution, type, cancellationToken).ConfigureAwait(false)];
    }

    public static async Task<TypeHierarchyItem[]> SupertypesAsync(Solution solution, TypeHierarchyItem item, CancellationToken cancellationToken)
    {
        if (await ResolveAsync(solution, item.Data, cancellationToken).ConfigureAwait(false) is not INamedTypeSymbol type)
        {
            return [];
        }

        var result = new List<TypeHierarchyItem>();
        foreach (var super in new[] { type.BaseType }.Concat(type.Interfaces).OfType<INamedTypeSymbol>())
        {
            result.Add(await TypeItemAsync(solution, super.OriginalDefinition, cancellationToken).ConfigureAwait(false));
        }

        return [.. result];
    }

    public static async Task<TypeHierarchyItem[]> SubtypesAsync(Solution solution, TypeHierarchyItem item, CancellationToken cancellationToken)
    {
        if (await ResolveAsync(solution, item.Data, cancellationToken).ConfigureAwait(false) is not INamedTypeSymbol type)
        {
            return [];
        }

        var subtypes = new List<INamedTypeSymbol>();
        if (type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Interface)
        {
            subtypes.AddRange(await SymbolFinder.FindDerivedInterfacesAsync(type, solution, transitive: false, cancellationToken: cancellationToken).ConfigureAwait(false));
            subtypes.AddRange(await SymbolFinder.FindImplementationsAsync(type, solution, transitive: false, cancellationToken: cancellationToken).ConfigureAwait(false));
        }
        else if (type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Class)
        {
            subtypes.AddRange(await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: false, cancellationToken: cancellationToken).ConfigureAwait(false));
        }

        var result = new List<TypeHierarchyItem>();
        foreach (var subtype in subtypes.Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default).OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            result.Add(await TypeItemAsync(solution, subtype, cancellationToken).ConfigureAwait(false));
        }

        return [.. result];
    }

    private static async Task<TypeHierarchyItem> TypeItemAsync(Solution solution, INamedTypeSymbol type, CancellationToken cancellationToken)
    {
        var (uri, range, selection) = await PlaceAsync(solution, type, cancellationToken).ConfigureAwait(false);
        return new TypeHierarchyItem(type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), Kind(type), uri, range, selection)
        {
            Detail = type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null,
            Data = Data(type),
        };
    }

    // ------------------------------------------------------------------ Go to Base

    /// <summary>Base types and interfaces of a type, or the members a member overrides or implements, nearest first.</summary>
    public static async Task<Location[]> BaseAsync(Document document, int position, CancellationToken cancellationToken)
    {
        if (await NavigationFeature.SymbolAtAsync(document, position, cancellationToken).ConfigureAwait(false) is not { } symbol)
        {
            return [];
        }

        var result = new List<Location>();
        foreach (var target in Bases(symbol))
        {
            var locations = await NavigationFeature.LocationsAsync(document.Project.Solution, target.Locations, cancellationToken).ConfigureAwait(false);
            result.AddRange(locations.Length > 0 ? locations : await MetadataFeature.DefinitionAsync(document, target, cancellationToken).ConfigureAwait(false));
        }

        return [.. result.Distinct()];
    }

    private static IEnumerable<ISymbol> Bases(ISymbol symbol)
    {
        switch (symbol)
        {
            case INamedTypeSymbol type:
                for (var b = type.BaseType; b is not null && b.SpecialType != SpecialType.System_Object; b = b.BaseType)
                {
                    yield return b.OriginalDefinition;
                }

                foreach (var i in type.AllInterfaces)
                {
                    yield return i.OriginalDefinition;
                }

                break;
            default:
                for (var o = Overridden(symbol); o is not null; o = Overridden(o))
                {
                    yield return o.OriginalDefinition;
                }

                foreach (var implemented in ImplementedMembers(symbol))
                {
                    yield return implemented.OriginalDefinition;
                }

                break;
        }
    }

    private static ISymbol? Overridden(ISymbol symbol) => symbol switch
    {
        IMethodSymbol m => m.OverriddenMethod,
        IPropertySymbol p => p.OverriddenProperty,
        IEventSymbol e => e.OverriddenEvent,
        _ => null,
    };

    /// <summary>
    /// Interface members that <paramref name="symbol"/> implements, implicitly or explicitly, also through the member it
    /// overrides (<c>Circle.Area</c> implements <c>IShape.Area</c> when <c>Shape.Area</c> does).
    /// </summary>
    private static IEnumerable<ISymbol> ImplementedMembers(ISymbol symbol)
    {
        if (symbol.ContainingType is not { } type || symbol is not (IMethodSymbol or IPropertySymbol or IEventSymbol))
        {
            yield break;
        }

        foreach (var member in type.AllInterfaces.SelectMany(i => i.GetMembers()))
        {
            var implementation = type.FindImplementationForInterfaceMember(member);
            for (var candidate = symbol; candidate is not null; candidate = Overridden(candidate))
            {
                if (SymbolEqualityComparer.Default.Equals(implementation?.OriginalDefinition, candidate.OriginalDefinition))
                {
                    yield return member;
                    break;
                }
            }
        }
    }

    // ------------------------------------------------------------------ inheritance margin

    /// <summary>Types and members declared in <paramref name="span"/> that implement, override or are implemented or overridden.</summary>
    public static async Task<InheritanceMarginItem[]> MarginAsync(Document document, Microsoft.CodeAnalysis.Text.TextSpan span, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null)
        {
            return [];
        }

        var solution = document.Project.Solution;
        var result = new List<InheritanceMarginItem>();
        foreach (var declaration in root.DescendantNodes(span).Where(n => n.Span.IntersectsWith(span)))
        {
            var (symbol, identifier) = declaration switch
            {
                BaseTypeDeclarationSyntax t => (model.GetDeclaredSymbol(t, cancellationToken), t.Identifier),
                MethodDeclarationSyntax m => (model.GetDeclaredSymbol(m, cancellationToken), m.Identifier),
                PropertyDeclarationSyntax p => (model.GetDeclaredSymbol(p, cancellationToken), p.Identifier),
                IndexerDeclarationSyntax i => (model.GetDeclaredSymbol(i, cancellationToken), i.ThisKeyword),
                EventDeclarationSyntax e => (model.GetDeclaredSymbol(e, cancellationToken), e.Identifier),
                VariableDeclaratorSyntax { Parent.Parent: EventFieldDeclarationSyntax } v => (model.GetDeclaredSymbol(v, cancellationToken), v.Identifier),
                _ => (null, default),
            };
            if (symbol is null || !span.Contains(identifier.SpanStart))
            {
                continue;
            }

            var targets = new List<(string Relation, ISymbol Target)>();
            if (symbol is INamedTypeSymbol type)
            {
                if (type.BaseType is { SpecialType: not SpecialType.System_Object and not SpecialType.System_ValueType and not SpecialType.System_Enum } baseType
                    && type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Class)
                {
                    targets.Add((InheritanceRelations.Inherits, baseType.OriginalDefinition));
                }

                targets.AddRange(type.Interfaces.Select(i => (type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Interface ? InheritanceRelations.Inherits : InheritanceRelations.Implements, (ISymbol)i.OriginalDefinition)));
                if (type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Interface)
                {
                    targets.AddRange((await SymbolFinder.FindImplementationsAsync(type, solution, transitive: false, cancellationToken: cancellationToken).ConfigureAwait(false))
                        .Select(t => (InheritanceRelations.ImplementedBy, (ISymbol)t)));
                    targets.AddRange((await SymbolFinder.FindDerivedInterfacesAsync(type, solution, transitive: false, cancellationToken: cancellationToken).ConfigureAwait(false))
                        .Select(t => (InheritanceRelations.InheritedBy, (ISymbol)t)));
                }
                else if (type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Class && !type.IsSealed && !type.IsStatic)
                {
                    targets.AddRange((await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: false, cancellationToken: cancellationToken).ConfigureAwait(false))
                        .Select(t => (InheritanceRelations.InheritedBy, (ISymbol)t)));
                }
            }
            else
            {
                if (Overridden(symbol) is { } overridden)
                {
                    targets.Add((InheritanceRelations.Overrides, overridden.OriginalDefinition));
                }

                targets.AddRange(ImplementedMembers(symbol).Select(m => (InheritanceRelations.Implements, m.OriginalDefinition)));
                if (symbol.ContainingType?.TypeKind == Microsoft.CodeAnalysis.TypeKind.Interface)
                {
                    targets.AddRange((await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: cancellationToken).ConfigureAwait(false))
                        .Select(m => (InheritanceRelations.ImplementedBy, m)));
                }
                else if (symbol.IsVirtual || symbol.IsAbstract || symbol.IsOverride)
                {
                    targets.AddRange((await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: cancellationToken).ConfigureAwait(false))
                        .Where(o => SymbolEqualityComparer.Default.Equals(Overridden(o)?.OriginalDefinition, symbol.OriginalDefinition))
                        .Select(m => (InheritanceRelations.OverriddenBy, m)));
                }
            }

            if (targets.Count == 0)
            {
                continue;
            }

            var resolved = new List<InheritanceTarget>();
            foreach (var (relation, target) in targets)
            {
                var locations = await NavigationFeature.LocationsAsync(solution, target.Locations.Take(1), cancellationToken).ConfigureAwait(false);
                resolved.Add(new InheritanceTarget(relation, target.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), locations.FirstOrDefault()));
            }

            result.Add(new InheritanceMarginItem(
                text.Lines.GetLineFromPosition(identifier.SpanStart).LineNumber,
                symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                [.. resolved.Select(t => t.Relation).Distinct()],
                [.. resolved]));
        }

        return [.. result];
    }

    // ------------------------------------------------------------------ symbol info (F1)

    public static async Task<SymbolDetails?> InfoAsync(Document document, int position, CancellationToken cancellationToken)
    {
        if (await NavigationFeature.SymbolAtAsync(document, position, cancellationToken).ConfigureAwait(false) is not { } symbol)
        {
            return null;
        }

        var inSource = symbol.Locations.Any(l => l.IsInSource);
        string? help = null;
        if (!inSource && symbol.ContainingAssembly is { } assembly)
        {
            help = IsFramework(assembly.Name) ? ApiReferenceUrl(symbol) : await PackageUrlAsync(document, assembly, cancellationToken).ConfigureAwait(false);
        }

        return new SymbolDetails(
            symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            symbol.Kind.ToString(),
            symbol is INamespaceSymbol ? null : DocumentationCommentId.CreateDeclarationId(symbol.OriginalDefinition),
            symbol.ContainingAssembly?.Name,
            inSource,
            help);
    }

    private static bool IsFramework(string assembly) =>
        assembly is "mscorlib" or "netstandard" or "System" || assembly.StartsWith("System.", StringComparison.Ordinal) || assembly.StartsWith("Microsoft.", StringComparison.Ordinal);

    /// <summary><c>https://learn.microsoft.com/dotnet/api/system.collections.generic.list-1.add</c> for <c>List&lt;T&gt;.Add</c>.</summary>
    internal static string ApiReferenceUrl(ISymbol symbol)
    {
        static string TypeName(INamedTypeSymbol type)
        {
            var name = type.Arity > 0 ? $"{type.Name}-{type.Arity}" : type.Name;
            if (type.ContainingType is { } outer)
            {
                return TypeName(outer) + "." + name;
            }

            return type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." + name : name;
        }

        var page = symbol switch
        {
            INamespaceSymbol ns => ns.ToDisplayString(),
            INamedTypeSymbol type => TypeName(type.OriginalDefinition),
            IMethodSymbol { MethodKind: MethodKind.Constructor } ctor => TypeName(ctor.ContainingType.OriginalDefinition) + ".-ctor",
            _ when symbol.ContainingType is { } type => TypeName(type.OriginalDefinition) + "." + symbol.Name,
            _ => symbol.Name,
        };
        return "https://learn.microsoft.com/dotnet/api/" + page.ToLowerInvariant();
    }

    /// <summary>The <c>projectUrl</c> of the NuGet package an assembly comes from (its <c>.nuspec</c> in the global packages folder).</summary>
    private static async Task<string?> PackageUrlAsync(Document document, IAssemblySymbol assembly, CancellationToken cancellationToken)
    {
        var compilation = await document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (compilation?.GetMetadataReference(assembly) is not PortableExecutableReference { FilePath: { } path })
        {
            return null;
        }

        for (var directory = Path.GetDirectoryName(path); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (Directory.EnumerateFiles(directory, "*.nuspec").FirstOrDefault() is { } nuspec)
            {
                var xml = System.Xml.Linq.XDocument.Load(nuspec);
                var url = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "projectUrl")?.Value.Trim();
                return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.ToString() : null;
            }

            if (Path.GetFileName(directory) is "lib" or "ref" && Path.GetDirectoryName(directory) is { } package
                && Directory.EnumerateFiles(package, "*.nuspec").FirstOrDefault() is null)
            {
                return null;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Accessors stand for their property or event, and constructed symbols for their definitions.</summary>
    private static ISymbol Normalize(ISymbol symbol) => symbol switch
    {
        IMethodSymbol { AssociatedSymbol: { } associated } => associated.OriginalDefinition,
        IMethodSymbol { MethodKind: MethodKind.ReducedExtension, ReducedFrom: { } from } => from.OriginalDefinition,
        _ => symbol.OriginalDefinition,
    };

    private static JsonElement Data(ISymbol symbol) =>
        JsonSerializer.SerializeToElement(DocumentationCommentId.CreateDeclarationId(symbol), ProtocolJsonContext.Default.String);

    /// <summary>The symbol of a hierarchy item's <c>data</c>, from the first project whose compilation has it (source first).</summary>
    private static async Task<ISymbol?> ResolveAsync(Solution solution, JsonElement? data, CancellationToken cancellationToken)
    {
        if (data is not { ValueKind: JsonValueKind.String } element || element.GetString() is not { Length: > 0 } id)
        {
            return null;
        }

        ISymbol? fromMetadata = null;
        foreach (var project in solution.Projects)
        {
            if (await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is { } compilation
                && DocumentationCommentId.GetFirstSymbolForDeclarationId(id, compilation) is { } symbol)
            {
                if (symbol.Locations.Any(l => l.IsInSource))
                {
                    return symbol;
                }

                fromMetadata ??= symbol;
            }
        }

        return fromMetadata;
    }

    private static async Task<(string Uri, Protocol.Range Range, Protocol.Range Selection)> PlaceAsync(Solution solution, ISymbol symbol, CancellationToken cancellationToken)
    {
        if (symbol.Locations.FirstOrDefault(l => l.IsInSource) is { SourceTree: { } tree } location)
        {
            var uri = (await NavigationFeature.LocationsAsync(solution, [location], cancellationToken).ConfigureAwait(false)).FirstOrDefault()?.Uri ?? DocumentUris.FromPath(tree.FilePath);
            var node = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken);
            var selection = Positions.ToRange(location.GetLineSpan().Span);
            var range = node is null ? selection : Positions.ToRange(tree.GetLineSpan(node.Span, cancellationToken).Span);
            return (uri, range, selection);
        }

        var empty = new Protocol.Range(new Position(0, 0), new Position(0, 0));
        return (string.Empty, empty, empty);
    }

    private static string Detail(ISymbol symbol) =>
        symbol.ContainingType is { } type ? type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) : symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty;

    internal static int Kind(ISymbol symbol) => WorkspaceSymbolsFeature.Kind(symbol);
}
