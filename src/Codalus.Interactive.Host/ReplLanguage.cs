using Codalus.Interactive.Protocol;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Codalus.Interactive.Host;

/// <summary>
/// Editor services for the pending submission (ADR 0015): completion, signature help and semantic tokens. They run on
/// the compilation of the submission as the next one in the chain, so they see every variable and <c>#r</c> so far,
/// which a language server without the script state could not.
/// </summary>
internal static class ReplLanguage
{
    private static readonly CSharpParseOptions ScriptParseOptions = CSharpParseOptions.Default.WithKind(SourceCodeKind.Script);

    private static readonly HashSet<SyntaxKind> ControlKeywords =
    [
        SyntaxKind.IfKeyword, SyntaxKind.ElseKeyword, SyntaxKind.WhileKeyword, SyntaxKind.ForKeyword, SyntaxKind.ForEachKeyword,
        SyntaxKind.DoKeyword, SyntaxKind.SwitchKeyword, SyntaxKind.CaseKeyword, SyntaxKind.DefaultKeyword, SyntaxKind.BreakKeyword,
        SyntaxKind.ContinueKeyword, SyntaxKind.ReturnKeyword, SyntaxKind.GotoKeyword, SyntaxKind.TryKeyword, SyntaxKind.CatchKeyword,
        SyntaxKind.FinallyKeyword, SyntaxKind.ThrowKeyword, SyntaxKind.YieldKeyword, SyntaxKind.AwaitKeyword, SyntaxKind.InKeyword,
    ];

    private static readonly string[] Keywords =
    [
        "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false",
        "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is",
        "lock", "long", "nameof", "new", "null", "object", "operator", "out", "override", "params", "private", "protected",
        "public", "readonly", "record", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
        "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using",
        "var", "virtual", "void", "volatile", "while", "with", "yield",
    ];

    /// <summary>True when Enter should run the text: it parses as a whole submission (no open block, string or comment).</summary>
    public static bool IsComplete(string code) =>
        code.Trim().Length > 0 && SyntaxFactory.IsCompleteSubmission(SyntaxFactory.ParseSyntaxTree(code, ScriptParseOptions));

    public static ReplCompletionResult Completion(ReplSession session, string code, int position, CancellationToken cancellationToken)
    {
        position = Math.Clamp(position, 0, code.Length);
        var start = position;
        while (start > 0 && (char.IsLetterOrDigit(code[start - 1]) || code[start - 1] == '_'))
        {
            start--;
        }

        var (compilation, tree) = session.Pending(code);
        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot(cancellationToken);
        if (InsideStringOrComment(root, position))
        {
            return new ReplCompletionResult(start, []);
        }

        IEnumerable<ISymbol> symbols;
        var members = false;
        if (start > 0 && code[start - 1] == '.' && root.FindToken(start - 1).Parent is { } parent)
        {
            members = true;
            symbols = parent switch
            {
                MemberAccessExpressionSyntax access => MembersOf(model, access.Expression, start, cancellationToken),
                QualifiedNameSyntax qualified => MembersOf(model, qualified.Left, start, cancellationToken),
                MemberBindingExpressionSyntax binding when binding.Parent?.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>() is { } conditional =>
                    MembersOf(model, conditional.Expression, start, cancellationToken),
                _ => [],
            };
        }
        else
        {
            symbols = model.LookupSymbols(start);
        }

        var items = symbols
            .Where(s => s.CanBeReferencedByName && !s.IsImplicitlyDeclared && !(s.ContainingType?.IsScriptClass == false && s is IMethodSymbol { MethodKind: MethodKind.Constructor }))
            .GroupBy(s => s.Name, StringComparer.Ordinal)
            .Select(g => Item(g.First(), g.Count()))
            .ToList();
        if (!members)
        {
            items.AddRange(Keywords.Select(k => new CompletionItem(k) { Kind = CompletionItemKind.Keyword, SortText = "~" + k }));
        }

        return new ReplCompletionResult(start, [.. items.OrderBy(i => i.SortText ?? i.Label, StringComparer.OrdinalIgnoreCase)]);
    }

    public static SignatureHelp? SignatureHelp(ReplSession session, string code, int position, CancellationToken cancellationToken)
    {
        position = Math.Clamp(position, 0, code.Length);
        var (compilation, tree) = session.Pending(code);
        var model = compilation.GetSemanticModel(tree);
        var token = tree.GetRoot(cancellationToken).FindToken(Math.Max(0, position - 1));
        var arguments = token.Parent?.AncestorsAndSelf().OfType<ArgumentListSyntax>()
            .FirstOrDefault(a => a.OpenParenToken.Span.End <= position && (a.CloseParenToken.IsMissing || position <= a.CloseParenToken.SpanStart));
        if (arguments is null)
        {
            return null;
        }

        IMethodSymbol[] methods = arguments.Parent switch
        {
            InvocationExpressionSyntax invocation => [.. model.GetMemberGroup(invocation.Expression, cancellationToken).OfType<IMethodSymbol>()],
            ObjectCreationExpressionSyntax creation when model.GetTypeInfo(creation, cancellationToken).Type is INamedTypeSymbol type =>
                [.. type.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public)],
            _ => [],
        };
        if (methods.Length == 0)
        {
            return null;
        }

        var active = arguments.Arguments.GetSeparators().Count(s => s.SpanStart < position);
        var signatures = methods.Select(Signature).ToArray();
        var activeSignature = Array.FindIndex(methods, m => m.Parameters.Length > active || m.Parameters.LastOrDefault()?.IsParams == true);
        return new SignatureHelp(signatures, Math.Max(0, activeSignature), active);
    }

    /// <summary>LSP-encoded semantic tokens (<see cref="TokenTypes"/> legend), one token per line part.</summary>
    public static int[] Tokens(ReplSession session, string code, CancellationToken cancellationToken)
    {
        var (compilation, tree) = session.Pending(code);
        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot(cancellationToken);
        var spans = new List<(TextSpan Span, int Type)>();
        foreach (var token in root.DescendantTokens(descendIntoTrivia: true))
        {
            AddTrivia(token.LeadingTrivia, spans);
            if (Classify(model, token, cancellationToken) is { } type && token.Span.Length > 0)
            {
                spans.Add((token.Span, TokenTypes.IndexOf(type)));
            }

            AddTrivia(token.TrailingTrivia, spans);
        }

        var text = tree.GetText(cancellationToken);
        var data = new List<int>();
        int previousLine = 0, previousCharacter = 0;
        foreach (var (span, type) in spans.Where(s => s.Type >= 0).OrderBy(s => s.Span.Start))
        {
            var startLine = text.Lines.GetLineFromPosition(span.Start).LineNumber;
            var endLine = text.Lines.GetLineFromPosition(span.End).LineNumber;
            for (var line = startLine; line <= endLine; line++)
            {
                var lineSpan = text.Lines[line].Span;
                var start = Math.Max(span.Start, lineSpan.Start);
                var end = Math.Min(span.End, lineSpan.End);
                if (end <= start)
                {
                    continue;
                }

                var character = start - lineSpan.Start;
                data.Add(line - previousLine);
                data.Add(line == previousLine ? character - previousCharacter : character);
                data.Add(end - start);
                data.Add(type);
                data.Add(0);
                previousLine = line;
                previousCharacter = character;
            }
        }

        return [.. data];
    }

    private static IEnumerable<ISymbol> MembersOf(SemanticModel model, ExpressionSyntax expression, int position, CancellationToken cancellationToken)
    {
        var symbol = model.GetSymbolInfo(expression, cancellationToken).Symbol;
        if (symbol is INamespaceSymbol ns)
        {
            return model.LookupNamespacesAndTypes(position, ns);
        }

        if (symbol is INamedTypeSymbol type && expression is not ThisExpressionSyntax && model.GetTypeInfo(expression, cancellationToken).Type is not null &&
            SymbolEqualityComparer.Default.Equals(model.GetTypeInfo(expression, cancellationToken).Type, type) && !IsValue(model, expression, cancellationToken))
        {
            return model.LookupStaticMembers(position, type);
        }

        return model.GetTypeInfo(expression, cancellationToken).Type is { } instanceType
            ? model.LookupSymbols(position, instanceType, includeReducedExtensionMethods: true).Where(s => !s.IsStatic || s is IMethodSymbol { MethodKind: MethodKind.ReducedExtension })
            : [];
    }

    /// <summary>True when the expression is a value (a variable named like its type, say), not the type itself.</summary>
    private static bool IsValue(SemanticModel model, ExpressionSyntax expression, CancellationToken cancellationToken) =>
        model.GetSymbolInfo(expression, cancellationToken).Symbol is ILocalSymbol or IFieldSymbol or IPropertySymbol or IParameterSymbol or IMethodSymbol;

    private static bool InsideStringOrComment(SyntaxNode root, int position)
    {
        if (position == 0)
        {
            return false;
        }

        var token = root.FindToken(position - 1, findInsideTrivia: true);
        if (token.Span.Contains(position - 1) && token.Kind() is SyntaxKind.StringLiteralToken or SyntaxKind.InterpolatedStringTextToken or SyntaxKind.CharacterLiteralToken)
        {
            return position < token.Span.End || !token.Text.EndsWith('"');
        }

        var trivia = root.FindTrivia(position - 1);
        return trivia.Kind() is SyntaxKind.SingleLineCommentTrivia or SyntaxKind.MultiLineCommentTrivia;
    }

    private static CompletionItem Item(ISymbol symbol, int overloads)
    {
        var detail = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        if (overloads > 1)
        {
            detail += $" (+{overloads - 1} overloads)";
        }

        return new CompletionItem(symbol.Name) { Kind = Kind(symbol), Detail = detail };
    }

    private static int Kind(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol => CompletionItemKind.Module,
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => CompletionItemKind.Interface,
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => CompletionItemKind.Enum,
        INamedTypeSymbol { TypeKind: TypeKind.Struct } => CompletionItemKind.Struct,
        INamedTypeSymbol => CompletionItemKind.Class,
        ITypeParameterSymbol => CompletionItemKind.TypeParameter,
        IMethodSymbol => CompletionItemKind.Method,
        IPropertySymbol => CompletionItemKind.Property,
        IFieldSymbol { IsConst: true } => CompletionItemKind.Constant,
        IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => CompletionItemKind.EnumMember,
        IFieldSymbol => CompletionItemKind.Field,
        IEventSymbol => CompletionItemKind.Event,
        _ => CompletionItemKind.Variable,
    };

    private static SignatureInformation Signature(IMethodSymbol method)
    {
        var parameters = method.Parameters.Select(p => p.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)).ToArray();
        var name = method.MethodKind == MethodKind.Constructor
            ? method.ContainingType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
            : $"{method.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {method.Name}";
        var label = $"{name}({string.Join(", ", parameters)})";
        return new SignatureInformation(label, null, [.. parameters.Select(p => new ParameterInformation(p, null))]);
    }

    private static void AddTrivia(SyntaxTriviaList trivia, List<(TextSpan Span, int Type)> spans)
    {
        foreach (var item in trivia)
        {
            if (item.Kind() is SyntaxKind.SingleLineCommentTrivia or SyntaxKind.MultiLineCommentTrivia)
            {
                spans.Add((item.Span, TokenTypes.IndexOf("comment")));
            }
            else if (item.Kind() is SyntaxKind.SingleLineDocumentationCommentTrivia or SyntaxKind.MultiLineDocumentationCommentTrivia)
            {
                spans.Add((item.Span, TokenTypes.IndexOf("xmlDocComment")));
            }
        }
    }

    private static string? Classify(SemanticModel model, SyntaxToken token, CancellationToken cancellationToken)
    {
        if (token.Parent is DirectiveTriviaSyntax || token.Parent?.Parent is DirectiveTriviaSyntax && token.IsKeyword())
        {
            return token.IsKind(SyntaxKind.StringLiteralToken) ? "string" : "preprocessor";
        }

        if (SyntaxFacts.IsKeywordKind(token.Kind()))
        {
            return ControlKeywords.Contains(token.Kind()) ? "controlKeyword" : "keyword";
        }

        switch (token.Kind())
        {
            case SyntaxKind.StringLiteralToken when token.Text.StartsWith('@'):
                return "stringVerbatim";
            case SyntaxKind.StringLiteralToken or SyntaxKind.CharacterLiteralToken or SyntaxKind.InterpolatedStringTextToken
                or SyntaxKind.InterpolatedStringStartToken or SyntaxKind.InterpolatedStringEndToken or SyntaxKind.InterpolatedVerbatimStringStartToken
                or SyntaxKind.SingleLineRawStringLiteralToken or SyntaxKind.MultiLineRawStringLiteralToken
                or SyntaxKind.InterpolatedSingleLineRawStringStartToken or SyntaxKind.InterpolatedMultiLineRawStringStartToken
                or SyntaxKind.InterpolatedRawStringEndToken or SyntaxKind.Utf8StringLiteralToken:
                return "string";
            case SyntaxKind.NumericLiteralToken:
                return "number";
            case SyntaxKind.IdentifierToken:
                break;
            default:
                return null;
        }

        var node = token.Parent;
        if (node is null)
        {
            return null;
        }

        if (node is IdentifierNameSyntax { IsVar: true } && model.GetSymbolInfo(node, cancellationToken).Symbol is not INamedTypeSymbol { Name: "var" })
        {
            return "keyword";
        }

        var symbol = model.GetDeclaredSymbol(node, cancellationToken)
            ?? model.GetSymbolInfo(node, cancellationToken) switch
            {
                { Symbol: { } s } => s,
                { CandidateSymbols: [var first, ..] } => first,
                _ => null,
            };
        return symbol switch
        {
            INamespaceSymbol => "namespace",
            INamedTypeSymbol { IsRecord: true, TypeKind: TypeKind.Struct } => "recordStruct",
            INamedTypeSymbol { IsRecord: true } => "record",
            INamedTypeSymbol { TypeKind: TypeKind.Interface } => "interface",
            INamedTypeSymbol { TypeKind: TypeKind.Enum } => "enum",
            INamedTypeSymbol { TypeKind: TypeKind.Struct } => "struct",
            INamedTypeSymbol { TypeKind: TypeKind.Delegate } => "delegate",
            INamedTypeSymbol => "class",
            ITypeParameterSymbol => "typeParameter",
            IMethodSymbol { MethodKind: MethodKind.ReducedExtension } or IMethodSymbol { IsExtensionMethod: true } => "extensionMethod",
            IMethodSymbol => "method",
            IPropertySymbol => "property",
            IFieldSymbol { IsConst: true } => "constant",
            IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => "enumMember",
            IFieldSymbol { ContainingType.IsScriptClass: true } => "local",
            IFieldSymbol => "field",
            IEventSymbol => "event",
            ILocalSymbol => "local",
            IParameterSymbol => "parameter",
            ILabelSymbol => "label",
            _ => null,
        };
    }
}
