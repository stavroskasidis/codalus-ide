using Codalus.LanguageServices.Host.Razor;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis.Text;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>Razor markup tokens (<see cref="RazorMarkupScanner"/>) on their own, with the C# spans given.</summary>
public sealed class RazorMarkupScannerTests
{
    /// <summary>The tokens as "text:type", with the C# spans marked in <paramref name="text"/> by ⟦ and ⟧.</summary>
    private static List<string> Scan(string text, RazorBoundTags? bound = null, TextSpan? range = null)
    {
        var code = new List<TextSpan>();
        var plain = new System.Text.StringBuilder();
        var start = -1;
        foreach (var c in text)
        {
            if (c == '⟦')
            {
                start = plain.Length;
            }
            else if (c == '⟧')
            {
                code.Add(TextSpan.FromBounds(start, plain.Length));
            }
            else
            {
                plain.Append(c);
            }
        }

        var source = plain.ToString();
        var tokens = RazorMarkupScanner.Scan(source, [.. code], bound ?? RazorBoundTags.Empty, range ?? new TextSpan(0, source.Length));
        return [.. tokens.Select(t => $"{source.AsSpan(t.Start, t.Length)}:{TokenTypes.All[t.Type]}")];
    }

    private static RazorBoundTags Components(params string[] names) => new(new HashSet<string>(names), new HashSet<string>(), [], new HashSet<string>());

    [Fact]
    public void Components_are_the_bound_capitalized_tags()
    {
        var tokens = Scan("<Greeting ⟦Name⟧=\"x\" class=\"y\" /><Unknown /><greeting />", Components("Greeting"));
        Assert.Equal(
            ["<:markupTagDelimiter", "Greeting:razorComponentElement", "Name:razorComponentAttribute", "=:markupOperator", "\":markupAttributeQuote",
             "x:markupAttributeValue", "\":markupAttributeQuote", "class:markupAttribute", "=:markupOperator", "\":markupAttributeQuote", "y:markupAttributeValue",
             "\":markupAttributeQuote", "/>:markupTagDelimiter",
             "<:markupTagDelimiter", "Unknown:markupElement", "/>:markupTagDelimiter",
             "<:markupTagDelimiter", "greeting:markupElement", "/>:markupTagDelimiter"],
            tokens);
    }

    [Fact]
    public void Csharp_spans_are_left_to_roslyn()
    {
        // An expression's C#, a code block's (with its '<'), an attribute's value: no markup tokens inside.
        var tokens = Scan("<p>@⟦count⟧</p>\n@⟦if (a < b)\n{⟧\n<b>x</b>\n⟦}⟧\n<a href=\"@(⟦url⟧)\">");
        Assert.Equal(
            ["<:markupTagDelimiter", "p:markupElement", ">:markupTagDelimiter", "@:razorTransition", "</:markupTagDelimiter", "p:markupElement", ">:markupTagDelimiter",
             "@:razorTransition", "<:markupTagDelimiter", "b:markupElement", ">:markupTagDelimiter", "</:markupTagDelimiter", "b:markupElement", ">:markupTagDelimiter",
             "<:markupTagDelimiter", "a:markupElement", "href:markupAttribute", "=:markupOperator", "\":markupAttributeQuote", "@(:razorTransition", "):razorTransition",
             "\":markupAttributeQuote", ">:markupTagDelimiter"],
            tokens);
    }

    [Fact]
    public void Escapes_addresses_scripts_and_directive_lines_are_not_markup()
    {
        Assert.Empty(Scan("mail me@example.com or @@home"));
        Assert.Equal(["@:razorTransition", "typeparam:razorDirective"], Scan("@typeparam TItem where TItem : IComparable<TItem>"));
        Assert.Equal(
            ["<:markupTagDelimiter", "script:markupElement", ">:markupTagDelimiter", "</:markupTagDelimiter", "script:markupElement", ">:markupTagDelimiter"],
            Scan("<script>if (a<b) { }</script>"));
        Assert.Equal(["@:razorTransition", "page:razorDirective", "\"/counter\":string"], Scan("@page \"/counter\""));
    }

    [Fact]
    public void Comments_blocks_and_directive_attributes()
    {
        Assert.Equal(["@:razorCommentTransition", "*:razorCommentStar", " a\nb :razorComment", "*:razorCommentStar", "@:razorCommentTransition"], Scan("@* a\nb *@"));
        Assert.Equal(["<!--:markupCommentPunctuation", " <p> :markupComment", "-->:markupCommentPunctuation"], Scan("<!-- <p> -->"));
        Assert.Equal(["@:razorTransition", "code:razorDirective", "{:razorTransition", "}:razorTransition"], Scan("@code {⟦ int x; ⟧}"));
        Assert.Equal(["@{:razorTransition", "}:razorTransition"], Scan("@{⟦ var x = 1; ⟧}"));
        Assert.Equal(
            ["<:markupTagDelimiter", "input:markupElement", "@:razorTransition", "bind:razorDirectiveAttribute", "::razorDirectiveColon", "event:razorDirectiveAttribute",
             "=:markupOperator", "\":markupAttributeQuote", "oninput:markupAttributeValue", "\":markupAttributeQuote", "/>:markupTagDelimiter"],
            Scan("<input @bind:event=\"oninput\" />"));
    }

    [Fact]
    public void Tag_helpers_come_in_document_order_with_their_bound_attributes()
    {
        var bound = new RazorBoundTags(new HashSet<string>(), new HashSet<string>(), ["a"], new HashSet<string> { "asp-page" });
        var tokens = Scan("<a asp-page=\"/x\" class=\"c\">A</a><a href=\"/\">B</a>", bound);
        Assert.Contains("a:razorTagHelperElement", tokens);
        Assert.Contains("asp-page:razorTagHelperAttribute", tokens);
        Assert.Contains("class:markupAttribute", tokens);
        Assert.Equal(2, tokens.Count(t => t == "a:razorTagHelperElement")); // the start tag and its end tag
        Assert.Equal(2, tokens.Count(t => t == "a:markupElement")); // the second <a>, not a tag helper
    }

    [Fact]
    public void Only_the_range_is_returned_though_the_scan_starts_at_the_top()
    {
        var text = "@* x *@\n<p class=\"a\">\n<b>";
        var second = text.IndexOf("<p", StringComparison.Ordinal);
        Assert.Equal(["<:markupTagDelimiter", "p:markupElement", "class:markupAttribute", "=:markupOperator", "\":markupAttributeQuote", "a:markupAttributeValue", "\":markupAttributeQuote", ">:markupTagDelimiter"],
            Scan(text, range: new TextSpan(second, text.IndexOf('\n', second) - second)));

        // A large file scans in one linear pass.
        var large = string.Concat(Enumerable.Repeat("<div class=\"row\">@* note *@ <span>@@x</span></div>\n", 20_000));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var tokens = RazorMarkupScanner.Scan(large, [], RazorBoundTags.Empty, new TextSpan(0, 200));
        Assert.NotEmpty(tokens);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"{watch.Elapsed}");
    }
}
