using System.Text.RegularExpressions;

namespace Codalus.Workbench.Previewer;

/// <summary>Where in a XAML file a failed binding is declared: the file, and the 0-based line and column of its path.</summary>
public sealed record XamlBindingLocation(string Path, int Line, int Column);

/// <summary>
/// Finds the XAML of a binding that failed at run time (M64, ADR 0103): an attribute named as the binding's target
/// property whose <c>{Binding}</c> (or <c>CompiledBinding</c>, <c>ReflectionBinding</c>) has the failed path. The files
/// whose <c>x:Class</c> is a class the element sits in come first, nearest first (Avalonia's agent names them), then
/// the others; within a file, an element of the failed element's type and name wins.
/// </summary>
public static partial class XamlBindingLocator
{
    public static XamlBindingLocation? Find(LiveBindingFailure failure, IReadOnlyList<(string Path, string Text)> files)
    {
        var type = failure.ElementType[(failure.ElementType.LastIndexOf('.') + 1)..];
        var ordered = files
            .Select(f => (File: f, Rank: Rank(f.Text, failure.Ancestors)))
            .OrderBy(f => f.Rank)
            .ThenBy(f => f.File.Path, StringComparer.Ordinal)
            .Select(f => f.File);
        foreach (var (path, text) in ordered)
        {
            XamlBindingLocation? best = null;
            var bestScore = -1;
            foreach (Match attribute in AttributeRegex().Matches(text))
            {
                var name = attribute.Groups["name"].Value;
                if (name[(name.IndexOf(':', StringComparison.Ordinal) + 1)..] != failure.Property
                    || BindingPath(attribute.Groups["value"].Value) is not { } binding || binding.Path != failure.Path)
                {
                    continue;
                }

                var (element, elementName) = ElementOf(text, attribute.Index);
                var score = (element == type ? 1 : 0) + (failure.ElementName.Length > 0 && elementName == failure.ElementName ? 2 : 0);
                if (score > bestScore)
                {
                    bestScore = score;
                    var offset = attribute.Groups["value"].Index + binding.Offset;
                    var line = 0;
                    var lineStart = 0;
                    for (var i = 0; i < offset; i++)
                    {
                        if (text[i] == '\n')
                        {
                            line++;
                            lineStart = i + 1;
                        }
                    }

                    best = new XamlBindingLocation(path, line, offset - lineStart);
                }
            }

            if (best is not null)
            {
                return best;
            }
        }

        return null;
    }

    /// <summary>How near the file's <c>x:Class</c> is to the element: its index among the ancestors, else after them all.</summary>
    private static int Rank(string text, IReadOnlyList<string> ancestors)
    {
        var match = ClassRegex().Match(text);
        if (!match.Success)
        {
            return int.MaxValue;
        }

        for (var i = 0; i < ancestors.Count; i++)
        {
            if (ancestors[i] == match.Groups["name"].Value.Trim())
            {
                return i;
            }
        }

        return int.MaxValue - 1;
    }

    /// <summary>A binding's path in an attribute value (the first positional argument, or <c>Path=</c>) and its offset in the value.</summary>
    internal static (string Path, int Offset)? BindingPath(string value)
    {
        var match = BindingRegex().Match(value);
        if (!match.Success)
        {
            return null;
        }

        var arguments = match.Groups["arguments"];
        var depth = 0;
        var start = 0;
        var index = 0;
        var text = arguments.Value;
        for (var i = 0; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : ',';
            depth += c == '{' ? 1 : c == '}' ? -1 : 0;
            if (c != ',' || depth != 0)
            {
                continue;
            }

            var argument = text[start..Math.Min(i, text.Length)];
            var trimmed = argument.Trim();
            var lead = argument.Length - argument.TrimStart().Length;
            var equals = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0 && index == 0 && trimmed.Length > 0)
            {
                return (trimmed, arguments.Index + start + lead);
            }

            if (equals > 0 && trimmed[..equals].Trim() == "Path")
            {
                var path = trimmed[(equals + 1)..];
                return (path.Trim(), arguments.Index + start + lead + equals + 1 + (path.Length - path.TrimStart().Length));
            }

            start = i + 1;
            index++;
        }

        return (string.Empty, match.Index);
    }

    /// <summary>The local name of the element whose start tag holds the offset, and its <c>x:Name</c> or <c>Name</c>.</summary>
    private static (string Element, string Name) ElementOf(string text, int offset)
    {
        var open = text.LastIndexOf('<', Math.Max(0, offset - 1));
        if (open < 0)
        {
            return (string.Empty, string.Empty);
        }

        var tag = TagRegex().Match(text, open);
        var element = tag.Success ? tag.Groups["element"].Value : string.Empty;
        var end = text.IndexOf('>', offset);
        var start = text[open..(end < 0 ? text.Length : end)];
        var name = NameRegex().Match(start);
        return (element[(element.IndexOf(':', StringComparison.Ordinal) + 1)..], name.Success ? name.Groups["name"].Value : string.Empty);
    }

    [GeneratedRegex(@"(?<=[\s<])(?<name>[\w:.]+)\s*=\s*""(?<value>[^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex AttributeRegex();

    [GeneratedRegex(@"\{\s*(?:\w+:)?(?:Binding|CompiledBinding|ReflectionBinding)(?:Extension)?(?<arguments>[^}]*(?:\{[^}]*\}[^}]*)*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex BindingRegex();

    [GeneratedRegex(@"\bx:Class\s*=\s*""(?<name>[^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex ClassRegex();

    [GeneratedRegex(@"\G<(?<element>[\w:.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\b(?:x:)?Name\s*=\s*""(?<name>[^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();
}
