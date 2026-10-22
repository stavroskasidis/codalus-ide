using System.Text.Json;
using Codalus.Tests;

namespace Codalus.Projects.Tests;

/// <summary>
/// A NuGet V3 feed whose search says which packages are <c>verified</c> (D39: nuget.org's reserved ID prefix), for the
/// verified-owner tests. The same feed stands in for nuget.org (through <c>CODALUS_NUGET_ORG_INDEX</c>) or for another
/// feed that passes nuget.org's flag on (a mirror, an upstream). It answers search (with <c>packageid:</c>) and
/// registrations; tests never reach nuget.org. Every URL is under a path of its own: NuGet keeps service indexes and
/// registrations in its machine-wide HTTP cache, keyed by URL, and a loopback port comes back for later feeds.
/// </summary>
internal sealed class MockVerifiedFeed : IAsyncDisposable
{
    private static readonly string[] s_authors = ["Contoso"];
    private readonly List<(string Id, string Version, bool Verified)> _packages = [];
    private readonly MockHttpServer _server;
    private readonly string _prefix = Guid.NewGuid().ToString("N");

    public MockVerifiedFeed() => _server = new MockHttpServer(Handle);

    public string IndexUrl => Base + "v3/index.json";

    /// <summary>The search queries the feed was asked (<c>q</c>), in order.</summary>
    public IEnumerable<string> Searches => _server.Requests.Where(r => r.Path.TrimStart('/') == _prefix + "/search").Select(r => r.QueryValue("q") ?? string.Empty);

    private string Base => _server.BaseUrl.TrimEnd('/') + "/" + _prefix + "/";

    public MockVerifiedFeed Add(string id, string version, bool verified)
    {
        _packages.Add((id, version, verified));
        return this;
    }

    private MockHttpResponse Handle(MockHttpRequest request)
    {
        var path = request.Path.TrimStart('/');
        if (!path.StartsWith(_prefix + "/", StringComparison.Ordinal))
        {
            return MockHttpResponse.NotFound();
        }

        path = path[(_prefix.Length + 1)..];
        if (path == "v3/index.json")
        {
            return MockHttpResponse.Json($$"""
                { "version": "3.0.0", "resources": [
                  { "@id": "{{Base}}search", "@type": "SearchQueryService" },
                  { "@id": "{{Base}}search", "@type": "SearchQueryService/Versioned" },
                  { "@id": "{{Base}}search", "@type": "SearchQueryService/3.5.0" },
                  { "@id": "{{Base}}search", "@type": "SearchQueryService/3.4.0" },
                  { "@id": "{{Base}}search", "@type": "SearchQueryService/3.0.0-beta" },
                  { "@id": "{{Base}}registration/", "@type": "RegistrationsBaseUrl" },
                  { "@id": "{{Base}}registration/", "@type": "RegistrationsBaseUrl/3.6.0" }
                ] }
                """);
        }

        if (path == "search")
        {
            var query = request.QueryValue("q") ?? string.Empty;
            var exact = query.StartsWith("packageid:", StringComparison.OrdinalIgnoreCase) ? query["packageid:".Length..] : null;
            var data = _packages
                .Where(p => exact is null ? p.Id.Contains(query, StringComparison.OrdinalIgnoreCase) : p.Id.Equals(exact, StringComparison.OrdinalIgnoreCase))
                .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => new Dictionary<string, object>
                {
                    ["@id"] = $"{Base}registration/{g.Key.ToLowerInvariant()}/index.json",
                    ["registration"] = $"{Base}registration/{g.Key.ToLowerInvariant()}/index.json",
                    ["id"] = g.Key,
                    ["version"] = g.Last().Version,
                    ["description"] = "A package of Contoso's.",
                    ["authors"] = s_authors,
                    ["totalDownloads"] = 1200,
                    ["verified"] = g.Last().Verified,
                    ["versions"] = g.Select(p => new { version = p.Version, downloads = 600 }).ToArray(),
                })
                .ToList();
            return MockHttpResponse.Json(JsonSerializer.Serialize(new Dictionary<string, object> { ["totalHits"] = data.Count, ["data"] = data }));
        }

        if (path.StartsWith("registration/", StringComparison.Ordinal) && path.EndsWith("/index.json", StringComparison.Ordinal))
        {
            var id = path["registration/".Length..^"/index.json".Length];
            var versions = _packages.Where(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (versions.Count == 0)
            {
                return MockHttpResponse.NotFound();
            }

            var leaves = versions.Select(p => $$"""
                {
                  "@id": "{{Base}}registration/{{id}}/{{p.Version}}.json",
                  "catalogEntry": {
                    "@id": "{{Base}}catalog/{{id}}.{{p.Version}}.json",
                    "id": "{{p.Id}}",
                    "version": "{{p.Version}}",
                    "description": "A package of Contoso's.",
                    "authors": "Contoso",
                    "listed": true,
                    "published": "2026-10-01T00:00:00+00:00"
                  },
                  "packageContent": "{{Base}}flat/{{id}}/{{p.Version}}/{{id}}.{{p.Version}}.nupkg"
                }
                """);
            return MockHttpResponse.Json($$"""
                {
                  "count": 1,
                  "items": [
                    {
                      "@id": "{{Base}}registration/{{id}}/index.json#page/{{versions[0].Version}}/{{versions[^1].Version}}",
                      "count": {{versions.Count}},
                      "lower": "{{versions[0].Version}}",
                      "upper": "{{versions[^1].Version}}",
                      "items": [ {{string.Join(",", leaves)}} ]
                    }
                  ]
                }
                """);
        }

        return MockHttpResponse.NotFound();
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}
