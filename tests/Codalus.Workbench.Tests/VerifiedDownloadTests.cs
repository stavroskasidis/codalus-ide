using System.Net;
using System.Security.Cryptography;
using Codalus.Workbench.Node;

namespace Codalus.Workbench.Tests;

/// <summary>
/// The downloads of Node.js and vscode-js-debug: a body that stops coming is asked for again from where it stopped,
/// instead of holding the install (and everything waiting for Node.js) for ever (macOS CI, run 40632085595).
/// </summary>
public sealed class VerifiedDownloadTests : IDisposable
{
    private static readonly TimeSpan s_stall = TimeSpan.FromMilliseconds(200);
    private static readonly byte[] s_content = [.. Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7))];

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "codalus-download-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Target => Path.Combine(_folder, "node.partial");

    private static string Sha256 => Convert.ToHexStringLower(SHA256.HashData(s_content));

    /// <summary>Answers each request with the next of <paramref name="answers"/> (given the request's range start), and records the ranges asked for.</summary>
    private sealed class Server(params Func<long?, HttpResponseMessage>[] answers) : HttpMessageHandler
    {
        public List<long?> Ranges { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var from = request.Headers.Range?.Ranges.Single().From;
            Ranges.Add(from);
            return Task.FromResult(answers[Math.Min(Ranges.Count, answers.Length) - 1](from));
        }
    }

    /// <summary>A body that sends <paramref name="bytes"/> and then nothing more, until the reader gives up.</summary>
    private sealed class StallingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return read;
        }
    }

    private static HttpResponseMessage Stalls(int after) =>
        new(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(s_content[..after])) };

    private static HttpResponseMessage Rest(long? from) =>
        new(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(s_content[(int)from!.Value..]) };

    private static HttpResponseMessage Whole(long? from) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(s_content) };

    private Task FetchAsync(Server server, IProgress<string>? progress = null)
    {
        var http = new HttpClient(server);
        return VerifiedDownload.FetchAsync(http, new Uri("https://nodejs.org/dist/node.tar.gz"), Target, Sha256, "Node.js", progress, TestContext.Current.CancellationToken, s_stall);
    }

    [Fact]
    public async Task A_body_that_stalls_is_asked_for_again_from_where_it_stopped()
    {
        var server = new Server(_ => Stalls(100_000), Rest);
        var messages = new List<string>();

        await FetchAsync(server, new SynchronousProgress(messages.Add));

        Assert.Equal([null, 100_000L], server.Ranges);
        Assert.Equal(s_content, await File.ReadAllBytesAsync(Target, TestContext.Current.CancellationToken));
        Assert.Contains(messages, m => m.Contains("stopped at 0.1 MB (no data for 0.2 s); asking again (2 of 4)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_server_that_sends_the_whole_file_again_starts_it_over()
    {
        var server = new Server(_ => Stalls(100_000), Whole);

        await FetchAsync(server);

        Assert.Equal([null, 100_000L], server.Ranges);
        Assert.Equal(s_content, await File.ReadAllBytesAsync(Target, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_download_that_stalls_every_time_fails_and_says_so()
    {
        var server = new Server(from => from is null ? Stalls(100_000) : new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StreamContent(new StallingStream([])) });

        var error = await Assert.ThrowsAsync<IOException>(() => FetchAsync(server));

        Assert.Equal(VerifiedDownload.Attempts, server.Ranges.Count);
        Assert.Contains("failed after 4 attempts, at 0.1 MB: no data for 0.2 s", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_file_is_not_asked_for_again()
    {
        var server = new Server(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<HttpRequestException>(() => FetchAsync(server));

        Assert.Single(server.Ranges);
    }

    private sealed class SynchronousProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
