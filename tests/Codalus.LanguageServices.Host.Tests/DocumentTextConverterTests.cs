using System.Buffers;
using System.Text.Json;
using Codalus.LanguageServices.Protocol;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// A document's whole text is written in segments (<see cref="DocumentTextConverter"/>): the same JSON as one string,
/// without the escaping buffer of six times the text's length that the shared pool kept (64 MB of characters after a
/// 100k-line file's didOpen).
/// </summary>
public sealed class DocumentTextConverterTests
{
    /// <summary>
    /// Text that needs escaping everywhere (markup characters, quotes, backslashes, line breaks, tabs, non-ASCII), with
    /// a surrogate pair split by the first segment boundary and one at the very end.
    /// </summary>
    private static string Text()
    {
        var line = "if (a < b && c > d) { s = \"\\t\" + 'é'; }\r\n\t";
        var text = string.Concat(Enumerable.Repeat(line, (DocumentTextConverter.SegmentLength * 3 / line.Length) + 1));
        var boundary = DocumentTextConverter.SegmentLength - 1;
        return text[..boundary] + "😀" + text[boundary..] + "😀";
    }

    [Fact]
    public void A_large_text_is_written_as_one_string_would_be_and_reads_back_the_same()
    {
        var text = Text();
        Assert.True(char.IsHighSurrogate(text[DocumentTextConverter.SegmentLength - 1]));

        var item = new TextDocumentItem("file:///a.cs", "csharp", 3, text);
        var json = JsonSerializer.Serialize(new DidOpenTextDocumentParams(item), ProtocolJsonContext.Default.DidOpenTextDocumentParams);

        // What System.Text.Json writes for the whole string at once, with the same options.
        var whole = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(whole, new JsonWriterOptions { Encoder = ProtocolJsonContext.Default.Options.Encoder }))
        {
            writer.WriteStringValue(text);
        }

        Assert.Contains("\"text\":" + System.Text.Encoding.UTF8.GetString(whole.WrittenSpan), json, StringComparison.Ordinal);
        Assert.Equal(item, JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.DidOpenTextDocumentParams)!.TextDocument);

        // A full-text change (servers that sync whole documents) likewise.
        var change = new DidChangeTextDocumentParams(new VersionedTextDocumentIdentifier("file:///a.cs", 4), [new TextDocumentContentChangeEvent(null, text)]);
        var changeJson = JsonSerializer.Serialize(change, ProtocolJsonContext.Default.DidChangeTextDocumentParams);
        Assert.Contains("\"text\":" + System.Text.Encoding.UTF8.GetString(whole.WrittenSpan), changeJson, StringComparison.Ordinal);
        Assert.Equal(text, Assert.Single(JsonSerializer.Deserialize(changeJson, ProtocolJsonContext.Default.DidChangeTextDocumentParams)!.ContentChanges).Text);
    }

    [Fact]
    public void Writing_a_large_text_allocates_far_less_than_six_times_its_length()
    {
        var item = new TextDocumentItem("file:///a.cs", "csharp", 1, string.Concat(Enumerable.Repeat(Text(), 16)));
        var parameters = new DidOpenTextDocumentParams(item);

        // The output buffer is allocated first (6 bytes per character fits any escaping), so what is measured is the
        // writing. Nothing has escaped a string this large before in this process, so no pooled buffer can hide one.
        var buffer = new ArrayBufferWriter<byte>((item.Text.Length * 6) + 1024);
        var before = GC.GetAllocatedBytesForCurrentThread();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            JsonSerializer.Serialize(writer, parameters, ProtocolJsonContext.Default.DidOpenTextDocumentParams);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Escaped as one string, the text needs a buffer of 12 bytes per character (16 MB here); in segments, a few
        // hundred kilobytes.
        Assert.True(allocated < 2 * 1024 * 1024, $"{allocated:N0} bytes allocated to write {buffer.WrittenCount:N0} bytes of JSON ({item.Text.Length:N0} characters)");
    }
}
