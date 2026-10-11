using System.Net;
using DaedalusStudio.Workbench.Node;

namespace DaedalusStudio.Workbench.Tests;

/// <summary>The on-demand Node.js runtime (ADR 0024): a download that doesn't match the pinned checksum is refused.</summary>
public sealed class NodeRuntimeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "daedalus-node-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class FakeHandler(byte[] content) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
        }
    }

    [Fact]
    public async Task A_download_with_the_wrong_checksum_is_refused_and_removed()
    {
        Assert.SkipWhen(NodeRuntime.Platform is null, "No Node.js build for this platform.");
        var handler = new FakeHandler("not node"u8.ToArray());
        using var http = new HttpClient(handler);
        using var node = new NodeRuntime(_folder, http);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => node.EnsureAsync(null, TestContext.Current.CancellationToken));

        Assert.Contains("checksum", error.Message, StringComparison.Ordinal);
        Assert.Equal($"https://nodejs.org/dist/v{NodeRuntime.Version}/node-v{NodeRuntime.Version}-{NodeRuntime.Platform}", Assert.Single(handler.Requests).ToString());
        Assert.False(node.IsInstalled);
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(_folder, "node")), f => !f.EndsWith(".lock", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_install_waits_while_another_holds_the_cache_lock()
    {
        // Found on the Mac: two test assemblies downloading Node.js into the same cache wrote the same partial file.
        var path = Path.Combine(_folder, "node", "test.lock");
        var first = await CacheLock.AcquireAsync(path, TestContext.Current.CancellationToken);
        var second = CacheLock.AcquireAsync(path, TestContext.Current.CancellationToken);
        await Task.Delay(600, TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);

        first.Dispose();
        using var held = await second.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Paths_follow_the_platform_layout()
    {
        using var node = new NodeRuntime(_folder);
        Assert.EndsWith(OperatingSystem.IsWindows() ? "node.exe" : Path.Combine("bin", "node"), node.NodePath, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("npm", "bin", "npm-cli.js"), node.NpmCli, StringComparison.Ordinal);
        Assert.StartsWith(Path.Combine(_folder, "node", NodeRuntime.Version), node.Folder, StringComparison.Ordinal);
    }
}
