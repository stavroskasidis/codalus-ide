using System.Reflection;
using System.Text;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Diagnostic = Microsoft.CodeAnalysis.Diagnostic;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace Codalus.LanguageServices.Host.Features;

/// <summary>
/// Compiles a debugger expression the Codalus debugger's own evaluator doesn't read (M58, ADR 0094: lambdas, LINQ,
/// <c>typeof</c>, patterns, generic and extension methods…) against the place the program is stopped at. The result is
/// a small assembly with one static method, <c>Evaluate</c>, which takes what the expression uses of the frame as
/// parameters (<c>this</c>, then the locals and parameters by name) and returns the value as an object; the debugger
/// loads it into the program and calls it.
/// <para>
/// The expression is bound where the frame is: the file's usings and namespace, the method's type parameters (the
/// method is generic over them, in the runtime's order: the types' first), and names looked up at that position. A
/// member reached without a receiver gets one (<c>Count</c> is <c>__this.Count</c>). The debugger reads what the
/// program's own code couldn't, so accessibility isn't checked: the compilation ignores it (two compiler options
/// that aren't public, set through reflection, <see cref="IgnoreAccessibility"/>) and the assembly says
/// <c>IgnoresAccessChecksTo</c> every assembly it references, which the runtime honors.
/// </para>
/// </summary>
internal sealed class ExpressionCompilerFeature(Func<Solution> currentSolution)
{
    public const string TypeName = "__CodalusExpression";
    public const string MethodName = "Evaluate";
    private const string This = "__this";
    private int _count;

    public async Task<CompileExpressionResult> CompileAsync(CompileExpressionParams parameters, CancellationToken cancellationToken)
    {
        var solution = currentSolution();
        var documents = solution.GetDocumentIdsWithFilePath(parameters.SourcePath).Select(solution.GetDocument).OfType<Document>().ToList();
        var module = parameters.ModulePath is { Length: > 0 } path ? Path.GetFileNameWithoutExtension(path) : null;
        var document = documents.FirstOrDefault(d => module is not null && string.Equals(d.Project.AssemblyName, module, StringComparison.OrdinalIgnoreCase)
                           && (parameters.ModulePath is null || d.Project.OutputFilePath is null || SameFolder(d.Project.OutputFilePath, parameters.ModulePath)))
                       ?? documents.FirstOrDefault(d => module is not null && string.Equals(d.Project.AssemblyName, module, StringComparison.OrdinalIgnoreCase))
                       ?? documents.FirstOrDefault();
        if (document is null)
        {
            return Failed("The frame's source file isn't in a loaded project, so the expression can't be compiled.");
        }

        try
        {
            return document.Project.Language switch
            {
                LanguageNames.CSharp => await CSharpAsync(document, parameters, cancellationToken).ConfigureAwait(false),
                LanguageNames.VisualBasic => await VisualBasicExpressionCompiler.CompileAsync(document, parameters, Interlocked.Increment(ref _count), cancellationToken).ConfigureAwait(false),
                _ => Failed($"{document.Project.Language} expressions can't be compiled here."),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return Failed("The expression couldn't be compiled: " + ex.Message);
        }
    }

    internal static CompileExpressionResult Failed(params string[] errors) => new(null, null, null, [], 0, errors);

    private static bool SameFolder(string left, string right) =>
        string.Equals(Path.GetFileName(Path.GetDirectoryName(left)), Path.GetFileName(Path.GetDirectoryName(right)), StringComparison.OrdinalIgnoreCase);

    /// <summary>The position in <paramref name="text"/> of a 1-based line and column (the line's first character that isn't a space without one).</summary>
    internal static int PositionOf(SourceText text, int line, int column)
    {
        var index = Math.Clamp(line - 1, 0, text.Lines.Count - 1);
        var span = text.Lines[index];
        if (column > 0)
        {
            return Math.Min(span.Start + column - 1, span.End);
        }

        var content = text.ToString(span.Span);
        return span.Start + (content.Length - content.TrimStart().Length);
    }

    private async Task<CompileExpressionResult> CSharpAsync(Document document, CompileExpressionParams parameters, CancellationToken cancellationToken)
    {
        var tree = (CSharpSyntaxTree)(await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false))!;
        var root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
        var model = (await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false))!;
        var original = (CSharpCompilation)model.Compilation;
        var position = PositionOf(await document.GetTextAsync(cancellationToken).ConfigureAwait(false), parameters.Line, parameters.Column);

        var expression = SyntaxFactory.ParseExpression(parameters.Expression, options: tree.Options, consumeFullText: true);
        if (expression.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(Describe).ToArray() is { Length: > 0 } syntaxErrors)
        {
            return Failed(syntaxErrors);
        }

        // Where the frame is: its member (out of lambdas and local functions), its type, and whether it has a this.
        var member = model.GetEnclosingSymbol(position, cancellationToken);
        while (member is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction })
        {
            member = member.ContainingSymbol;
        }

        var type = member as INamedTypeSymbol ?? member?.ContainingType;
        var rewriter = new Rewriter(model, position, type, expression);
        var rewritten = (ExpressionSyntax)rewriter.Visit(expression);
        if (rewriter.Problem is { } problem)
        {
            return Failed(problem);
        }

        if (rewriter.NeedsThis && (type is null || member is null or INamedTypeSymbol || member.IsStatic))
        {
            return Failed("'this' isn't available in a static method.");
        }

        if (rewriter.Captured.FirstOrDefault(c => c.Type.IsAnonymousType) is { Name: { } anonymous })
        {
            return Failed($"'{anonymous}' has an anonymous type, which a compiled expression can't take.");
        }

        // The type parameters in scope, as the runtime orders a frame's type arguments: the types', outermost first, then the method's.
        var typeParameters = new List<ITypeParameterSymbol>();
        var containing = new Stack<INamedTypeSymbol>();
        for (var current = type; current is not null; current = current.ContainingType)
        {
            containing.Push(current);
        }

        typeParameters.AddRange(containing.SelectMany(t => t.TypeParameters));
        typeParameters.AddRange((member as IMethodSymbol)?.TypeParameters ?? []);

        var names = new List<string>();
        var signature = new List<string>();
        if (rewriter.NeedsThis)
        {
            names.Add(CompileExpressionResult.This);
            signature.Add(type!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + " " + This);
        }

        foreach (var (name, capturedType) in rewriter.Captured)
        {
            names.Add(name);
            signature.Add(capturedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + " @" + name);
        }

        var assemblyName = "CodalusExpression_" + Interlocked.Increment(ref _count).ToString(System.Globalization.CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N");
        var references = new List<MetadataReference> { original.ToMetadataReference() };
        references.AddRange(original.References);
        var friends = new SortedSet<string>(StringComparer.Ordinal) { original.AssemblyName ?? string.Empty };
        foreach (var reference in original.References)
        {
            if (original.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly)
            {
                friends.Add(assembly.Identity.Name);
            }
        }

        friends.Remove(string.Empty);
        var options = IgnoreAccessibility(new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            allowUnsafe: true,
            optimizationLevel: OptimizationLevel.Release,
            checkOverflow: original.Options.CheckOverflow,
            nullableContextOptions: NullableContextOptions.Disable,
            concurrentBuild: false));

        // As a value first; an expression without one (a call of a method that returns nothing) as a statement.
        CompileExpressionResult? result = null;
        foreach (var statement in (bool[])[false, true])
        {
            var source = Source(root, position, original, friends, typeParameters, signature, rewritten.ToFullString(), statement);
            var compilation = CSharpCompilation.Create(assemblyName, [CSharpSyntaxTree.ParseText(source, tree.Options, cancellationToken: cancellationToken)], references, options);
            using var stream = new MemoryStream();
            var emitted = compilation.Emit(stream, cancellationToken: cancellationToken);
            if (emitted.Success)
            {
                return new CompileExpressionResult(stream.ToArray(), (type?.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." : string.Empty) + TypeName, MethodName, [.. names], typeParameters.Count, []);
            }

            var errors = emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            result ??= Failed([.. errors.Select(Describe).Distinct()]);

            // CS0030, CS0029: void isn't converted to object; CS0201: only some expressions are statements (then the first errors stand).
            if (!errors.Any(d => d.Id is "CS0030" or "CS0029" && d.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("'void'", StringComparison.Ordinal)))
            {
                break;
            }
        }

        return result!;
    }

    private static string Describe(Diagnostic diagnostic) => diagnostic.Id + ": " + diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The compilation unit around the expression: the usings in scope at the position, the namespace, the attributes that let the assembly reach non-public members, and the method.</summary>
    private static string Source(SyntaxNode root, int position, CSharpCompilation original, IEnumerable<string> friends, List<ITypeParameterSymbol> typeParameters, List<string> signature, string expression, bool statement)
    {
        var source = new StringBuilder();
        source.AppendLine("#nullable disable").AppendLine("#pragma warning disable");

        // The file's extern aliases and usings, the other files' global usings, then the usings of the namespaces around the position.
        var unit = (CompilationUnitSyntax)root;
        foreach (var alias in unit.Externs)
        {
            source.AppendLine(alias.WithoutTrivia().ToFullString());
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directive in original.SyntaxTrees.Where(t => t != root.SyntaxTree).SelectMany(t => ((CompilationUnitSyntax)t.GetRoot()).Usings.Where(u => u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword))).Concat(unit.Usings))
        {
            var text = directive.WithGlobalKeyword(default).WithoutTrivia().NormalizeWhitespace().ToFullString();
            if (seen.Add(text))
            {
                source.AppendLine(text);
            }
        }

        foreach (var friend in friends)
        {
            source.Append("[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo(\"").Append(friend.Replace("\"", "\\\"", StringComparison.Ordinal)).AppendLine("\")]");
        }

        source.AppendLine("namespace System.Runtime.CompilerServices { [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true)] internal sealed class IgnoresAccessChecksToAttribute : System.Attribute { public IgnoresAccessChecksToAttribute(string assemblyName) { } } }");

        var namespaces = root.FindToken(position).Parent?.AncestorsAndSelf().OfType<BaseNamespaceDeclarationSyntax>().Reverse().ToList() ?? [];
        foreach (var declaration in namespaces)
        {
            source.Append("namespace ").Append(declaration.Name.WithoutTrivia().ToFullString()).AppendLine(" {");
            foreach (var directive in declaration.Usings)
            {
                source.AppendLine(directive.WithoutTrivia().NormalizeWhitespace().ToFullString());
            }
        }

        source.Append("public static class ").Append(TypeName).AppendLine(" {");
        source.Append("public static object ").Append(MethodName);
        if (typeParameters.Count > 0)
        {
            source.Append('<').AppendJoin(", ", typeParameters.Select(p => "@" + p.Name)).Append('>');
        }

        source.Append('(').AppendJoin(", ", signature).Append(')');
        foreach (var parameter in typeParameters)
        {
            if (Constraints(parameter) is { Length: > 0 } constraints)
            {
                source.Append(" where @").Append(parameter.Name).Append(" : ").Append(constraints);
            }
        }

        source.AppendLine().AppendLine("{");
        source.AppendLine(statement ? expression + ";\nreturn null;" : "return (object)(" + expression + "\n);");
        source.AppendLine("} }");
        source.Append('}', namespaces.Count);
        return source.ToString();
    }

    /// <summary>A type parameter's constraints as its declaration writes them, so what the frame's code can do with it the expression can.</summary>
    private static string Constraints(ITypeParameterSymbol parameter)
    {
        var constraints = new List<string>();
        if (parameter.HasUnmanagedTypeConstraint)
        {
            constraints.Add("unmanaged");
        }
        else if (parameter.HasValueTypeConstraint)
        {
            constraints.Add("struct");
        }
        else if (parameter.HasReferenceTypeConstraint)
        {
            constraints.Add("class");
        }
        else if (parameter.HasNotNullConstraint)
        {
            constraints.Add("notnull");
        }

        constraints.AddRange(parameter.ConstraintTypes.Select(t => t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
        if (parameter.HasConstructorConstraint && !parameter.HasValueTypeConstraint && !parameter.HasUnmanagedTypeConstraint)
        {
            constraints.Add("new()");
        }

        return string.Join(", ", constraints);
    }

    /// <summary>
    /// The options with accessibility ignored: every member of the references imported (<c>MetadataImportOptions.All</c>)
    /// and the binder told not to check access (<c>BinderFlags.IgnoreAccessibility</c>), as the compiler's own expression
    /// evaluator sets them. Neither setter is public; a Roslyn that renames them fails here, and
    /// <c>ExpressionCompilerTests</c> with it.
    /// </summary>
    internal static CSharpCompilationOptions IgnoreAccessibility(CSharpCompilationOptions options)
    {
        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var type = typeof(CSharpCompilationOptions);
        var import = type.GetMethod("WithMetadataImportOptions", Instance, [typeof(MetadataImportOptions)])
            ?? throw new NotSupportedException("This Roslyn has no CSharpCompilationOptions.WithMetadataImportOptions.");
        var flags = type.Assembly.GetType("Microsoft.CodeAnalysis.CSharp.BinderFlags") ?? throw new NotSupportedException("This Roslyn has no BinderFlags.");
        var binder = type.GetMethod("WithTopLevelBinderFlags", Instance, [flags]) ?? throw new NotSupportedException("This Roslyn has no CSharpCompilationOptions.WithTopLevelBinderFlags.");
        options = (CSharpCompilationOptions)import.Invoke(options, [MetadataImportOptions.All])!;
        return (CSharpCompilationOptions)binder.Invoke(options, [Enum.Parse(flags, "IgnoreAccessibility")])!;
    }

    /// <summary>
    /// Finds what the expression takes from the frame, and gives members reached without a receiver theirs: a simple
    /// name that is a local or a parameter at the position becomes a parameter of the method; one that is an instance
    /// member of the frame's type becomes <c>__this.Name</c>; <c>this</c> becomes <c>__this</c>.
    /// </summary>
    private sealed class Rewriter : CSharpSyntaxRewriter
    {
        private readonly SemanticModel _model;
        private readonly int _position;
        private readonly INamedTypeSymbol? _type;
        private readonly HashSet<string> _declared = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ITypeSymbol> _captured = new(StringComparer.Ordinal);

        public Rewriter(SemanticModel model, int position, INamedTypeSymbol? type, ExpressionSyntax expression)
        {
            _model = model;
            _position = position;
            _type = type;

            // The names the expression declares itself (a lambda's parameters, pattern and out variables, a query's range
            // variables) aren't the frame's.
            foreach (var node in expression.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case ParameterSyntax parameter:
                        _declared.Add(parameter.Identifier.ValueText);
                        break;
                    case SingleVariableDesignationSyntax designation:
                        _declared.Add(designation.Identifier.ValueText);
                        break;
                    case FromClauseSyntax from:
                        _declared.Add(from.Identifier.ValueText);
                        break;
                    case LetClauseSyntax let:
                        _declared.Add(let.Identifier.ValueText);
                        break;
                    case JoinClauseSyntax join:
                        _declared.Add(join.Identifier.ValueText);
                        break;
                    case JoinIntoClauseSyntax into:
                        _declared.Add(into.Identifier.ValueText);
                        break;
                    case QueryContinuationSyntax continuation:
                        _declared.Add(continuation.Identifier.ValueText);
                        break;
                }
            }
        }

        public bool NeedsThis { get; private set; }

        public string? Problem { get; private set; }

        /// <summary>The frame's locals and parameters the expression uses, in the order it first names them.</summary>
        public List<(string Name, ITypeSymbol Type)> Captured { get; } = [];

        public override SyntaxNode? VisitThisExpression(ThisExpressionSyntax node)
        {
            NeedsThis = true;
            return SyntaxFactory.IdentifierName(This).WithTriviaFrom(node);
        }

        public override SyntaxNode? VisitBaseExpression(BaseExpressionSyntax node)
        {
            Problem ??= "'base' can't be used in an expression here.";
            return node;
        }

        // The name after a dot is the receiver's member, a named argument's label and an initializer's left side aren't
        // looked up at the frame, and neither is a property pattern's name.
        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node) => node.WithExpression((ExpressionSyntax)Visit(node.Expression));

        public override SyntaxNode? VisitMemberBindingExpression(MemberBindingExpressionSyntax node) => node;

        public override SyntaxNode? VisitQualifiedName(QualifiedNameSyntax node) => node;

        public override SyntaxNode? VisitAliasQualifiedName(AliasQualifiedNameSyntax node) => node;

        public override SyntaxNode? VisitNameColon(NameColonSyntax node) => node;

        public override SyntaxNode? VisitNameEquals(NameEqualsSyntax node) => node;

        public override SyntaxNode? VisitExpressionColon(ExpressionColonSyntax node) => node;

        public override SyntaxNode? VisitInitializerExpression(InitializerExpressionSyntax node)
        {
            if (!node.IsKind(SyntaxKind.ObjectInitializerExpression) && !node.IsKind(SyntaxKind.WithInitializerExpression))
            {
                return base.VisitInitializerExpression(node);
            }

            return node.WithExpressions(SyntaxFactory.SeparatedList(
                node.Expressions.Select(e => e is AssignmentExpressionSyntax { Left: IdentifierNameSyntax } assignment ? assignment.WithRight((ExpressionSyntax)Visit(assignment.Right)) : (ExpressionSyntax)Visit(e)),
                node.Expressions.GetSeparators()));
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node) => Name(node) ?? base.VisitIdentifierName(node);

        // Its type arguments are types; the name itself may be a method or a nested type of the frame's type.
        public override SyntaxNode? VisitGenericName(GenericNameSyntax node) =>
            Name(node) ?? node.WithTypeArgumentList((TypeArgumentListSyntax)Visit(node.TypeArgumentList));

        /// <summary>The name with what the method needs to mean the same there as at the frame; null when it's fine as it is.</summary>
        private SyntaxNode? Name(SimpleNameSyntax node)
        {
            var name = node.Identifier.ValueText;
            if (_declared.Contains(name) || node.Identifier.IsKind(SyntaxKind.GlobalKeyword))
            {
                return null;
            }

            var symbols = _model.LookupSymbols(_position, name: name);
            var typeOnly = IsType(node);
            if (!typeOnly && symbols.FirstOrDefault(s => s is ILocalSymbol or IParameterSymbol or IRangeVariableSymbol) is { } variable)
            {
                var type = variable switch
                {
                    ILocalSymbol local => local.Type,
                    IParameterSymbol parameter => parameter.Type,
                    _ => null,
                };
                if (type is null)
                {
                    Problem ??= $"'{name}' is a query's range variable, which an expression can't use here.";
                    return null;
                }

                if (_captured.TryAdd(name, type))
                {
                    Captured.Add((name, type));
                }

                return null;
            }

            if (!typeOnly && symbols.Any(s => s is IMethodSymbol { MethodKind: MethodKind.LocalFunction }))
            {
                Problem ??= $"'{name}' is a local function, which an expression can't call here.";
                return null;
            }

            if (!typeOnly && symbols.FirstOrDefault(s => s is IFieldSymbol or IPropertySymbol or IEventSymbol or IMethodSymbol { MethodKind: MethodKind.Ordinary }) is { ContainingType: { } owner } member)
            {
                if (member.IsStatic)
                {
                    // A static member of the frame's type, a base or an enclosing type: by its type, which isn't around the method.
                    return SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, TypeSyntax(owner), node.WithoutTrivia()).WithTriviaFrom(node);
                }

                // An instance member of the frame's type or of a base.
                if (_type is not null && InheritsFrom(_type, owner))
                {
                    NeedsThis = true;
                    return SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName(This), node.WithoutTrivia()).WithTriviaFrom(node);
                }

                return null;
            }

            // A nested type named without its outer type, which only the outer type's own code can do.
            if (symbols.Length > 0 && symbols.All(s => s is INamedTypeSymbol { ContainingType: not null }) && symbols[0].ContainingType is { } outer)
            {
                var inner = node is GenericNameSyntax generic ? generic.WithTypeArgumentList((TypeArgumentListSyntax)Visit(generic.TypeArgumentList)) : node;
                return typeOnly
                    ? SyntaxFactory.QualifiedName((NameSyntax)TypeSyntax(outer), inner.WithoutTrivia()).WithTriviaFrom(node)
                    : SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, TypeSyntax(outer), inner.WithoutTrivia()).WithTriviaFrom(node);
            }

            return null;
        }

        private static TypeSyntax TypeSyntax(INamedTypeSymbol type) => SyntaxFactory.ParseTypeName(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

        private static bool InheritsFrom(INamedTypeSymbol type, INamedTypeSymbol? ancestor)
        {
            for (var current = type; current is not null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, ancestor?.OriginalDefinition))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Whether a name stands where only a type can: a member of the same name isn't meant there.</summary>
        private static bool IsType(SimpleNameSyntax node) => node.Parent switch
        {
            TypeArgumentListSyntax or TypeOfExpressionSyntax or DefaultExpressionSyntax or SizeOfExpressionSyntax or ArrayTypeSyntax or NullableTypeSyntax or PointerTypeSyntax
                or TypePatternSyntax or ArrayCreationExpressionSyntax or VariableDeclarationSyntax or RefTypeSyntax or TupleElementSyntax => true,
            ObjectCreationExpressionSyntax creation => creation.Type == node,
            CastExpressionSyntax cast => cast.Type == node,
            DeclarationPatternSyntax pattern => pattern.Type == node,
            RecursivePatternSyntax pattern => pattern.Type == node,
            DeclarationExpressionSyntax declaration => declaration.Type == node,
            ParameterSyntax parameter => parameter.Type == node,
            BinaryExpressionSyntax binary => binary.Right == node && (binary.IsKind(SyntaxKind.AsExpression) || binary.IsKind(SyntaxKind.IsExpression)),
            _ => false,
        };
    }
}
