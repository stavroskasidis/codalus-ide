using System.Text;

namespace DaedalusStudio.Shell.Editor;

/// <summary>How a text file is stored: its encoding, whether it starts with a byte order mark, and its line ending.</summary>
public sealed record TextFileFormat(Encoding Encoding, bool HasBom, string LineEnding)
{
    public static TextFileFormat Default { get; } = new(new UTF8Encoding(false), false, Environment.NewLine);

    /// <summary>For the status bar: <c>UTF-8</c>, <c>UTF-8 with BOM</c>, <c>UTF-16 LE</c>…</summary>
    public string EncodingName => Encoding switch
    {
        UTF8Encoding => HasBom ? "UTF-8 with BOM" : "UTF-8",
        UnicodeEncoding { CodePage: 1201 } => "UTF-16 BE",
        UnicodeEncoding => "UTF-16 LE",
        UTF32Encoding => "UTF-32",
        _ when Encoding.CodePage == 28591 => "Latin-1",
        _ => Encoding.WebName,
    };

    public string LineEndingName => LineEnding switch
    {
        "\r\n" => "CRLF",
        "\r" => "CR",
        _ => "LF",
    };
}

/// <summary>
/// Reads and writes text files the way they were: the encoding (detected from the BOM, else UTF-8 when the bytes are
/// valid UTF-8, else Latin-1), the BOM, and the line endings (kept as they are in the text). Writes are atomic.
/// </summary>
public static class TextFiles
{
    public static async Task<(string Text, TextFileFormat Format)> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return Decode(bytes);
    }

    public static (string Text, TextFileFormat Format) Decode(ReadOnlySpan<byte> bytes)
    {
        Encoding encoding;
        var bom = 0;
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            (encoding, bom) = (new UTF8Encoding(true), 3);
        }
        else if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00]))
        {
            (encoding, bom) = (new UTF32Encoding(bigEndian: false, byteOrderMark: true), 4);
        }
        else if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            (encoding, bom) = (new UnicodeEncoding(bigEndian: false, byteOrderMark: true), 2);
        }
        else if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            (encoding, bom) = (new UnicodeEncoding(bigEndian: true, byteOrderMark: true), 2);
        }
        else
        {
            encoding = IsUtf8(bytes) ? new UTF8Encoding(false) : Encoding.Latin1;
        }

        var text = encoding.GetString(bytes[bom..]);
        return (text, new TextFileFormat(encoding, bom > 0, DetectLineEnding(text)));
    }

    /// <summary>The first line ending in <paramref name="text"/>, or the platform's for text without one.</summary>
    public static string DetectLineEnding(string text)
    {
        var index = text.AsSpan().IndexOfAny('\r', '\n');
        if (index < 0)
        {
            return Environment.NewLine;
        }

        return text[index] == '\n' ? "\n" : index + 1 < text.Length && text[index + 1] == '\n' ? "\r\n" : "\r";
    }

    public static byte[] Encode(string text, TextFileFormat format)
    {
        var body = format.Encoding.GetBytes(text);
        if (!format.HasBom)
        {
            return body;
        }

        var preamble = format.Encoding.GetPreamble();
        return [.. preamble, .. body];
    }

    /// <summary>Writes through a temporary file in the same folder, then replaces the target.</summary>
    public static async Task WriteAsync(string path, string text, TextFileFormat format, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + "." + Environment.ProcessId + ".tmp");
        await File.WriteAllBytesAsync(temp, Encode(text, format), cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    private static bool IsUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
