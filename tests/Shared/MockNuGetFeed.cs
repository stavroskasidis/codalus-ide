using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Codalus.Tests;

/// <summary>
/// A NuGet v3 feed for tests (M22c): the service index, <c>SearchQueryService</c> (filtered by package type) and
/// <c>PackageBaseAddress</c> (versions and downloads), serving plugin packages built in memory. Tests never reach
/// nuget.org.
/// </summary>
internal sealed class MockNuGetFeed : IAsyncDisposable
{
    private readonly List<(string Id, string Version, string PackageType, byte[] Package, string? Description)> _packages = [];
    private static readonly string[] s_authors = ["Contoso"];
    private readonly MockHttpServer _server;

    public MockNuGetFeed() => _server = new MockHttpServer(Handle);

    /// <summary>The feed's service index.</summary>
    public string Source => Base + "v3/index.json";

    public MockHttpServer Server => _server;

    private string Base => _server.BaseUrl.TrimEnd('/') + "/";

    /// <summary>
    /// Adds a plugin package: <paramref name="files"/> (paths relative to the plugin folder) go under <c>plugin/</c>,
    /// with a manifest of <paramref name="id"/> and <paramref name="version"/> unless the files have one.
    /// </summary>
    public void AddPlugin(string id, string version, IReadOnlyDictionary<string, string>? files = null, string packageType = "CodalusPlugin", string? description = null, string? manifestJson = null)
    {
        var content = new Dictionary<string, string>(files ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        content.TryAdd("codalus-plugin.json", manifestJson ?? $$"""
            {
              "id": "{{id}}",
              "version": "{{version}}",
              "displayName": "{{id}}",
              "publisher": "{{id.Split('.')[0]}}",
              "engines": { "codalus": "^1.0.0" },
              "contributes": { "settings": { "title": "{{id}}", "properties": { "{{id}}.on": { "type": "boolean", "default": true } } } }
            }
            """);
        _packages.Add((id, version, packageType, Build(id, version, packageType, content), description));
    }

    private static byte[] Build(string id, string version, string packageType, IReadOnlyDictionary<string, string> files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }

            Add(id + ".nuspec", $"""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata>
                    <id>{id}</id>
                    <version>{version}</version>
                    <authors>Contoso</authors>
                    <description>A test plugin.</description>
                    <packageTypes><packageType name="{packageType}" /></packageTypes>
                  </metadata>
                </package>
                """);
            foreach (var (path, text) in files)
            {
                Add("plugin/" + path, text);
            }
        }

        return stream.ToArray();
    }

    private MockHttpResponse Handle(MockHttpRequest request)
    {
        var path = request.Path.TrimStart('/');
        if (path == "v3/index.json")
        {
            return MockHttpResponse.Json($$"""
                { "version": "3.0.0", "resources": [
                  { "@id": "{{Base}}search", "@type": "SearchQueryService/3.5.0" },
                  { "@id": "{{Base}}flat/", "@type": "PackageBaseAddress/3.0.0" }
                ] }
                """);
        }

        if (path == "search")
        {
            var query = request.QueryValue("q") ?? string.Empty;
            var type = request.QueryValue("packageType");
            var matches = _packages.Where(p => (type is null || p.PackageType == type) && p.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
                .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    id = g.Key,
                    version = g.Last().Version,
                    description = g.Last().Description,
                    authors = s_authors,
                    totalDownloads = 42,
                    versions = g.Select(p => new { version = p.Version }).ToArray(),
                });
            return MockHttpResponse.Json(JsonSerializer.Serialize(new { totalHits = matches.Count(), data = matches }));
        }

        if (path.StartsWith("flat/", StringComparison.Ordinal))
        {
            var parts = path["flat/".Length..].Split('/');
            var versions = _packages.Where(p => p.Id.Equals(parts[0], StringComparison.OrdinalIgnoreCase)).ToList();
            if (parts is [_, "index.json"])
            {
                return versions.Count == 0 ? MockHttpResponse.NotFound() : MockHttpResponse.Json(JsonSerializer.Serialize(new { versions = versions.Select(v => v.Version.ToLowerInvariant()) }));
            }

            if (parts is [_, var version, _] && versions.FirstOrDefault(v => v.Version.Equals(version, StringComparison.OrdinalIgnoreCase)) is { Package: { } package })
            {
                return new MockHttpResponse(200, package, "application/octet-stream");
            }
        }

        return MockHttpResponse.NotFound();
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}
