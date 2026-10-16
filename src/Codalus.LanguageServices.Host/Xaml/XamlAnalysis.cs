using System.Text;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Diagnostic = Codalus.LanguageServices.Protocol.Diagnostic;
using DiagnosticSeverity = Codalus.LanguageServices.Protocol.DiagnosticSeverity;
using Range = Codalus.LanguageServices.Protocol.Range;
using SymbolKind = Codalus.LanguageServices.Protocol.SymbolKind;

namespace Codalus.LanguageServices.Host.Xaml;

/// <summary>
/// Live XAML diagnostics, formatting, folding, document symbols and the logical tree (M19a, ADR 0047). Diagnostics are
/// what the XAML compiler would reject and the parse can see: syntax errors, undeclared prefixes, unknown types,
/// members and enum values, and compiled bindings' paths that the data type doesn't have.
/// </summary>
internal static class XamlAnalysis
{
    public const string Source = "XAML";

    public static Diagnostic[] Diagnose(XamlContext context)
    {
        var result = new List<Diagnostic>();
        void Report(int start, int end, string code, string message, int severity = DiagnosticSeverity.Error) =>
            result.Add(new Diagnostic(context.Range(start, Math.Max(start + 1, end)), severity, code, Source, message));

        foreach (var error in context.Document.Errors)
        {
            Report(error.Start, error.End, "XAML0001", error.Message);
        }

        if (context.Types is not { HasDialect: true } types || context.Document.Root is not { } root)
        {
            return [.. result];
        }

        var ignorable = (root.Attributes.FirstOrDefault(a => a.LocalName == "Ignorable" && XamlDocument.ResolvePrefix(root, a.Prefix) == XamlNames.MarkupCompatibility)?.Value ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        bool Ignored(string? prefix, XamlElement scope) =>
            prefix is not null && (ignorable.Contains(prefix) || XamlDocument.ResolvePrefix(scope, prefix) is XamlNames.Design or XamlNames.MarkupCompatibility or XamlNames.XamlLanguage or XamlNames.Xml);

        foreach (var element in context.Document.Elements)
        {
            if (element.Name.Length == 0 || Ignored(element.Prefix, element))
            {
                continue;
            }

            var xmlns = XamlDocument.ResolvePrefix(element, element.Prefix);
            if (xmlns is null)
            {
                Report(element.NameStart, element.NameEnd, "XAML0002", element.Prefix is null ? "No default XML namespace is declared." : $"The prefix '{element.Prefix}' isn't declared.");
                continue;
            }

            if (types.ClrNamespaces(xmlns).Count == 0)
            {
                continue; // a namespace this compilation doesn't map: nothing to check against
            }

            var type = context.TypeOf(element);
            var local = element.LocalName;
            var typeName = element.IsPropertyElement ? local[..local.IndexOf('.', StringComparison.Ordinal)] : local;
            if (type is null)
            {
                Report(element.NameStart, element.NameStart + (element.Prefix is null ? 0 : element.Prefix.Length + 1) + typeName.Length, "XAML0003", $"The type '{typeName}' isn't in {xmlns}.");
                continue;
            }

            if (element.IsPropertyElement)
            {
                var memberName = local[(local.IndexOf('.', StringComparison.Ordinal) + 1)..];
                if (types.Member(type, memberName) is null && types.Attached(type).All(m => m.Name != memberName))
                {
                    Report(element.NameStart, element.NameEnd, "XAML0004", $"'{type.Name}' has no property '{memberName}'.");
                }

                continue;
            }

            foreach (var attribute in element.Attributes)
            {
                CheckAttribute(context, types, element, type, attribute, Ignored, Report);
            }
        }

        return [.. result];
    }

    private static void CheckAttribute(
        XamlContext context,
        XamlTypeSystem types,
        XamlElement element,
        INamedTypeSymbol type,
        XamlAttribute attribute,
        Func<string?, XamlElement, bool> ignored,
        Action<int, int, string, string, int> report)
    {
        var name = attribute.Name;
        if (name == "xmlns" || name.StartsWith("xmlns:", StringComparison.Ordinal) || name.StartsWith("Classes.", StringComparison.Ordinal) || ignored(attribute.Prefix, element))
        {
            if (attribute.Prefix is { } p && XamlDocument.ResolvePrefix(element, p) == XamlNames.XamlLanguage && attribute.LocalName == "DataType" && attribute.Value is { } dataType
                && !dataType.TrimStart().StartsWith('{') && context.ResolveTypeName(element, dataType) is null)
            {
                report(attribute.ValueStart, attribute.ValueEnd, "XAML0003", $"The type '{dataType.Trim()}' isn't found.", DiagnosticSeverity.Error);
            }

            return;
        }

        if (attribute.Prefix is { } prefix && !name.Contains('.', StringComparison.Ordinal))
        {
            if (XamlDocument.ResolvePrefix(element, prefix) is null)
            {
                report(attribute.NameStart, attribute.NameEnd, "XAML0002", $"The prefix '{prefix}' isn't declared.", DiagnosticSeverity.Error);
            }

            return;
        }

        XamlMember? member;
        var dot = name.LastIndexOf('.');
        if (dot > 0)
        {
            var ownerName = name[..dot];
            if (context.ResolveTypeName(element, ownerName) is not { } owner)
            {
                report(attribute.NameStart, attribute.NameStart + dot, "XAML0003", $"The type '{ownerName}' isn't found.", DiagnosticSeverity.Error);
                return;
            }

            member = types.Attached(owner).FirstOrDefault(m => m.Name == name[(dot + 1)..]) ?? types.Member(owner, name[(dot + 1)..]);
            if (member is null)
            {
                report(attribute.NameStart + dot + 1, attribute.NameEnd, "XAML0004", $"'{owner.Name}' has no attached property '{name[(dot + 1)..]}'.", DiagnosticSeverity.Error);
                return;
            }
        }
        else
        {
            member = types.Member(type, name);
            if (member is null)
            {
                report(attribute.NameStart, attribute.NameEnd, "XAML0004", $"'{type.Name}' has no property or event '{name}'.", DiagnosticSeverity.Error);
                return;
            }
        }

        if (attribute.Value is not { } value || attribute.ValueStart < 0)
        {
            return;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith('{') && !trimmed.StartsWith("{}", StringComparison.Ordinal))
        {
            CheckBinding(context, element, attribute, report);
            return;
        }

        // Enum values (flags as comma-separated names, or numbers).
        var valueType = member.ValueType is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable ? nullable.TypeArguments[0] : member.ValueType;
        if (member.Kind != XamlMemberKind.Event && valueType is { TypeKind: TypeKind.Enum } && trimmed.Length > 0)
        {
            var names = valueType.GetMembers().OfType<IFieldSymbol>().Where(f => f.IsConst).Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var part in trimmed.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!names.Contains(part) && !long.TryParse(part, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _))
                {
                    report(attribute.ValueStart, attribute.ValueEnd, "XAML0005", $"'{part}' isn't a value of {valueType.Name} ({string.Join(", ", names.Take(8))}{(names.Count > 8 ? ", …" : string.Empty)}).", DiagnosticSeverity.Error);
                    break;
                }
            }
        }
    }

    /// <summary>A compiled binding's path: each member must exist on the type before it, from the data type in scope.</summary>
    private static void CheckBinding(XamlContext context, XamlElement element, XamlAttribute attribute, Action<int, int, string, string, int> report)
    {
        var value = attribute.Value!;
        var open = value.IndexOf('{', StringComparison.Ordinal);
        var close = FindClose(value, open);
        if (close < 0 || MarkupExtensionContext.At(value, close) is not { IsBinding: true } binding)
        {
            return;
        }

        var compiled = binding.Kind == "CompiledBinding" || (binding.Kind == "Binding" && context.CompilesBindings(element));
        if (!compiled || context.DataType(element) is not { } dataType)
        {
            return;
        }

        // The path: the first positional argument, or Path=.
        var arguments = value[(open + 1 + binding.Name.Length)..close];
        string? path = null;
        var pathOffset = 0;
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= arguments.Length; i++)
        {
            if (i < arguments.Length && arguments[i] == '{')
            {
                depth++;
            }
            else if (i < arguments.Length && arguments[i] == '}')
            {
                depth--;
            }
            else if (i == arguments.Length || (arguments[i] == ',' && depth == 0))
            {
                var argument = arguments[start..i];
                var trimmed = argument.Trim();
                var equals = trimmed.IndexOf('=', StringComparison.Ordinal);
                if (start == 0 && equals < 0 && trimmed.Length > 0)
                {
                    path = trimmed;
                    pathOffset = start + argument.IndexOf(trimmed, StringComparison.Ordinal);
                }
                else if (equals > 0 && trimmed[..equals].Trim() == "Path")
                {
                    path = trimmed[(equals + 1)..].Trim();
                    pathOffset = start + argument.IndexOf(path, StringComparison.Ordinal);
                }
                else if (equals > 0 && trimmed[..equals].Trim() is "ElementName" or "RelativeSource" or "Source")
                {
                    return; // another source than the data context
                }

                start = i + 1;
            }
        }

        if (path is null || path.Length == 0 || path == "." || path.StartsWith('$') || path.StartsWith('#') || path.Contains('(', StringComparison.Ordinal))
        {
            return;
        }

        ITypeSymbol? type = dataType;
        var segmentStart = 0;
        foreach (var raw in path.Split('.'))
        {
            var segment = raw.Trim().TrimStart('!');
            var indexer = segment.IndexOf('[', StringComparison.Ordinal);
            var memberName = indexer >= 0 ? segment[..indexer] : segment;
            var member = XamlTypeSystem.BindableMembers(type!).FirstOrDefault(m => m.Name == memberName);
            if (member is null)
            {
                var at = attribute.ValueStart + open + 1 + binding.Name.Length + pathOffset + segmentStart;
                report(at, at + raw.Length, "XAML0006", $"'{memberName}' isn't a property of {type!.Name} (the binding compiles against x:DataType {dataType.Name}).", DiagnosticSeverity.Error);
                return;
            }

            type = member switch
            {
                IPropertySymbol property => property.Type,
                IMethodSymbol method => method.ReturnType,
                _ => null,
            };
            if (indexer >= 0 && type is not null)
            {
                type = XamlTypeSystem.ElementType(type);
            }

            if (type is null || type.TypeKind == TypeKind.TypeParameter || type.SpecialType == SpecialType.System_Object || type.TypeKind == TypeKind.Dynamic)
            {
                return;
            }

            segmentStart += raw.Length + 1;
        }
    }

    private static int FindClose(string value, int open)
    {
        if (open < 0)
        {
            return -1;
        }

        var depth = 0;
        for (var i = open; i < value.Length; i++)
        {
            if (value[i] == '{')
            {
                depth++;
            }
            else if (value[i] == '}' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    // ------------------------------------------------------------------ formatting

    /// <summary>
    /// Indents elements by their depth and attributes on their own lines under the first attribute (or one level in),
    /// and trims trailing whitespace; text content, comments and CDATA keep their lines. Only whitespace changes.
    /// </summary>
    public static TextEdit[] Format(XamlContext context, Range? range, FormattingOptions options)
    {
        var text = context.Text;
        var document = context.Document;
        if (document.Errors.Count > 0)
        {
            return []; // never reformat what doesn't parse
        }

        var unit = options.InsertSpaces ? new string(' ', Math.Max(1, options.TabSize)) : "\t";
        var firstLine = range?.Start.Line ?? 0;
        var lastLine = range?.End.Line ?? text.Lines.Count - 1;
        var protectedSpans = ProtectedSpans(context.TextString);
        var edits = new List<TextEdit>();
        for (var lineIndex = firstLine; lineIndex <= Math.Min(lastLine, text.Lines.Count - 1); lineIndex++)
        {
            var line = text.Lines[lineIndex];
            var lineText = text.ToString(line.Span);
            var content = lineText.TrimStart();
            var indentLength = lineText.Length - content.Length;
            var trimmedEnd = content.TrimEnd();

            // Trailing whitespace.
            if (trimmedEnd.Length < content.Length && !protectedSpans.Any(s => line.Start + indentLength + trimmedEnd.Length >= s.Start && line.Start + indentLength + trimmedEnd.Length < s.End))
            {
                edits.Add(new TextEdit(context.Range(line.Start + indentLength + trimmedEnd.Length, line.End), string.Empty));
            }

            if (content.Length == 0)
            {
                continue;
            }

            var offset = line.Start + indentLength;
            if (protectedSpans.Any(s => offset > s.Start && offset < s.End))
            {
                continue;
            }

            if (Indentation(document, offset, content, unit) is { } wanted && wanted != lineText[..indentLength])
            {
                edits.Add(new TextEdit(context.Range(line.Start, offset), wanted));
            }
        }

        return [.. edits];
    }

    private static string? Indentation(XamlDocument document, int offset, string content, string unit)
    {
        string Depth(XamlElement element) => string.Concat(Enumerable.Repeat(unit, element.Ancestors().Count()));
        var container = document.ElementAt(offset);
        if (content.StartsWith("</", StringComparison.Ordinal))
        {
            var closing = document.Elements.FirstOrDefault(e => e.EndTagNameStart == offset + 2);
            return closing is null ? null : Depth(closing);
        }

        if (content.StartsWith("<!--", StringComparison.Ordinal))
        {
            return container is null ? string.Empty : Depth(container) + unit;
        }

        if (content.StartsWith('<'))
        {
            var element = document.Elements.FirstOrDefault(e => e.Start == offset);
            return element is null ? null : Depth(element);
        }

        // Inside a start tag: an attribute (or the tag's end) on its own line.
        if (container is not null && offset > container.NameEnd && (container.StartTagEnd < 0 || offset < container.StartTagEnd))
        {
            if (content.StartsWith("/>", StringComparison.Ordinal) || content.StartsWith('>'))
            {
                return Depth(container);
            }

            var first = container.Attributes.FirstOrDefault();
            if (first is not null && first.NameStart < offset && !document.Text[container.Start..first.NameStart].Contains('\n', StringComparison.Ordinal))
            {
                var lineStart = document.Text.LastIndexOf('\n', container.Start) + 1;
                var column = first.NameStart - lineStart;
                var tagIndent = Depth(container);
                var tagColumn = container.Start - lineStart;
                return tagIndent + new string(' ', Math.Max(0, column - tagColumn));
            }

            return Depth(container) + unit;
        }

        return null; // text content keeps its indentation
    }

    /// <summary>Comments, CDATA and multi-line attribute values: their insides are never touched.</summary>
    private static List<(int Start, int End)> ProtectedSpans(string text)
    {
        var spans = new List<(int, int)>();
        foreach (var (open, close) in new[] { ("<!--", "-->"), ("<![CDATA[", "]]>") })
        {
            var i = 0;
            while ((i = text.IndexOf(open, i, StringComparison.Ordinal)) >= 0)
            {
                var end = text.IndexOf(close, i + open.Length, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + close.Length;
                spans.Add((i, end));
                i = end;
            }
        }

        return spans;
    }

    // ------------------------------------------------------------------ structure

    public static FoldingRange[] Folding(XamlContext context)
    {
        var result = new List<FoldingRange>();
        foreach (var element in context.Document.Elements)
        {
            var start = context.Position(element.Start).Line;
            var end = context.Position(Math.Max(element.Start, element.End < 0 ? context.TextString.Length : element.End)).Line;
            if (end > start)
            {
                result.Add(new FoldingRange(start, end));
            }
        }

        return [.. result];
    }

    public static DocumentSymbol[] Symbols(XamlContext context)
    {
        DocumentSymbol Symbol(XamlElement element)
        {
            var name = (element.Attribute("x:Name") ?? element.Attribute("Name"))?.Value;
            var range = context.Range(element.Start, element.End < 0 ? context.TextString.Length : element.End);
            return new DocumentSymbol(name is null ? element.LocalName : $"{element.LocalName} {name}", element.IsPropertyElement ? SymbolKind.Property : SymbolKind.Class, range, context.Range(element.NameStart, element.NameEnd))
            {
                Detail = element.Attribute("x:Key")?.Value,
                Children = [.. element.Children.Select(Symbol)],
            };
        }

        return context.Document.Root is { } root ? [Symbol(root)] : [];
    }

    private static readonly HashSet<string> s_notLogical = new(StringComparer.Ordinal)
    {
        "Resources", "Styles", "DataTemplates", "Template", "ItemTemplate", "ItemsPanel", "ContentTemplate", "HeaderTemplate",
        "KeyBindings", "Transitions", "Flyout", "ContextFlyout", "ContextMenu", "Tip", "Theme", "ItemContainerTheme", "DataContext",
        "PreviewWith", "Effect", "RenderTransform", "OpacityMask", "Clip", "FocusAdorner", "Interactions", "Behaviors",
    };

    /// <summary>
    /// The XAML's logical tree as the previewer sees it: the controls it declares, nested as their logical parents
    /// (through content property elements, not templates, styles or resources). The root's type is its <c>x:Class</c>
    /// (what runs); a styles or resources file's root is its <c>Design.PreviewWith</c> content.
    /// </summary>
    public static XamlLogicalNode[] LogicalTree(XamlContext context)
    {
        if (context.Document.Root is not { } root)
        {
            return [];
        }

        bool IsLogical(XamlElement element)
        {
            if (element.IsPropertyElement || element.Name.Length == 0 || XamlDocument.ResolvePrefix(element, element.Prefix) is XamlNames.XamlLanguage or XamlNames.Design)
            {
                return false;
            }

            return context.Types is null || context.TypeOf(element) is not { } type || XamlTypeSystem.IsControl(type);
        }

        List<XamlLogicalNode> Collect(XamlElement element)
        {
            var nodes = new List<XamlLogicalNode>();
            foreach (var child in element.Children)
            {
                if (child.IsPropertyElement)
                {
                    var member = child.LocalName[(child.LocalName.IndexOf('.', StringComparison.Ordinal) + 1)..];
                    if (!s_notLogical.Contains(member))
                    {
                        nodes.AddRange(Collect(child));
                    }
                }
                else if (IsLogical(child))
                {
                    nodes.Add(Node(child, null));
                }
            }

            return nodes;
        }

        XamlLogicalNode Node(XamlElement element, string? type)
        {
            var name = (element.Attribute("x:Name") ?? element.Attribute("Name"))?.Value;
            var end = element.End < 0 ? context.TextString.Length : element.End;
            return new XamlLogicalNode(type ?? element.LocalName, name, context.Range(element.Start, end), context.Range(element.Start, element.StartTagEnd < 0 ? end : element.StartTagEnd), [.. Collect(element)]);
        }

        if (IsLogical(root))
        {
            var className = root.Attribute("x:Class")?.Value is { Length: > 0 } cls ? cls[(cls.LastIndexOf('.') + 1)..].Trim() : null;
            return [Node(root, className)];
        }

        // Styles and resource dictionaries preview their Design.PreviewWith.
        var preview = root.Children.FirstOrDefault(c => c.LocalName.EndsWith(".PreviewWith", StringComparison.Ordinal))?.Children.FirstOrDefault(IsLogical);
        return preview is null ? [] : [Node(preview, null)];
    }

    public static string Describe(XamlLogicalNode node)
    {
        var builder = new StringBuilder();
        void Walk(XamlLogicalNode n, int depth)
        {
            builder.Append(' ', depth * 2).Append(n.Type).Append(n.Name is null ? string.Empty : " " + n.Name).Append('\n');
            foreach (var child in n.Children)
            {
                Walk(child, depth + 1);
            }
        }

        Walk(node, 0);
        return builder.ToString();
    }
}
