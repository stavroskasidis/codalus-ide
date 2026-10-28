using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

namespace Codalus.Debugging.Tests;

/// <summary>The DAP client's own diagnostics, without an adapter.</summary>
public sealed class DapConnectionTests
{
    private static byte[] Frame(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        return [.. Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), .. body];
    }

    /// <summary>
    /// Describe says what a session that stopped making progress waits for: the requests not answered and the last
    /// messages read, the program's output left out (linux-arm64's debug.depth waited 300 s without saying why).
    /// </summary>
    [Fact]
    public async Task Describe_lists_the_unanswered_requests_and_the_last_messages()
    {
        var toClient = new Pipe();
        var toAdapter = new Pipe();
        await using var connection = new DapConnection(toClient.Reader.AsStream(), toAdapter.Writer.AsStream());
        connection.Start();
        var ct = TestContext.Current.CancellationToken;
        Assert.StartsWith("0 request(s) unanswered; the connection is open; the last messages read: none", connection.Describe(), StringComparison.Ordinal);

        var threads = connection.RequestAsync("threads", new EmptyArguments(), DapJsonContext.Default.EmptyArguments, ct);
        var configured = connection.RequestAsync("configurationDone", new EmptyArguments(), DapJsonContext.Default.EmptyArguments, ct);
        await toClient.Writer.WriteAsync(Frame("""{"seq":1,"type":"event","event":"output","body":{"output":"hello\n"}}"""), ct);
        await toClient.Writer.WriteAsync(Frame("""{"seq":2,"type":"response","request_seq":1,"command":"threads","success":true,"body":{"threads":[]}}"""), ct);
        await toClient.Writer.WriteAsync(Frame("""{"seq":3,"type":"event","event":"stopped","body":{"reason":"breakpoint","threadId":1}}"""), ct);
        await threads;
        await WaitAsync(() => connection.Describe().Contains("event stopped (breakpoint)", StringComparison.Ordinal), ct);

        var described = connection.Describe();
        Assert.StartsWith("1 request(s) unanswered: configurationDone (", described, StringComparison.Ordinal);
        Assert.Contains("the last messages read: response threads (", described, StringComparison.Ordinal);
        Assert.DoesNotContain("output", described, StringComparison.Ordinal);
        Assert.False(configured.IsCompleted);

        toClient.Writer.Complete();
        await Assert.ThrowsAsync<DapConnectionLostException>(() => configured);
        await connection.Closed;
        Assert.Contains("0 request(s) unanswered; the connection is closed", connection.Describe(), StringComparison.Ordinal);
    }

    private static async Task WaitAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(10, cancellationToken);
        }
    }
}
