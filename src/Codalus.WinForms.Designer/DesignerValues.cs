using System.ComponentModel;
using System.ComponentModel.Design.Serialization;
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using Codalus.LanguageServices.Protocol;

namespace Codalus.WinForms.Designer;

/// <summary>
/// Finds types by the full C# names the model uses: in the framework and the loaded assemblies, then (#454) in the
/// assemblies of the search folders (the app's build output, its packages' assemblies among them), which nothing has
/// loaded yet when only the designer names their types: a form's base type from a control library's package, or a
/// package's control dropped from the Toolbox.
/// </summary>
// TODO(issue): a class library designed with no app that references it runs on its own output folder, which has none of
// its packages' assemblies (a library's build doesn't copy them): add the packages' folders from its assets file to the
// search folders (ADR 0073, "1.0: control libraries from packages").
internal sealed class DesignerTypes
{
    private readonly Dictionary<string, Type?> _cache = new(StringComparer.Ordinal);
    private static readonly Assembly[] s_framework = [typeof(Control).Assembly, typeof(Point).Assembly, typeof(Color).Assembly, typeof(Component).Assembly, typeof(object).Assembly];
    private readonly List<Assembly> _assemblies = [.. s_framework];
    private readonly HashSet<Type> _covered = [];
    private string[] _folders = [];
    private Dictionary<string, string>? _index;

    private static readonly Dictionary<string, Type> s_keywords = new(StringComparer.Ordinal)
    {
        ["object"] = typeof(object), ["string"] = typeof(string), ["bool"] = typeof(bool), ["char"] = typeof(char), ["byte"] = typeof(byte),
        ["short"] = typeof(short), ["int"] = typeof(int), ["long"] = typeof(long), ["float"] = typeof(float), ["double"] = typeof(double),
        ["decimal"] = typeof(decimal), ["uint"] = typeof(uint), ["ulong"] = typeof(ulong), ["sbyte"] = typeof(sbyte), ["ushort"] = typeof(ushort),
    };

    public void Add(Assembly assembly)
    {
        if (!_assemblies.Contains(assembly))
        {
            _assemblies.Insert(0, assembly);
            _cache.Clear();
        }
    }

    /// <summary>The folders whose assemblies declare the types the loaded ones don't (the project's and its app's output).</summary>
    public void SearchIn(IReadOnlyList<string> folders)
    {
        if (!_folders.SequenceEqual(folders, StringComparer.OrdinalIgnoreCase))
        {
            _folders = [.. folders];
            _index = null;
            _cache.Clear();
        }
    }

    /// <summary>The type <paramref name="name"/> names (<c>System.Windows.Forms.Button</c>, <c>Outer.Inner</c>), or null.</summary>
    public Type? Find(string name)
    {
        if (s_keywords.TryGetValue(name, out var keyword))
        {
            return keyword;
        }

        if (_cache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        Type? found = Type.GetType(name, throwOnError: false);
        foreach (var candidate in Candidates(name))
        {
            if (found is not null)
            {
                break;
            }

            foreach (var assembly in _assemblies.Concat(AppDomain.CurrentDomain.GetAssemblies()))
            {
                if (assembly.GetType(candidate, throwOnError: false) is { } type)
                {
                    found = type;
                    break;
                }
            }
        }

        // #454: a type of an assembly nothing has loaded yet (the project's code names it, but only running that code loads it).
        foreach (var candidate in Candidates(name))
        {
            if (found is not null || name.Contains(',', StringComparison.Ordinal))
            {
                break;
            }

            found = FromSearchFolders(candidate);
        }

        if (found is not null)
        {
            CoverMissingDesigners(found);
        }

        _cache[name] = found;
        return found;
    }

    /// <summary>The type <paramref name="name"/> (<c>A.B+C</c>) declared by an assembly of the search folders, loaded for it.</summary>
    private Type? FromSearchFolders(string name)
    {
        _index ??= Index(_folders);
        if (!_index.TryGetValue(name, out var path) || Load(path) is not { } assembly)
        {
            return null;
        }

        try
        {
            return assembly.GetType(name, throwOnError: false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException or TypeLoadException)
        {
            // A type whose base type's assembly is missing: not found, and why in the IDE's log.
            Console.Error.WriteLine($"[designer] {name} in {Path.GetFileName(path)} didn't load: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The public types each assembly of <paramref name="folders"/> declares (nested ones as <c>Outer+Inner</c>), read
    /// from their metadata without loading them: loading every assembly of a control library's output to look would
    /// take seconds, and what isn't needed stays unloaded. Only the folders' own files: satellites and <c>runtimes/</c>
    /// are the runtime's to pick.
    /// </summary>
    private static Dictionary<string, string> Index(IEnumerable<string> folders)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(folder, "*.dll");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                try
                {
                    using var pe = new PEReader(File.OpenRead(file));
                    if (!pe.HasMetadata)
                    {
                        continue;
                    }

                    var reader = pe.GetMetadataReader();
                    if (!reader.IsAssembly)
                    {
                        continue;
                    }

                    foreach (var handle in reader.TypeDefinitions)
                    {
                        if (PublicName(reader, reader.GetTypeDefinition(handle)) is { } typeName)
                        {
                            index.TryAdd(typeName, file);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
                {
                    // Not an assembly (a native library), or held: its types aren't found here.
                }
            }
        }

        return index;
    }

    /// <summary>A public type's reflection name (<c>A.B+C</c>), or null for a type code outside its assembly can't name.</summary>
    private static string? PublicName(MetadataReader reader, TypeDefinition type)
    {
        var visibility = type.Attributes & TypeAttributes.VisibilityMask;
        var name = reader.GetString(type.Name);
        if (visibility == TypeAttributes.Public)
        {
            var ns = reader.GetString(type.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        return visibility == TypeAttributes.NestedPublic && PublicName(reader, reader.GetTypeDefinition(type.GetDeclaringType())) is { } outer ? outer + "+" + name : null;
    }

    /// <summary>
    /// The assembly at <paramref name="path"/>, through the default context by its name (the app's deps file binds it,
    /// else the session's resolver finds it in the search folders), so it isn't loaded twice; else from the path.
    /// </summary>
    private static Assembly? Load(string path)
    {
        try
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyName(AssemblyName.GetAssemblyName(path));
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            try
            {
                return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            }
            catch (Exception again) when (again is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                Console.Error.WriteLine($"[designer] {Path.GetFileName(path)} didn't load: {again.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// A control library's designers that aren't there (#454): a vendor names its controls' designers in a design-time
    /// assembly of its own, which the app's output doesn't have unless its design-time package is referenced. Windows
    /// Forms then finds no designer: a form deriving from the library's form has no root designer and doesn't load, and
    /// its controls have none. Such a <see cref="DesignerAttribute"/> is replaced, for <paramref name="type"/> and the
    /// types deriving from it, by the nearest base type's of the same kind that is there (<c>Form</c>'s for a form,
    /// <c>Control</c>'s for a control), so it's designed as what it derives from. The framework's own types are left
    /// alone: their designers are always there. A vendor's design-time assembly that is there but built for the
    /// classic IDE's out-of-process designer (DevExpress's, against <c>Microsoft.DotNet.DesignTools.Server</c>) doesn't
    /// load either, and gets the same fallback.
    /// </summary>
    // TODO(issue): #461, a 1.x study of an MIT compatibility layer for the classic IDE's designer-server API, so a
    // vendor's own designers (DevExpress's Grid Designer, its smart tags) load here.
    private void CoverMissingDesigners(Type type)
    {
        if (!_covered.Add(type) || !typeof(IComponent).IsAssignableFrom(type) || s_framework.Contains(type.Assembly))
        {
            return;
        }

        try
        {
            Dictionary<string, DesignerAttribute>? replacements = null;
            foreach (var missing in TypeDescriptor.GetAttributes(type).OfType<DesignerAttribute>().Where(a => !Resolves(a.DesignerTypeName)))
            {
                for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
                {
                    if (TypeDescriptor.GetAttributes(baseType).OfType<DesignerAttribute>().FirstOrDefault(a => a.TypeId.Equals(missing.TypeId) && Resolves(a.DesignerTypeName)) is { } replacement)
                    {
                        (replacements ??= [])[Key(missing)] = replacement;
                        Console.Error.WriteLine($"[designer] {NameOf(type)}'s designer {missing.DesignerTypeName} isn't there: it's designed as a {NameOf(baseType)}.");
                        break;
                    }
                }
            }

            if (replacements is not null)
            {
                TypeDescriptor.AddProvider(new ReplacedDesigners(TypeDescriptor.GetProvider(type), replacements), type);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An attribute that throws as it's read: the type keeps its designers, and Windows Forms says what fails.
            Console.Error.WriteLine($"[designer] {NameOf(type)}'s designers couldn't be read: {ex.Message}");
        }
    }

    private bool Resolves(string designerTypeName)
    {
        try
        {
            return Type.GetType(designerTypeName, throwOnError: false) is not null
                || (!designerTypeName.Contains(',', StringComparison.Ordinal) && Find(designerTypeName) is not null);
        }
        catch (Exception ex) when (ex is FileLoadException or BadImageFormatException or TypeLoadException or ArgumentException)
        {
            return false;
        }
    }

    private static string Key(DesignerAttribute attribute) => attribute.DesignerTypeName + "|" + attribute.DesignerBaseTypeName;

    /// <summary>A type's attributes with its missing designers replaced by its base type's (#454).</summary>
    private sealed class ReplacedDesigners(TypeDescriptionProvider parent, Dictionary<string, DesignerAttribute> replacements) : TypeDescriptionProvider(parent)
    {
        public override ICustomTypeDescriptor? GetTypeDescriptor(Type objectType, object? instance) =>
            base.GetTypeDescriptor(objectType, instance) is { } descriptor ? new Descriptor(descriptor, replacements) : null;

        private sealed class Descriptor(ICustomTypeDescriptor parent, Dictionary<string, DesignerAttribute> replacements) : CustomTypeDescriptor(parent)
        {
            public override AttributeCollection GetAttributes() =>
                new([.. base.GetAttributes().Cast<Attribute>().Select(a => a is DesignerAttribute designer && replacements.TryGetValue(Key(designer), out var replacement) ? replacement : a)]);
        }
    }

    /// <summary><c>A.B.C</c> as written, then with the last dots as nested types (<c>A.B+C</c>, <c>A+B+C</c>).</summary>
    private static IEnumerable<string> Candidates(string name)
    {
        yield return name;
        var nested = name;
        for (var dot = nested.LastIndexOf('.'); dot > 0; dot = nested.LastIndexOf('.', dot - 1))
        {
            nested = nested[..dot] + "+" + nested[(dot + 1)..];
            yield return nested;
        }
    }

    /// <summary>A type's full C# name (nested types with dots).</summary>
    public static string NameOf(Type type) => (type.FullName ?? type.Name).Replace('+', '.');

    /// <summary>
    /// <paramref name="type"/>'s instance property <paramref name="name"/>, its most derived declaration (not an
    /// indexer), as <c>TypeDescriptor</c> takes it; null when it has none. <c>Type.GetProperty(name)</c> throws when a
    /// derived type hides a base type's property with another type: <c>CheckedListBox.Items</c> hides
    /// <c>ListBox.Items</c>, and a CheckedListBox on the form failed every later change ("Ambiguous match found").
    /// </summary>
    public static System.Reflection.PropertyInfo? Property(Type type, string name, bool nonPublic = false)
    {
        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | (nonPublic ? System.Reflection.BindingFlags.NonPublic : 0);
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.GetProperties(flags | System.Reflection.BindingFlags.DeclaredOnly).FirstOrDefault(p => p.Name == name && p.GetIndexParameters().Length == 0) is { } property)
            {
                return property;
            }
        }

        return null;
    }
}

/// <summary>
/// Turns the model's values into objects (what running the statement would give) and objects back into values (what
/// the classic IDE's CodeDOM serializers write: literals, enum members, <c>InstanceDescriptor</c>s' constructors and
/// static members, colors by name or <c>FromArgb</c>, the class's components by name).
/// </summary>
internal sealed class DesignerValues(DesignerTypes types, Func<string, object?> component, Func<object, string?> nameOf, Func<string, object?> resource)
{
    // ------------------------------------------------------------------ values → objects

    public object? Evaluate(DesignerValue value, Type? target = null)
    {
        var result = value.Kind switch
        {
            DesignerValueKinds.Literal => Literal(value),
            DesignerValueKinds.Enum => Enum(value),
            DesignerValueKinds.Member => Member(value),
            DesignerValueKinds.New => New(value),
            DesignerValueKinds.Call => Call(value),
            DesignerValueKinds.Array => Array(value),
            DesignerValueKinds.Resource => resource(value.Value ?? string.Empty),
            DesignerValueKinds.Reference => component(value.Value ?? string.Empty),
            DesignerValueKinds.TypeOf => Type(value.Type),
            _ => throw new InvalidOperationException($"Unknown value kind {value.Kind}."),
        };
        return target is null ? result : Convert(result, target);
    }

    private Type Type(string? name) => name is not null && types.Find(name) is { } type ? type : throw new InvalidOperationException($"The type {name} wasn't found (build the project?).");

    private static object? Literal(DesignerValue value)
    {
        var text = value.Value ?? string.Empty;
        var invariant = CultureInfo.InvariantCulture;
        return value.Type switch
        {
            "null" => null,
            "string" => text,
            "bool" => text == "true",
            "char" => text.Length > 0 ? text[0] : '\0',
            "int" => int.Parse(text, invariant),
            "uint" => uint.Parse(text, invariant),
            "long" => long.Parse(text, invariant),
            "ulong" => ulong.Parse(text, invariant),
            "float" => float.Parse(text, invariant),
            "double" => double.Parse(text, invariant),
            "decimal" => decimal.Parse(text, invariant),
            "byte" => byte.Parse(text, invariant),
            "sbyte" => sbyte.Parse(text, invariant),
            "short" => short.Parse(text, invariant),
            "ushort" => ushort.Parse(text, invariant),
            _ => text,
        };
    }

    private object? Enum(DesignerValue value)
    {
        var type = Type(value.Type);
        if (!type.IsEnum)
        {
            return Member(value with { Kind = DesignerValueKinds.Member, Value = value.Members?.FirstOrDefault() });
        }

        return value.Members is { Length: > 0 } members
            ? System.Enum.Parse(type, string.Join(", ", members))
            : System.Enum.ToObject(type, long.Parse(value.Value ?? "0", CultureInfo.InvariantCulture));
    }

    private object? Member(DesignerValue value)
    {
        var type = Type(value.Type);
        var name = value.Value ?? string.Empty;
        if (type.IsEnum)
        {
            return System.Enum.Parse(type, name);
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        if (type.GetProperty(name, flags) is { } property)
        {
            return property.GetValue(null);
        }

        if (type.GetField(name, flags) is { } field)
        {
            return field.GetValue(null);
        }

        throw new InvalidOperationException($"{DesignerTypes.NameOf(type)} has no static member {name}.");
    }

    private object New(DesignerValue value)
    {
        var type = Type(value.Type);
        var arguments = (value.Arguments ?? []).Select(a => Evaluate(a)).ToArray();
        foreach (var constructor in type.GetConstructors().Where(c => c.GetParameters().Length == arguments.Length))
        {
            if (TryConvert(arguments, constructor.GetParameters(), out var converted))
            {
                return constructor.Invoke(converted);
            }
        }

        if (arguments.Length == 0 && type.IsValueType)
        {
            return Activator.CreateInstance(type)!;
        }

        throw new InvalidOperationException($"{DesignerTypes.NameOf(type)} has no constructor for ({string.Join(", ", arguments.Select(a => a?.GetType().Name ?? "null"))}).");
    }

    private object? Call(DesignerValue value)
    {
        var type = Type(value.Type);
        var arguments = (value.Arguments ?? []).Select(a => Evaluate(a)).ToArray();
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == value.Value && m.GetParameters().Length == arguments.Length))
        {
            if (TryConvert(arguments, method.GetParameters(), out var converted))
            {
                return method.Invoke(null, converted);
            }
        }

        throw new InvalidOperationException($"{DesignerTypes.NameOf(type)}.{value.Value} wasn't found for these arguments.");
    }

    private System.Array Array(DesignerValue value)
    {
        var type = Type(value.Type);
        var items = value.Arguments ?? [];
        var array = System.Array.CreateInstance(type, items.Length);
        for (var i = 0; i < items.Length; i++)
        {
            array.SetValue(Evaluate(items[i], type), i);
        }

        return array;
    }

    /// <summary>Invokes <paramref name="name"/> on <paramref name="target"/> with the model's arguments (an instance method, by arity and convertible arguments).</summary>
    public void Invoke(object target, string name, DesignerValue[] arguments)
    {
        var values = arguments.Select(a => Evaluate(a)).ToArray();
        foreach (var method in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name == name && m.GetParameters().Length == values.Length))
        {
            if (TryConvert(values, method.GetParameters(), out var converted))
            {
                method.Invoke(target, converted);
                return;
            }
        }

        throw new InvalidOperationException($"{DesignerTypes.NameOf(target.GetType())}.{name} wasn't found for these arguments.");
    }

    private static bool TryConvert(object?[] arguments, ParameterInfo[] parameters, out object?[] converted)
    {
        converted = new object?[arguments.Length];
        for (var i = 0; i < arguments.Length; i++)
        {
            try
            {
                converted[i] = Convert(arguments[i], parameters[i].ParameterType);
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or ArgumentException)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A value as <paramref name="target"/>: numbers between numeric types, numbers to enums, else as it is when assignable.</summary>
    public static object? Convert(object? value, Type target)
    {
        if (value is null)
        {
            return !target.IsValueType || Nullable.GetUnderlyingType(target) is not null ? null : throw new InvalidCastException($"null isn't a {target.Name}.");
        }

        if (target.IsInstanceOfType(value))
        {
            return value;
        }

        var underlying = Nullable.GetUnderlyingType(target) ?? target;
        if (underlying.IsEnum && value is IConvertible)
        {
            return System.Enum.ToObject(underlying, value);
        }

        if (value is IConvertible && underlying.IsPrimitive || underlying == typeof(decimal))
        {
            return System.Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);
        }

        throw new InvalidCastException($"A {value.GetType().Name} isn't a {target.Name}.");
    }

    // ------------------------------------------------------------------ objects → values

    /// <summary>The code for <paramref name="value"/>, or null when code can't say it (an image: the classic IDE puts those in the resources).</summary>
    public DesignerValue? Serialize(object? value)
    {
        switch (value)
        {
            case null:
                return DesignerValue.Null;
            case string s:
                return DesignerValue.String(s);
            case bool b:
                return DesignerValue.Bool(b);
            case char c:
                return new DesignerValue { Kind = DesignerValueKinds.Literal, Type = "char", Value = c.ToString() };
            case int or long or short or byte or sbyte or uint or ulong or ushort or float or double:
                return new DesignerValue
                {
                    Kind = DesignerValueKinds.Literal,
                    Type = value switch { int => "int", long => "long", short => "short", byte => "byte", sbyte => "sbyte", uint => "uint", ulong => "ulong", ushort => "ushort", float => "float", _ => "double" },
                    Value = value switch { float f => f.ToString("R", CultureInfo.InvariantCulture), double d => d.ToString("R", CultureInfo.InvariantCulture), _ => System.Convert.ToString(value, CultureInfo.InvariantCulture) },
                };
            case Enum e:
                return EnumValue(e);
            case Type type:
                return new DesignerValue { Kind = DesignerValueKinds.TypeOf, Type = DesignerTypes.NameOf(type) };
            case Color color:
                return ColorValue(color);
            case System.Array array when array.Rank == 1:
            {
                var items = new List<DesignerValue>();
                foreach (var item in array)
                {
                    if (Serialize(item) is not { } serialized)
                    {
                        return null;
                    }

                    items.Add(serialized);
                }

                return DesignerValue.ArrayOf(DesignerTypes.NameOf(array.GetType().GetElementType()!), [.. items]);
            }
        }

        if (value is IComponent && nameOf(value) is { } name)
        {
            return DesignerValue.ReferenceTo(name);
        }

        var converter = TypeDescriptor.GetConverter(value);
        if (converter.CanConvertTo(typeof(InstanceDescriptor)) && converter.ConvertTo(value, typeof(InstanceDescriptor)) is InstanceDescriptor { MemberInfo: { } member } descriptor)
        {
            var arguments = new List<DesignerValue>();
            foreach (var argument in descriptor.Arguments)
            {
                if (Serialize(argument) is not { } serialized)
                {
                    return null;
                }

                arguments.Add(serialized);
            }

            return member switch
            {
                ConstructorInfo constructor => DesignerValue.NewOf(DesignerTypes.NameOf(constructor.DeclaringType!), [.. arguments]),
                MethodInfo { IsStatic: true } method => DesignerValue.CallOf(DesignerTypes.NameOf(method.DeclaringType!), method.Name, [.. arguments]),
                FieldInfo { IsStatic: true } field => DesignerValue.MemberOf(DesignerTypes.NameOf(field.DeclaringType!), field.Name),
                PropertyInfo property when property.GetGetMethod()?.IsStatic == true => DesignerValue.MemberOf(DesignerTypes.NameOf(property.DeclaringType!), property.Name),
                _ => null,
            };
        }

        return null;
    }

    private static DesignerValue EnumValue(Enum value)
    {
        var type = value.GetType();
        var name = DesignerTypes.NameOf(type);
        if (System.Enum.GetName(type, value) is { } single)
        {
            return DesignerValue.EnumOf(name, single);
        }

        if (type.IsDefined(typeof(FlagsAttribute), inherit: false))
        {
            var remaining = System.Convert.ToUInt64(value, CultureInfo.InvariantCulture);
            var members = new List<string>();
            foreach (var member in System.Enum.GetValues(type).Cast<Enum>())
            {
                var bits = System.Convert.ToUInt64(member, CultureInfo.InvariantCulture);
                if (bits != 0 && (bits & (bits - 1)) == 0 && (remaining & bits) == bits)
                {
                    members.Add(member.ToString());
                    remaining &= ~bits;
                }
            }

            if (remaining == 0 && members.Count > 0)
            {
                return DesignerValue.EnumOf(name, [.. members]);
            }
        }

        return new DesignerValue { Kind = DesignerValueKinds.Enum, Type = name, Value = System.Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) };
    }

    private static DesignerValue ColorValue(Color color)
    {
        if (color.IsEmpty)
        {
            return DesignerValue.MemberOf("System.Drawing.Color", "Empty");
        }

        if (color.IsKnownColor)
        {
            return color.IsSystemColor ? DesignerValue.MemberOf("System.Drawing.SystemColors", color.Name) : DesignerValue.MemberOf("System.Drawing.Color", color.Name);
        }

        DesignerValue[] components = color.A == 255
            ? [DesignerValue.Int(color.R), DesignerValue.Int(color.G), DesignerValue.Int(color.B)]
            : [DesignerValue.Int(color.A), DesignerValue.Int(color.R), DesignerValue.Int(color.G), DesignerValue.Int(color.B)];
        return DesignerValue.CallOf("System.Drawing.Color", "FromArgb", components);
    }
}
