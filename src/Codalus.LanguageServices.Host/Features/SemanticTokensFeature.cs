using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Codalus.LanguageServices.Host.Features;

/// <summary>Semantic tokens from Roslyn's classifier, for the whole document or (preferably) the visible range.</summary>
internal static class SemanticTokensFeature
{
    public static SemanticTokensOptions Options => new(new SemanticTokensLegend(TokenTypes.All, TokenModifiers.All), Range: true, Full: true);

    public static async Task<SemanticTokens> TokensAsync(Document document, Protocol.Range? range, CancellationToken cancellationToken)
    {
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var span = range is null ? new TextSpan(0, text.Length) : Positions.ToSpan(text, range);

        // Whole lines, so that tokens crossing the range edges are complete.
        if (range is not null && text.Lines.Count > 0)
        {
            span = TextSpan.FromBounds(text.Lines.GetLineFromPosition(span.Start).Start, text.Lines.GetLineFromPosition(span.End).EndIncludingLineBreak);
        }

        var classified = await Classifier.GetClassifiedSpansAsync(document, span, cancellationToken).ConfigureAwait(false);
        return new SemanticTokens(SemanticTokenEncoder.Encode(text, ClassificationMapper.Map(classified)));
    }

    /// <summary>The <see cref="SemanticTokens.ResultId"/> of an answer from the syntax alone (<see cref="SyntacticAsync"/>).</summary>
    public const string Syntactic = "syntactic";

    /// <summary>
    /// The tokens a C# file's syntax gives without its project's compilation: keywords, literals, comments,
    /// preprocessor directives and excluded code, operators and punctuation, classified as Roslyn's classifier does,
    /// so the full answer only adds identifiers' colors. Null for other languages.
    /// </summary>
    public static async Task<SemanticTokens?> SyntacticAsync(Document document, Protocol.Range? range, CancellationToken cancellationToken)
    {
        if (document.Project.Language != LanguageNames.CSharp
            || await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } root)
        {
            return null;
        }

        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var span = range is null ? new TextSpan(0, text.Length) : Positions.ToSpan(text, range);
        if (range is not null && text.Lines.Count > 0)
        {
            span = TextSpan.FromBounds(text.Lines.GetLineFromPosition(span.Start).Start, text.Lines.GetLineFromPosition(span.End).EndIncludingLineBreak);
        }

        var spans = new List<ClassifiedSpan>();
        foreach (var token in root.DescendantTokens(span))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Trivia(token.LeadingTrivia, span, spans);
            if (Classify(token) is { } type && token.Span.Length > 0)
            {
                spans.Add(new ClassifiedSpan(type, token.Span));
                if (type == ClassificationTypeNames.StringLiteral)
                {
                    Escapes(token, spans);
                }
            }

            Trivia(token.TrailingTrivia, span, spans);
        }

        return new SemanticTokens(SemanticTokenEncoder.Encode(text, ClassificationMapper.Map(spans))) { ResultId = Syntactic };
    }

    private static void Trivia(SyntaxTriviaList trivia, TextSpan within, List<ClassifiedSpan> spans)
    {
        foreach (var item in trivia)
        {
            if (!item.Span.IntersectsWith(within))
            {
                continue;
            }

            switch (item.Kind())
            {
                case SyntaxKind.SingleLineCommentTrivia or SyntaxKind.MultiLineCommentTrivia:
                    spans.Add(new ClassifiedSpan(ClassificationTypeNames.Comment, item.Span));
                    break;
                case SyntaxKind.DisabledTextTrivia:
                    spans.Add(new ClassifiedSpan(ClassificationTypeNames.ExcludedCode, item.Span));
                    break;
                default:
                    if (item.IsDirective && item.GetStructure() is DirectiveTriviaSyntax directive)
                    {
                        // "#if", "#region": the hash and the directive's keyword.
                        spans.Add(new ClassifiedSpan(ClassificationTypeNames.PreprocessorKeyword, TextSpan.FromBounds(directive.HashToken.SpanStart, directive.DirectiveNameToken.Span.End)));
                    }

                    break;
            }
        }
    }

    private static string? Classify(SyntaxToken token)
    {
        var kind = token.Kind();
        if (SyntaxFacts.IsReservedKeyword(kind) || (kind != SyntaxKind.IdentifierToken && SyntaxFacts.IsContextualKeyword(kind) && kind is not (SyntaxKind.VarKeyword or SyntaxKind.NameOfKeyword)))
        {
            return IsControl(token) ? ClassificationTypeNames.ControlKeyword : ClassificationTypeNames.Keyword;
        }

        switch (kind)
        {
            case SyntaxKind.NumericLiteralToken:
                return ClassificationTypeNames.NumericLiteral;
            case SyntaxKind.CharacterLiteralToken or SyntaxKind.Utf8StringLiteralToken
                or SyntaxKind.SingleLineRawStringLiteralToken or SyntaxKind.MultiLineRawStringLiteralToken
                or SyntaxKind.Utf8SingleLineRawStringLiteralToken or SyntaxKind.Utf8MultiLineRawStringLiteralToken
                or SyntaxKind.InterpolatedRawStringEndToken or SyntaxKind.InterpolatedSingleLineRawStringStartToken or SyntaxKind.InterpolatedMultiLineRawStringStartToken:
                return ClassificationTypeNames.StringLiteral;
            case SyntaxKind.StringLiteralToken:
                return token.Text.StartsWith('@') ? ClassificationTypeNames.VerbatimStringLiteral : ClassificationTypeNames.StringLiteral;
            case SyntaxKind.InterpolatedStringStartToken or SyntaxKind.InterpolatedVerbatimStringStartToken or SyntaxKind.InterpolatedStringEndToken or SyntaxKind.InterpolatedStringTextToken:
                return token.Parent?.FirstAncestorOrSelf<InterpolatedStringExpressionSyntax>() is { StringStartToken.RawKind: (int)SyntaxKind.InterpolatedVerbatimStringStartToken }
                    ? ClassificationTypeNames.VerbatimStringLiteral
                    : ClassificationTypeNames.StringLiteral;
        }

        if (!SyntaxFacts.IsPunctuation(kind))
        {
            return null; // identifiers wait for the semantic answer
        }

        return IsOperator(token) ? ClassificationTypeNames.Operator : ClassificationTypeNames.Punctuation;
    }

    private static bool IsControl(SyntaxToken token) => token.Kind() switch
    {
        SyntaxKind.IfKeyword or SyntaxKind.ElseKeyword or SyntaxKind.WhileKeyword or SyntaxKind.ForKeyword or SyntaxKind.ForEachKeyword
            or SyntaxKind.DoKeyword or SyntaxKind.SwitchKeyword or SyntaxKind.CaseKeyword or SyntaxKind.TryKeyword or SyntaxKind.CatchKeyword
            or SyntaxKind.FinallyKeyword or SyntaxKind.GotoKeyword or SyntaxKind.BreakKeyword or SyntaxKind.ContinueKeyword
            or SyntaxKind.ReturnKeyword or SyntaxKind.ThrowKeyword or SyntaxKind.YieldKeyword => true,
        SyntaxKind.DefaultKeyword => token.Parent is DefaultSwitchLabelSyntax,
        _ => false,
    };

    private static bool IsOperator(SyntaxToken token)
    {
        var kind = token.Kind();
        if (SyntaxFacts.IsBinaryExpressionOperatorToken(kind) || SyntaxFacts.IsAssignmentExpressionOperatorToken(kind)
            || SyntaxFacts.IsPrefixUnaryExpressionOperatorToken(kind) || SyntaxFacts.IsPostfixUnaryExpression(kind)
            || kind is SyntaxKind.EqualsGreaterThanToken or SyntaxKind.QuestionQuestionToken or SyntaxKind.DotDotToken)
        {
            // "<" and ">" of a type argument list and "*" of a pointer type are punctuation; "!" of null-forgiving too.
            return token.Parent is not (TypeArgumentListSyntax or TypeParameterListSyntax or PointerTypeSyntax or FunctionPointerTypeSyntax);
        }

        return kind is SyntaxKind.QuestionToken or SyntaxKind.ColonToken && token.Parent is ConditionalExpressionSyntax;
    }

    /// <summary>The escape sequences of a regular string or character literal.</summary>
    private static void Escapes(SyntaxToken token, List<ClassifiedSpan> spans)
    {
        if (token.Kind() is not (SyntaxKind.StringLiteralToken or SyntaxKind.CharacterLiteralToken or SyntaxKind.InterpolatedStringTextToken)
            || token.Parent?.FirstAncestorOrSelf<InterpolatedStringExpressionSyntax>() is { StringStartToken.RawKind: (int)SyntaxKind.InterpolatedVerbatimStringStartToken })
        {
            return;
        }

        var text = token.Text;
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (text[i] != '\\')
            {
                continue;
            }

            var length = text[i + 1] switch
            {
                'x' => 2 + HexDigits(text, i + 2, 4),
                'u' => 6,
                'U' => 10,
                _ => 2,
            };
            length = Math.Min(length, text.Length - i - (token.IsKind(SyntaxKind.InterpolatedStringTextToken) ? 0 : 1));
            if (length >= 2)
            {
                spans.Add(new ClassifiedSpan(ClassificationTypeNames.StringEscapeCharacter, new TextSpan(token.SpanStart + i, length)));
            }

            i += length - 1;
        }
    }

    private static int HexDigits(string text, int start, int most)
    {
        var count = 0;
        while (count < most && start + count < text.Length && char.IsAsciiHexDigit(text[start + count]))
        {
            count++;
        }

        return count;
    }
}
