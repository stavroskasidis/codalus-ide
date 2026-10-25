using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Location = Codalus.LanguageServices.Protocol.Location;

namespace Codalus.LanguageServices.Host.Features;

/// <summary>
/// Code Metrics (M12b), computed the way the classic IDE and <c>roslyn-analyzers</c>' metrics do:
/// <list type="bullet">
/// <item>cyclomatic complexity: 1 per member plus 1 per branch (conditions, loops, case labels, switch arms, catch
/// clauses, <c>&amp;&amp;</c>, <c>||</c>, <c>??</c> and <c>?.</c>);</item>
/// <item>maintainability index: <c>max(0, (171 − 5.2 ln V − 0.23 CC − 16.2 ln LOC) × 100 / 171)</c> with the Halstead
/// volume <c>V</c> from the member's tokens;</item>
/// <item>depth of inheritance: base classes up to <c>object</c>;</item>
/// <item>class coupling: the distinct types a member or type uses, other than itself and the special types;</item>
/// <item>lines: the lines of source code, and the executable statements.</item>
/// </list>
/// Types, namespaces and projects add up their members' complexity and lines, take the union of the coupled types and
/// the average maintainability index.
/// </summary>
internal static class CodeMetricsFeature
{
    public static async Task<CodeMetricsResult> MetricsAsync(Solution solution, ProjectId? only, ProjectId miscellaneous, CancellationToken cancellationToken)
    {
        var projects = new List<CodeMetricsItem>();
        foreach (var project in solution.Projects.Where(p => p.Language == LanguageNames.CSharp && p.Id != miscellaneous && (only is null || p.Id == only)))
        {
            projects.Add(await ProjectAsync(project, cancellationToken).ConfigureAwait(false));
        }

        return new CodeMetricsResult([.. projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)]);
    }

    private static async Task<CodeMetricsItem> ProjectAsync(Project project, CancellationToken cancellationToken)
    {
        var types = new Dictionary<INamedTypeSymbol, List<(Metrics Metrics, CodeMetricsItem Item)>>(SymbolEqualityComparer.Default);
        var typeLocations = new Dictionary<INamedTypeSymbol, Location>(SymbolEqualityComparer.Default);
        foreach (var document in project.Documents)
        {
            if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } root
                || await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model
                || root.SyntaxTree.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);

            // Top-level statements: one member of the generated Program class.
            if (root is CompilationUnitSyntax unit && unit.Members.OfType<GlobalStatementSyntax>().ToList() is { Count: > 0 } globals
                && model.Compilation.GetEntryPoint(cancellationToken) is { ContainingType: { } program })
            {
                if (!types.TryGetValue(program, out var programMembers))
                {
                    types[program] = programMembers = [];
                    typeLocations[program] = new Location(WorkspaceManager.UriOf(document), Positions.ToRange(text, new Microsoft.CodeAnalysis.Text.TextSpan(globals[0].SpanStart, 0)));
                }

                var metrics = Measure(model, globals[0], [.. globals], program, cancellationToken);
                programMembers.Add((metrics, Item("<top-level statements>", "member", metrics, 0, []) with
                {
                    Location = typeLocations[program],
                }));
            }
            foreach (var type in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(type, cancellationToken) is not { } typeSymbol)
                {
                    continue;
                }

                if (!types.TryGetValue(typeSymbol, out var members))
                {
                    types[typeSymbol] = members = [];
                    typeLocations[typeSymbol] = new Location(WorkspaceManager.UriOf(document), Positions.ToRange(text, type.Identifier.Span));
                }

                foreach (var member in MembersOf(type))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var metrics = Measure(model, member, typeSymbol, cancellationToken);
                    var item = Item(NameOf(model, member, cancellationToken), "member", metrics, 0, []) with
                    {
                        Location = new Location(WorkspaceManager.UriOf(document), Positions.ToRange(text, IdentifierSpan(member))),
                    };
                    members.Add((metrics, item));
                }
            }
        }

        var namespaces = new Dictionary<string, List<(Metrics Metrics, int Depth, CodeMetricsItem Item)>>(StringComparer.Ordinal);
        foreach (var (type, members) in types)
        {
            var metrics = Metrics.Sum(members.Select(m => m.Metrics), type);
            var depth = DepthOf(type);
            var item = Item(type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), "type", metrics, depth,
                [.. members.Select(m => m.Item).OrderBy(i => i.Name, StringComparer.Ordinal)]) with { Location = typeLocations[type] };
            var ns = type.ContainingNamespace is { IsGlobalNamespace: false } containing ? containing.ToDisplayString() : "(global)";
            if (!namespaces.TryGetValue(ns, out var list))
            {
                namespaces[ns] = list = [];
            }

            list.Add((metrics, depth, item));
        }

        var namespaceItems = namespaces.OrderBy(n => n.Key, StringComparer.Ordinal).Select(n =>
        {
            var metrics = Metrics.Sum(n.Value.Select(t => t.Metrics), null);
            return (metrics, depth: n.Value.Max(t => t.Depth), item: Item(n.Key, "namespace", metrics, n.Value.Max(t => t.Depth), [.. n.Value.Select(t => t.Item).OrderBy(i => i.Name, StringComparer.Ordinal)]));
        }).ToList();
        var total = Metrics.Sum(namespaceItems.Select(n => n.metrics), null);
        return Item(project.Name, "project", total, namespaceItems.Count == 0 ? 0 : namespaceItems.Max(n => n.depth), [.. namespaceItems.Select(n => n.item)]);
    }

    private static CodeMetricsItem Item(string name, string kind, Metrics metrics, int depth, CodeMetricsItem[] children) =>
        new(name, kind, metrics.MaintainabilityIndex, metrics.Complexity, depth, metrics.Coupled.Count, metrics.SourceLines, metrics.ExecutableLines, children);

    /// <summary>The members with code or a declaration of their own (fields count as one line each).</summary>
    private static IEnumerable<SyntaxNode> MembersOf(BaseTypeDeclarationSyntax type) => type switch
    {
        TypeDeclarationSyntax declaration => declaration.Members.Where(m => m is not BaseTypeDeclarationSyntax and not DelegateDeclarationSyntax)
            .SelectMany(m => m is BaseFieldDeclarationSyntax field ? field.Declaration.Variables.Cast<SyntaxNode>() : [m]),
        EnumDeclarationSyntax enumeration => enumeration.Members,
        _ => [],
    };

    private static string NameOf(SemanticModel model, SyntaxNode member, CancellationToken cancellationToken) =>
        model.GetDeclaredSymbol(member, cancellationToken) is { } symbol
            ? symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat.WithMemberOptions(SymbolDisplayMemberOptions.IncludeParameters))
            : member.ToString().Split('\n')[0].Trim();

    private static Microsoft.CodeAnalysis.Text.TextSpan IdentifierSpan(SyntaxNode member) => member switch
    {
        MethodDeclarationSyntax m => m.Identifier.Span,
        ConstructorDeclarationSyntax c => c.Identifier.Span,
        PropertyDeclarationSyntax p => p.Identifier.Span,
        EventDeclarationSyntax e => e.Identifier.Span,
        VariableDeclaratorSyntax v => v.Identifier.Span,
        EnumMemberDeclarationSyntax e => e.Identifier.Span,
        _ => member.GetFirstToken().Span,
    };

    private static int DepthOf(INamedTypeSymbol type)
    {
        if (type.TypeKind is not TypeKind.Class)
        {
            return type.TypeKind is TypeKind.Struct or TypeKind.Enum ? 1 : 0;
        }

        var depth = 0;
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            depth++;
        }

        return depth;
    }

    private static Metrics Measure(SemanticModel model, SyntaxNode member, INamedTypeSymbol owner, CancellationToken cancellationToken) =>
        Measure(model, member, [member], owner, cancellationToken);

    /// <summary>
    /// Measures a member from its <paramref name="nodes"/>: the declaration itself, or the global statements of a
    /// program with top-level statements (<paramref name="member"/> is then the first of them).
    /// </summary>
    private static Metrics Measure(SemanticModel model, SyntaxNode member, IReadOnlyList<SyntaxNode> nodes, INamedTypeSymbol owner, CancellationToken cancellationToken)
    {
        var complexity = member is VariableDeclaratorSyntax or EnumMemberDeclarationSyntax ? 0 : 1;
        var coupled = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        if (model.GetDeclaredSymbol(member, cancellationToken) is { } symbol)
        {
            foreach (var type in SignatureTypes(symbol))
            {
                Couple(coupled, type, owner);
            }
        }

        var executable = 0;
        foreach (var node in nodes.SelectMany(n => n.DescendantNodesAndSelf()))
        {
            if (node is StatementSyntax and not BlockSyntax and not LocalFunctionStatementSyntax || node is ArrowExpressionClauseSyntax)
            {
                executable++;
            }

            if (model.GetOperation(node, cancellationToken) is not { } operation || operation.Syntax != node)
            {
                continue;
            }

            complexity += operation switch
            {
                IConditionalOperation or ILoopOperation or ICatchClauseOperation or ICoalesceOperation or IConditionalAccessOperation
                    or ISwitchExpressionArmOperation or ICoalesceAssignmentOperation => 1,
                ISwitchCaseOperation @case => @case.Clauses.Count(c => c.CaseKind != CaseKind.Default),
                IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr } => 1,
                _ => 0,
            };
            Couple(coupled, operation.Type, owner);
            if (operation is IInvocationOperation invocation)
            {
                Couple(coupled, invocation.TargetMethod.ContainingType, owner);
            }
            else if (operation is IMemberReferenceOperation reference)
            {
                Couple(coupled, reference.Member.ContainingType, owner);
            }
        }

        // Fields and enum members have no executable code: one line, no complexity.
        var lines = member is VariableDeclaratorSyntax or EnumMemberDeclarationSyntax ? 1 : nodes.Sum(SourceLines);
        if (member is VariableDeclaratorSyntax { Initializer: not null })
        {
            executable++;
        }

        return new Metrics(complexity, lines, executable, coupled, [MaintainabilityIndex(nodes, complexity, lines)]);
    }

    private static IEnumerable<ITypeSymbol?> SignatureTypes(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.Parameters.Select(p => p.Type).Append(method.ReturnType),
        IPropertySymbol property => property.Parameters.Select(p => p.Type).Append(property.Type),
        IFieldSymbol field => [field.Type],
        IEventSymbol @event => [@event.Type],
        _ => [],
    };

    private static void Couple(HashSet<INamedTypeSymbol> coupled, ITypeSymbol? type, INamedTypeSymbol owner)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                Couple(coupled, array.ElementType, owner);
                return;
            case INamedTypeSymbol named when named.SpecialType == SpecialType.None && !named.IsAnonymousType && named.TypeKind != TypeKind.Error
                                            && !SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, owner.OriginalDefinition):
                coupled.Add(named.OriginalDefinition);
                foreach (var argument in named.TypeArguments)
                {
                    Couple(coupled, argument, owner);
                }

                return;
        }
    }

    /// <summary>Non-blank lines that are not only a comment.</summary>
    private static int SourceLines(SyntaxNode member)
    {
        var count = 0;
        foreach (var line in member.ToString().Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    private static int MaintainabilityIndex(IReadOnlyList<SyntaxNode> nodes, int complexity, int lines)
    {
        var operators = new Dictionary<string, int>(StringComparer.Ordinal);
        var operands = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in nodes.SelectMany(n => n.DescendantTokens()))
        {
            var target = token.Kind() is SyntaxKind.IdentifierToken or SyntaxKind.NumericLiteralToken or SyntaxKind.StringLiteralToken
                or SyntaxKind.CharacterLiteralToken or SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword or SyntaxKind.NullKeyword
                ? operands
                : operators;
            target[token.ValueText] = target.GetValueOrDefault(token.ValueText) + 1;
        }

        var vocabulary = operators.Count + operands.Count;
        var length = operators.Values.Sum() + operands.Values.Sum();
        var volume = vocabulary > 1 ? length * Math.Log2(vocabulary) : 1;
        var index = (171 - 5.2 * Math.Log(Math.Max(1, volume)) - 0.23 * complexity - 16.2 * Math.Log(Math.Max(1, lines))) * 100 / 171;
        return (int)Math.Round(Math.Clamp(index, 0, 100));
    }

    private sealed record Metrics(int Complexity, int SourceLines, int ExecutableLines, HashSet<INamedTypeSymbol> Coupled, List<int> Indexes)
    {
        public int MaintainabilityIndex => Indexes.Count == 0 ? 100 : (int)Math.Round(Indexes.Average());

        public static Metrics Sum(IEnumerable<Metrics> parts, INamedTypeSymbol? owner)
        {
            var coupled = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            int complexity = 0, lines = 0, executable = 0;
            var indexes = new List<int>();
            foreach (var part in parts)
            {
                complexity += part.Complexity;
                lines += part.SourceLines;
                executable += part.ExecutableLines;
                coupled.UnionWith(part.Coupled);
                indexes.AddRange(part.Indexes);
            }

            if (owner is not null)
            {
                foreach (var type in owner.Interfaces.Append(owner.BaseType))
                {
                    Couple(coupled, type, owner);
                }
            }

            return new Metrics(complexity, lines, executable, coupled, indexes);
        }
    }
}
