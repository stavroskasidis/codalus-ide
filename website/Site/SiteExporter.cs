using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Codalus.Website.Site;

/// <summary>
/// Writes the running site to static files, the way GitHub Pages serves them: a page at <c>download/</c> becomes
/// <c>download/index.html</c>, other files keep their names. It starts from every route without parameters and every
/// guide page, then follows each local link and CSS <c>url()</c>, so assets come along and a link to something that
/// isn't there fails the export, as does an <c>#anchor</c> with no matching id. Links are relative to the pages'
/// <c>&lt;base href&gt;</c>, so a bare <c>#anchor</c> or a <c>/rooted</c> path is an error too.
/// </summary>
internal sealed partial class SiteExporter(UserGuide guide, EndpointDataSource endpoints)
{
    public async Task<IReadOnlyList<string>> ExportAsync(Uri server, string outDir)
    {
        foreach (var owned in new[] { "assets", "docs" })
        {
            var dir = Path.Combine(outDir, owned);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        Directory.CreateDirectory(outDir);
        var errors = new List<string>(guide.Errors);
        var referrers = new Dictionary<string, string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        void Enqueue(string path, string from)
        {
            if (referrers.TryAdd(path, from))
            {
                queue.Enqueue(path);
            }
        }

        foreach (var seed in Seeds())
        {
            Enqueue(seed, "(routes)");
        }

        var ids = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var anchors = new List<(string Page, string Target, string Fragment)>();
        var written = new List<string>();
        using var http = new HttpClient { BaseAddress = server };
        while (queue.TryDequeue(out var path))
        {
            using var response = await http.GetAsync(new Uri(path, UriKind.Relative));
            if (!response.IsSuccessStatusCode)
            {
                errors.Add($"{referrers[path]}: links to {(path.Length == 0 ? "./" : path)}, which answers {(int)response.StatusCode}");
                continue;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            var file = OutputFile(path);
            var target = Path.Combine(outDir, file);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, bytes);
            written.Add(file);

            switch (response.Content.Headers.ContentType?.MediaType)
            {
                case "text/html":
                    var html = await response.Content.ReadAsStringAsync();
                    ids[path] = IdPattern().Matches(html).Select(m => WebUtility.HtmlDecode(m.Groups["id"].Value)).ToHashSet(StringComparer.Ordinal);
                    foreach (Match match in LinkPattern().Matches(BaseTag().Replace(html, "")))
                    {
                        if (Resolve(WebUtility.HtmlDecode(match.Groups["url"].Value), path, errors) is { } link)
                        {
                            Enqueue(link.Path, path.Length == 0 ? "./" : path);
                            if (link.Fragment.Length > 0)
                            {
                                anchors.Add((path, link.Path, link.Fragment));
                            }
                        }
                    }

                    break;
                case "text/css":
                    foreach (Match match in CssUrlPattern().Matches(await response.Content.ReadAsStringAsync()))
                    {
                        var url = match.Groups["url"].Value;
                        if (!SchemePattern().IsMatch(url))
                        {
                            Enqueue(new Uri(new Uri("http://site/" + path), url).AbsolutePath.TrimStart('/'), path);
                        }
                    }

                    break;
            }
        }

        foreach (var (page, target, fragment) in anchors)
        {
            if (ids.TryGetValue(target, out var targetIds) && !targetIds.Contains(fragment))
            {
                errors.Add($"{(page.Length == 0 ? "./" : page)}: links to {target}#{fragment}, which has no such id");
            }
        }

        if (errors.Count > 0)
        {
            throw new SiteException("the website has errors:\n  " + string.Join("\n  ", errors.Distinct()));
        }

        await File.WriteAllTextAsync(Path.Combine(outDir, ".nojekyll"), "");
        return written;
    }

    /// <summary>Every route without parameters (pages and data), and the guide's pages.</summary>
    private IEnumerable<string> Seeds() =>
        endpoints.Endpoints.OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern)
            .Where(p => p.Parameters.Count == 0 && p.RawText is { } raw && !raw.TrimStart('/').StartsWith('_'))
            .Select(p => SitePath(p.RawText!.Trim('/')))
            .Concat(guide.Pages.Select(p => p.Url))
            .Distinct()
            .Order(StringComparer.Ordinal);

    /// <summary>A path on the site: pages end with <c>/</c> (<c>download/</c>), files have an extension.</summary>
    private static string SitePath(string path) =>
        path.Length == 0 || path.EndsWith('/') || Path.HasExtension(path) ? path : path + "/";

    private static string OutputFile(string path) =>
        path.Length == 0 || path.EndsWith('/') ? path + "index.html" : path;

    private static (string Path, string Fragment)? Resolve(string url, string page, List<string> errors)
    {
        if (url.Length == 0 || SchemePattern().IsMatch(url) || url.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        var where = page.Length == 0 ? "./" : page;
        if (url.StartsWith('#') || url.StartsWith('/'))
        {
            var instead = url.StartsWith('#') ? $"{(page.Length == 0 ? "./" : page)}{url}" : url.TrimStart('/');
            errors.Add($"{where}: the link {url} resolves against <base href>, not this page; write \"{instead}\"");
            return null;
        }

        var hash = url.IndexOf('#', StringComparison.Ordinal);
        var path = Uri.UnescapeDataString(hash < 0 ? url : url[..hash]);
        var fragment = hash < 0 ? "" : Uri.UnescapeDataString(url[(hash + 1)..]);
        path = path == "./" ? "" : path;
        if (path.Length > 0 && !path.EndsWith('/') && !Path.HasExtension(path))
        {
            errors.Add($"{where}: the page link {url} should end with / (Pages would redirect it)");
            return null;
        }

        return (path, fragment);
    }

    [GeneratedRegex(@"\s(?:href|src)=""(?<url>[^""]*)""")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"<base\s[^>]*>")]
    private static partial Regex BaseTag();

    [GeneratedRegex(@"\sid=""(?<id>[^""]*)""")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"url\(\s*['""]?(?<url>[^'"")]+)['""]?\s*\)")]
    private static partial Regex CssUrlPattern();

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.-]*:")]
    private static partial Regex SchemePattern();
}
