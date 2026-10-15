using System.Text;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.QuickInfo;

namespace Codalus.LanguageServices.Host.Features;

/// <summary>Quick Info with Roslyn's <see cref="QuickInfoService"/>, rendered as Markdown.</summary>
internal static class HoverFeature
{
    public static async Task<Hover?> HoverAsync(Document document, int position, CancellationToken cancellationToken)
    {
        if (QuickInfoService.GetService(document) is not { } service
            || await service.GetQuickInfoAsync(document, position, cancellationToken).ConfigureAwait(false) is not { } info
            || info.Sections.IsEmpty)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var section in info.Sections)
        {
            var text = section.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append("\n\n");
            }

            builder.Append(section.Kind switch
            {
                QuickInfoSectionKinds.Description => Markdown.Code(text, Markdown.Fence(document)),
                QuickInfoSectionKinds.TypeParameters or QuickInfoSectionKinds.AnonymousTypes => Markdown.Code(text, Markdown.Fence(document)),
                QuickInfoSectionKinds.ReturnsDocumentationComments => "**Returns:** " + Markdown.Escape(text),
                QuickInfoSectionKinds.ValueDocumentationComments => "**Value:** " + Markdown.Escape(text),
                QuickInfoSectionKinds.Exception => "**Exceptions:** " + Markdown.Escape(text),
                _ => Markdown.Escape(text),
            });
        }

        if (builder.Length == 0)
        {
            return null;
        }

        var source = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        return new Hover(MarkupContent.Markdown(builder.ToString()), Positions.ToRange(source, info.Span));
    }
}
