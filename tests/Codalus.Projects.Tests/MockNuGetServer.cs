using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Codalus.Projects.Tests;

/// <summary>
/// A minimal NuGet V3 feed over HTTP (service index, search, registrations, README template) for what a local feed
/// can't express: deprecation, vulnerabilities, download counts. Serves one package, <c>Old.Package</c>. The SDK's NuGet
/// (7.x) ignores the search endpoint of a plain-HTTP feed even with <c>allowInsecureConnections</c>, so only
/// registrations and the README template are used from here; search stays covered by the local feed. The SDK's NuGet
/// (7.x) ignores the search endpoint of a plain-HTTP feed even with <c>allowInsecureConnections</c>, so only
/// registrations and the README template are used from here; search stays covered by the local feed.
/// </summary>
internal sealed class MockNuGetServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();

    public MockNuGetServer()
    {
        var port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _ = ServeAsync();
    }

    public string BaseUrl { get; }

    public string IndexUrl => BaseUrl + "v3/index.json";

    public int Requests { get; private set; }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            Requests++;
            var path = context.Request.Url!.AbsolutePath.ToLowerInvariant();
            var (status, body, type) = path switch
            {
                "/v3/index.json" => (200, Index(), "application/json"),
                "/search/query" => (200, Search(), "application/json"),
                "/registration/old.package/index.json" => (200, Registration(), "application/json"),
                "/flat/old.package/1.0.0/readme" => (200, "# Old.Package\n\nDeprecated; use **New.Package**.\n", "text/markdown"),
                "/flat/old.package/index.json" => (200, """{ "versions": [ "0.9.0", "1.0.0" ] }""", "application/json"),
                "/registration/framework.package/index.json" => (200, FrameworkRegistration(), "application/json"),
                _ => (404, string.Empty, "text/plain"),
            };
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = status;
            context.Response.ContentType = type;
            context.Response.ContentLength64 = bytes.Length;
            try
            {
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
            catch (HttpListenerException)
            {
            }
        }
    }

    private string Index() => $$"""
        {
          "version": "3.0.0",
          "resources": [
            { "@id": "{{BaseUrl}}search/query", "@type": "SearchQueryService" },
            { "@id": "{{BaseUrl}}search/query", "@type": "SearchQueryService/3.0.0-rc" },
            { "@id": "{{BaseUrl}}search/query", "@type": "SearchQueryService/3.5.0" },
            { "@id": "{{BaseUrl}}registration/", "@type": "RegistrationsBaseUrl" },
            { "@id": "{{BaseUrl}}registration/", "@type": "RegistrationsBaseUrl/3.6.0" },
            { "@id": "{{BaseUrl}}flat/", "@type": "PackageBaseAddress/3.0.0" },
            { "@id": "{{BaseUrl}}flat/{lower_id}/{lower_version}/readme", "@type": "ReadmeUriTemplate/6.13.0" }
          ]
        }
        """;

    private string Search() => $$"""
        {
          "totalHits": 1,
          "data": [
            {
              "@id": "{{BaseUrl}}registration/old.package/index.json",
              "registration": "{{BaseUrl}}registration/old.package/index.json",
              "id": "Old.Package",
              "version": "1.0.0",
              "description": "An old package.",
              "authors": [ "Someone" ],
              "totalDownloads": 1234,
              "versions": [ { "version": "0.9.0", "downloads": 34 }, { "version": "1.0.0", "downloads": 1200 } ],
              "vulnerabilities": [ { "advisoryUrl": "https://example.invalid/advisory/1", "severity": "2" } ],
              "deprecation": { "reasons": [ "Legacy" ], "message": "Use New.Package." }
            }
          ]
        }
        """;

    private string Registration() => $$"""
        {
          "count": 1,
          "items": [
            {
              "@id": "{{BaseUrl}}registration/old.package/index.json#page/0.9.0/1.0.0",
              "count": 2,
              "lower": "0.9.0",
              "upper": "1.0.0",
              "items": [
                {{Leaf("0.9.0", deprecated: false)}},
                {{Leaf("1.0.0", deprecated: true)}}
              ]
            }
          ]
        }
        """;

    private string Leaf(string version, bool deprecated) => $$"""
        {
          "@id": "{{BaseUrl}}registration/old.package/{{version}}.json",
          "catalogEntry": {
            "@id": "{{BaseUrl}}catalog/old.package.{{version}}.json",
            "id": "Old.Package",
            "version": "{{version}}",
            "description": "An old package.",
            "authors": "Someone",
            "listed": true,
            "licenseExpression": "MIT",
            "published": "2020-01-01T00:00:00+00:00",
            "dependencyGroups": [ { "targetFramework": "net8.0", "dependencies": [ { "id": "Some.Dependency", "range": "[1.0.0, )" } ] } ]
            {{(deprecated ? """, "deprecation": { "reasons": [ "Legacy", "CriticalBugs" ], "message": "Use New.Package.", "alternatePackage": { "id": "New.Package", "range": "[2.0.0, )" } }, "vulnerabilities": [ { "advisoryUrl": "https://example.invalid/advisory/1", "severity": "2" } ]""" : string.Empty)}}
          },
          "packageContent": "{{BaseUrl}}flat/old.package/{{version}}/old.package.{{version}}.nupkg"
        }
        """;

    /// <summary>
    /// <c>Framework.Package</c> (M33): each version for newer frameworks, as packages that drop old frameworks go. 1.0.0
    /// is for netstandard2.0, 2.0.0 for netstandard2.1, 3.0.0 for net6.0 and 4.0.0 for net8.0.
    /// </summary>
    private string FrameworkRegistration()
    {
        string Leaf(string version, string framework) => $$"""
            {
              "@id": "{{BaseUrl}}registration/framework.package/{{version}}.json",
              "catalogEntry": {
                "@id": "{{BaseUrl}}catalog/framework.package.{{version}}.json",
                "id": "Framework.Package",
                "version": "{{version}}",
                "description": "A package that moves to newer frameworks.",
                "authors": "Someone",
                "listed": true,
                "dependencyGroups": [ { "targetFramework": "{{framework}}" } ]
              },
              "packageContent": "{{BaseUrl}}flat/framework.package/{{version}}/framework.package.{{version}}.nupkg"
            }
            """;
        return $$"""
            {
              "count": 1,
              "items": [
                {
                  "@id": "{{BaseUrl}}registration/framework.package/index.json#page/1.0.0/4.0.0",
                  "count": 4,
                  "lower": "1.0.0",
                  "upper": "4.0.0",
                  "items": [ {{Leaf("1.0.0", ".NETStandard2.0")}}, {{Leaf("2.0.0", ".NETStandard2.1")}}, {{Leaf("3.0.0", "net6.0")}}, {{Leaf("4.0.0", "net8.0")}} ]
                }
              ]
            }
            """;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        _stop.Dispose();
    }
}
