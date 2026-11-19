using System.Net;
using Avalonia.Remote.Protocol;
using Avalonia.Remote.Protocol.Designer;
using Avalonia.Remote.Protocol.Viewport;

// A stand-in for Avalonia's designer host (started as PreviewerSession starts Avalonia.Designer.HostApp): it connects
// over Avalonia's own transport and answers each update with its XAML and how many times it has had it ("…#2") as the
// error, so a test tells the answers apart.
// The first time it sees XAML containing "<!--held-->" it holds the answer back until the next update comes, as
// Avalonia's designer can (its sender misses a wake-up, and the answer waits until it sends anything else), and takes
// two seconds to load it again, so its second answer comes after the session has sent its next update. Updates are
// answered in order, as the designer loads them on its UI thread.
// XAML containing "<!--frame-->" loads without an error and sends its frame. XAML containing "<!--held frame-->" loads
// without an error too, and from then on each frame it renders (for that update, and for each new render scale) waits
// the same way until it sends anything else: the answer to a viewport measure.
var transport = args[Array.IndexOf(args, "--transport") + 1];
var sessionId = args[Array.IndexOf(args, "--session-id") + 1];
var port = new Uri(transport).Port;

var gate = new object();
var seen = new Dictionary<string, int>(StringComparer.Ordinal);
object? held = null;
var holdFrames = false;
var scale = 1.0;
long sequence = 0;
var closed = new TaskCompletionSource();
var connection = await new BsonTcpTransport().Connect(IPAddress.Loopback, port);
connection.OnException += (_, _) => closed.TrySetResult();

FrameMessage Frame() => new()
{
    SequenceId = ++sequence,
    Format = PixelFormat.Rgba8888,
    Width = (int)(100 * scale),
    Height = (int)(50 * scale),
    Stride = (int)(100 * scale) * 4,
    Data = new byte[(int)(100 * scale) * 4 * (int)(50 * scale)],
    DpiX = 96 * scale,
    DpiY = 96 * scale,
};

// What the designer sends goes after what waited in its queue.
void Send(object message)
{
    if (held is not null)
    {
        _ = connection.Send(held);
        held = null;
    }

    _ = connection.Send(message);
}

connection.OnMessage += (sender, message) =>
{
    lock (gate)
    {
        switch (message)
        {
            case MeasureViewportMessage measure:
                Send(new MeasureViewportMessage { Width = measure.Width, Height = measure.Height });
                return;
            case ClientRenderInfoMessage render:
                scale = render.DpiX / 96;
                if (holdFrames)
                {
                    held = Frame();
                }

                return;
            case UpdateXamlMessage update:
                break;
            default:
                return;
        }

        if (held is not null)
        {
            _ = connection.Send(held);
            held = null;
        }

        var xaml = ((UpdateXamlMessage)message).Xaml;
        var times = seen[xaml] = seen.GetValueOrDefault(xaml) + 1;
        if (xaml.Contains("<!--frame-->", StringComparison.Ordinal))
        {
            Send(new UpdateXamlResultMessage());
            Send(Frame());
            return;
        }

        if (xaml.Contains("<!--held frame-->", StringComparison.Ordinal))
        {
            holdFrames = true;
            Send(new UpdateXamlResultMessage());
            held = Frame();
            return;
        }

        var answer = new UpdateXamlResultMessage { Error = $"{xaml}#{times}" };
        var again = times > 1;
        if (!again && xaml.Contains("<!--held-->", StringComparison.Ordinal))
        {
            held = answer;
            return;
        }

        if (again)
        {
            Thread.Sleep(2000);
        }

        Send(answer);
    }
};
connection.Start();
await connection.Send(new StartDesignerSessionMessage { SessionId = sessionId });
await closed.Task;
