using System.Text;
using Codalus.Website.Components;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Codalus.Website.Site;

/// <summary>
/// The website as an ASP.NET Core app: Razor components rendered on the server (static SSR, no interactivity), the
/// files in <c>wwwroot</c>, and the docs search index, sitemap and robots.txt. <c>--export &lt;dir&gt;</c> starts it on
/// a loopback port, writes every page to static files for GitHub Pages (<see cref="SiteExporter"/>) and stops.
/// </summary>
internal static class SiteHost
{
    private static readonly Dictionary<string, string> s_switches = new(StringComparer.Ordinal)
    {
        ["--export"] = "Export",
        ["--stable"] = "Site:Stable",
        ["--nightly"] = "Site:Nightly",
        ["--ref"] = "Site:Ref",
        ["--base-url"] = "Site:BaseUrl",
        ["--repo"] = "Site:Repository",
    };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var config = new ConfigurationBuilder().AddEnvironmentVariables().AddCommandLine(args, s_switches).Build();
            var options = config.GetSection("Site").Get<SiteOptions>() ?? new SiteOptions();
            if (config["Export"] is not { Length: > 0 } exportDir)
            {
                await using var app = Create(options.Resolved(), args, quiet: false);
                await app.RunAsync();
                return 0;
            }

            options = (options with { BasePath = new Uri(options.BaseUrl).AbsolutePath }).Resolved();

            // dotnet run starts a web app in its project folder, so a relative path is taken from the repository root.
            var outDir = Path.GetFullPath(exportDir, options.RepoRoot);
            var written = await ExportAsync(options, outDir);
            Console.WriteLine($"Exported {written.Count} files to {outDir} (downloads: {options.Stable ?? options.Nightly ?? "the releases page"}).");
            return 0;
        }
        catch (SiteException e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
    }

    public static WebApplication Create(SiteOptions options, string[] args, bool quiet)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = Path.Combine(options.RepoRoot, "website"),
            WebRootPath = options.WebRoot,
        });
        if (quiet)
        {
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
        }

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<Releases>();
        builder.Services.AddSingleton<UserGuide>();
        builder.Services.AddRazorComponents();

        var app = builder.Build();
        app.UseStatusCodePagesWithReExecute("/404.html");
        app.UseStaticFiles();
        app.UseAntiforgery();
        MapData(app);
        app.MapRazorComponents<App>();
        return app;
    }

    /// <summary>Starts the site on a loopback port and exports it into <paramref name="outDir"/>.</summary>
    public static async Task<IReadOnlyList<string>> ExportAsync(SiteOptions options, string outDir)
    {
        await using var app = Create(options, [], quiet: true);
        app.Urls.Clear();
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            var exporter = new SiteExporter(app.Services.GetRequiredService<UserGuide>(), app.Services.GetRequiredService<EndpointDataSource>());
            return await exporter.ExportAsync(new Uri(address.TrimEnd('/') + "/"), outDir);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    /// <summary>The pages a sitemap lists, relative to the site's base.</summary>
    public static IEnumerable<string> PageUrls(UserGuide guide) => s_pages.Concat(guide.Pages.Select(p => p.Url));

    private static readonly string[] s_pages = ["", "features/", "download/"];

    private static void MapData(WebApplication app)
    {
        app.MapGet("/docs/search-index.json", (UserGuide guide) =>
            Results.Json(guide.Pages.Select(p => new { title = p.Title, url = p.Url, sections = p.Sections })));

        app.MapGet("/sitemap.xml", (UserGuide guide, SiteOptions site) =>
        {
            var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");
            foreach (var url in PageUrls(guide))
            {
                xml.Append("  <url><loc>").Append(site.BaseUrl).Append(url).Append("</loc></url>\n");
            }

            return Results.Text(xml.Append("</urlset>\n").ToString(), "application/xml", Encoding.UTF8);
        });

        app.MapGet("/robots.txt", (SiteOptions site) => Results.Text($"User-agent: *\nAllow: /\nSitemap: {site.BaseUrl}sitemap.xml\n"));
    }
}
