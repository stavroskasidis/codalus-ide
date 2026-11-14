using System.Text.RegularExpressions;
using Codalus.LanguageServices.Host.Features;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;

namespace Codalus.LanguageServices.Host.Xaml;

/// <summary>A place in a XAML file that names a symbol or a resource key: the name's span.</summary>
internal sealed record XamlReference(string Path, SourceText Text, int Start, int Length);

/// <summary>
/// Rename across C# and XAML, both ways (M64, ADR 0103). A C# rename of a type, property, command, method or field
/// also renames its uses in the solution's XAML: compiled binding paths under <c>x:DataType</c> (and WPF's from
/// <c>d:DataContext</c>), <c>{x:Static}</c>, <c>x:Class</c>, <c>x:DataType</c>, <c>{x:Type}</c>, <c>TargetType</c>,
/// element and property-element names, attributes, selectors and event handlers. In XAML, a binding path, a type, a
/// member or an event handler whose symbol is in source renames that symbol through Roslyn (and so its C# uses) and its
/// XAML uses; a resource key renames its <c>x:Key</c>, its <c>StaticResource</c>/<c>DynamicResource</c> uses in the
/// related projects' XAML and the string keys of <c>FindResource</c>-style calls in their C#. Files that XAML generates
/// (WPF's <c>obj/*.g.cs</c>) aren't edited: the build writes them again from the renamed XAML.
/// </summary>
internal static partial class XamlRename
{
    // ------------------------------------------------------------------ the same symbol in two compilations

    /// <summary>Whether two symbols are one declaration, though they come from different compilations (a XAML file's project, the C# document's).</summary>
    public static bool Same(ISymbol? a, ISymbol? b) =>
        a is not null && b is not null && (SymbolEqualityComparer.Default.Equals(a.OriginalDefinition, b.OriginalDefinition) || Key(a) == Key(b));

    private static string Key(ISymbol symbol) =>
        (symbol.OriginalDefinition.GetDocumentationCommentId() ?? symbol.OriginalDefinition.ToDisplayString()) + "|" + symbol.ContainingAssembly?.Name;

    /// <summary>A symbol the solution declares in source (not only in metadata, not only in generated code).</summary>
    public static bool InSource(ISymbol symbol, Solution solution) =>
        symbol.Locations.Any(l => l.IsInSource && solution.GetDocument(l.SourceTree) is { } document && document is not SourceGeneratedDocument && !IsGenerated(document.FilePath));

    /// <summary>WPF's markup compiler output (<c>MainWindow.g.cs</c>, <c>MainWindow.g.i.cs</c>), which follows the XAML.</summary>
    public static bool IsGenerated(string? path) =>
        path is not null && (path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ finding XAML references

    /// <summary>The XAML files of <paramref name="projects"/> (each once) whose text has <paramref name="word"/> as a whole word.</summary>
    private static async Task<List<(string Path, SourceText Text)>> FilesWithAsync(IEnumerable<Project> projects, string word, CancellationToken cancellationToken)
    {
        var result = new List<(string, SourceText)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            foreach (var additional in project.AdditionalDocuments)
            {
                if (additional.FilePath is not { } path || !XamlContext.IsXaml(path) || !seen.Add(Path.GetFullPath(path)))
                {
                    continue;
                }

                var text = await additional.GetTextAsync(cancellationToken).ConfigureAwait(false);
                if (Occurrences(text.ToString(), word).Any())
                {
                    result.Add((Path.GetFullPath(path), text));
                }
            }
        }

        return result;
    }

    /// <summary>The offsets of <paramref name="word"/> in <paramref name="text"/> as a whole word (no letter, digit or underscore around it).</summary>
    public static IEnumerable<int> Occurrences(string text, string word)
    {
        if (word.Length == 0)
        {
            yield break;
        }

        for (var at = text.IndexOf(word, StringComparison.Ordinal); at >= 0; at = text.IndexOf(word, at + word.Length, StringComparison.Ordinal))
        {
            var before = at == 0 ? ' ' : text[at - 1];
            var after = at + word.Length >= text.Length ? ' ' : text[at + word.Length];
            if (!IsWordPart(before) && !IsWordPart(after))
            {
                yield return at;
            }
        }
    }

    private static bool IsWordPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Every place in the solution's XAML that names <paramref name="symbol"/>.</summary>
    public static async Task<IReadOnlyList<XamlReference>> ReferencesAsync(Solution solution, ISymbol symbol, CancellationToken cancellationToken)
    {
        var result = new List<XamlReference>();
        if (symbol.Kind is not (Microsoft.CodeAnalysis.SymbolKind.NamedType or Microsoft.CodeAnalysis.SymbolKind.Property or Microsoft.CodeAnalysis.SymbolKind.Field or Microsoft.CodeAnalysis.SymbolKind.Method or Microsoft.CodeAnalysis.SymbolKind.Event)
            || symbol is IMethodSymbol { MethodKind: not MethodKind.Ordinary })
        {
            return result; // locals, parameters, namespaces, accessors: XAML doesn't name them
        }

        var name = symbol.Name;
        foreach (var (path, _) in await FilesWithAsync(solution.Projects, name, cancellationToken).ConfigureAwait(false))
        {
            if (await XamlContext.CreateAsync(solution, path, null, cancellationToken).ConfigureAwait(false) is not { } context)
            {
                continue;
            }

            foreach (var at in Occurrences(context.TextString, name))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = await XamlNavigation.TargetAsync(context, at + Math.Min(1, name.Length), cancellationToken).ConfigureAwait(false);
                if (target?.Symbol is { } found && at >= target.Start && at + name.Length <= target.End && Same(found, symbol))
                {
                    result.Add(new XamlReference(path, context.Text, at, name.Length));
                }
            }
        }

        return result;
    }

    /// <summary>The projects a resource key of <paramref name="project"/> reaches: itself, the projects it references and those that reference it.</summary>
    private static IEnumerable<Project> ResourceScope(Project project)
    {
        var solution = project.Solution;
        var graph = solution.GetProjectDependencyGraph();
        var ids = new HashSet<ProjectId> { project.Id };
        ids.UnionWith(graph.GetProjectsThatThisProjectTransitivelyDependsOn(project.Id));
        ids.UnionWith(graph.GetProjectsThatTransitivelyDependOnThisProject(project.Id));
        return ids.Select(solution.GetProject).OfType<Project>();
    }

    /// <summary>
    /// Every place that names the resource <paramref name="key"/>: its <c>x:Key</c>, the markup extensions that look it
    /// up in the XAML of <paramref name="project"/>'s related projects, and the string keys of their C#'s
    /// <c>FindResource</c>, <c>TryFindResource</c>, <c>TryGetResource</c> and <c>Resources["…"]</c>.
    /// </summary>
    public static async Task<IReadOnlyList<XamlReference>> ResourceReferencesAsync(Project project, string key, CancellationToken cancellationToken)
    {
        var solution = project.Solution;
        var scope = ResourceScope(project).ToList();
        var result = new List<XamlReference>();
        foreach (var (path, _) in await FilesWithAsync(scope, key, cancellationToken).ConfigureAwait(false))
        {
            if (await XamlContext.CreateAsync(solution, path, null, cancellationToken).ConfigureAwait(false) is not { } context)
            {
                continue;
            }

            foreach (var at in Occurrences(context.TextString, key))
            {
                var element = context.Document.ElementAt(at);
                var attribute = element?.Attributes.FirstOrDefault(a => a.ContainsValue(at));
                if (attribute is { Name: "x:Key" } && attribute.Value?.Trim() == key)
                {
                    result.Add(new XamlReference(path, context.Text, at, key.Length));
                    continue;
                }

                var target = await XamlNavigation.TargetAsync(context, at + Math.Min(1, key.Length), cancellationToken).ConfigureAwait(false);
                if (target?.ResourceKey == key && at >= target.Start && at + key.Length <= target.End)
                {
                    result.Add(new XamlReference(path, context.Text, at, key.Length));
                }
            }
        }

        // The C#'s string keys.
        foreach (var document in scope.SelectMany(p => p.Documents).Where(d => d.FilePath is { } f && !IsGenerated(f)).DistinctBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            if (document.Project.Language != LanguageNames.CSharp || !text.ToString().Contains('"' + key + '"', StringComparison.Ordinal))
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            foreach (var literal in root?.DescendantNodes().OfType<LiteralExpressionSyntax>().Where(l => l.IsKind(SyntaxKind.StringLiteralExpression) && l.Token.Text == "\"" + key + "\"") ?? [])
            {
                if (IsResourceLookup(literal))
                {
                    result.Add(new XamlReference(document.FilePath!, text, literal.Token.SpanStart + 1, key.Length));
                }
            }
        }

        return result;
    }

    /// <summary>A string key of a resource lookup: <c>FindResource("k")</c>, <c>TryFindResource("k")</c>, <c>TryGetResource("k", …)</c>, <c>Resources["k"]</c>.</summary>
    private static bool IsResourceLookup(LiteralExpressionSyntax literal)
    {
        if (literal.Parent is not ArgumentSyntax argument)
        {
            return false;
        }

        if (argument.Parent is ArgumentListSyntax { Parent: InvocationExpressionSyntax invocation } && invocation.ArgumentList.Arguments[0] == argument)
        {
            var name = invocation.Expression switch
            {
                MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
                _ => null,
            };
            return name is "FindResource" or "TryFindResource" or "TryGetResource" or "GetResource";
        }

        return argument.Parent is BracketedArgumentListSyntax { Parent: ElementAccessExpressionSyntax access }
            && access.Expression switch
            {
                MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText == "Resources",
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText == "Resources",
                MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText == "Resources",
                _ => false,
            };
    }

    // ------------------------------------------------------------------ edits

    /// <summary>The references as edits, one <see cref="TextDocumentEdit"/> per file, in the order of the files' first references.</summary>
    public static IEnumerable<TextDocumentEdit> Edits(IEnumerable<XamlReference> references, string newName, Func<string, int?> versionOf) =>
        references
            .GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TextDocumentEdit(
                new OptionalVersionedTextDocumentIdentifier(DocumentUris.FromPath(g.Key), versionOf(g.Key)),
                [.. g.DistinctBy(r => r.Start).OrderBy(r => r.Start).Select(r => new TextEdit(Positions.ToRange(r.Text, new TextSpan(r.Start, r.Length)), newName))]));

    /// <summary>
    /// A C# rename's edit (<see cref="RenameFeature"/>) with the symbol's XAML uses added, and without WPF's generated
    /// files (which the build writes again from the renamed XAML).
    /// </summary>
    public static async Task<WorkspaceEdit> WithXamlAsync(WorkspaceEdit edit, Solution solution, ISymbol symbol, string newName, Func<string, int?> versionOf, CancellationToken cancellationToken)
    {
        var references = await ReferencesAsync(solution, symbol, cancellationToken).ConfigureAwait(false);
        var changes = edit.DocumentChanges.Where(c => !IsGenerated(DocumentUris.ToPath(c.TextDocument.Uri))).ToList();
        foreach (var xaml in Edits(references, newName, versionOf))
        {
            changes.RemoveAll(c => string.Equals(c.TextDocument.Uri, xaml.TextDocument.Uri, StringComparison.OrdinalIgnoreCase));
            changes.Add(xaml);
        }

        return edit with { DocumentChanges = [.. changes] };
    }

    // ------------------------------------------------------------------ rename from XAML

    /// <summary>
    /// What a rename at the position renames: an element name (as before, <see cref="XamlNavigation.RenameAsync"/>), a
    /// resource key defined in the project's XAML, or a symbol declared in the solution's source; null otherwise.
    /// </summary>
    public static async Task<PrepareRenameResult?> PrepareAsync(XamlContext context, int offset, CancellationToken cancellationToken)
    {
        if (await XamlNavigation.PrepareRenameAsync(context, offset, cancellationToken).ConfigureAwait(false) is { } named)
        {
            return named;
        }

        var target = await XamlNavigation.TargetAsync(context, offset, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return null;
        }

        if (target.ResourceKey is { Length: > 0 } key)
        {
            var defined = (await XamlCompletion.ResourceKeysAsync(context, cancellationToken).ConfigureAwait(false)).Any(r => r.Key == key);
            return defined ? new PrepareRenameResult(context.Range(target.Start, target.End), key) : null;
        }

        if (target.Symbol is not { } symbol || context.Project is not { } project || !InSource(symbol, project.Solution) || NameSpan(context, target, symbol, offset) is not { } span)
        {
            return null;
        }

        return new PrepareRenameResult(context.Range(span, span + symbol.Name.Length), symbol.Name);
    }

    /// <summary>The start of the symbol's name in the target's span: the occurrence under the caret, else the last one (<c>vm:MainViewModel</c>'s type name).</summary>
    private static int? NameSpan(XamlContext context, XamlTarget target, ISymbol symbol, int offset)
    {
        var occurrences = Occurrences(context.TextString[..Math.Min(context.TextString.Length, target.End)], symbol.Name).Where(at => at >= target.Start).ToList();
        return occurrences.Count == 0 ? null : occurrences.Where(at => offset >= at && offset <= at + symbol.Name.Length).Cast<int?>().FirstOrDefault() ?? occurrences[^1];
    }

    /// <summary>Renames what <see cref="PrepareAsync"/> found: an element name, a resource key, or a symbol with its C# and XAML uses.</summary>
    public static async Task<WorkspaceEdit?> RenameAsync(XamlContext context, int offset, string newName, Func<string, int?> versionOf, CancellationToken cancellationToken)
    {
        if (await XamlNavigation.PrepareRenameAsync(context, offset, cancellationToken).ConfigureAwait(false) is not null)
        {
            return await XamlNavigation.RenameAsync(context, offset, newName, versionOf, cancellationToken).ConfigureAwait(false);
        }

        var target = await XamlNavigation.TargetAsync(context, offset, cancellationToken).ConfigureAwait(false);
        if (target is null || context.Project is not { } project)
        {
            return null;
        }

        if (target.ResourceKey is { Length: > 0 } key)
        {
            if (!ResourceKeyRegex().IsMatch(newName))
            {
                return null;
            }

            var references = await ResourceReferencesAsync(project, key, cancellationToken).ConfigureAwait(false);
            return new WorkspaceEdit([.. Edits(references, newName, versionOf)]);
        }

        if (target.Symbol is not { } symbol || !InSource(symbol, project.Solution) || !SyntaxFacts.IsValidIdentifier(newName))
        {
            return null;
        }

        var solution = project.Solution;
        var renamed = await Renamer.RenameSymbolAsync(solution, symbol, new SymbolRenameOptions(), newName, cancellationToken).ConfigureAwait(false);
        var edit = await WorkspaceEdits.ComputeAsync(solution, renamed, versionOf, cancellationToken).ConfigureAwait(false);
        return await WithXamlAsync(edit, solution, symbol, newName, versionOf, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A resource key a markup extension can name without quotes.</summary>
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.\-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceKeyRegex();
}
