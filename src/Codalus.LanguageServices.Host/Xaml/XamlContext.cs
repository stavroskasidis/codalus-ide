using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Codalus.LanguageServices.Host.Xaml;

/// <summary>An <c>.axaml</c> file's text (the editor's while it is open), its parse, and its project's type information.</summary>
internal sealed class XamlContext
{
    private XamlContext(string path, SourceText text, Project? project, Compilation? compilation)
    {
        Path = path;
        Text = text;
        TextString = text.ToString();
        Document = XamlDocument.Parse(TextString);
        Project = project;
        Compilation = compilation;
        Types = compilation is null ? null : XamlTypeSystem.For(compilation);
    }

    public string Path { get; }

    public SourceText Text { get; }

    public string TextString { get; }

    public XamlDocument Document { get; }

    /// <summary>The project whose additional file it is (Avalonia's targets add every <c>AvaloniaXaml</c> item), or null.</summary>
    public Project? Project { get; }

    public Compilation? Compilation { get; }

    public XamlTypeSystem? Types { get; }

    public static bool IsXaml(string path) => path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase);

    /// <summary>The file's context: its project's additional document (and compilation), or the open text alone.</summary>
    public static async Task<XamlContext?> CreateAsync(Solution solution, string path, SourceText? openText, CancellationToken cancellationToken)
    {
        path = System.IO.Path.GetFullPath(path);
        var id = solution.GetDocumentIdsWithFilePath(path).FirstOrDefault(solution.ContainsAdditionalDocument);
        if (id is not null && solution.GetAdditionalDocument(id) is { } additional)
        {
            var text = await additional.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var compilation = await additional.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            return new XamlContext(path, text, additional.Project, compilation);
        }

        if (openText is null && File.Exists(path))
        {
            openText = SourceText.From(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
        }

        return openText is null ? null : new XamlContext(path, openText, null, null);
    }

    public static XamlContext ForText(string path, string text, Project? project, Compilation? compilation) =>
        new(path, SourceText.From(text), project, compilation);

    public Protocol.Range Range(int start, int end) => new(Position(start), Position(Math.Max(start, end)));

    public Protocol.Position Position(int offset) => Positions.ToPosition(Text, offset);

    public int OffsetOf(Protocol.Position position) => Positions.ToOffset(Text, position);

    // ------------------------------------------------------------------ types of elements

    /// <summary>The type an element names (a property element: its owner's), or null.</summary>
    public INamedTypeSymbol? TypeOf(XamlElement element)
    {
        if (Types is null)
        {
            return null;
        }

        var local = element.LocalName;
        if (element.IsPropertyElement)
        {
            local = local[..local.IndexOf('.', StringComparison.Ordinal)];
        }

        return Types.ResolveType(XamlDocument.ResolvePrefix(element, element.Prefix), local);
    }

    /// <summary>A type written in an attribute (<c>vm:MainWindowViewModel</c>, <c>Button</c>) at <paramref name="scope"/>.</summary>
    public INamedTypeSymbol? ResolveTypeName(XamlElement scope, string? name)
    {
        if (Types is null || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        name = name.Trim().Replace('|', ':');
        if (name.StartsWith("{x:Type", StringComparison.Ordinal) && name.EndsWith('}'))
        {
            name = name[7..^1].Trim();
            if (name.StartsWith("TypeName=", StringComparison.Ordinal))
            {
                name = name[9..];
            }
        }

        var colon = name.IndexOf(':', StringComparison.Ordinal);
        var prefix = colon > 0 ? name[..colon] : null;
        var local = colon > 0 ? name[(colon + 1)..] : name;
        return Types.ResolveType(XamlDocument.ResolvePrefix(scope, prefix), local);
    }

    /// <summary>The object element an element belongs to: itself, or a property element's parent.</summary>
    public static XamlElement? ObjectElement(XamlElement? element)
    {
        while (element is { IsPropertyElement: true })
        {
            element = element.Parent;
        }

        return element;
    }

    /// <summary>The data type in scope at <paramref name="element"/>: the closest <c>x:DataType</c> (or a data template's <c>DataType</c>).</summary>
    public INamedTypeSymbol? DataType(XamlElement element)
    {
        for (var current = element; current is not null; current = current.Parent)
        {
            var attribute = current.Attribute("x:DataType")
                ?? (current.LocalName is "DataTemplate" or "TreeDataTemplate" ? current.Attribute("DataType") : null);
            if (attribute?.Value is { } value)
            {
                return ResolveTypeName(current, value);
            }

            // A data template without a data type: its items' type is unknown (the compiler asks for one).
            if (current.LocalName is "DataTemplate" or "TreeDataTemplate" && current != element)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>The target type of the closest style, control theme or control template (for setters and <c>TemplateBinding</c>).</summary>
    public INamedTypeSymbol? TargetType(XamlElement element)
    {
        for (var current = element; current is not null; current = current.Parent)
        {
            if (current.LocalName is "ControlTheme" or "ControlTemplate" or "Style" && current.Attribute("TargetType")?.Value is { } target)
            {
                return ResolveTypeName(current, target);
            }

            if (current.LocalName == "Style" && current.Attribute("Selector")?.Value is { } selector && XamlSelectors.LastType(selector) is { } type)
            {
                return ResolveTypeName(current, type);
            }
        }

        return null;
    }

    /// <summary>Whether bindings at <paramref name="element"/> compile (<c>x:CompileBindings</c>, else the project's <c>AvaloniaUseCompiledBindingsByDefault</c>).</summary>
    public bool CompilesBindings(XamlElement element)
    {
        for (var current = element; current is not null; current = current.Parent)
        {
            if (current.Attribute("x:CompileBindings")?.Value is { } value)
            {
                return value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }

        return Project?.AnalyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions
            .TryGetValue("build_property.AvaloniaUseCompiledBindingsByDefault", out var byDefault) == true
            && byDefault.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The code-behind class (<c>x:Class</c> on the root), or null.</summary>
    public INamedTypeSymbol? CodeBehind =>
        Document.Root?.Attribute("x:Class")?.Value is { Length: > 0 } name ? Compilation?.GetTypeByMetadataName(name.Trim()) : null;

    // ------------------------------------------------------------------ the project's other XAML

    /// <summary>The project's other XAML files and its referenced projects' (resources, styles, classes), parsed.</summary>
    public async Task<IReadOnlyList<(string Path, XamlDocument Document)>> ProjectXamlAsync(CancellationToken cancellationToken)
    {
        var result = new List<(string, XamlDocument)> { (Path, Document) };
        if (Project is null)
        {
            return result;
        }

        var projects = new List<Project> { Project };
        projects.AddRange(Project.ProjectReferences.Select(r => Project.Solution.GetProject(r.ProjectId)).OfType<Project>());
        foreach (var project in projects)
        {
            foreach (var additional in project.AdditionalDocuments)
            {
                if (additional.FilePath is not { } file || !IsXaml(file) || string.Equals(System.IO.Path.GetFullPath(file), Path, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var text = await additional.GetTextAsync(cancellationToken).ConfigureAwait(false);
                result.Add((file, s_parses.GetOrAdd(text, t => XamlDocument.Parse(t.ToString()))));
            }
        }

        return result;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SourceText, XamlDocument> s_parses = new();
}

/// <summary>A markup extension in an attribute value, as far as the caret: <c>{Binding Items[0].Ti</c>.</summary>
/// <param name="Name">The extension's name as written (<c>Binding</c>, <c>x:Static</c>).</param>
/// <param name="NameStart">The offset of the name in the value.</param>
/// <param name="Key">The current argument's key (<c>Mode</c> in <c>Mode=On</c>), or null for a positional one.</param>
/// <param name="Index">The current argument's index.</param>
/// <param name="Argument">The current argument's text before the caret (after its <c>=</c>).</param>
/// <param name="ArgumentStart">The offset in the value where <see cref="Argument"/> starts.</param>
internal sealed record MarkupExtensionContext(string Name, int NameStart, string? Key, int Index, string Argument, int ArgumentStart)
{
    public bool InName => Index < 0;

    /// <summary>The innermost unclosed markup extension at <paramref name="caret"/> in <paramref name="value"/>, or null.</summary>
    public static MarkupExtensionContext? At(string value, int caret)
    {
        caret = Math.Clamp(caret, 0, value.Length);
        var open = new Stack<int>();
        for (var i = 0; i < caret; i++)
        {
            if (value[i] == '{')
            {
                open.Push(i);
            }
            else if (value[i] == '}' && open.Count > 0)
            {
                open.Pop();
            }
        }

        if (open.Count == 0)
        {
            return null;
        }

        var start = open.Peek() + 1;
        var nameEnd = start;
        while (nameEnd < caret && !char.IsWhiteSpace(value[nameEnd]) && value[nameEnd] is not (',' or '}'))
        {
            nameEnd++;
        }

        var name = value[start..nameEnd];
        if (nameEnd >= caret)
        {
            return new MarkupExtensionContext(name, start, null, -1, name, start);
        }

        // The arguments: split at top-level commas.
        var index = 0;
        var argumentStart = nameEnd;
        var depth = 0;
        for (var i = nameEnd; i < caret; i++)
        {
            if (value[i] == '{')
            {
                depth++;
            }
            else if (value[i] == '}')
            {
                depth--;
            }
            else if (value[i] == ',' && depth == 0)
            {
                index++;
                argumentStart = i + 1;
            }
        }

        while (argumentStart < caret && char.IsWhiteSpace(value[argumentStart]))
        {
            argumentStart++;
        }

        var argument = value[argumentStart..caret];
        var equals = argument.IndexOf('=', StringComparison.Ordinal);
        if (equals >= 0 && !argument[..equals].Contains('{', StringComparison.Ordinal))
        {
            var valueStart = argumentStart + equals + 1;
            while (valueStart < caret && char.IsWhiteSpace(value[valueStart]))
            {
                valueStart++;
            }

            return new MarkupExtensionContext(name, start, argument[..equals].Trim(), index, value[valueStart..caret], valueStart);
        }

        return new MarkupExtensionContext(name, start, null, index, argument, argumentStart);
    }

    /// <summary>The extension's name without the x: prefix or the Extension suffix.</summary>
    public string Kind => (Name.StartsWith("x:", StringComparison.Ordinal) ? Name[2..] : Name) is var n && n.EndsWith("Extension", StringComparison.Ordinal) ? n[..^9] : n;

    public bool IsBinding => Kind is "Binding" or "CompiledBinding" or "ReflectionBinding";

    public bool IsResource => Kind is "StaticResource" or "DynamicResource";

    /// <summary>A binding's path argument (the first positional one, or <c>Path=</c>).</summary>
    public bool IsPath => IsBinding && (Key == "Path" || (Key is null && Index == 0));
}

/// <summary>Avalonia style selectors: the type names in them, their classes and names.</summary>
internal static class XamlSelectors
{
    /// <summary>The last type a selector names (<c>Button</c> in <c>StackPanel &gt; Button:pointerover</c>), or null.</summary>
    public static string? LastType(string selector)
    {
        var templatePart = selector.LastIndexOf("/template/", StringComparison.Ordinal);
        var part = templatePart >= 0 ? selector[(templatePart + 10)..] : selector;
        string? last = null;
        foreach (var token in Tokens(part))
        {
            if (token.Kind == ' ' && token.Text.Length > 0 && !token.Text.StartsWith('^'))
            {
                last = token.Text;
            }
        }

        return last ?? (templatePart >= 0 ? LastType(selector[..templatePart]) : null);
    }

    /// <summary>The selector's tokens: types (' '), classes ('.'), pseudo-classes (':'), names ('#'), with their offsets.</summary>
    public static IEnumerable<(char Kind, string Text, int Start)> Tokens(string selector)
    {
        var i = 0;
        while (i < selector.Length)
        {
            var c = selector[i];
            if (c is '.' or ':' or '#' || char.IsLetter(c) || c == '_')
            {
                var kind = char.IsLetter(c) || c == '_' ? ' ' : c;
                var start = kind == ' ' ? i : i + 1;
                var end = start;
                while (end < selector.Length && (char.IsLetterOrDigit(selector[end]) || selector[end] is '_' or '-' || (kind == ' ' && selector[end] == '|')))
                {
                    end++;
                }

                yield return (kind, selector[start..end], start);
                i = Math.Max(end, i + 1);
            }
            else
            {
                i++;
            }
        }
    }
}
