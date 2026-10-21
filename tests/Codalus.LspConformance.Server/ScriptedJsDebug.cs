using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codalus.LspConformance.Server;

/// <summary>
/// Started with the arguments of js-debug's DAP server (<c>dapDebugServer.js &lt;port&gt; &lt;host&gt;</c>): a stand-in for
/// vscode-js-debug in a browser launch, for <c>JsDebugAdapter</c>'s tests (ADR 0026). It listens there and prints the line
/// js-debug prints, and each connection is a session, the first the launch session. Its <c>launch</c> asks for a child
/// session for the page (<c>startDebugging</c>), as js-debug does once it has attached to the browser's page, and says in
/// an <c>output</c> event whether js-debug would open the launch's url itself or leave the page blank
/// (<c>skipNavigateForTest</c>). A child answers its start after its <c>configurationDone</c>, as js-debug does, and an
/// <c>evaluate</c> in it (<c>location.replace</c>, the page opened) comes back as an <c>output</c> event. The launch's
/// first <c>runtimeArgs</c> entry scripts how the children's attaches go:
/// <list type="bullet">
/// <item><c>page-fails</c>: the page's start fails, as when the page closes while js-debug attaches to it;</item>
/// <item><c>page-fails-while-another-attaches</c>: two children; the first to connect fails its <c>initialize</c> once the
/// second has connected, and the second is answered half a second after that, and attaches.</item>
/// </list>
/// </summary>
internal static class ScriptedJsDebug
{
    public static async Task RunAsync(IPAddress host, int port)
    {
        var listener = new TcpListener(host, port);
        listener.Start();
        Console.WriteLine($"Debug server listening at {host}:{((IPEndPoint)listener.LocalEndpoint).Port}");
        using var launch = await listener.AcceptTcpClientAsync();

        // The session ends with its launch session: JsDebugAdapter disconnects it, or kills this process.
        await LaunchSessionAsync(launch.GetStream(), listener);
        listener.Stop();
    }

    private static async Task LaunchSessionAsync(NetworkStream stream, TcpListener listener)
    {
        while (await ScriptedDap.ReadAsync(stream) is { } message)
        {
            if (message.GetProperty("type").GetString() != "request")
            {
                continue; // the answers to startDebugging
            }

            var command = message.GetProperty("command").GetString()!;
            var requestSeq = message.GetProperty("seq").GetInt32();
            switch (command)
            {
                case "initialize":
                    await ScriptedDap.RespondAsync(stream, requestSeq, command, new JsonObject { ["supportsConfigurationDoneRequest"] = true });
                    await ScriptedDap.EventAsync(stream, "initialized", null);
                    break;
                case "launch":
                    {
                        var arguments = message.GetProperty("arguments");
                        var script = new Script(arguments.TryGetProperty("runtimeArgs", out var runtimeArgs) && runtimeArgs.GetArrayLength() > 0 ? runtimeArgs[0].GetString() : null);
                        _ = AcceptChildrenAsync(listener, script);
                        for (var target = 1; target <= script.Children; target++)
                        {
                            await ScriptedDap.WriteAsync(stream, new JsonObject
                            {
                                ["type"] = "request",
                                ["command"] = "startDebugging",
                                ["arguments"] = new JsonObject
                                {
                                    ["request"] = "launch",
                                    ["configuration"] = new JsonObject { ["type"] = arguments.GetProperty("type").GetString(), ["name"] = "Page", ["__pendingTargetId"] = $"target-{target}" },
                                },
                            });
                        }

                        await ScriptedDap.RespondAsync(stream, requestSeq, command, null);
                        var opens = arguments.TryGetProperty("skipNavigateForTest", out _)
                            ? "js-debug leaves the page blank."
                            : $"js-debug opens {arguments.GetProperty("url").GetString()}.";
                        await ScriptedDap.EventAsync(stream, "output", new JsonObject { ["category"] = "console", ["output"] = opens + "\n" });
                        break;
                    }

                case "disconnect":
                    await ScriptedDap.RespondAsync(stream, requestSeq, command, null);
                    return;
                default:
                    await ScriptedDap.RespondAsync(stream, requestSeq, command, null);
                    break;
            }
        }
    }

    private static async Task AcceptChildrenAsync(TcpListener listener, Script script)
    {
        try
        {
            for (var index = 0; index < script.Children; index++)
            {
                var client = await listener.AcceptTcpClientAsync();
                if (index == 1)
                {
                    script.SecondConnected.TrySetResult();
                }

                var child = index;
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        await ChildSessionAsync(client.GetStream(), script, child);
                    }
                });
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // The session ended.
        }
    }

    private static async Task ChildSessionAsync(NetworkStream stream, Script script, int index)
    {
        (int Seq, string Command)? start = null;
        try
        {
            while (await ScriptedDap.ReadAsync(stream) is { } message)
            {
                if (message.GetProperty("type").GetString() != "request")
                {
                    continue;
                }

                var command = message.GetProperty("command").GetString()!;
                var requestSeq = message.GetProperty("seq").GetInt32();
                switch (command)
                {
                    case "initialize" when script.Name == "page-fails-while-another-attaches" && index == 0:
                        // It fails while its sibling is still connecting, before either has been configured.
                        await script.SecondConnected.Task;
                        await ScriptedDap.FailAsync(stream, requestSeq, command, "Target closed.");
                        script.FirstFailed.TrySetResult();
                        break;
                    case "initialize":
                        if (script.Name == "page-fails-while-another-attaches")
                        {
                            await script.FirstFailed.Task;
                            await Task.Delay(500);
                        }

                        await ScriptedDap.RespondAsync(stream, requestSeq, command, new JsonObject { ["supportsConfigurationDoneRequest"] = true });
                        await ScriptedDap.EventAsync(stream, "initialized", null);
                        break;
                    case "launch" or "attach" when script.Name == "page-fails":
                        await ScriptedDap.FailAsync(stream, requestSeq, command, "Target closed.");
                        break;
                    case "launch" or "attach":
                        start = (requestSeq, command);
                        break;
                    case "configurationDone":
                        await ScriptedDap.RespondAsync(stream, requestSeq, command, null);
                        if (start is { } started)
                        {
                            await ScriptedDap.RespondAsync(stream, started.Seq, started.Command, null);
                        }

                        break;
                    case "setBreakpoints":
                        await ScriptedDap.RespondAsync(stream, requestSeq, command, new JsonObject { ["breakpoints"] = new JsonArray() });
                        break;
                    case "evaluate":
                        var expression = message.GetProperty("arguments").GetProperty("expression").GetString();
                        await ScriptedDap.EventAsync(stream, "output", new JsonObject { ["category"] = "console", ["output"] = $"evaluate: {expression}\n" });
                        await ScriptedDap.RespondAsync(stream, requestSeq, command, new JsonObject { ["result"] = "undefined", ["variablesReference"] = 0 });
                        break;
                    case "disconnect":
                        await ScriptedDap.RespondAsync(stream, requestSeq, command, null);
                        return;
                    default:
                        await ScriptedDap.RespondAsync(stream, requestSeq, command, null);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonException)
        {
            // The client went away.
        }
    }

    private sealed class Script(string? name)
    {
        public string? Name { get; } = name;

        public int Children => Name == "page-fails-while-another-attaches" ? 2 : 1;

        public TaskCompletionSource SecondConnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
