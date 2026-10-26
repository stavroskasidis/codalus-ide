using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Text;

namespace Codalus.LanguageServices.Host.Razor;

/// <summary>
/// Semantic tokens for a Razor file (ADR 0023), with Razor's own token types as the classic IDE colors them: the C# the
/// generator copied is classified by Roslyn in the generated documents and mapped back; the markup around it is scanned
/// here (<see cref="RazorMarkupScanner"/>). Which tags are components (or tag helpers in <c>.cshtml</c>) comes from the
/// generated code (<see cref="RazorBoundTags"/>), so it matches what the build compiles.
/// </summary>
internal static class RazorClassifier
{
    /// <summary>The tokens of <paramref name="range"/> of the Razor file (null: the whole file), LSP-encoded.</summary>
    public static async Task<SemanticTokens> TokensAsync(RazorMapping mapping, Protocol.Range? range, CancellationToken cancellationToken)
    {
        var text = mapping.RazorText;
        var span = new TextSpan(0, text.Length);
        if (range is not null && text.Lines.Count > 0)
        {
            span = Positions.ToSpan(text, range);
            span = TextSpan.FromBounds(text.Lines.GetLineFromPosition(span.Start).Start, text.Lines.GetLineFromPosition(span.End).EndIncludingLineBreak);
        }

        var tokens = await ClassifyAsync(mapping, span, cancellationToken).ConfigureAwait(false);
        return new SemanticTokens(SemanticTokenEncoder.Encode(text, tokens));
    }

    public static async Task<List<TokenSpan>> ClassifyAsync(RazorMapping mapping, TextSpan range, CancellationToken cancellationToken)
    {
        var code = CodeSpans(mapping);
        var bound = RazorBoundTags.Of(mapping.Parts[0].Text);
        var markup = RazorMarkupScanner.Scan(mapping.RazorText.ToString(), [.. code.Select(c => c.Razor)], bound, range);
        var csharp = await CSharpAsync(code, range, cancellationToken).ConfigureAwait(false);

        // A markup token over C# wins: a directive's keyword (the generator copies "using …"), a component's parameter name.
        var starts = markup.Select(t => t.Start).ToArray();
        var result = new List<TokenSpan>(markup.Count + csharp.Count);
        result.AddRange(markup);
        foreach (var token in csharp)
        {
            if (!Overlaps(markup, starts, token))
            {
                result.Add(token);
            }
        }

        return result;
    }

    /// <summary>Whether <paramref name="token"/> overlaps a token of <paramref name="sorted"/> (sorted, non-overlapping; <paramref name="starts"/> their starts).</summary>
    private static bool Overlaps(List<TokenSpan> sorted, int[] starts, TokenSpan token)
    {
        var index = Array.BinarySearch(starts, token.Start);
        index = index >= 0 ? index : ~index - 1; // the last token starting at or before it
        for (var i = Math.Max(0, index); i < sorted.Count && sorted[i].Start < token.End; i++)
        {
            if (sorted[i].End > token.Start)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The C# spans of the Razor file, sorted, each once (the generator's documents can map the same span: <c>@using</c>).</summary>
    private static List<(TextSpan Razor, RazorPart Part, TextSpan Generated)> CodeSpans(RazorMapping mapping)
    {
        var spans = new List<(TextSpan Razor, RazorPart Part, TextSpan Generated)>();
        var seen = new HashSet<TextSpan>();
        foreach (var part in mapping.Parts)
        {
            foreach (var (razor, generated) in part.Spans)
            {
                if (razor.Length > 0 && seen.Add(razor))
                {
                    spans.Add((razor, part, generated));
                }
            }
        }

        spans.Sort((a, b) => a.Razor.Start.CompareTo(b.Razor.Start));
        return spans;
    }

    /// <summary>Roslyn's classification of the C# spans in <paramref name="range"/>, one request per generated document.</summary>
    private static async Task<List<TokenSpan>> CSharpAsync(List<(TextSpan Razor, RazorPart Part, TextSpan Generated)> code, TextSpan range, CancellationToken cancellationToken)
    {
        var result = new List<TokenSpan>();
        foreach (var group in code.Where(c => c.Razor.Start < range.End && c.Razor.End > range.Start).GroupBy(c => c.Part))
        {
            var spans = group.OrderBy(c => c.Generated.Start).ToList();
            var bounds = TextSpan.FromBounds(spans[0].Generated.Start, spans.Max(s => s.Generated.End));
            var classified = await Classifier.GetClassifiedSpansAsync(group.Key.Document, bounds, cancellationToken).ConfigureAwait(false);
            var generatedStarts = spans.Select(s => s.Generated.Start).ToArray();
            foreach (var token in ClassificationMapper.Map(classified))
            {
                var index = Array.BinarySearch(generatedStarts, token.Start);
                index = index >= 0 ? index : ~index - 1;
                if (index >= 0 && token.End <= spans[index].Generated.End)
                {
                    result.Add(token with { Start = spans[index].Razor.Start + (token.Start - spans[index].Generated.Start) });
                }
            }
        }

        return result;
    }
}

/// <summary>
/// The tags the generator bound, read from its output: components (<c>OpenComponent&lt;T&gt;</c>) and the child content
/// parameters written as tags (<c>&lt;Found&gt;</c>), and in <c>.cshtml</c> the tag helpers' elements in document order
/// with their bound attributes.
/// </summary>
internal sealed partial class RazorBoundTags
{
    private static readonly ConditionalWeakTable<SourceText, RazorBoundTags> s_cache = new();

    public static RazorBoundTags Empty { get; } = new(new HashSet<string>(), new HashSet<string>(), [], new HashSet<string>());

    public RazorBoundTags(IReadOnlySet<string> components, IReadOnlySet<string> childContents, IReadOnlyList<string> tagHelperElements, IReadOnlySet<string> tagHelperAttributes)
    {
        Components = components;
        ChildContents = childContents;
        TagHelperElements = tagHelperElements;
        TagHelperAttributes = tagHelperAttributes;
    }

    /// <summary>The simple names of the components the file renders.</summary>
    public IReadOnlySet<string> Components { get; }

    /// <summary>Child content parameters written as tags (<c>RenderFragment</c> parameters: <c>&lt;Found&gt;</c>, <c>&lt;NotAuthorized&gt;</c>).</summary>
    public IReadOnlySet<string> ChildContents { get; }

    /// <summary>The elements tag helpers apply to, in document order (<c>.cshtml</c>).</summary>
    public IReadOnlyList<string> TagHelperElements { get; }

    /// <summary>The attributes tag helpers bind.</summary>
    public IReadOnlySet<string> TagHelperAttributes { get; }

    public bool IsComponent(string tag)
    {
        var dot = tag.LastIndexOf('.');
        var name = tag[(dot + 1)..];
        return name.Length > 0 && (char.IsUpper(name[0]) || dot >= 0) && (Components.Contains(name) || ChildContents.Contains(name));
    }

    public static RazorBoundTags Of(SourceText generated) => s_cache.GetValue(generated, Read);

    private static RazorBoundTags Read(SourceText generated)
    {
        var text = generated.ToString();
        var components = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in OpenComponent().Matches(text))
        {
            var name = match.Groups[1].Value;
            components.Add(name[(name.LastIndexOf('.') + 1)..]);
        }

        var childContents = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in ChildContent().Matches(text))
        {
            childContents.Add(match.Groups[1].Value);
        }

        var elements = TagHelperBegin().Matches(text).Select(m => m.Groups[1].Value).ToList();
        var fields = TagHelperField().Matches(text).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);
        var attributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in TagHelperAttribute().Matches(text))
        {
            if (match.Groups[1].Success)
            {
                attributes.Add(match.Groups[1].Value);
            }
            else if (fields.TryGetValue(match.Groups[2].Value, out var name))
            {
                attributes.Add(name);
            }
        }

        return new RazorBoundTags(components, childContents, elements, attributes);
    }

    [GeneratedRegex(@"\.OpenComponent<(?:global::)?([\w.]+)")]
    private static partial Regex OpenComponent();

    [GeneratedRegex(@"\.Add(?:Attribute|ComponentParameter)\(\d+, ""(\w+)"", \(global::Microsoft\.AspNetCore\.Components\.RenderFragment")]
    private static partial Regex ChildContent();

    [GeneratedRegex(@"__tagHelperScopeManager\.Begin\(""([^""]+)""")]
    private static partial Regex TagHelperBegin();

    [GeneratedRegex(@"(__tagHelperAttribute_\d+) = new global::Microsoft\.AspNetCore\.Razor\.TagHelpers\.TagHelperAttribute\(""([^""]+)""")]
    private static partial Regex TagHelperField();

    [GeneratedRegex(@"\.AddTagHelperAttribute\((?:""([^""]+)""|(__tagHelperAttribute_\d+)\))")]
    private static partial Regex TagHelperAttribute();
}
