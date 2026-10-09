using System.Collections.Immutable;
using Daedalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Location = Daedalus.LanguageServices.Protocol.Location;

namespace Daedalus.LanguageServices.Host.Features;

/// <summary>Go to Definition, Go to Implementation and document highlights with <see cref="SymbolFinder"/>.</summary>
internal static class NavigationFeature
{
    /// <summary>
    /// Source locations of the symbol at <paramref name="position"/>, including source-generated files; for symbols
    /// defined only in referenced assemblies, the decompiled declaration.
    /// </summary>
    public static async Task<Location[]> DefinitionAsync(Document document, int position, CancellationToken cancellationToken)
    {
        if (await SymbolAtAsync(document, position, cancellationToken).ConfigureAwait(false) is not { } symbol)
        {
            return [];
        }

        var locations = await LocationsAsync(document.Project.Solution, symbol.Locations, cancellationToken).ConfigureAwait(false);
        return locations.Length > 0 ? locations : await MetadataFeature.DefinitionAsync(document, symbol, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Implementations of an interface or abstract member or type, overrides of a virtual member, or derived classes.
    /// For a concrete, non-virtual symbol: its definition (as in Visual Studio).
    /// </summary>
    public static async Task<Location[]> ImplementationAsync(Document document, int position, CancellationToken cancellationToken)
    {
        if (await SymbolAtAsync(document, position, cancellationToken).ConfigureAwait(false) is not { } symbol)
        {
            return [];
        }

        var solution = document.Project.Solution;
        var results = new List<ISymbol>();
        switch (symbol)
        {
            case INamedTypeSymbol { TypeKind: TypeKind.Interface } type:
                results.AddRange(await SymbolFinder.FindImplementationsAsync(type, solution, transitive: true, cancellationToken: cancellationToken).ConfigureAwait(false));
                break;
            case INamedTypeSymbol { TypeKind: TypeKind.Class } type:
                results.AddRange(await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: true, cancellationToken: cancellationToken).ConfigureAwait(false));
                if (!type.IsAbstract)
                {
                    results.Add(type);
                }

                break;
            case IMethodSymbol or IPropertySymbol or IEventSymbol:
                if (symbol.ContainingType?.TypeKind == TypeKind.Interface)
                {
                    // An abstract or virtual implementation stands for its overrides.
                    foreach (var implementation in await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: cancellationToken).ConfigureAwait(false))
                    {
                        results.Add(implementation);
                        if (implementation.IsAbstract || implementation.IsVirtual)
                        {
                            results.AddRange(await SymbolFinder.FindOverridesAsync(implementation, solution, cancellationToken: cancellationToken).ConfigureAwait(false));
                        }
                    }
                }
                else if (symbol.IsAbstract || symbol.IsVirtual || symbol.IsOverride)
                {
                    results.AddRange(await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: cancellationToken).ConfigureAwait(false));
                    if (!symbol.IsAbstract)
                    {
                        results.Add(symbol);
                    }
                }
                else
                {
                    results.Add(symbol);
                }

                break;
            default:
                results.Add(symbol);
                break;
        }

        return await LocationsAsync(solution, results.Where(s => !s.IsAbstract || s is INamedTypeSymbol { TypeKind: TypeKind.Class }).SelectMany(s => s.Locations), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Definitions (write) and references (read) of the symbol at <paramref name="position"/> in this document.</summary>
    public static async Task<DocumentHighlight[]> HighlightsAsync(Document document, int position, CancellationToken cancellationToken)
    {
        if (await SymbolAtAsync(document, position, cancellationToken).ConfigureAwait(false) is not { } symbol || symbol is INamespaceSymbol)
        {
            return [];
        }

        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
        var highlights = new List<DocumentHighlight>();
        var references = await SymbolFinder.FindReferencesAsync(symbol, document.Project.Solution, ImmutableHashSet.Create(document), cancellationToken).ConfigureAwait(false);
        foreach (var referenced in references)
        {
            // References cascade to related symbols (a property's accessors); only the symbol's own name is highlighted.
            var definitions = referenced.Definition.Name == symbol.Name ? referenced.Definition.Locations : [];
            foreach (var definition in definitions.Where(l => l.IsInSource && l.SourceTree == tree))
            {
                highlights.Add(new DocumentHighlight(Positions.ToRange(text, definition.SourceSpan), DocumentHighlightKind.Write));
            }

            foreach (var reference in referenced.Locations.Where(l => l.Document.Id == document.Id && !l.IsImplicit))
            {
                highlights.Add(new DocumentHighlight(Positions.ToRange(text, reference.Location.SourceSpan), DocumentHighlightKind.Read));
            }
        }

        return [.. highlights.DistinctBy(h => h.Range)];
    }

    internal static async Task<ISymbol?> SymbolAtAsync(Document document, int position, CancellationToken cancellationToken)
    {
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position, cancellationToken).ConfigureAwait(false);
        if (symbol is null && position > 0)
        {
            // The caret right after an identifier (VS behavior).
            symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position - 1, cancellationToken).ConfigureAwait(false);
        }

        return symbol switch
        {
            IAliasSymbol alias => alias.Target,
            IMethodSymbol { MethodKind: MethodKind.ReducedExtension } reduced => reduced.ReducedFrom ?? reduced,
            _ => symbol?.OriginalDefinition is { } original && !SymbolEqualityComparer.Default.Equals(original, symbol) && original.Locations.Any(l => l.IsInSource) ? original : symbol,
        };
    }

    internal static async Task<Location[]> LocationsAsync(Solution solution, IEnumerable<Microsoft.CodeAnalysis.Location> locations, CancellationToken cancellationToken)
    {
        var result = new List<Location>();
        foreach (var location in locations.Where(l => l.IsInSource && l.SourceTree is not null))
        {
            var document = solution.GetDocument(location.SourceTree!)
                           ?? await WorkspaceManager.FindGeneratedAsync(solution, location.SourceTree!, cancellationToken).ConfigureAwait(false);
            var uri = document is not null ? WorkspaceManager.UriOf(document) : DocumentUris.FromPath(location.SourceTree!.FilePath);
            result.Add(new Location(uri, Positions.ToRange(location.GetLineSpan().Span)));
        }

        return [.. result.Distinct()];
    }
}
