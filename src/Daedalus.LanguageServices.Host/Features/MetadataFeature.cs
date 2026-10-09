using System.Collections.Concurrent;
using Daedalus.LanguageServices.Protocol;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using FullTypeName = ICSharpCode.Decompiler.TypeSystem.FullTypeName;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Location = Daedalus.LanguageServices.Protocol.Location;

namespace Daedalus.LanguageServices.Host.Features;

/// <summary>
/// Go to Definition into referenced assemblies: the symbol's top-level type is decompiled with ICSharpCode.Decompiler
/// into a read-only <c>daedalus-metadata</c> document, and the location points at the member's declaration in it.
/// Reference assemblies (the SDK's ref packs) have no method bodies, so their members decompile as stubs.
/// </summary>
internal static class MetadataFeature
{
    private static readonly ConcurrentDictionary<(string Assembly, string Type), string> s_decompiled = new();

    public static async Task<Location[]> DefinitionAsync(Document document, ISymbol symbol, CancellationToken cancellationToken)
    {
        if (symbol is INamespaceSymbol || symbol.ContainingAssembly is null)
        {
            return [];
        }

        var compilation = await document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (compilation?.GetMetadataReference(symbol.ContainingAssembly) is not PortableExecutableReference { FilePath: { } assemblyPath })
        {
            return [];
        }

        var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        while (type?.ContainingType is { } outer)
        {
            type = outer;
        }

        if (type is null)
        {
            return [];
        }

        var metadataName = MetadataName(type);
        var text = Decompile(assemblyPath, metadataName);
        var uri = DocumentUris.FromMetadata(assemblyPath, metadataName);
        return [new Location(uri, Find(text, symbol))];
    }

    public static MetadataText? Text(string uri)
    {
        if (DocumentUris.MetadataTarget(uri) is not { } target)
        {
            return null;
        }

        return new MetadataText(Decompile(target.AssemblyPath, target.TypeMetadataName), Path.GetFileNameWithoutExtension(target.AssemblyPath), target.TypeMetadataName);
    }

    private static string Decompile(string assemblyPath, string metadataName) => s_decompiled.GetOrAdd((assemblyPath, metadataName), key =>
    {
        try
        {
            var decompiler = new CSharpDecompiler(key.Assembly, new DecompilerSettings { ThrowOnAssemblyResolveErrors = false });
            var code = decompiler.DecompileTypeAsString(new FullTypeName(key.Type));
            return $"// Decompiled from {Path.GetFileName(key.Assembly)} ({key.Assembly})\n// with ICSharpCode.Decompiler. Read-only.\n\n{code}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return $"// Could not decompile {key.Type} from {key.Assembly}: {ex.Message}\n";
        }
    });

    /// <summary><c>Namespace.Outer`1</c>: the metadata name the decompiler expects.</summary>
    private static string MetadataName(INamedTypeSymbol type) =>
        type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." + type.MetadataName : type.MetadataName;

    /// <summary>The declaration of <paramref name="symbol"/> in decompiled code (by name), or the start of the file.</summary>
    private static Protocol.Range Find(string code, ISymbol symbol)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var text = tree.GetText();
        var name = symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor ? ctor.ContainingType.Name : symbol.Name;
        var token = tree.GetRoot().DescendantNodes()
            .Select(n => n switch
            {
                BaseTypeDeclarationSyntax t => t.Identifier,
                DelegateDeclarationSyntax d => d.Identifier,
                MethodDeclarationSyntax m => m.Identifier,
                ConstructorDeclarationSyntax c => c.Identifier,
                PropertyDeclarationSyntax p => p.Identifier,
                EventDeclarationSyntax e => e.Identifier,
                EnumMemberDeclarationSyntax e => e.Identifier,
                VariableDeclaratorSyntax v when v.Parent?.Parent is FieldDeclarationSyntax or EventFieldDeclarationSyntax => v.Identifier,
                _ => default,
            })
            .FirstOrDefault(t => t.ValueText == name);
        return token == default ? new Protocol.Range(new Position(0, 0), new Position(0, 0)) : Positions.ToRange(text, token.Span);
    }
}
