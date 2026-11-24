using System.IO.Pipelines;
using System.Text.Json;
using Codalus.Debugging.Host.Mono;
using Codalus.Debugging.Host.NetFx;

namespace Codalus.Debugging.Tests;

/// <summary>
/// The Codalus debugger's launch that ran past one of its start limits answers with its code as DAP's raw message and
/// its text as <c>body.error.format</c>: the client shows the text and tells the timeout by the code, so DebugService
/// starts again (a review of 1.0 on macOS: the first start of a program just built failed, the second one worked).
/// </summary>
public sealed class ClrStartTimeoutTests
{
    [Fact]
    public async Task A_start_timeout_reaches_the_client_as_its_code_with_its_text()
    {
        var ct = TestContext.Current.CancellationToken;
        var toServer = new Pipe();
        var toClient = new Pipe();
        using var server = new DapServer(toServer.Reader.AsStream(), toClient.Writer.AsStream())
        {
            Handler = (command, _, _) => Task.FromException<JsonElement?>(command == "launch"
                ? new ClrStartTimeoutException("App's .NET runtime didn't start in 30 seconds: is it a .NET program?")
                : new ClrException("App couldn't be debugged.")),
        };
        using var stop = new CancellationTokenSource();
        _ = server.RunAsync(stop.Token);
        await using var client = new DapConnection(toClient.Reader.AsStream(), toServer.Writer.AsStream());
        client.Start();

        var timedOut = await Assert.ThrowsAsync<DapException>(() => client.RequestAsync("launch", new EmptyArguments(), DapJsonContext.Default.EmptyArguments, ct));
        Assert.Equal("App's .NET runtime didn't start in 30 seconds: is it a .NET program?", timedOut.Message);
        Assert.Equal(CoreClrAdapter.StartTimeoutCode, timedOut.Code);
        Assert.True(DebugAdapter.IsClrStartTimeout(timedOut));
        Assert.False(DebugAdapter.IsStartupTimeout(timedOut)); // netcoredbg's: it isn't waited for as one

        // Any other failure keeps its text as the message, without a code: not started again.
        var failed = await Assert.ThrowsAsync<DapException>(() => client.RequestAsync("attach", new EmptyArguments(), DapJsonContext.Default.EmptyArguments, ct));
        Assert.Equal("App couldn't be debugged.", failed.Message);
        Assert.Null(failed.Code);
        Assert.False(DebugAdapter.IsClrStartTimeout(failed));
        await stop.CancelAsync();
    }

    [Fact]
    public void The_error_body_carries_the_text_to_show()
    {
        var body = DapServer.ErrorBody("a \"quoted\" {text}");
        Assert.Equal("a \"quoted\" {text}", body.GetProperty("error").GetProperty("format").GetString());
        Assert.True(body.GetProperty("error").GetProperty("showUser").GetBoolean());
        Assert.Equal(JsonValueKind.Number, body.GetProperty("error").GetProperty("id").ValueKind);
    }
}
