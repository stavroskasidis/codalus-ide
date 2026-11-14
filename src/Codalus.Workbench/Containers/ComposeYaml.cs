using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Codalus.Workbench.Containers;

/// <summary>
/// A reader of the block YAML compose files are written in (M63, ADR 0102), into <see cref="JsonNode"/>s with every
/// scalar a string: mappings, sequences (<c>- item</c>, and <c>- key: value</c> starting a mapping), plain, single- and
/// double-quoted scalars, literal and folded block scalars (<c>|</c>, <c>&gt;</c>), one-line flow sequences and mappings
/// (<c>[a, b]</c>, <c>{a: b}</c>), and comments. Anchors are dropped, an alias reads as its text and merge keys
/// (<c>&lt;&lt;</c>) are skipped. It reads what the IDE needs of a compose file when the engine's
/// <c>compose config --format json</c> can't say it (podman-compose), and the fixtures in tests; it isn't a full YAML
/// parser and never needs to be (no reflection, so the Native AOT shell takes it as it is).
/// </summary>
public static class ComposeYaml
{
    private sealed class Line(int indent, string text)
    {
        public int Indent { get; set; } = indent;

        public string Text { get; set; } = text;
    }

    /// <summary>The document's root, or null when it has none. Throws <see cref="FormatException"/> on what it can't read.</summary>
    public static JsonNode? Parse(string text)
    {
        var raw = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var lines = new List<Line>();
        for (var i = 0; i < raw.Length; i++)
        {
            var line = raw[i].TrimEnd();
            var content = line.TrimStart(' ');
            if (content.Length == 0 || content[0] == '#' || content is "---" or "...")
            {
                continue;
            }

            if (content.StartsWith('\t'))
            {
                throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"Line {i + 1}: a tab in the indentation."));
            }

            var indent = line.Length - content.Length;
            content = StripComment(content);

            // A block scalar (key: | or - >) takes the lines indented more than its own, as they are.
            if (BlockScalarIndicator(content) is not null)
            {
                var block = new StringBuilder();
                var blockIndent = -1;
                while (i + 1 < raw.Length)
                {
                    var next = raw[i + 1].TrimEnd();
                    var nextContent = next.TrimStart(' ');
                    var nextIndent = next.Length - nextContent.Length;
                    if (nextContent.Length > 0 && nextIndent <= indent)
                    {
                        break;
                    }

                    if (nextContent.Length > 0 && blockIndent < 0)
                    {
                        blockIndent = nextIndent;
                    }

                    block.Append(nextContent.Length == 0 ? string.Empty : next[Math.Min(blockIndent, next.Length)..]).Append('\n');
                    i++;
                }

                content += "\u0000" + block.ToString();
            }

            lines.Add(new Line(indent, content));
        }

        var position = 0;
        return lines.Count == 0 ? null : ParseNode(lines, ref position, lines[0].Indent);
    }

    private static string? BlockScalarIndicator(string content)
    {
        while (content.StartsWith("- ", StringComparison.Ordinal))
        {
            content = content[2..].TrimStart();
        }

        var value = SplitKey(content) is { } pair ? pair.Value : content;
        return value is ['|' or '>', ..] && value.Length <= 3 && value.Skip(1).All(c => c is '-' or '+' || char.IsAsciiDigit(c)) ? value : null;
    }

    /// <summary>The text without a comment: a <c>#</c> after whitespace, outside quotes.</summary>
    private static string StripComment(string content)
    {
        char? quote = null;
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (quote is { } open)
            {
                if (c == open)
                {
                    quote = null;
                }
                else if (c == '\\' && open == '"')
                {
                    i++;
                }
            }
            else if ((c is '"' or '\'') && (i == 0 || content[i - 1] is ' ' or '[' or '{' or ',' or ':' or '-'))
            {
                quote = c;
            }
            else if (c == '#' && i > 0 && content[i - 1] is ' ' or '\t')
            {
                return content[..i].TrimEnd();
            }
        }

        return content;
    }

    private static JsonNode? ParseNode(List<Line> lines, ref int position, int minimumIndent)
    {
        if (position >= lines.Count || lines[position].Indent < minimumIndent)
        {
            return null;
        }

        var line = lines[position];
        return IsSequenceItem(line.Text) ? ParseSequence(lines, ref position, line.Indent) : ParseMapping(lines, ref position, line.Indent);
    }

    private static bool IsSequenceItem(string text) => text == "-" || text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("-\u0000", StringComparison.Ordinal);

    private static JsonArray ParseSequence(List<Line> lines, ref int position, int indent)
    {
        var array = new JsonArray();
        while (position < lines.Count && lines[position].Indent == indent && IsSequenceItem(lines[position].Text))
        {
            var line = lines[position];
            var rest = line.Text.Length > 1 ? line.Text[1..] : string.Empty;
            var spaces = rest.Length - rest.TrimStart(' ').Length;
            rest = rest.TrimStart(' ');
            if (rest.Length == 0 || IsAnchorOnly(rest))
            {
                position++;
                array.Add(ParseNode(lines, ref position, indent + 1));
            }
            else if (IsSequenceItem(rest) || (rest[0] is not ('"' or '\'' or '[' or '{' or '\u0000') && SplitKey(rest) is not null))
            {
                // "- key: value" (or "- - item"): the item is a node whose first line starts after the dash.
                line.Indent = indent + 1 + spaces;
                line.Text = rest;
                array.Add(ParseNode(lines, ref position, line.Indent));
            }
            else
            {
                position++;
                array.Add(Scalar(lines, ref position, rest));
            }
        }

        return array;
    }

    private static JsonObject ParseMapping(List<Line> lines, ref int position, int indent)
    {
        var map = new JsonObject();
        while (position < lines.Count && lines[position].Indent == indent && !IsSequenceItem(lines[position].Text))
        {
            var line = lines[position];
            if (SplitKey(line.Text) is not { } pair)
            {
                throw new FormatException($"'{line.Text}' isn't a key and a value.");
            }

            position++;
            JsonNode? value;
            if (pair.Value.Length == 0 || IsAnchorOnly(pair.Value))
            {
                // The value is the block below: indented more, or a sequence at the key's own indentation.
                value = position < lines.Count && (lines[position].Indent > indent || (lines[position].Indent == indent && IsSequenceItem(lines[position].Text)))
                    ? ParseNode(lines, ref position, lines[position].Indent)
                    : null;
            }
            else
            {
                value = Scalar(lines, ref position, pair.Value);
            }

            if (pair.Key != "<<")
            {
                map[pair.Key] = value;
            }
        }

        return map;
    }

    /// <summary>An anchor with nothing after it (<c>key: &amp;name</c>): the value is the block below.</summary>
    private static bool IsAnchorOnly(string value) => value.StartsWith('&') && !value.Contains(' ', StringComparison.Ordinal);

    /// <summary>"key: value" (or "key:"), the key unquoted; null when the text isn't one.</summary>
    private static (string Key, string Value)? SplitKey(string text)
    {
        var block = text.IndexOf('\u0000', StringComparison.Ordinal);
        var head = block < 0 ? text : text[..block];
        string key;
        int after;
        if (head.Length > 0 && head[0] is '"' or '\'')
        {
            var end = head.IndexOf(head[0], 1);
            if (end < 0 || end + 1 >= head.Length || head[end + 1] != ':')
            {
                return null;
            }

            key = head[1..end];
            after = end + 2;
        }
        else
        {
            var colon = -1;
            for (var i = 0; i < head.Length; i++)
            {
                if (head[i] == ':' && (i + 1 == head.Length || head[i + 1] == ' '))
                {
                    colon = i;
                    break;
                }

                if (head[i] is '[' or '{' or '"' or '\'' && i == 0)
                {
                    return null;
                }
            }

            if (colon <= 0)
            {
                return null;
            }

            key = head[..colon].Trim();
            after = colon + 1;
        }

        return (key, (text[after..]).TrimStart(' '));
    }

    /// <summary>A scalar, a flow collection (joining the lines it spans), or a block scalar's text.</summary>
    private static JsonNode? Scalar(List<Line> lines, ref int position, string text)
    {
        var block = text.IndexOf('\u0000', StringComparison.Ordinal);
        if (block >= 0)
        {
            var indicator = text[..block].Trim();
            var body = text[(block + 1)..];
            var keep = indicator.Contains('+', StringComparison.Ordinal);
            var strip = indicator.Contains('-', StringComparison.Ordinal);
            if (indicator.StartsWith('>'))
            {
                body = string.Join('\n', body.Split("\n\n")
                    .Select(paragraph => string.Join(' ', paragraph.Split('\n', StringSplitOptions.RemoveEmptyEntries)))) + "\n";
            }

            body = keep ? body : body.TrimEnd('\n') + (strip ? string.Empty : "\n");
            return JsonValue.Create(body);
        }

        if (text.StartsWith('&'))
        {
            // An anchor: the value after it.
            var space = text.IndexOf(' ', StringComparison.Ordinal);
            text = space < 0 ? string.Empty : text[(space + 1)..].TrimStart();
            if (text.Length == 0)
            {
                return null;
            }
        }

        if (text[0] is '[' or '{')
        {
            while (!Balanced(text) && position < lines.Count)
            {
                text += " " + lines[position].Text;
                position++;
            }

            var index = 0;
            var node = Flow(text, ref index);
            return node;
        }

        return Plain(text);
    }

    private static bool Balanced(string text)
    {
        var depth = 0;
        char? quote = null;
        foreach (var c in text)
        {
            if (quote is { } open)
            {
                quote = c == open ? null : quote;
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c is '[' or '{')
            {
                depth++;
            }
            else if (c is ']' or '}')
            {
                depth--;
            }
        }

        return depth <= 0;
    }

    private static JsonNode? Flow(string text, ref int index)
    {
        SkipSpaces(text, ref index);
        if (index >= text.Length)
        {
            return null;
        }

        if (text[index] == '[')
        {
            index++;
            var array = new JsonArray();
            while (true)
            {
                SkipSpaces(text, ref index);
                if (index >= text.Length || text[index] == ']')
                {
                    index++;
                    return array;
                }

                array.Add(Flow(text, ref index));
                SkipSpaces(text, ref index);
                if (index < text.Length && text[index] == ',')
                {
                    index++;
                }
            }
        }

        if (text[index] == '{')
        {
            index++;
            var map = new JsonObject();
            while (true)
            {
                SkipSpaces(text, ref index);
                if (index >= text.Length || text[index] == '}')
                {
                    index++;
                    return map;
                }

                var key = FlowScalar(text, ref index, key: true) ?? string.Empty;
                SkipSpaces(text, ref index);
                JsonNode? value = null;
                if (index < text.Length && text[index] == ':')
                {
                    index++;
                    value = Flow(text, ref index);
                }

                map[key] = value;
                SkipSpaces(text, ref index);
                if (index < text.Length && text[index] == ',')
                {
                    index++;
                }
            }
        }

        return FlowScalar(text, ref index, key: false) is { } scalar ? JsonValue.Create(scalar) : null;
    }

    private static string? FlowScalar(string text, ref int index, bool key)
    {
        if (text[index] is '"' or '\'')
        {
            var start = index;
            index++;
            while (index < text.Length)
            {
                if (text[index] == '\\' && text[start] == '"')
                {
                    index += 2;
                }
                else if (text[index] == '\'' && text[start] == '\'' && index + 1 < text.Length && text[index + 1] == '\'')
                {
                    index += 2; // '' is a quote in a single-quoted scalar
                }
                else if (text[index] == text[start])
                {
                    break;
                }
                else
                {
                    index++;
                }
            }

            index++;
            return Unquote(text[start..Math.Min(index, text.Length)]);
        }

        var begin = index;
        while (index < text.Length && text[index] is not (',' or ']' or '}') && !(key && text[index] == ':' && (index + 1 == text.Length || text[index + 1] is ' ' or ',' or '}')))
        {
            index++;
        }

        var plain = text[begin..index].Trim();
        return plain.Length == 0 || plain is "~" or "null" ? null : plain;
    }

    private static void SkipSpaces(string text, ref int index)
    {
        while (index < text.Length && text[index] == ' ')
        {
            index++;
        }
    }

    private static JsonValue? Plain(string text)
    {
        text = text.Trim();
        if (text is "~" or "null" or "Null" or "NULL")
        {
            return null;
        }

        return JsonValue.Create(text[0] is '"' or '\'' ? Unquote(text) : text);
    }

    private static string Unquote(string text)
    {
        if (text.Length < 2)
        {
            return text;
        }

        var inner = text[1..^1];
        if (text[0] == '\'')
        {
            return inner.Replace("''", "'", StringComparison.Ordinal);
        }

        var builder = new StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length)
            {
                i++;
                builder.Append(inner[i] switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '0' => '\0',
                    _ => inner[i],
                });
            }
            else
            {
                builder.Append(inner[i]);
            }
        }

        return builder.ToString();
    }
}
