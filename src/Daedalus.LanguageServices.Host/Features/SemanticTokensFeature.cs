using Daedalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Text;

namespace Daedalus.LanguageServices.Host.Features;

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
}
