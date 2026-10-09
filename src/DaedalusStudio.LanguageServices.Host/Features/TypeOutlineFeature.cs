using DaedalusStudio.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace DaedalusStudio.LanguageServices.Host.Features;

/// <summary>
/// <c>daedalus/typeOutline</c>: the type around a position and its public methods, from syntax (types as written). Used
/// by Create Unit Tests (ADR 0010) to generate a test class.
/// </summary>
internal static class TypeOutlineFeature
{
    public static async Task<TypeOutline?> OutlineAsync(Solution solution, string uri, Position position, CancellationToken cancellationToken)
    {
        if (await SyntaxTrees.GetAsync(solution, uri, cancellationToken).ConfigureAwait(false) is not { } tree)
        {
            return null;
        }

        var text = await tree.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
        return Outline(root, text, Positions.ToOffset(text, position));
    }

    internal static TypeOutline? Outline(SyntaxNode root, SourceText text, int offset)
    {
        var token = root.FindToken(Math.Clamp(offset, 0, Math.Max(0, root.FullSpan.End - 1)));
        var type = token.Parent?.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault(t => t is ClassDeclarationSyntax or RecordDeclarationSyntax or StructDeclarationSyntax);
        if (type is null)
        {
            return null;
        }

        var ns = type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString().Replace(" ", string.Empty, StringComparison.Ordinal)).ToList();
        var nesting = type.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.ValueText).ToList();
        var methods = type.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword) || m.Modifiers.Any(SyntaxKind.InternalKeyword))
            .Where(m => !m.Modifiers.Any(SyntaxKind.AbstractKeyword))
            .Select(m => new MethodOutline(
                m.Identifier.ValueText,
                m.ReturnType.ToString(),
                [.. m.ParameterList.Parameters.Select(p => new ParameterOutline(p.Identifier.ValueText, p.Type?.ToString() ?? "object"))],
                m.Modifiers.Any(SyntaxKind.StaticKeyword),
                m.Modifiers.Any(SyntaxKind.AsyncKeyword) || m.ReturnType.ToString() is "Task" or "ValueTask" || m.ReturnType.ToString().StartsWith("Task<", StringComparison.Ordinal) || m.ReturnType.ToString().StartsWith("ValueTask<", StringComparison.Ordinal),
                Positions.ToPosition(text, m.Identifier.SpanStart).Line))
            .ToList();
        var selected = token.Parent?.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault() is { } method && methods.Any(m => m.Name == method.Identifier.ValueText)
            ? method.Identifier.ValueText
            : null;
        var hasParameterlessConstructor = !type.Members.OfType<ConstructorDeclarationSyntax>().Any(c => !c.Modifiers.Any(SyntaxKind.StaticKeyword))
            ? type is not TypeDeclarationSyntax { ParameterList.Parameters.Count: > 0 }
            : type.Members.OfType<ConstructorDeclarationSyntax>().Any(c => c.ParameterList.Parameters.Count == 0 && c.Modifiers.Any(SyntaxKind.PublicKeyword));
        return new TypeOutline(
            type.Identifier.ValueText,
            ns.Count == 0 ? null : string.Join('.', ns),
            string.Join('.', nesting),
            type.Modifiers.Any(SyntaxKind.StaticKeyword),
            hasParameterlessConstructor,
            [.. methods],
            selected);
    }
}

/// <summary>Syntax trees of documents: the workspace's current text, or the file on disk for unknown documents.</summary>
internal static class SyntaxTrees
{
    public static async Task<SyntaxTree?> GetAsync(Solution solution, string uri, CancellationToken cancellationToken)
    {
        if (await WorkspaceManager.FindDocumentAsync(solution, uri, cancellationToken).ConfigureAwait(false) is { } document
            && await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false) is { } documentTree)
        {
            return documentTree;
        }

        if (DocumentUris.ToPath(uri) is { } path && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                return CSharpSyntaxTree.ParseText(SourceText.From(text), CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), path, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }
}
