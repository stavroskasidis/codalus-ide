using System.Text;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.QuickInfo;

namespace Codalus.LanguageServices.Host.Features;

/// <summary>
/// Quick Info with Roslyn's <see cref="QuickInfoService"/>: as Markdown for any client, and as classified text (the
/// <c>codalusDescription</c> extension) for the editor's own Quick Info, which draws it in the editor's colors.
/// </summary>
internal static class HoverFeature
{
    public static async Task<Hover?> HoverAsync(Document document, int position, CancellationToken cancellationToken)
    {
        if (QuickInfoService.GetService(document) is not { } service
            || await FrameworkGuard.IsMissingAsync(document, cancellationToken).ConfigureAwait(false)
            || await service.GetQuickInfoAsync(document, position, cancellationToken).ConfigureAwait(false) is not { } info
            || info.Sections.IsEmpty)
        {
            return null;
        }

        var description = Describe(info);
        var markdown = ToMarkdown(description, Markdown.Fence(document));
        if (markdown.Length == 0)
        {
            return null;
        }

        var source = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        return new Hover(MarkupContent.Markdown(markdown), Positions.ToRange(source, info.Span)) { Description = description };
    }

    /// <summary>Quick Info's sections as a description: the signature (without its overload count), then each section's body.</summary>
    internal static SymbolDescription Describe(QuickInfoItem info)
    {
        ClassifiedRun[] signature = [];
        var overloads = 0;
        var sections = new List<DescriptionSection>();
        foreach (var section in info.Sections)
        {
            var runs = ClassifiedText.Runs(section.TaggedParts);
            if (section.Kind == QuickInfoSectionKinds.Description)
            {
                (signature, overloads) = ClassifiedText.SplitOverloads(ClassifiedText.Trim(runs));
                continue;
            }

            var kind = section.Kind switch
            {
                QuickInfoSectionKinds.DocumentationComments => DescriptionSectionKinds.Summary,
                QuickInfoSectionKinds.RemarksDocumentationComments => DescriptionSectionKinds.Remarks,
                QuickInfoSectionKinds.ReturnsDocumentationComments => DescriptionSectionKinds.Returns,
                QuickInfoSectionKinds.ValueDocumentationComments => DescriptionSectionKinds.Value,
                QuickInfoSectionKinds.TypeParameters => DescriptionSectionKinds.TypeParameters,
                QuickInfoSectionKinds.AnonymousTypes => DescriptionSectionKinds.AnonymousTypes,
                QuickInfoSectionKinds.Usage => DescriptionSectionKinds.Usage,
                QuickInfoSectionKinds.Exception => DescriptionSectionKinds.Exceptions,
                QuickInfoSectionKinds.Captures => DescriptionSectionKinds.Captures,
                "NullabilityAnalysis" => DescriptionSectionKinds.Nullability,
                _ => DescriptionSectionKinds.Other,
            };
            var body = WithoutHeading(runs, kind);
            if (body.Length > 0)
            {
                sections.Add(new DescriptionSection(kind, body));
            }
        }

        return new SymbolDescription(signature, [.. sections]) { Glyph = ClassifiedText.Glyph(info.Tags), Overloads = overloads };
    }

    /// <summary>
    /// A section's body. Roslyn writes some sections' headings itself ("Returns:", "Exceptions:" on a line of their own,
    /// then the body indented); the client writes headings, so they go, with the indentation.
    /// </summary>
    internal static ClassifiedRun[] WithoutHeading(List<ClassifiedRun> runs, string kind)
    {
        var lines = ClassifiedText.Lines(runs);
        while (lines.Count > 1 && lines[0].All(r => string.IsNullOrWhiteSpace(r.Text)))
        {
            lines.RemoveAt(0); // Roslyn starts some sections with a line break
        }

        if (kind is not (DescriptionSectionKinds.Summary or DescriptionSectionKinds.TypeParameters or DescriptionSectionKinds.AnonymousTypes or DescriptionSectionKinds.Nullability)
            && lines.Count > 1 && lines[0].All(r => r.Kind == ClassifiedRun.PlainText)
            && string.Concat(lines[0].Select(r => r.Text)).Trim() is { Length: > 1 } heading && heading.EndsWith(':'))
        {
            lines.RemoveAt(0);
        }

        var result = new List<ClassifiedRun>();
        foreach (var line in lines)
        {
            if (result.Count > 0)
            {
                result.Add(new ClassifiedRun("\n", ClassifiedRun.LineBreak));
            }

            for (var i = 0; i < line.Count; i++)
            {
                var run = line[i];
                result.Add(i == 0 && run.Kind == ClassifiedRun.PlainText ? run with { Text = run.Text.TrimStart() } : run);
            }
        }

        return ClassifiedText.Trim(result);
    }

    /// <summary>The description as Markdown: the signature in a code block, then each section, its heading once.</summary>
    internal static string ToMarkdown(SymbolDescription description, string fence)
    {
        var builder = new StringBuilder();
        var signature = string.Concat(description.Signature.Select(r => r.Text));
        if (signature.Length > 0)
        {
            builder.Append(Markdown.Code(signature + Overloads(description.Overloads), fence));
        }

        foreach (var section in description.Sections)
        {
            var text = string.Concat(section.Text.Select(r => r.Text));
            if (builder.Length > 0)
            {
                builder.Append("\n\n");
            }

            builder.Append(Heading(section.Kind) is { } heading ? "**" + heading + "** " + Markdown.Escape(text) : Markdown.Escape(text));
        }

        return builder.ToString();
    }

    private static string Overloads(int count) => count switch
    {
        0 => string.Empty,
        1 => " (+ 1 overload)",
        _ => FormattableString.Invariant($" (+ {count} overloads)"),
    };

    private static string? Heading(string kind) => kind switch
    {
        DescriptionSectionKinds.Returns => "Returns:",
        DescriptionSectionKinds.Value => "Value:",
        DescriptionSectionKinds.Exceptions => "Exceptions:",
        DescriptionSectionKinds.Remarks => "Remarks:",
        DescriptionSectionKinds.Usage => "Usage:",
        _ => null,
    };
}
