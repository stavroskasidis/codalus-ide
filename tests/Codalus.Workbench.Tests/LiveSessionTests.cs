using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Codalus.Workbench.Previewer;

namespace Codalus.Workbench.Tests;

/// <summary>The IDE's side of the live agent (M19b, ADR 0052) against a stand-in agent on a real loopback socket.</summary>
public sealed class LiveSessionTests
{
    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    [Fact]
    public async Task The_session_hands_its_app_the_agent_and_reads_replies_errors_and_events()
    {
        await using var session = LiveSession.Start(typeof(LiveSessionTests).Assembly.Location)!;
        var environment = session.Environment(new Dictionary<string, string> { ["DOTNET_STARTUP_HOOKS"] = "other.dll" });
        Assert.Equal("other.dll" + Path.PathSeparator + typeof(LiveSessionTests).Assembly.Location, environment["DOTNET_STARTUP_HOOKS"]);
        var port = int.Parse(environment[LiveSession.PortVariable], CultureInfo.InvariantCulture);
        var token = environment[LiveSession.TokenVariable];

        // A stranger is refused; the agent with the token is the app.
        using (var stranger = new TcpClient())
        {
            await stranger.ConnectAsync("127.0.0.1", port, Timeout());
            await stranger.GetStream().WriteAsync("hello\twrong\n"u8.ToArray(), Timeout());
        }

        var connected = new TaskCompletionSource();
        session.Connected += (_, _) => connected.TrySetResult();
        using var agent = new TcpClient();
        await agent.ConnectAsync("127.0.0.1", port, Timeout());
        var stream = agent.GetStream();
        var reader = new StreamReader(stream, new UTF8Encoding(false));
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
        await writer.WriteLineAsync(("hello\t" + token).AsMemory(), Timeout());
        await connected.Task.WaitAsync(Timeout());
        Assert.True(session.IsConnected);

        // The stand-in answers each request as the agent would.
        var answering = Task.Run(async () =>
        {
            while (await reader.ReadLineAsync(Timeout()) is { } line)
            {
                var parts = line.Split('\t');
                var reply = parts[1] switch
                {
                    "windows" => "ok\t1\u001fMain\u001fApp.MainWindow",
                    "tree" => "ok\t1\u001f0\u001fMainWindow\u001f\u001f0\u001e2\u001f1\u001fButton\u001fGo\u001f0",
                    "props" => "ok\tContent\u001fHi\u001fObject\u001fLocal\u001f0\u001eIsPointerOver\u001fFalse\u001fBoolean\u001fDefault\u001f1",
                    "path" => "ok\tApp.MainWindow\tMainWindow:0:/StackPanel:0:/Button:0:Go",
                    "set" when parts[3] == "IsPointerOver" => "error\tIsPointerOver is read-only.",
                    _ => "ok\tok",
                };
                await writer.WriteLineAsync((parts[0] + "\t" + reply).AsMemory(), Timeout());
                if (parts[1] == "pick")
                {
                    await writer.WriteLineAsync("event\tpicked\t7\t2".AsMemory(), Timeout());
                }
            }
        }, TestContext.Current.CancellationToken);

        Assert.Equal(new LiveWindow(1, "Main", "App.MainWindow"), Assert.Single(await session.WindowsAsync(Timeout())));
        var tree = await session.TreeAsync(1, visual: false, Timeout());
        Assert.Equal([new LiveNode(1, 0, "MainWindow", "", false), new LiveNode(2, 1, "Button", "Go", false)], tree);
        var properties = await session.PropertiesAsync(2, Timeout());
        Assert.Equal(new LiveProperty("Content", "Hi", "Object", "Local", false), properties[0]);
        Assert.True(properties[1].IsReadOnly);
        var path = await session.PathAsync(2, Timeout());
        Assert.Equal("App.MainWindow", path.WindowType);
        Assert.Equal([new PreviewPathStep("MainWindow", 0), new PreviewPathStep("StackPanel", 0), new PreviewPathStep("Button", 0, "Go")], path.Steps);
        var error = await Assert.ThrowsAsync<PreviewerException>(() => session.SetAsync(2, "IsPointerOver", "true", Timeout()));
        Assert.Contains("read-only", error.Message, StringComparison.Ordinal);

        var picked = new TaskCompletionSource<(int, int)>();
        session.Picked += (_, e) => picked.TrySetResult(e);
        await session.PickAsync(on: true, Timeout());
        Assert.Equal((7, 2), await picked.Task.WaitAsync(Timeout()));

        // The app's exit ends the connection; requests then say so.
        var disconnected = new TaskCompletionSource();
        session.Disconnected += (_, _) => disconnected.TrySetResult();
        agent.Dispose();
        await disconnected.Task.WaitAsync(Timeout());
        Assert.False(session.IsConnected);
        await Assert.ThrowsAsync<PreviewerException>(() => session.WindowsAsync(Timeout()));
        await answering.ContinueWith(_ => { }, TaskScheduler.Default);
    }
}
