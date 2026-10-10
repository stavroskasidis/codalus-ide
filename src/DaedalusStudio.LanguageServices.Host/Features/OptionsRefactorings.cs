using System.Text;
using DaedalusStudio.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using RoslynCodeAction = Microsoft.CodeAnalysis.CodeActions.CodeAction;

namespace DaedalusStudio.LanguageServices.Host.Features;

/// <summary>
/// A refactoring that asks before it runs (M12b, ADR 0018): <see cref="Options"/> describes the dialog, and the
/// answers come back with <c>codeAction/resolve</c>. Without answers it runs with the defaults (every item checked).
/// </summary>
internal sealed class OptionsCodeAction(string title, CodeActionOptions options, Func<CodeActionChoices, CancellationToken, Task<Solution>> apply) : RoslynCodeAction
{
    public override string Title => title;

    public override string? EquivalenceKey => "daedalus:" + title;

    public CodeActionOptions Options { get; } = options;

    public Task<Solution> ApplyAsync(CodeActionChoices choices, CancellationToken cancellationToken) => apply(choices, cancellationToken);

    protected override async Task<IEnumerable<CodeActionOperation>> ComputeOperationsAsync(CancellationToken cancellationToken) =>
        [new ApplyChangesOperation(await apply(new CodeActionChoices(Options.Name, [.. Options.Items.Where(i => i.Checked).Select(i => i.Id)]), cancellationToken).ConfigureAwait(false))];
}

/// <summary>
/// The refactorings Visual Studio shows a dialog for, which Roslyn implements behind internal options services:
/// Generate Constructor, Generate Equals and GetHashCode, Generate Overrides, Extract Interface and Change Signature.
/// </summary>
internal static class OptionsRefactorings
{
    private static readonly SymbolDisplayFormat s_type = SymbolDisplayFormat.MinimallyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static async Task<IReadOnlyList<RoslynCodeAction>> ForAsync(Document document, TextSpan span, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null)
        {
            return [];
        }

        var token = root.FindToken(span.Start);
        var actions = new List<RoslynCodeAction>();
        var typeSyntax = token.Parent?.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        var onTypeLine = typeSyntax is not null && (token.Parent == typeSyntax || token.Parent is BaseListSyntax || token.Parent?.Parent is BaseListSyntax || token == typeSyntax.Identifier);
        var memberSyntax = token.Parent?.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault(m => m is not TypeDeclarationSyntax);
        if (typeSyntax is not null && model.GetDeclaredSymbol(typeSyntax, cancellationToken) is INamedTypeSymbol type && type.TypeKind is TypeKind.Class or TypeKind.Struct)
        {
            var onMemberDeclaration = memberSyntax is FieldDeclarationSyntax or PropertyDeclarationSyntax;
            if (onTypeLine || onMemberDeclaration)
            {
                actions.AddRange(TypeActions(document, typeSyntax, type));
            }
        }

        if (memberSyntax is MethodDeclarationSyntax or ConstructorDeclarationSyntax && model.GetDeclaredSymbol(memberSyntax, cancellationToken) is IMethodSymbol { Parameters.Length: > 1 } method
            && (token.Parent == memberSyntax || token.Parent?.AncestorsAndSelf().TakeWhile(n => n != memberSyntax).Any(n => n is ParameterListSyntax) == true))
        {
            actions.Add(ChangeSignature(document, method));
        }

        return actions;
    }

    private static IEnumerable<RoslynCodeAction> TypeActions(Document document, TypeDeclarationSyntax typeSyntax, INamedTypeSymbol type)
    {
        var members = type.GetMembers().Where(m => !m.IsStatic && !m.IsImplicitlyDeclared && m switch
        {
            IFieldSymbol f => !f.IsConst && f.AssociatedSymbol is null,
            IPropertySymbol p => !p.IsIndexer && (p.SetMethod is not null || IsAutoProperty(p)),
            _ => false,
        }).ToList();
        if (members.Count > 0)
        {
            var items = members.Select(m => new OptionItem(m.Name, $"{TypeOf(m).ToDisplayString(s_type)} {m.Name}", true)).ToArray();
            yield return new OptionsCodeAction(
                "Generate constructor…",
                new CodeActionOptions($"Generate Constructor: {type.Name}", items) { Message = "Initialize these members from parameters:" },
                (choices, ct) => GenerateConstructorAsync(document, typeSyntax, type, members, choices, ct));
            yield return new OptionsCodeAction(
                "Generate Equals and GetHashCode…",
                new CodeActionOptions($"Generate Equals and GetHashCode: {type.Name}",
                [
                    .. items,
                    new OptionItem("!equatable", $"Implement IEquatable<{type.Name}>", true),
                    new OptionItem("!operators", "Generate operators == and !=", type.TypeKind == TypeKind.Struct),
                ]) { Message = "Compare these members:" },
                (choices, ct) => GenerateEqualityAsync(document, typeSyntax, type, members, choices, ct));
        }

        var overridable = Overridable(type).ToList();
        if (overridable.Count > 0)
        {
            yield return new OptionsCodeAction(
                "Generate overrides…",
                new CodeActionOptions($"Generate Overrides: {type.Name}", [.. overridable.Select(m => new OptionItem(Key(m), m.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat), false) { Detail = m.ContainingType.Name })]),
                (choices, ct) => GenerateOverridesAsync(document, typeSyntax, overridable, choices, ct));
        }

        var publicMembers = type.GetMembers().Where(m => !m.IsStatic && m.DeclaredAccessibility == Accessibility.Public && !m.IsImplicitlyDeclared && m switch
        {
            IMethodSymbol { MethodKind: MethodKind.Ordinary } => true,
            IPropertySymbol { IsIndexer: false } => true,
            IEventSymbol => true,
            _ => false,
        }).ToList();
        if (publicMembers.Count > 0 && type.TypeKind == TypeKind.Class)
        {
            var name = "I" + type.Name;
            yield return new OptionsCodeAction(
                "Extract interface…",
                new CodeActionOptions($"Extract Interface: {type.Name}", [.. publicMembers.Select(m => new OptionItem(Key(m), m.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat), true))])
                {
                    NameLabel = "Interface name",
                    Name = name,
                    Message = "Members of the interface:",
                },
                (choices, ct) => ExtractInterfaceAsync(document, typeSyntax, type, publicMembers, choices, ct));
        }
    }

    private static bool IsAutoProperty(IPropertySymbol property) =>
        property.ContainingType.GetMembers().OfType<IFieldSymbol>().Any(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, property));

    private static ITypeSymbol TypeOf(ISymbol member) => member switch
    {
        IFieldSymbol f => f.Type,
        IPropertySymbol p => p.Type,
        _ => throw new ArgumentException(member.Name, nameof(member)),
    };

    private static string Key(ISymbol symbol) => DocumentationCommentId.CreateDeclarationId(symbol) ?? symbol.Name;

    private static string ParameterName(string member)
    {
        var trimmed = member.TrimStart('_');
        if (trimmed.StartsWith("m_", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }

        var name = trimmed.Length == 0 ? "value" : char.ToLowerInvariant(trimmed[0]) + trimmed[1..];
        return SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;
    }

    // ------------------------------------------------------------------ generators

    private static Task<Solution> GenerateConstructorAsync(Document document, TypeDeclarationSyntax typeSyntax, INamedTypeSymbol type, List<ISymbol> members, CodeActionChoices choices, CancellationToken cancellationToken)
    {
        var chosen = members.Where(m => choices.Selected.Contains(m.Name)).ToList();
        var parameters = string.Join(", ", chosen.Select(m => $"{TypeOf(m).ToDisplayString(s_type)} {ParameterName(m.Name)}"));
        var body = new StringBuilder();
        foreach (var member in chosen)
        {
            var parameter = ParameterName(member.Name);
            body.Append(parameter.TrimStart('@') == member.Name ? $"this.{member.Name} = {parameter};" : $"{member.Name} = {parameter};").Append('\n');
        }

        var accessibility = type.IsAbstract ? "protected" : "public";
        return InsertMembersAsync(document, typeSyntax, [$"{accessibility} {typeSyntax.Identifier.ValueText}({parameters})\n{{\n{body}}}"], afterFields: true, cancellationToken);
    }

    private static Task<Solution> GenerateEqualityAsync(Document document, TypeDeclarationSyntax typeSyntax, INamedTypeSymbol type, List<ISymbol> members, CodeActionChoices choices, CancellationToken cancellationToken)
    {
        var chosen = members.Where(m => choices.Selected.Contains(m.Name)).ToList();
        var equatable = choices.Selected.Contains("!equatable");
        var operators = choices.Selected.Contains("!operators");
        var name = typeSyntax.Identifier.ValueText + (typeSyntax.TypeParameterList?.ToString() ?? string.Empty);
        var isStruct = type.TypeKind == TypeKind.Struct;
        var compare = chosen.Count == 0 ? "true" : string.Join(" && ", chosen.Select(m =>
            TypeOf(m).IsValueType && TypeOf(m).SpecialType != SpecialType.None
                ? $"{m.Name} == other.{m.Name}"
                : $"System.Collections.Generic.EqualityComparer<{TypeOf(m).ToDisplayString(s_type)}>.Default.Equals({m.Name}, other.{m.Name})"));
        var otherType = isStruct ? name : name + "?";
        var members2 = new List<string>
        {
            $"public override bool Equals(object? obj) => obj is {name} other && Equals(other);",
            (equatable ? "public" : "private") + $" bool Equals({otherType} other) => {(isStruct ? string.Empty : "other is not null && ")}{compare};",
            $"public override int GetHashCode() => System.HashCode.Combine({string.Join(", ", chosen.Take(8).Select(m => m.Name))});",
        };
        if (operators)
        {
            members2.Add($"public static bool operator ==({otherType} left, {otherType} right) => {(isStruct ? "left.Equals(right)" : "System.Collections.Generic.EqualityComparer<" + name + "?>.Default.Equals(left, right)")};");
            members2.Add($"public static bool operator !=({otherType} left, {otherType} right) => !(left == right);");
        }

        return equatable
            ? AddBaseAndMembersAsync(document, typeSyntax, $"System.IEquatable<{name}>", members2, cancellationToken)
            : InsertMembersAsync(document, typeSyntax, members2, afterFields: false, cancellationToken);
    }

    private static IEnumerable<ISymbol> Overridable(INamedTypeSymbol type)
    {
        var overridden = type.GetMembers().Select(m => m switch
        {
            IMethodSymbol { OverriddenMethod: { } o } => o,
            IPropertySymbol { OverriddenProperty: { } o } => o,
            IEventSymbol { OverriddenEvent: { } o } => (ISymbol)o,
            _ => null,
        }).OfType<ISymbol>().ToHashSet<ISymbol>(SymbolEqualityComparer.Default);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var b = type.BaseType; b is not null; b = b.BaseType)
        {
            foreach (var member in b.GetMembers().Where(m => (m.IsVirtual || m.IsAbstract || m.IsOverride) && !m.IsSealed && m.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal
                                                             && m is IMethodSymbol { MethodKind: MethodKind.Ordinary } or IPropertySymbol or IEventSymbol))
            {
                var key = member.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat).Split('.').Last();
                if (overridden.Contains(member) || !seen.Add(key))
                {
                    continue;
                }

                yield return member;
            }
        }
    }

    private static Task<Solution> GenerateOverridesAsync(Document document, TypeDeclarationSyntax typeSyntax, List<ISymbol> overridable, CodeActionChoices choices, CancellationToken cancellationToken)
    {
        var code = new List<string>();
        foreach (var member in overridable.Where(m => choices.Selected.Contains(Key(m))))
        {
            var access = member.DeclaredAccessibility switch
            {
                Accessibility.Protected => "protected",
                Accessibility.ProtectedOrInternal => "protected internal",
                _ => "public",
            };
            switch (member)
            {
                case IMethodSymbol method:
                    var parameters = string.Join(", ", method.Parameters.Select(p => $"{RefKind(p)}{p.Type.ToDisplayString(s_type)} {p.Name}"));
                    var arguments = string.Join(", ", method.Parameters.Select(p => RefKind(p) + p.Name));
                    var call = method.IsAbstract ? "throw new System.NotImplementedException()" : $"base.{method.Name}({arguments})";
                    code.Add($"{access} override {method.ReturnType.ToDisplayString(s_type)} {method.Name}({parameters}) => {call};");
                    break;
                case IPropertySymbol property:
                    var get = property.GetMethod is null ? string.Empty : property.IsAbstract ? " get => throw new System.NotImplementedException();" : $" get => base.{property.Name};";
                    var set = property.SetMethod is null ? string.Empty : property.IsAbstract ? " set => throw new System.NotImplementedException();" : $" set => base.{property.Name} = value;";
                    code.Add($"{access} override {property.Type.ToDisplayString(s_type)} {property.Name} {{{get}{set} }}");
                    break;
                case IEventSymbol evt:
                    code.Add($"{access} override event {evt.Type.ToDisplayString(s_type)} {evt.Name} {{ add => base.{evt.Name} += value; remove => base.{evt.Name} -= value; }}");
                    break;
            }
        }

        return InsertMembersAsync(document, typeSyntax, code, afterFields: false, cancellationToken);
    }

    private static string RefKind(IParameterSymbol p) => p.RefKind switch
    {
        Microsoft.CodeAnalysis.RefKind.Ref => "ref ",
        Microsoft.CodeAnalysis.RefKind.Out => "out ",
        Microsoft.CodeAnalysis.RefKind.In => "in ",
        _ => string.Empty,
    };

    private static async Task<Solution> ExtractInterfaceAsync(Document document, TypeDeclarationSyntax typeSyntax, INamedTypeSymbol type, List<ISymbol> members, CodeActionChoices choices, CancellationToken cancellationToken)
    {
        var name = string.IsNullOrWhiteSpace(choices.Name) ? "I" + type.Name : choices.Name.Trim();
        var body = new StringBuilder();
        foreach (var member in members.Where(m => choices.Selected.Contains(Key(m))))
        {
            switch (member)
            {
                case IMethodSymbol method:
                    var typeParameters = method.TypeParameters.Length == 0 ? string.Empty : "<" + string.Join(", ", method.TypeParameters.Select(t => t.Name)) + ">";
                    var parameters = string.Join(", ", method.Parameters.Select(p => $"{RefKind(p)}{p.Type.ToDisplayString(s_type)} {p.Name}"));
                    body.Append(System.Globalization.CultureInfo.InvariantCulture, $"    {method.ReturnType.ToDisplayString(s_type)} {method.Name}{typeParameters}({parameters});\n\n");
                    break;
                case IPropertySymbol property:
                    var accessors = (property.GetMethod is { DeclaredAccessibility: Accessibility.Public } ? " get;" : string.Empty)
                                    + (property.SetMethod is { DeclaredAccessibility: Accessibility.Public } ? (property.SetMethod.IsInitOnly ? " init;" : " set;") : string.Empty);
                    body.Append(System.Globalization.CultureInfo.InvariantCulture, $"    {property.Type.ToDisplayString(s_type)} {property.Name} {{{accessors} }}\n\n");
                    break;
                case IEventSymbol evt:
                    body.Append(System.Globalization.CultureInfo.InvariantCulture, $"    event {evt.Type.ToDisplayString(s_type)} {evt.Name};\n\n");
                    break;
            }
        }

        var root = (CompilationUnitSyntax)(await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false))!;
        var usings = string.Concat(root.Usings.Select(u => u.ToFullString()));
        var ns = type.ContainingNamespace is { IsGlobalNamespace: false } containing ? containing.ToDisplayString() : null;
        var accessibility = type.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";
        var text = usings + (usings.Length > 0 ? "\n" : string.Empty)
                   + (ns is null ? string.Empty : $"namespace {ns};\n\n")
                   + $"{accessibility} interface {name}\n{{\n{body.ToString().TrimEnd('\n')}\n}}\n";

        // The class implements the interface; the interface goes into a new file next to the class's.
        var withBase = await AddBaseAndMembersAsync(document, typeSyntax, name, [], cancellationToken).ConfigureAwait(false);
        var folder = Path.GetDirectoryName(document.FilePath) ?? string.Empty;
        var project = withBase.GetProject(document.Project.Id)!;
        return project.AddDocument(name + ".cs", SourceText.From(text, Encoding.UTF8), document.Folders, Path.Combine(folder, name + ".cs")).Project.Solution;
    }

    // ------------------------------------------------------------------ Change Signature

    private static OptionsCodeAction ChangeSignature(Document document, IMethodSymbol method) => new(
        "Change signature…",
        new CodeActionOptions($"Change Signature: {method.Name}", [.. method.Parameters.Select(p => new OptionItem(p.Name, $"{p.Type.ToDisplayString(s_type)} {p.Name}", true))])
        {
            Reorderable = true,
            Message = "Reorder the parameters, or uncheck one to remove it. Calls are updated.",
        },
        (choices, ct) => ChangeSignatureAsync(document, method, choices, ct));

    private static async Task<Solution> ChangeSignatureAsync(Document document, IMethodSymbol method, CodeActionChoices choices, CancellationToken cancellationToken)
    {
        var order = choices.Selected.Select(name => method.Parameters.FirstOrDefault(p => p.Name == name)).OfType<IParameterSymbol>().Select(p => p.Ordinal).ToList();
        var solution = document.Project.Solution;
        var editors = new Dictionary<DocumentId, List<(TextSpan Span, Func<SyntaxNode, SyntaxNode> Rewrite)>>();
        void Add(Document doc, TextSpan span, Func<SyntaxNode, SyntaxNode> rewrite)
        {
            if (!editors.TryGetValue(doc.Id, out var list))
            {
                editors[doc.Id] = list = [];
            }

            list.Add((span, rewrite));
        }

        // The declaration(s) and every call.
        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            if (solution.GetDocument(reference.SyntaxTree) is { } declarationDocument
                && (await reference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false)) is BaseMethodDeclarationSyntax declaration)
            {
                Add(declarationDocument, declaration.ParameterList.Span, node => Reorder((ParameterListSyntax)node, order));
            }
        }

        foreach (var referenced in await SymbolFinder.FindReferencesAsync(method, solution, cancellationToken).ConfigureAwait(false))
        {
            foreach (var location in referenced.Locations.Where(l => !l.IsImplicit))
            {
                var root = await location.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                var node = root?.FindNode(location.Location.SourceSpan);
                var arguments = node?.AncestorsAndSelf().Select(n => n switch
                {
                    InvocationExpressionSyntax i => i.ArgumentList,
                    BaseObjectCreationExpressionSyntax c => c.ArgumentList,
                    ConstructorInitializerSyntax c => c.ArgumentList,
                    _ => null,
                }).FirstOrDefault(a => a is not null);
                if (arguments is not null)
                {
                    Add(location.Document, arguments.Span, n => Reorder((ArgumentListSyntax)n, order, method));
                }
            }
        }

        foreach (var (id, edits) in editors)
        {
            var doc = solution.GetDocument(id)!;
            var root = (await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false))!;
            var nodes = edits.Select(e => (Node: root.FindNode(e.Span, getInnermostNodeForTie: false), e.Rewrite))
                .Select(e => (Node: e.Node.AncestorsAndSelf().FirstOrDefault(n => n is ParameterListSyntax or ArgumentListSyntax) ?? e.Node, e.Rewrite))
                .DistinctBy(e => e.Node.Span)
                .ToDictionary(e => e.Node, e => e.Rewrite);
            root = root.ReplaceNodes(nodes.Keys, (original, rewritten) => nodes[original](rewritten));
            solution = solution.WithDocumentSyntaxRoot(id, root);
        }

        return solution;
    }

    private static ParameterListSyntax Reorder(ParameterListSyntax list, List<int> order)
    {
        var parameters = order.Where(i => i < list.Parameters.Count).Select((index, position) => list.Parameters[index].WithLeadingTrivia(position == 0 ? list.Parameters[0].GetLeadingTrivia() : SyntaxFactory.TriviaList(SyntaxFactory.Space))).ToList();
        return list.WithParameters(SyntaxFactory.SeparatedList(parameters));
    }

    private static ArgumentListSyntax Reorder(ArgumentListSyntax list, List<int> order, IMethodSymbol method)
    {
        // Positional arguments map to parameters by position; named ones keep their name and follow the positional ones.
        var positional = list.Arguments.TakeWhile(a => a.NameColon is null).ToList();
        var named = list.Arguments.Skip(positional.Count).Where(a => order.Any(i => method.Parameters[i].Name == a.NameColon!.Name.Identifier.ValueText)).ToList();
        var reordered = new List<ArgumentSyntax>();
        foreach (var index in order)
        {
            if (index < positional.Count && !(method.Parameters[index].IsParams && positional.Count > method.Parameters.Length))
            {
                reordered.Add(positional[index]);
            }
            else if (index < positional.Count)
            {
                reordered.AddRange(positional.Skip(index));
            }
        }

        reordered.AddRange(named);
        var firstTrivia = list.Arguments.Count > 0 ? list.Arguments[0].GetLeadingTrivia() : SyntaxFactory.TriviaList();
        var normalized = reordered.Select((a, i) => a.WithLeadingTrivia(i == 0 ? firstTrivia : SyntaxFactory.TriviaList(SyntaxFactory.Space))).ToList();
        return list.WithArguments(SyntaxFactory.SeparatedList(normalized));
    }

    // ------------------------------------------------------------------ inserting members

    private static async Task<Solution> InsertMembersAsync(Document document, TypeDeclarationSyntax typeSyntax, List<string> code, bool afterFields, CancellationToken cancellationToken)
    {
        if (code.Count == 0)
        {
            return document.Project.Solution;
        }

        var members = code.Select(c => SyntaxFactory.ParseMemberDeclaration(c)!.WithAdditionalAnnotations(Formatter.Annotation)).ToList();
        var root = (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false))!;
        var current = (TypeDeclarationSyntax)root.FindNode(typeSyntax.Span);
        var index = afterFields
            ? current.Members.LastIndexOf(m => m is FieldDeclarationSyntax or ConstructorDeclarationSyntax || (m is PropertyDeclarationSyntax p && p.AccessorList?.Accessors.All(a => a.Body is null && a.ExpressionBody is null) == true)) + 1
            : current.Members.Count;
        var updated = current.WithMembers(current.Members.InsertRange(index, members));
        var newDocument = document.WithSyntaxRoot(root.ReplaceNode(current, updated));
        newDocument = await Formatter.FormatAsync(newDocument, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        return newDocument.Project.Solution;
    }

    private static async Task<Solution> AddBaseAndMembersAsync(Document document, TypeDeclarationSyntax typeSyntax, string baseType, List<string> code, CancellationToken cancellationToken)
    {
        var root = (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false))!;
        var current = (TypeDeclarationSyntax)root.FindNode(typeSyntax.Span);
        var baseEntry = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(baseType));
        TypeDeclarationSyntax updated;
        if (current.BaseList is { } list)
        {
            updated = current.WithBaseList(list.AddTypes(baseEntry));
        }
        else
        {
            // ": Base" after the identifier (or type parameters, or a record's parameters), before constraints and the body.
            var newList = SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(baseEntry))
                .WithColonToken(SyntaxFactory.Token(SyntaxKind.ColonToken).WithLeadingTrivia(SyntaxFactory.Space).WithTrailingTrivia(SyntaxFactory.Space));
            var withoutTrailing = current.ParameterList is not null
                ? current.WithParameterList(current.ParameterList.WithTrailingTrivia())
                : current.TypeParameterList is not null
                    ? current.WithTypeParameterList(current.TypeParameterList.WithTrailingTrivia())
                    : current.WithIdentifier(current.Identifier.WithTrailingTrivia());
            updated = withoutTrailing.WithBaseList(newList.WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed));
        }

        if (code.Count > 0)
        {
            updated = updated.WithMembers(updated.Members.AddRange(code.Select(c => SyntaxFactory.ParseMemberDeclaration(c)!.WithAdditionalAnnotations(Formatter.Annotation))));
        }

        var newDocument = document.WithSyntaxRoot(root.ReplaceNode(current, updated.WithAdditionalAnnotations(Formatter.Annotation)));
        newDocument = await Formatter.FormatAsync(newDocument, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        return newDocument.Project.Solution;
    }
}
