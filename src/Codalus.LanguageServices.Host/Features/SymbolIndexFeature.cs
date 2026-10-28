using System.Collections.Concurrent;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Editing;
using SymbolKind = Codalus.LanguageServices.Protocol.SymbolKind;

namespace Codalus.LanguageServices.Host.Features;

/// <summary>
/// Code Search's index (M47, ADR 0078): every type and member declared in the solution's documents, from syntax only,
/// so it is ready while projects still compile and is cheap to send again for the documents an edit changed. The
/// shell keeps it next to the solution, so the next session searches before this host has loaded anything. C# and
/// Visual Basic go through <see cref="SyntaxGenerator"/>'s language-neutral view of declarations.
/// </summary>
internal static class SymbolIndexFeature
{
    /// <summary>The declarations of <paramref name="uris"/> (null: every document of every project but Miscellaneous Files).</summary>
    public static async Task<SymbolIndexResult> IndexAsync(Solution solution, ProjectId miscellaneous, string[]? uris, CancellationToken cancellationToken)
    {
        var wanted = uris?.Select(DocumentUris.ToPath).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = solution.Projects.Where(p => p.Id != miscellaneous && p.SupportsCompilation)
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath is { } path && (wanted is null || wanted.Contains(path)) && seen.Add(path)) // a multi-targeted project's document once
            .ToList();

        // In parallel, in the solution's order: the first index, while the background pass compiles, parses most of the
        // documents (12 to 16 s for 10,000 of them one at a time, the processors two thirds idle).
        var indexed = new IndexedDocument?[candidates.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, candidates.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2), CancellationToken = cancellationToken },
            async (i, token) => indexed[i] = await IndexDocumentAsync(candidates[i], token).ConfigureAwait(false)).ConfigureAwait(false);
        return new SymbolIndexResult([.. indexed.OfType<IndexedDocument>()]);
    }

    private static async Task<IndexedDocument?> IndexDocumentAsync(Document document, CancellationToken cancellationToken)
    {
        if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } root)
        {
            return null;
        }

        var generator = SyntaxGenerator.GetGenerator(document);
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var symbols = new List<IndexedSymbol>();
        Walk(root, null, null);
        return new IndexedDocument(DocumentUris.FromPath(document.FilePath!), document.Project.Name, [.. symbols]);

        // Namespaces and types are walked into; members are not (their bodies declare nothing to find).
        void Walk(SyntaxNode node, string? ns, string? type)
        {
            foreach (var child in node.ChildNodes())
            {
                var kind = generator.GetDeclarationKind(child);
                switch (kind)
                {
                    case DeclarationKind.Namespace:
                        var name = generator.GetName(child);
                        Walk(child, ns is null ? name : ns + "." + name, type);
                        break;
                    case DeclarationKind.Class or DeclarationKind.Struct or DeclarationKind.Interface or DeclarationKind.Enum or DeclarationKind.Delegate:
                        if (Add(child, kind, type ?? ns) is { } typeName)
                        {
                            Walk(child, ns, type is null ? typeName : type + "." + typeName);
                        }

                        break;
                    case DeclarationKind.Method or DeclarationKind.Constructor or DeclarationKind.Property or DeclarationKind.Indexer
                        or DeclarationKind.Field or DeclarationKind.Event or DeclarationKind.CustomEvent or DeclarationKind.EnumMember
                        or DeclarationKind.Operator or DeclarationKind.ConversionOperator:
                        Add(child, kind, type);
                        break;
                    case DeclarationKind.None when child is not Microsoft.CodeAnalysis.CSharp.Syntax.GlobalStatementSyntax:
                        // A field with several variables (each variable is the declaration), attribute and base lists
                        // (which declare nothing). Top-level statements are code, not declarations.
                        Walk(child, ns, type);
                        break;
                }
            }
        }

        string? Add(SyntaxNode node, DeclarationKind kind, string? container)
        {
            var name = generator.GetName(node);
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            var position = NameStart(node, name);
            var line = text.Lines.GetLinePosition(position);
            symbols.Add(new IndexedSymbol(name, Kind(kind, node, generator), line.Line, line.Character) { Container = container });
            return name;
        }

        // The name's token, skipping attributes (whose names could be the same); else where the declaration starts.
        int NameStart(SyntaxNode node, string name)
        {
            foreach (var token in node.DescendantTokens(n => generator.GetDeclarationKind(n) is not (DeclarationKind.Attribute or DeclarationKind.Parameter)))
            {
                if (token.ValueText == name)
                {
                    return token.SpanStart;
                }
            }

            return node.SpanStart;
        }
    }

    private static int Kind(DeclarationKind kind, SyntaxNode node, SyntaxGenerator generator) => kind switch
    {
        DeclarationKind.Struct => SymbolKind.Struct,
        DeclarationKind.Interface => SymbolKind.Interface,
        DeclarationKind.Enum => SymbolKind.Enum,
        DeclarationKind.Method => SymbolKind.Method,
        DeclarationKind.Constructor => SymbolKind.Constructor,
        DeclarationKind.Property or DeclarationKind.Indexer => SymbolKind.Property,
        DeclarationKind.Field => (generator.GetModifiers(node).IsConst ? SymbolKind.Constant : SymbolKind.Field),
        DeclarationKind.Event or DeclarationKind.CustomEvent => SymbolKind.Event,
        DeclarationKind.EnumMember => SymbolKind.EnumMember,
        DeclarationKind.Operator or DeclarationKind.ConversionOperator => SymbolKind.Operator,
        _ => SymbolKind.Class,
    };

    // ------------------------------------------------------------------ external items

    /// <summary>The public types of an assembly, kept for the session (keyed by its path and time written).</summary>
    private static readonly ConcurrentDictionary<(string Path, DateTime Written), ExternalType[]> s_assemblies = new();

    private sealed record ExternalType(string Name, string Namespace, string MetadataName, int Kind, string AssemblyPath, string AssemblyName);

    /// <summary>
    /// Types of the referenced assemblies (not the projects') whose names match <paramref name="query"/>: a substring,
    /// or camel-case humps (<c>SB</c> finds <c>StringBuilder</c>). Their locations are <c>codalus-metadata:</c>
    /// documents, decompiled when opened.
    /// </summary>
    public static async Task<SymbolInformation[]> ExternalAsync(Solution solution, ProjectId miscellaneous, string query, int limit, CancellationToken cancellationToken)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return [];
        }

        var types = new Dictionary<string, ExternalType>(StringComparer.Ordinal);
        var assemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in solution.Projects.Where(p => p.Id != miscellaneous && p.SupportsCompilation))
        {
            if (await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is not { } compilation)
            {
                continue;
            }

            foreach (var reference in compilation.References.OfType<PortableExecutableReference>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reference.FilePath is not { } path || !assemblies.Add(path) || compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
                {
                    continue;
                }

                foreach (var type in TypesOf(path, assembly))
                {
                    if (Matches(query, type.Name))
                    {
                        types.TryAdd(type.Namespace + "." + type.MetadataName, type);
                    }
                }
            }
        }

        return [.. types.Values
            .OrderBy(t => t.Name.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0 : t.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(t => t.Name.Length)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(t => new SymbolInformation(t.Name, t.Kind, new Protocol.Location(DocumentUris.FromMetadata(t.AssemblyPath, t.MetadataName), new Protocol.Range(new Position(0, 0), new Position(0, 0))))
            {
                ContainerName = t.Namespace.Length == 0 ? t.AssemblyName : $"{t.Namespace} ({t.AssemblyName})",
            })];
    }

    private static ExternalType[] TypesOf(string path, IAssemblySymbol assembly)
    {
        DateTime written;
        try
        {
            written = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            written = default;
        }

        return s_assemblies.GetOrAdd((path, written), _ =>
        {
            var found = new List<ExternalType>();
            Collect(assembly.GlobalNamespace);
            return [.. found];

            void Collect(INamespaceSymbol ns)
            {
                foreach (var type in ns.GetTypeMembers().Where(t => t.DeclaredAccessibility == Accessibility.Public && t.CanBeReferencedByName))
                {
                    found.Add(new ExternalType(type.Name, ns.IsGlobalNamespace ? string.Empty : ns.ToDisplayString(), MetadataFeature.MetadataName(type), WorkspaceSymbolsFeature.Kind(type), path, assembly.Identity.Name));
                }

                foreach (var child in ns.GetNamespaceMembers())
                {
                    Collect(child);
                }
            }
        });
    }

    /// <summary>A case-insensitive substring, or the query's letters as the starts of the name's words in order.</summary>
    internal static bool Matches(string query, string name)
    {
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var q = 0;
        for (var i = 0; i < name.Length && q < query.Length; i++)
        {
            var wordStart = i == 0 || char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]) || name[i - 1] == '_';
            if (wordStart && char.ToUpperInvariant(name[i]) == char.ToUpperInvariant(query[q]))
            {
                q++;
            }
        }

        return q == query.Length;
    }
}
