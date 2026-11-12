using System.Text;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Diagnostic = Microsoft.CodeAnalysis.Diagnostic;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace Codalus.LanguageServices.Host.Features;

/// <summary>
/// <see cref="ExpressionCompilerFeature"/> for Visual Basic (M58): the debugger's own evaluator reads C#, so every
/// Visual Basic expression it doesn't happen to read comes here. The same method, <c>Evaluate</c> in a module, with the
/// frame's <c>Me</c>, locals and parameters as its parameters.
/// <para>
/// The Visual Basic compiler has no option to ignore accessibility, so: <c>Friend</c> members are reached through a
/// copy of the program's compilation that names the expression's assembly a friend, and a <c>Private</c> or
/// <c>Protected</c> field or property of <c>Me</c> is read by the debugger and passed in as a parameter
/// (<see cref="CompileExpressionResult.Parameters"/> holds <c>this.Name</c> for it). A private method of <c>Me</c>
/// can't be called from a compiled expression.
/// </para>
/// </summary>
internal static class VisualBasicExpressionCompiler
{
    private const string This = "__this";

    public static async Task<CompileExpressionResult> CompileAsync(Document document, CompileExpressionParams parameters, int count, CancellationToken cancellationToken)
    {
        var tree = (await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false))!;
        var root = (CompilationUnitSyntax)await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
        var model = (await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false))!;
        var original = (VisualBasicCompilation)model.Compilation;
        var position = ExpressionCompilerFeature.PositionOf(await document.GetTextAsync(cancellationToken).ConfigureAwait(false), parameters.Line, parameters.Column);

        var expression = SyntaxFactory.ParseExpression(parameters.Expression, consumeFullText: true);
        if (expression.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(Describe).ToArray() is { Length: > 0 } syntaxErrors)
        {
            return ExpressionCompilerFeature.Failed(syntaxErrors);
        }

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
            return ExpressionCompilerFeature.Failed(problem);
        }

        if (rewriter.NeedsThis && (type is null || member is null or INamedTypeSymbol || member.IsStatic || type.TypeKind == TypeKind.Module))
        {
            return ExpressionCompilerFeature.Failed("'Me' isn't available in a Shared method.");
        }

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
            signature.Add(This + " As " + Display(type!));
        }

        foreach (var (name, expressionText, capturedType) in rewriter.Captured)
        {
            names.Add(expressionText);
            signature.Add("[" + name + "] As " + Display(capturedType));
        }

        var assemblyName = "CodalusExpression_" + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N");

        // The program's compilation with the expression's assembly as a friend: its Friend members are in reach.
        var friend = original.AddSyntaxTrees(SyntaxFactory.ParseSyntaxTree($"<Assembly: Global.System.Runtime.CompilerServices.InternalsVisibleTo(\"{assemblyName}\")>", tree.Options, cancellationToken: cancellationToken));
        var references = new List<MetadataReference> { friend.ToMetadataReference() };
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
        var options = new VisualBasicCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            globalImports: original.Options.GlobalImports,
            optionStrict: OptionStrict.Off,
            optionInfer: true,
            optionExplicit: true,
            optionCompareText: original.Options.OptionCompareText,
            checkOverflow: original.Options.CheckOverflow,
            optimizationLevel: OptimizationLevel.Release,
            concurrentBuild: false);

        CompileExpressionResult? result = null;
        foreach (var statement in (bool[])[false, true])
        {
            var source = Source(root, friends, type, typeParameters, signature, rewritten.ToFullString(), statement);
            var compilation = VisualBasicCompilation.Create(assemblyName, [SyntaxFactory.ParseSyntaxTree(source, tree.Options, cancellationToken: cancellationToken)], references, options);
            using var stream = new MemoryStream();
            var emitted = compilation.Emit(stream, cancellationToken: cancellationToken);
            if (emitted.Success)
            {
                return new CompileExpressionResult(stream.ToArray(), (type?.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." : string.Empty) + ExpressionCompilerFeature.TypeName, ExpressionCompilerFeature.MethodName, [.. names], typeParameters.Count, []);
            }

            var errors = emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            result ??= ExpressionCompilerFeature.Failed([.. errors.Select(Describe).Distinct()]);

            // BC30491: an expression that doesn't produce a value (a Sub's call): as a statement then.
            if (!errors.Any(d => d.Id == "BC30491"))
            {
                break;
            }
        }

        return result!;
    }

    private static string Describe(Diagnostic diagnostic) => diagnostic.Id + ": " + diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture);

    private static readonly SymbolDisplayFormat s_qualified = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    private static string Display(ITypeSymbol type) => Microsoft.CodeAnalysis.VisualBasic.SymbolDisplay.ToDisplayString(type, s_qualified);

    private static string Source(CompilationUnitSyntax root, IEnumerable<string> friends, INamedTypeSymbol? type, List<ITypeParameterSymbol> typeParameters, List<string> signature, string expression, bool statement)
    {
        var source = new StringBuilder();
        source.AppendLine("Option Strict Off").AppendLine("Option Infer On").AppendLine("Option Explicit On");
        foreach (var imports in root.Imports)
        {
            source.AppendLine(imports.WithoutTrivia().NormalizeWhitespace().ToFullString());
        }

        foreach (var friend in friends)
        {
            source.Append("<Assembly: Global.System.Runtime.CompilerServices.IgnoresAccessChecksTo(\"").Append(friend.Replace("\"", "\"\"", StringComparison.Ordinal)).AppendLine("\")>");
        }

        source.AppendLine("Namespace Global.System.Runtime.CompilerServices");
        source.AppendLine("<Global.System.AttributeUsage(Global.System.AttributeTargets.Assembly, AllowMultiple:=True)> Friend NotInheritable Class IgnoresAccessChecksToAttribute");
        source.AppendLine("Inherits Global.System.Attribute").AppendLine("Public Sub New(assemblyName As String)").AppendLine("End Sub").AppendLine("End Class").AppendLine("End Namespace");

        var ns = type?.ContainingNamespace is { IsGlobalNamespace: false } containing ? containing.ToDisplayString() : null;
        if (ns is not null)
        {
            source.Append("Namespace Global.").AppendLine(ns);
        }

        source.Append("Public Module ").AppendLine(ExpressionCompilerFeature.TypeName);
        source.Append("Public Function ").Append(ExpressionCompilerFeature.MethodName);
        if (typeParameters.Count > 0)
        {
            source.Append("(Of ").AppendJoin(", ", typeParameters.Select(p => "[" + p.Name + "]" + Constraints(p))).Append(')');
        }

        source.Append('(').AppendJoin(", ", signature).AppendLine(") As Object");
        source.AppendLine(statement ? expression + "\nReturn Nothing" : "Return CObj(" + expression + "\n)");
        source.AppendLine("End Function").AppendLine("End Module");
        if (ns is not null)
        {
            source.AppendLine("End Namespace");
        }

        return source.ToString();
    }

    private static string Constraints(ITypeParameterSymbol parameter)
    {
        var constraints = new List<string>();
        if (parameter.HasValueTypeConstraint)
        {
            constraints.Add("Structure");
        }
        else if (parameter.HasReferenceTypeConstraint)
        {
            constraints.Add("Class");
        }

        constraints.AddRange(parameter.ConstraintTypes.Select(Display));
        if (parameter.HasConstructorConstraint && !parameter.HasValueTypeConstraint)
        {
            constraints.Add("New");
        }

        return constraints.Count == 0 ? string.Empty : " As {" + string.Join(", ", constraints) + "}";
    }

    /// <summary>As the C# one: the frame's locals and parameters become parameters, <c>Me</c> and its members reached without it get <c>__this</c>.</summary>
    private sealed class Rewriter : VisualBasicSyntaxRewriter
    {
        private readonly SemanticModel _model;
        private readonly int _position;
        private readonly INamedTypeSymbol? _type;
        private readonly HashSet<string> _declared = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _captured = new(StringComparer.OrdinalIgnoreCase);

        public Rewriter(SemanticModel model, int position, INamedTypeSymbol? type, ExpressionSyntax expression)
        {
            _model = model;
            _position = position;
            _type = type;
            foreach (var node in expression.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case ModifiedIdentifierSyntax identifier:
                        _declared.Add(identifier.Identifier.ValueText); // a lambda's parameters, a query's range variables
                        break;
                    case VariableNameEqualsSyntax equals:
                        _declared.Add(equals.Identifier.Identifier.ValueText);
                        break;
                }
            }
        }

        public bool NeedsThis { get; private set; }

        public string? Problem { get; private set; }

        /// <summary>Each parameter: its name in the method, the expression the debugger evaluates for it, and its type.</summary>
        public List<(string Name, string Expression, ITypeSymbol Type)> Captured { get; } = [];

        public override SyntaxNode? VisitMeExpression(MeExpressionSyntax node)
        {
            NeedsThis = true;
            return SyntaxFactory.IdentifierName(This).WithTriviaFrom(node);
        }

        public override SyntaxNode? VisitMyBaseExpression(MyBaseExpressionSyntax node)
        {
            Problem ??= "'MyBase' can't be used in an expression here.";
            return node;
        }

        public override SyntaxNode? VisitMyClassExpression(MyClassExpressionSyntax node)
        {
            Problem ??= "'MyClass' can't be used in an expression here.";
            return node;
        }

        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            // Me.Name of a member the module can't reach: its value, read by the debugger.
            if (node.Expression is MeExpressionSyntax && node.Name is IdentifierNameSyntax name && PrivateValue(name.Identifier.ValueText) is { } value)
            {
                return value.WithTriviaFrom(node);
            }

            return node.Expression is null ? node : node.WithExpression((ExpressionSyntax)Visit(node.Expression));
        }

        public override SyntaxNode? VisitQualifiedName(QualifiedNameSyntax node) => node;

        public override SyntaxNode? VisitNameColonEquals(NameColonEqualsSyntax node) => node;

        public override SyntaxNode? VisitNamedFieldInitializer(NamedFieldInitializerSyntax node) => node.WithExpression((ExpressionSyntax)Visit(node.Expression));

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node) => Name(node) ?? base.VisitIdentifierName(node);

        public override SyntaxNode? VisitGenericName(GenericNameSyntax node) => Name(node) ?? node;

        /// <summary>A parameter for the value of a field or property of <c>Me</c> the module can't reach; null for a member it can, or a method.</summary>
        private IdentifierNameSyntax? PrivateValue(string name)
        {
            if (_type is null || _model.LookupSymbols(_position, name: name).FirstOrDefault(s => s is IFieldSymbol or IPropertySymbol) is not { ContainingType: { } owner } member
                || member.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal)
            {
                return null;
            }

            // The debugger's evaluator reads it: through this, or by its type for a Shared one.
            var parameter = "__me_" + member.Name;
            if (_captured.TryAdd(parameter, name))
            {
                var receiver = member.IsStatic ? owner.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) : CompileExpressionResult.This;
                Captured.Add((parameter, receiver + "." + member.Name, member is IFieldSymbol field ? field.Type : ((IPropertySymbol)member).Type));
            }

            return SyntaxFactory.IdentifierName(parameter);
        }

        private SyntaxNode? Name(SimpleNameSyntax node)
        {
            var name = node.Identifier.ValueText;
            if (IsType(node) || _declared.Contains(name))
            {
                return null;
            }

            var symbols = _model.LookupSymbols(_position, name: name);
            if (symbols.FirstOrDefault(s => s is ILocalSymbol or IParameterSymbol) is { } variable)
            {
                if (_captured.TryAdd(variable.Name, variable.Name))
                {
                    Captured.Add((variable.Name, variable.Name, variable is ILocalSymbol local ? local.Type : ((IParameterSymbol)variable).Type));
                }

                return null;
            }

            if (symbols.FirstOrDefault(s => s is IFieldSymbol or IPropertySymbol or IEventSymbol or IMethodSymbol { MethodKind: MethodKind.Ordinary }) is { ContainingType: { } owner } member
                && _type is not null && (member.IsStatic || InheritsFrom(_type, owner)))
            {
                if (node is IdentifierNameSyntax && PrivateValue(name) is { } value)
                {
                    return value.WithTriviaFrom(node);
                }

                if (member is IMethodSymbol && member.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
                {
                    Problem ??= $"'{name}' is a {member.DeclaredAccessibility.ToString().ToLowerInvariant()} method, which a compiled Visual Basic expression can't call.";
                    return null;
                }

                if (member.IsStatic)
                {
                    // A Shared member: by its type, which isn't around the module.
                    return SyntaxFactory.SimpleMemberAccessExpression(SyntaxFactory.ParseTypeName(Display(owner)), node.WithoutTrivia()).WithTriviaFrom(node);
                }

                NeedsThis = true;
                return SyntaxFactory.SimpleMemberAccessExpression(SyntaxFactory.IdentifierName(This), node.WithoutTrivia()).WithTriviaFrom(node);
            }

            return null;
        }

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

        private static bool IsType(SimpleNameSyntax node) => node.Parent switch
        {
            TypeArgumentListSyntax or GetTypeExpressionSyntax or ArrayTypeSyntax or NullableTypeSyntax or SimpleAsClauseSyntax or ArrayCreationExpressionSyntax => true,
            ObjectCreationExpressionSyntax creation => creation.Type == node,
            CastExpressionSyntax cast => cast.Type == node,
            TypeOfExpressionSyntax typeOf => typeOf.Type == node,
            _ => false,
        };
    }
}
