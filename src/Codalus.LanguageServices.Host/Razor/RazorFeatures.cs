using System.Runtime.CompilerServices;
using Codalus.LanguageServices.Host.Features;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Location = Codalus.LanguageServices.Protocol.Location;
using Range = Codalus.LanguageServices.Protocol.Range;

namespace Codalus.LanguageServices.Host.Razor;

/// <summary>
/// Language features in Razor files (ADR 0023). In C# (an expression, an event handler, an <c>@code</c> block) a request
/// runs on the generated document at the mapped position, and its ranges are mapped back. In markup, Razor's own
/// features: completion of components, their parameters and directives; hover and Go to Definition on components and
/// parameters.
/// </summary>
internal sealed class RazorFeatures
{
    private const string ComponentInterface = "Microsoft.AspNetCore.Components.IComponent";
    private const string ParameterAttribute = "Microsoft.AspNetCore.Components.ParameterAttribute";

    private static readonly string[] s_directives =
    [
        "page", "code", "using", "inject", "inherits", "implements", "layout", "attribute", "rendermode", "typeparam",
        "namespace", "preservewhitespace", "functions", "model", "addTagHelper", "removeTagHelper", "section",
    ];

    private static readonly string[] s_directiveAttributes =
    [
        "@onclick", "@onchange", "@oninput", "@onsubmit", "@onkeydown", "@onkeyup", "@onfocus", "@onblur", "@bind", "@bind-value", "@bind:event",
        "@bind:after", "@ref", "@key", "@attributes", "@formname", "@rendermode", "@onclick:preventDefault", "@onclick:stopPropagation",
    ];

    private static readonly string[] s_htmlElements =
    [
        "a", "article", "aside", "button", "code", "div", "em", "footer", "form", "h1", "h2", "h3", "h4", "header", "img", "input", "label", "li",
        "main", "nav", "ol", "option", "p", "pre", "section", "select", "span", "strong", "table", "tbody", "td", "textarea", "th", "thead", "tr", "ul",
    ];

    private readonly CompletionFeature _completion;
    private readonly ConditionalWeakTable<Compilation, IReadOnlyList<INamedTypeSymbol>> _components = new();

    private readonly Action<string> _log;

    public RazorFeatures(CompletionFeature completion, Action<string> log)
    {
        _completion = completion;
        _log = log;
    }

    // ------------------------------------------------------------------ completion

    public async Task<CompletionList?> CompleteAsync(RazorMapping mapping, int offset, CompletionContext? context, CancellationToken cancellationToken)
    {
        if (mapping.ToGenerated(offset) is { } generated)
        {
            var list = await _completion.CompleteAsync(generated.Document, generated.Offset, context, cancellationToken).ConfigureAwait(false);
            var mapped = list is null ? null : MapCompletion(generated.Part, list);

            // "@word" at the start of a line is an implicit expression until it is a known directive: offer both.
            if (RazorContext.At(mapping.RazorText.ToString(), offset).Kind == RazorContextKind.Directive
                && await MarkupCompletionAsync(mapping, offset, cancellationToken).ConfigureAwait(false) is { } directives)
            {
                return new CompletionList(mapped?.IsIncomplete ?? false, [.. directives.Items, .. mapped?.Items ?? []]);
            }

            return mapped;
        }

        return await MarkupCompletionAsync(mapping, offset, cancellationToken).ConfigureAwait(false);
    }

    private CompletionList MapCompletion(RazorPart part, CompletionList list)
    {
        _completion.MapLastList(part.ToRazor);
        var items = list.Items.Select(item => item with
        {
            TextEdit = item.TextEdit is { } edit ? part.ToRazor(edit.Range) is { } range ? edit with { Range = range } : null : null,
            InsertText = item.TextEdit is { } original && part.ToRazor(original.Range) is null ? original.NewText : item.InsertText,
            AdditionalTextEdits = null,
        }).ToArray();
        return new CompletionList(list.IsIncomplete, items)
        {
            ItemDefaults = list.ItemDefaults?.EditRange is { } editRange && part.ToRazor(editRange) is { } mapped ? new CompletionItemDefaults(mapped) : null,
        };
    }

    private async Task<CompletionList?> MarkupCompletionAsync(RazorMapping mapping, int offset, CancellationToken cancellationToken)
    {
        var text = mapping.RazorText.ToString();
        var context = RazorContext.At(text, offset);
        var word = new Range(Positions.ToPosition(mapping.RazorText, context.WordStart), Positions.ToPosition(mapping.RazorText, offset));
        CompletionItem Item(string label, int kind, string? detail = null, string? insert = null, string? sort = null, string? documentation = null) => new(label)
        {
            Kind = kind,
            Detail = detail,
            SortText = sort ?? label,
            TextEdit = new TextEdit(word, insert ?? label),
            InsertTextFormat = insert is not null && insert.Contains('$', StringComparison.Ordinal) ? InsertTextFormat.Snippet : null,
            Documentation = documentation is null ? null : MarkupContent.Markdown(documentation),
        };

        switch (context.Kind)
        {
            case RazorContextKind.Directive:
                return new CompletionList(false, [.. s_directives.Select(d => Item(d, CompletionItemKind.Keyword, "Razor directive"))]);

            case RazorContextKind.TagName:
            {
                var components = await ComponentsAsync(mapping.Razor.Project, cancellationToken).ConfigureAwait(false);
                var items = components.Select(c => Item(c.Name, CompletionItemKind.Class, c.ToDisplayString(), sort: "0" + c.Name, documentation: Summary(c)))
                    .Concat(s_htmlElements.Select(e => Item(e, CompletionItemKind.Property, "HTML element", sort: "1" + e)));
                return new CompletionList(false, [.. items.DistinctBy(i => i.Label)]);
            }

            case RazorContextKind.Attribute:
            {
                var items = new List<CompletionItem>();
                if (await ComponentAsync(mapping.Razor.Project, context.TagName!, cancellationToken).ConfigureAwait(false) is { } component)
                {
                    items.AddRange(Parameters(component).Where(p => !context.ExistingAttributes.Contains(p.Name))
                        .Select(p => Item(p.Name, CompletionItemKind.Property, $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {component.Name}.{p.Name}",
                            insert: p.Name + "=\"$1\"", sort: "0" + p.Name, documentation: Summary(p))));
                }

                items.AddRange(s_directiveAttributes.Where(a => !context.ExistingAttributes.Contains(a)).Select(a => Item(a, CompletionItemKind.Keyword, "Blazor directive attribute", sort: "1" + a)));
                return new CompletionList(false, [.. items]);
            }

            default:
                return null;
        }
    }

    // ------------------------------------------------------------------ hover and definition

    public async Task<Hover?> HoverAsync(RazorMapping mapping, int offset, CancellationToken cancellationToken)
    {
        if (mapping.ToGenerated(offset) is { } generated)
        {
            var hover = await HoverFeature.HoverAsync(generated.Document, generated.Offset, cancellationToken).ConfigureAwait(false);
            return hover is null ? null : hover with { Range = hover.Range is { } range ? generated.Part.ToRazor(range) : null };
        }

        if (await SymbolAtAsync(mapping, offset, cancellationToken).ConfigureAwait(false) is not var (symbol, span))
        {
            return null;
        }

        var display = symbol is IPropertySymbol property
            ? $"{property.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {property.ContainingType.Name}.{property.Name}"
            : $"component {symbol.ToDisplayString()}";
        var summary = Summary(symbol);
        return new Hover(MarkupContent.Markdown("```csharp\n" + display + "\n```" + (summary is null ? string.Empty : "\n\n" + summary)), Positions.ToRange(mapping.RazorText, span));
    }

    public async Task<Location[]> DefinitionAsync(RazorMapping mapping, int offset, CancellationToken cancellationToken)
    {
        if (mapping.ToGenerated(offset) is { } generated)
        {
            return await NavigationFeature.DefinitionAsync(generated.Document, generated.Offset, cancellationToken).ConfigureAwait(false);
        }

        return await SymbolAtAsync(mapping, offset, cancellationToken).ConfigureAwait(false) is var (symbol, _)
            ? await NavigationFeature.LocationsAsync(mapping.Razor.Project.Solution, symbol.Locations, cancellationToken).ConfigureAwait(false)
            : [];
    }

    public async Task<DocumentHighlight[]> HighlightsAsync(RazorMapping mapping, int offset, CancellationToken cancellationToken)
    {
        if (mapping.ToGenerated(offset) is not { } generated)
        {
            return [];
        }

        var highlights = await NavigationFeature.HighlightsAsync(generated.Document, generated.Offset, cancellationToken).ConfigureAwait(false);
        return [.. highlights.Select(h => generated.Part.ToRazor(h.Range) is { } range ? h with { Range = range } : null).OfType<DocumentHighlight>()];
    }

    /// <summary>The component or parameter named at <paramref name="offset"/> in markup, with the name's span.</summary>
    private async Task<(ISymbol Symbol, TextSpan Span)?> SymbolAtAsync(RazorMapping mapping, int offset, CancellationToken cancellationToken)
    {
        var text = mapping.RazorText.ToString();
        if (RazorContext.NameAt(text, offset) is not var (name, span, isTag, tagName))
        {
            return null;
        }

        var component = await ComponentAsync(mapping.Razor.Project, isTag ? name : tagName!, cancellationToken).ConfigureAwait(false);
        if (component is null)
        {
            return null;
        }

        if (isTag)
        {
            return (component, span);
        }

        return Parameters(component).FirstOrDefault(p => p.Name == name) is { } parameter ? (parameter, span) : null;
    }

    // ------------------------------------------------------------------ components

    private async Task<IReadOnlyList<INamedTypeSymbol>> ComponentsAsync(Project project, CancellationToken cancellationToken)
    {
        if (await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is not { } compilation)
        {
            return [];
        }

        return _components.GetValue(compilation, FindComponents);
    }

    private async Task<INamedTypeSymbol?> ComponentAsync(Project project, string name, CancellationToken cancellationToken)
    {
        var components = await ComponentsAsync(project, cancellationToken).ConfigureAwait(false);
        return components.FirstOrDefault(c => c.Name == name) ?? components.FirstOrDefault(c => c.ToDisplayString() == name);
    }

    /// <summary>The components of the project and of the component libraries it references (the framework's and its own).</summary>
    private static IReadOnlyList<INamedTypeSymbol> FindComponents(Compilation compilation)
    {
        if (compilation.GetTypeByMetadataName(ComponentInterface) is not { } component)
        {
            return [];
        }

        var result = new List<INamedTypeSymbol>();
        var assemblies = new List<IAssemblySymbol> { compilation.Assembly };
        assemblies.AddRange(compilation.SourceModule.ReferencedAssemblySymbols.Where(a =>
            a.Name.StartsWith("Microsoft.AspNetCore.Components", StringComparison.Ordinal)
            || a.Modules.Any(m => m.ReferencedAssemblySymbols.Any(r => r.Name == "Microsoft.AspNetCore.Components")) && !a.Name.StartsWith("Microsoft.", StringComparison.Ordinal) && !a.Name.StartsWith("System.", StringComparison.Ordinal)));
        foreach (var assembly in assemblies)
        {
            Collect(assembly.GlobalNamespace, component, result, sameAssembly: SymbolEqualityComparer.Default.Equals(assembly, compilation.Assembly));
        }

        return [.. result.OrderBy(c => c.Name, StringComparer.Ordinal)];
    }

    private static void Collect(INamespaceSymbol ns, INamedTypeSymbol component, List<INamedTypeSymbol> result, bool sameAssembly)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            if (!type.IsAbstract && !type.IsStatic && (type.DeclaredAccessibility == Accessibility.Public || sameAssembly && type.DeclaredAccessibility == Accessibility.Internal)
                && !type.Name.StartsWith("__", StringComparison.Ordinal) && type.AllInterfaces.Contains(component, SymbolEqualityComparer.Default))
            {
                result.Add(type);
            }
        }

        foreach (var nested in ns.GetNamespaceMembers())
        {
            Collect(nested, component, result, sameAssembly);
        }
    }

    private static IEnumerable<IPropertySymbol> Parameters(INamedTypeSymbol component)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var type = component; type is not null; type = type.BaseType)
        {
            foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.DeclaredAccessibility == Accessibility.Public && property.SetMethod is not null && seen.Add(property.Name)
                    && property.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == ParameterAttribute))
                {
                    yield return property;
                }
            }
        }
    }

    private static string? Summary(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            return System.Xml.Linq.XElement.Parse(xml).Element("summary")?.Value.Trim() is { Length: > 0 } summary
                ? System.Text.RegularExpressions.Regex.Replace(summary, @"\s+", " ")
                : null;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }
}

internal enum RazorContextKind
{
    None,
    Directive,
    TagName,
    Attribute,
}

/// <summary>Where a position is in Razor markup: a directive after <c>@</c>, a tag name after <c>&lt;</c>, or a tag's attributes.</summary>
internal sealed record RazorContext(RazorContextKind Kind, int WordStart, string? TagName, IReadOnlySet<string> ExistingAttributes)
{
    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or ':' or '.';

    public static RazorContext At(string text, int offset)
    {
        var wordStart = offset;
        while (wordStart > 0 && (IsNameChar(text[wordStart - 1]) || text[wordStart - 1] == '@' && wordStart - 1 > 0 && !char.IsWhiteSpace(text[wordStart - 2]) && text[wordStart - 2] != '<'))
        {
            wordStart--;
        }

        var empty = new HashSet<string>(StringComparer.Ordinal);

        // A directive: "@" at the start of a line (Razor directives), followed by the word.
        if (wordStart > 0 && text[wordStart - 1] == '@')
        {
            var lineStart = text.LastIndexOf('\n', Math.Max(0, wordStart - 2)) + 1;
            if (text.AsSpan(lineStart, wordStart - 1 - lineStart).Trim().Length == 0)
            {
                return new RazorContext(RazorContextKind.Directive, wordStart, null, empty);
            }
        }

        // Inside a tag: the last '<' before the word, with no '>' after it.
        var open = text.LastIndexOf('<', Math.Max(0, wordStart - 1));
        if (open < 0 || text.IndexOf('>', open) is var close and >= 0 && close < wordStart)
        {
            return new RazorContext(RazorContextKind.None, wordStart, null, empty);
        }

        var nameEnd = open + 1;
        while (nameEnd < text.Length && IsNameChar(text[nameEnd]))
        {
            nameEnd++;
        }

        if (wordStart == open + 1)
        {
            return new RazorContext(RazorContextKind.TagName, wordStart, null, empty);
        }

        var tag = text[(open + 1)..nameEnd];
        if (tag.Length == 0 || tag.StartsWith('/') || wordStart <= nameEnd || InAttributeValue(text, nameEnd, wordStart))
        {
            return new RazorContext(RazorContextKind.None, wordStart, null, empty);
        }

        var attributes = System.Text.RegularExpressions.Regex.Matches(text[nameEnd..wordStart], @"(@?[\w:\-.]+)\s*=")
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        return new RazorContext(RazorContextKind.Attribute, wordStart, tag, attributes);
    }

    /// <summary>Whether <paramref name="offset"/> is inside a quoted attribute value of the tag.</summary>
    private static bool InAttributeValue(string text, int start, int offset)
    {
        char? quote = null;
        for (var i = start; i < offset; i++)
        {
            if (quote is null && text[i] is '"' or '\'')
            {
                quote = text[i];
            }
            else if (quote == text[i])
            {
                quote = null;
            }
        }

        return quote is not null;
    }

    /// <summary>The tag or attribute name at <paramref name="offset"/>: (name, span, is it the tag, the tag's name).</summary>
    public static (string Name, TextSpan Span, bool IsTag, string? TagName)? NameAt(string text, int offset)
    {
        var start = offset;
        while (start > 0 && IsNameChar(text[start - 1]))
        {
            start--;
        }

        var end = offset;
        while (end < text.Length && IsNameChar(text[end]))
        {
            end++;
        }

        if (end == start)
        {
            return null;
        }

        var name = text[start..end];
        if (start > 0 && text[start - 1] == '<' || start > 1 && text[start - 1] == '/' && text[start - 2] == '<')
        {
            return (name, TextSpan.FromBounds(start, end), true, null);
        }

        var context = At(text, start);
        return context.Kind == RazorContextKind.Attribute && end < text.Length && text[end] == '='
            ? (name, TextSpan.FromBounds(start, end), false, context.TagName)
            : null;
    }
}
