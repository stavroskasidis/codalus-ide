using System.Globalization;
using System.Text;

namespace Codalus.Rpc;

/// <summary>Reads <c>Content-Length</c>-framed messages (LSP and the Debug Adapter Protocol frame them the same way).</summary>
public sealed class FramedMessageReader(Stream stream)
{
    private static ReadOnlySpan<byte> HeaderTerminator => "\r\n\r\n"u8;

    private byte[] _buffer = new byte[64 * 1024];
    private int _start;
    private int _end;

    /// <summary>The size of the read buffer (tests: a large message doesn't grow it).</summary>
    internal int BufferCapacity => _buffer.Length;

    public async Task<byte[]?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        int contentLength;
        while (true)
        {
            var headerEnd = _buffer.AsSpan(_start, _end - _start).IndexOf(HeaderTerminator);
            if (headerEnd >= 0)
            {
                contentLength = ParseContentLength(_buffer.AsSpan(_start, headerEnd));
                _start += headerEnd + HeaderTerminator.Length;
                break;
            }

            if (!await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
        }

        if (contentLength > _buffer.Length)
        {
            return await ReadLargeMessageAsync(contentLength, cancellationToken).ConfigureAwait(false);
        }

        while (_end - _start < contentLength)
        {
            if (!await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("Stream ended in the middle of a message.");
            }
        }

        var message = _buffer.AsSpan(_start, contentLength).ToArray();
        _start += contentLength;
        return message;
    }

    /// <summary>
    /// A message larger than the buffer is read into its own array. Growing the buffer for it (doubling, then a copy of
    /// the message out of it) made garbage of twice the message's size and kept the buffer at the largest message's size
    /// for the connection's life: 8 MB in the shell after a 100k-line file's document symbols.
    /// </summary>
    private async Task<byte[]> ReadLargeMessageAsync(int contentLength, CancellationToken cancellationToken)
    {
        // What is buffered is the start of this message: the buffer is smaller than it.
        var message = new byte[contentLength];
        var filled = _end - _start;
        _buffer.AsSpan(_start, filled).CopyTo(message);
        _start = _end = 0;
        while (filled < contentLength)
        {
            var read = await stream.ReadAsync(message.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException("Stream ended in the middle of a message.");
            }

            filled += read;
        }

        return message;
    }

    private async Task<bool> FillAsync(CancellationToken cancellationToken)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        if (_end == _buffer.Length)
        {
            Array.Resize(ref _buffer, _buffer.Length * 2);
        }

        var read = await stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
        _end += read;
        return read > 0;
    }

    private static int ParseContentLength(ReadOnlySpan<byte> headers)
    {
        foreach (var line in Encoding.ASCII.GetString(headers).Split("\r\n"))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator > 0 && line.AsSpan(0, separator).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                return int.Parse(line.AsSpan(separator + 1).Trim(), CultureInfo.InvariantCulture);
            }
        }

        throw new InvalidDataException("Message header has no Content-Length.");
    }
}

/// <summary>How messages are delimited on a connection's streams.</summary>
public enum JsonRpcFraming
{
    /// <summary>A <c>Content-Length</c> header before each message (LSP, the Debug Adapter Protocol, the hosts).</summary>
    ContentLength,

    /// <summary>One message per line, with no newline inside it (the Agent Client Protocol's stdio transport, M48).</summary>
    NewlineDelimited,
}

/// <summary>
/// Reads newline-delimited messages (M48: the Agent Client Protocol's stdio transport). Blank lines, and lines that
/// aren't a JSON object (a program that logs to its standard output), are skipped.
/// </summary>
public sealed class LineMessageReader(Stream stream)
{
    private byte[] _buffer = new byte[64 * 1024];
    private int _start;
    private int _end;

    public async Task<byte[]?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var newline = _buffer.AsSpan(_start, _end - _start).IndexOf((byte)'\n');
            if (newline >= 0)
            {
                var line = _buffer.AsSpan(_start, newline).Trim(" \t\r"u8);
                _start += newline + 1;
                if (line.Length > 0 && line[0] == (byte)'{')
                {
                    return line.ToArray();
                }

                continue;
            }

            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            if (_end == _buffer.Length)
            {
                Array.Resize(ref _buffer, _buffer.Length * 2);
            }

            var read = await stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                // The last line without its newline still counts.
                var rest = _buffer.AsSpan(_start, _end - _start).Trim(" \t\r"u8);
                _start = _end = 0;
                return rest.Length > 0 && rest[0] == (byte)'{' ? rest.ToArray() : null;
            }

            _end += read;
        }
    }
}

/// <summary>Writes <c>Content-Length</c>-framed messages.</summary>
public static class FramedMessageWriter
{
    /// <summary>The header for a body of <paramref name="length"/> bytes.</summary>
    public static byte[] Header(int length) =>
        Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"Content-Length: {length}\r\n\r\n"));
}
