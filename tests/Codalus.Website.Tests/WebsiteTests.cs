using System.Net;
using System.Reflection;
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

    public void Dispose() => Directory.Delete(_temp, recursive: true);

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
        return repo;
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
