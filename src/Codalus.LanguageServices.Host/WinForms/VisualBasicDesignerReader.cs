using System.Globalization;
using System.Text;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Codalus.LanguageServices.Host.WinForms;

/// <summary>Helpers over the syntax of Visual Basic designer files (ADR 0073, Visual Basic forms).</summary>
internal static class VisualBasicDesignerSyntax
{
    /// <summary>The class (of the file's classes, nested ones too) whose body declares <c>Sub InitializeComponent()</c>.</summary>
    public static (ClassBlockSyntax Class, MethodBlockSyntax Method)? FindInitializeComponent(SyntaxNode root)
    {
        foreach (var type in root.DescendantNodes(n => n is CompilationUnitSyntax or NamespaceBlockSyntax or ClassBlockSyntax).OfType<ClassBlockSyntax>())
        {
            if (type.Members.OfType<MethodBlockSyntax>().FirstOrDefault(m => m.IsKind(SyntaxKind.SubBlock)
                    && string.Equals(m.SubOrFunctionStatement.Identifier.ValueText, "InitializeComponent", StringComparison.OrdinalIgnoreCase)
                    && (m.SubOrFunctionStatement.ParameterList?.Parameters.Count ?? 0) == 0) is { } method)
            {
                return (type, method);
            }
        }

        return null;
    }

    /// <summary>The namespace the class is declared in (nested namespaces joined; after the project's root namespace), or null.</summary>
    public static string? NamespaceOf(SyntaxNode node, string? rootNamespace)
    {
        var parts = node.Ancestors().OfType<NamespaceBlockSyntax>().Reverse().Select(n => n.NamespaceStatement.Name.ToString()).ToList();
        if (parts.FirstOrDefault() is { } first && first.StartsWith("Global", StringComparison.OrdinalIgnoreCase) && (first.Length == 6 || first[6] == '.'))
        {
            // Namespace Global.X: from the global namespace, not the root namespace.
            parts[0] = first.Length == 6 ? string.Empty : first[7..];
            rootNamespace = null;
        }

        var all = (rootNamespace is { Length: > 0 } ? parts.Prepend(rootNamespace) : parts).Where(p => p.Length > 0).ToList();
        return all.Count > 0 ? string.Join('.', all) : null;
    }

    /// <summary>The <c>Imports</c> namespaces of the file (not aliases or XML namespaces).</summary>
    public static IEnumerable<string> ImportsOf(SyntaxNode root) =>
        root is CompilationUnitSyntax unit
            ? unit.Imports.SelectMany(i => i.ImportsClauses).OfType<SimpleImportsClauseSyntax>().Where(c => c.Alias is null).Select(c => StripGlobal(c.Name.ToString()))
            : [];

    /// <summary>The base type a class's <c>Inherits</c> names, as written.</summary>
    public static TypeSyntax? InheritsOf(ClassBlockSyntax type) => type.Inherits.SelectMany(i => i.Types).FirstOrDefault();

    /// <summary>A name without <c>Global.</c> and spaces.</summary>
    public static string StripGlobal(string text)
    {
        text = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        return text.StartsWith("Global.", StringComparison.OrdinalIgnoreCase) ? text["Global.".Length..] : text;
    }

    /// <summary>The namespaces a .NET Visual Basic Windows Forms project imports (the SDK's and the Windows Forms template's).</summary>
    public static readonly string[] WindowsFormsImplicitImports =
        ["Microsoft.VisualBasic", "System", "System.Collections", "System.Collections.Generic", "System.Data", "System.Diagnostics", "System.Drawing", "System.Linq",
         "System.Threading.Tasks", "System.Windows.Forms", "System.Xml.Linq"];
}

/// <summary>
/// Reads a Visual Basic InitializeComponent (ADR 0073, Visual Basic forms) into the same <see cref="DesignerModel"/> as
/// C#'s: what the classic IDE's designer writes for .NET (<c>Button1 = New Button()</c>) and for .NET Framework
/// (<c>Me.Button1 = New System.Windows.Forms.Button()</c>, CodeDOM's <c>CType</c> and <c>ChrW</c>), components declared
/// <c>Friend WithEvents</c>, events hooked by <c>AddHandler</c> (a <c>Handles</c> clause is the class's, read by
/// <see cref="VisualBasicHandles"/>). Names ignore case as Visual Basic does: each is read as its declaration spells it.
/// TODO(issue): members are read as written, so one written in another case than its declaration's (<c>Button1.text</c>,
/// which Visual Basic compiles) is another property to the designer and the writer; the classic IDE writes them as declared.
/// </summary>
internal static class VisualBasicDesignerReader
{
    private static readonly HashSet<string> s_layoutMethods = new(StringComparer.Ordinal) { "SuspendLayout", "ResumeLayout", "PerformLayout" };

    public static DesignerSource Read(SyntaxTree tree, string baseType, IEnumerable<string> globalImports, IDesignerTypeResolver? resolver, string? rootNamespace)
    {
        var root = tree.GetRoot();
        if (VisualBasicDesignerSyntax.FindInitializeComponent(root) is not var (type, method))
        {
            throw new DesignerReadException("The file has no InitializeComponent method.", null);
        }

        var text = tree.GetText();
        var className = type.ClassStatement.Identifier.ValueText;
        var ns = VisualBasicDesignerSyntax.NamespaceOf(type, rootNamespace);
        var imports = VisualBasicDesignerSyntax.ImportsOf(root).Concat(globalImports).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        resolver ??= new SyntacticDesignerTypeResolver(ns);

        var fields = new Dictionary<string, DesignerField>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in type.Members.OfType<FieldDeclarationSyntax>())
        {
            var declared = field.Declarators.SelectMany(d => d.Names).Select(n => n.Identifier.ValueText).ToArray();
            var modifiers = string.Join(' ', field.Modifiers.Select(m => m.Text));
            foreach (var name in field.Declarators.SelectMany(d => d.Names))
            {
                fields[name.Identifier.ValueText] = new DesignerField(name.Identifier.ValueText, field.Span, name.Identifier.Span, declared, modifiers);
            }
        }

        // The names first, as their declarations spell them: a statement can refer to a component created later.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var spelled = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["components"] = "components" };
        foreach (var statement in method.Statements)
        {
            switch (statement)
            {
                case LocalDeclarationStatementSyntax local:
                    foreach (var name in local.Declarators.SelectMany(d => d.Names))
                    {
                        names.Add(name.Identifier.ValueText);
                        spelled[name.Identifier.ValueText] = name.Identifier.ValueText;
                    }

                    break;
                case AssignmentStatementSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentStatement, Left: var left, Right: ObjectCreationExpressionSyntax }
                    when FieldName(left) is { } created && fields.TryGetValue(created, out var declaredField) && !string.Equals(created, "components", StringComparison.OrdinalIgnoreCase):
                    names.Add(declaredField.Name);
                    spelled[declaredField.Name] = declaredField.Name;
                    break;
            }
        }

        var reader = new Reader(tree, className, ns, resolver, names, spelled);
        var statements = new List<SourceStatement>();
        var components = new List<DesignerComponent> { new(className, baseType) { IsRoot = true } };
        foreach (var statement in method.Statements)
        {
            foreach (var (section, lines) in Sections(text, statement))
            {
                statements.Add(new SourceStatement(new DesignerStatement { Kind = DesignerStatementKinds.Section, Target = section, Line = text.Lines.GetLineFromPosition(lines.Start).LineNumber }, lines));
            }

            var read = reader.Statement(statement);
            statements.Add(new SourceStatement(read, DesignerReader.LinesOf(text, statement.Span), ValueSpan(statement), ArgumentSpans(statement)));
            if (read.Kind == DesignerStatementKinds.Create && read.Target is not ("components" or "") && read.Value?.Type is { } createdType)
            {
                var isLocal = statement is LocalDeclarationStatementSyntax;
                components.Add(new DesignerComponent(read.Target, createdType)
                {
                    IsLocal = isLocal,
                    Modifier = !isLocal && fields.TryGetValue(read.Target, out var field) ? field.Modifiers : null,
                });
            }
        }

        var model = new DesignerModel
        {
            ClassName = className,
            Language = DesignerLanguages.VisualBasic,
            Namespace = ns,
            BaseType = baseType,
            Imports = imports,
            Components = [.. components],
            Statements = [.. statements.Select(s => s.Statement)],
            UsesResources = reader.UsesResources,
            UsesThis = reader.UsesThis,
            QualifiedNames = reader.Qualified,
        };
        var (rule, prefix) = SectionStyle(text, statements, reader.UsesThis);
        var endSub = text.Lines.GetLineFromPosition(method.EndSubOrFunctionStatement.SpanStart);
        var subIndent = DesignerReader.IndentationAt(text, method.SubOrFunctionStatement.DeclarationKeyword.SpanStart);
        return new DesignerSource
        {
            Text = text,
            Tree = tree,
            Model = model,
            Statements = statements,
            Fields = fields,
            Indent = method.Statements.FirstOrDefault() is { } first
                ? DesignerReader.IndentationAt(text, first.SpanStart)
                : subIndent + (subIndent.Contains('\t', StringComparison.Ordinal) ? "\t" : "    "),
            FieldIndent = subIndent,
            NewLine = DesignerReader.NewLineOf(text),
            SectionRule = rule,
            SectionPrefix = prefix,
            BodyStart = statements.Count > 0 ? statements[0].Lines.Start : text.Lines.GetLineFromPosition(method.SubOrFunctionStatement.Span.End).EndIncludingLineBreak,
            BodyEnd = method.Statements.LastOrDefault() is { } last ? DesignerReader.LinesOf(text, last.Span).End : endSub.Start,
            FirstFieldPoint = endSub.EndIncludingLineBreak,
            NameUses = NameUsesOf(type, method, names, spelled),
            ResourceKeys = ResourceKeysOf(method),
            Names = names,
        };
    }

    /// <summary>The field a creation's left side assigns (<c>Button1</c>, <c>Me.Button1</c>, <c>components</c>), as written, or null.</summary>
    private static string? FieldName(ExpressionSyntax left) => left switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        MemberAccessExpressionSyntax { Expression: MeExpressionSyntax, Name: IdentifierNameSyntax name } => name.Identifier.ValueText,
        _ => null,
    };

    private static TextSpan? ValueSpan(StatementSyntax statement) => statement is AssignmentStatementSyntax assignment ? assignment.Right.Span : null;

    private static TextSpan[]? ArgumentSpans(StatementSyntax statement) => InvocationOf(statement) is { ArgumentList: { } list } ? [.. list.Arguments.Select(a => a.Span)] : null;

    private static InvocationExpressionSyntax? InvocationOf(StatementSyntax statement) => statement switch
    {
        ExpressionStatementSyntax { Expression: InvocationExpressionSyntax invocation } => invocation,
        CallStatementSyntax { Invocation: InvocationExpressionSyntax invocation } => invocation,
        _ => null,
    };

    /// <summary>
    /// The classic IDE's section comments in a statement's leading trivia: three comments, the middle one naming the
    /// component (<c>' </c>, <c>' Button1</c>, <c>' </c> for .NET; <c>'</c>, <c>'Button1</c>, <c>'</c> for .NET Framework).
    /// </summary>
    private static IEnumerable<(string Name, TextSpan Lines)> Sections(SourceText text, StatementSyntax statement)
    {
        var comments = statement.GetLeadingTrivia().Where(t => t.IsKind(SyntaxKind.CommentTrivia)).ToList();
        for (var i = 0; i + 2 < comments.Count; i++)
        {
            if (comments[i].ToString().TrimEnd() == "'" && comments[i + 2].ToString().TrimEnd() == "'"
                && comments[i + 1].ToString() is var middle && middle.Length > 1 && middle[0] == '\'')
            {
                var name = middle[1..].Trim();
                if (name.Length > 0 && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '.'))
                {
                    yield return (name, TextSpan.FromBounds(DesignerReader.LinesOf(text, comments[i].Span).Start, DesignerReader.LinesOf(text, comments[i + 2].Span).End));
                    i += 2;
                }
            }
        }
    }

    /// <summary>The file's section comments' rule and prefix; for a file without any, .NET's (<c>' </c>) or, in the <c>Me.</c> style, .NET Framework's (<c>'</c>).</summary>
    private static (string Rule, string Prefix) SectionStyle(SourceText text, List<SourceStatement> statements, bool usesMe)
    {
        if (statements.FirstOrDefault(s => s.Statement.Kind == DesignerStatementKinds.Section) is { } section)
        {
            var line = text.Lines.GetLineFromPosition(section.Lines.Start);
            var rule = line.ToString().TrimStart();
            var middle = text.Lines[line.LineNumber + 1].ToString().TrimStart();
            return (rule == "'" ? "'" : "' ", middle.StartsWith("' ", StringComparison.Ordinal) ? "' " : "'");
        }

        return usesMe ? ("'", "'") : ("' ", "' ");
    }

    /// <summary>
    /// The components' and locals' names as InitializeComponent uses them (<c>Button1</c>, <c>Me.Button1</c>, not
    /// <c>x.Button1</c>), and as the designer file's <c>Handles</c> clauses name them.
    /// </summary>
    private static List<(string, TextSpan)> NameUsesOf(ClassBlockSyntax type, MethodBlockSyntax method, HashSet<string> names, Dictionary<string, string> spelled)
    {
        var uses = method.Statements.SelectMany(s => s.DescendantNodes()).OfType<IdentifierNameSyntax>()
            .Where(i => names.Contains(i.Identifier.ValueText) && !(i.Parent is MemberAccessExpressionSyntax access && access.Name == i && access.Expression is not MeExpressionSyntax))
            .Select(i => (spelled[i.Identifier.ValueText], i.Identifier.Span))
            .ToList();
        foreach (var item in type.Members.OfType<MethodBlockSyntax>().SelectMany(m => m.SubOrFunctionStatement.HandlesClause?.Events ?? default))
        {
            if (item.EventContainer is WithEventsEventContainerSyntax container && names.Contains(container.Identifier.ValueText))
            {
                uses.Add((spelled[container.Identifier.ValueText], container.Identifier.Span));
            }
        }

        return uses;
    }

    private static List<DesignerResourceKey> ResourceKeysOf(MethodBlockSyntax method) =>
        [.. method.Statements.SelectMany(s => s.DescendantNodesAndSelf()).OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax resources } && string.Equals(resources.Identifier.ValueText, "resources", StringComparison.OrdinalIgnoreCase)
                && i.ArgumentList?.Arguments.LastOrDefault() is SimpleArgumentSyntax { Expression: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } })
            .Select(i =>
            {
                var literal = (LiteralExpressionSyntax)((SimpleArgumentSyntax)i.ArgumentList!.Arguments[^1]).Expression;
                return new DesignerResourceKey(((MemberAccessExpressionSyntax)i.Expression!).Name.Identifier.ValueText, literal.Token.ValueText, literal.Span);
            })];

    /// <summary>The state of one read: the names in scope (as declared) and the style seen so far.</summary>
    private sealed class Reader(SyntaxTree tree, string className, string? ns, IDesignerTypeResolver resolver, HashSet<string> names, Dictionary<string, string> spelled)
    {
        public bool UsesResources { get; private set; }

        public bool UsesThis { get; private set; }

        public bool Qualified { get; private set; }

        private DesignerReadException Fail(SyntaxNode node, string message) => new(message, DesignerReader.Line(tree, node));

        /// <summary>A component's or local's name as declared, or null when <paramref name="name"/> isn't one (<c>components</c> is).</summary>
        private string? Known(string name) => (names.Contains(name) || string.Equals(name, "components", StringComparison.OrdinalIgnoreCase)) ? spelled[name] : null;

        private static bool IsResources(ExpressionSyntax? expression) =>
            expression is IdentifierNameSyntax identifier && string.Equals(identifier.Identifier.ValueText, "resources", StringComparison.OrdinalIgnoreCase);

        public DesignerStatement Statement(StatementSyntax statement)
        {
            var line = DesignerReader.Line(tree, statement);
            switch (statement)
            {
                case LocalDeclarationStatementSyntax { Declarators: [{ Names: [var name] } declarator] }:
                {
                    // Dim x As T = New T(…), or Dim x As New T(…).
                    var creation = declarator.Initializer?.Value as ObjectCreationExpressionSyntax
                        ?? (declarator.AsClause as AsNewClauseSyntax)?.NewExpression as ObjectCreationExpressionSyntax
                        ?? throw Fail(statement, "a local that isn't created with New");
                    var value = Value(creation);
                    if (value.Type == "System.ComponentModel.ComponentResourceManager")
                    {
                        UsesResources = true;
                    }

                    return new DesignerStatement { Kind = DesignerStatementKinds.Create, Target = spelled[name.Identifier.ValueText], Value = value, Line = line };
                }

                case LocalDeclarationStatementSyntax:
                    throw Fail(statement, "a local that isn't created with New");

                case AssignmentStatementSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentStatement } assignment:
                    return Assignment(assignment, line);

                case AssignmentStatementSyntax assignment:
                    throw Fail(statement, $"the operator {assignment.OperatorToken.Text}");

                case AddRemoveHandlerStatementSyntax { RawKind: (int)SyntaxKind.AddHandlerStatement } handler:
                    return AddHandler(handler, line);

                case ExpressionStatementSyntax { Expression: var expression } when expression is InvocationExpressionSyntax or MemberAccessExpressionSyntax or IdentifierNameSyntax:
                    return Invocation(expression, line);

                case CallStatementSyntax call:
                    return Invocation(call.Invocation, line);

                default:
                    throw Fail(statement, $"a statement the designer doesn't write ({Describe(statement)})");
            }
        }

        private static string Describe(StatementSyntax statement) => statement switch
        {
            MultiLineIfBlockSyntax or SingleLineIfStatementSyntax => "If",
            ForBlockSyntax or ForEachBlockSyntax or WhileBlockSyntax or DoLoopBlockSyntax => "a loop",
            TryBlockSyntax => "Try",
            WithBlockSyntax => "With",
            ReturnStatementSyntax => "Return",
            _ => statement.Kind().ToString(),
        };

        private DesignerStatement Assignment(AssignmentStatementSyntax assignment, int line)
        {
            var (target, path) = TargetPath(assignment.Left);
            if (path is null)
            {
                if (assignment.Right is not ObjectCreationExpressionSyntax || target.Length == 0)
                {
                    throw Fail(assignment, $"{assignment.Left} is assigned something other than a new object");
                }

                return new DesignerStatement { Kind = DesignerStatementKinds.Create, Target = target, Value = Value(assignment.Right), Line = line };
            }

            return new DesignerStatement { Kind = DesignerStatementKinds.Assign, Target = target, Member = path, Value = Value(assignment.Right), Line = line };
        }

        /// <summary><c>AddHandler Me.Button1.Click, AddressOf Me.Button1_Click</c> (or over <c>New EventHandler(AddressOf …)</c>).</summary>
        private DesignerStatement AddHandler(AddRemoveHandlerStatementSyntax handler, int line)
        {
            var (target, path) = TargetPath(handler.EventExpression);
            if (path is null)
            {
                throw Fail(handler, $"AddHandler on {handler.EventExpression}, which isn't an event of a component");
            }

            var right = Unwrap(handler.DelegateExpression);
            DesignerValue? creation = null;
            if (right is ObjectCreationExpressionSyntax { ArgumentList.Arguments: [SimpleArgumentSyntax argument] } created)
            {
                creation = new DesignerValue { Kind = DesignerValueKinds.New, Type = TypeName(created.Type), Arguments = [] };
                right = Unwrap(argument.Expression);
            }

            if (right is UnaryExpressionSyntax { RawKind: (int)SyntaxKind.AddressOfExpression, Operand: var operand } && MethodName(operand) is { } method)
            {
                return new DesignerStatement { Kind = DesignerStatementKinds.Event, Target = target, Member = path, Handler = method, Value = creation, Line = line };
            }

            throw Fail(handler, right is LambdaExpressionSyntax
                ? $"the {path} event's handler is a lambda (the designer hooks events to methods of the class)"
                : $"the {path} event's handler isn't a method of the class");
        }

        private string? MethodName(ExpressionSyntax expression)
        {
            switch (Unwrap(expression))
            {
                case IdentifierNameSyntax identifier when Known(identifier.Identifier.ValueText) is null:
                    return identifier.Identifier.ValueText;
                case MemberAccessExpressionSyntax { Expression: MeExpressionSyntax, Name: IdentifierNameSyntax name }:
                    UsesThis = true;
                    return name.Identifier.ValueText;
                default:
                    return null;
            }
        }

        private DesignerStatement Invocation(ExpressionSyntax expression, int line)
        {
            var invocation = expression as InvocationExpressionSyntax;
            var callee = invocation is null ? expression : invocation.Expression ?? throw Fail(expression, "a call without a target");
            var arguments = invocation?.ArgumentList?.Arguments.ToList() ?? [];

            // CType(Me.x, System.ComponentModel.ISupportInitialize).BeginInit()
            if (callee is MemberAccessExpressionSyntax { Name: IdentifierNameSyntax initName, Expression: { } initOf } && initName.Identifier.ValueText is "BeginInit" or "EndInit"
                && Unwrap(initOf) is CastExpressionSyntax cast && arguments.Count == 0)
            {
                var (castTarget, castPath) = TargetPath(Unwrap(cast.Expression));
                if (castPath is not null)
                {
                    throw Fail(expression, $"{initName.Identifier.ValueText} on {cast.Expression}");
                }

                NoteQualified(cast.Type);
                return new DesignerStatement { Kind = DesignerStatementKinds.Layout, Target = castTarget, Member = initName.Identifier.ValueText, Line = line };
            }

            if (arguments.Any(a => a is not SimpleArgumentSyntax { NameColonEquals: null }))
            {
                throw Fail(expression, "a call with named or omitted arguments");
            }

            var values = arguments.Cast<SimpleArgumentSyntax>().Select(a => a.Expression).ToList();

            // resources.ApplyResources(Me.x, "x")
            if (callee is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ApplyResources" } apply && IsResources(apply.Expression) && values.Count is 2 or 3)
            {
                var target = Value(values[0]);
                if (target.Kind != DesignerValueKinds.Reference || Value(values[1]) is not { Kind: DesignerValueKinds.Literal, Type: "string" } key)
                {
                    throw Fail(expression, "ApplyResources on something other than a component");
                }

                return new DesignerStatement { Kind = DesignerStatementKinds.ApplyResources, Target = target.Value ?? string.Empty, Value = key, Line = line };
            }

            var (callTarget, callPath) = TargetPath(callee);
            if (callPath is null)
            {
                throw Fail(expression, $"a call to {callee}");
            }

            var method = callPath.Contains('.', StringComparison.Ordinal) ? callPath[(callPath.LastIndexOf('.') + 1)..] : callPath;
            if (s_layoutMethods.Contains(method))
            {
                return new DesignerStatement { Kind = DesignerStatementKinds.Layout, Target = callTarget, Member = callPath, Arguments = [.. values.Select(Value)], Line = line };
            }

            if (callTarget.Length == 0 && !callPath.Contains('.', StringComparison.Ordinal))
            {
                throw Fail(expression, $"a call to the class's own method {callPath}");
            }

            return new DesignerStatement { Kind = DesignerStatementKinds.Call, Target = callTarget, Member = callPath, Arguments = [.. values.Select(Value)], Line = line };
        }

        /// <summary>A statement's left side as its target (a component's or local's name, <c>components</c>, or empty for the root) and the member path after it.</summary>
        private (string Target, string? Path) TargetPath(ExpressionSyntax expression)
        {
            var parts = new List<string>();
            var current = expression;
            while (true)
            {
                switch (current)
                {
                    case MemberAccessExpressionSyntax { RawKind: (int)SyntaxKind.SimpleMemberAccessExpression, Expression: { } inner, Name: IdentifierNameSyntax name }:
                        parts.Add(name.Identifier.ValueText);
                        current = inner;
                        continue;
                    case MeExpressionSyntax:
                        UsesThis = true;
                        parts.Reverse();
                        return Split(parts, fromMe: true);
                    case MyClassExpressionSyntax or MyBaseExpressionSyntax:
                        parts.Reverse();
                        return Split(parts, fromMe: true);
                    case IdentifierNameSyntax identifier:
                        parts.Add(identifier.Identifier.ValueText);
                        parts.Reverse();
                        return Split(parts, fromMe: false);
                    case ParenthesizedExpressionSyntax parenthesized:
                        current = parenthesized.Expression;
                        continue;
                    default:
                        throw Fail(expression, $"{expression} isn't a component or a property of one");
                }
            }

            (string, string?) Split(List<string> path, bool fromMe)
            {
                if (path.Count == 0)
                {
                    return (string.Empty, null);
                }

                if (Known(path[0]) is { } known)
                {
                    return (known, path.Count > 1 ? string.Join('.', path.Skip(1)) : null);
                }

                if (!fromMe && string.Equals(path[0], "resources", StringComparison.OrdinalIgnoreCase))
                {
                    throw Fail(expression, "a use of resources the designer doesn't write");
                }

                return (string.Empty, string.Join('.', path));
            }
        }

        private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
        {
            while (expression is ParenthesizedExpressionSyntax parenthesized)
            {
                expression = parenthesized.Expression;
            }

            return expression;
        }

        private void NoteQualified(SyntaxNode type)
        {
            var text = VisualBasicDesignerSyntax.StripGlobal(type.ToString());
            if (text.StartsWith("System.Windows.Forms.", StringComparison.Ordinal) || text.StartsWith("System.Drawing.", StringComparison.Ordinal))
            {
                Qualified = true;
            }
        }

        private string TypeName(SyntaxNode type)
        {
            NoteQualified(type);
            return resolver.FullName(type) ?? throw Fail(type, $"the type {type} isn't known");
        }

        private string FullClassName => ns is null ? className : ns + "." + className;

        private static string Shorten(string text) => text.Length > 60 ? text[..57] + "…" : text;

        /// <summary>A value: a literal, an enum or shared member, a creation, a shared call, an array, a resource, a component.</summary>
        public DesignerValue Value(ExpressionSyntax expression)
        {
            expression = Unwrap(expression);
            if (VisualBasicStrings.Text(expression) is { } text && expression is not LiteralExpressionSyntax)
            {
                // "a" & vbCrLf & "b", ChrW(13) & ChrW(10): a string (a character alone, as CodeDOM writes a Char).
                return expression is InvocationExpressionSyntax && text.Length == 1
                    ? new DesignerValue { Kind = DesignerValueKinds.Literal, Type = "char", Value = text }
                    : DesignerValue.String(text);
            }

            switch (expression)
            {
                case LiteralExpressionSyntax literal:
                    return Literal(literal);

                case UnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression, Operand: var operand }
                    when Unwrap(operand) is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } number:
                {
                    var positive = Literal(number);
                    return positive with { Value = "-" + positive.Value };
                }

                case PredefinedCastExpressionSyntax predefined:
                {
                    // CByte(255), CSng(1.5): a number of that type.
                    var inner = Value(predefined.Expression);
                    return Keyword(predefined.Keyword.Kind()) is { } type && inner.Kind == DesignerValueKinds.Literal && inner.Type is not ("string" or "bool" or "null")
                        ? inner with { Type = type }
                        : inner;
                }

                case CastExpressionSyntax cast:
                    return Cast(cast);

                case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.OrExpression } or:
                    return Flags(or, null);

                case MeExpressionSyntax:
                    return DesignerValue.ReferenceTo(string.Empty);

                case IdentifierNameSyntax identifier:
                    if (Known(identifier.Identifier.ValueText) is { } known)
                    {
                        return DesignerValue.ReferenceTo(known);
                    }

                    throw Fail(expression, $"the name {identifier.Identifier.ValueText} isn't a component of the form (a field or a variable the designer doesn't know)");

                case MemberAccessExpressionSyntax access:
                    return Member(access);

                case ObjectCreationExpressionSyntax creation:
                    if (creation.Initializer is not null)
                    {
                        throw Fail(expression, "an object initializer");
                    }

                    return new DesignerValue
                    {
                        Kind = DesignerValueKinds.New,
                        Type = TypeName(creation.Type),
                        Arguments = [.. Arguments(creation.ArgumentList, expression)],
                    };

                case ArrayCreationExpressionSyntax array when array.ArrayBounds is null && array.RankSpecifiers is [{ CommaTokens.Count: 0 }]:
                    return new DesignerValue
                    {
                        Kind = DesignerValueKinds.Array,
                        Type = TypeName(array.Type),
                        Arguments = [.. array.Initializer.Initializers.Select(Value)],
                    };

                case InvocationExpressionSyntax invocation:
                    return Call(invocation, null);

                case GetTypeExpressionSyntax getType:
                    return new DesignerValue
                    {
                        Kind = DesignerValueKinds.TypeOf,
                        Type = string.Equals(VisualBasicDesignerSyntax.StripGlobal(getType.Type.ToString()), className, StringComparison.OrdinalIgnoreCase) ? FullClassName : TypeName(getType.Type),
                    };

                case LambdaExpressionSyntax:
                    throw Fail(expression, "a lambda");

                default:
                    throw Fail(expression, $"the expression {Shorten(expression.ToString())}");
            }
        }

        private IEnumerable<DesignerValue> Arguments(ArgumentListSyntax? list, SyntaxNode at)
        {
            foreach (var argument in list?.Arguments ?? default)
            {
                if (argument is not SimpleArgumentSyntax { NameColonEquals: null } simple)
                {
                    throw Fail(at, "named or omitted arguments");
                }

                yield return Value(simple.Expression);
            }
        }

        /// <summary>The model's name for a Visual Basic numeric type keyword (<c>Byte</c> is <c>byte</c>, <c>Integer</c> <c>int</c>), or null.</summary>
        private static string? Keyword(SyntaxKind keyword) => keyword switch
        {
            SyntaxKind.CByteKeyword or SyntaxKind.ByteKeyword => "byte",
            SyntaxKind.CSByteKeyword or SyntaxKind.SByteKeyword => "sbyte",
            SyntaxKind.CShortKeyword or SyntaxKind.ShortKeyword => "short",
            SyntaxKind.CUShortKeyword or SyntaxKind.UShortKeyword => "ushort",
            SyntaxKind.CIntKeyword or SyntaxKind.IntegerKeyword => "int",
            SyntaxKind.CUIntKeyword or SyntaxKind.UIntegerKeyword => "uint",
            SyntaxKind.CLngKeyword or SyntaxKind.LongKeyword => "long",
            SyntaxKind.CULngKeyword or SyntaxKind.ULongKeyword => "ulong",
            SyntaxKind.CSngKeyword or SyntaxKind.SingleKeyword => "float",
            SyntaxKind.CDblKeyword or SyntaxKind.DoubleKeyword => "double",
            SyntaxKind.CDecKeyword or SyntaxKind.DecimalKeyword => "decimal",
            _ => null,
        };

        private static DesignerValue Literal(LiteralExpressionSyntax literal)
        {
            static DesignerValue Of(string type, string text) => new() { Kind = DesignerValueKinds.Literal, Type = type, Value = text };
            return literal.Token.Value switch
            {
                null => DesignerValue.Null,
                string s => Of("string", s),
                bool b => Of("bool", b ? "true" : "false"),
                char c => Of("char", c.ToString()),
                short h => Of("short", h.ToString(CultureInfo.InvariantCulture)),
                ushort uh => Of("ushort", uh.ToString(CultureInfo.InvariantCulture)),
                int i => Of("int", i.ToString(CultureInfo.InvariantCulture)),
                uint u => Of("uint", u.ToString(CultureInfo.InvariantCulture)),
                long l => Of("long", l.ToString(CultureInfo.InvariantCulture)),
                ulong ul => Of("ulong", ul.ToString(CultureInfo.InvariantCulture)),
                float f => Of("float", f.ToString("R", CultureInfo.InvariantCulture)),
                double d => Of("double", d.ToString("R", CultureInfo.InvariantCulture)),
                decimal m => Of("decimal", m.ToString(CultureInfo.InvariantCulture)),
                _ => Of("string", literal.Token.ValueText),
            };
        }

        private DesignerValue Cast(CastExpressionSyntax cast)
        {
            var operand = Unwrap(cast.Expression);
            if (cast.Type is PredefinedTypeSyntax predefined)
            {
                // CType(255, Byte), CType(CType(255, Byte), Integer): a number of that type.
                var inner = Value(operand);
                return Keyword(predefined.Keyword.Kind()) is { } keyword && inner.Kind == DesignerValueKinds.Literal && inner.Type is not ("string" or "bool" or "null")
                    ? inner with { Type = keyword }
                    : inner;
            }

            var type = TypeName(cast.Type);
            switch (operand)
            {
                case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.OrExpression } or:
                    return Flags(or, type);
                case InvocationExpressionSyntax invocation when VisualBasicStrings.Text(invocation) is null:
                    return Call(invocation, type);
                case LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } number:
                    return new DesignerValue { Kind = DesignerValueKinds.Enum, Type = type, Value = Literal(number).Value };
                case UnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression }:
                    return new DesignerValue { Kind = DesignerValueKinds.Enum, Type = type, Value = Value(operand).Value };
                default:
                    var value = Value(operand);
                    return value.Kind == DesignerValueKinds.Member && value.Type == type ? value with { Kind = DesignerValueKinds.Enum, Members = [value.Value!], Value = null } : value;
            }
        }

        /// <summary><c>A.X Or A.Y</c> (and CodeDOM's nested, parenthesized form): flags of one enum.</summary>
        private DesignerValue Flags(BinaryExpressionSyntax or, string? castType)
        {
            var members = new List<string>();
            string? type = castType;
            void Visit(ExpressionSyntax part)
            {
                part = Unwrap(part);
                switch (part)
                {
                    case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.OrExpression } nested:
                        Visit(nested.Left);
                        Visit(nested.Right);
                        break;
                    case CastExpressionSyntax nestedCast when nestedCast.Type is not PredefinedTypeSyntax && TypeName(nestedCast.Type) is var nestedType && (type is null || type == nestedType):
                        type = nestedType;
                        Visit(nestedCast.Expression);
                        break;
                    case MemberAccessExpressionSyntax { Expression: { } of, Name: IdentifierNameSyntax member }:
                        var memberType = TypeName(of);
                        if (type is not null && type != memberType)
                        {
                            throw Fail(part, $"flags of different types ({type} and {memberType})");
                        }

                        type = memberType;
                        members.Add(member.Identifier.ValueText);
                        break;
                    default:
                        throw Fail(part, $"the expression {Shorten(part.ToString())} in a combination of flags");
                }
            }

            Visit(or);
            return new DesignerValue { Kind = DesignerValueKinds.Enum, Type = type, Members = [.. members] };
        }

        private DesignerValue Member(MemberAccessExpressionSyntax access)
        {
            if (access.Expression is null || !access.IsKind(SyntaxKind.SimpleMemberAccessExpression))
            {
                throw Fail(access, $"the expression {Shorten(access.ToString())}");
            }

            // Me.Button1, Me.components: a component.
            if (access.Expression is MeExpressionSyntax && access.Name is IdentifierNameSyntax field)
            {
                UsesThis = true;
                return Known(field.Identifier.ValueText) is { } known
                    ? DesignerValue.ReferenceTo(known)
                    : throw Fail(access, $"the name {field.Identifier.ValueText} isn't a component of the form");
            }

            if (RootName(access) is { } root && (Known(root) is not null || string.Equals(root, "resources", StringComparison.OrdinalIgnoreCase)))
            {
                throw Fail(access, $"a property read of {root} ({Shorten(access.ToString())})");
            }

            // Type.Member: an enum member or a shared field or property (Color.Red, SystemColors.Control, My.Resources.Resources.Logo).
            var type = TypeName(access.Expression);
            var member = access.Name.Identifier.ValueText;
            return resolver.IsEnum(type) == true
                ? new DesignerValue { Kind = DesignerValueKinds.Enum, Type = type, Members = [member] }
                : DesignerValue.MemberOf(type, member);
        }

        private static string? RootName(ExpressionSyntax expression)
        {
            while (expression is MemberAccessExpressionSyntax { Expression: { } inner })
            {
                expression = inner;
            }

            return expression is IdentifierNameSyntax identifier ? identifier.Identifier.ValueText : null;
        }

        private DesignerValue Call(InvocationExpressionSyntax invocation, string? castType)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax { Expression: { } of, Name: IdentifierNameSyntax method } access)
            {
                throw Fail(invocation, $"a call to {invocation.Expression}");
            }

            var arguments = Arguments(invocation.ArgumentList, invocation).ToList();
            if (IsResources(of) && method.Identifier.ValueText is "GetObject" or "GetString" && arguments is [{ Kind: DesignerValueKinds.Literal, Type: "string" } key])
            {
                UsesResources = true;
                return new DesignerValue { Kind = DesignerValueKinds.Resource, Type = method.Identifier.ValueText == "GetString" ? "System.String" : castType, Value = key.Value };
            }

            if (RootName(of) is { } root && (Known(root) is not null || string.Equals(root, "resources", StringComparison.OrdinalIgnoreCase)) || of is MeExpressionSyntax)
            {
                throw Fail(invocation, $"a call to {Shorten(access.ToString())} (the designer only calls shared methods in values)");
            }

            return new DesignerValue
            {
                Kind = DesignerValueKinds.Call,
                Type = TypeName(of),
                Value = method.Identifier.ValueText,
                Arguments = [.. arguments],
            };
        }
    }
}

/// <summary>
/// Visual Basic's strings with characters a literal can't hold: <c>"a" &amp; vbCrLf &amp; "b"</c> (.NET),
/// <c>"a" &amp; Global.Microsoft.VisualBasic.ChrW(13) &amp; …</c> (CodeDOM, .NET Framework).
/// </summary>
internal static class VisualBasicStrings
{
    private static readonly Dictionary<string, string> s_constants = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vbCrLf"] = "\r\n", ["vbNewLine"] = "\r\n", ["vbCr"] = "\r", ["vbLf"] = "\n", ["vbTab"] = "\t", ["vbNullChar"] = "\0", ["vbBack"] = "\b",
        ["vbFormFeed"] = "\f", ["vbVerticalTab"] = "\v",
    };

    private static readonly Dictionary<string, string> s_controlChars = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CrLf"] = "\r\n", ["NewLine"] = "\r\n", ["Cr"] = "\r", ["Lf"] = "\n", ["Tab"] = "\t", ["NullChar"] = "\0", ["Back"] = "\b", ["FormFeed"] = "\f",
        ["VerticalTab"] = "\v", ["Quote"] = "\"",
    };

    /// <summary>The text a string expression stands for (literals, <c>&amp;</c>, <c>ChrW</c>, <c>vbCrLf</c>, <c>ControlChars.Tab</c>), or null.</summary>
    public static string? Text(ExpressionSyntax expression)
    {
        switch (expression)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return Text(parenthesized.Expression);
            case LiteralExpressionSyntax { Token.Value: string s }:
                return s;
            case LiteralExpressionSyntax { Token.Value: char c }:
                return c.ToString();
            case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.ConcatenateExpression } concatenation:
                return Text(concatenation.Left) is { } left && Text(concatenation.Right) is { } right ? left + right : null;
            case IdentifierNameSyntax identifier when s_constants.TryGetValue(identifier.Identifier.ValueText, out var constant):
                return constant;
            case MemberAccessExpressionSyntax { Expression: { } of, Name: IdentifierNameSyntax name } when IsVisualBasicModule(of, "Constants") && s_constants.TryGetValue(name.Identifier.ValueText, out var constant):
                return constant;
            case MemberAccessExpressionSyntax { Expression: { } of, Name: IdentifierNameSyntax name } when IsVisualBasicModule(of, "ControlChars") && s_controlChars.TryGetValue(name.Identifier.ValueText, out var control):
                return control;
            case InvocationExpressionSyntax { ArgumentList.Arguments: [SimpleArgumentSyntax { NameColonEquals: null, Expression: var argument }] } invocation
                when ChrName(invocation.Expression) is not null && Code(argument) is { } code and >= 0 and <= 0xFFFF:
                return ((char)code).ToString();
            default:
                return null;
        }
    }

    private static string? ChrName(ExpressionSyntax? expression) => expression switch
    {
        IdentifierNameSyntax { Identifier.ValueText: var name } when name.Equals("ChrW", StringComparison.OrdinalIgnoreCase) || name.Equals("Chr", StringComparison.OrdinalIgnoreCase) => name,
        MemberAccessExpressionSyntax { Expression: { } of, Name.Identifier.ValueText: var name } when (name.Equals("ChrW", StringComparison.OrdinalIgnoreCase) || name.Equals("Chr", StringComparison.OrdinalIgnoreCase))
            && (IsVisualBasicModule(of, "Strings") || IsVisualBasicNamespace(of)) => name,
        _ => null,
    };

    private static int? Code(ExpressionSyntax expression) => expression switch
    {
        ParenthesizedExpressionSyntax parenthesized => Code(parenthesized.Expression),
        LiteralExpressionSyntax { Token.Value: int i } => i,
        LiteralExpressionSyntax { Token.Value: short h } => h,
        _ => null,
    };

    /// <summary><c>Microsoft.VisualBasic</c> or <c>Global.Microsoft.VisualBasic</c>.</summary>
    private static bool IsVisualBasicNamespace(ExpressionSyntax expression) =>
        VisualBasicDesignerSyntax.StripGlobal(expression.ToString()).Equals("Microsoft.VisualBasic", StringComparison.OrdinalIgnoreCase);

    /// <summary>A module of Microsoft.VisualBasic by its name, qualified or not (<c>ControlChars</c>, <c>Microsoft.VisualBasic.ControlChars</c>).</summary>
    private static bool IsVisualBasicModule(ExpressionSyntax expression, string module)
    {
        var text = VisualBasicDesignerSyntax.StripGlobal(expression.ToString());
        return text.Equals(module, StringComparison.OrdinalIgnoreCase) || text.Equals("Microsoft.VisualBasic." + module, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <paramref name="text"/> as a Visual Basic expression: quoted runs (<c>""</c> for a quote) joined by <c>&amp;</c> with
    /// the characters a literal can't hold: <c>vbCrLf</c>, <c>vbTab</c>, <c>ChrW(n)</c> (.NET), or each as
    /// <c>Global.Microsoft.VisualBasic.ChrW(n)</c> (<paramref name="codeDom"/>, as CodeDOM writes them for .NET Framework).
    /// </summary>
    public static string Literal(string text, bool codeDom)
    {
        if (text.Length == 0)
        {
            return "\"\"";
        }

        var parts = new List<string>();
        var run = new StringBuilder();
        void Flush()
        {
            if (run.Length > 0)
            {
                parts.Add("\"" + run.ToString().Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
                run.Clear();
            }
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!NeedsChrW(c))
            {
                run.Append(c);
                continue;
            }

            Flush();
            if (codeDom)
            {
                parts.Add($"Global.Microsoft.VisualBasic.ChrW({(int)c})");
            }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                parts.Add("vbCrLf");
                i++;
            }
            else
            {
                parts.Add(c switch { '\r' => "vbCr", '\n' => "vbLf", '\t' => "vbTab", '\0' => "vbNullChar", _ => $"ChrW({(int)c})" });
            }
        }

        Flush();
        return string.Join(" & ", parts);
    }

    /// <summary>A character a Visual Basic string literal can't hold as itself: control characters, line breaks, and the typographic quotes Visual Basic reads as quotes.</summary>
    public static bool NeedsChrW(char c) => char.IsControl(c) || c is (char)0x2028 or (char)0x2029 or (char)0x201C or (char)0x201D or (char)0xFF02;
}
