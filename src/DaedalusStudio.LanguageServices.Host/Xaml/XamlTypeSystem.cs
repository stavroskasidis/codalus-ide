using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace DaedalusStudio.LanguageServices.Host.Xaml;

internal enum XamlMemberKind
{
    /// <summary>A CLR property.</summary>
    Property,

    /// <summary>An Avalonia property without a CLR wrapper (<c>XxxProperty</c> field).</summary>
    AvaloniaProperty,

    /// <summary>An attached property (<c>Owner.Xxx</c>).</summary>
    Attached,

    Event,
}

/// <summary>A member XAML can set: its name, the type of its value, and the symbol that declares it.</summary>
internal sealed record XamlMember(string Name, ITypeSymbol? ValueType, XamlMemberKind Kind, ISymbol Symbol, INamedTypeSymbol Owner)
{
    /// <summary>An attribute can set it (a settable property, an Avalonia property, an event; not a read-only collection).</summary>
    public bool Settable { get; init; } = true;
}

/// <summary>
/// XAML's view of a Roslyn compilation (ADR 0047): XML namespaces to CLR namespaces through Avalonia's
/// <c>[XmlnsDefinition]</c> attributes and <c>using:</c>/<c>clr-namespace:</c>, the types an element can name, the
/// members an attribute can set (CLR properties, Avalonia properties, attached properties, events), content
/// properties, pseudo-classes. Types come from source and from metadata alike, so the project's own controls and view
/// models are known before it is built. One instance per compilation.
/// </summary>
internal sealed class XamlTypeSystem
{
    private static readonly ConditionalWeakTable<Compilation, XamlTypeSystem> s_cache = new();

    private readonly Lazy<Dictionary<string, IReadOnlyList<string>>> _xmlns;
    private readonly ConcurrentDictionary<string, IReadOnlyList<INamedTypeSymbol>> _typesByXmlns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<INamedTypeSymbol, IReadOnlyList<XamlMember>> _members = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<INamedTypeSymbol, IReadOnlyList<XamlMember>> _attached = new(SymbolEqualityComparer.Default);

    private XamlTypeSystem(Compilation compilation)
    {
        Compilation = compilation;
        _xmlns = new(ReadXmlnsDefinitions);
    }

    public static XamlTypeSystem For(Compilation compilation) => s_cache.GetValue(compilation, c => new XamlTypeSystem(c));

    public Compilation Compilation { get; }

    public INamedTypeSymbol? Type(string metadataName) => Compilation.GetTypeByMetadataName(metadataName);

    /// <summary>Whether Avalonia is referenced at all.</summary>
    public bool IsAvalonia => Type("Avalonia.AvaloniaObject") is not null;

    // ------------------------------------------------------------------ namespaces

    private Dictionary<string, IReadOnlyList<string>> ReadXmlnsDefinitions()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var assembly in Compilation.SourceModule.ReferencedAssemblySymbols.Append(Compilation.Assembly))
        {
            foreach (var attribute in assembly.GetAttributes())
            {
                if (attribute.AttributeClass?.Name == "XmlnsDefinitionAttribute"
                    && attribute.ConstructorArguments is [{ Value: string uri }, { Value: string clr }, ..])
                {
                    if (!map.TryGetValue(uri, out var list))
                    {
                        map[uri] = list = [];
                    }

                    if (!list.Contains(clr))
                    {
                        list.Add(clr);
                    }
                }
            }
        }

        return map.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value, StringComparer.Ordinal);
    }

    /// <summary>The CLR namespaces of an XML namespace (<c>using:</c>, <c>clr-namespace:</c> or an <c>[XmlnsDefinition]</c> URI).</summary>
    public IReadOnlyList<string> ClrNamespaces(string? xmlns)
    {
        if (string.IsNullOrEmpty(xmlns))
        {
            return [];
        }

        if (xmlns.StartsWith("using:", StringComparison.Ordinal))
        {
            return [xmlns[6..].Trim()];
        }

        if (xmlns.StartsWith("clr-namespace:", StringComparison.Ordinal))
        {
            var rest = xmlns[14..];
            var semicolon = rest.IndexOf(';', StringComparison.Ordinal);
            return [(semicolon < 0 ? rest : rest[..semicolon]).Trim()];
        }

        return _xmlns.Value.GetValueOrDefault(xmlns) ?? [];
    }

    /// <summary>The XML namespaces with <c>[XmlnsDefinition]</c>s (for <c>xmlns</c> completion).</summary>
    public IEnumerable<string> KnownXmlNamespaces => _xmlns.Value.Keys;

    private INamespaceSymbol? Namespace(string clr)
    {
        var current = Compilation.GlobalNamespace;
        foreach (var part in clr.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.GetNamespaceMembers().FirstOrDefault(n => n.Name == part);
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    /// <summary>The namespaces declared in the project's own source (for <c>xmlns:</c> completion).</summary>
    public IEnumerable<string> SourceNamespaces()
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        void Walk(INamespaceSymbol ns)
        {
            if (!ns.IsGlobalNamespace && ns.GetTypeMembers().Any(t => t.Locations.Any(l => l.IsInSource)))
            {
                result.Add(ns.ToDisplayString());
            }

            foreach (var child in ns.GetNamespaceMembers())
            {
                Walk(child);
            }
        }

        Walk(Compilation.Assembly.GlobalNamespace);
        return result;
    }

    // ------------------------------------------------------------------ types

    /// <summary>The type <paramref name="name"/> (an element's local name, which may name a markup extension without its suffix) in <paramref name="xmlns"/>.</summary>
    public INamedTypeSymbol? ResolveType(string? xmlns, string name)
    {
        if (xmlns == XamlNames.XamlLanguage)
        {
            return XamlLanguageType(name);
        }

        foreach (var clr in ClrNamespaces(xmlns))
        {
            if (Find(clr, name) is { } type)
            {
                return type;
            }
        }

        return null;
    }

    private INamedTypeSymbol? Find(string clr, string name)
    {
        var prefix = clr.Length == 0 ? string.Empty : clr + ".";
        return Compilation.GetTypeByMetadataName(prefix + name)
               ?? Compilation.GetTypeByMetadataName(prefix + name + "Extension")
               ?? Namespace(clr)?.GetTypeMembers(name).FirstOrDefault(t => t.Arity == 0);
    }

    /// <summary>The x: namespace's types (<c>x:Double</c>, <c>x:String</c>…).</summary>
    private INamedTypeSymbol? XamlLanguageType(string name) => name switch
    {
        "String" => Compilation.GetSpecialType(SpecialType.System_String),
        "Double" => Compilation.GetSpecialType(SpecialType.System_Double),
        "Int32" => Compilation.GetSpecialType(SpecialType.System_Int32),
        "Boolean" => Compilation.GetSpecialType(SpecialType.System_Boolean),
        "Object" => Compilation.GetSpecialType(SpecialType.System_Object),
        "Single" => Compilation.GetSpecialType(SpecialType.System_Single),
        "Int64" => Compilation.GetSpecialType(SpecialType.System_Int64),
        "Char" => Compilation.GetSpecialType(SpecialType.System_Char),
        "Byte" => Compilation.GetSpecialType(SpecialType.System_Byte),
        "Decimal" => Compilation.GetSpecialType(SpecialType.System_Decimal),
        "TimeSpan" => Compilation.GetTypeByMetadataName("System.TimeSpan"),
        "Uri" => Compilation.GetTypeByMetadataName("System.Uri"),
        "Type" => Compilation.GetTypeByMetadataName("System.Type"),
        _ => null,
    };

    public static readonly string[] XamlLanguageTypes = ["String", "Double", "Int32", "Boolean", "Object", "Single", "Int64", "Char", "Byte", "Decimal", "TimeSpan", "Uri", "Type"];

    /// <summary>The public, non-generic classes and structs of an XML namespace (the project's internal ones too).</summary>
    public IReadOnlyList<INamedTypeSymbol> TypesIn(string? xmlns)
    {
        if (string.IsNullOrEmpty(xmlns) || xmlns == XamlNames.XamlLanguage || xmlns == XamlNames.Design || xmlns == XamlNames.MarkupCompatibility)
        {
            return [];
        }

        return _typesByXmlns.GetOrAdd(xmlns, uri =>
        {
            var types = new List<INamedTypeSymbol>();
            foreach (var clr in ClrNamespaces(uri))
            {
                if (Namespace(clr) is { } ns)
                {
                    types.AddRange(ns.GetTypeMembers().Where(IsUsable));
                }
            }

            return [.. types.DistinctBy(t => t.Name).OrderBy(t => t.Name, StringComparer.Ordinal)];
        });
    }

    private bool IsUsable(INamedTypeSymbol type) =>
        type.Arity == 0
        && type.TypeKind is TypeKind.Class or TypeKind.Struct or TypeKind.Enum or TypeKind.Interface
        && !type.Name.StartsWith('<')
        && (type.DeclaredAccessibility == Accessibility.Public
            || (type.DeclaredAccessibility == Accessibility.Internal && SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, Compilation.Assembly)));

    /// <summary>Whether an element can create it (a non-static, non-abstract class or struct with a public parameterless constructor).</summary>
    public static bool IsCreatable(INamedTypeSymbol type) =>
        type is { IsAbstract: false, IsStatic: false } && type.TypeKind is TypeKind.Class or TypeKind.Struct
        && (type.TypeKind == TypeKind.Struct || type.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public));

    public bool IsMarkupExtension(INamedTypeSymbol type) =>
        type.Name.EndsWith("Extension", StringComparison.Ordinal) || DerivesFrom(type, "Avalonia.Data.BindingBase") || DerivesFrom(type, "Avalonia.Markup.Xaml.MarkupExtension");

    public static bool DerivesFrom(ITypeSymbol? type, string metadataName)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString() == metadataName)
            {
                return true;
            }
        }

        return type?.AllInterfaces.Any(i => i.ToDisplayString() == metadataName) == true;
    }

    public static bool IsControl(ITypeSymbol? type) => DerivesFrom(type, "Avalonia.Controls.Control");

    // ------------------------------------------------------------------ members

    /// <summary>The members of <paramref name="type"/> and its bases that XAML can set, without attached properties.</summary>
    public IReadOnlyList<XamlMember> Members(INamedTypeSymbol type) => _members.GetOrAdd(type, t =>
    {
        var result = new Dictionary<string, XamlMember>(StringComparer.Ordinal);
        for (var current = t; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                switch (member)
                {
                    case IPropertySymbol { IsStatic: false, IsIndexer: false, DeclaredAccessibility: Accessibility.Public } property when !result.ContainsKey(property.Name):
                        var settable = property.SetMethod is { DeclaredAccessibility: Accessibility.Public } || property.Name == "Classes";
                        result[property.Name] = new XamlMember(property.Name, property.Type, XamlMemberKind.Property, property, current) { Settable = settable };
                        break;
                    case IEventSymbol { IsStatic: false, DeclaredAccessibility: Accessibility.Public } @event when !result.ContainsKey(@event.Name):
                        result[@event.Name] = new XamlMember(@event.Name, @event.Type, XamlMemberKind.Event, @event, current);
                        break;
                    case IFieldSymbol { IsStatic: true, DeclaredAccessibility: Accessibility.Public } field
                        when field.Name.EndsWith("Property", StringComparison.Ordinal) && field.Name.Length > 8 && IsAvaloniaProperty(field.Type) && !IsAttached(field.Type):
                        var name = field.Name[..^8];
                        if (!result.TryGetValue(name, out var existing))
                        {
                            result[name] = new XamlMember(name, ValueType(field.Type), XamlMemberKind.AvaloniaProperty, field, current);
                        }
                        else if (existing.Kind == XamlMemberKind.Property && !existing.Settable && field.Type.Name == "StyledProperty")
                        {
                            result[name] = existing with { Settable = true }; // a read-only CLR wrapper of a settable styled property
                        }

                        break;
                }
            }
        }

        return [.. result.Values.OrderBy(m => m.Name, StringComparer.Ordinal)];
    });

    public XamlMember? Member(INamedTypeSymbol type, string name) => Members(type).FirstOrDefault(m => m.Name == name);

    /// <summary>The attached properties <paramref name="owner"/> declares (<c>Grid.Row</c>), with <c>SetXxx</c> methods as a fallback.</summary>
    public IReadOnlyList<XamlMember> Attached(INamedTypeSymbol owner) => _attached.GetOrAdd(owner, t =>
    {
        var result = new Dictionary<string, XamlMember>(StringComparer.Ordinal);
        for (var current = t; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member is IFieldSymbol { IsStatic: true, DeclaredAccessibility: Accessibility.Public } field
                    && field.Name.EndsWith("Property", StringComparison.Ordinal) && field.Name.Length > 8 && IsAttached(field.Type))
                {
                    result.TryAdd(field.Name[..^8], new XamlMember(field.Name[..^8], ValueType(field.Type), XamlMemberKind.Attached, field, current));
                }
                else if (member is IMethodSymbol { IsStatic: true, DeclaredAccessibility: Accessibility.Public, Parameters.Length: 2 } method
                         && method.Name.StartsWith("Set", StringComparison.Ordinal) && method.Name.Length > 3 && current.ContainingNamespace?.ToDisplayString().StartsWith("Avalonia", StringComparison.Ordinal) != true)
                {
                    result.TryAdd(method.Name[3..], new XamlMember(method.Name[3..], method.Parameters[1].Type, XamlMemberKind.Attached, method, current));
                }
            }
        }

        return [.. result.Values.OrderBy(m => m.Name, StringComparer.Ordinal)];
    });

    private static bool IsAvaloniaProperty(ITypeSymbol type) => DerivesFrom(type, "Avalonia.AvaloniaProperty");

    private static bool IsAttached(ITypeSymbol type) => type is INamedTypeSymbol { Name: "AttachedProperty" };

    /// <summary>The value type of <c>StyledProperty&lt;T&gt;</c>, <c>DirectProperty&lt;TOwner, T&gt;</c> and <c>AttachedProperty&lt;T&gt;</c>.</summary>
    private static ITypeSymbol? ValueType(ITypeSymbol propertyType)
    {
        for (var current = propertyType as INamedTypeSymbol; current is not null; current = current.BaseType)
        {
            if (current.TypeArguments.Length > 0)
            {
                return current.TypeArguments[^1];
            }
        }

        return null;
    }

    /// <summary>The <c>[Content]</c> property of a type or its bases.</summary>
    public XamlMember? ContentProperty(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.GetAttributes().Any(a => a.AttributeClass?.Name == "ContentAttribute"))
                {
                    return Member(type, property.Name);
                }
            }
        }

        return null;
    }

    /// <summary>The pseudo-classes a type declares (<c>[PseudoClasses(":pointerover", …)]</c>) with its bases'.</summary>
    public IReadOnlyList<string> PseudoClasses(INamedTypeSymbol? type)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal) { ":pointerover", ":pressed", ":disabled", ":focus", ":focus-within", ":focus-visible" };
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var attribute in current.GetAttributes().Where(a => a.AttributeClass?.Name == "PseudoClassesAttribute"))
            {
                foreach (var argument in attribute.ConstructorArguments)
                {
                    if (argument.Kind == TypedConstantKind.Array)
                    {
                        foreach (var value in argument.Values)
                        {
                            if (value.Value is string s)
                            {
                                result.Add(s);
                            }
                        }
                    }
                    else if (argument.Value is string single)
                    {
                        result.Add(single);
                    }
                }
            }
        }

        return [.. result];
    }

    /// <summary>The types that declare attached properties among an XML namespace's types (<c>Grid</c>, <c>DockPanel</c>…).</summary>
    public IReadOnlyList<INamedTypeSymbol> AttachedOwners(string? xmlns) =>
        [.. TypesIn(xmlns).Where(t => t.GetMembers().OfType<IFieldSymbol>().Any(f => f.IsStatic && IsAttached(f.Type)))];

    // ------------------------------------------------------------------ values

    /// <summary>The names a value of <paramref name="type"/> can be written as: enum members, booleans, named colors, static instances.</summary>
    public IReadOnlyList<(string Name, ISymbol? Symbol)> ValuesOf(ITypeSymbol? type)
    {
        if (type is null)
        {
            return [];
        }

        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            type = nullable.TypeArguments[0];
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            return [.. type.GetMembers().OfType<IFieldSymbol>().Where(f => f.IsConst).Select(f => (f.Name, (ISymbol?)f))];
        }

        if (type.SpecialType == SpecialType.System_Boolean)
        {
            return [("True", null), ("False", null)];
        }

        type = type.WithNullableAnnotation(NullableAnnotation.None);
        var name = type.ToDisplayString();
        if (name is "Avalonia.Media.IBrush" or "Avalonia.Media.Brush" or "Avalonia.Media.ISolidColorBrush" or "Avalonia.Media.SolidColorBrush" or "Avalonia.Media.Color")
        {
            var colors = Type("Avalonia.Media.Colors");
            return colors is null ? [] : [.. colors.GetMembers().OfType<IPropertySymbol>().Where(p => p.IsStatic).Select(p => (p.Name, (ISymbol?)p))];
        }

        // Types with static instances of themselves (ThemeVariant.Dark, FontFamily.Default, Cursor…).
        return [.. type.GetMembers()
            .Where(m => m.IsStatic && m.DeclaredAccessibility == Accessibility.Public && m switch
            {
                IPropertySymbol p => SymbolEqualityComparer.Default.Equals(p.Type.WithNullableAnnotation(NullableAnnotation.None), type),
                IFieldSymbol f => SymbolEqualityComparer.Default.Equals(f.Type.WithNullableAnnotation(NullableAnnotation.None), type),
                _ => false,
            })
            .Select(m => (m.Name, (ISymbol?)m))];
    }

    // ------------------------------------------------------------------ binding paths

    /// <summary>The members a binding path can name on <paramref name="type"/>: public instance properties, and methods a command can call.</summary>
    public static IEnumerable<ISymbol> BindableMembers(ITypeSymbol type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member.IsStatic || member.DeclaredAccessibility != Accessibility.Public || !seen.Add(member.Name))
                {
                    continue;
                }

                if (member is IPropertySymbol { IsIndexer: false } || member is IMethodSymbol { MethodKind: MethodKind.Ordinary, Parameters.Length: <= 1, IsGenericMethod: false } method && !method.Name.StartsWith("get_", StringComparison.Ordinal))
                {
                    yield return member;
                }
            }
        }

        if (type.TypeKind == TypeKind.Interface)
        {
            foreach (var member in type.AllInterfaces.SelectMany(i => i.GetMembers()).OfType<IPropertySymbol>().Where(p => seen.Add(p.Name)))
            {
                yield return member;
            }
        }
    }

    /// <summary>The element type of a collection (for <c>Items[0]</c>), or null.</summary>
    public static ITypeSymbol? ElementType(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
        {
            return array.ElementType;
        }

        var enumerable = type.AllInterfaces.Append(type as INamedTypeSymbol).OfType<INamedTypeSymbol>()
            .FirstOrDefault(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T);
        return enumerable?.TypeArguments[0];
    }
}
