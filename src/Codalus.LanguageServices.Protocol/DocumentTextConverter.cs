using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codalus.LanguageServices.Protocol;

/// <summary>
/// Writes a document's whole text (<c>didOpen</c>, a full-text change) in segments. System.Text.Json escapes a string
/// through one buffer of six times its length, rented from the shared pool, which then keeps it: 64 MB of characters for
/// a 100k-line file's <c>didOpen</c>, alive in the shell long after the open. In segments the buffer stays small. The JSON
/// is the same, and reading is unchanged.
/// </summary>
public sealed class DocumentTextConverter : JsonConverter<string>
{
    /// <summary>The characters escaped at a time.</summary>
    public const int SegmentLength = 16 * 1024;

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString();

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        if (value.Length <= SegmentLength)
        {
            writer.WriteStringValue(value);
            return;
        }

        // A surrogate pair split between two segments is joined by the writer.
        var text = value.AsSpan();
        for (var start = 0; start < text.Length; start += SegmentLength)
        {
            var length = Math.Min(SegmentLength, text.Length - start);
            writer.WriteStringValueSegment(text.Slice(start, length), isFinalSegment: start + length == text.Length);
        }
    }
}
