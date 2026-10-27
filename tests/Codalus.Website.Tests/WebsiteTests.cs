using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Codalus.Website.Site;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Codalus.Website.Tests;

public sealed class WebsiteTests : IDisposable
{
    private static readonly string s_repoRoot = typeof(WebsiteTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "CodalusRepoRoot").Value!;

    private readonly string _temp = Directory.CreateTempSubdirectory("codalus-website-").FullName;

    public void Dispose()
    {
        // git's objects are read-only, which Windows won't delete.
        foreach (var file in Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_temp, recursive: true);
    }

    [Fact]
    public async Task The_site_exports_every_page_with_every_link_and_anchor_resolved()
    {
        var outDir = Path.Combine(_temp, "site");
        var files = await SiteHost.ExportAsync(Options(stable: "v0.1.0", nightly: "v0.2.0-nightly.20261020.1", basePath: "/codalus/"), outDir);

        foreach (var page in new[] { "index.html", "features/index.html", "download/index.html", "docs/index.html", "docs/getting-started/index.html", "404.html" })
        {
            Assert.Contains(page, files);
        }

        Assert.Equal(
            Directory.GetFiles(Path.Combine(s_repoRoot, "docs", "user-guide"), "*.md").Length,
            files.Count(f => f.StartsWith("docs/", StringComparison.Ordinal) && f.EndsWith("index.html", StringComparison.Ordinal)));
        Assert.Contains("docs/search-index.json", files);
        Assert.Contains("sitemap.xml", files);
        Assert.Contains("assets/site.css", files);
        Assert.Contains("assets/fonts/schibsted-grotesk.woff2", files);
        Assert.Contains("assets/fonts/OFL-schibsted-grotesk.txt", files);
        Assert.True(File.Exists(Path.Combine(outDir, ".nojekyll")));
        Assert.Contains("<base href=\"/codalus/\"", File.ReadAllText(Path.Combine(outDir, "docs", "git", "index.html")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_page_carries_the_products_name_and_never_the_old_one()
    {
        var outDir = Path.Combine(_temp, "site");
        var files = await SiteHost.ExportAsync(Options(), outDir);

        foreach (var page in files.Where(f => f.EndsWith(".html", StringComparison.Ordinal)))
        {
            var html = File.ReadAllText(Path.Combine(outDir, page));
            Assert.Contains("<span>Codalus <span class=\"brand-sub\">IDE</span></span>", html, StringComparison.Ordinal);
            Assert.DoesNotMatch(@"(?i)(Codalus|Daedalus)(\s|<[^>]*>)*Studio", html);
        }
    }

    [Fact]
    public async Task The_export_leaves_the_linux_repositories_alone()
    {
        var outDir = Path.Combine(_temp, "site");
        Directory.CreateDirectory(Path.Combine(outDir, "apt", "dists"));
        File.WriteAllText(Path.Combine(outDir, "codalus-packages.asc"), "key");

        await SiteHost.ExportAsync(Options(), outDir);

        Assert.True(Directory.Exists(Path.Combine(outDir, "apt", "dists")));
        Assert.Equal("key", File.ReadAllText(Path.Combine(outDir, "codalus-packages.asc")));
    }

    [Fact]
    public async Task The_download_page_offers_the_stable_release_and_mentions_a_nightly()
    {
        var html = await Get(Options(stable: "v0.1.0", nightly: "v0.2.0-nightly.20261020.1"), "download/");

        Assert.Contains("https://github.com/stavroskasidis/codalus-ide/releases/download/v0.1.0/codalus-0.1.0-win-x64-setup.exe", html, StringComparison.Ordinal);
        Assert.Contains("releases/download/v0.1.0/codalus-0.1.0-linux-arm64.AppImage", html, StringComparison.Ordinal);
        Assert.Contains("releases/tag/v0.2.0-nightly.20261020.1", html, StringComparison.Ordinal);
        Assert.DoesNotContain("No stable release yet", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_home_pages_download_buttons_carry_each_platforms_file()
    {
        var html = await Get(Options(stable: "v0.2.0"), "");

        // site.js points the buttons at the visitor's file; without script they lead to the Download page.
        Assert.Contains("data-windows=\"https://github.com/stavroskasidis/codalus-ide/releases/download/v0.2.0/codalus-0.2.0-win-x64-setup.exe\"", html, StringComparison.Ordinal);
        Assert.Contains("data-macos=\"https://github.com/stavroskasidis/codalus-ide/releases/download/v0.2.0/codalus-0.2.0-osx-arm64.pkg\"", html, StringComparison.Ordinal);
        Assert.Contains("data-linux=\"https://github.com/stavroskasidis/codalus-ide/releases/download/v0.2.0/codalus-0.2.0-linux-x64.AppImage\"", html, StringComparison.Ordinal);
        Assert.Contains("data-linux-arm64=\"https://github.com/stavroskasidis/codalus-ide/releases/download/v0.2.0/codalus-0.2.0-linux-arm64.AppImage\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"download/\" data-download-os", html, StringComparison.Ordinal);

        var none = await Get(Options(), "");
        Assert.DoesNotContain("data-windows=", none, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_stable_release_the_download_page_offers_the_nightly()
    {
        var html = await Get(Options(nightly: "v0.1.0-nightly.20261013.8"), "download/");

        Assert.Contains("/releases/download/v0.1.0-nightly.20261013.8/codalus-0.1.0-nightly.20261013.8-osx-arm64.pkg", html, StringComparison.Ordinal);
        Assert.Contains("No stable release yet", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_any_release_the_downloads_lead_to_the_releases_page()
    {
        var html = await Get(Options(), "download/");

        Assert.Contains("href=\"https://github.com/stavroskasidis/codalus-ide/releases\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/releases/download/", html, StringComparison.Ordinal);
        Assert.Contains("codalus-&lt;version&gt;-win-x64-setup.exe", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Guide_links_stay_on_the_site_and_other_files_go_to_github()
    {
        var html = await Get(Options() with { Ref = "develop" }, "docs/");

        Assert.Contains("href=\"docs/getting-started/\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://github.com/stavroskasidis/codalus-ide/blob/develop/docs/plugins.md\"", html, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"href=""(?!https?:)[^""]*\.md(#[^""]*)?""", html);
    }

    [Fact]
    public async Task An_unknown_guide_page_is_not_found()
    {
        using var site = await RunningSite.StartAsync(Options());

        using var response = await site.Http.GetAsync(new Uri("docs/no-such-page/", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("This page isn't here.", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_broken_link_in_the_guide_fails_the_export()
    {
        var options = Options() with { RepoRoot = FakeRepository("See [the missing page](missing.md).") };

        var error = await Assert.ThrowsAsync<SiteException>(() => SiteHost.ExportAsync(options, Path.Combine(_temp, "site")));

        Assert.Contains("missing.md", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_link_to_a_heading_that_does_not_exist_fails_the_export()
    {
        var options = Options() with { RepoRoot = FakeRepository("See [a part](getting-started.md#no-such-heading).") };

        var error = await Assert.ThrowsAsync<SiteException>(() => SiteHost.ExportAsync(options, Path.Combine(_temp, "site")));

        Assert.Contains("docs/getting-started/#no-such-heading", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Headings_get_github_style_ids_and_links_within_a_page_carry_its_address()
    {
        var options = Options() with
        {
            RepoRoot = FakeRepository("## Debugging the server and the page together\n\nText.\n\nSee [it](#debugging-the-server-and-the-page-together)."),
        };

        await SiteHost.ExportAsync(options, Path.Combine(_temp, "site"));

        var html = File.ReadAllText(Path.Combine(_temp, "site", "docs", "index.html"));
        Assert.Contains("id=\"debugging-the-server-and-the-page-together\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"docs/#debugging-the-server-and-the-page-together\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_home_and_download_pages_describe_the_product_to_search_engines()
    {
        var options = Options(stable: "v0.6.0");
        foreach (var path in new[] { "", "download/" })
        {
            var html = await Get(options, path);
            var json = Regex.Match(html, "<script type=\"application/ld\\+json\">(?<json>.*?)</script>", RegexOptions.Singleline);
            Assert.True(json.Success, $"no JSON-LD on /{path}");

            var app = JsonElement.Parse(json.Groups["json"].Value);
            Assert.Equal("SoftwareApplication", app.GetProperty("@type").GetString());
            Assert.Equal("Codalus IDE", app.GetProperty("name").GetString());
            Assert.Equal("0.6.0", app.GetProperty("softwareVersion").GetString());
            Assert.Equal("0", app.GetProperty("offers").GetProperty("price").GetString());
            Assert.Equal("https://github.com/stavroskasidis/codalus-ide", app.GetProperty("sameAs")[0].GetString());
        }

        Assert.DoesNotContain("application/ld+json", await Get(options, "features/"), StringComparison.Ordinal);
        Assert.DoesNotContain("softwareVersion", await Get(Options(), ""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_page_has_its_own_preview_image_and_the_docs_their_breadcrumbs()
    {
        var options = Options();
        var images = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in new[] { "", "features/", "download/", "docs/", "docs/debugging/", "docs/data/" })
        {
            var html = await Get(options, path);
            var og = Regex.Match(html, "<meta property=\"og:image\" content=\"https://codalus.dev/(?<file>assets/screenshots/[^\"]+)\"");
            Assert.True(og.Success, $"no og:image on /{path}");
            Assert.True(File.Exists(Path.Combine(s_repoRoot, "website", "wwwroot", og.Groups["file"].Value)), $"/{path}'s preview {og.Groups["file"].Value} doesn't exist");
            Assert.Contains($"<meta name=\"twitter:image\" content=\"https://codalus.dev/{og.Groups["file"].Value}\"", html, StringComparison.Ordinal);
            Assert.Single(Regex.Matches(html, "property=\"og:image\""));
            images.Add(og.Groups["file"].Value);
        }

        Assert.True(images.Count >= 4, string.Join(", ", images)); // not one image for every page

        var debugging = await Get(options, "docs/debugging/");
        var crumbs = Regex.Matches(debugging, "<script type=\"application/ld\\+json\">(?<json>.*?)</script>", RegexOptions.Singleline)
            .Select(m => JsonElement.Parse(m.Groups["json"].Value)).Single(j => j.GetProperty("@type").GetString() == "BreadcrumbList");
        Assert.Equal(
            ["https://codalus.dev/", "https://codalus.dev/docs/", "https://codalus.dev/docs/debugging/"],
            crumbs.GetProperty("itemListElement").EnumerateArray().Select(i => i.GetProperty("item").GetString()));
    }

    [Fact]
    public async Task The_home_page_shows_each_app_model_with_its_picture()
    {
        var html = await Get(Options(), "");

        var tabs = Regex.Matches(html, "role=\"tab\" id=\"tab-(?<key>[a-z]+)\" aria-controls=\"model-\\k<key>\"").Select(m => m.Groups["key"].Value).ToList();
        Assert.Equal(["console", "blazor", "aspnet", "maui", "wpf", "winforms", "winui", "avalonia", "aspire", "wcf", "netfx"], tabs);
        foreach (var key in tabs)
        {
            var panel = Regex.Match(html, $"<div class=\"shot model\" role=\"tabpanel\" id=\"model-{key}\"[^>]*>\\s*<figure><img src=\"(?<file>assets/screenshots/[^\"]+)\"");
            Assert.True(panel.Success, $"no panel with a picture for {key}");
            Assert.True(File.Exists(Path.Combine(s_repoRoot, "website", "wwwroot", panel.Groups["file"].Value)), $"{key}'s {panel.Groups["file"].Value} doesn't exist");
        }

        // The first panel shows as the page loads, the others hidden until site.js selects one.
        Assert.Single(Regex.Matches(html, "role=\"tabpanel\" id=\"model-[a-z]+\" aria-labelledby=\"tab-[a-z]+\">"));

        // The main screenshot sits above them, the one picture the page loads at once.
        var main = html.IndexOf("<img src=\"assets/screenshots/editor-dark.png\"", StringComparison.Ordinal);
        Assert.InRange(main, 0, html.IndexOf("id=\"app-models\"", StringComparison.Ordinal));
        Assert.DoesNotContain("loading=\"eager\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_screenshot_on_the_site_is_a_picture_the_smoke_test_saves()
    {
        // website/screenshots.txt: <picture on the site> <runner> <the smoke test's picture> (eng/update-screenshots.sh).
        var entries = File.ReadLines(Path.Combine(s_repoRoot, "website", "screenshots.txt"))
            .Where(line => line.Trim().Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToList();
        Assert.All(entries, e => Assert.True(e.Length == 3 && e[1] is "Linux" or "Windows" or "macOS", string.Join(' ', e)));

        var onSite = Directory.GetFiles(Path.Combine(s_repoRoot, "website", "wwwroot", "assets", "screenshots"))
            .Select(Path.GetFileName).Order(StringComparer.Ordinal);
        Assert.Equal(onSite, entries.Select(e => e[0]).Order(StringComparer.Ordinal));

        // The names in SmokeTest's Screenshot calls, an interpolation's holes matching anything; not a failed step's
        // picture (smoke-test-{id}.png), which would match every name.
        var source = string.Concat(Directory.GetFiles(Path.Combine(s_repoRoot, "src", "Codalus.Shell", "Infrastructure"), "SmokeTest*.cs").Select(File.ReadAllText));
        var saved = Regex.Matches(source, @"Screenshot\(\$?""(?<name>[^""]+\.png)""")
            .Select(m => m.Groups["name"].Value)
            .Where(name => name != "smoke-test-{id}.png")
            .Select(name => new Regex("^" + Regex.Replace(Regex.Escape(name), @"\\\{[^}]*\}", ".+") + "$"))
            .ToList();
        Assert.True(saved.Count > 50, $"only {saved.Count} Screenshot calls found");
        Assert.All(entries, e => Assert.True(saved.Any(r => r.IsMatch(e[2])), $"{e[0]}: the smoke test saves no {e[2]}"));
    }

    [Fact]
    public async Task The_sitemap_dates_each_page_by_its_sources_last_commit_with_the_whole_history_only()
    {
        var repo = FakeRepository("The guide.");
        var noGit = await Get(Options() with { RepoRoot = repo }, "sitemap.xml");
        Assert.Contains("<loc>https://codalus.dev/docs/</loc>", noGit, StringComparison.Ordinal);
        Assert.DoesNotContain("<lastmod>", noGit, StringComparison.Ordinal);

        Git(repo, null, "init", "-q");
        Git(repo, "2026-10-01T12:00:00Z", "add", "docs/user-guide/README.md");
        Git(repo, "2026-10-01T12:00:00Z", "commit", "-q", "-m", "Guide");
        Git(repo, "2026-10-05T12:00:00Z", "add", ".");
        Git(repo, "2026-10-05T12:00:00Z", "commit", "-q", "-m", "Getting started");
        Git(repo, "2026-10-09T12:00:00Z", "commit", "-q", "--allow-empty", "-m", "Elsewhere");

        var sitemap = await Get(Options() with { RepoRoot = repo }, "sitemap.xml");
        Assert.Contains("<loc>https://codalus.dev/docs/</loc><lastmod>2026-10-01</lastmod>", sitemap, StringComparison.Ordinal);
        Assert.Contains("<loc>https://codalus.dev/docs/getting-started/</loc><lastmod>2026-10-05</lastmod>", sitemap, StringComparison.Ordinal);
        Assert.Contains("<loc>https://codalus.dev/</loc></url>", sitemap, StringComparison.Ordinal);

        // A shallow clone would date every page to its newest commit.
        var shallow = Path.Combine(_temp, "shallow");
        Git(_temp, null, "clone", "-q", "--depth", "1", new Uri(repo).AbsoluteUri, shallow);
        Assert.DoesNotContain("<lastmod>", await Get(Options() with { RepoRoot = shallow }, "sitemap.xml"), StringComparison.Ordinal);
    }

    private static SiteOptions Options(string? stable = null, string? nightly = null, string basePath = "/") =>
        new SiteOptions { RepoRoot = s_repoRoot, Stable = stable, Nightly = nightly, BasePath = basePath }.Resolved();

    private static async Task<string> Get(SiteOptions options, string path)
    {
        using var site = await RunningSite.StartAsync(options);
        return await site.Http.GetStringAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
    }

    /// <summary>A repository with the real website files and a two-page guide.</summary>
    private string FakeRepository(string readmeBody)
    {
        var repo = Path.Combine(_temp, "repo");
        var wwwroot = Path.Combine(s_repoRoot, "website", "wwwroot");
        foreach (var file in Directory.GetFiles(wwwroot, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(repo, "website", "wwwroot", Path.GetRelativePath(wwwroot, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        var guide = Path.Combine(repo, "docs", "user-guide");
        Directory.CreateDirectory(guide);
        File.WriteAllText(Path.Combine(guide, "README.md"), "# User guide\n\nThe guide.\n\n" + readmeBody + "\n");
        File.WriteAllText(Path.Combine(guide, "getting-started.md"), "# Installing and first start\n\n## Prerequisites\n\n## First start\n");
        // The guide's pages and headings the Home page's app models link to.
        foreach (var (page, body) in new[]
        {
            ("editing.md", "# Editing and navigating code\n"),
            ("web.md", "# Web development\n\n## Razor and Blazor\n"),
            ("maui.md", "# .NET MAUI\n"),
            ("windows-desktop.md", "# WPF, Windows Forms and WinUI 3\n\n## The WPF XAML Previewer\n\n## The Windows Forms designer\n\n## WinUI 3\n"),
            ("avalonia.md", "# Avalonia\n"),
            ("containers.md", "# Containers, Aspire and publishing\n\n## .NET Aspire\n"),
            ("wcf.md", "# WCF\n"),
            ("net-framework.md", "# .NET Framework\n"),
        })
        {
            File.WriteAllText(Path.Combine(guide, page), body);
        }

        return repo;
    }

    private static void Git(string dir, string? date, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in (string[])["-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", .. args])
        {
            start.ArgumentList.Add(arg);
        }

        if (date is not null)
        {
            start.Environment["GIT_AUTHOR_DATE"] = date;
            start.Environment["GIT_COMMITTER_DATE"] = date;
        }

        using var git = Process.Start(start)!;
        var stderr = git.StandardError.ReadToEndAsync();
        git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        Assert.True(git.ExitCode == 0, $"git {string.Join(' ', args)}: {stderr.Result}");
    }

    /// <summary>The site served on a loopback port.</summary>
    private sealed class RunningSite : IDisposable
    {
        private readonly WebApplication _app;

        private RunningSite(WebApplication app, HttpClient http)
        {
            _app = app;
            Http = http;
        }

        public HttpClient Http { get; }

        public static async Task<RunningSite> StartAsync(SiteOptions options)
        {
            var app = SiteHost.Create(options, [], quiet: true);
            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new RunningSite(app, new HttpClient { BaseAddress = new Uri(address.TrimEnd('/') + "/") });
        }

        public void Dispose()
        {
            Http.Dispose();
            _app.StopAsync().GetAwaiter().GetResult();
            ((IDisposable)_app).Dispose();
        }
    }
}
