using System.Globalization;
using System.Text;

namespace Codalus.Workbench.Previewer;

public enum XamlBreakpointKind
{
    /// <summary>On an element's start tag: stops when the element is created.</summary>
    Element,

    /// <summary>On an attribute (or a property element): stops when XAML sets its property.</summary>
    Attribute,

    /// <summary>Nothing a XAML breakpoint can stop at (<see cref="XamlBreakpointTarget.Problem"/> says why).</summary>
    None,
}

/// <summary>
/// What a XAML breakpoint on a line stops at (M64b): the element whose start tag is on the line (its name's 1-based
/// line and column, as Avalonia's source information gives an element's), or the property an attribute on the line
/// sets on its element. <see cref="Binding"/>: the attribute's value is a binding or a dynamic resource, which sets the
/// property again later.
/// </summary>
public sealed record XamlBreakpointTarget(XamlBreakpointKind Kind, int Line, int Column, string Element, string Property, bool Binding, string? Problem);

/// <summary>
/// XAML breakpoints (M64b, ADR 0103): what a breakpoint on a line of a XAML file is, and the text the live agent reads
/// them from (the app's environment at launch, and its <c>xbreaks</c> request). A line where an element's start tag
/// begins is an element breakpoint (the first element on it); a line with only attributes of a start tag that began
/// above is an attribute breakpoint (its first attribute); a property element (<c>&lt;ListBox.ItemTemplate&gt;</c>) is
/// its parent's property. A tolerant scanner, not a parser: an unfinished edit doesn't stop it.
/// </summary>
public static class XamlBreakpoints
{
    /// <summary>Whether a file's breakpoints are XAML breakpoints (the live agent's, not the debugger's).</summary>
    public static bool IsXamlFile(string path) =>
        path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);

    private sealed record Attribute(string Name, int Line, int Column, string Value);

    private sealed class Tag
    {
        public required string Name { get; init; }

        public required int Line { get; init; }

        public required int Column { get; init; }

        public Tag? Parent { get; init; }

        public List<Attribute> Attributes { get; } = [];
    }

    /// <summary>The target of a breakpoint on <paramref name="line"/> (1-based) of the XAML text.</summary>
    public static XamlBreakpointTarget Resolve(string text, int line)
    {
        var tags = Scan(text);
        if (tags.FirstOrDefault(t => t.Line == line) is { } start)
        {
            var dot = start.Name.IndexOf('.', StringComparison.Ordinal);
            if (dot > 0 && start.Parent is { } owner)
            {
                // <ListBox.ItemTemplate>: its parent's property.
                var local = LocalName(start.Name);
                var ownerType = LocalName(start.Name[..dot]);
                var property = LocalName(owner.Name) == ownerType ? local[(local.IndexOf('.', StringComparison.Ordinal) + 1)..] : local;
                return new XamlBreakpointTarget(XamlBreakpointKind.Attribute, owner.Line, owner.Column, LocalName(owner.Name), property, false, null);
            }

            return new XamlBreakpointTarget(XamlBreakpointKind.Element, start.Line, start.Column, LocalName(start.Name), string.Empty, false, null);
        }

        foreach (var tag in tags)
        {
            if (tag.Attributes.FirstOrDefault(a => a.Line == line) is not { } attribute)
            {
                continue;
            }

            var (property, problem) = PropertyOf(attribute.Name);
            return new XamlBreakpointTarget(problem is null ? XamlBreakpointKind.Attribute : XamlBreakpointKind.None, tag.Line, tag.Column, LocalName(tag.Name), property,
                IsBinding(attribute.Value), problem);
        }

        return new XamlBreakpointTarget(XamlBreakpointKind.None, line, 0, string.Empty, string.Empty, false,
            "No element or attribute starts on this line: put a XAML breakpoint on an element's start tag or on an attribute's line.");
    }

    private static string LocalName(string name) => name[(name.IndexOf(':', StringComparison.Ordinal) + 1)..];

    /// <summary>The property an attribute sets: <c>Text</c>, <c>DockPanel.Dock</c>, <c>Name</c> for <c>x:Name</c>; or why none.</summary>
    private static (string Property, string? Problem) PropertyOf(string name)
    {
        if (name == "x:Name")
        {
            return ("Name", null);
        }

        if (name == "xmlns" || name.StartsWith("xmlns:", StringComparison.Ordinal))
        {
            return (name, "A namespace declaration sets no property.");
        }

        var colon = name.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0)
        {
            var prefix = name[..colon];
            var local = name[(colon + 1)..];
            if (prefix is "x" or "d" or "mc" && !local.Contains('.', StringComparison.Ordinal))
            {
                return (name, $"{name} is a directive or a design-time attribute: the running app sets no property for it.");
            }

            return (local, null);
        }

        return (name, null);
    }

    /// <summary>A value that sets the property again later: a binding or a dynamic resource.</summary>
    private static bool IsBinding(string value)
    {
        var trimmed = value.TrimStart();
        if (!trimmed.StartsWith('{') || trimmed.StartsWith("{}", StringComparison.Ordinal))
        {
            return false;
        }

        var name = new string([.. trimmed[1..].TakeWhile(c => !char.IsWhiteSpace(c) && c != '}' && c != ',')]);
        name = LocalName(name);
        return name is "Binding" or "CompiledBinding" or "ReflectionBinding" or "TemplateBinding" or "DynamicResource" or "MultiBinding";
    }

    /// <summary>The start tags of the text, in order, with their attributes and parents (comments, CDATA and instructions skipped).</summary>
    private static List<Tag> Scan(string text)
    {
        var tags = new List<Tag>();
        var stack = new Stack<Tag>();
        var line = 1;
        var lineStart = 0;
        var i = 0;

        void Advance(int to)
        {
            for (; i < to && i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    lineStart = i + 1;
                }
            }
        }

        while (i < text.Length)
        {
            var open = text.IndexOf('<', i);
            if (open < 0)
            {
                break;
            }

            Advance(open);
            if (string.CompareOrdinal(text, i, "<!--", 0, 4) == 0)
            {
                var end = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                Advance(end < 0 ? text.Length : end + 3);
                continue;
            }

            if (string.CompareOrdinal(text, i, "<![CDATA[", 0, 9) == 0)
            {
                var end = text.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                Advance(end < 0 ? text.Length : end + 3);
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] is '?' or '!')
            {
                var end = text.IndexOf('>', i + 1);
                Advance(end < 0 ? text.Length : end + 1);
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == '/')
            {
                // An end tag: its element and any left unclosed inside it are done.
                var end = text.IndexOf('>', i + 2);
                var closing = text[(i + 2)..(end < 0 ? text.Length : end)].Trim();
                while (stack.Count > 0 && stack.Pop().Name != closing)
                {
                }

                Advance(end < 0 ? text.Length : end + 1);
                continue;
            }

            // A start tag: its name, then its attributes up to '>' or '/>'.
            Advance(i + 1);
            var nameStart = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '>' && text[i] != '/' && text[i] != '<')
            {
                i++;
            }

            var tag = new Tag
            {
                Name = text[nameStart..i],
                Line = line,
                Column = nameStart - lineStart + 1,
                Parent = stack.Count > 0 ? stack.Peek() : null,
            };
            if (tag.Name.Length == 0)
            {
                continue;
            }

            tags.Add(tag);
            var selfClosing = false;
            while (i < text.Length)
            {
                var c = text[i];
                if (c == '\n')
                {
                    Advance(i + 1);
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                if (c == '>')
                {
                    i++;
                    break;
                }

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '>')
                {
                    selfClosing = true;
                    i += 2;
                    break;
                }

                if (c == '<')
                {
                    break; // an unfinished tag: the next one starts here
                }

                var attributeStart = i;
                var attributeLine = line;
                var attributeColumn = i - lineStart + 1;
                while (i < text.Length && text[i] != '=' && !char.IsWhiteSpace(text[i]) && text[i] != '>' && text[i] != '/' && text[i] != '<')
                {
                    i++;
                }

                var name = text[attributeStart..i];
                while (i < text.Length && char.IsWhiteSpace(text[i]) && text[i] != '\n')
                {
                    i++;
                }

                var value = string.Empty;
                if (i < text.Length && text[i] == '=')
                {
                    i++;
                    while (i < text.Length && char.IsWhiteSpace(text[i]))
                    {
                        Advance(i + 1);
                    }

                    if (i < text.Length && text[i] is '"' or '\'')
                    {
                        var quote = text[i];
                        var end = text.IndexOf(quote, i + 1);
                        var valueEnd = end < 0 ? text.Length : end;
                        value = text[(i + 1)..valueEnd];
                        Advance(end < 0 ? text.Length : end + 1);
                    }
                }

                if (name.Length > 0)
                {
                    tag.Attributes.Add(new Attribute(name, attributeLine, attributeColumn, value));
                }
                else
                {
                    i++;
                }
            }

            if (!selfClosing)
            {
                stack.Push(tag);
            }
        }

        return tags;
    }

    // ------------------------------------------------------------------ the agent's text

    /// <summary>A breakpoint as the live agent gets it.</summary>
    public sealed record AgentBreakpoint(string Id, string File, XamlBreakpointTarget Target);

    /// <summary>
    /// The agent's text for the breakpoints (<c>bindings 1|0</c>, then id, kind, file, line, column, property, binding
    /// per breakpoint), base64: the launch's <c>CODALUS_LIVE_AGENT_BREAKPOINTS</c> and the <c>xbreaks</c> request.
    /// </summary>
    public static string Encode(IEnumerable<AgentBreakpoint> breakpoints, bool breakOnFailedBinding)
    {
        var text = new StringBuilder().Append("bindings\t").Append(breakOnFailedBinding ? '1' : '0').Append('\n');
        foreach (var (id, file, target) in breakpoints)
        {
            if (target.Kind == XamlBreakpointKind.None)
            {
                continue;
            }

            text.Append("bp\t").Append(Clean(id)).Append('\t').Append(target.Kind == XamlBreakpointKind.Element ? "element" : "attribute").Append('\t')
                .Append(Clean(file)).Append('\t').Append(target.Line.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(target.Column.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(Clean(target.Property)).Append('\t')
                .Append(target.Binding ? '1' : '0').Append('\n');
        }

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text.ToString()));
    }

    private static string Clean(string text) => text.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
}
