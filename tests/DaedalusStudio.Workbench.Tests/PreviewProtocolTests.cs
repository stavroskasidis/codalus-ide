using System.Net;
using System.Net.Sockets;
using Avalonia.Remote.Protocol;
using DaedalusStudio.Workbench.Previewer;
using Designer = Avalonia.Remote.Protocol.Designer;
using Viewport = Avalonia.Remote.Protocol.Viewport;

namespace DaedalusStudio.Workbench.Tests;

/// <summary>
/// M19a (ADR 0048): the previewer's hand-written codec of Avalonia's remote protocol against Avalonia's own transport
/// (<c>Avalonia.Remote.Protocol</c>), in both directions, and the agent's reply parsing.
/// </summary>
public sealed class PreviewProtocolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Avalonia's TCP transport connected to a socket that <see cref="RemoteProtocol"/> reads and writes.</summary>
    private sealed class Wire : IAsyncDisposable
    {
        private readonly TaskCompletionSource<object> _received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TcpClient _socket = null!;

        public IAvaloniaRemoteTransportConnection Avalonia { get; private set; } = null!;

        public Stream Ours => _socket.GetStream();

        public static async Task<Wire> ConnectAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var wire = new Wire();
                var accept = listener.AcceptTcpClientAsync(Ct).AsTask();
                wire.Avalonia = await new BsonTcpTransport().Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                wire._socket = await accept;
                wire.Avalonia.OnMessage += (_, message) => wire._received.TrySetResult(message);
                wire.Avalonia.Start();
                return wire;
            }
            finally
            {
                listener.Stop();
            }
        }

        public async Task SendToAvaloniaAsync(PreviewMessage message)
        {
            await Ours.WriteAsync(RemoteProtocol.Serialize(message), Ct);
            await Ours.FlushAsync(Ct);
        }

        public Task<object> ReceivedByAvalonia => _received.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        public ValueTask DisposeAsync()
        {
            (Avalonia as IDisposable)?.Dispose();
            _socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }
    [Fact]
    public async Task Avalonias_messages_decode()
    {
        await using var wire = await Wire.ConnectAsync();
        await wire.Avalonia.Send(new Designer.StartDesignerSessionMessage { SessionId = "abc" });
        Assert.Equal(new StartDesignerSessionMessage("abc"), await RemoteProtocol.ReadAsync(wire.Ours, Ct));

        await wire.Avalonia.Send(new Designer.UpdateXamlResultMessage
        {
            Error = "boom",
            Exception = new Designer.ExceptionDetails { ExceptionType = "XamlParseException", Message = "Unexpected", LineNumber = 3, LinePosition = 7 },
        });
        var result = Assert.IsType<UpdateXamlResultMessage>(await RemoteProtocol.ReadAsync(wire.Ours, Ct));
        Assert.Equal("boom", result.Error);
        Assert.Null(result.Handle);
        Assert.Equal(new PreviewExceptionDetails("XamlParseException", "Unexpected", 3, 7), result.Exception);

        await wire.Avalonia.Send(new Designer.UpdateXamlResultMessage { Handle = "42" });
        var success = Assert.IsType<UpdateXamlResultMessage>(await RemoteProtocol.ReadAsync(wire.Ours, Ct));
        Assert.Null(success.Error);
        Assert.Null(success.Exception);

        var pixels = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        await wire.Avalonia.Send(new Viewport.FrameMessage
        {
            SequenceId = 1L << 40,
            Format = Viewport.PixelFormat.Rgba8888,
            Data = pixels,
            Width = 4,
            Height = 4,
            Stride = 16,
            DpiX = 144,
            DpiY = 144,
        });
        var frame = Assert.IsType<FrameMessage>(await RemoteProtocol.ReadAsync(wire.Ours, Ct));
        Assert.Equal((1L << 40, PreviewPixelFormat.Rgba8888, 4, 4, 16, 144d), (frame.SequenceId, frame.Format, frame.Width, frame.Height, frame.Stride, frame.DpiX));
        Assert.Equal(pixels, frame.Data);

        await wire.Avalonia.Send(new Viewport.RequestViewportResizeMessage { Width = 300, Height = 200 });
        Assert.Equal(new RequestViewportResizeMessage(300, 200), await RemoteProtocol.ReadAsync(wire.Ours, Ct));
    }

    [Fact]
    public async Task Our_messages_decode_in_avalonia()
    {
        await using (var wire = await Wire.ConnectAsync())
        {
            await wire.SendToAvaloniaAsync(new UpdateXamlMessage("<Button Content=\"Ü\" />", "C:/app/App.dll", "/Views/Main.axaml"));
            var update = Assert.IsType<Designer.UpdateXamlMessage>(await wire.ReceivedByAvalonia);
            Assert.Equal(("<Button Content=\"Ü\" />", "C:/app/App.dll", "/Views/Main.axaml"), (update.Xaml, update.AssemblyPath, update.XamlFileProjectPath));
        }

        await using (var wire = await Wire.ConnectAsync())
        {
            await wire.SendToAvaloniaAsync(new ClientSupportedPixelFormatsMessage([PreviewPixelFormat.Rgba8888, PreviewPixelFormat.Bgra8888]));
            var formats = Assert.IsType<Viewport.ClientSupportedPixelFormatsMessage>(await wire.ReceivedByAvalonia);
            Assert.Equal([Viewport.PixelFormat.Rgba8888, Viewport.PixelFormat.Bgra8888], formats.Formats);
        }

        await using (var wire = await Wire.ConnectAsync())
        {
            await wire.SendToAvaloniaAsync(new ClientRenderInfoMessage(192, 192));
            var info = Assert.IsType<Viewport.ClientRenderInfoMessage>(await wire.ReceivedByAvalonia);
            Assert.Equal((192d, 192d), (info.DpiX, info.DpiY));
        }

        await using (var wire = await Wire.ConnectAsync())
        {
            await wire.SendToAvaloniaAsync(new ClientViewportAllocatedMessage(640, 480, 96, 96));
            var viewport = Assert.IsType<Viewport.ClientViewportAllocatedMessage>(await wire.ReceivedByAvalonia);
            Assert.Equal((640d, 480d, 96d), (viewport.Width, viewport.Height, viewport.DpiX));
        }

        await using (var wire = await Wire.ConnectAsync())
        {
            await wire.SendToAvaloniaAsync(new FrameReceivedMessage(1234567890123));
            Assert.Equal(1234567890123, Assert.IsType<Viewport.FrameReceivedMessage>(await wire.ReceivedByAvalonia).SequenceId);
        }

        await using (var wire = await Wire.ConnectAsync())
        {
            await wire.SendToAvaloniaAsync(new PointerPressedEventMessage(PreviewMouseButton.Left, 10.5, 20));
            var pressed = Assert.IsType<Avalonia.Remote.Protocol.Input.PointerPressedEventMessage>(await wire.ReceivedByAvalonia);
            Assert.Equal((Avalonia.Remote.Protocol.Input.MouseButton.Left, 10.5, 20d), (pressed.Button, pressed.X, pressed.Y));
        }
    }

    [Fact]
    public void Our_messages_round_trip()
    {
        PreviewMessage[] messages =
        [
            new UpdateXamlResultMessage("e", null, new PreviewExceptionDetails("T", "m", null, 4)),
            new FrameMessage(9, PreviewPixelFormat.Bgra8888, [1, 2, 3], 1, 1, 4, 96, 96),
            new ScrollEventMessage(0, -1, 5, 6),
            new PointerMovedEventMessage(1, 2),
            new MeasureViewportMessage(1, 2),
        ];
        foreach (var message in messages)
        {
            var (type, body) = RemoteProtocol.Encode(message);
            var decoded = RemoteProtocol.Decode(type, body);
            if (message is FrameMessage frame)
            {
                var copy = Assert.IsType<FrameMessage>(decoded);
                Assert.Equal(frame.Data, copy.Data);
                Assert.Equal(frame with { Data = copy.Data }, copy);
            }
            else
            {
                Assert.Equal(message, decoded);
            }
        }
    }

    [Fact]
    public void Agent_replies_parse()
    {
        var hit = PreviewerSession.ParseHit("12,34.5,100,20\tWindow:0:/DockPanel:0:/Button:1:IncrementButton");
        Assert.NotNull(hit);
        Assert.Equal(new PreviewRect(12, 34.5, 100, 20), hit.Bounds);
        Assert.Equal([new("Window", 0), new("DockPanel", 0), new("Button", 1, "IncrementButton")], hit.Path);
        Assert.Null(PreviewerSession.ParseHit(string.Empty));
        Assert.Null(PreviewerSession.ParseRect("none"));
    }
}
