using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using SymbolKind = Codalus.LanguageServices.Protocol.SymbolKind;

namespace Codalus.LanguageServices.Host.Features;

/// <summary>
/// Visual Basic's document symbols and folding ranges (M35), from its syntax as <see cref="DocumentSymbolsFeature"/>
/// and <see cref="FoldingFeature"/> do for C#: namespaces, types (classes, modules, structures, interfaces, enums),
/// members, and the blocks that fold (types, methods, properties and their accessors, multi-line lambdas,
/// <c>#Region</c>, groups of <c>Imports</c>, comment blocks).
/// </summary>
internal static class VisualBasicStructure
{
    // ------------------------------------------------------------------ symbols

    public static IEnumerable<DocumentSymbol> Symbols(SyntaxNode node, SourceText text)
    {
        var members = node switch
        {
            CompilationUnitSyntax unit => unit.Members,
            NamespaceBlockSyntax ns => ns.Members,
            TypeBlockSyntax type => type.Members,
            _ => default,
        };

        foreach (var member in members)
        {
            switch (member)
            {
                case NamespaceBlockSyntax ns:
                    yield return Symbol(ns.NamespaceStatement.Name.ToString(), SymbolKind.Namespace, ns, ns.NamespaceStatement.Name.Span, text, null, Symbols(ns, text));
                    break;
                case EnumBlockSyntax e:
                    yield return Symbol(e.EnumStatement.Identifier.ValueText, SymbolKind.Enum, e, e.EnumStatement.Identifier.Span, text, null,
                        e.Members.OfType<EnumMemberDeclarationSyntax>().Select(m => Symbol(m.Identifier.ValueText, SymbolKind.EnumMember, m, m.Identifier.Span, text, null, [])));
                    break;
                case TypeBlockSyntax type:
                    var statement = type.BlockStatement;
                    yield return Symbol(statement.Identifier.ValueText + TypeParameters(statement.TypeParameterList), TypeKind(type), type, statement.Identifier.Span, text, null, Symbols(type, text));
                    break;
                case DelegateStatementSyntax d:
                    yield return Symbol(d.Identifier.ValueText + TypeParameters(d.TypeParameterList), SymbolKind.Function, d, d.Identifier.Span, text, Parameters(d.ParameterList), []);
                    break;
                case MethodBlockSyntax { SubOrFunctionStatement: var method } block:
                    yield return Symbol(method.Identifier.ValueText + TypeParameters(method.TypeParameterList), SymbolKind.Method, block, method.Identifier.Span, text,
                        Parameters(method.ParameterList) + (method.AsClause is { } returns ? " As " + returns.Type : string.Empty), []);
                    break;
                case MethodStatementSyntax method:
                    // An interface's or a MustOverride method: a statement without a block.
                    yield return Symbol(method.Identifier.ValueText + TypeParameters(method.TypeParameterList), SymbolKind.Method, method, method.Identifier.Span, text,
                        Parameters(method.ParameterList) + (method.AsClause is { } asClause ? " As " + asClause.Type : string.Empty), []);
                    break;
                case ConstructorBlockSyntax constructor:
                    yield return Symbol("New", SymbolKind.Constructor, constructor, constructor.SubNewStatement.NewKeyword.Span, text, Parameters(constructor.SubNewStatement.ParameterList), []);
                    break;
                case OperatorBlockSyntax op:
                    yield return Symbol("Operator " + op.OperatorStatement.OperatorToken.Text, SymbolKind.Operator, op, op.OperatorStatement.OperatorToken.Span, text, Parameters(op.OperatorStatement.ParameterList), []);
                    break;
                case PropertyBlockSyntax property:
                    yield return Symbol(property.PropertyStatement.Identifier.ValueText, SymbolKind.Property, property, property.PropertyStatement.Identifier.Span, text, AsType(property.PropertyStatement.AsClause), []);
                    break;
                case PropertyStatementSyntax property:
                    yield return Symbol(property.Identifier.ValueText, SymbolKind.Property, property, property.Identifier.Span, text, AsType(property.AsClause), []);
                    break;
                case EventBlockSyntax e:
                    yield return Symbol(e.EventStatement.Identifier.ValueText, SymbolKind.Event, e, e.EventStatement.Identifier.Span, text, AsType(e.EventStatement.AsClause), []);
                    break;
                case EventStatementSyntax e:
                    yield return Symbol(e.Identifier.ValueText, SymbolKind.Event, e, e.Identifier.Span, text, AsType(e.AsClause), []);
                    break;
                case FieldDeclarationSyntax field:
                    var kind = field.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)) ? SymbolKind.Constant : SymbolKind.Field;
                    foreach (var declarator in field.Declarators)
                    {
                        foreach (var name in declarator.Names)
                        {
                            yield return Symbol(name.Identifier.ValueText, kind, field, name.Identifier.Span, text, AsType(declarator.AsClause), []);
                        }
                    }

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

    private static int TypeKind(TypeBlockSyntax type) => type switch
    {
        InterfaceBlockSyntax => SymbolKind.Interface,
        StructureBlockSyntax => SymbolKind.Struct,
        ModuleBlockSyntax => SymbolKind.Module,
        _ => SymbolKind.Class,
    };

    private static string TypeParameters(TypeParameterListSyntax? list) =>
        list is null ? string.Empty : "(Of " + string.Join(", ", list.Parameters.Select(p => p.Identifier.ValueText)) + ")";

    private static string Parameters(ParameterListSyntax? list) =>
        "(" + string.Join(", ", (list?.Parameters ?? default).Select(p => p.AsClause?.Type.ToString() ?? p.Identifier.Identifier.ValueText)) + ")";

    private static string? AsType(AsClauseSyntax? clause) => clause switch
    {
        SimpleAsClauseSyntax simple => simple.Type.ToString(),
        AsNewClauseSyntax asNew => asNew.NewExpression.ToString(),
        _ => null,
    };

    // ------------------------------------------------------------------ folding

    public static IReadOnlyList<FoldingRange> Folding(SyntaxNode root, SourceText text, CancellationToken cancellationToken)
    {
        var ranges = new List<FoldingRange>();
        void Add(int start, int end, string? kind = null, string? collapsed = null)
        {
            var startLine = text.Lines.GetLineFromPosition(start);
            var endLine = text.Lines.GetLinePosition(end).Line;
            if (endLine > startLine.LineNumber)
            {
                ranges.Add(new FoldingRange(startLine.LineNumber, endLine)
                {
                    StartCharacter = startLine.End - startLine.Start,
                    Kind = kind,
                    CollapsedText = collapsed,
                });
            }
        }

        foreach (var node in root.DescendantNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (node)
            {
                case NamespaceBlockSyntax or TypeBlockSyntax or EnumBlockSyntax:
                case MethodBlockBaseSyntax or PropertyBlockSyntax or EventBlockSyntax:
                case MultiLineLambdaExpressionSyntax:
                    Add(node.SpanStart, node.Span.End);
                    break;
                case XmlNodeSyntax when node.Parent is not XmlNodeSyntax:
                    Add(node.SpanStart, node.Span.End); // an XML literal
                    break;
            }
        }

        // Groups of Imports.
        if (root is CompilationUnitSyntax { Imports: { Count: > 1 } imports })
        {
            Add(imports[0].SpanStart, imports[^1].Span.End, "imports", "Imports …");
        }

        // #Region … #End Region, and runs of comment lines.
        var regions = new Stack<(int Start, string Title)>();
        var comments = new List<SyntaxTrivia>();
        void FlushComments()
        {
            if (comments.Count > 1)
            {
                Add(comments[0].SpanStart, comments[^1].Span.End, "comment");
            }

            comments.Clear();
        }

        foreach (var trivia in root.DescendantTrivia())
        {
            switch (trivia.Kind())
            {
                case SyntaxKind.RegionDirectiveTrivia when trivia.GetStructure() is RegionDirectiveTriviaSyntax region:
                    regions.Push((trivia.SpanStart, region.Name.ValueText));
                    break;
                case SyntaxKind.EndRegionDirectiveTrivia when regions.Count > 0:
                    var (start, title) = regions.Pop();
                    Add(start, trivia.Span.End, "region", title.Length > 0 ? title : "#Region");
                    break;
                case SyntaxKind.CommentTrivia or SyntaxKind.DocumentationCommentTrivia:
                    if (comments.Count > 0 && text.Lines.GetLinePosition(trivia.SpanStart).Line > text.Lines.GetLinePosition(comments[^1].Span.End).Line + 1)
                    {
                        FlushComments();
                    }

                    comments.Add(trivia);
                    break;
                case SyntaxKind.WhitespaceTrivia or SyntaxKind.EndOfLineTrivia:
                    break;
                default:
                    FlushComments();
                    break;
            }
        }

        FlushComments();
        return ranges;
    }
}

/// <summary>
/// <c>codalus/testCandidates</c> for Visual Basic (M35): test classes and methods by attribute name, from syntax, as
/// <see cref="TestCandidatesFeature"/> finds them in C#. Types are named in the project's root namespace, as VB
/// compiles them.
/// </summary>
internal static class VisualBasicTestCandidates
{
    public static TestCandidate[] Find(SyntaxNode root, SourceText text, string? rootNamespace)
    {
        var results = new List<TestCandidate>();
        foreach (var type in root.DescendantNodes(n => n is CompilationUnitSyntax or NamespaceBlockSyntax or TypeBlockSyntax).OfType<TypeBlockSyntax>())
        {
            if (type is not (ClassBlockSyntax or StructureBlockSyntax) || type.BlockStatement.Modifiers.Any(m => m.IsKind(SyntaxKind.MustInheritKeyword)))
            {
                continue;
            }

            var ns = Namespace(type, rootNamespace);
            var typeName = (ns is null ? string.Empty : ns + ".") + NestedName(type);
            var methods = new List<TestCandidate>();
            foreach (var method in type.Members.OfType<MethodBlockSyntax>())
            {
                var statement = method.SubOrFunctionStatement;
                var names = Names(statement.AttributeLists).ToList();
                if (TestCandidatesFeature.TestAttributeOf(names) is not { } attribute)
                {
                    continue;
                }

                var parameterized = statement.ParameterList?.Parameters.Count > 0 || TestCandidatesFeature.s_parameterizedAttributes.Contains(attribute)
                    || names.Any(TestCandidatesFeature.s_parameterizedAttributes.Contains);
                methods.Add(new TestCandidate(TestCandidateKinds.Method, statement.Identifier.ValueText, typeName, ns,
                    Positions.ToRange(text, WithoutAttributes(method, statement.AttributeLists)), Positions.ToRange(text, statement.Identifier.Span), parameterized));
            }

            var statementOfType = type.BlockStatement;
            if (methods.Count > 0 || Names(statementOfType.AttributeLists).Any(TestCandidatesFeature.s_classAttributes.Contains))
            {
                results.Add(new TestCandidate(TestCandidateKinds.Class, statementOfType.Identifier.ValueText, typeName, ns,
                    Positions.ToRange(text, WithoutAttributes(type, statementOfType.AttributeLists)), Positions.ToRange(text, statementOfType.Identifier.Span), Parameterized: false));
                results.AddRange(methods);
            }
        }

        return [.. results];
    }

    private static IEnumerable<string> Names(SyntaxList<AttributeListSyntax> lists) =>
        lists.SelectMany(l => l.Attributes).Select(a =>
        {
            var name = a.Name switch
            {
                QualifiedNameSyntax q => q.Right.Identifier.ValueText,
                SimpleNameSyntax s => s.Identifier.ValueText,
                _ => a.Name.ToString(),
            };
            return name.EndsWith("Attribute", StringComparison.Ordinal) && name.Length > "Attribute".Length ? name[..^"Attribute".Length] : name;
        });

    private static TextSpan WithoutAttributes(SyntaxNode block, SyntaxList<AttributeListSyntax> attributes)
    {
        var start = attributes.Count > 0 ? attributes[^1].GetLastToken().GetNextToken().SpanStart : block.SpanStart;
        return TextSpan.FromBounds(Math.Min(start, block.Span.End), block.Span.End);
    }

    private static string? Namespace(SyntaxNode node, string? rootNamespace)
    {
        var parts = new List<string>();
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is NamespaceBlockSyntax ns)
            {
                var name = ns.NamespaceStatement.Name.ToString().Replace(" ", string.Empty, StringComparison.Ordinal);
                if (name.StartsWith("Global.", StringComparison.OrdinalIgnoreCase))
                {
                    parts.Insert(0, name["Global.".Length..]);
                    rootNamespace = null; // Namespace Global.X opts out of the root namespace
                }
                else
                {
                    parts.Insert(0, name);
                }
            }
        }

        if (!string.IsNullOrEmpty(rootNamespace))
        {
            parts.Insert(0, rootNamespace);
        }

        return parts.Count == 0 ? null : string.Join('.', parts);
    }

    private static string NestedName(TypeBlockSyntax type)
    {
        var builder = new System.Text.StringBuilder();
        for (SyntaxNode? node = type; node is TypeBlockSyntax block; node = node.Parent)
        {
            var name = block.BlockStatement.Identifier.ValueText;
            if (block.BlockStatement.TypeParameterList is { Parameters.Count: > 0 } parameters)
            {
                name += "`" + parameters.Parameters.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            builder.Insert(0, builder.Length == 0 ? name : name + "+");
        }

        return builder.ToString();
    }
}
