using System.Net;
using System.Net.Sockets;
using DaedalusStudio.Workbench.Build;

namespace DaedalusStudio.Workbench.Tests;

/// <summary>Console windows' command line (Windows' default for running and debugging) and the server probe that opens their browser.</summary>
public sealed class ConsoleWindowTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("a&b", "\"a&b\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("", "\"\"")]
    public void Arguments_are_quoted_for_cmd(string argument, string quoted) => Assert.Equal(quoted, ConsoleWindow.Quote(argument));

    [Fact]
    public void The_window_runs_the_program_then_waits_for_a_key()
    {
        var start = ConsoleWindow.StartInfo("Hello & co", @"C:\apps\Hello App\Hello.exe", ["--name", "two words"], @"C:\apps", [new("MODE", "test"), new("GONE", null)]);
        Assert.Equal("cmd.exe", start.FileName);
        Assert.Equal("/s /c \"title Hello  co & \"C:\\apps\\Hello App\\Hello.exe\" --name \"two words\" & echo. & pause\"", start.Arguments);
        Assert.False(start.CreateNoWindow);
        Assert.False(start.UseShellExecute);
        Assert.Equal("test", start.Environment["MODE"]);
        Assert.False(start.Environment.ContainsKey("GONE"));
    }

    [Fact]
    public async Task The_probe_answers_once_the_address_accepts_connections_and_gives_up_when_the_program_ends()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            Assert.True(await ServerProbe.WaitUntilListeningAsync(new Uri($"http://127.0.0.1:{port}/"), TimeSpan.FromSeconds(10), () => true, TestContext.Current.CancellationToken));
        }
        finally
        {
            listener.Stop();
        }

        // A program that ended: no answer, at once (the freed port may already be another test's on Linux).
        var probe = ServerProbe.WaitUntilListeningAsync(new Uri($"http://127.0.0.1:{port}/"), TimeSpan.FromSeconds(30), () => false, TestContext.Current.CancellationToken);
        Assert.False(await probe.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }
}
