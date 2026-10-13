using System.Buffers.Binary;
using System.Text;

namespace Codalus.Workbench.Previewer;

/// <summary>
/// Avalonia's remote protocol (<c>Avalonia.Remote.Protocol</c>, MIT, ADR 0048), the messages the previewer uses: XAML
/// in, frames out. Each message on the wire is a little-endian int32 body length, the message type's 16-byte GUID
/// (<c>[AvaloniaRemoteMessageGuid]</c>) and a BSON document of its public properties. Avalonia's own transport
/// serializes with reflection, which the Native AOT shell can't use, so this is a small hand-written codec; the tests
/// check it against Avalonia's transport in both directions.
/// </summary>
public abstract record PreviewMessage;

public enum PreviewPixelFormat
{
    Rgb565 = 0,
    Rgba8888 = 1,
    Bgra8888 = 2,
}

public enum PreviewMouseButton
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 3,
}

/// <summary>The designer host's first message after it connects.</summary>
public sealed record StartDesignerSessionMessage(string? SessionId) : PreviewMessage;

/// <summary>XAML to render: the text, the assembly it belongs to (for its <c>x:Class</c> and resources) and its path in the project.</summary>
public sealed record UpdateXamlMessage(string Xaml, string? AssemblyPath, string? XamlFileProjectPath) : PreviewMessage;

public sealed record PreviewExceptionDetails(string? ExceptionType, string? Message, int? LineNumber, int? LinePosition);

/// <summary>The outcome of an <see cref="UpdateXamlMessage"/>: an error (with the exception's position) or none.</summary>
public sealed record UpdateXamlResultMessage(string? Error, string? Handle, PreviewExceptionDetails? Exception) : PreviewMessage;

public sealed record ClientRenderInfoMessage(double DpiX, double DpiY) : PreviewMessage;

public sealed record ClientSupportedPixelFormatsMessage(IReadOnlyList<PreviewPixelFormat> Formats) : PreviewMessage;

public sealed record ClientViewportAllocatedMessage(double Width, double Height, double DpiX, double DpiY) : PreviewMessage;

public sealed record RequestViewportResizeMessage(double Width, double Height) : PreviewMessage;

public sealed record MeasureViewportMessage(double Width, double Height) : PreviewMessage;

/// <summary>A rendered frame: <see cref="Data"/> holds <see cref="Height"/> rows of <see cref="Stride"/> bytes.</summary>
public sealed record FrameMessage(long SequenceId, PreviewPixelFormat Format, byte[] Data, int Width, int Height, int Stride, double DpiX, double DpiY) : PreviewMessage;

/// <summary>The client's acknowledgement; the designer sends the next frame only after it.</summary>
public sealed record FrameReceivedMessage(long SequenceId) : PreviewMessage;

public sealed record PointerMovedEventMessage(double X, double Y) : PreviewMessage;

public sealed record PointerPressedEventMessage(PreviewMouseButton Button, double X, double Y) : PreviewMessage;

public sealed record PointerReleasedEventMessage(PreviewMouseButton Button, double X, double Y) : PreviewMessage;

public sealed record ScrollEventMessage(double DeltaX, double DeltaY, double X, double Y) : PreviewMessage;

/// <summary>A message this codec doesn't know (it is skipped).</summary>
public sealed record UnknownPreviewMessage(Guid Type) : PreviewMessage;

public static class RemoteProtocol
{
    public static readonly Guid StartDesignerSession = new("854887CF-2694-4EB6-B499-7461B6FB96C7");
    public static readonly Guid UpdateXaml = new("9AEC9A2E-6315-4066-B4BA-E9A9EFD0F8CC");
    public static readonly Guid UpdateXamlResult = new("B7A70093-0C5D-47FD-9261-22086D43A2E2");
    public static readonly Guid ClientRenderInfo = new("7A3C25D3-3652-438D-8EF1-86E942CC96C0");
    public static readonly Guid ClientSupportedPixelFormats = new("63481025-7016-43FE-BADC-F2FD0F88609E");
    public static readonly Guid ClientViewportAllocated = new("BD7A8DE6-3DB8-4A13-8583-D6D4AB189A31");
    public static readonly Guid RequestViewportResize = new("9B47B3D8-61DF-4C38-ACD4-8C1BB72554AC");
    public static readonly Guid MeasureViewport = new("6E3C5310-E2B1-4C3D-8688-01183AA48C5B");
    public static readonly Guid Frame = new("F58313EE-FE69-4536-819D-F52EDF201A0E");
    public static readonly Guid FrameReceived = new("68014F8A-289D-4851-8D34-5367EDA7F827");
    public static readonly Guid PointerMoved = new("6228F0B9-99F2-4F62-A621-414DA2881648");
    public static readonly Guid PointerPressed = new("7E9E2818-F93F-411A-800E-6B1AEB11DA46");
    public static readonly Guid PointerReleased = new("4ADC84EE-E7C8-4BCF-986C-DE3A2F78EDE4");
    public static readonly Guid Scroll = new("79301A05-F02D-4B90-BB39-472563B504AE");

    /// <summary>Messages larger than this are refused (a 4K frame at 2× scale is about 130 MB).</summary>
    public const int MaxMessageLength = 256 * 1024 * 1024;

    // ------------------------------------------------------------------ framing

    public static byte[] Serialize(PreviewMessage message)
    {
        var (type, body) = Encode(message);
        var buffer = new byte[20 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, body.Length);
        type.TryWriteBytes(buffer.AsSpan(4, 16));
        body.CopyTo(buffer, 20);
        return buffer;
    }

    /// <summary>Reads one message; null at the end of the stream.</summary>
    public static async Task<PreviewMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[20];
        if (!await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 5 or > MaxMessageLength)
        {
            throw new InvalidDataException($"A previewer message of {length} bytes.");
        }

        var type = new Guid(header.AsSpan(4, 16));
        var body = new byte[length];
        if (!await ReadExactAsync(stream, body, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return Decode(type, body);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    // ------------------------------------------------------------------ messages

    public static (Guid Type, byte[] Body) Encode(PreviewMessage message)
    {
        var w = new BsonWriter();
        Guid type;
        switch (message)
        {
            case StartDesignerSessionMessage m:
                type = StartDesignerSession;
                w.String("SessionId", m.SessionId);
                break;
            case UpdateXamlMessage m:
                type = UpdateXaml;
                w.String("Xaml", m.Xaml);
                w.String("AssemblyPath", m.AssemblyPath);
                w.String("XamlFileProjectPath", m.XamlFileProjectPath);
                break;
            case UpdateXamlResultMessage m:
                type = UpdateXamlResult;
                w.String("Error", m.Error);
                w.String("Handle", m.Handle);
                if (m.Exception is { } e)
                {
                    w.Document("Exception", d =>
                    {
                        d.String("ExceptionType", e.ExceptionType);
                        d.String("Message", e.Message);
                        d.NullableInt32("LineNumber", e.LineNumber);
                        d.NullableInt32("LinePosition", e.LinePosition);
                    });
                }
                else
                {
                    w.Null("Exception");
                }

                break;
            case ClientRenderInfoMessage m:
                type = ClientRenderInfo;
                w.Double("DpiX", m.DpiX);
                w.Double("DpiY", m.DpiY);
                break;
            case ClientSupportedPixelFormatsMessage m:
                type = ClientSupportedPixelFormats;
                w.Array("Formats", m.Formats.Select(f => (int)f).ToList());
                break;
            case ClientViewportAllocatedMessage m:
                type = ClientViewportAllocated;
                w.Double("Width", m.Width);
                w.Double("Height", m.Height);
                w.Double("DpiX", m.DpiX);
                w.Double("DpiY", m.DpiY);
                break;
            case RequestViewportResizeMessage m:
                type = RequestViewportResize;
                w.Double("Width", m.Width);
                w.Double("Height", m.Height);
                break;
            case MeasureViewportMessage m:
                type = MeasureViewport;
                w.Double("Width", m.Width);
                w.Double("Height", m.Height);
                break;
            case FrameMessage m:
                type = Frame;
                w.Int64("SequenceId", m.SequenceId);
                w.Int32("Format", (int)m.Format);
                w.Binary("Data", m.Data);
                w.Int32("Width", m.Width);
                w.Int32("Height", m.Height);
                w.Int32("Stride", m.Stride);
                w.Double("DpiX", m.DpiX);
                w.Double("DpiY", m.DpiY);
                break;
            case FrameReceivedMessage m:
                type = FrameReceived;
                w.Int64("SequenceId", m.SequenceId);
                break;
            case PointerMovedEventMessage m:
                type = PointerMoved;
                w.Double("X", m.X);
                w.Double("Y", m.Y);
                w.Array("Modifiers", []);
                break;
            case PointerPressedEventMessage m:
                type = PointerPressed;
                w.Int32("Button", (int)m.Button);
                w.Double("X", m.X);
                w.Double("Y", m.Y);
                w.Array("Modifiers", []);
                break;
            case PointerReleasedEventMessage m:
                type = PointerReleased;
                w.Int32("Button", (int)m.Button);
                w.Double("X", m.X);
                w.Double("Y", m.Y);
                w.Array("Modifiers", []);
                break;
            case ScrollEventMessage m:
                type = Scroll;
                w.Double("DeltaX", m.DeltaX);
                w.Double("DeltaY", m.DeltaY);
                w.Double("X", m.X);
                w.Double("Y", m.Y);
                w.Array("Modifiers", []);
                break;
            default:
                throw new ArgumentException($"{message.GetType().Name} can't be sent.", nameof(message));
        }

        return (type, w.ToArray());
    }

    public static PreviewMessage Decode(Guid type, byte[] body)
    {
        var d = BsonReader.Read(body);
        if (type == StartDesignerSession)
        {
            return new StartDesignerSessionMessage(d.String("SessionId"));
        }

        if (type == UpdateXaml)
        {
            return new UpdateXamlMessage(d.String("Xaml") ?? string.Empty, d.String("AssemblyPath"), d.String("XamlFileProjectPath"));
        }

        if (type == UpdateXamlResult)
        {
            var e = d.Document("Exception");
            return new UpdateXamlResultMessage(d.String("Error"), d.String("Handle"),
                e is null ? null : new PreviewExceptionDetails(e.String("ExceptionType"), e.String("Message"), e.NullableInt32("LineNumber"), e.NullableInt32("LinePosition")));
        }

        if (type == Frame)
        {
            return new FrameMessage(d.Int64("SequenceId"), (PreviewPixelFormat)d.Int32("Format"), d.Binary("Data") ?? [], d.Int32("Width"), d.Int32("Height"), d.Int32("Stride"), d.Double("DpiX"), d.Double("DpiY"));
        }

        if (type == RequestViewportResize)
        {
            return new RequestViewportResizeMessage(d.Double("Width"), d.Double("Height"));
        }

        if (type == MeasureViewport)
        {
            return new MeasureViewportMessage(d.Double("Width"), d.Double("Height"));
        }

        if (type == ClientRenderInfo)
        {
            return new ClientRenderInfoMessage(d.Double("DpiX"), d.Double("DpiY"));
        }

        if (type == ClientViewportAllocated)
        {
            return new ClientViewportAllocatedMessage(d.Double("Width"), d.Double("Height"), d.Double("DpiX"), d.Double("DpiY"));
        }

        if (type == ClientSupportedPixelFormats)
        {
            return new ClientSupportedPixelFormatsMessage([.. d.Int32Array("Formats").Select(f => (PreviewPixelFormat)f)]);
        }

        if (type == FrameReceived)
        {
            return new FrameReceivedMessage(d.Int64("SequenceId"));
        }

        if (type == PointerMoved)
        {
            return new PointerMovedEventMessage(d.Double("X"), d.Double("Y"));
        }

        if (type == PointerPressed)
        {
            return new PointerPressedEventMessage((PreviewMouseButton)d.Int32("Button"), d.Double("X"), d.Double("Y"));
        }

        if (type == PointerReleased)
        {
            return new PointerReleasedEventMessage((PreviewMouseButton)d.Int32("Button"), d.Double("X"), d.Double("Y"));
        }

        return type == Scroll
            ? new ScrollEventMessage(d.Double("DeltaX"), d.Double("DeltaY"), d.Double("X"), d.Double("Y"))
            : new UnknownPreviewMessage(type);
    }
}

/// <summary>The BSON subset the remote protocol uses: documents of doubles, strings, documents, arrays, binary, booleans, null, int32 and int64.</summary>
internal sealed class BsonWriter
{
    private readonly GrowableBuffer _stream = new();
    private readonly Stack<long> _starts = new();

    public BsonWriter() => Begin();

    /// <summary>A byte buffer with a movable position (a <see cref="MemoryStream"/> without the disposal).</summary>
    private sealed class GrowableBuffer
    {
        private byte[] _bytes = new byte[256];
        private int _length;

        public long Position { get; set; }

        public void Write(ReadOnlySpan<byte> data)
        {
            var end = (int)Position + data.Length;
            if (end > _bytes.Length)
            {
                System.Array.Resize(ref _bytes, Math.Max(end, _bytes.Length * 2));
            }

            data.CopyTo(_bytes.AsSpan((int)Position));
            Position = end;
            _length = Math.Max(_length, end);
        }

        public void WriteByte(byte value) => Write([value]);

        public byte[] ToArray() => _bytes.AsSpan(0, _length).ToArray();
    }

    private void Begin()
    {
        _starts.Push(_stream.Position);
        Span<byte> placeholder = stackalloc byte[4];
        _stream.Write(placeholder);
    }

    private void End()
    {
        _stream.WriteByte(0);
        var start = _starts.Pop();
        var end = _stream.Position;
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, (int)(end - start));
        _stream.Position = start;
        _stream.Write(length);
        _stream.Position = end;
    }

    private void Name(byte type, string name)
    {
        _stream.WriteByte(type);
        _stream.Write(Encoding.UTF8.GetBytes(name));
        _stream.WriteByte(0);
    }

    private void RawInt32(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        _stream.Write(bytes);
    }

    public void Double(string name, double value)
    {
        Name(0x01, name);
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
        _stream.Write(bytes);
    }

    public void String(string name, string? value)
    {
        if (value is null)
        {
            Null(name);
            return;
        }

        Name(0x02, name);
        var bytes = Encoding.UTF8.GetBytes(value);
        RawInt32(bytes.Length + 1);
        _stream.Write(bytes);
        _stream.WriteByte(0);
    }

    public void Document(string name, Action<BsonWriter> write)
    {
        Name(0x03, name);
        Begin();
        write(this);
        End();
    }

    public void Array(string name, IReadOnlyList<int> values)
    {
        Name(0x04, name);
        Begin();
        for (var i = 0; i < values.Count; i++)
        {
            Int32(i.ToString(System.Globalization.CultureInfo.InvariantCulture), values[i]);
        }

        End();
    }

    public void Binary(string name, byte[] value)
    {
        Name(0x05, name);
        RawInt32(value.Length);
        _stream.WriteByte(0); // generic subtype
        _stream.Write(value);
    }

    public void Null(string name) => Name(0x0A, name);

    public void Int32(string name, int value)
    {
        Name(0x10, name);
        RawInt32(value);
    }

    public void NullableInt32(string name, int? value)
    {
        if (value is { } v)
        {
            Int32(name, v);
        }
        else
        {
            Null(name);
        }
    }

    public void Int64(string name, long value)
    {
        Name(0x12, name);
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        _stream.Write(bytes);
    }

    public byte[] ToArray()
    {
        End();
        return _stream.ToArray();
    }
}

/// <summary>A decoded BSON document: element names to values (double, string, document, int list, bytes, bool, null, int, long).</summary>
internal sealed class BsonReader
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    public static BsonReader Read(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        return ReadDocument(data, ref offset);
    }

    private static BsonReader ReadDocument(ReadOnlySpan<byte> data, ref int offset)
    {
        var start = offset;
        var length = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
        if (length < 5 || start + length > data.Length)
        {
            throw new InvalidDataException("A truncated BSON document.");
        }

        offset += 4;
        var document = new BsonReader();
        while (offset < start + length - 1)
        {
            var type = data[offset++];
            var nameEnd = data[offset..].IndexOf((byte)0);
            var name = Encoding.UTF8.GetString(data.Slice(offset, nameEnd));
            offset += nameEnd + 1;
            object? value;
            switch (type)
            {
                case 0x01:
                    value = BinaryPrimitives.ReadDoubleLittleEndian(data[offset..]);
                    offset += 8;
                    break;
                case 0x02:
                case 0x0D:
                case 0x0E:
                    var size = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
                    value = Encoding.UTF8.GetString(data.Slice(offset + 4, Math.Max(0, size - 1)));
                    offset += 4 + size;
                    break;
                case 0x03:
                    value = ReadDocument(data, ref offset);
                    break;
                case 0x04:
                    value = ReadDocument(data, ref offset)._values.Values.ToList();
                    break;
                case 0x05:
                    var binary = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
                    var subtype = data[offset + 4];
                    var payload = data.Slice(offset + 5, binary);

                    // The old binary subtype 2 wraps its bytes in another length.
                    value = subtype == 2 && binary >= 4 ? payload[4..].ToArray() : payload.ToArray();
                    offset += 5 + binary;
                    break;
                case 0x06:
                case 0x0A:
                    value = null;
                    break;
                case 0x07:
                    value = null;
                    offset += 12;
                    break;
                case 0x08:
                    value = data[offset++] != 0;
                    break;
                case 0x09:
                case 0x11:
                case 0x12:
                    value = BinaryPrimitives.ReadInt64LittleEndian(data[offset..]);
                    offset += 8;
                    break;
                case 0x10:
                    value = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
                    offset += 4;
                    break;
                default:
                    throw new InvalidDataException($"BSON type 0x{type:x2} isn't supported.");
            }

            document._values[name] = value;
        }

        offset = start + length;
        return document;
    }

    public string? String(string name) => _values.GetValueOrDefault(name) as string;

    public BsonReader? Document(string name) => _values.GetValueOrDefault(name) as BsonReader;

    public byte[]? Binary(string name) => _values.GetValueOrDefault(name) as byte[];

    public double Double(string name) => _values.GetValueOrDefault(name) switch
    {
        double d => d,
        int i => i,
        long l => l,
        _ => 0,
    };

    public int Int32(string name) => _values.GetValueOrDefault(name) switch
    {
        int i => i,
        long l => (int)l,
        double d => (int)d,
        _ => 0,
    };

    public int? NullableInt32(string name) => _values.GetValueOrDefault(name) is null ? null : Int32(name);

    public long Int64(string name) => _values.GetValueOrDefault(name) switch
    {
        long l => l,
        int i => i,
        double d => (long)d,
        _ => 0,
    };

    public IReadOnlyList<int> Int32Array(string name) =>
        _values.GetValueOrDefault(name) is List<object?> items ? [.. items.Select(i => i switch { int n => n, long n => (int)n, _ => 0 })] : [];
}
