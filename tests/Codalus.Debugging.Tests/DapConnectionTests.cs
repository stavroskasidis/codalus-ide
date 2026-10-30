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

    /// <summary>
    /// Closed comes after the events read before the end: a two-program session ended on Closed while each program's
    /// exited and terminated events were still on their way, and lost both exit lines (Linux arm64 CI, run 40911316698).
    /// </summary>
    [Fact]
    public async Task Closed_completes_after_the_events_read_before_the_end_were_raised()
    {
        var toClient = new Pipe();
        var toAdapter = new Pipe();
        await using var connection = new DapConnection(toClient.Reader.AsStream(), toAdapter.Writer.AsStream());
        var raised = new List<string>();
        connection.EventReceived += async e =>
        {
            await Task.Delay(300); // a handler slower than the end of the adapter's output
            lock (raised)
            {
                raised.Add(e.Name);
            }
        };
        connection.Start();
        var ct = TestContext.Current.CancellationToken;

        await toClient.Writer.WriteAsync(Frame("""{"seq":1,"type":"event","event":"exited","body":{"exitCode":15}}"""), ct);
        await toClient.Writer.WriteAsync(Frame("""{"seq":2,"type":"event","event":"terminated"}"""), ct);
        await toClient.Writer.CompleteAsync();
        await connection.Closed.WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.True(connection.OutputEnded);
        lock (raised)
        {
            Assert.Equal([DapEvents.Exited, DapEvents.Terminated], raised);
        }
    }

    /// <summary>
    /// A request made once the adapter's output ended fails at once: its answer can't be read, and an event handler
    /// waiting for it held Closed up (to its 10 s limit) while the last events were raised.
    /// </summary>
    [Fact]
    public async Task A_request_made_after_the_output_ended_fails_at_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var toClient = new Pipe();
        var toAdapter = new Pipe();
        await using var connection = new DapConnection(toClient.Reader.AsStream(), toAdapter.Writer.AsStream());
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.EventReceived += async _ => await held.Task; // the last events still being raised
        connection.Start();

        await toClient.Writer.WriteAsync(Frame("""{"seq":1,"type":"event","event":"exited","body":{"exitCode":15}}"""), ct);
        await toClient.Writer.CompleteAsync();
        await WaitAsync(() => connection.OutputEnded, ct);

        var request = connection.RequestAsync("threads", new EmptyArguments(), DapJsonContext.Default.EmptyArguments, ct);
        await Assert.ThrowsAsync<DapConnectionLostException>(() => request.WaitAsync(TimeSpan.FromSeconds(5), ct));
        Assert.False(connection.Closed.IsCompleted); // the event is still being raised
        held.SetResult();
        await connection.Closed.WaitAsync(TimeSpan.FromSeconds(10), ct);
    }

    private static async Task WaitAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(10, cancellationToken);
        }
    }
}
