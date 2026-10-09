using DaedalusStudio.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using Location = DaedalusStudio.LanguageServices.Protocol.Location;
using SymbolKind = DaedalusStudio.LanguageServices.Protocol.SymbolKind;

namespace DaedalusStudio.LanguageServices.Host.Features;

/// <summary>Find All References and Rename with <see cref="SymbolFinder"/> and <see cref="Renamer"/>.</summary>
internal static class ReferencesFeature
{
    public static async Task<Location[]> ReferencesAsync(Document document, int position, bool includeDeclaration, CancellationToken cancellationToken)
    {
        if (await NavigationFeature.SymbolAtAsync(document, position, cancellationToken).ConfigureAwait(false) is not { } symbol)
        {
            return [];
        }

        var solution = document.Project.Solution;
        var result = new List<Location>();
        foreach (var referenced in await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken).ConfigureAwait(false))
        {
            if (includeDeclaration)
            {
                result.AddRange(await NavigationFeature.LocationsAsync(solution, referenced.Definition.Locations, cancellationToken).ConfigureAwait(false));
            }

            foreach (var reference in referenced.Locations.Where(l => !l.IsImplicit))
            {
                var text = await reference.Document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                result.Add(new Location(WorkspaceManager.UriOf(reference.Document), Positions.ToRange(text, reference.Location.SourceSpan)));
            }
        }

        return [.. result.Distinct().OrderBy(l => l.Uri, StringComparer.Ordinal).ThenBy(l => l.Range.Start.Line).ThenBy(l => l.Range.Start.Character)];
    }
}

internal static class RenameFeature
{
    /// <summary>The identifier to rename at <paramref name="position"/>, if it is a symbol defined in the solution.</summary>
    public static async Task<PrepareRenameResult?> PrepareAsync(Document document, int position, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return null;
        }

        var token = root.FindToken(position);
        if (!token.IsKind(SyntaxKind.IdentifierToken) && position > 0)
        {
            token = root.FindToken(position - 1);
        }

        if (!token.IsKind(SyntaxKind.IdentifierToken)
            || await NavigationFeature.SymbolAtAsync(document, token.SpanStart, cancellationToken).ConfigureAwait(false) is not { } symbol
            || !symbol.Locations.Any(l => l.IsInSource && document.Project.Solution.GetDocument(l.SourceTree) is not null))
        {
            return null;
        }

        return new PrepareRenameResult(Positions.ToRange(text, token.Span), token.ValueText);
    }

    public static async Task<WorkspaceEdit?> RenameAsync(Document document, int position, string newName, Func<string, int?> versionOf, CancellationToken cancellationToken)
    {
        if (!SyntaxFacts.IsValidIdentifier(newName)
            || await NavigationFeature.SymbolAtAsync(document, position, cancellationToken).ConfigureAwait(false) is not { } symbol)
        {
            return null;
        }

        var solution = document.Project.Solution;
        var renamed = await Renamer.RenameSymbolAsync(solution, symbol, new SymbolRenameOptions(), newName, cancellationToken).ConfigureAwait(false);
        return await WorkspaceEdits.ComputeAsync(solution, renamed, versionOf, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Formatting with Roslyn's formatter, which follows the project's <c>.editorconfig</c>.</summary>
internal static class FormattingFeature
{
    public static DocumentOnTypeFormattingOptions OnTypeOptions => new("}", [";"]);

    public static async Task<Protocol.TextEdit[]> FormatAsync(Document document, TextSpan? span, CancellationToken cancellationToken)
    {
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var formatted = span is { } s
            ? await Formatter.FormatAsync(document, s, cancellationToken: cancellationToken).ConfigureAwait(false)
            : await Formatter.FormatAsync(document, cancellationToken: cancellationToken).ConfigureAwait(false);
        var changes = await formatted.GetTextChangesAsync(document, cancellationToken).ConfigureAwait(false);
        return [.. changes.Select(c => new Protocol.TextEdit(Positions.ToRange(text, c.Span), c.NewText ?? string.Empty))];
    }

    /// <summary>After <c>}</c>: the block it closes; after <c>;</c>: the statement or member it ends.</summary>
    public static async Task<Protocol.TextEdit[]> OnTypeAsync(Document document, int position, string ch, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || position == 0)
        {
            return [];
        }

        var token = root.FindToken(position - 1);
        var node = (ch, token.Kind()) switch
        {
            ("}", SyntaxKind.CloseBraceToken) => token.Parent,
            (";", SyntaxKind.SemicolonToken) => token.Parent?.FirstAncestorOrSelf<SyntaxNode>(n => n is StatementSyntax or MemberDeclarationSyntax or UsingDirectiveSyntax),
            _ => null,
        };
        return node is null || node.ContainsDiagnostics && ch == ";"
            ? []
            : await FormatAsync(document, node.FullSpan, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Go To All's symbol search: declarations in the solution matching a (camel-case or substring) pattern.</summary>
internal static class WorkspaceSymbolsFeature
{
    private const int MaxResults = 200;

    public static async Task<SymbolInformation[]> SearchAsync(Solution solution, ProjectId miscellaneous, string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var found = new List<ISymbol>();
        foreach (var project in solution.Projects.Where(p => p.Id != miscellaneous))
        {
            found.AddRange(await SymbolFinder.FindSourceDeclarationsWithPatternAsync(project, query.Trim(), SymbolFilter.TypeAndMember, cancellationToken).ConfigureAwait(false));
            if (found.Count > MaxResults * 4)
            {
                break;
            }
        }

        var result = new List<SymbolInformation>();
        foreach (var symbol in found
                     .Where(s => !s.IsImplicitlyDeclared && s.Locations.Any(l => l.IsInSource))
                     .DistinctBy(s => (s.Name, s.Locations.First().SourceTree?.FilePath, s.Locations.First().SourceSpan.Start))
                     .OrderBy(s => s.Name.StartsWith(query.Trim(), StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                     .ThenBy(s => s.Name.Length)
                     .ThenBy(s => s.Name, StringComparer.Ordinal)
                     .Take(MaxResults))
        {
            var locations = await NavigationFeature.LocationsAsync(solution, symbol.Locations.Take(1), cancellationToken).ConfigureAwait(false);
            if (locations.Length > 0)
            {
                result.Add(new SymbolInformation(symbol.Name, Kind(symbol), locations[0]) { ContainerName = symbol.ContainingSymbol?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) });
            }
        }

        return [.. result];
    }

    private static int Kind(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => SymbolKind.Interface,
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => SymbolKind.Enum,
        INamedTypeSymbol { TypeKind: TypeKind.Struct } => SymbolKind.Struct,
        INamedTypeSymbol => SymbolKind.Class,
        IMethodSymbol { MethodKind: MethodKind.Constructor } => SymbolKind.Constructor,
        IMethodSymbol => SymbolKind.Method,
        IPropertySymbol => SymbolKind.Property,
        IFieldSymbol { IsConst: true } => SymbolKind.Constant,
        IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => SymbolKind.EnumMember,
        IFieldSymbol => SymbolKind.Field,
        IEventSymbol => SymbolKind.Event,
        _ => SymbolKind.Variable,
    };
}
