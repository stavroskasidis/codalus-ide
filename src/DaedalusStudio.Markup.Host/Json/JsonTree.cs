using System.Text;
using System.Text.Json;

namespace DaedalusStudio.Markup.Host.Json;

internal enum JsonKind
{
    Object,
    Array,
    String,
    Number,
    True,
    False,
    Null,
}

/// <summary>A JSON value with its character offsets in the document (UTF-16, end exclusive).</summary>
internal sealed class JsonNodeInfo(JsonKind kind, int start)
{
    public JsonKind Kind { get; } = kind;

    public int Start { get; } = start;

    public int End { get; set; } = start;

    /// <summary>Object members in document order: the key's offsets and the value.</summary>
    public List<JsonMember> Members { get; } = [];

    public List<JsonNodeInfo> Items { get; } = [];

    /// <summary>The value of a string, number or literal (raw JSON text for numbers).</summary>
    public string? Text { get; set; }

    public double? Number { get; set; }

    public JsonNodeInfo? Member(string name) => Members.LastOrDefault(m => m.Key == name)?.Value;

    public string TypeName => Kind switch
    {
        JsonKind.Object => "object",
        JsonKind.Array => "array",
        JsonKind.String => "string",
        JsonKind.Number => Number is { } n && Math.Abs(n % 1) < double.Epsilon ? "integer" : "number",
        JsonKind.True or JsonKind.False => "boolean",
        _ => "null",
    };
}

internal sealed record JsonMember(string Key, int KeyStart, int KeyEnd, JsonNodeInfo Value);

/// <summary>A syntax error at a character offset.</summary>
internal sealed record JsonSyntaxError(int Offset, string Message);

/// <summary>Parses JSON (or JSON with comments and trailing commas) into <see cref="JsonNodeInfo"/> with positions.</summary>
internal static class JsonTree
{
    public static (JsonNodeInfo? Root, JsonSyntaxError? Error) Parse(string text, bool allowComments)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var map = new ByteToChar(bytes);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            CommentHandling = allowComments ? JsonCommentHandling.Skip : JsonCommentHandling.Disallow,
            AllowTrailingCommas = allowComments,
        });
        var stack = new Stack<JsonNodeInfo>();
        JsonNodeInfo? root = null;
        (string Name, int Start, int End)? pendingKey = null;
        try
        {
            while (reader.Read())
            {
                var start = map.Char((int)reader.TokenStartIndex);
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        var keyEnd = map.Char((int)reader.BytesConsumed);
                        pendingKey = (reader.GetString()!, start, keyEnd);
                        continue;
                    case JsonTokenType.StartObject or JsonTokenType.StartArray:
                        var container = new JsonNodeInfo(reader.TokenType == JsonTokenType.StartObject ? JsonKind.Object : JsonKind.Array, start);
                        Attach(container);
                        stack.Push(container);
                        continue;
                    case JsonTokenType.EndObject or JsonTokenType.EndArray:
                        stack.Pop().End = map.Char((int)reader.BytesConsumed);
                        continue;
                }

                var node = new JsonNodeInfo(
                    reader.TokenType switch
                    {
                        JsonTokenType.String => JsonKind.String,
                        JsonTokenType.Number => JsonKind.Number,
                        JsonTokenType.True => JsonKind.True,
                        JsonTokenType.False => JsonKind.False,
                        _ => JsonKind.Null,
                    },
                    start)
                {
                    End = map.Char((int)reader.BytesConsumed),
                };
                if (reader.TokenType == JsonTokenType.String)
                {
                    node.Text = reader.GetString();
                }
                else if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var number))
                {
                    node.Number = number;
                    node.Text = Encoding.UTF8.GetString(reader.ValueSpan);
                }

                Attach(node);
            }

            return (root, null);
        }
        catch (JsonException e)
        {
            // BytePositionInLine counts bytes: convert to characters on that line.
            var line = (int)(e.LineNumber ?? 0);
            var lineStart = 0;
            for (var i = 0; i < line && lineStart < text.Length; i++)
            {
                var next = text.IndexOf('\n', lineStart);
                lineStart = next < 0 ? text.Length : next + 1;
            }

            var byteInLine = (int)(e.BytePositionInLine ?? 0);
            var offset = lineStart;
            for (var bytesSeen = 0; offset < text.Length && bytesSeen < byteInLine && text[offset] != '\n'; offset++)
            {
                bytesSeen += Encoding.UTF8.GetByteCount(text.AsSpan(offset, char.IsHighSurrogate(text[offset]) && offset + 1 < text.Length ? 2 : 1));
                if (char.IsHighSurrogate(text[offset]))
                {
                    offset++;
                }
            }

            var message = e.Message;
            var cut = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
            return (root, new JsonSyntaxError(Math.Min(offset, text.Length), cut > 0 ? message[..cut].Trim() : message));
        }

        void Attach(JsonNodeInfo node)
        {
            if (stack.Count == 0)
            {
                root ??= node;
            }
            else if (stack.Peek().Kind == JsonKind.Object && pendingKey is var (name, keyStart, keyEnd))
            {
                stack.Peek().Members.Add(new JsonMember(name, keyStart, keyEnd, node));
            }
            else
            {
                stack.Peek().Items.Add(node);
            }

            pendingKey = null;
        }
    }

    /// <summary>Maps byte offsets (increasing) to character offsets without rescanning from the start.</summary>
    private sealed class ByteToChar(byte[] bytes)
    {
        private int _byte;
        private int _char;

        public int Char(int byteOffset)
        {
            if (byteOffset < _byte)
            {
                (_byte, _char) = (0, 0);
            }

            while (_byte < byteOffset && _byte < bytes.Length)
            {
                var b = bytes[_byte];
                var length = b < 0x80 ? 1 : b < 0xE0 ? 2 : b < 0xF0 ? 3 : 4;
                _byte += length;
                _char += length == 4 ? 2 : 1; // four-byte sequences are surrogate pairs in UTF-16
            }

            return _char;
        }
    }
}
