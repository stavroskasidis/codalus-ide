using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Location = Codalus.LanguageServices.Protocol.Location;
using SymbolKind = Codalus.LanguageServices.Protocol.SymbolKind;

namespace Codalus.LanguageServices.Host.Features;

/// <summary>
/// Class View and the Object Browser (deferred from M12a): a project's symbols a level at a time. Without references,
/// the roots are the namespaces (and global types) declared in the project's source; with them, the assemblies it
/// compiles against, its own first. Below are namespaces, types, then nested types and members. Items are named by
/// documentation comment ids (<c>N:</c>, <c>T:</c>, <c>M:</c>…), with <c>A:</c> for an assembly and <c>A:assembly|N:…</c>
/// for a namespace of one, so a later request finds them again in the compilation.
/// </summary>
internal static class ObjectBrowserFeature
{
    private const int MaxSearchResults = 200;

    private static readonly SymbolDisplayFormat s_signature = SymbolDisplayFormat.MinimallyQualifiedFormat
        .WithMemberOptions(SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType)
        .WithParameterOptions(SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeParamsRefOut);

    /// <summary>Type names as declared (<c>String</c>, not <c>string</c>), with type parameters.</summary>
    private static readonly SymbolDisplayFormat s_typeName = SymbolDisplayFormat.MinimallyQualifiedFormat.RemoveMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static async Task<BrowseResult> BrowseAsync(Project project, BrowseParams parameters, CancellationToken cancellationToken)
    {
        if (await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is not { } compilation)
        {
            return new BrowseResult([]);
        }

        if (parameters.Query is { Length: > 0 } query)
        {
            return new BrowseResult([.. await SearchAsync(project, compilation, query, parameters.References, cancellationToken).ConfigureAwait(false)]);
        }

        IEnumerable<BrowseItem> items = parameters.Parent switch
        {
            null when parameters.References => Assemblies(compilation),
            null => Namespaces(compilation.Assembly.GlobalNamespace, compilation.Assembly, source: true, assemblyPrefix: null),
            { } parent when parent.StartsWith("A:", StringComparison.Ordinal) && !parent.Contains('|', StringComparison.Ordinal)
                => Assembly(compilation, parent[2..]) is { } assembly ? Namespaces(assembly.GlobalNamespace, assembly, source: false, assemblyPrefix: parent) : [],
            { } parent => Children(compilation, parent, parameters.References),
        };
        return new BrowseResult([.. items]);
    }

    /// <summary>An item's declaration (in source, or a read-only metadata document) and its description.</summary>
    public static async Task<BrowseDescription?> DescribeAsync(Project project, string id, CancellationToken cancellationToken)
    {
        if (await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is not { } compilation || Resolve(compilation, id) is not { } symbol)
        {
            return null;
        }

        Location? location = null;
        if (symbol.Locations.FirstOrDefault(l => l.IsInSource) is { SourceTree: { } tree } source && project.Solution.GetDocument(tree) is { } document)
        {
            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            location = new Location(WorkspaceManager.UriOf(document), Positions.ToRange(text, source.SourceSpan));
        }
        else if (project.Documents.FirstOrDefault() is { } any && symbol is not INamespaceSymbol)
        {
            location = (await MetadataFeature.DefinitionAsync(any, symbol, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        }

        var summary = symbol.GetDocumentationCommentXml(cancellationToken: cancellationToken) is { Length: > 0 } xml ? Summary(xml) : null;
        var container = symbol switch
        {
            INamespaceSymbol => null,
            { ContainingType: { } type } => "Member of " + type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat.RemoveMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes)),
            { ContainingNamespace: { IsGlobalNamespace: false } ns } => "Member of " + ns.ToDisplayString(),
            _ => null,
        };
        return new BrowseDescription(Declaration(symbol), location)
        {
            Summary = summary,
            Container = container,
            Assembly = symbol.ContainingAssembly?.Identity.GetDisplayName(),
        };
    }

    // ------------------------------------------------------------------ levels

    private static IEnumerable<BrowseItem> Assemblies(Compilation compilation)
    {
        yield return AssemblyItem(compilation.Assembly);
        foreach (var assembly in compilation.References
                     .Select(compilation.GetAssemblyOrModuleSymbol).OfType<IAssemblySymbol>()
                     .DistinctBy(a => a.Identity.Name)
                     .OrderBy(a => a.Identity.Name, StringComparer.OrdinalIgnoreCase))
        {
            yield return AssemblyItem(assembly);
        }
    }

    private static BrowseItem AssemblyItem(IAssemblySymbol assembly) =>
        new("A:" + assembly.Identity.Name, assembly.Identity.Name, SymbolKind.Package, true) { Detail = assembly.Identity.Version.ToString() };

    private static IAssemblySymbol? Assembly(Compilation compilation, string name) =>
        compilation.Assembly.Identity.Name == name
            ? compilation.Assembly
            : compilation.References.Select(compilation.GetAssemblyOrModuleSymbol).OfType<IAssemblySymbol>().FirstOrDefault(a => a.Identity.Name == name);

    /// <summary>
    /// The namespaces of an assembly that hold types (flattened, by full name), then its global types. In source, the
    /// project's own declarations; in metadata, what another assembly can use.
    /// </summary>
    private static IEnumerable<BrowseItem> Namespaces(INamespaceSymbol global, IAssemblySymbol assembly, bool source, string? assemblyPrefix)
    {
        var namespaces = new List<INamespaceSymbol>();
        void Walk(INamespaceSymbol ns)
        {
            foreach (var child in ns.GetNamespaceMembers())
            {
                if (child.GetTypeMembers().Any(t => Visible(t, assembly, source)))
                {
                    namespaces.Add(child);
                }

                Walk(child);
            }
        }

        Walk(global);
        foreach (var ns in namespaces.OrderBy(n => n.ToDisplayString(), StringComparer.Ordinal))
        {
            var id = DocumentationCommentId.CreateDeclarationId(ns) ?? "N:" + ns.ToDisplayString();
            yield return new BrowseItem(assemblyPrefix is null ? id : $"{assemblyPrefix}|{id}", ns.ToDisplayString(), SymbolKind.Namespace, true);
        }

        foreach (var type in global.GetTypeMembers().Where(t => Visible(t, assembly, source)).OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            yield return Item(type, source);
        }
    }

    private static List<BrowseItem> Children(Compilation compilation, string parent, bool references)
    {
        var assemblyName = parent.StartsWith("A:", StringComparison.Ordinal) ? parent[2..parent.IndexOf('|', StringComparison.Ordinal)] : null;
        var id = assemblyName is null ? parent : parent[(parent.IndexOf('|', StringComparison.Ordinal) + 1)..];
        var assembly = assemblyName is null ? compilation.Assembly : Assembly(compilation, assemblyName);
        if (assembly is null)
        {
            return [];
        }

        var source = SymbolEqualityComparer.Default.Equals(assembly, compilation.Assembly);
        if (id.StartsWith("N:", StringComparison.Ordinal))
        {
            // The namespace of this assembly (the compilation's merges every assembly's).
            var ns = id[2..].Split('.').Aggregate<string, INamespaceSymbol?>(assembly.GlobalNamespace, (current, part) => current?.GetNamespaceMembers().FirstOrDefault(n => n.Name == part));
            return ns is null ? [] : ns.GetTypeMembers().Where(t => Visible(t, assembly, source)).OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => Item(t, source)).ToList();
        }

        if (Resolve(compilation, id) is not INamedTypeSymbol type)
        {
            return [];
        }

        source = type.Locations.Any(l => l.IsInSource);
        return
        [
            .. type.GetTypeMembers().Where(t => Visible(t, type.ContainingAssembly, source)).OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => Item(t, source)),
            .. type.GetMembers()
                .Where(m => m is not INamedTypeSymbol && !m.IsImplicitlyDeclared && Visible(m, type.ContainingAssembly, source)
                            && m is not IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise or MethodKind.StaticConstructor })
                .OrderBy(m => m.Kind switch { Microsoft.CodeAnalysis.SymbolKind.Field => 0, Microsoft.CodeAnalysis.SymbolKind.Property => 2, Microsoft.CodeAnalysis.SymbolKind.Event => 3, _ => 1 })
                .ThenBy(m => m.Name, StringComparer.Ordinal)
                .Select(m => Item(m, source)),
        ];
    }

    private static async Task<IEnumerable<BrowseItem>> SearchAsync(Project project, Compilation compilation, string query, bool references, CancellationToken cancellationToken)
    {
        var found = new List<ISymbol>();
        if (!references)
        {
            found.AddRange((await SymbolFinder.FindSourceDeclarationsWithPatternAsync(project, query, SymbolFilter.TypeAndMember, cancellationToken).ConfigureAwait(false)).Take(MaxSearchResults));
        }
        else
        {
            void Walk(INamespaceSymbol ns, IAssemblySymbol assembly, bool source)
            {
                foreach (var type in ns.GetTypeMembers())
                {
                    if (found.Count >= MaxSearchResults)
                    {
                        return;
                    }

                    if (!Visible(type, assembly, source))
                    {
                        continue;
                    }

                    if (type.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add(type);
                    }

                    found.AddRange(type.GetMembers().Where(m => m is not INamedTypeSymbol && !m.IsImplicitlyDeclared && Visible(m, assembly, source)
                                                                && m.CanBeReferencedByName && m.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                        .Take(MaxSearchResults - found.Count));
                }

                foreach (var child in ns.GetNamespaceMembers())
                {
                    Walk(child, assembly, source);
                }
            }

            foreach (var assembly in new[] { compilation.Assembly }.Concat(compilation.References.Select(compilation.GetAssemblyOrModuleSymbol).OfType<IAssemblySymbol>()))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Walk(assembly.GlobalNamespace, assembly, SymbolEqualityComparer.Default.Equals(assembly, compilation.Assembly));
            }
        }

        return found
            .Where(s => DocumentationCommentId.CreateDeclarationId(s) is { Length: > 0 })
            .OrderBy(s => s.Name.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0 : s.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .Take(MaxSearchResults)
            .Select(s => Item(s, s.Locations.Any(l => l.IsInSource)) with { Detail = s.ContainingSymbol?.ToDisplayString() });
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Source shows everything the project declares; metadata what code outside the assembly can use.</summary>
    private static bool Visible(ISymbol symbol, IAssemblySymbol assembly, bool source) =>
        SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, assembly)
        && (source || symbol.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal)
        && !symbol.IsImplicitlyDeclared
        && (source || symbol is not INamedTypeSymbol || symbol.CanBeReferencedByName);

    private static BrowseItem Item(ISymbol symbol, bool source) =>
        new(DocumentationCommentId.CreateDeclarationId(symbol) ?? symbol.ToDisplayString(), symbol is INamedTypeSymbol ? symbol.ToDisplayString(s_typeName) : symbol.Name, Kind(symbol),
            symbol is INamedTypeSymbol type && (type.GetMembers().Any(m => !m.IsImplicitlyDeclared) || type.GetTypeMembers().Length > 0))
        {
            Detail = symbol is INamedTypeSymbol ? null : symbol.ToDisplayString(s_signature),
            IsSource = source,
        };

    private static ISymbol? Resolve(Compilation compilation, string id)
    {
        var declaration = id.StartsWith("A:", StringComparison.Ordinal) && id.Contains('|', StringComparison.Ordinal) ? id[(id.IndexOf('|', StringComparison.Ordinal) + 1)..] : id;
        return DocumentationCommentId.GetFirstSymbolForDeclarationId(declaration, compilation);
    }

    /// <summary>How the declaration reads: <c>public sealed class Circle</c>, <c>public override double Area()</c>.</summary>
    private static string Declaration(ISymbol symbol)
    {
        if (symbol is INamespaceSymbol ns)
        {
            return "namespace " + ns.ToDisplayString();
        }

        var access = symbol.DeclaredAccessibility switch
        {
            Accessibility.Public => "public ",
            Accessibility.Protected => "protected ",
            Accessibility.Internal => "internal ",
            Accessibility.Private => "private ",
            Accessibility.ProtectedOrInternal => "protected internal ",
            Accessibility.ProtectedAndInternal => "private protected ",
            _ => string.Empty,
        };
        if (symbol is INamedTypeSymbol type)
        {
            var keyword = type.TypeKind switch
            {
                TypeKind.Interface => "interface",
                TypeKind.Struct => type.IsRecord ? "record struct" : "struct",
                TypeKind.Enum => "enum",
                TypeKind.Delegate => "delegate",
                _ => type.IsRecord ? "record" : "class",
            };
            var modifiers = type.TypeKind == TypeKind.Class
                ? (type.IsStatic ? "static " : type.IsAbstract ? "abstract " : type.IsSealed ? "sealed " : string.Empty)
                : string.Empty;
            return $"{access}{modifiers}{keyword} {type.ToDisplayString(s_typeName)}";
        }

        return access + symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat.WithMemberOptions(
            SymbolDisplayMemberOptions.IncludeModifiers | SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType
            | SymbolDisplayMemberOptions.IncludeRef | SymbolDisplayMemberOptions.IncludeConstantValue));
    }

    /// <summary>The text of a documentation comment's <c>summary</c>, on one line.</summary>
    private static string? Summary(string xml)
    {
        try
        {
            var element = System.Xml.Linq.XElement.Parse(xml.StartsWith("<member", StringComparison.Ordinal) ? xml : "<member>" + xml + "</member>");
            var summary = element.Element("summary");
            if (summary is null)
            {
                return null;
            }

            var text = string.Concat(summary.Nodes().Select(n => n switch
            {
                System.Xml.Linq.XText t => t.Value,
                System.Xml.Linq.XElement { Name.LocalName: "see" or "seealso" } e => (e.Attribute("cref")?.Value ?? e.Attribute("langword")?.Value ?? e.Value) is var reference && reference.Length > 2 && reference[1] == ':' ? reference[2..] : reference,
                System.Xml.Linq.XElement e => e.Value,
                _ => string.Empty,
            }));
            return string.Join(' ', text.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static int Kind(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol => SymbolKind.Namespace,
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => SymbolKind.Interface,
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => SymbolKind.Enum,
        INamedTypeSymbol { TypeKind: TypeKind.Struct } => SymbolKind.Struct,
        INamedTypeSymbol { TypeKind: TypeKind.Delegate } => SymbolKind.Function,
        INamedTypeSymbol => SymbolKind.Class,
        IMethodSymbol { MethodKind: MethodKind.Constructor } => SymbolKind.Constructor,
        IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion } => SymbolKind.Operator,
        IMethodSymbol => SymbolKind.Method,
        IPropertySymbol => SymbolKind.Property,
        IEventSymbol => SymbolKind.Event,
        IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => SymbolKind.EnumMember,
        IFieldSymbol { IsConst: true } => SymbolKind.Constant,
        IFieldSymbol => SymbolKind.Field,
        _ => SymbolKind.Variable,
    };
}
