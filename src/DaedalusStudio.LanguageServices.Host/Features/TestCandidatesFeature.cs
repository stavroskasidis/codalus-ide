using System.Text;
using DaedalusStudio.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace DaedalusStudio.LanguageServices.Host.Features;

/// <summary>
/// <c>daedalus/testCandidates</c> (ADR 0010): test methods and classes by attribute name, from syntax only. Attribute
/// names are matched without their namespace and <c>Attribute</c> suffix, so custom attributes that follow the usual
/// naming (<c>SkippableFact</c>, <c>AvaloniaTheory</c>, <c>UITestMethod</c>) are found too.
/// </summary>
internal static class TestCandidatesFeature
{
    // xUnit (Fact, Theory and their *Fact/*Theory variants), NUnit, MSTest (and *TestMethod), TUnit.
    private static readonly HashSet<string> s_methodAttributes = new(StringComparer.Ordinal)
    {
        "Test", "TestCase", "TestCaseSource", "Theory", "Fact", "TestMethod", "DataTestMethod",
    };

    private static readonly HashSet<string> s_parameterizedAttributes = new(StringComparer.Ordinal)
    {
        "Theory", "TestCase", "TestCaseSource", "DataTestMethod", "DataRow", "DynamicData", "InlineData", "MemberData", "ClassData",
        "Arguments", "MethodDataSource", "ClassDataSource", "Values", "Range", "Random", "ValueSource",
    };

    private static readonly HashSet<string> s_classAttributes = new(StringComparer.Ordinal) { "TestFixture", "TestClass" };

    public static async Task<TestCandidatesResult> FindAsync(Solution solution, IReadOnlyList<TextDocumentIdentifier> documents, CancellationToken cancellationToken)
    {
        var results = new DocumentTestCandidates[documents.Count];
        await Parallel.ForAsync(0, documents.Count, cancellationToken, async (i, ct) =>
        {
            var uri = documents[i].Uri;
            var candidates = await FindInDocumentAsync(solution, uri, ct).ConfigureAwait(false);
            results[i] = new DocumentTestCandidates(uri, candidates);
        }).ConfigureAwait(false);
        return new TestCandidatesResult(results);
    }

    private static async Task<TestCandidate[]> FindInDocumentAsync(Solution solution, string uri, CancellationToken cancellationToken)
    {
        SyntaxTree tree;
        if (await WorkspaceManager.FindDocumentAsync(solution, uri, cancellationToken).ConfigureAwait(false) is { } document
            && await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false) is { } documentTree)
        {
            tree = documentTree;
        }
        else if (DocumentUris.ToPath(uri) is { } path && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                tree = CSharpSyntaxTree.ParseText(SourceText.From(text), CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), path, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
        else
        {
            return [];
        }

        var root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
        var sourceText = await tree.GetTextAsync(cancellationToken).ConfigureAwait(false);
        return Find(root, sourceText);
    }

    /// <summary>The candidates in a parsed file, in document order.</summary>
    internal static TestCandidate[] Find(SyntaxNode root, SourceText text)
    {
        var results = new List<TestCandidate>();
        foreach (var type in root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax).OfType<TypeDeclarationSyntax>())
        {
            if (type is not (ClassDeclarationSyntax or RecordDeclarationSyntax or StructDeclarationSyntax)
                || type.Modifiers.Any(SyntaxKind.AbstractKeyword) || type.Modifiers.Any(SyntaxKind.StaticKeyword) && !HasAttribute(type.AttributeLists, s_classAttributes))
            {
                continue;
            }

            var ns = Namespace(type);
            var typeName = (ns is null ? string.Empty : ns + ".") + NestedName(type);
            var methods = new List<TestCandidate>();
            foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
            {
                if (TestAttribute(method.AttributeLists) is not { } attribute)
                {
                    continue;
                }

                var parameterized = method.ParameterList.Parameters.Count > 0 || s_parameterizedAttributes.Contains(attribute)
                    || method.AttributeLists.SelectMany(l => l.Attributes).Any(a => s_parameterizedAttributes.Contains(SimpleName(a.Name)));
                methods.Add(new TestCandidate(
                    TestCandidateKinds.Method,
                    method.Identifier.ValueText,
                    typeName,
                    ns,
                    Positions.ToRange(text, method.Span),
                    Positions.ToRange(text, method.Identifier.Span),
                    parameterized));
            }

            if (methods.Count > 0 || HasAttribute(type.AttributeLists, s_classAttributes))
            {
                results.Add(new TestCandidate(
                    TestCandidateKinds.Class,
                    type.Identifier.ValueText,
                    typeName,
                    ns,
                    Positions.ToRange(text, type.Span),
                    Positions.ToRange(text, type.Identifier.Span),
                    Parameterized: false));
                results.AddRange(methods);
            }
        }

        return [.. results];
    }

    private static string? TestAttribute(SyntaxList<AttributeListSyntax> lists)
    {
        foreach (var attribute in lists.SelectMany(l => l.Attributes))
        {
            var name = SimpleName(attribute.Name);
            if (s_methodAttributes.Contains(name))
            {
                return name;
            }

            if (name.EndsWith("Fact", StringComparison.Ordinal))
            {
                return "Fact";
            }

            if (name.EndsWith("Theory", StringComparison.Ordinal))
            {
                return "Theory";
            }

            if (name.EndsWith("TestMethod", StringComparison.Ordinal))
            {
                return "TestMethod";
            }
        }

        return null;
    }

    private static bool HasAttribute(SyntaxList<AttributeListSyntax> lists, HashSet<string> names) =>
        lists.SelectMany(l => l.Attributes).Any(a => names.Contains(SimpleName(a.Name)));

    /// <summary><c>Xunit.FactAttribute</c> → <c>Fact</c>.</summary>
    private static string SimpleName(NameSyntax name)
    {
        var simple = name switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
            SimpleNameSyntax s => s.Identifier.ValueText,
            _ => name.ToString(),
        };
        return simple.EndsWith("Attribute", StringComparison.Ordinal) && simple.Length > "Attribute".Length ? simple[..^"Attribute".Length] : simple;
    }

    private static string? Namespace(SyntaxNode node)
    {
        var parts = new List<string>();
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is BaseNamespaceDeclarationSyntax ns)
            {
                parts.Insert(0, ns.Name.ToString().Replace(" ", string.Empty, StringComparison.Ordinal));
            }
        }

        return parts.Count == 0 ? null : string.Join('.', parts);
    }

    /// <summary><c>Outer+Inner`1</c>: the metadata name within the namespace, as runners report it.</summary>
    private static string NestedName(TypeDeclarationSyntax type)
    {
        var builder = new StringBuilder();
        for (SyntaxNode? node = type; node is TypeDeclarationSyntax declaration; node = node.Parent)
        {
            var name = declaration.Identifier.ValueText;
            if (declaration.TypeParameterList is { Parameters.Count: > 0 } parameters)
            {
                name += "`" + parameters.Parameters.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            builder.Insert(0, builder.Length == 0 ? name : name + "+");
        }

        return builder.ToString();
    }
}
