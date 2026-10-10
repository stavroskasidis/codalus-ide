using DaedalusStudio.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using SymbolKind = DaedalusStudio.LanguageServices.Protocol.SymbolKind;

namespace DaedalusStudio.LanguageServices.Host.Features;

/// <summary>
/// <c>textDocument/documentSymbol</c>: namespaces, types and members of a document, nested, from syntax only (fast, and
/// it works before the project is compiled). Feeds the Document Outline, the navigation bar, sticky scroll and the
/// members under a file in Solution Explorer.
/// </summary>
internal static class DocumentSymbolsFeature
{
    public static async Task<DocumentSymbol[]> SymbolsAsync(Document document, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        return root is null ? [] : [.. Children(root, text)];
    }

    internal static IEnumerable<DocumentSymbol> Children(SyntaxNode node, SourceText text)
    {
        var members = node switch
        {
            CompilationUnitSyntax unit => unit.Members,
            BaseNamespaceDeclarationSyntax ns => ns.Members,
            TypeDeclarationSyntax type => type.Members,
            _ => default,
        };

        foreach (var member in members)
        {
            switch (member)
            {
                case FileScopedNamespaceDeclarationSyntax ns:
                    // A file-scoped namespace covers the rest of the file: show it as the container of what follows.
                    yield return Symbol(ns.Name.ToString(), SymbolKind.Namespace, ns, ns.Name.Span, text, null, Children(ns, text));
                    break;
                case NamespaceDeclarationSyntax ns:
                    yield return Symbol(ns.Name.ToString(), SymbolKind.Namespace, ns, ns.Name.Span, text, null, Children(ns, text));
                    break;
                case EnumDeclarationSyntax e:
                    yield return Symbol(e.Identifier.ValueText, SymbolKind.Enum, e, e.Identifier.Span, text, null,
                        e.Members.Select(m => Symbol(m.Identifier.ValueText, SymbolKind.EnumMember, m, m.Identifier.Span, text, null, [])));
                    break;
                case TypeDeclarationSyntax type:
                    yield return Symbol(type.Identifier.ValueText + TypeParameters(type.TypeParameterList), TypeKind(type), type, type.Identifier.Span, text,
                        type is RecordDeclarationSyntax { ParameterList: { } parameters } ? Parameters(parameters) : null, Children(type, text));
                    break;
                case DelegateDeclarationSyntax d:
                    yield return Symbol(d.Identifier.ValueText + TypeParameters(d.TypeParameterList), SymbolKind.Function, d, d.Identifier.Span, text, Parameters(d.ParameterList), []);
                    break;
                case MethodDeclarationSyntax m:
                    yield return Symbol(m.Identifier.ValueText + TypeParameters(m.TypeParameterList), SymbolKind.Method, m, m.Identifier.Span, text, Parameters(m.ParameterList) + " : " + m.ReturnType, []);
                    break;
                case ConstructorDeclarationSyntax c:
                    yield return Symbol(c.Identifier.ValueText, SymbolKind.Constructor, c, c.Identifier.Span, text, Parameters(c.ParameterList), []);
                    break;
                case DestructorDeclarationSyntax d:
                    yield return Symbol("~" + d.Identifier.ValueText, SymbolKind.Method, d, d.Identifier.Span, text, "()", []);
                    break;
                case PropertyDeclarationSyntax p:
                    yield return Symbol(p.Identifier.ValueText, SymbolKind.Property, p, p.Identifier.Span, text, p.Type.ToString(), []);
                    break;
                case IndexerDeclarationSyntax i:
                    yield return Symbol("this" + i.ParameterList.ToString(), SymbolKind.Property, i, i.ThisKeyword.Span, text, i.Type.ToString(), []);
                    break;
                case EventDeclarationSyntax e:
                    yield return Symbol(e.Identifier.ValueText, SymbolKind.Event, e, e.Identifier.Span, text, e.Type.ToString(), []);
                    break;
                case BaseFieldDeclarationSyntax field:
                    var kind = field is EventFieldDeclarationSyntax ? SymbolKind.Event
                        : field.Modifiers.Any(SyntaxKind.ConstKeyword) ? SymbolKind.Constant
                        : SymbolKind.Field;
                    foreach (var variable in field.Declaration.Variables)
                    {
                        yield return Symbol(variable.Identifier.ValueText, kind, field, variable.Identifier.Span, text, field.Declaration.Type.ToString(), []);
                    }

                    break;
                case OperatorDeclarationSyntax o:
                    yield return Symbol("operator " + o.OperatorToken.Text, SymbolKind.Operator, o, o.OperatorToken.Span, text, Parameters(o.ParameterList), []);
                    break;
                case ConversionOperatorDeclarationSyntax o:
                    yield return Symbol($"{o.ImplicitOrExplicitKeyword.Text} operator {o.Type}", SymbolKind.Operator, o, o.Type.Span, text, Parameters(o.ParameterList), []);
                    break;
                case GlobalStatementSyntax:
                    break;
            }
        }
    }

    private static DocumentSymbol Symbol(string name, int kind, SyntaxNode node, TextSpan selection, SourceText text, string? detail, IEnumerable<DocumentSymbol> children)
    {
        var list = children.ToArray();
        return new DocumentSymbol(name, kind, Positions.ToRange(text, node.Span), Positions.ToRange(text, selection))
        {
            Detail = detail,
            Children = list.Length > 0 ? list : null,
        };
    }

    private static int TypeKind(TypeDeclarationSyntax type) => type switch
    {
        InterfaceDeclarationSyntax => SymbolKind.Interface,
        StructDeclarationSyntax => SymbolKind.Struct,
        RecordDeclarationSyntax r when r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) => SymbolKind.Struct,
        _ => SymbolKind.Class,
    };

    private static string TypeParameters(TypeParameterListSyntax? list) =>
        list is null ? string.Empty : "<" + string.Join(", ", list.Parameters.Select(p => p.Identifier.ValueText)) + ">";

    private static string Parameters(BaseParameterListSyntax list) =>
        "(" + string.Join(", ", list.Parameters.Select(p => p.Type?.ToString() ?? p.Identifier.ValueText)) + ")";
}
