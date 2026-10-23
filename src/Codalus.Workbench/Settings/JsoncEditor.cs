using System.Text;

namespace Codalus.Workbench.Settings;

/// <summary>
/// Minimal, comment-preserving edits to the top-level object of a JSONC document (settings.json): set,
/// replace or remove one property. Everything outside the edited member — comments, ordering, formatting —
/// is left untouched. The input must be well-formed JSONC; callers parse it first.
/// </summary>
public static class JsoncEditor
{
    private const string Indent = "    ";

    /// <summary>Sets <paramref name="key"/> to <paramref name="valueJson"/>, or removes it when that is null.</summary>
    public static string SetProperty(string text, string key, string? valueJson)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return valueJson is null ? "{\n}\n" : $"{{\n{Indent}{Quote(key)}: {valueJson}\n}}\n";
        }

        var scanner = new Scanner(text);
        var members = scanner.ReadTopLevelMembers(out var openBrace, out var closeBrace);
        return SetIn(text, members, openBrace, closeBrace, key, valueJson, Indent);
    }

    /// <summary>
    /// Sets <paramref name="member"/> of the object <paramref name="key"/> to <paramref name="valueJson"/>, or removes it
    /// when that is null, leaving the object's other members, comments and formatting as they are (Connect and Disconnect
    /// of one <c>ai.agents</c> entry, ADR 0083). When <paramref name="key"/> isn't there it is added with that one member;
    /// when its value isn't an object, the whole value is replaced, as <see cref="SetProperty"/> does.
    /// </summary>
    public static string SetMember(string text, string key, string member, string? valueJson)
    {
        var whole = valueJson is null ? null : $"{{ {Quote(member)}: {valueJson} }}";
        if (string.IsNullOrWhiteSpace(text))
        {
            return whole is null ? SetProperty(text, key, null) : SetProperty(text, key, whole);
        }

        var scanner = new Scanner(text);
        var members = scanner.ReadTopLevelMembers(out _, out _);
        var existing = members.Find(m => m.Name == key);
        if (existing is null)
        {
            return whole is null ? text : SetProperty(text, key, whole);
        }

        if (text[existing.ValueStart] != '{')
        {
            return SetProperty(text, key, whole ?? "{}");
        }

        var inner = new Scanner(text, existing.ValueStart);
        var innerMembers = inner.ReadObjectMembers(out var open, out var close);

        // The entries' own indentation (the first entry's), else one level deeper than the key's.
        var indent = innerMembers.Count > 0 && LineIndent(text, innerMembers[0].NameStart) is { Length: > 0 } own ? own : LineIndent(text, existing.NameStart) + Indent;
        return SetIn(text, innerMembers, open, close, member, valueJson, indent);
    }

    /// <summary>The whitespace before <paramref name="position"/> on its line (empty when other text is before it).</summary>
    private static string LineIndent(string text, int position)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, position - 1)) + 1;
        var before = text[lineStart..position];
        return before.All(c => c is ' ' or '\t') ? before : string.Empty;
    }

    private static string SetIn(string text, List<Member> members, int openBrace, int closeBrace, string key, string? valueJson, string indent)
    {
        var existing = members.FindIndex(m => m.Name == key);
        if (existing >= 0)
        {
            var member = members[existing];
            if (valueJson is not null)
            {
                return string.Concat(text.AsSpan(0, member.ValueStart), valueJson, text.AsSpan(member.ValueEnd));
            }

            return RemoveMember(text, members, existing, openBrace, closeBrace);
        }

        if (valueJson is null)
        {
            return text;
        }

        var entry = $"{Quote(key)}: {valueJson}";
        if (members.Count == 0)
        {
            // The closing brace one level out from the entries (the top level's at the line's start).
            var inner = text[(openBrace + 1)..closeBrace];
            var closing = indent.Length >= Indent.Length ? indent[..^Indent.Length] : string.Empty;
            var body = (inner.Trim().Length == 0 ? string.Empty : inner.TrimEnd()) + $"\n{indent}{entry}\n{closing}";
            return string.Concat(text.AsSpan(0, openBrace + 1), body, text.AsSpan(closeBrace));
        }

        var last = members[^1];
        var afterLast = last.ValueEnd;
        if (last.CommaEnd > 0)
        {
            // Trailing comma already present (allowed in JSONC): insert after it.
            afterLast = last.CommaEnd;
            return string.Concat(text.AsSpan(0, afterLast), $"\n{indent}{entry},", text.AsSpan(afterLast));
        }

        return string.Concat(text.AsSpan(0, afterLast), $",\n{indent}{entry}", text.AsSpan(afterLast));
    }

    private static string RemoveMember(string text, List<Member> members, int index, int openBrace, int closeBrace)
    {
        var member = members[index];

        // Remove from the start of the member's line (if it only has whitespace before it) through its comma.
        var start = member.NameStart;
        var lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        if (lineStart > openBrace && text.AsSpan(lineStart, start - lineStart).IsWhiteSpace())
        {
            start = lineStart;
        }

        var end = member.CommaEnd > 0 ? member.CommaEnd : member.ValueEnd;
        var lineEnd = text.IndexOf('\n', end);
        if (lineEnd >= 0 && lineEnd < closeBrace && text.AsSpan(end, lineEnd - end).IsWhiteSpace())
        {
            end = lineEnd + 1;
        }

        var result = string.Concat(text.AsSpan(0, start), text.AsSpan(end));

        // Removing the last member leaves a dangling comma on the previous one.
        if (member.CommaEnd == 0 && index > 0 && members[index - 1].CommaEnd > 0)
        {
            var comma = members[index - 1].CommaEnd - 1;
            result = result.Remove(comma, 1);
        }

        return result;
    }

    private static string Quote(string key)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in key)
        {
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                _ => c.ToString(),
            });
        }

        return sb.Append('"').ToString();
    }

    private sealed record Member(string Name, int NameStart, int ValueStart, int ValueEnd, int CommaEnd);

    private sealed class Scanner(string text, int start = 0)
    {
        private int _pos = start;

        public List<Member> ReadTopLevelMembers(out int openBrace, out int closeBrace)
        {
            SkipTrivia();
            if (_pos >= text.Length || text[_pos] != '{')
            {
                throw new FormatException("settings.json must contain a JSON object.");
            }

            return ReadObjectMembers(out openBrace, out closeBrace);
        }

        /// <summary>The members of the object at the current position (its <c>{</c>).</summary>
        public List<Member> ReadObjectMembers(out int openBrace, out int closeBrace)
        {
            SkipTrivia();
            if (_pos >= text.Length || text[_pos] != '{')
            {
                throw new FormatException($"Expected an object at {_pos}.");
            }

            openBrace = _pos++;
            var members = new List<Member>();
            while (true)
            {
                SkipTrivia();
                if (_pos >= text.Length)
                {
                    throw new FormatException("Unterminated object.");
                }

                if (text[_pos] == '}')
                {
                    closeBrace = _pos;
                    return members;
                }

                var nameStart = _pos;
                var name = ReadString();
                SkipTrivia();
                Expect(':');
                SkipTrivia();
                var valueStart = _pos;
                SkipValue();
                var valueEnd = _pos;
                SkipTrivia();
                var commaEnd = 0;
                if (_pos < text.Length && text[_pos] == ',')
                {
                    commaEnd = ++_pos;
                }

                members.Add(new Member(name, nameStart, valueStart, valueEnd, commaEnd));
            }
        }

        private void SkipValue()
        {
            if (_pos >= text.Length)
            {
                throw new FormatException("Missing value.");
            }

            switch (text[_pos])
            {
                case '"':
                    ReadString();
                    return;
                case '{' or '[':
                    var depth = 0;
                    do
                    {
                        SkipTrivia();
                        var c = text[_pos];
                        if (c == '"')
                        {
                            ReadString();
                            continue;
                        }

                        if (c is '{' or '[')
                        {
                            depth++;
                        }
                        else if (c is '}' or ']')
                        {
                            depth--;
                        }

                        _pos++;
                    }
                    while (depth > 0 && _pos < text.Length);
                    return;
                default:
                    while (_pos < text.Length && text[_pos] is not (',' or '}' or ']' or '/') && !char.IsWhiteSpace(text[_pos]))
                    {
                        _pos++;
                    }

                    return;
            }
        }

        private string ReadString()
        {
            Expect('"');
            var sb = new StringBuilder();
            while (_pos < text.Length && text[_pos] != '"')
            {
                if (text[_pos] == '\\' && _pos + 1 < text.Length)
                {
                    _pos++;
                    sb.Append(text[_pos] switch { 'n' => '\n', 't' => '\t', var other => other });
                }
                else
                {
                    sb.Append(text[_pos]);
                }

                _pos++;
            }

            Expect('"');
            return sb.ToString();
        }

        private void Expect(char c)
        {
            if (_pos >= text.Length || text[_pos] != c)
            {
                throw new FormatException($"Expected '{c}' at {_pos}.");
            }

            _pos++;
        }

        private void SkipTrivia()
        {
            while (_pos < text.Length)
            {
                if (char.IsWhiteSpace(text[_pos]))
                {
                    _pos++;
                }
                else if (text.AsSpan(_pos).StartsWith("//"))
                {
                    var end = text.IndexOf('\n', _pos);
                    _pos = end < 0 ? text.Length : end + 1;
                }
                else if (text.AsSpan(_pos).StartsWith("/*"))
                {
                    var end = text.IndexOf("*/", _pos + 2, StringComparison.Ordinal);
                    _pos = end < 0 ? text.Length : end + 2;
                }
                else
                {
                    return;
                }
            }
        }
    }
}
