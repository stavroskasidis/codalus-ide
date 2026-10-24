using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Codalus.Website.Site;

/// <summary>One page of the user guide, rendered.</summary>
/// <param name="Slug">The page's name in its address: <c>docs/&lt;slug&gt;/</c>; empty for the README (<c>docs/</c>).</param>
/// <param name="Outline">The page's ## headings, for its "On this page" list.</param>
internal sealed record GuidePage(string Slug, string Source, string Title, string Description, string Html, IReadOnlyList<SearchSection> Sections, IReadOnlyList<GuideHeading> Outline)
{
    /// <summary>The page's address, relative to the site's base.</summary>
    public string Url => GuideUrl(Slug);

    public static string GuideUrl(string slug) => slug.Length == 0 ? "docs/" : $"docs/{slug}/";
}

/// <summary>A part of a page for the docs search: the text under one heading.</summary>
internal sealed record SearchSection(string Heading, string Id, string Text);

/// <summary>A heading of a page and its anchor.</summary>
internal sealed record GuideHeading(string Text, string Id);

/// <summary>
/// The user guide (<c>docs/user-guide/*.md</c>) as site pages, in the order the README lists them. Links between guide
/// pages stay on the site; links to other files in the repository go to GitHub, and a link to a file that doesn't exist
/// is an error. Rendered again when a page changes, so <c>dotnet watch</c> shows edits to the guide too.
/// </summary>
internal sealed partial class UserGuide(SiteOptions options)
{
    public const string Folder = "docs/user-guide";

    private static readonly MarkdownPipeline s_pipeline = new MarkdownPipelineBuilder()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseTaskLists()
        .UseAutoLinks()
        .Build();

    private readonly Lock _lock = new();
    private (DateTime Stamp, Rendered Guide)? _cache;

    private string GuideDir => Path.Combine(options.RepoRoot, Folder);

    /// <summary>The guide's pages in reading order, the README first.</summary>
    public IReadOnlyList<GuidePage> Pages => Current().Pages;

    /// <summary>Broken links found while rendering.</summary>
    public IReadOnlyList<string> Errors => Current().Errors;

    public GuidePage? Find(string? slug) => Pages.FirstOrDefault(p => p.Slug == (slug ?? "").Trim('/'));

    private Rendered Current()
    {
        var stamp = Directory.GetFiles(GuideDir, "*.md").Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max();
        lock (_lock)
        {
            if (_cache is not { } cache || cache.Stamp != stamp)
            {
                _cache = cache = (stamp, Render());
            }

            return cache.Guide;
        }
    }

    private Rendered Render()
    {
        var files = Directory.GetFiles(GuideDir, "*.md").Select(Path.GetFileName).OfType<string>().ToList();
        if (!files.Contains("README.md"))
        {
            throw new SiteException($"{Folder}/README.md is missing");
        }

        var listed = ListedPages(File.ReadAllText(Path.Combine(GuideDir, "README.md")));
        var ordered = listed.Where(files.Contains)
            .Prepend("README.md")
            .Concat(files.Where(f => f != "README.md" && !listed.Contains(f)).Order(StringComparer.Ordinal))
            .Distinct()
            .ToList();
        var errors = new List<string>();
        return new Rendered(ordered.Select(file => RenderPage(file, errors)).ToList(), errors);
    }

    private static string SlugOf(string markdownFile) => markdownFile == "README.md" ? "" : Path.GetFileNameWithoutExtension(markdownFile);

    /// <summary>The guide pages the README links to, in its order.</summary>
    private static List<string> ListedPages(string readme) =>
        Markdown.Parse(readme, s_pipeline).Descendants<LinkInline>()
            .Select(link => (link.Url ?? "").Split('#')[0])
            .Where(url => url.EndsWith(".md", StringComparison.Ordinal) && !url.Contains('/', StringComparison.Ordinal) && !url.Contains(':', StringComparison.Ordinal))
            .Distinct()
            .ToList();

    private GuidePage RenderPage(string file, List<string> errors)
    {
        var source = File.ReadAllText(Path.Combine(GuideDir, file)).ReplaceLineEndings("\n");
        var document = Markdown.Parse(source, s_pipeline);
        var slug = SlugOf(file);

        foreach (var link in document.Descendants<LinkInline>())
        {
            link.Url = RewriteLink(file, slug, link.Url ?? "", link.IsImage, errors);
        }

        var title = file == "README.md"
            ? "User guide"
            : document.Descendants<HeadingBlock>().FirstOrDefault(h => h.Level == 1) is { } h1
                ? InlineText(h1.Inline)
                : throw new SiteException($"{Folder}/{file} has no # title");
        var description = document.OfType<ParagraphBlock>().Select(p => Collapse(InlineText(p.Inline))).FirstOrDefault(t => t.Length > 0) ?? title;
        if (description.Length > 160)
        {
            description = description[..description.LastIndexOf(' ', 157)] + "…";
        }

        var outline = document.OfType<HeadingBlock>()
            .Where(h => h.Level == 2 && h.GetAttributes().Id is { Length: > 0 })
            .Select(h => new GuideHeading(InlineText(h.Inline), h.GetAttributes().Id!))
            .ToList();
        return new GuidePage(slug, $"{Folder}/{file}", title, description, document.ToHtml(s_pipeline), Sections(document, source), outline);
    }

    /// <summary>
    /// Every link on the site is relative to its <c>&lt;base href&gt;</c>, so a link within the page carries the page's
    /// address too (<c>docs/git/#stash</c>, not <c>#stash</c>).
    /// </summary>
    private string RewriteLink(string file, string slug, string url, bool isImage, List<string> errors)
    {
        if (url.StartsWith('#'))
        {
            return GuidePage.GuideUrl(slug) + url;
        }

        if (url.Length == 0 || UrlScheme().IsMatch(url))
        {
            return url;
        }

        var hash = url.IndexOf('#', StringComparison.Ordinal);
        var path = Uri.UnescapeDataString(hash < 0 ? url : url[..hash]);
        var fragment = hash < 0 ? "" : url[hash..];
        var full = Path.GetFullPath(Path.Combine(GuideDir, path));
        var relative = Path.GetRelativePath(options.RepoRoot, full).Replace('\\', '/');
        if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            errors.Add($"{Folder}/{file}: the link {url} leaves the repository");
            return url;
        }

        if (relative.StartsWith(Folder + "/", StringComparison.Ordinal) && !relative[(Folder.Length + 1)..].Contains('/', StringComparison.Ordinal)
            && full.EndsWith(".md", StringComparison.Ordinal) && File.Exists(full))
        {
            return GuidePage.GuideUrl(SlugOf(Path.GetFileName(full))) + fragment;
        }

        if (File.Exists(full))
        {
            return isImage ? $"https://raw.githubusercontent.com/{options.Repository}/{options.Ref}/{relative}" : options.Blob(relative) + fragment;
        }

        if (Directory.Exists(full))
        {
            return options.Tree(relative);
        }

        errors.Add($"{Folder}/{file}: the link {url} points at {relative}, which doesn't exist");
        return url;
    }

    /// <summary>The page split at its ## and ### headings, as plain text, for the search.</summary>
    private static List<SearchSection> Sections(MarkdownDocument document, string source)
    {
        var sections = new List<SearchSection>();
        var heading = "";
        var id = "";
        var start = 0;
        foreach (var block in document)
        {
            if (block is HeadingBlock { Level: 1 })
            {
                start = block.Span.End + 1;
            }
            else if (block is HeadingBlock { Level: 2 or 3 } next)
            {
                Add(block.Span.Start);
                heading = InlineText(next.Inline);
                id = next.GetAttributes().Id ?? "";
                start = block.Span.End + 1;
            }
        }

        Add(source.Length);
        return sections;

        void Add(int end)
        {
            var text = end > start ? Collapse(Markdown.ToPlainText(source[start..end], s_pipeline)) : "";
            if (text.Length > 0 || heading.Length > 0)
            {
                sections.Add(new SearchSection(heading, id, text));
            }
        }
    }

    private static string InlineText(ContainerInline? container)
    {
        var text = new StringBuilder();
        foreach (var inline in container?.Descendants<Inline>() ?? [])
        {
            switch (inline)
            {
                case LiteralInline literal:
                    text.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    text.Append(code.Content);
                    break;
                case LineBreakInline:
                    text.Append(' ');
                    break;
            }
        }

        return text.ToString().Trim();
    }

    private static string Collapse(string text) => Whitespace().Replace(text, " ").Trim();

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.-]*:")]
    private static partial Regex UrlScheme();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private sealed record Rendered(IReadOnlyList<GuidePage> Pages, IReadOnlyList<string> Errors);
}
