namespace DaedalusStudio.Markup.Host.Json;

internal enum JsonCursorKind
{
    /// <summary>Where a property name goes (inside an object, before its colon).</summary>
    PropertyName,

    /// <summary>Where a value goes (after a colon, or in an array).</summary>
    Value,

    None,
}

/// <summary>
/// What is at a position in JSON being edited: the path of the enclosing value (property names and array indexes),
/// whether a property name or a value goes there, the span completion replaces, and the keys the object already has.
/// </summary>
internal sealed record JsonCursor(
    JsonCursorKind Kind,
    IReadOnlyList<string> Path,
    int ReplaceStart,
    int ReplaceEnd,
    bool InString,
    string Prefix,
    IReadOnlySet<string> ExistingKeys)
{
    /// <summary>Scans <paramref name="text"/> tolerantly (it may not parse) up to <paramref name="offset"/>.</summary>
    public static JsonCursor Locate(string text, int offset)
    {
        var frames = new List<Frame>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                if (i >= offset)
                {
                    break;
                }

                i++;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] is '/' or '*')
            {
                var end = text[i + 1] == '/' ? text.IndexOf('\n', i) : text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : text[i + 1] == '/' ? end : end + 2;
                if (offset > i && offset <= end)
                {
                    return None(frames);
                }

                i = end;
                continue;
            }

            if (i >= offset)
            {
                break;
            }

            var top = frames.Count > 0 ? frames[^1] : null;
            switch (c)
            {
                case '{':
                    frames.Add(new Frame(isObject: true, parentKey: top?.KeyForChild()));
                    i++;
                    continue;
                case '[':
                    frames.Add(new Frame(isObject: false, parentKey: top?.KeyForChild()));
                    i++;
                    continue;
                case '}' or ']':
                    if (frames.Count > 0)
                    {
                        frames.RemoveAt(frames.Count - 1);
                    }

                    if (frames.Count > 0)
                    {
                        frames[^1].ValueDone();
                    }

                    i++;
                    continue;
                case ':':
                    if (top is { IsObject: true })
                    {
                        top.ExpectingKey = false;
                    }

                    i++;
                    continue;
                case ',':
                    top?.Next();
                    i++;
                    continue;
                case '"':
                    var close = StringEnd(text, i);
                    var stringEnd = close < 0 ? text.Length : close + 1;
                    if (offset > i && (close < 0 || offset <= close))
                    {
                        // Inside this string: completing a key or a string value.
                        var kind = top is { IsObject: true, ExpectingKey: true } ? JsonCursorKind.PropertyName : JsonCursorKind.Value;
                        var contentEnd = close < 0 ? LineEnd(text, i) : close;
                        return new JsonCursor(kind, BuildPath(frames, kind), i + 1, Math.Max(offset, contentEnd), true, text[(i + 1)..offset], Keys(text, frames, close < 0 ? text.Length : close + 1, expectingKey: false));
                    }

                    if (top is { IsObject: true, ExpectingKey: true })
                    {
                        top.Key = Unescape(text[(i + 1)..Math.Max(i + 1, stringEnd - 1)]);
                    }
                    else
                    {
                        top?.ValueDone();
                    }

                    i = stringEnd;
                    continue;
                default:
                    // A literal or number: skip it, unless the position is inside it (a value being typed).
                    var wordEnd = i;
                    while (wordEnd < text.Length && !char.IsWhiteSpace(text[wordEnd]) && text[wordEnd] is not (',' or '}' or ']' or ':' or '"'))
                    {
                        wordEnd++;
                    }

                    if (offset > i && offset <= wordEnd)
                    {
                        var kind = top is { IsObject: true, ExpectingKey: true } ? JsonCursorKind.PropertyName : JsonCursorKind.Value;
                        return new JsonCursor(kind, BuildPath(frames, kind), i, wordEnd, false, text[i..offset], Keys(text, frames, wordEnd, expectingKey: false));
                    }

                    top?.ValueDone();
                    i = Math.Max(wordEnd, i + 1);
                    continue;
            }
        }

        // Between tokens.
        var current = frames.Count > 0 ? frames[^1] : null;
        if (current is null)
        {
            return None(frames);
        }

        var cursorKind = current is { IsObject: true, ExpectingKey: true } ? JsonCursorKind.PropertyName
            : current.IsObject && current.HasValue ? JsonCursorKind.None
            : JsonCursorKind.Value;
        return new JsonCursor(cursorKind, BuildPath(frames, cursorKind), offset, offset, false, string.Empty, Keys(text, frames, offset, current.IsObject && current.ExpectingKey));
    }

    private static JsonCursor None(List<Frame> frames) => new(JsonCursorKind.None, BuildPath(frames, JsonCursorKind.None), 0, 0, false, string.Empty, new HashSet<string>());

    /// <summary>The path to the object (for a property name) or to the value being completed.</summary>
    private static List<string> BuildPath(List<Frame> frames, JsonCursorKind kind)
    {
        var path = new List<string>();
        for (var f = 0; f < frames.Count; f++)
        {
            if (f > 0)
            {
                path.Add(frames[f].ParentKey ?? "0");
            }
        }

        if (kind == JsonCursorKind.Value && frames.Count > 0)
        {
            path.Add(frames[^1].IsObject ? frames[^1].Key ?? string.Empty : frames[^1].Index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return path;
    }

    /// <summary>The keys of the innermost object, before and after the position.</summary>
    private static HashSet<string> Keys(string text, List<Frame> frames, int from, bool expectingKey)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (frames.Count == 0 || !frames[^1].IsObject)
        {
            return keys;
        }

        foreach (var key in frames[^1].Keys)
        {
            keys.Add(key);
        }

        // Scan forward to the end of this object for the keys that follow.
        var depth = 0;
        for (var i = from; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                var close = StringEnd(text, i);
                if (close < 0)
                {
                    break;
                }

                if (depth == 0 && expectingKey)
                {
                    keys.Add(Unescape(text[(i + 1)..close]));
                }

                i = close;
            }
            else if (c is '{' or '[')
            {
                depth++;
            }
            else if (c is '}' or ']')
            {
                if (depth == 0)
                {
                    break;
                }

                depth--;
            }
            else if (depth == 0 && c == ',')
            {
                expectingKey = true;
            }
            else if (depth == 0 && c == ':')
            {
                expectingKey = false;
            }
        }

        return keys;
    }

    private static int StringEnd(string text, int open)
    {
        for (var i = open + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
            }
            else if (text[i] == '"')
            {
                return i;
            }
            else if (text[i] == '\n')
            {
                return -1;
            }
        }

        return -1;
    }

    private static int LineEnd(string text, int from)
    {
        var end = text.IndexOf('\n', from);
        return end < 0 ? text.Length : end;
    }

    private static string Unescape(string raw) => raw.Contains('\\', StringComparison.Ordinal) ? System.Text.RegularExpressions.Regex.Unescape(raw) : raw;

    private sealed class Frame(bool isObject, string? parentKey)
    {
        public bool IsObject { get; } = isObject;

        public string? ParentKey { get; } = parentKey;

        public bool ExpectingKey { get; set; } = isObject;

        public bool HasValue { get; private set; }

        public string? Key { get; set; }

        public int Index { get; private set; }

        public List<string> Keys { get; } = [];

        /// <summary>The path segment of a child container opened now.</summary>
        public string KeyForChild() => IsObject ? Key ?? string.Empty : Index.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public void ValueDone() => HasValue = true;

        public void Next()
        {
            if (IsObject)
            {
                if (Key is not null)
                {
                    Keys.Add(Key);
                }

                Key = null;
                ExpectingKey = true;
            }
            else
            {
                Index++;
            }

            HasValue = false;
        }
    }
}
