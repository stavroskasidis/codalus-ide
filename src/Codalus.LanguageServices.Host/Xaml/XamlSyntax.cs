namespace Codalus.LanguageServices.Host.Xaml;

/// <summary>An attribute: its name (<c>prefix:Local</c> or <c>Owner.Property</c>), its value and their offsets.</summary>
internal sealed class XamlAttribute
{
    public required string Name { get; init; }

    public required int NameStart { get; init; }

    public int NameEnd => NameStart + Name.Length;

    /// <summary>The value without its quotes, or null when the attribute has no value (yet).</summary>
    public string? Value { get; set; }

    /// <summary>The offset after the opening quote.</summary>
    public int ValueStart { get; set; } = -1;

    /// <summary>The offset of the closing quote (or where the value stops when it is unterminated).</summary>
    public int ValueEnd { get; set; } = -1;

    public bool Closed { get; set; }

    public string? Prefix => Name.IndexOf(':') is var i and > 0 ? Name[..i] : null;

    public string LocalName => Name.IndexOf(':') is var i and >= 0 ? Name[(i + 1)..] : Name;

    public bool ContainsValue(int offset) => ValueStart >= 0 && offset >= ValueStart && offset <= ValueEnd;
}

/// <summary>An element or a property element (<c>&lt;Grid.RowDefinitions&gt;</c>) with its offsets.</summary>
internal sealed class XamlElement
{
    public required string Name { get; init; }

    /// <summary>The offset of its <c>&lt;</c>.</summary>
    public required int Start { get; init; }

    public int NameStart => Start + 1;

    public int NameEnd => NameStart + Name.Length;

    /// <summary>The offset after its start tag's <c>&gt;</c>, or -1 while the tag is unfinished.</summary>
    public int StartTagEnd { get; set; } = -1;

    /// <summary>The offset after its end tag (or its <c>/&gt;</c>); the text's length while it is unclosed.</summary>
    public int End { get; set; } = -1;

    public bool SelfClosing { get; set; }

    /// <summary>The offset of its end tag's name, or -1.</summary>
    public int EndTagNameStart { get; set; } = -1;

    public bool Closed { get; set; }

    public XamlElement? Parent { get; init; }

    public List<XamlAttribute> Attributes { get; } = [];

    public List<XamlElement> Children { get; } = [];

    /// <summary>Text content directly inside (trimmed), for <c>&lt;x:Double&gt;8&lt;/x:Double&gt;</c> and the like.</summary>
    public string? Text { get; set; }

    public string? Prefix => Name.IndexOf(':') is var i and > 0 ? Name[..i] : null;

    /// <summary>The name without its prefix (<c>Grid</c>, <c>Grid.RowDefinitions</c>).</summary>
    public string LocalName => Name.IndexOf(':') is var i and >= 0 ? Name[(i + 1)..] : Name;

    public bool IsPropertyElement => LocalName.Contains('.', StringComparison.Ordinal);

    public XamlAttribute? Attribute(string name) => Attributes.FirstOrDefault(a => a.Name == name);

    /// <summary>The element and its descendants, in document order.</summary>
    public IEnumerable<XamlElement> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var descendant in child.DescendantsAndSelf())
            {
                yield return descendant;
            }
        }
    }

    public IEnumerable<XamlElement> Ancestors()
    {
        for (var current = Parent; current is not null; current = current.Parent)
        {
            yield return current;
        }
    }
}

internal sealed record XamlSyntaxError(int Start, int End, string Message);

/// <summary>
/// A tolerant XAML parser (M19a, ADR 0047): elements, attributes and their offsets from text being typed, with the
/// errors a strict XML parser would stop at. It never throws; unclosed tags and elements end at the text's end.
/// </summary>
internal sealed class XamlDocument
{
    private XamlDocument(string text) => Text = text;

    public string Text { get; }

    public XamlElement? Root { get; private set; }

    public List<XamlSyntaxError> Errors { get; } = [];

    public IEnumerable<XamlElement> Elements => Root?.DescendantsAndSelf() ?? [];

    public static XamlDocument Parse(string text)
    {
        var document = new XamlDocument(text);
        document.Run();
        return document;
    }

    private void Run()
    {
        var text = Text;
        var stack = new Stack<XamlElement>();
        var i = 0;
        var textStart = 0;
        while (i < text.Length)
        {
            if (text[i] != '<')
            {
                i++;
                continue;
            }

            if (stack.Count > 0 && i > textStart)
            {
                AppendText(stack.Peek(), text[textStart..i]);
            }

            if (Starts(i, "<!--"))
            {
                var end = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                if (end < 0)
                {
                    Errors.Add(new XamlSyntaxError(i, text.Length, "The comment isn't closed (-->)."));
                    i = text.Length;
                }
                else
                {
                    i = end + 3;
                }
            }
            else if (Starts(i, "<![CDATA["))
            {
                var end = text.IndexOf("]]>", i, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 3;
            }
            else if (Starts(i, "<?") || Starts(i, "<!"))
            {
                var end = text.IndexOf('>', i);
                i = end < 0 ? text.Length : end + 1;
            }
            else if (Starts(i, "</"))
            {
                i = EndTag(i, stack);
            }
            else
            {
                i = StartTag(i, stack);
            }

            textStart = i;
        }

        while (stack.Count > 0)
        {
            var open = stack.Pop();
            open.End = text.Length;
            Errors.Add(new XamlSyntaxError(open.Start, open.NameEnd, $"The element '{open.Name}' isn't closed."));
        }
    }

    private static void AppendText(XamlElement element, string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length > 0)
        {
            element.Text = element.Text is null ? trimmed : element.Text + " " + trimmed;
        }
    }

    private bool Starts(int offset, string value) => string.CompareOrdinal(Text, offset, value, 0, value.Length) == 0;

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or ':' or '.' or '-';

    private int ReadName(int offset)
    {
        var end = offset;
        while (end < Text.Length && IsNameChar(Text[end]))
        {
            end++;
        }

        return end;
    }

    private int StartTag(int start, Stack<XamlElement> stack)
    {
        var text = Text;
        var nameEnd = ReadName(start + 1);
        var element = new XamlElement { Name = text[(start + 1)..nameEnd], Start = start, Parent = stack.Count > 0 ? stack.Peek() : null };
        if (element.Parent is { } parent)
        {
            parent.Children.Add(element);
        }
        else if (Root is null)
        {
            Root = element;
        }
        else
        {
            Errors.Add(new XamlSyntaxError(start, nameEnd, "A XAML file has one root element."));
        }

        if (element.Name.Length == 0)
        {
            Errors.Add(new XamlSyntaxError(start, start + 1, "An element name is expected after '<'."));
        }

        var i = nameEnd;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '>')
            {
                element.StartTagEnd = i + 1;
                stack.Push(element);
                return i + 1;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '>')
            {
                element.StartTagEnd = i + 2;
                element.End = i + 2;
                element.SelfClosing = true;
                element.Closed = true;
                return i + 2;
            }

            if (c == '<')
            {
                // A new tag before this one ended: the user is typing in it.
                Errors.Add(new XamlSyntaxError(start, nameEnd, $"The start tag of '{element.Name}' isn't finished ('>' expected)."));
                stack.Push(element);
                return i;
            }

            if (!IsNameChar(c))
            {
                Errors.Add(new XamlSyntaxError(i, i + 1, $"Unexpected '{c}' in the tag of '{element.Name}'."));
                i++;
                continue;
            }

            var attributeEnd = ReadName(i);
            var attribute = new XamlAttribute { Name = text[i..attributeEnd], NameStart = i };
            element.Attributes.Add(attribute);
            i = attributeEnd;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i >= text.Length || text[i] != '=')
            {
                Errors.Add(new XamlSyntaxError(attribute.NameStart, attribute.NameEnd, $"The attribute '{attribute.Name}' has no value."));
                continue;
            }

            i++;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i >= text.Length || text[i] is not ('"' or '\''))
            {
                Errors.Add(new XamlSyntaxError(attribute.NameStart, attribute.NameEnd, $"The value of '{attribute.Name}' must be quoted."));
                continue;
            }

            var quote = text[i];
            attribute.ValueStart = i + 1;
            var close = text.IndexOf(quote, i + 1);
            var lessThan = text.IndexOf('<', i + 1);
            if (close < 0 || (lessThan >= 0 && lessThan < close))
            {
                // An unterminated value (the user is typing it) runs to the end of its line or the next tag.
                var newline = text.IndexOf('\n', i + 1);
                var stop = new[] { newline, lessThan, text.Length }.Where(x => x >= 0).Min();
                attribute.ValueEnd = stop;
                attribute.Value = text[attribute.ValueStart..stop];
                Errors.Add(new XamlSyntaxError(attribute.NameStart, stop, $"The value of '{attribute.Name}' isn't closed ({quote})."));
                i = stop;
                continue;
            }
            attribute.ValueEnd = close;
            attribute.Value = text[attribute.ValueStart..close];
            attribute.Closed = true;
            i = close + 1;
        }

        Errors.Add(new XamlSyntaxError(start, nameEnd, $"The start tag of '{element.Name}' isn't finished ('>' expected)."));
        element.End = text.Length;
        stack.Push(element);
        return text.Length;
    }

    private int EndTag(int start, Stack<XamlElement> stack)
    {
        var text = Text;
        var nameStart = start + 2;
        var nameEnd = ReadName(nameStart);
        var name = text[nameStart..nameEnd];
        var close = text.IndexOf('>', nameEnd);
        var next = text.IndexOf('<', nameEnd);
        var end = close < 0 || (next >= 0 && next < close) ? nameEnd : close + 1;
        if (end == nameEnd)
        {
            Errors.Add(new XamlSyntaxError(start, nameEnd, $"The end tag '{name}' isn't finished ('>' expected)."));
        }

        if (!stack.Any(e => e.Name == name))
        {
            Errors.Add(new XamlSyntaxError(start, Math.Max(nameEnd, start + 2), stack.Count > 0
                ? $"The end tag '{name}' doesn't match the open element '{stack.Peek().Name}'."
                : $"The end tag '{name}' has no start tag."));
            return end;
        }

        while (stack.Count > 0)
        {
            var open = stack.Pop();
            if (open.Name == name)
            {
                open.End = end;
                open.EndTagNameStart = nameStart;
                open.Closed = true;
                return end;
            }

            open.End = start;
            Errors.Add(new XamlSyntaxError(open.Start, open.NameEnd, $"The element '{open.Name}' isn't closed."));
        }

        return end;
    }

    // ------------------------------------------------------------------ lookup

    /// <summary>The innermost element whose start tag or content contains <paramref name="offset"/>.</summary>
    public XamlElement? ElementAt(int offset)
    {
        XamlElement? found = null;
        var current = Root;
        while (current is not null && offset >= current.Start && offset <= (current.End < 0 ? Text.Length : current.End))
        {
            found = current;
            current = current.Children.FirstOrDefault(c => offset > c.Start && offset <= (c.End < 0 ? Text.Length : c.End));
        }

        return found;
    }

    /// <summary>The namespace a prefix maps to at <paramref name="element"/> (the empty prefix: the default namespace).</summary>
    public static string? ResolvePrefix(XamlElement element, string? prefix)
    {
        var name = string.IsNullOrEmpty(prefix) ? "xmlns" : "xmlns:" + prefix;
        for (var current = element; current is not null; current = current.Parent)
        {
            if (current.Attribute(name) is { Value: { } value })
            {
                return value;
            }
        }

        return prefix switch
        {
            "x" => XamlNames.XamlLanguage,
            "xml" => XamlNames.Xml,
            _ => null,
        };
    }

    /// <summary>Every prefix declared at <paramref name="element"/> or above, with its namespace (the closest wins).</summary>
    public static IReadOnlyDictionary<string, string> Prefixes(XamlElement element)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var current = element; current is not null; current = current.Parent)
        {
            foreach (var attribute in current.Attributes)
            {
                if (attribute.Name.StartsWith("xmlns:", StringComparison.Ordinal) && attribute.Value is { } value)
                {
                    result.TryAdd(attribute.Name[6..], value);
                }
            }
        }

        return result;
    }
}

/// <summary>The well-known XAML namespaces.</summary>
internal static class XamlNames
{
    public const string Avalonia = "https://github.com/avaloniaui";

    /// <summary>.NET MAUI's default XML namespace (M20d).</summary>
    public const string Maui = "http://schemas.microsoft.com/dotnet/2021/maui";
    public const string XamlLanguage = "http://schemas.microsoft.com/winfx/2006/xaml";
    public const string Design = "http://schemas.microsoft.com/expression/blend/2008";
    public const string MarkupCompatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    public const string Xml = "http://www.w3.org/XML/1998/namespace";
}
