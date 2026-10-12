using System.Text.RegularExpressions;
using DaedalusStudio.LanguageServices.Host.Features;
using DaedalusStudio.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Location = DaedalusStudio.LanguageServices.Protocol.Location;
using Range = DaedalusStudio.LanguageServices.Protocol.Range;

namespace DaedalusStudio.LanguageServices.Host.Xaml;

/// <summary>What is at a position of a XAML file: a symbol, a resource key, a style class or an element name, with its span.</summary>
internal sealed record XamlTarget(int Start, int End)
{
    public ISymbol? Symbol { get; init; }

    public string? ResourceKey { get; init; }

    public string? StyleClass { get; init; }

    public string? ElementName { get; init; }

    /// <summary>A directive or keyword's description (<c>x:Name</c>…), for hover.</summary>
    public string? Description { get; init; }
}

/// <summary>Hover, Go to Definition and <c>x:Name</c> rename in XAML (M19a, ADR 0046).</summary>
internal static partial class XamlNavigation
{
    private static readonly Dictionary<string, string> s_directives = new(StringComparer.Ordinal)
    {
        ["x:Name"] = "The element's name: a field of the code-behind class (Avalonia's name generator) and a target for `#Name` and `ElementName`.",
        ["Name"] = "The element's name: a field of the code-behind class and a target for `#Name` and `ElementName`.",
        ["x:Key"] = "The key of a resource, for `StaticResource` and `DynamicResource`.",
        ["x:Class"] = "The code-behind class this XAML is compiled into.",
        ["x:DataType"] = "The type of the data context here: bindings compile against it.",
        ["x:CompileBindings"] = "Whether bindings here compile against `x:DataType`.",
        ["d:DataContext"] = "The data context in the previewer only.",
        ["d:DesignWidth"] = "The width in the previewer only.",
        ["d:DesignHeight"] = "The height in the previewer only.",
        ["mc:Ignorable"] = "Prefixes the XAML compiler ignores (the design-time `d:` attributes).",
    };

    // ------------------------------------------------------------------ what is at a position

    public static async Task<XamlTarget?> TargetAsync(XamlContext context, int offset, CancellationToken cancellationToken)
    {
        var element = context.Document.ElementAt(offset);
        if (element is null)
        {
            return null;
        }

        var types = context.Types;
        if ((offset >= element.NameStart && offset <= element.NameEnd) || (element.EndTagNameStart >= 0 && offset >= element.EndTagNameStart && offset <= element.EndTagNameStart + element.Name.Length))
        {
            var start = offset >= element.NameStart && offset <= element.NameEnd ? element.NameStart : element.EndTagNameStart;
            if (element.IsPropertyElement)
            {
                var local = element.LocalName;
                var owner = context.TypeOf(element);
                var name = local[(local.IndexOf('.', StringComparison.Ordinal) + 1)..];
                var member = owner is null || types is null ? null : types.Member(owner, name) ?? types.Attached(owner).FirstOrDefault(m => m.Name == name);
                return new XamlTarget(start, start + element.Name.Length) { Symbol = member?.Symbol };
            }

            return new XamlTarget(start, start + element.Name.Length) { Symbol = context.TypeOf(element) };
        }

        foreach (var attribute in element.Attributes)
        {
            if (offset >= attribute.NameStart && offset <= attribute.NameEnd)
            {
                var prefixes = XamlDocument.Prefixes(element);
                var normalized = attribute.Prefix is { } p && prefixes.GetValueOrDefault(p) is { } ns
                    ? (ns == XamlNames.XamlLanguage ? "x:" : ns == XamlNames.Design ? "d:" : ns == XamlNames.MarkupCompatibility ? "mc:" : p + ":") + attribute.LocalName
                    : attribute.Name;
                if (s_directives.TryGetValue(normalized, out var description) && normalized != "Name")
                {
                    return new XamlTarget(attribute.NameStart, attribute.NameEnd) { Description = $"**{attribute.Name}**: {description}" };
                }

                var member = XamlCompletion.Member(context, element, context.TypeOf(element), attribute.Name);
                return new XamlTarget(attribute.NameStart, attribute.NameEnd) { Symbol = member?.Symbol };
            }

            if (attribute.ContainsValue(offset))
            {
                return await ValueTargetAsync(context, element, attribute, offset, cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    private static Task<XamlTarget?> ValueTargetAsync(XamlContext context, XamlElement element, XamlAttribute attribute, int offset, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var value = attribute.Value ?? string.Empty;
        var caret = offset - attribute.ValueStart;
        var (start, end) = Word(value, caret);
        var word = value[start..end];
        var absoluteStart = attribute.ValueStart + start;
        var absoluteEnd = attribute.ValueStart + end;
        XamlTarget? Result(XamlTarget? target) => target;

        if (MarkupExtensionContext.At(value, caret) is { } extension)
        {
            // Complete the argument to its end for the lookup.
            var argumentEnd = end;
            if (extension.IsResource)
            {
                return Task.FromResult(Result(new XamlTarget(absoluteStart, absoluteEnd) { ResourceKey = word }));
            }

            if (extension.IsPath && !word.StartsWith('$'))
            {
                if (word.StartsWith('#'))
                {
                    var dot = word.IndexOf('.', StringComparison.Ordinal);
                    if (dot < 0 || caret - start <= dot)
                    {
                        var name = dot < 0 ? word[1..] : word[1..dot];
                        return Task.FromResult(Result(new XamlTarget(absoluteStart + 1, absoluteStart + 1 + name.Length) { ElementName = name }));
                    }
                }

                // The path segment under the caret.
                var segmentStart = caret;
                while (segmentStart > start && value[segmentStart - 1] != '.')
                {
                    segmentStart--;
                }

                var segmentEnd = caret;
                while (segmentEnd < argumentEnd && value[segmentEnd] is not ('.' or '[' or ',' or '}'))
                {
                    segmentEnd++;
                }

                var prefix = segmentStart > start ? value[start..(segmentStart - 1)] : string.Empty;
                var segment = value[segmentStart..segmentEnd].TrimStart('!');
                if (XamlBindingPaths.Resolve(context, element, prefix) is { } type)
                {
                    var member = XamlTypeSystem.BindableMembers(type).FirstOrDefault(m => m.Name == segment);
                    return Task.FromResult(Result(new XamlTarget(attribute.ValueStart + segmentStart, attribute.ValueStart + segmentEnd) { Symbol = member }));
                }

                return Task.FromResult<XamlTarget?>(null);
            }

            if (extension.Key == "ElementName")
            {
                return Task.FromResult(Result(new XamlTarget(absoluteStart, absoluteEnd) { ElementName = word }));
            }

            if (extension.Kind == "Static")
            {
                var dot = word.LastIndexOf('.');
                if (dot > 0 && context.ResolveTypeName(element, word[..dot]) is { } owner)
                {
                    var member = owner.GetMembers(word[(dot + 1)..]).FirstOrDefault();
                    return Task.FromResult(Result(caret - start > dot
                        ? new XamlTarget(absoluteStart + dot + 1, absoluteEnd) { Symbol = member }
                        : new XamlTarget(absoluteStart, absoluteStart + dot) { Symbol = owner }));
                }
            }

            if (extension.Kind == "Type")
            {
                return Task.FromResult(Result(new XamlTarget(absoluteStart, absoluteEnd) { Symbol = context.ResolveTypeName(element, word) }));
            }

            if (extension.InName)
            {
                var type = context.Types?.ResolveType(XamlDocument.ResolvePrefix(element, extension.Name.Contains(':', StringComparison.Ordinal) ? extension.Name[..extension.Name.IndexOf(':', StringComparison.Ordinal)] : null),
                    extension.Name.Contains(':', StringComparison.Ordinal) ? extension.Name[(extension.Name.IndexOf(':', StringComparison.Ordinal) + 1)..] : extension.Name);
                return Task.FromResult(Result(new XamlTarget(absoluteStart, absoluteEnd) { Symbol = type }));
            }

            return Task.FromResult<XamlTarget?>(null);
        }

        var attributeName = attribute.Name;
        if (attributeName is "x:Name" or "Name")
        {
            return Task.FromResult(Result(new XamlTarget(attribute.ValueStart, attribute.ValueEnd) { ElementName = value.Trim(), Symbol = NameField(context, value.Trim()) }));
        }

        if (attributeName.EndsWith(":Class", StringComparison.Ordinal))
        {
            return Task.FromResult(Result(new XamlTarget(attribute.ValueStart, attribute.ValueEnd) { Symbol = context.Compilation?.GetTypeByMetadataName(value.Trim()) }));
        }

        if (attributeName.EndsWith(":DataType", StringComparison.Ordinal) || attributeName is "DataType" or "TargetType")
        {
            return Task.FromResult(Result(new XamlTarget(attribute.ValueStart, attribute.ValueEnd) { Symbol = context.ResolveTypeName(element, value) }));
        }

        if (attributeName == "Classes")
        {
            return Task.FromResult(Result(new XamlTarget(absoluteStart, absoluteEnd) { StyleClass = word }));
        }

        if (attributeName == "Selector")
        {
            var token = XamlSelectors.Tokens(value).FirstOrDefault(t => caret >= t.Start && caret <= t.Start + t.Text.Length);
            return Task.FromResult(token.Text is null ? null : token.Kind switch
            {
                ' ' => new XamlTarget(attribute.ValueStart + token.Start, attribute.ValueStart + token.Start + token.Text.Length) { Symbol = context.ResolveTypeName(element, token.Text) },
                '.' => new XamlTarget(attribute.ValueStart + token.Start, attribute.ValueStart + token.Start + token.Text.Length) { StyleClass = token.Text },
                '#' => new XamlTarget(attribute.ValueStart + token.Start, attribute.ValueStart + token.Start + token.Text.Length) { ElementName = token.Text },
                _ => null,
            });
        }

        if (element.LocalName == "Setter" && attributeName == "Property" && context.TargetType(element) is { } target)
        {
            return Task.FromResult(Result(new XamlTarget(attribute.ValueStart, attribute.ValueEnd) { Symbol = XamlCompletion.Member(context, element, target, value.Trim())?.Symbol }));
        }

        // An enum member or a static instance.
        if (XamlCompletion.Member(context, element, context.TypeOf(element), attributeName) is { } m && context.Types is { } types)
        {
            if (m.Kind == XamlMemberKind.Event && context.CodeBehind is { } codeBehind)
            {
                return Task.FromResult(Result(new XamlTarget(attribute.ValueStart, attribute.ValueEnd) { Symbol = codeBehind.GetMembers(value.Trim()).FirstOrDefault() }));
            }

            var symbol = types.ValuesOf(m.ValueType).FirstOrDefault(v => string.Equals(v.Name, word, StringComparison.OrdinalIgnoreCase)).Symbol;
            return Task.FromResult(symbol is null ? null : Result(new XamlTarget(absoluteStart, absoluteEnd) { Symbol = symbol }));
        }

        return Task.FromResult<XamlTarget?>(null);
    }

    private static (int Start, int End) Word(string value, int caret)
    {
        static bool IsPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or ':' or '#' or '$' or '-' or '!' or '[' or ']' or '|';
        caret = Math.Clamp(caret, 0, value.Length);
        var start = caret;
        while (start > 0 && IsPart(value[start - 1]))
        {
            start--;
        }

        var end = caret;
        while (end < value.Length && IsPart(value[end]))
        {
            end++;
        }

        return (start, end);
    }

    /// <summary>The code-behind's field for an <c>x:Name</c> (generated by Avalonia's name generator), if any.</summary>
    private static ISymbol? NameField(XamlContext context, string name) =>
        context.CodeBehind?.GetMembers(name).FirstOrDefault(m => m is IFieldSymbol or IPropertySymbol);

    // ------------------------------------------------------------------ hover

    public static async Task<Hover?> HoverAsync(XamlContext context, int offset, CancellationToken cancellationToken)
    {
        if (await TargetAsync(context, offset, cancellationToken).ConfigureAwait(false) is not { } target)
        {
            return null;
        }

        var range = context.Range(target.Start, target.End);
        if (target.Description is { } description)
        {
            return new Hover(MarkupContent.Markdown(description), range);
        }

        if (target.ResourceKey is { } key)
        {
            var found = (await XamlCompletion.ResourceKeysAsync(context, cancellationToken).ConfigureAwait(false)).FirstOrDefault(r => r.Key == key);
            return found.Key is null
                ? new Hover(MarkupContent.Markdown($"Resource `{key}`: not defined in this project's XAML (a theme's or a library's resource?)."), range)
                : new Hover(MarkupContent.Markdown($"Resource `{key}`: `{found.Kind}`, defined in {Path.GetFileName(found.File)}."), range);
        }

        if (target.Symbol is { } symbol)
        {
            var signature = symbol switch
            {
                INamedTypeSymbol type => $"{(type.TypeKind == TypeKind.Struct ? "struct" : type.TypeKind == TypeKind.Enum ? "enum" : "class")} {type.ToDisplayString()}",
                IPropertySymbol property => $"{property.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {property.ContainingType.Name}.{property.Name}",
                IFieldSymbol field when field.Type.Name is "StyledProperty" or "DirectProperty" or "AttachedProperty" =>
                    $"{field.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {field.ContainingType.Name}.{field.Name}",
                IFieldSymbol field => $"{field.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {field.ContainingType.Name}.{field.Name}",
                IEventSymbol @event => $"event {@event.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {@event.ContainingType.Name}.{@event.Name}",
                _ => symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            };
            var summary = XamlDocs.Summary(symbol);
            return new Hover(MarkupContent.Markdown($"```csharp\n{signature}\n```" + (summary is null ? string.Empty : "\n\n" + summary)), range);
        }

        if (target.StyleClass is { } cls)
        {
            return new Hover(MarkupContent.Markdown($"Style class `{cls}`."), range);
        }

        return target.ElementName is { } name ? new Hover(MarkupContent.Markdown($"Element `{name}`."), range) : null;
    }

    // ------------------------------------------------------------------ definition

    public static async Task<Location[]> DefinitionAsync(XamlContext context, int offset, CancellationToken cancellationToken)
    {
        if (await TargetAsync(context, offset, cancellationToken).ConfigureAwait(false) is not { } target)
        {
            return [];
        }

        if (target.ResourceKey is { } key)
        {
            return [.. (await context.ProjectXamlAsync(cancellationToken).ConfigureAwait(false))
                .SelectMany(x => x.Document.Elements.Where(e => e.Attribute("x:Key")?.Value == key).Select(e => (x.Path, x.Document, e.Attribute("x:Key")!)))
                .Take(1)
                .Select(x => Location(x.Path, x.Document, x.Item3.ValueStart, x.Item3.ValueEnd))];
        }

        if (target.StyleClass is { } cls)
        {
            return [.. (await context.ProjectXamlAsync(cancellationToken).ConfigureAwait(false))
                .SelectMany(x => x.Document.Elements
                    .Where(e => e.LocalName == "Style" && e.Attribute("Selector")?.Value is { } s && XamlSelectors.Tokens(s).Any(t => t.Kind == '.' && t.Text == cls))
                    .Select(e => Location(x.Path, x.Document, e.Start, e.NameEnd)))];
        }

        if (target.ElementName is { } name && target.Symbol is null)
        {
            var named = context.Document.Elements.FirstOrDefault(e => (e.Attribute("x:Name") ?? e.Attribute("Name"))?.Value == name);
            return named is null ? [] : [Location(context.Path, context.Document, named.Start, named.NameEnd)];
        }

        if (target.Symbol is not { } symbol || context.Project is not { } project)
        {
            return [];
        }

        var locations = await NavigationFeature.LocationsAsync(project.Solution, symbol.Locations, cancellationToken).ConfigureAwait(false);
        if (locations.Length > 0)
        {
            return locations;
        }

        return project.Documents.FirstOrDefault() is { } document
            ? await MetadataFeature.DefinitionAsync(document, symbol, cancellationToken).ConfigureAwait(false)
            : [];
    }

    private static Location Location(string path, XamlDocument document, int start, int end)
    {
        var text = Microsoft.CodeAnalysis.Text.SourceText.From(document.Text);
        return new Location(DocumentUris.FromPath(path), new Range(Positions.ToPosition(text, start), Positions.ToPosition(text, Math.Max(start, end))));
    }

    // ------------------------------------------------------------------ x:Name rename

    /// <summary>The element name at the position (its <c>x:Name</c>, a <c>#Name</c> or an <c>ElementName</c>), or null.</summary>
    public static async Task<PrepareRenameResult?> PrepareRenameAsync(XamlContext context, int offset, CancellationToken cancellationToken)
    {
        var target = await TargetAsync(context, offset, cancellationToken).ConfigureAwait(false);
        return target?.ElementName is { Length: > 0 } name && context.Document.Elements.Any(e => (e.Attribute("x:Name") ?? e.Attribute("Name"))?.Value == name)
            ? new PrepareRenameResult(context.Range(target.Start, target.End), name)
            : null;
    }

    /// <summary>
    /// Renames an element: its <c>x:Name</c>, <c>#Name</c> in bindings and selectors, <c>ElementName=Name</c>, and the
    /// code-behind's uses of the generated field.
    /// </summary>
    public static async Task<WorkspaceEdit?> RenameAsync(XamlContext context, int offset, string newName, Func<string, int?> version, CancellationToken cancellationToken)
    {
        var target = await TargetAsync(context, offset, cancellationToken).ConfigureAwait(false);
        if (target?.ElementName is not { Length: > 0 } name || !IdentifierRegex().IsMatch(newName))
        {
            return null;
        }

        var edits = new List<TextEdit>();
        var pattern = new Regex($@"(?<=#){Regex.Escape(name)}\b|(?<=ElementName\s*=\s*){Regex.Escape(name)}\b", RegexOptions.CultureInvariant);
        foreach (var element in context.Document.Elements)
        {
            foreach (var attribute in element.Attributes.Where(a => a.Value is not null && a.ValueStart >= 0))
            {
                if (attribute.Name is "x:Name" or "Name" && attribute.Value!.Trim() == name)
                {
                    var leading = attribute.Value.Length - attribute.Value.TrimStart().Length;
                    edits.Add(new TextEdit(context.Range(attribute.ValueStart + leading, attribute.ValueStart + leading + name.Length), newName));
                }
                else if (attribute.Value!.Contains('{', StringComparison.Ordinal) || attribute.Name == "Selector")
                {
                    foreach (Match match in pattern.Matches(attribute.Value))
                    {
                        edits.Add(new TextEdit(context.Range(attribute.ValueStart + match.Index, attribute.ValueStart + match.Index + match.Length), newName));
                    }
                }
            }
        }

        var changes = new List<TextDocumentEdit>
        {
            new(new OptionalVersionedTextDocumentIdentifier(DocumentUris.FromPath(context.Path), version(context.Path)), [.. edits]),
        };

        // The code-behind's uses of the generated field.
        if (NameField(context, name) is { } field && context.Project is { } project)
        {
            var references = await SymbolFinder.FindReferencesAsync(field, project.Solution, cancellationToken).ConfigureAwait(false);
            foreach (var group in references.SelectMany(r => r.Locations).GroupBy(l => l.Document.FilePath))
            {
                if (group.Key is not { } file || group.First().Document is SourceGeneratedDocument)
                {
                    continue;
                }

                var text = await group.First().Document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                changes.Add(new TextDocumentEdit(
                    new OptionalVersionedTextDocumentIdentifier(DocumentUris.FromPath(file), version(file)),
                    [.. group.Select(l => new TextEdit(Positions.ToRange(text, l.Location.SourceSpan), newName)).Distinct()]));
            }
        }

        return new WorkspaceEdit([.. changes]);
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();
}
