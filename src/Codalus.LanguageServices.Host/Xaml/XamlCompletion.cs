using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Range = Codalus.LanguageServices.Protocol.Range;

namespace Codalus.LanguageServices.Host.Xaml;

/// <summary>
/// XAML completion (M19a, ADR 0047): element names (types of the default and prefixed namespaces, property elements),
/// attributes (properties, attached properties, events, directives), values (enums, booleans, colors, static
/// instances, types, setters' properties and values, style classes, selectors, <c>xmlns</c>, <c>x:Class</c>), markup
/// extensions and their arguments (resource keys, binding paths from <c>x:DataType</c>, binding modes, element
/// names, <c>x:Static</c> members).
/// </summary>
internal static class XamlCompletion
{
    private static readonly (string Name, string Description)[] s_extensions =
    [
        ("Binding", "A binding to the data context (compiled when x:DataType is set and compiled bindings are on)."),
        ("CompiledBinding", "A binding compiled against x:DataType."),
        ("ReflectionBinding", "A binding resolved by reflection at run time."),
        ("StaticResource", "A resource looked up once, when the XAML loads."),
        ("DynamicResource", "A resource that follows changes (themes, merged dictionaries)."),
        ("TemplateBinding", "A property of the templated control, in a control template."),
        ("x:Static", "A static property, field or constant."),
        ("x:Type", "A type."),
        ("x:Null", "null."),
        ("OnPlatform", "A value per operating system."),
        ("OnFormFactor", "A value per form factor (desktop, mobile)."),
        ("RelativeSource", "A binding source relative to the target."),
    ];

    private static readonly (string Name, string Description)[] s_bindingArguments =
    [
        ("Path", "The property path."),
        ("Mode", "OneWay, TwoWay, OneTime, OneWayToSource or Default."),
        ("Converter", "An IValueConverter."),
        ("ConverterParameter", "The converter's parameter."),
        ("ElementName", "A named element as the source."),
        ("RelativeSource", "A source relative to the target."),
        ("Source", "An explicit source object."),
        ("StringFormat", "A format string for the value."),
        ("FallbackValue", "The value when the binding fails."),
        ("TargetNullValue", "The value when the source is null."),
        ("UpdateSourceTrigger", "When a two-way binding writes back."),
        ("Delay", "Milliseconds to wait before writing back."),
    ];

    public static async Task<CompletionList> CompleteAsync(XamlContext context, int offset, CancellationToken cancellationToken)
    {
        var items = new List<CompletionItem>();
        var range = Complete(context, offset, items, out var asyncWork);
        if (asyncWork is not null)
        {
            await asyncWork(items, cancellationToken).ConfigureAwait(false);
        }

        return new CompletionList(false, [.. items.DistinctBy(i => i.Label)]) { ItemDefaults = range is null ? null : new CompletionItemDefaults(range) };
    }

    private static Range? Complete(XamlContext context, int offset, List<CompletionItem> items, out Func<List<CompletionItem>, CancellationToken, Task>? asyncWork)
    {
        asyncWork = null;
        var text = context.TextString;
        var document = context.Document;

        // An end tag being typed: the open element's name.
        var tagStart = text.LastIndexOf('<', Math.Max(0, offset - 1));
        if (tagStart >= 0 && tagStart + 1 < text.Length && text[tagStart + 1] == '/' && !text[tagStart..offset].Contains('>', StringComparison.Ordinal))
        {
            var open = document.ElementAt(tagStart);
            while (open is not null && open.Closed && open.End <= tagStart)
            {
                open = open.Parent;
            }

            var range = context.Range(tagStart + 2, offset);
            if (open is not null)
            {
                items.Add(Item(open.Name + ">", CompletionItemKind.Class, range, filter: open.Name));
            }

            return range;
        }

        var element = document.ElementAt(offset);
        if (element is null)
        {
            return null;
        }

        // The element's name.
        if (offset >= element.NameStart && offset <= element.NameEnd)
        {
            var range = context.Range(element.NameStart, element.NameEnd);
            ElementNames(context, element.Parent ?? element, items, range);
            return range;
        }

        if (element.EndTagNameStart >= 0 && offset >= element.EndTagNameStart && offset <= element.EndTagNameStart + element.Name.Length)
        {
            var range = context.Range(element.EndTagNameStart, element.EndTagNameStart + element.Name.Length);
            items.Add(Item(element.Name, CompletionItemKind.Class, range));
            return range;
        }

        var inStartTag = offset > element.NameEnd && (element.StartTagEnd < 0 || offset < element.StartTagEnd);
        if (!inStartTag)
        {
            return null;
        }

        foreach (var attribute in element.Attributes)
        {
            if (attribute.ContainsValue(offset))
            {
                return Values(context, element, attribute, offset, items, out asyncWork);
            }

            if (offset >= attribute.NameStart && offset <= attribute.NameEnd)
            {
                var range = context.Range(attribute.NameStart, attribute.NameEnd);
                Attributes(context, element, items, range, attribute.Name[..(offset - attribute.NameStart)]);
                return range;
            }
        }

        var empty = context.Range(offset, offset);
        Attributes(context, element, items, empty, string.Empty);
        return empty;
    }

    // ------------------------------------------------------------------ element names

    private static void ElementNames(XamlContext context, XamlElement parent, List<CompletionItem> items, Range range)
    {
        if (context.Types is not { } types)
        {
            return;
        }

        var prefixes = XamlDocument.Prefixes(parent);
        foreach (var type in types.TypesIn(XamlDocument.ResolvePrefix(parent, null)).Where(XamlTypeSystem.IsCreatable))
        {
            items.Add(TypeItem(type.Name, type, range));
        }

        foreach (var (prefix, xmlns) in prefixes)
        {
            if (xmlns == XamlNames.XamlLanguage)
            {
                items.AddRange(XamlTypeSystem.XamlLanguageTypes.Select(t => Item($"{prefix}:{t}", CompletionItemKind.Struct, range)));
                continue;
            }

            foreach (var type in types.TypesIn(xmlns).Where(XamlTypeSystem.IsCreatable))
            {
                items.Add(TypeItem($"{prefix}:{type.Name}", type, range));
            }
        }

        // Property elements of the parent: <Grid.RowDefinitions>, <Button.Flyout>.
        if (XamlContext.ObjectElement(parent) is { } owner && context.TypeOf(owner) is { } ownerType)
        {
            foreach (var member in types.Members(ownerType).Where(m => m.Kind != XamlMemberKind.Event))
            {
                items.Add(Item($"{owner.Name}.{member.Name}", CompletionItemKind.Property, range, Detail(member), XamlDocs.Summary(member.Symbol)));
            }
        }
    }

    private static CompletionItem TypeItem(string label, INamedTypeSymbol type, Range range) =>
        Item(label, type.TypeKind == TypeKind.Struct ? CompletionItemKind.Struct : CompletionItemKind.Class, range, type.ToDisplayString(), XamlDocs.Summary(type));

    // ------------------------------------------------------------------ attribute names

    private static void Attributes(XamlContext context, XamlElement element, List<CompletionItem> items, Range range, string typed)
    {
        if (element.IsPropertyElement)
        {
            return;
        }

        var present = element.Attributes.Where(a => a.NameEnd != context.OffsetOf(range.End) || a.NameStart != context.OffsetOf(range.Start)).Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        void Add(CompletionItem item)
        {
            if (!present.Contains(item.Label))
            {
                items.Add(item);
            }
        }

        var isRoot = element.Parent is null;
        var prefixes = XamlDocument.Prefixes(element);
        var x = prefixes.FirstOrDefault(p => p.Value == XamlNames.XamlLanguage).Key ?? "x";
        var d = prefixes.FirstOrDefault(p => p.Value == XamlNames.Design).Key;
        Add(Attribute($"{x}:Name", range, "The element's name (a field in the code-behind)."));
        Add(Attribute($"{x}:Key", range, "The resource's key."));
        Add(Attribute($"{x}:DataType", range, "The type of the data context, for compiled bindings."));
        Add(Attribute($"{x}:CompileBindings", range, "Whether bindings here compile against x:DataType."));
        if (isRoot)
        {
            Add(Attribute($"{x}:Class", range, "The code-behind class."));
            Add(Attribute("xmlns", range, "The default XML namespace."));
        }

        Add(Attribute("xmlns:", range, "An XML namespace prefix.", snippet: false));
        if (d is not null)
        {
            Add(Attribute($"{d}:DataContext", range, "The data context at design time (the previewer)."));
            if (isRoot)
            {
                Add(Attribute($"{d}:DesignWidth", range, "The width at design time (the previewer)."));
                Add(Attribute($"{d}:DesignHeight", range, "The height at design time (the previewer)."));
            }
        }

        if (isRoot && prefixes.Any(p => p.Value == XamlNames.MarkupCompatibility))
        {
            var mc = prefixes.First(p => p.Value == XamlNames.MarkupCompatibility).Key;
            Add(Attribute($"{mc}:Ignorable", range, "Prefixes the XAML compiler ignores (d)."));
        }

        if (context.Types is not { } types || context.TypeOf(element) is not { } type)
        {
            return;
        }

        foreach (var member in types.Members(type).Where(m => m.Settable))
        {
            Add(Item(member.Name, member.Kind == XamlMemberKind.Event ? CompletionItemKind.Event : CompletionItemKind.Property, range, Detail(member), XamlDocs.Summary(member.Symbol),
                insert: member.Name + "=\"$0\"", snippet: true));
        }

        // Attached properties: Owner.Property, for every owner of the namespaces in scope (or the one typed).
        var dot = typed.IndexOf('.', StringComparison.Ordinal);
        IEnumerable<(string Prefix, INamedTypeSymbol Owner)> owners = dot > 0
            ? context.ResolveTypeName(element, typed[..dot]) is { } typedOwner ? [(typed[..dot], typedOwner)] : []
            : types.AttachedOwners(XamlDocument.ResolvePrefix(element, null)).Select(o => (o.Name, o))
                .Concat(prefixes.Where(p => p.Value is not (XamlNames.XamlLanguage or XamlNames.Design or XamlNames.MarkupCompatibility))
                    .SelectMany(p => types.AttachedOwners(p.Value).Select(o => ($"{p.Key}:{o.Name}", o))));
        foreach (var (prefix, owner) in owners)
        {
            foreach (var member in types.Attached(owner))
            {
                Add(Item($"{prefix}.{member.Name}", CompletionItemKind.Property, range, Detail(member), XamlDocs.Summary(member.Symbol), insert: $"{prefix}.{member.Name}=\"$0\"", snippet: true));
            }
        }
    }

    private static CompletionItem Attribute(string name, Range range, string documentation, bool snippet = true) =>
        Item(name, CompletionItemKind.Keyword, range, null, documentation, insert: snippet ? name + "=\"$0\"" : name, snippet: snippet);

    private static string Detail(XamlMember member) =>
        $"{member.ValueType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "?"} {member.Owner.Name}.{member.Name}";

    // ------------------------------------------------------------------ values

    private static Range? Values(XamlContext context, XamlElement element, XamlAttribute attribute, int offset, List<CompletionItem> items, out Func<List<CompletionItem>, CancellationToken, Task>? asyncWork)
    {
        asyncWork = null;
        var value = attribute.Value ?? string.Empty;
        var caret = offset - attribute.ValueStart;
        var whole = context.Range(attribute.ValueStart, attribute.ValueEnd);
        if (MarkupExtensionContext.At(value, caret) is { } extension)
        {
            return Extension(context, element, attribute, extension, offset, items, out asyncWork);
        }

        var types = context.Types;
        var name = attribute.Name;
        if (name is "xmlns" || name.StartsWith("xmlns:", StringComparison.Ordinal))
        {
            items.Add(Item(types is { IsMaui: true, IsAvalonia: false } ? XamlNames.Maui : XamlNames.Avalonia, CompletionItemKind.Module, whole));
            items.Add(Item(XamlNames.XamlLanguage, CompletionItemKind.Module, whole));
            items.Add(Item(XamlNames.Design, CompletionItemKind.Module, whole));
            items.Add(Item(XamlNames.MarkupCompatibility, CompletionItemKind.Module, whole));
            if (types is not null)
            {
                items.AddRange(types.KnownXmlNamespaces.Select(n => Item(n, CompletionItemKind.Module, whole)));
                items.AddRange(types.SourceNamespaces().Select(n => Item("using:" + n, CompletionItemKind.Module, whole)));
            }

            return whole;
        }

        if (name.EndsWith(":Ignorable", StringComparison.Ordinal))
        {
            items.Add(Item("d", CompletionItemKind.Value, whole));
            return whole;
        }

        if (name.EndsWith(":Class", StringComparison.Ordinal))
        {
            if (context.Compilation is { } compilation)
            {
                foreach (var tree in compilation.SyntaxTrees)
                {
                    foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>()
                                 .Where(c => c.Modifiers.Any(m => m.Text == "partial")))
                    {
                        if (compilation.GetSemanticModel(tree).GetDeclaredSymbol(declaration) is INamedTypeSymbol symbol)
                        {
                            items.Add(TypeItem(symbol.ToDisplayString(), symbol, whole));
                        }
                    }
                }
            }

            return whole;
        }

        if (name.EndsWith(":DataType", StringComparison.Ordinal) || name is "DataType" or "TargetType")
        {
            TypeNames(context, element, items, whole, creatableOnly: false);
            return whole;
        }

        if (name is "Selector" && element.LocalName == "Style")
        {
            return Selector(context, element, value, caret, attribute, items, out asyncWork);
        }

        if (name is "Classes")
        {
            var (start, end) = Token(value, caret, c => !char.IsWhiteSpace(c));
            var range = context.Range(attribute.ValueStart + start, attribute.ValueStart + end);
            asyncWork = async (list, ct) =>
            {
                foreach (var cls in await StyleClassesAsync(context, ct).ConfigureAwait(false))
                {
                    list.Add(Item(cls, CompletionItemKind.Value, range));
                }
            };
            return range;
        }

        if (types is null)
        {
            return null;
        }

        // A setter's property and value.
        if (element.LocalName == "Setter" && context.TargetType(element) is { } target)
        {
            if (name == "Property")
            {
                foreach (var member in types.Members(target).Where(m => m.Kind != XamlMemberKind.Event && m.Settable))
                {
                    items.Add(Item(member.Name, CompletionItemKind.Property, whole, Detail(member), XamlDocs.Summary(member.Symbol)));
                }

                return whole;
            }

            if (name == "Value" && element.Attribute("Property")?.Value is { } property && Member(context, element, target, property) is { } setterMember)
            {
                ValueItems(types, setterMember.ValueType, items, whole);
                return whole;
            }
        }

        var objectType = context.TypeOf(element);
        if (Member(context, element, objectType, name) is not { } m)
        {
            return null;
        }

        if (m.Kind == XamlMemberKind.Event)
        {
            // Event handlers: the code-behind's methods with two parameters.
            if (context.CodeBehind is { } codeBehind)
            {
                foreach (var method in codeBehind.GetMembers().OfType<IMethodSymbol>().Where(x => x.MethodKind == MethodKind.Ordinary && x.Parameters.Length == 2))
                {
                    items.Add(Item(method.Name, CompletionItemKind.Method, whole, method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
                }
            }

            return whole;
        }

        if (m.ValueType?.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString() == "System.Type")
        {
            TypeNames(context, element, items, whole, creatableOnly: false);
            return whole;
        }

        ValueItems(types, m.ValueType, items, whole);
        return whole;
    }

    /// <summary>The member an attribute name sets: a property of <paramref name="type"/>, or an attached property (<c>Grid.Row</c>).</summary>
    public static XamlMember? Member(XamlContext context, XamlElement scope, INamedTypeSymbol? type, string name)
    {
        if (context.Types is not { } types)
        {
            return null;
        }

        var dot = name.LastIndexOf('.');
        if (dot > 0)
        {
            return context.ResolveTypeName(scope, name[..dot]) is { } owner
                ? types.Attached(owner).FirstOrDefault(m => m.Name == name[(dot + 1)..]) ?? types.Member(owner, name[(dot + 1)..])
                : null;
        }

        return type is null ? null : types.Member(type, name);
    }

    private static void ValueItems(XamlTypeSystem types, ITypeSymbol? type, List<CompletionItem> items, Range range)
    {
        foreach (var (value, symbol) in types.ValuesOf(type))
        {
            items.Add(Item(value, symbol is IFieldSymbol { IsConst: true } ? CompletionItemKind.EnumMember : CompletionItemKind.Value, range, null, symbol is null ? null : XamlDocs.Summary(symbol)));
        }
    }

    private static void TypeNames(XamlContext context, XamlElement scope, List<CompletionItem> items, Range range, bool creatableOnly)
    {
        if (context.Types is not { } types)
        {
            return;
        }

        foreach (var type in types.TypesIn(XamlDocument.ResolvePrefix(scope, null)).Where(t => !creatableOnly || XamlTypeSystem.IsCreatable(t)))
        {
            items.Add(TypeItem(type.Name, type, range));
        }

        foreach (var (prefix, xmlns) in XamlDocument.Prefixes(scope))
        {
            foreach (var type in types.TypesIn(xmlns).Where(t => !creatableOnly || XamlTypeSystem.IsCreatable(t)))
            {
                items.Add(TypeItem($"{prefix}:{type.Name}", type, range));
            }
        }
    }

    // ------------------------------------------------------------------ markup extensions

    private static Range? Extension(XamlContext context, XamlElement element, XamlAttribute attribute, MarkupExtensionContext extension, int offset, List<CompletionItem> items, out Func<List<CompletionItem>, CancellationToken, Task>? asyncWork)
    {
        asyncWork = null;
        var types = context.Types;
        if (extension.InName)
        {
            var range = context.Range(attribute.ValueStart + extension.NameStart, offset);
            foreach (var (name, description) in s_extensions)
            {
                items.Add(Item(name, CompletionItemKind.Keyword, range, null, description));
            }

            if (types is not null)
            {
                foreach (var type in types.TypesIn(XamlDocument.ResolvePrefix(element, null)).Where(t => t.Name.EndsWith("Extension", StringComparison.Ordinal) && XamlTypeSystem.IsCreatable(t)))
                {
                    items.Add(TypeItem(type.Name[..^9], type, range));
                }
            }

            return range;
        }

        var argumentRange = context.Range(attribute.ValueStart + extension.ArgumentStart, offset);
        if (extension.IsResource && extension.Index == 0 && extension.Key is null or "ResourceKey")
        {
            asyncWork = async (list, ct) =>
            {
                foreach (var (key, kind, file) in await ResourceKeysAsync(context, ct).ConfigureAwait(false))
                {
                    list.Add(Item(key, CompletionItemKind.Constant, argumentRange, kind, $"Defined in {Path.GetFileName(file)}."));
                }
            };
            return argumentRange;
        }

        if (extension.IsBinding)
        {
            if (extension.Key is null && extension.Index > 0 && !extension.Argument.Contains('=', StringComparison.Ordinal))
            {
                foreach (var (name, description) in s_bindingArguments)
                {
                    items.Add(Item(name + "=", CompletionItemKind.Property, argumentRange, null, description, filter: name));
                }

                return argumentRange;
            }

            if (extension.IsPath)
            {
                return BindingPath(context, element, extension.Argument, attribute.ValueStart + extension.ArgumentStart, offset, items);
            }

            switch (extension.Key)
            {
                case "Mode" when types is not null && (types.Type("Avalonia.Data.BindingMode") ?? types.Type("Microsoft.Maui.Controls.BindingMode")) is { } mode:
                    ValueItems(types, mode, items, argumentRange);
                    return argumentRange;
                case "UpdateSourceTrigger" when types?.Type("Avalonia.Data.UpdateSourceTrigger") is { } trigger:
                    ValueItems(types, trigger, items, argumentRange);
                    return argumentRange;
                case "ElementName":
                    foreach (var name in Names(context.Document))
                    {
                        items.Add(Item(name, CompletionItemKind.Reference, argumentRange));
                    }

                    return argumentRange;
            }

            return argumentRange;
        }

        if (extension.Kind == "TemplateBinding" && types is not null && context.TargetType(element) is { } target)
        {
            foreach (var member in types.Members(target).Where(m => m.Kind != XamlMemberKind.Event))
            {
                items.Add(Item(member.Name, CompletionItemKind.Property, argumentRange, Detail(member), XamlDocs.Summary(member.Symbol)));
            }

            return argumentRange;
        }

        if (extension.Kind == "Type")
        {
            TypeNames(context, element, items, argumentRange, creatableOnly: false);
            return argumentRange;
        }

        if (extension.Kind == "Static" && types is not null)
        {
            var dot = extension.Argument.LastIndexOf('.');
            if (dot > 0 && context.ResolveTypeName(element, extension.Argument[..dot]) is { } owner)
            {
                var memberRange = context.Range(attribute.ValueStart + extension.ArgumentStart + dot + 1, offset);
                foreach (var member in owner.GetMembers().Where(m => m.IsStatic && m.DeclaredAccessibility == Accessibility.Public && m is IPropertySymbol or IFieldSymbol))
                {
                    items.Add(Item(member.Name, member is IFieldSymbol { IsConst: true } ? CompletionItemKind.Constant : CompletionItemKind.Property, memberRange, null, XamlDocs.Summary(member)));
                }

                return memberRange;
            }

            TypeNames(context, element, items, argumentRange, creatableOnly: false);
            return argumentRange;
        }

        return null;
    }

    /// <summary>A binding path's members: the data type's (x:DataType), or a named element's (<c>#Name.</c>), or an ancestor's (<c>$parent[Type].</c>).</summary>
    private static Range BindingPath(XamlContext context, XamlElement element, string path, int pathStart, int offset, List<CompletionItem> items)
    {
        var lastDot = path.LastIndexOf('.');
        var range = context.Range(pathStart + lastDot + 1, offset);
        if (lastDot < 0)
        {
            items.Add(Item("$parent", CompletionItemKind.Keyword, range, null, "An ancestor: $parent[Type] or $parent[Type;level]."));
            items.Add(Item("$self", CompletionItemKind.Keyword, range, null, "The target element."));
            foreach (var name in Names(context.Document))
            {
                items.Add(Item("#" + name, CompletionItemKind.Reference, range, null, "A named element."));
            }
        }

        if (XamlBindingPaths.Resolve(context, element, lastDot < 0 ? string.Empty : path[..lastDot]) is not { } type)
        {
            return range;
        }

        foreach (var member in XamlTypeSystem.BindableMembers(type))
        {
            var (kind, detail) = member switch
            {
                IPropertySymbol property => (CompletionItemKind.Property, property.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)),
                IMethodSymbol method => (CompletionItemKind.Method, method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)),
                _ => (CompletionItemKind.Field, null as string),
            };
            items.Add(Item(member.Name, kind, range, detail, XamlDocs.Summary(member), sort: kind == CompletionItemKind.Property ? "0" + member.Name : "1" + member.Name));
        }

        return range;
    }

    /// <summary>The <c>x:Name</c> and <c>Name</c> values of a document.</summary>
    public static IEnumerable<string> Names(XamlDocument document) =>
        document.Elements.Select(e => e.Attribute("x:Name")?.Value ?? e.Attribute("Name")?.Value).OfType<string>().Where(n => n.Length > 0).Distinct(StringComparer.Ordinal);

    // ------------------------------------------------------------------ selectors and classes

    private static Range? Selector(XamlContext context, XamlElement element, string value, int caret, XamlAttribute attribute, List<CompletionItem> items, out Func<List<CompletionItem>, CancellationToken, Task>? asyncWork)
    {
        asyncWork = null;
        var start = caret;
        while (start > 0 && (char.IsLetterOrDigit(value[start - 1]) || value[start - 1] is '_' or '-' or '|'))
        {
            start--;
        }

        var marker = start > 0 ? value[start - 1] : ' ';
        var range = context.Range(attribute.ValueStart + start, attribute.ValueStart + caret);
        switch (marker)
        {
            case '.':
                asyncWork = async (list, ct) =>
                {
                    foreach (var cls in await StyleClassesAsync(context, ct).ConfigureAwait(false))
                    {
                        list.Add(Item(cls, CompletionItemKind.Value, range));
                    }
                };
                return range;
            case ':':
                var typeName = XamlSelectors.LastType(value[..Math.Max(0, start - 1)]);
                var type = typeName is null ? null : context.ResolveTypeName(element, typeName);
                foreach (var pseudo in context.Types?.PseudoClasses(type) ?? [])
                {
                    items.Add(Item(pseudo.TrimStart(':'), CompletionItemKind.Value, range));
                }

                foreach (var function in new[] { "is", "not", "nth-child", "nth-last-child" })
                {
                    items.Add(Item(function, CompletionItemKind.Function, range, insert: function + "($0)", snippet: true));
                }

                return range;
            case '#':
                foreach (var name in Names(context.Document))
                {
                    items.Add(Item(name, CompletionItemKind.Reference, range));
                }

                return range;
        }

        TypeNames(context, element, items, range, creatableOnly: false);
        items.Add(Item("/template/", CompletionItemKind.Keyword, range, null, "Selects inside the control's template."));
        items.Add(Item("^", CompletionItemKind.Keyword, range, null, "The parent selector (in a nested style)."));
        return range;
    }

    /// <summary>The style classes the project's XAML uses: <c>Classes</c> values, <c>Classes.name</c> attributes, and selectors' <c>.name</c>.</summary>
    public static async Task<IReadOnlyList<string>> StyleClassesAsync(XamlContext context, CancellationToken cancellationToken)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (_, document) in await context.ProjectXamlAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var element in document.Elements)
            {
                foreach (var attribute in element.Attributes)
                {
                    if (attribute is { Name: "Classes", Value: { } classes })
                    {
                        result.UnionWith(classes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(c => !c.StartsWith('{')));
                    }
                    else if (attribute.Name.StartsWith("Classes.", StringComparison.Ordinal))
                    {
                        result.Add(attribute.Name[8..]);
                    }
                    else if (attribute is { Name: "Selector", Value: { } selector })
                    {
                        result.UnionWith(XamlSelectors.Tokens(selector).Where(t => t.Kind == '.' && t.Text.Length > 0).Select(t => t.Text));
                    }
                }
            }
        }

        return [.. result];
    }

    /// <summary>The resource keys (<c>x:Key</c>) of the project's XAML and its references': key, element type, file.</summary>
    public static async Task<IReadOnlyList<(string Key, string Kind, string File)>> ResourceKeysAsync(XamlContext context, CancellationToken cancellationToken)
    {
        var result = new List<(string, string, string)>();
        foreach (var (path, document) in await context.ProjectXamlAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var element in document.Elements)
            {
                if (element.Attribute("x:Key")?.Value is { Length: > 0 } key && !key.StartsWith('{'))
                {
                    result.Add((key, element.LocalName, path));
                }
            }
        }

        return [.. result.DistinctBy(r => r.Item1)];
    }

    // ------------------------------------------------------------------ helpers

    private static (int Start, int End) Token(string value, int caret, Func<char, bool> isPart)
    {
        var start = Math.Clamp(caret, 0, value.Length);
        while (start > 0 && isPart(value[start - 1]))
        {
            start--;
        }

        var end = Math.Clamp(caret, 0, value.Length);
        while (end < value.Length && isPart(value[end]))
        {
            end++;
        }

        return (start, end);
    }

    private static CompletionItem Item(string label, int kind, Range range, string? detail = null, string? documentation = null, string? insert = null, bool snippet = false, string? filter = null, string? sort = null) =>
        new(label)
        {
            Kind = kind,
            Detail = detail,
            Documentation = documentation is null ? null : MarkupContent.Markdown(documentation),
            TextEdit = new TextEdit(range, insert ?? label),
            InsertTextFormat = snippet ? InsertTextFormat.Snippet : null,
            FilterText = filter ?? label,
            SortText = sort,
        };
}

/// <summary>Binding paths: the type a path leads to.</summary>
internal static class XamlBindingPaths
{
    /// <summary>The type <paramref name="path"/> (complete segments, no trailing dot) leads to from the data type in scope, or null.</summary>
    public static ITypeSymbol? Resolve(XamlContext context, XamlElement element, string path)
    {
        ITypeSymbol? type;
        var rest = path.Trim();
        if (rest.StartsWith("$parent", StringComparison.Ordinal))
        {
            var close = rest.IndexOf(']', StringComparison.Ordinal);
            var name = rest.Length > 8 && rest[7] == '[' && close > 8 ? rest[8..close].Split(';')[0] : null;
            type = name is null ? null : context.ResolveTypeName(element, name);
            rest = close > 0 ? rest[(close + 1)..].TrimStart('.') : string.Empty;
        }
        else if (rest.StartsWith("$self", StringComparison.Ordinal))
        {
            type = context.TypeOf(XamlContext.ObjectElement(element)!);
            rest = rest[5..].TrimStart('.');
        }
        else if (rest.StartsWith('#'))
        {
            var dot = rest.IndexOf('.', StringComparison.Ordinal);
            var name = dot < 0 ? rest[1..] : rest[1..dot];
            var named = context.Document.Elements.FirstOrDefault(e => (e.Attribute("x:Name")?.Value ?? e.Attribute("Name")?.Value) == name);
            type = named is null ? null : context.TypeOf(named);
            rest = dot < 0 ? string.Empty : rest[(dot + 1)..];
        }
        else
        {
            type = context.DataType(element);
        }

        foreach (var raw in rest.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (type is null)
            {
                return null;
            }

            var segment = raw.Trim().TrimStart('!');
            var indexer = segment.IndexOf('[', StringComparison.Ordinal);
            var name = indexer >= 0 ? segment[..indexer] : segment;
            if (name.StartsWith('(') && name.EndsWith(')'))
            {
                return null; // a cast or an attached property: not followed
            }

            var member = XamlTypeSystem.BindableMembers(type).FirstOrDefault(m => m.Name == name);
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
        }

        return type;
    }
}

/// <summary>XML documentation summaries for hover and completion.</summary>
internal static class XamlDocs
{
    public static string? Summary(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml(expandIncludes: false);
        if (string.IsNullOrEmpty(xml))
        {
            return null;
        }

        try
        {
            var summary = System.Xml.Linq.XElement.Parse(xml).Element("summary");
            if (summary is null)
            {
                return null;
            }

            var text = string.Concat(summary.Nodes().Select(n => n switch
            {
                System.Xml.Linq.XText t => t.Value,
                System.Xml.Linq.XElement { Name.LocalName: "see" or "seealso" } e => (e.Attribute("cref")?.Value ?? e.Attribute("langword")?.Value ?? e.Value) is var r && r.Length > 2 && r[1] == ':' ? r[(r.LastIndexOf('.') + 1)..] : r,
                System.Xml.Linq.XElement e => e.Value,
                _ => string.Empty,
            }));
            return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }
}
