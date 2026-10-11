using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

namespace DaedalusStudio.Debugging.Tests;

/// <summary>DAP's <c>runInTerminal</c> reverse request (M14c): answered through the terminal launcher, off the read loop.</summary>
public sealed class RunInTerminalTests
{
    private sealed class Launcher : ITerminalLauncher
    {
        public TerminalLaunch? Last { get; private set; }

        public Task<int?> LaunchAsync(TerminalLaunch launch, CancellationToken cancellationToken)
        {
            Last = launch;
            return launch.Program == "missing" ? throw new FileNotFoundException("not found: missing") : Task.FromResult<int?>(4242);
        }
    }

    private static byte[] Frame(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        return [.. Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), .. body];
    }

    private static async Task<JsonElement> ReadResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        var reader = new DaedalusStudio.Rpc.FramedMessageReader(stream);
        var payload = await reader.ReadMessageAsync(cancellationToken) ?? throw new InvalidOperationException("No response.");
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task The_request_starts_the_program_and_answers_with_its_process_id_or_the_failure()
    {
        var toClient = new Pipe();
        var toAdapter = new Pipe();
        var launcher = new Launcher();
        await using var connection = new DapConnection(toClient.Reader.AsStream(), toAdapter.Writer.AsStream());
        connection.Handle("runInTerminal", (arguments, ct) => DebugAdapter.RunInTerminalAsync(launcher, arguments, ct));
        connection.Start();
        var adapter = toAdapter.Reader.AsStream();
        var ct = TestContext.Current.CancellationToken;

        await toClient.Writer.WriteAsync(Frame("""
            {"seq":7,"type":"request","command":"runInTerminal","arguments":{"kind":"integrated","title":"App","cwd":"/work","args":["/usr/bin/dotnet","App.dll","x y"],"env":{"A":"1","B":null}}}
            """), ct);
        var response = await ReadResponseAsync(adapter, ct);
        Assert.Equal(7, response.GetProperty("request_seq").GetInt32());
        Assert.True(response.GetProperty("success").GetBoolean());
        Assert.Equal(4242, response.GetProperty("body").GetProperty("processId").GetInt32());
        Assert.Equal(("App", "/usr/bin/dotnet", "/work"), (launcher.Last!.Title, launcher.Last.Program, launcher.Last.WorkingDirectory));
        Assert.Equal(["App.dll", "x y"], launcher.Last.Arguments);
        Assert.Equal("1", launcher.Last.Environment["A"]);
        Assert.Null(launcher.Last.Environment["B"]);

        await toClient.Writer.WriteAsync(Frame("""
            {"seq":8,"type":"request","command":"runInTerminal","arguments":{"cwd":"/work","args":["missing"]}}
            """), ct);
        response = await ReadResponseAsync(adapter, ct);
        Assert.False(response.GetProperty("success").GetBoolean());
        Assert.Contains("not found: missing", response.GetProperty("message").GetString(), StringComparison.Ordinal);
    }
}
