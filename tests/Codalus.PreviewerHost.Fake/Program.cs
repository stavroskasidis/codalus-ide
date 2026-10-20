using System.Net;
using Avalonia.Remote.Protocol;
using Avalonia.Remote.Protocol.Designer;

// A stand-in for Avalonia's designer host (started as PreviewerSession starts Avalonia.Designer.HostApp): it connects
// over Avalonia's own transport and answers each update with its XAML as the error, so a test tells the answers apart.
// The first time it sees XAML containing "<!--held-->" it holds the answer back until the next update comes, as
// Avalonia's designer can (its sender misses a wake-up, and the answer waits until it sends anything else), and takes
// two seconds to load it again, so its second answer comes after the session has sent its next update. Updates are
// answered in order, as the designer loads them on its UI thread.
var transport = args[Array.IndexOf(args, "--transport") + 1];
var sessionId = args[Array.IndexOf(args, "--session-id") + 1];
var port = new Uri(transport).Port;

var gate = new object();
var seen = new HashSet<string>(StringComparer.Ordinal);
UpdateXamlResultMessage? held = null;
var closed = new TaskCompletionSource();
var connection = await new BsonTcpTransport().Connect(IPAddress.Loopback, port);
connection.OnException += (_, _) => closed.TrySetResult();
connection.OnMessage += (sender, message) =>
{
    if (message is not UpdateXamlMessage update)
    {
        return;
    }

    lock (gate)
    {
        if (held is not null)
        {
            _ = connection.Send(held);
            held = null;
        }

        var answer = new UpdateXamlResultMessage { Error = update.Xaml };
        var again = !seen.Add(update.Xaml);
        if (!again && update.Xaml.Contains("<!--held-->", StringComparison.Ordinal))
        {
            held = answer;
            return;
        }

        if (again)
        {
            Thread.Sleep(2000);
        }

        _ = connection.Send(answer);
    }
};
connection.Start();
await connection.Send(new StartDesignerSessionMessage { SessionId = sessionId });
await closed.Task;
