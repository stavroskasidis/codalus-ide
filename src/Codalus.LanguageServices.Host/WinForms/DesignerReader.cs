using System.Globalization;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Codalus.LanguageServices.Host.WinForms;

/// <summary>A statement of InitializeComponent with where it is: whole lines, from its indentation to its line break.</summary>
internal sealed record SourceStatement(DesignerStatement Statement, TextSpan Lines, StatementSyntax? Syntax);

/// <summary>A designer file read (M41a, ADR 0073): its model, and where each part of it is in the text.</summary>
internal sealed class DesignerSource
{
    public required SourceText Text { get; init; }

    public required SyntaxTree Tree { get; init; }

    public required ClassDeclarationSyntax Class { get; init; }

    public required MethodDeclarationSyntax Method { get; init; }

    public required DesignerModel Model { get; init; }

    /// <summary>The statements and section comments, in order (parallel to <see cref="DesignerModel.Statements"/>).</summary>
    public required IReadOnlyList<SourceStatement> Statements { get; init; }

    /// <summary>The designer class's fields by name (the components', <c>components</c>…).</summary>
    public required IReadOnlyDictionary<string, FieldDeclarationSyntax> Fields { get; init; }

    /// <summary>One statement's indentation.</summary>
    public required string Indent { get; init; }

    public required string NewLine { get; init; }

    /// <summary>The empty lines of a section comment: <c>// </c> (Visual Studio's, with its space) or <c>//</c>.</summary>
    public required string SectionRule { get; init; }

    /// <summary>The names of the class's components and locals (not the root).</summary>
    public HashSet<string> Names { get; } = new(StringComparer.Ordinal);
}

/// <summary>Thrown for code the designer can't read, with the line (zero-based) it's on.</summary>
internal sealed class DesignerReadException(string message, int? line) : Exception(message)
{
    public int? Line { get; } = line;
}

/// <summary>
/// Reads InitializeComponent (M41a, ADR 0073) into a <see cref="DesignerModel"/>: the statements Visual Studio's
/// designer writes (creations, assignments, <c>Controls.Add</c> and other calls, layout calls, event hookups,
/// <c>resources.ApplyResources</c> and <c>GetObject</c>, section comments), with values as expression trees. Anything
/// else (control flow, lambdas, calls to the class's own methods, unknown names) can't be designed: the read fails with
/// the reason and its line, and the file opens as code, as Visual Studio does.
/// </summary>
internal static class DesignerReader
{
    /// <summary>The namespaces a .NET Windows Forms project imports implicitly (<c>ImplicitUsings</c>).</summary>
    public static readonly string[] WindowsFormsImplicitUsings =
        ["System", "System.Collections.Generic", "System.Drawing", "System.IO", "System.Linq", "System.Net.Http", "System.Threading", "System.Threading.Tasks", "System.Windows.Forms"];

    private static readonly HashSet<string> s_layoutMethods = new(StringComparer.Ordinal) { "SuspendLayout", "ResumeLayout", "PerformLayout", "BeginInit", "EndInit" };

    /// <summary>
    /// Reads <paramref name="tree"/>'s InitializeComponent. <paramref name="baseType"/> is the class's base type (from the
    /// compilation, or the code file's base list); <paramref name="resolver"/> names types (null: by syntax).
    /// </summary>
    public static DesignerSource Read(SyntaxTree tree, string baseType, IEnumerable<string> globalUsings, IDesignerTypeResolver? resolver = null)
    {
        var root = tree.GetRoot();
        if (DesignerSyntax.FindInitializeComponent(root) is not var (type, method))
        {
            throw new DesignerReadException("The file has no InitializeComponent method.", null);
        }

        if (method.Body is not { } body)
        {
            throw new DesignerReadException("InitializeComponent has no body.", Line(tree, method));
        }

        var text = tree.GetText();
        var ns = DesignerSyntax.NamespaceOf(type);
        var imports = DesignerSyntax.UsingsOf(root).Concat(globalUsings).Distinct(StringComparer.Ordinal).ToArray();
        resolver ??= new SyntacticDesignerTypeResolver(ns);
        var fields = new Dictionary<string, FieldDeclarationSyntax>(StringComparer.Ordinal);
        foreach (var field in type.Members.OfType<FieldDeclarationSyntax>())
        {
            foreach (var variable in field.Declaration.Variables)
            {
                fields[variable.Identifier.Text] = field;
            }
        }

        // The names first: a statement can refer to a component created later (Visual Studio creates all of them first anyway).
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var statement in body.Statements)
        {
            switch (statement)
            {
                case LocalDeclarationStatementSyntax local:
                    foreach (var variable in local.Declaration.Variables)
                    {
                        names.Add(variable.Identifier.Text);
                    }

                    break;
                case ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression, Left: var left, Right: ObjectCreationExpressionSyntax } }
                    when FieldName(left) is { } created && fields.ContainsKey(created) && created != "components":
                    // A field created with new (a root property assigned a new object, AutoScaleDimensions = new SizeF(…), isn't one).
                    names.Add(created);
                    break;
            }
        }

        var reader = new Reader(tree, type.Identifier.Text, ns, resolver, names);
        var statements = new List<SourceStatement>();
        var components = new List<DesignerComponent> { new(type.Identifier.Text, baseType) { IsRoot = true } };
        foreach (var statement in body.Statements)
        {
            foreach (var (section, lines) in Sections(text, statement))
            {
                statements.Add(new SourceStatement(new DesignerStatement { Kind = DesignerStatementKinds.Section, Target = section, Line = text.Lines.GetLineFromPosition(lines.Start).LineNumber }, lines, null));
            }

            var read = reader.Statement(statement);
            statements.Add(new SourceStatement(read, LinesOf(text, statement.Span), statement));
            if (read.Kind == DesignerStatementKinds.Create && read.Target is not ("components" or "") && read.Value?.Type is { } createdType)
            {
                var isLocal = statement is LocalDeclarationStatementSyntax;
                components.Add(new DesignerComponent(read.Target, createdType)
                {
                    IsLocal = isLocal,
                    Modifier = !isLocal && fields.TryGetValue(read.Target, out var field) ? string.Join(' ', field.Modifiers.Select(m => m.Text)) : null,
                });
            }
        }

        var model = new DesignerModel
        {
            ClassName = type.Identifier.Text,
            Namespace = ns,
            BaseType = baseType,
            Imports = imports,
            Components = [.. components],
            Statements = [.. statements.Select(s => s.Statement)],
            UsesResources = reader.UsesResources,
            UsesThis = reader.UsesThis,
            QualifiedNames = reader.Qualified,
        };
        var source = new DesignerSource
        {
            Text = text,
            Tree = tree,
            Class = type,
            Method = method,
            Model = model,
            Statements = statements,
            Fields = fields,
            Indent = IndentOf(text, body),
            NewLine = NewLineOf(text),
            SectionRule = SectionRuleOf(text, statements),
        };
        foreach (var name in names)
        {
            source.Names.Add(name);
        }

        return source;
    }

    /// <summary>The field a creation's left side assigns (<c>button1</c>, <c>this.button1</c>, <c>components</c>), or null.</summary>
    private static string? FieldName(ExpressionSyntax left) => left switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: IdentifierNameSyntax name } => name.Identifier.Text,
        _ => null,
    };

    public static int Line(SyntaxTree tree, SyntaxNode node) => tree.GetLineSpan(node.Span).StartLinePosition.Line;

    /// <summary>Whole lines: from the start of the first line to after the last line's break.</summary>
    public static TextSpan LinesOf(SourceText text, TextSpan span)
    {
        var first = text.Lines.GetLineFromPosition(span.Start);
        var last = text.Lines.GetLineFromPosition(Math.Max(span.Start, span.End - 1));
        return TextSpan.FromBounds(first.Start, last.EndIncludingLineBreak);
    }

    /// <summary>
    /// Visual Studio's section comments in a statement's leading trivia: three line comments, the middle one naming the
    /// component (<c>// </c>, <c>// button1</c>, <c>// </c>).
    /// </summary>
    private static IEnumerable<(string Name, TextSpan Lines)> Sections(SourceText text, StatementSyntax statement)
    {
        var comments = statement.GetLeadingTrivia().Where(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia)).ToList();
        for (var i = 0; i + 2 < comments.Count; i++)
        {
            if (comments[i].ToString().TrimEnd() == "//" && comments[i + 2].ToString().TrimEnd() == "//"
                && comments[i + 1].ToString() is var middle && middle.Length > 3 && middle.StartsWith("// ", StringComparison.Ordinal))
            {
                var name = middle[3..].Trim();
                if (name.Length > 0 && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '.'))
                {
                    yield return (name, TextSpan.FromBounds(LinesOf(text, comments[i].Span).Start, LinesOf(text, comments[i + 2].Span).End));
                    i += 2;
                }
            }
        }
    }

    private static string IndentOf(SourceText text, BlockSyntax body)
    {
        if (body.Statements.FirstOrDefault() is { } first)
        {
            var line = text.Lines.GetLineFromPosition(first.SpanStart);
            return text.ToString(TextSpan.FromBounds(line.Start, first.SpanStart));
        }

        var braceLine = text.Lines.GetLineFromPosition(body.OpenBraceToken.SpanStart);
        var braceIndent = text.ToString(TextSpan.FromBounds(braceLine.Start, body.OpenBraceToken.SpanStart));
        return braceIndent + (braceIndent.Contains('\t', StringComparison.Ordinal) ? "\t" : "    ");
    }

    private static string NewLineOf(SourceText text) =>
        text.Lines.Count > 1 && text.Lines[0].EndIncludingLineBreak - text.Lines[0].End == 2 ? "\r\n" : "\n";

    /// <summary>The rule lines of the file's section comments; Visual Studio's (<c>// </c>) for a file that has none.</summary>
    private static string SectionRuleOf(SourceText text, List<SourceStatement> statements)
    {
        if (statements.FirstOrDefault(s => s.Statement.Kind == DesignerStatementKinds.Section) is { } section)
        {
            var first = text.Lines.GetLineFromPosition(section.Lines.Start).ToString().TrimStart();
            return first == "//" ? "//" : "// ";
        }

        return "// ";
    }

    /// <summary>The state of one read: the names in scope and the style seen so far.</summary>
    private sealed class Reader(SyntaxTree tree, string className, string? ns, IDesignerTypeResolver resolver, HashSet<string> names)
    {
        public bool UsesResources { get; private set; }

        public bool UsesThis { get; private set; }

        public bool Qualified { get; private set; }

        private DesignerReadException Fail(SyntaxNode node, string message) => new(message, Line(tree, node));

        public DesignerStatement Statement(StatementSyntax statement)
        {
            var line = Line(tree, statement);
            switch (statement)
            {
                case LocalDeclarationStatementSyntax { Declaration.Variables: [{ Initializer.Value: ObjectCreationExpressionSyntax creation } variable] }:
                {
                    var value = Value(creation);
                    var name = variable.Identifier.Text;
                    if (value.Type == "System.ComponentModel.ComponentResourceManager")
                    {
                        UsesResources = true;
                    }

                    return new DesignerStatement { Kind = DesignerStatementKinds.Create, Target = name, Value = value, Line = line };
                }

                case ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assignment }:
                    return Assignment(assignment, line);

                case ExpressionStatementSyntax { Expression: InvocationExpressionSyntax invocation }:
                    return Invocation(invocation, line);

                case LocalDeclarationStatementSyntax:
                    throw Fail(statement, "a local that isn't created with new");

                default:
                    throw Fail(statement, $"a statement the designer doesn't write ({Describe(statement)})");
            }
        }

        private static string Describe(StatementSyntax statement) => statement switch
        {
            IfStatementSyntax => "if",
            ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax => "a loop",
            TryStatementSyntax => "try",
            ReturnStatementSyntax => "return",
            _ => statement.Kind().ToString(),
        };

        private DesignerStatement Assignment(AssignmentExpressionSyntax assignment, int line)
        {
            var (target, path) = TargetPath(assignment.Left);
            if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
            {
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

            if (assignment.IsKind(SyntaxKind.AddAssignmentExpression) && path is not null)
            {
                // An event: a method group (Click += OnClick), or a delegate created over one (Click += new EventHandler(this.OnClick)).
                var right = Unwrap(assignment.Right);
                DesignerValue? creation = null;
                if (right is ObjectCreationExpressionSyntax { ArgumentList.Arguments: [var argument] } created)
                {
                    creation = new DesignerValue { Kind = DesignerValueKinds.New, Type = TypeName(created.Type), Arguments = [] };
                    right = Unwrap(argument.Expression);
                }

                if (MethodName(right) is { } handler)
                {
                    return new DesignerStatement { Kind = DesignerStatementKinds.Event, Target = target, Member = path, Handler = handler, Value = creation, Line = line };
                }

                throw Fail(assignment, right is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax
                    ? $"the {path} event's handler is a lambda (the designer hooks events to methods of the class)"
                    : $"the {path} event's handler isn't a method of the class");
            }

            throw Fail(assignment, $"the operator {assignment.OperatorToken.Text}");
        }

        private string? MethodName(ExpressionSyntax expression)
        {
            switch (expression)
            {
                case IdentifierNameSyntax identifier when !names.Contains(identifier.Identifier.Text):
                    return identifier.Identifier.Text;
                case MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: IdentifierNameSyntax name }:
                    UsesThis = true;
                    return name.Identifier.Text;
                default:
                    return null;
            }
        }

        private DesignerStatement Invocation(InvocationExpressionSyntax invocation, int line)
        {
            // ((System.ComponentModel.ISupportInitialize)(this.x)).BeginInit()
            if (invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "BeginInit" or "EndInit" } initAccess
                && Unwrap(initAccess.Expression) is CastExpressionSyntax cast && invocation.ArgumentList.Arguments.Count == 0)
            {
                var (castTarget, castPath) = TargetPath(Unwrap(cast.Expression));
                if (castPath is not null)
                {
                    throw Fail(invocation, $"{initAccess.Name} on {cast.Expression}");
                }

                NoteQualified(cast.Type);
                return new DesignerStatement { Kind = DesignerStatementKinds.Layout, Target = castTarget, Member = initAccess.Name.Identifier.Text, Line = line };
            }

            var arguments = invocation.ArgumentList.Arguments;
            if (arguments.Any(a => !a.RefKindKeyword.IsKind(SyntaxKind.None) || a.NameColon is not null))
            {
                throw Fail(invocation, "a call with named or ref arguments");
            }

            // resources.ApplyResources(this.x, "x")
            if (invocation.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "resources" }, Name.Identifier.Text: "ApplyResources" }
                && arguments.Count is 2 or 3)
            {
                var target = Value(arguments[0].Expression);
                if (target.Kind != DesignerValueKinds.Reference || Value(arguments[1].Expression) is not { Kind: DesignerValueKinds.Literal, Type: "string" } key)
                {
                    throw Fail(invocation, "ApplyResources on something other than a component");
                }

                return new DesignerStatement { Kind = DesignerStatementKinds.ApplyResources, Target = target.Value ?? string.Empty, Value = key, Line = line };
            }

            var (callTarget, callPath) = TargetPath(invocation.Expression);
            if (callPath is null)
            {
                throw Fail(invocation, $"a call to {invocation.Expression}");
            }

            var method = callPath.Contains('.', StringComparison.Ordinal) ? callPath[(callPath.LastIndexOf('.') + 1)..] : callPath;
            if (s_layoutMethods.Contains(method) && method is "SuspendLayout" or "ResumeLayout" or "PerformLayout")
            {
                return new DesignerStatement { Kind = DesignerStatementKinds.Layout, Target = callTarget, Member = callPath, Arguments = [.. arguments.Select(a => Value(a.Expression))], Line = line };
            }

            if (callTarget.Length == 0 && !callPath.Contains('.', StringComparison.Ordinal))
            {
                // A method of the class itself (this.Foo()): code, not something the designer writes.
                throw Fail(invocation, $"a call to the class's own method {callPath}");
            }

            return new DesignerStatement { Kind = DesignerStatementKinds.Call, Target = callTarget, Member = callPath, Arguments = [.. arguments.Select(a => Value(a.Expression))], Line = line };
        }

        /// <summary>
        /// Splits a statement's left side into its target (a component's or local's name, <c>components</c>, or empty for
        /// the root) and the member path after it (null when it's the target itself).
        /// </summary>
        private (string Target, string? Path) TargetPath(ExpressionSyntax expression)
        {
            var parts = new List<string>();
            var current = expression;
            while (true)
            {
                switch (current)
                {
                    case MemberAccessExpressionSyntax { Name: IdentifierNameSyntax name } access:
                        parts.Add(name.Identifier.Text);
                        current = access.Expression;
                        continue;
                    case ThisExpressionSyntax:
                        UsesThis = true;
                        parts.Reverse();
                        return Split(parts, fromThis: true);
                    case IdentifierNameSyntax identifier:
                        parts.Add(identifier.Identifier.Text);
                        parts.Reverse();
                        return Split(parts, fromThis: false);
                    case ParenthesizedExpressionSyntax parenthesized:
                        current = parenthesized.Expression;
                        continue;
                    default:
                        throw Fail(expression, $"{expression} isn't a component or a property of one");
                }
            }

            (string, string?) Split(List<string> path, bool fromThis)
            {
                if (path.Count == 0)
                {
                    return (string.Empty, null);
                }

                if (path[0] == "components" || names.Contains(path[0]))
                {
                    return (path[0], path.Count > 1 ? string.Join('.', path.Skip(1)) : null);
                }

                if (!fromThis && path[0] == "resources")
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
            var text = type.ToString();
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

        /// <summary>A value: a literal, an enum or static member, a creation, a static call, an array, a resource, a component.</summary>
        public DesignerValue Value(ExpressionSyntax expression)
        {
            expression = Unwrap(expression);
            switch (expression)
            {
                case LiteralExpressionSyntax literal:
                    return Literal(literal);

                case PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression, Operand: var operand }
                    when Unwrap(operand) is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } number:
                {
                    var positive = Literal(number);
                    return positive with { Value = "-" + positive.Value };
                }

                case CastExpressionSyntax cast:
                    return Cast(cast);

                case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.BitwiseOrExpression } or:
                    return Flags(or, null);

                case ThisExpressionSyntax:
                    // The root as a value (ContainerControl = this): both styles write it; only this. qualifiers mark the .NET Framework style.
                    return DesignerValue.ReferenceTo(string.Empty);

                case IdentifierNameSyntax identifier:
                    if (identifier.Identifier.Text == "components" || names.Contains(identifier.Identifier.Text))
                    {
                        return DesignerValue.ReferenceTo(identifier.Identifier.Text);
                    }

                    throw Fail(expression, $"the name {identifier.Identifier.Text} isn't a component of the form (a field or a variable the designer doesn't know)");

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
                        Arguments = [.. (creation.ArgumentList?.Arguments ?? default).Select(a => Value(a.Expression))],
                    };

                case ArrayCreationExpressionSyntax array when array.Type.RankSpecifiers is [{ Sizes: [OmittedArraySizeExpressionSyntax] }] && array.Initializer is { } initializer:
                    return new DesignerValue
                    {
                        Kind = DesignerValueKinds.Array,
                        Type = TypeName(array.Type.ElementType),
                        Arguments = [.. initializer.Expressions.Select(Value)],
                    };

                case InvocationExpressionSyntax invocation:
                    return Call(invocation, null);

                case TypeOfExpressionSyntax typeOf:
                    return new DesignerValue { Kind = DesignerValueKinds.TypeOf, Type = typeOf.Type.ToString() == className ? FullClassName : TypeName(typeOf.Type) };

                case LambdaExpressionSyntax or AnonymousMethodExpressionSyntax:
                    throw Fail(expression, "a lambda");

                default:
                    throw Fail(expression, $"the expression {Shorten(expression.ToString())}");
            }
        }

        private string FullClassName => ns is null ? className : ns + "." + className;

        private static string Shorten(string text) => text.Length > 60 ? text[..57] + "…" : text;

        private static DesignerValue Literal(LiteralExpressionSyntax literal)
        {
            var value = literal.Token.Value;
            static DesignerValue Of(string type, string text) => new() { Kind = DesignerValueKinds.Literal, Type = type, Value = text };
            return value switch
            {
                null => DesignerValue.Null,
                string s => Of("string", s),
                bool b => Of("bool", b ? "true" : "false"),
                char c => Of("char", c.ToString()),
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
                // ((byte)(0)), ((int)(((byte)(255)))): a number of that type.
                var inner = Value(operand);
                if (inner.Kind == DesignerValueKinds.Literal && inner.Type is not ("string" or "bool" or "null"))
                {
                    return inner with { Type = predefined.Keyword.Text };
                }

                return inner;
            }

            var type = TypeName(cast.Type);
            switch (operand)
            {
                case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.BitwiseOrExpression } or:
                    return Flags(or, type);
                case InvocationExpressionSyntax invocation:
                    return Call(invocation, type);
                case LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } number:
                    return new DesignerValue { Kind = DesignerValueKinds.Enum, Type = type, Value = Literal(number).Value };
                case PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression }:
                    return new DesignerValue { Kind = DesignerValueKinds.Enum, Type = type, Value = Value(operand).Value };
                default:
                    var value = Value(operand);
                    return value.Kind == DesignerValueKinds.Member && value.Type == type ? value with { Kind = DesignerValueKinds.Enum, Members = [value.Value!], Value = null } : value;
            }
        }

        /// <summary><c>A.X | A.Y</c> (and Visual Studio's nested, parenthesized form): flags of one enum.</summary>
        private DesignerValue Flags(BinaryExpressionSyntax or, string? castType)
        {
            var members = new List<string>();
            string? type = castType;
            void Visit(ExpressionSyntax part)
            {
                part = Unwrap(part);
                switch (part)
                {
                    case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.BitwiseOrExpression } nested:
                        Visit(nested.Left);
                        Visit(nested.Right);
                        break;
                    case CastExpressionSyntax nestedCast when TypeName(nestedCast.Type) is var nestedType && (type is null || type == nestedType):
                        type = nestedType;
                        Visit(nestedCast.Expression);
                        break;
                    case MemberAccessExpressionSyntax { Name: IdentifierNameSyntax member } access:
                        var memberType = TypeName(access.Expression);
                        if (type is not null && type != memberType)
                        {
                            throw Fail(part, $"flags of different types ({type} and {memberType})");
                        }

                        type = memberType;
                        members.Add(member.Identifier.Text);
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
            // this.button1, this.components: a component.
            if (access.Expression is ThisExpressionSyntax && access.Name is IdentifierNameSyntax field)
            {
                UsesThis = true;
                if (field.Identifier.Text == "components" || names.Contains(field.Identifier.Text))
                {
                    return DesignerValue.ReferenceTo(field.Identifier.Text);
                }

                throw Fail(access, $"the name {field.Identifier.Text} isn't a component of the form");
            }

            if (RootName(access) is { } root && (root == "components" || names.Contains(root) || root == "resources"))
            {
                throw Fail(access, $"a property read of {root} ({Shorten(access.ToString())})");
            }

            // Type.Member: an enum member or a static field or property (Color.Red, SystemColors.Control, Properties.Resources.Logo).
            var type = TypeName(access.Expression);
            var member = access.Name.Identifier.Text;
            return resolver.IsEnum(type) == true
                ? new DesignerValue { Kind = DesignerValueKinds.Enum, Type = type, Members = [member] }
                : DesignerValue.MemberOf(type, member);
        }

        private static string? RootName(ExpressionSyntax expression)
        {
            while (expression is MemberAccessExpressionSyntax access)
            {
                expression = access.Expression;
            }

            return expression is IdentifierNameSyntax identifier ? identifier.Identifier.Text : null;
        }

        private DesignerValue Call(InvocationExpressionSyntax invocation, string? castType)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax { Name: IdentifierNameSyntax method } access)
            {
                throw Fail(invocation, $"a call to {invocation.Expression}");
            }

            var arguments = invocation.ArgumentList.Arguments;
            if (access.Expression is IdentifierNameSyntax { Identifier.Text: "resources" } && method.Identifier.Text is "GetObject" or "GetString" && arguments.Count == 1
                && Value(arguments[0].Expression) is { Kind: DesignerValueKinds.Literal, Type: "string" } key)
            {
                UsesResources = true;
                return new DesignerValue { Kind = DesignerValueKinds.Resource, Type = method.Identifier.Text == "GetString" ? "System.String" : castType, Value = key.Value };
            }

            if (RootName(access.Expression) is { } root && (root == "components" || names.Contains(root) || root == "resources") || access.Expression is ThisExpressionSyntax)
            {
                throw Fail(invocation, $"a call to {Shorten(invocation.Expression.ToString())} (the designer only calls static methods in values)");
            }

            return new DesignerValue
            {
                Kind = DesignerValueKinds.Call,
                Type = TypeName(access.Expression),
                Value = method.Identifier.Text,
                Arguments = [.. arguments.Select(a => Value(a.Expression))],
            };
        }
    }
}
