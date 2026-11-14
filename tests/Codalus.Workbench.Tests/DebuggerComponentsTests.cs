using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Codalus.Tests;
using Codalus.Workbench.Debugging;

namespace Codalus.Workbench.Tests;

/// <summary>
/// M58b (ADR 0094): the Codalus debugger's bundles a package offers as components (for x64 programs on Apple silicon,
/// for 32-bit .NET Framework programs). The package's index names each release asset with its SHA-256; a bundle is
/// downloaded once, checked against it and unpacked into the cache. A mock server stands in for the release.
/// </summary>
public sealed class DebuggerComponentsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-debugger-components-" + Guid.NewGuid().ToString("N"));

    private static readonly JsonSerializerOptions s_json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DebuggerComponentsTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>A bundle's archive as eng/package.sh makes it: one top folder with the debugger in it.</summary>
    private static byte[] Archive(string executable)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            foreach (var (name, text) in new[] { ("codalus-debugger/" + executable, "the debugger"), ("codalus-debugger/bundle.id", "1.2.3-test") })
            {
                var data = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = data });
            }
        }

        return buffer.ToArray();
    }

    private string Index(params DebuggerComponent[] components)
    {
        var path = Path.Combine(_root, DebuggerComponents.IndexFile);
        File.WriteAllText(path, JsonSerializer.Serialize(components, s_json));
        return path;
    }

    [Theory]
    [InlineData("test-rid", "codalus-debughost")]
    [InlineData("win-test", "codalus-debughost.exe")]
    public async Task A_bundle_is_downloaded_once_checked_and_unpacked(string platform, string executable)
    {
        var archive = Archive(executable);
        var asset = $"codalus-debugger-1.2.3-{platform}.tar.gz";
        await using var release = new MockHttpServer(request => request.Path == "/releases/" + asset ? new MockHttpResponse(200, archive, "application/gzip") : MockHttpResponse.NotFound());
        using var http = new HttpClient();
        var index = Index(new DebuggerComponent(platform, asset, Convert.ToHexStringLower(SHA256.HashData(archive)), archive.Length, release.BaseUrl + "/releases/" + asset));
        using var components = new DebuggerComponents(Path.Combine(_root, "cache"), http, index);

        Assert.True(components.Offers(platform));
        Assert.Null(components.Installed(platform));
        var lines = new List<string>();
        var path = await components.EnsureAsync(platform, new Progress<string>(lines.Add), Ct);

        Assert.Equal(Path.Combine(_root, "cache", "codalus-debugger", $"codalus-debugger-1.2.3-{platform}", executable), path);
        Assert.Equal("the debugger", await File.ReadAllTextAsync(path, Ct));
        Assert.Equal(path, components.Installed(platform));
        Assert.Equal(Path.Combine("codalus-debugger", $"codalus-debugger-1.2.3-{platform}"), components.RelativeFolder(platform));

        // The second time it is there: the release isn't asked again.
        var requests = release.Requests.Count;
        Assert.Equal(path, await components.EnsureAsync(platform, null, Ct));
        Assert.Equal(requests, release.Requests.Count);
    }

    [Fact]
    public async Task A_bundle_that_isnt_the_one_the_package_named_is_refused()
    {
        var archive = Archive("codalus-debughost");
        await using var release = new MockHttpServer(_ => new MockHttpResponse(200, archive, "application/gzip"));
        using var http = new HttpClient();
        var index = Index(new DebuggerComponent("test-rid", "codalus-debugger-1.2.3-test-rid.tar.gz", new string('0', 64), archive.Length, release.BaseUrl + "/releases/codalus-debugger-1.2.3-test-rid.tar.gz"));
        using var components = new DebuggerComponents(Path.Combine(_root, "cache"), http, index);

        await Assert.ThrowsAsync<InvalidDataException>(() => components.EnsureAsync("test-rid", null, Ct));
        Assert.Null(components.Installed("test-rid"));
    }

    [Fact]
    public async Task An_installation_that_lists_no_bundle_for_a_platform_says_so()
    {
        using var none = new DebuggerComponents(Path.Combine(_root, "cache"), null, Path.Combine(_root, "missing.json"));
        Assert.Empty(none.Available);
        Assert.False(none.Offers("test-rid"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => none.EnsureAsync("test-rid", null, Ct));
        Assert.Contains("has no Codalus debugger bundle for test-rid", error.Message, StringComparison.Ordinal);

        // A damaged index is no bundles, not an exception.
        File.WriteAllText(Path.Combine(_root, "broken.json"), "{ not json");
        using var broken = new DebuggerComponents(Path.Combine(_root, "cache"), null, Path.Combine(_root, "broken.json"));
        Assert.Empty(broken.Available);
    }
}
