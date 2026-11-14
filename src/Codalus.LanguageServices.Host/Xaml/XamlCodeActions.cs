using System.Text;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using LspCodeAction = Codalus.LanguageServices.Protocol.CodeAction;
using Range = Codalus.LanguageServices.Protocol.Range;

namespace Codalus.LanguageServices.Host.Xaml;

/// <summary>
/// XAML's code actions (M64, ADR 0103), listed with their edits (no resolve step):
/// <list type="bullet">
/// <item><b>Extract Resource</b>: an attribute's value moves to a keyed resource in the file's resources, or in
/// <c>App.axaml</c>/<c>App.xaml</c>, and the attribute looks it up; a variant replaces every equal value of the same
/// property in the file.</item>
/// <item><b>Extract Style</b>: an element's plain property attributes move into a style's setters: a class selector in
/// Avalonia (the element gets the class), a keyed <c>Style</c> with a <c>TargetType</c> in WPF (the element gets
/// <c>Style="{StaticResource …}"</c>).</item>
/// <item><b>Generate from use</b>: a binding path the data type lacks becomes a property of the view model of
/// <c>x:DataType</c> or <c>d:DataContext</c> (an <c>[ObservableProperty]</c> field with CommunityToolkit.Mvvm); a
/// command becomes an <c>ICommand</c> property, or a <c>[RelayCommand]</c> method with CommunityToolkit.Mvvm; a
/// converter resource that doesn't exist becomes a converter class and its resource.</item>
/// </list>
/// The new key, class or member gets a name from what it replaces; rename it afterwards (the rename crosses C# and XAML).
/// </summary>
internal static class XamlCodeActions
{
    public const string ExtractKind = "refactor.extract";

    public static async Task<LspCodeAction[]> ActionsAsync(XamlContext context, Range range, Func<string, int?> versionOf, CancellationToken cancellationToken)
    {
        var offset = context.OffsetOf(range.Start);
        var element = context.Document.ElementAt(offset);
        if (element is null || context.Types is null)
        {
            return [];
        }

        var actions = new List<LspCodeAction>();
        var attribute = element.Attributes.FirstOrDefault(a => a.ContainsValue(offset));
        if (attribute is not null)
        {
            actions.AddRange(await GenerateAsync(context, element, attribute, offset, versionOf, cancellationToken).ConfigureAwait(false));
            actions.AddRange(await ExtractResourceAsync(context, element, attribute, versionOf, cancellationToken).ConfigureAwait(false));
        }

        if (!element.IsPropertyElement && offset >= element.Start && (element.StartTagEnd < 0 || offset < element.StartTagEnd))
        {
            actions.AddRange(await ExtractStyleAsync(context, element, versionOf, cancellationToken).ConfigureAwait(false));
        }

        return [.. actions];
    }

    // ------------------------------------------------------------------ Extract Resource

    private static async Task<IEnumerable<LspCodeAction>> ExtractResourceAsync(XamlContext context, XamlElement element, XamlAttribute attribute, Func<string, int?> versionOf, CancellationToken cancellationToken)
    {
        var value = attribute.Value?.Trim();
        if (string.IsNullOrEmpty(value) || value.StartsWith('{') || attribute.Prefix is not null || attribute.Name is "Name" or "Classes" or "Style" or "Selector" or "Property"
            || element.IsPropertyElement || XamlCompletion.Member(context, element, context.TypeOf(element), attribute.Name) is not { Kind: not XamlMemberKind.Event } member)
        {
            return [];
        }

        var keys = (await XamlCompletion.ResourceKeysAsync(context, cancellationToken).ConfigureAwait(false)).Select(k => k.Key).ToHashSet(StringComparer.Ordinal);
        var key = Unique(element.LocalName.Replace(".", string.Empty, StringComparison.Ordinal) + attribute.Name.Replace(".", string.Empty, StringComparison.Ordinal), keys.Contains);
        if (ResourceElement(context, member.ValueType, key, value) is not { } resource)
        {
            return [];
        }

        var lookup = "{StaticResource " + key + "}";
        var equal = context.Document.Elements.SelectMany(e => e.Attributes.Where(a => a != attribute && a.Name == attribute.Name && a.Value?.Trim() == value && a.ValueStart >= 0)).ToList();
        var actions = new List<LspCodeAction>();

        // In this file.
        if (Insert(context, "Resources", resource.Element) is { } here)
        {
            var edits = new List<(int Start, int End, string Text)> { (attribute.ValueStart, attribute.ValueEnd, lookup), here };
            edits.AddRange(Namespaces(context, resource.Namespaces));
            actions.Add(Action($"Extract Resource '{key}'", ExtractKind, Edit(context, edits, versionOf)));
            if (equal.Count > 0)
            {
                edits.AddRange(equal.Select(a => (a.ValueStart, a.ValueEnd, lookup)));
                actions.Add(Action($"Extract Resource '{key}' and use it for {equal.Count} equal value{(equal.Count == 1 ? string.Empty : "s")}", ExtractKind, Edit(context, edits, versionOf)));
            }
        }

        // In the app's resources.
        if (await AppContextAsync(context, cancellationToken).ConfigureAwait(false) is { } app && app.Path != context.Path
            && ResourceElement(app, member.ValueType, key, value) is { } appResource && Insert(app, "Resources", appResource.Element) is { } there)
        {
            var appEdits = new List<(int, int, string)> { there };
            appEdits.AddRange(Namespaces(app, appResource.Namespaces));
            var edit = Edit(context, [(attribute.ValueStart, attribute.ValueEnd, lookup)], versionOf);
            actions.Add(Action($"Extract Resource '{key}' to {Path.GetFileName(app.Path)}", ExtractKind,
                edit with { DocumentChanges = [.. edit.DocumentChanges, .. Edit(app, appEdits, versionOf).DocumentChanges] }));
        }

        return actions;
    }

    /// <summary>The resource element for a value of <paramref name="type"/>, and the XML namespaces it needs; null when there is none.</summary>
    private static (string Element, IReadOnlyList<(string Prefix, string Namespace)> Namespaces)? ResourceElement(XamlContext context, ITypeSymbol? type, string key, string value)
    {
        if (type is null)
        {
            return null;
        }

        var wpf = context.IsWpfStyle;
        var system = wpf ? "sys" : "x";
        var namespaces = wpf ? new[] { ("sys", "clr-namespace:System;assembly=mscorlib") } : [];
        string? Primitive(string name) => $"<{system}:{name} x:Key=\"{key}\">{value}</{system}:{name}>";
        switch (type.SpecialType)
        {
            case SpecialType.System_String:
                return (Primitive("String")!, namespaces);
            case SpecialType.System_Double:
                return (Primitive("Double")!, namespaces);
            case SpecialType.System_Int32:
                return (Primitive("Int32")!, namespaces);
            case SpecialType.System_Boolean:
                return (Primitive("Boolean")!, namespaces);
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            return null;
        }

        if (type.Name is "IBrush" or "Brush" or "IImmutableBrush")
        {
            return value.StartsWith('#') || value.All(char.IsLetter) ? ($"<SolidColorBrush x:Key=\"{key}\" Color=\"{value}\" />", []) : null;
        }

        // A type of the default namespace that XAML writes as text: Thickness, CornerRadius, Color, FontFamily…
        var xmlns = context.Document.Root is { } root ? XamlDocument.ResolvePrefix(root, null) : null;
        return type is INamedTypeSymbol named && context.Types?.ResolveType(xmlns, named.Name) is { } resolved && SymbolEqualityComparer.Default.Equals(resolved, named)
            ? ($"<{named.Name} x:Key=\"{key}\">{value}</{named.Name}>", [])
            : null;
    }

    /// <summary>The project's application XAML (its root is an <c>Application</c>): Avalonia's App.axaml, WPF's App.xaml.</summary>
    private static async Task<XamlContext?> AppContextAsync(XamlContext context, CancellationToken cancellationToken)
    {
        if (context.Document.Root?.LocalName == "Application")
        {
            return context;
        }

        if (context.Project is not { } project)
        {
            return null;
        }

        foreach (var additional in project.AdditionalDocuments)
        {
            if (additional.FilePath is not { } path || !XamlContext.IsXaml(path))
            {
                continue;
            }

            var text = await additional.GetTextAsync(cancellationToken).ConfigureAwait(false);
            if (XamlDocument.Parse(text.ToString()).Root?.LocalName == "Application")
            {
                return await XamlContext.CreateAsync(project.Solution, path, null, cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ Extract Style

    private static async Task<IEnumerable<LspCodeAction>> ExtractStyleAsync(XamlContext context, XamlElement element, Func<string, int?> versionOf, CancellationToken cancellationToken)
    {
        var type = context.TypeOf(element);
        if (type is null || element == context.Document.Root || element.Attributes.Count == 0)
        {
            return [];
        }

        var moved = element.Attributes.Where(a => a.Value is not null && a.ValueStart >= 0 && Movable(context, element, type, a)).ToList();
        var wpf = context.IsWpfStyle;
        if (moved.Count == 0 || (wpf && element.Attribute("Style") is not null))
        {
            return [];
        }

        var typeName = element.Name; // as written, with its prefix
        string lookupAttribute;
        string style;
        string container;
        string title;
        var setterIndent = "{unit}";
        var setters = string.Join("{nl}", moved.Select(a => $"{setterIndent}<Setter Property=\"{a.Name}\" Value=\"{a.Value}\" />"));
        if (wpf)
        {
            var keys = (await XamlCompletion.ResourceKeysAsync(context, cancellationToken).ConfigureAwait(false)).Select(k => k.Key).ToHashSet(StringComparer.Ordinal);
            var key = Unique(element.LocalName + "Style", keys.Contains);
            style = $"<Style x:Key=\"{key}\" TargetType=\"{typeName}\">{{nl}}{setters}{{nl}}</Style>";
            lookupAttribute = $" Style=\"{{StaticResource {key}}}\"";
            container = "Resources";
            title = $"Extract Style '{key}'";
        }
        else
        {
            var classes = (await XamlCompletion.StyleClassesAsync(context, cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
            var name = Unique(char.ToLowerInvariant(element.LocalName[0]) + element.LocalName[1..] + "Style", classes.Contains);
            style = $"<Style Selector=\"{typeName.Replace(':', '|')}.{name}\">{{nl}}{setters}{{nl}}</Style>";
            lookupAttribute = name;
            container = "Styles";
            title = $"Extract Style '.{name}'";
        }

        if (Insert(context, container, style) is not { } inserted)
        {
            return [];
        }

        // The start tag's attributes again, without the moved ones, with the class (Avalonia) or the style (WPF).
        var text = context.TextString;
        var builder = new StringBuilder();
        var at = element.NameEnd;
        var classesAttribute = wpf ? null : element.Attribute("Classes");
        foreach (var attribute in element.Attributes)
        {
            var end = attribute.ValueEnd >= 0 ? Math.Min(text.Length, attribute.ValueEnd + 1) : attribute.NameEnd;
            if (!moved.Contains(attribute))
            {
                var segment = text[at..end];
                if (attribute == classesAttribute)
                {
                    var close = attribute.ValueEnd - at;
                    segment = segment[..close] + (attribute.Value!.Trim().Length > 0 ? " " : string.Empty) + lookupAttribute + segment[close..];
                }

                builder.Append(segment);
            }

            at = end;
        }

        if (wpf)
        {
            builder.Append(lookupAttribute);
        }
        else if (classesAttribute is null)
        {
            builder.Append(" Classes=\"").Append(lookupAttribute).Append('"');
        }

        return [Action(title, ExtractKind, Edit(context, [(element.NameEnd, at, builder.ToString()), inserted], versionOf))];
    }

    /// <summary>An attribute a style's setter can carry: a property of the element's own type, not a name, class, style, event, directive or attached property.</summary>
    private static bool Movable(XamlContext context, XamlElement element, INamedTypeSymbol type, XamlAttribute attribute) =>
        attribute.Prefix is null && !attribute.Name.Contains('.', StringComparison.Ordinal) && !attribute.Name.StartsWith("xmlns", StringComparison.Ordinal)
        && attribute.Name is not ("Name" or "Classes" or "Style" or "Theme")
        && XamlCompletion.Member(context, element, type, attribute.Name) is { Kind: not XamlMemberKind.Event, Settable: true };

    // ------------------------------------------------------------------ generate from use

    private static async Task<IEnumerable<LspCodeAction>> GenerateAsync(XamlContext context, XamlElement element, XamlAttribute attribute, int offset, Func<string, int?> versionOf, CancellationToken cancellationToken)
    {
        var value = attribute.Value ?? string.Empty;
        var caret = offset - attribute.ValueStart;
        if (MarkupExtensionContext.At(value, caret) is not { } extension || context.Project is not { } project)
        {
            return [];
        }

        if (extension.IsResource)
        {
            return await ConverterAsync(context, extension, value, caret, versionOf, cancellationToken).ConfigureAwait(false);
        }

        if (!extension.IsPath || extension.IsXBind)
        {
            return [];
        }

        // The path segment at the caret and the path before it.
        var start = extension.ArgumentStart;
        var segmentStart = caret;
        while (segmentStart > start && (char.IsLetterOrDigit(value[segmentStart - 1]) || value[segmentStart - 1] == '_'))
        {
            segmentStart--;
        }

        var segmentEnd = caret;
        while (segmentEnd < value.Length && (char.IsLetterOrDigit(value[segmentEnd]) || value[segmentEnd] == '_'))
        {
            segmentEnd++;
        }

        var name = value[segmentStart..segmentEnd];
        var prefix = segmentStart > start && value[segmentStart - 1] == '.' ? value[start..(segmentStart - 1)] : segmentStart == start ? string.Empty : null;
        if (prefix is null || !SyntaxFacts.IsValidIdentifier(name) || prefix.StartsWith('#') || prefix.StartsWith('$')
            || XamlBindingPaths.Resolve(context, element, prefix, value) is not INamedTypeSymbol { TypeKind: TypeKind.Class } owner
            || XamlTypeSystem.BindableMembers(owner).Any(m => m.Name == name)
            || owner.DeclaringSyntaxReferences.Select(r => r.GetSyntax(cancellationToken)).OfType<TypeDeclarationSyntax>()
                .FirstOrDefault(d => !XamlRename.IsGenerated(d.SyntaxTree.FilePath) && project.Solution.GetDocument(d.SyntaxTree) is not null) is not { } declaration)
        {
            return [];
        }

        var document = project.Solution.GetDocument(declaration.SyntaxTree)!;
        var compilation = context.Compilation!;
        var toolkitProperty = compilation.GetTypeByMetadataName("CommunityToolkit.Mvvm.ComponentModel.ObservablePropertyAttribute") is not null
            && Inherits(owner, "CommunityToolkit.Mvvm.ComponentModel.ObservableObject");
        var toolkitCommand = compilation.GetTypeByMetadataName("CommunityToolkit.Mvvm.Input.RelayCommandAttribute") is not null;
        var nullable = declaration.SyntaxTree.Options is CSharpParseOptions && compilation.Options.NullableContextOptions != NullableContextOptions.Disable;
        var target = XamlCompletion.Member(context, element, context.TypeOf(element), attribute.Name);
        var isCommand = attribute.Name == "Command" || target?.ValueType?.Name == "ICommand";

        string member;
        string title;
        string? import = null;
        if (isCommand && toolkitCommand && name.EndsWith("Command", StringComparison.Ordinal) && name.Length > "Command".Length)
        {
            var method = name[..^"Command".Length];
            member = $"[RelayCommand]{{nl}}private void {method}(){{nl}}{{{{nl}}}}";
            import = "CommunityToolkit.Mvvm.Input";
            title = $"Generate [RelayCommand] method '{method}' in {owner.Name}";
        }
        else if (isCommand)
        {
            member = $"public System.Windows.Input.ICommand{(nullable ? "?" : string.Empty)} {name} {{ get; set; }}";
            title = $"Generate command '{name}' in {owner.Name}";
        }
        else
        {
            var (typeName, initializer) = PropertyType(target?.ValueType, nullable);
            if (toolkitProperty)
            {
                member = $"[ObservableProperty]{{nl}}private {typeName} _{char.ToLowerInvariant(name[0])}{name[1..]}{initializer};";
                import = "CommunityToolkit.Mvvm.ComponentModel";
                title = $"Generate [ObservableProperty] '{name}' in {owner.Name}";
            }
            else
            {
                member = $"public {typeName} {name} {{ get; set; }}{initializer}{(initializer.Length > 0 ? ";" : string.Empty)}";
                title = $"Generate property '{name}' in {owner.Name}";
            }
        }

        var edit = await AddMemberAsync(document, declaration, member, import, makePartial: import is not null, versionOf, cancellationToken).ConfigureAwait(false);
        return edit is null ? [] : [Action(title, CodeActionKind.QuickFix, edit)];
    }

    private static bool Inherits(INamedTypeSymbol type, string baseName)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == baseName)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The C# type of a new property for a XAML property of <paramref name="type"/>, and its initializer.</summary>
    private static (string Type, string Initializer) PropertyType(ITypeSymbol? type, bool nullable) => type?.SpecialType switch
    {
        SpecialType.System_String => nullable ? ("string", " = string.Empty") : ("string", string.Empty),
        SpecialType.System_Boolean => ("bool", string.Empty),
        SpecialType.System_Int32 => ("int", string.Empty),
        SpecialType.System_Double => ("double", string.Empty),
        SpecialType.System_Decimal => ("decimal", string.Empty),
        SpecialType.System_Int64 => ("long", string.Empty),
        _ when type is { SpecialType: SpecialType.None } && type.ToDisplayString() is "bool?" or "System.Nullable<bool>" => ("bool?", string.Empty),
        _ => (nullable ? "object?" : "object", string.Empty),
    };

    /// <summary>A converter resource that doesn't exist: the converter class (a new file) and its resource in this file.</summary>
    private static async Task<IEnumerable<LspCodeAction>> ConverterAsync(XamlContext context, MarkupExtensionContext extension, string value, int caret, Func<string, int?> versionOf, CancellationToken cancellationToken)
    {
        var start = extension.ArgumentStart;
        var end = caret;
        while (end < value.Length && value[end] is not ('}' or ',' or ' '))
        {
            end++;
        }

        var key = value[start..end].Trim();
        var before = value[..Math.Max(0, extension.NameStart - 1)].TrimEnd();
        if (!SyntaxFacts.IsValidIdentifier(key) || !(before.EndsWith("Converter=", StringComparison.Ordinal) || key.EndsWith("Converter", StringComparison.Ordinal))
            || context.Project is not { FilePath: { } projectFile } project
            || (await XamlCompletion.ResourceKeysAsync(context, cancellationToken).ConfigureAwait(false)).Any(k => k.Key == key))
        {
            return [];
        }

        var folder = Path.Combine(Path.GetDirectoryName(projectFile)!, "Converters");
        var file = Path.Combine(folder, key + ".cs");
        if (File.Exists(file) || project.Documents.Any(d => string.Equals(d.FilePath, file, StringComparison.OrdinalIgnoreCase)))
        {
            return [];
        }

        var rootNamespace = RootNamespace(project);
        var ns = rootNamespace + ".Converters";
        var wpf = context.IsWpfStyle;
        var nullable = context.Compilation?.Options.NullableContextOptions is { } options && options != NullableContextOptions.Disable && !wpf;
        var q = nullable ? "?" : string.Empty;
        var nl = Newline(context.TextString);
        var source = string.Join(nl,
        [
            "using System;",
            "using System.Globalization;",
            wpf ? "using System.Windows.Data;" : "using Avalonia.Data.Converters;",
            string.Empty,
            $"namespace {ns};",
            string.Empty,
            $"public sealed class {key} : IValueConverter",
            "{",
            $"    public object{q} Convert(object{q} value, Type targetType, object{q} parameter, CultureInfo culture) => value;",
            string.Empty,
            $"    public object{q} ConvertBack(object{q} value, Type targetType, object{q} parameter, CultureInfo culture) => throw new NotSupportedException();",
            "}",
            string.Empty,
        ]);

        // The resource, with a prefix for the converters' namespace.
        var clr = wpf ? "clr-namespace:" + ns : "using:" + ns;
        var root = context.Document.Root!;
        var prefix = XamlDocument.Prefixes(root).FirstOrDefault(p => p.Value == clr).Key;
        var namespaces = new List<(string, string)>();
        if (prefix is null)
        {
            prefix = Unique("converters", p => XamlDocument.Prefixes(root).ContainsKey(p));
            namespaces.Add((prefix, clr));
        }

        if (Insert(context, "Resources", $"<{prefix}:{key} x:Key=\"{key}\" />") is not { } inserted)
        {
            return [];
        }

        var xamlEdits = new List<(int, int, string)> { inserted };
        xamlEdits.AddRange(Namespaces(context, namespaces));
        var edit = Edit(context, xamlEdits, versionOf);
        var origin = new Position(0, 0);
        var created = new TextDocumentEdit(new OptionalVersionedTextDocumentIdentifier(DocumentUris.FromPath(file), null), [new TextEdit(new Range(origin, origin), source)]);
        return [Action($"Generate converter '{key}'", CodeActionKind.QuickFix, edit with { DocumentChanges = [created, .. edit.DocumentChanges] })];
    }

    /// <summary>The project's root namespace: MSBuild's (when the analyzer config has it), else its assembly's name.</summary>
    private static string RootNamespace(Project project) =>
        project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.RootNamespace", out var value) && value.Length > 0
            ? value
            : project.DefaultNamespace is { Length: > 0 } defaultNamespace ? defaultNamespace : project.AssemblyName;

    /// <summary>A member at the end of a C# type (and the type made partial, a using added, for CommunityToolkit.Mvvm's generators).</summary>
    private static async Task<WorkspaceEdit?> AddMemberAsync(Document document, TypeDeclarationSyntax declaration, string member, string? import, bool makePartial, Func<string, int?> versionOf, CancellationToken cancellationToken)
    {
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var source = text.ToString();
        var nl = Newline(source);
        var typeIndent = Indent(source, declaration.SpanStart);
        var memberIndent = declaration.Members.FirstOrDefault() is { } first ? Indent(source, first.SpanStart) : typeIndent + "    ";
        var lines = member.Replace("{nl}", "\n", StringComparison.Ordinal).Split('\n').Select(l => memberIndent + l);
        var close = declaration.CloseBraceToken.SpanStart;
        var closeLine = text.Lines.GetLineFromPosition(close);
        var edits = new List<Protocol.TextEdit>();
        var lineStart = closeLine.Start;
        var onItsOwnLine = string.IsNullOrWhiteSpace(source[lineStart..close]);
        var inserted = (declaration.Members.Count > 0 ? nl : string.Empty) + string.Join(nl, lines) + nl;
        edits.Add(onItsOwnLine
            ? new Protocol.TextEdit(Positions.ToRange(text, new TextSpan(lineStart, 0)), inserted)
            : new Protocol.TextEdit(Positions.ToRange(text, new TextSpan(close, 0)), nl + inserted + typeIndent));

        if (makePartial && !declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            edits.Add(new Protocol.TextEdit(Positions.ToRange(text, new TextSpan(declaration.Keyword.SpanStart, 0)), "partial "));
        }

        if (import is not null && await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is CompilationUnitSyntax root
            && !root.Usings.Any(u => u.Name?.ToString() == import)
            && !(await document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is { } compilation && GlobalUsings(compilation).Contains(import)))
        {
            edits.Add(root.Usings.LastOrDefault() is { } last
                ? new Protocol.TextEdit(Positions.ToRange(text, new TextSpan(last.Span.End, 0)), $"{nl}using {import};")
                : new Protocol.TextEdit(Positions.ToRange(text, new TextSpan(0, 0)), $"using {import};{nl}{nl}"));
        }

        var path = document.FilePath!;
        return new WorkspaceEdit([new TextDocumentEdit(new OptionalVersionedTextDocumentIdentifier(DocumentUris.FromPath(path), versionOf(path)), [.. edits.OrderBy(e => e.Range.Start.Line).ThenBy(e => e.Range.Start.Character)])]);
    }

    private static HashSet<string> GlobalUsings(Compilation compilation) =>
    [
        .. compilation.SyntaxTrees.SelectMany(t => t.GetCompilationUnitRoot().Usings.Where(u => u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)).Select(u => u.Name?.ToString() ?? string.Empty)),
    ];

    // ------------------------------------------------------------------ XAML text

    /// <summary>
    /// Where a new resource or style goes and its text: in the root's <c>{Root}.{property}</c> (its dictionary, if it has
    /// one), made when missing as the root's first child; in the root itself when it is a dictionary or styles file.
    /// <paramref name="content"/> uses <c>{nl}</c> for its line breaks and <c>{unit}</c> for one level of indentation.
    /// </summary>
    private static (int Start, int End, string Text)? Insert(XamlContext context, string property, string content)
    {
        var text = context.TextString;
        if (context.Document.Root is not { Closed: true, SelfClosing: false, EndTagNameStart: > 0 } root)
        {
            return null;
        }

        var nl = Newline(text);
        var rootIndent = Indent(text, root.Start);
        var unit = Unit(context, root);
        string Lines(string indent) =>
            string.Join(nl, content.Split("{nl}").Select(l => indent + l.Replace("{unit}", unit, StringComparison.Ordinal))) + nl;

        var directly = property == "Resources" ? root.LocalName == "ResourceDictionary" : root.LocalName is "Styles";
        if (directly)
        {
            return BeforeEnd(text, root, Lines(rootIndent + unit), nl);
        }

        var holder = root.Children.FirstOrDefault(c => c.IsPropertyElement && c.LocalName == root.LocalName + "." + property);
        if (holder is { SelfClosing: false, EndTagNameStart: > 0 })
        {
            var dictionary = property == "Resources" ? holder.Children.FirstOrDefault(c => c.LocalName == "ResourceDictionary" && c is { SelfClosing: false, EndTagNameStart: > 0 }) : null;
            var container = dictionary ?? holder;
            return BeforeEnd(text, container, Lines(Indent(text, container.Start) + unit), nl);
        }

        if (holder is not null)
        {
            return null; // <Window.Resources /> : left as it is
        }

        // A new property element, first in the root.
        var childIndent = rootIndent + unit;
        var wrapped = childIndent + "<" + root.Name + "." + property + ">" + nl + Lines(childIndent + unit) + childIndent + "</" + root.Name + "." + property + ">" + nl;
        if (root.Children.FirstOrDefault() is { } first)
        {
            var lineStart = LineStart(text, first.Start);
            return string.IsNullOrWhiteSpace(text[lineStart..first.Start])
                ? (lineStart, lineStart, wrapped + (first.LocalName is { } && !first.IsPropertyElement ? nl : string.Empty))
                : (first.Start, first.Start, nl + wrapped + Indent(text, first.Start));
        }

        return BeforeEnd(text, root, wrapped, nl);
    }

    /// <summary>Text before an element's end tag: on a line of its own before the end tag's line, or after its start tag.</summary>
    private static (int, int, string) BeforeEnd(string text, XamlElement element, string lines, string nl)
    {
        var endTag = element.EndTagNameStart - 2;
        var lineStart = LineStart(text, endTag);
        return string.IsNullOrWhiteSpace(text[lineStart..endTag]) && lineStart > element.StartTagEnd
            ? (lineStart, lineStart, lines)
            : (endTag, endTag, nl + lines + Indent(text, element.Start));
    }

    /// <summary>Declarations for the namespaces the new XAML uses that the root doesn't declare, after its last <c>xmlns</c>.</summary>
    private static IEnumerable<(int Start, int End, string Text)> Namespaces(XamlContext context, IEnumerable<(string Prefix, string Namespace)> namespaces)
    {
        if (context.Document.Root is not { } root)
        {
            yield break;
        }

        var declared = XamlDocument.Prefixes(root);
        var missing = namespaces.Where(n => !declared.ContainsKey(n.Prefix)).ToList();
        var last = root.Attributes.LastOrDefault(a => a.Name.StartsWith("xmlns", StringComparison.Ordinal) && a.ValueEnd >= 0);
        if (missing.Count == 0 || last is null)
        {
            yield break;
        }

        var text = context.TextString;
        var column = last.NameStart - LineStart(text, last.NameStart);
        var ownLine = string.IsNullOrWhiteSpace(text[LineStart(text, last.NameStart)..last.NameStart]);
        var separator = ownLine ? Newline(text) + new string(' ', column) : " ";
        var after = last.ValueEnd + 1;
        yield return (after, after, string.Concat(missing.Select(n => $"{separator}xmlns:{n.Prefix}=\"{n.Namespace}\"")));
    }

    private static string Newline(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static int LineStart(string text, int offset)
    {
        var start = Math.Clamp(offset, 0, text.Length);
        while (start > 0 && text[start - 1] != '\n')
        {
            start--;
        }

        return start;
    }

    private static string Indent(string text, int offset)
    {
        var start = LineStart(text, offset);
        var end = start;
        while (end < text.Length && text[end] is ' ' or '\t')
        {
            end++;
        }

        return text[start..end];
    }

    /// <summary>One level of the file's indentation: its root's first child's, else two spaces.</summary>
    private static string Unit(XamlContext context, XamlElement root)
    {
        var text = context.TextString;
        var rootIndent = Indent(text, root.Start);
        return root.Children.FirstOrDefault(c => string.IsNullOrWhiteSpace(text[LineStart(text, c.Start)..c.Start])) is { } child
            && Indent(text, child.Start) is { } childIndent && childIndent.Length > rootIndent.Length && childIndent.StartsWith(rootIndent, StringComparison.Ordinal)
            ? childIndent[rootIndent.Length..]
            : "  ";
    }

    private static string Unique(string name, Func<string, bool> taken)
    {
        if (!taken(name))
        {
            return name;
        }

        for (var i = 2; ; i++)
        {
            if (!taken(name + i))
            {
                return name + i;
            }
        }
    }

    private static WorkspaceEdit Edit(XamlContext context, IEnumerable<(int Start, int End, string Text)> edits, Func<string, int?> versionOf) =>
        new([new TextDocumentEdit(
            new OptionalVersionedTextDocumentIdentifier(DocumentUris.FromPath(context.Path), versionOf(context.Path)),
            [.. edits.OrderBy(e => e.Start).ThenBy(e => e.End).Select(e => new Protocol.TextEdit(context.Range(e.Start, e.End), e.Text))])]);

    private static LspCodeAction Action(string title, string kind, WorkspaceEdit edit) => new(title) { Kind = kind, Edit = edit };
}
